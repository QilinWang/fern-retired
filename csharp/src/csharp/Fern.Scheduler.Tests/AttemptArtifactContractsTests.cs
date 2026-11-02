using System.Text.Json;
using Fern.Scheduler.AttemptContracts;
using Xunit;
using Xunit.Abstractions;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Old-owner goldens for the Attempt artifact contracts (correction 362: the
/// bytes come from the implementation being replaced, before the new code is
/// trusted).
///
/// The old owner here is not a test fixture — crates/fern-artifact ships none
/// for these three records — it is the corpus of real committed attempts under
/// results/**/committed-attempts. Each directory holds worker_request.v1.json,
/// worker_result_report.v1.json and attempt_artifact_commit.v1.json, and the
/// commit carries the request_digest and result_digest that the Rust binary
/// computed at the time. Reproducing those two values from the two files
/// beside them is a byte-exact proof of the canonical JSON rule, and it needs
/// no cargo.
/// </summary>
public sealed class AttemptArtifactContractsTests
{
    readonly ITestOutputHelper _output;

    public AttemptArtifactContractsTests(ITestOutputHelper output) => _output = output;

    sealed record Triple(string Directory, WorkerRequestV1 Request, WorkerResultReportV1 Result, AttemptArtifactCommitV1 Commit);

    // Enumerated once: the corpus is ~1,900 directories and every test needs it.
    static readonly Lazy<IReadOnlyList<string>> Corpus = new(ScanCorpus);

    static IReadOnlyList<string> CorpusDirectories() => Corpus.Value;

    static IReadOnlyList<string> ScanCorpus()
    {
        var root = Path.Combine(TestRegistry.RepoRoot(), "results");
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(root, "attempt_artifact_commit.v1.json", SearchOption.AllDirectories)
            .Select(file => Path.GetDirectoryName(file)!)
            .Where(directory =>
                File.Exists(Path.Combine(directory, "worker_request.v1.json")) &&
                File.Exists(Path.Combine(directory, "worker_result_report.v1.json")))
            .ToArray();
    }

    static Triple Read(string directory) => new(
        directory,
        JsonSerializer.Deserialize<WorkerRequestV1>(
            File.ReadAllText(Path.Combine(directory, "worker_request.v1.json")), AttemptArtifactDigest.Canonical)!,
        JsonSerializer.Deserialize<WorkerResultReportV1>(
            File.ReadAllText(Path.Combine(directory, "worker_result_report.v1.json")), AttemptArtifactDigest.Canonical)!,
        JsonSerializer.Deserialize<AttemptArtifactCommitV1>(
            File.ReadAllText(Path.Combine(directory, "attempt_artifact_commit.v1.json")), AttemptArtifactDigest.Canonical)!);

    /// <summary>
    /// The identity golden. Every committed attempt's stored request_digest and
    /// result_digest must be reproduced from the request and result beside it.
    /// This is what caught the two real rules: a request sorts its outputs by
    /// ArtifactRole declaration order while a result sorts by artifact key as
    /// text, and DeclaredPointDerivationV1 is tagged on "operation" rather than
    /// "kind" — assuming "kind" reproduced only 46% of the corpus.
    /// </summary>
    [Fact]
    public void EveryCommittedAttemptDigestIsReproduced()
    {
        var directories = CorpusDirectories();
        if (directories.Count == 0)
        {
            _output.WriteLine("SKIPPED: no committed attempts under results/; nothing to compare against.");
            return;
        }

        var checkedCount = 0;
        var requestFailures = new List<string>();
        var resultFailures = new List<string>();
        foreach (var directory in directories)
        {
            var triple = Read(directory);
            checkedCount++;
            if (AttemptArtifactDigest.OfRequest(triple.Request) != triple.Commit.RequestDigest)
            {
                requestFailures.Add(directory);
            }

            if (AttemptArtifactDigest.OfResult(triple.Result) != triple.Commit.ResultDigest)
            {
                resultFailures.Add(directory);
            }
        }

        _output.WriteLine($"committed attempts checked: {checkedCount}");
        Assert.True(checkedCount > 0, "the corpus scan found no readable attempt triple");
        Assert.True(
            requestFailures.Count == 0,
            $"{requestFailures.Count}/{checkedCount} request digests differ, first: {requestFailures.FirstOrDefault()}");
        Assert.True(
            resultFailures.Count == 0,
            $"{resultFailures.Count}/{checkedCount} result digests differ, first: {resultFailures.FirstOrDefault()}");
    }

    /// <summary>
    /// The parse/serialize golden. The stored files are key-sorted, two-space
    /// JSON with a trailing newline — a different form from the compact
    /// declaration-order bytes the digest is taken over, which is exactly why
    /// both are pinned. Round-tripping through the typed records must not lose
    /// or invent a field.
    /// </summary>
    [Fact]
    public void EveryCommittedAttemptRoundTripsWithoutLosingAField()
    {
        var directories = CorpusDirectories();
        if (directories.Count == 0)
        {
            _output.WriteLine("SKIPPED: no committed attempts under results/.");
            return;
        }

        var checkedFiles = 0;
        var differences = new List<string>();
        foreach (var directory in directories)
        {
            var triple = Read(directory);
            foreach (var (name, value) in new (string, object)[]
                     {
                         ("worker_request.v1.json", triple.Request),
                         ("worker_result_report.v1.json", triple.Result),
                         ("attempt_artifact_commit.v1.json", triple.Commit),
                     })
            {
                checkedFiles++;
                var stored = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, name)));
                var again = JsonDocument.Parse(
                    JsonSerializer.Serialize(value, value.GetType(), AttemptArtifactDigest.Canonical));
                if (!SameJson(stored.RootElement, again.RootElement))
                {
                    differences.Add(Path.Combine(directory, name));
                }
            }
        }

        _output.WriteLine($"files round-tripped: {checkedFiles}");
        Assert.True(
            differences.Count == 0,
            $"{differences.Count}/{checkedFiles} files did not round-trip, first: {differences.FirstOrDefault()}");
    }

    /// <summary>Order-independent structural comparison: the stored files are
    /// key-sorted and the typed writer emits declaration order, so only the
    /// content may be compared, not the byte sequence.</summary>
    static bool SameJson(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                var l = left.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                var r = right.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                return l.Length == r.Length
                    && l.Zip(r).All(pair => pair.First.Name == pair.Second.Name
                        && SameJson(pair.First.Value, pair.Second.Value));
            case JsonValueKind.Array:
                var la = left.EnumerateArray().ToArray();
                var ra = right.EnumerateArray().ToArray();
                return la.Length == ra.Length && la.Zip(ra).All(pair => SameJson(pair.First, pair.Second));
            default:
                return left.GetRawText() == right.GetRawText();
        }
    }

    static Triple FirstTriple()
    {
        var directories = CorpusDirectories();
        Assert.True(directories.Count > 0, "this test needs one committed attempt under results/");
        return Read(directories[0]);
    }

    /// <summary>
    /// Ports the accept half of Rust
    /// `attempt_commit_is_order_independent_and_rejects_missing_or_unsafe_outputs`:
    /// a real commit validates against its own request and result, and the
    /// commit's identity does not depend on the order its outputs are listed in.
    /// </summary>
    [Fact]
    public void AttemptCommitIsOrderIndependentAndAcceptsARealTriple()
    {
        var triple = FirstTriple();
        Assert.IsType<ContractCheck.Ok>(triple.Commit.ValidateAgainst(triple.Request, triple.Result));

        var reversed = triple.Commit with { Outputs = triple.Commit.Outputs.Reverse().ToArray() };
        Assert.IsType<ContractCheck.Ok>(reversed.ValidateAgainst(triple.Request, triple.Result));

        var reversedResult = triple.Result with { Outputs = triple.Result.Outputs.Reverse().ToArray() };
        Assert.Equal(
            AttemptArtifactDigest.OfResult(triple.Result),
            AttemptArtifactDigest.OfResult(reversedResult));

        var reversedRequest = triple.Request with { Outputs = triple.Request.Outputs.Reverse().ToArray() };
        Assert.Equal(
            AttemptArtifactDigest.OfRequest(triple.Request),
            AttemptArtifactDigest.OfRequest(reversedRequest));
    }

    /// <summary>Ports the refuse half of the same Rust test: a commit that
    /// drops one of the result's outputs must not validate.</summary>
    [Fact]
    public void AttemptCommitRejectsMissingOutputs()
    {
        var triple = FirstTriple();
        var short_ = triple.Commit with { Outputs = triple.Commit.Outputs.Skip(1).ToArray() };
        var refused = Assert.IsType<ContractCheck.Refused>(short_.ValidateAgainst(triple.Request, triple.Result));
        Assert.Equal("attempt_artifact_commit.outputs", refused.Field);
        Assert.Equal("must commit every output in the worker result exactly once", refused.Detail);
    }

    /// <summary>A commit naming the same artifact twice is refused by key,
    /// before any count check.</summary>
    [Fact]
    public void AttemptCommitRejectsADuplicateArtifactKey()
    {
        var triple = FirstTriple();
        var duplicated = triple.Commit with
        {
            Outputs = triple.Commit.Outputs.Concat([triple.Commit.Outputs[0]]).ToArray(),
        };
        var refused = Assert.IsType<ContractCheck.Refused>(duplicated.ValidateAgainst(triple.Request, triple.Result));
        Assert.Equal("attempt_artifact_commit.outputs.artifact_key", refused.Field);
        Assert.Equal("contains a duplicate artifact key", refused.Detail);
    }

    /// <summary>A commit whose stated request digest is not the request's own
    /// digest is refused. This is the check that makes the whole identity port
    /// load-bearing: it is what runs before a handoff directory is deleted.</summary>
    [Fact]
    public void AttemptCommitRejectsAWrongRequestDigest()
    {
        var triple = FirstTriple();
        var tampered = triple.Commit with
        {
            RequestDigest = new ScientificContentDigest("sha256:" + new string('0', 64)),
        };
        var refused = Assert.IsType<ContractCheck.Refused>(tampered.ValidateAgainst(triple.Request, triple.Result));
        Assert.Equal("attempt_artifact_commit.request_digest", refused.Field);
        Assert.Equal("does not match the worker request", refused.Detail);
    }

    /// <summary>Ports Rust `worker_result_must_match_attempt_request_and_required_output_contract`:
    /// a result naming another Attempt is refused before anything else.</summary>
    [Fact]
    public void WorkerResultMustMatchAttemptRequest()
    {
        var triple = FirstTriple();
        var wrongAttempt = triple.Result with { AttemptId = "not-the-attempt" };
        var refused = Assert.IsType<ContractCheck.Refused>(
            AttemptArtifactCommitContract.ValidateResultAgainstRequest(wrongAttempt, triple.Request));
        Assert.Equal("worker_result.attempt_id", refused.Field);

        var wrongRun = triple.Result with { RunHash = "not-the-run" };
        var refusedRun = Assert.IsType<ContractCheck.Refused>(
            AttemptArtifactCommitContract.ValidateResultAgainstRequest(wrongRun, triple.Request));
        Assert.Equal("worker_result.run_hash", refusedRun.Field);
    }

    /// <summary>Ports Rust `worker_result_cannot_relabel_one_artifact_key_as_two_outputs`.</summary>
    [Fact]
    public void WorkerResultCannotRelabelOneArtifactKeyAsTwoOutputs()
    {
        var triple = FirstTriple();
        var first = triple.Result.Outputs[0];
        var second = triple.Result.Outputs[1];
        var relabelled = triple.Result with
        {
            Outputs = triple.Result.Outputs
                .Select(output => output == second ? second with { ArtifactKey = first.ArtifactKey } : output)
                .ToArray(),
        };
        var refused = Assert.IsType<ContractCheck.Refused>(
            AttemptArtifactCommitContract.ValidateResultAgainstRequest(relabelled, triple.Request));
        Assert.Equal("worker_result.outputs.artifact_key", refused.Field);
        Assert.Contains("is reused for more than one output role", refused.Detail, StringComparison.Ordinal);
    }

    /// <summary>Ports Rust `worker_result_refuses_an_output_declared_as_omitted`:
    /// a role the request declared omitted may not appear in the result.</summary>
    [Fact]
    public void WorkerResultRefusesAnOutputDeclaredAsOmitted()
    {
        var triple = FirstTriple();
        var reported = triple.Result.Outputs[0];
        var omitted = triple.Request with
        {
            Outputs = triple.Request.Outputs
                .Select(contract => contract.Role == reported.Role
                    ? contract with { Disposition = new OutputDisposition.Omitted("not produced for this cell") }
                    : contract)
                .ToArray(),
        };
        // Changing the request changes its digest, and Rust checks that first,
        // so the result must name the mutated request to reach the branch
        // under test.
        var rebound = triple.Result with { RequestDigest = AttemptArtifactDigest.OfRequest(omitted) };
        var refused = Assert.IsType<ContractCheck.Refused>(
            AttemptArtifactCommitContract.ValidateResultAgainstRequest(rebound, omitted));
        Assert.Equal("worker_result.outputs", refused.Field);
        Assert.Contains("was declared omitted but the result contains it", refused.Detail, StringComparison.Ordinal);
    }

    /// <summary>Ports Rust `worker_request_json_rejects_capability_secrets_and_unknown_fields`
    /// for the read side: serde's deny_unknown_fields, which STJ does not do by
    /// default. A record carrying a field this port does not model must refuse
    /// rather than parse and then digest to something else.</summary>
    [Fact]
    public void WorkerRequestJsonRejectsUnknownFields()
    {
        var triple = FirstTriple();
        var withExtra = JsonSerializer.Serialize(triple.Request, AttemptArtifactDigest.Canonical)
            .TrimEnd('}') + ",\"capability_secret\":\"leaked\"}";
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<WorkerRequestV1>(withExtra, AttemptArtifactDigest.Canonical));
    }

    /// <summary>A result reporting a role the request never declared is refused
    /// by role name, in the wire spelling Rust used.</summary>
    [Fact]
    public void WorkerResultRefusesAnUndeclaredRole()
    {
        var triple = FirstTriple();
        var undeclared = triple.Result.Outputs[0] with { Role = ArtifactRole.WorkerLog };
        var report = triple.Result with { Outputs = [undeclared] };
        var refused = Assert.IsType<ContractCheck.Refused>(
            AttemptArtifactCommitContract.ValidateResultAgainstRequest(report, triple.Request));
        Assert.Equal("worker_result.outputs", refused.Field);
        Assert.Equal("worker_log was not declared by the request", refused.Detail);
    }

    // ------------------------------------------- attempt metadata projection

    /// <summary>Ports Rust `census_counts_fifteen_metadata_files`: fifteen
    /// entries, no duplicate path, every one classifiable, and exactly two Home
    /// intents and two runner claims — the pairs are the worker and Stage
    /// spellings of the same two responsibilities.</summary>
    [Fact]
    public void CensusCountsFifteenMetadataFiles()
    {
        Assert.Equal(15, AttemptMetadata.Census.Count);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in AttemptMetadata.Census)
        {
            Assert.True(paths.Add(entry.RelativePath), entry.RelativePath);
            Assert.IsType<AttemptMetadata.Classification.Classified>(
                AttemptMetadata.Classify(entry.RelativePath));
        }

        Assert.Equal(2, AttemptMetadata.Census.Count(entry => entry.Class == AttemptJsonClass.HomeIntent));
        Assert.Equal(2, AttemptMetadata.Census.Count(entry => entry.Class == AttemptJsonClass.RunnerClaim));
    }

    /// <summary>Ports Rust `unknown_sidecar_is_refused`.</summary>
    [Fact]
    public void UnknownSidecarIsRefused()
    {
        var refused = Assert.IsType<AttemptMetadata.Classification.Refused>(
            AttemptMetadata.Classify("authority.json"));
        Assert.Contains("refused", refused.Detail, StringComparison.Ordinal);
        Assert.IsType<AttemptMetadata.Classification.Classified>(
            AttemptMetadata.Classify("inputs/training_worker_request.json"));
        Assert.IsType<AttemptMetadata.Classification.Classified>(
            AttemptMetadata.Classify("outputs/metrics.json"));
    }

    static ObservedAttemptJson Observed(string path, string? schema, string digest, string? body) =>
        new(path, schema, digest, body is null ? null : JsonDocument.Parse(body).RootElement.Clone());

    /// <summary>Ports Rust `projection_keeps_one_request_one_report_and_object_digests`,
    /// including its last assertion: the projection references objects by
    /// digest and must not copy a handler name into itself.</summary>
    [Fact]
    public void ProjectionKeepsOneRequestOneReportAndObjectDigests()
    {
        var files = new[]
        {
            Observed("request/stage_runner_request.v1.json", "fern.stage_runner_request.v1",
                "sha256:" + new string('a', 64),
                "{\"schema\":\"fern.stage_runner_request.v1\",\"stage_attempt_id\":\"attempt-1\","
                + "\"handler\":\"fern.train.current\","
                + "\"implementation_digest\":\"sha256:" + new string('b', 64) + "\","
                + "\"invocation\":{\"plan_digest\":\"sha256:" + new string('c', 64) + "\"}}"),
            Observed("report/stage_runner_report.v1.json", "fern.stage_runner_report.v1",
                "sha256:" + new string('d', 64),
                "{\"schema\":\"fern.stage_runner_report.v1\",\"stage_attempt_id\":\"attempt-1\"}"),
            Observed("remote_attempt_instructions.v1.json", "fern.remote_attempt_instructions.v1",
                "sha256:" + new string('e', 64), null),
            Observed("attempt_artifact_commit.v1.json", "fern.attempt_artifact_commit.v1",
                "sha256:" + new string('f', 64), null),
        };

        var authority = new AttemptCheckedAuthorityV1(
            ReceiptDigest: "sha256:" + new string('1', 64),
            StagePlanDigest: "sha256:" + new string('c', 64),
            ImplementationDigest: "sha256:" + new string('b', 64));
        var projected = Assert.IsType<AttemptMetadata.ProjectionResult.Projected>(
            AttemptMetadata.Project(files, authority)).Projection;

        Assert.Equal("attempt-1", projected.AttemptId);
        Assert.Equal("request/stage_runner_request.v1.json", projected.HomeIntent!.RelativePath);
        Assert.Equal("report/stage_runner_report.v1.json", projected.RunnerClaim!.RelativePath);
        Assert.Single(projected.DerivedTransport);
        Assert.Contains(projected.Objects, o => o.Kind == "fern.stage_plan.v1");
        Assert.NotNull(projected.CheckedAuthority.ReceiptDigest);

        var rendered = JsonSerializer.Serialize(projected);
        Assert.DoesNotContain("fern.train.current", rendered, StringComparison.Ordinal);
    }

    /// <summary>Ports Rust `two_home_intents_are_refused`: the duplicate
    /// authority the whole census exists to prevent.</summary>
    [Fact]
    public void TwoHomeIntentsAreRefused()
    {
        var files = new[]
        {
            Observed("worker_request.v1.json", null, "sha256:" + new string('a', 64), null),
            Observed("request/stage_runner_request.v1.json", null, "sha256:" + new string('b', 64), null),
        };
        var refused = Assert.IsType<AttemptMetadata.ProjectionResult.Refused>(
            AttemptMetadata.Project(files, new AttemptCheckedAuthorityV1()));
        Assert.Contains("exactly one Home intent", refused.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The corpus anchor. Every committed attempt must project with exactly one
    /// Home intent and one runner claim, its attempt_id must be the directory
    /// it lives in, and every content digest must be the plain sha256 of the
    /// bytes on disk — hashed here by the framework, not by the port, so this
    /// is not the port agreeing with itself.
    /// </summary>
    [Fact]
    public void EveryCommittedAttemptProjectsOneIntentOneClaimAndHonestDigests()
    {
        var directories = CorpusDirectories();
        if (directories.Count == 0)
        {
            _output.WriteLine("SKIPPED: no committed attempts under results/.");
            return;
        }

        var checkedCount = 0;
        foreach (var directory in directories)
        {
            var files = new List<ObservedAttemptJson>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
            {
                var bytes = File.ReadAllBytes(path);
                var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                using var document = JsonDocument.Parse(bytes);
                var schema = document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("schema", out var name)
                    && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
                files.Add(new ObservedAttemptJson(
                    relative,
                    schema,
                    "sha256:" + Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
                    document.RootElement.Clone()));
            }

            var projected = Assert.IsType<AttemptMetadata.ProjectionResult.Projected>(
                AttemptMetadata.Project(files, new AttemptCheckedAuthorityV1())).Projection;
            Assert.NotNull(projected.HomeIntent);
            Assert.NotNull(projected.RunnerClaim);
            Assert.Equal(Path.GetFileName(directory), projected.AttemptId);
            Assert.Equal(AttemptMetadata.ProjectionSchema, projected.Schema);
            checkedCount++;
        }

        _output.WriteLine($"attempt directories projected: {checkedCount}");
        Assert.True(checkedCount > 0);
    }
}
