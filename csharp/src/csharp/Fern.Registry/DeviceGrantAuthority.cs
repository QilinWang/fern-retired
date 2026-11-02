using Microsoft.Data.Sqlite;

namespace Fern.Registry;

/// <summary>
/// The durable key to one physical GPU. Capacity counting says how many may
/// fly; a grant says WHO holds the card right now. The owner is a Cell
/// Attempt or an environment Install Attempt -- never a process, PID, SSH
/// connection, or Site profile. Two profiles sharing one GPU UUID contend for
/// the same grants; an environment can never mint capacity.
/// </summary>
public sealed record DeviceGrant(
    string GrantId,
    string DeviceId,
    long GrantGeneration,
    string OwnerKind,
    string OwnerAttemptId,
    string SiteId,
    long GrantedAtMs);

public static class DeviceGrantAuthority
{
    /// <summary>
    /// Grant the device to one owner, or refuse naming every current holder.
    /// <paramref name="maxConcurrent"/> is the device's declared stage
    /// capacity (MAX(max_gpu_stages) across the profiles sharing the UUID,
    /// the same rule the capacity counter uses); one on every card today.
    /// The generation is per-device monotone across releases, so a stale
    /// receipt carrying an old generation stays checkably non-authorizing.
    /// </summary>
    public static RegistryResult<DeviceGrant> Acquire(
        RegistryWriter writer,
        string deviceId,
        string siteId,
        string ownerKind,
        string ownerAttemptId,
        long maxConcurrent)
    {
        if (string.IsNullOrWhiteSpace(deviceId)
            || string.IsNullOrWhiteSpace(siteId)
            || string.IsNullOrWhiteSpace(ownerAttemptId)
            || ownerKind is not ("cell" or "environment_install")
            || maxConcurrent < 1)
        {
            return Reject(
                "a device grant requires the physical device id, the Site that realized it, "
                + "an owner attempt, owner kind cell or environment_install, and a positive capacity");
        }

        return writer.WriteImmediate(connection =>
            AcquireInTransaction(connection, deviceId, siteId, ownerKind, ownerAttemptId, maxConcurrent));
    }

