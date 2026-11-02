using Fern.Install;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Owner, 2026-09-18: "install new must delete old, and don't store garbage."
/// The deployment root had reached 33 GiB — 122 copies of one program and 82
/// receipts of a service retired on 2026-09-11 — because publishing never
/// removed anything. These hold the two things that must stay: what this
/// machine runs, and enough recent copies to roll back to.
/// </summary>
public sealed class DeployRetentionTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "fern-retention-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    string Publish(string directory, string name, string digest, DateTime written, int bytes = 1024)
    {
        var folder = Path.Combine(_root, directory);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{name}.{digest}");
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, written);
        File.WriteAllText(path + ".receipt.json", $"{{\"digest\":\"{digest}\"}}");
        return path;
    }

    static string Digest(char filler) => new(filler, 64);

    [Fact]
    public void ASelectedCopyIsKeptHoweverOldItIs()
    {
        var selected = Publish("run-operator-binaries", "fern-run", Digest('a'), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Publish("run-operator-binaries", "fern-run", Digest('b'), new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc));
        Publish("run-operator-binaries", "fern-run", Digest('c'), new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc));
        var stale = Publish("run-operator-binaries", "fern-run", Digest('d'), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Directory.CreateDirectory(Path.Combine(_root, "runtime-selections"));
        File.WriteAllText(Path.Combine(_root, "runtime-selections", "one.json"),
            $"{{\"run_binary\":\"/anywhere/fern-run.{Digest('a')}\"}}");

        var plan = DeployRetention.Compute(_root, keep: 2);

        Assert.DoesNotContain(plan.Removals, removal => removal.Path == selected);
        Assert.Contains(plan.Removals, removal => removal.Path == stale);
        DeployRetention.Apply(plan);
        Assert.True(File.Exists(selected));
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(stale + ".receipt.json"));
    }

    /// <summary>
    /// Slice F6: on 2026-09-26 the cleanup removed a compiler the local
    /// supervisor named on its command line. A digest a loaded unit or a Site
    /// declaration names is kept however old, with its receipt.
    /// </summary>
    [Fact]
    public void ACopyALoadedUnitNamesIsKeptHoweverOldItIs()
    {
        var named = Publish("compile-binaries", "fern-compile", Digest('8'), new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        Publish("compile-binaries", "fern-compile", Digest('b'), new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc));
        Publish("compile-binaries", "fern-compile", Digest('c'), new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc));
        var stale = Publish("compile-binaries", "fern-compile", Digest('d'), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var without = DeployRetention.Compute(_root, keep: 2);
        Assert.Contains(without.Removals, removal => removal.Path == named);

        var plan = DeployRetention.Compute(_root, keep: 2, new HashSet<string> { Digest('8') });

        Assert.DoesNotContain(plan.Removals, removal => removal.Path == named || removal.Path == named + ".receipt.json");
        Assert.Contains(plan.Kept, kept => kept.StartsWith(named, StringComparison.Ordinal) && kept.Contains("loaded unit"));
        Assert.Contains(plan.Removals, removal => removal.Path == stale);
    }

    [Fact]
    public void TheNewestCopiesSurviveSoARollbackHasBytesToRollBackTo()
    {
        var newest = Publish("compile-binaries", "fern-compile", Digest('e'), new DateTime(2026, 9, 18, 3, 0, 0, DateTimeKind.Utc));
        var second = Publish("compile-binaries", "fern-compile", Digest('f'), new DateTime(2026, 9, 18, 2, 0, 0, DateTimeKind.Utc));
        var third = Publish("compile-binaries", "fern-compile", Digest('0'), new DateTime(2026, 9, 18, 1, 0, 0, DateTimeKind.Utc));

        DeployRetention.Apply(DeployRetention.Compute(_root, keep: 2));

        Assert.True(File.Exists(newest));
        Assert.True(File.Exists(second));
        Assert.False(File.Exists(third));
    }

    [Fact]
    public void ReceiptsOfTheRetiredResidentSchedulerGo()
    {
        var retired = Path.Combine(_root, DeployRetention.RetiredReceiptDirectory, "fern-scheduler-sha256:" + Digest('1'));
        Directory.CreateDirectory(retired);
        File.WriteAllText(Path.Combine(retired, "receipt.json"), "{}");

        var plan = DeployRetention.Compute(_root, keep: 2);
        Assert.Contains(plan.Removals, removal => removal.Reason.Contains("retired resident scheduler"));
        DeployRetention.Apply(plan);
        Assert.False(Directory.Exists(retired));
    }

    [Fact]
    public void CurrentPointsAtTheNewestCopy()
    {
        Publish("worker-site-binaries", "fern-site", Digest('2'), new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc));
        var newest = Publish("worker-site-binaries", "fern-site", Digest('3'), new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc));

        DeployRetention.RefreshCurrentLinks(_root);

        var link = Path.Combine(_root, "worker-site-binaries", "current");
        Assert.Equal(newest, new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void NothingOutsideTheMemberDirectoriesIsTouched()
    {
        var sources = Path.Combine(_root, "study-source-roots", "one");
        Directory.CreateDirectory(sources);
        File.WriteAllText(Path.Combine(sources, "model.py"), "a study's frozen source");
        Publish("run-operator-binaries", "fern-run", Digest('4'), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Publish("run-operator-binaries", "fern-run", Digest('5'), new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc));
        Publish("run-operator-binaries", "fern-run", Digest('6'), new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc));

        var plan = DeployRetention.Compute(_root, keep: 2);
        Assert.DoesNotContain(plan.Removals, removal => removal.Path.Contains("study-source-roots", StringComparison.Ordinal));
        DeployRetention.Apply(plan);
        Assert.True(File.Exists(Path.Combine(sources, "model.py")));
    }

    /// <summary>
    /// Review of slice F6: a binary a running process names in its environment (a trainer calls
    /// back through FERN_RUN_BIN) is kept, however many installs came after it.
    /// </summary>
    [Fact]
    public void ABinaryARunningProcessNamesIsKept()
    {
        var digest = Digest('7');
        var start = new System.Diagnostics.ProcessStartInfo("sleep", "30") { UseShellExecute = false };
        start.Environment["FERN_RUN_BIN"] = $"/home/x/runtime/deploy/run-operator-binaries/fern-run.{digest}";
        using var child = System.Diagnostics.Process.Start(start)!;
        try
        {
            Assert.Contains(digest, DeployRetention.RunningDigests());
        }
        finally
        {
            child.Kill();
            child.WaitForExit();
        }
    }
}
