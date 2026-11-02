using System.Diagnostics;
using System.Text.Json;
using Fern.Registry;

namespace Fern.Install;

/// <summary>
/// `install-control-plane-members`: reads one strict InstallConfig JSON object from
/// stdin, installs, and prints the Installation as JSON. Exit 2 is a caller
/// mistake, 1 an installer refusal, 0 success.
/// </summary>
public static class InstallCli
{
    public const string Verb = "install-control-plane-members";

    /// <summary>
    /// Borrow a committed Registry publish only until its content-addressed publisher returns.
    /// CHG 2026-09-11 night (slice 10): the publisher is handed the Registry's
    /// input digest and a build thunk; it may answer with a published copy
    /// built from the same inputs and never call the thunk.
    /// </summary>
    public static int PublishRegistryFromCommit(Func<string, Func<string>, int> publish, TextWriter diagnostic)
    {
        using var build = new PreparedControlBuild();
        CommittedControlSource source;
        // The Registry binary writes accepted scientific facts, so it keeps the
        // hard rule: committed source only. That rule is about WHERE THE BYTES
        // COME FROM, not about whether anything else in the tree is uncommitted.
        // Routing it through SelectSource made it refuse on any dirty tree and
        // offer a --from-worktree exit this path does not accept.
        try { source = CommittedControlSource.Prepare(ReviewedWorkspace(), Path.Combine(build.Root, "source")); }
        catch (CommittedControlSource.GitTerminationException error) { throw build.RetainForGitTermination(error); }
        diagnostic.WriteLine(Provenance("Registry", source));
        var member = ControlMembers.Named("fern-registry");
        var inputDigest = ControlMembers.InputDigest(source, member, new ControlMembers.Toolchains(source.Root).For(member.Kind));
        var result = publish(inputDigest, () => PublishDotnet(source, member, Path.Combine(build.Root, "registry-publish")));
        build.Terminate();
        return result;
    }

    /// <summary>
    /// `publish-binary --member NAME`: build one control-plane member from the
    /// committed source and publish it by content address under the
    /// deployment root with a receipt beside it, unless a published copy was
    /// built from the same inputs, which is then the answer. Committed source
    /// only, like the Registry publish; the worktree exit is
    /// install-control-plane's `--from-worktree`.
    /// </summary>
    public static int PublishMember(ControlMember member, string deploymentRoot, bool rebuild, TextWriter stdout, TextWriter stderr)
    {
        using var build = new PreparedControlBuild();
        var workspace = ReviewedWorkspace();
        CommittedControlSource source;
        try { source = CommittedControlSource.Prepare(workspace, Path.Combine(build.Root, "source")); }
        catch (CommittedControlSource.GitTerminationException termination) { throw build.RetainForGitTermination(termination); }
        stderr.WriteLine(Provenance(member.Name, source));
        var root = AbsoluteFrom(workspace, deploymentRoot);
        var toolchain = new ControlMembers.Toolchains(source.Root).For(member.Kind);
        var digest = ControlMembers.InputDigest(source, member, toolchain);
        if (!rebuild && ControlMemberPublications.Find(root, member, digest) is { } published)
        {
            stderr.WriteLine($"{member.Name} unchanged at {published.Path}: published from the same inputs ({digest})");
            stdout.WriteLine(published.Path);
            build.Terminate();
            return 0;
        }
        var output = Path.Combine(build.Root, member.Name + "-publish");
        var built = member.Kind == ControlMemberKind.Gleam ? PublishGleam(source, member, output) : PublishDotnet(source, member, output);
        var (sha256, path, refusal) = DurableFiles.PublishBinary(built, Path.Combine(root, member.PublishDirectory), member.PublishPrefix);
        if (refusal is not null)
        {
            stderr.WriteLine($"publish immutable {member.Name} binary: {refusal}");
            return 1;
        }
        ControlMemberPublications.WriteReceipt(path!, member, sha256!, digest, source, toolchain, "fern-run publish-binary");
        stderr.WriteLine($"{member.Name} published at {path} from inputs {digest}");
        stdout.WriteLine(path);
        build.Terminate();
        return 0;
    }

