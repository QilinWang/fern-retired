using System.Text.Json;
using System.Text.Json.Serialization;

// NOTE ON THE NAMESPACE. Fern.Experiment/StageScientificContracts.cs already
// declares ArtifactKey, ScientificContentDigest, ArtifactRoleV1 and
// ReportedArtifactV1, and Fern.Scheduler files import them. Those types are a
// close but NOT digest-faithful overlap: their ReportedForecastMeaning is an
// untyped JsonElement, which reserializes in whatever key order it was parsed
// with and therefore cannot reproduce serde declaration order. This family
// lives in its own namespace so the two cannot be confused, and is a dedupe
// candidate once the Experiment types carry the typed meaning.
namespace Fern.Scheduler.AttemptContracts;

// The typed Attempt commit records, ported from crates/fern-artifact/src/lib.rs
// (WorkerRequestV1, WorkerResultReportV1, AttemptArtifactCommitV1) and the
// forecast/metrics types they reach.
//
// WHY THE FIELD ORDER IS LOAD-BEARING. Rust computes each record's identity as
// sha256(domain-prefix || serde_json::to_vec(value)). serde writes struct
// fields in DECLARATION order, compact, with no key sorting, and internally
// tagged enums put their tag first. So the declaration order below is not
// style: change it and every digest changes. The order here was taken from the
// Rust source and checked against 1,876 committed attempts under
// results/**/committed-attempts — every one of their stored request_digest
// values is reproduced exactly by AttemptArtifactDigest.OfRequest.
//
// Two enums are tagged on "operation" rather than "kind"; assuming "kind" for
// all of them reproduced only 46% of the corpus, which is how that was found.

// ---------------------------------------------------------------- identities

/// <summary>A typed identity Rust carried as a newtype over String. A
/// readonly record struct keeps the swap between two of them a compile error
/// rather than a wrong answer that looks right.</summary>
[JsonConverter(typeof(StringIdentityConverter<ArtifactKey>))]
public readonly record struct ArtifactKey(string Value) : IStringIdentity<ArtifactKey>
{
    public static ArtifactKey From(string value) => new(value);
    public override string ToString() => Value;
}

[JsonConverter(typeof(StringIdentityConverter<ByteDigest>))]
public readonly record struct ByteDigest(string Value) : IStringIdentity<ByteDigest>
{
    public static ByteDigest From(string value) => new(value);
    public override string ToString() => Value;
}

[JsonConverter(typeof(StringIdentityConverter<ScientificContentDigest>))]
public readonly record struct ScientificContentDigest(string Value) : IStringIdentity<ScientificContentDigest>
{
    public static ScientificContentDigest From(string value) => new(value);
    public override string ToString() => Value;
}

[JsonConverter(typeof(StringIdentityConverter<ScientificCaseId>))]
public readonly record struct ScientificCaseId(string Value) : IStringIdentity<ScientificCaseId>
{
    public static ScientificCaseId From(string value) => new(value);
    public override string ToString() => Value;
}

[JsonConverter(typeof(StringIdentityConverter<ModelArchitectureHash>))]
public readonly record struct ModelArchitectureHash(string Value) : IStringIdentity<ModelArchitectureHash>
{
    public static ModelArchitectureHash From(string value) => new(value);
    public override string ToString() => Value;
}

[JsonConverter(typeof(StringIdentityConverter<ModelImplementationHash>))]
public readonly record struct ModelImplementationHash(string Value) : IStringIdentity<ModelImplementationHash>
{
    public static ModelImplementationHash From(string value) => new(value);
    public override string ToString() => Value;
}

public interface IStringIdentity<TSelf> where TSelf : IStringIdentity<TSelf>
{
    string Value { get; }
    static abstract TSelf From(string value);
}

