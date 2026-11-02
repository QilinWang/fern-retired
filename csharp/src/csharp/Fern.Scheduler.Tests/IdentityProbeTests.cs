using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Pass 6 bounded identity reader. A real child process, 5s / 64 KiB bounds,
/// caller JSON of the probe output strict per Pass 2 addendum
/// reference:7930679b4dc5ecbdeff33f81d7a6c99cd92024da0d1c57d0bc708e169e34c7b3.
/// </summary>
[Collection(TimingSensitiveCollection.Name)]
public sealed class IdentityProbeTests
{
    const string Python = "/home/user/.venv/bin/python";
    const string IdentityScript =
        "print('{\"schema\":\"fern.installation_identity.v4\",\"role\":\"scheduler\",\"run_plan_protocol\":\"fern.run_plan_parent_child.v2\"}')";

    [Fact]
    public void DuplicateProbeOfTheSameBytesIsTheSameIdentity()
    {
        var first = Assert.IsType<IdentityProbe.Outcome.Accepted>(ProbeIdentity());
        var second = Assert.IsType<IdentityProbe.Outcome.Accepted>(ProbeIdentity());
        Assert.Equal(first.Identity.Role, second.Identity.Role);
        Assert.Equal(first.Identity.Schema, second.Identity.Schema);
        Assert.Equal(first.Identity.RunPlanProtocol, second.Identity.RunPlanProtocol);
    }

    [Fact]
    public void IdentityProbeRefusesAPathWithNoExecutableBit()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"fern-scheduler-{Guid.NewGuid():N}");
        File.WriteAllBytes(path, "not executable"u8.ToArray());
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var outcome = IdentityProbe.Run(path, InstallationIdentityContract.SchedulerRole, timeoutMs: 1000, maxBytes: 1024);
        Assert.IsType<IdentityProbe.Outcome.NotExecutable>(outcome);
    }

    [Fact]
    public void IdentityProbeRefusesAProgramThatNeverReturns()
    {
        var outcome = IdentityProbe.Run(
            "/bin/sleep",
            InstallationIdentityContract.SchedulerRole,
            timeoutMs: 400,
            maxBytes: IdentityProbe.MaxOutputBytes,
            "30");
        Assert.IsType<IdentityProbe.Outcome.TimedOut>(outcome);
    }

    [Fact]
    public void DelayedOutputAfterTheTimeoutIsDiscarded()
    {
        var outcome = IdentityProbe.Run(
            Python,
            InstallationIdentityContract.SchedulerRole,
            timeoutMs: 400,
            maxBytes: IdentityProbe.MaxOutputBytes,
            "-c",
            "import time; time.sleep(2); " + IdentityScript);
        Assert.IsType<IdentityProbe.Outcome.TimedOut>(outcome);
    }

    [Fact]
    public void IdentityProbeRefusesUnboundedOutput()
    {
        var outcome = IdentityProbe.Run(
            Python,
            InstallationIdentityContract.SchedulerRole,
            timeoutMs: 2000,
            maxBytes: 1024,
            "-c",
            "import sys, time; sys.stdout.write('x' * 200000); sys.stdout.flush(); time.sleep(5)");
        Assert.IsType<IdentityProbe.Outcome.UnboundedOutput>(outcome);
    }

    [Fact]
    public void ExtraBytesAreRefusedAndARestartDoesNotReuseAPartialRead()
    {
        var extra = IdentityProbe.Run(
            Python,
            InstallationIdentityContract.SchedulerRole,
            timeoutMs: 2000,
            maxBytes: IdentityProbe.MaxOutputBytes,
            "-c",
            "import sys; sys.stdout.write('{\"schema\":\"fern.installation_identity.v4\",\"role\":\"scheduler\",\"run_plan_protocol\":\"fern.run_plan_parent_child.v2\"}{}')");
        var failed = Assert.IsType<IdentityProbe.Outcome.ParseFailed>(extra);
        Assert.IsType<InstallationIdentityParse.Refused>(failed.Parse);

        Assert.IsType<IdentityProbe.Outcome.TimedOut>(
            IdentityProbe.Run(
                "/bin/sleep",
                InstallationIdentityContract.SchedulerRole,
                timeoutMs: 300,
                maxBytes: IdentityProbe.MaxOutputBytes,
                "30"));
        Assert.IsType<IdentityProbe.Outcome.Accepted>(ProbeIdentity());
    }

    static IdentityProbe.Outcome ProbeIdentity() =>
        IdentityProbe.Run(
            Python,
            InstallationIdentityContract.SchedulerRole,
            timeoutMs: 2000,
            maxBytes: IdentityProbe.MaxOutputBytes,
            "-c",
            IdentityScript);
}
