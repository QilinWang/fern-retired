using Fern.Registry;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// What a fresh build says when it meets a Registry Home written before
/// 2026-09-11.
///
/// It refuses — the schema is not the current one and a reader that guessed
/// would read the wrong tables — but the refusal has to name the SITUATION.
/// On the owner's word those Homes are archives that nothing reads, and an
/// operator who meets this needs to be told where the results actually are,
/// not handed two hex strings to compare.
/// </summary>
public sealed class ArchivedRegistryRefusalTests
{
    /// <summary>The fingerprint of the store of 2026-09-08, as that build wrote it.</summary>
    const string ArchivedIdentity =
        "registry-schema:df2351ad516d40f2d7b8c0d67a6905320117de46705557f7f30b06c8b97f7124";

    /// <summary>A Home carrying one stated schema identity and nothing else.</summary>
    static string HomeWithIdentity(string identity)
    {
        var directory = Directory.CreateTempSubdirectory("fern-archived-");
        var path = Path.Combine(directory.FullName, "lifecycle.db");
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE registry_schema (singleton INTEGER PRIMARY KEY, schema_identity TEXT NOT NULL);
            INSERT INTO registry_schema VALUES (1, $identity);
            """;
        command.Parameters.AddWithValue("$identity", identity);
        command.ExecuteNonQuery();
        return path;
    }

    static string RefusalFor(string identity)
    {
        var path = HomeWithIdentity(identity);
        var refusal = Assert.Throws<RegistryFault>(() => RegistryHome.OpenReader(path));
        return refusal.Message;
    }

    /// <summary>
    /// The known archive is named, dated, and told where its results live. Not
    /// "differs from": a mismatch an operator cannot act on is a bug in the
    /// message, not in the database.
    /// </summary>
    [Fact]
    public void TheArchivedStoreIsRefusedByNameAndSaysWhereItsResultsAre()
    {
        var message = RefusalFor(ArchivedIdentity);

        Assert.Contains("pre-2026-09-11 store", message, StringComparison.Ordinal);
        Assert.Contains("2026-09-08", message, StringComparison.Ordinal);
        Assert.Contains("nothing reads it", message, StringComparison.Ordinal);
        Assert.Contains("science database", message, StringComparison.Ordinal);
        Assert.Contains("results.json", message, StringComparison.Ordinal);
        Assert.Contains(ArchivedIdentity, message, StringComparison.Ordinal);
        // The generic wording must NOT appear: a named archive is not an
        // unexplained mismatch, and printing both would bury the sentence that
        // matters.
        Assert.DoesNotContain("differs from", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unknown fingerprint is still an unexplained mismatch and keeps the
    /// generic refusal. A Home nobody can account for is a question, not an
    /// archive, and must not be told it is one.
    /// </summary>
    [Fact]
    public void AnUnknownFingerprintKeepsTheGenericRefusal()
    {
        var message = RefusalFor("registry-schema:" + new string('9', 64));

        // CHG 2026-09-26: the generic refusal names the stored fingerprint and
        // what the Home lacks; a label alone no longer refuses a current Home.
        Assert.Contains("stored schema identity registry-schema:9999", message, StringComparison.Ordinal);
        Assert.Contains("archive this database whole", message, StringComparison.Ordinal);
        Assert.DoesNotContain("pre-2026-09-11 store", message, StringComparison.Ordinal);
    }

    /// <summary>A writer refuses on the same gate, so no archive is opened for writing either.</summary>
    [Fact]
    public void AWriterRefusesTheArchivedStoreToo()
    {
        var path = HomeWithIdentity(ArchivedIdentity);
        var refusal = Assert.Throws<RegistryFault>(() => RegistryHome.OpenWriter(path));
        Assert.Contains("pre-2026-09-11 store", refusal.Message, StringComparison.Ordinal);
    }
}