/// <summary>One converter for the whole newtype family: Rust wrote them as
/// plain JSON strings, so an extra object wrapper would change every digest.</summary>
public sealed class StringIdentityConverter<T> : JsonConverter<T> where T : IStringIdentity<T>
{
    public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        T.From(reader.GetString() ?? throw new JsonException($"{typeof(T).Name} must be a JSON string"));

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

// -------------------------------------------------------------- plain enums

[JsonConverter(typeof(JsonStringEnumConverter<ArtifactRole>))]
public enum ArtifactRole
{
    // Declaration order IS the Ord order Rust sorts request outputs by.
    [JsonStringEnumMemberName("world_parameters")] WorldParameters,
    [JsonStringEnumMemberName("latent_clean_trajectory")] LatentCleanTrajectory,
    [JsonStringEnumMemberName("recorded_reference_trajectory")] RecordedReferenceTrajectory,
    [JsonStringEnumMemberName("unit_noise")] UnitNoise,
    [JsonStringEnumMemberName("missingness_mask")] MissingnessMask,
    [JsonStringEnumMemberName("contamination_mask")] ContaminationMask,
    [JsonStringEnumMemberName("training_scale")] TrainingScale,
    [JsonStringEnumMemberName("split_definition")] SplitDefinition,
    [JsonStringEnumMemberName("forecast_origin_plan")] ForecastOriginPlan,
    [JsonStringEnumMemberName("final_noisy_input")] FinalNoisyInput,
    [JsonStringEnumMemberName("dense_point_forecast")] DensePointForecast,
    [JsonStringEnumMemberName("legacy_point_forecast")] LegacyPointForecast,
    [JsonStringEnumMemberName("quantile_forecast")] QuantileForecast,
    [JsonStringEnumMemberName("predictive_samples")] PredictiveSamples,
    [JsonStringEnumMemberName("raw_evaluation_tensors")] RawEvaluationTensors,
    [JsonStringEnumMemberName("model_checkpoint")] ModelCheckpoint,
    [JsonStringEnumMemberName("reconstruction_manifest")] ReconstructionManifest,
    [JsonStringEnumMemberName("compact_metrics")] CompactMetrics,
    [JsonStringEnumMemberName("release_metrics_table")] ReleaseMetricsTable,
    [JsonStringEnumMemberName("campaign_metrics_table")] CampaignMetricsTable,
    [JsonStringEnumMemberName("invocation_receipt")] InvocationReceipt,
    [JsonStringEnumMemberName("execution_environment")] ExecutionEnvironment,
    [JsonStringEnumMemberName("diagnostics")] Diagnostics,
    [JsonStringEnumMemberName("worker_log")] WorkerLog,
    [JsonStringEnumMemberName("compiled_experiment")] CompiledExperiment,
    [JsonStringEnumMemberName("raw_forecast")] RawForecast,
    [JsonStringEnumMemberName("postprocess_spec")] PostprocessSpec,
    [JsonStringEnumMemberName("sequence_forecast")] SequenceForecast,
    [JsonStringEnumMemberName("metrics")] Metrics,
    [JsonStringEnumMemberName("score_receipt")] ScoreReceipt,
    [JsonStringEnumMemberName("analysis_report")] AnalysisReport,
    [JsonStringEnumMemberName("training_worker_request")] TrainingWorkerRequest,
    [JsonStringEnumMemberName("generated_dataset")] GeneratedDataset,
    [JsonStringEnumMemberName("materialized_cell")] MaterializedCell,
    [JsonStringEnumMemberName("horizon_end_response")] HorizonEndResponse,
    // CHG 2026-09-04 (study chain, the formal shape): one study cell as one Stage.
    [JsonStringEnumMemberName("study_cell_request")] StudyCellRequest,
    [JsonStringEnumMemberName("unit_results")] UnitResults,
    [JsonStringEnumMemberName("unit_predictions")] UnitPredictions,
    [JsonStringEnumMemberName("unit_targets")] UnitTargets,
}

[JsonConverter(typeof(JsonStringEnumConverter<MetricTargetKind>))]
public enum MetricTargetKind
{
    [JsonStringEnumMemberName("observed_corrupted")] ObservedCorrupted,
    [JsonStringEnumMemberName("recorded_reference")] RecordedReference,
    [JsonStringEnumMemberName("latent_clean")] LatentClean,
}

[JsonConverter(typeof(JsonStringEnumConverter<ForecastValueSpace>))]
public enum ForecastValueSpace
{
    [JsonStringEnumMemberName("physical")] Physical,
    [JsonStringEnumMemberName("training_standardized")] TrainingStandardized,
}

[JsonConverter(typeof(JsonStringEnumConverter<PointKind>))]
public enum PointKind
{
    [JsonStringEnumMemberName("conditional_mean")] ConditionalMean,
    [JsonStringEnumMemberName("conditional_median")] ConditionalMedian,
    [JsonStringEnumMemberName("native_point_head")] NativePointHead,
}

[JsonConverter(typeof(JsonStringEnumConverter<SampleStatisticV1>))]
public enum SampleStatisticV1
{
    [JsonStringEnumMemberName("mean")] Mean,
    [JsonStringEnumMemberName("lower_median")] LowerMedian,
}

[JsonConverter(typeof(JsonStringEnumConverter<TensorDtype>))]
public enum TensorDtype
{
    [JsonStringEnumMemberName("float32")] Float32,
    [JsonStringEnumMemberName("float64")] Float64,
    [JsonStringEnumMemberName("int64")] Int64,
    [JsonStringEnumMemberName("uint8")] Uint8,
    [JsonStringEnumMemberName("bool")] Bool,
}

[JsonConverter(typeof(JsonStringEnumConverter<TensorByteOrder>))]
public enum TensorByteOrder
{
    [JsonStringEnumMemberName("little_endian")] LittleEndian,
}

[JsonConverter(typeof(JsonStringEnumConverter<TensorStorageOrder>))]
public enum TensorStorageOrder
{
    [JsonStringEnumMemberName("row_major")] RowMajor,
}

// ------------------------------------------------------------ tagged unions

/// <summary>Rust: `#[serde(tag = "kind")] enum OutputDisposition`. Closed with
/// a private constructor so nobody adds a case the switches do not know.</summary>
public abstract record OutputDisposition
{
    public sealed record Required : OutputDisposition;
    public sealed record Optional : OutputDisposition;
    public sealed record Omitted([property: JsonPropertyName("reason")] string Reason) : OutputDisposition;
    private OutputDisposition() { }
}

/// <summary>Rust: `#[serde(tag = "operation")] enum DeclaredPointDerivationV1`.
/// The tag is "operation", not "kind" — assuming "kind" here silently changed
/// the digest of every request carrying a quantile forecast.</summary>
public abstract record DeclaredPointDerivationV1
{
    public sealed record NativePointHead(
        [property: JsonPropertyName("producer_implementation")] ModelImplementationHash ProducerImplementation)
        : DeclaredPointDerivationV1;

    public sealed record PredictiveSampleReduction(
        [property: JsonPropertyName("source_artifact_key")] ArtifactKey SourceArtifactKey,
        [property: JsonPropertyName("statistic")] SampleStatisticV1 Statistic,
        [property: JsonPropertyName("derivation_version")] uint DerivationVersion)
        : DeclaredPointDerivationV1;

    public sealed record ExactQuantileSelection(
        [property: JsonPropertyName("source_artifact_key")] ArtifactKey SourceArtifactKey,
        [property: JsonPropertyName("quantile")] ExactRatio Quantile,
        [property: JsonPropertyName("derivation_version")] uint DerivationVersion)
        : DeclaredPointDerivationV1;

    private DeclaredPointDerivationV1() { }
}

/// <summary>Rust: `ReportedPointDerivationV1`, tagged on "operation". Its
/// sample/quantile variants name the source `source`, where the declared twin
/// says `source_artifact_key`; the two are not interchangeable.</summary>
public abstract record ReportedPointDerivationV1
{
    public sealed record NativePointHead(
        [property: JsonPropertyName("producer_implementation")] ModelImplementationHash ProducerImplementation)
        : ReportedPointDerivationV1;

    public sealed record PredictiveSampleReduction(
        [property: JsonPropertyName("source")] ArtifactReference Source,
        [property: JsonPropertyName("statistic")] SampleStatisticV1 Statistic,
        [property: JsonPropertyName("derivation_version")] uint DerivationVersion)
        : ReportedPointDerivationV1;

    public sealed record ExactQuantileSelection(
        [property: JsonPropertyName("source")] ArtifactReference Source,
        [property: JsonPropertyName("quantile")] ExactRatio Quantile,
        [property: JsonPropertyName("derivation_version")] uint DerivationVersion)
        : ReportedPointDerivationV1;

