using Fern.Registry;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Registry.Tests;

public sealed class JournalTests
{
    [Fact]
    public void HomeReplaySurvivesReopenAndRejectsChangedArguments()
    {
        var path = ProductionHome.CreateRegistry();
        var id = Guid.NewGuid().ToString("N");
        using (var writer = RegistryHome.OpenWriter(path))
            Assert.Equal("{\"accepted\":true}", writer.Replay(id, "claim", "{}", () => "{\"accepted\":true}"));
        using var reopened = RegistryHome.OpenWriter(path);
        Assert.Equal("{\"accepted\":true}", reopened.Replay(id, "claim", "{}", () => throw new Exception("must not execute twice")));
        var changed = Assert.Throws<ArgumentException>(() => reopened.Replay(id, "claim", "changed", () => "wrong"));
        // CHG 2026-09-26 (schema 19): the request is kept as text, so the
        // refusal names what was recorded and what arrived.
        Assert.Contains("recorded \"claim\\n{}\"", changed.Message, StringComparison.Ordinal);
        Assert.Contains("received \"claim\\nchanged\"", changed.Message, StringComparison.Ordinal);
        using var stored = reopened.Connection.CreateCommand();
        stored.CommandText = "SELECT request_text FROM operation_outcomes WHERE idempotency_key = $id";
        stored.Parameters.AddWithValue("$id", id);
        Assert.Equal("claim\n{}", stored.ExecuteScalar());
    }

    // CHG 2026-09-26 (owner, on the old outcomes: "like useless"): the test that a replay
    // of an outcome without request text is refused is retired; step 18 carries no such
    // outcome, and request_text is NOT NULL.

