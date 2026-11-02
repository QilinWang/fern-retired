using Fern.Registry;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// The one-store schema's ten operations, and the two properties that are not
/// conventions a caller could forget: fencing lives in the write, and a
/// decision is one transaction.
///
/// Every test here opens a real Home with the current schema and drives the
/// public operations. None of them trains, spawns a process, or touches a
/// device.
/// </summary>
public sealed class RegistryOperationsTests
{
    // ── fixture ──────────────────────────────────────────────────────────────

    static (string Path, long SourceId, long SiteId) Home()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        long sourceId, siteId;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
                VALUES ('5e87434e', '[]', 'commit', '2026-09-11')
                RETURNING id
                """;
            sourceId = Convert.ToInt64(command.ExecuteScalar());
        }
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sites (name, capabilities_json, capacity_json, created_at)
                VALUES ('local-4070', '{"device":"cuda"}', '{"slots":1}', '2026-09-11')
                RETURNING id
                """;
            siteId = Convert.ToInt64(command.ExecuteScalar());
        }
        // Slice 6 (row 31): the Site holds the source, or the claim refuses it.
        ProductionHome.DeclareCodeFrom(writer, siteId);
        return (path, sourceId, siteId);
    }

    static RegistryOperations.CompiledCell Cell(long ordinal, string name) => new(
        Ordinal: ordinal,
        Name: name,
        Dataset: "rossler_param_shock.series",
        Seed: 42,
        ConfigJson: $$"""{"schema":"fern.training.v2","cell":"{{name}}"}""",
        RequirementsJson: """{"device":"cuda","requires_cuda":true}""",
        RetentionJson: """{"unit_results":"retained"}""");

    static RegistryOperations.RegisteredStudy RegisterTwoCells(string path, long sourceId, string name = "a-study")
    {
        using var writer = RegistryHome.OpenWriter(path);
        var registered = RegistryOperations.RegisterStudy(
            writer, name, 1, """{"schema":"fern.compiled-study.v1"}""", sourceId,
            [Cell(0, "reference"), Cell(1, "treatment")]);
        Assert.True(registered.TryGetValue(out var value), Refusal(registered));
        return value;
    }

    static string Refusal<T>(RegistryResult<T> result) =>
        result.TryGetRefusal(out var refusal) ? refusal.Detail : "";

    // ── RegisterStudy ────────────────────────────────────────────────────────

    /// <summary>
    /// Registering writes one cell per compiled cell, each carrying the bytes
    /// it will be given and what it needs to run
    /// (requirements_json). Those two are the whole reason a cell row exists
    /// separately from the compiled study document.
    /// </summary>
    [Fact]
    public void RegisterStudyWritesOneCellPerCompiledCellWithItsShaAndRequirements()
    {
        var (path, sourceId, _) = Home();
        var registered = RegisterTwoCells(path, sourceId);

        Assert.Equal(2, registered.CellIds.Count);
        using var reader = RegistryHome.OpenReader(path);
        using var command = reader.Connection.CreateCommand();
        command.CommandText =
            "SELECT ordinal, name, requirements_json, accepted_attempt_id FROM cells WHERE study_id = $study ORDER BY ordinal";
        command.Parameters.AddWithValue("$study", registered.StudyId);
        using var rows = command.ExecuteReader();

        Assert.True(rows.Read());
        Assert.Equal(0L, rows.GetInt64(0));
        Assert.Equal("reference", rows.GetString(1));
        Assert.Contains("requires_cuda", rows.GetString(2));
        Assert.True(rows.IsDBNull(3), "a freshly registered cell has accepted nothing");

        Assert.True(rows.Read());
        Assert.Equal(1L, rows.GetInt64(0));
        Assert.Equal("treatment", rows.GetString(1));
        Assert.False(rows.Read());

        var view = RegistryOperations.ReadStudy(reader.Connection, registered.StudyId);
        Assert.True(view.TryGetValue(out var study), Refusal(view));
        Assert.Equal("a-study", study.Name);
        Assert.Equal("run", study.RequestedState);
        Assert.Equal(2, study.CellCount);
        Assert.Equal(0, study.AcceptedCellCount);
    }

    /// <summary>A cell with no payload sha is refused: bytes nobody can verify are not registered.</summary>
    // ── TryStartAttempt ──────────────────────────────────────────────────────

    /// <summary>
    /// Two schedulers claim one cell; one wins. The loser is refused because
    /// the winner's attempt is open, and the refusal says so — it does not
    /// silently open a second attempt on the same cell.
    /// </summary>
    [Fact]
    public void TwoAttemptsOnOneCellAndOnlyOneWins()
    {
        var (path, sourceId, siteId) = Home();
        var registered = RegisterTwoCells(path, sourceId);
        var cellId = registered.CellIds[0];

        using var writer = RegistryHome.OpenWriter(path);
        var first = RegistryOperations.TryStartAttempt(writer, cellId, siteId, """{"cell":"reference"}""");
        var second = RegistryOperations.TryStartAttempt(writer, cellId, siteId, """{"cell":"reference"}""");

        Assert.True(first.TryGetValue(out var started), Refusal(first));
        Assert.Equal(1L, started.Ordinal);
        Assert.Equal(1L, started.FencingEpoch);
        Assert.False(second.IsOk, "a cell with an open attempt is not claimed twice");
        Assert.Contains("open attempt", Refusal(second), StringComparison.Ordinal);

        using var reader = RegistryHome.OpenReader(path);
        Assert.Equal(1L, ScalarLong(reader.Connection, "SELECT COUNT(*) FROM attempts WHERE cell_id = " + cellId));
    }

    /// <summary>A cancelled study claims no new cell; the field is read inside the claim's own transaction.</summary>
    [Fact]
    public void ACancelledStudyClaimsNoNewCell()
    {
        var (path, sourceId, siteId) = Home();
        var registered = RegisterTwoCells(path, sourceId);

        using var writer = RegistryHome.OpenWriter(path);
        Assert.True(RegistryOperations.RequestStudyState(writer, registered.StudyId, "cancel").IsOk);
        var claim = RegistryOperations.TryStartAttempt(writer, registered.CellIds[0], siteId, "{}");

        Assert.False(claim.IsOk);
        Assert.Contains("cancel", Refusal(claim), StringComparison.Ordinal);
        Assert.Empty(RegistryOperations.ListRunnableCells(writer.Connection, registered.StudyId));
    }

    // ── fencing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// THE FENCING TEST. A successor claim takes the next epoch; every write
    /// the displaced worker makes at its own epoch touches zero rows and is
    /// refused. Renew, Record and Finish are each proved separately, because
    /// each is a different UPDATE and a missing `AND fencing_epoch = ?` on any
    /// one of them would be a silent hole.
    /// </summary>
    [Fact]
    public void AStaleEpochIsRefusedByRenewRecordAndFinishAlike()
    {
        var (path, sourceId, siteId) = Home();
        var registered = RegisterTwoCells(path, sourceId);
        var cellId = registered.CellIds[0];

        using var writer = RegistryHome.OpenWriter(path);
        var first = RegistryOperations.TryStartAttempt(writer, cellId, siteId, "{}");
        Assert.True(first.TryGetValue(out var displaced), Refusal(first));

        // The displaced attempt is lost, and a successor takes the next epoch.
        Assert.True(RegistryOperations.FailAttempt(writer, displaced.AttemptId, displaced.FencingEpoch, "lost", "{}").IsOk);
        var second = RegistryOperations.TryStartAttempt(writer, cellId, siteId, "{}");
        Assert.True(second.TryGetValue(out var successor), Refusal(second));
        Assert.Equal(displaced.FencingEpoch + 1, successor.FencingEpoch);

        // The displaced worker is still running and still writing. Nothing lands.
        var renew = RegistryOperations.RenewAttempt(writer, displaced.AttemptId, displaced.FencingEpoch);
        var record = RegistryOperations.RecordEvent(writer, displaced.AttemptId, displaced.FencingEpoch, "progress", "train", "{}");
        var finish = RegistryOperations.FinishAttempt(writer, displaced.AttemptId, displaced.FencingEpoch, """{"mse":1}""");

        foreach (var (what, result) in new[] { ("renew", renew), ("record", record), ("finish", finish) })
        {
            Assert.False(result.IsOk, $"{what} at a stale epoch must be refused");
            Assert.True(result.TryGetRefusal(out var refusal));
            Assert.Equal("fenced", refusal.ErrorCategory);
            Assert.Equal(displaced.FencingEpoch, refusal.StaleEpoch);
        }

        using var reader = RegistryHome.OpenReader(path);
        // The only event is the `lost` one FailAttempt wrote at the displaced
        // attempt's own epoch, which was legitimate. Nothing the displaced
        // worker wrote afterwards landed.
        Assert.Equal(0L, ScalarLong(reader.Connection, "SELECT COUNT(*) FROM events WHERE kind = 'progress'"));
        Assert.Equal(1L, ScalarLong(reader.Connection, "SELECT COUNT(*) FROM events WHERE kind = 'lost'"));
        Assert.True(ScalarIsNull(reader.Connection, "SELECT accepted_attempt_id FROM cells WHERE id = " + cellId),
            "a fenced finish must not accept a result");

        // And the successor, at its own epoch, works.
        Assert.True(RegistryOperations.RenewAttempt(writer, successor.AttemptId, successor.FencingEpoch).IsOk);
        Assert.True(RegistryOperations.RecordEvent(writer, successor.AttemptId, successor.FencingEpoch, "progress", "train", "{}").IsOk);
    }

    // ── the accepted result ──────────────────────────────────────────────────

    /// <summary>
    /// A cell accepts one result. The first FinishAttempt sets
    /// accepted_attempt_id; a second completion on the same cell is refused,
    /// and refused whole — the second attempt is not left completed with its
    /// result unaccepted.
    /// </summary>
    [Fact]
    public void FinishAttemptSetsTheAcceptedAttemptOnceAndRefusesASecond()
    {
        var (path, sourceId, siteId) = Home();
        var registered = RegisterTwoCells(path, sourceId);
        var cellId = registered.CellIds[0];

        using var writer = RegistryHome.OpenWriter(path);
        var first = RegistryOperations.TryStartAttempt(writer, cellId, siteId, "{}");
        Assert.True(first.TryGetValue(out var accepted), Refusal(first));
        var finished = RegistryOperations.FinishAttempt(writer, accepted.AttemptId, accepted.FencingEpoch, """{"mse":0.25}""");
        Assert.True(finished.TryGetValue(out var finishedCell), Refusal(finished));
        Assert.Equal(cellId, finishedCell);

        using (var reader = RegistryHome.OpenReader(path))
        {
            Assert.Equal(accepted.AttemptId,
                ScalarLong(reader.Connection, "SELECT accepted_attempt_id FROM cells WHERE id = " + cellId));
        }

        // An accepted cell is no longer runnable, and no second attempt starts.
        Assert.DoesNotContain(RegistryOperations.ListRunnableCells(writer.Connection, registered.StudyId),
            cell => cell.CellId == cellId);
        var again = RegistryOperations.TryStartAttempt(writer, cellId, siteId, "{}");
        Assert.False(again.IsOk);
        Assert.Contains("accepted attempt", Refusal(again), StringComparison.Ordinal);

        // Forcing a second completion through the database directly: the
        // operation must still refuse to move the accepted column.
        using (var force = writer.Connection.CreateCommand())
        {
            force.CommandText =
                """
                INSERT INTO attempts (cell_id, ordinal, site_id, fencing_epoch, state, request_json, started_at)
                VALUES ($cell, 99, $site, 99, 'running', '{}', '2026-09-11')
                """;
            force.Parameters.AddWithValue("$cell", cellId);
            force.Parameters.AddWithValue("$site", siteId);
            force.ExecuteNonQuery();
        }
        var second = RegistryOperations.FinishAttempt(writer, ScalarLong(writer.Connection,
            "SELECT id FROM attempts WHERE cell_id = " + cellId + " AND ordinal = 99"), 99, """{"mse":9}""");
        Assert.False(second.IsOk, "a cell accepts one result");
        Assert.Contains("one result", Refusal(second), StringComparison.Ordinal);

        using var after = RegistryHome.OpenReader(path);
        Assert.Equal(accepted.AttemptId,
            ScalarLong(after.Connection, "SELECT accepted_attempt_id FROM cells WHERE id = " + cellId));
        // Refused whole: the forced attempt is NOT left completed with its
        // result unaccepted. This is the assertion that caught a real bug --
        // the first cut updated the attempt before checking the cell, and a
        // refusal is a returned value, so WriteImmediate committed it anyway.
        Assert.Equal("running",
            ScalarText(after.Connection, "SELECT state FROM attempts WHERE cell_id = " + cellId + " AND ordinal = 99"));
        Assert.True(ScalarIsNull(after.Connection,
            "SELECT result_json FROM attempts WHERE cell_id = " + cellId + " AND ordinal = 99"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static string ScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    static bool ScalarIsNull(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull;
    }
}