    private ReportedPointDerivationV1() { }
}

public abstract record DeclaredForecastMeaningV1
{
    public sealed record DensePoint(
        [property: JsonPropertyName("point_kind")] PointKind PointKind,
        [property: JsonPropertyName("derivation")] DeclaredPointDerivationV1 Derivation)
        : DeclaredForecastMeaningV1;

    public sealed record Quantiles(
        [property: JsonPropertyName("levels")] IReadOnlyList<ExactRatio> Levels)
        : DeclaredForecastMeaningV1;

    public sealed record PredictiveSamples(
        [property: JsonPropertyName("sample_count")] ulong SampleCount,
        [property: JsonPropertyName("sampling_contract_version")] uint SamplingContractVersion)
        : DeclaredForecastMeaningV1;

    private DeclaredForecastMeaningV1() { }
}

public abstract record ForecastMeaningV1
{
    public sealed record DensePoint(
        [property: JsonPropertyName("point_kind")] PointKind PointKind,
        [property: JsonPropertyName("derivation")] ReportedPointDerivationV1 Derivation)
        : ForecastMeaningV1;

    public sealed record Quantiles(
        [property: JsonPropertyName("levels")] IReadOnlyList<ExactRatio> Levels)
        : ForecastMeaningV1;

    public sealed record PredictiveSamples(
        [property: JsonPropertyName("sample_count")] ulong SampleCount,
        [property: JsonPropertyName("sampling_contract_version")] uint SamplingContractVersion)
        : ForecastMeaningV1;

    private ForecastMeaningV1() { }
}

// ------------------------------------------------------------------ records

public readonly record struct ExactRatio(
    [property: JsonPropertyName("numerator")] ulong Numerator,
    [property: JsonPropertyName("denominator")] ulong Denominator);

public sealed record TensorAxis(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("length")] ulong Length);

public sealed record TensorLayout(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("dtype")] TensorDtype Dtype,
    [property: JsonPropertyName("byte_order")] TensorByteOrder ByteOrder,
    [property: JsonPropertyName("storage_order")] TensorStorageOrder StorageOrder,
    [property: JsonPropertyName("axes")] IReadOnlyList<TensorAxis> Axes);

public sealed record ScientificCaseReference(
    [property: JsonPropertyName("case_id")] ScientificCaseId CaseId,
    [property: JsonPropertyName("manifest_digest")] ScientificContentDigest ManifestDigest);

public sealed record ArtifactReference(
    [property: JsonPropertyName("artifact_key")] ArtifactKey ArtifactKey,
    [property: JsonPropertyName("role")] ArtifactRole Role,
    [property: JsonPropertyName("content_digest")] ScientificContentDigest ContentDigest);

public sealed record CaseRealization(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("dose")] ExactRatio Dose,
    [property: JsonPropertyName("final_input_content_digest")] ScientificContentDigest FinalInputContentDigest,
    [property: JsonPropertyName("retained_final_input")] ArtifactReference? RetainedFinalInput);

public sealed record WorkerAuthorityReference(
    [property: JsonPropertyName("coordinator_lease_id")] string CoordinatorLeaseId,
    [property: JsonPropertyName("coordinator_epoch")] long CoordinatorEpoch,
    [property: JsonPropertyName("coordinate_lease_id")] string CoordinateLeaseId,
    [property: JsonPropertyName("coordinate_epoch")] long CoordinateEpoch,
    [property: JsonPropertyName("resource_lease_id")] string ResourceLeaseId,
    [property: JsonPropertyName("resource_epoch")] long ResourceEpoch);

public sealed record MetricContractV1(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("required_targets")] IReadOnlyList<MetricTargetKind> RequiredTargets,
    [property: JsonPropertyName("ept_threshold_multipliers")] IReadOnlyList<ExactRatio> EptThresholdMultipliers,
    [property: JsonPropertyName("require_point_wd")] bool RequirePointWd);

public sealed record DeclaredForecastOutputV1(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("scientific_cases")] IReadOnlyList<ScientificCaseReference> ScientificCases,
    [property: JsonPropertyName("forecast_origin_plan")] ArtifactReference ForecastOriginPlan,
    [property: JsonPropertyName("channel_order")] IReadOnlyList<string> ChannelOrder,
    [property: JsonPropertyName("value_space")] ForecastValueSpace ValueSpace,
    [property: JsonPropertyName("scale_reference")] ArtifactReference? ScaleReference,
    [property: JsonPropertyName("meaning")] DeclaredForecastMeaningV1 Meaning);

public sealed record ReportedForecastMeaningV1(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("scientific_cases")] IReadOnlyList<ScientificCaseReference> ScientificCases,
    [property: JsonPropertyName("forecast_origin_plan")] ArtifactReference ForecastOriginPlan,
    [property: JsonPropertyName("channel_order")] IReadOnlyList<string> ChannelOrder,
    [property: JsonPropertyName("value_space")] ForecastValueSpace ValueSpace,
    [property: JsonPropertyName("scale_reference")] ArtifactReference? ScaleReference,
    [property: JsonPropertyName("meaning")] ForecastMeaningV1 Meaning);

public sealed record OutputContract(
    [property: JsonPropertyName("role")] ArtifactRole Role,
    [property: JsonPropertyName("artifact_key")] ArtifactKey? ArtifactKey,
    [property: JsonPropertyName("disposition")] OutputDisposition Disposition,
    [property: JsonPropertyName("tensor_layout")] TensorLayout? TensorLayout,
    [property: JsonPropertyName("forecast_contract")] DeclaredForecastOutputV1? ForecastContract);

public sealed record ReportedArtifactV1(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("artifact_key")] ArtifactKey ArtifactKey,
    [property: JsonPropertyName("role")] ArtifactRole Role,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("byte_count")] ulong ByteCount,
    [property: JsonPropertyName("byte_digest")] ByteDigest ByteDigest,
    [property: JsonPropertyName("content_digest")] ScientificContentDigest ContentDigest,
    [property: JsonPropertyName("tensor_layout")] TensorLayout? TensorLayout,
    [property: JsonPropertyName("reported_forecast_meaning")] ReportedForecastMeaningV1? ReportedForecastMeaning);

