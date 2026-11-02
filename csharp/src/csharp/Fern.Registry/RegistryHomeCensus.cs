using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Fern.Registry;

/// <summary>
/// What one Registry Home is, read without writing to it: its schema version
/// stamp, whether this binary considers it current, and every table it holds
/// with how many rows and which writer — if any — can still produce one.
///
/// WHY IT EXISTS. Two questions had no command. The first is the schema version
/// stamp: a Home written before 2026-09-10 carries none, reads 0, and is
/// deliberately NOT walked forward by <see cref="RegistrySchemaTransforms"/> —
/// so the first table that ever leaves <c>Schema.sql</c> turns that skip into a
/// refusal with no path forward, and until now nothing could tell a stamped Home
/// from an unstamped one. The second is what a schema cut would destroy: a table
/// with rows and no writer is unreachable evidence, and dropping it is
/// irreversible, so somebody has to be able to read the list first.
///
/// READ-ONLY BY CONSTRUCTION. The connection is opened <c>Mode=ReadOnly</c> and
/// nothing here writes, not even the version stamp a current Home is missing:
/// this is a check, and a check must not change the thing it is checking.
/// It deliberately does NOT go through <see cref="RegistryHome"/>, whose readers
/// refuse a Home that is not current — that is exactly the Home this command
/// exists to describe.
/// </summary>
public static class RegistryHomeCensus
{
    /// <summary>
    /// The six tables the ten Registry operations in <see cref="RegistryOperations"/>
    /// write. Declared here and checked against the operations' own SQL by
    /// <c>RegistryHomeCensusTests.TheDeclaredSetsAreWhatTheSourceActuallyWrites</c>,
    /// which fails when the two disagree — so this list cannot quietly rot.
    /// </summary>
    // CHG 2026-09-11 night (slice 1 of the cell port): `supervisors` and
    // `operator_requests` join the six, written by the supervisor office and
    // operator-request operations in the same class.
    // CHG 2026-09-25: `dispatch_last_moves`, written by MoveLast; and
    // `cell_config_regions`, written by RegisterStudy since 2026-09-14 and
    // missing from this list since then. And `study_compilers` (slice M2).
    // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): `site_sources` and
    // `site_compilers` left with installed sources and carried compilers.
    // CHG 2026-09-26: `site_compilers`, written by RecordSiteCompiler
    // (install-study-source carries the compiler a study names to the Site).
    // CHG 2026-09-26 (cards-without-hashes): `cell_config_regions` left at schema 18.
    public static readonly IReadOnlyList<string> StudyOperationTables =
        ["attempts", "cells", "dispatch_last_moves", "dispatch_priorities", "events", "operator_requests", "sites", "sources", "studies", "study_compilers", "supervisors"];

    /// <summary>
    /// Tables some OTHER production writer in this build still writes: the
    /// authorities behind `registry-admit-statistic`, `verify-machine-admission`,
    /// `stop-runpod-pod`, the source-content and artifact verbs, and the schema's
    /// own identity row. Same test, same failure when it drifts.
    /// </summary>
    // CHG 2026-09-11 night: the retirements of 2026-09-11 were reversed on the
    // owner's order, so the Plan, Stage, Matrix, lease, grant and daemon
    // authorities write their tables again; the list is what the source writes
    // tonight (85 tables), derived the way the test derives it.
    // CHG 2026-09-11 night (slice 8 of the cell port): the Plan, Stage Plan,
    // Matrix, evidence, queue and daemon families left again, by semantics,
    // and schema version 5 dropped their tables; the list is what the source
    // writes now (33 tables after slice 9). Six tables nothing writes stayed because a
    // surviving table referenced them or the statistics admission reads
    // them; slice 11 moved the references to cells and attempts and dropped
    // five (accepted_results, coordinate_attempts, matrix_coordinates,
    // experiment_matrices, run_plans). CHG 2026-09-12: the per-run archive
    // tables (runs, run_events, configs) left too — the Home never wrote one,
    // and their readers open an archive database by argument — and the model
    // card tables with them, because the compiler declares and checks what a
    // card described.
    // CHG 2026-09-26 (cards-without-hashes, schema 20): `analyses` left with
    // `registry-admit-statistic`, the statistic admission that wrote it.
    public static readonly IReadOnlyList<string> OtherAuthorityTables =
    [
        "artifact_blob_locations",
        "artifact_blobs",
        "artifact_gc_claims",
        "artifact_holds",
        "artifact_references",
        "artifact_snapshot_files",
        "artifact_snapshots",
        "device_grants",
        "environment_install_lease_roots",
        "environment_install_leases",
        "operation_outcomes",
        "provider_stop_requests",
        "registry_observation",
        "registry_schema",
        "transition_receipts"
    ];

