using System.Globalization;
using System.Text.Json;
using Fern.RunData;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// Source-test inventory for the first per-lane read domain:
/// <list type="bullet">
/// <item><description><c>test_enroll_and_complete</c>, <c>test_reenroll_resets_status</c>, <c>test_enroll_registers_config_preimage</c>, <c>test_upsert_config_preimage_is_immutable</c>, and <c>test_reenroll_updates_code_fingerprint_to_latest_attempt</c> retain exact destination names below for the C# enrollment transaction. The Rust lifecycle tests keep their caller-boundary assertions.</description></item>
/// <item><description>The five <c>model_config_hash</c> source tests retain their exact destination names below, exercising the C# projection used by fresh enrollment and historical backfill. The former Rust digest is retired as implementation archaeology, not byte parity.</description></item>
/// <item><description><c>the_writer_fills_every_column_the_lists_promise</c> retains its exact destination name below as a real C# enrollment/read assertion. Its former Rust source-text scan was implementation archaeology and is retired.</description></item>
/// <item><description><c>accepted_typed_projection_is_atomic_idempotent_and_conflict_refusing</c> retains its exact Rust name and crosses the real C# child for the complete compatibility projection; <c>a_measured_zero_overlap_is_stored_and_an_unmeasured_one_stays_null</c> maps to exact find/nullability assertions.</description></item>
/// <item><description><c>test_registry_lifecycle_names_enroll_mark_running_complete</c> maps to pending, find, and usable projections.</description></item>
/// <item><description><c>test_compare</c> maps to grouped aggregate assertions.</description></item>
/// <item><description>The four <c>test_audit_run_artifact_storage_*</c> source tests retain their exact names and cross the real C# child, filesystem, and migrated SQLite database for every closed policy.</description></item>
/// <item><description><c>test_upsert_analysis_preimage_is_immutable</c> and <c>test_analysis_row_round_trips_and_rehashes_to_its_analysis_hash</c> retain their exact names and cross the real C# analysis writer/read/list child over migrated file databases.</description></item>
/// <item><description><c>discard_run_deletes_allowed_artifact_tree_then_row</c> and <c>discard_run_refuses_artifact_outside_allowed_roots_without_effect</c> cross the real C# discard child, filesystem, and migrated SQLite boundary.</description></item>
/// </list>
/// CHG 2026-09-05: the enrollment writer and its exclusive tests are retired.
/// The original inventory above remains historical context; retained completion,
/// failure and config-read tests now use fixed fixture rows, not enrollment.
/// The source-to-test dispositions live with retired/csharp/run-enrollment-2026-09-05.
/// </summary>
// CHG 2026-09-12: these open an ARCHIVE, not a Registry Home. The per-run
// tables were part of the Home's schema until version 8, so the fixture used
// to be a Home; no operation of a Home ever wrote a run, and the readers under
// test take the database they read by argument. RunArchive.Create makes the
// database these rows actually live in.
public sealed class RunViewsReaderTests
{
    [Fact]
    public void lifecycle_fixture_types_belong_only_to_the_test_assembly()
    {
        var production = typeof(RunViewsReader).Assembly;
        foreach (var fixture in new[] { typeof(RunLifecycleFixtureStore), typeof(RunLifecycleFixtureRequest), typeof(RunLifecycleFixtureReport) })
        {
            Assert.Same(typeof(RunViewsReaderTests).Assembly, fixture.Assembly);
            Assert.Null(production.GetType($"Fern.RunData.{fixture.Name}"));
        }
    }

