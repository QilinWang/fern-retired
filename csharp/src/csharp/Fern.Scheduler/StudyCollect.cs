using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Fern.Experiment;
using Fern.Registry;
using Microsoft.Data.Sqlite;

namespace Fern.Scheduler;

/// <summary>
/// The study chain's plating, first half (2026-09-04): which registered unit
/// file goes where, so `study collect` reads a registered study's units as it
/// reads hand-run ones. Pure over the registry's rows; `fern-run
/// collect-study` copies the bytes.
/// CHG 2026-09-05: Execute now owns the Registry snapshot and verified local
/// export; the terminal adapter only projects its report. Ordered workflow:
/// docs/how-to/study-collection.md.
///
/// The second half, written 2026-09-11 for `collect-study-results` when each
/// cell began saving its own result, kept in the same class when the first
/// half came back the same night:
/// What collecting a finished study means now that a cell can save its own
/// result.
///
/// A cell ends in one of two ways. It saved its results document into the
/// selected science store itself and handed back a REFERENCE — which database,
/// which study and run, and the digest of what it stored — or it handed back
/// the DOCUMENT, which is what a machine that cannot reach the results tree
/// does. The first collects into one small file of references that
/// `study collect <card> --accepted <file>` reads back through the store; the
/// second collects into one folder per cell, as it always did.
///
/// A study that did both has no single answer, and guessing which the reader
/// wanted is how half a study goes missing quietly. It is refused with both
/// lists of cell names.
///
/// The decision lives here rather than in the verb because a verb keeps no
/// logic of its own: this is the part with cases, so this is the part with
/// tests.
///
/// CHG 2026-09-26 (slice 2 of
/// docs/programs/2026-09-10-the-cli-is-a-language-not-a-catalog/2026-09-26-the-command-line-as-a-language-in-gleam/design.md):
/// a study whose other cells still wait is collected. Until now the verb
/// refused a study while any cell had no accepted attempt, and that night the
/// accepted cells of running studies could be read only from worker files. Now
/// the accepted cells are collected exactly as before, and each cell without an
/// accepted attempt is carried in the plan with the state monitor-study prints
/// for it, so the verb names it. Only a study with no accepted cell at all is
/// refused, as it was.
/// </summary>
public static class StudyCollect
{
    // CHG: persistent result wires are owned by their reader, not by the
    // retiring LocalCellWorker. Existing documents and event bytes are unchanged.
    public const string UnitResultsSchema = "fern.study_unit_results.v4";

    /// <summary>
    /// What a runtime reports back about the results document when the request named a results
    /// root. It comes in two shapes and says which it is in <c>saved</c>.
    ///
    /// <c>saved: true</c> carries the database, study, run, sha256 and reference: the store took
    /// the document, and this worker records the REFERENCE in place of it — a few hundred bytes
    /// instead of a hundred kilobytes — after checking the digest against the results file the
    /// cell wrote.
    ///
    /// <c>saved: false</c> carries the database it tried and a reason. The cell is NOT failed:
    /// it trained, its results.json is in the attempt's outputs folder, and that document is
    /// recorded exactly as it was before any of this existed. The reason is written against the
    /// attempt so a person reading `monitor-study` sees that this cell's result is not in the
    /// store yet, instead of discovering it when a collection comes up short.
    /// </summary>
    public const string SavedResultSchema = "fern.saved_cell_result.v1";

    /// <summary>The event kind carrying why a trained cell's result did not reach the store.</summary>
    public const string ResultNotSaved = "result_not_saved";

    /// <summary>The one file a study whose cells saved themselves collects into.</summary>
    public const string AcceptedCellResultsSchema = "fern.accepted_cell_results.v1";

    /// <summary>
    /// One accepted cell, the text its attempt recorded, and — when the science
    /// store would not take the document — why.
    /// </summary>
    /// <param name="StoreRefusedBecause">
    /// Null when the store took the document or was never asked. A sentence when
    /// the cell trained and its result stayed in the attempt's folder, which is
    /// the case a collection must never pass over quietly: a store unreachable
    /// for a whole card is twenty cells whose numbers are not where the study
    /// says they are.
    /// </param>
    public sealed record Accepted(string Cell, string Result, string? StoreRefusedBecause = null);

