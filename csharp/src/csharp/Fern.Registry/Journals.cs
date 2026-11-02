using Microsoft.Data.Sqlite;

namespace Fern.Registry;

public readonly record struct TransitionReceiptWrite(
    string OperationName,
    string ObjectKind,
    string ObjectId,
    string? PriorState,
    string? NextState,
    string ActorKind,
    string ActorId,
    string InputIdentityJson,
    string OutputIdentityJson,
    string PolicyRevision,
    string CorrelationId,
    long OccurredAtMs,
    string? AttemptId = null,
    string? CoordinatorLeaseId = null,
    string? CoordinateLeaseId = null,
    string? ResourceSlotLeaseId = null,
    long? FencingEpoch = null,
    string? CausationReceiptId = null,
    string? ErrorCategory = null);

public readonly record struct OperationOutcomeWrite(
    string OperationScope,
    string ActorId,
    string IdempotencyKey,
    string OperationName,
    // CHG 2026-09-26 (schema 19): the request itself, as text, where a SHA-256
    // of it was stored; a key sent again is compared with it byte for byte.
    string RequestText,
    string OutcomeKind,
    string ResultJson,
    long CreatedAtMs,
    string? ErrorCategory = null,
    string? TransitionReceiptId = null);

public static class Journals
{
    public static RegistryResult<string> AppendTransitionReceipt(
        RegistryWriter writer,
        TransitionReceiptWrite receipt)
    {
        RegistryHome.RequireDomainPrefix(receipt.ObjectKind, nameof(receipt.ObjectKind));
        return writer.WriteImmediate(connection => AppendTransitionReceiptInOpenTransaction(connection, receipt));
    }

    public static RegistryResult AppendOperationOutcome(
        RegistryWriter writer,
        OperationOutcomeWrite outcome)
    {
        RegistryHome.RequireDomainPrefix(outcome.OperationScope, nameof(outcome.OperationScope));
        return writer.WriteImmediate(connection => AppendOperationOutcomeInOpenTransaction(connection, outcome));
    }

