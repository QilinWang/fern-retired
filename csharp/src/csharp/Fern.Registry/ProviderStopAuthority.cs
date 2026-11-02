using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fern.Registry;

/// <summary>The closed provider set the Registry can name for a lifecycle request.</summary>
public enum ProviderKind
{
    Runpod,
}

/// <summary>
/// The desired state is a provider fact, not a Fern Site, container, or child
/// process state. Its SQL spelling intentionally matches the provider wire.
/// </summary>
public enum ProviderDesiredStatus
{
    Exited,
}

/// <summary>
/// <see cref="Prepared"/> is safe to submit, <see cref="NetworkUnknown"/>
/// means the provider action may or may not have happened, and
/// <see cref="ExpiredWithoutSubmission"/> proves the database deadline passed
/// before Fern started a provider mutation, and
/// <see cref="Terminal"/> means the matching typed provider response is
/// durably recorded. No SSH or process observation may change this state.
/// </summary>
public enum ProviderStopState
{
    Prepared,
    NetworkUnknown,
    ExpiredWithoutSubmission,
    Terminal,
}

public sealed record ProviderLifecycleActor(string Kind, string ActorId);

/// <summary>
/// The authority request written before contacting a provider. The deadline is
/// a duration so an idempotent replay can compare it exactly; the resulting
/// absolute deadline is what the network caller obeys.
/// </summary>
public sealed record PrepareProviderStopWrite(
    ProviderKind ProviderKind,
    string ProviderPodId,
    string SiteId,
    ProviderDesiredStatus DesiredStatus,
    ProviderLifecycleActor Actor,
    string IdempotencyKey,
    long DeadlineDurationMs);

public sealed record ProviderStopRequest(
    string ProviderStopRequestId,
    ProviderKind ProviderKind,
    string ProviderPodId,
    string SiteId,
    string SiteProfileRevision,
    ProviderDesiredStatus DesiredStatus,
    ProviderLifecycleActor Actor,
    string IdempotencyKey,
    long DeadlineDurationMs,
    long DeadlineAtMs,
    ProviderStopState State,
    string? NetworkUnknownDetail,
    long? NetworkUnknownAtMs,
    string? ExpiredWithoutSubmissionDetail,
    long? ExpiredWithoutSubmissionAtMs,
    string? TerminalReceiptJson,
    long? TerminalReceivedAtMs);

/// <summary>
/// A response reduced to the exact provider facts this authority is allowed to
/// accept. The HTTP client must parse this from the provider's JSON; callers
/// cannot manufacture a terminal result from an SSH or child-process exit.
/// </summary>
public sealed record ProviderTerminalResponse(
    string ProviderPodId,
    ProviderDesiredStatus DesiredStatus);

public sealed record RecordProviderStopTerminalWrite(
    string ProviderStopRequestId,
    ProviderLifecycleActor Actor,
    ProviderTerminalResponse Response);

public sealed record MarkProviderStopNetworkUnknownWrite(
    string ProviderStopRequestId,
    ProviderLifecycleActor Actor,
    string Detail);

/// <summary>
/// Records that the original submission window elapsed before a provider
/// request began. This is intentionally not a network-unknown transition:
/// the latter means a request may have reached RunPod and must retain the Pod
/// lock for reconciliation.
/// </summary>
public sealed record ExpireProviderStopWithoutSubmissionWrite(
    string ProviderStopRequestId,
    ProviderLifecycleActor Actor,
    string Detail);

public sealed record ProviderStopLookup(
    ProviderKind ProviderKind,
    ProviderLifecycleActor Actor,
    string IdempotencyKey);

/// <summary>
/// The sole Registry writer for an external provider stop. It deliberately
/// stops at preparing, recording unknown transport, and accepting an already
/// typed provider response: HTTP remains a bounded CLI transport boundary.
/// This prevents a provider token or a remote process observation from entering
/// Registry authority.
/// </summary>
public static class ProviderStopAuthority
{
    public const string ObjectKind = "provider-stop-request";
    public const string PolicyRevision = "fern.provider_stop.v1";

    const long MinimumDeadlineMs = 1_000;
    const long MaximumDeadlineMs = 300_000;