    /// <summary>
    /// CHG 2026-09-10: one argument shape, not two. This verb used to have a
    /// second body that read an InstallConfig as JSON from stdin, chosen when
    /// no flag was passed. It was the Go installer's shape (Installer.cs still
    /// notes "Go: InstallConfig in install.go") and the Go binary is retired:
    /// fern-run set FERN_INSTALL_OPERATOR_MODE=1 to steer around it, and the
    /// only callers that reached it were two tests. The usage line it printed
    /// for a stray argument was unreachable — the operator branch above it
    /// already returned on any argument at all.
    /// </summary>
    /// <param name="namedElsewhere">
    /// CHG 2026-09-26 (slice F6): the digests loaded units and Site declarations
    /// name, read by the caller through the Gleam flow. Without them the old
    /// copies are not cleaned: what is in use cannot be told from what is not.
    /// </param>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, IReadOnlySet<string>? namedElsewhere = null) =>
        RunOperator(args, stdout, stderr, namedElsewhere);

    static int RunOperator(string[] args, TextWriter stdout, TextWriter stderr, IReadOnlySet<string>? namedElsewhere)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            PrintOperatorUsage(stdout);
            return 0;
        }
        var parsed = ParseOperatorArgs(args, stderr);
        if (parsed is null)
        {
            return 2;
        }

        try
        {
            using var build = new PreparedControlBuild();
            var workspace = ReviewedWorkspace();
            // CHG: bootstrap provenance is not the identity of the source it
            // installs. Resolve HEAD once and publish only verified committed
            // bytes, while runtime store paths remain relative to workspace.
            CommittedControlSource source;
            try { source = SelectSource(workspace, Path.Combine(build.Root, "source"), parsed.FromWorktree); }
            catch (CommittedControlSource.GitTerminationException error)
            {
                throw build.RetainForGitTermination(error);
            }
            var sourceRevision = source.Commit + "+archive:" + source.ArchiveSha256;
            stderr.WriteLine(Provenance("control set", source));
            var deploymentRoot = AbsoluteFrom(workspace, parsed.DeploymentRoot);

            // CHG 2026-09-11 night (slice 10 of the cell port): a member is
            // built only when no published copy under the deployment root
            // carries the same input digest (ControlMembers: its project
            // closure, the root build inputs, the recipe, the toolchain). A
            // worktree build neither reuses nor is reused; its receipt says
            // dirty. --rebuild builds every member regardless. A member the
            // operator named by path is taken as given and has no digest.
            var toolchains = new ControlMembers.Toolchains(source.Root);
            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            string Provide(string name, Func<ControlMember, string> buildMember)
            {
                var member = ControlMembers.Named(name);
                if (!source.Dirty && CommittedControlSource.DifferingInputs(workspace, source, member) is { Count: > 0 } differing)
                {
                    throw new InvalidOperationException(
                        $"{name}: its own build sources differ from HEAD, so a committed build would not be the code in "
                        + $"front of you:\n    {string.Join("\n    ", differing)}\n"
                        + "  override:  pass --from-worktree; the binary records itself as built from a dirty tree\n"
                        + "  recompute: commit those files, then run this again. An edit outside this member's own "
                        + "projects does not reach here.");
                }
                var digest = ControlMembers.InputDigest(source, member, toolchains.For(member.Kind));
                inputs[name] = digest;
                if (!source.Dirty && !parsed.Rebuild && ControlMemberPublications.Find(deploymentRoot, member, digest) is { } published)
                {
                    stderr.WriteLine($"{name}: reusing {published.Path}, published from the same inputs ({digest})");
                    return published.Path;
                }
                stderr.WriteLine($"{name}: building; no published copy under {deploymentRoot} was built from inputs {digest}");
                return buildMember(member);
            }

            // CellPhaseSite now constructs typed JsonNode payloads. The
            // linker cannot prove that JsonValue factories needed for
            // those payloads survive trimming, and treating IL2026 as an
            // error is the correct refusal. Keep this member self-
            // contained and single-file, but do not make an unproven
            // trimming/AOT claim in the production installation set.
            var site = Provide("fern-site", member => PublishDotnet(source, member, Path.Combine(build.Root, "site-publish")));
            // CHG 2026-09-14: the Stage runner is no longer built or installed.
            // Its two verbs answered the Rust-era stage protocol -- a capability
            // probe and a check of one stage's returned artifacts -- and the
            // contracts behind both retired with the plan layer
            // (retired/csharp/rust-era-experiment-types-2026-09-14/). A cell's
            // result is read by the worker that ran it.
            // `fern-experiment` is a separate private subprocess, not an
            // alias of the human fern-run command: Stage-plan lowering still
            // execs its typed stdin/stdout contract. Build both operator
            // members here instead of publishing this process's apphost. A
            // normal `dotnet build` apphost needs its adjacent DLL; the
            // installer copies one content-addressed file, so only a
            // self-contained single-file publish can be an installed member.
            var experimentBuilt = parsed.ExperimentBinary is { Length: > 0 } declaredExperiment
                ? AbsoluteFrom(workspace, declaredExperiment)
                : Provide("fern-experiment", member => PublishDotnet(source, member, Path.Combine(build.Root, "experiment-publish")));
            var runBuilt = Provide("fern-run", member => PublishDotnet(source, member, Path.Combine(build.Root, "run-publish")));
            // The card compiler, built from the same committed bytes as the
            // C# members. It is a separate toolchain, not a separate source: a
            // machine whose fern-compile came from another commit would mint
            // identities the registered cells were not compiled under.
            // CHG 2026-09-27: the Gleam program, packed as one escript (PublishGleam).
            var compileBuilt = Provide("fern-compile", member => PublishGleam(source, member, Path.Combine(build.Root, "compile-publish")));
            var config = new InstallConfig
            {
                SourceSiteBinary = site,
                SourceExperimentBinary = experimentBuilt,
                SourceRunBinary = runBuilt,
                SourceCompileBinary = compileBuilt,
                SourceRevision = sourceRevision,
                MemberInputs = inputs,
                BuildInventory = Path.Combine(deploymentRoot, "build-inventory.db"),
                Registry = AbsoluteFrom(workspace, parsed.Registry),
                WorkRoot = AbsoluteFrom(workspace, parsed.WorkRoot),
                DeployRoot = AbsoluteFrom(workspace, parsed.DeployRoot),
                ProfilesRoot = AbsoluteFrom(workspace, parsed.ProfilesRoot),
                SshHelper = AbsoluteFrom(workspace, parsed.SshHelper),
                DeploymentRoot = deploymentRoot,
            };
            return ControlPlaneInstaller.Install(config) switch
            {
                Refused refused => Refuse(refused.Detail),
                Accepted<Installation> installed => Report(installed.Value),
            };

            int Refuse(string detail)
            {
                stderr.WriteLine(detail);
                return 1;
            }

            int Report(Installation installation)
            {
                // Each member built or reused from committed inputs gets its
                // receipt beside the published copy, once; a copy published
                // earlier keeps the receipt it has.
                foreach (var (name, path) in new[]
                {
                    ("fern-site", installation.SiteBinaryPath),
                    ("fern-experiment", installation.ExperimentBinaryPath),
                    ("fern-run", installation.RunBinaryPath),
                    ("fern-compile", installation.CompileBinaryPath),
                })
                {
                    if (!inputs.TryGetValue(name, out var digest)) continue;
                    var member = ControlMembers.Named(name);
                    if (ControlMemberPublications.WriteReceipt(path, member, DurableFiles.FileSha256(path), digest, source, toolchains.For(member.Kind), "fern-run install-control-plane"))
                    {
                        stderr.WriteLine($"{name}: receipt {ControlMemberPublications.ReceiptPath(path)}");
                    }
                }
                // The public success response is the build operation's
                // termination receipt, not merely its durable-installation
                // receipt. Do not print it while the prepared publish/artifacts
                // world is still owned by this process. If cleanup cannot be
                // proved, refuse and name that retained root instead.
                build.Terminate();
                PrintInstallation(installation, stdout, deploymentRoot, config.Registry, namedElsewhere);
                return 0;
            }
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            stderr.WriteLine(error.Message);
            return 1;
        }
    }

    sealed record OperatorArgs(
        string Registry,
        string WorkRoot,
        string DeploymentRoot,
        // The three stores the installed set reads at run time. They default
        // to the installing tree's, which is what an ordinary install wants,
        // but they are separate flags because the set is bound to content and
        // may be served from anywhere the stores exist.
        string DeployRoot,
        string ProfilesRoot,
        string SshHelper,
        // Null means the operator named no path, which is now the normal case:
        // the Stage runner is built here rather than picked up from a Rust
        // build output. An explicit --stage-runner-binary still wins.
        // Null means the operator named no path: the installer publishes the
        // distinct Fern.Experiment.Cli executable itself. An explicit
        // --experiment-binary still wins.
        string? ExperimentBinary,
        bool FromWorktree,
        // Build every member even when a published copy carries the same
        // input digest.
        bool Rebuild);

    static OperatorArgs? ParseOperatorArgs(IReadOnlyList<string> args, TextWriter stderr)
    {
        string? registry = null;
        var workRoot = Fern.Registry.RepositoryLayout.WorkerWorkRoot; // CHG 2026-09-26: was tmp/fern-run-worker
        // CHG 2026-09-26: deploy/ moved to runtime/deploy/ and profiles/ to config/profiles/ (Fern.Registry.RepositoryLayout).
        var deploymentRoot = Fern.Registry.RepositoryLayout.DeployDirectory;
        var deployRoot = Fern.Registry.RepositoryLayout.DeployDirectory;
        var profilesRoot = Fern.Registry.RepositoryLayout.ProfilesDirectory;
        var sshHelper = "scripts/connect_runpod_ssh.sh";
        string? experiment = null;
        var fromWorktree = false;
        var rebuild = false;
        for (var i = 0; i < args.Count; i++)
        {
            var flag = args[i];
            if (flag == "--from-worktree") { fromWorktree = true; continue; }
            if (flag == "--rebuild") { rebuild = true; continue; }
            if (flag is not ("--registry" or "--work-root" or "--deployment-root"
                or "--deploy-root" or "--profiles-root" or "--ssh-helper"
                or "--experiment-binary"))
            {
                stderr.WriteLine($"unknown flag {flag}");
                PrintOperatorUsage(stderr);
                return null;
            }
            if (i + 1 >= args.Count)
            {
                stderr.WriteLine($"missing value for {flag}");
                return null;
            }
            var value = args[++i];
            switch (flag)
            {
                case "--registry": registry = value; break;
                case "--work-root": workRoot = value; break;
                case "--deployment-root": deploymentRoot = value; break;
                case "--deploy-root": deployRoot = value; break;
                case "--profiles-root": profilesRoot = value; break;
                case "--ssh-helper": sshHelper = value; break;
                case "--experiment-binary": experiment = value; break;
            }
        }
        return new OperatorArgs(
            registry ?? Fern.Registry.RegistryHome.DefaultRegistryPath, workRoot, deploymentRoot,
            deployRoot, profilesRoot, sshHelper,
            experiment, fromWorktree, rebuild);
    }

    static void PrintOperatorUsage(TextWriter output)
    {
        output.WriteLine("Publish this machine's immutable control binaries with one receipt; starts nothing");
        output.WriteLine("  --from-worktree  build the code in front of you rather than HEAD; the binary records");
        output.WriteLine("                   itself as built from a dirty tree");
        output.WriteLine("  --rebuild        build every member, even one whose published copy was built from");
        output.WriteLine("                   the same inputs (by default such a copy is reused)");
        output.WriteLine();
        output.WriteLine($"Usage: fern-run {Verb} [OPTIONS]");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine($"      --registry <REGISTRY>                    Registry Home database this installed set is recorded against [default: selected runtime Registry]");
        output.WriteLine("      --work-root <WORK_ROOT>                  Attempt handoff and worker work root recorded in the receipt [default: runtime/tmp/fern-run-worker]");
        output.WriteLine("      --deployment-root <DEPLOYMENT_ROOT>      Root for immutable local binaries and installation receipts [default: runtime/deploy]");
        output.WriteLine("      --deploy-root <DEPLOY_ROOT>              Content-addressed Stage source store the supervisor reads [default: runtime/deploy]");
        output.WriteLine("      --profiles-root <PROFILES_ROOT>          Directory of execution profile JSON files [default: config/profiles]");
        output.WriteLine("      --ssh-helper <SSH_HELPER>                Maintained helper an SSH Worker Site is reached through [default: scripts/connect_runpod_ssh.sh]");
        output.WriteLine("      --experiment-binary <EXPERIMENT_BINARY>  Reviewed experiment compiler [default: published from src/csharp/Fern.Experiment.Cli]");
        output.WriteLine("  -h, --help                                   Print help");
    }


    /// <summary>
    /// The commit, or the tree when the operator asks for it by flag.
    ///
    /// A control binary is a tool, not a scientific fact: a Cell's science is
    /// fixed by its frozen implementation source, which is content-addressed
    /// and verified on its own. So a dirty tree is a thing to RECORD, never a
    /// reason to refuse an installation. The receipt carries the difference and
    /// `fern-run version` prints "(dirty tree)" after the commit.
    /// </summary>
    static CommittedControlSource SelectSource(string workspace, string destination, bool fromWorktree)
    {
        // CHG 2026-09-18 (owner): the tree as a whole is nobody's business
        // here. Committed bytes are the source; whether THIS member's own
        // inputs are clean is checked per member, where the answer changes a
        // byte, by Provide below.
        if (fromWorktree) return CommittedControlSource.PrepareFromWorktree(workspace, destination);
        return CommittedControlSource.Prepare(workspace, destination);
    }

    static string Provenance(string what, CommittedControlSource source) => source.Dirty
        ? $"building {what} from the working tree at {source.Commit}; the binary records itself as built from a dirty tree"
        : $"building {what} from committed source {source.Commit}; uncommitted changes are excluded";

    static string ReviewedWorkspace()
    {
        var declared = Environment.GetEnvironmentVariable("FERN_SOURCE_ROOT");
        var root = Path.GetFullPath(string.IsNullOrEmpty(declared) ? Directory.GetCurrentDirectory() : declared);
        if (!File.Exists(Path.Combine(root, "python", "fern_train", "worker.py")))
        {
            throw new InvalidOperationException($"reviewed Fern workspace {root} does not contain python/fern_train/worker.py");
        }
        return root;
    }

    static string AbsoluteFrom(string workspace, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspace, path));

    /// <summary>
    /// CHG 2026-09-11 night (slice 10): the fixed publish arguments are the
    /// member's recipe (ControlMembers.Recipe), which the input digest names,
    /// so the recipe recorded is the recipe run; the single-file note moved
    /// there with them.
    /// </summary>
    static string PublishDotnet(CommittedControlSource source, ControlMember member, string output)
    {
        var artifacts = output + "-artifacts";
        var arguments = new List<string>(ControlMembers.Recipe(member))
        {
            // Installation is a bounded immutable build, not another writer
            // to the checkout's shared bin/obj tree. A dedicated artifacts
            // root prevents concurrent developer builds or a symlinked
            // reviewed source root from deleting bundle inputs while this
            // publish is still consuming them.
            "--artifacts-path", artifacts,
            "-o", output,
            $"-p:FernCommittedSourceRevision={source.Commit}",
            // A dirty build has a base commit and NO committed-source archive:
            // claiming one asserts "these are exactly commit X's files" while
            // the stamp says they are not, and that contradiction is what the
            // registry publisher refuses. Saying less is saying the truth.
            $"-p:FernCommittedSourceArchiveSha256={(source.Dirty ? "" : source.ArchiveSha256)}",
            $"-p:FernSourceWasDirty={(source.Dirty ? "1" : "0")}",
            Path.Combine(source.Root, member.Project),
        };
        RunBuild("dotnet", arguments, source.Root, $"dotnet publish {member.Project}");
        var path = Path.Combine(output, member.Name);
        RequireExecutable(path, member.Name);
        return path;
    }

    /// <summary>
    /// Builds one Go module from the committed source and returns the single
    /// executable it produced.
    /// </summary>
    /// <remarks>
    /// <c>GOFLAGS=-mod=readonly</c>: the build may fill the module cache but
    /// never rewrites the committed go.mod or go.sum (CHG 2026-09-11: was
    /// <c>-mod=mod</c>, which would have done so silently); the tree carries
    /// no vendor directory. <c>GOOS/GOARCH</c> are pinned to linux/amd64 so a set built
    /// on one machine installs on the pod. <c>-C</c> keeps the module root out
    /// of the working directory, exactly as the module's own README builds it.
    /// </remarks>
    /// <summary>
    /// CHG 2026-09-27 (the Go compiler retires): a Gleam member is exported as an
    /// Erlang shipment and packed into one escript, <c>build/fern-compile</c>
    /// in the member's directory, which is the file published. Running it needs
    /// Erlang on the machine, as the Gleam Site and scheduler programs do.
    /// </summary>
    static string PublishGleam(CommittedControlSource source, ControlMember member, string output)
    {
        Directory.CreateDirectory(output);
        var project = Path.Combine(source.Root, member.Project);
        RunBuild("gleam", ["export", "erlang-shipment"], project, $"gleam export erlang-shipment {member.Project}");
        RunBuild("gleam", ["run", "-m", "export_executable"], project, $"gleam run -m export_executable {member.Project}");
        var packed = Path.Combine(project, "build", member.Name);
        var path = Path.Combine(output, member.Name);
        File.Copy(packed, path, overwrite: true);
        RequireExecutable(path, member.Name);
        return path;
    }

    static void RunBuild(
        string program,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string label,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = program,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var (name, value) in environment) process.StartInfo.Environment[name] = value;
        }
        if (string.Equals(program, "dotnet", StringComparison.Ordinal))
        {
            // An installer subprocess must own the lifetime of every build
            // process it starts. A reusable MSBuild node can otherwise retain
            // the redirected stdout/stderr handles after `dotnet publish`
            // exits, so the installer waits forever for EOF even though the
            // publish process itself is already gone.
            process.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        }
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception missing)
        {
            // Naming the toolchain is the difference between "installation
            // failed" and "install Go on this machine".
            throw new InvalidOperationException(
                $"{label} could not start: {program} is not on this machine's PATH ({missing.Message})");
        }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(600_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new InvalidOperationException($"{label} exceeded its ten-minute deadline");
        }
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0)
        {
            var standardError = BoundedBuildOutput(stderr.Result);
            var standardOutput = BoundedBuildOutput(stdout.Result);
            throw new InvalidOperationException(
                $"{label} failed with exit status {process.ExitCode}; stderr: {standardError}; stdout: {standardOutput}");
        }
    }

    static string BoundedBuildOutput(string value)
    {
        const int maximumCharacters = 8_192;
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return "<empty>";
        }
        return trimmed.Length <= maximumCharacters
            ? trimmed
            : "<earlier output omitted> " + trimmed[^maximumCharacters..];
    }

    static void RequireExecutable(string path, string role)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"{role} {path} is not a regular file");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                throw new InvalidOperationException($"{role} {path} has no executable bit");
        }
    }

    public static void PrintInstallation(Installation installation, TextWriter output,
        string? deploymentRoot = null, string? registryPath = null, IReadOnlySet<string>? namedElsewhere = null)
    {
        output.WriteLine($"immutable fern-site binary {installation.SiteBinaryPath}");
        output.WriteLine($"immutable fern-experiment binary {installation.ExperimentBinaryPath}");
        output.WriteLine($"immutable fern-run binary {installation.RunBinaryPath}");
        output.WriteLine($"immutable fern-compile binary {installation.CompileBinaryPath}");
        output.WriteLine($"active binary set {installation.BinarySetId}");
        output.WriteLine($"installation receipt {installation.ReceiptPath}");
        output.WriteLine("published only; this command starts nothing");
        // Owner, 2026-09-18: "install new must delete old, and don't store
        // garbage." What this set names is protected, as is the newest copy of
        // each member beyond it; the rest of a root that had reached 33 GiB goes.
        if (deploymentRoot is { Length: > 0 } root)
        {
            // CHG 2026-09-26 (slice F6): nothing is cleaned without knowing what
            // loaded units and Site declarations name, and every removal is
            // printed by name.
            if (namedElsewhere is null)
            {
                output.WriteLine("superseded: not cleaned; which binaries loaded units and Site declarations name was not read "
                    + "(fern-run install-control-plane-members --scheduler-program DIR reads it)");
            }
            else
            {
                var retention = DeployRetention.Compute(root, keep: 2, namedElsewhere);
                if (retention.Removals.Count != 0)
                {
                    var freed = DeployRetention.Apply(retention);
                    output.WriteLine($"superseded: removed {retention.Removals.Count} file(s), {freed / 1024d / 1024 / 1024:F2} GiB freed");
                    foreach (var removal in retention.Removals) output.WriteLine($"  removed {removal.Path} ({removal.Reason})");
                }
            }
            foreach (var link in DeployRetention.RefreshCurrentLinks(root)) output.WriteLine($"current: {link}");
        }
        foreach (var line in SelectionFollowUp(installation, deploymentRoot, registryPath))
            output.WriteLine(line);
    }

    /// <summary>
    /// CHG 2026-09-14: what stands between publishing a set and running it.
    ///
    /// Publishing writes new bytes; SELECTING is the separate act that makes
    /// them the bytes this machine reads, and the two are separate on purpose:
    /// a tool may be published from a dirty tree, and only a deliberate act
    /// moves what every caller runs. What that separation cost was a trap: the
    /// install printed "active binary set X" and then left every caller on the
    /// previous set with nothing saying so, and its closing line named
    /// run-study-cells, which runs cells against the set the install did NOT
    /// just publish. A compiler word added, published and tested the same
    /// afternoon was refused by the binary still selected.
    ///
    /// So the install now names the set that is selected until someone acts,
    /// and writes the select-runtime line with every argument filled in. It
    /// holds three of the four itself; the fourth, the Registry binary, is the
    /// one the current selection already names, because fern-registry is
    /// published by its own verb and is not a member of this set.
    /// </summary>
    internal static IEnumerable<string> SelectionFollowUp(Installation installation,
        string? deploymentRoot, string? registryPath)
    {
        // CHG 2026-09-14, second pass: the command is printed with the operator's
        // absolute path, not the bare word `fern-run`. A control binary is never
        // on PATH in this repository -- it is content-addressed and named by the
        // runtime selection, which is the point -- so a bare word is a line the
        // reader cannot run, and the first person to paste this one got
        // "fish: Unknown command: fern-run". The path used is the operator just
        // published: it exists whether or not this machine has a selection, and
        // it is the binary built from the same source as the receipt it names.
        var operatorPath = installation.RunBinaryPath;
        var runCells = $"then: {operatorPath} run-study-cells --store <registry> --study <name> --site <this machine's Site>";
        if (deploymentRoot is null || registryPath is null)
        {
            yield return $"next: select this set with `{operatorPath} select-runtime`, then run-study-cells";
            yield break;
        }
        var root = Path.GetFullPath(deploymentRoot);
        var selectionPath = Path.Combine(root, "current", "runtime.json");
        RuntimeSelection? current = null;
        try { if (File.Exists(selectionPath)) current = RuntimeSelection.Read(selectionPath); }
        catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or RegistryFault)
        {
            // A selection this process cannot read is still a reason to print
            // the command: naming it is the whole point, and refusing here
            // would fail an install whose bytes are already published.
            current = null;
        }

        if (current is not null && current.BinarySetId == installation.BinarySetId)
        {
            yield return $"the runtime selection already names this set; {selectionPath} needs no change";
            yield return runCells;
            yield break;
        }
        yield return current is null
            ? $"NOT YET SELECTED: this machine has no readable runtime selection at {selectionPath}, so nothing reads the set above"
            : $"NOT YET SELECTED: until the line below runs, this machine reads {current.BinarySetId}"
              + $" (fern-compile {Path.GetFileName(current.CompileBinary)})";
        var registryBinary = current?.RegistryBinary
            ?? $"<a fern-registry published by `{operatorPath} publish-registry-binary --build`>";
        yield return "next, select it:";
        yield return $"  {operatorPath} select-runtime --receipt {installation.ReceiptPath}"
            + $" --registry {Path.GetFullPath(registryPath)}"
            + $" --registry-binary {registryBinary}"
            + $" --deployment-root {root}";
        yield return runCells;
    }

    internal sealed class PreparedControlBuild : IDisposable
    {
        bool terminated;
        bool retained;

        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"fern-control-build-{Environment.ProcessId}-{Guid.NewGuid():N}");
        public PreparedControlBuild() => Directory.CreateDirectory(Root);

        internal InvalidOperationException RetainForGitTermination(CommittedControlSource.GitTerminationException error)
        {
            retained = true;
            return new InvalidOperationException($"{error.Message}; temporary build root retained at {Root}", error);
        }

        public void Terminate()
        {
            if (retained)
                throw new InvalidOperationException($"Git termination is unconfirmed; temporary build root retained at {Root}");
            if (terminated)
            {
                return;
            }
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"prepared control build root {Root} could not be released: {error.Message}",
                    error);
            }
            if (Directory.Exists(Root))
            {
                throw new InvalidOperationException($"prepared control build root {Root} still exists after cleanup");
            }
            terminated = true;
        }

        public void Dispose()
        {
            if (terminated || retained)
            {
                return;
            }
            // Failure cleanup is best effort so it cannot replace the
            // operation's original refusal. The accepted path always calls
            // Terminate explicitly and never reaches this fallback.
            try { Directory.Delete(Root, recursive: true); } catch (Exception) { }
        }
    }
}
