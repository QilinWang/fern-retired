using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Fern.Registry;

public sealed record ArtifactSnapshotFileWrite(
    string RelativePath,
    string Role,
    long ByteCount,
    string Sha256);

/// <param name="AttemptId">The one-store attempt (attempts.id) whose artifact directory is sealed.</param>
public sealed record ArtifactSnapshotWrite(
    long AttemptId,
    string RunHash,
    string ArtifactDir,
    IReadOnlyList<ArtifactSnapshotFileWrite> Files,
    long TotalBytes,
    string ReceiptDigest);

public sealed record ArtifactSnapshotReceipt(
    string Schema,
    string SnapshotId,
    bool Replayed);

/// <summary>
/// Atomically admits one completed Attempt's complete file manifest. This
/// records bytes and their locations; it does not select the Attempt as the
/// accepted scientific result.
/// </summary>
public static class ArtifactSnapshotAuthority
{
    public const string Schema = "fern.artifact_snapshot_admission.v1";
    const string StorageSite = "legacy-local";

    static readonly HashSet<string> Roles = new(StringComparer.Ordinal)
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

    static readonly HashSet<string> RequiredRoles = new(StringComparer.Ordinal)
    {
        "metrics_receipt",
        "invocation",
        "execution_environment",
    };

    public static RegistryResult<ArtifactSnapshotReceipt> Record(
        RegistryWriter writer,
        ArtifactSnapshotWrite request)
    {
        var prepared = Prepare(request);
        if (!prepared.TryGetValue(out var files))
        {
            prepared.TryGetRefusal(out var refusal);
            return RegistryResult<ArtifactSnapshotReceipt>.Rejected(refusal);
        }

        return writer.WriteImmediate(connection => RecordInOpenTransaction(connection, request, files));
    }

    internal static RegistryResult<ArtifactSnapshotReceipt> RecordInOpenTransaction(
        SqliteConnection connection,
        ArtifactSnapshotWrite request,
        IReadOnlyList<ArtifactSnapshotFileWrite> files)
    {
        using (var existing = connection.CreateCommand())
        {
            existing.CommandText =
                "SELECT snapshot_id, run_hash, artifact_dir, file_count, total_bytes, receipt_digest "
                + "FROM artifact_snapshots WHERE attempt_id = $attempt";
            existing.Parameters.AddWithValue("$attempt", request.AttemptId);
            using var row = existing.ExecuteReader();
            if (row.Read())
            {
                var snapshotId = row.GetString(0);
                var storedRun = row.IsDBNull(1) ? null : row.GetString(1);
                var storedDirectory = row.GetString(2);
                var storedCount = row.GetInt64(3);
                var storedBytes = row.GetInt64(4);
                var storedDigest = row.GetString(5);
                row.Close();
                var storedFiles = ReadFiles(connection, snapshotId);
                if (storedRun == request.RunHash
                    && storedDirectory == request.ArtifactDir
                    && storedCount == files.Count
                    && storedBytes == request.TotalBytes
                    && storedDigest == request.ReceiptDigest
                    && storedFiles.SequenceEqual(files))
                {
                    var replayStamp = DatabaseStamp(connection);
                    foreach (var file in files)
                    {
                        RegisterCatalog(connection, snapshotId, request.ArtifactDir, file, replayStamp);
                    }
                    return RegistryResult<ArtifactSnapshotReceipt>.Ok(new(Schema, snapshotId, true));
                }

                return Reject(
                    $"Attempt {request.AttemptId} already sealed a different artifact manifest; create a new Attempt instead of replacing its snapshot");
            }
        }

        // CHG 2026-09-11 night (slice 11): the attempt is a row of attempts and
        // the cell it belongs to is on that row; the Matrix coordinate is gone.
        long cellId;
        using (var cell = connection.CreateCommand())
        {
            cell.CommandText = "SELECT cell_id FROM attempts WHERE id = $attempt";
            cell.Parameters.AddWithValue("$attempt", request.AttemptId);
            cellId = cell.ExecuteScalar() is { } found and not DBNull
                ? Convert.ToInt64(found, CultureInfo.InvariantCulture)
                : throw new RegistryFault($"cannot seal artifacts for unknown Attempt {request.AttemptId}");
        }

        var stamp = DatabaseStamp(connection);
        var snapshot = LifecycleStringIdentity.ArtifactSnapshotId(
            request.AttemptId.ToString(CultureInfo.InvariantCulture),
            request.RunHash,
            request.ArtifactDir,
            stamp);
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO artifact_snapshots "
                + "(snapshot_id, cell_id, attempt_id, run_hash, artifact_dir, file_count, total_bytes, receipt_digest, recorded_at) "
                + "VALUES ($snapshot, $cell, $attempt, $run, $directory, $count, $bytes, $digest, $stamp)";
            insert.Parameters.AddWithValue("$snapshot", snapshot);
            insert.Parameters.AddWithValue("$cell", cellId);
            insert.Parameters.AddWithValue("$attempt", request.AttemptId);
            insert.Parameters.AddWithValue("$run", request.RunHash);
            insert.Parameters.AddWithValue("$directory", request.ArtifactDir);
            insert.Parameters.AddWithValue("$count", files.Count);
            insert.Parameters.AddWithValue("$bytes", request.TotalBytes);
            insert.Parameters.AddWithValue("$digest", request.ReceiptDigest);
            insert.Parameters.AddWithValue("$stamp", stamp);
            insert.ExecuteNonQuery();
        }