    /// <summary>Which writer, if any, can still produce a row for one table.</summary>
    public enum Writer
    {
        /// <summary>One of the ten operations in RegistryOperations.</summary>
        StudyOperation,

        /// <summary>Another authority in this build.</summary>
        OtherAuthority,

        /// <summary>Nothing in this build writes it. Its rows, if any, are unreachable evidence.</summary>
        // CHG 2026-09-28: no writer does not mean no reader. Historical resource
        // slot pointers remain reachable by the storage reclamation guards.
        None,
    }

    /// <summary>One table of the Home.</summary>
    public sealed record Table(string Name, long Rows, Writer WrittenBy);

    /// <summary>The whole answer.</summary>
    public sealed record Home(
        string Path,
        long SchemaVersion,
        string SchemaIdentity,
        bool IsCurrent,
        string? WhyNotCurrent,
        IReadOnlyList<Table> Tables)
    {
        public long RowsTotal => Tables.Sum(table => table.Rows);
        public int TablesWithRows => Tables.Count(table => table.Rows > 0);
    }

    /// <summary>
    /// How many tables a Registry Home of this repository is expected to hold.
    ///
    /// The owner's number (2026-09-11): ten. The six the study operations write,
    /// plus the few an authority beside them still needs. A Home holding more
    /// than that is carrying a layer that retired, and the report says so rather
    /// than leaving a person to count a hundred names. It is a warning and never
    /// a refusal: an old Home is still readable, and what to do about it is the
    /// owner's decision, not this reader's.
    /// </summary>
    public const int ExpectedTablesAtMost = 10;

    /// <summary>The sentence a version-0 Home gets, because 0 does not mean "version zero".</summary>
    public const string UnstampedMeaning =
        "0 means the file carries no stamp at all: it was created before 2026-09-10, when the stamp was "
        + "introduced. RegistrySchemaTransforms deliberately does not walk such a Home forward, so the first "
        + "table to leave Schema.sql makes it refuse with no path forward";

    /// <summary>Read one Home. Refuses by name a path that is not one; writes nothing either way.</summary>
    public static RegistryResult<Home> Read(string path)
    {
        if (Directory.Exists(path))
        {
            return Refused($"{path} is a directory, not a Registry Home file");
        }
        if (!File.Exists(path))
        {
            return Refused($"there is no file at {path}");
        }

        SqliteConnection connection;
        try
        {
            connection = RegistrySchema.OpenReadOnly(path);
        }
        catch (SqliteException error)
        {
            return Refused($"{path} cannot be opened as a SQLite database: {error.Message}");
        }
        using var opened = connection;

        IReadOnlyList<string> names;
        try
        {
            // SQLite opens lazily: a file that is not a database is only refused
            // when the first statement runs, so the read is what has to be
            // guarded, not the open.
            names = TableNames(connection);
        }
        catch (SqliteException error)
        {
            return Refused($"{path} cannot be opened as a SQLite database: {error.Message}");
        }
        if (!names.Contains(RegistrySchema.IdentityTable))
        {
            return Refused(
                $"{path} is a SQLite database but not a Registry Home: it has no {RegistrySchema.IdentityTable} table");
        }

        var version = RegistrySchemaTransforms.ReadVersion(connection);
        // CHG 2026-09-27: the schema a Home holds is said from its version stamp; the
        // stored label is what it was created with, and a walk no longer rewrites it.
        // A Home stamped 0 has no version to say it from, so its stored label is shown.
        var identity = version == RegistrySchemaTransforms.BeforeVersionsExisted
            ? ReadIdentity(connection)
            : EmbeddedRegistrySchema.IdentityOf(version);
        string? whyNot = null;
        try
        {
            RegistrySchema.RequireCurrent(connection, path);
        }
        catch (RegistryFault fault)
        {
            whyNot = fault.Message;
        }

        var written = StudyOperationTables.ToHashSet(StringComparer.Ordinal);
        var others = OtherAuthorityTables.ToHashSet(StringComparer.Ordinal);
        var tables = names
            .Select(name => new Table(
                name,
                CountRows(connection, name),
                written.Contains(name) ? Writer.StudyOperation
                    : others.Contains(name) ? Writer.OtherAuthority
                    : Writer.None))
            .OrderBy(table => table.Name, StringComparer.Ordinal)
            .ToList();

        return RegistryResult<Home>.Ok(new Home(path, version, identity, whyNot is null, whyNot, tables));
    }

