using Fern.Registry;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Xunit;

namespace Fern.Registry.Tests;

public sealed class PinnedBinaryReclaimTests
{
    [Fact]
    public void typed_handoff_is_removed_only_after_registry_and_durable_bytes_agree()
    {
        var registry = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(registry);
        var root = Path.Combine(Path.GetTempPath(), $"fern-handoff-reclaim-{Guid.NewGuid():N}");
        var handoff = Path.Combine(root, "cell-a");
        var durable = Path.Combine(Path.GetTempPath(), $"fern-handoff-durable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(handoff);
        Directory.CreateDirectory(durable);
        try
        {
            var bytes = "durable evidence"u8.ToArray();
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            File.WriteAllBytes(Path.Combine(durable, "receipt.json"), bytes);
            using (var seed = writer.Connection.CreateCommand())
            {
                // Slice 11: the accepted attempt is the cell's (cells.accepted_attempt_id);
                // the study, source and site rows the cell and attempt reference
                // are not what this test is about, so the keys are left off.
                seed.CommandText =
                    "PRAGMA foreign_keys = OFF; "
                    + "INSERT INTO cells (id, study_id, ordinal, name, dataset, seed, config_json, requirements_json, retention_json, accepted_attempt_id, created_at) "
                    + "VALUES (1, 1, 0, 'cell-a', 'lorenz', 42, '{}', '{}', '{}', 1, '2026-01-01T00:00:00Z'); "
                    + "INSERT INTO attempts (id, cell_id, ordinal, site_id, fencing_epoch, state, request_json, started_at) "
                    + "VALUES (1, 1, 1, 1, 1, 'completed', '{}', '2026-01-01T00:00:00Z'); "
                    + "INSERT INTO artifact_snapshots (snapshot_id, cell_id, attempt_id, run_hash, artifact_dir, file_count, total_bytes, receipt_digest, recorded_at) "
                    + "VALUES ('snapshot-a', 1, 1, 'run-a', $durable, 1, $bytes, 'sealed', '2026-01-01T00:00:00Z'); "
                    + "INSERT INTO artifact_snapshot_files (snapshot_id, relative_path, role, byte_count, sha256, required_for_acceptance) "
                    + "VALUES ('snapshot-a', 'receipt.json', 'metrics', $bytes, $sha, 1); "
                    + "PRAGMA foreign_keys = ON;";
                seed.Parameters.AddWithValue("$durable", durable);
                seed.Parameters.AddWithValue("$bytes", bytes.LongLength);
                seed.Parameters.AddWithValue("$sha", sha256);
                seed.ExecuteNonQuery();
            }

            var request = new HandoffStorageReclaimRequest(
                root,
                Apply: false,
                [new HandoffDirectoryEvidence(
                    handoff,
                    17,
                    [new HandoffAttemptEvidence("1", "run-a", [$"sha256:{sha256}"])])]);
            var preview = HandoffStorageReclaimAuthority.Preview(writer.Connection, request);
            Assert.Equal("removable", Assert.Single(preview.Items).Decision);
            Assert.True(Directory.Exists(handoff));

            var applied = HandoffStorageReclaimAuthority.Reclaim(writer, request with { Apply = true });
            Assert.True(applied.TryGetValue(out var receipt));
            Assert.Equal(17, receipt.RemovedBytes);
            Assert.False(Directory.Exists(handoff));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(durable)) Directory.Delete(durable, recursive: true);
        }
    }