public sealed record CommittedArtifactV1(
    [property: JsonPropertyName("artifact")] ReportedArtifactV1 Artifact,
    [property: JsonPropertyName("relative_path")] string RelativePath);

public sealed record WorkerRequestV1(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("matrix_id")] string MatrixId,
    [property: JsonPropertyName("coordinate_id")] string CoordinateId,
    [property: JsonPropertyName("attempt_id")] string AttemptId,
    [property: JsonPropertyName("run_hash")] string RunHash,
    [property: JsonPropertyName("scientific_case")] ScientificCaseReference ScientificCase,
    [property: JsonPropertyName("realization")] CaseRealization Realization,
    [property: JsonPropertyName("model_architecture")] ModelArchitectureHash ModelArchitecture,
    [property: JsonPropertyName("implementation")] ModelImplementationHash Implementation,
    [property: JsonPropertyName("execution_profile_revision")] string ExecutionProfileRevision,
    [property: JsonPropertyName("authority")] WorkerAuthorityReference Authority,
    [property: JsonPropertyName("metric_contract")] MetricContractV1 MetricContract,
    [property: JsonPropertyName("outputs")] IReadOnlyList<OutputContract> Outputs);

public sealed record WorkerResultReportV1(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("attempt_id")] string AttemptId,
    [property: JsonPropertyName("run_hash")] string RunHash,
    [property: JsonPropertyName("request_digest")] ScientificContentDigest RequestDigest,
    [property: JsonPropertyName("outputs")] IReadOnlyList<ReportedArtifactV1> Outputs);

public sealed record AttemptArtifactCommitV1(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("attempt_id")] string AttemptId,
    [property: JsonPropertyName("run_hash")] string RunHash,
    [property: JsonPropertyName("request_digest")] ScientificContentDigest RequestDigest,
    [property: JsonPropertyName("result_digest")] ScientificContentDigest ResultDigest,
    [property: JsonPropertyName("outputs")] IReadOnlyList<CommittedArtifactV1> Outputs);

// ------------------------------------------------------------ identity math

/// <summary>
/// The canonical bytes and content addresses Rust computes for these records.
///
/// Rust: `domain_digest(domain, &serde_json::to_vec(&canonical))`, i.e.
/// sha256(domain-prefix bytes || compact JSON of the value with its fields in
/// declaration order), rendered `sha256:` + lowercase hex. Both canonical
/// forms sort a collection first, and the two sorts are different:
/// a request sorts its outputs by <see cref="ArtifactRole"/> declaration
/// order, a result sorts its outputs by artifact key as ordinal text.
///
/// Verified against 1,876 committed attempts under results/**/committed-attempts:
/// every stored request_digest and result_digest is reproduced exactly.
/// </summary>
public static class AttemptArtifactDigest
{
    /// serde writes compact JSON, emits nulls, and escapes only what JSON
    /// requires — never non-ASCII, and never `&`, `&lt;` or `&gt;`.
    public static readonly JsonSerializerOptions Canonical = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Rust marks all three records #[serde(deny_unknown_fields)]. STJ
        // ignores unmapped members by default, which would let a record
        // carrying a field this port does not know pass silently and then
        // digest to something else. Refuse it the way serde does.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new OutputDispositionConverter(),
            new DeclaredPointDerivationConverter(),
            new ReportedPointDerivationConverter(),
            new DeclaredForecastMeaningConverter(),
            new ForecastMeaningConverter(),
        },
    };

    public static ScientificContentDigest OfRequest(WorkerRequestV1 request)
    {
        var canonical = request with { Outputs = request.Outputs.OrderBy(output => output.Role).ToArray() };
        return Domain("fern.worker_request.v1\0", JsonSerializer.SerializeToUtf8Bytes(canonical, Canonical));
    }

    public static ScientificContentDigest OfResult(WorkerResultReportV1 result)
    {
        var canonical = result with
        {
            Outputs = result.Outputs
                .OrderBy(output => output.ArtifactKey.Value, StringComparer.Ordinal)
                .ToArray(),
        };
        return Domain("fern.worker_result_report.v1\0", JsonSerializer.SerializeToUtf8Bytes(canonical, Canonical));
    }

    static ScientificContentDigest Domain(string domain, byte[] body)
    {
        // The domain prefix ends in a NUL, so a value can never be read as
        // belonging to another family by concatenation.
        var prefix = System.Text.Encoding.UTF8.GetBytes(domain);
        var buffer = new byte[prefix.Length + body.Length];
        prefix.CopyTo(buffer, 0);
        body.CopyTo(buffer, prefix.Length);
        var hash = System.Security.Cryptography.SHA256.HashData(buffer);
        return new ScientificContentDigest("sha256:" + Convert.ToHexString(hash).ToLowerInvariant());
    }
}

/// <summary>A refusal is data with the field that failed, the way Rust's
/// ContractError carried one. Nothing here throws for a bad record.</summary>
public abstract record ContractCheck
{
    public sealed record Ok : ContractCheck;
    public sealed record Refused(string Field, string Detail) : ContractCheck;
    private ContractCheck() { }

    public static readonly ContractCheck Passed = new Ok();
    public static ContractCheck Refuse(string field, string detail) => new Refused(field, detail);
}

public static class AttemptArtifactCommitContract
{
    public const string CommitSchema = "fern.attempt_artifact_commit.v1";
    public const string RequestSchema = "fern.worker_request.v1";
    public const string ResultSchema = "fern.worker_result_report.v1";

