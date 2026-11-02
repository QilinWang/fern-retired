using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fern.Registry;

/// <summary>
/// The current model-environment admission for one Worker Site. Install is the
/// producer. Compile is the consumer. A JSON file is a copy of this row.
/// </summary>
public sealed record SiteEnvironmentAdmission(
    string SiteId,
    string EnvironmentIdentity,
    string ExecutionProfileRevision,
    string Interpreter,
    string EnvironmentKind,
    IReadOnlyList<string> Models,
    string Outcome,
    string ObservedEnvironmentDigest,
    string? DeviceId,
    string ReceiptJson,
    long RecordedAtMs,
    string RecordedBy,
    string? MachineGeneration = null,
    string? EndpointSessionId = null,
    string? InstallAttemptId = null,
    string? BeforeEffectJson = null,
    string? AfterEffectJson = null,
    string? MachineAdmissionIdentity = null,
    long? MachineAdmissionRecordedAtMs = null);

public sealed record SiteEnvironmentAdmissionWrite(
    string SiteId,
    string EnvironmentIdentity,
    string ExecutionProfileRevision,
    string Interpreter,
    string EnvironmentKind,
    IReadOnlyList<string> Models,
    string Outcome,
    string ObservedEnvironmentDigest,
    string? DeviceId,
    string ReceiptJson,
    string RecordedBy,
    string? MachineGeneration = null,
    string? EndpointSessionId = null,
    string? InstallAttemptId = null,
    string? BeforeEffectJson = null,
    string? AfterEffectJson = null,
    string? MachineAdmissionIdentity = null,
    long? MachineAdmissionRecordedAtMs = null);

public sealed record SitePlacementFacts(
    string? DeviceId,
    string? MachineGeneration,
    string? Hostname);

public sealed record SiteEnvironmentAdmissionRecord(
    string Status,
    string SiteId,
    string EnvironmentIdentity,
    string? TransitionReceiptId);

public static class SiteEnvironmentAdmissionAuthority
{
    public const string ObjectKind = "csharp:site-environment-admission";
    public const string PolicyRevision = "fern.site_environment_admission.v1";

    /// <summary>
    /// Occasion metadata inside an installation receipt: which attempt and
    /// lease performed the run, never what was proved. Excluded from the
    /// content classification so two identical proofs from two different
    /// attempts replay as one.
    /// </summary>
    static readonly IReadOnlySet<string> AdmissionOccasionFields =
        ReceiptSemanticContent.ObservationMetadataFields
            .Concat(["install_lease_id", "fencing_token", "owner_attempt_id"])
            .ToHashSet(StringComparer.Ordinal);


    /// <summary>The current environment admission of the Site named, from its row. CHG 2026-09-11 night (slice 9): was the site_environment_admissions table.</summary>
    public static SiteEnvironmentAdmission? Current(SqliteConnection connection, string siteId) =>
        SiteAdmissions.Read<SiteEnvironmentAdmission>(connection, siteId, SiteAdmissions.EnvironmentKey);

    /// <summary>
    /// Where the Site sits: its card, machine generation and host, from the
    /// machine admission on its row, the card falling back to the first one the
    /// Site declares. CHG 2026-09-11 night (slice 9): was worker_sites and
    /// worker_site_capabilities.
    /// </summary>
    public static SitePlacementFacts? RegisteredPlacement(SqliteConnection connection, string siteId)
    {
        var capabilities = SiteAdmissions.Capabilities(connection, siteId);
        if (capabilities is null)
        {
            return null;
        }
        var machine = MachineAdmissionAuthority.Current(connection, siteId);
        var cards = SiteAdmissions.DeclaredCards(capabilities);
        return new SitePlacementFacts(machine?.DeviceId ?? (cards.Count > 0 ? cards[0] : null), machine?.MachineGeneration, machine?.Hostname);
    }

    public static string? RegisteredDeviceId(SqliteConnection connection, string siteId) =>
        RegisteredPlacement(connection, siteId)?.DeviceId;


