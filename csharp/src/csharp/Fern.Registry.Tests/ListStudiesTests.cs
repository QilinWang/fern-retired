using System.Globalization;
using Fern.Registry;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// `list-studies` answers what a Registry Home holds, which nothing could answer before: every other
/// read needed a study name. These pin the fact it reports — a cell is accepted when it names the
/// attempt that was accepted — against a real Home built by the production migrations.
/// </summary>
public sealed class ListStudiesTests
{
    /// <summary>A study names the source it was registered from, so the test makes one first.</summary>
    private static long AddSource(SqliteConnection connection)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
            VALUES ('0000000000000000000000000000000000000000', '[]', 'commit', '2026-09-13T00:00:00Z') RETURNING id
            """;
        return Convert.ToInt64(insert.ExecuteScalar()!, CultureInfo.InvariantCulture);
    }

    private static long AddStudy(SqliteConnection connection, string name, string state, string createdAt)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO studies (name, revision, compiled_study_json, source_id, requested_state, created_at)
            VALUES ($name, 1, '{}', $source, $state, $now) RETURNING id
            """;
        insert.Parameters.AddWithValue("$source", AddSource(connection));
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$state", state);
        insert.Parameters.AddWithValue("$now", createdAt);
        return Convert.ToInt64(insert.ExecuteScalar()!, CultureInfo.InvariantCulture);
    }

    private static long AddCell(SqliteConnection connection, long studyId, int ordinal, bool accepted)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO cells (study_id, ordinal, name, dataset, seed, config_json,
                               requirements_json, retention_json, created_at)
            VALUES ($study, $ordinal, $name, 'd', 1, '{}', '{}', '{}', '2026-09-13T00:00:00Z') RETURNING id
            """;
        insert.Parameters.AddWithValue("$study", studyId);
        insert.Parameters.AddWithValue("$ordinal", ordinal);
        insert.Parameters.AddWithValue("$name", $"cell-{ordinal}");
        var cellId = Convert.ToInt64(insert.ExecuteScalar()!, CultureInfo.InvariantCulture);
        if (accepted)
        {
            // A cell is accepted by naming a real attempt of its own; there is no attempt state of
            // "accepted", and the foreign key means the attempt has to exist.
            using var site = connection.CreateCommand();
            site.CommandText = """
                INSERT INTO sites (name, capabilities_json, capacity_json, created_at)
                VALUES ($name, '{}', '{}', '2026-09-13T00:00:00Z') RETURNING id
                """;
            site.Parameters.AddWithValue("$name", $"site-{cellId}");
            var siteId = Convert.ToInt64(site.ExecuteScalar()!, CultureInfo.InvariantCulture);

            using var attempt = connection.CreateCommand();
            attempt.CommandText = """
                INSERT INTO attempts (cell_id, ordinal, site_id, fencing_epoch, state, request_json, started_at)
                VALUES ($cell, 0, $site, 1, 'completed', '{}', '2026-09-13T00:00:00Z') RETURNING id
                """;
            attempt.Parameters.AddWithValue("$cell", cellId);
            attempt.Parameters.AddWithValue("$site", siteId);
            var attemptId = Convert.ToInt64(attempt.ExecuteScalar()!, CultureInfo.InvariantCulture);

            using var accept = connection.CreateCommand();
            accept.CommandText = "UPDATE cells SET accepted_attempt_id = $attempt WHERE id = $id";
            accept.Parameters.AddWithValue("$attempt", attemptId);
            accept.Parameters.AddWithValue("$id", cellId);
            accept.ExecuteNonQuery();
        }
        return cellId;
    }

    [Fact]
    public void AHomeReportsEveryStudyNewestFirstWithHowItsCellsStand()
    {
        var registry = ProductionHome.CreateRegistry();
        using (var writer = RegistryHome.OpenWriter(registry))
        {
            var older = AddStudy(writer.Connection, "older-study", "run", "2026-09-10T00:00:00Z");
            AddCell(writer.Connection, older, 0, accepted: true);
            AddCell(writer.Connection, older, 1, accepted: false);
            var newer = AddStudy(writer.Connection, "newer-study", "pause", "2026-09-12T00:00:00Z");
            AddCell(writer.Connection, newer, 0, accepted: true);
        }

        var listed = RunListStudies(registry, state: null);

        Assert.Equal(new[] { "newer-study", "older-study" }, listed.Select(row => row.Name));
        Assert.Equal((1L, 1L), (listed[0].Cells, listed[0].Accepted));
        Assert.Equal((2L, 1L), (listed[1].Cells, listed[1].Accepted));
        Assert.Equal("pause", listed[0].State);
    }

    [Fact]
    public void AStateNarrowsTheListAndAnEmptyHomeIsNotAnError()
    {
        var registry = ProductionHome.CreateRegistry();
        Assert.Empty(RunListStudies(registry, state: null));

        using (var writer = RegistryHome.OpenWriter(registry))
        {
            AddStudy(writer.Connection, "running", "run", "2026-09-12T00:00:00Z");
            AddStudy(writer.Connection, "paused", "pause", "2026-09-12T01:00:00Z");
        }

        Assert.Equal(new[] { "running" }, RunListStudies(registry, "run").Select(row => row.Name));
        Assert.Empty(RunListStudies(registry, "cancel"));
    }

    [Fact]
    public void AStudyWithNoCellsReportsZeroRatherThanVanishing()
    {
        var registry = ProductionHome.CreateRegistry();
        using (var writer = RegistryHome.OpenWriter(registry))
            AddStudy(writer.Connection, "registered-but-empty", "run", "2026-09-12T00:00:00Z");

        var listed = RunListStudies(registry, state: null);

        Assert.Single(listed);
        Assert.Equal((0L, 0L), (listed[0].Cells, listed[0].Accepted));
    }

    private sealed record Row(string Name, string State, long Cells, long Accepted);

    /// <summary>The same query the verb runs, against the same Home, so the test pins the fact and not a mock.</summary>
    private static List<Row> RunListStudies(string registry, string? state)
    {
        using var reader = RegistryHome.OpenReader(registry);
        using var command = reader.Connection.CreateCommand();
        command.CommandText = """
            SELECT s.name, s.requested_state,
                   (SELECT COUNT(*) FROM cells c WHERE c.study_id = s.id) AS cells,
                   (SELECT COUNT(*) FROM cells c WHERE c.study_id = s.id
                      AND c.accepted_attempt_id IS NOT NULL) AS accepted
            FROM studies s
            WHERE ($state IS NULL OR s.requested_state = $state)
            ORDER BY s.created_at DESC, s.name
            """;
        command.Parameters.AddWithValue("$state", (object?)state ?? DBNull.Value);
        var rows = new List<Row>();
        using var found = command.ExecuteReader();
        while (found.Read())
            rows.Add(new Row(found.GetString(0), found.GetString(1), found.GetInt64(2), found.GetInt64(3)));
        return rows;
    }
}
