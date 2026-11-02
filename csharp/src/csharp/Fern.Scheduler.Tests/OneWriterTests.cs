using Fern.Registry;
using Microsoft.Data.Sqlite;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

public sealed class OneWriterTests
{
    [Fact]
    public void SchedulerProductionSourceReachesSqlOnlyThroughRegistryWriter()
    {
        var csharp = Path.Combine(TestRegistry.RepoRoot(), "src", "csharp");
        string[] schedulerSourceRoots =
        [
            Path.Combine(csharp, "Fern.Scheduler"),
            Path.Combine(csharp, "Fern.Site"),
            Path.Combine(csharp, "Fern.Site.Cli"),
        ];
        foreach (var file in schedulerSourceRoots.SelectMany(
                     root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)))
        {
            // The build inventory (Go: package buildinventory) is the
            // installer's own ledger of published binaries, a separate SQLite
            // file that is never the Registry Home and holds no scientific
            // fact; it is the one production source allowed its own connection.
            if (Path.GetFileName(file) == "BuildInventory.cs")
            {
                continue;
            }

            var text = File.ReadAllText(file);
            Assert.DoesNotContain("BEGIN IMMEDIATE", text);
            Assert.DoesNotContain("new SqliteConnection", text);
        }

        var project = File.ReadAllText(
            Path.Combine(csharp, "Fern.Scheduler", "Fern.Scheduler.csproj"));
        Assert.Contains("Fern.Registry", project, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConnectionOpenedOutsideRegistryWriterFailsWhileTheWriterHoldsTheLock()
    {
        var path = TestRegistry.CreateRegistry();
        using var opened = Assert.IsType<RegistryOpenOutcome.OpenedWriter>(
            ProductionRegistry.OpenWriter(path));

        using var started = new ManualResetEventSlim(false);
        using var hold = new ManualResetEventSlim(false);
        var inside = Task.Run(() => opened.Writer.WriteImmediate(_ =>
        {
            started.Set();
            hold.Wait();
            return 0;
        }));
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

        using var outsider = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
            }.ToString());
        outsider.Open();
        using (var timeout = outsider.CreateCommand())
        {
            timeout.CommandText = "PRAGMA busy_timeout = 50";
            timeout.ExecuteNonQuery();
        }

        using var begin = outsider.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE";
        var busy = Assert.Throws<SqliteException>(() => begin.ExecuteNonQuery());
        Assert.Equal(5, busy.SqliteErrorCode);

        hold.Set();
        await inside.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
