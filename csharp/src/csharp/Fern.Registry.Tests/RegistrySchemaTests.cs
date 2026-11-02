using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Registry.Tests;

public sealed class RegistrySchemaTests : IDisposable
{
    readonly string directory = Directory.CreateTempSubdirectory("fern-schema-tests-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    string PathFor(string name) => Path.Combine(directory, name);

    static SqliteConnection Open(string path, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitializesCurrentSchemaDirectlyForNewOrEmptyFiles(bool emptyFile)
    {
        var path = PathFor("current.db");
        if (emptyFile) File.WriteAllBytes(path, []);
        var receipt = Assert.IsType<RegistryInitializationOutcome.Initialized>(
            RegistrySchema.Initialize(path)).Receipt;

        Assert.Equal(RegistryInitializationReceipt.SchemaId, receipt.Schema);
        Assert.Equal(path, receipt.Registry);
        Assert.Equal(EmbeddedRegistrySchema.Identity, receipt.SchemaIdentity);
        Assert.False(receipt.Replayed);
        using var connection = Open(path, readOnly: true);
        RegistrySchema.RequireCurrent(connection, path);
        Assert.Equal(receipt.SchemaIdentity, RegistrySchema.ReadIdentity(connection));
        Assert.Equal(0L, Scalar(connection,
            "SELECT count(*) FROM sqlite_schema WHERE lower(name) LIKE '%recipe%' OR lower(sql) LIKE '%recipe%'"));
        Assert.Equal(0L, Scalar(connection,
            "SELECT count(*) FROM sqlite_schema WHERE name = '__diesel_schema_migrations'"));
        Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM registry_schema"));
        Assert.Equal(32, RegistryObservation.Read(connection).Identity.Length);
    }

    [Fact]
    public void EmbeddedDefinitionMatchesTheOnlyCurrentSchemaSource()
    {
        var file = Path.Combine(ProductionHome.FindRepoRoot(), "src", "csharp", "Fern.Registry", "Schema.sql");
        var sql = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(sql, EmbeddedRegistrySchema.Sql);
        // CHG 2026-09-26: the stored label is the schema version; no hash.
        Assert.Equal($"registry-schema:version-{RegistrySchemaTransforms.Current}", EmbeddedRegistrySchema.Identity);
        Assert.Equal(new[] { "fern.registry.schema.sql" }, typeof(RegistrySchema).Assembly.GetManifestResourceNames());
    }

    [Fact]
    public void ExistingUnidentifiedDatabaseIsRefusedWithoutChangingAnyBytes()
    {
        var path = PathFor("archive-input.db");
        using (var original = Open(path))
            Execute(original, "CREATE TABLE historical_rows(id INTEGER PRIMARY KEY, payload BLOB); INSERT INTO historical_rows VALUES (7, x'00ff1020');");
        var bytes = File.ReadAllBytes(path);
        Assert.False(File.Exists(path + ".access"));

        var refusal = Assert.IsType<RegistryInitializationOutcome.Refused>(RegistrySchema.Initialize(path));

        Assert.Contains("archive this database whole", refusal.Detail);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        foreach (var suffix in new[] { ".access", "-journal", "-wal", "-shm" })
            Assert.False(File.Exists(path + suffix));
        using var unchanged = Open(path, readOnly: true);
        Assert.Equal("00FF1020", Scalar(unchanged, "SELECT hex(payload) FROM historical_rows WHERE id = 7"));
        Assert.Equal(1L, Scalar(unchanged, "SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'"));
    }

    [Theory]
    // CHG 2026-09-26: scheduler_leases and runs retired with earlier schema
    // steps, so the damage names tables the schema still has.
    // CHG 2026-09-26: site_sources retired at step 16; study_compilers stands in.
    [InlineData("DROP TABLE study_compilers", "table:study_compilers")]
    [InlineData("ALTER TABLE sites ADD COLUMN unexpected TEXT", "table:sites")]
    [InlineData("DROP TRIGGER fern_observe_sites_INSERT", "trigger:fern_observe_sites_INSERT")]
    [InlineData("CREATE TABLE unexpected_rows(value TEXT)", "table:unexpected_rows")]
    public void MatchingIdentityDoesNotHideMissingOrChangedSchemaObjects(string damage, string objectName)
    {
        var path = PathFor("damaged.db");
        Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(path));
        using (var fixture = Open(path)) Execute(fixture, damage);
        var bytes = File.ReadAllBytes(path);
        var accessBytes = File.ReadAllBytes(path + ".access");

        using (var reader = Open(path, readOnly: true))
        {
            Assert.Equal(EmbeddedRegistrySchema.Identity, RegistrySchema.ReadIdentity(reader));
            var error = Assert.Throws<RegistryFault>(() => RegistrySchema.RequireCurrent(reader, path));
            Assert.Contains(objectName, error.Message);
        }
        var refusal = Assert.IsType<RegistryInitializationOutcome.Refused>(RegistrySchema.Initialize(path));
        Assert.Contains(objectName, refusal.Detail);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(accessBytes, File.ReadAllBytes(path + ".access"));
    }

    [Fact]
    public void ACommentInsideAStoredDefinitionIsNotASchemaDifference()
    {
        // Owner 2026-09-26: correcting a comment must not make a build refuse a
        // Home whose tables did not change. SQLite keeps a CREATE statement's
        // text, comments included, so the stored text gets a comment here.
        var path = PathFor("commented.db");
        Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(path));
        using (var fixture = Open(path))
            Execute(fixture, "PRAGMA writable_schema = ON; "
                + "UPDATE sqlite_schema SET sql = replace(sql, 'CREATE TABLE sites (', 'CREATE TABLE sites ( -- a note' || char(10)) WHERE name = 'sites'; "
                + "PRAGMA writable_schema = OFF;");
        using var reader = Open(path, readOnly: true);
        Assert.Contains("-- a note", (string)Scalar(reader, "SELECT sql FROM sqlite_schema WHERE name = 'sites'")!);
        RegistrySchema.RequireCurrent(reader, path);
    }

