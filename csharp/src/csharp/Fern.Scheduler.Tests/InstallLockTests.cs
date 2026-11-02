using System.Text.Json;
using Fern.Scheduler;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

/// <summary>
/// One install per machine at a time — the step `install-control-plane` has
/// carried as missing since it was written.
///
/// The failure it prevents: two installs interleaving in one deployment root,
/// where the second publishes over bytes the first is still writing and the
/// receipt then names a set of binaries that never existed together.
/// </summary>
public sealed class InstallLockTests
{
    static string Root() => Directory.CreateTempSubdirectory("fern-install-lock-").FullName;

    [Fact]
    public void ASecondInstallIsRefusedAndToldWhoHasTheMachine()
    {
        var root = Root();

        var first = InstallLock.Acquire(root, "install-control-plane");
        Assert.True(first.IsOk, first.IsOk ? "" : first.Refusal);
        using var held = first.Value;

        var second = InstallLock.Acquire(root, "install-control-plane");

        Assert.False(second.IsOk, "two installs must not run in one deployment root");
        Assert.Contains("another install already holds", second.Refusal, StringComparison.Ordinal);
        Assert.Contains($"pid {System.Environment.ProcessId}", second.Refusal, StringComparison.Ordinal);
        Assert.Contains(System.Environment.MachineName, second.Refusal, StringComparison.Ordinal);
        Assert.Contains("install-control-plane", second.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMachineIsFreeAgainWhenTheInstallFinishes()
    {
        var root = Root();
        var first = InstallLock.Acquire(root, "install-control-plane");
        Assert.True(first.IsOk);
        Assert.True(InstallLock.IsHeld(root));

        first.Value.Dispose();

        Assert.False(InstallLock.IsHeld(root));
        var second = InstallLock.Acquire(root, "install-control-plane");
        Assert.True(second.IsOk, second.IsOk ? "" : second.Refusal);
        second.Value.Dispose();
    }

    /// <summary>
    /// The case the retired lease worked hardest for and this one gets free: an
    /// install that died leaves the file behind and the machine still free,
    /// because the exclusion was the open handle and the operating system
    /// released it. No deadline to guess, no stale lease to reconcile.
    /// </summary>
    [Fact]
    public void AnInstallThatDiedLeavesNoLockBehind()
    {
        var root = Root();
        var abandoned = InstallLock.Acquire(root, "install-control-plane");
        Assert.True(abandoned.IsOk);
        abandoned.Value.Dispose();
        // The file may be gone; write one back to stand for a crash that left it.
        File.WriteAllText(Path.Combine(root, InstallLock.NoteName),
            """{"schema":"fern.install_lock.v1","pid":999999,"host":"gone","started_at":"2026-09-11T00:00:00Z","purpose":"install-control-plane"}""");

        Assert.False(InstallLock.IsHeld(root), "a file nobody holds open excludes nobody");
        var next = InstallLock.Acquire(root, "install-control-plane");
        Assert.True(next.IsOk, next.IsOk ? "" : next.Refusal);
        next.Value.Dispose();
    }

    [Fact]
    public void TheLockSaysWhoHoldsItAndWhyInItsOwnFile()
    {
        var root = Root();
        var held = InstallLock.Acquire(root, "publish-registry-binary");
        Assert.True(held.IsOk);
        using var _ = held.Value;

        using var note = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, InstallLock.NoteName)));

        Assert.Equal(InstallLock.Schema, note.RootElement.GetProperty("schema").GetString());
        Assert.Equal("publish-registry-binary", note.RootElement.GetProperty("purpose").GetString());
        Assert.Equal(System.Environment.ProcessId, note.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal(System.Environment.MachineName, note.RootElement.GetProperty("host").GetString());
    }
}
