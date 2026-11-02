using Fern.Scheduler;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Executable shell-script probes, the same fixtures Go's install and service
/// tests write (writeProbe, writeControlProbe, writeCardProbe,
/// writeStageRunnerProbe). Each probe lives in its own fresh directory.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
static class ProbeFixtures
{
    /// <summary>
    /// The stores an installed set reads at run time, created inside a fixture
    /// tree and returned. The installer refuses a store that is not there, so
    /// every install fixture needs them present; the paths are separate
    /// arguments because the set names them individually rather than deriving
    /// them from one workspace.
    /// </summary>
    internal static (string DeployRoot, string ProfilesRoot, string SshHelper) WriteControlStores(string root)
    {
        var deploy = Path.Combine(root, "deploy");
        var profiles = Path.Combine(root, "profiles");
        var helper = Path.Combine(
            root, StagePackageTransport.MaintainedSshHelperRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(deploy);
        Directory.CreateDirectory(profiles);
        Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
        File.WriteAllText(helper, "#!/bin/sh\nexit 0\n");
        return (deploy, profiles, helper);
    }

    public const string SchedulerIdentity =
        """{"schema":"fern.installation_identity.v4","role":"scheduler","run_plan_protocol":"fern.run_plan_parent_child.v2"}""";

    public static string WriteProbe(string body)
    {
        var directory = Directory.CreateTempSubdirectory("fern-probe-");
        var path = Path.Combine(directory.FullName, "probe");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    public static string WritePrintingProbe(string json) => WriteProbe("printf '%s\\n' '" + json + "'");

    public static string WriteControlProbe(string role) => WritePrintingProbe(
        $$"""{"schema":"fern.installation_identity.v4","role":"{{role}}","run_plan_protocol":"fern.run_plan_parent_child.v2"}""");

    public static string WriteCardProbe() => WritePrintingProbe(
        """{"schema":"fern.card_writer_installation.v1","role":"card-writer"}""");

}
