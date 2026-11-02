using System.Runtime.InteropServices;

namespace Fern.Registry;

/// <summary>
/// One address-level exclusion for cooperating Registry opens and maintenance.
/// The adjacent .access file is permanent: deleting or replacing it would split
/// the lock identity and erase retired-address refusal. It is not a SQLite lock
/// and cannot protect against an old binary or a direct SQLite client.
/// </summary>
public sealed class RegistryAccess : IDisposable
{
    enum State : byte { Active = (byte)'A', Maintenance = (byte)'M', Retired = (byte)'R' }

    readonly FileStream? handle;
    readonly FileStream? databaseHandle;
    readonly string databasePath;
    readonly bool exclusive;
    bool resumeOnDispose;
    readonly (long Inode, int DeviceMajor, int DeviceMinor)? sourceIdentity;
    bool disposed;
    State? reservedTargetState;

    // Relocation reserves an absent destination without creating a database.
    // A retired destination is admissible only here, for checked reverse moves;
    // ordinary open/create and ResumeAccess still cannot unretire an address.
    internal static RegistryAccess ReserveRelocationTarget(string path)
    {
        var full = Path.GetFullPath(path);
        RequireUnaliased(full);
        RequireUnaliased(full + ".access");
        if (File.Exists(full) || Directory.Exists(full))
            throw new RegistryFault($"Registry relocation destination is occupied: {full}");
        try
        {
            using var initial = new FileStream(full + ".access", FileMode.CreateNew, FileAccess.Write, FileShare.None);
            initial.WriteByte((byte)State.Active);
            initial.Flush(true);
        }
        catch (IOException) when (File.Exists(full + ".access")) { }
        var stream = new FileStream(full + ".access", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var access = new RegistryAccess(stream, null, full, exclusive: false);
        try
        {
            if (File.Exists(full) || Directory.Exists(full))
                throw new RegistryFault($"Registry relocation destination appeared: {full}");
            access.reservedTargetState = access.ReadState();
            access.WriteState(State.Maintenance);
            return access;
        }
        catch { access.Dispose(); throw; }
    }

    internal void ActivateRelocatedTarget()
    {
        RequireRelocationAccess(databasePath);
        if (reservedTargetState is null || !File.Exists(databasePath))
            throw new RegistryFault("Relocation activation requires its reserved, populated destination");
        WriteState(State.Active);
    }

    internal void RetireArchivedTarget()
    {
        RequireRelocationAccess(databasePath);
        if (reservedTargetState is null || !File.Exists(databasePath))
            throw new RegistryFault("Archive retirement requires the reserved, populated destination");
        WriteState(State.Retired);
    }
    internal void RequireRelocationAccess(string path)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Path.GetFullPath(path) != databasePath || ReadState() != State.Maintenance
            || (!exclusive && reservedTargetState is null))
            throw new RegistryFault("Database relocation access requires held maintenance exclusion for that exact address");
    }

    internal void RequireOriginalFileAt(string path)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (sourceIdentity is null || sourceIdentity != ReadLinuxFileIdentity(path))
            throw new RegistryFault($"Registry source identity changed at {path}");
    }

    // CHG 2026-09-26 (owner: "get rid of 256 entirely"): HashClosedDatabase returned a
    // SHA-256 of the held bytes, which the 2026-09-04 relocation compared with the hashes
    // of its copy and archive. This compares the bytes themselves. Two SQLite files with
    // identical rows can still differ in page layout; "the same experiment" compares rows.
    /// <summary>Whether the file at path holds exactly the held database's bytes.</summary>
    internal bool ClosedDatabaseEquals(string path)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!exclusive || databaseHandle is null) throw new RegistryFault("Comparison requires held database exclusion");
        databaseHandle.Position = 0;
        using var other = File.OpenRead(path);
        if (other.Length != databaseHandle.Length) return false;
        var held = new byte[1 << 16];
        var copy = new byte[held.Length];
        while (true)
        {
            var read = databaseHandle.ReadAtLeast(held, held.Length, throwOnEndOfStream: false);
            if (read == 0) return true;
            other.ReadExactly(copy, 0, read);
            if (!held.AsSpan(0, read).SequenceEqual(copy.AsSpan(0, read))) return false;
        }
    }

    internal void CopyClosedDatabase(Stream output)
    {
        RequireRelocationAccess(databasePath);
        databaseHandle!.Position = 0;
        databaseHandle.CopyTo(output);
    }

    RegistryAccess(FileStream? handle, FileStream? databaseHandle, string databasePath, bool exclusive)
    {
        this.handle = handle;
        this.databaseHandle = databaseHandle;
        this.databasePath = databasePath;
        this.exclusive = exclusive;
        if (exclusive) sourceIdentity = ReadLinuxFileIdentity(databasePath);
    }

    internal static RegistryAccess OpenConnection(string path, bool readOnly) =>
        Acquire(path, maintenance: false, readOnly);

    internal static bool IsFrozenCopy(string path)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(path) || File.Exists(path + ".access")) return false;
        const UnixFileMode writes = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        var full = Path.GetFullPath(path);
        return (File.GetUnixFileMode(full) & writes) == 0
            && (File.GetUnixFileMode(Path.GetDirectoryName(full)!) & writes) == 0;
    }

    /// <summary>
    /// Exclude new opens and creation, or refuse immediately while a cooperating
    /// connection is open. Example: using var access = RegistryAccess.BeginMaintenance(path).
    /// Disposal leaves maintenance durable; ResumeAccess is an explicit decision.
    /// CHG: orderly disposal now resumes maintenance begun here from active,
    /// but only while the same source remains and retirement has not begun.
    /// Process death and recovery of an earlier maintenance remain closed.
    /// This is an access prerequisite, not proof of no live Plans or foreign clients.
    /// </summary>
    public static RegistryAccess BeginMaintenance(string path)
    {
        // The maintenance protocol is currently proved on the local Linux Home.
        if (!OperatingSystem.IsLinux())
            throw new RegistryFault("Registry maintenance access is supported only on Linux");
        var access = Acquire(path, maintenance: true, readOnly: false);
        try
        {
            if (!File.Exists(access.databasePath))
                throw new RegistryFault("Registry maintenance requires an existing database");
            access.WriteState(State.Maintenance);
            return access;
        }
        catch { access.Dispose(); throw; }
    }

    /// <summary>Explicitly resume this existing, non-retired address while holding exclusion: access.ResumeAccess().</summary>
    public void ResumeAccess()
    {
        RequireMaintenance();
        if (!SourceUnchanged())
            throw new RegistryFault("Registry access cannot resume an absent or replaced database; resolve its location before resuming");
        WriteState(State.Active);
    }

    /// <summary>Persist refusal of this old address before relocation: access.RetireAddress(). No bytes are moved.</summary>
    // CHG: relocation now retires after rename; maintenance protects the old
    // address across the rename itself, including the interrupted M/M state.
    public void RetireAddress()
    {
        RequireMaintenance();
        WriteState(State.Retired);
    }

    void RequireMaintenance()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!exclusive || ReadState() != State.Maintenance)
            throw new RegistryFault("Registry access operation requires held maintenance exclusion; retired addresses cannot resume");
    }

    static RegistryAccess Acquire(string path, bool maintenance, bool readOnly)
    {
        var full = Path.GetFullPath(path);
        var lockPath = full + ".access";
        RequireUnaliased(full);
        RequireUnaliased(lockPath);
        // Honor the frozen-copy boundary even when the process runs as root.
        // It must not initialize a writable Home beside an immutable reader.
        if (!readOnly && IsFrozenCopy(full))
            throw new RegistryFault("Frozen Registry copy is read-only; select a writable Home instead");
        FileStream? stream = null;
        FileStream? database = null;
        try
        {
            // CHG: a reader needs only read permission. Lock the database first,
            // then consult any address state, so marker creation cannot race an
            // unprotected read. A failed maintenance attempt never publishes M.
            if (readOnly)
                database = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (!readOnly && !File.Exists(lockPath))
            {
                try
                {
                    // Publish a complete initial state under exclusive sharing.
                    // A concurrent opener either sees A or refuses the held lock.
                    using var initial = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    initial.WriteByte((byte)State.Active);
                    initial.Flush(flushToDisk: true);
                }
                catch (IOException) when (File.Exists(lockPath)) { }
            }
            // FileStream uses flock on Unix, including same-process handles.
            // Keep this descriptor until AFTER SQLite closes. Never unlink it.
            if (!readOnly || File.Exists(lockPath))
                stream = new FileStream(lockPath, FileMode.Open,
                    maintenance ? FileAccess.ReadWrite : FileAccess.Read,
                    maintenance ? FileShare.None : FileShare.ReadWrite);
            // CHG: exclusion must also cover readers that found no access file.
            if (maintenance)
                database = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.None);
            var access = new RegistryAccess(stream, database, full, maintenance);
            var state = access.ReadState();
            if (state == State.Retired || (!maintenance && state != State.Active))
                throw new RegistryFault($"Registry access {full} is {state.ToString().ToLowerInvariant()}; do not recreate or bypass this address");
            access.resumeOnDispose = maintenance && state == State.Active;
            return access;
        }
        catch (Exception error)
        {
            stream?.Dispose();
            database?.Dispose();
            if (error is RegistryFault) throw;
            throw new RegistryFault($"Registry access {full} refused: a connection or maintenance may hold the address, or its access file is unavailable: {error.Message}", error);
        }
    }

    State ReadState()
    {
        // Only readers can have no address file. Their database lock still
        // excludes maintenance; writers never infer active from missing state.
        if (handle is null) return State.Active;
        handle.Position = 0;
        var value = handle.ReadByte();
        if (handle.Length != 1 || value is not ((byte)State.Active) and not ((byte)State.Maintenance) and not ((byte)State.Retired))
            throw new RegistryFault($"Registry access {databasePath} has invalid state; explicit recovery is required");
        return (State)value;
    }

    void WriteState(State state)
    {
        if (handle is null) throw new RegistryFault("Registry state writes require maintenance access");
        handle.Position = 0;
        handle.WriteByte((byte)state);
        handle.Flush(flushToDisk: true);
    }

    bool SourceUnchanged()
    {
        if (!File.Exists(databasePath)) return false;
        RequireUnaliased(databasePath);
        return sourceIdentity == ReadLinuxFileIdentity(databasePath);
    }

    internal static void RequireUnaliased(string path)
    {
        for (var part = path; part is not null; part = Path.GetDirectoryName(part))
        {
            if ((File.Exists(part) || Directory.Exists(part) || new FileInfo(part).LinkTarget is not null)
                && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new RegistryFault($"Registry access refuses symbolic path {part}; use the canonical address");
        }
        // Two hard links would have different adjacent access files. Refuse
        // rather than treating one database inode as two Registry addresses.
        if (OperatingSystem.IsLinux() && File.Exists(path))
        {
            var buffer = Marshal.AllocHGlobal(256); // Linux struct statx, stable UAPI layout.
            try
            {
                if (Statx(-100, path, 0x100, 4, buffer) != 0 || (Marshal.ReadInt32(buffer) & 4) == 0)
                    throw new RegistryFault($"Registry access cannot verify link identity for {path}");
                if (Marshal.ReadInt32(buffer, 16) != 1)
                    throw new RegistryFault($"Registry access refuses hard-linked file {path}");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    static extern int Statx(int directory, string path, int flags, uint mask, nint buffer);

    internal static (long Inode, int DeviceMajor, int DeviceMinor) ReadLinuxFileIdentity(string path)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (Statx(-100, path, 0x100, 0x100, buffer) != 0 || (Marshal.ReadInt32(buffer) & 0x100) == 0)
                throw new RegistryFault($"Registry access cannot verify source identity for {path}");
            return (Marshal.ReadInt64(buffer, 32), Marshal.ReadInt32(buffer, 136), Marshal.ReadInt32(buffer, 140));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Release OS locks; safely resume only this holder's unchanged pre-retirement source.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (reservedTargetState is { } previous && !File.Exists(databasePath)
                && !Directory.Exists(databasePath) && ReadState() == State.Maintenance)
                WriteState(previous);
            if (resumeOnDispose && ReadState() == State.Maintenance && SourceUnchanged())
                WriteState(State.Active);
        }
        catch (Exception error) when (error is RegistryFault or IOException or UnauthorizedAccessException)
        {
            // Preserve the original operation's exception. If source identity
            // or state cannot be checked, leave maintenance closed, not active.
        }
        finally
        {
            databaseHandle?.Dispose();
            handle?.Dispose();
        }
    }
}
