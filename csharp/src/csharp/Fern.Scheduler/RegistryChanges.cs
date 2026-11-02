using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fern.Experiment;
using Fern.Registry;
using Microsoft.Data.Sqlite;

namespace Fern.Scheduler;

/// <summary>
/// CHG 2026-09-26 (slices 3 and 5 of
/// docs/programs/2026-09-10-the-cli-is-a-language-not-a-catalog/2026-09-26-the-command-line-as-a-language-in-gleam/design.md,
/// section "Slice 3 contract"): how every payload of the Home's read routes is
/// written. Compact, in the order its producer adds the fields (the order the
/// contract lists them), absent values as <c>null</c>, and sentences written
/// as they read rather than with every quote and angle bracket escaped, so
/// two calls on the same Registry give the same bytes and a person can read
/// them.
/// </summary>
public static class PayloadJson
{
    public static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Write(JsonNode node) => node.ToJsonString(Options);

    /// <summary>The body of a refused route: <c>{"refusal":"one sentence"}</c>.</summary>
    public static string Refusal(string sentence) => Write(new JsonObject { ["refusal"] = sentence });
}

/// <summary>
/// CHG 2026-09-26 (slice 5 of
/// docs/programs/2026-09-10-the-cli-is-a-language-not-a-catalog/2026-09-26-the-command-line-as-a-language-in-gleam/design.md):
/// the changes a person asks of the Registry that take more than one call,
/// decided once here for the verb that asks today and the Home's change route.
/// A change that is one Registry operation (pause-cells, hold-cell,
/// cancel-study, release-request, set-priority, move-last) is that operation,
/// called by both; retry-cell's decision is <see cref="StudyCellRetry.Request"/>.
/// Each answer is the sentence the verb prints, and a refusal is the sentence
/// the verb prints on standard error.
/// </summary>
public static class RegistryChanges
{
    /// <summary>A cell of a study by its name, as hold-cell, set-priority and move-last find it, or null.</summary>
    public static long? CellId(SqliteConnection connection, long studyId, string cellName)
    {
        using var find = connection.CreateCommand();
        find.CommandText = "SELECT id FROM cells WHERE study_id = $study AND name = $name";
        find.Parameters.AddWithValue("$study", studyId);
        find.Parameters.AddWithValue("$name", cellName);
        var found = find.ExecuteScalar();
        return found is null or DBNull ? null : Convert.ToInt64(found, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// disable-site: a hold over the Site's own scope, so no cell is claimed on
    /// it; cells already running finish. A Site already disabled keeps the hold
    /// that stands, and nothing is recorded.
    /// </summary>
    public static TextResult<string> DisableSite(RegistryWriter writer, long siteId, string siteName, string reason, string by)
    {
        var standing = SiteProjection.DisablingHolds(writer.Connection, siteId);
        if (standing.Count != 0)
        {
            return TextResult<string>.Ok(
                $"Site {siteName} is already disabled by {standing[0].RequestedBy} ({standing[0].Reason}); request {standing[0].RequestId} stands");
        }
        var asked = RegistryOperations.RequestOperator(writer, RegistryOperations.RequestHold, RegistryOperations.SiteScope(siteId), reason, by);
        if (!asked.TryGetValue(out var id))
        {
            asked.TryGetRefusal(out var refusal);
            return TextResult<string>.Refused($"disable-site refused: {refusal.Detail}");
        }
        return TextResult<string>.Ok($"Site {siteName} disabled (request {id}): {reason}; enable-site puts it back");
    }

    /// <summary>enable-site: release every hold standing over the Site's own scope; a Site not disabled is left as it is.</summary>
    public static TextResult<string> EnableSite(RegistryWriter writer, long siteId, string siteName, string reason, string by)
    {
        var standing = SiteProjection.DisablingHolds(writer.Connection, siteId);
        if (standing.Count == 0)
        {
            return TextResult<string>.Ok($"Site {siteName} is not disabled; nothing to release");
        }
        foreach (var hold in standing)
        {
            var released = RegistryOperations.ReleaseRequest(writer, hold.RequestId, by, reason);
            if (!released.IsOk)
            {
                released.TryGetRefusal(out var refusal);
                return TextResult<string>.Refused($"enable-site refused: {refusal.Detail}");
            }
        }
        return TextResult<string>.Ok($"Site {siteName} enabled: {standing.Count} hold(s) released ({reason})");
    }

    /// <summary>
    /// <c>study stop</c>: a standing pause over the study (pause-cells
    /// --study), a standing hold over one of its cells (hold-cell --cell), or,
    /// when <paramref name="final"/>, the study asked to cancel (cancel-study).
    /// Cells already running finish in every case. cancel-study records no
    /// reason and nobody; the reason and who asked reach only the Home's log.
    /// </summary>
    public static TextResult<string> StopStudy(
        RegistryWriter writer, long studyId, string studyName, long? cellId, bool final, string reason, string by)
    {
        if (final)
        {
            var asked = RegistryOperations.RequestStudyState(writer, studyId, RegistryOperations.StateCancel);
            if (!asked.TryGetValue(out var state))
            {
                asked.TryGetRefusal(out var refusal);
                return TextResult<string>.Refused($"cancel-study refused: {refusal.Detail}");
            }
            return TextResult<string>.Ok($"study {studyName} is {state}; no new cell will be claimed while it is not run");
        }
        var (kind, verb, scope) = cellId is { } cell
            ? (RegistryOperations.RequestHold, "hold-cell", RegistryOperations.CellScope(cell))
            : (RegistryOperations.RequestPause, "pause-cells", RegistryOperations.StudyScope(studyId));
        var requested = RegistryOperations.RequestOperator(writer, kind, scope, reason, by);
        if (!requested.TryGetValue(out var id))
        {
            requested.TryGetRefusal(out var refusal);
            return TextResult<string>.Refused($"{verb} refused: {refusal.Detail}");
        }
        return TextResult<string>.Ok($"{kind} {id} recorded over {scope}: {reason}; release it with release-request --request {id}");
    }

    /// <summary>
    /// <c>study resume</c>. Over the study: release every pause and hold
    /// standing over its scope (release-request). Over one cell: release the
    /// holds standing over the cell, and ask for the cell again (retry-cell)
    /// when its last attempt failed, was lost or was cancelled, or when no hold
    /// stood over it. A cell whose hold is lifted while its attempt is open,
    /// or before any attempt, is not asked for again: the hold was all that
    /// kept it. retry-cell declares an open attempt lost only when it has been
    /// silent past the bound, which makes that resume a repair; otherwise it
    /// refuses with the attempt's facts, and nothing is written.
    /// </summary>
    public static TextResult<string> ResumeStudy(
        RegistryWriter writer, long studyId, string studyName, string? cellName, long? cellId, string reason, string by,
        IStudyCellRetryDecision? retry = null)
    {
        var connection = writer.Connection;
        var said = new List<string>();
        IReadOnlyList<RegistryOperations.OperatorRequest> standing;
        if (cellId is not { } cell || cellName is null)
        {
            var scope = RegistryOperations.StudyScope(studyId);
            standing = [.. RegistryOperations.PendingRequests(connection, RegistryOperations.RequestPause, scope)
                .Concat(RegistryOperations.PendingRequests(connection, RegistryOperations.RequestHold, scope))
                .OrderBy(request => request.RequestId)];
            if (standing.Count == 0)
            {
                return TextResult<string>.Ok($"study {studyName} has no standing pause or hold; nothing to release");
            }
        }
        else
        {
            standing = RegistryOperations.PendingRequests(connection, RegistryOperations.RequestHold, RegistryOperations.CellScope(cell));
            if (standing.Count == 0 || LatestAttemptState(connection, cell)
                    is RegistryOperations.AttemptFailed or RegistryOperations.AttemptLost or RegistryOperations.AttemptCancelled)
            {
                var requested = StudyCellRetry.Request(writer, studyId, cellName, by, reason, implementedBy: retry);
                if (!requested.TryGetValue(out var retried))
                {
                    requested.TryGetRefusal(out var refusal);
                    return TextResult<string>.Refused($"retry-cell: {refusal.Detail}");
                }
                said.Add(StudyCellRetry.Sentence(retried, studyName));
            }
        }
        foreach (var request in standing)
        {
            var released = RegistryOperations.ReleaseRequest(writer, request.RequestId, by, reason);
            if (!released.IsOk)
            {
                released.TryGetRefusal(out var refusal);
                return TextResult<string>.Refused($"release-request refused: {refusal.Detail}");
            }
            said.Add($"request {request.RequestId} released: {reason}");
        }
        return TextResult<string>.Ok(string.Join("\n", said));
    }

    /// <summary>
    /// <c>study plan</c>: set the priority of the study or one of its cells
    /// (set-priority), then, when asked, move it to the back of its rank
    /// (move-last), the order move-last's help gives for putting work behind
    /// even the slow cells.
    /// </summary>
    public static TextResult<string> PlanStudy(
        RegistryWriter writer, long studyId, long? cellId, string? priority, bool last, string reason, string by)
    {
        var scope = cellId is { } cell ? RegistryOperations.CellScope(cell) : RegistryOperations.StudyScope(studyId);
        var said = new List<string>();
        if (priority is not null)
        {
            var set = RegistryOperations.SetPriority(writer, scope, priority, reason, by);
            if (!set.TryGetValue(out var recorded))
            {
                set.TryGetRefusal(out var refusal);
                return TextResult<string>.Refused($"set-priority refused: {refusal.Detail}");
            }
            said.Add($"priority {recorded.Priority} over {recorded.Scope} set by {recorded.SetBy}: {recorded.Reason}");
        }
        if (last)
        {
            var moved = RegistryOperations.MoveLast(writer, scope, reason, by);
            if (!moved.TryGetValue(out var recorded))
            {
                moved.TryGetRefusal(out var refusal);
                return TextResult<string>.Refused($"move-last refused: {refusal.Detail}");
            }
            said.Add($"{recorded.Scope} moved behind study {recorded.BehindStudyId} by {recorded.SetBy}: {recorded.Reason}");
        }
        return TextResult<string>.Ok(string.Join("\n", said));
    }

    /// <summary>The state of a cell's latest attempt, or null when it has none.</summary>
    static string? LatestAttemptState(SqliteConnection connection, long cellId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM attempts WHERE cell_id = $cell ORDER BY ordinal DESC LIMIT 1";
        command.Parameters.AddWithValue("$cell", cellId);
        return command.ExecuteScalar() as string;
    }
}
