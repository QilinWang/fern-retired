using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fern.RunData;
using Microsoft.Data.Sqlite;

namespace Fern.Registry;

public sealed record ArtifactHoldPermit(string HoldId, string Sha256, string HoldKind, string Capability);

public sealed record AcquireHoldReceipt(string HoldId, ArtifactHoldPermit? Permit = null);

public sealed record ReleaseHoldReceipt(string HoldId);

public sealed record ArtifactGcPreview(
    string LocationId,
    string StorageSiteId,
    bool MayPrepare,
    IReadOnlyList<string> BlockingReasons,
    string AutomaticGc,
    string? RetentionPolicy,
    string? RetentionReason);

public sealed record ArtifactGcPermit(string GcClaimId, string LocationId, string StorageSiteId, string Capability);

public sealed record PrepareGcReceipt(string GcClaimId, string LocationId, ArtifactGcPermit? Permit = null);

public sealed record ReconcileGcReceipt(IReadOnlyList<string> PreparedClaimIds, IReadOnlyList<string> DeletingWithoutClaim);

public sealed record OptionalRetentionClass(
    string Role,
    string Filename,
    long FileCount,
    long TotalBytes,
    string StorageSite,
    string MaterializationRequirement,
    string GcEligibility);

public sealed record LegacyLocalGcExclusion(
    string Policy,
    string AutomaticGc,
    string Reason,
    long FileCount,
    long TotalBytes,
    double TotalGib);

public sealed record OptionalRetentionAudit(
    string Schema,
    IReadOnlyList<OptionalRetentionClass> Classes,
    LegacyLocalGcExclusion LegacyLocal);

public sealed record SealedArtifactFile(
    string SnapshotId,
    long CellId,
    long AttemptId,
    string? RunHash,
    string ArtifactDir,
    string RelativePath,
    string Role,
    long ByteCount,
    string Sha256,
    bool RequiredForAcceptance)
{
    /// <summary>
    /// Some rows are absolute. Some are relative to the repository root. An
    /// absolute directory replaces the base; a relative one never joins
    /// against the process working directory.
    /// </summary>
    public string ResolvedPath(string repositoryRoot)
    {
        var stored = ArtifactDir;
        return Path.IsPathRooted(stored)
            ? Path.Combine(stored, RelativePath)
            : Path.Combine(repositoryRoot, stored, RelativePath);
    }
}

public sealed record SealedArtifactFilesReport(string Schema, IReadOnlyList<SealedArtifactFile> Files);

public sealed record PresentLocationWrite(
    string Sha256,
    long ByteCount,
    string StorageSiteId,
    string Path,
    string Stamp);

public sealed record AcquireHoldWrite(
    string Sha256,
    string HoldKind,
    string Reason,
    string ActorKind,
    string ActorId,
    string IdempotencyKey);

public sealed record ReleaseHoldWrite(
    ArtifactHoldPermit Permit,
    string ActorKind,
    string ActorId,
    string IdempotencyKey);

public sealed record PrepareGcWrite(
    string LocationId,
    string ActorKind,
    string ActorId,
    string IdempotencyKey);


public static class ArtifactRetentionAuthority
{
    public const string HoldObjectKind = "csharp:artifact-hold";
    public const string LocationObjectKind = "csharp:artifact-location";
    public const string ReconcileScope = "csharp:artifact:gc-reconcile";
    public const string PolicyRevision = "fern.artifact_retention.v1";
    public const string LegacyLocalStorageSite = "legacy-local";
    public const string LegacyLocalRetentionPolicy = "legacy_manual_only";
    public const string LegacyLocalAutomaticGc = "forbidden";
    public const string LegacyLocalRetentionReason = "locations predate verified storage-site identity";
    public const string OptionalRetentionSchema = "fern.acceptance_optional_retention_audit.v1";
    public const string SealedArtifactFilesSchema = "fern.sealed_artifact_files.v1";

    static readonly HashSet<string> KnownHoldKinds = new(StringComparer.Ordinal)
    {
        "scientific_audit",
        "forensic",
        "transfer",
        "operator",
    };