    public static RegistryResult<ProviderStopRequest> Prepare(
        RegistryWriter writer,
        PrepareProviderStopWrite request)
    {
        var refusal = ValidatePreparation(request);
        if (refusal is not null)
        {
            return RegistryResult<ProviderStopRequest>.Rejected(refusal.Value);
        }

        return writer.WriteImmediate(connection => PrepareInTransaction(connection, request));
    }

    public static RegistryResult<ProviderStopRequest> MarkNetworkUnknown(
        RegistryWriter writer,
        MarkProviderStopNetworkUnknownWrite request)
    {
        var refusal = ValidateUnknown(request);
        if (refusal is not null)
        {
            return RegistryResult<ProviderStopRequest>.Rejected(refusal.Value);
        }

        return writer.WriteImmediate(connection => MarkUnknownInTransaction(connection, request));
    }

    /// <summary>
    /// Close a prepared request only when Registry's own clock confirms its
    /// immutable submission deadline is past. This has no provider transport
    /// side effect and releases the active-Pod lock for a new authorization.
    /// </summary>
    public static RegistryResult<ProviderStopRequest> ExpireWithoutSubmission(
        RegistryWriter writer,
        ExpireProviderStopWithoutSubmissionWrite request)
    {
        var refusal = ValidateNoSubmissionExpiry(request);
        if (refusal is not null)
        {
            return RegistryResult<ProviderStopRequest>.Rejected(refusal.Value);
        }

        return writer.WriteImmediate(connection => ExpireWithoutSubmissionInTransaction(connection, request));
    }

    public static RegistryResult<ProviderStopRequest> RecordTerminal(
        RegistryWriter writer,
        RecordProviderStopTerminalWrite request)
    {
        var refusal = ValidateTerminal(request);
        if (refusal is not null)
        {
            return RegistryResult<ProviderStopRequest>.Rejected(refusal.Value);
        }

        return writer.WriteImmediate(connection => RecordTerminalInTransaction(connection, request));
    }

