using Fern.Registry;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// Provider lifecycle facts are tested against the production migration chain,
/// never an ad-hoc provider-stop table.  These are Registry-boundary tests;
/// they do not claim to call RunPod.
/// </summary>
public sealed class ProviderStopAuthorityTests
{
    [Fact]
    public void prepared_request_is_idempotent_and_keeps_the_site_profile_and_pod_identity()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        RegisterSshSite(writer, "runpod-site", "profile-revision-a");
        var request = Prepare("runpod-site", "pod-immutable-a", "stop-key-a");

        var prepared = ProviderStopAuthority.Prepare(writer, request);
        Assert.True(prepared.TryGetValue(out var first), Detail(prepared));
        Assert.Equal(ProviderStopState.Prepared, first.State);
        Assert.Equal("pod-immutable-a", first.ProviderPodId);
        Assert.Equal("profile-revision-a", first.SiteProfileRevision);
        Assert.Equal(ProviderDesiredStatus.Exited, first.DesiredStatus);
        Assert.Equal("operator-a", first.Actor.ActorId);
        Assert.Equal(30_000, first.DeadlineDurationMs);
        Assert.Equal(first.DeadlineDurationMs, first.DeadlineAtMs - ReadCreatedAt(writer, first.ProviderStopRequestId));

        var replay = ProviderStopAuthority.Prepare(writer, request);
        Assert.True(replay.TryGetValue(out var same), Detail(replay));
        Assert.Equal(first.ProviderStopRequestId, same.ProviderStopRequestId);
        Assert.Equal(first.DeadlineAtMs, same.DeadlineAtMs);

        var extended = ProviderStopAuthority.Prepare(
            writer,
            request with { DeadlineDurationMs = 60_000 });
        Assert.True(extended.TryGetRefusal(out var deadlineConflict));
        Assert.Equal("idempotency_key_conflict", deadlineConflict.ErrorCategory);