    public static RegistryResult<SiteEnvironmentAdmissionRecord> Record(
        RegistryWriter writer,
        SiteEnvironmentAdmissionWrite request)
    {
        if (string.IsNullOrWhiteSpace(request.SiteId)
            || string.IsNullOrWhiteSpace(request.EnvironmentIdentity)
            || string.IsNullOrWhiteSpace(request.ExecutionProfileRevision)
            || string.IsNullOrWhiteSpace(request.Interpreter)
            || string.IsNullOrWhiteSpace(request.EnvironmentKind)
            || string.IsNullOrWhiteSpace(request.Outcome)
            || string.IsNullOrWhiteSpace(request.ObservedEnvironmentDigest)
            || string.IsNullOrWhiteSpace(request.ReceiptJson)
            || string.IsNullOrWhiteSpace(request.RecordedBy))
        {
            return Reject("recording an environment admission requires site, identity, profile, interpreter, kind, outcome, digest, receipt, and actor");
        }

        if (request.Outcome is not ("installed" or "already_ready" or "repaired_environment"))
        {
            return Reject($"environment admission outcome {request.Outcome} is not a ready admission");
        }

        if (request.Models is null || request.Models.Count == 0)
        {
            return Reject("an environment admission names the closed model set it checked");
        }

        if (string.IsNullOrWhiteSpace(request.MachineAdmissionIdentity)
            || !request.MachineAdmissionIdentity.StartsWith("sha256:", StringComparison.Ordinal)
            || request.MachineAdmissionRecordedAtMs is null or <= 0)
        {
            return Reject(
                "recording an environment admission requires the current MachineAdmission identity and recorded_at_ms; "
                + "run verify-machine-admission before site install-model-environment");
        }

        return writer.WriteImmediate(connection => RecordInTransaction(connection, request));
    }

