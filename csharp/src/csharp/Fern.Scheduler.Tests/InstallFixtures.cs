using Fern.Scheduler;
using Fern.Install;

namespace Fern.Scheduler.Tests;

/// <summary>
/// One complete control-plane InstallConfig whose stores are named separately
/// from the directory the test uses for installation outputs. Those two are
/// different directories on purpose: an installed set is bound to content, so
/// where it was built and where it reads from are independent facts, and a
/// fixture that conflated them could not show the difference.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
static class InstallFixtures
{
    internal static InstallConfig ConfigFor(
        string installTree,
        string registry,
        string deployRoot,
        string profilesRoot,
        string sshHelper) =>
        new()
        {
            SourceSiteBinary = ProbeFixtures.WriteControlProbe(InstallationIdentityContract.WorkerSiteRole),
            SourceExperimentBinary = ProbeFixtures.WriteProbe("exit 0"),
            SourceRunBinary = ProbeFixtures.WriteProbe("exit 0"),
            SourceCompileBinary = ProbeFixtures.WriteProbe("exit 0"),
            SourceRevision = "",
            Registry = registry,
            DeployRoot = deployRoot,
            ProfilesRoot = profilesRoot,
            SshHelper = sshHelper,
            // Everything the installation PRODUCES lives beside the stores, not
            // in the install tree, so the tree can be deleted afterwards.
            WorkRoot = Path.Combine(Path.GetDirectoryName(registry)!, "work"),
            DeploymentRoot = Path.Combine(Path.GetDirectoryName(registry)!, "deployment"),
            BuildInventory = Path.Combine(
                Path.GetDirectoryName(registry)!, "deployment", BuildInventory.FileName),
        };
}
