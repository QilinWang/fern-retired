using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fern.Registry;

/// <summary>
/// Records a verified artifact snapshot move. The caller moves and verifies the
/// bytes first; this authority changes only Registry facts in one transaction.
/// </summary>
public sealed record ArtifactSnapshotRelocationWrite(
    string SnapshotId,
    string AttemptId,
    string ActorKind,
    string ActorId,
    string OldDir,
    string NewDir);

public sealed record ArtifactSnapshotRelocationReceipt(
    string Schema,
    string SnapshotId,
    string ArtifactDir,
    string TransitionReceiptId);

public sealed record ScratchEvidenceRelocationRequest(
    string ScratchRoot,
    string ArtifactRoot,
    string ActorKind,
    string ActorId,
    bool Apply,
    int? Limit);

public sealed record ScratchEvidenceRelocationReport(
    string Schema,
    bool Applied,
    long Moved,
    long Replays,
    long Missing,
    long Conflicts,
    long FailedVerification,
    long LegacyWithoutRunHash,
    long PlannedMoves,
    long PlannedDestinationExists,
    long MovedBytes);

public static class ArtifactSnapshotRelocationAuthority
{
    public const string Schema = "fern.artifact_snapshot_relocation.v1";
    public const string ScratchEvidenceSchema = "fern.scratch_evidence_relocation.v1";
    const string ObjectKind = "csharp:artifact-snapshot";
    const string PolicyRevision = "fern.evidence-relocation.v1";

    public static RegistryResult<ArtifactSnapshotRelocationReceipt> Relocate(
        RegistryWriter writer,
        ArtifactSnapshotRelocationWrite request)
    {
        if (string.IsNullOrWhiteSpace(request.SnapshotId) || string.IsNullOrWhiteSpace(request.AttemptId)
            || string.IsNullOrWhiteSpace(request.ActorKind) || string.IsNullOrWhiteSpace(request.ActorId)
            || string.IsNullOrWhiteSpace(request.OldDir) || string.IsNullOrWhiteSpace(request.NewDir))
        {
            return Refuse("snapshot relocation requires snapshot, attempt, actor, old path, and new path");
        }

        try
        {
            return writer.WriteImmediate(connection => RegistryResult<ArtifactSnapshotRelocationReceipt>.Ok(
                RelocateInOpenTransaction(connection, request)));
        }
        catch (RelocationRefusal refusal)
        {
            return Refuse(refusal.Detail);
        }
    }

    /// <summary>
    /// Move every scratch-homed sealed snapshot to its deterministic durable
    /// home. Filesystem rename and exact-set digest verification happen before
    /// the existing fenced Registry transaction. A crash after rename is
    /// replayed from the already-present durable directory on the next call.
    /// </summary>
    public static RegistryResult<ScratchEvidenceRelocationReport> RelocateScratchEvidence(
        string registry,
        ScratchEvidenceRelocationRequest request)
    {
        try
        {
            RequireScratchRequest(request);
            var scratchRoot = Path.GetFullPath(request.ScratchRoot);
            var artifactRoot = Path.GetFullPath(request.ArtifactRoot);
            var candidates = ReadScratchCandidates(registry, scratchRoot);
            long moved = 0, replays = 0, missing = 0, conflicts = 0, failed = 0, legacy = 0;
            long plannedMoves = 0, plannedDestinationExists = 0, movedBytes = 0;

            foreach (var candidate in candidates)
            {
                var completed = request.Apply ? moved + replays : plannedMoves + plannedDestinationExists;
                if (request.Limit is int limit && completed >= limit) break;
                if (candidate.RunHash is null)
                {
                    legacy++;
                    continue;
                }

                RequireSafeSegment(candidate.RunHash, "run_hash");
                RequireSafeSegment(candidate.AttemptId, "attempt_id");
                var durable = Path.Combine(artifactRoot, "committed-attempts", candidate.RunHash, candidate.AttemptId);
                var sourceExists = Directory.Exists(candidate.ArtifactDir);
                var destinationExists = Directory.Exists(durable);
                if (!request.Apply)
                {
                    if (destinationExists) plannedDestinationExists++;
                    else if (!sourceExists) missing++;
                    else
                    {
                        plannedMoves++;
                        movedBytes += candidate.Files.Sum(file => file.ByteCount);
                    }
                    continue;
                }

                if (destinationExists)
                {
                    if (!SealedFilesMatch(durable, candidate.Files))
                    {
                        conflicts++;
                        continue;
                    }
                    using var replayWriter = RegistryHome.OpenWriter(registry);
                    var replay = Relocate(replayWriter, Write(candidate, request, durable));
                    if (!replay.TryGetValue(out _))
                    {
                        conflicts++;
                        continue;
                    }
                    replays++;
                    continue;
                }
                if (!sourceExists)
                {
                    missing++;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(durable)
                    ?? throw new RelocationRefusal("durable evidence home has no parent"));
                Directory.Move(candidate.ArtifactDir, durable);
                if (!SealedFilesMatch(durable, candidate.Files))
                {
                    Directory.Move(durable, candidate.ArtifactDir);
                    failed++;
                    continue;
                }
                using var writer = RegistryHome.OpenWriter(registry);
                var result = Relocate(writer, Write(candidate, request, durable));
                if (!result.TryGetValue(out _))
                {
                    // Leave the verified durable directory in place. The next
                    // invocation recognizes it as a crash replay and retries
                    // only the fenced Registry admission.
                    conflicts++;
                    continue;
                }
                moved++;
                movedBytes += candidate.Files.Sum(file => file.ByteCount);
            }

            return RegistryResult<ScratchEvidenceRelocationReport>.Ok(new(
                ScratchEvidenceSchema, request.Apply, moved, replays, missing, conflicts, failed, legacy,
                plannedMoves, plannedDestinationExists, movedBytes));
        }
        catch (RelocationRefusal refusal)
        {
            return RegistryResult<ScratchEvidenceRelocationReport>.Rejected(
                new RegistryRefusal("refused", "invalid_request", refusal.Detail));
        }
        catch (IOException error)
        {
            return RegistryResult<ScratchEvidenceRelocationReport>.Rejected(
                new RegistryRefusal("refused", "filesystem_fault", error.Message));
        }
        catch (UnauthorizedAccessException error)
        {
            return RegistryResult<ScratchEvidenceRelocationReport>.Rejected(
                new RegistryRefusal("refused", "filesystem_fault", error.Message));
        }
    }

