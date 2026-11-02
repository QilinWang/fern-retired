using Microsoft.Data.Sqlite;
using System.Text.Json.Serialization;

namespace Fern.Registry;

/// <summary>Committed database change notification; never a Plan authority or event cursor.</summary>
public static class RegistryObservation
{
    public sealed record Version(
        [property: JsonPropertyName("identity")] string Identity,
        [property: JsonPropertyName("revision")] long Revision);

    public static Version Read(SqliteConnection connection)
    {
        using var query = connection.CreateCommand();
        query.CommandTimeout = 5;
        query.CommandText = "SELECT identity, revision FROM registry_observation WHERE singleton = 1";
        using var row = query.ExecuteReader();
        if (!row.Read()) throw new RegistryFault("Registry observation coordinate is missing; reconciliation is required");
        return new Version(row.GetString(0), row.GetInt64(1));
    }

    // Called only by initialization, inside the schema/identity transaction. Ordinary
    // application tables cover every monitor input, including shared authority.
    // FTS virtual/shadow tables are not state owners; their source tables are.
    internal static void InstallTriggers(SqliteConnection connection)
    {
        var old = new List<string>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT name FROM sqlite_master WHERE type = 'trigger' AND name GLOB 'fern_observe_*'";
            using var rows = query.ExecuteReader();
            while (rows.Read()) old.Add(rows.GetString(0));
        }
        foreach (var trigger in old)
        {
            using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP TRIGGER \"{trigger.Replace("\"", "\"\"")}\"";
            drop.ExecuteNonQuery();
        }
        var tables = new List<string>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT name FROM pragma_table_list WHERE schema = 'main' AND type = 'table'
                AND name NOT LIKE 'sqlite_%' AND name NOT IN ('registry_observation', 'registry_schema')
                ORDER BY name
                """;
            using var rows = query.ExecuteReader();
            while (rows.Read()) tables.Add(rows.GetString(0));
        }
        foreach (var table in tables)
        foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            var quoted = table.Replace("\"", "\"\"");
            using var create = connection.CreateCommand();
            create.CommandText = $"""
                CREATE TRIGGER "fern_observe_{quoted}_{operation}" AFTER {operation} ON "{quoted}"
                BEGIN UPDATE registry_observation SET revision = revision + 1 WHERE singleton = 1; END
                """;
            create.ExecuteNonQuery();
        }
    }
}
