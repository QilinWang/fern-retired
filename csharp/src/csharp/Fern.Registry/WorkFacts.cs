using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Fern.Registry;

/// <summary>
/// One registered study and how far its declared cells have got.
///
/// CHG 2026-09-11: this was one registered PLAN. The wire key stays `plans`
/// because the `work` tool outside this repository parses it and its rename is
/// that tool's patch to make; what fills it is now a study, which is the only
/// registered unit of work the store has.
/// </summary>
public sealed record WorkFactsPlan(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("cells_accepted")] long CellsAccepted,
    [property: JsonPropertyName("cells_total")] long CellsTotal,
    [property: JsonPropertyName("host")] string? Host);

/// <summary>
/// One immutable Analysis row. <c>metrics</c> are the numbers the reporting
/// analysis put in its free-form <c>summary</c>; nothing here recomputes them.
///
/// CHG 2026-09-26 (schema 20; owner: "Analysis names: remove, even this
/// analysis entering registry is suspicious slop"): always empty. Statistic
/// admission and the analyses table it wrote are gone. The key stays on the
/// wire for the reason <see cref="WorkFactsEvidenceRelease"/> gives: the
/// `work` tool outside this repository parses this document.
/// </summary>
public sealed record WorkFactsAnalysis(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("metrics")] IReadOnlyDictionary<string, double> Metrics);

/// <summary>
/// One frozen Evidence Release and its latest terminal event.
///
/// CHG 2026-09-11: always empty. The evidence-release family retired with the
/// tables it read; the key stays on the wire because the `work` tool outside
/// this repository reads this document, and a key that disappears is a parse
/// error there rather than a smaller answer.
/// </summary>
public sealed record WorkFactsEvidenceRelease(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("state")] string State);

/// <summary>Everything Registry Home holds about one identity, in one document.</summary>
public sealed record WorkFactsDocument(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("observed_at")] string ObservedAt,
    [property: JsonPropertyName("plans")] IReadOnlyList<WorkFactsPlan> Plans,
    [property: JsonPropertyName("analyses")] IReadOnlyList<WorkFactsAnalysis> Analyses,
    [property: JsonPropertyName("evidence_releases")] IReadOnlyList<WorkFactsEvidenceRelease> EvidenceReleases,
    [property: JsonPropertyName("withdrawn")] IReadOnlyList<string> Withdrawn);

/// <summary>
/// The read-only projection behind <c>fern-registry work-facts</c>.
///
/// A planning tool outside the Registry needs to SHOW a result without copying
/// its numbers into a second store, so this answers one question — what does
/// Registry Home know about this identity — from the rows the owning
/// authorities already wrote. It opens nothing but a query-only connection,
/// declares no table, and never falls back to a guess: an identity that matches
/// no row is reported as unknown rather than described.
///
/// The identity is resolved by asking the tables in order of how exactly they
/// name a thing: an Analysis hash, an Evidence Release, a registered plan, then
/// a Research Design card that plans are bound to. That order matters because
/// only the last one is a foreign key rather than a primary key.
///
/// CHG 2026-09-26 (schema 20): an Analysis hash is no longer asked for; the
/// analyses table left the Registry with statistic admission, so
/// `analysis:&lt;key&gt;` is answered as unknown like any other identity nobody
/// registered.
/// </summary>
public static class WorkFacts
{
    public const string DocumentSchema = "fern.work-facts.v1";

    public const string KindStudy = "study";
    // CHG 2026-09-26 (schema 20): KindAnalysis ("analysis") and the
    // AnalysisRegistered state left with the analyses table.
    public const string KindUnknown = "unknown";

    static readonly JsonSerializerOptions Wire = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        IndentSize = 2,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Render the document exactly as the command prints it, with a trailing newline.</summary>
    public static string Render(WorkFactsDocument document) =>
        JsonSerializer.Serialize(document, Wire) + "\n";

