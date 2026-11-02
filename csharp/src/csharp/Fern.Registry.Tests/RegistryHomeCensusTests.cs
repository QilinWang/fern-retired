using System.Text.RegularExpressions;
using Fern.Registry;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// `inspect-registry-home`: the reader a schema cut needs before it happens.
///
/// The two facts it exists for are asserted here against real files — a Home
/// this build just created, and a hand-made database with no version stamp,
/// which is what every Home written before 2026-09-10 looks like.
/// </summary>
public sealed class RegistryHomeCensusTests
{
    static RegistryHomeCensus.Home Read(string path)
    {
        var read = RegistryHomeCensus.Read(path);
        Assert.True(read.TryGetValue(out var home), Refusal(read));
        return home;
    }

    static string Refusal<T>(RegistryResult<T> result) =>
        result.TryGetRefusal(out var refusal) ? refusal.Detail : "";

    [Fact]
    public void AFreshHomeIsCurrentStampedAndEmptyOfRows()
    {
        var path = ProductionHome.CreateRegistry();

        var home = Read(path);

        Assert.True(home.IsCurrent);
        Assert.Null(home.WhyNotCurrent);
        Assert.Equal(RegistrySchemaTransforms.Current, home.SchemaVersion);
        Assert.StartsWith("registry-schema:", home.SchemaIdentity, StringComparison.Ordinal);
        // The schema identity row is there; no study has been registered yet.
        Assert.Equal(1, Assert.Single(home.Tables, table => table.Name == "registry_schema").Rows);
        Assert.All(
            home.Tables.Where(table => table.WrittenBy == RegistryHomeCensus.Writer.StudyOperation),
            table => Assert.Equal(0, table.Rows));
    }

    [Fact]
    public void EveryTableIsClassifiedByWhetherAnythingStillWritesIt()
    {
        var home = Read(ProductionHome.CreateRegistry());

        var written = home.Tables.Where(table => table.WrittenBy == RegistryHomeCensus.Writer.StudyOperation)
            .Select(table => table.Name).ToList();
        // CHG 2026-09-11 night (slice 1 of the cell port): supervisors and operator_requests join the six.
        // CHG 2026-09-11 night (slice 6): site_sources, which declared source a Site holds.
        // CHG 2026-09-11 night (slice 7): dispatch_priorities, the order queued work is offered in.
        // CHG 2026-09-25: dispatch_last_moves, a move to the back of a rank; and cell_config_regions,
        // which RegisterStudy has written since 2026-09-14.
        // CHG 2026-09-26: site_compilers, which install-study-source writes through RecordSiteCompiler.
        // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): site_sources and site_compilers
        // leave with installed sources and carried compilers.
        // CHG 2026-09-26 (cards-without-hashes): cell_config_regions leaves at schema 18.
        Assert.Equal(["attempts", "cells", "dispatch_last_moves", "dispatch_priorities", "events", "operator_requests", "sites", "sources", "studies", "study_compilers", "supervisors"], written);
        Assert.Contains(home.Tables, table => table.WrittenBy == RegistryHomeCensus.Writer.OtherAuthority);
        // CHG 2026-09-14: nothing falls in the third group any more. The six
        // that did were the shadow tables of agent memory's search index, and
        // they left with the index. A fresh Home now carries no table that
        // nothing writes, which is the state this census was built to report
        // the absence of; when one appears again, this assertion is what says
        // so.
        // CHG 2026-09-28: the unused preparation snapshot writer is retired;
        // its historical table remains readable without a schema/data migration.
        // CHG 2026-09-28: resource slot declaration also retired; its historical
        // pointers remain readable by reclamation guards without a new writer.
        Assert.Equal(new[] { "preparation_operations", "resource_slots" }, home.Tables
            .Where(table => table.WrittenBy == RegistryHomeCensus.Writer.None)
            .Select(table => table.Name).Order(StringComparer.Ordinal));
        // Nothing is left unclassified: the three groups are the whole Home.
        Assert.Equal(home.Tables.Count, home.Tables.Count(table =>
            table.WrittenBy is RegistryHomeCensus.Writer.StudyOperation
                or RegistryHomeCensus.Writer.OtherAuthority or RegistryHomeCensus.Writer.None));
    }