    [Fact]
    public void AForeignIdentityCannotBeRewrittenAsCurrent()
    {
        var path = PathFor("different-schema.db");
        using (var fixture = Open(path))
            Execute(fixture, "CREATE TABLE registry_schema(singleton INTEGER, schema_identity TEXT); INSERT INTO registry_schema VALUES (1, 'different-schema');");
        var bytes = File.ReadAllBytes(path);

        var refusal = Assert.IsType<RegistryInitializationOutcome.Refused>(RegistrySchema.Initialize(path));

        Assert.Contains("different-schema", refusal.Detail);
        Assert.Contains("archive this database whole", refusal.Detail);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".access"));
    }

    [Fact]
    public void FailedInitializationRollsBackSchemaAndCanRetry()
    {
        using var connection = Open(":memory:");
        // The real schema seeds its observation coordinate after its DDL. Fail
        // that SQLite function, without accepting a substitute schema input.
        connection.CreateFunction<int, byte[]>("randomblob", _ =>
            throw new InvalidOperationException("fixture observation seed refused"));

        var refusal = Assert.IsType<RegistryInitializationOutcome.Refused>(
            RegistrySchema.InitializeConnection(connection, ":memory:"));

        Assert.Contains("fixture observation seed refused", refusal.Detail);
        Assert.Equal(0L, Scalar(connection, "SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'"));
        connection.CreateFunction<int, byte[]>("randomblob", count => RandomNumberGenerator.GetBytes(count));
        var receipt = Assert.IsType<RegistryInitializationOutcome.Initialized>(
            RegistrySchema.InitializeConnection(connection, ":memory:")).Receipt;
        Assert.False(receipt.Replayed);
        RegistrySchema.RequireCurrent(connection, ":memory:");
        Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM registry_schema"));
    }

    [Fact]
    public async Task ConcurrentInitializationPublishesOneIdentity()
    {
        var path = PathFor("concurrent.db");
        using var start = new ManualResetEventSlim(false);
        var pending = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return RegistrySchema.Initialize(path);
        })).ToArray();
        start.Set();
        var outcomes = await Task.WhenAll(pending);
        Assert.Single(outcomes.OfType<RegistryInitializationOutcome.Initialized>(), x => !x.Receipt.Replayed);
        foreach (var outcome in outcomes)
        {
            if (outcome is RegistryInitializationOutcome.Refused refusal)
                Assert.Contains("Registry access", refusal.Detail);
            var replay = Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(path));
            Assert.True(replay.Receipt.Replayed);
        }
        using var connection = Open(path, readOnly: true);
        RegistrySchema.RequireCurrent(connection, path);
        Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM registry_schema"));
        Assert.Equal(0L, RegistryObservation.Read(connection).Revision);
    }

    [Fact]
    public void CurrentReadsAndInitializationReplayAreReadOnly()
    {
        var path = PathFor("replay.db");
        Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(path));
        var bytes = File.ReadAllBytes(path);
        var access = File.ReadAllBytes(path + ".access");
        RegistryObservation.Version observation;
        using (var reader = Open(path, readOnly: true))
        {
            RegistrySchema.RequireCurrent(reader, path);
            observation = RegistryObservation.Read(reader);
        }

        var replay = Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(path));

        Assert.True(replay.Receipt.Replayed);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(access, File.ReadAllBytes(path + ".access"));
        using var unchanged = Open(path, readOnly: true);
        Assert.Equal(observation, RegistryObservation.Read(unchanged));
    }

    [Fact]
    public void CurrentSchemaKeepsObservationAndSchemaIdentityImmutable()
    {
        var path = PathFor("observed.db");
        Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(path));
        using var connection = Open(path);
        var before = RegistryObservation.Read(connection);

        // CHG 2026-09-26: a site row, not a configs row; configs left the Home on 2026-09-12 (schema step 7).
        Execute(connection, "INSERT INTO sites(name, capabilities_json, capacity_json, created_at) VALUES ('fixture-site', '{}', '{}', '2026-09-26T00:00:00Z')");

        var after = RegistryObservation.Read(connection);
        Assert.Equal(before.Identity, after.Identity);
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Throws<SqliteException>(() => Execute(connection, "UPDATE registry_schema SET schema_identity = 'different'"));
        Assert.Throws<SqliteException>(() => Execute(connection, "DELETE FROM registry_schema"));
        Assert.Throws<SqliteException>(() => Execute(connection, "DELETE FROM registry_observation"));
        Assert.Throws<SqliteException>(() => Execute(connection, "UPDATE registry_observation SET identity = 'different'"));
        RegistrySchema.RequireCurrent(connection, path);
    }
}
