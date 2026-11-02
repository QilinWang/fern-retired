using System.Security.Cryptography;
using System.Text;
using Fern.Registry;
using Fern.Scheduler;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// The deploy/ inventory names, for every content-addressed path, the durable
/// selector that still wants it, and calls the rest removable. Each selector
/// is exercised once on a synthetic workspace; nothing here touches the real
/// deploy/ directory.
/// </summary>
public sealed class DeployInventoryTests : IDisposable
{
    readonly string _workspace = Path.Combine(Path.GetTempPath(), $"fern-deploy-inventory-{Guid.NewGuid():N}");
    readonly string _registry = TestRegistry.CreateRegistry();

    string Deploy => Path.Combine(_workspace, "deploy");
    string Systemd => Path.Combine(_workspace, "systemd-user");

    static readonly string LiveScheduler = Digest("live scheduler");
    static readonly string StaleScheduler = Digest("stale scheduler");
    static readonly string Orphan = Digest("nobody names this");
    static readonly string Supervisor = Digest("running supervisor");
    static readonly string WorkRegistry = Digest("work.toml registry");
    static readonly string ProfileArchive = Digest("profile source root");
    static readonly string DeployedArchive = Digest("deployed source archive");
    static readonly string OrphanArchive = Digest("orphan source archive");
    static readonly string Content = Digest("registered source content");
    static readonly string DeclaredBlob = Digest("declared blob");
    static readonly string StrayBlob = Digest("stray blob");
    static readonly string Unregistered = Digest("unregistered implementation");

