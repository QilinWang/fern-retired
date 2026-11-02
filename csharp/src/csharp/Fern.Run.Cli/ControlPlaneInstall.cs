using Fern.Registry;
using Fern.Scheduler;
using Spectre.Console;
using Fern.Install;

namespace Fern.Run.Cli;

/// <summary>
/// The composite that installs Fern's control plane on a machine that has
/// nothing on it yet.
///
/// The sequence it walks, and which parts of it exist, are owned by
/// docs/design/original/by_ai/2026-09-10-what-a-smooth-install-needs.md. This is the
/// third layer of the command surface: an operation is a function that does one
/// thing, a leaf is a verb that wraps one operation for a caller who genuinely
/// needs it alone, and a composite is this — the if/else that decides which
/// leaves to run against the machine in front of it. Only two steps below have
/// their own verb, because only those two are worth invoking alone:
/// `initialize-registry` and `install-scheduler-service`.
///
/// A step reports one of three states, and the distinction is the point:
/// Ready means the machine already satisfies it; Prepares means it does not and
/// this command knows how to fix it; Unbuilt means it does not and NOTHING in
/// this repository knows how to fix it. An Unbuilt step is not a failure of the
/// machine, it is a gap in Fern, so it is named as such and the run refuses
/// rather than pretending the install completed.
/// </summary>
/// <summary>
/// Which machine this control plane is being installed on.
///
/// The sequence was one fixed list with an unconditional Registry Home step, so
/// running the installer on a rented GPU pod created a Registry there: a second
/// authority for the one question the Registry exists to answer. The role is a
/// required argument rather than a default, because guessing it wrong in either
/// direction is silent.
/// </summary>
internal enum ControlPlaneRole
{
    RegistryHome,
    WorkerSite,
}

internal static class ControlPlaneInstall
{
    public const string Verb = "install-control-plane";

    sealed record Step(string Name, Func<InstallPaths, StepState> Probe, Action<InstallPaths>? Prepare);

    /// <summary>
    /// One root derives the whole layout, because a fresh machine has no
    /// opinion about where anything goes. Each part is overridable for a
    /// machine that does.
    /// </summary>
    internal sealed record InstallPaths(
        string Root,
        string Registry,
        string WorkRoot,
        string DeploymentRoot,
        string DeployRoot,
        string ProfilesRoot,
        string SshHelper);

    // ---------------------------------------------------------------- steps

    /// <summary>
    /// The order is the order of
    /// docs/design/original/by_ai/2026-09-10-what-a-smooth-install-needs.md, minus the
    /// steps the installer performs for itself once it is reached.
    /// </summary>
    /// <summary>
    /// The steps every machine needs, whatever it is for.
    /// </summary>
    static readonly Step[] Shared =
    [
        new("dotnet runtime", ProbeDotnetRuntime, null),
        new("deployment root", paths => Folder(paths.DeploymentRoot), paths => Create(paths.DeploymentRoot)),
        new("deploy store root", paths => Folder(paths.DeployRoot), paths => Create(paths.DeployRoot)),
        new("execution profiles", paths => Folder(paths.ProfilesRoot), paths => Create(paths.ProfilesRoot)),
        new("work root", paths => Folder(paths.WorkRoot), paths => Create(paths.WorkRoot)),
        new("maintained SSH helper", ProbeSshHelper, null),
        new("deployment lock", ProbeDeploymentLock, null),
    ];

    /// <summary>
    /// The one step that belongs to Registry Home alone. A worker site reads the
    /// Registry over a wire; it does not hold one.
    /// </summary>
    static readonly Step RegistryHomeStep = new("Registry Home", ProbeRegistry, PrepareRegistry);

    /// <summary>
    /// The sequence for one role, in the order of
    /// docs/design/original/by_ai/2026-09-10-what-a-smooth-install-needs.md. The
    /// Registry Home step keeps its position before the deployment lock.
    /// </summary>
    static Step[] SequenceFor(ControlPlaneRole role) =>
        role switch
        {
            ControlPlaneRole.WorkerSite => Shared,
            ControlPlaneRole.RegistryHome =>
            [
                .. Shared[..^1],
                RegistryHomeStep,
                Shared[^1],
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "unknown control plane role"),
        };

    /// <summary>
    /// A directory is the one thing on this list that is safe to create without
    /// asking: it holds nothing yet, and an existing one is left exactly as it
    /// is. Deciding what to do with a NON-empty deployment root is the
    /// installer's job, not this command's — it refuses a published binary
    /// whose bytes disagree with the digest in its name, which is a stronger
    /// answer than anything a directory check could give.
    /// </summary>
    static StepState Folder(string path) =>
        Directory.Exists(path)
            ? new StepState.Ready(path)
            : new StepState.Prepares($"create {path}");