    /// <summary>
    /// Rust `AttemptArtifactCommitV1::validate_against`, in its order: the
    /// result is checked against the request first, then the commit's own
    /// schema, identity, two digests, and its output set. Every refusal keeps
    /// the Rust field name and wording, because these are what an operator
    /// reads immediately before a deletion.
    /// </summary>
    public static ContractCheck ValidateAgainst(
        this AttemptArtifactCommitV1 commit, WorkerRequestV1 request, WorkerResultReportV1 result)
    {
        var resultCheck = ValidateResultAgainstRequest(result, request);
        if (resultCheck is ContractCheck.Refused) return resultCheck;

        if (commit.Schema != CommitSchema)
        {
            return ContractCheck.Refuse(
                "attempt_artifact_commit.schema", $"expected \"{CommitSchema}\", found \"{commit.Schema}\"");
        }

        if (commit.AttemptId != request.AttemptId || commit.RunHash != request.RunHash)
        {
            return ContractCheck.Refuse(
                "attempt_artifact_commit.identity", "Attempt and run must match the worker request");
        }

        if (commit.RequestDigest != AttemptArtifactDigest.OfRequest(request))
        {
            return ContractCheck.Refuse(
                "attempt_artifact_commit.request_digest", "does not match the worker request");
        }

        if (commit.ResultDigest != AttemptArtifactDigest.OfResult(result))
        {
            return ContractCheck.Refuse(
                "attempt_artifact_commit.result_digest",
                "does not match the worker result's reported artifact");
        }

        var reported = result.Outputs.ToDictionary(artifact => artifact.ArtifactKey);
        var committedKeys = new HashSet<ArtifactKey>();
        var relativePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in commit.Outputs)
        {
            if (!committedKeys.Add(output.Artifact.ArtifactKey))
            {
                return ContractCheck.Refuse(
                    "attempt_artifact_commit.outputs.artifact_key", "contains a duplicate artifact key");
            }

            if (!relativePaths.Add(output.RelativePath))
            {
                return ContractCheck.Refuse(
                    "attempt_artifact_commit.outputs.relative_path", "contains a duplicate relative path");
            }

            if (!reported.TryGetValue(output.Artifact.ArtifactKey, out var match)
                || !SameValue(match, output.Artifact))
            {
                return ContractCheck.Refuse(
                    "attempt_artifact_commit.outputs",
                    $"artifact \"{output.Artifact.ArtifactKey.Value}\" does not exactly match the worker result");
            }
        }

        if (committedKeys.Count != reported.Count)
        {
            return ContractCheck.Refuse(
                "attempt_artifact_commit.outputs",
                "must commit every output in the worker result exactly once");
        }

        return ContractCheck.Passed;
    }

    /// <summary>Rust `WorkerResultReportV1::validate_against`, structural half.
    /// The per-artifact forecast/tensor checks Rust performs through
    /// `matches_reported` are NOT reproduced here; see the port report.</summary>
    public static ContractCheck ValidateResultAgainstRequest(
        WorkerResultReportV1 result, WorkerRequestV1 request)
    {
        if (request.Schema != RequestSchema)
        {
            return ContractCheck.Refuse(
                "worker_request.schema", $"expected \"{RequestSchema}\", found \"{request.Schema}\"");
        }

        if (result.Schema != ResultSchema)
        {
            return ContractCheck.Refuse(
                "worker_result.schema", $"expected \"{ResultSchema}\", found \"{result.Schema}\"");
        }

        if (result.AttemptId != request.AttemptId)
        {
            return ContractCheck.Refuse(
                "worker_result.attempt_id", $"expected \"{request.AttemptId}\", found \"{result.AttemptId}\"");
        }

        if (result.RunHash != request.RunHash)
        {
            return ContractCheck.Refuse(
                "worker_result.run_hash", $"expected \"{request.RunHash}\", found \"{result.RunHash}\"");
        }

        if (result.RequestDigest != AttemptArtifactDigest.OfRequest(request))
        {
            return ContractCheck.Refuse(
                "worker_result.request_digest", "does not match the declared worker request");
        }

        var contracts = request.Outputs.ToDictionary(contract => contract.Role);
        var observedRoles = new HashSet<ArtifactRole>();
        var observedKeys = new HashSet<ArtifactKey>();
        foreach (var output in result.Outputs)
        {
            if (!contracts.TryGetValue(output.Role, out var contract))
            {
                return ContractCheck.Refuse(
                    "worker_result.outputs", $"{RoleName(output.Role)} was not declared by the request");
            }

            if (contract.Disposition is OutputDisposition.Omitted)
            {
                return ContractCheck.Refuse(
                    "worker_result.outputs",
                    $"{RoleName(output.Role)} was declared omitted but the result contains it");
            }

            if (!observedRoles.Add(output.Role))
            {
                return ContractCheck.Refuse(
                    "worker_result.outputs", $"duplicate reported {RoleName(output.Role)} artifact");
            }

            if (!observedKeys.Add(output.ArtifactKey))
            {
                return ContractCheck.Refuse(
                    "worker_result.outputs.artifact_key",
                    $"artifact key \"{output.ArtifactKey.Value}\" is reused for more than one output role");
            }

            if (contract.ArtifactKey != output.ArtifactKey)
            {
                return ContractCheck.Refuse(
                    "worker_result.outputs.artifact_key",
                    $"{RoleName(output.Role)} must use the predeclared artifact key");
            }

            if (!SameValue(contract.TensorLayout, output.TensorLayout))
            {
                return ContractCheck.Refuse(
                    "worker_result.outputs.tensor_layout",
                    $"{RoleName(output.Role)} does not match the requested layout");
            }
        }

        foreach (var contract in request.Outputs)
        {
            if (contract.Disposition is OutputDisposition.Required && !observedRoles.Contains(contract.Role))
            {
                return ContractCheck.Refuse(
                    "worker_result.outputs", $"missing required {RoleName(contract.Role)}");
            }
        }

        return ContractCheck.Passed;
    }


    /// <summary>
    /// Rust compared these values with derived PartialEq, which is structural
    /// all the way down. C# record equality is structural for its members but
    /// an IReadOnlyList member compares by REFERENCE, so two layouts with equal
    /// axes read as different. Comparing the canonical bytes restores the Rust
    /// meaning; it is only used where a comparison decides a refusal.
    /// </summary>
    static bool SameValue<T>(T left, T right) =>
        JsonSerializer.Serialize(left, AttemptArtifactDigest.Canonical)
            == JsonSerializer.Serialize(right, AttemptArtifactDigest.Canonical);

    /// The wire spelling Rust's `ArtifactRole::as_str` produces, which is what
    /// its refusal sentences name.
    public static string RoleName(ArtifactRole role) =>
        JsonSerializer.Serialize(role, AttemptArtifactDigest.Canonical).Trim('"');
}

// -------------------------------------------------- internally tagged unions

