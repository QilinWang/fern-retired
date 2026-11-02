using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Fern.Registry;

/// <summary>
/// Owns the Registry-fenced cleanup of the run-plans pinned-binary shelf.
/// A pin is removable only when its supervisor has neither a running Plan
/// Attempt on this host nor a live local process. The filesystem unlink stays
/// inside the same immediate transaction that proves no current authority
/// pointer exists for an apply request.
/// </summary>
public sealed record PinnedBinaryReclaimRequest(string WorkRoot, string LocalHost, bool Apply);

public sealed record PinnedBinaryReclaimItem(string Path, long Bytes, string Decision, string Reason);

public sealed record PinnedBinaryReclaimReport(
    string Schema,
    bool Applied,
    long RemovedBytes,
    IReadOnlyList<PinnedBinaryReclaimItem> Items);

public sealed record HandoffAttemptEvidence(
    string AttemptId,
    string RunHash,
    IReadOnlyList<string> OutputByteDigests);

public sealed record HandoffDirectoryEvidence(
    string Path,
    long Bytes,
    IReadOnlyList<HandoffAttemptEvidence> Attempts);

public sealed record HandoffStorageReclaimRequest(
    string WorkRoot,
    bool Apply,
    IReadOnlyList<HandoffDirectoryEvidence> Directories);

public sealed record HandoffStorageReclaimReport(
    string Schema,
    bool Applied,
    long RemovedBytes,
    IReadOnlyList<PinnedBinaryReclaimItem> Items);

/// <summary>
/// Projects the durable Registry and filesystem facts that make a typed
/// handoff directory reclaimable. Rust remains the owner of decoding and
/// validating Fern's typed worker commit documents; Registry owns acceptance,
/// snapshot identity, durable byte verification, and the final guarded unlink.
/// No Registry business row is mutated by this operation.
/// </summary>
public static class HandoffStorageReclaimAuthority
{
    public const string Schema = "fern.handoff_storage_reclaim.v1";

    public static HandoffStorageReclaimReport Preview(
        SqliteConnection connection,
        HandoffStorageReclaimRequest request)
    {
        RequireRequest(request);
        return new HandoffStorageReclaimReport(Schema, false, 0, Inspect(connection, request));
    }

    public static RegistryResult<HandoffStorageReclaimReport> Reclaim(
        RegistryWriter writer,
        HandoffStorageReclaimRequest request)
    {
        try
        {
            RequireRequest(request);
            return writer.WriteImmediate(connection =>
            {
                var current = PinnedBinaryReclaimAuthority.CurrentAuthorityPointers(connection);
                if (current.Count != 0)
                {
                    throw new RegistryFault(
                        "refusing handoff cleanup while Registry Home still has current authority pointers ("
                        + string.Join(", ", current)
                        + "). Finish/cancel/reconcile those objects first; expiry alone is not permission to race a stale writer.");
                }

                var items = Inspect(connection, request);
                long removed = 0;
                foreach (var item in items.Where(item => item.Decision == "removable"))
                {
                    Directory.Delete(item.Path, recursive: true);
                    removed += item.Bytes;
                }
                return RegistryResult<HandoffStorageReclaimReport>.Ok(
                    new HandoffStorageReclaimReport(Schema, true, removed, items));
            });
        }
        catch (Exception error) when (error is RegistryFault or IOException or UnauthorizedAccessException)
        {
            return RegistryResult<HandoffStorageReclaimReport>.Rejected(
                new RegistryRefusal("refused", "invalid_request", error.Message));
        }
    }

    static IReadOnlyList<PinnedBinaryReclaimItem> Inspect(
        SqliteConnection connection,
        HandoffStorageReclaimRequest request) =>
        request.Directories.Select(directory => InspectOne(connection, request.WorkRoot, directory)).ToArray();