    public static SealedArtifactFilesReport SealedArtifactFiles(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        using (var orphan = connection.CreateCommand())
        {
            orphan.Transaction = transaction;
            orphan.CommandTimeout = 5;
            orphan.CommandText =
                """
                SELECT file.snapshot_id
                FROM artifact_snapshot_files file
                LEFT JOIN artifact_snapshots snapshot ON snapshot.snapshot_id = file.snapshot_id
                WHERE snapshot.snapshot_id IS NULL
                LIMIT 1
                """;
            if (orphan.ExecuteScalar() is string missingSnapshot)
            {
                throw new RegistryFault(
                    $"artifact_snapshot_files names snapshot {missingSnapshot}, which Registry Home does not have");
            }
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 5;
        command.CommandText =
            """
            SELECT snapshot.snapshot_id, snapshot.cell_id, snapshot.attempt_id,
                   snapshot.run_hash, snapshot.artifact_dir,
                   file.relative_path, file.role, file.byte_count, file.sha256,
                   file.required_for_acceptance
            FROM artifact_snapshot_files file
            JOIN artifact_snapshots snapshot ON snapshot.snapshot_id = file.snapshot_id
            """;
        using var reader = command.ExecuteReader();
        var files = new List<SealedArtifactFile>();
        while (reader.Read())
        {
            files.Add(new SealedArtifactFile(
                reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6), reader.GetInt64(7),
                reader.GetString(8), reader.GetInt64(9) != 0));
        }
        transaction.Commit();
        return new SealedArtifactFilesReport(SealedArtifactFilesSchema, files);
    }

    static readonly HashSet<string> KnownFileRoles = new(StringComparer.Ordinal)
    {
        "metrics_receipt",
        "invocation",
        "execution_environment",
        "checkpoint",
        "point_forecast",
        "raw_evaluation_tensors",
        "sequence_tensors",
        "forecast_calibration",
        "quantile_diagnostics",
        "diagnostics",
        "other",
    };