    static void Create(string path) => Directory.CreateDirectory(path);

    static StepState ProbeRegistry(InstallPaths paths) =>
        File.Exists(paths.Registry)
            ? new StepState.Ready(paths.Registry)
            : new StepState.Prepares($"initialize {paths.Registry}");

    static void PrepareRegistry(InstallPaths paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Registry)!);
        if (RegistrySchema.Initialize(paths.Registry) is RegistryInitializationOutcome.Refused refused)
        {
            throw new InvalidOperationException($"initialize Registry Home: {refused.Detail}");
        }
    }

    /// <summary>
    /// The helper is a file in the checkout, so its absence means the source
    /// tree is wrong rather than the machine. Nothing can create it here.
    /// </summary>
    static StepState ProbeSshHelper(InstallPaths paths) =>
        File.Exists(paths.SshHelper)
            ? new StepState.Ready(paths.SshHelper)
            : new StepState.Unbuilt(
                $"{paths.SshHelper} is missing from this checkout",
                "git checkout -- scripts/connect_runpod_ssh.sh",
                "step 3, refuse a missing declared input",
                Blocks: true);

    /// <summary>
    /// Nothing in this repository installs a .NET runtime, and this command
    /// deliberately does not become the first thing that does: it would download
    /// and execute a script from the network, which is a decision for a person
    /// and not a side effect of an install verb. So the state is Unbuilt and it
    /// carries the exact command.
    ///
    /// Pseudo-code for the step, if it is ever built:
    ///
    ///     download https://dot.net/v1/dotnet-install.sh to a temporary path
    ///     verify its digest against a pinned value committed here
    ///     run it with --channel from global.json and --install-dir under Root
    ///     re-probe; refuse if the runtime still does not answer
    ///
    /// The pinned digest is the part that makes it Fern-shaped rather than a
    /// curl-to-shell: an unpinned installer is an unversioned dependency, which
    /// is the thing every other identity check in this tree exists to prevent.
    /// </summary>
    static StepState ProbeDotnetRuntime(InstallPaths paths)
    {
        _ = paths;
        var probe = BoundedProbe.Run("/usr/bin/env", ["dotnet", "--list-runtimes"], 10_000, 1 << 20);
        if (probe is not BoundedProbe.Outcome.Completed completed || completed.ExitCode != 0)
        {
            return new StepState.Unbuilt(
                "no dotnet on PATH",
                "curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 11.0",
                "step 4, build the five executables",
                Blocks: true);
        }

        var runtimes = System.Text.Encoding.UTF8.GetString(completed.Stdout);
        return runtimes.Contains("Microsoft.NETCore.App 11.", StringComparison.Ordinal)
            ? new StepState.Ready("Microsoft.NETCore.App 11")
            : new StepState.Unbuilt(
                "dotnet answers, but no Microsoft.NETCore.App 11 runtime is installed",
                "curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 11.0",
                "step 4, build the five executables",
                Blocks: true);
    }

    /// <summary>
    /// Step 2 of the design has no implementation. The helpers it needs exist —
    /// DurableFiles.TryLockExclusive, OpenLockFile and ClosesOnExec — and no
    /// caller takes the lock, so an install can republish binaries under the
    /// deployment root while a Plan is starting and reading them.
    ///
    /// Pseudo-code for the step:
    ///
    ///     handle = DurableFiles.OpenLockFile(deploymentRoot/.fern-deployment.lock)
    ///     if not DurableFiles.TryLockExclusive(handle):
    ///         refuse, naming the pid in the lock file
    ///     ... publish, activate, write the unit and the receipt ...
    ///     close the handle, which releases the flock
    ///
    /// and the same acquisition at Plan start, so the two exclude each other
    /// rather than only excluding their own kind.
    /// </summary>
    /// <summary>
    /// CHG 2026-09-11: built. The exclusion is a file in the deployment root
    /// held open with no sharing for the life of the install
    /// (<see cref="Fern.Install.InstallLock"/>), so the operating system is
    /// what says a second writer exists, and a holder that dies releases it
    /// immediately rather than leaving a deadline to reconcile.
    /// </summary>
    static StepState ProbeDeploymentLock(InstallPaths paths) =>
        Fern.Install.InstallLock.IsHeld(paths.DeploymentRoot)
            ? new StepState.Unbuilt(
                $"another install holds {paths.DeploymentRoot}: "
                    + Fern.Install.InstallLock.Describe(paths.DeploymentRoot),
                "wait for the running install to finish",
                "one install per machine at a time",
                Blocks: true)
            : new StepState.Ready($"free to install; this run will hold {Fern.Install.InstallLock.FileName}");

    // ------------------------------------------------------------ rendering

    /// <summary>
    /// The table is for a person, and for an agent reading a transcript: nine
    /// rows say what the machine has, what will change, and what Fern cannot do,
    /// in less text than one paragraph explaining the same thing.
    /// </summary>
    static void Render(IAnsiConsole console, IReadOnlyList<(Step Step, StepState State)> observed, InstallPaths paths)
    {
        console.Write(new Rule($"[bold]install-control-plane[/] [dim]{paths.Root}[/]").LeftJustified());
        var table = new Table().Border(TableBorder.SimpleHeavy);
        table.AddColumn("step");
        table.AddColumn("state");
        table.AddColumn("detail");
        foreach (var (step, state) in observed)
        {
            var (mark, detail) = state switch
            {
                StepState.Ready ready => ("[green]ready[/]", ready.Detail),
                StepState.Prepares prepares => ("[yellow]prepares[/]", prepares.Detail),
                StepState.Unbuilt unbuilt => ("[red]unbuilt[/]", unbuilt.Detail),
            };
            table.AddRow(Markup.Escape(step.Name), mark, Markup.Escape(detail));
        }

        console.Write(table);
    }

    static void RenderGaps(IAnsiConsole console, IReadOnlyList<(Step Step, StepState State)> observed)
    {
        foreach (var (step, state) in observed)
        {
            if (state is not StepState.Unbuilt unbuilt)
            {
                continue;
            }

            console.Write(new Panel(
                    new Markup(
                        $"[dim]{Markup.Escape(unbuilt.DesignStep)}[/]\n\n"
                        + $"{Markup.Escape(unbuilt.Detail)}\n\n"
                        + $"by hand: [bold]{Markup.Escape(unbuilt.ByHand)}[/]"))
                .Header($"[red]unbuilt:[/] {Markup.Escape(step.Name)}")
                .BorderColor(Color.Red));
        }
    }

    // ------------------------------------------------------------- the walk

    internal static int Run(
        InstallPaths paths,
        bool planOnly,
        ControlPlaneRole role,
        IAnsiConsole console,
        TextWriter stderr,
        bool fromWorktree = false)
    {
        var observed = SequenceFor(role).Select(step => (Step: step, State: step.Probe(paths))).ToList();
        Render(console, observed, paths);

        var blocking = observed
            .Where(row => row.State is StepState.Unbuilt { Blocks: true })
            .ToList();
        if (observed.Any(row => row.State is StepState.Unbuilt))
        {
            RenderGaps(console, observed);
        }

        if (planOnly)
        {
            return blocking.Count > 0 ? 1 : 0;
        }

        if (blocking.Count > 0)
        {
            stderr.WriteLine(
                $"{Verb}: {blocking.Count} step(s) this repository cannot perform; nothing was installed");
            return 1;
        }

        // One install per machine at a time, for the whole of the walk below.
        // Taken after the plan is rendered and the refusals are counted, so
        // `--plan-only` never takes it and a run that will refuse anyway does
        // not make a second operator wait.
        var machine = Fern.Install.InstallLock.Acquire(paths.DeploymentRoot, Verb);
        if (!machine.IsOk)
        {
            stderr.WriteLine($"{Verb}: {machine.Refusal}");
            return 1;
        }
        using var held = machine.Value;

        foreach (var (step, state) in observed)
        {
            if (state is not StepState.Prepares prepares || step.Prepare is null)
            {
                continue;
            }

            console.MarkupLine($"[yellow]·[/] {Markup.Escape(prepares.Detail)}");
            try
            {
                step.Prepare(paths);
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
            {
                stderr.WriteLine($"{Verb}: {step.Name}: {error.Message}");
                return 1;
            }
        }

        // The one step with its own verb that this composite must run rather
        // than reimplement. Its flags are the composite's derived paths.
        //
        // CHG 2026-09-12: --from-worktree travels with them. Without it the
        // refusal that step raises names an exit no reachable command offered:
        // the member installer's own verb is not registered anywhere, and
        // several lanes share this repository, so one lane's uncommitted C#
        // left every machine uninstallable.
        console.Write(new Rule("[bold]publish the control binaries[/]").LeftJustified());
        string[] flags =
        [
            "--registry", paths.Registry,
            "--work-root", paths.WorkRoot,
            "--deployment-root", paths.DeploymentRoot,
            "--deploy-root", paths.DeployRoot,
            "--profiles-root", paths.ProfilesRoot,
            "--ssh-helper", paths.SshHelper,
        ];
        return InstallCli.Run(
            fromWorktree ? [.. flags, "--from-worktree"] : flags,
            Console.Out,
            stderr);
    }
}