    public DeployInventoryTests()
    {
        Directory.CreateDirectory(Systemd);
        Directory.CreateDirectory(Path.Combine(_workspace, "profiles"));
        var unit = Path.Combine(Systemd, "fern-scheduler-sha256:test.service");
        File.WriteAllText(unit, "[Unit]\nDescription=test\n");
        var unitDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(unit))).ToLowerInvariant();

        Binary("scheduler-binaries", "fern-scheduler." + LiveScheduler);
        Binary("scheduler-binaries", "fern-scheduler." + StaleScheduler);
        Binary("scheduler-binaries", "fern-scheduler." + Orphan);
        Binary("run-operator-binaries", "fern-run." + Supervisor);
        Binary("registry-binaries", "fern-registry." + WorkRegistry);
        Write("scheduler-services/svc/live.json",
            $$"""{"unit_path":"{{unit}}","unit_sha256":"sha256:{{unitDigest}}","scheduler_binary_sha256":"{{LiveScheduler}}","worker_binary_sha256":null}""");
        Write("scheduler-services/svc/stale.json",
            $$"""{"unit_path":"{{unit}}","unit_sha256":"sha256:{{Digest("older unit bytes")}}","scheduler_binary_sha256":"{{StaleScheduler}}"}""");
        File.WriteAllText(Path.Combine(_workspace, "work.toml"),
            $"[registry]\nfacts = [\"{Path.Combine(Deploy, "registry-binaries", "fern-registry." + WorkRegistry)}\", \"work-facts\"]\n");
        File.WriteAllText(Path.Combine(_workspace, "profiles", "pod.json"),
            $$"""{"profile_id":"pod","source_root":"/tmp/fern-runtime-hot/source/{{ProfileArchive}}"}""");
        Write("deployments.jsonl",
            $$"""{"deployed_at_unix_ms":1786637061016,"profile_id":"pod","binary_sha256":"{{Digest("remote worker")}}","source_archive_sha256":"{{DeployedArchive}}"}""" + "\n");
        foreach (var archive in new[] { ProfileArchive, DeployedArchive, OrphanArchive }) Write($"fern-source.{archive}.tgz", "archive bytes");
        Write("runpod-fern-source-2026-07-24.tgz", "july record");
        Write($"source-contents/{Content}/manifest.json",
            $$"""{"files":[{"path":"python/a.py","byte_digest":"sha256:{{DeclaredBlob}}","bytes":3,"kind":"regular_file"}]}""");
        Write($"source-blobs/{DeclaredBlob}", "abc");
        Write($"source-blobs/{StrayBlob}", "stray");
        Write($"source-content-roots/{Content}/python/a.py", "abc");
        Write($"source-content-roots/{Content}/csharp/X/obj/project.assets.json", "a build wrote this");
        Write($"implementations/{Unregistered}/manifest.json", "{}");
        Write("README.md", "index");

        // CHG 2026-09-13: nothing registers a source content any more. The
        // frozen source's catalogue left the Home with the store it indexed, so
        // the rows this fixture used to seed cannot be written and the stores
        // below are named by nothing -- which is what the assertions now say.
        // retired/csharp/frozen-source-2026-09-13/.
        using var writer = RegistryHome.OpenWriter(_registry);
    }

    [Fact]
    public void every_selector_keeps_its_digest_and_nothing_else_is_kept()
    {
        SeedRunningAttempt();
        var items = Inspect();
        Assert.Equal("keep", Decision(items, "scheduler-binaries", "fern-scheduler." + LiveScheduler));
        Assert.Contains("installation receipt scheduler-services/svc/live.json of live unit fern-scheduler-sha256:test.service", Reason(items, "scheduler-binaries", "fern-scheduler." + LiveScheduler));
        Assert.Equal("removable", Decision(items, "scheduler-binaries", "fern-scheduler." + StaleScheduler));
        Assert.Equal("removable", Decision(items, "scheduler-binaries", "fern-scheduler." + Orphan));
        Assert.Equal(DeployInventory.NothingNamesIt, Reason(items, "scheduler-binaries", "fern-scheduler." + Orphan));
        Assert.Contains("open Site supervisor:1 runs this binary", Reason(items, "run-operator-binaries", "fern-run." + Supervisor));
        Assert.Contains("work.toml selects", Reason(items, "registry-binaries", "fern-registry." + WorkRegistry));
        Assert.Contains("profile pod names this source archive", Reason(items, $"fern-source.{ProfileArchive}.tgz"));
        Assert.Contains("deployment record 2026-08-13 pod source_archive_sha256", Reason(items, $"fern-source.{DeployedArchive}.tgz"));
        Assert.Equal("removable", Decision(items, $"fern-source.{OrphanArchive}.tgz"));
        Assert.Equal("keep", Decision(items, "runpod-fern-source-2026-07-24.tgz"));
        // The frozen source's stores: nothing registers one, so nothing names
        // one, and every blob is loose rather than declared.
        Assert.Equal("removable", Decision(items, "source-contents", Content));
        Assert.Equal("removable", Decision(items, "implementations", Unregistered));
        Assert.Equal("removable", Decision(items, "source-blobs", StrayBlob));
        Assert.Equal("removable", Decision(items, "source-blobs", DeclaredBlob));
        Assert.Contains("0 blob(s) declared", Reason(items, "source-blobs"));
        // A running attempt still holds a materialized root open: kept by the
        // attempt rather than by a row, which is the protection that survived.
        Assert.Equal("keep", Decision(items, "source-content-roots", Content));
        Assert.Equal("keep", Decision(items, "README.md"));
        Assert.Equal("keep", Decision(items, "scheduler-services"));
    }

    [Fact]
    public void with_no_running_attempt_a_materialized_root_is_a_rebuildable_copy()
    {
        var items = Inspect();
        Assert.Equal("removable", Decision(items, "source-content-roots", Content));
        Assert.Equal("removable", Decision(items, "run-operator-binaries", "fern-run." + Supervisor));
    }

    [Fact]
    public void a_missing_deploy_root_is_refused_by_name()
    {
        using var writer = RegistryHome.OpenWriter(_registry);
        var missing = Path.Combine(_workspace, "absent");
        var refusal = Assert.Throws<DeployInventoryRefusal>(() => DeployInventory.Inspect(writer.Connection, Request(missing)));
        Assert.Contains(missing, refusal.Message);
    }

    IReadOnlyList<DeployInventory.Item> Inspect()
    {
        using var writer = RegistryHome.OpenWriter(_registry);
        return DeployInventory.Inspect(writer.Connection, Request(Deploy));
    }

    DeployInventory.Request Request(string deploy) =>
        new(deploy, Path.Combine(_workspace, "profiles"), Path.Combine(_workspace, "work.toml"), Systemd);

    // CHG 2026-09-11 night (slice 8): the process holding a binary set is the
    // Site's supervisor now (its office row names the binary), where it was
    // the Plan attempt's supervisor.
    void SeedRunningAttempt()
    {
        using var writer = RegistryHome.OpenWriter(_registry);
        Execute(writer.Connection, $$"""
            INSERT INTO sites (id, name, capabilities_json, capacity_json, created_at)
            VALUES (1, 'local-4070', '{"device":"cuda"}', '{"slots":1}', '2026-09-11');
            INSERT INTO supervisors (site_id, host, pid, epoch, lease_until, binary_sha256, started_at)
            VALUES (1, 'test-host', 1, 1, '2999-01-01T00:00:00Z', '{{Supervisor}}', '2026-09-11T00:00:00Z');
            """);
    }

    static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    string Decision(IReadOnlyList<DeployInventory.Item> items, params string[] tail) => Find(items, tail).Decision;
    string Reason(IReadOnlyList<DeployInventory.Item> items, params string[] tail) => Find(items, tail).Reason;

    DeployInventory.Item Find(IReadOnlyList<DeployInventory.Item> items, string[] tail)
    {
        var path = Path.Combine([Deploy, .. tail]);
        return Assert.Single(items, item => item.Path == path);
    }

    void Binary(string family, string name) => Write(Path.Combine(family, name), "binary bytes of " + name);

    void Write(string relative, string text)
    {
        var path = Path.Combine(Deploy, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }
}