    [Fact]
    public void a_pinned_binary_is_kept_unless_both_the_attempt_and_the_process_are_gone()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var root = CreateShelf("rule");
        try
        {
            const string host = "pinned-test-host";
            var deadWithRow = uint.MaxValue - 1;
            var aliveWithoutRow = (uint)Environment.ProcessId;
            var deadWithoutRow = uint.MaxValue - 2;
            InsertRunningPlanAttempt(writer, host, deadWithRow);
            var shelf = Path.Combine(root, "run-plans");
            foreach (var pid in new[] { deadWithRow, aliveWithoutRow, deadWithoutRow })
            {
                File.WriteAllText(Path.Combine(shelf, $"fern-run.pinned.{pid}"), "binary");
            }
            File.WriteAllText(Path.Combine(shelf, "supervisor.log"), "not a pin");

            var preview = PinnedBinaryReclaimAuthority.Preview(
                writer.Connection,
                new PinnedBinaryReclaimRequest(root, host, Apply: false));
            Assert.Equal(3, preview.Items.Count);
            Assert.Equal("keep", Decision(preview, deadWithRow).Decision);
            Assert.Contains("still running", Decision(preview, deadWithRow).Reason, StringComparison.Ordinal);
            Assert.Equal("keep", Decision(preview, aliveWithoutRow).Decision);
            Assert.Contains("alive", Decision(preview, aliveWithoutRow).Reason, StringComparison.Ordinal);
            Assert.Equal("removable", Decision(preview, deadWithoutRow).Decision);

            var result = PinnedBinaryReclaimAuthority.Reclaim(
                writer,
                new PinnedBinaryReclaimRequest(root, host, Apply: true));
            Assert.True(result.TryGetValue(out var reclaimed));
            Assert.Equal(6, reclaimed.RemovedBytes);
            Assert.True(File.Exists(Path.Combine(shelf, $"fern-run.pinned.{deadWithRow}")));
            Assert.True(File.Exists(Path.Combine(shelf, $"fern-run.pinned.{aliveWithoutRow}")));
            Assert.False(File.Exists(Path.Combine(shelf, $"fern-run.pinned.{deadWithoutRow}")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void a_freed_pinned_inode_is_counted_once_across_its_links()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var root = CreateShelf("inode");
        try
        {
            var shelf = Path.Combine(root, "run-plans");
            var first = Path.Combine(shelf, $"fern-run.pinned.{uint.MaxValue - 3}");
            var second = Path.Combine(shelf, $"fern-run.pinned.{uint.MaxValue - 4}");
            File.WriteAllText(first, "eight by8");
            CreateHardLink(first, second);

            var preview = PinnedBinaryReclaimAuthority.Preview(
                writer.Connection,
                new PinnedBinaryReclaimRequest(root, "pinned-test-host", Apply: false));
            Assert.All(preview.Items, item => Assert.Equal("removable", item.Decision));
            Assert.Equal(0, preview.Items.Sum(item => item.Bytes));

            var result = PinnedBinaryReclaimAuthority.Reclaim(
                writer,
                new PinnedBinaryReclaimRequest(root, "pinned-test-host", Apply: true));
            Assert.True(result.TryGetValue(out var reclaimed));
            Assert.Equal(9, reclaimed.RemovedBytes);
            Assert.False(File.Exists(first));
            Assert.False(File.Exists(second));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void a_current_authority_pointer_refuses_pinned_cleanup_without_mutation()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var root = CreateShelf("refusal");
        try
        {
            var pin = Path.Combine(root, "run-plans", $"fern-run.pinned.{uint.MaxValue - 5}");
            File.WriteAllText(pin, "binary");
            InsertCurrentPlanPointer(writer);

            var result = PinnedBinaryReclaimAuthority.Reclaim(
                writer,
                new PinnedBinaryReclaimRequest(root, "pinned-test-host", Apply: true));
            Assert.True(result.TryGetRefusal(out var refusal));
            Assert.Contains("current authority pointers", refusal.Detail, StringComparison.Ordinal);
            Assert.True(File.Exists(pin));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void historical_resource_slot_pointer_refuses_pinned_and_handoff_cleanup()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var root = CreateShelf("historical-slot");
        try
        {
            var pin = Path.Combine(root, "run-plans", $"fern-run.pinned.{uint.MaxValue - 6}");
            File.WriteAllText(pin, "historical binary");
            var handoff = Path.Combine(root, "handoff");
            Directory.CreateDirectory(handoff);
            var evidence = Path.Combine(handoff, "evidence.json");
            File.WriteAllText(evidence, "historical evidence");
            InsertHistoricalResourceSlot(writer, "historical-lease");

            Assert.Equal(new[] { "1 resource slot" },
                PinnedBinaryReclaimAuthority.CurrentAuthorityPointers(writer.Connection));
            var pinned = PinnedBinaryReclaimAuthority.Reclaim(writer,
                new PinnedBinaryReclaimRequest(root, "pinned-test-host", Apply: true));
            Assert.True(pinned.TryGetRefusal(out var pinnedRefusal));
            Assert.Contains("1 resource slot", pinnedRefusal.Detail, StringComparison.Ordinal);
            var handoffs = HandoffStorageReclaimAuthority.Reclaim(writer,
                new HandoffStorageReclaimRequest(root, true,
                    [new HandoffDirectoryEvidence(handoff, 19,
                        [new HandoffAttemptEvidence("1", "historical-run", [])])]));
            Assert.True(handoffs.TryGetRefusal(out var handoffRefusal));
            Assert.Contains("1 resource slot", handoffRefusal.Detail, StringComparison.Ordinal);
            Assert.Equal("historical binary", File.ReadAllText(pin));
            Assert.Equal("historical evidence", File.ReadAllText(evidence));
            using var pointer = writer.Connection.CreateCommand();
            pointer.CommandText = "SELECT current_lease_id FROM resource_slots WHERE resource_slot_id='historical-slot'";
            Assert.Equal("historical-lease", pointer.ExecuteScalar());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void a_resource_slot_without_a_pointer_does_not_block_pinned_cleanup()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var root = CreateShelf("idle-historical-slot");
        try
        {
            var pin = Path.Combine(root, "run-plans", $"fern-run.pinned.{uint.MaxValue - 7}");
            File.WriteAllText(pin, "historical binary");
            InsertHistoricalResourceSlot(writer, null);
            Assert.Empty(PinnedBinaryReclaimAuthority.CurrentAuthorityPointers(writer.Connection));
            var result = PinnedBinaryReclaimAuthority.Reclaim(writer,
                new PinnedBinaryReclaimRequest(root, "pinned-test-host", Apply: true));
            Assert.True(result.TryGetValue(out var reclaimed));
            Assert.Equal(17, reclaimed.RemovedBytes);
            Assert.False(File.Exists(pin));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static void InsertHistoricalResourceSlot(RegistryWriter writer, string? lease)
    {
        // CHG 2026-09-28: seed historical evidence directly; no production
        // declaration writer remains. A missing old lease record is not release.
        using var command = writer.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO resource_slots(resource_slot_id, execution_profile_revision,
                description_json, declared_by, declared_at_ms, current_lease_id,
                authority_version, updated_at_ms)
            VALUES('historical-slot', 'profile@old', '{}', 'historical-owner', 1, $lease, 0, 1)
            """;
        command.Parameters.AddWithValue("$lease", (object?)lease ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    static PinnedBinaryReclaimItem Decision(PinnedBinaryReclaimReport report, uint pid) =>
        Assert.Single(report.Items, item => item.Path.EndsWith($"fern-run.pinned.{pid}", StringComparison.Ordinal));

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    static extern int Link(string existingPath, string newPath);

    static void CreateHardLink(string existingPath, string newPath)
    {
        if (Link(existingPath, newPath) != 0)
        {
            throw new InvalidOperationException($"link failed with errno {Marshal.GetLastWin32Error()}");
        }
    }

    static string CreateShelf(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"fern-pinned-reclaim-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "run-plans"));
        return root;
    }

    // CHG 2026-09-11 night (slice 8): the process holding a binary is the
    // Site's supervisor now (its office row names host and pid), where it was
    // the Plan attempt's opener.
    static void InsertRunningPlanAttempt(RegistryWriter writer, string host, uint pid, string leaseUntil = "2000-01-01T00:00:00Z")
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO sites (id, name, capabilities_json, capacity_json, created_at)
            VALUES (1, 'pinned-site', '{"device":"cuda"}', '{"slots":1}', '2026-09-11');
            INSERT INTO supervisors (site_id, host, pid, epoch, lease_until, binary_sha256, started_at)
            VALUES (1, $host, $pid, (SELECT COALESCE(MAX(epoch), 0) + 1 FROM supervisors WHERE site_id = 1),
                    $lease, 'sha256:supervisor', '2026-09-11T00:00:00Z');
            """;
        command.Parameters.AddWithValue("$host", host);
        command.Parameters.AddWithValue("$pid", (long)pid);
        command.Parameters.AddWithValue("$lease", leaseUntil);
        command.ExecuteNonQuery();
    }

    // CHG 2026-09-11 night (slice 8): a current authority is a Site office
    // held, where it was a Plan's current lease.
    static void InsertCurrentPlanPointer(RegistryWriter writer) =>
        InsertRunningPlanAttempt(writer, "elsewhere", 4242, leaseUntil: "2999-01-01T00:00:00Z");
}