    /// <summary>
    /// Reconciliation starts from the immutable request the Registry already
    /// accepted. It never accepts a caller-supplied replacement Pod id.
    /// </summary>
    public static RegistryResult<ProviderStopRequest> LoadForReconcile(
        SqliteConnection connection,
        ProviderStopLookup lookup)
    {
        var refusal = ValidateLookup(lookup);
        if (refusal is not null)
        {
            return RegistryResult<ProviderStopRequest>.Rejected(refusal.Value);
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT provider_stop_request_id, provider_kind, provider_pod_id, site_id,
                   site_profile_revision, desired_status, actor_kind, actor_id,
                   idempotency_key, deadline_duration_ms, deadline_at_ms, state,
                   network_unknown_detail, network_unknown_at_ms,
                   expired_without_submission_detail, expired_without_submission_at_ms,
                   terminal_receipt_json, terminal_received_at_ms
            FROM provider_stop_requests
            WHERE provider_kind = $provider AND actor_id = $actor AND idempotency_key = $key
            """;
        command.Parameters.AddWithValue("$provider", ProviderKindWire(lookup.ProviderKind));
        command.Parameters.AddWithValue("$actor", lookup.Actor.ActorId);
        command.Parameters.AddWithValue("$key", lookup.IdempotencyKey);
        using var row = command.ExecuteReader();
        if (!row.Read())
        {
            return Reject("unknown_request",
                $"no {ProviderKindWire(lookup.ProviderKind)} provider stop request exists for actor {lookup.Actor.ActorId} and idempotency key {lookup.IdempotencyKey}");
        }

        var result = ReadRequest(row);
        if (!string.Equals(result.Actor.Kind, lookup.Actor.Kind, StringComparison.Ordinal))
        {
            return Reject("actor_mismatch",
                $"provider stop request {result.ProviderStopRequestId} belongs to actor kind {result.Actor.Kind}, not {lookup.Actor.Kind}");
        }

        return RegistryResult<ProviderStopRequest>.Ok(result);
    }

    static RegistryResult<ProviderStopRequest> PrepareInTransaction(
        SqliteConnection connection,
        PrepareProviderStopWrite request)
    {
        var existing = ReadByIdempotency(connection, request.ProviderKind, request.Actor.ActorId, request.IdempotencyKey);
        if (existing is not null)
        {
            if (!SamePreparation(existing, request))
            {
                return Reject("idempotency_key_conflict",
                    $"idempotency key {request.IdempotencyKey} was already used for a different provider stop request");
            }

            if (!string.Equals(existing.Actor.Kind, request.Actor.Kind, StringComparison.Ordinal))
            {
                return Reject("actor_mismatch",
                    $"provider stop request {existing.ProviderStopRequestId} belongs to actor kind {existing.Actor.Kind}, not {request.Actor.Kind}");
            }

            return RegistryResult<ProviderStopRequest>.Ok(existing);
        }

        var site = ReadSite(connection, request.SiteId);
        if (site is null)
        {
            return Reject("unknown_site", $"Worker Site {request.SiteId} is not registered in this Registry Home");
        }

        if (!string.Equals(site.Value.Executor, "ssh", StringComparison.Ordinal))
        {
            return Reject("site_not_provider_backed",
                $"Worker Site {request.SiteId} uses {site.Value.Executor}; a RunPod stop request requires the separately registered SSH Site");
        }

        var activeForPod = ReadActiveByProviderPod(connection, request.ProviderKind, request.ProviderPodId);
        if (activeForPod is not null)
        {
            var instruction = activeForPod.State == ProviderStopState.NetworkUnknown
                ? "reconcile its stored Pod id before another provider mutation"
                : "replay that idempotency key until it reaches terminal, network_unknown, or no-submission expiry";
            return Reject("provider_pod_stop_already_active",
                $"provider Pod {request.ProviderPodId} already has active stop request {activeForPod.ProviderStopRequestId} in state {StateWire(activeForPod.State)}; {instruction}");
        }

        var nowMs = DatabaseNowMs(connection);
        var requestId = "provider-stop-" + Guid.NewGuid().ToString("N");
        var deadlineAtMs = checked(nowMs + request.DeadlineDurationMs);
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT INTO provider_stop_requests (
                    provider_stop_request_id, provider_kind, provider_pod_id, site_id,
                    site_profile_revision, desired_status, actor_kind, actor_id,
                    idempotency_key, deadline_duration_ms, deadline_at_ms, state,
                    network_unknown_detail, network_unknown_at_ms,
                    expired_without_submission_detail, expired_without_submission_at_ms,
                    terminal_receipt_json, terminal_received_at_ms, created_at_ms, updated_at_ms
                ) VALUES (
                    $id, $provider, $pod, $site,
                    $revision, $desired, $actorKind, $actor,
                    $key, $duration, $deadline, 'prepared',
                    NULL, NULL, NULL, NULL, NULL, NULL, $now, $now
                )
                """;
            insert.Parameters.AddWithValue("$id", requestId);
            insert.Parameters.AddWithValue("$provider", ProviderKindWire(request.ProviderKind));
            insert.Parameters.AddWithValue("$pod", request.ProviderPodId);
            insert.Parameters.AddWithValue("$site", request.SiteId);
            insert.Parameters.AddWithValue("$revision", site.Value.ProfileRevision);
            insert.Parameters.AddWithValue("$desired", DesiredStatusWire(request.DesiredStatus));
            insert.Parameters.AddWithValue("$actorKind", request.Actor.Kind);
            insert.Parameters.AddWithValue("$actor", request.Actor.ActorId);
            insert.Parameters.AddWithValue("$key", request.IdempotencyKey);
            insert.Parameters.AddWithValue("$duration", request.DeadlineDurationMs);
            insert.Parameters.AddWithValue("$deadline", deadlineAtMs);
            insert.Parameters.AddWithValue("$now", nowMs);
            insert.ExecuteNonQuery();
        }

        var prepared = new ProviderStopRequest(
            requestId,
            request.ProviderKind,
            request.ProviderPodId,
            request.SiteId,
            site.Value.ProfileRevision,
            request.DesiredStatus,
            request.Actor,
            request.IdempotencyKey,
            request.DeadlineDurationMs,
            deadlineAtMs,
            ProviderStopState.Prepared,
            null,
            null,
            null,
            null,
            null,
            null);
        AppendReceipt(
            connection,
            prepared,
            "prepare-provider-stop",
            prior: null,
            next: ProviderStopState.Prepared,
            input: RegistryJson.Serialize(request),
            output: RegistryJson.Serialize(prepared),
            nowMs);
        return RegistryResult<ProviderStopRequest>.Ok(prepared);
    }

