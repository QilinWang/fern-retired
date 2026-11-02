using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Registry.Tests;

public sealed class EnvironmentInstallLeaseTests
{
    const string ChronosPython = "/workspace/.venv/bin/python";
    const string ZeusPython = "/workspace/.venv-zeus/bin/python";

    [Fact]
    public void ASecondInstallerOnTheSameRootsIsRefused()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var first = EnvironmentInstallLeaseAuthority.Acquire(
            writer, Request("attempt-1", ChronosPython, ["/workspace/.venv/bin/python"]));
        Assert.True(first.TryGetValue(out var lease), Detail(first));
        Assert.Equal(1, lease.FencingToken);
        Assert.Contains("model-cache:huggingface", lease.MutationRoots);
        Assert.Contains("package-cache:uv", lease.MutationRoots);

        var second = EnvironmentInstallLeaseAuthority.Acquire(
            writer, Request("attempt-2", ChronosPython, ["/workspace/.venv/bin/python"]));
        Assert.True(second.TryGetRefusal(out var refusal), Detail(second));
        Assert.Contains("already owns", refusal.Detail, StringComparison.Ordinal);
        Assert.Contains("attempt-1", refusal.Detail, StringComparison.Ordinal);
        Assert.Contains("fencing token 1", refusal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedModelCacheConflictsAcrossInterpretersOnOneMachine()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        _ = EnvironmentInstallLeaseAuthority.Acquire(
            writer, Request("attempt-chronos", ChronosPython, [ChronosPython])).Expect("EnvironmentInstallLeaseAuthority.Acquire");

        var zeus = EnvironmentInstallLeaseAuthority.Acquire(
            writer, Request("attempt-zeus", ZeusPython, [ZeusPython]));
        Assert.True(zeus.TryGetRefusal(out var refusal), Detail(zeus));
        Assert.Contains("model-cache:huggingface", refusal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ADifferentMachineGenerationMayLeaseTheSameNamedCache()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        Assert.True(EnvironmentInstallLeaseAuthority.Acquire(
            writer, Request("attempt-pod", ChronosPython, [ChronosPython], generation: "sha256:pod")).IsOk);
        var laptop = EnvironmentInstallLeaseAuthority.Acquire(
            writer, Request("attempt-laptop", ChronosPython, [ChronosPython], generation: "sha256:laptop"));
        Assert.True(laptop.TryGetValue(out var lease), Detail(laptop));
        Assert.Equal("sha256:laptop", lease.MachineGeneration);
        Assert.Equal(1, lease.FencingToken);
    }

    [Fact]
    public void ReleaseAllowsASuccessorAttemptAndIncrementsTheFencingToken()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var first = EnvironmentInstallLeaseAuthority.Acquire(
            writer, Request("attempt-1", ChronosPython, [ChronosPython])).Expect("EnvironmentInstallLeaseAuthority.Acquire");
        Assert.True(EnvironmentInstallLeaseAuthority.Release(
            writer,
            first.LeaseId,
            "attempt-1",
            EnvironmentInstallLeaseAuthority.EnvironmentAdmittedReleaseReason).IsOk);

        var successor = EnvironmentInstallLeaseAuthority.Acquire(
            writer, Request("attempt-2", ChronosPython, [ChronosPython]));
        Assert.True(successor.TryGetValue(out var next), Detail(successor));
        Assert.Equal(2, next.FencingToken);
        Assert.NotEqual(first.LeaseId, next.LeaseId);
    }

    [Fact]
    public void OwnerReleaseRecordsAPossibleEnvironmentChangeReceipt()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var lease = EnvironmentInstallLeaseAuthority.Acquire(
            writer, Request("attempt-1", ChronosPython, [ChronosPython])).Expect("EnvironmentInstallLeaseAuthority.Acquire");

        var released = EnvironmentInstallLeaseAuthority.Release(
            writer,
            lease.LeaseId,
            "attempt-1",
            EnvironmentInstallLeaseAuthority.EnvironmentChangeMayHaveHappenedReleaseReason);