    /// <summary>
    /// The same acquisition inside a caller-owned transaction, for authorities
    /// (cell open) whose grant must commit or roll back with the rest of
    /// their decision.
    /// </summary>
    public static RegistryResult<DeviceGrant> AcquireInTransaction(
        SqliteConnection connection,
        string deviceId,
        string siteId,
        string ownerKind,
        string ownerAttemptId,
        long maxConcurrent)
    {
        {
            if (!TableExists(connection, "device_grants"))
            {
                return Reject("Registry Home has no device_grants table; install the current control set first");
            }

            var live = LiveForDevice(connection, deviceId);
            if (live.Count >= maxConcurrent)
            {
                var holders = string.Join(", ", live.Select(grant =>
                    $"{grant.OwnerKind} {grant.OwnerAttemptId} (generation {grant.GrantGeneration}, via {grant.SiteId})"));
                return Reject(
                    $"device {deviceId} has {live.Count} live grant(s) of {maxConcurrent}: {holders}; "
                    + "a lost transport does not release a grant -- reconcile the owner or wait for its release");
            }

            long nextGeneration;
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT COALESCE(MAX(grant_generation), 0) + 1 FROM device_grants WHERE device_id = $device";
                command.Parameters.AddWithValue("$device", deviceId);
                nextGeneration = Convert.ToInt64(command.ExecuteScalar());
            }

            var grant = new DeviceGrant(
                "device-grant:" + Guid.NewGuid().ToString("N"),
                deviceId,
                nextGeneration,
                ownerKind,
                ownerAttemptId,
                siteId,
                NowMs(connection));
            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO device_grants (
                    grant_id, device_id, grant_generation, owner_kind, owner_attempt_id,
                    site_id, granted_at_ms)
                VALUES ($id, $device, $generation, $kind, $owner, $site, $now)
                """;
            insert.Parameters.AddWithValue("$id", grant.GrantId);
            insert.Parameters.AddWithValue("$device", grant.DeviceId);
            insert.Parameters.AddWithValue("$generation", grant.GrantGeneration);
            insert.Parameters.AddWithValue("$kind", grant.OwnerKind);
            insert.Parameters.AddWithValue("$owner", grant.OwnerAttemptId);
            insert.Parameters.AddWithValue("$site", grant.SiteId);
            insert.Parameters.AddWithValue("$now", grant.GrantedAtMs);
            insert.ExecuteNonQuery();
            return RegistryResult<DeviceGrant>.Ok(grant);
        }
    }

    /// <summary>The owner hands the card back. Only the owner may.</summary>
    public static RegistryResult<string> Release(
        RegistryWriter writer,
        string grantId,
        string ownerAttemptId,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(grantId) || string.IsNullOrWhiteSpace(ownerAttemptId))
        {
            return RegistryResult<string>.Rejected(new RegistryRefusal(
                "refused", "invalid_request", "releasing a device grant requires the grant id and its owner attempt"));
        }
        return writer.WriteImmediate(connection => ReleaseInTransaction(
            connection, grantId,
            requiredOwner: ownerAttemptId,
            string.IsNullOrWhiteSpace(reason) ? "released" : reason));
    }

    /// <summary>
    /// Authority-side release after reconciliation proved the owner's work
    /// terminal or absent. A separate operation from the owner's own release
    /// on purpose: loss of observation is not proof of death, so this path
    /// must state what the reconciliation concluded.
    /// </summary>
    public static RegistryResult<string> ReconcileRelease(
        RegistryWriter writer,
        string grantId,
        string reconciledOutcome)
    {
        if (string.IsNullOrWhiteSpace(grantId) || string.IsNullOrWhiteSpace(reconciledOutcome))
        {
            return RegistryResult<string>.Rejected(new RegistryRefusal(
                "refused", "invalid_request",
                "a reconcile release names the grant and the reconciled outcome that justifies it"));
        }
        return writer.WriteImmediate(connection =>
            ReleaseInTransaction(connection, grantId, requiredOwner: null, "reconciled: " + reconciledOutcome));
    }

    /// <summary>
    /// The same MAX(max_gpu_stages) rule the capacity counter uses across
    /// every Site profile sharing this UUID. Returns 1 when the device is
    /// not yet registered, so an unregistered CUDA Site cannot mint extra
    /// capacity by omitting the row.
    /// </summary>
    // CHG 2026-09-11 night (slice 6): `DeclaredCapacity`, which read a card's
    // declared capacity from the retired worker_sites table, is retired with
    // that table; on the one-store schema a declared card takes one cell at a
    // time, RegistryOperations.DeviceCapacity, the capacity the claim grants
    // against (slice 3).

    /// <summary>
    /// O9: environment mutation does not own the GPU. A CUDA profile's
    /// torch.cuda / runtime observation borrows the card for that check
    /// only, as an <c>environment_install</c> owner, and must release it
    /// before uv pip or snapshot repair. A CPU profile returns no grant.
    /// A CUDA profile whose Site has no registered GPU UUID is refused
    /// rather than occupying an untracked card.
    /// </summary>
    public static RegistryResult<DeviceGrant?> BeginInstallCudaValidation(
        RegistryWriter writer,
        string siteId,
        string profileDevice,
        string ownerAttemptId)
    {
        if (profileDevice != "cuda")
        {
            return RegistryResult<DeviceGrant?>.Ok(null);
        }

        if (string.IsNullOrWhiteSpace(siteId) || string.IsNullOrWhiteSpace(ownerAttemptId))
        {
            return RegistryResult<DeviceGrant?>.Rejected(new RegistryRefusal(
                "refused",
                "invalid_request",
                "CUDA validation grant requires the Site id and the install attempt"));
        }

        // CHG 2026-09-11 night (slice 6, row 35): the card comes from the
        // registered Site's own declaration (register-site --device or
        // --observe-cards, slice 3), not from a MachineAdmission: on the
        // one-store schema the Site row is the machine's admission. The first
        // declared card nobody holds is borrowed; a Site that declares none is
        // refused rather than occupying an untracked card.
        string? capabilitiesJson;
        using (var read = writer.Connection.CreateCommand())
        {
            read.CommandText = "SELECT capabilities_json FROM sites WHERE name = $name";
            read.Parameters.AddWithValue("$name", siteId);
            capabilitiesJson = read.ExecuteScalar() as string;
        }
        if (capabilitiesJson is null)
        {
            return RegistryResult<DeviceGrant?>.Rejected(new RegistryRefusal(
                "refused",
                "invalid_request",
                $"CUDA validation on Site {siteId} requires that Site to be registered (register-site) so the install cannot occupy an untracked card"));
        }
        var cards = RegistryOperations.DevicesIn(capabilitiesJson);
        if (cards.Count == 0)
        {
            return RegistryResult<DeviceGrant?>.Rejected(new RegistryRefusal(
                "refused",
                "invalid_request",
                $"CUDA validation on Site {siteId} requires a declared card (register-site --device or --observe-cards) so the install cannot occupy an untracked card"));
        }
        var deviceId = cards.FirstOrDefault(card => LiveForDevice(writer.Connection, card).Count < RegistryOperations.DeviceCapacity) ?? cards[0];
        var acquired = Acquire(
            writer,
            deviceId,
            siteId,
            "environment_install",
            ownerAttemptId,
            RegistryOperations.DeviceCapacity);
        if (acquired.TryGetValue(out var grant))
        {
            return RegistryResult<DeviceGrant?>.Ok(grant);
        }

        acquired.TryGetRefusal(out var refusal);
        return RegistryResult<DeviceGrant?>.Rejected(refusal);
    }

    public static IReadOnlyList<DeviceGrant> LiveForDevice(SqliteConnection connection, string deviceId)
    {
        var grants = new List<DeviceGrant>();
        if (string.IsNullOrWhiteSpace(deviceId) || !TableExists(connection, "device_grants"))
        {
            return grants;
        }
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT grant_id, device_id, grant_generation, owner_kind, owner_attempt_id, site_id, granted_at_ms
            FROM device_grants
            WHERE device_id = $device AND released_at_ms IS NULL
            ORDER BY grant_generation
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            grants.Add(new DeviceGrant(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6)));
        }
        return grants;
    }

    public static RegistryResult<string> ReleaseInTransaction(
        SqliteConnection connection, string grantId, string? requiredOwner, string reason)
    {
        {
            if (!TableExists(connection, "device_grants"))
            {
                return RegistryResult<string>.Rejected(new RegistryRefusal(
                    "refused", "invalid_request", "Registry Home has no device_grants table"));
            }
            string? owner = null;
            long? releasedAt = null;
            using (var lookup = connection.CreateCommand())
            {
                lookup.CommandText =
                    "SELECT owner_attempt_id, released_at_ms FROM device_grants WHERE grant_id = $id";
                lookup.Parameters.AddWithValue("$id", grantId);
                using var row = lookup.ExecuteReader();
                if (row.Read())
                {
                    owner = row.GetString(0);
                    releasedAt = row.IsDBNull(1) ? null : row.GetInt64(1);
                }
            }
            if (owner is null)
            {
                return RegistryResult<string>.Rejected(new RegistryRefusal(
                    "refused", "invalid_request", $"device grant {grantId} does not exist"));
            }
            if (releasedAt is not null)
            {
                // Releasing a released grant is an idempotent no-op, never an error.
                return RegistryResult<string>.Ok(grantId);
            }
            if (requiredOwner is not null && !string.Equals(owner, requiredOwner, StringComparison.Ordinal))
            {
                return RegistryResult<string>.Rejected(new RegistryRefusal(
                    "refused", "invalid_request",
                    $"device grant {grantId} is owned by {owner}, not {requiredOwner}; "
                    + "a non-owner ends a grant only through reconcile-release with its reconciled outcome"));
            }
            using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE device_grants
                SET released_at_ms = $now, release_reason = $reason
                WHERE grant_id = $id AND released_at_ms IS NULL
                """;
            update.Parameters.AddWithValue("$id", grantId);
            update.Parameters.AddWithValue("$now", NowMs(connection));
            update.Parameters.AddWithValue("$reason", reason);
            update.ExecuteNonQuery();
            return RegistryResult<string>.Ok(grantId);
        }
    }

    static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    static long NowMs(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(unixepoch('subsec') * 1000 AS INTEGER)";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static RegistryResult<DeviceGrant> Reject(string detail) =>
        RegistryResult<DeviceGrant>.Rejected(new RegistryRefusal("refused", "invalid_request", detail));
}