    static RegistryResult<ProviderStopRequest> MarkUnknownInTransaction(
        SqliteConnection connection,
        MarkProviderStopNetworkUnknownWrite request)
    {
        var stored = ReadById(connection, request.ProviderStopRequestId);
        if (stored is null)
        {
            return Reject("unknown_request", $"provider stop request {request.ProviderStopRequestId} does not exist");
        }

        var actorMismatch = RequireActor(stored, request.Actor);
        if (actorMismatch is not null)
        {
            return RegistryResult<ProviderStopRequest>.Rejected(actorMismatch.Value);
        }

        if (stored.State == ProviderStopState.Terminal)
        {
            return RegistryResult<ProviderStopRequest>.Ok(stored);
        }

        if (stored.State == ProviderStopState.NetworkUnknown)
        {
            return string.Equals(stored.NetworkUnknownDetail, request.Detail, StringComparison.Ordinal)
                ? RegistryResult<ProviderStopRequest>.Ok(stored)
                : Reject("unknown_outcome_already_recorded",
                    $"provider stop request {stored.ProviderStopRequestId} already records a different network-unknown detail; reconcile its stored Pod id");
        }

        if (stored.State == ProviderStopState.ExpiredWithoutSubmission)
        {
            return Reject("submission_never_started",
                $"provider stop request {stored.ProviderStopRequestId} expired before provider submission and cannot become network-unknown");
        }

        var nowMs = DatabaseNowMs(connection);
        using (var update = connection.CreateCommand())
        {
            update.CommandText =
                """
                UPDATE provider_stop_requests
                SET state = 'network_unknown', network_unknown_detail = $detail,
                    network_unknown_at_ms = $now, updated_at_ms = $now
                WHERE provider_stop_request_id = $id AND state = 'prepared'
                """;
            update.Parameters.AddWithValue("$detail", request.Detail);
            update.Parameters.AddWithValue("$now", nowMs);
            update.Parameters.AddWithValue("$id", stored.ProviderStopRequestId);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new RegistryFault($"provider stop request {stored.ProviderStopRequestId} changed while recording network unknown");
            }
        }