    [Fact]
    public void FailedReplayRollsBackItsNestedDomainWrite()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var id = Guid.NewGuid().ToString("N");
        Assert.Throws<InvalidOperationException>(() => writer.Replay(id, "claim", "{}", () =>
        {
            writer.WriteImmediate(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at) VALUES ('x', '[]', 'commit', 'now')";
                return command.ExecuteNonQuery();
            });
            throw new InvalidOperationException("lost before the enclosing commit");
        }));
        using var count = writer.Connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM sources";
        Assert.Equal(0L, count.ExecuteScalar());
        Assert.Equal("{\"accepted\":true}", writer.Replay(id, "claim", "{}", () => "{\"accepted\":true}"));
    }
    [Fact]
    public void pass_three_receipts_and_outcomes_reject_direct_rewrite()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        foreach (var (operation, correlation) in new[]
                 {
                     ("declare-resource-slot", "append-only-declare"),
                     ("claim-resource-slot", "append-only-claim"),
                 })
        {
            var appended = Journals.AppendTransitionReceipt(writer, new TransitionReceiptWrite(
                OperationName: operation,
                ObjectKind: "csharp:resource-slot",
                ObjectId: "local-test/gpu-0",
                PriorState: null,
                NextState: "written",
                ActorKind: "test",
                ActorId: "append-only",
                InputIdentityJson: "{}",
                OutputIdentityJson: "{}",
                PolicyRevision: "test-policy@1",
                CorrelationId: correlation,
                OccurredAtMs: operation == "declare-resource-slot" ? 1 : 2));
            Assert.True(appended.IsOk);
        }
        Assert.True(Journals.AppendOperationOutcome(
            writer,
            Outcome("csharp:append-only", "append-only-outcome", "blocked", "resource_occupied")).IsOk);

        using (var sequence = writer.Connection.CreateCommand())
        {
            sequence.CommandText =
                "SELECT object_sequence FROM transition_receipts "
                + "WHERE object_kind = 'csharp:resource-slot' AND object_id = 'local-test/gpu-0' "
                + "ORDER BY object_sequence";
            using var rows = sequence.ExecuteReader();
            var actual = new List<long>();
            while (rows.Read()) actual.Add(rows.GetInt64(0));
            Assert.Equal(new long[] { 1, 2 }, actual);
        }

        using (var rewrite = writer.Connection.CreateCommand())
        {
            rewrite.CommandText = "UPDATE transition_receipts SET operation_name = 'tampered'";
            Assert.Throws<SqliteException>(() => rewrite.ExecuteNonQuery());
        }
        using (var erase = writer.Connection.CreateCommand())
        {
            erase.CommandText = "DELETE FROM operation_outcomes";
            Assert.Throws<SqliteException>(() => erase.ExecuteNonQuery());
        }
    }

    [Fact]
    public void Two_scopes_append_outcomes_without_collision()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var first = Journals.AppendOperationOutcome(writer, Outcome("csharp:substrate:alpha", "key-1", "blocked", "resource_occupied"));
        var second = Journals.AppendOperationOutcome(writer, Outcome("csharp:substrate:beta", "key-1", "blocked", "resource_occupied"));
        Assert.True(first.IsOk);
        Assert.True(second.IsOk);
    }

    /// <summary>
    /// CHG 2026-09-26 (schema 19): was Receipt_id_is_the_content_address_not_a_sequence,
    /// which pinned the id to a SHA-256 over the receipt's place, correlation
    /// id and previous hash. The id is the place itself now, written out:
    /// <c>object_kind:object_id:object_sequence</c>. It is still not one
    /// counter across the Home: each object counts its own receipts from 1.
    /// </summary>
    [Fact]
    public void Receipt_id_is_its_place_written_out()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        string Append(string objectId, string correlation)
        {
            var result = Journals.AppendTransitionReceipt(writer, new TransitionReceiptWrite(
                OperationName: "registry-b-probe",
                ObjectKind: "csharp:substrate",
                ObjectId: objectId,
                PriorState: null,
                NextState: "written",
                ActorKind: "test",
                ActorId: "registry-b",
                InputIdentityJson: "{}",
                OutputIdentityJson: "{}",
                PolicyRevision: "registry-b",
                CorrelationId: correlation,
                OccurredAtMs: 1));
            Assert.True(result.TryGetValue(out var receiptId));
            return receiptId!;
        }
        Assert.Equal("csharp:substrate:object-1:1", Append("object-1", "corr-1"));
        Assert.Equal("csharp:substrate:object-1:2", Append("object-1", "corr-2"));
        Assert.Equal("csharp:substrate:object-2:1", Append("object-2", "corr-1"));
    }

    [Fact]
    public void Refusal_round_trips_into_outcome_kind_and_error_category()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var refusal = new RegistryRefusal("blocked", "resource_occupied", "resource occupied");
        var written = Journals.AppendOperationOutcome(writer, Outcome(
            "csharp:substrate:round-trip",
            "key-rt",
            refusal.OutcomeKind,
            refusal.ErrorCategory));
        Assert.True(written.IsOk);

        using var reader = RegistryHome.OpenReader(path);
        using var command = reader.Connection.CreateCommand();
        command.CommandText =
            """
            SELECT outcome_kind, error_category
            FROM operation_outcomes
            WHERE operation_scope = $scope
            """;
        command.Parameters.AddWithValue("$scope", "csharp:substrate:round-trip");
        using var row = command.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal("blocked", row.GetString(0));
        Assert.Equal("resource_occupied", row.GetString(1));
    }

    [Fact]
    public void Rendered_scheduler_refusal_keeps_no_longer_current()
    {
        var refusal = new RegistryRefusal(
            OutcomeKind: "refused",
            ErrorCategory: "stale_epoch",
            Detail: "unused",
            HolderActor: "operator:a",
            HolderHost: "host-a",
            LeaseId: "lease-old",
            StaleEpoch: 1,
            SuccessorEpoch: 2);
        var text = refusal.Render();
        Assert.Contains(RegistryRefusal.NoLongerCurrentSubstring, text);
        Assert.Contains("lease-old", text);
        Assert.Contains("epoch 1", text);
    }

    /// <summary>
    /// The receipt hash covers Rust's 21-field declaration-order material,
    /// including explicit nulls. This is separate from the five-field receipt
    /// ID preimage: content changes must change the receipt hash even when the
    /// receipt's chain position does not change.
    ///
    /// CHG 2026-09-26 (schema 19): there is no receipt hash. What it covered,
    /// every field in declaration order, absent ones as null, is what the row
    /// itself stores; this reads the row back and checks that no hash column
    /// remains.
    /// </summary>
    [Fact]
    public void A_receipt_stores_every_field_as_written_and_no_hash()
    {
        var path = ProductionHome.CreateRegistry();
        using (var writer = RegistryHome.OpenWriter(path))
        {
            var result = Journals.AppendTransitionReceipt(writer, new TransitionReceiptWrite(
                OperationName: "registry-b-probe",
                ObjectKind: "csharp:substrate",
                ObjectId: "object-hash",
                PriorState: null,
                NextState: "written",
                ActorKind: "test",
                ActorId: "registry-b",
                InputIdentityJson: "{}",
                OutputIdentityJson: "{}",
                PolicyRevision: "registry-b",
                CorrelationId: "corr-hash",
                OccurredAtMs: 1));
            Assert.True(result.IsOk);
        }

        using var reader = RegistryHome.OpenReader(path);
        using var command = reader.Connection.CreateCommand();
        command.CommandText =
            """
            SELECT transition_receipt_id, operation_name, object_sequence, prior_state, next_state, actor_kind,
                   actor_id, attempt_id, fencing_epoch, input_identity_json, output_identity_json, policy_revision,
                   correlation_id, causation_receipt_id, error_category, occurred_at_ms
            FROM transition_receipts
            WHERE object_kind = 'csharp:substrate' AND object_id = 'object-hash'
            """;
        using (var row = command.ExecuteReader())
        {
            Assert.True(row.Read());
            Assert.Equal("csharp:substrate:object-hash:1", row.GetString(0));
            Assert.Equal("registry-b-probe", row.GetString(1));
            Assert.Equal(1L, row.GetInt64(2));
            Assert.True(row.IsDBNull(3));
            Assert.Equal("written", row.GetString(4));
            Assert.Equal("test", row.GetString(5));
            Assert.Equal("registry-b", row.GetString(6));
            Assert.True(row.IsDBNull(7));
            Assert.True(row.IsDBNull(8));
            Assert.Equal("{}", row.GetString(9));
            Assert.Equal("{}", row.GetString(10));
            Assert.Equal("registry-b", row.GetString(11));
            Assert.Equal("corr-hash", row.GetString(12));
            Assert.True(row.IsDBNull(13));
            Assert.True(row.IsDBNull(14));
            Assert.Equal(1L, row.GetInt64(15));
        }
        using var columns = reader.Connection.CreateCommand();
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('transition_receipts') WHERE name LIKE '%hash%'";
        Assert.Equal(0L, Convert.ToInt64(columns.ExecuteScalar()));
    }

    /// <summary>
    /// CHG 2026-09-26 (schema 19): was Receipt_hash_matches_the_rust_source_golden_for_text_encoding,
    /// which pinned the two receipts' ids and hashes to Rust's digests. The
    /// claims kept: text with quotes, escapes, tabs, newlines and non-ASCII is
    /// stored and read back exactly, and a successor follows its predecessor in
    /// the object's order and names it as its cause.
    /// </summary>
    [Fact]
    public void Receipt_text_is_stored_exactly_and_a_successor_follows_its_predecessor()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var result = Journals.AppendTransitionReceipt(writer, new TransitionReceiptWrite(
            OperationName: "write-\"é\nreceipt",
            ObjectKind: "csharp:transition",
            ObjectId: "object\t東京",
            PriorState: "prior\\path",
            NextState: "next\nline",
            ActorKind: "agent",
            ActorId: "agent-\"λ",
            InputIdentityJson: "{\"note\":\"quoted \\\"value\\\"\"}",
            OutputIdentityJson: "{\"line\":\"one\\ntwo\"}",
            PolicyRevision: "policy\\revision",
            CorrelationId: "correlation-δ",
            OccurredAtMs: 1_727_000_000_123,
            AttemptId: "attempt\t1",
            CoordinatorLeaseId: "coordinator-α",
            CoordinateLeaseId: "coordinate-β",
            ResourceSlotLeaseId: "resource-γ",
            FencingEpoch: 17,
            CausationReceiptId: "cause\tε",
            ErrorCategory: "error-\"ζ\""));
        Assert.True(result.TryGetValue(out var receiptId));
        Assert.Equal("csharp:transition:object\t東京:1", receiptId);

        using var reader = RegistryHome.OpenReader(path);
        using var command = reader.Connection.CreateCommand();
        command.CommandText =
            """
            SELECT operation_name, object_id, prior_state, next_state, actor_id, input_identity_json,
                   output_identity_json, policy_revision, correlation_id, attempt_id, coordinator_lease_id,
                   coordinate_lease_id, resource_slot_lease_id, fencing_epoch, causation_receipt_id, error_category
            FROM transition_receipts WHERE transition_receipt_id = $id
            """;
        command.Parameters.AddWithValue("$id", receiptId);
        using (var row = command.ExecuteReader())
        {
            Assert.True(row.Read());
            Assert.Equal("write-\"é\nreceipt", row.GetString(0));
            Assert.Equal("object\t東京", row.GetString(1));
            Assert.Equal("prior\\path", row.GetString(2));
            Assert.Equal("next\nline", row.GetString(3));
            Assert.Equal("agent-\"λ", row.GetString(4));
            Assert.Equal("{\"note\":\"quoted \\\"value\\\"\"}", row.GetString(5));
            Assert.Equal("{\"line\":\"one\\ntwo\"}", row.GetString(6));
            Assert.Equal("policy\\revision", row.GetString(7));
            Assert.Equal("correlation-δ", row.GetString(8));
            Assert.Equal("attempt\t1", row.GetString(9));
            Assert.Equal("coordinator-α", row.GetString(10));
            Assert.Equal("coordinate-β", row.GetString(11));
            Assert.Equal("resource-γ", row.GetString(12));
            Assert.Equal(17L, row.GetInt64(13));
            Assert.Equal("cause\tε", row.GetString(14));
            Assert.Equal("error-\"ζ\"", row.GetString(15));
        }

        var successor = Journals.AppendTransitionReceipt(writer, new TransitionReceiptWrite(
            OperationName: "advance-二",
            ObjectKind: "csharp:transition",
            ObjectId: "object\t東京",
            PriorState: "next\nline",
            NextState: null,
            ActorKind: "agent",
            ActorId: "agent-\"λ",
            InputIdentityJson: "{\"second\":\"\\t\"}",
            OutputIdentityJson: "null",
            PolicyRevision: "policy\\revision-2",
            CorrelationId: "correlation-successor-η",
            OccurredAtMs: 1_727_000_000_124,
            CoordinatorLeaseId: "coordinator-α",
            ResourceSlotLeaseId: "resource-γ",
            FencingEpoch: 18,
            CausationReceiptId: receiptId));
        Assert.True(successor.TryGetValue(out var successorId));
        Assert.Equal("csharp:transition:object\t東京:2", successorId);

        using var successorCommand = reader.Connection.CreateCommand();
        successorCommand.CommandText =
            "SELECT object_sequence, causation_receipt_id, prior_state FROM transition_receipts WHERE transition_receipt_id = $id";
        successorCommand.Parameters.AddWithValue("$id", successorId);
        using var successorRow = successorCommand.ExecuteReader();
        Assert.True(successorRow.Read());
        Assert.Equal(2L, successorRow.GetInt64(0));
        Assert.Equal(receiptId, successorRow.GetString(1));
        Assert.Equal("next\nline", successorRow.GetString(2));
    }

    private static OperationOutcomeWrite Outcome(string scope, string key, string kind, string? category) => new(
        OperationScope: scope,
        ActorId: "registry-b-test",
        IdempotencyKey: key,
        OperationName: "registry-b-probe",
        RequestText: "registry-b-probe\n{}",
        OutcomeKind: kind,
        ResultJson: "{}",
        CreatedAtMs: 1,
        ErrorCategory: category);
}
