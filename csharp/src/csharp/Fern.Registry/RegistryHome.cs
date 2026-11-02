using Dapper;
using Microsoft.Data.Sqlite;
using System.Data;
using System.Globalization;

namespace Fern.Registry;

/// <summary>
/// Opens a current-schema Registry Home. RegistrySchema owns fresh initialization.
/// </summary>
public static class RegistryHome
{
    /// <summary>
    /// Teaches Dapper what a boolean column actually stores, once for the process.
    ///
    /// SQLite has no boolean storage class. A declared type only sets an affinity,
    /// by substring: <c>INTEGER</c> and <c>BIGINT</c> contain "INT" and take
    /// INTEGER affinity, while <c>BOOL</c> and <c>BOOLEAN</c> contain none of the
    /// recognised substrings and take NUMERIC. Six columns in this schema are
    /// spelled that way.
    ///
    /// Without a handler, the type Dapper sees MOVES with the data:
    /// Microsoft.Data.Sqlite reports a field's type from the stored value when the
    /// result has rows, and from the declared type NAME when it is empty — and
    /// "BOOLEAN" maps to nothing, so it falls back to String. A mapping that fits
    /// the populated case therefore cannot fit the empty one. That shipped once, on
    /// 2026-09-10: a conversion of the Site list passed all 642 Registry tests,
    /// because every fixture registers a Site before listing Sites, and crashed
    /// `fern-run site list` on a fresh Registry, which is the first command a new
    /// machine runs. Neither <c>object</c> nor a <c>CAST</c> rescues it; a computed
    /// column has no declared name to fall back to at all.
    ///
    /// Registered here because it is a process-wide setting and this type is the
    /// one seam every Registry open passes through, including
    /// Fern.Scheduler.ProductionRegistry, which delegates to it. Record types may
    /// now declare plain <c>bool</c> for these columns and be honest about it.
    ///
    /// Failure record:
    /// `.ai/failure_patterns/2026-09-10-a-column-type-that-changes-when-the-table-is-empty.md`.
    /// </summary>
    static RegistryHome() => SqlMapper.AddTypeHandler(new BooleanStoredAsInteger());

    sealed class BooleanStoredAsInteger : SqlMapper.TypeHandler<bool>
    {
        public override bool Parse(object value) =>
            Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;

        public override void SetValue(IDbDataParameter parameter, bool value) =>
            parameter.Value = value ? 1L : 0L;
    }

    // The current operational default remains here until the separately checked
    // database relocation. It is a database path, never a campaign derivation.
    // CHG: The previous Home address is retired by its whole-file archive.
    // CHG: all ordinary readers now use the explicitly selected installed context.
    public static string DefaultRegistryPath => RuntimeSelection.Read().Registry;

    public const string DomainPrefix = "csharp:";
    const int RegistryCommandTimeoutSeconds = 5;

    public static RegistryWriter OpenWriter(string path)
    {
        RequireRegularFile(path);
        var access = RegistryAccess.OpenConnection(path, readOnly: false);
        SqliteConnection? connection = null;
        try
        {
            // CHG: access exclusion precedes SQLite and lasts until its disposal.
            RequireRegularFile(path);
            connection = OpenSqlite(path, readOnly: false, immutable: false);
            RegistrySchema.RequireCurrent(connection, path);
            ApplyWriterPragmas(connection);
            return new RegistryWriter(connection, access);
        }
        catch
        {
            connection?.Dispose();
            access.Dispose();
            throw;
        }
    }

    public static RegistryReader OpenReader(string path)
    {
        RequireRegularFile(path);
        var access = RegistryAccess.OpenConnection(path, readOnly: true);
        SqliteConnection? connection = null;
        try
        {
            // CHG: readers also exclude relocation while their handle is live.
            RequireRegularFile(path);
            connection = OpenSqlite(path, readOnly: true, immutable: false);
            ApplyReaderPragmas(connection);
            RegistrySchema.RequireCurrent(connection, path);
            return new RegistryReader(connection, access);
        }
        catch
        {
            connection?.Dispose();
            access.Dispose();
            throw;
        }
    }

