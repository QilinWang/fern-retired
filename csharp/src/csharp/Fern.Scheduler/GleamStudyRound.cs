using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fern.Registry;

namespace Fern.Scheduler;

/// <summary>
/// One supervisor's scheduling round, decided by the Gleam program under
/// <c>tools/fern-scheduler-gleam</c> (its <c>cycle</c> ask), with this class
/// carrying the Registry to it and its actions back.
///
/// The round is slices C4–C6 of the scheduling-loop design
/// (docs/programs/2026-09-24-gleam-scheduler-decisions/2026-09-24-the-cycle-that-claims-and-dispatches/design.md):
/// every cell that does not run is named with its reason, how far that reason
/// reaches and what lifts it; a refused cell hands its slot to the next one
/// instead of holding the queue; and a round that claims nothing still says
/// why. The program decides what to claim and when to rest; the Registry's
/// claim transaction still decides whether a claim is won, and the worker and
/// the fenced finish or fail are the same ones every cycle uses.
///
/// The program is borrowed: the command that started it owns it, as it does
/// for <see cref="GleamSchedulerDecision"/>. A program that does not answer,
/// or answers with anything but actions, is a <see cref="GleamProgramException"/>,
/// never a fall back to the C# cycle.
/// </summary>
public sealed class GleamStudyRound(GleamProgram program, string supervisor)
{
    /// <summary>The program this round asks; the office lease (slice W1) asks the same one.</summary>
    public GleamProgram Program => program;

    /// <summary>
    /// How long a claim the Registry refused waits before it is proposed again
    /// when nothing it depended on has changed: the Registry's own wait before
    /// it offers again a cell whose failure may go differently.
    /// </summary>
    public static readonly TimeSpan RefusalBackoff = RegistryOperations.RetryWindow;

    /// <summary>Claims lost to another supervisor in one reading before the round reads again.</summary>
    public const int RacesPerReading = 3;

    /// <summary>
    /// How long a free slot here may sit beside a claimable cell without a
    /// dispatch before it is told: the time after which the Registry calls an
    /// attempt's supervisor gone.
    /// </summary>
    public static readonly TimeSpan DispatchDeadline = RegistryOperations.StaleAfter;

    /// <summary>
    /// How long a training may go without reporting a step before it is told.
    /// It has no effect yet: the Registry keeps no training-progress time, only
    /// the supervisor's renewal, so every open attempt is sent with its progress
    /// unobserved and is not judged.
    /// </summary>
    public static readonly TimeSpan TrainingProgressDeadline = RegistryOperations.StaleAfter;

    static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(30);

    public abstract record Action;
    public sealed record Claim(long CellId, long SiteId, string Request) : Action;
    public sealed record Dispatch(long CellId, long SiteId, long AttemptId, long Epoch) : Action;
    public sealed record Settle(long CellId, long SiteId, string Request) : Action;
    public sealed record Notify(IReadOnlyList<Notice> Notices) : Action;
    public sealed record Rest(Outcome Outcome) : Action;

    /// <summary>A change worth telling, once: its kind, a stable key a receiver can recognise again, and the sentence.</summary>
    public sealed record Notice(string Kind, string Key, IReadOnlyList<long> CellIds, string Sentence);

    /// <summary>One reason that stops cells, with who acts next and every cell it stops.</summary>
    public sealed record Waiting(string Kind, string Key, string Actor, IReadOnlyList<long> CellIds, string Sentence);

    /// <summary>How a round ended, and the reasons of every cell that did not run.</summary>
    public sealed record Outcome(string Kind, DateTimeOffset? Until, IReadOnlyList<Waiting> Waiting)
    {
        /// <summary>The same outcome read twice is not printed twice.</summary>
        public string Key => Kind + "|" + (Until?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "")
            + "|" + string.Join(";", Waiting.Select(group => group.Key + ":" + string.Join(",", group.CellIds)));

        public bool IsComplete => Kind == "scope_complete";
    }