/// <summary>
/// serde's internally tagged enums write the tag first and then the variant's
/// own fields in declaration order. System.Text.Json's built-in polymorphism
/// can WRITE that shape but cannot READ it unless the discriminator happens to
/// be the first property — and the committed files on disk are key-sorted, so
/// `derivation` arrives before `kind`. These converters read the object whole,
/// dispatch on the tag wherever it sits, and write the serde shape back.
///
/// They are registered on the options rather than by attribute so that
/// serializing a concrete variant does not re-enter the converter.
/// </summary>
public abstract class TaggedUnionConverter<TBase> : JsonConverter<TBase> where TBase : class
{
    protected abstract string Tag { get; }
    protected abstract Type VariantFor(string tag);
    protected abstract string TagFor(TBase value);

    public override bool CanConvert(Type type) => type == typeof(TBase);

    public override TBase Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (!document.RootElement.TryGetProperty(Tag, out var tag) || tag.GetString() is not { } name)
        {
            throw new JsonException($"{typeof(TBase).Name} needs a \"{Tag}\" tag");
        }

        // The tag is consumed here, so it must not reach the variant: with
        // deny_unknown_fields honoured, an unmapped "kind"/"operation" would
        // refuse the very shape this converter exists to read.
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals(Tag)) continue;
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return (TBase)JsonSerializer.Deserialize(body.ToArray(), VariantFor(name), options)!;
    }

    public override void Write(Utf8JsonWriter writer, TBase value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(Tag, TagFor(value));
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(value, value.GetType(), options));
        foreach (var property in body.RootElement.EnumerateObject())
        {
            property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}

public sealed class OutputDispositionConverter : TaggedUnionConverter<OutputDisposition>
{
    protected override string Tag => "kind";
    protected override Type VariantFor(string tag) => tag switch
    {
        "required" => typeof(OutputDisposition.Required),
        "optional" => typeof(OutputDisposition.Optional),
        "omitted" => typeof(OutputDisposition.Omitted),
        _ => throw new JsonException($"unknown output disposition {tag}"),
    };
    protected override string TagFor(OutputDisposition value) => value switch
    {
        OutputDisposition.Required => "required",
        OutputDisposition.Optional => "optional",
        OutputDisposition.Omitted => "omitted",
        // Unreachable: the hierarchy is closed by its private constructor.
        _ => throw new JsonException($"unknown output disposition {value.GetType().Name}"),
    };
}

public sealed class DeclaredPointDerivationConverter : TaggedUnionConverter<DeclaredPointDerivationV1>
{
    protected override string Tag => "operation";
    protected override Type VariantFor(string tag) => tag switch
    {
        "native_point_head" => typeof(DeclaredPointDerivationV1.NativePointHead),
        "predictive_sample_reduction" => typeof(DeclaredPointDerivationV1.PredictiveSampleReduction),
        "exact_quantile_selection" => typeof(DeclaredPointDerivationV1.ExactQuantileSelection),
        _ => throw new JsonException($"unknown declared point derivation {tag}"),
    };
    protected override string TagFor(DeclaredPointDerivationV1 value) => value switch
    {
        DeclaredPointDerivationV1.NativePointHead => "native_point_head",
        DeclaredPointDerivationV1.PredictiveSampleReduction => "predictive_sample_reduction",
        DeclaredPointDerivationV1.ExactQuantileSelection => "exact_quantile_selection",
        _ => throw new JsonException($"unknown declared point derivation {value.GetType().Name}"),
    };
}

public sealed class ReportedPointDerivationConverter : TaggedUnionConverter<ReportedPointDerivationV1>
{
    protected override string Tag => "operation";
    protected override Type VariantFor(string tag) => tag switch
    {
        "native_point_head" => typeof(ReportedPointDerivationV1.NativePointHead),
        "predictive_sample_reduction" => typeof(ReportedPointDerivationV1.PredictiveSampleReduction),
        "exact_quantile_selection" => typeof(ReportedPointDerivationV1.ExactQuantileSelection),
        _ => throw new JsonException($"unknown reported point derivation {tag}"),
    };
    protected override string TagFor(ReportedPointDerivationV1 value) => value switch
    {
        ReportedPointDerivationV1.NativePointHead => "native_point_head",
        ReportedPointDerivationV1.PredictiveSampleReduction => "predictive_sample_reduction",
        ReportedPointDerivationV1.ExactQuantileSelection => "exact_quantile_selection",
        _ => throw new JsonException($"unknown reported point derivation {value.GetType().Name}"),
    };
}

public sealed class DeclaredForecastMeaningConverter : TaggedUnionConverter<DeclaredForecastMeaningV1>
{
    protected override string Tag => "kind";
    protected override Type VariantFor(string tag) => tag switch
    {
        "dense_point" => typeof(DeclaredForecastMeaningV1.DensePoint),
        "quantiles" => typeof(DeclaredForecastMeaningV1.Quantiles),
        "predictive_samples" => typeof(DeclaredForecastMeaningV1.PredictiveSamples),
        _ => throw new JsonException($"unknown declared forecast meaning {tag}"),
    };
    protected override string TagFor(DeclaredForecastMeaningV1 value) => value switch
    {
        DeclaredForecastMeaningV1.DensePoint => "dense_point",
        DeclaredForecastMeaningV1.Quantiles => "quantiles",
        DeclaredForecastMeaningV1.PredictiveSamples => "predictive_samples",
        _ => throw new JsonException($"unknown declared forecast meaning {value.GetType().Name}"),
    };
}

public sealed class ForecastMeaningConverter : TaggedUnionConverter<ForecastMeaningV1>
{
    protected override string Tag => "kind";
    protected override Type VariantFor(string tag) => tag switch
    {
        "dense_point" => typeof(ForecastMeaningV1.DensePoint),
        "quantiles" => typeof(ForecastMeaningV1.Quantiles),
        "predictive_samples" => typeof(ForecastMeaningV1.PredictiveSamples),
        _ => throw new JsonException($"unknown forecast meaning {tag}"),
    };
    protected override string TagFor(ForecastMeaningV1 value) => value switch
    {
        ForecastMeaningV1.DensePoint => "dense_point",
        ForecastMeaningV1.Quantiles => "quantiles",
        ForecastMeaningV1.PredictiveSamples => "predictive_samples",
        _ => throw new JsonException($"unknown forecast meaning {value.GetType().Name}"),
    };
}