    internal static void RequireDomainPrefix(string value, string field)
    {
        if (!value.StartsWith(DomainPrefix, StringComparison.Ordinal))
        {
            throw new RegistryFault($"{field} must start with {DomainPrefix} so C# never shares Go's scope or object id");
        }
    }

    // Only the relocation owner may open SQLite while holding the address
    // exclusion itself. This does not expose a public skip-lock reader/writer.
    internal static SqliteConnection OpenRelocationDatabase(RegistryAccess access, string path)
    {
        access.RequireRelocationAccess(path);
        var connection = OpenSqlite(path, readOnly: false, immutable: false);
        try
        {
            RegistrySchema.RequireCurrent(connection, path);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    // Archive is a closed-file operation. It deliberately does not admit the
    // old schema as a writable current Home or replay any historical SQL.
    internal static SqliteConnection OpenArchiveDatabase(string path, RegistryAccess? maintenance = null)
    {
        if (maintenance is not null) maintenance.RequireRelocationAccess(path);
        var connection = OpenSqlite(path, readOnly: maintenance is null, immutable: false);
        try
        {
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('run_plans','run_plan_attempts','run_plan_leases','transition_receipts')";
            if (Convert.ToInt64(check.ExecuteScalar()) != 4)
                throw new RegistryFault("Archive source lacks Registry plan and receipt tables; no schema conversion is attempted");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    internal static void VerifyArchivedDatabase(string path)
    {
        using var connection = OpenSqlite(path, readOnly: true, immutable: true);
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA quick_check";
        if (check.ExecuteScalar() as string != "ok")
            throw new RegistryFault($"Archived database integrity check failed: {path}");
    }

    internal static void VerifyClosedArchive(string path)
    {
        using var connection = OpenSqlite(path, readOnly: true, immutable: true);
        RegistrySchema.RequireCurrent(connection, path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        if (command.ExecuteScalar() as string != "ok")
            throw new RegistryFault($"Registry archive integrity check failed: {path}");
    }

    private static SqliteConnection OpenSqlite(string path, bool readOnly, bool immutable)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        // CHG: a frozen, marker-free Linux copy is not a writable Registry Home.
        // SQLite's ordinary read-only WAL open may still create -wal/-shm. Use
        // immutable only where both file and directory deny all write modes,
        // and never omit a journal that may contain committed or recovery data.
        // CHG: the relocation owner also verifies its private, closed archive
        // with immutable access before publishing it; this is not a public flag.
        // https://www.sqlite.org/wal.html#read_only_databases
        if (readOnly && (immutable || RegistryAccess.IsFrozenCopy(path)))
        {
            var fullPath = Path.GetFullPath(path);
            foreach (var suffix in new[] { "-wal", "-journal" })
                if (File.Exists(fullPath + suffix) && new FileInfo(fullPath + suffix).Length > 0)
                    throw new RegistryFault("Frozen Registry copy has a nonempty journal; make a checkpointed copy through its owner, never delete the journal to force a read");
            builder.DataSource = new Uri(fullPath).AbsoluteUri + "?immutable=1";
        }
        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch (Exception exception)
        {
            connection.Dispose();
            throw new RegistryFault($"open Registry Home {path}: {exception.Message}", exception);
        }
    }

    private static void ApplyWriterPragmas(SqliteConnection connection)
    {
        ExecutePragma(connection, "PRAGMA journal_mode = WAL");
        ExecutePragma(connection, "PRAGMA foreign_keys = ON");
    }

    private static void ApplyReaderPragmas(SqliteConnection connection)
    {
        ExecutePragma(connection, "PRAGMA query_only = 1");
        ExecutePragma(connection, $"PRAGMA busy_timeout = {BusyTimeouts.ReaderWaitsForAWriterMs}");
    }

    private static void ExecutePragma(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = RegistryCommandTimeoutSeconds;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void RequireRegularFile(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.Directory) != 0)
        {
            throw new RegistryFault($"Registry Home {path} is not a regular file");
        }
    }

}
