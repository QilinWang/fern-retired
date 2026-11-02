using Fern.Registry;
using Fern.Scheduler;
using Microsoft.Data.Sqlite;
using Spectre.Console;

namespace Fern.Run.Cli;

/// <summary>
/// The composite that answers "is this machine ready to be given cells?", for a
/// machine that will only RUN work — a pod — rather than schedule it.
///
/// It is the sibling of <see cref="ControlPlaneInstall"/> and the split matters,
/// because the two jobs need different things and one of them needs much less:
///
/// <list type="bullet">
/// <item>a scheduler host holds a Registry Home, publishes five content-addressed
/// executables, and materializes a systemd unit — that is
/// <c>install-control-plane</c>;</item>
/// <item>a Worker Site holds NO DATABASE AT ALL. `csharp/AGENTS.md` states the
/// rule — never create a worker-local Registry — and the code keeps it:
/// nothing in Fern.Site opens SQLite. A Site writes JSON receipts under its work
/// root, and the Home Registry is the only database in the system. Its own
/// execution profile names the machine that holds it, in
/// <c>registry_home_profile_id</c>.</item>
/// </list>
///
/// It also needs no installation of its own. The Site binary, its support files
/// and the frozen Python source are pushed by the scheduler when a Plan first
/// needs this machine, verified by digest, and reused afterwards. So this
/// command does not install anything. It observes, and it names the one command
/// that changes state.
///
/// Steps and states are <see cref="ControlPlaneInstall"/>'s: Ready, Prepares,
/// and Unbuilt for something nothing in this repository can do. The sequence
/// this serves is
/// docs/design/original/by_ai/2026-09-10-what-moving-a-cell-to-a-site-needs.md, phase 0.
/// </summary>
internal static class WorkerSiteReadiness
{
    public const string Verb = "inspect-worker-site-readiness";

    internal sealed record Target(string WorkspaceRoot, string ProfileId, string Registry, string Actor);

    /// <summary>
    /// Read-only by construction: every probe below either reads a file, reads
    /// the Registry, or asks the machine about itself through the observer that
    /// `verify-machine-admission` already uses. Registration is deliberately NOT
    /// performed here — it is one existing verb, and a command that observes
    /// should not also quietly write.
    /// </summary>
    static StepState ProfileDeclared(Target target)
    {
        // CHG 2026-09-26: profiles/ moved to config/profiles/ (Fern.Registry.RepositoryLayout).
        var path = Path.Combine(WorktreeControlPaths.Profiles(target.WorkspaceRoot), target.ProfileId + ".json");
        return File.Exists(path)
            ? new StepState.Ready(path)
            : new StepState.Unbuilt(
                $"no execution profile {target.ProfileId} in {WorktreeControlPaths.Profiles(target.WorkspaceRoot)}",
                $"write {Fern.Registry.RepositoryLayout.ProfilesDirectory}/{target.ProfileId}.json, then re-run",
                "phase 0, a machine becomes a Worker Site",
                Blocks: true);
    }

    /// <summary>
    /// The one question only the machine can answer. For an ssh executor this
    /// reaches the machine over the maintained helper, so a refusal here is
    /// usually the pod being unreachable or its declared python being absent,
    /// not a Fern problem.
    /// </summary>
    static StepState MachineAnswersItsProfile(Target target)
    {
        var observed = MachineAdmissionVerification.Observe(
            target.WorkspaceRoot, target.ProfileId, ObserverVersion, target.Actor);
        if (!observed.IsOk)
        {
            return new StepState.Unbuilt(
                observed.Failure.Detail,
                $"fern-run verify-machine-admission --profile {target.ProfileId}",
                "phase 0, a machine becomes a Worker Site",
                Blocks: true);
        }

        var write = observed.Value;
        var device = write.CudaAvailable
            ? $"{write.Device} ({write.CudaDeviceName ?? "cuda"})"
            : write.Device;
        return new StepState.Ready($"{write.Hostname}, {device}, profile revision {write.ExecutionProfileRevision}");
    }

    /// <summary>
    /// Registered and enabled are read here and changed by their own verbs.
    /// Neither is Unbuilt in the "Fern cannot do this" sense — the capability
    /// exists — so both are reported as work a person does with one command.
    /// </summary>
    static StepState RegisteredAtHome(Target target)
    {
        // CHG 2026-09-11 night (slice 9): the Site is the `sites` row register-site
        // mints from the profile, and disabled is a standing hold over it.
        RegistryOperations.Site? row = null;
        IReadOnlyList<RegistryOperations.OperatorRequest> holds = [];
        try
        {
            using var connection = new SqliteConnection($"Data Source={target.Registry};Mode=ReadOnly");
            connection.Open();
            row = RegistryOperations.ListSites(connection).FirstOrDefault(site => site.Name == target.ProfileId);
            if (row is not null)
            {
                holds = RegistryOperations.PendingRequests(connection, RegistryOperations.RequestHold, RegistryOperations.SiteScope(row.SiteId));
            }
        }
        catch (SqliteException)
        {
            row = null;
        }
        if (row is null)
        {
            return new StepState.Prepares(
                $"not registered; run: fern-run register-site --registry {target.Registry} --site {target.ProfileId} --profile {Fern.Registry.RepositoryLayout.ProfilesDirectory}/{target.ProfileId}.json");
        }
        return holds.Count == 0
            ? new StepState.Ready($"registered as Site {row.SiteId}")
            : new StepState.Prepares(
                $"registered but disabled ({holds[0].Reason}); run: fern-run enable-site --registry {target.Registry} --site {target.ProfileId}");
    }

    const string ObserverVersion = "fern-run/inspect-worker-site-readiness";

    static readonly (string Name, Func<Target, StepState> Probe)[] Sequence =
    [
        ("execution profile", ProfileDeclared),
        ("machine answers its profile", MachineAnswersItsProfile),
        ("registered at Home", RegisteredAtHome),
    ];

    internal static int Run(Target target, IAnsiConsole console, TextWriter stderr)
    {
        console.Write(new Rule($"[bold]worker site[/] [dim]{target.ProfileId}[/]").LeftJustified());
        var observed = new List<(string Name, StepState State)>();
        foreach (var (name, probe) in Sequence)
        {
            var state = probe(target);
            observed.Add((name, state));
            // A blocking gap makes every later probe meaningless — an
            // unreachable machine cannot answer whether it is registered
            // usefully — so the walk stops rather than printing noise.
            if (state is StepState.Unbuilt { Blocks: true })
            {
                break;
            }
        }

        var table = new Table().Border(TableBorder.SimpleHeavy);
        table.AddColumn("step");
        table.AddColumn("state");
        table.AddColumn("detail");
        foreach (var (name, state) in observed)
        {
            var (mark, detail) = state switch
            {
                StepState.Ready ready => ("[green]ready[/]", ready.Detail),
                StepState.Prepares prepares => ("[yellow]to do[/]", prepares.Detail),
                StepState.Unbuilt unbuilt => ("[red]blocked[/]", unbuilt.Detail),
            };
            table.AddRow(Markup.Escape(name), mark, Markup.Escape(detail));
        }

        console.Write(table);
        console.MarkupLine(
            "[dim]A Worker Site holds no database. Its receipts are JSON under its work root, and the\n"
            + "Site binary and frozen source arrive from the scheduler when a Plan first needs it.[/]");

        var blocked = observed.Count(row => row.State is StepState.Unbuilt);
        if (blocked > 0)
        {
            stderr.WriteLine($"{Verb}: {target.ProfileId} cannot be given cells yet");
        }

        return blocked > 0 ? 1 : 0;
    }
}
