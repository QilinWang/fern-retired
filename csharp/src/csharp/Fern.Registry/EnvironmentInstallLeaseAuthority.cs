using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Fern.Registry;

/// <summary>
/// One EnvironmentInstallAttempt's exclusive ownership of named mutation
/// roots on one machine generation. The public install command has no
/// standing write authority. <c>uv</c> does not honor the fencing token;
/// this table is the owner proof until environment generations are published
/// as new directories.
/// </summary>
public sealed record EnvironmentInstallLease(
    string LeaseId,
    string MachineGeneration,
    string Interpreter,
    string SiteId,
    IReadOnlyList<string> MutationRoots,
    long FencingToken,
    string OwnerAttemptId,
    string OwnerActor,
    long IssuedAtMs,
    long DeadlineMs);

public sealed record EnvironmentInstallLeaseAcquire(
    string MachineGeneration,
    string Interpreter,
    string SiteId,
    IReadOnlyList<string> MutationRoots,
    string OwnerAttemptId,
    string OwnerActor,
    long LifetimeMs);

public sealed record EnvironmentInstallLeaseReconcileRelease(
    string LeaseId,
    string OwnerAttemptId,
    string ObserverActor,
    string ObservedOutcome,
    string ObservationReceiptJson);

public sealed record EnvironmentInstallLeaseReconcileReleaseResult(
    string LeaseId,
    string OwnerAttemptId,
    long FencingToken,
    string Outcome,
    string ReleaseReason,
    string TransitionReceiptId);

public static class EnvironmentInstallLeaseAuthority
{
    public const string ObjectKind = "csharp:environment-install-lease";
    public const string PolicyRevision = "fern.environment_install_lease.v2";
    public const string OwnerAttemptProcessMarker = "FERN_ENVIRONMENT_INSTALL_ATTEMPT_ID";
    public const string NoEnvironmentChangeStartedReleaseReason = "no_environment_change_started";
    public const string EnvironmentChangeMayHaveHappenedReleaseReason = "environment_change_may_have_happened";
    public const string EnvironmentAdmittedReleaseReason = "environment_admitted";
    public const string OwnerReleasedLeaseOperation = "environment-install owner released lease";

    // One environment_install_leases row exactly as stored, field order
    // mirroring the shared SELECT list in UnreleasedById and LiveForSite.
    sealed record EnvironmentInstallLeaseStoredRow(
        string Lease_Id,
        string Machine_Generation,
        string Interpreter,
        string Site_Id,
        string Mutation_Roots_Json,
        long Fencing_Token,
        string Owner_Attempt_Id,
        string Owner_Actor,
        long Issued_At_Ms,
        long Deadline_Ms);

    public static RegistryResult<EnvironmentInstallLease> Acquire(
        RegistryWriter writer,
        EnvironmentInstallLeaseAcquire request)
    {
        if (string.IsNullOrWhiteSpace(request.MachineGeneration)
            || string.IsNullOrWhiteSpace(request.Interpreter)
            || string.IsNullOrWhiteSpace(request.SiteId)
            || string.IsNullOrWhiteSpace(request.OwnerAttemptId)
            || string.IsNullOrWhiteSpace(request.OwnerActor)
            || request.MutationRoots is null
            || request.MutationRoots.Count == 0
            || request.LifetimeMs <= 0)
        {
            return Reject(
                "an environment install lease requires machine generation, interpreter, Site, mutation roots, attempt, actor, and a positive lifetime");
        }

        var roots = request.MutationRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => root.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(root => root, StringComparer.Ordinal)
            .ToArray();
        if (roots.Length == 0)
        {
            return Reject("an environment install lease names at least one mutation root");
        }

        return writer.WriteImmediate(connection => AcquireInTransaction(connection, request, roots));
    }

    public static RegistryResult<string> Release(
        RegistryWriter writer,
        string leaseId,
        string ownerAttemptId,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(leaseId) || string.IsNullOrWhiteSpace(ownerAttemptId))
        {
            return RegistryResult<string>.Rejected(
                new RegistryRefusal("refused", "invalid_request", "releasing an install lease requires the lease id and owner attempt"));
        }

