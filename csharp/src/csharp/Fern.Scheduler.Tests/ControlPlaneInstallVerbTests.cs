using Fern.Registry;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

/// <summary>
/// The installer exists in the library, but until a command dispatches to it
/// nothing can install anything. These prove the operator's verb reaches the
/// installer rather than a usage banner: each case asserts a message only the
/// installer's own boundary can produce.
///
/// CHG 2026-09-11: the cases used to spawn `fern-scheduler-cs`, which retired
/// with the daemon. `install-control-plane` in fern-run is the one door now,
/// and the argument boundary is asserted where it lives.
/// </summary>
public sealed class InstallVerbReachableTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InstallerHelpDoesNotAdvertiseRetiredSurfaces(bool rootHelp)
    {
        var arguments = rootHelp ? new[] { "--help" } : new[] { "install-control-plane", "--help" };
        var result = CorrectionsDigestVerbTests.RunFernRun(arguments);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("install-control-plane", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("install-scheduler-service", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("scheduler/reconcile", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("install-knowledge-reconcile-service", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// When a build source differs from HEAD the member installer refuses and
    /// names --from-worktree as the override. Until 2026-09-12 no reachable
    /// command accepted that flag: the member installer's own verb is
    /// registered nowhere, and the composite built its argument list without
    /// it. Several lanes share this repository, so one lane's uncommitted C#
    /// left every machine uninstallable with a message pointing at an exit
    /// that did not exist. This is that exit, at the door an operator uses.
    /// </summary>
    [Fact]
    public void TheOverrideTheRefusalNamesIsReachableFromTheInstallVerb()
    {
        var help = CorrectionsDigestVerbTests.RunFernRun("install-control-plane", "--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("--from-worktree", help.StandardOutput, StringComparison.Ordinal);

        var root = Path.Combine(Path.GetTempPath(), "fern-install-worktree-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var planned = CorrectionsDigestVerbTests.RunFernRun(
                "install-control-plane", "--root", root, "--role", "RegistryHome", "--plan-only", "--from-worktree");
            Assert.DoesNotContain("Unrecognized command or argument", planned.StandardError, StringComparison.Ordinal);
            Assert.Contains("deployment root", planned.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SuccessfulInstallationReportsOnlyInstalledMembersAndExplicitActivation()
    {
        var installation = new Installation(
            "/test/receipt.json", "/test/site", "/test/experiment", "/test/run",
            "/test/compile", "test-set");
        using var output = new StringWriter();
        InstallCli.PrintInstallation(installation, output);
        var text = output.ToString();
        foreach (var member in new[] { "/test/site", "/test/experiment", "/test/run", "/test/compile" })
            Assert.Contains(member, text, StringComparison.Ordinal);
        Assert.Contains("published only; this command starts nothing", text, StringComparison.Ordinal);
        Assert.DoesNotContain("systemctl", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fern-card", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Knowledge reconcile", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// CHG 2026-09-14: publishing a set is not running it. The install used to
    /// print "active binary set X" and then name run-study-cells, while every
    /// caller stayed on the previously SELECTED set -- so a compiler word added,
    /// published and tested the same afternoon was refused by the binary still
    /// in use. The report now names what is selected until someone acts, and
    /// writes the select-runtime line with all four arguments filled in.
    /// </summary>
    [Fact]
    public void AnInstallThatIsNotYetSelectedNamesWhatStillRunsAndHowToMoveIt()
    {
        var installation = new Installation(
            "/deploy/control-plane-installations/home/new-set.json", "/test/site", "/test/experiment",
            "/test/run", "/test/compile", "sha256:new-set");
        using var root = new ScratchDeployment("sha256:old-set");

        var lines = InstallCli.SelectionFollowUp(installation, root.Path, "/home/user/study/lifecycle.db").ToList();
        var text = string.Join("\n", lines);

        Assert.Contains("NOT YET SELECTED", text, StringComparison.Ordinal);
        Assert.Contains("sha256:old-set", text, StringComparison.Ordinal);
        // The operator's absolute path, never the bare word: no control binary
        // is on PATH here, so a bare `fern-run` is a line the reader cannot run.
        Assert.Contains("/test/run select-runtime", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" fern-run select-runtime", text, StringComparison.Ordinal);
        // Every argument select-runtime requires, filled in: nobody reconstructs a path.
        Assert.Contains("--receipt /deploy/control-plane-installations/home/new-set.json", text, StringComparison.Ordinal);
        Assert.Contains("--registry /home/user/study/lifecycle.db", text, StringComparison.Ordinal);
        Assert.Contains($"--registry-binary {root.RegistryBinary}", text, StringComparison.Ordinal);
        Assert.Contains($"--deployment-root {root.Path}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstallThatIsAlreadySelectedSaysSoAndAsksForNothing()
    {
        var installation = new Installation(
            "/deploy/receipt.json", "/test/site", "/test/experiment", "/test/run", "/test/compile", "sha256:same-set");
        using var root = new ScratchDeployment("sha256:same-set");

        var text = string.Join("\n", InstallCli.SelectionFollowUp(installation, root.Path, "/home/user/study/lifecycle.db"));

        Assert.Contains("already names this set", text, StringComparison.Ordinal);
        Assert.DoesNotContain("select-runtime", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT YET SELECTED", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMachineWithNoSelectionIsToldThatNothingReadsWhatWasJustPublished()
    {
        var installation = new Installation(
            "/deploy/receipt.json", "/test/site", "/test/experiment", "/test/run", "/test/compile", "sha256:first-set");
        var empty = Path.Combine(Path.GetTempPath(), "fern-empty-deploy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var text = string.Join("\n", InstallCli.SelectionFollowUp(installation, empty, "/home/user/study/lifecycle.db"));
            Assert.Contains("no readable runtime selection", text, StringComparison.Ordinal);
            Assert.Contains("publish-registry-binary", text, StringComparison.Ordinal);
        }
        finally { Directory.Delete(empty, recursive: true); }
    }

    /// <summary>
    /// A deployment root laid out the way select-runtime lays one out: the
    /// selection lives in a directory named by the sha256 of its own bytes and
    /// `current` is a symbolic link to it, because RuntimeSelection.Read
    /// refuses a selection whose directory does not carry that identity, and
    /// refuses one naming files that are absent.
    /// </summary>
    sealed class ScratchDeployment : IDisposable
    {
        public string Path { get; }
        public string RegistryBinary { get; }

        public ScratchDeployment(string binarySetId)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fern-deploy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            string Touch(string name)
            {
                var path = System.IO.Path.Combine(Path, name);
                File.WriteAllText(path, name);
                return path;
            }
            RegistryBinary = Touch("fern-registry.old");
            var selection = new RuntimeSelection(RuntimeSelection.CurrentSchema, Touch("lifecycle.db"),
                Touch("fern-run.old"), RegistryBinary, Touch("fern-experiment.old"), Touch("fern-compile.old"),
                Touch("receipt.json"), binarySetId, new string('a', 64), new string('b', 40), "test publisher");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(selection);
            var identity = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var directory = System.IO.Path.Combine(Path, "runtime-selections", identity);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(System.IO.Path.Combine(directory, "runtime.json"), bytes);
            Directory.CreateSymbolicLink(System.IO.Path.Combine(Path, "current"), directory);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>
    /// CHG 2026-09-10: the boundary this proves is the flag parser, not a JSON
    /// reader. The verb used to accept an InstallConfig on stdin when no flag
    /// was passed; that second argument shape is gone, so an unknown INPUT is
    /// now an unknown flag.
    /// </summary>
    [Fact]
    public void TheInstallerNamesAnUnknownFlagAndWritesNothing()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = InstallCli.Run(["--not-a-flag", "x"], stdout, stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("unknown flag --not-a-flag", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout.ToString().Trim());
    }

    /// <summary>
    /// CHG 2026-09-10: this used to drive the installer through the verb by
    /// piping a whole InstallConfig as JSON. With one argument shape left, the
    /// verb reaches the installer only after publishing four executables, which
    /// no test may do, so the installer's first boundary is asserted where it
    /// lives.
    /// </summary>
    [Fact]
    public void TheInstallerRefusesAMissingRegistryHomeBeforeTouchingAnythingElse()
    {
        var outcome = ControlPlaneInstaller.Install(new InstallConfig
        {
            SourceSiteBinary = "/nowhere/fern-site",
            SourceExperimentBinary = "/nowhere/fern-experiment",
            SourceRunBinary = "/nowhere/fern-run",
            SourceCompileBinary = "/nowhere/fern-compile",
            SourceRevision = "0000000000000000000000000000000000000000",
            BuildInventory = "/nowhere/build-inventory.db",
            Registry = "/nowhere/lifecycle.db",
            WorkRoot = "/nowhere/work",
            DeployRoot = "/nowhere/deploy",
            ProfilesRoot = "/nowhere/profiles",
            SshHelper = "/nowhere/scripts/connect_runpod_ssh.sh",
            DeploymentRoot = "/nowhere/deploy",
        });

        Assert.Contains(
            "Registry Home database",
            UnionCase.Of<Refused>(outcome).Detail,
            StringComparison.Ordinal);
    }
}
