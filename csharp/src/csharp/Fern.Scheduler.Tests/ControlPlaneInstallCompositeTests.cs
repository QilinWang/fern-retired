using Fern.Run.Cli;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

/// <summary>
/// The composite that brings a machine from nothing to an installed control
/// plane. These tests drive it against a temporary root and stop at
/// <c>--plan-only</c>, because completing it publishes five executables.
///
/// What is worth pinning here is the decision, not the rendering: which steps a
/// bare directory reports, that a gap a person can work around does not stop the
/// run while a missing declared input does, and that preparing twice is a
/// no-op — an operator who runs this on a machine that is already half set up
/// must not be told it will recreate what already exists.
/// </summary>
public sealed class ControlPlaneInstallCompositeTests
{
    [Fact]
    public void ABareDirectoryReportsWhatWillBeCreatedAndThatTheMachineIsFree()
    {
        var root = Directory.CreateTempSubdirectory("fern-control-plane-").FullName;
        try
        {
            var console = new TestConsole().Width(200);
            var exitCode = Run(Paths(root), planOnly: true, console);

            // CHG 2026-09-11: the deployment lock exists now. A free machine
            // says so and names the file this run would hold; a machine another
            // install is holding blocks instead, which InstallLockTests covers.
            Assert.Contains("deployment lock", console.Output, StringComparison.Ordinal);
            Assert.Contains("free to install", console.Output, StringComparison.Ordinal);
            Assert.Contains(Fern.Install.InstallLock.FileName, console.Output, StringComparison.Ordinal);

            // Every directory under a fresh root is missing, and the Registry
            // Home with it.
            foreach (var step in new[] { "deployment root", "deploy store root", "execution profiles", "work root", "Registry Home" })
            {
                Assert.Contains(step, console.Output, StringComparison.Ordinal);
            }

            Assert.Contains("prepares", console.Output, StringComparison.Ordinal);

            // A gap the operator can work around is reported, not fatal.
            Assert.Equal(0, exitCode);
            Assert.False(
                Directory.Exists(Path.Combine(root, "deploy")),
                "--plan-only created a directory");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AMissingDeclaredInputBlocksTheRunAndAMissingGuardDoesNot()
    {
        var root = Directory.CreateTempSubdirectory("fern-control-plane-").FullName;
        try
        {
            var console = new TestConsole().Width(200);
            var withHelper = Run(Paths(root), planOnly: true, console);
            Assert.Equal(0, withHelper);

            // The SSH helper is a file of the checkout, so its absence means the
            // source tree is wrong; no instruction to the operator repairs that.
            var withoutHelper = Run(
                Paths(root) with { SshHelper = Path.Combine(root, "no-such-helper.sh") },
                planOnly: true,
                new TestConsole().Width(200));
            Assert.Equal(1, withoutHelper);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AlreadyPreparedStepsReportReadyRatherThanOfferingToRecreateThem()
    {
        var root = Directory.CreateTempSubdirectory("fern-control-plane-").FullName;
        try
        {
            foreach (var directory in new[] { "deploy", "store", "profiles", "work" })
            {
                Directory.CreateDirectory(Path.Combine(root, directory));
            }

            var console = new TestConsole().Width(200);
            Assert.Equal(0, Run(Paths(root), planOnly: true, console));
            Assert.DoesNotContain("create " + Path.Combine(root, "deploy"), console.Output, StringComparison.Ordinal);
            Assert.Contains("ready", console.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static ControlPlaneInstall.InstallPaths Paths(string root) => new(
        Root: root,
        Registry: Path.Combine(root, "lifecycle.db"),
        WorkRoot: Path.Combine(root, "work"),
        DeploymentRoot: Path.Combine(root, "deploy"),
        DeployRoot: Path.Combine(root, "store"),
        ProfilesRoot: Path.Combine(root, "profiles"),
        SshHelper: Path.Combine(TestRegistry.RepoRoot(), "scripts", "connect_runpod_ssh.sh"));

    [Fact]
    public void AWorkerInstallDoesNotInitialiseARegistry()
    {
        // A GPU worker is not Registry Home. The sequence was one fixed list
        // with an unconditional "Registry Home" step, so installing the control
        // plane on a rented pod created a second Registry there -- a second
        // authority for the one question the Registry exists to answer.
        var root = Directory.CreateTempSubdirectory("fern-worker-install-").FullName;
        try
        {
            var console = new TestConsole().Width(200);
            Run(Paths(root), planOnly: true, console, ControlPlaneRole.WorkerSite);

            Assert.DoesNotContain("Registry Home", console.Output, StringComparison.Ordinal);
            Assert.False(
                File.Exists(Path.Combine(root, "fern.db")),
                "a worker install must not create a Registry, even under --plan-only");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ARegistryHomeInstallStillInitialisesTheRegistry()
    {
        // The other half of the same decision: giving the command a role must
        // not quietly remove the step from the machine that does need it.
        var root = Directory.CreateTempSubdirectory("fern-home-install-").FullName;
        try
        {
            var console = new TestConsole().Width(200);
            Run(Paths(root), planOnly: true, console, ControlPlaneRole.RegistryHome);

            Assert.Contains("Registry Home", console.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static int Run(
        ControlPlaneInstall.InstallPaths paths,
        bool planOnly,
        IAnsiConsole console,
        ControlPlaneRole role = ControlPlaneRole.RegistryHome) =>
        ControlPlaneInstall.Run(paths, planOnly, role, console, TextWriter.Null);
}