    static ArtifactSnapshotRelocationWrite Write(
        ScratchCandidate candidate,
        ScratchEvidenceRelocationRequest request,
        string durable) => new(
            candidate.SnapshotId,
            candidate.AttemptId,
            request.ActorKind,
            request.ActorId,
            candidate.ArtifactDir,
            durable);

    static List<ScratchCandidate> ReadScratchCandidates(string registry, string scratchRoot)
    {
        using var reader = RegistryHome.OpenReader(registry);
        using var command = reader.Connection.CreateCommand();
        command.CommandText =
            "SELECT snapshot_id, attempt_id, run_hash, artifact_dir FROM artifact_snapshots ORDER BY snapshot_id";
        using var rows = command.ExecuteReader();
        var identities = new List<(string SnapshotId, string AttemptId, string? RunHash, string ArtifactDir)>();
        while (rows.Read())
        {
            var artifactDir = Path.GetFullPath(rows.GetString(3));
            if (!Inside(artifactDir, scratchRoot)) continue;
            identities.Add((rows.GetString(0), rows.GetInt64(1).ToString(CultureInfo.InvariantCulture), rows.IsDBNull(2) ? null : rows.GetString(2), artifactDir));
        }
        rows.Close();
        return identities
            .Select(identity => new ScratchCandidate(
                identity.SnapshotId,
                identity.AttemptId,
                identity.RunHash,
                identity.ArtifactDir,
                ReadFiles(reader.Connection, identity.SnapshotId)))
            .ToList();
    }

    static bool Inside(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "."
            && !Path.IsPathRooted(relative)
            && !relative.Split(Path.DirectorySeparatorChar).Any(part => part == "..");
    }