        Assert.True(released.TryGetValue(out var releaseReason), Detail(released));
        Assert.Equal(EnvironmentInstallLeaseAuthority.EnvironmentChangeMayHaveHappenedReleaseReason, releaseReason);
        Assert.Empty(EnvironmentInstallLeaseAuthority.LiveForSite(writer.Connection, "runpod-a4500"));
        Assert.Equal(
            EnvironmentInstallLeaseAuthority.EnvironmentChangeMayHaveHappenedReleaseReason,
            Scalar<string>(
                writer.Connection,
                "SELECT release_reason FROM environment_install_leases WHERE lease_id = $lease",
                ("$lease", lease.LeaseId)));
        Assert.Equal(
            EnvironmentInstallLeaseAuthority.OwnerReleasedLeaseOperation,
            Scalar<string>(
                writer.Connection,
                "SELECT operation_name FROM transition_receipts WHERE object_id = $lease AND next_state = $state",
                ("$lease", lease.LeaseId),
                ("$state", EnvironmentInstallLeaseAuthority.EnvironmentChangeMayHaveHappenedReleaseReason)));
        Assert.Equal(
            lease.FencingToken,
            Scalar<long>(
                writer.Connection,
                "SELECT fencing_epoch FROM transition_receipts WHERE object_id = $lease AND next_state = $state",
                ("$lease", lease.LeaseId),
                ("$state", EnvironmentInstallLeaseAuthority.EnvironmentChangeMayHaveHappenedReleaseReason)));
        var output = Scalar<string>(
            writer.Connection,
            "SELECT output_identity_json FROM transition_receipts WHERE object_id = $lease AND next_state = $state",
            ("$lease", lease.LeaseId),
            ("$state", EnvironmentInstallLeaseAuthority.EnvironmentChangeMayHaveHappenedReleaseReason));
        Assert.Contains("\"release_reason\":\"environment_change_may_have_happened\"", output, StringComparison.Ordinal);
        Assert.Contains("\"owner_attempt_process_marker_value\":\"attempt-1\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnerReleaseRefusesANonOwnerAndKeepsTheMutationRoots()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var lease = EnvironmentInstallLeaseAuthority.Acquire(
            writer, Request("attempt-1", ChronosPython, [ChronosPython])).Expect("EnvironmentInstallLeaseAuthority.Acquire");

        var stolen = EnvironmentInstallLeaseAuthority.Release(
            writer,
            lease.LeaseId,
            "attempt-2",
            EnvironmentInstallLeaseAuthority.EnvironmentChangeMayHaveHappenedReleaseReason);

