using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fern.Registry;
using Microsoft.Data.Sqlite;

namespace Fern.Scheduler;

/// <summary>
/// The study -> cell -> attempt projection, and the follow loop over it.
///
/// It lives here rather than in the verb that prints it, for the reason the
/// orchestrator's loop does: a human command wraps the route, and a projection
/// reachable only through option parsing cannot be tested without the CLI.
///
/// It reads the new tables only -- studies, cells, attempts, sites -- through
/// RegistryOperations and one join. No stage table is named anywhere in it.
/// </summary>
public static class StudyProjection
{
    /// <summary>How often --follow re-reads. The same quarter second `fern-run monitor --follow` uses.</summary>
    public const int FollowIntervalMs = 250;

    /// <summary>
    /// Render once, or keep rendering until the study is finished or stopped.
    ///
    /// It prints only when the projection CHANGED. A lease renewal, a heartbeat
    /// or a progress timestamp is not a change a person is watching for, and a
    /// follow that reprinted on every tick would bury the one line that matters
    /// under a thousand identical ones. `fern-run monitor --follow` learned that
    /// rule the same way; this is the same rule over the new tables.
    ///
    /// It stops on its own at a terminal state: every cell accepted, or the
    /// study paused or cancelled. A follow that never returned would be a
    /// command an operator has to remember to kill.
    ///
    /// Separated from the option parsing so a test can drive it with a small
    /// interval and no process at all.
    /// </summary>
    public static int Follow(
        SqliteConnection connection,
        long studyId,
        bool follow,
        int intervalMs,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellation,
        bool events = false,
        ISchedulerDecision? placement = null)
    {
        string? printed = null;
        while (true)
        {
            var view = RegistryOperations.ReadStudy(connection, studyId);
            if (!view.TryGetValue(out var head))
            {
                view.TryGetRefusal(out var refusal);
                stderr.WriteLine($"monitor-study: {refusal.Detail}");
                return 3;
            }
            var rendered = Render(connection, head, events: events, placement: placement);
            if (rendered != printed)
            {
                stdout.Write(rendered);
                printed = rendered;
            }
            if (!follow || cancellation.IsCancellationRequested || Terminal(head))
            {
                return 0;
            }
            try
            {
                Task.Delay(intervalMs, cancellation).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// When a follow has nothing left to wait for: every cell has an accepted
    /// attempt, or the study is not in state run so no new cell will be claimed.
    /// </summary>
    static bool Terminal(RegistryOperations.StudyView head) =>
        head.AcceptedCellCount == head.CellCount
        || head.RequestedState != RegistryOperations.StateRun;

    /// <summary>
    /// The study, then one line per cell with its current attempt.
    ///
    /// One projection, three layers, in the order a person asks about them: is
    /// the study running, which cells are done, and what is the live attempt on
    /// the one that is not.
    /// CHG 2026-09-11 night (slice 7, row 36): a cell that is not running says
    /// why (held, paused, waiting for a person, in its retry window, refused on
    /// its Site, no Site holds its source, no Site admits it, every admitting
    /// Site full, or placeable and waiting for a supervisor that is absent or
    /// unconfirmed); a running attempt names the process holding it and the
    /// Site's supervisor; <paramref name="events"/> lists the attempt's
    /// recorded events under the line. The retired PlanMonitor and PlanWatch
    /// read the same questions off the old cell tables.
    /// </summary>
    /// <summary>
    /// Which sections of the configuration this study's cells disagree about.
    ///
    /// The Go compiler mints one digest per top-level section beside the
    /// configuration identity, and a cell carries them. Two cells share a
    /// section exactly when they share its digest, so the answer is a grouping
    /// of strings and needs no vocabulary: this host still does not know what
    /// `model` means, only that these two cells wrote different digests for it.
    ///
    /// A sweep is written to vary one thing. This is the line that says whether
    /// it did, before a person reads a single number out of it.
    ///
    /// Silent when fewer than two cells carry regions, which is every study
    /// registered before 2026-09-14 and every study of one cell.
    ///
    /// CHG 2026-09-26 (docs/programs/2026-09-26-cards-without-hashes; owner: "the
    /// card diff section supersedes this, so this should be retired"): the digests
    /// are gone (cell_config_regions left at schema 18). The line is read from
    /// each cell's stored configuration, cells.config_json, compared field by
    /// field, so it holds for every study of two cells or more and also names the
    /// fields that differ. A field is a path to a value that is not an object; a
    /// list is compared whole. Still no vocabulary: `model` is only a key.
    /// The method was RegionDifferences.
    /// </summary>
    internal static string ConfigurationDifferences(SqliteConnection connection, long studyId)
    {
        var configs = new List<Dictionary<string, string>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT config_json FROM cells WHERE study_id = $study ORDER BY ordinal";
            command.Parameters.AddWithValue("$study", studyId);
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    using var document = JsonDocument.Parse(rows.GetString(0));
                    if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                    FieldsOf(document.RootElement, "", fields);
                }
                catch (JsonException)
                {
                    continue;
                }
                configs.Add(fields);
            }
        }
        if (configs.Count < 2) return "";

        static string SectionOf(string field) => field.IndexOf('.') is var dot and >= 0 ? field[..dot] : field;
        string ValueIn(Dictionary<string, string> config, string field) =>
            config.TryGetValue(field, out var value) ? value : "(absent)";
        var every = configs.SelectMany(config => config.Keys).Distinct(StringComparer.Ordinal)
            .OrderBy(field => field, StringComparer.Ordinal).ToList();
        var differing = every.Where(field =>
            configs.Select(config => ValueIn(config, field)).Distinct(StringComparer.Ordinal).Count() > 1).ToList();
        // The compiler's identity scheme names how identities are made, not what a cell is.
        var sections = every.Select(SectionOf).Where(section => section != "identity_scheme")
            .Distinct(StringComparer.Ordinal).OrderBy(section => section, StringComparer.Ordinal).ToList();
        var moved = sections.Where(section => differing.Any(field => SectionOf(field) == section)).ToList();
        var shared = sections.Where(section => !moved.Contains(section)).ToList();
        if (moved.Count == 0)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"  every cell declares the same configuration in all {shared.Count} sections; they differ only in data or seed\n");
        }
        int VersionsOf(string section) => configs
            .Select(config => string.Join("\n", every.Where(field => SectionOf(field) == section)
                .Select(field => field + "=" + ValueIn(config, field))))
            .Distinct(StringComparer.Ordinal).Count();
        var counts = string.Join(", ", moved.Select(section =>
            string.Create(CultureInfo.InvariantCulture, $"{section} ({VersionsOf(section)} versions)")));
        const int Named = 12;
        var named = string.Join(", ", differing.Take(Named))
            + (differing.Count > Named ? string.Create(CultureInfo.InvariantCulture, $", and {differing.Count - Named} more") : "");
        return string.Create(CultureInfo.InvariantCulture, $"  cells differ in: {counts}\n")
            + string.Create(CultureInfo.InvariantCulture, $"  fields that differ: {named}\n")
            + string.Create(CultureInfo.InvariantCulture,
                $"  and agree in: {(shared.Count == 0 ? "no section" : string.Join(", ", shared))}\n");
    }

    /// <summary>Every path to a value that is not an object, with that value written compactly.</summary>
    static void FieldsOf(JsonElement element, string path, Dictionary<string, string> fields)
    {
        if (element.ValueKind == JsonValueKind.Object && element.EnumerateObject().Any())
        {
            foreach (var property in element.EnumerateObject())
            {
                FieldsOf(property.Value, path.Length == 0 ? property.Name : path + "." + property.Name, fields);
            }
            return;
        }
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) element.WriteTo(writer);
        fields[path] = Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string Render(
        SqliteConnection connection,
        RegistryOperations.StudyView head,
        long maxAttempts = RegistryOperations.DefaultMaxAttempts,
        bool events = false,
        ISchedulerDecision? placement = null)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"study {head.Name} revision {head.Revision}  state={head.RequestedState}  {head.AcceptedCellCount}/{head.CellCount} cells accepted\n");
        var studyScope = RegistryOperations.StudyScope(head.StudyId);
        foreach (var pause in RegistryOperations.PendingRequests(connection, RegistryOperations.RequestPause, RegistryOperations.ScopeAll, studyScope))
        {
            text.Append(CultureInfo.InvariantCulture,
                $"  paused by {pause.RequestedBy} over {pause.Scope}: {pause.Reason} (release-request --request {pause.RequestId})\n");
        }
        foreach (var hold in RegistryOperations.PendingRequests(connection, RegistryOperations.RequestHold, studyScope))
        {
            text.Append(CultureInfo.InvariantCulture,
                $"  held by {hold.RequestedBy}: {hold.Reason} (release-request --request {hold.RequestId})\n");
        }
        // CHG 2026-09-14, round 3 of the stage-vocabulary design: where this
        // study's cells differ, before the table of what they are doing. A
        // sweep is written to vary one thing, and this is the line that says
        // whether it did -- read from the digests the compiler minted, so it
        // costs no re-derivation and cannot disagree with the identity.
        // CHG 2026-09-26: read from the stored configurations, field by field.
        text.Append(ConfigurationDifferences(connection, head.StudyId));
        text.Append("\n  cell                            state        site         attempt  epoch  tries  last progress\n");
        // CHG 2026-09-26 (slice 3 of the command-line design): each cell is read
        // once by ReadCells, which the payload of GET /v1/studies/{name} reads
        // too; this loop writes those readings as the lines it always wrote, in
        // the same order.
        foreach (var cell in ReadCells(connection, head, maxAttempts, placement))
        {
            var site = cell.Site ?? "-";
            var lastProgress = cell.LastProgressAt ?? "-";
            var line = string.Create(CultureInfo.InvariantCulture,
                $"  {cell.Name,-30}  {cell.State,-11}  {site,-11}  {cell.Attempt,7}  {cell.Epoch,5}  {cell.Tries,5}  {lastProgress}");
            text.Append(line).Append(cell.Remote.Length == 0 ? "" : "  remote=" + cell.Remote).Append('\n');
            if (cell.RetryWhy is { } retry)
            {
                text.Append("      why: ").Append(retry).Append('\n');
            }
            if (cell.Office is { } office)
            {
                text.Append("      ").Append(cell.Remote.Length == 0 ? "no process has beaten for it yet; " : $"held by {cell.Remote}; ").Append(office).Append('\n');
                if (cell.Silent)
                {
                    text.Append(CultureInfo.InvariantCulture,
                        // CHG 2026-09-26 (slice W2): silence no longer releases an attempt.
                        $"      silent since {lastProgress}: it keeps its cell and its slot until a person gives it up with retry-cell (the Home tells the owner after 30 min)\n");
                }
            }
            if (cell.Spent is { } spent)
            {
                text.Append("      ").Append(spent).Append('\n');
            }
            else if (cell.Waiting is { } waiting)
            {
                text.Append("      waiting: ").Append(waiting).Append('\n');
            }
            if (cell.FailureWhy is { } why)
            {
                text.Append("      why: ").Append(why).Append('\n');
            }
            else if (cell.Note is { } note)
            {
                text.Append("      note: ").Append(note).Append('\n');
            }
            if (events && cell.AttemptId != 0)
            {
                AppendEvents(connection, cell.AttemptId, text);
            }
        }
        return text.ToString();
    }

    /// <summary>
    /// CHG 2026-09-26 (slice 3 of
    /// docs/programs/2026-09-10-the-cli-is-a-language-not-a-catalog/2026-09-26-the-command-line-as-a-language-in-gleam/design.md):
    /// one cell as monitor-study shows it, read once for the text and for the
    /// payload the Home serves at <c>GET /v1/studies/{name}</c>, so the two
    /// cannot disagree about a state, a waiting sentence or a why.
    /// </summary>
    /// <param name="State">The state column: accepted, the attempt's state, or waiting (<see cref="StateShown"/>).</param>
    /// <param name="Site">The Site of the attempt shown, or null before any attempt.</param>
    /// <param name="Attempt">The ordinal of the attempt shown, 0 before any attempt.</param>
    /// <param name="Epoch">Its fencing epoch, 0 before any attempt.</param>
    /// <param name="Tries">The attempts the budget counts.</param>
    /// <param name="LastProgressAt">The attempt's last progress, or when it started when it reported none; null before any attempt.</param>
    /// <param name="Remote">The process the attempt's last beat named, or empty.</param>
    /// <param name="AttemptId">The attempt shown, 0 before any attempt; --events reads its records.</param>
    /// <param name="Office">For an open attempt, its Site's supervisor in the words of the line under it; null otherwise.</param>
    /// <param name="Silent">An open attempt whose last progress is older than StaleAfter.</param>
    /// <param name="RetryWhy">The retry line of a retried cell that waits.</param>
    /// <param name="Spent">"n attempts spent: ..." for a failed or lost cell whose tries are spent.</param>
    /// <param name="Waiting">Why a cell with no open attempt is not running.</param>
    /// <param name="FailureWhy">The failure's sentence, for an attempt that ended without a result.</param>
    /// <param name="Note">An accepted result whose science store would not take it.</param>
    public sealed record CellView(
        string Name, string State, string? Site, long Attempt, long Epoch, long Tries, string? LastProgressAt,
        string Remote, long AttemptId, string? Office, bool Silent, string? RetryWhy, string? Spent,
        string? Waiting, string? FailureWhy, string? Note);

    /// <summary>
    /// Every cell of the study in the study's order, with its current attempt
    /// and the sentences written under it.
    /// CHG 2026-09-26 (slice 3 of the command-line design): the reading half of
    /// what <see cref="Render"/> did in one loop, moved here verbatim so the
    /// payload reads the same query; the comments below travelled with it.
    /// </summary>
    /// <param name="placement">
    /// CHG 2026-09-27: the placement rule a waiting reason follows. The Home
    /// and the monitor verbs pass the Gleam scheduler program's when they were
    /// started with one, so the reason a person reads comes from the engine
    /// that is deciding; null is the C# rule.
    /// </param>
    public static IReadOnlyList<CellView> ReadCells(
        SqliteConnection connection,
        RegistryOperations.StudyView head,
        long maxAttempts = RegistryOperations.DefaultMaxAttempts,
        ISchedulerDecision? placement = null)
    {
        var cells = new List<CellView>();
        var supervisors = new Dictionary<long, RegistryOperations.SupervisorView?>();
        OfferView? offer = null;
        using var command = connection.CreateCommand();
        // The fields PlanCellWatch re-read that still exist: the attempt's state,
        // its Site, its ordinal and epoch, how many tries the cell has had, and
        // when it last reported. What it also carried -- phase receipts,
        // transport attempts, remote-execution reconciliations, worker startup
        // observations -- read the old cell tables, which retire with the Stage
        // layer; on the new store those are rows in `events` and are read by
        // asking for a cell, not by widening this line.
        command.CommandText =
            """
            SELECT c.name,
                   CASE WHEN c.accepted_attempt_id IS NOT NULL THEN 'accepted'
                        ELSE COALESCE(a.state, 'waiting') END,
                   COALESCE(s.name, '-'), COALESCE(a.ordinal, 0), COALESCE(a.fencing_epoch, 0),
                   COUNTED_ATTEMPTS,
                   COALESCE(a.last_progress_at, a.started_at, '-'),
                   COALESCE(a.remote_execution_id, ''),
                   -- Why, for an attempt that ended without a result. The
                   -- failure's own words are in the last event written against
                   -- it; a row that says only "failed" sends a person to a
                   -- worker folder to find out what every reader already has.
                   COALESCE((SELECT e.detail_json FROM events e
                             WHERE e.attempt_id = a.id ORDER BY e.id DESC LIMIT 1), ''),
                   COALESCE((SELECT e.kind FROM events e
                             WHERE e.attempt_id = a.id ORDER BY e.id DESC LIMIT 1), ''),
                   c.id, COALESCE(a.id, 0), COALESCE(a.site_id, 0), c.config_json, c.requirements_json, c.ordinal
            FROM cells c
            LEFT JOIN attempts a ON a.id = COALESCE(
                c.accepted_attempt_id,
                (SELECT id FROM attempts WHERE cell_id = c.id ORDER BY ordinal DESC LIMIT 1))
            LEFT JOIN sites s ON s.id = a.site_id
            WHERE c.study_id = $study
            ORDER BY c.ordinal
            """;
        // CHG 2026-09-25 (slice M1): the tries shown are the ones the budget counts.
        command.CommandText = command.CommandText.Replace(
            "COUNTED_ATTEMPTS", RegistryOperations.CountedAttempts("c.id"), StringComparison.Ordinal);
        command.Parameters.AddWithValue("$study", head.StudyId);
        using var rows = command.ExecuteReader();
        // CHG 2026-09-26 (slice 2 of the command-line design): the cells a claim
        // may take now, read once and only when a retried cell asks.
        IReadOnlySet<long>? claimable = null;
        while (rows.Read())
        {
            var state = rows.GetString(1);
            var remote = rows.GetString(7);
            var lastEvent = rows.GetString(8);
            var tries = rows.GetInt64(5);
            var cellId = rows.GetInt64(10);
            var attemptId = rows.GetInt64(11);
            var siteId = rows.GetInt64(12);
            var lastProgress = rows.GetString(6);
            // CHG 2026-09-26 (slice 2 of the command-line design): a cell a person
            // made claimable again with retry-cell reads `waiting`, not the state
            // its last attempt ended in; StateShown says when.
            var retried = rows.GetString(9) == RegistryOperations.RetryRequestedEvent;
            if (retried)
            {
                claimable ??= RegistryOperations.ListRunnableCells(connection, head.StudyId, maxAttempts)
                    .Select(runnable => runnable.CellId).ToHashSet();
                state = StateShown(state, rows.GetString(9), claimable.Contains(cellId));
            }
            string? retryWhy = null, office = null, spent = null, waiting = null, failureWhy = null, note = null;
            var silent = false;
            if (retried && state == Waiting)
            {
                retryWhy = RetrySentence(lastEvent, tries, maxAttempts, rows.GetInt64(3));
            }
            if (state is RegistryOperations.AttemptOpened or RegistryOperations.AttemptRunning)
            {
                // Who holds it: the process the beat named (host/pid, slice 5)
                // and the Site's supervisor, so a person knows what to look at
                // and what to kill; and whether it has gone quiet.
                if (!supervisors.TryGetValue(siteId, out var supervisor))
                {
                    supervisor = RegistryOperations.ReadSupervisor(connection, siteId);
                    supervisors[siteId] = supervisor;
                }
                var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                office = supervisor is null
                    ? "no supervisor has held the office of this Site"
                    : supervisor.HoldsOfficeAt(now)
                        ? $"supervisor {supervisor.Host}/{supervisor.Pid} epoch {supervisor.Epoch} holds the office"
                        : supervisor.IsUnconfirmedAt(now)
                            ? $"supervisor {supervisor.Host}/{supervisor.Pid} epoch {supervisor.Epoch} is unconfirmed since {supervisor.LeaseUntil}"
                            : $"supervisor {supervisor.Host}/{supervisor.Pid} epoch {supervisor.Epoch} ended {supervisor.EndedAt} ({supervisor.Outcome})";
                silent = IsSilent(lastProgress);
            }
            // A cell that has spent its attempts is waiting for a person, not
            // for a machine, and the row's "failed" alone does not say that.
            if (state is RegistryOperations.AttemptFailed or RegistryOperations.AttemptLost
                && tries >= maxAttempts)
            {
                spent = string.Create(CultureInfo.InvariantCulture,
                    $"{tries} attempts spent: {CellFailureDetail.Sentence(lastEvent)}");
            }
            else if (state is "waiting" or RegistryOperations.AttemptFailed or RegistryOperations.AttemptLost or RegistryOperations.AttemptCancelled)
            {
                offer ??= ReadOffer(connection);
                waiting = WaitingReason(connection, head, cellId, rows.GetString(13), rows.GetString(14), rows.GetInt64(15), offer, supervisors, placement);
            }
            if ((state is RegistryOperations.AttemptFailed or RegistryOperations.AttemptLost or RegistryOperations.AttemptCancelled)
                && CellFailureDetail.Sentence(lastEvent) is { Length: > 0 } why)
            {
                failureWhy = why;
            }
            // A cell can finish, be accepted, and still have its result sitting
            // only in the attempt's folder because the science store would not
            // take it. That is not a failure and must not read as one, but a
            // person about to collect this study has to know before they do.
            else if (rows.GetString(9) == StudyCollect.ResultNotSaved
                && CellFailureDetail.Sentence(lastEvent) is { Length: > 0 } unsaved)
            {
                note = unsaved;
            }
            var siteName = rows.GetString(2);
            cells.Add(new CellView(
                rows.GetString(0), state, siteName == "-" ? null : siteName, rows.GetInt64(3), rows.GetInt64(4), tries,
                lastProgress == "-" ? null : lastProgress, remote, attemptId, office, silent, retryWhy, spent,
                waiting, failureWhy, note));
        }
        return cells;
    }

    /// <summary>
    /// CHG 2026-09-26 (slice 3 of
    /// docs/programs/2026-09-10-the-cli-is-a-language-not-a-catalog/2026-09-26-the-command-line-as-a-language-in-gleam/design.md):
    /// the study and its cells as the Home answers <c>GET /v1/studies/{name}</c>,
    /// from the cells
    /// <see cref="ReadCells"/> reads for the text, with the budget the text
    /// uses. The field order is the contract's, so two calls on the same
    /// Registry give the same bytes.
    ///
    /// Per cell: <c>held_by</c> is the process the beat of an open attempt
    /// named (the text's "held by"), <c>why</c> the retry line or the
    /// failure's sentence, <c>waiting</c> and <c>preparation</c> the two
    /// sentences under a waiting cell; a value the text does not print is
    /// <c>null</c>. The lines the contract has no field for (the office of an
    /// open attempt's Site, "silent since", "attempts spent", the store note)
    /// are the text's alone; the office is on <c>GET /v1/sites/{name}</c>.
    /// </summary>
    public static string StudyPayload(SqliteConnection connection, RegistryOperations.StudyView head, ISchedulerDecision? placement = null)
    {
        var compiler = RegistryOperations.ReadStudyCompiler(connection, head.StudyId);
        string registeredAt;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT created_at FROM studies WHERE id = $id";
            command.Parameters.AddWithValue("$id", head.StudyId);
            registeredAt = (string)command.ExecuteScalar()!;
        }
        var cells = new JsonArray();
        foreach (var cell in ReadCells(connection, head, placement: placement))
        {
            cells.Add(new JsonObject
            {
                ["name"] = cell.Name,
                ["state"] = cell.State,
                ["site"] = cell.Site,
                ["attempt"] = cell.Attempt,
                ["epoch"] = cell.Epoch,
                ["tries"] = cell.Tries,
                ["last_progress_at"] = cell.LastProgressAt,
                ["held_by"] = cell.Office is not null && cell.Remote.Length != 0 ? cell.Remote : null,
                ["why"] = cell.RetryWhy ?? cell.FailureWhy,
                ["waiting"] = cell.Waiting,
            });
        }
        return PayloadJson.Write(new JsonObject
        {
            ["study"] = new JsonObject
            {
                ["name"] = head.Name,
                ["revision"] = head.Revision,
                ["state"] = head.RequestedState,
                ["source_id"] = head.SourceId,
                ["registered_at"] = registeredAt,
                ["compiler"] = compiler is null ? null : new JsonObject { ["sha256"] = compiler.Sha256, ["path"] = compiler.Path },
                ["attempt_budget"] = StudyAttemptBudget.Of(connection, head.StudyId),
            },
            ["cells"] = cells,
        });
    }

    /// <summary>
    /// One registered study as list-studies reads it: its cell counts come from
    /// the same read, so a study and its cells cannot be reported from two
    /// different moments.
    /// CHG 2026-09-26 (slice 3 of the command-line design): moved here from
    /// list-studies so the verb and <see cref="StudiesPayload"/> read one query;
    /// <paramref name="SourceId"/> added for the payload.
    /// </summary>
    public sealed record RegisteredStudy(long Id, string Name, long Revision, string RequestedState, string CreatedAt,
                                         long Cells, long AcceptedCells, long SourceId);

    /// <summary>Every registered study, newest first, or only those in <paramref name="state"/>. Read-only.</summary>
    public static IReadOnlyList<RegisteredStudy> ListStudies(SqliteConnection connection, string? state)
    {
        var rows = new List<RegisteredStudy>();
        using var command = connection.CreateCommand();
        // The cell counts come from the same read, so a study and its cells cannot be reported from
        // two different moments. A cell is accepted when it names the attempt that was accepted,
        // which is the same fact list-accepted-cells reads; there is no attempt state of "accepted".
        command.CommandText = """
            SELECT s.id, s.name, s.revision, s.requested_state, s.created_at,
                   (SELECT COUNT(*) FROM cells c WHERE c.study_id = s.id) AS cells,
                   (SELECT COUNT(*) FROM cells c WHERE c.study_id = s.id
                      AND c.accepted_attempt_id IS NOT NULL) AS accepted,
                   s.source_id
            FROM studies s
            WHERE ($state IS NULL OR s.requested_state = $state)
            ORDER BY s.created_at DESC, s.name
            """;
        command.Parameters.AddWithValue("$state", (object?)state ?? DBNull.Value);
        using var found = command.ExecuteReader();
        while (found.Read())
        {
            rows.Add(new RegisteredStudy(
                found.GetInt64(0), found.GetString(1), found.GetInt64(2), found.GetString(3), found.GetString(4),
                found.GetInt64(5), found.GetInt64(6), found.GetInt64(7)));
        }
        return rows;
    }

    /// <summary>
    /// CHG 2026-09-26 (slice 3 of the command-line design): every study as the
    /// Home answers <c>GET /v1/studies</c>, newest first as list-studies prints
    /// them; <c>compiler_sha256</c> is the compiler the study records, or null.
    /// </summary>
    public static string StudiesPayload(SqliteConnection connection)
    {
        var studies = new JsonArray();
        foreach (var study in ListStudies(connection, state: null))
        {
            studies.Add(new JsonObject
            {
                ["name"] = study.Name,
                ["revision"] = study.Revision,
                ["state"] = study.RequestedState,
                ["cells_accepted"] = study.AcceptedCells,
                ["cells_total"] = study.Cells,
                ["registered_at"] = study.CreatedAt,
                ["source_id"] = study.SourceId,
                ["compiler_sha256"] = RegistryOperations.ReadStudyCompiler(connection, study.Id)?.Sha256,
            });
        }
        return PayloadJson.Write(new JsonObject { ["studies"] = studies });
    }

    /// <summary>The state column of a cell nobody has attempted, and of a retried cell a claim may take.</summary>
    public const string Waiting = "waiting";

    /// <summary>
    /// CHG 2026-09-26 (slice 2 of
    /// docs/programs/2026-09-10-the-cli-is-a-language-not-a-catalog/2026-09-26-the-command-line-as-a-language-in-gleam/design.md):
    /// the state column of one cell, from the state the projection's join
    /// reads (<c>accepted</c>, the latest attempt's state, or <c>waiting</c>
    /// before any attempt), the kind of that attempt's latest record, and
    /// whether the store would offer the cell to a claim now.
    ///
    /// An attempt that ended without a result shows its own state, except when
    /// a person has since asked for a retry with retry-cell (the attempt's
    /// latest record is <c>retry_requested</c>) and the cell is claimable: then
    /// it is <c>waiting</c>. On 2026-09-26 such a cell printed <c>failed</c>
    /// while it sat in the queue. A cell that failed with no retry, or whose
    /// retry no claim may take (a hold, a pause, a study not in state run),
    /// shows its attempt's state as before. collect-study-results names a cell
    /// it does not collect by the same rule.
    /// </summary>
    public static string StateShown(string state, string lastEventKind, bool claimable) =>
        state is RegistryOperations.AttemptFailed or RegistryOperations.AttemptLost or RegistryOperations.AttemptCancelled
        && lastEventKind == RegistryOperations.RetryRequestedEvent
        && claimable
            ? Waiting
            : state;

    /// <summary>
    /// CHG 2026-09-26: the line under a retried cell that waits: who asked for
    /// the retry and why (the record retry-cell writes), which attempt comes
    /// next, and how many of the cell's tries are spent. A retry after the
    /// tries are spent allows one more attempt; the claim reads that from the
    /// same record.
    /// </summary>
    static string RetrySentence(string detailJson, long tries, long maxAttempts, long lastOrdinal)
    {
        var asked = CellFailureDetail.Sentence(detailJson);
        try
        {
            using var detail = JsonDocument.Parse(detailJson);
            if (detail.RootElement.ValueKind == JsonValueKind.Object
                && detail.RootElement.TryGetProperty("requested_by", out var by) && by.ValueKind == JsonValueKind.String
                && detail.RootElement.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
            {
                asked = $"retry requested by {by.GetString()}: {reason.GetString()}";
            }
        }
        catch (JsonException)
        {
            // A record that is not JSON keeps the sentence CellFailureDetail read from it.
        }
        if (asked.Length == 0)
        {
            asked = "retry requested";
        }
        var spent = tries >= maxAttempts
            ? string.Create(CultureInfo.InvariantCulture, $"{tries} of {maxAttempts} tries spent; the retry allows one more")
            : string.Create(CultureInfo.InvariantCulture, $"{tries} of {maxAttempts} tries spent");
        return string.Create(CultureInfo.InvariantCulture, $"{asked}; its next attempt is attempt {lastOrdinal + 1} ({spent})");
    }

    /// <summary>The last progress is older than StaleAfter: the supervisor holding it has stopped beating.</summary>
    /// <summary>The offer this cycle, for a reader outside this projection that must not compute its own.</summary>
    internal static OfferView ReadOffer(SqliteConnection connection) => OfferView.Read(connection);

    internal static bool IsSilent(string lastProgress) =>
        DateTimeOffset.TryParse(lastProgress, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
        && DateTimeOffset.UtcNow - at > RegistryOperations.StaleAfter;

    /// <summary>What every registered Site offers this cycle, read once per projection: the decision's own inputs.</summary>
    /// <param name="Running">
    /// CHG 2026-09-26 (slice 2 of the command-line design): what each Site's
    /// open attempts are, as "cell of study", oldest attempt first, so a
    /// waiting sentence can say what a busy Site is doing. Busy is their count.
    /// </param>
    internal sealed record OfferView(
        IReadOnlyList<SchedulerDecision.SiteOffer> Sites,
        IReadOnlyDictionary<long, int> Busy,
        IReadOnlyDictionary<long, IReadOnlyList<string>> Running)
    {
        public static OfferView Read(SqliteConnection connection)
        {
            // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): no
            // Site holds sources any more, so the offer carries none.
            var sites = RegistryOperations.ListSites(connection)
                .Where(site => RegistryOperations.PendingRequests(
                        connection, RegistryOperations.RequestHold, RegistryOperations.SiteScope(site.SiteId)).Count == 0
                    && RegistryOperations.PendingRequests(
                        connection, RegistryOperations.RequestPause, RegistryOperations.SiteScope(site.SiteId)).Count == 0)
                .Select(site => SchedulerDecision.ReadOffer(site.SiteId, site.Name, site.CapabilitiesJson, site.CapacityJson))
                .ToList();
            var busy = new Dictionary<long, int>();
            // CHG 2026-09-26: one row per open attempt with its cell and study
            // names, in place of a count per Site; the count is taken from it.
            var running = new Dictionary<long, List<string>>();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT a.site_id, c.name, p.name
                FROM attempts a JOIN cells c ON c.id = a.cell_id JOIN studies p ON p.id = c.study_id
                WHERE a.state IN ('opened', 'running')
                ORDER BY a.site_id, a.id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var siteId = reader.GetInt64(0);
                if (!running.TryGetValue(siteId, out var cells))
                {
                    running[siteId] = cells = [];
                }
                cells.Add($"{reader.GetString(1)} of {reader.GetString(2)}");
                busy[siteId] = cells.Count;
            }
            return new OfferView(sites, busy,
                running.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value));
        }
    }

    /// <summary>
    /// Why a cell with no open attempt is not running, in the order the store
    /// decides it: the study's state, a hold or a pause, its last failure's
    /// class and window, then the decision over the registered Sites, then
    /// the supervisor of the Site it would go to.
    /// </summary>
    internal static string? WaitingReason(
        SqliteConnection connection,
        RegistryOperations.StudyView head,
        long cellId,
        string configJson,
        string requirementsJson,
        long ordinal,
        OfferView offer,
        Dictionary<long, RegistryOperations.SupervisorView?> supervisors,
        ISchedulerDecision? rule = null)
    {
        if (head.RequestedState != RegistryOperations.StateRun)
        {
            return $"study is {head.RequestedState}; no cell is claimed";
        }
        var holds = RegistryOperations.PendingRequests(
            connection, RegistryOperations.RequestHold, RegistryOperations.StudyScope(head.StudyId), RegistryOperations.CellScope(cellId));
        if (holds.Count != 0)
        {
            return $"held by {holds[0].RequestedBy}: {holds[0].Reason} (release-request --request {holds[0].RequestId})";
        }
        var pauses = RegistryOperations.PendingRequests(
            connection, RegistryOperations.RequestPause, RegistryOperations.ScopeAll, RegistryOperations.StudyScope(head.StudyId));
        if (pauses.Count != 0)
        {
            return $"paused by {pauses[0].RequestedBy}: {pauses[0].Reason} (release-request --request {pauses[0].RequestId})";
        }
        if (RegistryOperations.LastFailure(connection, cellId) is { } failure
            && failure.LastEventKind != RegistryOperations.RetryRequestedEvent)
        {
            switch (failure.Class)
            {
                case RegistryOperations.FailureDeterministic:
                case RegistryOperations.FailureHuman:
                    return $"waits for a person, its last failure was {failure.Class}: {failure.Sentence} (retry-cell)";
                case RegistryOperations.FailureTransient:
                    if (DateTimeOffset.TryParse(failure.EndedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ended)
                        && DateTimeOffset.UtcNow - ended < RegistryOperations.RetryWindow)
                    {
                        return $"transient failure at {failure.EndedAt}; offered again {RegistryOperations.RetryWindow.TotalMinutes:F0} min after it (retry-cell lifts the wait)";
                    }
                    break;
                case RegistryOperations.FailureSite:
                    break;
            }
        }
        var demand = SchedulerDecision.ReadDemand(cellId, head.StudyId, ordinal, "", configJson, requirementsJson, head.SourceId);
        // CHG 2026-09-27: the configured rule decides, so this reason and the
        // round's placement cannot disagree when a Gleam program is deciding.
        var decision = (rule ?? CSharpSchedulerDecision.Instance).Choose([demand], offer.Sites, offer.Busy);
        if (decision.Unplaced.Count != 0)
        {
            // CHG 2026-09-26 (slice 2 of the command-line design): a Site that holds
            // the source and admits the cell is named first; the decision's own
            // reason stands only when there is none.
            return HoldersFirst(connection, head, demand, offer, supervisors) ?? decision.Unplaced[0].Because;
        }
        var placement = decision.Placements[0];
        var site = offer.Sites.First(candidate => candidate.SiteId == placement.SiteId);
        // CHG 2026-09-25 (slices M1 and M3): the claim refuses a source on a Site
        // whose compiler refused it, or where several of its cells failed alike;
        // the line says so rather than "placeable".
        if (RegistryOperations.SourceRefusedOnSite(connection, head.SourceId, site.SiteId, site.Name) is { } refused)
        {
            return refused;
        }
        if (!supervisors.TryGetValue(site.SiteId, out var supervisor))
        {
            supervisor = RegistryOperations.ReadSupervisor(connection, site.SiteId);
            supervisors[site.SiteId] = supervisor;
        }
        var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        if (supervisor is null)
        {
            return $"placeable on Site {site.Name}, whose office no supervisor has ever held (install-supervisor-service, or run-study-cells --serve there)";
        }
        if (supervisor.IsUnconfirmedAt(now))
        {
            return $"placeable on Site {site.Name}, whose supervisor {supervisor.Host}/{supervisor.Pid} is unconfirmed since {supervisor.LeaseUntil}";
        }
        if (!supervisor.HoldsOfficeAt(now))
        {
            return $"placeable on Site {site.Name}, whose last supervisor ended {supervisor.EndedAt} ({supervisor.Outcome}, {supervisor.Disposition})";
        }
        return $"placeable on Site {site.Name}; its supervisor {supervisor.Host}/{supervisor.Pid} claims at its next cycle";
    }

    /// <summary>
    /// CHG 2026-09-26 (slice 2 of
    /// docs/programs/2026-09-10-the-cli-is-a-language-not-a-catalog/2026-09-26-the-command-line-as-a-language-in-gleam/design.md):
    /// why an unplaced cell waits, when some Site already holds the study's
    /// source and admits the cell.
    ///
    /// The decision visits the Sites in id order and keeps the reason of the
    /// last one that turned the cell away, so a cell whose Site was only busy
    /// was reported by the Site after it. On 2026-09-26 the sentence named
    /// autodl-bj-5090-2, which did not hold the source and had been unreachable
    /// for hours, while local-4070 held the source, admitted the cell and was
    /// running other cells.
    ///
    /// The claim is pulled: each Site's supervisor claims for its own Site, and
    /// only a Site that holds the source can claim without an install (the
    /// claim refuses the others by name, and the automatic install is not
    /// asked for while one Site holds it, SourcePreparation.Plan). So those
    /// Sites come first, in the decision's order, leaving out one whose
    /// failures refuse this source there; each says what it is running, and,
    /// when its supervisor does not hold the office, says so in the words the
    /// placeable sentence uses. Then come the Sites that could take the cell
    /// after an install, each with its install line.
    ///
    /// Null when no Site both holds the source and admits the cell: the
    /// decision's own reason stands, as before.
    ///
    /// CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): no Site
    /// holds a source now; every Site that admits the cell can take it, so
    /// those are named (less the ones whose failures refuse this source there),
    /// and nothing is said about installing.
    /// </summary>
    static string? HoldersFirst(
        SqliteConnection connection,
        RegistryOperations.StudyView head,
        SchedulerDecision.CellDemand demand,
        OfferView offer,
        Dictionary<long, RegistryOperations.SupervisorView?> supervisors)
    {
        var admitting = offer.Sites.Where(site => SchedulerDecision.Refusal(site, demand) is null).ToList();
        var holders = admitting
            .Where(site => RegistryOperations.SourceRefusedOnSite(connection, head.SourceId, site.SiteId, site.Name) is null)
            .ToList();
        if (holders.Count == 0)
        {
            return null;
        }
        var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var named = holders.Select(site =>
        {
            var doing = offer.Running.TryGetValue(site.SiteId, out var cells) && cells.Count != 0
                ? $"Site {site.Name}, running {string.Join(", ", cells)}"
                : $"Site {site.Name}, which admits this cell but is full";
            if (!supervisors.TryGetValue(site.SiteId, out var supervisor))
            {
                supervisor = RegistryOperations.ReadSupervisor(connection, site.SiteId);
                supervisors[site.SiteId] = supervisor;
            }
            return supervisor is null
                ? $"{doing}, whose office no supervisor has ever held (install-supervisor-service, or run-study-cells --serve there)"
                : supervisor.IsUnconfirmedAt(now)
                    ? $"{doing}, whose supervisor {supervisor.Host}/{supervisor.Pid} is unconfirmed since {supervisor.LeaseUntil}"
                    : !supervisor.HoldsOfficeAt(now)
                        ? $"{doing}, whose last supervisor ended {supervisor.EndedAt} ({supervisor.Outcome}, {supervisor.Disposition})"
                        : doing;
        });
        return "waits for " + string.Join("; or for ", named);
    }

    /// <summary>The attempt's recorded events, oldest first: when, what kind, which phase, and the sentence it carried.</summary>
    static void AppendEvents(SqliteConnection connection, long attemptId, StringBuilder text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT at, kind, phase, detail_json FROM events WHERE attempt_id = $attempt ORDER BY id";
        command.Parameters.AddWithValue("$attempt", attemptId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var phase = reader.IsDBNull(2) ? "" : " " + reader.GetString(2);
            var sentence = CellFailureDetail.Sentence(reader.GetString(3));
            text.Append(CultureInfo.InvariantCulture, $"      {reader.GetString(0)}  {reader.GetString(1)}{phase}  {sentence}\n");
        }
    }
}

/// <summary>A projection that cannot be made from the rows present; the sentence says which fact is missing. Retired PlanMonitor's refusal, kept by StepProgress and PreparationOperation (slice 8).</summary>
public sealed class MonitorRefusal(string detail) : Exception(detail);