// ------------------------------------------------------- attempt JSON census

// Ported from crates/fern-artifact/src/attempt_metadata.rs. The point of this
// census is stated in the Rust module header and is worth repeating: JSON is
// not the problem, duplicate authority centers are. An Attempt may persist Home
// intent and the runner claim; checked decisions live in Registry Home;
// compiled objects stay content-addressed and the Attempt keeps only a digest.
// An unlisted sidecar is REFUSED so a new authority cannot appear by accident.

/// <summary>Where one Attempt JSON gets its authority, if it has any.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AttemptJsonClass>))]
public enum AttemptJsonClass
{
    /// Home intent. Persisted as request.json in the worker or Stage spelling.
    [JsonStringEnumMemberName("home_intent")] HomeIntent,
    /// Runner claim. Persisted as report.json in the worker or Stage spelling.
    [JsonStringEnumMemberName("runner_claim")] RunnerClaim,
    /// Checked decision. Registry Home is the authority; the file is a leftover sidecar.
    [JsonStringEnumMemberName("checked_authority")] CheckedAuthority,
    /// Immutable payload or content-addressed object. Attempt keeps a digest.
    [JsonStringEnumMemberName("immutable_payload")] ImmutablePayload,
    /// Derived or transport-only. Never a second source of truth.
    [JsonStringEnumMemberName("derived_transport")] DerivedTransport,
}

public sealed record AttemptJsonCensusEntry(string RelativePath, AttemptJsonClass Class, string? SchemaId);

