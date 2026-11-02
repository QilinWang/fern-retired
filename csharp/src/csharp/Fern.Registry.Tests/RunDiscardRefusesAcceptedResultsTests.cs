using Fern.RunData;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// `fern-run discard-run` deletes a run's row and its artifact directory. It
/// must refuse when that run is the accepted result for a coordinate.
///
/// This is not a concurrency guard. One operator, one mistyped hash, an idle
/// machine, and the weights and metrics are gone while `accepted_results` still
/// names them — discoverable only when a later lookup comes back empty. The
/// database cannot catch it: `accepted_results.run_hash` is `NOT NULL UNIQUE`
/// and, unlike the two sibling columns beside it, carries no `REFERENCES` back
/// to `runs`, so enabling foreign keys would not fire.
///
/// Before 2026-09-10 the whole deletion path had no behavioural test at all —
/// the only `discard-run` test asserted that a retired flag is rejected.
/// </summary>
public sealed class RunDiscardRefusesAcceptedResultsTests
{
    [Fact]
    public void DiscardingAnAcceptedRunIsRefusedAndLeavesItsArtifactsOnDisk()
    {
        var root = Directory.CreateTempSubdirectory("fern-discard-").FullName;
        try
        {
            var artifacts = Path.Combine(root, "artifacts");
            Directory.CreateDirectory(artifacts);
            var weights = Path.Combine(artifacts, "model.safetensors");
            File.WriteAllText(weights, "the result somebody accepted");
            var database = Accepted(root, "run-accepted", artifacts, Path.Combine(root, "other"));

            var refusal = Assert.Throws<RunDataFault>(
                () => RunDiscardStore.Discard(database, "run-accepted", [root]));

            Assert.Contains("accepted result", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("cell-1", refusal.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(weights), "the refusal deleted the bytes anyway");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiscardingARunNobodyAcceptedStillWorks()
    {
        var root = Directory.CreateTempSubdirectory("fern-discard-").FullName;
        try
        {
            var artifacts = Path.Combine(root, "artifacts");
            Directory.CreateDirectory(artifacts);
            File.WriteAllText(Path.Combine(artifacts, "model.safetensors"), "nobody accepted this");
            var database = Accepted(root, "run-other", Path.Combine(root, "other"), artifacts);

            var report = RunDiscardStore.Discard(database, "run-unaccepted", [root]);

            Assert.NotNull(report.DbId);
            Assert.False(Directory.Exists(artifacts), "the artifacts survived a legitimate discard");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Two runs, each with its artifact directory (the real schema keeps
    /// <c>runs.artifact_dir</c> unique): <paramref name="acceptedHash"/> is the
    /// run the accepted attempt of cell 1 sealed, and "run-unaccepted" is not
    /// accepted by anything, so one Home serves both cases.
    /// CHG 2026-09-11 night (slice 11): a real Home instead of two hand-made
    /// tables; the accepted attempt is cells.accepted_attempt_id and the run
    /// it sealed is on its artifact snapshot. The study, source and site rows
    /// the cell and attempt reference are not what this test is about, so the
    /// keys are left off for the seed.
    /// </summary>
    static string Accepted(string root, string acceptedHash, string artifacts, string otherArtifacts)
    {
        var database = ProductionHome.CreateRegistry();
        // The per-run tables are an archive's; this fixture is a database that
        // carries both shapes, which is what an old campaign file is.
        RunArchive.AddTo(database);
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();
        using var seed = connection.CreateCommand();
        seed.CommandText = """
            PRAGMA foreign_keys = OFF;
            INSERT INTO runs (run_hash, artifact_dir, dataset, model_type, seed, pred_len, seq_len)
            VALUES ($accepted, $dir, 'lorenz', 'fern', 42, 8, 64), ('run-unaccepted', $other, 'lorenz', 'fern', 43, 8, 64);
            INSERT INTO cells (id, study_id, ordinal, name, dataset, seed, config_json, requirements_json, retention_json, accepted_attempt_id, created_at)
            VALUES (1, 1, 0, 'cell-1', 'lorenz', 42, '{}', '{}', '{}', 1, 'now');
            INSERT INTO attempts (id, cell_id, ordinal, site_id, fencing_epoch, state, request_json, started_at)
            VALUES (1, 1, 1, 1, 1, 'completed', '{}', 'now');
            INSERT INTO artifact_snapshots (snapshot_id, cell_id, attempt_id, run_hash, artifact_dir, file_count, total_bytes, receipt_digest, recorded_at)
            VALUES ('snapshot-1', 1, 1, $accepted, $dir, 0, 0, 'sealed', 'now');
            PRAGMA foreign_keys = ON;
            """;
        seed.Parameters.AddWithValue("$accepted", acceptedHash);
        seed.Parameters.AddWithValue("$dir", artifacts);
        seed.Parameters.AddWithValue("$other", otherArtifacts);
        seed.ExecuteNonQuery();
        return database;
    }
}
