using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// The Registry refused any schema difference and offered no way to satisfy the
/// refusal, so changing one column meant archiving every Home whole — 211
/// databases under results/ on 2026-09-10. That followed from identifying the
/// schema only by a SHA-256 of its own text, which says "different" and never
/// "which version", so it could not say which transformation to apply.
/// </summary>
public sealed class RegistrySchemaVersionTests : IDisposable
{
    readonly string directory = Directory.CreateTempSubdirectory("fern-schema-version-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    string PathFor(string name) => Path.Combine(directory, name);

    static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    static long VersionOf(string path)
    {
        using var connection = Open(path);
        return RegistrySchemaTransforms.ReadVersion(connection);
    }

    [Fact]
    public void ANewHomeIsStampedWithTheCurrentVersion()
    {
        var path = PathFor("new.db");
        Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(path));
        Assert.Equal(RegistrySchemaTransforms.Current, VersionOf(path));
    }

    [Fact]
    public void AHomeWrittenBeforeVersionsExistedIsAcceptedAsCurrent()
    {
        // Every Home in existence on 2026-09-10: the current schema, no stamp.
        // The stamp is the only thing it lacks, so it IS current.
        var path = PathFor("unstamped.db");
        Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(path));
        using (var connection = Open(path))
        {
            RegistrySchemaTransforms.StampVersion(connection, RegistrySchemaTransforms.BeforeVersionsExisted);
        }

        var outcome = RegistrySchema.Initialize(path);