        var unknown = stored with
        {
            State = ProviderStopState.NetworkUnknown,
            NetworkUnknownDetail = request.Detail,
            NetworkUnknownAtMs = nowMs,
        };
        AppendReceipt(
            connection,
            unknown,
            "provider-stop-network-unknown",
            prior: ProviderStopState.Prepared,
            next: ProviderStopState.NetworkUnknown,
            input: RegistryJson.Serialize(request),
            output: RegistryJson.Serialize(unknown),
            nowMs);
        return RegistryResult<ProviderStopRequest>.Ok(unknown);
    }

    static RegistryResult<ProviderStopRequest> ExpireWithoutSubmissionInTransaction(
        SqliteConnection connection,
        ExpireProviderStopWithoutSubmissionWrite request)
    {
        var stored = ReadById(connection, request.ProviderStopRequestId);
        if (stored is null)
        {
            return Reject("unknown_request", $"provider stop request {request.ProviderStopRequestId} does not exist");
        }

        var actorMismatch = RequireActor(stored, request.Actor);
        if (actorMismatch is not null)
        {
            return RegistryResult<ProviderStopRequest>.Rejected(actorMismatch.Value);
        }

        if (stored.State == ProviderStopState.ExpiredWithoutSubmission)
        {
            return string.Equals(stored.ExpiredWithoutSubmissionDetail, request.Detail, StringComparison.Ordinal)
                ? RegistryResult<ProviderStopRequest>.Ok(stored)
                : Reject("expired_outcome_already_recorded",
                    $"provider stop request {stored.ProviderStopRequestId} already records a different no-submission expiry detail");
        }

        if (stored.State == ProviderStopState.NetworkUnknown)
        {
            return Reject("provider_submission_unknown",
                $"provider stop request {stored.ProviderStopRequestId} may already have reached the provider and must reconcile its stored Pod id");
        }

        if (stored.State == ProviderStopState.Terminal)
        {
            return Reject("terminal_outcome_already_recorded",
                $"provider stop request {stored.ProviderStopRequestId} already has a typed terminal receipt and cannot expire without submission");
        }

        var nowMs = DatabaseNowMs(connection);
        if (nowMs < stored.DeadlineAtMs)
        {
            return Reject("deadline_not_elapsed",
                $"provider stop request {stored.ProviderStopRequestId} remains prepared until its durable deadline");
        }

        using (var update = connection.CreateCommand())
        {
            update.CommandText =
                """
                UPDATE provider_stop_requests
                SET state = 'expired_without_submission',
                    expired_without_submission_detail = $detail,
                    expired_without_submission_at_ms = $now,
                    updated_at_ms = $now
                WHERE provider_stop_request_id = $id AND state = 'prepared'
                """;
            update.Parameters.AddWithValue("$detail", request.Detail);
            update.Parameters.AddWithValue("$now", nowMs);
            update.Parameters.AddWithValue("$id", stored.ProviderStopRequestId);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new RegistryFault(
                    $"provider stop request {stored.ProviderStopRequestId} changed while recording no-submission expiry");
            }
        }

        var expired = stored with
        {
            State = ProviderStopState.ExpiredWithoutSubmission,
            ExpiredWithoutSubmissionDetail = request.Detail,
            ExpiredWithoutSubmissionAtMs = nowMs,
        };
        AppendReceipt(
            connection,
            expired,
            "expire-provider-stop-without-submission",
            prior: ProviderStopState.Prepared,
            next: ProviderStopState.ExpiredWithoutSubmission,
            input: RegistryJson.Serialize(request),
            output: RegistryJson.Serialize(expired),
            nowMs);
        return RegistryResult<ProviderStopRequest>.Ok(expired);
    }

    static RegistryResult<ProviderStopRequest> RecordTerminalInTransaction(
        SqliteConnection connection,
        RecordProviderStopTerminalWrite request)
    {
        var stored = ReadById(connection, request.ProviderStopRequestId);
        if (stored is null)
        {
            return Reject("unknown_request", $"provider stop request {request.ProviderStopRequestId} does not exist");
        }

        var actorMismatch = RequireActor(stored, request.Actor);
        if (actorMismatch is not null)
        {
            return RegistryResult<ProviderStopRequest>.Rejected(actorMismatch.Value);
        }

        if (!string.Equals(stored.ProviderPodId, request.Response.ProviderPodId, StringComparison.Ordinal))
        {
            return Reject("provider_pod_mismatch",
                $"provider response named Pod {request.Response.ProviderPodId}, not stored Pod {stored.ProviderPodId}");
        }

        if (request.Response.DesiredStatus != stored.DesiredStatus)
        {
            return Reject("provider_state_mismatch",
                $"provider response for Pod {stored.ProviderPodId} was {DesiredStatusWire(request.Response.DesiredStatus)}, not requested {DesiredStatusWire(stored.DesiredStatus)}");
        }

        var terminalJson = RegistryJson.Serialize(request.Response);
        if (stored.State == ProviderStopState.Terminal)
        {
            return string.Equals(stored.TerminalReceiptJson, terminalJson, StringComparison.Ordinal)
                ? RegistryResult<ProviderStopRequest>.Ok(stored)
                : Reject("terminal_receipt_conflict",
                    $"provider stop request {stored.ProviderStopRequestId} already has a different terminal receipt");
        }

        if (stored.State == ProviderStopState.ExpiredWithoutSubmission)
        {
            return Reject("submission_never_started",
                $"provider stop request {stored.ProviderStopRequestId} expired before provider submission and cannot accept a provider terminal receipt");
        }

        var nowMs = DatabaseNowMs(connection);
        using (var update = connection.CreateCommand())
        {
            update.CommandText =
                """
                UPDATE provider_stop_requests
                SET state = 'terminal', terminal_receipt_json = $receipt,
                    terminal_received_at_ms = $now, updated_at_ms = $now
                WHERE provider_stop_request_id = $id AND state IN ('prepared', 'network_unknown')
                """;
            update.Parameters.AddWithValue("$receipt", terminalJson);
            update.Parameters.AddWithValue("$now", nowMs);
            update.Parameters.AddWithValue("$id", stored.ProviderStopRequestId);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new RegistryFault($"provider stop request {stored.ProviderStopRequestId} changed before its terminal receipt was recorded");
            }
        }

        var terminal = stored with
        {
            State = ProviderStopState.Terminal,
            TerminalReceiptJson = terminalJson,
            TerminalReceivedAtMs = nowMs,
        };
        AppendReceipt(
            connection,
            terminal,
            "record-provider-stop-terminal",
            prior: stored.State,
            next: ProviderStopState.Terminal,
            input: RegistryJson.Serialize(request),
            output: RegistryJson.Serialize(terminal),
            nowMs);
        return RegistryResult<ProviderStopRequest>.Ok(terminal);
    }

    static ProviderStopRequest? ReadByIdempotency(
        SqliteConnection connection,
        ProviderKind providerKind,
        string actorId,
        string idempotencyKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT provider_stop_request_id, provider_kind, provider_pod_id, site_id,
                   site_profile_revision, desired_status, actor_kind, actor_id,
                   idempotency_key, deadline_duration_ms, deadline_at_ms, state,
                   network_unknown_detail, network_unknown_at_ms,
                   expired_without_submission_detail, expired_without_submission_at_ms,
                   terminal_receipt_json, terminal_received_at_ms
            FROM provider_stop_requests
            WHERE provider_kind = $provider AND actor_id = $actor AND idempotency_key = $key
            """;
        command.Parameters.AddWithValue("$provider", ProviderKindWire(providerKind));
        command.Parameters.AddWithValue("$actor", actorId);
        command.Parameters.AddWithValue("$key", idempotencyKey);
        using var row = command.ExecuteReader();
        return row.Read() ? ReadRequest(row) : null;
    }

    static ProviderStopRequest? ReadActiveByProviderPod(
        SqliteConnection connection,
        ProviderKind providerKind,
        string providerPodId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT provider_stop_request_id, provider_kind, provider_pod_id, site_id,
                   site_profile_revision, desired_status, actor_kind, actor_id,
                   idempotency_key, deadline_duration_ms, deadline_at_ms, state,
                   network_unknown_detail, network_unknown_at_ms,
                   expired_without_submission_detail, expired_without_submission_at_ms,
                   terminal_receipt_json, terminal_received_at_ms
            FROM provider_stop_requests
            WHERE provider_kind = $provider
              AND provider_pod_id = $pod
              AND state IN ('prepared', 'network_unknown')
            ORDER BY created_at_ms, provider_stop_request_id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$provider", ProviderKindWire(providerKind));
        command.Parameters.AddWithValue("$pod", providerPodId);
        using var row = command.ExecuteReader();
        return row.Read() ? ReadRequest(row) : null;
    }

    static ProviderStopRequest? ReadById(SqliteConnection connection, string requestId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT provider_stop_request_id, provider_kind, provider_pod_id, site_id,
                   site_profile_revision, desired_status, actor_kind, actor_id,
                   idempotency_key, deadline_duration_ms, deadline_at_ms, state,
                   network_unknown_detail, network_unknown_at_ms,
                   expired_without_submission_detail, expired_without_submission_at_ms,
                   terminal_receipt_json, terminal_received_at_ms
            FROM provider_stop_requests WHERE provider_stop_request_id = $id
            """;
        command.Parameters.AddWithValue("$id", requestId);
        using var row = command.ExecuteReader();
        return row.Read() ? ReadRequest(row) : null;
    }

    static ProviderStopRequest ReadRequest(SqliteDataReader row)
    {
        var providerKind = ParseProviderKind(row.GetString(1));
        return new ProviderStopRequest(
            row.GetString(0),
            providerKind,
            row.GetString(2),
            row.GetString(3),
            row.GetString(4),
            ParseDesiredStatus(row.GetString(5)),
            new ProviderLifecycleActor(row.GetString(6), row.GetString(7)),
            row.GetString(8),
            row.GetInt64(9),
            row.GetInt64(10),
            ParseState(row.GetString(11)),
            row.IsDBNull(12) ? null : row.GetString(12),
            row.IsDBNull(13) ? null : row.GetInt64(13),
            row.IsDBNull(14) ? null : row.GetString(14),
            row.IsDBNull(15) ? null : row.GetInt64(15),
            row.IsDBNull(16) ? null : row.GetString(16),
            row.IsDBNull(17) ? null : row.GetInt64(17));
    }

    // CHG 2026-09-11 night (slice 9): the Site is the `sites` row register-site
    // mints (worker_sites retired); its executor is a declared capability and
    // its profile revision is the machine admission's, when one was recorded.
    static (string Executor, string ProfileRevision)? ReadSite(SqliteConnection connection, string siteId)
    {
        var capabilities = SiteAdmissions.Capabilities(connection, siteId);
        if (capabilities is null)
        {
            return null;
        }
        var executor = capabilities["executor"]?.GetValue<string>() ?? "local";
        var revision = MachineAdmissionAuthority.Current(connection, siteId)?.ExecutionProfileRevision ?? "";
        return (executor, revision);
    }

    static bool SamePreparation(ProviderStopRequest stored, PrepareProviderStopWrite request) =>
        stored.ProviderKind == request.ProviderKind
        && string.Equals(stored.ProviderPodId, request.ProviderPodId, StringComparison.Ordinal)
        && string.Equals(stored.SiteId, request.SiteId, StringComparison.Ordinal)
        && stored.DesiredStatus == request.DesiredStatus
        && string.Equals(stored.Actor.ActorId, request.Actor.ActorId, StringComparison.Ordinal)
        && string.Equals(stored.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal)
        && stored.DeadlineDurationMs == request.DeadlineDurationMs;

    static RegistryRefusal? RequireActor(ProviderStopRequest stored, ProviderLifecycleActor actor) =>
        string.Equals(stored.Actor.Kind, actor.Kind, StringComparison.Ordinal)
        && string.Equals(stored.Actor.ActorId, actor.ActorId, StringComparison.Ordinal)
            ? null
            : new RegistryRefusal("refused", "actor_mismatch",
                $"provider stop request {stored.ProviderStopRequestId} belongs to another actor");

    static RegistryRefusal? ValidatePreparation(PrepareProviderStopWrite request)
    {
        if (!Enum.IsDefined(request.ProviderKind) || request.ProviderKind != ProviderKind.Runpod)
        {
            return Refusal("unsupported_provider", "only the typed runpod provider is implemented");
        }

        if (request.DesiredStatus != ProviderDesiredStatus.Exited)
        {
            return Refusal("invalid_desired_status", "a RunPod stop request must require desiredStatus EXITED");
        }

        if (InvalidText(request.ProviderPodId, 256)
            || InvalidText(request.SiteId, 256)
            || InvalidText(request.IdempotencyKey, 256)
            || InvalidActor(request.Actor))
        {
            return Refusal("invalid_request",
                "provider stop requires a Pod id, Worker Site id, actor kind/id, and idempotency key");
        }

        if (request.DeadlineDurationMs < MinimumDeadlineMs || request.DeadlineDurationMs > MaximumDeadlineMs)
        {
            return Refusal("invalid_deadline",
                $"provider stop deadline must be between {MinimumDeadlineMs} and {MaximumDeadlineMs} milliseconds");
        }

        return null;
    }

    static RegistryRefusal? ValidateUnknown(MarkProviderStopNetworkUnknownWrite request) =>
        InvalidText(request.ProviderStopRequestId, 256)
        || InvalidText(request.Detail, 1024)
        || InvalidActor(request.Actor)
            ? Refusal("invalid_request", "recording provider network unknown requires request id, actor, and bounded detail")
            : null;

    static RegistryRefusal? ValidateNoSubmissionExpiry(ExpireProviderStopWithoutSubmissionWrite request) =>
        InvalidText(request.ProviderStopRequestId, 256)
        || InvalidText(request.Detail, 1024)
        || InvalidActor(request.Actor)
            ? Refusal("invalid_request", "recording provider no-submission expiry requires request id, actor, and bounded detail")
            : null;

    static RegistryRefusal? ValidateTerminal(RecordProviderStopTerminalWrite request)
    {
        if (InvalidText(request.ProviderStopRequestId, 256)
            || request.Response is null
            || InvalidText(request.Response.ProviderPodId, 256)
            || InvalidActor(request.Actor))
        {
            return Refusal("invalid_request", "recording provider terminal receipt requires request id, actor, and provider Pod id");
        }

        return request.Response.DesiredStatus != ProviderDesiredStatus.Exited
            ? Refusal("invalid_desired_status", "only desiredStatus EXITED is a terminal RunPod stop receipt")
            : null;
    }

    static RegistryRefusal? ValidateLookup(ProviderStopLookup lookup) =>
        lookup.ProviderKind != ProviderKind.Runpod
        || InvalidText(lookup.IdempotencyKey, 256)
        || InvalidActor(lookup.Actor)
            ? Refusal("invalid_request", "provider reconciliation requires runpod, actor kind/id, and idempotency key")
            : null;

    static bool InvalidActor(ProviderLifecycleActor? actor) =>
        actor is null || InvalidText(actor.Kind, 64) || InvalidText(actor.ActorId, 256);

    static bool InvalidText(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maximumLength;

    static RegistryResult<ProviderStopRequest> Reject(string category, string detail) =>
        RegistryResult<ProviderStopRequest>.Rejected(Refusal(category, detail));

    static RegistryRefusal Refusal(string category, string detail) =>
        new("refused", category, detail);

    static void AppendReceipt(
        SqliteConnection connection,
        ProviderStopRequest request,
        string operation,
        ProviderStopState? prior,
        ProviderStopState next,
        string input,
        string output,
        long nowMs)
    {
        var written = Journals.AppendTransitionReceiptInOpenTransaction(
            connection,
            new TransitionReceiptWrite(
                OperationName: operation,
                ObjectKind: ObjectKind,
                ObjectId: request.ProviderStopRequestId,
                PriorState: prior is null ? null : StateWire(prior.Value),
                NextState: StateWire(next),
                ActorKind: request.Actor.Kind,
                ActorId: request.Actor.ActorId,
                InputIdentityJson: input,
                OutputIdentityJson: output,
                PolicyRevision: PolicyRevision,
                CorrelationId: request.ProviderStopRequestId,
                OccurredAtMs: nowMs));
        if (!written.TryGetValue(out _))
        {
            if (written.TryGetRefusal(out var refusal))
            {
                throw new RegistryFault(refusal.Detail);
            }

            throw new RegistryFault($"cannot append {operation} transition receipt");
        }
    }

    static string ProviderKindWire(ProviderKind kind) => kind switch
    {
        ProviderKind.Runpod => "runpod",
        _ => throw new RegistryFault($"unknown provider kind {kind}"),
    };

    static ProviderKind ParseProviderKind(string value) => value switch
    {
        "runpod" => ProviderKind.Runpod,
        _ => throw new RegistryFault($"stored provider kind {value} is not supported"),
    };

    public static string DesiredStatusWire(ProviderDesiredStatus status) => status switch
    {
        ProviderDesiredStatus.Exited => "EXITED",
        _ => throw new RegistryFault($"unknown provider desired status {status}"),
    };

    static ProviderDesiredStatus ParseDesiredStatus(string value) => value switch
    {
        "EXITED" => ProviderDesiredStatus.Exited,
        _ => throw new RegistryFault($"stored provider desired status {value} is not supported"),
    };

    static string StateWire(ProviderStopState state) => state switch
    {
        ProviderStopState.Prepared => "prepared",
        ProviderStopState.NetworkUnknown => "network_unknown",
        ProviderStopState.ExpiredWithoutSubmission => "expired_without_submission",
        ProviderStopState.Terminal => "terminal",
        _ => throw new RegistryFault($"unknown provider stop state {state}"),
    };

    static ProviderStopState ParseState(string value) => value switch
    {
        "prepared" => ProviderStopState.Prepared,
        "network_unknown" => ProviderStopState.NetworkUnknown,
        "expired_without_submission" => ProviderStopState.ExpiredWithoutSubmission,
        "terminal" => ProviderStopState.Terminal,
        _ => throw new RegistryFault($"stored provider stop state {value} is not supported"),
    };

    static long DatabaseNowMs(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(unixepoch('subsec') * 1000 AS INTEGER)";
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