public sealed record AttemptJsonRecordV1(
    [property: JsonPropertyName("relative_path")] string RelativePath,
    [property: JsonPropertyName("class")] AttemptJsonClass Class,
    [property: JsonPropertyName("schema_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SchemaId,
    [property: JsonPropertyName("content_digest")] string ContentDigest);

public sealed record AttemptObjectRefV1(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("content_digest")] string ContentDigest);

/// <summary>Registry Home's checked facts for this Attempt. Digests only —
/// every member is omitted when absent, as serde's skip_serializing_if does,
/// which is why an empty authority renders as `{}` and not five nulls.</summary>
public sealed record AttemptCheckedAuthorityV1(
    [property: JsonPropertyName("receipt_digest")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReceiptDigest = null,
    [property: JsonPropertyName("commit_digest")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CommitDigest = null,
    [property: JsonPropertyName("stage_plan_digest")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StagePlanDigest = null,
    [property: JsonPropertyName("compiled_experiment_digest")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CompiledExperimentDigest = null,
    [property: JsonPropertyName("implementation_digest")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImplementationDigest = null);

public sealed record AttemptMetadataProjectionV1(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("attempt_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AttemptId,
    [property: JsonPropertyName("home_intent")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AttemptJsonRecordV1? HomeIntent,
    [property: JsonPropertyName("runner_claim")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AttemptJsonRecordV1? RunnerClaim,
    [property: JsonPropertyName("checked_authority")] AttemptCheckedAuthorityV1 CheckedAuthority,
    [property: JsonPropertyName("objects")] IReadOnlyList<AttemptObjectRefV1> Objects,
    [property: JsonPropertyName("derived_transport")] IReadOnlyList<AttemptJsonRecordV1> DerivedTransport);

/// <summary>One file fed to the projection. The caller hashed the bytes; this
/// type never opens disk, exactly as the Rust one did not.</summary>
public sealed record ObservedAttemptJson(
    string RelativePath, string? SchemaId, string ContentDigest, JsonElement? Body);

public static class AttemptMetadata
{
    public const string ProjectionSchema = "fern.attempt_metadata_projection.v1";

    /// <summary>The fifteen Attempt metadata JSON files. Payload tensors and
    /// inputs/ and outputs/ objects are classified by prefix, not by growing
    /// this list.</summary>
    public static readonly IReadOnlyList<AttemptJsonCensusEntry> Census =
    [
        new("worker_request.v1.json", AttemptJsonClass.HomeIntent, "fern.worker_request.v1"),
        new("request/stage_runner_request.v1.json", AttemptJsonClass.HomeIntent, "fern.stage_runner_request.v1"),
        new("worker_result_report.v1.json", AttemptJsonClass.RunnerClaim, "fern.worker_result_report.v1"),
        new("report/stage_runner_report.v1.json", AttemptJsonClass.RunnerClaim, "fern.stage_runner_report.v1"),
        new("attempt_artifact_commit.v1.json", AttemptJsonClass.CheckedAuthority, "fern.attempt_artifact_commit.v1"),
        new("invocation.json", AttemptJsonClass.CheckedAuthority, "fern.run_invocation.v3"),
        new("execution_environment.json", AttemptJsonClass.ImmutablePayload, "fern.execution_environment.v1"),
        new("fern.json", AttemptJsonClass.ImmutablePayload, null),
        new("request.json", AttemptJsonClass.ImmutablePayload, "fern.training_worker_request.v1"),
        new("scientific_case/scientific_case_files.v1.json", AttemptJsonClass.ImmutablePayload, "fern.scientific_case_files.v1"),
        new("remote_attempt_instructions.v1.json", AttemptJsonClass.DerivedTransport, "fern.remote_attempt_instructions.v1"),
        new("remote_attempt_report.v1.json", AttemptJsonClass.DerivedTransport, "fern.remote_attempt_report.v1"),
        new("heartbeat.json", AttemptJsonClass.DerivedTransport, null),
        new("run_packet.json", AttemptJsonClass.DerivedTransport, "fern.run_packet.v2"),
        new("registration.json", AttemptJsonClass.DerivedTransport, null),
    ];

    static readonly string[] PayloadJson =
    [
        "forecast_calibration.json", "calibration.json", "hist.json", "result.json",
        "logs/training_worker_result.json",
        "evidence/quantile/v1/manifest.json", "evidence/quantile/v1/checked.json",
        "evidence/quantile/v1/diagnostics.json", "quantile_diag.json", "diagnostics.json",
    ];

    public abstract record Classification
    {
        public sealed record Classified(AttemptJsonClass Class) : Classification;
        public sealed record Refused(string Detail) : Classification;
        private Classification() { }
    }

    /// <summary>
    /// Rust `classify_attempt_json`. An unknown path is refused rather than
    /// defaulted, so a new authoritative sidecar cannot appear by accident;
    /// the refusal text is the one an operator sees.
    /// </summary>
    public static Classification Classify(string relativePath)
    {
        var path = relativePath.StartsWith("./", StringComparison.Ordinal) ? relativePath[2..] : relativePath;
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        if (path.Length == 0
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Contains("..", StringComparison.Ordinal))
        {
            return new Classification.Refused(
                $"Attempt JSON path \"{path}\" is not a workspace-relative file");
        }

        var entry = Census.FirstOrDefault(candidate => candidate.RelativePath == path);
        if (entry is not null)
        {
            return new Classification.Classified(entry.Class);
        }

        if (PayloadJson.Contains(path)
            || path.StartsWith("inputs/", StringComparison.Ordinal)
            || path.StartsWith("outputs/", StringComparison.Ordinal)
            || path.StartsWith("scientific_case/", StringComparison.Ordinal))
        {
            return new Classification.Classified(AttemptJsonClass.ImmutablePayload);
        }

        return new Classification.Refused(
            $"new authoritative Attempt sidecar \"{path}\" is refused; persist Home intent as request, "
            + "the runner claim as report, checked decisions in Registry, or mark the file derived/transport-only");
    }

    public abstract record ProjectionResult
    {
        public sealed record Projected(AttemptMetadataProjectionV1 Projection) : ProjectionResult;
        public sealed record Refused(string Detail) : ProjectionResult;
        private ProjectionResult() { }
    }

    /// <summary>
    /// Rust `project_attempt_metadata`. Merges observed Attempt JSON into one
    /// request / report / Registry / object view. Two Home intents or two
    /// runner claims are refused: that is the duplicate-authority the census
    /// exists to prevent.
    /// </summary>
    public static ProjectionResult Project(
        IReadOnlyList<ObservedAttemptJson> files, AttemptCheckedAuthorityV1 checkedAuthority)
    {
        AttemptJsonRecordV1? homeIntent = null;
        AttemptJsonRecordV1? runnerClaim = null;
        var derivedTransport = new List<AttemptJsonRecordV1>();
        // A sorted map: the projection's object order is its KEY order, and the
        // key is the object name for a named ref and the relative path for a
        // payload. Rust used a BTreeMap, so ordinal text order is the contract.
        var objects = new SortedDictionary<string, AttemptObjectRefV1>(StringComparer.Ordinal);
        string? attemptId = null;

        foreach (var file in files)
        {
            if (Classify(file.RelativePath) is not Classification.Classified classified)
            {
                return new ProjectionResult.Refused(
                    ((Classification.Refused)Classify(file.RelativePath)).Detail);
            }

            var record = new AttemptJsonRecordV1(
                file.RelativePath, classified.Class, file.SchemaId, file.ContentDigest);
            switch (classified.Class)
            {
                case AttemptJsonClass.HomeIntent:
                    if (homeIntent is not null)
                    {
                        return new ProjectionResult.Refused(
                            "an Attempt may persist exactly one Home intent request");
                    }

                    attemptId ??= AttemptIdFrom(file.Body);
                    CollectObjectRefs(file.Body, objects);
                    homeIntent = record;
                    break;
                case AttemptJsonClass.RunnerClaim:
                    if (runnerClaim is not null)
                    {
                        return new ProjectionResult.Refused(
                            "an Attempt may persist exactly one runner-claim report");
                    }

                    attemptId ??= AttemptIdFrom(file.Body);
                    runnerClaim = record;
                    break;
                case AttemptJsonClass.CheckedAuthority:
                    // Leftover sidecar. The projection prefers Registry digests.
                    break;
                case AttemptJsonClass.ImmutablePayload:
                    if (!objects.ContainsKey(file.RelativePath))
                    {
                        objects[file.RelativePath] = new AttemptObjectRefV1(
                            file.SchemaId ?? "fern.attempt_payload", file.ContentDigest);
                    }

                    break;
                case AttemptJsonClass.DerivedTransport:
                    derivedTransport.Add(record);
                    break;
            }
        }

        derivedTransport.Sort((left, right) =>
            string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return new ProjectionResult.Projected(new AttemptMetadataProjectionV1(
            ProjectionSchema,
            attemptId,
            homeIntent,
            runnerClaim,
            checkedAuthority,
            objects.Values.ToArray(),
            derivedTransport));
    }

    /// Rust reads `attempt_id`, then `stage_attempt_id`. Both spellings exist
    /// because the worker and Stage requests name the same thing differently.
    static string? AttemptIdFrom(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } value) return null;
        if (value.TryGetProperty("attempt_id", out var direct) && direct.ValueKind == JsonValueKind.String)
        {
            return direct.GetString();
        }

        return value.TryGetProperty("stage_attempt_id", out var stage) && stage.ValueKind == JsonValueKind.String
            ? stage.GetString()
            : null;
    }

    static string? StringAt(JsonElement value, params string[] path)
    {
        var current = value;
        foreach (var step in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(step, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    /// Rust `collect_object_refs`: three named content-addressed objects the
    /// request points at, taken in the same fallback order.
    static void CollectObjectRefs(JsonElement? body, SortedDictionary<string, AttemptObjectRefV1> objects)
    {
        if (body is not { ValueKind: JsonValueKind.Object } value) return;

        if (StringAt(value, "invocation", "plan_digest") is { } planDigest)
        {
            objects["stage_plan"] = new AttemptObjectRefV1("fern.stage_plan.v1", planDigest);
        }

        var implementation = StringAt(value, "implementation_digest")
            ?? StringAt(value, "invocation", "implementation", "implementation_digest")
            ?? StringAt(value, "implementation");
        if (implementation is { } implementationDigest)
        {
            objects["implementation"] = new AttemptObjectRefV1("fern.implementation", implementationDigest);
        }

        if (StringAt(value, "model_architecture") is { } architecture)
        {
            objects["model_architecture"] = new AttemptObjectRefV1("fern.model_architecture", architecture);
        }
    }
}