        Assert.IsType<RegistryInitializationOutcome.Initialized>(outcome);
    }

    [Fact]
    public void ReadingAnUnstampedHomeWritesNothingToIt()
    {
        // A check must not change the thing it is checking. Five callers use
        // Initialize purely as a gate, including backfill-protocol-columns
        // --dry-run, whose flag is read after the gate; and nothing in the path
        // asks whether the file is one of the 98 archived fern.db under results/.
        // Stamping the version here would have edited recorded evidence.
        var path = PathFor("untouched.db");
        RegistrySchema.Initialize(path);
        using (var connection = Open(path))
        {
            RegistrySchemaTransforms.StampVersion(connection, RegistrySchemaTransforms.BeforeVersionsExisted);
        }
        var bytes = File.ReadAllBytes(path);
        var objects = ObjectNames(path);

        RegistrySchema.Initialize(path);
        RegistrySchema.Initialize(path);

        Assert.Equal(objects, ObjectNames(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(RegistrySchemaTransforms.BeforeVersionsExisted, VersionOf(path));
    }

    [Fact]
    public void AVersionNewerThanThisBinaryIsRefusedByNumber()
    {
        var path = PathFor("from-the-future.db");
        RegistrySchema.Initialize(path);
        using (var connection = Open(path))
        {
            RegistrySchemaTransforms.StampVersion(connection, RegistrySchemaTransforms.Current + 5);
        }

        var outcome = RegistrySchema.Initialize(path);

        var refusal = Assert.IsType<RegistryInitializationOutcome.Refused>(outcome);
        Assert.Contains((RegistrySchemaTransforms.Current + 5).ToString(), refusal.Detail);
        Assert.Contains(RegistrySchemaTransforms.Current.ToString(), refusal.Detail);
    }

    [Fact]
    public void AnUnstampedHomeWhoseSchemaDiffersIsStillRefused()
    {
        // Adoption is for a Home that only lacks the stamp. One that also
        // differs has no registered way forward and must say so.
        var path = PathFor("different.db");
        RegistrySchema.Initialize(path);
        using (var connection = Open(path))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE somebody_elses_table (id INTEGER PRIMARY KEY)";
            command.ExecuteNonQuery();
            RegistrySchemaTransforms.StampVersion(connection, RegistrySchemaTransforms.BeforeVersionsExisted);
        }

        var outcome = RegistrySchema.Initialize(path);

        var refusal = Assert.IsType<RegistryInitializationOutcome.Refused>(outcome);
        Assert.Contains("somebody_elses_table", refusal.Detail);
    }

    [Fact]
    public void TheVersionIsNotASchemaObjectSoTheGateNeverSeesIt()
    {
        // PRAGMA user_version lives in the file header. If it were a table the
        // object comparison would refuse every Home written before it existed,
        // which is the bootstrap this avoids.
        var path = PathFor("header.db");
        RegistrySchema.Initialize(path);
        Assert.DoesNotContain(ObjectNames(path), name => name.Contains("user_version", StringComparison.Ordinal));
    }

    [Fact]
    public void AHomeMissingAnObjectNeverReachesTheWritablePath()
    {
        // The identity is a SHA over the whole Schema.sql text, comments
        // included, so editing a comment moves it while every stored DDL string
        // stays byte-identical. Such a Home once answered "I can be brought
        // current", took the writable acquire and created .access, and was only
        // then refused.
        // CHG 2026-09-26: the label alone no longer refuses (a Home is current by
        // version and objects); this Home is refused for the trigger the test
        // drops, still without reaching the writable path.
        var path = PathFor("identity-drift.db");
        RegistrySchema.Initialize(path);
        File.Delete(path + ".access");
        using (var connection = Open(path))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER registry_schema_no_update; "
                + "UPDATE registry_schema SET schema_identity='registry-schema:someone-elses' WHERE singleton=1";
            command.ExecuteNonQuery();
            RegistrySchemaTransforms.StampVersion(connection, RegistrySchemaTransforms.BeforeVersionsExisted);
        }

        var outcome = RegistrySchema.Initialize(path);

        var refusal = Assert.IsType<RegistryInitializationOutcome.Refused>(outcome);
        Assert.Contains("someone-elses", refusal.Detail);
        Assert.False(File.Exists(path + ".access"));
    }

    // CHG 2026-09-11 night: the shipped list has a real step now (slice 1 of the
    // cell port), so the test step leaves the CURRENT version rather than 1, and
    // the tests below count from there.
    static readonly RegistrySchemaTransforms.Step AddsAColumn = new(
        From: RegistrySchemaTransforms.Current, Why: "a test step one past the shipped list, so the walk itself is exercised",
        // CHG 2026-09-12: over `sites` rather than `model_architectures`, which
        // retired with the model cards. The step needs any table the Home keeps;
        // this one is the Home's own.
        Sql: "ALTER TABLE sites ADD COLUMN a_test_column TEXT");

    [Fact]
    public void ARealStepRunsStampsItsVersionAndLeavesTheIdentityRow()
    {
        // Any step that exists changed Schema.sql, so it changed the identity,
        // and RequireCurrent checks the identity row FIRST. Without moving the
        // row the machinery would refuse itself on its first real use: step
        // succeeds, version stamped, identity mismatch, rollback.
        // CHG 2026-09-27 (owner: rewriting a label equal to the version, past its
        // two triggers, was pure ceremony): the walk leaves the row as it is; the
        // version stamp says what the Home holds. A step that deletes no rows
        // makes the walk copy nothing.
        var path = PathFor("stepped.db");
        RegistrySchema.Initialize(path);
        using var connection = Open(path);
        SetIdentity(connection, "registry-schema:older");

        var copied = RegistrySchema.BringCurrent(connection, path, [AddsAColumn], current: RegistrySchemaTransforms.Current + 1);

        Assert.Null(copied);
        Assert.Equal(RegistrySchemaTransforms.Current + 1, RegistrySchemaTransforms.ReadVersion(connection));
        Assert.Equal("registry-schema:older", IdentityOf(connection));
        Assert.True(HasColumn(connection, "sites", "a_test_column"), "the step's SQL ran");
        Assert.Equal(2L, TriggerCount(connection));
    }

    /// <summary>
    /// CHG 2026-09-27 (owner: a Registry upgrade must not make a person copy the
    /// whole database by hand): a walk whose way holds a step that deletes rows
    /// copies the whole Registry itself before its first step, the rows included;
    /// a step that fails leaves the Home as it was and takes the copy away.
    /// </summary>
    [Fact]
    public void AStepThatDeletesRowsMakesTheWalkCopyTheHomeFirst()
    {
        var path = PathFor("copied.db");
        RegistrySchema.Initialize(path);
        using var connection = Open(path);
        Execute(connection, "INSERT INTO sites (name, capabilities_json, capacity_json, created_at) VALUES ('kept', '{}', '{}', '2026-09-27')");
        var addsAColumn = AddsAColumn with { DeletesRows = false };
        var deletes = new RegistrySchemaTransforms.Step(
            From: RegistrySchemaTransforms.Current + 1, Why: "a test step that deletes rows",
            Sql: "DELETE FROM sites", DeletesRows: true);

        var copied = RegistrySchema.BringCurrent(connection, path, [addsAColumn, deletes], current: RegistrySchemaTransforms.Current + 2);

        Assert.NotNull(copied);
        Assert.StartsWith(path + ".before-walk-", copied!, StringComparison.Ordinal);
        using (var copy = Open(copied))
        {
            // The Home as it was before the walk's first step, not between the two.
            Assert.Equal(RegistrySchemaTransforms.Current, RegistrySchemaTransforms.ReadVersion(copy));
            Assert.False(HasColumn(copy, "sites", "a_test_column"));
            using var count = copy.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM sites";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);
        }

        // A second walk the same day takes the next number rather than the same name.
        var again = PathFor("copied-again.db");
        RegistrySchema.Initialize(again);
        File.Copy(copied, again + Path.GetFileName(copied)[Path.GetFileName(path).Length..]);
        using var second = Open(again);
        var numbered = RegistrySchema.BringCurrent(second, again, [addsAColumn, deletes], current: RegistrySchemaTransforms.Current + 2);
        Assert.EndsWith("-2", numbered!, StringComparison.Ordinal);

        // A step that fails: the Home keeps its version, and the walk's copy is gone.
        var failing = PathFor("failing.db");
        RegistrySchema.Initialize(failing);
        using var third = Open(failing);
        var broken = new RegistrySchemaTransforms.Step(
            From: RegistrySchemaTransforms.Current + 1, Why: "a step that cannot run",
            Sql: "ALTER TABLE no_such_table ADD COLUMN x TEXT", DeletesRows: true);
        Assert.ThrowsAny<Exception>(() => RegistrySchema.BringCurrent(third, failing, [addsAColumn, broken], current: RegistrySchemaTransforms.Current + 2));
        Assert.Empty(Directory.GetFiles(directory, "failing.db.before-walk-*"));
    }

    /// <summary>
    /// The step to 13 is the first that ADDS rather than drops, and what it
    /// adds is a table rather than a column. That is not a preference: this
    /// schema compares every object against the embedded text, and
    /// `ALTER TABLE ADD COLUMN` writes a CREATE TABLE that no cleanly written
    /// Schema.sql can reproduce, because SQLite appends the column after the
    /// table constraints. A Home stepped that way is refused at `table:cells`
    /// by the very identity check the step exists to satisfy -- which is what
    /// happened on the live Home on 2026-09-14 before this was written.
    ///
    /// So the claim is both halves: the step reaches the current identity, and
    /// the cells it finds are untouched, because their digests live beside them
    /// rather than inside their row.
    /// </summary>
    [Fact]
    public void TheStepToThirteenAddsTheRegionTableAndLeavesEveryCellItFindsAlone()
    {
        var path = PathFor("regions.db");
        RegistrySchema.Initialize(path);
        using var connection = Open(path);
        Execute(connection,
            """
            INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
            VALUES ('5e87434e', '[]', 'commit', '2026-09-14');
            INSERT INTO studies (name, revision, compiled_study_json, source_id, requested_state, created_at)
            VALUES ('older', 1, '{}', 1, 'run', '2026-09-14');
            INSERT INTO cells (study_id, ordinal, name, dataset, seed, config_json,
                               requirements_json, retention_json, created_at)
            VALUES (1, 0, 'cell-0', 'lorenz.series', 42, '{}', '{}', '{}', '2026-09-14');
            """);

        // A Home as it stood at 12: the table gone, the version stamped back.
        // CHG 2026-09-26: a current Home has no region table (step 17 drops it).
        RegistrySchemaTransforms.StampVersion(connection, 12);
        SetIdentity(connection, "registry-schema:older");

        var step = RegistrySchemaTransforms.From(12);
        Assert.NotNull(step);
        RegistrySchema.BringCurrent(connection, path, [step!], current: 13);

        Assert.Equal(13, RegistrySchemaTransforms.ReadVersion(connection));
        // CHG 2026-09-26: the table exists after step 12, and step 17 takes it out
        // again (it is empty, so no copy is needed); only then is the Home current.
        Execute(connection, "SELECT 1 FROM cell_config_regions");
        RegistrySchemaTransforms.StampVersion(connection, 17);
        RegistrySchema.BringCurrent(connection, path, [RegistrySchemaTransforms.From(17)!], current: 18);
        // The half the live Home refused: the stepped schema must BE the
        // embedded schema, object for object, or every reader refuses it.
        // CHG 2026-09-27: the walk leaves the stored label as it was.
        Assert.Equal("registry-schema:older", IdentityOf(connection));
        // CHG 2026-09-26: RequireCurrent also requires this binary's version
        // stamp now; the object-for-object comparison this line makes is the
        // same, so the Home is stamped the version its objects already are.
        RegistrySchemaTransforms.StampVersion(connection, RegistrySchemaTransforms.Current);
        RegistrySchema.RequireCurrent(connection, path);

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT name, dataset, seed FROM cells";
        using var rows = read.ExecuteReader();
        Assert.True(rows.Read());
        Assert.Equal("cell-0", rows.GetString(0));
        Assert.Equal("lorenz.series", rows.GetString(1));
        Assert.Equal(42L, rows.GetInt64(2));
        Assert.False(rows.Read());
    }

    [Fact]
    public void AStepThatFailsLeavesTheVersionAndTheIdentityWhereTheyWere()
    {
        var path = PathFor("bad-step.db");
        RegistrySchema.Initialize(path);
        using var connection = Open(path);
        SetIdentity(connection, "registry-schema:older");
        var broken = new RegistrySchemaTransforms.Step(RegistrySchemaTransforms.Current, "a step that cannot run", "ALTER TABLE no_such_table ADD COLUMN x TEXT");

        Assert.ThrowsAny<Exception>(() => RegistrySchema.BringCurrent(connection, path, [broken], current: RegistrySchemaTransforms.Current + 1));

        Assert.Equal(RegistrySchemaTransforms.Current, RegistrySchemaTransforms.ReadVersion(connection));
        Assert.Equal("registry-schema:older", IdentityOf(connection));
    }

    [Fact]
    public void AMissingStepIsRefusedByTheVersionItCannotLeave()
    {
        var path = PathFor("no-step.db");
        RegistrySchema.Initialize(path);
        using var connection = Open(path);

        var fault = Assert.ThrowsAny<Exception>(() => RegistrySchema.BringCurrent(connection, path, [], current: RegistrySchemaTransforms.Current + 2));

        Assert.Contains(RegistrySchemaTransforms.Current.ToString(), fault.Message);
        Assert.Contains((RegistrySchemaTransforms.Current + 2).ToString(), fault.Message);
    }

    static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void AHomeLabelledByAnOlderBuildIsCurrentWhenItsObjectsAre()
    {
        // Owner 2026-09-26: a label written before a comment was corrected, or
        // by the whole-file SHA of older builds, does not refuse a Home whose
        // version and objects are this binary's.
        var path = PathFor("older-label.db");
        RegistrySchema.Initialize(path);
        using var connection = Open(path);
        SetIdentity(connection, "registry-schema:written-by-an-older-build");
        RegistrySchema.RequireCurrent(connection, path);
    }

    [Fact]
    public void AHomeAtAnOlderVersionIsRefusedWithTheWayForward()
    {
        var path = PathFor("older-version.db");
        RegistrySchema.Initialize(path);
        using var connection = Open(path);
        RegistrySchemaTransforms.StampVersion(connection, RegistrySchemaTransforms.Current - 1);
        var refusal = Assert.Throws<RegistryFault>(() => RegistrySchema.RequireCurrent(connection, path));
        Assert.Contains("older than this binary's", refusal.Message);
        Assert.Contains("initialize-registry", refusal.Message);
    }

    static void SetIdentity(SqliteConnection connection, string identity)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER registry_schema_no_update; "
            + $"UPDATE registry_schema SET schema_identity='{identity}' WHERE singleton=1; "
            + "CREATE TRIGGER registry_schema_no_update BEFORE UPDATE ON registry_schema "
            + "BEGIN SELECT RAISE(ABORT, 'Registry schema identity is immutable'); END";
        command.ExecuteNonQuery();
    }

    static string? IdentityOf(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_identity FROM registry_schema WHERE singleton = 1";
        return command.ExecuteScalar() as string;
    }

    static long TriggerCount(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'registry_schema_no_%'";
        return (long)command.ExecuteScalar()!;
    }

    static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM pragma_table_info('{table}') WHERE name = $name";
        command.Parameters.AddWithValue("$name", column);
        return (long)command.ExecuteScalar()! > 0;
    }

    [Fact]
    public void EveryRegisteredStepLeavesADistinctVersionAndReachesTheCurrentOne()
    {
        long seen = RegistrySchemaTransforms.BeforeVersionsExisted;
        var froms = new HashSet<long>();
        foreach (var step in RegistrySchemaTransforms.Steps)
        {
            Assert.True(froms.Add(step.From), $"two steps leave version {step.From}");
            Assert.False(string.IsNullOrWhiteSpace(step.Why), $"step from {step.From} says why it exists");
            seen = Math.Max(seen, step.From + 1);
        }
        if (RegistrySchemaTransforms.Steps.Count > 0)
            Assert.Equal(RegistrySchemaTransforms.Current, seen);
    }

    [Fact]
    public void ARefusedHomeGetsNoWritableAccessFile()
    {
        // The refusal must precede writable address access: falling through to
        // the writable transaction to decide created the .access file for a
        // database that was then refused.
        var path = PathFor("future-refused.db");
        RegistrySchema.Initialize(path);
        File.Delete(path + ".access");
        using (var connection = Open(path))
        {
            RegistrySchemaTransforms.StampVersion(connection, RegistrySchemaTransforms.Current + 1);
        }
        var bytes = File.ReadAllBytes(path);

        Assert.IsType<RegistryInitializationOutcome.Refused>(RegistrySchema.Initialize(path));

        Assert.False(File.Exists(path + ".access"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    static List<string> ObjectNames(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY name";
        using var rows = command.ExecuteReader();
        var names = new List<string>();
        while (rows.Read()) names.Add(rows.GetString(0));
        return names;
    }
}