    static void RequireScratchRequest(ScratchEvidenceRelocationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ScratchRoot)
            || string.IsNullOrWhiteSpace(request.ArtifactRoot)
            || string.IsNullOrWhiteSpace(request.ActorKind)
            || string.IsNullOrWhiteSpace(request.ActorId)
            || request.Limit is < 0)
        {
            throw new RelocationRefusal(
                "scratch evidence relocation requires roots, actor, and a non-negative optional limit");
        }
    }

    static void RequireSafeSegment(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".."
            || value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new RelocationRefusal($"{field} is not a safe durable-home path segment");
        }
    }

    static bool SealedFilesMatch(string directory, IReadOnlyList<SnapshotFile> expected)
    {
        if (!Directory.Exists(directory)) return false;
        var actual = new Dictionary<string, (long Bytes, string Sha256)>(StringComparer.Ordinal);
        if (!CaptureFiles(directory, directory, actual)) return false;
        if (actual.Count != expected.Count) return false;
        return expected.All(file => actual.TryGetValue(file.RelativePath, out var observed)
            && observed.Bytes == file.ByteCount
            && observed.Sha256 == file.Sha256);
    }

    static bool CaptureFiles(
        string root,
        string directory,
        Dictionary<string, (long Bytes, string Sha256)> files)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (!CaptureFiles(root, path, files)) return false;
                continue;
            }
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            RequireRelativePath(relative);
            using var stream = File.OpenRead(path);
            files.Add(relative, (stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream))));
        }
        return true;
    }

    static ArtifactSnapshotRelocationReceipt RelocateInOpenTransaction(
        SqliteConnection connection,
        ArtifactSnapshotRelocationWrite request)
    {
        var snapshot = ReadSnapshot(connection, request.SnapshotId);
        if (snapshot is null)
        {
            throw new RelocationRefusal($"snapshot {request.SnapshotId} does not exist; nothing was relocated");
        }
        if (snapshot.Value.AttemptId != request.AttemptId)
        {
            throw new RelocationRefusal(
                $"snapshot {request.SnapshotId} belongs to attempt {snapshot.Value.AttemptId}, not {request.AttemptId}; nothing was relocated");
        }
        if (snapshot.Value.ArtifactDir != request.OldDir)
        {
            throw new RelocationRefusal(
                $"snapshot {request.SnapshotId} is recorded at \"{snapshot.Value.ArtifactDir}\", not the expected \"{request.OldDir}\"; nothing was relocated — re-read the row before retrying");
        }

        var files = ReadFiles(connection, request.SnapshotId);
        foreach (var file in files)
        {
            RequireRelativePath(file.RelativePath);
            var oldPath = Path.Combine(request.OldDir, file.RelativePath);
            var old = ReadPresentLocation(connection, oldPath);
            if (old is not null && old.Value.Sha256 != file.Sha256)
            {
                throw new RelocationRefusal(
                    $"artifact location \"{oldPath}\" records digest {old.Value.Sha256}, not the snapshot digest {file.Sha256}");
            }
        }

        using (var update = connection.CreateCommand())
        {
            update.CommandText =
                "UPDATE artifact_snapshots SET artifact_dir = $new WHERE snapshot_id = $snapshot AND attempt_id = $attempt AND artifact_dir = $old";
            update.Parameters.AddWithValue("$new", request.NewDir);
            update.Parameters.AddWithValue("$snapshot", request.SnapshotId);
            update.Parameters.AddWithValue("$attempt", request.AttemptId);
            update.Parameters.AddWithValue("$old", request.OldDir);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new RelocationRefusal($"snapshot {request.SnapshotId} changed while relocation was being admitted; nothing was relocated");
            }
        }

        var stamp = DateTimeOffset.UtcNow.ToString("O");
        foreach (var file in files)
        {
            var oldPath = Path.Combine(request.OldDir, file.RelativePath);
            var newPath = Path.Combine(request.NewDir, file.RelativePath);
            RetireOldLocation(connection, oldPath, file.Sha256, stamp);
            var stored = ArtifactRetentionAuthority.RecordPresentLocationInOpenTransaction(
                connection,
                new PresentLocationWrite(
                    file.Sha256,
                    file.ByteCount,
                    ArtifactRetentionAuthority.LegacyLocalStorageSite,
                    newPath,
                    stamp));
            if (!stored.TryGetValue(out _))
            {
                if (stored.TryGetRefusal(out var refusal)) throw new RelocationRefusal(refusal.Detail);
                throw new RegistryFault("recording relocated artifact location returned neither a receipt nor a refusal");
            }
            RegisterSnapshotReference(connection, request.SnapshotId, file, stamp);
        }

        var nowMs = DatabaseNowMs(connection);
        var receipt = Journals.AppendTransitionReceiptInOpenTransaction(
            connection,
            new TransitionReceiptWrite(
                OperationName: "relocate-artifact-snapshot",
                ObjectKind: ObjectKind,
                ObjectId: request.SnapshotId,
                PriorState: "scratch-homed",
                NextState: "durable",
                ActorKind: request.ActorKind,
                ActorId: request.ActorId,
                InputIdentityJson: JsonSerializer.Serialize(new { from = request.OldDir }, RegistryJson.Options),
                OutputIdentityJson: JsonSerializer.Serialize(new { to = request.NewDir }, RegistryJson.Options),
                PolicyRevision: PolicyRevision,
                CorrelationId: request.SnapshotId,
                OccurredAtMs: nowMs,
                AttemptId: request.AttemptId));
        if (!receipt.TryGetValue(out var receiptId))
        {
            if (receipt.TryGetRefusal(out var refusal)) throw new RelocationRefusal(refusal.Detail);
            throw new RegistryFault("artifact relocation receipt returned neither an id nor a refusal");
        }
        return new ArtifactSnapshotRelocationReceipt(Schema, request.SnapshotId, request.NewDir, receiptId);
    }

    static (string AttemptId, string ArtifactDir)? ReadSnapshot(SqliteConnection connection, string snapshotId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT attempt_id, artifact_dir FROM artifact_snapshots WHERE snapshot_id = $snapshot";
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        using var row = command.ExecuteReader();
        return row.Read() ? (row.GetString(0), row.GetString(1)) : null;
    }

    static List<SnapshotFile> ReadFiles(SqliteConnection connection, string snapshotId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT relative_path, byte_count, sha256, required_for_acceptance FROM artifact_snapshot_files WHERE snapshot_id = $snapshot ORDER BY relative_path";
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        using var row = command.ExecuteReader();
        var files = new List<SnapshotFile>();
        while (row.Read()) files.Add(new SnapshotFile(row.GetString(0), row.GetInt64(1), row.GetString(2), row.GetInt64(3) != 0));
        return files;
    }

    static (string LocationId, string Sha256)? ReadPresentLocation(SqliteConnection connection, string path)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT location_id, sha256 FROM artifact_blob_locations WHERE storage_site_id = $site AND path = $path AND state = 'present'";
        command.Parameters.AddWithValue("$site", ArtifactRetentionAuthority.LegacyLocalStorageSite);
        command.Parameters.AddWithValue("$path", path);
        using var row = command.ExecuteReader();
        return row.Read() ? (row.GetString(0), row.GetString(1)) : null;
    }

    static void RetireOldLocation(SqliteConnection connection, string oldPath, string sha256, string stamp)
    {
        var old = ReadPresentLocation(connection, oldPath);
        if (old is null) return;
        if (old.Value.Sha256 != sha256) throw new RelocationRefusal($"artifact location \"{oldPath}\" changed while relocation was being admitted");
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE artifact_blob_locations SET state = 'deleted', removed_at = $at WHERE location_id = $id";
        update.Parameters.AddWithValue("$at", stamp);
        update.Parameters.AddWithValue("$id", old.Value.LocationId);
        update.ExecuteNonQuery();
    }

    static void RegisterSnapshotReference(SqliteConnection connection, string snapshotId, SnapshotFile file, string stamp)
    {
        if (!file.RequiredForAcceptance) return;
        var referenceId = Sha256Hex(Encoding.UTF8.GetBytes(
            $"artifact-reference\u001f{snapshotId}\u001f{file.RelativePath}\u001f{file.Sha256}"));
        using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO artifact_references (reference_id, sha256, owner_kind, owner_id, purpose, materialization_requirement, recorded_at, released_at) VALUES ($id, $sha, 'artifact_snapshot', $snapshot, 'sealed_file_identity', 'bytes_required', $at, NULL) ON CONFLICT DO NOTHING";
        insert.Parameters.AddWithValue("$id", referenceId);
        insert.Parameters.AddWithValue("$sha", file.Sha256);
        insert.Parameters.AddWithValue("$snapshot", snapshotId);
        insert.Parameters.AddWithValue("$at", stamp);
        insert.ExecuteNonQuery();
    }

    static long DatabaseNowMs(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(strftime('%s', 'now') AS INTEGER) * 1000";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static void RequireRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
            || path.Split(['/', '\\']).Any(part => part is "" or "." or ".."))
        {
            throw new RelocationRefusal($"snapshot file path \"{path}\" is not a safe relative path");
        }
    }

    static RegistryResult<ArtifactSnapshotRelocationReceipt> Refuse(string detail) =>
        RegistryResult<ArtifactSnapshotRelocationReceipt>.Rejected(new RegistryRefusal("refused", "invalid_request", detail));

    static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    sealed class RelocationRefusal(string detail) : Exception
    {
        public string Detail { get; } = detail;
    }

    sealed record SnapshotFile(string RelativePath, long ByteCount, string Sha256, bool RequiredForAcceptance);
    sealed record ScratchCandidate(
        string SnapshotId,
        string AttemptId,
        string? RunHash,
        string ArtifactDir,
        IReadOnlyList<SnapshotFile> Files);
}