        foreach (var file in files)
        {
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText =
                    "INSERT INTO artifact_snapshot_files "
                    + "(snapshot_id, relative_path, role, byte_count, sha256, required_for_acceptance) "
                    + "VALUES ($snapshot, $path, $role, $bytes, $sha, $required)";
                insert.Parameters.AddWithValue("$snapshot", snapshot);
                insert.Parameters.AddWithValue("$path", file.RelativePath);
                insert.Parameters.AddWithValue("$role", file.Role);
                insert.Parameters.AddWithValue("$bytes", file.ByteCount);
                insert.Parameters.AddWithValue("$sha", file.Sha256);
                insert.Parameters.AddWithValue("$required", RequiredRoles.Contains(file.Role));
                insert.ExecuteNonQuery();
            }
            RegisterCatalog(connection, snapshot, request.ArtifactDir, file, stamp);
        }

        return RegistryResult<ArtifactSnapshotReceipt>.Ok(new(Schema, snapshot, false));
    }

    static RegistryResult<IReadOnlyList<ArtifactSnapshotFileWrite>> Prepare(ArtifactSnapshotWrite request)
    {
        if (request.AttemptId <= 0
            || string.IsNullOrWhiteSpace(request.RunHash)
            || string.IsNullOrWhiteSpace(request.ArtifactDir))
        {
            return RejectFiles("artifact snapshot requires an Attempt, run hash, and artifact directory");
        }
        if (request.Files is null)
        {
            return RejectFiles("artifact snapshot files must be supplied explicitly");
        }
        if (!LowerSha256(request.ReceiptDigest))
        {
            return RejectFiles("artifact receipt digest must be exactly 64 lowercase hexadecimal characters");
        }

        var files = request.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var file in files)
        {
            if (!SafeRelativePath(file.RelativePath))
            {
                return RejectFiles($"artifact snapshot contains unsafe relative path {file.RelativePath}");
            }
            if (!paths.Add(file.RelativePath))
            {
                return RejectFiles($"artifact snapshot contains duplicate relative path {file.RelativePath}");
            }
            if (!Roles.Contains(file.Role))
            {
                return RejectFiles($"artifact snapshot contains unknown file role {file.Role}");
            }
            if (file.ByteCount < 0)
            {
                return RejectFiles($"artifact snapshot file {file.RelativePath} has a negative byte count");
            }
            if (!LowerSha256(file.Sha256))
            {
                return RejectFiles($"artifact snapshot file {file.RelativePath} has an invalid SHA-256");
            }
            try
            {
                totalBytes = checked(totalBytes + file.ByteCount);
            }
            catch (OverflowException)
            {
                return RejectFiles("artifact snapshot total byte count exceeds Int64");
            }
        }
        if (totalBytes != request.TotalBytes)
        {
            return RejectFiles(
                $"artifact snapshot claims {request.TotalBytes} total bytes but its files contain {totalBytes}");
        }
        foreach (var role in RequiredRoles)
        {
            if (!files.Any(file => file.Role == role && file.ByteCount > 0))
            {
                return RejectFiles($"artifact snapshot lacks a non-empty {role} receipt");
            }
        }
        var digest = ReceiptDigest(files);
        if (digest != request.ReceiptDigest)
        {
            return RejectFiles(
                $"artifact snapshot receipt digest {request.ReceiptDigest} does not match its complete file manifest {digest}");
        }
        return RegistryResult<IReadOnlyList<ArtifactSnapshotFileWrite>>.Ok(files);
    }

    static void RegisterCatalog(
        SqliteConnection connection,
        string snapshotId,
        string directory,
        ArtifactSnapshotFileWrite file,
        string stamp)
    {
        using (var blob = connection.CreateCommand())
        {
            blob.CommandText =
                "INSERT INTO artifact_blobs (sha256, byte_count, first_recorded_at) "
                + "VALUES ($sha, $bytes, $stamp) ON CONFLICT(sha256) DO NOTHING";
            blob.Parameters.AddWithValue("$sha", file.Sha256);
            blob.Parameters.AddWithValue("$bytes", file.ByteCount);
            blob.Parameters.AddWithValue("$stamp", stamp);
            blob.ExecuteNonQuery();
        }
        using (var stored = connection.CreateCommand())
        {
            stored.CommandText = "SELECT byte_count FROM artifact_blobs WHERE sha256 = $sha";
            stored.Parameters.AddWithValue("$sha", file.Sha256);
            if (Convert.ToInt64(stored.ExecuteScalar(), CultureInfo.InvariantCulture) != file.ByteCount)
            {
                throw new RegistryFault(
                    $"artifact digest {file.Sha256} was already registered with a different byte count");
            }
        }

        var path = Path.Combine(directory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        using (var deleting = connection.CreateCommand())
        {
            deleting.CommandText =
                "SELECT COUNT(*) FROM artifact_blob_locations "
                + "WHERE storage_site_id = $site AND path = $path AND state = 'deleting'";
            deleting.Parameters.AddWithValue("$site", StorageSite);
            deleting.Parameters.AddWithValue("$path", path);
            if (Convert.ToInt64(deleting.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            {
                throw new RegistryFault(
                    $"location {StorageSite}:{path} is deleting; a new present copy cannot race GC");
            }
        }

        string? presentId = null;
        string? presentSha = null;
        using (var present = connection.CreateCommand())
        {
            present.CommandText =
                "SELECT location_id, sha256 FROM artifact_blob_locations "
                + "WHERE storage_site_id = $site AND path = $path AND state = 'present' LIMIT 1";
            present.Parameters.AddWithValue("$site", StorageSite);
            present.Parameters.AddWithValue("$path", path);
            using var row = present.ExecuteReader();
            if (row.Read())
            {
                presentId = row.GetString(0);
                presentSha = row.GetString(1);
            }
        }
        if (presentId is not null && presentSha == file.Sha256)
        {
            using var refresh = connection.CreateCommand();
            refresh.CommandText =
                "UPDATE artifact_blob_locations SET last_verified_at = $stamp, removed_at = NULL WHERE location_id = $id";
            refresh.Parameters.AddWithValue("$stamp", stamp);
            refresh.Parameters.AddWithValue("$id", presentId);
            refresh.ExecuteNonQuery();
        }
        else
        {
            if (presentId is not null)
            {
                using var replace = connection.CreateCommand();
                replace.CommandText =
                    "UPDATE artifact_blob_locations SET state = 'replaced', removed_at = $stamp WHERE location_id = $id";
                replace.Parameters.AddWithValue("$stamp", stamp);
                replace.Parameters.AddWithValue("$id", presentId);
                replace.ExecuteNonQuery();
            }
            var locationId = LifecycleStringIdentity.ArtifactLocationId(file.Sha256, StorageSite, path);
            using var location = connection.CreateCommand();
            location.CommandText =
                "INSERT INTO artifact_blob_locations "
                + "(location_id, sha256, storage_site_id, path, state, first_recorded_at, last_verified_at, removed_at) "
                + "VALUES ($id, $sha, $site, $path, 'present', $stamp, $stamp, NULL) "
                + "ON CONFLICT(location_id) DO UPDATE SET state = 'present', last_verified_at = excluded.last_verified_at, removed_at = NULL";
            location.Parameters.AddWithValue("$id", locationId);
            location.Parameters.AddWithValue("$sha", file.Sha256);
            location.Parameters.AddWithValue("$site", StorageSite);
            location.Parameters.AddWithValue("$path", path);
            location.Parameters.AddWithValue("$stamp", stamp);
            location.ExecuteNonQuery();
        }

        if (RequiredRoles.Contains(file.Role))
        {
            var referenceId = LifecycleStringIdentity.SnapshotFileReferenceId(
                snapshotId,
                file.RelativePath,
                file.Sha256);
            using var reference = connection.CreateCommand();
            reference.CommandText =
                "INSERT INTO artifact_references "
                + "(reference_id, sha256, owner_kind, owner_id, purpose, materialization_requirement, recorded_at, released_at) "
                + "VALUES ($id, $sha, 'artifact_snapshot', $snapshot, 'sealed_file_identity', 'bytes_required', $stamp, NULL) "
                + "ON CONFLICT DO NOTHING";
            reference.Parameters.AddWithValue("$id", referenceId);
            reference.Parameters.AddWithValue("$sha", file.Sha256);
            reference.Parameters.AddWithValue("$snapshot", snapshotId);
            reference.Parameters.AddWithValue("$stamp", stamp);
            reference.ExecuteNonQuery();
        }
    }

    static IReadOnlyList<ArtifactSnapshotFileWrite> ReadFiles(SqliteConnection connection, string snapshotId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT relative_path, role, byte_count, sha256 FROM artifact_snapshot_files "
            + "WHERE snapshot_id = $snapshot ORDER BY relative_path";
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        using var rows = command.ExecuteReader();
        var files = new List<ArtifactSnapshotFileWrite>();
        while (rows.Read())
        {
            files.Add(new(rows.GetString(0), rows.GetString(1), rows.GetInt64(2), rows.GetString(3)));
        }
        return files;
    }

    static bool SafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !Path.IsPathRooted(path)
        && !path.Contains('\\')
        && path.Split('/').All(segment => segment is not "" and not "." and not "..");

    static bool LowerSha256(string value) =>
        value is not null
        && value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    static string ReceiptDigest(IReadOnlyList<ArtifactSnapshotFileWrite> files)
    {
        var material = new StringBuilder();
        foreach (var file in files)
        {
            material.Append(file.RelativePath).Append('\u001f');
            material.Append(file.Role).Append('\u001f');
            material.Append(file.ByteCount.ToString(CultureInfo.InvariantCulture)).Append('\u001f');
            material.Append(file.Sha256).Append('\u001f');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()))).ToLowerInvariant();
    }

    static string DatabaseStamp(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT strftime('%Y-%m-%dT%H:%M:%fZ', 'now')";
        return command.ExecuteScalar() as string
            ?? throw new RegistryFault("database clock did not return an artifact snapshot timestamp");
    }

    static RegistryResult<ArtifactSnapshotReceipt> Reject(string detail) =>
        RegistryResult<ArtifactSnapshotReceipt>.Rejected(new("refused", "invalid_request", detail));

    static RegistryResult<IReadOnlyList<ArtifactSnapshotFileWrite>> RejectFiles(string detail) =>
        RegistryResult<IReadOnlyList<ArtifactSnapshotFileWrite>>.Rejected(new("refused", "invalid_request", detail));
}
