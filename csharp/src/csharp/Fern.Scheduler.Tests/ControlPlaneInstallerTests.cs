using System.Security.Cryptography;
using System.Text.Json;
using Fern.Registry;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Ports of tools/fern-control/scheduler/install_test.go at c2084f3d, one
/// test per Go test function with the same assertions.
/// </summary>
/// <remarks>
/// CHG 2026-09-11: the daemon, its systemd unit and the `fern-scheduler-cs`
/// member left with the Plan layer. What these prove is unchanged and is what
/// they always were: the installed set points at immutable published bytes,
/// the receipt names each member's digest, the build inventory activates
/// exactly that set, and a preflight refusal writes nothing.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ControlPlaneInstallerTests
{
    [Fact]
    public void InstallingActivatesExactlyThePublishedSet()
    {
        var root = Directory.CreateTempSubdirectory("fern-install-").FullName;
        var registry = Path.Combine(root, "campaign", "lifecycle.db");
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        File.WriteAllText(registry, "fixture registry");
        var siteSource = ProbeFixtures.WriteControlProbe(InstallationIdentityContract.WorkerSiteRole);
        var experimentSource = ProbeFixtures.WriteProbe("exit 0");
        var runSource = ProbeFixtures.WriteProbe("exit 0");
        var compileSource = ProbeFixtures.WriteProbe("exit 0");
        var config = new InstallConfig
        {
            SourceSiteBinary = siteSource,
            SourceExperimentBinary = experimentSource,
            SourceRunBinary = runSource,
            SourceCompileBinary = compileSource,
            SourceRevision = "",
            Registry = registry,
            // The stores live OUTSIDE the deployment root here, because several
            // of these tests assert that a refusal creates nothing under it.
            // In an ordinary install the two coincide at `deploy/`; the point
            // of the receipt naming them separately is that they need not.
            DeployRoot = ProbeFixtures.WriteControlStores(Path.Combine(root, "stores")).DeployRoot,
            ProfilesRoot = Path.Combine(root, "stores", "profiles"),
            SshHelper = Path.Combine(root, "stores", StagePackageTransport.MaintainedSshHelperRelativePath),
            WorkRoot = Path.Combine(root, "work"),
            DeploymentRoot = Path.Combine(root, "deploy"),
            BuildInventory = Path.Combine(root, "deploy", BuildInventory.FileName),
        };

        var installation = UnionCase.Of<Accepted<Installation>>(ControlPlaneInstaller.Install(config)).Value;
        Assert.False(
            installation.SiteBinaryPath == siteSource,
            $"installed set points at a mutable source binary: {installation}");
        // The three a worker machine cannot run a cell without: the operator
        // command, the Site executable, and the Go compiler that mints a
        // configuration's identity. Each is published content-addressed and
        // named in the one receipt.
        Assert.False(installation.CompileBinaryPath == compileSource, $"installed set points at a mutable card compiler: {installation}");
        Assert.False(
            installation.ExperimentBinaryPath == experimentSource || installation.RunBinaryPath == runSource,
            $"installed set points at mutable authoring/operator binaries: {installation}");

        using var receipt = JsonDocument.Parse(File.ReadAllBytes(installation.ReceiptPath));
        var fields = receipt.RootElement;
        Assert.Equal(ControlPlaneInstallationContract.ReceiptSchema, fields.GetProperty("schema").GetString());
        Assert.False(fields.TryGetProperty("home_binary_sha256", out _));
        Assert.False(fields.TryGetProperty("home_binary_path", out _));
        Assert.False(fields.TryGetProperty("card_binary_sha256", out _));
        Assert.False(fields.TryGetProperty("card_binary_path", out _));
        foreach (var digestField in new[]
                 {
                     "site_binary_sha256",
                     "experiment_binary_sha256", "run_binary_sha256", "compile_binary_sha256",
                 })
        {
            Assert.Equal(64, fields.GetProperty(digestField).GetString()!.Length);
        }

        Assert.False(
            Directory.Exists(Path.Combine(config.DeploymentRoot, "knowledge-reconcile-services")),
            "control-plane installation materialized a Cardbox-owned Knowledge service");

        using (var inventory = BuildInventory.Open(config.BuildInventory))
        {
            foreach (var (name, path) in new[]
                     {
                         ("fern-site", installation.SiteBinaryPath),
                         ("fern-experiment", installation.ExperimentBinaryPath),
                         ("fern-run", installation.RunBinaryPath),
                         ("fern-compile", installation.CompileBinaryPath),
                     })
            {
                var member = UnionCase.Of<Accepted<ActiveBinaryMember>>(inventory.ResolveActive(name)).Value;
                Assert.True(
                    member.SetId == installation.BinarySetId && member.Path == path,
                    $"installed {name} = {member}, want set {installation.BinarySetId} path {path}");
            }
            // The detail is asserted, not merely the case: Refused is shared by
            // every outcome union, so `Of<Refused>` alone would still pass if this
            // call started answering with some other operation's refusal.
            Assert.Contains(
                "fern-home",
                UnionCase.Of<Refused>(inventory.ResolveActive("fern-home")).Detail,
                StringComparison.Ordinal);
            Assert.Contains(
                "fern-card",
                UnionCase.Of<Refused>(inventory.ResolveActive("fern-card")).Detail,
                StringComparison.Ordinal);
        }

        var replayed = UnionCase.Of<Accepted<Installation>>(ControlPlaneInstaller.Install(config)).Value;
        Assert.Equal(installation, replayed);
    }

    [Fact]
    public void PreflightFailureWritesNothing()
    {
        var root = Directory.CreateTempSubdirectory("fern-install-").FullName;
        var registry = Path.Combine(root, "lifecycle.db");
        File.WriteAllText(registry, "fixture registry");
        var siteSource = Path.Combine(root, "fern-site");
        File.WriteAllText(siteSource, "not executable");
        File.SetUnixFileMode(siteSource, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var deploymentRoot = Path.Combine(root, "deploy");

        var refused = UnionCase.Of<Refused>(ControlPlaneInstaller.Install(new InstallConfig
        {
            SourceSiteBinary = siteSource,
            SourceExperimentBinary = ProbeFixtures.WriteProbe("exit 0"),
            SourceRunBinary = ProbeFixtures.WriteProbe("exit 0"),
            SourceCompileBinary = ProbeFixtures.WriteProbe("exit 0"),
            SourceRevision = "",
            BuildInventory = Path.Combine(deploymentRoot, BuildInventory.FileName),
            Registry = registry,
            // The stores live OUTSIDE the deployment root here, because several
            // of these tests assert that a refusal creates nothing under it.
            // In an ordinary install the two coincide at `deploy/`; the point
            // of the receipt naming them separately is that they need not.
            DeployRoot = ProbeFixtures.WriteControlStores(Path.Combine(root, "stores")).DeployRoot,
            ProfilesRoot = Path.Combine(root, "stores", "profiles"),
            SshHelper = Path.Combine(root, "stores", StagePackageTransport.MaintainedSshHelperRelativePath),
            WorkRoot = Path.Combine(root, "work"),
            DeploymentRoot = deploymentRoot,
        }));
        Assert.Contains("has no executable bit", refused.Detail, StringComparison.Ordinal);
        Assert.False(Directory.Exists(deploymentRoot), "preflight failure created deployment root");
    }

    [Fact]
    public void ACrossWiredWorkerSiteIsRefusedBeforeWriting()
    {
        var root = Directory.CreateTempSubdirectory("fern-install-").FullName;
        var registry = Path.Combine(root, "lifecycle.db");
        File.WriteAllText(registry, "fixture registry");
        var workRoot = Path.Combine(root, "work");
        var deploymentRoot = Path.Combine(root, "deploy");
        var refused = UnionCase.Of<Refused>(ControlPlaneInstaller.Install(new InstallConfig
        {
            SourceSiteBinary = ProbeFixtures.WriteControlProbe(InstallationIdentityContract.RegistryHomeRole),
            SourceExperimentBinary = ProbeFixtures.WriteProbe("exit 0"),
            SourceRunBinary = ProbeFixtures.WriteProbe("exit 0"),
            SourceCompileBinary = ProbeFixtures.WriteProbe("exit 0"),
            SourceRevision = "",
            BuildInventory = Path.Combine(deploymentRoot, BuildInventory.FileName),
            Registry = registry,
            // The stores live OUTSIDE the deployment root here, because several
            // of these tests assert that a refusal creates nothing under it.
            // In an ordinary install the two coincide at `deploy/`; the point
            // of the receipt naming them separately is that they need not.
            DeployRoot = ProbeFixtures.WriteControlStores(Path.Combine(root, "stores")).DeployRoot,
            ProfilesRoot = Path.Combine(root, "stores", "profiles"),
            SshHelper = Path.Combine(root, "stores", StagePackageTransport.MaintainedSshHelperRelativePath),
            WorkRoot = workRoot,
            DeploymentRoot = deploymentRoot,
        }));
        Assert.Contains("expected \"worker-site\"", refused.Detail, StringComparison.Ordinal);
        foreach (var path in new[] { workRoot, deploymentRoot })
        {
            Assert.False(Directory.Exists(path), $"cross-wired Worker Site refusal created {path}");
        }
    }

}