    /// <summary>The report a person reads, in the order the questions are asked.</summary>
    public static string Render(Home home)
    {
        var text = new System.Text.StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"registry home {home.Path}\n");
        text.Append(CultureInfo.InvariantCulture, $"  schema version {home.SchemaVersion}");
        if (home.SchemaVersion == RegistrySchemaTransforms.BeforeVersionsExisted)
        {
            text.Append(CultureInfo.InvariantCulture, $" — {UnstampedMeaning}");
        }
        text.Append(CultureInfo.InvariantCulture,
            $"\n  this binary's version {RegistrySchemaTransforms.Current}\n");
        text.Append(CultureInfo.InvariantCulture, $"  schema identity {home.SchemaIdentity}\n");
        var currency = home.IsCurrent ? "  current: yes\n" : $"  current: no — {home.WhyNotCurrent}\n";
        text.Append(currency);
        text.Append(CultureInfo.InvariantCulture,
            $"  {home.Tables.Count} tables, {home.TablesWithRows} with rows, {home.RowsTotal} rows in total\n");
        if (home.Tables.Count > ExpectedTablesAtMost)
        {
            var unwritten = home.Tables.Count(table => table.WrittenBy == Writer.None);
            var warning =
                $"  WARNING: this repository expects at most {ExpectedTablesAtMost} tables in a Home and this one has "
                + $"{home.Tables.Count}, {unwritten} of which nothing writes. A Home carrying tables no operation "
                + "writes is carrying a layer that retired; the rows below say whether anything would be lost by "
                + "cutting them.\n";
            text.Append(warning);
        }

        foreach (var (writer, title) in new[]
        {
            (Writer.StudyOperation, "written by the study operations"),
            (Writer.OtherAuthority, "written by another authority in this build"),
            (Writer.None, "no writer in this build — rows here are unreachable evidence"),
        })
        {
            var group = home.Tables.Where(table => table.WrittenBy == writer).ToList();
            text.Append(CultureInfo.InvariantCulture, $"\n{title} ({group.Count})\n");
            foreach (var table in group)
            {
                text.Append(CultureInfo.InvariantCulture, $"  {table.Name,-42} {table.Rows,10}\n");
            }
        }
        return text.ToString();
    }

    static RegistryResult<Home> Refused(string detail) =>
        RegistryResult<Home>.Rejected(new RegistryRefusal("refused", "invalid_request", detail));

    static IReadOnlyList<string> TableNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var names = new List<string>();
        using var row = command.ExecuteReader();
        while (row.Read())
        {
            names.Add(row.GetString(0));
        }
        return names;
    }

    static long CountRows(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        // The name came from sqlite_master in this same database, and it is
        // quoted, so there is nothing here a caller could have chosen.
        command.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    static string ReadIdentity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT schema_identity FROM {RegistrySchema.IdentityTable} WHERE singleton = 1";
        try
        {
            return command.ExecuteScalar() as string ?? "none";
        }
        catch (SqliteException)
        {
            return "unreadable";
        }
    }
}
