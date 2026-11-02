using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Dapper;

namespace Fern.RunData;

/// <summary>Raised when an explicit per-lane result database cannot provide a read-only projection.</summary>
public sealed class RunDataFault(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Wire settings for existing Fern run-result reports, which retain null fields as observable provenance.</summary>
public static class RunDataJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>
/// One explicit scope for a list operation. Exactly one of <see cref="DatabasePath"/>
/// and <see cref="CampaignRoot"/> is required; the reader never chooses a lane.
/// </summary>
public sealed record RunListRequest(
    string? DatabasePath = null,
    string? CampaignRoot = null,
    string? Dataset = null,
    string? Model = null,
    string? Status = null,
    long? TemplateId = null,
    long? Id = null,
    ulong Limit = 0);

/// <summary>Complete current projection of one <c>runs</c> row. SQLite INTEGER stays <see cref="long"/> and REAL stays <see cref="double"/>.</summary>
public sealed record RunDataRow(
    long Id,
    string? ArtifactDir,
    string Dataset,
    string? DatasetId,
    string ModelType,
    long Seed,
    long PredLen,
    long SeqLen,
    double NoiseSigma,
    long TrajectoryDrawOrdinal,
    long NoiseDrawOrdinal,
    long TrajectorySeedBase,
    long NoiseSeedBase,
    long TrajectoryDrawSeed,
    long NoiseDrawSeed,
    string? ConfigJson,
    string? ConfigHash,
    string? InvocationJson,
    string? RunHash,
    string? SuccessHash,
    string? ConfigName,
    string? RunNote,
    string? GitSha,
    string Status,
    string? StartedAt,
    string? FinishedAt,
    [property: JsonPropertyName("error_msg")] string? ErrorMessage,
    string Source,
    string Artifacts,
    long Pinned,
    double? Mse,
    double? Mae,
    double? Crps,
    double? Swd,
    double? Ept,
    double? CleanTargetMse,
    double? InjectedTargetNoiseMse,
    double? NoiseForecastOverlap,
    double? BaselineConstantMse,
    double? BaselineContextMeanMse,
    double? BaselineLastValueMse,
    long? TemplateId,
    long? NFeatures,
    string? CodeFingerprintJson,
    string? TargetHash);

public sealed record RunListReport(string Schema, IReadOnlyList<RunDataRow> Runs);

public sealed record HistoricalDashboardRunRow(
    long Id, string? ArtifactDir, string Dataset, string ModelType, long Seed,
    long PredLen, long SeqLen, double NoiseSigma, string? RunHash, string Status,
    string? FinishedAt, string Source, double? Mse, double? Mae, double? Crps,
    double? Swd, double? Ept);

public sealed record HistoricalDashboardRunReport(
    string Schema, IReadOnlyList<HistoricalDashboardRunRow> Runs);

public sealed record NextCampaignIdReport(string Schema, long NextCampaignId);

public sealed record RunByHashReport(string Schema, string RequestedRunHash, RunDataRow? Run);

public sealed record RunEventDataRow(long Id, string RunHash, string Ts, string Event, string? DetailJson);

public sealed record RunEventDataReport(string Schema, IReadOnlyList<RunEventDataRow> Events);

public sealed record RunEventWriteReport(string Schema, RunEventDataRow Event);

public sealed record RunPinWriteReport(string Schema, long RunId, long Pinned, long RowsChanged);
public sealed record RunSlimWriteReport(string Schema, long RunId, long FreedBytes, long RowsChanged);
public sealed record RunSlimGroupWriteReport(string Schema, long Kept, long Slimmed, long FreedBytes);

public sealed record RunInvocationWriteReport(string Schema, string RunHash, long RowsChanged);

public sealed record ProtocolBackfillReport(
    string Schema,
    long Rows,
    long RowsFilled,
    long RowsWithoutAProtocolBlock,
    bool DryRun);

public sealed record RunComparisonReport(string Schema, IReadOnlyList<RunComparisonRow> Comparisons);

public sealed record RunComparisonRow(
    string ModelType,
    double NoiseSigma,
    long NSeeds,
    double? AvgMse,
    double? AvgMae,
    double? AvgCrps,
    double? AvgSwd,
    double? AvgEpt);

/// <summary>Report-only storage facts. All mutation counters stay zero because the C# reader cannot apply a cleanup policy.</summary>
public sealed record RunStorageAuditProjection(
    long Checked,
    long Missing,
    long MarkedDbMetricsOnly,
    long MarkedStale,
    long StagedDbRowDeletion,
    long PurgedDbRows);

public sealed record RunStorageAuditReport(
    string Schema, string Policy, bool DryRun, long Checked, long Missing,
    long MarkedDbMetricsOnly, long MarkedStale, long StagedDbRowDeletion, long PurgedDbRows);

public sealed record RunMetricValues(
    double? Mse,
    double? Mae,
    double? Crps,
    double? Swd,
    double? Ept,
    double? CleanTargetMse,
    double? InjectedTargetNoiseMse,
    double? NoiseForecastOverlap,
    double? BaselineConstantMse,
    double? BaselineContextMeanMse,
    double? BaselineLastValueMse);

public sealed record AcceptedTypedRunProjectionReport(string Schema, string RunHash, long RowsChanged);

public sealed record AnalysisDataInput(
    string AnalysisHash, string Schema, string Kind, string QueryJson, string RunHashesJson,
    string ScoringFingerprint, string ParamsJson, string ResultJson);

public sealed record AnalysisDataRow(
    string AnalysisHash, string Schema, string Kind, string QueryJson, string RunHashesJson,
    string ScoringFingerprint, string ParamsJson, string ResultJson, string CreatedAt);

public sealed record AnalysisWriteReport(string Schema, string AnalysisHash, long Inserted);
public sealed record AnalysisReadReport(string Schema, string RequestedAnalysisHash, AnalysisDataRow? Analysis);
public sealed record AnalysisListReport(string Schema, IReadOnlyList<AnalysisDataRow> Analyses);
public sealed record RunDiscardReport(string Schema, string RunHash, long? DbId);

/// <summary>
/// Typed, read-only owner for independent per-lane <c>fern.db</c> run-result rows.
/// It does not open Registry Home, apply migrations, or write database state.
/// </summary>
public static class RunViewsReader
{
    public static string? FindArtifactDirectory(string databasePath, string runHash)
    {
        using var connection = OpenReader(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT artifact_dir FROM runs WHERE run_hash = $run LIMIT 1";
        command.Parameters.AddWithValue("$run", runHash);
        return command.ExecuteScalar() as string;
    }

    public const string ListSchema = "fern.run_list.v1";
    public const string ComparisonSchema = "fern.run_comparison.v1";
    public const string NextCampaignIdSchema = "fern.run_data.next_campaign_id.v1";
    public const string EventListSchema = "fern.run_event_list.v1";
    const int ReaderTimeoutSeconds = 2;

    const string RunColumns = """
        id AS Id, artifact_dir AS ArtifactDir, dataset AS Dataset, dataset_id AS DatasetId, model_type AS ModelType,
        seed AS Seed, pred_len AS PredLen, seq_len AS SeqLen, noise_sigma AS NoiseSigma, trajectory_draw_ordinal AS TrajectoryDrawOrdinal,
        noise_draw_ordinal AS NoiseDrawOrdinal, trajectory_seed_base AS TrajectorySeedBase, noise_seed_base AS NoiseSeedBase,
        trajectory_draw_seed AS TrajectoryDrawSeed, noise_draw_seed AS NoiseDrawSeed, config_json AS ConfigJson, config_hash AS ConfigHash,
        invocation_json AS InvocationJson, run_hash AS RunHash, success_hash AS SuccessHash, config_name AS ConfigName,
        run_note AS RunNote, git_sha AS GitSha, status AS Status, started_at AS StartedAt, finished_at AS FinishedAt,
        error_msg AS ErrorMessage, source AS Source, artifacts AS Artifacts, pinned AS Pinned, mse AS Mse, mae AS Mae, crps AS Crps, swd AS Swd, ept AS Ept,
        clean_target_mse AS CleanTargetMse, injected_target_noise_mse AS InjectedTargetNoiseMse, noise_forecast_overlap AS NoiseForecastOverlap,
        baseline_constant_mse AS BaselineConstantMse, baseline_context_mean_mse AS BaselineContextMeanMse,
        baseline_last_value_mse AS BaselineLastValueMse, template_id AS TemplateId, n_features AS NFeatures,
        code_fingerprint_json AS CodeFingerprintJson, target_hash AS TargetHash
        """;

    public static RunListReport List(RunListRequest request)
    {
        if (request.Id is not null &&
            (request.CampaignRoot is not null || request.Dataset is not null || request.Model is not null ||
             request.TemplateId is not null))
        {
            throw new RunDataFault("an exact run id cannot be combined with campaign, dataset, model, or template scope");
        }
        var paths = ListPaths(request);
        var rows = new List<RunDataRow>();
        foreach (var path in paths)
        {
            using var connection = OpenReader(path);
            rows.AddRange(ListOne(connection, request));
        }
        if (request.Limit > 0 && (ulong)rows.Count > request.Limit)
        {
            rows.RemoveRange(checked((int)request.Limit), rows.Count - checked((int)request.Limit));
        }
        return new RunListReport(ListSchema, rows);
    }

    public static RunDataRow? FindByRunHash(string databasePath, string runHash) =>
        FindOne(databasePath, "run_hash = $value", runHash);

    public static RunDataRow? FindById(string databasePath, long id) =>
        FindOne(databasePath, "id = $value", id);

    public static long NextCampaignId(string databasePath)
    {
        using var connection = OpenReader(databasePath);
        using var command = connection.CreateCommand();
        command.CommandTimeout = ReaderTimeoutSeconds;
        command.CommandText = "SELECT MAX(template_id) FROM runs";
        var scalar = command.ExecuteScalar();
        var highest = scalar is null or DBNull ? 0L : Convert.ToInt64(scalar);
        long next;
        try
        {
            next = checked(highest + 1);
        }
        catch (OverflowException exception)
        {
            throw new RunDataFault($"next campaign id overflows SQLite INTEGER after {highest}: {exception.Message}");
        }
        if (next <= 0)
        {
            throw new RunDataFault($"next campaign id must be positive, got {next} after maximum template_id {highest}");
        }
        return next;
    }

    public static IReadOnlyList<RunComparisonRow> Compare(string databasePath, string? dataset)
    {
        using var connection = OpenReader(databasePath);
        using var command = connection.CreateCommand();
        command.CommandTimeout = ReaderTimeoutSeconds;
        command.CommandText = $"""
            SELECT model_type, noise_sigma, COUNT(*), AVG(mse), AVG(mae), AVG(crps), AVG(swd), AVG(ept)
            FROM runs
            WHERE status = 'done' {((dataset is null) ? string.Empty : "AND dataset = $dataset")}
            GROUP BY model_type, noise_sigma
            ORDER BY model_type, noise_sigma
            """;
        if (dataset is not null)
        {
            command.Parameters.AddWithValue("$dataset", dataset);
        }
        using var reader = Read(command, "compare run rows");
        var rows = new List<RunComparisonRow>();
        while (reader.Read())
        {
            rows.Add(new RunComparisonRow(
                reader.GetString(0), reader.GetDouble(1), reader.GetInt64(2),
                NullableDouble(reader, 3), NullableDouble(reader, 4), NullableDouble(reader, 5),
                NullableDouble(reader, 6), NullableDouble(reader, 7)));
        }
        return rows;
    }

    public static IReadOnlyList<RunDataRow> Pending(string databasePath, long campaignId, bool includeFailed, bool includeRunning)
    {
        using var connection = OpenReader(databasePath);
        using var command = connection.CreateCommand();
        command.CommandTimeout = ReaderTimeoutSeconds;
        var statuses = new List<string> { "pending" };
        if (includeFailed) statuses.Add("failed");
        if (includeRunning) statuses.Add("running");
        var parameters = statuses.Select((_, index) => "$status" + index).ToArray();
        command.CommandText = $"SELECT {RunColumns} FROM runs WHERE template_id = $campaign_id AND status IN ({string.Join(", ", parameters)}) ORDER BY noise_sigma, trajectory_draw_ordinal, noise_draw_ordinal, model_type, seed";
        command.Parameters.AddWithValue("$campaign_id", campaignId);
        for (var index = 0; index < statuses.Count; index++)
        {
            command.Parameters.AddWithValue(parameters[index], statuses[index]);
        }
        return ReadRows(command, "list pending run rows");
    }

    public static IReadOnlyList<RunDataRow> Usable(string databasePath, long? templateId)
    {
        using var connection = OpenReader(databasePath);
        using var command = connection.CreateCommand();
        command.CommandTimeout = ReaderTimeoutSeconds;
        command.CommandText = $"SELECT {RunColumns} FROM runs WHERE status = 'done' AND success_hash IS NOT NULL AND success_hash = run_hash {(templateId is null ? string.Empty : "AND template_id = $template_id")} ORDER BY dataset, model_type, noise_sigma, seed";
        if (templateId is not null)
        {
            command.Parameters.AddWithValue("$template_id", templateId.Value);
        }
        return ReadRows(command, "list usable run rows");
    }

    public static IReadOnlyList<RunDataRow> StatisticsRuns(string databasePath, long campaignId)
    {
        using var connection = OpenReader(databasePath);
        using var command = connection.CreateCommand();
        command.CommandTimeout = ReaderTimeoutSeconds;
        command.CommandText = $"SELECT {RunColumns} FROM runs WHERE status = 'done' AND run_hash IS NOT NULL AND template_id = $campaign_id ORDER BY dataset, model_type, pred_len, noise_sigma, seed";
        command.Parameters.AddWithValue("$campaign_id", campaignId);
        return ReadRows(command, "list statistics input run rows");
    }

    public static IReadOnlyList<RunEventDataRow> WorkerExitedEvents(string databasePath, IReadOnlyList<string> runHashes)
    {
        if (runHashes.Count == 0) return [];
        using var connection = OpenReader(databasePath);
        using var command = connection.CreateCommand();
        command.CommandTimeout = ReaderTimeoutSeconds;
        var parameters = runHashes.Select((_, index) => "$run_hash" + index).ToArray();
        command.CommandText = $"SELECT id, run_hash, ts, event, detail_json FROM run_events WHERE event = 'worker_exited' AND run_hash IN ({string.Join(", ", parameters)}) ORDER BY id";
        for (var index = 0; index < runHashes.Count; index++)
        {
            command.Parameters.AddWithValue(parameters[index], runHashes[index]);
        }
        using var reader = Read(command, "list worker-exited event rows");
        var rows = new List<RunEventDataRow>();
        while (reader.Read())
        {
            rows.Add(new RunEventDataRow(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                NullableString(reader, 4)));
        }
        return rows;
    }

    public static RunStorageAuditProjection ProjectStorageAudit(string databasePath)
    {
        using var connection = OpenReader(databasePath);
        using var command = connection.CreateCommand();
        command.CommandTimeout = ReaderTimeoutSeconds;
        command.CommandText = "SELECT artifact_dir FROM runs WHERE artifact_dir IS NOT NULL ORDER BY id";
        using var reader = Read(command, "project run artifact storage");
        long checkedCount = 0;
        long missing = 0;
        while (reader.Read())
        {
            checkedCount++;
            var artifactPath = reader.GetString(0);
            if (!Directory.Exists(artifactPath) && !File.Exists(artifactPath))
            {
                missing++;
            }
        }
        return new RunStorageAuditProjection(checkedCount, missing, 0, 0, 0, 0);
    }

    static IReadOnlyList<string> ListPaths(RunListRequest request)
    {
        if ((request.DatabasePath is null) == (request.CampaignRoot is null))
        {
            throw new RunDataFault("list-runs needs exactly one of database_path or campaign_root");
        }
        if (request.DatabasePath is not null)
        {
            RequireRegularDatabase(request.DatabasePath);
            return [request.DatabasePath];
        }
        try
        {
            var paths = Directory.EnumerateDirectories(request.CampaignRoot!)
                .Select(directory => Path.Combine(directory, "fern.db"))
                .Where(File.Exists)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (paths.Length == 0)
            {
                throw new RunDataFault($"no lane registry (<lane>/fern.db) under campaign root {request.CampaignRoot}");
            }
            foreach (var path in paths)
            {
                RequireRegularDatabase(path);
            }
            return paths;
        }
        catch (Exception exception) when (exception is not RunDataFault)
        {
            throw new RunDataFault($"cannot read campaign root {request.CampaignRoot}", exception);
        }
    }

    static RunDataRow? FindOne(string databasePath, string predicate, object value)
    {
        using var connection = OpenReader(databasePath);
        using var command = connection.CreateCommand();
        command.CommandTimeout = ReaderTimeoutSeconds;
        command.CommandText = $"SELECT {RunColumns} FROM runs WHERE {predicate}";
        command.Parameters.AddWithValue("$value", value);
        using var reader = Read(command, "find run row");
        if (!reader.Read()) return null;
        var row = ReadRunRow(reader);
        if (reader.Read()) throw new RunDataFault("multiple run rows match a unique run identity");
        return row;
    }

    static IReadOnlyList<RunDataRow> ListOne(SqliteConnection connection, RunListRequest request)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = ReaderTimeoutSeconds;
        if (request.Id is not null)
        {
            command.CommandText = $"SELECT {RunColumns} FROM runs WHERE id = $id";
            command.Parameters.AddWithValue("$id", request.Id.Value);
            return ReadRows(command, "find run row by id");
        }
        command.CommandText = $"SELECT {RunColumns} FROM runs WHERE status = $status {(request.Dataset is null ? string.Empty : "AND dataset = $dataset")} {(request.Model is null ? string.Empty : "AND model_type = $model")} {(request.TemplateId is null ? string.Empty : "AND template_id = $template_id")} ORDER BY dataset, model_type, noise_sigma, seed";
        command.Parameters.AddWithValue("$status", string.IsNullOrEmpty(request.Status) ? "done" : request.Status);
        if (request.Dataset is not null) command.Parameters.AddWithValue("$dataset", request.Dataset);
        if (request.Model is not null) command.Parameters.AddWithValue("$model", request.Model);
        if (request.TemplateId is not null) command.Parameters.AddWithValue("$template_id", request.TemplateId.Value);
        return ReadRows(command, "list run rows");
    }

    static IReadOnlyList<RunDataRow> ReadRows(SqliteCommand command, string action)
    {
        using var reader = Read(command, action);
        var rows = new List<RunDataRow>();
        while (reader.Read()) rows.Add(ReadRunRow(reader));
        return rows;
    }

    static SqliteDataReader Read(SqliteCommand command, string action)
    {
        try
        {
            return command.ExecuteReader();
        }
        catch (SqliteException exception)
        {
            throw new RunDataFault($"cannot {action}", exception);
        }
    }

    static SqliteConnection OpenReader(string path)
    {
        try
        {
            return OpenReadOnlyDatabase(path);
        }
        catch (Exception exception) when (exception is not RunDataFault)
        {
            throw new RunDataFault($"cannot open per-lane run database {path} read-only", exception);
        }
    }

    internal static SqliteConnection OpenReadOnlyDatabase(string path)
    {
        // CHG: share the run reader's path refusal; SQLite failures remain database faults.
        RequireRegularDatabase(path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = ReaderTimeoutSeconds,
        }.ToString());
        try
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA query_only = ON";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    static void RequireRegularDatabase(string path)
    {
        if (!File.Exists(path))
        {
            throw new RunDataFault($"run database {path} is not a regular file");
        }
    }

    // Dapper maps the immutable constructor. Fern additionally checks nullability
    // and SQLite storage types, which a mapper may otherwise default or coerce.
    // The record remains the single field contract; no global Dapper settings.
    static readonly FrozenDictionary<string, (Type Type, bool Nullable)> RunFields =
        typeof(RunDataRow).GetProperties().ToFrozenDictionary(property => property.Name, property =>
            (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType,
             new System.Reflection.NullabilityInfoContext().Create(property).ReadState == System.Reflection.NullabilityState.Nullable), StringComparer.Ordinal);

    static RunDataRow ReadRunRow(SqliteDataReader reader)
    {
        if (reader.FieldCount != RunFields.Count) throw new RunDataFault("run row has an unexpected column count");
        for (var index = 0; index < reader.FieldCount; index++)
        {
            var name = reader.GetName(index);
            if (!RunFields.TryGetValue(name, out var field)) throw new RunDataFault($"unexpected run column {name}");
            var value = reader.GetValue(index);
            if (value is DBNull ? !field.Nullable : value.GetType() != field.Type)
                throw new RunDataFault($"run column {name} requires {(field.Nullable ? "nullable " : "")}{field.Type.Name}; observed {value.GetType().Name}");
        }
        try { return reader.GetRowParser<RunDataRow>()(reader); }
        catch (Exception error) when (error is InvalidOperationException or System.Data.DataException)
        {
            throw new RunDataFault("cannot decode the declared run row", error);
        }
    }

    static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    static double? NullableDouble(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    static long? NullableLong(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}

/// <summary>Append-only external observations for one explicit per-lane Run database.</summary>
public static class RunEventStore
{
    public const string WriteSchema = "fern.run_event_write.v1";
    const int CommandTimeoutSeconds = 2;

    public static RunEventWriteReport Record(
        string databasePath,
        string runHash,
        string eventName,
        string? detailJson)
    {
        if (!File.Exists(databasePath))
        {
            throw new RunDataFault($"run database {databasePath} is not a regular file");
        }
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(databasePath),
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = CommandTimeoutSeconds,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText =
                "INSERT INTO run_events (run_hash, event, detail_json) VALUES ($run_hash, $event, $detail) RETURNING id, run_hash, ts, event, detail_json";
            command.Parameters.AddWithValue("$run_hash", runHash);
            command.Parameters.AddWithValue("$event", eventName);
            command.Parameters.AddWithValue("$detail", (object?)detailJson ?? DBNull.Value);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new RunDataFault("record Run event returned no durable row");
            var row = new RunEventDataRow(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4));
            return new RunEventWriteReport(WriteSchema, row);
        }
        catch (Exception exception) when (exception is not RunDataFault)
        {
            throw new RunDataFault($"cannot record Run event {eventName} for {runHash}", exception);
        }
    }

    public static RunEventDataReport List(string databasePath, string runHash)
    {
        if (!File.Exists(databasePath))
        {
            throw new RunDataFault($"run database {databasePath} is not a regular file");
        }
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(databasePath),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = CommandTimeoutSeconds,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText =
                "SELECT id, run_hash, ts, event, detail_json FROM run_events WHERE run_hash = $run_hash ORDER BY id";
            command.Parameters.AddWithValue("$run_hash", runHash);
            using var reader = command.ExecuteReader();
            var rows = new List<RunEventDataRow>();
            while (reader.Read())
            {
                rows.Add(new RunEventDataRow(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
            return new RunEventDataReport(RunViewsReader.EventListSchema, rows);
        }
        catch (Exception exception) when (exception is not RunDataFault)
        {
            throw new RunDataFault($"cannot list Run events for {runHash}", exception);
        }
    }
}

/// <summary>Stable dashboard projection for archived lane databases; this reader never migrates its input.</summary>
public static class HistoricalDashboardReader
{
    public const string Schema = "fern.historical_dashboard_runs.v1";

    public static HistoricalDashboardRunReport List(string databasePath, string status, string? dataset)
    {
        try
        {
            using var connection = RunViewsReader.OpenReadOnlyDatabase(databasePath);
            var columns = new HashSet<string>(StringComparer.Ordinal);
            using (var tableInfo = connection.CreateCommand())
            {
                tableInfo.CommandText = "SELECT name FROM pragma_table_info('runs')";
                using var names = tableInfo.ExecuteReader();
                while (names.Read()) columns.Add(names.GetString(0));
            }
            var runHashExpression = columns.Contains("run_hash") ? "run_hash"
                : columns.Contains("commission_hash") ? "commission_hash AS run_hash"
                : throw new RunDataFault("runs table has neither run_hash nor its pre-migration name commission_hash");
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT id, artifact_dir, dataset, model_type, seed, pred_len, seq_len,
                       noise_sigma, {runHashExpression}, status, finished_at, source,
                       mse, mae, crps, swd, ept
                FROM runs
                WHERE status = $status AND ($dataset IS NULL OR dataset = $dataset)
                ORDER BY id DESC
                """;
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$dataset", (object?)dataset ?? DBNull.Value);
            using var reader = command.ExecuteReader();
            var rows = new List<HistoricalDashboardRunRow>();
            while (reader.Read())
            {
                rows.Add(new(
                    reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
                    reader.GetDouble(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetDouble(12), reader.IsDBNull(13) ? null : reader.GetDouble(13),
                    reader.IsDBNull(14) ? null : reader.GetDouble(14), reader.IsDBNull(15) ? null : reader.GetDouble(15),
                    reader.IsDBNull(16) ? null : reader.GetDouble(16)));
            }
            return new(Schema, rows);
        }
        catch (Exception exception) when (exception is not RunDataFault)
        {
            throw new RunDataFault($"cannot read historical dashboard rows from {databasePath}", exception);
        }
    }
}

public static class RunPinStore
{
    public const string WriteSchema = "fern.run_pin_write.v1";
    const int CommandTimeoutSeconds = 2;

    public static RunPinWriteReport Set(string databasePath, long runId, bool pinned)
    {
        if (!File.Exists(databasePath))
        {
            throw new RunDataFault($"run database {databasePath} is not a regular file");
        }
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(databasePath),
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = CommandTimeoutSeconds,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText = "UPDATE runs SET pinned = $pinned WHERE id = $run_id";
            command.Parameters.AddWithValue("$pinned", pinned ? 1L : 0L);
            command.Parameters.AddWithValue("$run_id", runId);
            var rowsChanged = command.ExecuteNonQuery();
            return new RunPinWriteReport(WriteSchema, runId, pinned ? 1L : 0L, rowsChanged);
        }
        catch (Exception exception) when (exception is not RunDataFault)
        {
            throw new RunDataFault($"cannot {(pinned ? "protect" : "unprotect")} Run {runId}", exception);
        }
    }
}

public static class RunSlimStore
{
    public const string WriteSchema = "fern.run_slim.v1";
    public const string GroupWriteSchema = "fern.run_group_slimming.v1";
    static readonly string[] HeavyFiles =
    [
        "model.safetensors", "sequence_tensors.safetensors", "raw_tensors.safetensors",
        "samples.ipc", "hist.ipc", "fern.json",
    ];

    public static RunSlimWriteReport Slim(string databasePath, long runId)
    {
        if (!File.Exists(databasePath)) throw new RunDataFault($"run database {databasePath} is not a regular file");
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 2 }.ToString());
            connection.Open();
            string? artifactDirectory;
            string artifacts;
            long pinned;
            using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT artifact_dir, artifacts, pinned FROM runs WHERE id = $id";
                select.Parameters.AddWithValue("$id", runId);
                using var reader = select.ExecuteReader();
                if (!reader.Read()) throw new RunDataFault($"Run {runId} does not exist");
                artifactDirectory = reader.IsDBNull(0) ? null : reader.GetString(0);
                artifacts = reader.GetString(1);
                pinned = reader.GetInt64(2);
            }
            if (artifacts == "slim" || pinned != 0 || artifactDirectory is null)
                return new RunSlimWriteReport(WriteSchema, runId, 0, 0);

            // CHG 2026-09-10: claim the run before deleting a byte of it, with a
            // conditional UPDATE whose WHERE repeats the two conditions read
            // above. The read alone was not a guard: `pinned` is what
            // `fern-run protect-run` writes, and a sweep that read `pinned = 0`
            // seconds earlier went on to delete the weights of a run somebody
            // had just protected — a protection switch failing silently at the
            // one moment it is meant to matter, leaving `artifacts = 'slim'`
            // beside `pinned = 1` with no record of which won. Affected rows
            // decides: zero means someone else changed the run first, and this
            // sweep stops without touching it.
            //
            // Marking before deleting also chooses the safe failure direction.
            // A crash between the two leaves a row saying slim while the files
            // are still there — over-reported, and recoverable. The other order
            // loses bytes while the row still promises them.
            using (var claim = connection.CreateCommand())
            {
                claim.CommandText =
                    "UPDATE runs SET artifacts = 'slim' WHERE id = $id AND pinned = 0 AND artifacts <> 'slim'";
                claim.Parameters.AddWithValue("$id", runId);
                if (claim.ExecuteNonQuery() != 1)
                {
                    return new RunSlimWriteReport(WriteSchema, runId, 0, 0);
                }
            }

            long freed = 0;
            foreach (var name in HeavyFiles)
            {
                var file = Path.Combine(artifactDirectory, name);
                try
                {
                    var length = new FileInfo(file).Length;
                    freed = checked(freed + length);
                    try { File.Delete(file); } catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            // The row was marked by the claim above; one row changed.
            return new RunSlimWriteReport(WriteSchema, runId, freed, 1);
        }
        catch (Exception exception) when (exception is not RunDataFault) { throw new RunDataFault($"cannot slim Run {runId}", exception); }
    }

    public static RunSlimGroupWriteReport SlimByGroup(string databasePath, ulong keepBest)
    {
        var candidates = RunViewsReader.List(new RunListRequest(DatabasePath: databasePath, Status: "done"))
            .Runs
            .Where(run => run.Artifacts == "full" && run.Pinned == 0)
            .GroupBy(run => (run.Dataset, run.ModelType, NoiseSigmaBits: BitConverter.DoubleToInt64Bits(run.NoiseSigma)))
            .Select(group => group
                .OrderBy(run => run.Mse is { } mse && double.IsFinite(mse) ? mse : double.PositiveInfinity)
                .ToArray())
            .ToArray();
        long kept = 0;
        long slimmed = 0;
        long freedBytes = 0;
        foreach (var group in candidates)
        {
            var keepCount = keepBest >= (ulong)group.Length ? group.Length : checked((int)keepBest);
            kept = checked(kept + keepCount);
            foreach (var run in group.Skip(keepCount))
            {
                var report = Slim(databasePath, run.Id);
                freedBytes = checked(freedBytes + report.FreedBytes);
                slimmed = checked(slimmed + 1);
            }
        }
        return new RunSlimGroupWriteReport(GroupWriteSchema, kept, slimmed, freedBytes);
    }
}

public static class RunStorageAuditStore
{
    public const string ReportSchema = "fern.run_storage_audit.v1";

    public static RunStorageAuditReport Audit(string databasePath, string policy, bool dryRun, bool markUnfinishedStale)
    {
        if (policy is not ("report_only" or "mark_db_metrics_only" or "stage_db_row_deletion" or "purge_db_rows"))
            throw new RunDataFault($"unknown Run storage audit policy {policy}");
        if (!File.Exists(databasePath)) throw new RunDataFault($"run database {databasePath} is not a regular file");
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.GetFullPath(databasePath), Mode = dryRun || policy == "report_only" ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 2 }.ToString());
            connection.Open();
            var missing = new List<(long Id, string Status)>();
            long checkedCount = 0;
            using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT id, status, artifact_dir FROM runs WHERE artifact_dir IS NOT NULL ORDER BY id";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    checkedCount++;
                    if (!Directory.Exists(reader.GetString(2)) && !File.Exists(reader.GetString(2)))
                        missing.Add((reader.GetInt64(0), reader.GetString(1)));
                }
            }
            long metrics = 0, stale = 0, staged = 0, purged = 0;
            if (!dryRun && policy != "report_only" && missing.Count > 0)
            {
                using var transaction = connection.BeginTransaction();
                foreach (var item in missing)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.Parameters.AddWithValue("$id", item.Id);
                    if (policy == "purge_db_rows") command.CommandText = "DELETE FROM runs WHERE id = $id";
                    else if (policy == "stage_db_row_deletion") command.CommandText = "UPDATE runs SET artifact_dir=NULL, artifacts='none', status='stale', error_msg='artifact_dir missing; staged for deletion' WHERE id=$id";
                    else command.CommandText = "UPDATE runs SET artifact_dir=NULL, artifacts='none' WHERE id=$id";
                    var changed = command.ExecuteNonQuery();
                    if (policy == "purge_db_rows") purged += changed;
                    else { metrics += changed; if (policy == "stage_db_row_deletion") { stale += changed; staged += changed; } }
                    if (policy == "mark_db_metrics_only" && markUnfinishedStale && item.Status is "pending" or "running")
                    {
                        using var mark = connection.CreateCommand();
                        mark.Transaction = transaction;
                        mark.CommandText = "UPDATE runs SET status='stale', error_msg='artifact_dir missing during cleanup' WHERE id=$id";
                        mark.Parameters.AddWithValue("$id", item.Id);
                        stale += mark.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }
            return new(ReportSchema, policy, dryRun, checkedCount, missing.Count, metrics, stale, staged, purged);
        }
        catch (Exception exception) when (exception is not RunDataFault) { throw new RunDataFault($"cannot audit Run artifact storage in {databasePath}", exception); }
    }
}

public static class AcceptedTypedRunProjectionStore
{
    public const string WriteSchema = "fern.accepted_typed_run_projection.v1";
    const int CommandTimeoutSeconds = 2;

    public static AcceptedTypedRunProjectionReport Project(
        string databasePath,
        string runHash,
        RunMetricValues metrics,
        string coordinateId,
        string attemptId)
    {
        if (!File.Exists(databasePath))
            throw new RunDataFault($"run database {databasePath} is not a regular file");
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(databasePath),
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = CommandTimeoutSeconds,
            }.ToString());
            connection.Open();
            using var transaction = connection.BeginTransaction();
            string status;
            string? successHash;
            string? artifactDirectory;
            RunMetricValues currentMetrics;
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandTimeout = CommandTimeoutSeconds;
                select.CommandText = """
                    SELECT status, success_hash, mse, mae, crps, swd, ept,
                           clean_target_mse, injected_target_noise_mse, noise_forecast_overlap,
                           baseline_constant_mse, baseline_context_mean_mse, baseline_last_value_mse,
                           artifact_dir
                    FROM runs WHERE run_hash = $run_hash
                    """;
                select.Parameters.AddWithValue("$run_hash", runHash);
                using var reader = select.ExecuteReader();
                if (!reader.Read()) throw new RunDataFault($"Run {runHash} does not exist");
                status = reader.GetString(0);
                successHash = NullableString(reader, 1);
                currentMetrics = new(
                    NullableDouble(reader, 2), NullableDouble(reader, 3), NullableDouble(reader, 4),
                    NullableDouble(reader, 5), NullableDouble(reader, 6), NullableDouble(reader, 7),
                    NullableDouble(reader, 8), NullableDouble(reader, 9), NullableDouble(reader, 10),
                    NullableDouble(reader, 11), NullableDouble(reader, 12));
                artifactDirectory = NullableString(reader, 13);
            }
            if (status == "done")
            {
                if (successHash == runHash && currentMetrics == metrics)
                    return new(WriteSchema, runHash, 0);
                throw new RunDataFault($"Run {runHash} already has a different terminal result");
            }
            if (status is not ("running" or "pending"))
                throw new RunDataFault($"Run {runHash} in state {status} cannot accept a typed result projection");

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandTimeout = CommandTimeoutSeconds;
                update.CommandText = """
                    UPDATE runs SET
                        success_hash = $run_hash,
                        status = 'done',
                        artifacts = $artifacts,
                        finished_at = $finished_at,
                        mse = COALESCE($mse, mse),
                        mae = COALESCE($mae, mae),
                        crps = COALESCE($crps, crps),
                        swd = COALESCE($swd, swd),
                        ept = COALESCE($ept, ept),
                        clean_target_mse = COALESCE($clean_target_mse, clean_target_mse),
                        injected_target_noise_mse = COALESCE($injected_target_noise_mse, injected_target_noise_mse),
                        noise_forecast_overlap = COALESCE($noise_forecast_overlap, noise_forecast_overlap),
                        baseline_constant_mse = COALESCE($baseline_constant_mse, baseline_constant_mse),
                        baseline_context_mean_mse = COALESCE($baseline_context_mean_mse, baseline_context_mean_mse),
                        baseline_last_value_mse = COALESCE($baseline_last_value_mse, baseline_last_value_mse)
                    WHERE run_hash = $run_hash
                    """;
                update.Parameters.AddWithValue("$run_hash", runHash);
                update.Parameters.AddWithValue("$artifacts", ArtifactLabel(artifactDirectory));
                update.Parameters.AddWithValue("$finished_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                AddNullable(update, "$mse", metrics.Mse);
                AddNullable(update, "$mae", metrics.Mae);
                AddNullable(update, "$crps", metrics.Crps);
                AddNullable(update, "$swd", metrics.Swd);
                AddNullable(update, "$ept", metrics.Ept);
                AddNullable(update, "$clean_target_mse", metrics.CleanTargetMse);
                AddNullable(update, "$injected_target_noise_mse", metrics.InjectedTargetNoiseMse);
                AddNullable(update, "$noise_forecast_overlap", metrics.NoiseForecastOverlap);
                AddNullable(update, "$baseline_constant_mse", metrics.BaselineConstantMse);
                AddNullable(update, "$baseline_context_mean_mse", metrics.BaselineContextMeanMse);
                AddNullable(update, "$baseline_last_value_mse", metrics.BaselineLastValueMse);
                if (update.ExecuteNonQuery() != 1) throw new RunDataFault($"Run {runHash} disappeared during projection");
            }
            InsertEvent(connection, transaction, runHash, "completed", null);
            var detail = JsonSerializer.Serialize(
                new ProjectionDetail("fern.v3_legacy_projection.v1", coordinateId, attemptId, runHash),
                RunDataJson.Options);
            InsertEvent(connection, transaction, runHash, "v3_accepted_projection", detail);
            transaction.Commit();
            return new(WriteSchema, runHash, 1);
        }
        catch (Exception exception) when (exception is not RunDataFault)
        {
            throw new RunDataFault($"cannot project accepted typed result for Run {runHash}", exception);
        }
    }

    static void InsertEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runHash,
        string eventName,
        string? detailJson)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText =
            "INSERT INTO run_events (run_hash, event, detail_json) VALUES ($run_hash, $event, $detail)";
        command.Parameters.AddWithValue("$run_hash", runHash);
        command.Parameters.AddWithValue("$event", eventName);
        command.Parameters.AddWithValue("$detail", (object?)detailJson ?? DBNull.Value);
        if (command.ExecuteNonQuery() != 1) throw new RunDataFault($"Run {runHash} event {eventName} was not recorded");
    }

    static string ArtifactLabel(string? artifactDirectory)
    {
        if (artifactDirectory is null || !Directory.Exists(artifactDirectory)) return "none";
        if (File.Exists(Path.Combine(artifactDirectory, "model.safetensors")) &&
            File.Exists(Path.Combine(artifactDirectory, "sequence_tensors.safetensors"))) return "full";
        return File.Exists(Path.Combine(artifactDirectory, "metrics.ipc")) ? "slim" : "none";
    }

    static void AddNullable(SqliteCommand command, string name, double? value) =>
        command.Parameters.AddWithValue(name, value.HasValue ? value.Value : DBNull.Value);

    static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    static double? NullableDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    sealed record ProjectionDetail(string Schema, string CoordinateId, string AttemptId, string RunHash);
}

public static class AnalysisDataStore
{
    public const string WriteSchema = "fern.analysis_write.v1";
    public const string ReadSchema = "fern.analysis_read.v1";
    public const string ListSchema = "fern.analysis_list.v1";

    public static AnalysisWriteReport Record(string databasePath, AnalysisDataInput analysis)
    {
        using var connection = OpenWriter(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO analyses (
                analysis_hash, schema, kind, query_json, run_hashes_json,
                scoring_fingerprint, params_json, result_json)
            VALUES ($analysis_hash, $schema, $kind, $query_json, $run_hashes_json,
                $scoring_fingerprint, $params_json, $result_json)
            """;
        Add(command, analysis);
        return new(WriteSchema, analysis.AnalysisHash, command.ExecuteNonQuery());
    }

    public static AnalysisReadReport Find(string databasePath, string analysisHash)
    {
        using var connection = RunViewsReader.OpenReadOnlyDatabase(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT analysis_hash, schema, kind, query_json, run_hashes_json,
                   scoring_fingerprint, params_json, result_json, created_at
            FROM analyses WHERE analysis_hash = $analysis_hash
            """;
        command.Parameters.AddWithValue("$analysis_hash", analysisHash);
        using var reader = command.ExecuteReader();
        var row = reader.Read() ? Read(reader) : null;
        return new(ReadSchema, analysisHash, row);
    }

    public static AnalysisListReport List(string databasePath, string? kindFilter, string? queryContains)
    {
        using var connection = RunViewsReader.OpenReadOnlyDatabase(databasePath);
        using var command = connection.CreateCommand();
        var predicates = new List<string>();
        if (kindFilter is not null)
        {
            predicates.Add("kind = $kind");
            command.Parameters.AddWithValue("$kind", kindFilter);
        }
        if (queryContains is not null)
        {
            // Preserve the retired Diesel LIKE semantics: caller-provided % and _
            // remain wildcard characters rather than becoming an exact filter.
            predicates.Add("query_json LIKE $query_contains");
            command.Parameters.AddWithValue("$query_contains", $"%{queryContains}%");
        }
        command.CommandText = """
            SELECT analysis_hash, schema, kind, query_json, run_hashes_json,
                   scoring_fingerprint, params_json, result_json, created_at
            FROM analyses
            """ + (predicates.Count == 0 ? "" : " WHERE " + string.Join(" AND ", predicates))
            + " ORDER BY created_at DESC, analysis_hash ASC";
        using var reader = command.ExecuteReader();
        var rows = new List<AnalysisDataRow>();
        while (reader.Read()) rows.Add(Read(reader));
        return new(ListSchema, rows);
    }

    static AnalysisDataRow Read(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8));

    static SqliteConnection OpenWriter(string databasePath)
    {
        if (!File.Exists(databasePath)) throw new RunDataFault($"run database {databasePath} is not a regular file");
        try
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 2 }.ToString());
            connection.Open();
            return connection;
        }
        catch (Exception exception) { throw new RunDataFault($"cannot open analysis store {databasePath}", exception); }
    }

    static void Add(SqliteCommand command, AnalysisDataInput analysis)
    {
        command.Parameters.AddWithValue("$analysis_hash", analysis.AnalysisHash);
        command.Parameters.AddWithValue("$schema", analysis.Schema);
        command.Parameters.AddWithValue("$kind", analysis.Kind);
        command.Parameters.AddWithValue("$query_json", analysis.QueryJson);
        command.Parameters.AddWithValue("$run_hashes_json", analysis.RunHashesJson);
        command.Parameters.AddWithValue("$scoring_fingerprint", analysis.ScoringFingerprint);
        command.Parameters.AddWithValue("$params_json", analysis.ParamsJson);
        command.Parameters.AddWithValue("$result_json", analysis.ResultJson);
    }
}

public static class RunDiscardStore
{
    public const string ReportSchema = "fern.run_discard_data.v1";

    /// <summary>Whether the database in front of us carries this table at all; the readers open archives and Homes alike.</summary>
    static bool HasTable(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    public static RunDiscardReport Discard(string databasePath, string runHash, IReadOnlyList<string> allowedRoots)
    {
        if (!File.Exists(databasePath)) throw new RunDataFault($"run database {databasePath} is not a regular file");
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.GetFullPath(databasePath), Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 2 }.ToString());
            connection.Open();
            long databaseId;
            string? artifactDirectory;
            using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT id, artifact_dir FROM runs WHERE run_hash = $run_hash";
                select.Parameters.AddWithValue("$run_hash", runHash);
                using var reader = select.ExecuteReader();
                if (!reader.Read()) return new(ReportSchema, runHash, null);
                databaseId = reader.GetInt64(0);
                artifactDirectory = reader.IsDBNull(1) ? null : reader.GetString(1);
            }

            // CHG 2026-09-10: refuse a run that is somebody's accepted result.
            // This needs no concurrency to be wrong: one operator, one mistyped
            // hash, an idle machine, and the artifacts are gone while
            // accepted_results still names them. The database cannot stop it
            // either — accepted_results.run_hash is NOT NULL UNIQUE and, unlike
            // its two sibling columns, carries no REFERENCES back to runs
            // (Schema.sql), so foreign keys would not fire even when enabled.
            // Withdraw the acceptance first; that is a decision with a record,
            // and this is not.
            // CHG 2026-09-12: ask the database in front of us how it records
            // an acceptance. A campaign archive says it in `accepted_results`
            // (the shape the 2026-09-10 refusal was written for, and the only
            // shape an old file has); a database carrying the one-store tables
            // says it as `cells.accepted_attempt_id` with the run on that
            // attempt's artifact snapshot. Either way the answer is a name, and
            // a run nobody accepted is discardable. The check the database
            // cannot answer is not silently skipped: a database with neither
            // shape holds no acceptance to contradict.
            if (HasTable(connection, "accepted_results"))
            {
                using var accepted = connection.CreateCommand();
                accepted.CommandText = "SELECT coordinate_id FROM accepted_results WHERE run_hash = $run_hash";
                accepted.Parameters.AddWithValue("$run_hash", runHash);
                if (accepted.ExecuteScalar() is string coordinate)
                {
                    throw new RunDataFault(
                        $"refusing to discard Run {runHash}: it is the accepted result for coordinate "
                        + $"{coordinate}; withdraw the acceptance before discarding its bytes");
                }
            }
            if (HasTable(connection, "cells") && HasTable(connection, "artifact_snapshots"))
            {
                using var accepted = connection.CreateCommand();
                accepted.CommandText =
                    "SELECT c.name FROM artifact_snapshots s JOIN cells c ON c.accepted_attempt_id = s.attempt_id "
                    + "WHERE s.run_hash = $run_hash";
                accepted.Parameters.AddWithValue("$run_hash", runHash);
                if (accepted.ExecuteScalar() is string cell)
                {
                    throw new RunDataFault(
                        $"refusing to discard Run {runHash}: it is the accepted result for cell "
                        + $"{cell}; withdraw the acceptance before discarding its bytes");
                }
            }
            if (artifactDirectory is not null && (Directory.Exists(artifactDirectory) || File.Exists(artifactDirectory)))
            {
                var canonicalArtifact = CanonicalExistingPath(artifactDirectory);
                var canonicalRoots = allowedRoots.Where(root => Directory.Exists(root) || File.Exists(root))
                    .Select(CanonicalExistingPath).ToArray();
                if (!canonicalRoots.Any(root => IsAtOrUnder(canonicalArtifact, root)))
                    throw new RunDataFault($"refusing to discard artifact_dir outside allowed roots: {artifactDirectory}");
                if (Directory.Exists(artifactDirectory)) Directory.Delete(artifactDirectory, recursive: true);
                else File.Delete(artifactDirectory);
                PruneEmptyDirectories(Path.GetDirectoryName(artifactDirectory), canonicalRoots);
            }
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM runs WHERE id = $id";
            delete.Parameters.AddWithValue("$id", databaseId);
            _ = delete.ExecuteNonQuery();
            return new(ReportSchema, runHash, databaseId);
        }
        catch (Exception exception) when (exception is not RunDataFault) { throw new RunDataFault($"cannot discard Run {runHash}", exception); }
    }

    static string CanonicalExistingPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new RunDataFault($"path {path} has no root");
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null) current = target.FullName;
        }
        return Path.GetFullPath(current);
    }

    static bool IsAtOrUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(path, root, comparison)
            || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison);
    }

    static void PruneEmptyDirectories(string? start, IReadOnlyList<string> stopRoots)
    {
        var current = start;
        while (current is not null && Directory.Exists(current))
        {
            var canonical = CanonicalExistingPath(current);
            if (!stopRoots.Any(root => IsAtOrUnder(canonical, root))) return;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (stopRoots.Any(root => string.Equals(canonical, root, comparison))) return;
            try { Directory.Delete(current); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            current = Path.GetDirectoryName(current);
        }
    }
}

public static class RunInvocationStore
{
    public const string WriteSchema = "fern.run_invocation_write.v1";
    const int CommandTimeoutSeconds = 2;

    public static RunInvocationWriteReport Record(
        string databasePath,
        string runHash,
        string invocationJson,
        string? configName)
    {
        if (!File.Exists(databasePath))
        {
            throw new RunDataFault($"run database {databasePath} is not a regular file");
        }
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(databasePath),
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = CommandTimeoutSeconds,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText = """
                UPDATE runs
                SET invocation_json = $invocation_json,
                    config_name = COALESCE($config_name, config_name)
                WHERE run_hash = $run_hash
                """;
            command.Parameters.AddWithValue("$invocation_json", invocationJson);
            command.Parameters.AddWithValue("$config_name", (object?)configName ?? DBNull.Value);
            command.Parameters.AddWithValue("$run_hash", runHash);
            var rowsChanged = command.ExecuteNonQuery();
            return new RunInvocationWriteReport(WriteSchema, runHash, rowsChanged);
        }
        catch (Exception exception) when (exception is not RunDataFault)
        {
            throw new RunDataFault($"cannot record invocation for Run {runHash}", exception);
        }
    }
}

/// <summary>Backfill the selectable protocol projection of every existing Run from its own invocation and receipts.</summary>
public static class RunProtocolBackfillStore
{
    public const string WriteSchema = "fern.run_protocol_backfill.v1";
    const int CommandTimeoutSeconds = 2;

    public static ProtocolBackfillReport Backfill(string databasePath, bool dryRun)
    {
        if (!File.Exists(databasePath))
        {
            throw new RunDataFault($"run database {databasePath} is not a regular file");
        }
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(databasePath),
                Mode = dryRun ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = CommandTimeoutSeconds,
            }.ToString());
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandTimeout = CommandTimeoutSeconds;
            select.CommandText = "SELECT id, invocation_json, artifact_dir, config_json FROM runs ORDER BY id";
            using var reader = select.ExecuteReader();
            var rows = new List<(long Id, string? InvocationJson, string? ArtifactDir, string? ConfigJson)>();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), NullableString(reader, 1), NullableString(reader, 2), NullableString(reader, 3)));
            }

            long rowsFilled = 0;
            long rowsWithoutProtocol = 0;
            foreach (var row in rows)
            {
                var projection = RunDataProjection.ProtocolFromInvocation(row.InvocationJson);
                if (projection.IsEmpty)
                {
                    rowsWithoutProtocol += 1;
                    continue;
                }
                rowsFilled += 1;
                if (dryRun) continue;

                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandTimeout = CommandTimeoutSeconds;
                update.CommandText = """
                    UPDATE runs
                    SET noise_law = $noise_law,
                        split_treatment_scheme = $split_treatment_scheme,
                        noise_ar1_phi = $noise_ar1_phi,
                        relative_dose = $relative_dose,
                        checkpoint_execution_mode = $checkpoint_execution_mode,
                        runtime_device = $runtime_device,
                        execution_kind = $execution_kind,
                        machine_alias = COALESCE($machine_alias, machine_alias),
                        model_config_hash = COALESCE($model_config_hash, model_config_hash)
                    WHERE id = $id
                    """;
                AddNullable(update, "$noise_law", projection.NoiseLaw);
                AddNullable(update, "$split_treatment_scheme", projection.SplitTreatmentScheme);
                AddNullable(update, "$noise_ar1_phi", projection.NoiseAr1Phi);
                AddNullable(update, "$relative_dose", projection.RelativeDose is null ? null : projection.RelativeDose.Value ? 1L : 0L);
                AddNullable(update, "$checkpoint_execution_mode", projection.CheckpointExecutionMode);
                AddNullable(update, "$runtime_device", projection.RuntimeDevice);
                AddNullable(update, "$execution_kind", projection.ExecutionKind);
                AddNullable(update, "$machine_alias", MachineAliasFromReceipt(row.ArtifactDir));
                AddNullable(update, "$model_config_hash", RunDataProjection.ModelConfigHash(row.ConfigJson));
                update.Parameters.AddWithValue("$id", row.Id);
                _ = update.ExecuteNonQuery();
            }
            transaction.Commit();
            return new ProtocolBackfillReport(WriteSchema, rows.Count, rowsFilled, rowsWithoutProtocol, dryRun);
        }
        catch (Exception exception) when (exception is not RunDataFault)
        {
            throw new RunDataFault($"cannot backfill Run protocol columns in {databasePath}", exception);
        }
    }

    static void AddNullable(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    static string? MachineAliasFromReceipt(string? artifactDirectory)
    {
        if (artifactDirectory is null || !Directory.Exists(artifactDirectory)) return null;
        try
        {
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(artifactDirectory, "execution_environment.json")));
            var accelerator = receipt.RootElement.TryGetProperty("cuda_devices", out var devices)
                && devices.ValueKind == JsonValueKind.Array
                && devices.GetArrayLength() > 0
                && devices[0].TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                    ? name.GetString() ?? "cpu"
                    : "cpu";
            return $"unknown-host-{NormalizeAliasPart(accelerator)}";
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    static string NormalizeAliasPart(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("NVIDIA ", StringComparison.Ordinal)) trimmed = trimmed[7..];
        if (trimmed.EndsWith(" GPU", StringComparison.Ordinal)) trimmed = trimmed[..^4];
        var pieces = new List<string>();
        var current = new StringBuilder();
        foreach (var character in trimmed.ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9') current.Append(character);
            else if (current.Length > 0) { pieces.Add(current.ToString()); current.Clear(); }
        }
        if (current.Length > 0) pieces.Add(current.ToString());
        return string.Join('-', pieces);
    }

}