    [Fact]
    public void historical_result_readers_refuse_invalid_inputs_without_mutation()
    {
        var directory = Directory.CreateTempSubdirectory("historical-result-invalid-").FullName;
        try
        {
            var corrupt = Path.Combine(directory, "corrupt.db");
            File.WriteAllText(corrupt, "not a SQLite database");
            var before = File.ReadAllBytes(corrupt);
            Assert.Throws<SqliteException>(() => AnalysisDataStore.Find(corrupt, "unused"));
            Assert.Throws<SqliteException>(() => AnalysisDataStore.List(corrupt, null, null));
            var dashboardFault = Assert.Throws<RunDataFault>(() => HistoricalDashboardReader.List(corrupt, "done", null));
            Assert.IsType<SqliteException>(dashboardFault.InnerException);
            Assert.Equal(before, File.ReadAllBytes(corrupt));
            var fault = Assert.Throws<RunDataFault>(() => AnalysisDataStore.Find(directory, "unused"));
            Assert.Equal($"run database {directory} is not a regular file", fault.Message);
            var missing = Path.Combine(directory, "missing.db");
            Assert.Throws<RunDataFault>(() => AnalysisDataStore.Find(missing, "unused"));
            Assert.Throws<RunDataFault>(() => AnalysisDataStore.List(missing, null, null));
            Assert.Throws<RunDataFault>(() => HistoricalDashboardReader.List(missing, "done", null));
            Assert.Equal(new[] { corrupt }, Directory.GetFileSystemEntries(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void lifecycle_fixture_refuses_unknown_state_without_row_or_event_changes()
    {
        var path = RunArchive.Create();
        const string runHash = "fixture-unknown-state";
        InsertRun(path, runHash, "lorenz", "fern", "pending", 42, 0.1, null, pinned: 0);
        var before = RunViewsReader.FindByRunHash(path, runHash);
        var error = Assert.Throws<RunDataFault>(() =>
            RunLifecycleFixtureStore.Apply(path, new(runHash, "unknown-state", null, null)));
        Assert.Equal("unknown fixture Run state unknown-state", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(before, RunViewsReader.FindByRunHash(path, runHash));
        Assert.Empty(RunEventStore.List(path, runHash).Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void named_run_mapping_preserves_every_value_with_reordered_storage_columns(bool nullableValues)
    {
        using var fixture = new RunMappingFixture(nullableValues);
        var before = File.ReadAllBytes(fixture.Path);
        var found = Assert.IsType<RunDataRow>(RunViewsReader.FindByRunHash(fixture.Path, "mapping-fixture"));
        Assert.Equal(fixture.Expected, found);
        Assert.Equal(JsonSerializer.Serialize(fixture.Expected, RunDataJson.Options), JsonSerializer.Serialize(found, RunDataJson.Options));
        Assert.Equal(fixture.Expected, Assert.Single(RunViewsReader.List(new(DatabasePath: fixture.Path)).Runs));
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
        Assert.Null(RunViewsReader.FindByRunHash(fixture.Path, "absent"));
    }

    [Theory]
    [InlineData("dataset", "NULL", "Dataset")]
    [InlineData("seed", "NULL", "Seed")]
    [InlineData("seed", "'not-an-integer'", "Seed")]
    [InlineData("mse", "'not-a-real'", "Mse")]
    [InlineData("config_json", "X'FF00'", "ConfigJson")]
    public void named_run_mapping_refuses_null_or_wrong_storage_type(string column, string value, string field)
    {
        using var fixture = new RunMappingFixture(nullableValues: false);
        fixture.Execute($"UPDATE runs SET {column} = {value}");
        var error = Assert.Throws<RunDataFault>(() => RunViewsReader.FindByRunHash(fixture.Path, "mapping-fixture"));
        Assert.Contains(field, error.Message);
    }

    [Fact]
    public void named_run_mapping_refuses_duplicate_identity_and_missing_legacy_column()
    {
        using var fixture = new RunMappingFixture(nullableValues: false);
        fixture.Execute("INSERT INTO runs SELECT * FROM runs");
        Assert.Contains("multiple run rows", Assert.Throws<RunDataFault>(() =>
            RunViewsReader.FindByRunHash(fixture.Path, "mapping-fixture")).Message);
        fixture.Execute("DELETE FROM runs; ALTER TABLE runs DROP COLUMN target_hash");
        Assert.Throws<RunDataFault>(() => RunViewsReader.FindByRunHash(fixture.Path, "absent"));
        Assert.Throws<RunDataFault>(() => RunViewsReader.List(new(DatabasePath: fixture.Path)));
    }

    // Deliberately unconstrained, isolated storage lets the reader encounter
    // corrupt NULL/type/identity values that the production writer would refuse.
    sealed class RunMappingFixture : IDisposable
    {
        readonly DirectoryInfo root = Directory.CreateTempSubdirectory("fern-run-mapping-");
        public string Path => System.IO.Path.Combine(root.FullName, "fixture.db");
        public RunDataRow Expected { get; }

        public RunMappingFixture(bool nullableValues)
        {
            var fields = typeof(RunDataRow).GetProperties();
            var nullability = new System.Reflection.NullabilityInfoContext();
            var values = fields.Select((field, index) => new
            {
                field.Name,
                Column = field.Name == "ErrorMessage" ? "error_msg" : JsonNamingPolicy.SnakeCaseLower.ConvertName(field.Name),
                Type = Nullable.GetUnderlyingType(field.PropertyType) ?? field.PropertyType,
                Nullable = nullability.Create(field).ReadState == System.Reflection.NullabilityState.Nullable,
                Index = index,
            }).ToArray();
            var data = values.ToDictionary(field => field.Name, field =>
                nullableValues && field.Nullable && field.Name != "RunHash" ? null :
                field.Type == typeof(string) ? (object)(field.Name == "RunHash" ? "mapping-fixture" : field.Name == "Status" ? "done" : field.Name + " 中文 ' [literal]") :
                field.Type == typeof(long) ? (object)(field.Name == "Id" ? long.MaxValue : field.Name == "Seed" ? long.MinValue : (long)field.Index) : (object)(field.Index + 0.125));
            Expected = JsonSerializer.Deserialize<RunDataRow>(JsonSerializer.Serialize(
                data.ToDictionary(pair => pair.Key == "ErrorMessage" ? "error_msg" : pair.Key, pair => pair.Value)))!;
            using var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE runs (extra_column TEXT, " + string.Join(", ", values.Reverse().Select(field =>
                field.Column + " " + (field.Type == typeof(string) ? "TEXT" : field.Type == typeof(long) ? "INTEGER" : "REAL"))) + ")";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO runs (" + string.Join(", ", values.Select(field => field.Column)) + ") VALUES ("
                + string.Join(", ", values.Select(field => "$" + field.Name)) + ")";
            foreach (var field in values) command.Parameters.AddWithValue("$" + field.Name, data[field.Name] ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        public void Execute(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose() => root.Delete(recursive: true);
    }

    [Fact]
    public void test_complete_historical_run_records_metrics_and_events()
    {
        var path = RunArchive.Create();
        const string runHash = "historical-complete";
        InsertRun(path, runHash, "lorenz", "fern", "pending", 42, 0.1, null, pinned: 0);
        RunEventStore.Record(path, runHash, "enrolled", null);
        Assert.Equal("pending", RunViewsReader.FindByRunHash(path, runHash)!.Status);

        Assert.Equal(1, RunLifecycleFixtureStore.Apply(path, new(runHash, "running", null, null)).RowsChanged);
        Assert.Equal("running", RunViewsReader.FindByRunHash(path, runHash)!.Status);
        var metrics = new RunMetricValues(0.003, 0.02, null, null, null, 0.3, 0.1, 0.0, 0.5, 0.6, 0.8);
        Assert.Equal(1, RunLifecycleFixtureStore.Apply(path, new(runHash, "done", metrics, null)).RowsChanged);
        var completed = RunViewsReader.FindByRunHash(path, runHash)!;
        Assert.Equal("done", completed.Status);
        Assert.Equal(runHash, completed.SuccessHash);
        Assert.Equal(0.003, completed.Mse);
        Assert.Equal(0.0, completed.NoiseForecastOverlap);
        Assert.Equal(new[] { "enrolled", "running", "completed" },
            RunEventStore.List(path, runHash).Events.Select(@event => @event.Event));
    }

    [Fact]
    public void test_mark_failed_and_stale_fixture_transitions_record_bounded_events()
    {
        var path = RunArchive.Create();
        const string failed = "fixture-failed", stale = "fixture-stale";
        InsertRun(path, failed, "lorenz", "fern", "pending", 42, 0.1, null, pinned: 0);
        InsertRun(path, stale, "lorenz", "fern", "pending", 42, 0.1, null, pinned: 0);
        var longDetail = new string('x', 700);
        Assert.Equal(1, RunLifecycleFixtureStore.Apply(path, new(failed, "failed", null, longDetail)).RowsChanged);
        Assert.Equal(1, RunLifecycleFixtureStore.Apply(path, new(stale, "stale", null, "missing artifact")).RowsChanged);
        Assert.Equal("failed", RunViewsReader.FindByRunHash(path, failed)!.Status);
        Assert.Equal(longDetail, RunViewsReader.FindByRunHash(path, failed)!.ErrorMessage);
        var detail = RunEventStore.List(path, failed).Events.Single(e => e.Event == "failed").DetailJson!;
        Assert.Equal(500, JsonDocument.Parse(detail).RootElement.GetProperty("error").GetString()!.Length);
        Assert.Equal("stale", RunViewsReader.FindByRunHash(path, stale)!.Status);
        Assert.Equal(0, RunLifecycleFixtureStore.Apply(path, new("unknown", "running", null, null)).RowsChanged);
        Assert.Empty(RunEventStore.List(path, "unknown").Events);
    }

    [Fact]
    public void config_preimage_reader_preserves_stored_bytes_and_missing_null()
    {
        var path = RunArchive.Create();
        const string configJson = """{"schema":"fern.cfg.v1","model":{"type":"fern"}}""";
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO configs (config_hash, schema, config_json) VALUES ('historical-config', 'fern.cfg.v1', $json)";
            insert.Parameters.AddWithValue("$json", configJson);
            Assert.Equal(1, insert.ExecuteNonQuery());
        }
        Assert.Equal(("fern.cfg.v1", configJson), ReadConfig(path, "historical-config"));
        var receipt = RunConfigPreimageStore.Find(path, "historical-config");
        Assert.Equal((RunConfigPreimageStore.ReadSchema, "historical-config", configJson),
            (receipt.Schema, receipt.RequestedConfigHash, receipt.ConfigJson));
        Assert.Null(RunConfigPreimageStore.Find(path, "missing-config").ConfigJson);
    }

    [Fact]
    public void test_record_and_list_run_events_round_trip()
    {
        var path = RunArchive.Create();

        var spawned = RunEventStore.Record(path, "run-a", "worker_spawned", "{\"pid\":1234}");
        _ = RunEventStore.Record(path, "run-b", "worker_spawned", null);
        var exited = RunEventStore.Record(path, "run-a", "worker_exited", null);
        var listed = RunEventStore.List(path, "run-a");

        Assert.Equal(RunEventStore.WriteSchema, spawned.Schema);
        Assert.Equal(("run-a", "worker_spawned", "{\"pid\":1234}"),
            (spawned.Event.RunHash, spawned.Event.Event, spawned.Event.DetailJson));
        Assert.True(exited.Event.Id > spawned.Event.Id);
        Assert.Equal(RunViewsReader.EventListSchema, listed.Schema);
        Assert.Equal(["worker_spawned", "worker_exited"], listed.Events.Select(item => item.Event));
        Assert.Equal([spawned.Event.Id, exited.Event.Id], listed.Events.Select(item => item.Id));
        Assert.Null(listed.Events[1].DetailJson);
        Assert.Empty(RunEventStore.List(path, "missing-run").Events);
        Assert.Throws<RunDataFault>(() =>
            RunEventStore.Record(Path.Combine(Path.GetTempPath(), "missing-run-event.db"), "run", "event", null));
    }

    [Fact]
    public void test_pin_prevents_slim()
    {
        var path = RunArchive.Create();
        InsertRun(path, "pin-me", "lorenz", "fern", "done", 4, 0.1, 0.25, pinned: 0);
        var runId = Assert.IsType<long>(RunViewsReader.FindByRunHash(path, "pin-me")?.Id);

        var protectedRun = RunPinStore.Set(path, runId, true);
        Assert.Equal((RunPinStore.WriteSchema, runId, 1L, 1L),
            (protectedRun.Schema, protectedRun.RunId, protectedRun.Pinned, protectedRun.RowsChanged));
        Assert.Equal(1, RunViewsReader.FindByRunHash(path, "pin-me")?.Pinned);

        var unprotectedRun = RunPinStore.Set(path, runId, false);
        Assert.Equal((runId, 0L, 1L),
            (unprotectedRun.RunId, unprotectedRun.Pinned, unprotectedRun.RowsChanged));
        Assert.Equal(0, RunViewsReader.FindByRunHash(path, "pin-me")?.Pinned);

        Assert.Equal(0, RunPinStore.Set(path, runId + 10_000, true).RowsChanged);
    }

    [Fact]
    public void slim_run_filesystem_and_sql_boundary_replays_after_post_delete_update_refusal()
    {
        var path = RunArchive.Create();
        var artifacts = Directory.CreateTempSubdirectory("fern-slim-boundary-");
        File.WriteAllBytes(Path.Combine(artifacts.FullName, "model.safetensors"), new byte[11]);
        File.WriteAllBytes(Path.Combine(artifacts.FullName, "sequence_tensors.safetensors"), new byte[13]);
        File.WriteAllText(Path.Combine(artifacts.FullName, "metrics.json"), "survives");
        var runId = InsertRun(path, "slim-boundary", "lorenz", "fern", "done", 4, 0.1, 0.25, pinned: 1,
            artifactDirectory: artifacts.FullName);

        Assert.Equal(0, RunSlimStore.Slim(path, runId).FreedBytes);
        Assert.True(File.Exists(Path.Combine(artifacts.FullName, "model.safetensors")));
        _ = RunPinStore.Set(path, runId, false);
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER refuse_slim BEFORE UPDATE OF artifacts ON runs
                WHEN NEW.artifacts = 'slim' BEGIN SELECT RAISE(ABORT, 'refuse slim'); END;
                """;
            command.ExecuteNonQuery();
        }

        // CHG 2026-09-10: the boundary moved, on purpose, and this is the
        // assertion that records it. Slim used to delete the heavy files and
        // THEN mark the row, so a refused mark left the bytes gone and the row
        // saying "full", and a replay finished the job. That ordering is what
        // let `protect-run` fail silently: a sweep that had read `pinned = 0`
        // deleted the weights of a run somebody protected a moment later.
        //
        // Slim now claims the run first — one conditional UPDATE repeating both
        // conditions, with an affected-rows check — and deletes only after the
        // claim succeeds. So a refused mark now deletes NOTHING, which is the
        // safe direction: over-reporting is recoverable, lost bytes are not.
        // The property this test exists for, that a refusal replays cleanly, is
        // unchanged and is still asserted below.
        Assert.Throws<RunDataFault>(() => RunSlimStore.Slim(path, runId));
        Assert.True(File.Exists(Path.Combine(artifacts.FullName, "model.safetensors")));
        Assert.True(File.Exists(Path.Combine(artifacts.FullName, "sequence_tensors.safetensors")));
        Assert.True(File.Exists(Path.Combine(artifacts.FullName, "metrics.json")));
        Assert.Equal("full", RunViewsReader.FindByRunHash(path, "slim-boundary")?.Artifacts);

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER refuse_slim";
            command.ExecuteNonQuery();
        }
        File.WriteAllBytes(Path.Combine(artifacts.FullName, "raw_tensors.safetensors"), new byte[7]);
        var replay = RunSlimStore.Slim(path, runId);
        Assert.Equal((RunSlimStore.WriteSchema, runId, 31L, 1L),
            (replay.Schema, replay.RunId, replay.FreedBytes, replay.RowsChanged));
        Assert.Equal("slim", RunViewsReader.FindByRunHash(path, "slim-boundary")?.Artifacts);
        Assert.True(File.Exists(Path.Combine(artifacts.FullName, "metrics.json")));
        artifacts.Delete(recursive: true);
    }

    [Fact]
    public void slim_run_by_group_keeps_the_best_unpinned_candidate_and_slims_the_rest()
    {
        var path = RunArchive.Create();
        var best = Directory.CreateTempSubdirectory("fern-slim-best-");
        var worse = Directory.CreateTempSubdirectory("fern-slim-worse-");
        var missingMetric = Directory.CreateTempSubdirectory("fern-slim-missing-metric-");
        var pinned = Directory.CreateTempSubdirectory("fern-slim-pinned-");
        File.WriteAllBytes(Path.Combine(best.FullName, "model.safetensors"), new byte[3]);
        File.WriteAllBytes(Path.Combine(worse.FullName, "model.safetensors"), new byte[5]);
        File.WriteAllBytes(Path.Combine(missingMetric.FullName, "model.safetensors"), new byte[7]);
        File.WriteAllBytes(Path.Combine(pinned.FullName, "model.safetensors"), new byte[11]);
        InsertRun(path, "best", "lorenz", "fern", "done", 1, 0.1, 0.1, pinned: 0, artifactDirectory: best.FullName);
        InsertRun(path, "worse", "lorenz", "fern", "done", 2, 0.1, 0.2, pinned: 0, artifactDirectory: worse.FullName);
        InsertRun(path, "missing-metric", "lorenz", "fern", "done", 3, 0.1, null, pinned: 0, artifactDirectory: missingMetric.FullName);
        InsertRun(path, "pinned", "lorenz", "fern", "done", 4, 0.1, 0.0, pinned: 1, artifactDirectory: pinned.FullName);

        var report = RunSlimStore.SlimByGroup(path, keepBest: 1);

        Assert.Equal((RunSlimStore.GroupWriteSchema, 1L, 2L, 12L),
            (report.Schema, report.Kept, report.Slimmed, report.FreedBytes));
        Assert.Equal("full", RunViewsReader.FindByRunHash(path, "best")?.Artifacts);
        Assert.Equal("slim", RunViewsReader.FindByRunHash(path, "worse")?.Artifacts);
        Assert.Equal("slim", RunViewsReader.FindByRunHash(path, "missing-metric")?.Artifacts);
        Assert.Equal("full", RunViewsReader.FindByRunHash(path, "pinned")?.Artifacts);
        Assert.True(File.Exists(Path.Combine(best.FullName, "model.safetensors")));
        Assert.False(File.Exists(Path.Combine(worse.FullName, "model.safetensors")));
        Assert.False(File.Exists(Path.Combine(missingMetric.FullName, "model.safetensors")));
        Assert.True(File.Exists(Path.Combine(pinned.FullName, "model.safetensors")));
    }

    [Fact]
    public void test_update_run_invocation()
    {
        var path = RunArchive.Create();
        InsertRun(path, "invocation-run", "lorenz", "fern", "pending", 4, 0.1, null, pinned: 0,
            configName: "old-config");

        var written = RunInvocationStore.Record(
            path,
            "invocation-run",
            "{\"schema\":\"fern.run_invocation.v1\",\"attempt\":1}",
            "new-config");
        Assert.Equal((RunInvocationStore.WriteSchema, "invocation-run", 1L),
            (written.Schema, written.RunHash, written.RowsChanged));
        var row = Assert.IsType<RunDataRow>(RunViewsReader.FindByRunHash(path, "invocation-run"));
        Assert.Equal("new-config", row.ConfigName);
        Assert.Contains("\"attempt\":1", row.InvocationJson);

        _ = RunInvocationStore.Record(
            path,
            "invocation-run",
            "{\"schema\":\"fern.run_invocation.v1\",\"attempt\":2}",
            null);
        row = Assert.IsType<RunDataRow>(RunViewsReader.FindByRunHash(path, "invocation-run"));
        Assert.Equal("new-config", row.ConfigName);
        Assert.Contains("\"attempt\":2", row.InvocationJson);
        Assert.Equal(0, RunInvocationStore.Record(path, "missing", "{}", null).RowsChanged);
    }

    [Fact]
    public void backfill_protocol_columns_preserves_nulls_and_projects_owned_evidence()
    {
        var path = RunArchive.Create();
        var artifacts = Directory.CreateTempSubdirectory("fern-protocol-backfill-");
        File.WriteAllText(Path.Combine(artifacts.FullName, "execution_environment.json"),
            "{\"cuda_devices\":[{\"name\":\"NVIDIA GeForce RTX 4090 GPU\"}]}");
        const string invocation = """
            {"identity":{"config":{"runtime":{"device":"cuda"},"execution":{"kind":"train"}},"context":{"protocol":{"noise_law":"ar1_gaussian","split_treatment_scheme":"noise_matched","relative_dose":true,"checkpoint_execution":{"mode":"fit"}}}}}
            """;
        _ = InsertRun(path, "project-me", "lorenz", "fern", "done", 4, 0.1, 0.25, pinned: 0,
            invocationJson: invocation, artifactDirectory: artifacts.FullName);
        _ = InsertRun(path, "leave-null", "lorenz", "fern", "done", 5, 0.2, 0.5, pinned: 0);
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE runs SET config_json = '{\"model\":{\"beta\":2,\"alpha\":1}}' WHERE run_hash = 'project-me'";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var dryRun = RunProtocolBackfillStore.Backfill(path, dryRun: true);
        Assert.Equal((RunProtocolBackfillStore.WriteSchema, 2L, 1L, 1L, true),
            (dryRun.Schema, dryRun.Rows, dryRun.RowsFilled, dryRun.RowsWithoutAProtocolBlock, dryRun.DryRun));
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT noise_law FROM runs WHERE run_hash = 'project-me'";
            Assert.Equal(DBNull.Value, command.ExecuteScalar());
        }

        var written = RunProtocolBackfillStore.Backfill(path, dryRun: false);
        Assert.Equal((2L, 1L, 1L, false),
            (written.Rows, written.RowsFilled, written.RowsWithoutAProtocolBlock, written.DryRun));
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT noise_law, split_treatment_scheme, noise_ar1_phi, relative_dose,
                       checkpoint_execution_mode, runtime_device, execution_kind,
                       machine_alias, model_config_hash
                FROM runs WHERE run_hash = 'project-me'
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("ar1_gaussian", reader.GetString(0));
            Assert.Equal("noise_matched", reader.GetString(1));
            Assert.Equal(0.0, reader.GetDouble(2));
            Assert.Equal(1, reader.GetInt64(3));
            Assert.Equal("fit", reader.GetString(4));
            Assert.Equal("cuda", reader.GetString(5));
            Assert.Equal("train", reader.GetString(6));
            Assert.Equal("unknown-host-geforce-rtx-4090", reader.GetString(7));
            Assert.Equal("e66aeff08063", reader.GetString(8));
        }
        Assert.Null(RunViewsReader.FindByRunHash(path, "leave-null")?.InvocationJson);
        artifacts.Delete(recursive: true);
    }

    [Fact]
    public void list_preserves_complete_rows_default_status_filters_order_and_exact_nullable_values()
    {
        var path = RunArchive.Create();
        InsertRun(path, "done-z", "alpha", "z-model", "done", 3, 1.0, 0.75, pinned: 0);
        InsertRun(path, "done-a", "alpha", "a-model", "done", 4, 0.1, 0.25, pinned: 1,
            trajectoryDrawOrdinal: 9_223_372_036_854_775_000,
            noiseForecastOverlap: 0.0,
            templateId: 42,
            nFeatures: 7,
            startedAt: "2026-08-27T12:00:00Z",
            invocationJson: "{\"schema\":\"fern.run_invocation.v1\"}",
            configName: "lorenz");
        InsertRun(path, "failed", "beta", "fern", "failed", 42, 0.0, null, pinned: 0, errorMessage: "OOM at epoch 5");
        InsertRun(path, "pending", "lorenz", "fern", "pending", 42, 0.1, null, pinned: 0);

        var report = RunViewsReader.List(new RunListRequest(DatabasePath: path));

        Assert.Equal("fern.run_list.v1", report.Schema);
        Assert.Equal(["done-a", "done-z"], report.Runs.Select(row => row.RunHash));
        var first = report.Runs[0];
        Assert.Equal(9_223_372_036_854_775_000, first.TrajectoryDrawOrdinal);
        Assert.Equal(0.1, first.NoiseSigma);
        Assert.Equal(0.0, first.NoiseForecastOverlap);
        Assert.Equal(42, first.TemplateId);
        Assert.Equal(7, first.NFeatures);
        Assert.Equal("2026-08-27T12:00:00Z", first.StartedAt);
        Assert.Equal("{\"schema\":\"fern.run_invocation.v1\"}", first.InvocationJson);
        Assert.Equal("lorenz", first.ConfigName);
        Assert.Null(first.CleanTargetMse);

        var template = RunViewsReader.List(new RunListRequest(DatabasePath: path, TemplateId: 42));
        Assert.Equal(["done-a"], template.Runs.Select(row => row.RunHash));
    }

    [Fact]
    public void find_by_hash_and_id_preserves_measured_zero_and_unmeasured_null()
    {
        var path = RunArchive.Create();
        var measuredId = InsertRun(path, "measured", "lorenz", "fern", "done", 42, 0.1, 0.5, pinned: 0,
            cleanTargetMse: 0.3,
            injectedTargetNoiseMse: 0.2,
            noiseForecastOverlap: 0.0);
        InsertRun(path, "unmeasured", "lorenz", "fern", "done", 43, 0.1, 0.5, pinned: 0);

        var byHash = RunViewsReader.FindByRunHash(path, "measured");
        var byId = RunViewsReader.FindById(path, measuredId);
        var listedById = RunViewsReader.List(new RunListRequest(DatabasePath: path, Id: measuredId));
        var blank = RunViewsReader.FindByRunHash(path, "unmeasured");

        Assert.NotNull(byHash);
        Assert.Equal(byHash, byId);
        Assert.Equal(byHash, Assert.Single(listedById.Runs));
        Assert.Equal(0.0, byHash.NoiseForecastOverlap);
        Assert.Equal(0.3, byHash.CleanTargetMse);
        Assert.Equal(0.2, byHash.InjectedTargetNoiseMse);
        Assert.InRange(Math.Abs(byHash.Mse!.Value - (byHash.CleanTargetMse!.Value + byHash.InjectedTargetNoiseMse!.Value + byHash.NoiseForecastOverlap!.Value)), 0.0, 1e-12);
        Assert.NotNull(blank);
        Assert.Null(blank.NoiseForecastOverlap);
        Assert.Null(blank.CleanTargetMse);
        Assert.Null(RunViewsReader.FindByRunHash(path, "missing"));
        Assert.Null(RunViewsReader.FindById(path, measuredId + 999));
    }

    [Fact]
    public void next_campaign_id_is_one_for_empty_and_maximum_template_plus_one()
    {
        var path = RunArchive.Create();
        Assert.Equal(1, RunViewsReader.NextCampaignId(path));

        InsertRun(path, "without-template", "lorenz", "fern", "done", 41, 0.1, 0.2, pinned: 0);
        InsertRun(path, "campaign-three", "lorenz", "fern", "done", 42, 0.1, 0.1, pinned: 0, templateId: 3);
        InsertRun(path, "campaign-seven", "lorenz", "fern", "done", 43, 0.1, 0.1, pinned: 0, templateId: 7);

        Assert.Equal(8, RunViewsReader.NextCampaignId(path));
    }

    [Fact]
    public void list_campaign_scope_filters_before_the_global_limit_and_refuses_an_empty_root()
    {
        var root = Directory.CreateTempSubdirectory("fern-run-data-campaign-");
        var first = MigratedLane(root, "lane-b");
        var second = MigratedLane(root, "lane-a");
        InsertRun(first, "z", "alpha", "z-model", "done", 3, 1.0, 0.75, pinned: 0);
        InsertRun(second, "a", "alpha", "a-model", "done", 4, 0.1, 0.25, pinned: 0);
        InsertRun(second, "other", "beta", "a-model", "done", 5, 0.1, 0.5, pinned: 0);

        var report = RunViewsReader.List(new RunListRequest(CampaignRoot: root.FullName, Dataset: "alpha", Limit: 1));

        Assert.Equal(["a"], report.Runs.Select(row => row.RunHash));
        Assert.Throws<RunDataFault>(() => RunViewsReader.List(new RunListRequest(CampaignRoot: Directory.CreateTempSubdirectory("empty-run-data-campaign-").FullName)));
    }

    [Fact]
    public void test_compare()
    {
        var path = RunArchive.Create();
        InsertRun(path, "fern-one", "lorenz", "fern", "done", 42, 0.1, 0.01, pinned: 0, templateId: 4, successHash: "fern-one");
        InsertRun(path, "fern-two", "lorenz", "fern", "done", 43, 0.1, 0.03, pinned: 0, templateId: 4, successHash: "fern-two");
        InsertRun(path, "dlinear", "lorenz", "dlinear", "done", 42, 0.1, 0.05, pinned: 0, templateId: 4, successHash: "different-hash");
        InsertRun(path, "pending", "lorenz", "fern", "pending", 44, 0.1, null, pinned: 0, templateId: 4);
        InsertRun(path, "failed", "lorenz", "fern", "failed", 45, 0.1, null, pinned: 0, templateId: 4);
        InsertRun(path, "running", "lorenz", "fern", "running", 46, 0.1, null, pinned: 0, templateId: 4);

        var comparison = RunViewsReader.Compare(path, "lorenz");
        var pending = RunViewsReader.Pending(path, 4, includeFailed: true, includeRunning: true);
        var usable = RunViewsReader.Usable(path, 4);

        Assert.Equal(2, comparison.Count);
        var fern = Assert.Single(comparison, row => row.ModelType == "fern");
        Assert.Equal(2, fern.NSeeds);
        Assert.InRange(Math.Abs(fern.AvgMse!.Value - 0.02), 0.0, 1e-9);
        Assert.Equal(["pending", "failed", "running"], pending.Select(row => row.Status));
        Assert.Equal(["fern-one", "fern-two"], usable.Select(row => row.RunHash));
    }

    [Fact]
    public void campaign_statistics_and_worker_events_preserve_run_identity()
    {
        var path = RunArchive.Create();
        InsertRun(path, "selected", "lorenz", "fern", "done", 42, 0.1, 0.25, pinned: 0,
            trajectoryDrawOrdinal: 3, targetHash: "target-hash",
            finishedAt: "2026-08-27T12:00:00Z", templateId: 42);
        InsertRun(path, "other-campaign", "ou", "duet", "done", 7, 0.5, 0.0, pinned: 0, templateId: 43);
        InsertRun(path, "not-done", "ou", "duet", "running", 8, 0.5, null, pinned: 0, templateId: 42);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO run_events (run_hash, event, detail_json) VALUES
                    ('selected', 'worker_spawned', '{"ignored":true}'),
                    ('selected', 'worker_exited', '{"duration_seconds":3}'),
                    ('other-campaign', 'worker_exited', '{"duration_seconds":9}');
                """;
            command.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(path);
        var selected = Assert.Single(RunViewsReader.StatisticsRuns(path, 42));
        Assert.Equal("selected", selected.RunHash);
        Assert.Equal("target-hash", selected.TargetHash);
        Assert.Equal("2026-08-27T12:00:00Z", selected.FinishedAt);
        Assert.Equal(0.25, selected.Mse);
        Assert.Empty(RunViewsReader.StatisticsRuns(path, 44));
        var workerExit = Assert.Single(RunViewsReader.WorkerExitedEvents(path, ["selected"]));
        Assert.Equal("selected", workerExit.RunHash);
        Assert.Equal("worker_exited", workerExit.Event);
        Assert.Equal("{\"duration_seconds\":3}", workerExit.DetailJson);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void test_audit_run_artifact_storage_report_only_is_read_only()
    {
        var path = RunArchive.Create();
        InsertRun(path, "missing-artifact", "lorenz", "fern", "done", 42, 0.1, 0.1, pinned: 0,
            artifactDirectory: Path.Combine(Path.GetTempPath(), $"missing-artifact-{Guid.NewGuid():N}"));
        var before = File.ReadAllBytes(path);

        var projection = RunViewsReader.ProjectStorageAudit(path);

        Assert.Equal(1, projection.Checked);
        Assert.Equal(1, projection.Missing);
        Assert.Equal(0, projection.MarkedDbMetricsOnly);
        Assert.Equal(0, projection.MarkedStale);
        Assert.Equal(0, projection.StagedDbRowDeletion);
        Assert.Equal(0, projection.PurgedDbRows);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void test_audit_run_artifact_storage_mark_db_metrics_only_keeps_done_row()
    {
        var path = RunArchive.Create();
        var missing = Path.Combine(Path.GetTempPath(), $"missing-artifact-{Guid.NewGuid():N}");
        InsertRun(path, "missing-done-artifact", "lorenz", "fern", "done", 42, 0.1, 0.1, pinned: 0,
            artifactDirectory: missing);

        var report = RunStorageAuditStore.Audit(path, "mark_db_metrics_only", dryRun: false, markUnfinishedStale: true);

        Assert.Equal((1L, 1L, 1L, 0L),
            (report.Checked, report.Missing, report.MarkedDbMetricsOnly, report.MarkedStale));
        var row = Assert.IsType<RunDataRow>(RunViewsReader.FindByRunHash(path, "missing-done-artifact"));
        Assert.Equal(("done", "none", null), (row.Status, row.Artifacts, row.ArtifactDir));
    }

    [Fact]
    public void test_audit_run_artifact_storage_stage_db_row_deletion_marks_stale()
    {
        var path = RunArchive.Create();
        var missing = Path.Combine(Path.GetTempPath(), $"missing-artifact-{Guid.NewGuid():N}");
        InsertRun(path, "staged-missing-artifact", "lorenz", "fern", "done", 42, 0.1, 0.1, pinned: 0,
            artifactDirectory: missing);

        var report = RunStorageAuditStore.Audit(path, "stage_db_row_deletion", dryRun: false, markUnfinishedStale: true);

        Assert.Equal((1L, 1L, 1L, 1L, 1L),
            (report.Checked, report.Missing, report.MarkedDbMetricsOnly, report.MarkedStale, report.StagedDbRowDeletion));
        var row = Assert.IsType<RunDataRow>(RunViewsReader.FindByRunHash(path, "staged-missing-artifact"));
        Assert.Equal(("stale", "none", null, "artifact_dir missing; staged for deletion"),
            (row.Status, row.Artifacts, row.ArtifactDir, row.ErrorMessage));
    }

    [Fact]
    public void test_audit_run_artifact_storage_purge_db_rows_deletes_rows()
    {
        var path = RunArchive.Create();
        var missing = Path.Combine(Path.GetTempPath(), $"missing-artifact-{Guid.NewGuid():N}");
        InsertRun(path, "purged-missing-artifact", "lorenz", "fern", "done", 42, 0.1, 0.1, pinned: 0,
            artifactDirectory: missing);

        var report = RunStorageAuditStore.Audit(path, "purge_db_rows", dryRun: false, markUnfinishedStale: true);

        Assert.Equal((1L, 1L, 1L), (report.Checked, report.Missing, report.PurgedDbRows));
        Assert.Null(RunViewsReader.FindByRunHash(path, "purged-missing-artifact"));
    }

    [Fact]
    public void storage_audit_uses_the_same_path_exists_semantics_as_the_rust_report_only_reader()
    {
        var path = RunArchive.Create();
        var artifactFile = Path.GetTempFileName();
        try
        {
            InsertRun(path, "file-artifact", "lorenz", "fern", "done", 42, 0.1, 0.1, pinned: 0, artifactDirectory: artifactFile);

            var projection = RunViewsReader.ProjectStorageAudit(path);

            Assert.Equal(1, projection.Checked);
            Assert.Equal(0, projection.Missing);
        }
        finally
        {
            File.Delete(artifactFile);
        }
    }

    [Fact]
    public void list_refusal_leaves_production_migrated_lane_bytes_unchanged()
    {
        var path = RunArchive.Create();
        InsertRun(path, "done", "lorenz", "fern", "done", 42, 0.1, 0.1, pinned: 0);
        var before = File.ReadAllBytes(path);

        Assert.Throws<RunDataFault>(() => RunViewsReader.List(new RunListRequest(DatabasePath: Path.Combine(Path.GetTempPath(), "missing-fern.db"))));

        Assert.Equal(before, File.ReadAllBytes(path));
    }


    static (string Schema, string ConfigJson) ReadConfig(string path, string configHash)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema, config_json FROM configs WHERE config_hash = $config_hash";
        command.Parameters.AddWithValue("$config_hash", configHash);
        using var row = command.ExecuteReader();
        Assert.True(row.Read());
        return (row.GetString(0), row.GetString(1));
    }

    static T Scalar<T>(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return Assert.IsType<T>(value);
    }

    static long InsertRun(
        string path,
        string runHash,
        string dataset,
        string model,
        string status,
        long seed,
        double noiseSigma,
        double? mse,
        long pinned,
        long trajectoryDrawOrdinal = 0,
        double? cleanTargetMse = null,
        double? injectedTargetNoiseMse = null,
        double? noiseForecastOverlap = null,
        long? templateId = null,
        long? nFeatures = null,
        string? startedAt = null,
        string? invocationJson = null,
        string? configName = null,
        string? errorMessage = null,
        string? successHash = null,
        string? artifactDirectory = null,
        string? targetHash = null,
        string? finishedAt = null,
        string? machineAlias = null)
    {
        // Release the fixture writer when this scope ends, before callers
        // inspect the database bytes with no outstanding writer handle.
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO runs (
                artifact_dir, dataset, dataset_id, model_type, seed, pred_len, seq_len, noise_sigma,
                trajectory_draw_ordinal, noise_draw_ordinal, trajectory_seed_base, noise_seed_base,
                trajectory_draw_seed, noise_draw_seed, run_hash, success_hash, config_name, invocation_json,
                status, started_at, finished_at, error_msg, source, artifacts, pinned, mse, clean_target_mse,
                injected_target_noise_mse, noise_forecast_overlap, template_id, n_features,
                target_hash, machine_alias)
            VALUES (
                $artifact_dir, $dataset, $dataset_id, $model_type, $seed, 8, 16, $noise_sigma,
                $trajectory_draw_ordinal, 0, 0, 0, 0, 0, $run_hash, $success_hash, $config_name, $invocation_json,
                $status, $started_at, $finished_at, $error_msg, 'user_defined', 'full', $pinned, $mse, $clean_target_mse,
                $injected_target_noise_mse, $noise_forecast_overlap, $template_id, $n_features,
                $target_hash, $machine_alias)
            RETURNING id;
            """;
        Add(command, "$artifact_dir", artifactDirectory ?? Path.Combine(Path.GetTempPath(), $"artifact-{runHash}"));
        Add(command, "$dataset", dataset);
        Add(command, "$dataset_id", dataset + "_id");
        Add(command, "$model_type", model);
        Add(command, "$seed", seed);
        Add(command, "$noise_sigma", noiseSigma);
        Add(command, "$trajectory_draw_ordinal", trajectoryDrawOrdinal);
        Add(command, "$run_hash", runHash);
        Add(command, "$success_hash", successHash);
        Add(command, "$config_name", configName);
        Add(command, "$invocation_json", invocationJson);
        Add(command, "$status", status);
        Add(command, "$started_at", startedAt);
        Add(command, "$finished_at", finishedAt);
        Add(command, "$error_msg", errorMessage);
        Add(command, "$pinned", pinned);
        Add(command, "$mse", mse);
        Add(command, "$clean_target_mse", cleanTargetMse);
        Add(command, "$injected_target_noise_mse", injectedTargetNoiseMse);
        Add(command, "$noise_forecast_overlap", noiseForecastOverlap);
        Add(command, "$template_id", templateId);
        Add(command, "$n_features", nFeatures);
        Add(command, "$target_hash", targetHash);
        Add(command, "$machine_alias", machineAlias);
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void a_missing_config_has_no_build_hash()
    {
        Assert.Null(RunDataProjection.ModelConfigHash(null));
        Assert.Null(RunDataProjection.ModelConfigHash("not json"));
        Assert.Null(RunDataProjection.ModelConfigHash("""{"runtime": {}}"""));
    }

    [Fact]
    public void the_hash_is_twelve_hex()
    {
        var hash = RunDataProjection.ModelConfigHash("""{"model":{"name":"fern","knobs":{"fern_architecture":"icml_pinned"}}}""");
        Assert.NotNull(hash);
        Assert.Matches("^[0-9a-f]{12}$", hash);
    }

    [Fact]
    public void key_order_does_not_change_the_hash()
    {
        Assert.Equal(
            RunDataProjection.ModelConfigHash("""{"model":{"a":1,"b":{"y":2,"x":3}}}"""),
            RunDataProjection.ModelConfigHash("""{"model":{"b":{"x":3,"y":2},"a":1}}"""));
    }

    [Fact]
    public void a_different_knob_is_a_different_build()
    {
        Assert.NotEqual(
            RunDataProjection.ModelConfigHash("""{"model":{"name":"fern","knobs":{"fern_architecture":"icml_pinned"}}}"""),
            RunDataProjection.ModelConfigHash("""{"model":{"name":"fern","knobs":{"fern_architecture":"current"}}}"""));
    }

    [Fact]
    public void only_the_model_block_enters_the_hash()
    {
        Assert.Equal(
            RunDataProjection.ModelConfigHash("""{"model":{"name":"dlinear"}}"""),
            RunDataProjection.ModelConfigHash("""{"model":{"name":"dlinear"},"runtime":{"device":"cuda"},"data":{"noise_sigma":2.0}}"""));
    }

    static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    static string MigratedLane(DirectoryInfo root, string laneName)
    {
        var lane = root.CreateSubdirectory(laneName);
        var database = Path.Combine(lane.FullName, "fern.db");
        File.WriteAllBytes(database, []);
        ProductionHome.Initialize(database);
        RunArchive.AddTo(database);
        return database;
    }

}

public sealed record RunLifecycleFixtureRequest(string RunHash, string State, RunMetricValues? Metrics, string? Detail);
public sealed record RunLifecycleFixtureReport(string Schema, string RunHash, string State, long RowsChanged);

/// <summary>Test-fixture transition owner retained after the old Diesel helpers left the active Rust crate.</summary>
public static class RunLifecycleFixtureStore
{
    public const string Schema = "fern.run_lifecycle_fixture.v1";
    public static RunLifecycleFixtureReport Apply(string databasePath, RunLifecycleFixtureRequest request)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = request.State switch
        {
            "running" => "UPDATE runs SET status='running',started_at=$now WHERE run_hash=$hash",
            "done" => "UPDATE runs SET success_hash=run_hash,status='done',artifacts=CASE WHEN artifact_dir IS NULL THEN 'none' ELSE artifacts END,finished_at=$now,mse=COALESCE($mse,mse),mae=COALESCE($mae,mae),crps=COALESCE($crps,crps),swd=COALESCE($swd,swd),ept=COALESCE($ept,ept),clean_target_mse=COALESCE($clean,clean_target_mse),injected_target_noise_mse=COALESCE($injected,injected_target_noise_mse),noise_forecast_overlap=COALESCE($overlap,noise_forecast_overlap),baseline_constant_mse=COALESCE($constant,baseline_constant_mse),baseline_context_mean_mse=COALESCE($context,baseline_context_mean_mse),baseline_last_value_mse=COALESCE($last,baseline_last_value_mse) WHERE run_hash=$hash",
            "failed" => "UPDATE runs SET status='failed',finished_at=$now,error_msg=$detail WHERE run_hash=$hash",
            "stale" => "UPDATE runs SET status='stale',error_msg=$detail WHERE run_hash=$hash",
            _ => throw new RunDataFault($"unknown fixture Run state {request.State}"),
        };
        command.Parameters.AddWithValue("$hash", request.RunHash); command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$detail", (object?)request.Detail ?? DBNull.Value);
        var m=request.Metrics; foreach(var (name,value) in new[]{("$mse",m?.Mse),("$mae",m?.Mae),("$crps",m?.Crps),("$swd",m?.Swd),("$ept",m?.Ept),("$clean",m?.CleanTargetMse),("$injected",m?.InjectedTargetNoiseMse),("$overlap",m?.NoiseForecastOverlap),("$constant",m?.BaselineConstantMse),("$context",m?.BaselineContextMeanMse),("$last",m?.BaselineLastValueMse)}) command.Parameters.AddWithValue(name,(object?)value??DBNull.Value);
        var rows=command.ExecuteNonQuery();
        if(rows>0){using var e=connection.CreateCommand();e.Transaction=transaction;e.CommandText="INSERT INTO run_events(run_hash,event,detail_json) VALUES($h,$e,$d)";e.Parameters.AddWithValue("$h",request.RunHash);e.Parameters.AddWithValue("$e",request.State=="done"?"completed":request.State);var preview=(request.Detail??string.Empty);if(preview.Length>500)preview=preview[..500];var detail=request.State switch{"failed"=>JsonSerializer.Serialize(new{error=preview}),"stale"=>JsonSerializer.Serialize(new{reason=preview}),_=>null};e.Parameters.AddWithValue("$d",(object?)detail??DBNull.Value);e.ExecuteNonQuery();}
        transaction.Commit(); return new(Schema,request.RunHash,request.State,rows);
    }
}