    static RegistryResult<SiteEnvironmentAdmissionRecord> RecordInTransaction(
        SqliteConnection connection,
        SiteEnvironmentAdmissionWrite request)
    {
        var parentProblem = ValidateMachineAdmissionParent(connection, request);
        if (parentProblem is not null)
        {
            return Reject(parentProblem);
        }

        var nowMs = DatabaseNowMs(connection);
        if (!SiteAdmissions.SiteExists(connection, request.SiteId))
        {
            return Reject($"Site {request.SiteId} is not registered in this Home; register-site mints it from the execution profile");
        }
        var previous = Current(connection, request.SiteId);
        var previousIdentity = previous?.EnvironmentIdentity;
        var previousReceiptJson = previous?.ReceiptJson;

        var contentUpdate = false;
        if (previousIdentity == request.EnvironmentIdentity && previousReceiptJson is not null)
        {
            // The same identity is NOT the same receipt: the identity
            // deliberately excludes outcome, the asset manifest, and effect
            // evidence, so an asset repair or a re-observation with new proof
            // content shares the identity of the admission it refreshes.
            // Classify by receipt CONTENT -- the semantic digest over proof
            // fields with the occasion fields excluded -- so an exact replay
            // is a no-op while changed content updates the durable row
            // instead of being silently dropped.
            // CHG 2026-09-26: the two canonical texts are compared, not their digests.
            try
            {
                var verdict = ReceiptSemanticContent.Classify(
                    ReceiptSemanticContent.Canonical(previousReceiptJson, AdmissionOccasionFields),
                    ReceiptSemanticContent.Canonical(request.ReceiptJson, AdmissionOccasionFields));
                if (verdict == ReceiptReplayVerdict.IdempotentReplay)
                {
                    return RegistryResult<SiteEnvironmentAdmissionRecord>.Ok(
                        new SiteEnvironmentAdmissionRecord("replayed", request.SiteId, request.EnvironmentIdentity, null));
                }
            }
            catch (Exception error) when (error is JsonException or ArgumentException)
            {
                // An unreadable stored receipt must not block the fresh one.
            }
            contentUpdate = true;
        }

        // CHG 2026-09-11 night (slice 9): the admission goes on the Site's row,
        // and the models it admits widen what the Site may run, which is what
        // the placement reads.
        SiteAdmissions.Write(
            connection, request.SiteId, SiteAdmissions.EnvironmentKey,
            new SiteEnvironmentAdmission(
                request.SiteId, request.EnvironmentIdentity, request.ExecutionProfileRevision, request.Interpreter,
                request.EnvironmentKind, request.Models, request.Outcome, request.ObservedEnvironmentDigest,
                request.DeviceId, request.ReceiptJson, nowMs, request.RecordedBy, request.MachineGeneration,
                request.EndpointSessionId, request.InstallAttemptId, request.BeforeEffectJson, request.AfterEffectJson,
                request.MachineAdmissionIdentity, request.MachineAdmissionRecordedAtMs),
            capabilities =>
            {
                var admitted = new SortedSet<string>(StringComparer.Ordinal);
                if (capabilities["admitted_models"] is System.Text.Json.Nodes.JsonArray declared)
                {
                    foreach (var model in declared)
                    {
                        if (model?.GetValue<string>() is { Length: > 0 } name) admitted.Add(name);
                    }
                }
                foreach (var model in request.Models) admitted.Add(model);
                capabilities["admitted_models"] = new System.Text.Json.Nodes.JsonArray(admitted.Select(model => (System.Text.Json.Nodes.JsonNode?)model).ToArray());
            });

        var receiptId = AppendReceipt(
            connection,
            request.SiteId,
            request.RecordedBy,
            previousIdentity is null
                ? "site-environment admitted"
                : contentUpdate
                    ? "site-environment admission content updated"
                    : "site-environment admission replaced",
            previousIdentity,
            request.EnvironmentIdentity,
            nowMs);
        return RegistryResult<SiteEnvironmentAdmissionRecord>.Ok(
            new SiteEnvironmentAdmissionRecord(
                previousIdentity is null ? "admitted" : contentUpdate ? "updated" : "replaced",
                request.SiteId,
                request.EnvironmentIdentity,
                receiptId));
    }


    static string? ValidateMachineAdmissionParent(
        SqliteConnection connection,
        SiteEnvironmentAdmissionWrite request)
    {
        var parent = MachineAdmissionAuthority.Current(connection, request.SiteId);
        if (parent is null)
        {
            return $"Site {request.SiteId} has no current MachineAdmission; run verify-machine-admission --on "
                + $"{request.SiteId} before site install-model-environment. Missing machine proof is not "
                + "permission to install or repair a model environment";
        }

        if (!string.Equals(
                parent.MachineAdmissionIdentity,
                request.MachineAdmissionIdentity,
                StringComparison.Ordinal))
        {
            return "environment admission MachineAdmission parent "
                + request.MachineAdmissionIdentity
                + " is not the current MachineAdmission "
                + parent.MachineAdmissionIdentity
                + $" for Site {request.SiteId}";
        }

        if (!string.Equals(parent.PythonExecutable, request.Interpreter, StringComparison.Ordinal))
        {
            return "environment admission interpreter "
                + request.Interpreter
                + " is not the MachineAdmission interpreter "
                + parent.PythonExecutable
                + $" for Site {request.SiteId}";
        }

        if (!string.Equals(parent.MachineGeneration, request.MachineGeneration, StringComparison.Ordinal))
        {
            return "environment admission machine generation "
                + (request.MachineGeneration ?? "(none)")
                + " is not the MachineAdmission generation "
                + parent.MachineGeneration
                + $" for Site {request.SiteId}";
        }

        if (parent.Device == "cuda")
        {
            if (string.IsNullOrWhiteSpace(request.DeviceId))
            {
                return "a CUDA environment admission must name the MachineAdmission GPU UUID";
            }

            if (!string.Equals(parent.DeviceId, request.DeviceId, StringComparison.Ordinal))
            {
                return "environment admission GPU "
                    + request.DeviceId
                    + " is not the MachineAdmission GPU "
                    + parent.DeviceId
                    + $" for Site {request.SiteId}";
            }
        }

        if (request.MachineAdmissionRecordedAtMs != parent.RecordedAtMs)
        {
            return "environment admission consumed MachineAdmission recorded_at_ms "
                + request.MachineAdmissionRecordedAtMs
                + " but the current MachineAdmission recorded_at_ms is "
                + parent.RecordedAtMs
                + $" for Site {request.SiteId}";
        }

        return ValidateReceiptParent(request, parent);
    }

