using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>Current initialization, observation transactions and the public Registry process boundary.</summary>
public sealed class RegistryInitializationTests
{
    [Fact]
    public void ObservationRevisionRollsBackWithTheWriter()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = new SqliteConnection($"Data Source={path};Pooling=False");
        writer.Open();
        using var reader = OpenReadOnly(path);
        const string revision = "SELECT revision FROM registry_observation WHERE singleton = 1";
        var before = Count(reader, revision);
        using (var transaction = writer.BeginTransaction())
        {
            using var insert = writer.CreateCommand();
            insert.Transaction = transaction;
            // CHG 2026-09-26: sites, not scheduler_authorities, a table the Home no longer has.
            insert.CommandText = "INSERT INTO sites(name, capabilities_json, capacity_json, created_at) VALUES ('observation-test', '{}', '{}', '0')";
            insert.ExecuteNonQuery();
            Assert.True(Count(writer, revision) > before);
            Assert.Equal(before, Count(reader, revision));
            transaction.Rollback();
        }
        Assert.Equal(before, Count(reader, revision));
        using var committed = writer.CreateCommand();
        committed.CommandText = "INSERT INTO sites(name, capabilities_json, capacity_json, created_at) VALUES ('observation-test', '{}', '{}', '0')";
        committed.ExecuteNonQuery();
        var inserted = Count(reader, revision);
        Assert.True(inserted > before);
        committed.CommandText = "UPDATE sites SET created_at = '1' WHERE name = 'observation-test'";
        committed.ExecuteNonQuery();
        Assert.True(Count(reader, revision) > inserted);
        var updated = Count(reader, revision);
        committed.CommandText = "DELETE FROM sites WHERE name = 'observation-test'";
        committed.ExecuteNonQuery();
        Assert.True(Count(reader, revision) > updated);
    }

    [Fact]
    public void ObservationRevisionCoversEveryOrdinaryApplicationTable()
    {
        var path = ProductionHome.CreateRegistry();
        using var database = OpenReadOnly(path);
        using var tables = database.CreateCommand();
        tables.CommandText = """
            SELECT name FROM pragma_table_list WHERE schema = 'main' AND type = 'table'
            AND name NOT LIKE 'sqlite_%' AND name NOT IN ('registry_observation', 'registry_schema')
            """;
        using var rows = tables.ExecuteReader();
        var names = new List<string>();
        while (rows.Read()) names.Add(rows.GetString(0));
        // CHG 2026-09-26: tables the current schema has; cell_attempts, scheduler_leases and
        // receipt_consumer_validations left the Home in the 2026-09-12/13 schema steps.
        Assert.Contains("cells", names);
        Assert.Contains("attempts", names);
        Assert.Contains("sites", names);
        foreach (var name in names)
        {
            using var trigger = database.CreateCommand();
            trigger.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND tbl_name = $table AND name LIKE 'fern_observe_%'";
            trigger.Parameters.AddWithValue("$table", name);
            Assert.Equal(3L, trigger.ExecuteScalar());
        }
    }

    [Fact]
    public void ObservationIdentitySurvivesReopenAndInitializationReplay()
    {
        var path = ProductionHome.CreateRegistry();
        string ReadIdentity()
        {
            using var database = OpenReadOnly(path);
            using var query = database.CreateCommand();
            query.CommandText = "SELECT identity FROM registry_observation WHERE singleton = 1";
            return (string)query.ExecuteScalar()!;
        }
        var identity = ReadIdentity();
        Assert.Equal(32, identity.Length);
        var replay = Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(path));
        Assert.True(replay.Receipt.Replayed);
        Assert.Equal(identity, ReadIdentity());
    }

    static long Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    [Fact]
    public void EmbeddedSchemaInitializesOutsideTheSourceTree()
    {
        var outsideWorkspace = Directory.CreateTempSubdirectory("fern-initialize-cli-").FullName;
        var path = Path.Combine(outsideWorkspace, "current.db");
        var created = ProductionHome.RunFernRegistryIn(outsideWorkspace,
            ["initialize-registry", "--registry", path], "", deadlineMilliseconds: 30_000);
        Assert.True(created.Exit == 0, created.Stderr);
        using var receipt = JsonDocument.Parse(created.Stdout);
        Assert.Equal(RegistryInitializationReceipt.SchemaId, receipt.RootElement.GetProperty("schema").GetString());
        Assert.Equal(path, receipt.RootElement.GetProperty("registry").GetString());
        Assert.Equal(EmbeddedRegistrySchema.Identity, receipt.RootElement.GetProperty("schema_identity").GetString());
        Assert.False(receipt.RootElement.GetProperty("replayed").GetBoolean());
        var bytes = File.ReadAllBytes(path);
        var replay = ProductionHome.RunFernRegistryIn(outsideWorkspace,
            ["initialize-registry", "--registry", path], "", deadlineMilliseconds: 30_000);
        Assert.True(replay.Exit == 0, replay.Stderr);
        using var replayReceipt = JsonDocument.Parse(replay.Stdout);
        Assert.True(replayReceipt.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(EmbeddedRegistrySchema.Identity, replayReceipt.RootElement.GetProperty("schema_identity").GetString());
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void InitializationCreatesAnEmptyHomeWithoutUnrelatedRows()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("fern-initialize-").FullName, "current.db");
        var initialized = Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(path));
        Assert.Equal(RegistryInitializationReceipt.SchemaId, initialized.Receipt.Schema);
        Assert.Equal(path, initialized.Receipt.Registry);
        Assert.Equal(EmbeddedRegistrySchema.Identity, initialized.Receipt.SchemaIdentity);
        Assert.False(initialized.Receipt.Replayed);
        using (var database = OpenReadOnly(path))
        {
            // CHG 2026-09-26: the current schema's tables; the five named here before left the Home in 2026-09-12/13.
            foreach (var table in new[] { "sites", "sources", "studies", "cells", "attempts", "events" })
                Assert.Equal(0, Count(database, $"SELECT COUNT(*) FROM {table}"));
            Assert.Equal(EmbeddedRegistrySchema.Identity, RegistrySchema.ReadIdentity(database));
        }
        var bytes = File.ReadAllBytes(path);
        var replay = Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(path));
        Assert.Equal(initialized.Receipt with { Replayed = true }, replay.Receipt);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void ForeignSchemaIsRefusedByNameWithoutChangingItsBytes()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("fern-foreign-schema-").FullName, "old.db");
        using (var database = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = "CREATE TABLE prior_data (value TEXT); INSERT INTO prior_data VALUES ('retained')";
            command.ExecuteNonQuery();
        }
        var bytes = File.ReadAllBytes(path);
        var refusal = Assert.IsType<RegistryInitializationOutcome.Refused>(RegistrySchema.Initialize(path));
        Assert.Contains(path, refusal.Detail, StringComparison.Ordinal);
        Assert.Contains("archive", refusal.Detail, StringComparison.Ordinal);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        foreach (var suffix in new[] { ".access", "-journal", "-wal", "-shm" })
            Assert.False(File.Exists(path + suffix), path + suffix);
    }
}