        var conflict = ProviderStopAuthority.Prepare(
            writer,
            request with { ProviderPodId = "pod-other" });
        Assert.True(conflict.TryGetRefusal(out var refusal));
        Assert.Equal("idempotency_key_conflict", refusal.ErrorCategory);
        Assert.Equal(1, CountRows(writer, "provider_stop_requests"));
    }

    [Fact]
    public void only_the_matching_exited_provider_receipt_becomes_terminal_and_is_append_only()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        RegisterSshSite(writer, "runpod-site", "profile-revision-a");
        var prepared = Require(ProviderStopAuthority.Prepare(
            writer,
            Prepare("runpod-site", "pod-immutable-a", "stop-key-a")));
        var actor = new ProviderLifecycleActor("cli", "operator-a");
        var receiptsBeforeMismatch = CountTransitionReceipts(writer, prepared.ProviderStopRequestId);

        var wrongPod = ProviderStopAuthority.RecordTerminal(
            writer,
            new RecordProviderStopTerminalWrite(
                prepared.ProviderStopRequestId,
                actor,
                new ProviderTerminalResponse("pod-other", ProviderDesiredStatus.Exited)));
        Assert.True(wrongPod.TryGetRefusal(out var mismatch));
        Assert.Equal("provider_pod_mismatch", mismatch.ErrorCategory);
        Assert.Equal("prepared", ReadState(writer, prepared.ProviderStopRequestId));
        Assert.Equal(receiptsBeforeMismatch, CountTransitionReceipts(writer, prepared.ProviderStopRequestId));

        var unknown = Require(ProviderStopAuthority.MarkNetworkUnknown(
            writer,
            new MarkProviderStopNetworkUnknownWrite(
                prepared.ProviderStopRequestId,
                actor,
                "submit response was not available before the deadline")));
        Assert.Equal(ProviderStopState.NetworkUnknown, unknown.State);

        var terminal = Require(ProviderStopAuthority.RecordTerminal(
            writer,
            new RecordProviderStopTerminalWrite(
                prepared.ProviderStopRequestId,
                actor,
                new ProviderTerminalResponse("pod-immutable-a", ProviderDesiredStatus.Exited))));
        Assert.Equal(ProviderStopState.Terminal, terminal.State);
        Assert.NotNull(terminal.TerminalReceiptJson);
        Assert.NotNull(terminal.TerminalReceivedAtMs);
        Assert.Equal("terminal", ReadState(writer, prepared.ProviderStopRequestId));
        Assert.Equal(3, CountTransitionReceipts(writer, prepared.ProviderStopRequestId));

        var replay = ProviderStopAuthority.RecordTerminal(
            writer,
            new RecordProviderStopTerminalWrite(
                prepared.ProviderStopRequestId,
                actor,
                new ProviderTerminalResponse("pod-immutable-a", ProviderDesiredStatus.Exited)));
        Assert.True(replay.TryGetValue(out var same), Detail(replay));
        Assert.Equal(terminal.TerminalReceiptJson, same.TerminalReceiptJson);

        using var tamper = writer.Connection.CreateCommand();
        tamper.CommandText =
            "UPDATE provider_stop_requests SET terminal_receipt_json = '{\"tampered\":true}' WHERE provider_stop_request_id = $id";
        tamper.Parameters.AddWithValue("$id", prepared.ProviderStopRequestId);
        var error = Assert.Throws<SqliteException>(() => tamper.ExecuteNonQuery());
        Assert.Contains("append-only", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void reconciliation_loads_the_stored_pod_not_a_caller_replacement()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        RegisterSshSite(writer, "runpod-site", "profile-revision-a");
        var prepared = Require(ProviderStopAuthority.Prepare(
            writer,
            Prepare("runpod-site", "pod-immutable-a", "stop-key-a")));
        var actor = new ProviderLifecycleActor("cli", "operator-a");
        Require(ProviderStopAuthority.MarkNetworkUnknown(
            writer,
            new MarkProviderStopNetworkUnknownWrite(
                prepared.ProviderStopRequestId,
                actor,
                "connection ended before a typed provider response")));

        using var reader = RegistryHome.OpenReader(path);
        var loaded = ProviderStopAuthority.LoadForReconcile(
            reader.Connection,
            new ProviderStopLookup(ProviderKind.Runpod, actor, "stop-key-a"));
        Assert.True(loaded.TryGetValue(out var request), Detail(loaded));
        Assert.Equal("pod-immutable-a", request.ProviderPodId);
        Assert.Equal(ProviderStopState.NetworkUnknown, request.State);
    }


    /// <summary>
    /// The no-submission expiry reads the DATABASE clock, not the caller's:
    /// a deadline that has not elapsed is refused and writes no receipt, the
    /// elapsed one expires exactly once however many times it is asked, and the
    /// expired request releases the pod lock without becoming terminal.
    /// </summary>
    // CHG 2026-09-04: this ran through the private fern-registry verbs, which
    // retired with the rest of that binary's dead process doors. The authority
    // is called directly; the behaviour asserted is unchanged.
    [Fact]
    public async Task the_database_clock_confirms_a_no_submission_expiry_and_releases_the_pod_lock()
    {
        var path = ProductionHome.CreateRegistry();
        var actor = new ProviderLifecycleActor("cli", "operator-a");
        ProviderStopRequest prepared;
        ProviderStopRequest notYetExpired;
        using (var writer = RegistryHome.OpenWriter(path))
        {
            RegisterSshSite(writer, "runpod-site", "profile-revision-a");
            prepared = Require(ProviderStopAuthority.Prepare(
                writer,
                Prepare("runpod-site", "pod-immutable-a", "stop-key-expired") with
                {
                    DeadlineDurationMs = 1_000,
                }));
            notYetExpired = Require(ProviderStopAuthority.Prepare(
                writer,
                Prepare("runpod-site", "pod-not-yet-expired", "stop-key-not-yet-expired")));
            Assert.Equal(1, CountTransitionReceipts(writer, prepared.ProviderStopRequestId));
        }

        const string ExpiryDetail = "deadline elapsed before any provider submission";
        using (var writer = RegistryHome.OpenWriter(path))
        {
            var tooSoon = ProviderStopAuthority.ExpireWithoutSubmission(
                writer,
                new ExpireProviderStopWithoutSubmissionWrite(notYetExpired.ProviderStopRequestId, actor, ExpiryDetail));
            Assert.True(tooSoon.TryGetRefusal(out _), "a deadline that has not elapsed was expired");
            Assert.Equal("prepared", ReadState(writer, notYetExpired.ProviderStopRequestId));
            Assert.Equal(1, CountTransitionReceipts(writer, notYetExpired.ProviderStopRequestId));
        }

        await Task.Delay(TimeSpan.FromMilliseconds(1_100));

        using (var writer = RegistryHome.OpenWriter(path))
        {
            var expired = ProviderStopAuthority.ExpireWithoutSubmission(
                writer,
                new ExpireProviderStopWithoutSubmissionWrite(prepared.ProviderStopRequestId, actor, ExpiryDetail));
            Assert.True(expired.TryGetValue(out var expiredRequest), Detail(expired));
            Assert.Equal(ProviderStopState.ExpiredWithoutSubmission, expiredRequest.State);

            var replay = ProviderStopAuthority.ExpireWithoutSubmission(
                writer,
                new ExpireProviderStopWithoutSubmissionWrite(prepared.ProviderStopRequestId, actor, ExpiryDetail));
            Assert.True(replay.TryGetValue(out var replayed), Detail(replay));
            Assert.Equal(ProviderStopState.ExpiredWithoutSubmission, replayed.State);
        }

        using var after = RegistryHome.OpenWriter(path);
        Assert.Equal("expired_without_submission", ReadState(after, prepared.ProviderStopRequestId));
        Assert.Equal(2, CountTransitionReceipts(after, prepared.ProviderStopRequestId));
        var falseUnknown = ProviderStopAuthority.MarkNetworkUnknown(
            after,
            new MarkProviderStopNetworkUnknownWrite(
                prepared.ProviderStopRequestId,
                actor,
                "a provider request might have happened"));
        Assert.True(falseUnknown.TryGetRefusal(out var unknownRefusal));
        Assert.Equal("submission_never_started", unknownRefusal.ErrorCategory);
        var falseTerminal = ProviderStopAuthority.RecordTerminal(
            after,
            new RecordProviderStopTerminalWrite(
                prepared.ProviderStopRequestId,
                actor,
                new ProviderTerminalResponse("pod-immutable-a", ProviderDesiredStatus.Exited)));
        Assert.True(falseTerminal.TryGetRefusal(out var terminalRefusal));
        Assert.Equal("submission_never_started", terminalRefusal.ErrorCategory);
        Assert.Equal(2, CountTransitionReceipts(after, prepared.ProviderStopRequestId));
        var sameKey = Require(ProviderStopAuthority.Prepare(
            after,
            Prepare("runpod-site", "pod-immutable-a", "stop-key-expired") with
            {
                DeadlineDurationMs = 1_000,
            }));
        Assert.Equal(ProviderStopState.ExpiredWithoutSubmission, sameKey.State);
        var extendedReplay = ProviderStopAuthority.Prepare(
            after,
            Prepare("runpod-site", "pod-immutable-a", "stop-key-expired"));
        Assert.True(extendedReplay.TryGetRefusal(out var conflict));
        Assert.Equal("idempotency_key_conflict", conflict.ErrorCategory);

        var laterRequest = Require(ProviderStopAuthority.Prepare(
            after,
            Prepare("runpod-site", "pod-immutable-a", "stop-key-after-expiry")));
        Assert.Equal(ProviderStopState.Prepared, laterRequest.State);
    }

    static PrepareProviderStopWrite Prepare(string siteId, string podId, string idempotencyKey) => new(
        ProviderKind.Runpod,
        podId,
        siteId,
        ProviderDesiredStatus.Exited,
        new ProviderLifecycleActor("cli", "operator-a"),
        idempotencyKey,
        30_000);

    /// <summary>
    /// Schema 062 has no <c>device_id</c>. Current <see cref="WorkerSiteAuthority.Register"/>
    /// writes that column, so a 062-era fixture must insert the Site row itself.
    /// </summary>
    static void InsertWorkerSiteAtSchema062(SqliteConnection connection, string siteId, string revision)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO worker_sites (
                site_id, execution_profile_id, execution_profile_revision, executor, device,
                enabled, capability_digest, max_concurrent_stages, max_gpu_stages, allowed_handlers,
                admitted_models, registered_by, registered_at_ms, updated_at_ms, disabled_reason
            ) VALUES (
                $id, $id, $revision, 'ssh', 'cuda',
                1, 'digest', 1, 1, '["train"]',
                '[]', 'operator-a', 1, 1, NULL
            )
            """;
        command.Parameters.AddWithValue("$id", siteId);
        command.Parameters.AddWithValue("$revision", revision);
        command.ExecuteNonQuery();
    }

    // CHG 2026-09-11 night (slice 9): the Site is the `sites` row; the revision
    // the stop request records is the machine admission's, none here.
    static void RegisterSshSite(RegistryWriter writer, string siteId, string revision)
    {
        var registered = RegistryOperations.RegisterSite(
            writer, siteId, """{"executor":"ssh","device":"cuda","admitted_models":["fern"],"devices":["GPU-a"]}""", """{"slots":1}""");
        Assert.True(registered.IsOk, Detail(registered));
        // The profile revision a stop request records is the machine admission's.
        var admitted = MachineAdmissionAuthority.Record(
            writer, MachineAdmissionTests.Write(siteId: siteId) with { ExecutionProfileRevision = revision });
        Assert.True(admitted.IsOk, Detail(admitted));
    }

    static T Require<T>(RegistryResult<T> result)
    {
        Assert.True(result.TryGetValue(out var value), Detail(result));
        return value;
    }

    static string Detail<T>(RegistryResult<T> result) =>
        result.TryGetRefusal(out var refusal) ? refusal.Render() : "operation returned no result";

    static long ReadCreatedAt(RegistryWriter writer, string requestId)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT created_at_ms FROM provider_stop_requests WHERE provider_stop_request_id = $id";
        command.Parameters.AddWithValue("$id", requestId);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static string ReadState(RegistryWriter writer, string requestId)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT state FROM provider_stop_requests WHERE provider_stop_request_id = $id";
        command.Parameters.AddWithValue("$id", requestId);
        return (string)(command.ExecuteScalar() ?? throw new InvalidOperationException("missing provider request"));
    }

    static long CountRows(RegistryWriter writer, string table)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static long CountTransitionReceipts(RegistryWriter writer, string requestId)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM transition_receipts WHERE object_kind = 'provider-stop-request' AND object_id = $id";
        command.Parameters.AddWithValue("$id", requestId);
        return Convert.ToInt64(command.ExecuteScalar());
    }

}