    /// <summary>Start this supervisor's round: which Study it serves (or every Study in state run) and on which Sites it claims.</summary>
    public void Start(long? studyId, IReadOnlyList<long> siteIds)
    {
        Ask(new JsonObject
        {
            ["kind"] = "start",
            ["settings"] = new JsonObject
            {
                ["study_id"] = studyId,
                ["site_ids"] = new JsonArray(siteIds.Select(id => (JsonNode)id).ToArray()),
                ["races_per_reading"] = RacesPerReading,
                ["refusal_backoff"] = (long)RefusalBackoff.TotalSeconds,
                ["deadlines"] = new JsonObject
                {
                    ["dispatch"] = (long)DispatchDeadline.TotalSeconds,
                    ["training_progress"] = (long)TrainingProgressDeadline.TotalSeconds,
                },
            },
        });
    }

    public IReadOnlyList<Action> Observed(JsonObject reading) =>
        Ask(new JsonObject { ["kind"] = "observed", ["observation"] = reading });

    public IReadOnlyList<Action> Won(long cellId, long siteId, long attemptId, long epoch) =>
        Answered(cellId, siteId, new JsonObject { ["kind"] = "won", ["attempt_id"] = attemptId, ["epoch"] = epoch });

    public IReadOnlyList<Action> Refused(long cellId, long siteId, string detail) =>
        Answered(cellId, siteId, new JsonObject { ["kind"] = "refused", ["detail"] = detail });

    public IReadOnlyList<Action> NoAnswer(long cellId, long siteId) =>
        Answered(cellId, siteId, new JsonObject { ["kind"] = "no_answer" });

    /// <summary>
    /// What the Registry says, afterwards, about a claim that got no answer.
    /// This side cannot prove an open attempt is the one its lost claim opened —
    /// the Registry keeps no claim name — so it says only "did not happen" when
    /// the cell has no open attempt, and "still unknown" otherwise. It never
    /// dispatches an attempt it cannot prove is its own.
    /// </summary>
    public IReadOnlyList<Action> Settled(long cellId, long siteId, bool cellHasOpenAttemptThere) =>
        Ask(new JsonObject
        {
            ["kind"] = "settle_answered",
            ["cell_id"] = cellId,
            ["site_id"] = siteId,
            ["settled"] = new JsonObject { ["kind"] = cellHasOpenAttemptThere ? "still_unknown" : "did_not_happen" },
        });

    IReadOnlyList<Action> Answered(long cellId, long siteId, JsonObject answer) =>
        Ask(new JsonObject
        {
            ["kind"] = "claim_answered",
            ["cell_id"] = cellId,
            ["site_id"] = siteId,
            ["answer"] = answer,
        });

    IReadOnlyList<Action> Ask(JsonObject message)
    {
        var answered = program.Ask(
            new JsonObject { ["ask"] = "cycle", ["supervisor"] = supervisor, ["message"] = message },
            AnswerWithin, null);
        if (!answered.IsOk)
        {
            throw new GleamProgramException("the Gleam round did not answer: " + answered.RefusalDetail);
        }
        if (answered.Value["answer"]?["actions"] is not JsonArray actions)
        {
            throw new GleamProgramException("the Gleam round answered without actions: " + answered.Value.ToJsonString());
        }
        try
        {
            return actions.Select(ReadAction).ToList();
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new GleamProgramException("the Gleam round returned an action this side cannot read: " + error.Message);
        }
    }

    static Action ReadAction(JsonNode? node)
    {
        var action = node as JsonObject ?? throw new InvalidOperationException("an action was not an object");
        return Text(action, "kind") switch
        {
            "claim" => new Claim(Number(action, "cell_id"), Number(action, "site_id"), Text(action, "request")),
            "dispatch" => new Dispatch(Number(action, "cell_id"), Number(action, "site_id"),
                Number(action, "attempt_id"), Number(action, "epoch")),
            "settle" => new Settle(Number(action, "cell_id"), Number(action, "site_id"), Text(action, "request")),
            "notify" => new Notify(Items(action, "notices").Select(notice => new Notice(
                Text(notice, "kind"), Text(notice, "key"), Numbers(notice, "cell_ids"), Text(notice, "sentence"))).ToList()),
            "rest" => new Rest(ReadOutcome(action["outcome"] as JsonObject
                ?? throw new InvalidOperationException("a rest carried no outcome"))),
            var other => throw new InvalidOperationException($"unknown action {other}"),
        };
    }

