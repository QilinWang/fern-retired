using Microsoft.Data.Sqlite;

namespace Fern.Registry;

/// <param name="CopiedBeforeWalk">
/// CHG 2026-09-27: the copy of the whole Registry the walk made before its
/// first step, when a step on its way deletes rows; null when it made none.
/// </param>
public sealed record RegistryInitializationReceipt(
    string Schema,
    string Registry,
    string SchemaIdentity,
    bool Replayed,
    string? CopiedBeforeWalk = null)
{
    public const string SchemaId = "fern.registry_initialization.v1";
}

public abstract record RegistryInitializationOutcome
{
    public sealed record Initialized(RegistryInitializationReceipt Receipt) : RegistryInitializationOutcome;
    public sealed record Refused(string Detail) : RegistryInitializationOutcome;
}

/// <summary>
/// Initialize one new Home from the current embedded schema.
///
/// A foreign schema is a read-only input to a refusal. A Home written under an
/// OLDER registered schema version is walked forward by
/// <see cref="RegistrySchemaTransforms"/> inside one transaction; a Home whose
/// version this binary does not know is refused, and reading a Home never
/// writes to it.
/// </summary>
public static class RegistrySchema
{
    public const string IdentityTable = "registry_schema";
    static readonly Lazy<SortedDictionary<string, string>> RequiredObjects = new(() =>
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        CreateObjects(connection);
        return ReadObjects(connection);
    });

    /// <summary>CHG 2026-09-27: what <see cref="RequireCurrent"/> compares, read from the same fresh Home.</summary>
    static readonly Lazy<SortedDictionary<string, string>> RequiredStructure = new(() =>
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        CreateObjects(connection);
        return ReadStructure(connection);
    });

    /// <summary>
    /// The exact DDL of the named objects as the embedded schema creates them,
    /// with the observation triggers of every named table, joined for one
    /// transform step. Reading them back from the same in-memory Home that
    /// <see cref="RequiredObjects"/> reads guarantees a walked-forward Home
    /// compares equal to a fresh one, object by object.
    /// </summary>
    internal static string StatementsFor(params string[] names)
    {
        // ReadObjects keys every object as "type:name", so a bare name is tried
        // as a table, an index and a trigger in turn.
        var required = RequiredObjects.Value;
        var statements = new List<string>();
        foreach (var name in names)
        {
            string? sql = null;
            foreach (var type in new[] { "table", "index", "trigger", "view" })
            {
                if (required.TryGetValue($"{type}:{name}", out sql)) break;
            }
            if (sql is null)
                throw new InvalidOperationException($"the embedded schema has no object named {name}");
            statements.Add(sql + ";");
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                if (required.TryGetValue($"trigger:fern_observe_{name}_{operation}", out var trigger))
                    statements.Add(trigger + ";");
            }
        }
        return string.Join("\n", statements);
    }

    public static RegistryInitializationOutcome Initialize(string registryPath)
    {
        try
        {
            if (Directory.Exists(registryPath))
                return new RegistryInitializationOutcome.Refused($"Registry Home \"{registryPath}\" is not a regular file");

            if (File.Exists(registryPath))
            {
                // Refusal must precede writable address access, database
                // creation, journaling changes and any application SQL write.
                using var access = RegistryAccess.OpenConnection(registryPath, readOnly: true);
                using var existing = Open(registryPath, SqliteOpenMode.ReadOnly);
                if (HasObjects(existing))
                {
                    // A refusal must precede writable address access, so decide
                    // it here: only a Home that CAN be brought current falls
                    // through to the writable transaction that would do it.
                    // Falling through unconditionally created the .access file
                    // for a database that was then refused (2026-09-10).
                    var version = RegistrySchemaTransforms.ReadVersion(existing);
                    if (version > RegistrySchemaTransforms.Current)
                    {
                        // Written by a binary that believed itself newer. Its
                        // objects may match and still mean something else.
                        throw RefuseExisting(registryPath,
                            $"schema version {version} is newer than this binary's {RegistrySchemaTransforms.Current}");
                    }
                    // CHG 2026-09-10: decide read-only and write NOTHING. A Home
                    // written before the stamp existed, whose objects and identity
                    // already match, IS current — the stamp is the only thing it
                    // lacks and reading it as current costs nobody anything.
                    // CHG 2026-09-26: "objects" alone now; the stored label no
                    // longer takes part (RequireCurrent).
                    // Stamping it here made a check write to the thing it checks:
                    // five callers use Initialize purely as a gate, including
                    // `backfill-protocol-columns --dry-run`, whose flag is read
                    // AFTER the gate, and nothing in the path looks at whether the
                    // file is one of the 98 archived fern.db under results/.
                    // AGENTS.md: a check must not change the thing it is checking.
                    if (version == RegistrySchemaTransforms.Current
                        || version == RegistrySchemaTransforms.BeforeVersionsExisted
                        || !CanBringCurrent(existing))
                    {
                        RequireCurrent(existing, registryPath);
                        return Success(registryPath, replayed: true);
                    }
                }
            }

            using var writableAccess = RegistryAccess.OpenConnection(registryPath, readOnly: false);
            using var connection = Open(registryPath, SqliteOpenMode.ReadWriteCreate);
            return InitializeConnection(connection, registryPath);
        }
        catch (Exception error) when (error is SqliteException or RegistryFault or IOException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            return new RegistryInitializationOutcome.Refused(
                $"cannot initialize Registry Home \"{registryPath}\": {error.Message}");
        }
    }

    /// <summary>
    /// Open one Home read-only, for a reader that must be able to describe a
    /// Home this binary would refuse as not current. It is here because this is
    /// the file that owns opening a Home by schema rather than by contract;
    /// <see cref="RegistryHome"/>'s readers call RequireCurrent and are the
    /// right door for everything else.
    /// </summary>
    public static SqliteConnection OpenReadOnly(string path) => Open(path, SqliteOpenMode.ReadOnly);

    /// <summary>
    /// Copy a Home to <paramref name="destination"/> with SQLite's online
    /// backup, reading the source read-only: one consistent moment of it, even
    /// while a Home server writes. CHG 2026-09-25 (slice P3): what
    /// <c>preview-registry-walk</c> walks, so a person sees what a schema step
    /// would do to a live Home before it is done to it.
    /// CHG 2026-09-27: also the walk's own copy before a step that deletes
    /// rows (was CopyForPreview). The walk holds its write transaction and has
    /// written nothing yet; this second, read-only connection reads the Home as
    /// last committed, and no other writer can commit meanwhile. The walk's own
    /// connection cannot be the source: SQLite refuses a backup read through a
    /// connection that holds the write lock.
    /// </summary>
    public static void CopyHome(string source, string destination)
    {
        if (File.Exists(destination)) throw new InvalidOperationException($"{destination} already exists");
        using var from = OpenReadOnly(source);
        using var to = Open(destination, SqliteOpenMode.ReadWriteCreate);
        from.BackupDatabase(to);
    }

    static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 60,
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    // The same transaction is used by the public path and by owned temporary
    // test connections. No alternative SQL or schema source can be supplied.
    internal static RegistryInitializationOutcome InitializeConnection(SqliteConnection connection, string registryPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON";
        command.ExecuteNonQuery();
        command.CommandText = "BEGIN IMMEDIATE";
        command.ExecuteNonQuery();
        string? copied = null;
        try
        {
            // Another initializer can have committed while this connection
            // waited for its transaction. Recheck before any DDL or row write.
            if (HasObjects(connection))
            {
                copied = BringCurrent(connection, registryPath);
                RequireCurrent(connection, registryPath);
                command.CommandText = "COMMIT";
                command.ExecuteNonQuery();
                return Success(registryPath, replayed: true, copied);
            }

            CreateObjects(connection);
            RegistrySchemaTransforms.StampVersion(connection, RegistrySchemaTransforms.Current);
            command.CommandText = "INSERT INTO registry_schema (singleton, schema_identity) VALUES (1, $identity)";
            command.Parameters.AddWithValue("$identity", EmbeddedRegistrySchema.Identity);
            command.ExecuteNonQuery();
            RequireCurrent(connection, registryPath);
            command.CommandText = "COMMIT";
            command.Parameters.Clear();
            command.ExecuteNonQuery();
            return Success(registryPath, replayed: false);
        }
        catch (Exception error) when (error is SqliteException or RegistryFault or InvalidOperationException)
        {
            command.CommandText = "ROLLBACK";
            command.Parameters.Clear();
            command.ExecuteNonQuery();
            // CHG 2026-09-27: the rollback leaves the Home as the copy holds it,
            // so the walk's own copy goes with it.
            if (copied is not null) File.Delete(copied);
            return new RegistryInitializationOutcome.Refused(error.Message);
        }
    }

    static RegistryInitializationOutcome Success(string path, bool replayed, string? copied = null) =>
        new RegistryInitializationOutcome.Initialized(new RegistryInitializationReceipt(
            RegistryInitializationReceipt.SchemaId, path, EmbeddedRegistrySchema.Identity, replayed, copied));

    static bool HasObjects(SqliteConnection connection)
    {
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT EXISTS(SELECT 1 FROM main.sqlite_schema WHERE name NOT LIKE 'sqlite_%')";
        return Convert.ToInt64(query.ExecuteScalar()) != 0;
    }

    static void CreateObjects(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = EmbeddedRegistrySchema.Sql;
        command.ExecuteNonQuery();
        RegistryObservation.InstallTriggers(connection);
    }

    /// <summary>Read the stored schema identity; never initialize or change a database.</summary>
    public static string ReadIdentity(SqliteConnection connection)
    {
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM main.sqlite_schema WHERE type = 'table' AND name = 'registry_schema'";
        if (exists.ExecuteScalar() is null)
            throw new RegistryFault("Registry has no current schema identity; archive this database whole and initialize a new Registry Home");
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT singleton, schema_identity FROM registry_schema";
        using var row = query.ExecuteReader();
        if (!row.Read() || row.GetInt64(0) != 1 || row.IsDBNull(1))
            throw new RegistryFault("Registry schema identity is missing or invalid; archive this database whole and initialize a new Registry Home");
        var identity = row.GetString(1);
        if (row.Read())
            throw new RegistryFault("Registry has multiple schema identities; archive this database whole and initialize a new Registry Home");
        return identity;
    }

    /// <summary>
    /// One read-only schema gate for all current Registry consumers. A matching
    /// label alone does not prove that the named tables and triggers still exist.
    /// </summary>
    /// <remarks>
    /// CHG 2026-09-26: a Home is current when its version stamp is this
    /// binary's (or predates stamps) and every object it holds is the embedded
    /// schema's, compared in canonical form (no comments, no layout). The stored
    /// identity is a label: a known archive's label is still refused by name,
    /// and a label written by an older build no longer refuses a Home whose
    /// objects are current. It was compared first and exactly, and it was a
    /// SHA-256 over Schema.sql comments included, so a comment edit made every
    /// build refuse every Home (owner, 2026-09-26: "why refused, we need to fix it").
    ///
    /// CHG 2026-09-27 (owner: compare structure with PRAGMA table_info, rather
    /// than the text): the objects are compared by what SQLite reports of them
    /// (<see cref="ReadStructure"/>), not by the text that created them. The
    /// canonical form of that text is gone, and so is the need for Schema.sql to
    /// spell a table the way SQLite rewrites it after ALTER TABLE ADD COLUMN: a
    /// column a step adds equals the one a fresh Home is created with. A Home
    /// missing an object, holding one more, or with a column added by hand is
    /// still refused and named. Not compared: a CHECK constraint, a partial
    /// index's WHERE and a trigger's body, which SQLite keeps only in the text.
    /// </remarks>
    public static void RequireCurrent(SqliteConnection connection, string registryPath)
    {
        var identity = ReadIdentity(connection);
        // A known archive gets its own sentence.
        if (ArchivedSchemas.TryGetValue(identity, out var described))
            throw RefuseArchived(registryPath, identity, described);
        var label = identity == EmbeddedRegistrySchema.Identity ? "" : $"stored schema identity {identity}; ";
        var version = RegistrySchemaTransforms.ReadVersion(connection);
        if (version > RegistrySchemaTransforms.Current)
            throw new RegistryFault(
                $"Registry Home \"{registryPath}\": schema version {version} is newer than this binary's {RegistrySchemaTransforms.Current}; use a newer fern-run");
        if (version != RegistrySchemaTransforms.Current && version != RegistrySchemaTransforms.BeforeVersionsExisted)
            throw new RegistryFault(
                $"Registry Home \"{registryPath}\": schema version {version} is older than this binary's {RegistrySchemaTransforms.Current}; "
                + "`fern-run preview-registry-walk` shows the walk forward and `fern-run initialize-registry` performs it");
        var actual = ReadStructure(connection);
        var required = RequiredStructure.Value;
        foreach (var (name, structure) in required)
        {
            if (!actual.TryGetValue(name, out var current))
                throw RefuseExisting(registryPath, $"{label}current schema is missing {name}");
            if (current != structure)
                throw RefuseExisting(registryPath, $"{label}current schema definition differs at {name}");
        }
        foreach (var name in actual.Keys)
            if (!required.ContainsKey(name))
                throw RefuseExisting(registryPath, $"{label}current schema has unexpected object {name}");
    }

    /// <summary>
    /// CHG 2026-09-27: every object of a Home by type and name, described by
    /// what SQLite reports of it. A table: its columns as PRAGMA table_info gives
    /// them (position, name, declared type, NOT NULL, default, primary key) and
    /// its foreign keys as PRAGMA foreign_key_list does. An index: its table,
    /// whether it is unique or partial, and its columns (PRAGMA index_list and
    /// index_info). A trigger or a view: the table it belongs to.
    /// </summary>
    static SortedDictionary<string, string> ReadStructure(SqliteConnection connection)
    {
        var objects = new List<(string Type, string Name, string Table)>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT type, name, tbl_name FROM main.sqlite_schema
                WHERE name NOT LIKE 'sqlite_%' AND sql IS NOT NULL
                """;
            using var rows = query.ExecuteReader();
            while (rows.Read()) objects.Add((rows.GetString(0), rows.GetString(1), rows.GetString(2)));
        }
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (type, name, table) in objects)
        {
            var lines = new List<string>();
            switch (type)
            {
                case "table":
                    lines.AddRange(Lines(connection, name, table, """
                        SELECT 'column ' || cid || ' ' || name || ' ' || type || ' not null ' || "notnull"
                            || ' default ' || ifnull(dflt_value, '-') || ' primary key ' || pk
                        FROM pragma_table_info($name) ORDER BY cid
                        """));
                    lines.AddRange(Lines(connection, name, table, """
                        SELECT 'foreign key ' || id || '.' || seq || ' ' || "from" || ' references ' || "table"
                            || '(' || ifnull("to", '-') || ') on update ' || on_update || ' on delete ' || on_delete
                        FROM pragma_foreign_key_list($name) ORDER BY id, seq
                        """));
                    break;
                case "index":
                    lines.Add($"on {table}");
                    lines.AddRange(Lines(connection, name, table, """
                        SELECT 'unique ' || "unique" || ' partial ' || partial FROM pragma_index_list($table) WHERE name = $name
                        """));
                    lines.AddRange(Lines(connection, name, table, """
                        SELECT 'column ' || seqno || ' ' || ifnull(name, '(expression)') FROM pragma_index_info($name) ORDER BY seqno
                        """));
                    break;
                default:
                    lines.Add($"on {table}");
                    break;
            }
            result.Add($"{type}:{name}", string.Join("\n", lines));
        }
        return result;
    }

    static List<string> Lines(SqliteConnection connection, string name, string table, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$name", name);
        if (sql.Contains("$table", StringComparison.Ordinal)) command.Parameters.AddWithValue("$table", table);
        using var rows = command.ExecuteReader();
        var lines = new List<string>();
        while (rows.Read()) lines.Add(rows.GetString(0));
        return lines;
    }

    /// <summary>
    /// Walk an existing Home forward to the current schema version, inside the
    /// caller's transaction, so <see cref="RequireCurrent"/> has a way to be
    /// satisfied instead of only a way to refuse.
    ///
    /// A Home written before the version stamp reads 0. When its objects
    /// already match the embedded schema it is current in every way but the
    /// stamp, and is adopted by stamping it — that is every Home in existence
    /// on 2026-09-10, and it transforms nothing. Otherwise each registered step
    /// runs in order, and a version with no registered step is refused by name,
    /// which is the honest answer: nobody wrote the way forward.
    /// CHG 2026-09-27: returns the copy of the whole Registry the walk made
    /// before its first step, or null. It makes one when a step on its way
    /// declares DeletesRows, and removes it again if a step fails.
    /// </summary>
    internal static string? BringCurrent(SqliteConnection connection, string registryPath) =>
        BringCurrent(connection, registryPath, RegistrySchemaTransforms.Steps, RegistrySchemaTransforms.Current);

    /// <summary>The same walk over a supplied step list, so a test can exercise a real transform while the shipped list is empty.</summary>
    internal static string? BringCurrent(SqliteConnection connection, string registryPath,
        IReadOnlyList<RegistrySchemaTransforms.Step> steps, long current)
    {
        // CHG 2026-09-27: only a Registry Home walks. The science store stamps
        // the same header with its own version; without this, one named here by
        // mistake would be copied and have Registry steps run on it before the
        // final check refused it.
        ReadIdentity(connection);
        var version = RegistrySchemaTransforms.ReadVersion(connection);
        if (version == current) return null;
        if (version > current)
            throw RefuseExisting(registryPath, $"schema version {version} is newer than this binary's {current}");

        if (version == RegistrySchemaTransforms.BeforeVersionsExisted)
        {
            // A Home predating the stamp is read as current when it matches and
            // handed to RequireCurrent when it does not, which names the object
            // that differs — a better answer than "no registered transform from
            // 0". Either way nothing is written: the stamp is not worth making a
            // check mutate recorded evidence to acquire.
            return null;
        }

        // CHG 2026-09-27 (owner): the walk copies the whole Registry itself, once,
        // before its first step, when a step on its way deletes rows; that copy is
        // where those rows are kept. A person used to make it by hand, one name per
        // step, before the walk would run. The way is read first, so a missing step
        // refuses before anything is copied.
        var way = new List<RegistrySchemaTransforms.Step>();
        for (var from = version; from < current; from++)
        {
            way.Add(Leaving(steps, from)
                ?? throw RefuseExisting(registryPath,
                    $"schema version {from} has no registered transform toward {current}"));
        }
        var copied = way.Any(step => step.DeletesRows) ? CopyBeforeWalk(registryPath) : null;
        try
        {
            foreach (var step in way)
            {
                // CHG 2026-09-25: a step that converts values names every row it
                // cannot carry, and nothing is written.
                if (step.Refuses is { } refuses)
                {
                    using var check = connection.CreateCommand();
                    check.CommandText = refuses;
                    var refusals = new List<string>();
                    using (var reader = check.ExecuteReader())
                    {
                        while (reader.Read()) refusals.Add(reader.GetString(0));
                    }
                    if (refusals.Count > 0)
                    {
                        // Not RefuseExisting: its advice, to archive the Home whole,
                        // is wrong here. The rows are fixed in place, then the same
                        // Home walks.
                        throw new RegistryFault(
                            $"Registry Home \"{registryPath}\": schema version {step.From} cannot walk forward, and nothing was written: "
                            + string.Join("; ", refusals) + ". " + step.RefusalRemedy);
                    }
                }
                using (var apply = connection.CreateCommand())
                {
                    apply.CommandText = step.Sql;
                    apply.ExecuteNonQuery();
                }
                RegistrySchemaTransforms.StampVersion(connection, step.From + 1);
            }
        }
        catch
        {
            if (copied is not null) File.Delete(copied);
            throw;
        }
        // CHG 2026-09-27 (owner: the identity row equals the version number, yet
        // each walk dropped its triggers, rewrote it and made them again, "纯仪式",
        // pure ceremony): the walk no longer touches the row. It holds the label
        // written when the Home was created (or by a walk before this change);
        // what a Home holds now is its version stamp, and readers say it from that.
        return copied;
    }

    /// <summary>
    /// CHG 2026-09-27: the walk's copy of the whole Registry, at
    /// <c>&lt;registry&gt;.before-walk-&lt;local date&gt;</c>, numbered -2, -3 and on
    /// when a walk the same day already left one.
    /// </summary>
    static string CopyBeforeWalk(string registryPath)
    {
        var stem = $"{registryPath}.before-walk-{DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}";
        var destination = stem;
        for (var number = 2; File.Exists(destination); number++) destination = $"{stem}-{number}";
        CopyHome(registryPath, destination);
        return destination;
    }

    static RegistrySchemaTransforms.Step? Leaving(IReadOnlyList<RegistrySchemaTransforms.Step> steps, long version)
    {
        foreach (var step in steps)
        {
            if (step.From == version) return step;
        }
        return null;
    }

    /// <summary>Whether a writable transaction could bring this Home current, read without writing anything.</summary>
    static bool CanBringCurrent(SqliteConnection connection)
    {
        // CHG 2026-09-27: a database without the registry_schema row, or a known
        // archive, is handed to RequireCurrent, which refuses it by name.
        string identity;
        try { identity = ReadIdentity(connection); }
        catch (RegistryFault) { return false; }
        if (ArchivedSchemas.ContainsKey(identity)) return false;
        var version = RegistrySchemaTransforms.ReadVersion(connection);
        if (version > RegistrySchemaTransforms.Current) return false;
        if (version == RegistrySchemaTransforms.BeforeVersionsExisted) return MatchesEmbedded(connection);
        return RegistrySchemaTransforms.From(version) is not null;
    }

    /// <summary>Whether every schema object already equals the embedded definition, without refusing.</summary>
    /// <remarks>CHG 2026-09-27: compared by structure, as <see cref="RequireCurrent"/> compares.</remarks>
    static bool MatchesEmbedded(SqliteConnection connection)
    {
        var actual = ReadStructure(connection);
        var required = RequiredStructure.Value;
        if (actual.Count != required.Count) return false;
        foreach (var (name, structure) in required)
        {
            if (!actual.TryGetValue(name, out var current) || current != structure) return false;
        }
        return true;
    }


    /// <summary>
    /// The schema fingerprints of Registry Homes written before 2026-09-11,
    /// with the day each was current.
    ///
    /// They are named rather than left to a generic mismatch because the
    /// mismatch is not a fault to investigate: on the owner's word of
    /// 2026-09-11 those Homes are archives that nothing reads, and an operator
    /// who meets this refusal needs to be told that, and told where the results
    /// actually are, not handed two hex strings to compare.
    ///
    /// A fingerprint joins this table when a schema is superseded, so the
    /// refusal keeps naming the situation instead of degrading to "differs".
    /// </summary>
    static readonly IReadOnlyDictionary<string, string> ArchivedSchemas =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["registry-schema:df2351ad516d40f2d7b8c0d67a6905320117de46705557f7f30b06c8b97f7124"] =
                "the store of 2026-09-08, last written by the fern-run built from commit 1eab3c03",
        };

    static RegistryFault RefuseExisting(string path, string detail) =>
        new($"Registry Home \"{path}\": {detail}; archive this database whole and initialize a new Registry Home");

    /// <summary>
    /// The refusal for a Home whose schema predates 2026-09-11: say what it is
    /// and where its results are, rather than that two fingerprints differ.
    /// </summary>
    static RegistryFault RefuseArchived(string path, string identity, string described) =>
        new($"Registry Home \"{path}\" is the pre-2026-09-11 store ({described}); "
          + "archived on the owner's word of 2026-09-11, nothing reads it. Its collected results are in the "
          + "science database and in each run's results.json; a new Home is a new file, started from the "
          + $"current schema. Stored fingerprint {identity}.");

    static SortedDictionary<string, string> ReadObjects(SqliteConnection connection)
    {
        using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT type || ':' || name, sql FROM main.sqlite_schema
            WHERE name NOT LIKE 'sqlite_%' AND sql IS NOT NULL ORDER BY type, name
            """;
        using var rows = query.ExecuteReader();
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        while (rows.Read()) result.Add(rows.GetString(0), rows.GetString(1));
        return result;
    }
}