    /// <summary>
    /// The reason this reader exists: a Home written before the stamp reads 0,
    /// and 0 is not a version — it is the absence of one. The report says so in
    /// the sentence rather than leaving a reader to know it.
    /// </summary>
    [Fact]
    public void AHomeWithNoStampReadsZeroAndTheReportExplainsWhatZeroMeans()
    {
        var path = ProductionHome.CreateRegistry();
        using (var connection = RegistrySchema.OpenReadOnly(path))
        {
            Assert.Equal(RegistrySchemaTransforms.Current, RegistrySchemaTransforms.ReadVersion(connection));
        }
        Unstamp(path);

        var home = Read(path);

        Assert.Equal(RegistrySchemaTransforms.BeforeVersionsExisted, home.SchemaVersion);
        var rendered = RegistryHomeCensus.Render(home);
        Assert.Contains("schema version 0", rendered, StringComparison.Ordinal);
        Assert.Contains("created before 2026-09-10", rendered, StringComparison.Ordinal);
        Assert.Contains("no path forward", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The owner's number: a Home of this repository holds at most ten tables,
    /// and one holding more is carrying a retired layer. It is a warning line,
    /// not a refusal — an old Home is still readable, and what to do about it is
    /// the owner's call.
    /// </summary>
    [Fact]
    public void AHomeWithMoreTablesThanTheRepositoryExpectsSaysSo()
    {
        var home = Read(ProductionHome.CreateRegistry());

        var rendered = RegistryHomeCensus.Render(home);
        Assert.True(home.Tables.Count > RegistryHomeCensus.ExpectedTablesAtMost,
            "this test is about a Home that exceeds the expectation; today's schema does");
        Assert.Contains($"expects at most {RegistryHomeCensus.ExpectedTablesAtMost} tables", rendered, StringComparison.Ordinal);
        Assert.Contains("nothing writes", rendered, StringComparison.Ordinal);
    }

    /// <summary>A Home this build would refuse to open is still described, with the refusal as the reason.</summary>
    [Fact]
    public void AHomeThatIsNotCurrentIsDescribedRatherThanRefused()
    {
        var path = ProductionHome.CreateRegistry();
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE a_table_this_build_does_not_know (x INTEGER)";
            command.ExecuteNonQuery();
        }

        var home = Read(path);

        Assert.False(home.IsCurrent);
        Assert.Contains("a_table_this_build_does_not_know", home.WhyNotCurrent!, StringComparison.Ordinal);
        Assert.Contains(home.Tables, table =>
            table.Name == "a_table_this_build_does_not_know" && table.WrittenBy == RegistryHomeCensus.Writer.None);
    }

    [Fact]
    public void APathThatIsNotARegistryHomeIsRefusedByName()
    {
        var directory = Directory.CreateTempSubdirectory("fern-census-").FullName;

        Assert.Contains("is a directory", Refusal(RegistryHomeCensus.Read(directory)), StringComparison.Ordinal);
        Assert.Contains("there is no file at",
            Refusal(RegistryHomeCensus.Read(Path.Combine(directory, "nothing.db"))), StringComparison.Ordinal);

        var text = Path.Combine(directory, "notes.txt");
        File.WriteAllText(text, "this is not a database");
        Assert.Contains("cannot be opened as a SQLite database",
            Refusal(RegistryHomeCensus.Read(text)), StringComparison.Ordinal);

        var plain = Path.Combine(directory, "plain.db");
        using (var connection = new SqliteConnection($"Data Source={plain};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE notes (x INTEGER)";
            command.ExecuteNonQuery();
        }
        Assert.Contains("not a Registry Home", Refusal(RegistryHomeCensus.Read(plain)), StringComparison.Ordinal);
    }

    /// <summary>
    /// The census is a DECLARED list, and this is what keeps it honest: the same
    /// two sets are derived here from the source the operations are written in,
    /// and the test fails when the declaration and the code disagree. Without it
    /// the report would quietly go stale the first time an authority retires —
    /// which is exactly what it is being used to decide.
    /// </summary>
    [Fact]
    public void TheDeclaredSetsAreWhatTheSourceActuallyWrites()
    {
        var root = ProductionHome.FindRepoRoot();
        var registry = Path.Combine(root, "src", "csharp", "Fern.Registry");
        var schema = File.ReadAllText(Path.Combine(registry, "Schema.sql"));
        var tables = Regex.Matches(schema, @"CREATE TABLE\s+""?([A-Za-z0-9_]+)""?")
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var operations = WrittenTables(File.ReadAllText(Path.Combine(registry, "RegistryOperations.cs")), tables);
        var others = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "csharp"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains("/obj/", StringComparison.Ordinal) || file.Contains("/bin/", StringComparison.Ordinal)
                || file.Contains("Tests", StringComparison.Ordinal)
                || Path.GetFileName(file) == "RegistryOperations.cs")
            {
                continue;
            }
            others.UnionWith(WrittenTables(File.ReadAllText(file), tables));
        }
        others.ExceptWith(operations);

        Assert.Equal(
            RegistryHomeCensus.StudyOperationTables.Order(StringComparer.Ordinal),
            operations.Order(StringComparer.Ordinal));
        Assert.Equal(
            RegistryHomeCensus.OtherAuthorityTables.Order(StringComparer.Ordinal),
            others.Order(StringComparer.Ordinal));
    }

    /// <summary>Every table this source inserts into, updates or deletes from.</summary>
    static HashSet<string> WrittenTables(string source, IReadOnlySet<string> known)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in new[]
        {
            @"INSERT(?:\s+OR\s+\w+)?\s+INTO\s+""?([A-Za-z0-9_]+)""?",
            @"REPLACE\s+INTO\s+""?([A-Za-z0-9_]+)""?",
            @"UPDATE\s+""?([A-Za-z0-9_]+)""?\s+SET",
            @"DELETE\s+FROM\s+""?([A-Za-z0-9_]+)""?",
        })
        {
            foreach (Match match in Regex.Matches(source, pattern, RegexOptions.IgnoreCase))
            {
                var table = match.Groups[1].Value;
                if (known.Contains(table))
                {
                    found.Add(table);
                }
            }
        }
        return found;
    }

    /// <summary>Drop the version stamp, the way a Home written before 2026-09-10 has none.</summary>
    static void Unstamp(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version = 0";
        command.ExecuteNonQuery();
    }
}