    static Outcome ReadOutcome(JsonObject outcome)
    {
        DateTimeOffset? until = outcome["until"] is JsonValue value && value.TryGetValue<long>(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
        var waiting = outcome.ContainsKey("waiting")
            ? Items(outcome, "waiting").Select(group =>
            {
                var reason = group["reason"] as JsonObject ?? throw new InvalidOperationException("a waiting group carried no reason");
                return new Waiting(Text(reason, "kind"), Text(reason, "key"), Text(reason, "actor"),
                    Numbers(group, "cell_ids"), Text(reason, "sentence"));
            }).ToList()
            : [];
        return new Outcome(Text(outcome, "kind"), until, waiting);
    }

    static IEnumerable<JsonObject> Items(JsonObject parent, string field) =>
        (parent[field] as JsonArray ?? throw new InvalidOperationException($"{field} was not a list"))
            .Select(item => item as JsonObject ?? throw new InvalidOperationException($"{field} held a non-object"));

    static string Text(JsonObject parent, string field) =>
        parent[field] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : throw new InvalidOperationException($"{field} was not a string");

    static long Number(JsonObject parent, string field) =>
        parent[field] is JsonValue value && value.TryGetValue<long>(out var number)
            ? number
            : throw new InvalidOperationException($"{field} was not an integer");

    static IReadOnlyList<long> Numbers(JsonObject parent, string field) =>
        (parent[field] as JsonArray ?? throw new InvalidOperationException($"{field} was not a list"))
            .Select(item => item is JsonValue value && value.TryGetValue<long>(out var number)
                ? number
                : throw new InvalidOperationException($"{field} held a non-integer"))
            .ToList();

    /// <summary>
    /// One reading of the Registry, in the form the round reads (its field list
    /// is in <c>cycle_wire.gleam</c>).
    ///
    /// One thing is not read yet and is said so here rather than guessed: a
    /// training's own progress time (sent as unobserved). A cell declares no
    /// dependencies: the Registry has no field for them.
    /// CHG 2026-09-25 (slice S2): a Site's contact is read from its last
    /// supervisor's lease as well as from source preparation; see
    /// <see cref="Contact"/>.
    /// CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
    /// slice 4): installed sources are retired, so the reading carries no held
    /// sources and no source preparations, and a Site's contact is read from its
    /// supervisors only.
    /// CHG 2026-09-25 (slice P2): the priorities are read. The Registry holds
    /// the four rank words and the moves to the back of a rank, and each Study
    /// and cell is sent with its own, or none; the round decides what a cell
    /// inherits, as the claim's order does.
    /// </summary>
    public static JsonObject Reading(
        IStudyStore store,
        IReadOnlyList<UnfinishedCell> cells,
        RegistryOperations.DispatchOrder order,
        IReadOnlyList<RegistryOperations.Site> sites,
        IReadOnlyList<RegistryOperations.SupervisorView> supervisors,
        IReadOnlyCollection<long> servedSiteIds,
        long maxAttempts,
        DateTimeOffset now,
        IReadOnlyList<RegistryOperations.SiteFailure>? siteFailures = null)
    {
        var at = now.ToUnixTimeSeconds();
        var studies = cells
            .GroupBy(cell => cell.StudyId)
            .Select(study => (JsonNode)new JsonObject
            {
                ["study_id"] = study.Key,
                ["name"] = study.First().StudyName,
                ["intent"] = study.First().StudyState switch
                {
                    RegistryOperations.StateRun => "run",
                    RegistryOperations.StatePause => "pause",
                    _ => "cancel",
                },
                ["priority"] = order.Priorities.GetValueOrDefault(RegistryOperations.StudyScope(study.Key)),
                ["moved_last"] = MoveJson(order, RegistryOperations.StudyScope(study.Key), at),
            })
            .ToArray();
        var scopes = new List<string> { RegistryOperations.ScopeAll };
        scopes.AddRange(cells.Select(cell => cell.StudyId).Distinct().Select(RegistryOperations.StudyScope));
        scopes.AddRange(cells.Select(cell => RegistryOperations.CellScope(cell.CellId)));
        scopes.AddRange(sites.Select(site => RegistryOperations.SiteScope(site.SiteId)));
        var requests = new[] { RegistryOperations.RequestPause, RegistryOperations.RequestHold }
            .SelectMany(kind => store.PendingRequests(kind, scopes))
            .Select(request => (JsonNode)new JsonObject
            {
                ["request_id"] = request.RequestId,
                ["kind"] = request.Kind,
                ["scope"] = request.Scope,
                ["by"] = request.RequestedBy,
                ["reason"] = request.Reason,
                ["at"] = Seconds(request.RequestedAt, at),
            })
            .ToArray();
        return new JsonObject
        {
            ["at"] = at,
            ["authority"] = new JsonObject { ["kind"] = "confirmed" },
            ["policy"] = new JsonObject
            {
                ["max_attempts"] = maxAttempts,
                ["retry_window"] = (long)RegistryOperations.RetryWindow.TotalSeconds,
                ["stale_after"] = (long)RegistryOperations.StaleAfter.TotalSeconds,
                // CHG 2026-09-25 (slice M3): the owner's three failures within ten minutes.
                ["burst_failures"] = RegistryOperations.BurstFailures,
                ["burst_window"] = (long)RegistryOperations.BurstWindow.TotalSeconds,
            },
            ["studies"] = new JsonArray(studies),
            ["cells"] = new JsonArray(cells.Select(cell => (JsonNode)CellJson(cell, order, at)).ToArray()),
            ["sites"] = new JsonArray(sites.Select(site => (JsonNode)SiteJson(
                site, supervisors, servedSiteIds, now, siteFailures ?? [])).ToArray()),
            ["requests"] = new JsonArray(requests),
        };
    }

    /// <summary>A move to the back of the rank over one scope, or null when none was made.</summary>
    static JsonObject? MoveJson(RegistryOperations.DispatchOrder order, string scope, long at) =>
        order.Moves.TryGetValue(scope, out var move)
            ? new JsonObject { ["behind"] = move.BehindStudyId, ["at"] = Seconds(move.SetAt, at) }
            : null;

    static JsonObject CellJson(UnfinishedCell cell, RegistryOperations.DispatchOrder order, long at)
    {
        var demand = SchedulerDecision.ReadDemand(
            cell.CellId, cell.StudyId, cell.Ordinal, cell.Name, cell.ConfigJson, cell.RequirementsJson, cell.SourceId);
        return new JsonObject
        {
            ["cell_id"] = cell.CellId,
            ["study_id"] = cell.StudyId,
            ["ordinal"] = cell.Ordinal,
            ["name"] = cell.Name,
            ["model_type"] = demand.ModelType,
            ["device"] = demand.Device,
            ["requires_cuda"] = demand.RequiresCuda,
            ["source_id"] = cell.SourceId,
            ["priority"] = order.Priorities.GetValueOrDefault(RegistryOperations.CellScope(cell.CellId)),
            ["moved_last"] = MoveJson(order, RegistryOperations.CellScope(cell.CellId), at),
            ["after"] = new JsonArray(),
            ["history"] = History(cell, at),
        };
    }

    static JsonObject History(UnfinishedCell cell, long at)
    {
        if (cell.AttemptId is not { } attemptId)
        {
            return new JsonObject { ["kind"] = "never_tried" };
        }
        var siteId = cell.AttemptSiteId ?? 0;
        if (cell.AttemptState is RegistryOperations.AttemptOpened or RegistryOperations.AttemptRunning)
        {
            var started = Seconds(cell.StartedAt, at);
            return new JsonObject
            {
                ["kind"] = "open",
                ["attempt_id"] = attemptId,
                ["site_id"] = siteId,
                ["started_at"] = started,
                ["beat_at"] = Seconds(cell.LastProgressAt, started),
                // CHG 2026-09-26 (slice W3): the trainer's own last epoch report,
                // apart from the supervisor's heartbeat, which also moves
                // `last_progress_at`; none until the trainer reports one.
                ["progress_at"] = cell.LastTrainingProgressAt is { } progress ? Seconds(progress, started) : null,
            };
        }
        var ended = Seconds(cell.EndedAt ?? cell.LastProgressAt, Seconds(cell.StartedAt, at));
        JsonObject last;
        if (cell.LastEventKind == RegistryOperations.RetryRequestedEvent)
        {
            var detail = Document(cell.LastEventDetailJson);
            last = new JsonObject
            {
                ["kind"] = "retry_requested",
                ["by"] = Field(detail, "requested_by") ?? "",
                ["reason"] = Field(detail, "reason") ?? "",
                ["at"] = Seconds(cell.LastEventAt, ended),
            };
        }
        else if (cell.AttemptState == RegistryOperations.AttemptFailed)
        {
            last = new JsonObject
            {
                ["kind"] = "failed",
                ["class"] = FailureClass(cell.LastEventDetailJson),
                ["site_id"] = siteId,
                ["ended_at"] = ended,
                ["sentence"] = CellFailureDetail.Sentence(cell.LastEventDetailJson ?? ""),
                // CHG 2026-09-25 (slice M1): the layer that failed, when the record names it.
                ["code"] = Field(Document(cell.LastEventDetailJson), "code"),
            };
        }
        else if (cell.AttemptState == RegistryOperations.AttemptCancelled)
        {
            last = new JsonObject { ["kind"] = "cancelled", ["ended_at"] = ended };
        }
        else
        {
            // Lost, or completed without being accepted: nobody is behind it
            // and it produced nothing the Registry kept.
            last = new JsonObject { ["kind"] = "lost", ["site_id"] = siteId, ["ended_at"] = ended };
        }
        return new JsonObject { ["kind"] = "ended", ["tries"] = cell.Tries, ["last"] = last };
    }

    /// <summary>
    /// The class a failure record names, or, for a record older than that
    /// field, the class its two flags state. A record with neither is sent
    /// without a class, which the round shows as unreadable; it is not read as
    /// transient, which would offer the cell again until its budget is spent
    /// without anyone being told why.
    /// </summary>
    internal static string? FailureClass(string? detailJson)
    {
        var detail = Document(detailJson);
        if (detail is null) return null;
        if (Field(detail, CellFailureDetail.FailureClassField) is { } named) return named;
        if (detail[CellFailureDetail.DeterministicField] is JsonValue deterministic
            && deterministic.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            if (deterministic.GetValueKind() == JsonValueKind.True) return RegistryOperations.FailureDeterministic;
            return detail[CellFailureDetail.SiteSpecificField] is JsonValue site && site.GetValueKind() == JsonValueKind.True
                ? RegistryOperations.FailureSite
                : RegistryOperations.FailureTransient;
        }
        return null;
    }

    /// <summary>
    /// The Site's own cell timeout, in seconds, from its supervisor declaration
    /// under the profiles directory of the repository this binary runs in.
    /// Null when there is no such declaration here (a Home or supervisor
    /// outside the repository, or a Site without one), and the flow then
    /// measures no budget for that Site rather than a number nobody stated.
    /// </summary>
    static long? DeclaredCellTimeout(string siteName)
    {
        if (GleamProgram.RepositoryRootOrNull() is not { } root) return null;
        var declaration = Path.Combine(root, Fern.Registry.RepositoryLayout.ProfilesDirectory, SiteFiles.SupervisorDeclarationName(siteName));
        if (!File.Exists(declaration)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(declaration));
        return document.RootElement.TryGetProperty("cell_timeout_seconds", out var seconds) && seconds.TryGetInt64(out var value)
            ? value : null;
    }

    static JsonObject SiteJson(
        RegistryOperations.Site site,
        IReadOnlyList<RegistryOperations.SupervisorView> supervisors,
        IReadOnlyCollection<long> servedSiteIds,
        DateTimeOffset now,
        IReadOnlyList<RegistryOperations.SiteFailure> siteFailures)
    {
        var at = now.ToUnixTimeSeconds();
        var offer = SchedulerDecision.ReadOffer(site.SiteId, site.Name, site.CapabilitiesJson, site.CapacityJson);
        return new JsonObject
        {
            ["site_id"] = offer.SiteId,
            ["name"] = offer.Name,
            ["admitted_models"] = new JsonArray(offer.AdmittedModels.Select(model => (JsonNode)model).ToArray()),
            ["device"] = offer.Device,
            ["slots"] = offer.Slots,
            // CHG 2026-09-27: the budget the flow measures this Site's trainings
            // against is the Site's own; it was one constant for every Site.
            ["cell_timeout_seconds"] = DeclaredCellTimeout(site.Name),
            ["contact"] = Contact(site.SiteId, supervisors, servedSiteIds, now),
            // CHG 2026-09-25 (slices M1 and M3, the reader's review): the Site's
            // recent failures, the same rows the claim reads, so the two agree.
            ["failures"] = new JsonArray(siteFailures.Where(failure => failure.SiteId == site.SiteId)
                .Select(failure => (JsonNode)new JsonObject
                {
                    ["source_id"] = failure.SourceId,
                    ["cell_id"] = failure.CellId,
                    ["ended_at"] = Seconds(failure.EndedAt, at),
                    ["sentence"] = failure.Sentence,
                    ["code"] = failure.Code,
                }).ToArray()),
        };
    }

    /// <summary>
    /// Whether the Site's supervisor holds its office. The Sites this process
    /// serves answer by definition: the process reading the Registry is their
    /// supervisor, with or without an office. Any other Site answers while its
    /// last supervisor has not ended and its lease has not run out; after
    /// that it has not answered since the ending or the lease's end; with no
    /// supervisor ever, it was never supervised. CHG 2026-09-25
    /// (how-a-site-is-reached-and-started, slice S2): until then every other
    /// Site was sent as answering.
    /// </summary>
    static JsonObject Contact(
        long siteId,
        IReadOnlyList<RegistryOperations.SupervisorView> supervisors,
        IReadOnlyCollection<long> servedSiteIds,
        DateTimeOffset now)
    {
        var at = now.ToUnixTimeSeconds();
        // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): a Site
        // that source preparation had seen disconnect was sent as not answering
        // here. Preparation is retired; outside tests it only ever reported a
        // connected Site, so the supervisors below are what says whether a
        // Site answers.
        if (servedSiteIds.Contains(siteId))
        {
            return new JsonObject { ["kind"] = "answering" };
        }
        if (supervisors.FirstOrDefault(supervisor => supervisor.SiteId == siteId) is not { } last)
        {
            return new JsonObject { ["kind"] = "never_supervised" };
        }
        // Leases are written in UTC; the comparison is of their text.
        if (last.HoldsOfficeAt(now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)))
        {
            return new JsonObject { ["kind"] = "answering" };
        }
        return new JsonObject { ["kind"] = "not_answering", ["since"] = Seconds(last.EndedAt ?? last.LeaseUntil, at) };
    }

    static JsonObject? Document(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? Field(JsonObject? document, string field) =>
        document?[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>A Registry time as whole seconds since 1970, or <paramref name="otherwise"/> when it is absent or unreadable.</summary>
    static long Seconds(string? iso, long otherwise) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUnixTimeSeconds()
            : otherwise;
}