        Assert.True(stolen.TryGetRefusal(out var refusal), Detail(stolen));
        Assert.Contains("belongs to attempt attempt-1", refusal.Detail, StringComparison.Ordinal);
        Assert.Single(EnvironmentInstallLeaseAuthority.LiveForSite(writer.Connection, "runpod-a4500"));
        Assert.Equal(
            0,
            Scalar<long>(
                writer.Connection,
                "SELECT COUNT(*) FROM transition_receipts WHERE object_id = $lease AND operation_name = $operation",
                ("$lease", lease.LeaseId),
                ("$operation", EnvironmentInstallLeaseAuthority.OwnerReleasedLeaseOperation)));
    }

    [Fact]
    public void ExpiredLeaseStillFencesTheMutationRootUntilReconciledOrReleased()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var first = EnvironmentInstallLeaseAuthority.Acquire(
            writer, Request("attempt-1", ChronosPython, [ChronosPython])).Expect("EnvironmentInstallLeaseAuthority.Acquire");
        ForceDeadlineIntoThePast(writer, first.LeaseId);

        var successor = EnvironmentInstallLeaseAuthority.Acquire(
            writer, Request("attempt-2", ChronosPython, [ChronosPython]));

        Assert.True(successor.TryGetRefusal(out var refusal), Detail(successor));
        Assert.Contains("exceeded its deadline", refusal.Detail, StringComparison.Ordinal);
        Assert.Contains("still owns", refusal.Detail, StringComparison.Ordinal);
        Assert.Contains("reconcile", refusal.Detail, StringComparison.Ordinal);
        Assert.Single(EnvironmentInstallLeaseAuthority.LiveForSite(writer.Connection, "runpod-a4500"));

        Assert.True(EnvironmentInstallLeaseAuthority.Release(
            writer,
            first.LeaseId,
            "attempt-1",
            EnvironmentInstallLeaseAuthority.NoEnvironmentChangeStartedReleaseReason).IsOk);
        var afterRelease = EnvironmentInstallLeaseAuthority.Acquire(
            writer, Request("attempt-2", ChronosPython, [ChronosPython]));
        Assert.True(afterRelease.TryGetValue(out var next), Detail(afterRelease));
        Assert.Equal(2, next.FencingToken);
    }

    [Fact]
    public void ReconcileReleaseRequiresAnExpiredV2LeaseAndAbsenceObservation()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var first = EnvironmentInstallLeaseAuthority.Acquire(
            writer, Request("attempt-1", ChronosPython, [ChronosPython])).Expect("EnvironmentInstallLeaseAuthority.Acquire");
        ForceDeadlineIntoThePast(writer, first.LeaseId);

        var released = EnvironmentInstallLeaseAuthority.ReconcileRelease(
            writer,
            new EnvironmentInstallLeaseReconcileRelease(
                first.LeaseId,
                first.OwnerAttemptId,
                "observer",
                "owner_process_absent",
                AbsenceObservation(first)));

        Assert.True(released.TryGetValue(out var receipt), Detail(released));
        Assert.Equal(first.LeaseId, receipt.LeaseId);
        Assert.Equal("reconciled_release", receipt.Outcome);
        // CHG 2026-09-26 (schema 19): a receipt id is its place written out,
        // <object_kind>:<object_id>:<object_sequence>, no longer 64 hex digits.
        Assert.StartsWith(EnvironmentInstallLeaseAuthority.ObjectKind + ":" + first.LeaseId + ":", receipt.TransitionReceiptId);
        Assert.Matches(":[0-9]+$", receipt.TransitionReceiptId);
        Assert.Empty(EnvironmentInstallLeaseAuthority.LiveForSite(writer.Connection, "runpod-a4500"));
        Assert.Equal(
            "reconciled: owner_process_absent",
            Scalar<string>(writer.Connection, "SELECT release_reason FROM environment_install_leases WHERE lease_id = $lease", ("$lease", first.LeaseId)));

        var successor = EnvironmentInstallLeaseAuthority.Acquire(
            writer, Request("attempt-2", ChronosPython, [ChronosPython]));
        Assert.True(successor.TryGetValue(out var next), Detail(successor));
        Assert.Equal(2, next.FencingToken);
    }

    [Fact]
    public void ReconcileReleaseRefusesAnActiveLeaseBeforeObservingDeathAsRelease()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var first = EnvironmentInstallLeaseAuthority.Acquire(
            writer, Request("attempt-1", ChronosPython, [ChronosPython])).Expect("EnvironmentInstallLeaseAuthority.Acquire");

        var released = EnvironmentInstallLeaseAuthority.ReconcileRelease(
            writer,
            new EnvironmentInstallLeaseReconcileRelease(
                first.LeaseId,
                first.OwnerAttemptId,
                "observer",
                "owner_process_absent",
                AbsenceObservation(first)));

        Assert.True(released.TryGetRefusal(out var refusal), Detail(released));
        Assert.Contains("has not exceeded its deadline", refusal.Detail, StringComparison.Ordinal);
        Assert.Single(EnvironmentInstallLeaseAuthority.LiveForSite(writer.Connection, "runpod-a4500"));
    }

    [Fact]
    public void ReconcileReleaseRefusesALiveProcessObservation()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var first = EnvironmentInstallLeaseAuthority.Acquire(
            writer, Request("attempt-1", ChronosPython, [ChronosPython])).Expect("EnvironmentInstallLeaseAuthority.Acquire");
        ForceDeadlineIntoThePast(writer, first.LeaseId);

        var released = EnvironmentInstallLeaseAuthority.ReconcileRelease(
            writer,
            new EnvironmentInstallLeaseReconcileRelease(
                first.LeaseId,
                first.OwnerAttemptId,
                "observer",
                "owner_process_absent",
                LiveObservation(first)));

        Assert.True(released.TryGetRefusal(out var refusal), Detail(released));
        Assert.Contains("live_matches", refusal.Detail, StringComparison.Ordinal);
        Assert.Single(EnvironmentInstallLeaseAuthority.LiveForSite(writer.Connection, "runpod-a4500"));
    }

    [Fact]
    public void ReconcileReleaseRefusesALegacyLeaseWithoutTheProcessMarkerContract()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var legacy = SeedLegacyExpiredLease(writer);

        var released = EnvironmentInstallLeaseAuthority.ReconcileRelease(
            writer,
            new EnvironmentInstallLeaseReconcileRelease(
                legacy.LeaseId,
                legacy.OwnerAttemptId,
                "observer",
                "owner_process_absent",
                AbsenceObservation(legacy)));

        Assert.True(released.TryGetRefusal(out var refusal), Detail(released));
        Assert.Contains("process marker contract", refusal.Detail, StringComparison.Ordinal);
        Assert.Single(EnvironmentInstallLeaseAuthority.LiveForSite(writer.Connection, "runpod-a4500"));
    }

    static EnvironmentInstallLeaseAcquire Request(
        string attemptId,
        string interpreter,
        IReadOnlyList<string> interpretersForRoots,
        string generation = "sha256:machine")
    {
        var roots = interpretersForRoots
            .SelectMany(ModelEnvironmentInstallationMutationRoots)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new EnvironmentInstallLeaseAcquire(
            generation,
            interpreter,
            "runpod-a4500",
            roots,
            attemptId,
            "tester",
            1_800_000);
    }

    static IReadOnlyList<string> ModelEnvironmentInstallationMutationRoots(string interpreter)
    {
        var environment = interpreter.EndsWith("/bin/python", StringComparison.Ordinal)
            ? interpreter[..^"/bin/python".Length]
            : interpreter;
        return
        [
            "environment:" + environment,
            "model-cache:huggingface",
            "package-cache:uv",
        ];
    }

    static void ForceDeadlineIntoThePast(RegistryWriter writer, string leaseId)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "UPDATE environment_install_leases SET issued_at_ms = 0, deadline_ms = 1 WHERE lease_id = $lease";
        command.Parameters.AddWithValue("$lease", leaseId);
        command.ExecuteNonQuery();
    }

    static EnvironmentInstallLease SeedLegacyExpiredLease(RegistryWriter writer)
    {
        var lease = new EnvironmentInstallLease(
            "lease:legacy",
            "sha256:machine",
            ChronosPython,
            "runpod-a4500",
            ModelEnvironmentInstallationMutationRoots(ChronosPython),
            1,
            "attempt-legacy",
            "tester",
            0,
            1);

        using (var insertLease = writer.Connection.CreateCommand())
        {
            insertLease.CommandText =
                """
                INSERT INTO environment_install_leases (
                    lease_id, machine_generation, interpreter, site_id, mutation_roots_json,
                    fencing_token, owner_attempt_id, owner_actor, issued_at_ms, deadline_ms,
                    released_at_ms, release_reason
                ) VALUES (
                    $lease, $generation, $interpreter, $site, $roots,
                    $token, $attempt, $actor, $issued, $deadline,
                    NULL, NULL
                )
                """;
            insertLease.Parameters.AddWithValue("$lease", lease.LeaseId);
            insertLease.Parameters.AddWithValue("$generation", lease.MachineGeneration);
            insertLease.Parameters.AddWithValue("$interpreter", lease.Interpreter);
            insertLease.Parameters.AddWithValue("$site", lease.SiteId);
            insertLease.Parameters.AddWithValue("$roots", System.Text.Json.JsonSerializer.Serialize(lease.MutationRoots));
            insertLease.Parameters.AddWithValue("$token", lease.FencingToken);
            insertLease.Parameters.AddWithValue("$attempt", lease.OwnerAttemptId);
            insertLease.Parameters.AddWithValue("$actor", lease.OwnerActor);
            insertLease.Parameters.AddWithValue("$issued", lease.IssuedAtMs);
            insertLease.Parameters.AddWithValue("$deadline", lease.DeadlineMs);
            insertLease.ExecuteNonQuery();
        }

        foreach (var root in lease.MutationRoots)
        {
            using var insertRoot = writer.Connection.CreateCommand();
            insertRoot.CommandText =
                """
                INSERT INTO environment_install_lease_roots (
                    machine_generation, mutation_root, lease_id
                ) VALUES ($generation, $root, $lease)
                """;
            insertRoot.Parameters.AddWithValue("$generation", lease.MachineGeneration);
            insertRoot.Parameters.AddWithValue("$root", root);
            insertRoot.Parameters.AddWithValue("$lease", lease.LeaseId);
            insertRoot.ExecuteNonQuery();
        }

        using (var insertReceipt = writer.Connection.CreateCommand())
        {
            insertReceipt.CommandText =
                """
                INSERT INTO transition_receipts (
                    transition_receipt_id, operation_name, object_kind, object_id, object_sequence,
                    prior_state, next_state, actor_kind, actor_id, attempt_id,
                    coordinator_lease_id, coordinate_lease_id, resource_slot_lease_id, fencing_epoch,
                    input_identity_json, output_identity_json, policy_revision, correlation_id,
                    causation_receipt_id, error_category, occurred_at_ms
                ) VALUES (
                    'legacy-environment-install-lease-acquired',
                    'environment-install lease acquired',
                    'csharp:environment-install-lease',
                    $lease,
                    1,
                    NULL,
                    '1',
                    'cli',
                    'tester',
                    $attempt,
                    NULL,
                    NULL,
                    NULL,
                    1,
                    '{}',
                    '{}',
                    'fern.environment_install_lease.v1',
                    $lease,
                    NULL,
                    NULL,
                    0
                )
                """;
            // CHG 2026-09-26 (schema 19): the two hash columns, previous_receipt_hash
            // (NULL here) and receipt_hash ('legacy-hash'), left the table.
            insertReceipt.Parameters.AddWithValue("$lease", lease.LeaseId);
            insertReceipt.Parameters.AddWithValue("$attempt", lease.OwnerAttemptId);
            insertReceipt.ExecuteNonQuery();
        }

        return lease;
    }

    static string AbsenceObservation(EnvironmentInstallLease lease) =>
        $$"""
        {"schema":"fern.environment_install_lease_process_observation.v1","lease_id":"{{lease.LeaseId}}","site_id":"{{lease.SiteId}}","machine_generation":"{{lease.MachineGeneration}}","interpreter":"{{lease.Interpreter}}","owner_attempt_id":"{{lease.OwnerAttemptId}}","observer_actor":"observer","process_observer":"linux_proc_environ_v1","process_marker_name":"FERN_ENVIRONMENT_INSTALL_ATTEMPT_ID","process_marker_value":"{{lease.OwnerAttemptId}}","observed_outcome":"owner_process_absent","live_matches":[]}
        """;

    static string LiveObservation(EnvironmentInstallLease lease) =>
        $$"""
        {"schema":"fern.environment_install_lease_process_observation.v1","lease_id":"{{lease.LeaseId}}","site_id":"{{lease.SiteId}}","machine_generation":"{{lease.MachineGeneration}}","interpreter":"{{lease.Interpreter}}","owner_attempt_id":"{{lease.OwnerAttemptId}}","observer_actor":"observer","process_observer":"linux_proc_environ_v1","process_marker_name":"FERN_ENVIRONMENT_INSTALL_ATTEMPT_ID","process_marker_value":"{{lease.OwnerAttemptId}}","observed_outcome":"owner_process_absent","live_matches":[{"pid":123,"process_start_ticks":456,"command_line":"uv pip install"}]}
        """;

    static T Scalar<T>(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    static string Detail<T>(RegistryResult<T> result) =>
        result.TryGetRefusal(out var refusal) ? refusal.Render() : "operation returned no result";
}