    static PinnedBinaryReclaimItem InspectOne(
        SqliteConnection connection,
        string workRoot,
        HandoffDirectoryEvidence directory)
    {
        var scratch = new DirectoryInfo(directory.Path);
        if (!scratch.Exists || scratch.LinkTarget is not null)
        {
            return Keep(directory, "handoff path is missing or is a symbolic link");
        }
        foreach (var attempt in directory.Attempts)
        {
            // CHG 2026-09-11 night (slice 11): "this attempt is the accepted one"
            // is cells.accepted_attempt_id; the run it sealed is on its snapshot,
            // checked below against the run the handoff names.
            if (!long.TryParse(attempt.AttemptId, NumberStyles.None, CultureInfo.InvariantCulture, out var attemptId))
            {
                return Keep(directory, $"handoff names attempt {attempt.AttemptId}, which is not an attempt id of this Home");
            }
            using (var accepted = connection.CreateCommand())
            {
                accepted.CommandText = "SELECT 1 FROM cells WHERE accepted_attempt_id = $attempt";
                accepted.Parameters.AddWithValue("$attempt", attemptId);
                if (accepted.ExecuteScalar() is null)
                {
                    return Keep(directory, "Attempt is not the accepted candidate for this exact run; its commit remains replayable");
                }
            }

            string snapshotId;
            string artifactDirectory;
            using (var snapshot = connection.CreateCommand())
            {
                snapshot.CommandText =
                    "SELECT snapshot_id, artifact_dir FROM artifact_snapshots WHERE attempt_id = $attempt AND run_hash = $run";
                snapshot.Parameters.AddWithValue("$attempt", attemptId);
                snapshot.Parameters.AddWithValue("$run", attempt.RunHash);
                using var row = snapshot.ExecuteReader();
                if (!row.Read())
                {
                    return Keep(directory, "accepted Attempt has no matching durable artifact snapshot");
                }
                snapshotId = row.GetString(0);
                artifactDirectory = row.GetString(1);
            }

            if (IsInside(artifactDirectory, workRoot))
            {
                return Keep(directory, "artifact snapshot points back into scratch instead of durable storage");
            }

            var files = ReadSnapshotFiles(connection, snapshotId);
            if (files.Count == 0)
            {
                return Keep(directory, "artifact snapshot has no file-level receipts");
            }
            var durableDigests = files.Select(file => $"sha256:{file.Sha256}").ToHashSet(StringComparer.Ordinal);
            if (attempt.OutputByteDigests.Any(digest => !durableDigests.Contains(digest)))
            {
                return Keep(directory, "at least one committed worker output has no byte-identical signed copy in durable artifact storage");
            }
            foreach (var file in files)
            {
                if (!SafeRelativePath(file.RelativePath))
                {
                    return Keep(directory, "artifact snapshot contains an unsafe relative path");
                }
                var durable = Path.Combine(artifactDirectory, file.RelativePath);
                var info = new FileInfo(durable);
                if (!info.Exists || info.LinkTarget is not null)
                {
                    return Keep(directory, "durable artifact is missing, linked, or not a regular file");
                }
                if (info.Length != file.ByteCount || Sha256File(durable) != file.Sha256)
                {
                    return Keep(directory, "durable artifact no longer matches its signed byte receipt");
                }
            }
        }
        return new PinnedBinaryReclaimItem(
            directory.Path,
            directory.Bytes,
            "removable",
            "every typed Attempt is accepted and independently byte-verified in durable artifact storage");
    }

    static List<(string RelativePath, long ByteCount, string Sha256)> ReadSnapshotFiles(
        SqliteConnection connection,
        string snapshotId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT relative_path, byte_count, sha256 FROM artifact_snapshot_files WHERE snapshot_id = $snapshot ORDER BY relative_path";
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        using var rows = command.ExecuteReader();
        var files = new List<(string, long, string)>();
        while (rows.Read()) files.Add((rows.GetString(0), rows.GetInt64(1), rows.GetString(2)));
        return files;
    }

    static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    static bool SafeRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !Path.IsPathRooted(path)
        && path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .All(segment => segment is not "" and not "." and not "..");

    static bool IsInside(string candidate, string root)
    {
        var resolvedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(resolvedRoot, StringComparison.Ordinal);
    }

    static PinnedBinaryReclaimItem Keep(HandoffDirectoryEvidence directory, string reason) =>
        new(directory.Path, directory.Bytes, "keep", reason);

    static void RequireRequest(HandoffStorageReclaimRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkRoot)) throw new RegistryFault("handoff reclaim requires a work root");
        foreach (var directory in request.Directories)
        {
            if (!IsInside(directory.Path, request.WorkRoot) || directory.Attempts.Count == 0)
            {
                throw new RegistryFault("handoff reclaim received an unbounded or empty directory candidate");
            }
        }
    }
}

public static class PinnedBinaryReclaimAuthority
{
    public const string Schema = "fern.pinned_binary_reclaim.v1";
    const string PinPrefix = "fern-run.pinned.";

    public static PinnedBinaryReclaimReport Preview(SqliteConnection connection, PinnedBinaryReclaimRequest request)
    {
        RequireRequest(request);
        return new PinnedBinaryReclaimReport(Schema, false, 0, Inspect(connection, request));
    }

