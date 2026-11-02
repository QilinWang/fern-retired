using Fern.Registry;
using System.CommandLine;
using Spectre.Console;

namespace Fern.Run.Cli;

/// <summary>
/// The command-line wiring for <see cref="ControlPlaneInstall"/>. The decision —
/// which steps a machine needs and what to do about each — lives in that file
/// with no dependency on System.CommandLine, so the test project links it
/// directly, the way it links MonitorPresentation.cs.
/// </summary>
internal static class ControlPlaneInstallVerbs
{
    public static void Add(Command parent)
    {
        var command = new Command(
            ControlPlaneInstall.Verb,
            "Bring a machine from nothing to an installed control plane: probe every step, create what is missing, then install; never starts work");

        var rootDirectory = new Option<string>("--root") { Description = "Directory the whole layout is derived from.", Required = true };
        // Deliberately no --registry. Program.cs appends this machine's default
        // Registry Home to ANY command that declares a non-required --registry
        // and was given none, so a composite that offered one would silently
        // install against the machine's existing Registry instead of the one
        // under --root — which on a fresh pod is the opposite of what was asked.
        // One root derives the layout; that is the whole premise of this verb.
        var sshHelper = new Option<string?>("--ssh-helper") { Description = "Maintained SSH helper. Default scripts/connect_runpod_ssh.sh in this checkout." };
        var planOnly = new Option<bool>("--plan-only") { Description = "Probe and print the table; change nothing." };
        var fromWorktree = new Option<bool>("--from-worktree")
        {
            Description = "Build the code in front of you rather than HEAD; the binaries record themselves as built from a dirty tree. "
                + "Without it the install refuses while any build source differs from HEAD, which on a shared repository can be another lane's work.",
        };
        var role = new Option<ControlPlaneRole>("--role")
        {
            Description = "RegistryHome holds the Registry; WorkerSite reads it over a wire and must not create one.",
            Required = true,
        };
        foreach (var option in new Option[] { rootDirectory, sshHelper, planOnly, role, fromWorktree })
        {
            command.Options.Add(option);
        }

        command.SetAction(parsed =>
        {
            var baseDirectory = Path.GetFullPath(parsed.GetValue(rootDirectory)!);
            var paths = new ControlPlaneInstall.InstallPaths(
                Root: baseDirectory,
                // CHG 2026-09-26: the layout below --root follows the repository's
                // (Fern.Registry.RepositoryLayout): the Registry, the store, the work
                // root and the deployment root under runtime/, the profiles under config/.
                Registry: Path.Combine(baseDirectory, Fern.Registry.RepositoryLayout.RegistryFile),
                WorkRoot: Path.Combine(baseDirectory, Fern.Registry.RepositoryLayout.WorkDirectory),
                DeploymentRoot: Path.Combine(baseDirectory, Fern.Registry.RepositoryLayout.DeployDirectory),
                DeployRoot: Path.Combine(baseDirectory, Fern.Registry.RepositoryLayout.StoreDirectory),
                ProfilesRoot: Path.Combine(baseDirectory, Fern.Registry.RepositoryLayout.ProfilesDirectory),
                SshHelper: parsed.GetValue(sshHelper) ?? Path.Combine(Directory.GetCurrentDirectory(), "scripts", "connect_runpod_ssh.sh"));
            return ControlPlaneInstall.Run(
                paths, parsed.GetValue(planOnly), parsed.GetValue(role), AnsiConsole.Console, Console.Error,
                parsed.GetValue(fromWorktree));
        });

        parent.Subcommands.Add(command);

        // CHG 2026-09-13: the member installer's own verb, registered. Its name
        // has been in the refusal messages since the installer was written
        // ("pass --from-worktree"), and a test drives it, but nothing dispatched
        // to it: the composite above was its only caller and built the argument
        // list itself. An operator who wants the publish step alone -- to
        // rebuild the binaries without probing a machine's layout again -- now
        // has the door the messages already named.
        var members = new Command(
            Fern.Install.InstallCli.Verb,
            "Publish this machine's immutable control binaries with one receipt; probes nothing and starts nothing. "
            + "With --scheduler-program DIR it then removes old copies that nothing names; without it, it removes none.");
        members.TreatUnmatchedTokensAsErrors = false;
        var memberArguments = new Argument<string[]>("options") { Arity = ArgumentArity.ZeroOrMore, Description = "Flags of the member installer; run it with --help to read them." };
        members.Arguments.Add(memberArguments);
        // CHG 2026-09-26 (slice F6 of how-a-site-is-reached-and-started):
        // `--scheduler-program DIR` is read here, not by the installer, which
        // cannot hold the Gleam flow: the flow says which binaries loaded
        // units and Site declarations name, and those are kept. Without it, or
        // when it cannot be read, nothing old is removed.
        members.SetAction(parsed =>
        {
            // `--scheduler-program=DIR` is the same as `--scheduler-program DIR`.
            var arguments = (parsed.GetValue(memberArguments) ?? [])
                .SelectMany(argument => argument.StartsWith("--scheduler-program=", StringComparison.Ordinal)
                    ? new[] { "--scheduler-program", argument["--scheduler-program=".Length..] }
                    : [argument])
                .ToList();
            var at = arguments.IndexOf("--scheduler-program");
            IReadOnlySet<string>? named = null;
            if (at >= 0 && at + 1 < arguments.Count)
            {
                var shipment = arguments[at + 1];
                arguments.RemoveRange(at, 2);
                var profilesAt = arguments.IndexOf("--profiles-root");
                var profiles = profilesAt >= 0 && profilesAt + 1 < arguments.Count
                    ? arguments[profilesAt + 1]
                    : RepositoryLayout.ProfilesDirectory;
                var registryAt = arguments.IndexOf("--registry");
                try
                {
                    using var program = Fern.Scheduler.GleamProgram.FromShipment(shipment);
                    var kept = new HashSet<string>(Fern.Scheduler.SiteFiles.NamedDigests(
                        program, profiles, new Fern.Scheduler.SiteFiles.SystemctlUser().LoadedServices()), StringComparer.Ordinal);
                    // The compilers registered studies were compiled with are kept
                    // too: a paused unit names nothing, and its studies still need them.
                    if (registryAt >= 0 && registryAt + 1 < arguments.Count)
                    {
                        using var registry = RegistryHome.OpenReader(arguments[registryAt + 1]);
                        kept.UnionWith(RegistryOperations.ListStudyCompilerDigests(registry.Connection));
                    }
                    kept.UnionWith(Fern.Install.DeployRetention.RunningDigests());
                    named = kept;
                    Console.Error.WriteLine($"{Fern.Install.InstallCli.Verb}: {named.Count} published digest(s) named by loaded units, Site declarations, registered studies and running processes are kept");
                }
                catch (Exception error) when (error is Fern.Scheduler.GleamProgramException or InvalidOperationException or IOException or RegistryFault or Microsoft.Data.Sqlite.SqliteException)
                {
                    Console.Error.WriteLine($"{Fern.Install.InstallCli.Verb}: what loaded units and Site declarations name could not be read, so no old copy is removed: {error.Message}");
                }
            }
            return Fern.Install.InstallCli.Run([.. arguments], Console.Out, Console.Error, named);
        });
        parent.Subcommands.Add(members);

        var readiness = new Command(
            WorkerSiteReadiness.Verb,
            "Read whether a machine can be given cells: its profile, what the machine answers, and its registration; changes nothing");
        var profile = new Option<string>("--profile") { Description = "Execution profile id of the machine to inspect.", Required = true };
        var registry = new Option<string?>("--registry") { Description = "Registry Home to read registration from." };
        var actor = new Option<string>("--actor") { Description = "Who is asking.", DefaultValueFactory = _ => "operator" };
        foreach (var option in new Option[] { profile, registry, actor })
        {
            readiness.Options.Add(option);
        }

        readiness.SetAction(parsed => WorkerSiteReadiness.Run(
            new WorkerSiteReadiness.Target(
                Directory.GetCurrentDirectory(),
                parsed.GetValue(profile)!,
                // Unlike install-control-plane, naming --registry here is right:
                // this reads the Home's Registry rather than creating one under a
                // new root, so Program.cs appending the machine default is exactly
                // the behaviour wanted.
                parsed.GetValue(registry) ?? RegistryHome.DefaultRegistryPath,
                parsed.GetValue(actor)!),
            AnsiConsole.Console,
            Console.Error));
        parent.Subcommands.Add(readiness);
    }
}