    public static RegistryResult<AcquireHoldReceipt> AcquireHold(RegistryWriter writer, AcquireHoldWrite request)
    {
        var reason = request.Reason.Trim();
        if (reason.Length == 0)
        {
            return Refuse<AcquireHoldReceipt>("artifact hold reason must be non-empty");
        }

        if (!KnownHoldKinds.Contains(request.HoldKind))
        {
            return Refuse<AcquireHoldReceipt>($"artifact hold kind {request.HoldKind} is not a typed hold");
        }

        return writer.WriteImmediate(connection =>
        {
            var requestText = RequestText(("op", "artifact:hold-acquire"), ("sha256", request.Sha256), ("hold_kind", request.HoldKind), ("reason", reason));
            var scope = $"csharp:artifact:hold-acquire:{request.Sha256}";
            if (TryReplay<AcquireHoldReceipt>(connection, scope, request.ActorId, request.IdempotencyKey, requestText, out var replayed, out var refusal))
            {
                if (replayed is not null)
                {
                    return RegistryResult<AcquireHoldReceipt>.Ok(replayed with { Permit = null });
                }

                if (refusal is not { } acquiredRefusal)
                {
                    throw new RegistryFault("artifact hold replay returned neither a receipt nor a refusal");
                }

                return RegistryResult<AcquireHoldReceipt>.Rejected(acquiredRefusal);
            }

            if (!BlobExists(connection, request.Sha256))
            {
                return Refuse<AcquireHoldReceipt>($"artifact blob {request.Sha256} is not registered");
            }

            if (DeletingLocationCount(connection, request.Sha256) > 0)
            {
                return Refuse<AcquireHoldReceipt>(
                    $"artifact {request.Sha256} has a deleting location; a new hold cannot race deletion");
            }

            var nowMs = DatabaseNowMs(connection);
            var stamp = Stamp(connection, nowMs);
            var holdId = $"artifact-hold-{Guid.NewGuid():D}";
            var capability = $"hold-cap-{Guid.NewGuid():D}";
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText =
                    """
                    INSERT INTO artifact_holds (
                        hold_id, sha256, hold_kind, authority, reason, recorded_at, released_at, capability_digest
                    ) VALUES (
                        $id, $sha, $kind, $authority, $reason, $at, NULL, $cap
                    )
                    """;
                insert.Parameters.AddWithValue("$id", holdId);
                insert.Parameters.AddWithValue("$sha", request.Sha256);
                insert.Parameters.AddWithValue("$kind", request.HoldKind);
                insert.Parameters.AddWithValue("$authority", request.ActorId);
                insert.Parameters.AddWithValue("$reason", reason);
                insert.Parameters.AddWithValue("$at", stamp);
                insert.Parameters.AddWithValue("$cap", Sha256Hex(Encoding.UTF8.GetBytes(capability)));
                insert.ExecuteNonQuery();
            }

            var receiptId = AppendReceipt(
                connection,
                "artifact-hold acquired",
                HoldObjectKind,
                holdId,
                priorState: null,
                nextState: "held",
                request.ActorKind,
                request.ActorId,
                $$"""{"sha256":"{{request.Sha256}}","hold_kind":"{{request.HoldKind}}"}""",
                $$"""{"hold_id":"{{holdId}}"}""",
                holdId,
                nowMs);
            var outcome = new AcquireHoldReceipt(
                holdId,
                new ArtifactHoldPermit(holdId, request.Sha256, request.HoldKind, capability));
            RecordOutcome(connection, scope, request.ActorId, request.IdempotencyKey, "artifact:hold-acquire", requestText, """{"hold_id":""" + JsonSerializer.Serialize(holdId) + "}", receiptId, nowMs);
            return RegistryResult<AcquireHoldReceipt>.Ok(outcome);
        });
    }

    public static RegistryResult<ReleaseHoldReceipt> ReleaseHold(RegistryWriter writer, ReleaseHoldWrite request)
    {
        return writer.WriteImmediate(connection =>
        {
            var permit = request.Permit;
            var requestText = RequestText(("op", "artifact:hold-release"), ("hold_id", permit.HoldId), ("sha256", permit.Sha256));
            var scope = $"csharp:artifact:hold-release:{permit.HoldId}";
            if (TryReplay<ReleaseHoldReceipt>(connection, scope, request.ActorId, request.IdempotencyKey, requestText, out var replayed, out var refusal))
            {
                if (replayed is not null)
                {
                    return RegistryResult<ReleaseHoldReceipt>.Ok(replayed);
                }

                if (refusal is not { } releasedRefusal)
                {
                    throw new RegistryFault("artifact hold release replay returned neither a receipt nor a refusal");
                }

                return RegistryResult<ReleaseHoldReceipt>.Rejected(releasedRefusal);
            }

            string? sha256;
            string? holdKind;
            string? storedDigest;
            bool released;
            using (var load = connection.CreateCommand())
            {
                load.CommandText =
                    """
                    SELECT sha256, hold_kind, capability_digest, released_at
                    FROM artifact_holds WHERE hold_id = $id
                    """;
                load.Parameters.AddWithValue("$id", permit.HoldId);
                using var row = load.ExecuteReader();
                if (!row.Read())
                {
                    return Refuse<ReleaseHoldReceipt>($"artifact hold {permit.HoldId} is not registered");
                }

                sha256 = row.GetString(0);
                holdKind = row.GetString(1);
                storedDigest = row.IsDBNull(2) ? null : row.GetString(2);
                released = !row.IsDBNull(3);
            }

            if (sha256 != permit.Sha256 || holdKind != permit.HoldKind)
            {
                return Refuse<ReleaseHoldReceipt>("artifact hold permit does not match the stored hold");
            }

            if (storedDigest != Sha256Hex(Encoding.UTF8.GetBytes(permit.Capability)))
            {
                return Refuse<ReleaseHoldReceipt>("artifact hold permit is not the holder of this hold");
            }

            if (released)
            {
                return Refuse<ReleaseHoldReceipt>($"artifact hold {permit.HoldId} is already released");
            }

            var nowMs = DatabaseNowMs(connection);
            var stamp = Stamp(connection, nowMs);
            using (var update = connection.CreateCommand())
            {
                update.CommandText = "UPDATE artifact_holds SET released_at = $at WHERE hold_id = $id";
                update.Parameters.AddWithValue("$at", stamp);
                update.Parameters.AddWithValue("$id", permit.HoldId);
                update.ExecuteNonQuery();
            }

            var receiptId = AppendReceipt(
                connection,
                "artifact-hold released",
                HoldObjectKind,
                permit.HoldId,
                priorState: "held",
                nextState: "released",
                request.ActorKind,
                request.ActorId,
                $$"""{"hold_id":"{{permit.HoldId}}"}""",
                """{"state":"released"}""",
                permit.HoldId,
                nowMs);
            var outcome = new ReleaseHoldReceipt(permit.HoldId);
            RecordOutcome(connection, scope, request.ActorId, request.IdempotencyKey, "artifact:hold-release", requestText, RegistryJson.Serialize(outcome), receiptId, nowMs);
            return RegistryResult<ReleaseHoldReceipt>.Ok(outcome);
        });
    }

    public static ArtifactGcPreview PreviewGc(SqliteConnection connection, string locationId)
    {
        string sha256;
        string storageSiteId;
        string state;
        using (var location = connection.CreateCommand())
        {
            location.CommandText =
                """
                SELECT sha256, storage_site_id, state
                FROM artifact_blob_locations WHERE location_id = $id
                """;
            location.Parameters.AddWithValue("$id", locationId);
            using var row = location.ExecuteReader();
            if (!row.Read())
            {
                throw new RegistryFault($"artifact location {locationId} is not registered");
            }

            sha256 = row.GetString(0);
            storageSiteId = row.GetString(1);
            state = row.GetString(2);
        }

        var blocking = new List<string>();
        string? retentionPolicy = null;
        string? retentionReason = null;
        if (storageSiteId == LegacyLocalStorageSite)
        {
            retentionPolicy = LegacyLocalRetentionPolicy;
            retentionReason = LegacyLocalRetentionReason;
            blocking.Add(
                $"legacy-local automatic GC is {LegacyLocalAutomaticGc} (policy {LegacyLocalRetentionPolicy}: {LegacyLocalRetentionReason})");
        }

        if (state != "present")
        {
            blocking.Add($"location is {state}, not present");
        }

        if (SnapshotRoleIsOther(connection, sha256))
        {
            blocking.Add("unknown or Other artifact role cannot enter automatic GC");
        }

        if (!HasExplicitRetentionDeclaration(connection, sha256))
        {
            blocking.Add("no explicit retention declaration; required_for_acceptance=false is not permission to delete");
        }

        var bytesRequired = ActiveBytesRequiredCount(connection, sha256);
        if (bytesRequired > 0)
        {
            blocking.Add($"{bytesRequired} active bytes_required reference(s)");
        }

        var holds = ActiveHoldCount(connection, sha256);
        if (holds > 0)
        {
            blocking.Add($"{holds} active hold(s)");
        }

        if (PreparedGcClaim(connection, locationId) is not null)
        {
            blocking.Add("a deletion claim is already prepared");
        }

        var mayPrepare = blocking.Count == 0;
        return new ArtifactGcPreview(
            locationId,
            storageSiteId,
            mayPrepare,
            blocking,
            mayPrepare ? "allowed" : "forbidden",
            retentionPolicy,
            retentionReason);
    }

    public static RegistryResult<PrepareGcReceipt> PrepareGc(RegistryWriter writer, PrepareGcWrite request)
    {
        return writer.WriteImmediate(connection =>
        {
            var requestText = RequestText(("op", "artifact:gc-prepare"), ("location_id", request.LocationId));
            var scope = $"csharp:artifact:gc-prepare:{request.LocationId}";
            if (TryReplay<PrepareGcReceipt>(connection, scope, request.ActorId, request.IdempotencyKey, requestText, out var replayed, out var refusal))
            {
                if (replayed is not null)
                {
                    return RegistryResult<PrepareGcReceipt>.Ok(replayed with { Permit = null });
                }

                if (refusal is not { } preparedRefusal)
                {
                    throw new RegistryFault("artifact GC prepare replay returned neither a receipt nor a refusal");
                }

                return RegistryResult<PrepareGcReceipt>.Rejected(preparedRefusal);
            }

            var preview = PreviewGc(connection, request.LocationId);
            if (!preview.MayPrepare)
            {
                return Refuse<PrepareGcReceipt>(
                    $"artifact location {request.LocationId} cannot be prepared for GC: {string.Join("; ", preview.BlockingReasons)}");
            }

            var nowMs = DatabaseNowMs(connection);
            var stamp = Stamp(connection, nowMs);
            var gcClaimId = $"artifact-gc-{Guid.NewGuid():D}";
            var capability = $"gc-cap-{Guid.NewGuid():D}";
            using (var update = connection.CreateCommand())
            {
                update.CommandText = "UPDATE artifact_blob_locations SET state = 'deleting' WHERE location_id = $id";
                update.Parameters.AddWithValue("$id", request.LocationId);
                update.ExecuteNonQuery();
            }

            using (var insert = connection.CreateCommand())
            {
                insert.CommandText =
                    """
                    INSERT INTO artifact_gc_claims (
                        gc_claim_id, location_id, storage_site_id, capability_digest, state, prepared_at
                    ) VALUES (
                        $id, $location, $site, $cap, 'prepared', $at
                    )
                    """;
                insert.Parameters.AddWithValue("$id", gcClaimId);
                insert.Parameters.AddWithValue("$location", request.LocationId);
                insert.Parameters.AddWithValue("$site", preview.StorageSiteId);
                insert.Parameters.AddWithValue("$cap", Sha256Hex(Encoding.UTF8.GetBytes(capability)));
                insert.Parameters.AddWithValue("$at", stamp);
                insert.ExecuteNonQuery();
            }

            var receiptId = AppendReceipt(
                connection,
                "artifact-gc prepared",
                LocationObjectKind,
                request.LocationId,
                priorState: "present",
                nextState: "deleting",
                request.ActorKind,
                request.ActorId,
                $$"""{"location_id":"{{request.LocationId}}"}""",
                $$"""{"gc_claim_id":"{{gcClaimId}}"}""",
                gcClaimId,
                nowMs);
            var recordJson = RegistryJson.Serialize(new PrepareGcReceipt(gcClaimId, request.LocationId, null));
            RecordOutcome(connection, scope, request.ActorId, request.IdempotencyKey, "artifact:gc-prepare", requestText, recordJson, receiptId, nowMs);
            return RegistryResult<PrepareGcReceipt>.Ok(new PrepareGcReceipt(
                gcClaimId,
                request.LocationId,
                new ArtifactGcPermit(gcClaimId, request.LocationId, preview.StorageSiteId, capability)));
        });
    }

    public static RegistryResult<ReconcileGcReceipt> ReconcileGcClaims(RegistryWriter writer, long nowMs)
    {
        return writer.WriteImmediate(connection =>
        {
            var requestText = RequestText(("op", "artifact:gc-reconcile"), ("now_ms", nowMs.ToString()));
            const string actorId = "artifact-gc-reconcile";
            var idempotency = $"reconcile:{nowMs}";
            if (TryReplay<ReconcileGcReceipt>(connection, ReconcileScope, actorId, idempotency, requestText, out var replayed, out var refusal))
            {
                if (replayed is not null)
                {
                    return RegistryResult<ReconcileGcReceipt>.Ok(replayed);
                }

                if (refusal is not { } reconcileRefusal)
                {
                    throw new RegistryFault("artifact GC reconcile replay returned neither a receipt nor a refusal");
                }

                return RegistryResult<ReconcileGcReceipt>.Rejected(reconcileRefusal);
            }

            var prepared = new List<string>();
            using (var claims = connection.CreateCommand())
            {
                claims.CommandText = "SELECT gc_claim_id FROM artifact_gc_claims WHERE state = 'prepared'";
                using var row = claims.ExecuteReader();
                while (row.Read())
                {
                    prepared.Add(row.GetString(0));
                }
            }

            var deleting = new List<string>();
            using (var locations = connection.CreateCommand())
            {
                locations.CommandText = "SELECT location_id FROM artifact_blob_locations WHERE state = 'deleting'";
                using var row = locations.ExecuteReader();
                while (row.Read())
                {
                    deleting.Add(row.GetString(0));
                }
            }

            var deletingWithoutClaim = new List<string>();
            foreach (var locationId in deleting)
            {
                if (PreparedGcClaim(connection, locationId) is null)
                {
                    deletingWithoutClaim.Add(locationId);
                }
            }

            var outcome = new ReconcileGcReceipt(prepared, deletingWithoutClaim);
            RecordOutcome(
                connection,
                ReconcileScope,
                actorId,
                idempotency,
                "artifact:gc-reconcile",
                requestText,
                RegistryJson.Serialize(outcome),
                receiptId: null,
                nowMs);
            return RegistryResult<ReconcileGcReceipt>.Ok(outcome);
        });
    }

    public static OptionalRetentionAudit AuditOptionalRetention(SqliteConnection connection)
    {
        var files = new List<(string Role, string RelativePath, long ByteCount, string Sha256)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT role, relative_path, byte_count, sha256
                FROM artifact_snapshot_files
                WHERE required_for_acceptance = 0
                """;
            using var row = command.ExecuteReader();
            while (row.Read())
            {
                files.Add((row.GetString(0), row.GetString(1), row.GetInt64(2), row.GetString(3)));
            }
        }

        var sitesByDigest = new SortedDictionary<string, List<(string Site, string LocationId)>>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT sha256, storage_site_id, location_id
                FROM artifact_blob_locations WHERE state = 'present'
                """;
            using var row = command.ExecuteReader();
            while (row.Read())
            {
                var sha256 = row.GetString(0);
                if (!sitesByDigest.TryGetValue(sha256, out var sites))
                {
                    sites = [];
                    sitesByDigest[sha256] = sites;
                }

                sites.Add((row.GetString(1), row.GetString(2)));
            }
        }

        var classTotals = new SortedDictionary<(string Role, string Filename, string Site, string Materialization, string Gc), (long Count, long Bytes)>();
        foreach (var (role, relativePath, byteCount, sha256) in files)
        {
            var filename = Path.GetFileName(relativePath);
            if (string.IsNullOrEmpty(filename))
            {
                filename = relativePath;
            }

            var materialization = LiveDeclaredMaterialization(connection, sha256);
            if (!sitesByDigest.TryGetValue(sha256, out var siteRows) || siteRows.Count == 0)
            {
                AddClass(classTotals, role, filename, "unlocated", materialization, "forbidden", byteCount);
                continue;
            }

            foreach (var (site, locationId) in siteRows)
            {
                var preview = PreviewGc(connection, locationId);
                AddClass(classTotals, role, filename, site, materialization, preview.AutomaticGc, byteCount);
            }
        }

        var classes = classTotals
            .Select(entry => new OptionalRetentionClass(
                entry.Key.Role,
                entry.Key.Filename,
                entry.Value.Count,
                entry.Value.Bytes,
                entry.Key.Site,
                entry.Key.Materialization,
                entry.Key.Gc))
            .OrderBy(item => item.Role, StringComparer.Ordinal)
            .ThenBy(item => item.Filename, StringComparer.Ordinal)
            .ThenBy(item => item.StorageSite, StringComparer.Ordinal)
            .ToList();

        var legacyDigests = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT sha256 FROM artifact_blob_locations
                WHERE storage_site_id = $site AND state = 'present'
                """;
            command.Parameters.AddWithValue("$site", LegacyLocalStorageSite);
            using var row = command.ExecuteReader();
            while (row.Read())
            {
                legacyDigests.Add(row.GetString(0));
            }
        }

        var blobBytes = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT sha256, byte_count FROM artifact_blobs";
            using var row = command.ExecuteReader();
            while (row.Read())
            {
                blobBytes[row.GetString(0)] = row.GetInt64(1);
            }
        }

        long totalBytes = 0;
        foreach (var digest in legacyDigests)
        {
            totalBytes += blobBytes.GetValueOrDefault(digest);
        }

        return new OptionalRetentionAudit(
            OptionalRetentionSchema,
            classes,
            new LegacyLocalGcExclusion(
                LegacyLocalRetentionPolicy,
                LegacyLocalAutomaticGc,
                LegacyLocalRetentionReason,
                legacyDigests.Count,
                totalBytes,
                totalBytes / (1024.0 * 1024.0 * 1024.0)));
    }

    public static RegistryResult<string> RecordPresentLocation(RegistryWriter writer, PresentLocationWrite location)
    {
        return writer.WriteImmediate(connection => RecordPresentLocationInOpenTransaction(connection, location));
    }

    internal static RegistryResult<string> RecordPresentLocationInOpenTransaction(
        SqliteConnection connection,
        PresentLocationWrite location)
    {
        if (string.IsNullOrEmpty(location.StorageSiteId))
        {
            return Refuse<string>("storage site id must be a non-empty name");
        }

        using (var deleting = connection.CreateCommand())
        {
            deleting.CommandText =
                """
                SELECT COUNT(*) FROM artifact_blob_locations
                WHERE storage_site_id = $site AND path = $path AND state = 'deleting'
                """;
            deleting.Parameters.AddWithValue("$site", location.StorageSiteId);
            deleting.Parameters.AddWithValue("$path", location.Path);
            if (Convert.ToInt64(deleting.ExecuteScalar()) > 0)
            {
                return Refuse<string>(
                    $"location {location.StorageSiteId}:{location.Path} is deleting; a new present copy cannot race GC");
            }
        }

        using (var blob = connection.CreateCommand())
        {
            blob.CommandText = "SELECT byte_count FROM artifact_blobs WHERE sha256 = $sha";
            blob.Parameters.AddWithValue("$sha", location.Sha256);
            var stored = blob.ExecuteScalar();
            if (stored is not null && Convert.ToInt64(stored) != location.ByteCount)
            {
                return Refuse<string>(
                    $"artifact digest {location.Sha256} is already registered with {Convert.ToInt64(stored)} bytes, not {location.ByteCount}");
            }

            if (stored is null)
            {
                using var insertBlob = connection.CreateCommand();
                insertBlob.CommandText =
                    "INSERT INTO artifact_blobs (sha256, byte_count, first_recorded_at) VALUES ($sha, $bytes, $at)";
                insertBlob.Parameters.AddWithValue("$sha", location.Sha256);
                insertBlob.Parameters.AddWithValue("$bytes", location.ByteCount);
                insertBlob.Parameters.AddWithValue("$at", location.Stamp);
                insertBlob.ExecuteNonQuery();
            }
        }

        string? presentId = null;
        string? presentSha = null;
        using (var present = connection.CreateCommand())
        {
            present.CommandText =
                """
                SELECT location_id, sha256 FROM artifact_blob_locations
                WHERE storage_site_id = $site AND path = $path AND state = 'present'
                """;
            present.Parameters.AddWithValue("$site", location.StorageSiteId);
            present.Parameters.AddWithValue("$path", location.Path);
            using var row = present.ExecuteReader();
            if (row.Read())
            {
                presentId = row.GetString(0);
                presentSha = row.GetString(1);
            }
        }

        if (presentId is not null && presentSha == location.Sha256)
        {
            using var touch = connection.CreateCommand();
            touch.CommandText =
                "UPDATE artifact_blob_locations SET last_verified_at = $at, removed_at = NULL WHERE location_id = $id";
            touch.Parameters.AddWithValue("$at", location.Stamp);
            touch.Parameters.AddWithValue("$id", presentId);
            touch.ExecuteNonQuery();
            return RegistryResult<string>.Ok(presentId);
        }

        if (presentId is not null)
        {
            using var replace = connection.CreateCommand();
            replace.CommandText =
                "UPDATE artifact_blob_locations SET state = 'replaced', removed_at = $at WHERE location_id = $id";
            replace.Parameters.AddWithValue("$at", location.Stamp);
            replace.Parameters.AddWithValue("$id", presentId);
            replace.ExecuteNonQuery();
        }

        var locationId = Sha256Hex(Encoding.UTF8.GetBytes(
            $"artifact-location\u001f{location.Sha256}\u001f{location.StorageSiteId}\u001f{location.Path}"));
        using (var existing = connection.CreateCommand())
        {
            existing.CommandText = "SELECT location_id FROM artifact_blob_locations WHERE location_id = $id";
            existing.Parameters.AddWithValue("$id", locationId);
            if (existing.ExecuteScalar() is not null)
            {
                using var restore = connection.CreateCommand();
                restore.CommandText =
                    "UPDATE artifact_blob_locations SET state = 'present', last_verified_at = $at, removed_at = NULL WHERE location_id = $id";
                restore.Parameters.AddWithValue("$at", location.Stamp);
                restore.Parameters.AddWithValue("$id", locationId);
                restore.ExecuteNonQuery();
                return RegistryResult<string>.Ok(locationId);
            }
        }

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT INTO artifact_blob_locations (
                    location_id, sha256, storage_site_id, path, state, first_recorded_at, last_verified_at, removed_at
                ) VALUES (
                    $id, $sha, $site, $path, 'present', $at, $at, NULL
                )
                """;
            insert.Parameters.AddWithValue("$id", locationId);
            insert.Parameters.AddWithValue("$sha", location.Sha256);
            insert.Parameters.AddWithValue("$site", location.StorageSiteId);
            insert.Parameters.AddWithValue("$path", location.Path);
            insert.Parameters.AddWithValue("$at", location.Stamp);
            insert.ExecuteNonQuery();
        }

        return RegistryResult<string>.Ok(locationId);
    }

    static void AddClass(
        SortedDictionary<(string Role, string Filename, string Site, string Materialization, string Gc), (long Count, long Bytes)> totals,
        string role,
        string filename,
        string site,
        string materialization,
        string gc,
        long byteCount)
    {
        var key = (role, filename, site, materialization, gc);
        totals.TryGetValue(key, out var current);
        totals[key] = (current.Count + 1, current.Bytes + byteCount);
    }

    static bool BlobExists(SqliteConnection connection, string sha256)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM artifact_blobs WHERE sha256 = $sha";
        command.Parameters.AddWithValue("$sha", sha256);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    static long DeletingLocationCount(SqliteConnection connection, string sha256)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM artifact_blob_locations WHERE sha256 = $sha AND state = 'deleting'";
        command.Parameters.AddWithValue("$sha", sha256);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static bool SnapshotRoleIsOther(SqliteConnection connection, string sha256)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT role FROM artifact_snapshot_files WHERE sha256 = $sha";
        command.Parameters.AddWithValue("$sha", sha256);
        using var row = command.ExecuteReader();
        while (row.Read())
        {
            var role = row.GetString(0);
            if (!KnownFileRoles.Contains(role) || role == "other")
            {
                return true;
            }
        }

        return false;
    }

    static bool HasExplicitRetentionDeclaration(SqliteConnection connection, string sha256)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM artifact_references
            WHERE sha256 = $sha AND owner_kind IN ('stage_consumer', 'stage_plan_export')
            """;
        command.Parameters.AddWithValue("$sha", sha256);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    static string LiveDeclaredMaterialization(SqliteConnection connection, string sha256)
    {
        var requirements = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT materialization_requirement FROM artifact_references
            WHERE sha256 = $sha AND released_at IS NULL AND owner_kind != 'artifact_snapshot'
            """;
        command.Parameters.AddWithValue("$sha", sha256);
        using var row = command.ExecuteReader();
        while (row.Read())
        {
            requirements.Add(row.GetString(0));
        }

        if (requirements.Contains("bytes_required"))
        {
            return "bytes_required";
        }

        if (requirements.Contains("identity_only"))
        {
            return "identity_only";
        }

        return "undeclared";
    }

    static long ActiveBytesRequiredCount(SqliteConnection connection, string sha256)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM artifact_references
            WHERE sha256 = $sha AND materialization_requirement = 'bytes_required' AND released_at IS NULL
            """;
        command.Parameters.AddWithValue("$sha", sha256);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static long ActiveHoldCount(SqliteConnection connection, string sha256)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM artifact_holds WHERE sha256 = $sha AND released_at IS NULL";
        command.Parameters.AddWithValue("$sha", sha256);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static string? PreparedGcClaim(SqliteConnection connection, string locationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT gc_claim_id FROM artifact_gc_claims
            WHERE location_id = $id AND state = 'prepared'
            """;
        command.Parameters.AddWithValue("$id", locationId);
        return command.ExecuteScalar() as string;
    }

    static bool TryReplay<T>(
        SqliteConnection connection,
        string scope,
        string actorId,
        string idempotencyKey,
        string requestText,
        out T? replayed,
        out RegistryRefusal? refusal)
        where T : class
    {
        replayed = null;
        refusal = null;
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT request_text, result_json FROM operation_outcomes
            WHERE operation_scope = $scope AND actor_id = $actor AND idempotency_key = $key
            """;
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$actor", actorId);
        command.Parameters.AddWithValue("$key", idempotencyKey);
        using var row = command.ExecuteReader();
        if (!row.Read())
        {
            return false;
        }

        // CHG 2026-09-26 (schema 19): the stored request is text and is
        // compared byte for byte. Every row has it: step 18 does not carry the
        // outcomes written before schema 19 (owner: "like useless").
        var storedText = row.GetString(0);
        var json = row.GetString(1);
        if (!string.Equals(storedText, requestText, StringComparison.Ordinal))
        {
            refusal = new RegistryRefusal(
                "refused",
                "invalid_request",
                $"idempotency key \"{idempotencyKey}\" was already used for a different artifact request");
            return true;
        }

        replayed = JsonSerializer.Deserialize<T>(json, RegistryJson.Options)
            ?? throw new RegistryFault("stored artifact outcome is unreadable");
        return true;
    }

    static void RecordOutcome(
        SqliteConnection connection,
        string scope,
        string actorId,
        string idempotencyKey,
        string operationName,
        string requestText,
        string resultJson,
        string? receiptId,
        long nowMs)
    {
        var written = Journals.AppendOperationOutcomeInOpenTransaction(
            connection,
            new OperationOutcomeWrite(
                OperationScope: scope,
                ActorId: actorId,
                IdempotencyKey: idempotencyKey,
                OperationName: operationName,
                RequestText: requestText,
                OutcomeKind: "completed",
                ResultJson: resultJson,
                CreatedAtMs: nowMs,
                TransitionReceiptId: receiptId));
        if (!written.IsOk && written.TryGetRefusal(out var refusal))
        {
            throw new RegistryFault(refusal.Detail);
        }
    }

    static string AppendReceipt(
        SqliteConnection connection,
        string operationName,
        string objectKind,
        string objectId,
        string? priorState,
        string nextState,
        string actorKind,
        string actorId,
        string inputJson,
        string outputJson,
        string correlationId,
        long nowMs)
    {
        var written = Journals.AppendTransitionReceiptInOpenTransaction(
            connection,
            new TransitionReceiptWrite(
                OperationName: operationName,
                ObjectKind: objectKind,
                ObjectId: objectId,
                PriorState: priorState,
                NextState: nextState,
                ActorKind: actorKind,
                ActorId: actorId,
                InputIdentityJson: inputJson,
                OutputIdentityJson: outputJson,
                PolicyRevision: PolicyRevision,
                CorrelationId: correlationId,
                OccurredAtMs: nowMs));
        if (!written.TryGetValue(out var receiptId))
        {
            if (!written.TryGetRefusal(out var refusal))
            {
                throw new RegistryFault("artifact transition append returned neither an id nor a refusal");
            }

            throw new RegistryFault(refusal.Detail);
        }

        return receiptId;
    }

    /// <summary>
    /// The request an idempotency key names, as the text it is stored and
    /// compared by: a JSON array of its [key, value] pairs, in order.
    /// CHG 2026-09-26 (schema 19): a SHA-256 was stored before, over each key
    /// and value followed by a NUL. The pairs are written as JSON now, because
    /// a NUL inside SQLite text is not read back reliably, and JSON keeps any
    /// value, a reason with a newline in it included, from running into the next.
    /// </summary>
    static string RequestText(params (string Key, string Value)[] parts) =>
        JsonSerializer.Serialize(parts.Select(part => new[] { part.Key, part.Value }));

    static long DatabaseNowMs(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(strftime('%s', 'now') AS INTEGER) * 1000";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static string Stamp(SqliteConnection connection, long nowMs)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT strftime('%Y-%m-%dT%H:%M:%fZ', $ms / 1000.0, 'unixepoch')";
        command.Parameters.AddWithValue("$ms", nowMs);
        return command.ExecuteScalar() as string
            ?? throw new RegistryFault("database clock did not return a timestamp");
    }

    static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    static RegistryResult<T> Refuse<T>(string detail) =>
        RegistryResult<T>.Rejected(new RegistryRefusal("refused", "invalid_request", detail));
}