    public static RegistryResult<string> AppendTransitionReceiptInOpenTransaction(
        SqliteConnection connection,
        TransitionReceiptWrite receipt)
    {
        // Rust &str/String cannot contain an unpaired UTF-16 surrogate. C#
        // restored that admission guarantee here while it derived identity and
        // hash bytes from the text (ReceiptPreimage.RequireValidUtf8).
        // CHG 2026-09-26 (schema 19): no hash is derived any more.
        // CHG 2026-09-27: the check is gone with its file; a C# string reaches
        // SQLite as UTF-8 through the driver, and nothing here read bytes.

        long previousSequence = 0;
        using (var previous = connection.CreateCommand())
        {
            // CHG 2026-09-26 (schema 19): only the sequence is read. The
            // previous receipt's hash, which chained this one to it, is gone.
            previous.CommandText =
                """
                SELECT object_sequence
                FROM transition_receipts
                WHERE object_kind = $kind AND object_id = $id
                ORDER BY object_sequence DESC
                LIMIT 1
                """;
            previous.Parameters.AddWithValue("$kind", receipt.ObjectKind);
            previous.Parameters.AddWithValue("$id", receipt.ObjectId);
            using var row = previous.ExecuteReader();
            if (row.Read())
            {
                previousSequence = row.GetInt64(0);
            }
        }

        var sequence = previousSequence + 1;
        // CHG 2026-09-26 (schema 19): the id is the receipt's place written
        // out, <object_kind>:<object_id>:<object_sequence>, where it was a
        // SHA-256 over that place, the correlation id and the previous hash.
        var receiptId = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{receipt.ObjectKind}:{receipt.ObjectId}:{sequence}");

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT INTO transition_receipts (
                    transition_receipt_id, operation_name, object_kind, object_id, object_sequence,
                    prior_state, next_state, actor_kind, actor_id, attempt_id,
                    coordinator_lease_id, coordinate_lease_id, resource_slot_lease_id, fencing_epoch,
                    input_identity_json, output_identity_json, policy_revision, correlation_id,
                    causation_receipt_id, error_category, occurred_at_ms
                ) VALUES (
                    $id, $op, $kind, $object, $seq,
                    $prior, $next, $actorKind, $actorId, $attempt,
                    $coordLease, $coordinateLease, $resourceLease, $epoch,
                    $input, $output, $policy, $corr,
                    $causation, $error, $at
                )
                """;
            insert.Parameters.AddWithValue("$id", receiptId);
            insert.Parameters.AddWithValue("$op", receipt.OperationName);
            insert.Parameters.AddWithValue("$kind", receipt.ObjectKind);
            insert.Parameters.AddWithValue("$object", receipt.ObjectId);
            insert.Parameters.AddWithValue("$seq", sequence);
            insert.Parameters.AddWithValue("$prior", (object?)receipt.PriorState ?? DBNull.Value);
            insert.Parameters.AddWithValue("$next", (object?)receipt.NextState ?? DBNull.Value);
            insert.Parameters.AddWithValue("$actorKind", receipt.ActorKind);
            insert.Parameters.AddWithValue("$actorId", receipt.ActorId);
            insert.Parameters.AddWithValue("$attempt", (object?)receipt.AttemptId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$coordLease", (object?)receipt.CoordinatorLeaseId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$coordinateLease", (object?)receipt.CoordinateLeaseId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$resourceLease", (object?)receipt.ResourceSlotLeaseId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$epoch", (object?)receipt.FencingEpoch ?? DBNull.Value);
            insert.Parameters.AddWithValue("$input", receipt.InputIdentityJson);
            insert.Parameters.AddWithValue("$output", receipt.OutputIdentityJson);
            insert.Parameters.AddWithValue("$policy", receipt.PolicyRevision);
            insert.Parameters.AddWithValue("$corr", receipt.CorrelationId);
            insert.Parameters.AddWithValue("$causation", (object?)receipt.CausationReceiptId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$error", (object?)receipt.ErrorCategory ?? DBNull.Value);
            insert.Parameters.AddWithValue("$at", receipt.OccurredAtMs);
            insert.ExecuteNonQuery();
        }

        return RegistryResult<string>.Ok(receiptId);
    }

    internal static RegistryResult AppendOperationOutcomeInOpenTransaction(
        SqliteConnection connection,
        OperationOutcomeWrite outcome)
    {
        try
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO operation_outcomes (
                    operation_scope, actor_id, idempotency_key, operation_name, request_text,
                    outcome_kind, result_json, error_category, transition_receipt_id, created_at_ms
                ) VALUES (
                    $scope, $actor, $key, $name, $request,
                    $kind, $json, $error, $receipt, $at
                )
                """;
            insert.Parameters.AddWithValue("$scope", outcome.OperationScope);
            insert.Parameters.AddWithValue("$actor", outcome.ActorId);
            insert.Parameters.AddWithValue("$key", outcome.IdempotencyKey);
            insert.Parameters.AddWithValue("$name", outcome.OperationName);
            insert.Parameters.AddWithValue("$request", outcome.RequestText);
            insert.Parameters.AddWithValue("$kind", outcome.OutcomeKind);
            insert.Parameters.AddWithValue("$json", outcome.ResultJson);
            insert.Parameters.AddWithValue("$error", (object?)outcome.ErrorCategory ?? DBNull.Value);
            insert.Parameters.AddWithValue("$receipt", (object?)outcome.TransitionReceiptId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$at", outcome.CreatedAtMs);
            insert.ExecuteNonQuery();
            return RegistryResult.Ok();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return RegistryResult.Rejected(new RegistryRefusal(
                OutcomeKind: "refused",
                ErrorCategory: "conflicting_replay",
                Detail: $"idempotency key {outcome.IdempotencyKey} already used in scope {outcome.OperationScope}"));
        }
    }

}