    /// <summary>
    /// CHG 2026-09-26: a cell with no accepted attempt yet, and the state
    /// monitor-study prints for it: waiting, opened, running, failed, lost or
    /// cancelled. It is not collected; it is named.
    /// </summary>
    public sealed record Unaccepted(string Cell, string State)
    {
        /// <summary>The line a collection prints for this cell on standard error.</summary>
        public string Sentence => $"cell {Cell} has no accepted attempt and was not collected; its state is {State}";
    }

    /// <summary>What to write, and where it goes.</summary>
    public abstract record Plan
    {
        /// <summary>
        /// CHG 2026-09-26: the cells this collection leaves out because no attempt
        /// of theirs is accepted yet, in the study's order. Empty when every cell
        /// is accepted.
        /// </summary>
        public IReadOnlyList<Unaccepted> NotCollected { get; init; } = [];

        /// <summary>Every cell saved itself: one document of references, written to the named FILE.</summary>
        public sealed record References(string Document, int CellCount) : Plan;

        /// <summary>Every cell handed its document back: one folder per cell under the named DIRECTORY.</summary>
        public sealed record Documents(IReadOnlyList<Accepted> Units) : Plan;
    }

    /// <summary>
    /// Decide what this study's accepted results collect into, or refuse by name.
    /// Reads nothing and writes nothing: it is given the rows and returns the text.
    /// </summary>
    public static TextResult<Plan> Decide(
        string studyName, IReadOnlyList<Accepted> units, IReadOnlyList<Unaccepted> unaccepted, bool asDocuments)
    {
        if (units.Count == 0)
        {
            // CHG 2026-09-26: the refusal names the cells and their states, in the
            // words the verb used when it refused for a missing cell.
            return TextResult<Plan>.Refused(unaccepted.Count == 0
                ? $"study {studyName} has no accepted cell to collect"
                : $"study {studyName} has no accepted cell to collect; {unaccepted.Count} cell(s) have no accepted attempt: "
                  + string.Join(", ", unaccepted.Select(cell => $"{cell.Cell} ({cell.State})")));
        }
        // CHG 2026-09-26: at least one cell is accepted, so the accepted cells are
        // collected by the rules below, unchanged, and the others ride on the plan.
        TextResult<Plan> Collect(Plan plan) => TextResult<Plan>.Ok(plan with { NotCollected = unaccepted });

        // CHG: an explicit document export is the recovery route after a store
        // refusal. Preserve every accepted byte, including saved references;
        // Python resolves those references and verifies all cells before saving
        // the collected scientific result. No attempt is retried or rewritten.
        if (asDocuments)
        {
            return Collect(new Plan.Documents(units));
        }

        // A cell whose result never reached the store is not a collectable cell,
        // whichever shape the rest of the study is in. It trained and its
        // document is in the attempt's folder; what is missing is the copy the
        // study's readers will look for, and a collection that wrote itself
        // anyway would be twenty green cells over twenty missing results.
        var refused = units.Where(unit => unit.StoreRefusedBecause is not null).ToList();
        if (refused.Count != 0)
        {
            var named = string.Join("; ", refused.Select(unit => $"{unit.Cell} ({unit.StoreRefusedBecause})"));
            return TextResult<Plan>.Refused(
                $"study {studyName} has {refused.Count} accepted cell(s) whose result never reached the store: {named}. "
                + "Collect them with --as-documents --into <directory>, then use study collect --from <directory> "
                + "with the selected --results-root to save the collected result without retraining");
        }

        var saved = units.Where(unit => IsSavedResult(unit.Result)).Select(unit => unit.Cell).ToList();
        if (saved.Count == 0)
        {
            return Collect(new Plan.Documents(units));
        }
        if (saved.Count != units.Count)
        {
            var carried = units.Where(unit => !IsSavedResult(unit.Result)).Select(unit => unit.Cell).ToList();
            return TextResult<Plan>.Refused(
                $"study {studyName} is half saved and half carried. Saved into a store: {string.Join(", ", saved)}. "
                + $"Carried as documents: {string.Join(", ", carried)}. Use --as-documents --into <directory>, "
                + "then study collect --from <directory> with the selected --results-root; no cell needs retraining");
        }

        var cells = new JsonObject();
        foreach (var unit in units)
        {
            using var reference = JsonDocument.Parse(unit.Result);
            var read = new JsonObject();
            // CHG 2026-09-26 (cards-without-hashes, row B16): sha256 is no longer
            // required; a cell names the revision it saved, one saved before names its
            // sha256, and whichever it recorded is carried into the file.
            foreach (var field in new[] { "database", "run", "reference" })
            {
                if (!reference.RootElement.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
                {
                    return TextResult<Plan>.Refused(
                        $"cell {unit.Cell} recorded a saved result that names no {field}; it cannot be collected by reference");
                }
                read[field] = value.GetString();
            }
            if (reference.RootElement.TryGetProperty("revision", out var revision) && revision.ValueKind == JsonValueKind.Number)
            {
                read["revision"] = revision.GetInt64();
            }
            if (reference.RootElement.TryGetProperty("sha256", out var digest) && digest.ValueKind == JsonValueKind.String)
            {
                read["sha256"] = digest.GetString();
            }
            cells[unit.Cell] = read;
        }

        var document = new JsonObject
        {
            ["schema"] = AcceptedCellResultsSchema,
            ["study"] = studyName,
            ["cells"] = cells,
        };
        return Collect(new Plan.References(
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", units.Count));
    }

    /// <summary>
    /// CHG 2026-09-26 (slice 3 of
    /// docs/programs/2026-09-10-the-cli-is-a-language-not-a-catalog/2026-09-26-the-command-line-as-a-language-in-gleam/design.md):
    /// one cell of a study as a collection reads it: accepted, with the
    /// ordinal of its accepted attempt, the result text that attempt recorded
    /// and, when the science store would not take it, why; or not accepted,
    /// with the state monitor-study prints for it.
    /// </summary>
    public sealed record CellResult(long CellId, string Cell, string State, long? Attempt, string? Result, string? StoreRefusedBecause);

    /// <summary>The state word of a cell whose attempt is accepted.</summary>
    public const string StateAccepted = "accepted";

    /// <summary>
    /// Every cell of a study in the study's order, as collect-study-results and
    /// the results payloads read it. Read-only.
    /// CHG 2026-09-26 (slice 3 of the command-line design): moved here from
    /// the collect-study-results verb, with the accepted attempt's ordinal
    /// added, so the verb and GET /v1/studies/{name}/results read one query;
    /// the verb's comments travelled with it.
    /// </summary>
    public static IReadOnlyList<CellResult> ReadCells(SqliteConnection connection, long studyId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.name, c.retention_json, a.result_json,
                   COALESCE((SELECT e.detail_json FROM events e
                             WHERE e.attempt_id = a.id AND e.kind = $notSaved
                             ORDER BY e.id DESC LIMIT 1), ''),
                   c.id,
                   COALESCE(latest.state, 'waiting'),
                   COALESCE((SELECT e.kind FROM events e WHERE e.attempt_id = latest.id
                             ORDER BY e.id DESC LIMIT 1), ''),
                   a.ordinal
            FROM cells c LEFT JOIN attempts a ON a.id = c.accepted_attempt_id
            LEFT JOIN attempts latest ON latest.id =
                (SELECT id FROM attempts WHERE cell_id = c.id ORDER BY ordinal DESC LIMIT 1)
            WHERE c.study_id = $study ORDER BY c.ordinal
            """;
        command.Parameters.AddWithValue("$notSaved", StudyCollect.ResultNotSaved);
        command.Parameters.AddWithValue("$study", studyId);
        var cells = new List<CellResult>();
        IReadOnlySet<long>? claimable = null;
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            if (rows.IsDBNull(2))
            {
                // CHG 2026-09-26: the state column monitor-study prints, by the same rule.
                var lastKind = rows.GetString(6);
                if (lastKind == RegistryOperations.RetryRequestedEvent)
                {
                    claimable ??= RegistryOperations.ListRunnableCells(connection, studyId)
                        .Select(runnable => runnable.CellId).ToHashSet();
                }
                cells.Add(new CellResult(rows.GetInt64(4), rows.GetString(0), StudyProjection.StateShown(
                    rows.GetString(5), lastKind, claimable?.Contains(rows.GetInt64(4)) ?? false), null, null, null));
                continue;
            }
            var refusedBecause = CellFailureDetail.Sentence(rows.GetString(3));
            cells.Add(new CellResult(
                rows.GetInt64(4), rows.GetString(0), StateAccepted, rows.GetInt64(7), rows.GetString(2),
                refusedBecause.Length == 0 ? null : refusedBecause));
        }
        return cells;
    }

    /// <summary>
    /// The sha256 of an accepted result's bytes as the Registry holds them, the
    /// digest list-accepted-cells prints (RegistryOperations.AcceptedCells).
    /// </summary>
    public static string ResultSha256(string result) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(result)));

    /// <summary>
    /// CHG 2026-09-26 (slice 3 of the command-line design): a study's cells as
    /// the Home answers <c>GET /v1/studies/{name}/results</c>: every cell in the study's
    /// order, an accepted one with its accepted attempt's ordinal and result
    /// digest, any other with its state and nulls.
    /// </summary>
    public static string ResultsPayload(SqliteConnection connection, long studyId, string studyName)
    {
        var cells = new JsonArray();
        foreach (var cell in ReadCells(connection, studyId))
        {
            cells.Add(new JsonObject
            {
                ["name"] = cell.Cell,
                ["state"] = cell.State,
                ["attempt"] = cell.Attempt,
                ["result_sha256"] = cell.Result is { } result ? ResultSha256(result) : null,
            });
        }
        return PayloadJson.Write(new JsonObject { ["study"] = studyName, ["cells"] = cells });
    }

    /// <summary>
    /// CHG 2026-09-26 (slice 3 of the command-line design): one accepted
    /// result as the Registry holds it, for <c>GET
    /// /v1/studies/{name}/results/{cell}</c>: the result's text is written into
    /// <c>result</c> byte for byte when it is JSON, and as a string when it is
    /// not, and <c>result_sha256</c> is the digest of those bytes. Refused by
    /// name for a cell the study does not have, and, naming its state, for a
    /// cell with no accepted attempt.
    /// </summary>
    public static TextResult<string> ResultPayload(SqliteConnection connection, long studyId, string studyName, string cellName)
    {
        var cell = ReadCells(connection, studyId).FirstOrDefault(one => one.Cell == cellName);
        if (cell is null)
        {
            return TextResult<string>.Refused($"study {studyName} has no cell named {cellName}");
        }
        if (cell.Result is not { } result)
        {
            return TextResult<string>.Refused($"cell {cellName} has no accepted attempt; its state is {cell.State}");
        }
        var isJson = true;
        try
        {
            using var _ = JsonDocument.Parse(result);
        }
        catch (JsonException)
        {
            isJson = false;
        }
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, PayloadJson.WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("study", studyName);
            json.WriteString("cell", cell.Cell);
            json.WriteNumber("attempt", cell.Attempt ?? 0);
            json.WriteString("result_sha256", ResultSha256(result));
            json.WritePropertyName("result");
            if (isJson)
            {
                json.WriteRawValue(result);
            }
            else
            {
                json.WriteStringValue(result);
            }
            json.WriteEndObject();
        }
        return TextResult<string>.Ok(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    /// <summary>
    /// Whether an accepted attempt recorded a REFERENCE rather than a document.
    /// Read by schema, because that is what the two shapes differ by; deciding
    /// it from "does it have a sha256 field" would be a guess.
    /// </summary>
    public static bool IsSavedResult(string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schema", out var schema)
                && schema.ValueKind == JsonValueKind.String
                && schema.GetString() == StudyCollect.SavedResultSchema;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