    public static RegistryResult<PinnedBinaryReclaimReport> Reclaim(
        RegistryWriter writer,
        PinnedBinaryReclaimRequest request)
    {
        try
        {
            RequireRequest(request);
            return writer.WriteImmediate(connection =>
            {
                var current = CurrentAuthorityPointers(connection);
                if (current.Count != 0)
                {
                    throw new ReclaimRefusal(
                        "refusing pinned-binary cleanup while Registry Home still has current authority pointers ("
                        + string.Join(", ", current)
                        + "). Finish/cancel/reconcile those objects first; expiry alone is not permission to race a stale writer.");
                }

                var items = Inspect(connection, request);
                long removed = 0;
                foreach (var item in items.Where(item => item.Decision == "removable"))
                {
                    var metadata = UnixFileMetadata.Read(item.Path);
                    UnixFileMetadata.Unlink(item.Path);
                    if (metadata.LinkCount == 1) removed += metadata.Length;
                }
                return RegistryResult<PinnedBinaryReclaimReport>.Ok(
                    new PinnedBinaryReclaimReport(Schema, true, removed, items));
            });
        }
        catch (ReclaimRefusal refusal)
        {
            return RegistryResult<PinnedBinaryReclaimReport>.Rejected(
                new RegistryRefusal("refused", "invalid_request", refusal.Detail));
        }
    }

    static IReadOnlyList<PinnedBinaryReclaimItem> Inspect(
        SqliteConnection connection,
        PinnedBinaryReclaimRequest request)
    {
        var shelf = Path.Combine(request.WorkRoot, "run-plans");
        if (!Directory.Exists(shelf)) return [];

        var runningSupervisorPids = RunningSupervisorPids(connection, request.LocalHost);
        return Directory.EnumerateFileSystemEntries(shelf)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Where(path => Path.GetFileName(path).StartsWith(PinPrefix, StringComparison.Ordinal))
            .Select(path => InspectOne(path, runningSupervisorPids))
            .ToArray();
    }

    static PinnedBinaryReclaimItem InspectOne(string path, IReadOnlySet<uint> runningSupervisorPids)
    {
        var name = Path.GetFileName(path);
        var metadata = UnixFileMetadata.Read(path);
        var uniqueBytes = metadata.LinkCount == 1 ? metadata.Length : 0;
        var pidText = name[PinPrefix.Length..];
        if (!uint.TryParse(pidText, out var pid))
        {
            return Keep(path, uniqueBytes, "unrecognized pinned entry name");
        }
        if (runningSupervisorPids.Contains(pid))
        {
            return Keep(
                path,
                uniqueBytes,
                "a registered plan attempt is still running under this supervisor; it execs this pin at its next stage boundary");
        }
        if (Directory.Exists(Path.Combine("/proc", pid.ToString(System.Globalization.CultureInfo.InvariantCulture))))
        {
            return Keep(path, uniqueBytes, "the supervisor process is still alive");
        }
        return new PinnedBinaryReclaimItem(
            path,
            uniqueBytes,
            "removable",
            "no running plan attempt was opened by this pid and the process is gone");
    }

    static PinnedBinaryReclaimItem Keep(string path, long bytes, string reason) =>
        new(path, bytes, "keep", reason);

    static HashSet<uint> RunningSupervisorPids(SqliteConnection connection, string localHost)
    {
        using var command = connection.CreateCommand();
        // CHG 2026-09-11 night (slice 8): the process holding a binary was the
        // Plan attempt's opener (host:pid in run_plan_attempts); it is the Site's
        // supervisor now, whose office row names its host and pid until it ends.
        command.CommandText = "SELECT host, pid FROM supervisors WHERE ended_at IS NULL";
        using var rows = command.ExecuteReader();
        var pids = new HashSet<uint>();
        while (rows.Read())
        {
            if (rows.GetString(0) == localHost && rows.GetInt64(1) is > 0 and <= uint.MaxValue) pids.Add((uint)rows.GetInt64(1));
        }
        return pids;
    }