    /// <summary>
    /// Read every fact for one identity. Throws <see cref="RegistryFault"/> only
    /// when a row contradicts the schema; an identity nobody registered is a
    /// normal answer with <c>found=false</c>.
    /// </summary>
    public static WorkFactsDocument Read(SqliteConnection connection, string id)
    {
        var observedAt = ObservedAt(connection);

        // CHG 2026-09-26 (schema 20): the Analysis branch, which read the
        // analyses table by its 16-digit key and flattened the stored summary's
        // numbers into metrics, left with that table.

        // CHG 2026-09-11: the Task plan, run plan, Research Design card and
        // Evidence Release branches are gone with the tables they read. No
        // Registry operation writes task_plans, run_plans, matrix_coordinates,
        // accepted_results or evidence_releases any more, so each of those
        // branches could only ever answer "absent" on a Home written since the
        // one-store cut — an answer that reads like a fact and is not one.
        // A registered study is what the store now holds, and it is asked for
        // by the name `register-study` was given.
        if (StudyFacts(connection, WithoutPrefix(id, "study:")) is { } study)
        {
            return Document(id, KindStudy, observedAt, [study], [], [], []);
        }

        return new WorkFactsDocument(
            DocumentSchema, id, KindUnknown, false, observedAt, [], [], [], []);
    }

    static WorkFactsDocument Document(
        string id,
        string kind,
        string observedAt,
        IReadOnlyList<WorkFactsPlan> plans,
        IReadOnlyList<WorkFactsAnalysis> analyses,
        IReadOnlyList<WorkFactsEvidenceRelease> releases,
        IReadOnlyList<string> withdrawn) =>
        new(DocumentSchema, id, kind, true, observedAt, plans, analyses, releases, withdrawn);

    // ---- The registered study ---------------------------------------------

    /// <summary>
    /// One registered study by name, or null when no study carries that name.
    ///
    /// The newest revision of a name is the one a person means; an older
    /// revision's cells are that revision's history. "How far" is a count of
    /// rows, because a study owns its cells directly and a cell is done when it
    /// has an accepted attempt. The host is the Site of the most recent
    /// attempt, which is the machine that last worked on this study.
    /// </summary>
    static WorkFactsPlan? StudyFacts(SqliteConnection connection, string studyName)
    {
        using var head = connection.CreateCommand();
        head.CommandText =
            """
            SELECT id, requested_state FROM studies
            WHERE name = $name ORDER BY revision DESC LIMIT 1
            """;
        head.Parameters.AddWithValue("$name", studyName);
        long studyId;
        string state;
        using (var row = head.ExecuteReader())
        {
            if (!row.Read())
            {
                return null;
            }
            studyId = row.GetInt64(0);
            state = row.GetString(1);
        }

        var (accepted, total) = StudyCellCounts(connection, studyId);
        return new WorkFactsPlan(studyName, state, accepted, total, HostForStudy(connection, studyId));
    }

    /// <summary>How many cells this study declared, and how many have an accepted result.</summary>
    static (long Accepted, long Total) StudyCellCounts(SqliteConnection connection, long studyId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*), COUNT(accepted_attempt_id) FROM cells WHERE study_id = $study";
        command.Parameters.AddWithValue("$study", studyId);
        using var row = command.ExecuteReader();
        return row.Read() ? (row.GetInt64(1), row.GetInt64(0)) : (0, 0);
    }

    /// <summary>The Site of this study's most recent attempt, or null before any cell is claimed.</summary>
    static string? HostForStudy(SqliteConnection connection, long studyId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.name FROM attempts a
            JOIN cells c ON c.id = a.cell_id
            JOIN sites s ON s.id = a.site_id
            WHERE c.study_id = $study
            ORDER BY a.id DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$study", studyId);
        return command.ExecuteScalar() as string;
    }

    // ---- Small helpers ---------------------------------------------------

    /// <summary>
    /// The observation clock is the database's, not this process's, so a report
    /// and the rows it describes are timestamped by the same authority.
    /// </summary>
    static string ObservedAt(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT strftime('%Y-%m-%dT%H:%M:%SZ', 'now')";
        return command.ExecuteScalar() as string
            ?? throw new RegistryFault("Registry Home did not answer its own clock");
    }

    static string WithoutPrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;
}