    static string? ValidateReceiptParent(
        SiteEnvironmentAdmissionWrite request,
        MachineAdmission parent)
    {
        try
        {
            using var document = JsonDocument.Parse(request.ReceiptJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schema", out var schema)
                || schema.GetString() != "fern.model_environment_installation.v1")
            {
                return "environment admission receipt must be fern.model_environment_installation.v1";
            }

            if (!root.TryGetProperty("machine_admission_identity", out var parentField)
                || parentField.GetString() != parent.MachineAdmissionIdentity)
            {
                return "environment admission receipt does not name the consumed current MachineAdmission";
            }

            if (root.TryGetProperty("site_id", out var siteField)
                && siteField.GetString() != request.SiteId)
            {
                return "environment admission receipt site_id does not match the Registry row";
            }

            if (root.TryGetProperty("environment_identity", out var identityField)
                && identityField.GetString() != request.EnvironmentIdentity)
            {
                return "environment admission receipt environment_identity does not match the Registry row";
            }

            if (root.TryGetProperty("machine_generation", out var generationField)
                && generationField.GetString() != parent.MachineGeneration)
            {
                return "environment admission receipt machine_generation does not match the consumed MachineAdmission";
            }

            if (root.TryGetProperty("device_id", out var deviceField)
                && parent.Device == "cuda"
                && deviceField.GetString() != parent.DeviceId)
            {
                return "environment admission receipt device_id does not match the consumed MachineAdmission";
            }
        }
        catch (JsonException error)
        {
            return "environment admission receipt JSON is invalid: " + error.Message;
        }

        return null;
    }

    static string AppendReceipt(
        SqliteConnection connection,
        string siteId,
        string actorId,
        string operation,
        string? prior,
        string next,
        long nowMs)
    {
        var written = Journals.AppendTransitionReceiptInOpenTransaction(
            connection,
            new TransitionReceiptWrite(
                OperationName: operation,
                ObjectKind: ObjectKind,
                ObjectId: siteId,
                PriorState: prior,
                NextState: next,
                ActorKind: "cli",
                ActorId: actorId,
                InputIdentityJson: JsonSerializer.Serialize(new { site_id = siteId }),
                OutputIdentityJson: JsonSerializer.Serialize(
                    new
                    {
                        site_id = siteId,
                        environment_identity = next,
                        machine_admission_identity = MachineAdmissionAuthority.Current(connection, siteId)
                            ?.MachineAdmissionIdentity,
                    }),
                PolicyRevision: PolicyRevision,
                CorrelationId: siteId,
                OccurredAtMs: nowMs));
        if (written.TryGetValue(out var receiptId))
        {
            return receiptId;
        }

        if (written.TryGetRefusal(out var refusal))
        {
            throw new RegistryFault(refusal.Detail);
        }

        throw new RegistryFault($"cannot append {operation} receipt");
    }

    static long DatabaseNowMs(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(unixepoch('subsec') * 1000 AS INTEGER)";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static RegistryResult<SiteEnvironmentAdmissionRecord> Reject(string detail) =>
        RegistryResult<SiteEnvironmentAdmissionRecord>.Rejected(
            new RegistryRefusal("refused", "invalid_request", detail));
}