        return writer.WriteImmediate(connection => ReleaseInTransaction(
            connection,
            leaseId,
            ownerAttemptId,
            string.IsNullOrWhiteSpace(reason) ? NoEnvironmentChangeStartedReleaseReason : reason));
    }

    public static RegistryResult<EnvironmentInstallLeaseReconcileReleaseResult> ReconcileRelease(
        RegistryWriter writer,
        EnvironmentInstallLeaseReconcileRelease request)
    {
        if (string.IsNullOrWhiteSpace(request.LeaseId)
            || string.IsNullOrWhiteSpace(request.OwnerAttemptId)
            || string.IsNullOrWhiteSpace(request.ObserverActor)
            || string.IsNullOrWhiteSpace(request.ObservedOutcome)
            || string.IsNullOrWhiteSpace(request.ObservationReceiptJson))
        {
            return RejectReconcileRelease(
                "a reconcile release names the lease, owner attempt, observer, observed outcome, and observation receipt");
        }

        return writer.WriteImmediate(connection => ReconcileReleaseInTransaction(connection, request));
    }

    public static EnvironmentInstallLease? UnreleasedById(
        SqliteConnection connection,
        string leaseId)
    {
        if (string.IsNullOrWhiteSpace(leaseId) || !TableExists(connection, "environment_install_leases"))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT lease_id, machine_generation, interpreter, site_id, mutation_roots_json,
                   fencing_token, owner_attempt_id, owner_actor, issued_at_ms, deadline_ms
            FROM environment_install_leases
            WHERE lease_id = $lease AND released_at_ms IS NULL
            """;
        command.Parameters.AddWithValue("$lease", leaseId);
        using var row = command.ExecuteReader();
        var storedRow = row.Parse<EnvironmentInstallLeaseStoredRow>().FirstOrDefault();
        return storedRow is null ? null : ReadLease(storedRow);
    }

    public static IReadOnlyList<EnvironmentInstallLease> LiveForSite(
        SqliteConnection connection,
        string siteId)
    {
        var leases = new List<EnvironmentInstallLease>();
        if (string.IsNullOrWhiteSpace(siteId) || !TableExists(connection, "environment_install_leases"))
        {
            return leases;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT lease_id, machine_generation, interpreter, site_id, mutation_roots_json,
                   fencing_token, owner_attempt_id, owner_actor, issued_at_ms, deadline_ms
            FROM environment_install_leases
            WHERE site_id = $site AND released_at_ms IS NULL
            ORDER BY fencing_token
            """;
        command.Parameters.AddWithValue("$site", siteId);
        using var reader = command.ExecuteReader();
        leases.AddRange(reader.Parse<EnvironmentInstallLeaseStoredRow>().Select(ReadLease));

        return leases;
    }

    sealed record AcquireInTransactionStoredHeld(
        string Lease_Id, string Owner_Attempt_Id, long Fencing_Token, long Deadline_Ms);

    static RegistryResult<EnvironmentInstallLease> AcquireInTransaction(
        SqliteConnection connection,
        EnvironmentInstallLeaseAcquire request,
        IReadOnlyList<string> roots)
    {
        if (!TableExists(connection, "environment_install_leases"))
        {
            return Reject(
                "Registry Home has no environment_install_leases table; install the current control set before mutating an environment");
        }

        var nowMs = DatabaseNowMs(connection);

        foreach (var root in roots)
        {
            using var held = connection.CreateCommand();
            held.CommandText =
                """
                SELECT r.lease_id, l.owner_attempt_id, l.fencing_token, l.deadline_ms
                FROM environment_install_lease_roots AS r
                JOIN environment_install_leases AS l ON l.lease_id = r.lease_id
                WHERE r.machine_generation = $generation AND r.mutation_root = $root
                """;
            held.Parameters.AddWithValue("$generation", request.MachineGeneration);
            held.Parameters.AddWithValue("$root", root);
            using var row = held.ExecuteReader();
            var heldRow = row.Parse<AcquireInTransactionStoredHeld>().FirstOrDefault();
            if (heldRow is not null)
            {
                if (heldRow.Deadline_Ms <= nowMs)
                {
                    return Reject(
                        $"environment install lease {heldRow.Lease_Id} exceeded its deadline at {heldRow.Deadline_Ms} "
                        + $"but still owns {root} on this machine (attempt {heldRow.Owner_Attempt_Id}, fencing token {heldRow.Fencing_Token}); "
                        + "reconcile or explicitly release the prior install before another installer mutates the same root");
                }

                return Reject(
                    $"environment install lease {heldRow.Lease_Id} already owns {root} on this machine "
                    + $"(attempt {heldRow.Owner_Attempt_Id}, fencing token {heldRow.Fencing_Token}) until {heldRow.Deadline_Ms}; "
                    + "a second installer must wait or refuse, not run uv at the same time");
            }
        }

        var token = NextFencingToken(connection, request.MachineGeneration);
        var leaseId = "lease:" + Guid.NewGuid().ToString("N");
        var deadlineMs = nowMs + request.LifetimeMs;
        var rootsJson = JsonSerializer.Serialize(roots);

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT INTO environment_install_leases (
                    lease_id, machine_generation, interpreter, site_id, mutation_roots_json,
                    fencing_token, owner_attempt_id, owner_actor, issued_at_ms, deadline_ms,
                    released_at_ms, release_reason
                ) VALUES (
                    $id, $generation, $interpreter, $site, $roots,
                    $token, $attempt, $actor, $now, $deadline,
                    NULL, NULL
                )
                """;
            insert.Parameters.AddWithValue("$id", leaseId);
            insert.Parameters.AddWithValue("$generation", request.MachineGeneration);
            insert.Parameters.AddWithValue("$interpreter", request.Interpreter);
            insert.Parameters.AddWithValue("$site", request.SiteId);
            insert.Parameters.AddWithValue("$roots", rootsJson);
            insert.Parameters.AddWithValue("$token", token);
            insert.Parameters.AddWithValue("$attempt", request.OwnerAttemptId);
            insert.Parameters.AddWithValue("$actor", request.OwnerActor);
            insert.Parameters.AddWithValue("$now", nowMs);
            insert.Parameters.AddWithValue("$deadline", deadlineMs);
            insert.ExecuteNonQuery();
        }

        foreach (var root in roots)
        {
            using var rootInsert = connection.CreateCommand();
            rootInsert.CommandText =
                """
                INSERT INTO environment_install_lease_roots (
                    machine_generation, mutation_root, lease_id
                ) VALUES ($generation, $root, $id)
                """;
            rootInsert.Parameters.AddWithValue("$generation", request.MachineGeneration);
            rootInsert.Parameters.AddWithValue("$root", root);
            rootInsert.Parameters.AddWithValue("$id", leaseId);
            try
            {
                rootInsert.ExecuteNonQuery();
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 19)
            {
                return Reject(
                    $"environment mutation root {root} is already leased on this machine; "
                    + "a second installer must wait or refuse, not run uv at the same time");
            }
        }

        var receiptId = AppendReceipt(
            connection,
            leaseId,
            request.OwnerActor,
            "environment-install lease acquired",
            null,
            token.ToString(),
            request.OwnerAttemptId,
            token,
            nowMs);
        _ = receiptId;

        return RegistryResult<EnvironmentInstallLease>.Ok(
            new EnvironmentInstallLease(
                leaseId,
                request.MachineGeneration,
                request.Interpreter,
                request.SiteId,
                roots,
                token,
                request.OwnerAttemptId,
                request.OwnerActor,
                nowMs,
                deadlineMs));
    }

    sealed record ReleaseInTransactionStoredRow(
        string Owner_Attempt_Id, string Owner_Actor, long Fencing_Token, long? Released_At_Ms,
        string? Release_Reason);

    static RegistryResult<string> ReleaseInTransaction(
        SqliteConnection connection,
        string leaseId,
        string ownerAttemptId,
        string reason)
    {
        if (!TableExists(connection, "environment_install_leases"))
        {
            return RegistryResult<string>.Rejected(
                new RegistryRefusal(
                    "refused",
                    "invalid_request",
                    "Registry Home has no environment_install_leases table; install the current control set"));
        }

        string ownerActor;
        long fencingToken;
        using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText =
                """
                SELECT owner_attempt_id, owner_actor, fencing_token, released_at_ms, release_reason
                FROM environment_install_leases
                WHERE lease_id = $id
                """;
            lookup.Parameters.AddWithValue("$id", leaseId);
            using var row = lookup.ExecuteReader();
            var storedRow = row.Parse<ReleaseInTransactionStoredRow>().FirstOrDefault();
            if (storedRow is null)
            {
                return RegistryResult<string>.Rejected(
                    new RegistryRefusal("refused", "not_found", $"environment install lease {leaseId} is not in this Registry Home"));
            }

            var leaseOwnerAttemptId = storedRow.Owner_Attempt_Id;
            if (leaseOwnerAttemptId != ownerAttemptId)
            {
                return RegistryResult<string>.Rejected(
                    new RegistryRefusal(
                        "refused",
                        "invalid_request",
                        $"environment install lease {leaseId} belongs to attempt {leaseOwnerAttemptId}, not {ownerAttemptId}"));
            }

            if (storedRow.Released_At_Ms is not null)
            {
                return RegistryResult<string>.Ok(storedRow.Release_Reason ?? "replayed");
            }

            ownerActor = storedRow.Owner_Actor;
            fencingToken = storedRow.Fencing_Token;
        }

        DeleteRoots(connection, leaseId);
        var nowMs = DatabaseNowMs(connection);
        using (var update = connection.CreateCommand())
        {
            update.CommandText =
                """
                UPDATE environment_install_leases
                SET released_at_ms = $now, release_reason = $reason
                WHERE lease_id = $id AND released_at_ms IS NULL
                """;
            update.Parameters.AddWithValue("$now", nowMs);
            update.Parameters.AddWithValue("$reason", reason);
            update.Parameters.AddWithValue("$id", leaseId);
            update.ExecuteNonQuery();
        }

        AppendReceipt(
            connection,
            leaseId,
            ownerActor,
            OwnerReleasedLeaseOperation,
            fencingToken.ToString(),
            reason,
            ownerAttemptId,
            fencingToken,
            nowMs,
            observedOutcome: reason);

        return RegistryResult<string>.Ok(reason);
    }

    sealed record ReconcileReleaseLookupStoredRow(
        string Lease_Id,
        string Machine_Generation,
        string Interpreter,
        string Site_Id,
        string Mutation_Roots_Json,
        long Fencing_Token,
        string Owner_Attempt_Id,
        string Owner_Actor,
        long Issued_At_Ms,
        long Deadline_Ms,
        long? Released_At_Ms,
        string? Release_Reason);

    static RegistryResult<EnvironmentInstallLeaseReconcileReleaseResult> ReconcileReleaseInTransaction(
        SqliteConnection connection,
        EnvironmentInstallLeaseReconcileRelease request)
    {
        if (!TableExists(connection, "environment_install_leases"))
        {
            return RejectReconcileRelease(
                "Registry Home has no environment_install_leases table; install the current control set");
        }

        using var lookup = connection.CreateCommand();
        lookup.CommandText =
            """
            SELECT lease_id, machine_generation, interpreter, site_id, mutation_roots_json,
                   fencing_token, owner_attempt_id, owner_actor, issued_at_ms, deadline_ms,
                   released_at_ms, release_reason
            FROM environment_install_leases
            WHERE lease_id = $lease
            """;
        lookup.Parameters.AddWithValue("$lease", request.LeaseId);
        using var row = lookup.ExecuteReader();
        var storedRow = row.Parse<ReconcileReleaseLookupStoredRow>().FirstOrDefault();
        row.Close();
        if (storedRow is null)
        {
            return RejectReconcileRelease($"environment install lease {request.LeaseId} is not in this Registry Home");
        }

        var lease = new EnvironmentInstallLease(
            storedRow.Lease_Id,
            storedRow.Machine_Generation,
            storedRow.Interpreter,
            storedRow.Site_Id,
            JsonSerializer.Deserialize<string[]>(storedRow.Mutation_Roots_Json) ?? [],
            storedRow.Fencing_Token,
            storedRow.Owner_Attempt_Id,
            storedRow.Owner_Actor,
            storedRow.Issued_At_Ms,
            storedRow.Deadline_Ms);
        var releasedAt = storedRow.Released_At_Ms;
        var priorReleaseReason = storedRow.Release_Reason;

        if (lease.OwnerAttemptId != request.OwnerAttemptId)
        {
            return RejectReconcileRelease(
                $"environment install lease {lease.LeaseId} belongs to attempt {lease.OwnerAttemptId}, not {request.OwnerAttemptId}");
        }

        if (releasedAt is not null)
        {
            return RegistryResult<EnvironmentInstallLeaseReconcileReleaseResult>.Ok(new(
                lease.LeaseId,
                lease.OwnerAttemptId,
                lease.FencingToken,
                "replayed_release",
                priorReleaseReason ?? "released",
                ""));
        }

        var nowMs = DatabaseNowMs(connection);
        if (lease.DeadlineMs > nowMs)
        {
            return RejectReconcileRelease(
                $"environment install lease {lease.LeaseId} has not exceeded its deadline {lease.DeadlineMs}; "
                + "only the owning install attempt may release an active lease");
        }

        var acquisitionPolicy = AcquisitionPolicyRevision(connection, lease.LeaseId);
        if (acquisitionPolicy != PolicyRevision)
        {
            return RejectReconcileRelease(
                $"environment install lease {lease.LeaseId} was acquired under "
                + $"{acquisitionPolicy ?? "unknown policy"}, not {PolicyRevision}; "
                + "it has no process marker contract, so /proc absence cannot prove the old installer is gone");
        }

        var observationRefusal = ValidateReconciliationObservation(lease, request);
        if (observationRefusal is { } refusedObservation)
        {
            return RegistryResult<EnvironmentInstallLeaseReconcileReleaseResult>.Rejected(refusedObservation);
        }

        DeleteRoots(connection, lease.LeaseId);
        var releaseReason = "reconciled: " + request.ObservedOutcome;
        using (var update = connection.CreateCommand())
        {
            update.CommandText =
                """
                UPDATE environment_install_leases
                SET released_at_ms = $now, release_reason = $reason
                WHERE lease_id = $id AND released_at_ms IS NULL
                """;
            update.Parameters.AddWithValue("$now", nowMs);
            update.Parameters.AddWithValue("$reason", releaseReason);
            update.Parameters.AddWithValue("$id", lease.LeaseId);
            update.ExecuteNonQuery();
        }

        var transitionReceipt = AppendReceipt(
            connection,
            lease.LeaseId,
            request.ObserverActor,
            "environment-install lease reconciled release",
            lease.FencingToken.ToString(),
            "released:" + request.ObservedOutcome,
            lease.OwnerAttemptId,
            lease.FencingToken,
            nowMs,
            ObservationDigest(request.ObservationReceiptJson),
            request.ObservedOutcome);
        return RegistryResult<EnvironmentInstallLeaseReconcileReleaseResult>.Ok(new(
            lease.LeaseId,
            lease.OwnerAttemptId,
            lease.FencingToken,
            "reconciled_release",
            releaseReason,
            transitionReceipt));
    }

    static void DeleteRoots(SqliteConnection connection, string leaseId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM environment_install_lease_roots WHERE lease_id = $id";
        command.Parameters.AddWithValue("$id", leaseId);
        command.ExecuteNonQuery();
    }

    static long NextFencingToken(SqliteConnection connection, string machineGeneration)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COALESCE(MAX(fencing_token), 0) FROM environment_install_leases
            WHERE machine_generation = $generation
            """;
        command.Parameters.AddWithValue("$generation", machineGeneration);
        return Convert.ToInt64(command.ExecuteScalar()) + 1;
    }

    static EnvironmentInstallLease ReadLease(EnvironmentInstallLeaseStoredRow row)
    {
        var roots = JsonSerializer.Deserialize<string[]>(row.Mutation_Roots_Json) ?? [];
        return new EnvironmentInstallLease(
            row.Lease_Id,
            row.Machine_Generation,
            row.Interpreter,
            row.Site_Id,
            roots,
            row.Fencing_Token,
            row.Owner_Attempt_Id,
            row.Owner_Actor,
            row.Issued_At_Ms,
            row.Deadline_Ms);
    }

    static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    static string? AcquisitionPolicyRevision(SqliteConnection connection, string leaseId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT policy_revision
            FROM transition_receipts
            WHERE object_kind = $kind
              AND object_id = $lease
              AND operation_name = 'environment-install lease acquired'
            ORDER BY object_sequence ASC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$kind", ObjectKind);
        command.Parameters.AddWithValue("$lease", leaseId);
        return command.ExecuteScalar() as string;
    }

    static RegistryRefusal? ValidateReconciliationObservation(
        EnvironmentInstallLease lease,
        EnvironmentInstallLeaseReconcileRelease request)
    {
        if (request.ObservedOutcome != "owner_process_absent")
        {
            return ReconcileRefusal(
                $"environment install lease reconciliation observed {request.ObservedOutcome}, not owner_process_absent");
        }

        try
        {
            using var document = JsonDocument.Parse(request.ObservationReceiptJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ReconcileRefusal("environment install lease observation receipt is not a JSON object");
            }

            foreach (var (name, expected) in new (string Name, string Expected)[]
                     {
                         ("schema", "fern.environment_install_lease_process_observation.v1"),
                         ("lease_id", lease.LeaseId),
                         ("site_id", lease.SiteId),
                         ("machine_generation", lease.MachineGeneration),
                         ("interpreter", lease.Interpreter),
                         ("owner_attempt_id", lease.OwnerAttemptId),
                         ("observer_actor", request.ObserverActor),
                         ("process_observer", "linux_proc_environ_v1"),
                         ("process_marker_name", OwnerAttemptProcessMarker),
                         ("process_marker_value", lease.OwnerAttemptId),
                         ("observed_outcome", request.ObservedOutcome),
                     })
            {
                if (!root.TryGetProperty(name, out var property)
                    || property.ValueKind != JsonValueKind.String
                    || property.GetString() != expected)
                {
                    return ReconcileRefusal(
                        $"environment install lease observation receipt field {name} is not {expected}");
                }
            }

            if (!root.TryGetProperty("live_matches", out var matches)
                || matches.ValueKind != JsonValueKind.Array)
            {
                return ReconcileRefusal(
                    "environment install lease observation receipt is missing live_matches array");
            }
            if (matches.GetArrayLength() != 0)
            {
                return ReconcileRefusal(
                    "environment install lease observation receipt still has live_matches; "
                    + "a live owner process cannot release its mutation roots through reconciliation");
            }
        }
        catch (JsonException error)
        {
            return ReconcileRefusal(
                "environment install lease observation receipt is invalid JSON: " + error.Message);
        }

        return null;
    }

    static string AppendReceipt(
        SqliteConnection connection,
        string leaseId,
        string actorId,
        string operation,
        string? prior,
        string next,
        string attemptId,
        long fencingToken,
        long nowMs,
        string? observationDigest = null,
        string? observedOutcome = null)
    {
        var written = Journals.AppendTransitionReceiptInOpenTransaction(
            connection,
            new TransitionReceiptWrite(
                OperationName: operation,
                ObjectKind: ObjectKind,
                ObjectId: leaseId,
                PriorState: prior,
                NextState: next,
                ActorKind: "cli",
                ActorId: actorId,
                InputIdentityJson: JsonSerializer.Serialize(new
                {
                    lease_id = leaseId,
                    attempt_id = attemptId,
                    observation_receipt_digest = observationDigest,
                    observed_outcome = observedOutcome,
                }),
                OutputIdentityJson: JsonSerializer.Serialize(new
                {
                    lease_id = leaseId,
                    fencing_token = fencingToken,
                    owner_attempt_process_marker = OwnerAttemptProcessMarker,
                    owner_attempt_process_marker_value = attemptId,
                    release_reason = next,
                    release_outcome = observedOutcome,
                }),
                PolicyRevision: PolicyRevision,
                CorrelationId: leaseId,
                OccurredAtMs: nowMs,
                AttemptId: attemptId,
                FencingEpoch: fencingToken));
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

    static string ObservationDigest(string observationReceiptJson) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(observationReceiptJson)));

    static long DatabaseNowMs(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(unixepoch('subsec') * 1000 AS INTEGER)";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static RegistryResult<EnvironmentInstallLease> Reject(string detail) =>
        RegistryResult<EnvironmentInstallLease>.Rejected(
            new RegistryRefusal("refused", "invalid_request", detail));

    static RegistryResult<EnvironmentInstallLeaseReconcileReleaseResult> RejectReconcileRelease(string detail) =>
        RegistryResult<EnvironmentInstallLeaseReconcileReleaseResult>.Rejected(ReconcileRefusal(detail));

    static RegistryRefusal ReconcileRefusal(string detail) =>
        new("refused", "invalid_request", detail);
}
