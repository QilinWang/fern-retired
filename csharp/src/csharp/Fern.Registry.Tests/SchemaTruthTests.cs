using Fern.Registry;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Registry.Tests;

public sealed class SchemaTruthTests
{
    [Fact]
    public void Opens_a_registry_with_the_current_embedded_schema()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        Assert.Equal(EmbeddedRegistrySchema.Identity, RegistrySchema.ReadIdentity(writer.Connection));
    }

    [Fact]
    public void Hand_written_schema_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fern-hand-{Guid.NewGuid():N}.db");
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
        using (var connection = new SqliteConnection(builder.ConnectionString))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE runs (id INTEGER PRIMARY KEY)";
            create.ExecuteNonQuery();
        }

        var bytes = File.ReadAllBytes(path);
        var error = Assert.Throws<RegistryFault>(() => RegistryHome.OpenWriter(path));
        Assert.Contains("archive", error.Message);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