    // CHG: public so `reclaim-storage --deploy` refuses under the same current-authority rule as pins.
    public static IReadOnlyList<string> CurrentAuthorityPointers(SqliteConnection connection)
    {
        var queries = new (string Kind, string Sql)[]
        {
            // CHG 2026-09-11 night (slice 8): the Plan, Matrix coordinator and
            // Coordinate leases retired with their tables; a current authority
            // is a Site office held or an attempt open. Resource slots stay.
            // A live office: not ended and its written lease not yet run out. A
            // supervisor that stopped renewing still pins the binary it may be
            // running (RunningSupervisorPids), but is no longer the authority.
            ("Site office", "SELECT COUNT(*) FROM supervisors WHERE ended_at IS NULL AND lease_until > '"
                + DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + "'"),
            ("resource slot", "SELECT COUNT(*) FROM resource_slots WHERE current_lease_id IS NOT NULL"),
        };
        var current = new List<string>();
        foreach (var (kind, sql) in queries)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var count = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (count > 0) current.Add($"{count} {kind}");
        }
        return current;
    }

    /// <summary>
    /// The live Site offices, each named by the Site, the host and the process
    /// holding it, so a refusal can say what is in the way rather than how many
    /// things are.
    ///
    /// CHG 2026-09-26: on that night the cleanup refused with "1 Site office"
    /// while the holder had exited thirteen minutes earlier and nothing was
    /// training anywhere; the Site, the host, the process and whether it was
    /// alive all had to be assembled by hand from three other commands. The
    /// standing itself is <see cref="SupervisorLiveness"/>, which needs a look
    /// at the process and so belongs to a caller that can take one.
    /// </summary>
    public static IReadOnlyList<SupervisorLiveness.Office> LiveOffices(SqliteConnection connection)
    {
        var offices = new List<SupervisorLiveness.Office>();
        using var command = connection.CreateCommand();
        // CHG 2026-09-26, after an audit: the Site name was selected and then
        // thrown away, so a refusal could not say which machine was in the way
        // — the whole reason this reader exists. `ended_at` is read rather than
        // filtered on, so a caller sees a closed office as closed instead of
        // not seeing it at all.
        command.CommandText = """
            SELECT t.name, s.host, s.pid, s.started_at, s.lease_until, s.ended_at
              FROM supervisors s JOIN sites t ON t.id = s.site_id
             WHERE s.lease_until > $now
             ORDER BY t.name
            """;
        command.Parameters.AddWithValue("$now",
            DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        using var read = command.ExecuteReader();
        while (read.Read())
        {
            if (DateTimeOffset.TryParse(read.GetString(3), out var started)
                && DateTimeOffset.TryParse(read.GetString(4), out var until))
            {
                offices.Add(new SupervisorLiveness.Office(
                    read.GetString(0), read.GetString(1), read.GetInt64(2), started, until,
                    Ended: !read.IsDBNull(5)));
            }
        }
        return offices;
    }

    static void RequireRequest(PinnedBinaryReclaimRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkRoot) || string.IsNullOrWhiteSpace(request.LocalHost))
        {
            throw new ReclaimRefusal("pinned-binary reclaim requires a work root and local host");
        }
        if (!OperatingSystem.IsLinux())
        {
            throw new ReclaimRefusal("pinned-binary reclaim requires Linux /proc and inode metadata");
        }
    }

    sealed class ReclaimRefusal(string detail) : Exception
    {
        public string Detail { get; } = detail;
    }

    static class UnixFileMetadata
    {
        [StructLayout(LayoutKind.Sequential)]
        struct StatBuffer
        {
            public ulong Device;
            public ulong Inode;
            public ulong LinkCount;
            public uint Mode;
            public uint UserId;
            public uint GroupId;
            public int Pad0;
            public ulong DeviceType;
            public long Length;
            public long BlockSize;
            public long Blocks;
            public long AccessSeconds;
            public ulong AccessNanoseconds;
            public long ModificationSeconds;
            public ulong ModificationNanoseconds;
            public long ChangeSeconds;
            public ulong ChangeNanoseconds;
            public long Reserved0;
            public long Reserved1;
            public long Reserved2;
        }

        [DllImport("libc", SetLastError = true, EntryPoint = "stat")]
        static extern int Stat(string path, out StatBuffer buffer);

        [DllImport("libc", SetLastError = true, EntryPoint = "unlink")]
        static extern int UnlinkNative(string path);

        public static (long Length, ulong LinkCount) Read(string path)
        {
            if (Stat(path, out var buffer) != 0)
            {
                throw new RegistryFault($"cannot inspect pinned binary {path}: errno {Marshal.GetLastWin32Error()}");
            }
            if (buffer.Length < 0) throw new RegistryFault($"pinned binary {path} has a negative length");
            return (buffer.Length, buffer.LinkCount);
        }

        public static void Unlink(string path)
        {
            if (UnlinkNative(path) != 0)
            {
                throw new RegistryFault($"cannot remove pinned binary {path}: errno {Marshal.GetLastWin32Error()}");
            }
        }
    }
}
