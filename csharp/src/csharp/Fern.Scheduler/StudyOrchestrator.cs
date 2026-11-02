using System.Globalization;
using Fern.Registry;

namespace Fern.Scheduler;

/// <summary>
/// One scheduling cycle over the one-store schema.
///
/// It calls exactly three things: <see cref="RegistryOperations"/> for every
/// durable fact, <see cref="SchedulerDecision"/> for the placement, and the
/// worker client it is handed for the execution. It holds no state between
/// cycles and no state inside one: everything it needs is read at the top of
/// the cycle and written through the Registry before it returns, so a
/// supervisor that dies mid-cycle leaves the Registry consistent and the next
/// supervisor simply reads it.
///
/// The order matters and is the whole design:
///
///   1. read the runnable cells and the Sites  (a projection; may be stale)
///   2. decide                                  (pure; no window to lose)
///   3. claim                                   (one transaction; settles races)
///   4. dispatch                                (the only slow step)
///   5. finish or fail                          (fenced by the claim's epoch)
///
/// Steps 1 and 2 can be wrong — two supervisors can decide the same placement.
/// Step 3 is where that is settled, once, by the database, and a supervisor
/// that loses a claim simply moves on. Nothing before step 3 is authoritative,
/// and nothing after it is guessed.
/// </summary>
public static class StudyOrchestrator
{
    /// <summary>
    /// What a worker client must do for the orchestrator: run one claimed cell
    /// and say how it ended.
    ///
    /// It is an interface so the orchestrator can be tested without a Site, a
    /// transport or a GPU. The production implementation is the existing cell
    /// worker; nothing about this contract invites a second one.
    /// </summary>
    public interface ICellWorker
    {
        /// <summary>
        /// Run the claimed cell. The epoch is the attempt's own and must travel
        /// with every progress report, so a displaced worker's late write is
        /// refused by the Registry rather than by the worker's own good manners.
        /// </summary>
        WorkerOutcome Run(ClaimedCell cell, CancellationToken cancellation);
    }

    /// <summary>
    /// A worker that can say, before a claim, why it cannot give a cell of a
    /// source its code on a Site: the repository is missing, or does not hold the
    /// commit the source was registered at. A cell it cannot serve is not
    /// claimed, so no attempt is spent on the machine's shortfall
    /// (docs/programs/2026-09-26-source-roots-stop-being-full-copies, slice 2).
    /// </summary>
    public interface ICodeCheck
    {
        string? CannotGiveCode(RegistryOperations.Site site, long sourceId);

        /// <summary>Delete the code directories an earlier supervisor made for attempts that are no longer open; returns them.</summary>
        IReadOnlyList<string> RemoveCodeLeftovers(IReadOnlySet<long> openAttempts);
    }

    /// <summary>The worker's reason it cannot give this source's code on this Site, or null.</summary>
    internal static string? CodeUnavailable(
        ICellWorker worker, IReadOnlyList<RegistryOperations.Site> sites, long siteId, long sourceId) =>
        worker is ICodeCheck check && sites.FirstOrDefault(site => site.SiteId == siteId) is { } site
            ? check.CannotGiveCode(site, sourceId)
            : null;

    // CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
    // slice 4; owner: "compile远端其实只是检查，本地compile卡片即可"): ICompilerChoice
    // and CompilerMismatch (slice M2) are retired. A cell run no longer compiles
    // its card, so no Site's compiler has to match the study's before a claim,
    // and no compiler is carried to a Site.

    /// <summary>What the orchestrator hands a worker: the cell, its attempt, and the epoch.</summary>
    /// <param name="Device">
    /// The card the Site granted this attempt, or null when the Site declares no
    /// cards and is counted by slots. The worker puts it in the trainer's
    /// environment; nothing else reads it.
    /// </param>
    /// <param name="CodeFrom">
    /// Where the Site takes the cell's code from, as the claim read it
    /// (<see cref="RegistryOperations.StartedAttempt.CodeFrom"/>); null from an
    /// older Home, which means installed sources. <see cref="CellCode"/> makes the
    /// directory from it, <paramref name="SourceRoot"/> and
    /// <paramref name="SourceId"/> (source-roots-stop-being-full-copies, slice 2).
    /// </param>
    public sealed record ClaimedCell(
        long StudyId, long CellId, string CellName, long AttemptId, long FencingEpoch,
        long SiteId, string RequestJson, string? Device = null, string? SourceRoot = null,
        string? CodeFrom = null, long? SourceId = null);

    /// <summary>
    /// How a run ended. A result means completed; a detail with no result means
    /// failed, unless <paramref name="EndState"/> names another ending the store
    /// accepts (cancelled, when a study asked to cancel reached the trainer).
    /// </summary>
    public sealed record WorkerOutcome(
        bool Completed, string? ResultJson, string DetailJson, string? RemoteExecutionId = null, string? EndState = null);

    /// <summary>What one cycle did, for a person reading a log or a test asserting on it.</summary>
    public sealed record CycleReport(
        int Runnable, int Placed, int Claimed, int Completed, int Failed, int LostClaims,
        IReadOnlyList<SchedulerDecision.Unplaced> Unplaced,
        IReadOnlyList<CellFailure> Failures);

    /// <summary>One cell that ran and produced no result, and the sentence a person is owed for it.</summary>
    /// <param name="Detail">The worker's detail document; see <see cref="CellFailureDetail"/>.</param>
    public sealed record CellFailure(long CellId, string CellName, string Detail)
    {
        /// <summary>The one sentence, lifted out of the detail document for a log line.</summary>
        public string Sentence => CellFailureDetail.Sentence(Detail);

        /// <summary>Whether handing this cell back would produce the same refusal.</summary>
        public bool Deterministic => CellFailureDetail.IsDeterministic(Detail);
    }

    /// <summary>
    /// Run one cycle. Returns what it did; never throws for a refusal, because
    /// a refusal is an ordinary outcome of racing another supervisor.
    /// </summary>
    public static CycleReport RunOnce(
        IStudyStore store,
        ICellWorker worker,
        long? studyId = null,
        CancellationToken cancellation = default,
        IReadOnlySet<long>? alreadyAttempted = null,
        string? siteName = null,
        long maxAttempts = RegistryOperations.DefaultMaxAttempts,
        ISchedulerDecision? implementedBy = null,
        long? officeEpoch = null)
    {
        // The reconciler, first thing in the cycle: an attempt whose supervisor
        // stopped beating is rewritten as lost, so its cell is claimable again
        // by this cycle rather than by a person. Ported from the retired plan
        // reconciler, which ran in exactly this position inside the resident
        // scheduler's loop.
        // CHG 2026-09-26 (slice W2 of leases-and-watching-a-running-cell, owner:
        // notify, do not re-dispatch): the cycle no longer closes silent
        // attempts. One may still be training on a machine nobody can reach;
        // the Home tells the owner after 30 minutes and a person gives it up
        // with `retry-cell`. ReconcileSilentAttempts stays for that person.

        // 1. Read. Both of these are projections and may be stale the moment
        //    they return; step 3 is what settles that.
        //
        //    A cell this process already ran and failed is dropped here. The
        //    store makes a failed cell runnable again — that is right, because
        //    another supervisor or a later command may legitimately retry it —
        //    but a loop that re-claimed its own failure would spend the machine
        //    printing one sentence: on 2026-09-11 a card refused thirteen cells
        //    in a millisecond each, and nothing stopped it coming round again.
        var runnable = store.ListRunnableCells(studyId, maxAttempts)
            .Where(cell => alreadyAttempted is null || !alreadyAttempted.Contains(cell.CellId))
            .ToList();
        // This process runs every cell it claims on the machine it was started
        // on, so it may only claim cells placed on THAT machine's Site. Before
        // this the decision could place a cell on the pod's Site and the local
        // worker would train it anyway, leaving an attempt that names a machine
        // it never ran on. The honest filter is here rather than after the
        // decision, so a cell no local Site admits is reported unplaced with
        // the reason instead of claimed and then refused.
        var sites = store.ListSites()
            .Where(site => siteName is null || site.Name == siteName)
            // CHG 2026-09-11 night (slice 3, row 30): a Site a person disabled
            // (a standing hold over it) or paused offers nothing this cycle;
            // the claim refuses it too, this only saves the refusal.
            .Where(site => store.PendingRequests(
                    RegistryOperations.RequestHold, [RegistryOperations.SiteScope(site.SiteId)]).Count == 0
                && store.PendingRequests(
                    RegistryOperations.RequestPause, [RegistryOperations.SiteScope(site.SiteId)]).Count == 0)
            .ToList();
        if (runnable.Count == 0 || sites.Count == 0)
        {
            return new CycleReport(runnable.Count, 0, 0, 0, 0, 0, [], []);
        }

        // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): a Site
        // no longer holds sources; every Site gives each cell its code from its
        // repository, and whether this machine can is asked before the claim.
        var offers = sites
            .Select(site => SchedulerDecision.ReadOffer(site.SiteId, site.Name, site.CapabilitiesJson, site.CapacityJson))
            .ToList();
        var demands = runnable
            .Select(cell => SchedulerDecision.ReadDemand(
                cell.CellId, cell.StudyId, cell.Ordinal, cell.Name, cell.ConfigJson, cell.RequirementsJson, cell.SourceId))
            .ToList();
        // The model type lives in the cell's configuration, which
        // ListRunnableCells does not carry; read it once per cycle rather than
        // widening a projection every caller pays for.
        // CHG: the projection now carries configuration in the same request;
        // fetching ReadCell here copied the full compiled Study for every
        // candidate before any remote worker could claim a cell.

        // 2. Decide. Pure. The caller may pass another implementation; omitted,
        //    it is this binary's own, and nothing else in the cycle changes.
        var decision = (implementedBy ?? CSharpSchedulerDecision.Instance).Choose(demands, offers, store.BusySlots());

        var claimed = 0;
        var lost = 0;
        var completed = 0;
        var failed = 0;
        var failures = new List<CellFailure>();
        var unplaced = decision.Unplaced.ToList();
        foreach (var placement in decision.Placements)
        {
            if (cancellation.IsCancellationRequested) break;

            var cell = runnable.First(candidate => candidate.CellId == placement.CellId);
            // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 2): the
            // cell's code must be available here before an attempt is opened.
            if (CodeUnavailable(worker, sites, placement.SiteId, cell.SourceId) is { } unavailable)
            {
                unplaced.Add(new SchedulerDecision.Unplaced(cell.CellId, unavailable));
                continue;
            }
            var request = $$"""{"schema":"fern.cell_request.v2","cell":"{{cell.Name}}"}""";

            // 3. Claim. One transaction. A refusal here is another supervisor
            //    winning the same cell, which is ordinary and not an error.
            //    CHG 2026-09-26 (slice W1): the claim carries the office's epoch,
            //    and the Registry refuses it unless that office is current and held.
            var started = store.TryStartAttempt(cell.CellId, placement.SiteId, request, maxAttempts, officeEpoch);
            if (!started.TryGetValue(out var attempt))
            {
                lost++;
                continue;
            }
            claimed++;

            // 4. Dispatch. The only slow step, and the only one outside a
            //    transaction: a transaction held across a training run would
            //    hold the write lock for hours.
            var claimedCell = new ClaimedCell(
                cell.StudyId, cell.CellId, cell.Name, attempt.AttemptId, attempt.FencingEpoch,
                placement.SiteId, request, attempt.Device, attempt.SourceRoot,
                attempt.CodeFrom, cell.SourceId);
            switch (RunClaimed(store, worker, claimedCell, cancellation, failures))
            {
                case RunEnding.Completed: completed++; break;
                case RunEnding.Failed: failed++; break;
                case RunEnding.Lost: lost++; break;
            }
        }

        return new CycleReport(
            runnable.Count, decision.Placements.Count, claimed, completed, failed, lost, unplaced, failures);
    }

    /// <summary>
    /// One cycle whose choices after the reading are the Gleam round's: which
    /// cell is claimed next, what a refusal changes, when the round rests and
    /// on what. The claim transaction, the worker and the fenced finish or fail
    /// are the ones <see cref="RunOnce"/> uses.
    ///
    /// The order is the same as <see cref="RunOnce"/>'s, with the decision
    /// replaced: reconcile; prepare missing sources exactly as the C# cycle
    /// does, before the reading, so a source recorded now is claimable now;
    /// read every unfinished cell, every Site and every standing request; then
    /// carry out the round's actions one by one, answering each claim with the
    /// Registry's own answer, until the round rests.
    /// </summary>
    static (CycleReport Report, GleamStudyRound.Outcome? Rested, IReadOnlyList<GleamStudyRound.Notice> Told) RunRoundOnce(
        IStudyStore store,
        ICellWorker worker,
        GleamStudyRound round,
        long? studyId,
        CancellationToken cancellation,
        string siteName,
        long maxAttempts,
        long? officeEpoch)
    {
        // CHG 2026-09-26 (slice W2 of leases-and-watching-a-running-cell, owner:
        // notify, do not re-dispatch): the cycle no longer closes silent
        // attempts. One may still be training on a machine nobody can reach;
        // the Home tells the owner after 30 minutes and a person gives it up
        // with `retry-cell`. ReconcileSilentAttempts stays for that person.

        var sites = store.ListSites();
        var runnable = store.ListRunnableCells(studyId, maxAttempts);

        // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): no
        // source is prepared or held; the reading carries neither.
        var cells = store.ListUnfinishedCells(studyId);
        var byId = cells.ToDictionary(cell => cell.CellId);
        var reading = GleamStudyRound.Reading(
            store, cells, store.ReadDispatchOrder(), sites,
            store.ListSiteSupervisors(),
            sites.Where(site => site.Name == siteName).Select(site => site.SiteId).ToList(),
            maxAttempts, DateTimeOffset.UtcNow, store.ListSiteFailures());
        var won = new Dictionary<long, (RegistryOperations.StartedAttempt Attempt, string Request)>();
        var told = new List<GleamStudyRound.Notice>();
        var failures = new List<CellFailure>();
        GleamStudyRound.Outcome? rested = null;
        var claimed = 0;
        var completed = 0;
        var failed = 0;
        var lost = 0;
        var pending = new Queue<GleamStudyRound.Action>(round.Observed(reading));
        while (pending.TryDequeue(out var action))
        {
            switch (action)
            {
                case GleamStudyRound.Notify notify:
                    told.AddRange(notify.Notices);
                    break;
                case GleamStudyRound.Claim claim:
                {
                    // A cancelled command claims nothing more. The claim the
                    // round is left waiting on ends with the program.
                    if (cancellation.IsCancellationRequested)
                    {
                        pending.Clear();
                        break;
                    }
                    if (!byId.TryGetValue(claim.CellId, out var cell))
                    {
                        throw new GleamProgramException($"the Gleam round claimed cell {claim.CellId}, which the reading did not hold");
                    }
                    var request = $$"""{"schema":"fern.cell_request.v2","cell":"{{cell.Name}}"}""";
                    IReadOnlyList<GleamStudyRound.Action> next;
                    // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 2): the
                    // cell's code must be available here before an attempt is opened.
                    if (CodeUnavailable(worker, sites, claim.SiteId, cell.SourceId) is { } unavailable)
                    {
                        lost++;
                        foreach (var item in round.Refused(claim.CellId, claim.SiteId, unavailable)) pending.Enqueue(item);
                        break;
                    }
                    try
                    {
                        // CHG 2026-09-26 (slice W1): with the office's epoch; see RunOnce.
                        var started = store.TryStartAttempt(claim.CellId, claim.SiteId, request, maxAttempts, officeEpoch);
                        if (started.TryGetValue(out var attempt))
                        {
                            won[claim.CellId] = (attempt, request);
                            next = round.Won(claim.CellId, claim.SiteId, attempt.AttemptId, attempt.FencingEpoch);
                        }
                        else
                        {
                            started.TryGetRefusal(out var refusal);
                            lost++;
                            next = round.Refused(claim.CellId, claim.SiteId, refusal.Render());
                        }
                    }
                    catch (HomeUnreachableException)
                    {
                        // Whether the claim happened is unknown; the round keeps
                        // its slot and asks, and does not send it again.
                        next = round.NoAnswer(claim.CellId, claim.SiteId);
                    }
                    foreach (var item in next) pending.Enqueue(item);
                    break;
                }
                case GleamStudyRound.Dispatch dispatch:
                {
                    if (!won.TryGetValue(dispatch.CellId, out var claim) || claim.Attempt.AttemptId != dispatch.AttemptId)
                    {
                        throw new GleamProgramException(
                            $"the Gleam round dispatched attempt {dispatch.AttemptId} of cell {dispatch.CellId}, which this side did not win");
                    }
                    var cell = byId[dispatch.CellId];
                    claimed++;
                    var claimedCell = new ClaimedCell(
                        cell.StudyId, cell.CellId, cell.Name, claim.Attempt.AttemptId, claim.Attempt.FencingEpoch,
                        dispatch.SiteId, claim.Request, claim.Attempt.Device, claim.Attempt.SourceRoot,
                        claim.Attempt.CodeFrom, cell.SourceId);
                    switch (RunClaimed(store, worker, claimedCell, cancellation, failures))
                    {
                        case RunEnding.Completed: completed++; break;
                        case RunEnding.Failed: failed++; break;
                        case RunEnding.Lost: lost++; break;
                    }
                    break;
                }
                case GleamStudyRound.Settle settle:
                {
                    bool open;
                    try
                    {
                        open = store.ListUnfinishedCells(studyId).Any(cell => cell.CellId == settle.CellId
                            && cell.AttemptSiteId == settle.SiteId
                            && cell.AttemptState is RegistryOperations.AttemptOpened or RegistryOperations.AttemptRunning);
                    }
                    catch (HomeUnreachableException)
                    {
                        open = true;
                    }
                    foreach (var item in round.Settled(settle.CellId, settle.SiteId, open)) pending.Enqueue(item);
                    break;
                }
                case GleamStudyRound.Rest rest:
                    rested = rest.Outcome;
                    break;
            }
        }
        return (new CycleReport(runnable.Count, claimed, claimed, completed, failed, lost, [], failures),
            rested, told);
    }

    /// <summary>What a round rested on, as the lines a person reads.</summary>
    static IEnumerable<string> Summary(GleamStudyRound.Outcome outcome)
    {
        yield return outcome.Kind switch
        {
            "work_dispatched" => "the round dispatched work",
            "work_underway" => "the round rests while work is under way",
            "waiting_for" => outcome.Until is { } until
                ? $"nothing can be claimed now; the earliest moment a reason names is {until.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)}"
                : "nothing can be claimed now; the round waits for one of the events named below",
            "scope_complete" => "every cell in scope is finished",
            "stopped_as_asked" => "every cell left is stopped by a person's word",
            var other => "the round rested: " + other,
        };
        foreach (var group in outcome.Waiting)
        {
            var cells = group.CellIds.Count == 1
                ? $"cell {group.CellIds[0]}"
                : $"cells {string.Join(", ", group.CellIds)}";
            yield return $"  {cells}: {group.Sentence}";
        }
    }

    /// <summary>How one dispatched attempt ended, as a cycle counts it.</summary>
    enum RunEnding { Completed, Failed, Lost }

    /// <summary>
    /// Run one claimed cell and record how it ended, fenced by the claim's own
    /// epoch: steps 4 and 5 of a cycle.
    /// </summary>
    /// <remarks>
    /// CHG 2026-09-25: moved out of <see cref="RunOnce"/> unchanged, so the
    /// Gleam round dispatches through the same worker call and the same
    /// finish or fail.
    /// </remarks>
    static RunEnding RunClaimed(
        IStudyStore store, ICellWorker worker, ClaimedCell claimedCell, CancellationToken cancellation, List<CellFailure> failures)
    {
        WorkerOutcome outcome;
        try
        {
            outcome = worker.Run(claimedCell, cancellation);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A worker that threw is a failed attempt, not a lost cycle.
            // Recording it is what lets the cell be claimed again.
            // The supervisor's own fault, not the cell's and not the
            // machine's: whether it repeats is unknown until it is read.
            var thrown = CellFailureDetail.Write(
                $"the worker threw while running the cell: {error.Message}",
                deterministic: false, CellFailureDetail.ClassTransient);
            store.FailAttempt(claimedCell.AttemptId, claimedCell.FencingEpoch,
                RegistryOperations.AttemptFailed, thrown);
            failures.Add(new CellFailure(claimedCell.CellId, claimedCell.CellName, thrown));
            return RunEnding.Failed;
        }

        // 5. Finish or fail, fenced by the claim's own epoch. If this
        //    supervisor was displaced while the worker ran, both of these
        //    are refused and the successor's result stands.
        if (outcome.RemoteExecutionId is not null)
        {
            store.RenewAttempt(claimedCell.AttemptId, claimedCell.FencingEpoch, outcome.RemoteExecutionId);
        }
        if (outcome.Completed && outcome.ResultJson is not null)
        {
            return store.FinishAttempt(claimedCell.AttemptId, claimedCell.FencingEpoch, outcome.ResultJson).IsOk
                ? RunEnding.Completed
                : RunEnding.Lost;
        }
        // CHG 2026-09-11 night (slice 2): the worker may name the ending
        // (cancelled); anything else it did not finish is failed.
        if (store.FailAttempt(claimedCell.AttemptId, claimedCell.FencingEpoch,
                outcome.EndState ?? RegistryOperations.AttemptFailed, outcome.DetailJson).IsOk)
        {
            failures.Add(new CellFailure(claimedCell.CellId, claimedCell.CellName, outcome.DetailJson));
            return RunEnding.Failed;
        }
        return RunEnding.Lost;
    }

    /// <summary>
    /// The sources each of these Sites holds, as the claim will judge it.
    /// </summary>
    /// <remarks>
    /// CHG 2026-09-25: moved out of <see cref="RunOnce"/> unchanged, so the
    /// Gleam round's cycle reads held sources the same way.
    /// </remarks>
    /// <summary>
    /// CHG 2026-09-26 (owner: "本机上的尝试，如果它的进程确实已经不在了，就自动放弃"):
    /// give up every open attempt of this Site that a process on THIS machine ran and whose
    /// process is gone. On this machine that is a lookup, not a guess: the attempt names
    /// <c>host/pid</c> (<see cref="LocalCellWorker.RunningHere"/>), and a trainer does not outlive
    /// the supervisor that started it. A pid taken by another process reads as alive, which only
    /// leaves the attempt to the Home's rule (given up after an hour of silence); an attempt of
    /// another machine, or one that never said which process ran it, is left to that rule too.
    /// Returns the sentences of what was given up.
    /// </summary>
    internal static IReadOnlyList<string> GiveUpAttemptsWhoseProcessIsGone(
        IStudyStore store, long siteId, string host, int ownPid, Func<int, bool> alive)
    {
        var given = new List<string>();
        foreach (var cell in store.ListUnfinishedCells(null))
        {
            if (cell.AttemptId is not { } attemptId || cell.FencingEpoch is not { } epoch || cell.AttemptSiteId != siteId
                || cell.AttemptState is not (RegistryOperations.AttemptOpened or RegistryOperations.AttemptRunning)
                || cell.RemoteExecutionId is not { } remote)
            {
                continue;
            }
            var slash = remote.LastIndexOf('/');
            if (slash <= 0 || remote[..slash] != host || !int.TryParse(remote[(slash + 1)..], out var pid)
                || pid == ownPid || alive(pid))
            {
                continue;
            }
            var sentence = $"attempt {attemptId} of cell {cell.Name} (study {cell.StudyName}) was run by process {pid} on "
                + $"{host}, which is gone; given up by looking, and its cell is offered again";
            var detail = new System.Text.Json.Nodes.JsonObject
            {
                ["detail"] = sentence,
                ["given_up_by"] = "the supervisor that took this Site's office after it, by looking",
                ["process"] = remote,
            }.ToJsonString();
            if (store.FailAttempt(attemptId, epoch, RegistryOperations.AttemptLost, detail).IsOk) given.Add(sentence);
        }
        return given;
    }

    /// <summary>Whether a process with this id exists on this machine.</summary>
    internal static bool ProcessAlive(int pid) => Directory.Exists($"/proc/{pid}");

    // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): HeldSources,
    // SitesOnThisMachine, RootIsAbsent, RecordedRootIsGone and
    // PrepareMissingSources are retired with installed sources. No Site holds a
    // source, nothing is installed, and a machine that cannot give a cell its
    // code says so before the claim (ICodeCheck).

    /// <summary>
    /// Repeat RunOnce until the study has nothing left to claim.
    ///
    /// It lives here, beside the cycle it repeats, rather than in the CLI verb
    /// that drives it: a human command wraps the route, and a loop reachable
    /// only through option parsing cannot be tested without it.
    ///
    /// It stops when a cycle claims nothing, and says WHY: a study with every
    /// cell accepted is finished; one that is paused or cancelled is waiting on
    /// an operator; one with runnable cells that no Site admits is waiting on a
    /// machine, and the reason the decision gave is printed. An empty loop that
    /// just exits would leave all three looking the same.
    ///
    /// It claims only cells placed on <paramref name="siteName"/>, which is the
    /// machine this process is: it runs what it claims locally, so claiming a
    /// cell placed on another machine would record an attempt at a Site that
    /// never saw it.
    ///
    /// A cell this loop already ran and failed is not claimed again by it. The
    /// store deliberately makes a failed cell runnable — another supervisor, or
    /// this command run again, may retry it — but deciding to retry is a
    /// person's, and a loop that re-claimed its own failures would never end:
    /// nothing in the one-store schema counts a cell's tries against a budget.
    /// Each failure is printed once, with the worker's sentence, and named
    /// again in the conclusion.
    /// </summary>
    /// <remarks>
    /// CHG 2026-09-11 night (slice 1 of the cell port, design note
    /// docs/design/original/by_ai/2026-09-11-restore-the-behavior-port-the-input.md,
    /// rows 24-27): the loop can hold the office of its Site. Given a
    /// <see cref="SupervisorIdentity"/>, it claims the Site's office before its
    /// first cycle and exits 1 when another supervisor holds it (retryable: the
    /// duty is outstanding, systemd tries again); it renews the lease every
    /// cycle and exits 1 when displaced; between cycles, when nothing runs, it
    /// consumes a stop addressed to its Site or to every Site and exits 0
    /// (completed: the duty was revoked, systemd stays down); and every ending
    /// is written on the supervisor's own row with the disposition the exit
    /// code came from. A process that is killed leaves that row unended behind
    /// an expired lease, which the monitor projects as unconfirmed. Without an
    /// identity nothing changes: a script or a route test runs one study and
    /// returns as before. A study name is optional under an identity: a
    /// resident supervisor serves every study in state run, which is why its
    /// office is the Site's and not a study's.
    ///
    /// The exit mapping is the retired daemon's (SchedulerServe.DispositionFor):
    /// completed 0, retryable 1, terminal configuration 2 (a Site nobody
    /// registered, so systemd must not hot-loop), fatal 3. One deliberate
    /// difference: a cancellation ends as completed with exit 0 rather than as
    /// retryable, because a person's Ctrl-C on a foreground command revokes the
    /// duty, and under systemd a SIGTERM is `systemctl stop`, which revokes it too.
    /// </remarks>
    public static int RunUntilIdle(
        IStudyStore store,
        StudyOrchestrator.ICellWorker worker,
        long? studyId,
        string? studyName,
        string siteName,
        int maxCycles,
        TextWriter stdout,
        TextWriter stderr,
        long maxAttempts = RegistryOperations.DefaultMaxAttempts,
        TimeSpan? serveInterval = null,
        CancellationToken cancellation = default,
        SupervisorIdentity? supervisor = null,
        long leaseSeconds = RegistryOperations.DefaultSupervisorLeaseSeconds,
        ISchedulerDecision? implementedBy = null,
        GleamStudyRound? round = null)
    {
        // The machine says which Site it is, once, before it claims anything. A
        // name no Site carries is a stop, not an empty loop: a typo would
        // otherwise read exactly like a study with nothing left to run. For a
        // supervisor it is a terminal configuration: exit 2, which the unit
        // lists as not to be restarted.
        var site = store.ListSites().FirstOrDefault(candidate => candidate.Name == siteName);
        if (site is null)
        {
            stderr.WriteLine(
                $"run-study-cells: no Site named {siteName} is registered; "
                + "register-site mints it from this machine's execution profile");
            return supervisor is null ? 3 : ExitTerminalConfiguration;
        }
        // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): source
        // preparation is retired, and with it the lines it printed here when the
        // Site connected and the runtime was selected.
        if (studyId is null && serveInterval is null)
        {
            stderr.WriteLine("run-study-cells: without --serve a study must be named; a one-shot run is one study's");
            return 3;
        }
        var label = studyName ?? "every study in state run";
        // CHG 2026-09-25: a Gleam round is started before the office is taken,
        // so a program that cannot answer ends the command holding nothing.
        round?.Start(studyId, [site.SiteId]);
        RegistryOperations.SupervisorPermit? office = null;
        // CHG 2026-09-26 (slice W1 of leases-and-watching-a-running-cell): with a
        // Gleam program the office is kept through its lease state machine. A
        // supervisor that finds the office held waits and asks again rather than
        // exiting to be restarted; a renewal nobody answered stops claiming, not
        // the training. Without one, every step below is as it was.
        var lease = supervisor is not null && round is not null ? new GleamOfficeLease(round.Program, leaseSeconds) : null;
        if (supervisor is not null)
        {
            while (true)
            {
                var sentAt = lease?.Now ?? 0;
                var claim = store.StartSupervisor(
                    site.SiteId, supervisor.Host, supervisor.Pid, supervisor.BinarySha256, leaseSeconds);
                if (claim.TryGetValue(out office))
                {
                    lease?.Granted(office.Epoch, sentAt);
                    break;
                }
                claim.TryGetRefusal(out var refusal);
                if (lease is null)
                {
                    stderr.WriteLine($"run-study-cells: {refusal.Detail}");
                    return ExitRetryable;
                }
                var holder = store.ListSiteSupervisors().FirstOrDefault(latest => latest.SiteId == site.SiteId);
                var told = lease.HeldBy(holder?.Host ?? "an unknown host", holder?.Pid ?? 0);
                stdout.WriteLine($"run-study-cells: {refusal.Detail}; {lease.Sentence}");
                var wait = Math.Max(1, (told.AskAgainAt ?? lease.Now + 1) - lease.Now);
                if (cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(wait)))
                {
                    stdout.WriteLine("run-study-cells: stopped waiting for the office as asked");
                    return ExitCompleted;
                }
            }
            stdout.WriteLine(
                $"run-study-cells: supervising Site {siteName} at epoch {office.Epoch} with a {leaseSeconds} s lease, serving {label}");
            // CHG 2026-09-26 (owner: an attempt on this machine whose process is gone is given
            // up without waiting for a person): what an earlier supervisor of this Site left here.
            foreach (var sentence in GiveUpAttemptsWhoseProcessIsGone(
                         store, office.SiteId, Environment.MachineName, Environment.ProcessId, ProcessAlive))
            {
                stdout.WriteLine("run-study-cells: " + sentence);
            }
            // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 2): and the
            // code directories it made for attempts that are no longer open.
            if (worker is ICodeCheck code)
            {
                var open = store.ListUnfinishedCells(null)
                    .Where(cell => cell.AttemptSiteId == office.SiteId && cell.AttemptId is not null
                        && cell.AttemptState is RegistryOperations.AttemptOpened or RegistryOperations.AttemptRunning)
                    .Select(cell => cell.AttemptId!.Value)
                    .ToHashSet();
                foreach (var removed in code.RemoveCodeLeftovers(open))
                {
                    stdout.WriteLine($"run-study-cells: removed {removed}, the code directory of an attempt that is no longer open");
                }
            }
        }
        var cycles = 0;
        var completed = 0;
        var failed = 0;
        var attempted = new HashSet<long>();
        var refusals = new List<CellFailure>();
        string? lastRested = null;
        while (maxCycles <= 0 || cycles < maxCycles)
        {
            // CHG 2026-09-26 (slice W1): through the lease, a renewal nobody
            // answered keeps the office until its lease runs out and claims
            // nothing meanwhile; only a refusal or a lease run out ends it.
            if (office is not null && lease is not null)
            {
                var renewed = lease.Renew(store, office);
                var ticked = lease.Tick();
                if ((renewed.Exit ?? ticked.Exit) is { } why)
                {
                    stderr.WriteLine($"run-study-cells: Site {siteName}: {why}; this supervisor exits and is retried");
                    return End(store, office, "office_lost", RegistryOperations.DispositionRetryable, ExitRetryable);
                }
                if (!lease.MayClaim)
                {
                    stdout.WriteLine($"run-study-cells: Site {siteName}: {lease.Sentence}");
                    if (cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Max(1, leaseSeconds / 3))))
                    {
                        stdout.WriteLine($"run-study-cells: stopped serving {label} as asked");
                        return End(store, office, "cancelled", RegistryOperations.DispositionCompleted, ExitCompleted);
                    }
                    continue;
                }
            }
            if (office is not null && lease is null && !store.RenewSupervisor(office, leaseSeconds).IsOk)
            {
                stderr.WriteLine(
                    $"run-study-cells: the office of Site {siteName} was taken by another supervisor; this one exits and is retried");
                return End(store, office, "office_lost", RegistryOperations.DispositionRetryable, ExitRetryable);
            }
            // A stop is consumed only here, when this supervisor runs
            // nothing: the retired daemon consumed one only between children,
            // and a cell that trains is never interrupted by a stop.
            // CHG 2026-09-22: check before claiming, including the first claim.
            // The previous worker has returned; runnable cells must not defer
            // a stop until the queue becomes empty.
            if (office is not null)
            {
                var stop = store.ConsumeStop(office);
                if (!stop.TryGetValue(out var request))
                {
                    stderr.WriteLine(
                        $"run-study-cells: the office of Site {siteName} was taken by another supervisor; this one exits and is retried");
                    return End(store, office, "office_lost", RegistryOperations.DispositionRetryable, ExitRetryable);
                }
                if (request is not null)
                {
                    stdout.WriteLine(
                        $"run-study-cells: stop requested by {request.RequestedBy} ({request.Reason}); "
                        + "nothing runs on this Site, so this supervisor exits and stays down");
                    return End(store, office, "stopped", RegistryOperations.DispositionCompleted, ExitCompleted);
                }
            }
            // CHG: RunOnce waits for the worker. Renew the office while that
            // work is active, and revoke its cancellation token on lost authority.
            using var working = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            using var renewing = new CancellationTokenSource();
            var officeLost = false;
            var renewal = office is null ? Task.CompletedTask : Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.05, leaseSeconds / 3.0)), renewing.Token);
                        // CHG 2026-09-26 (slice W1): through the lease, an
                        // unanswered renewal does not cancel the training; a
                        // refusal or a lease run out still does (W1's limit:
                        // keeping a training past its supervisor is W3 and W4).
                        if (lease is not null)
                        {
                            var renewed = lease.Renew(store, office);
                            if ((renewed.Exit ?? lease.Tick().Exit) is not null)
                            {
                                officeLost = true;
                                working.Cancel();
                                return;
                            }
                            continue;
                        }
                        if (!store.RenewSupervisor(office, leaseSeconds).IsOk)
                        {
                            officeLost = true;
                            working.Cancel();
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) when (renewing.IsCancellationRequested) { }
                catch (HomeUnreachableException)
                {
                    officeLost = true;
                    working.Cancel();
                }
            });
            CycleReport report;
            GleamStudyRound.Outcome? rested = null;
            IReadOnlyList<GleamStudyRound.Notice> told = [];
            try
            {
                // CHG 2026-09-25: with a Gleam round every choice after the
                // reading is the round's (slices C4–C6 of the scheduling-loop
                // design); without one the cycle is the C# one, unchanged.
                if (round is null)
                {
                    report = RunOnce(store, worker, studyId, working.Token,
                        alreadyAttempted: serveInterval is null ? attempted : null,
                        siteName: siteName, maxAttempts: maxAttempts, implementedBy: implementedBy,
                        officeEpoch: office?.Epoch);
                }
                else
                {
                    (report, rested, told) = RunRoundOnce(store, worker, round, studyId, working.Token, siteName,
                        maxAttempts, office?.Epoch);
                }
            }
            finally
            {
                renewing.Cancel();
                renewal.GetAwaiter().GetResult();
            }
            if (officeLost)
            {
                stderr.WriteLine($"run-study-cells: Site {siteName} lost its supervisor renewal; active work was cancelled");
                return End(store, office, "office_lost", RegistryOperations.DispositionRetryable, ExitRetryable);
            }
            cycles++;
            completed += report.Completed;
            failed += report.Failed;
            foreach (var failure in report.Failures)
            {
                attempted.Add(failure.CellId);
                refusals.Add(failure);
                stderr.WriteLine($"cell {failure.CellName} failed: {failure.Sentence}");
            }
            // A notice is told once, when it happens. What the round rested on
            // is printed when it differs from the last one, so a serving loop
            // that claims nothing still says why, without saying it every cycle.
            foreach (var notice in told)
            {
                stdout.WriteLine("run-study-cells: " + notice.Sentence);
            }
            if (rested is not null && rested.Key != lastRested)
            {
                lastRested = rested.Key;
                foreach (var line in Summary(rested))
                {
                    stdout.WriteLine("run-study-cells: " + line);
                }
            }
            // CHG 2026-09-11 night (slice 6): a cancellation that lands while a
            // cycle runs ends the serve the way one that lands in the wait
            // does, as asked, not as idle; which of the two it was is timing.
            if (serveInterval is not null && cancellation.IsCancellationRequested)
            {
                stdout.WriteLine($"run-study-cells: stopped serving {label} as asked");
                return End(store, office, "cancelled", RegistryOperations.DispositionCompleted, ExitCompleted);
            }

            // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): an
            // install that had just recorded a source kept a one-shot run going
            // for one more cycle; nothing installs a source any more.
            if (report.Claimed == 0)
            {
                RegistryOperations.StudyView? head = null;
                if (studyId is { } id)
                {
                    var view = store.ReadStudy(id);
                    if (!view.TryGetValue(out head))
                    {
                        stderr.WriteLine($"run-study-cells: study {studyName} disappeared while running");
                        return End(store, office, "study_disappeared", RegistryOperations.DispositionFatal, ExitFatal);
                    }
                }
                // Serving: an idle cycle is a wait, not an ending. The command
                // stays and keeps reconciling, because a cell can become
                // claimable again without anybody asking — a supervisor
                // elsewhere goes silent, a paused study is set running, a Site
                // frees a slot. It ends only when the study is finished or an
                // operator has said pause or cancel; a supervisor of every
                // study ends only on a stop.
                if (serveInterval is { } interval && (head is null || !StudyIsSettled(head)) && !cancellation.IsCancellationRequested)
                {
                    try
                    {
                        // CHG: an idle polling interval cannot outlive the office lease.
                        var wait = office is null ? interval : TimeSpan.FromMilliseconds(
                            Math.Min(interval.TotalMilliseconds, Math.Max(50, leaseSeconds * 1000.0 / 3)));
                        // CHG 2026-09-25: a round that names the earliest moment
                        // something it waits for happens is read again then, when
                        // that comes sooner, and never sooner than a second from now.
                        if (rested?.Until is { } until)
                        {
                            wait = TimeSpan.FromMilliseconds(Math.Min(wait.TotalMilliseconds,
                                Math.Max(1000, (until - DateTimeOffset.UtcNow).TotalMilliseconds)));
                        }
                        Task.Delay(wait, cancellation).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        stdout.WriteLine($"run-study-cells: stopped serving {label} as asked");
                        return End(store, office, "cancelled", RegistryOperations.DispositionCompleted, ExitCompleted);
                    }
                    continue;
                }
                stdout.WriteLine(head is null
                    ? $"no runnable cell on Site {siteName} across every study: {cycles} cycle(s); {completed} completed, {failed} failed"
                    : Conclusion(head, report, studyName!, cycles, completed, failed, refusals));
                var idleExit = failed == 0 ? ExitCompleted : 4;
                return End(store, office, "idle", RegistryOperations.DispositionCompleted, idleExit);
            }
            stdout.WriteLine(
                $"cycle {cycles}: {report.Claimed} claimed, {report.Completed} completed, {report.Failed} failed, {report.Unplaced.Count} unplaced");
        }
        stdout.WriteLine(
            $"run-study-cells: stopped after {cycles} cycle(s) as asked; {completed} completed, {failed} failed");
        return End(store, office, "cycles_exhausted", RegistryOperations.DispositionCompleted, ExitCompleted);
    }

    /// <summary>Who this process is, for the office it claims: the facts a monitor prints and a successor names.</summary>
    public sealed record SupervisorIdentity(string Host, long Pid, string BinarySha256);

    /// <summary>The retired daemon's exit codes, and what systemd does with each: 0 stays down, 1 and 3 restart, 2 never restarts.</summary>
    public const int ExitCompleted = 0;
    public const int ExitRetryable = 1;
    public const int ExitTerminalConfiguration = 2;
    public const int ExitFatal = 3;

    /// <summary>Write the ending on the office row, when there is one, and hand the exit code through.</summary>
    static int End(IStudyStore store, RegistryOperations.SupervisorPermit? office, string outcome, string disposition, int exitCode)
    {
        if (office is not null)
        {
            store.EndSupervisor(office, outcome, disposition, exitCode);
        }
        return exitCode;
    }

    /// <summary>Nothing left for a server to wait for: every cell accepted, or an operator has stopped the study.</summary>
    static bool StudyIsSettled(RegistryOperations.StudyView head) =>
        head.AcceptedCellCount == head.CellCount || head.RequestedState != RegistryOperations.StateRun;

    static string Conclusion(
        RegistryOperations.StudyView head,
        StudyOrchestrator.CycleReport report,
        string studyName,
        int cycles,
        int completed,
        int failed,
        IReadOnlyList<CellFailure> refusals)
    {
        var tally = $"{cycles} cycle(s); {completed} completed, {failed} failed; "
                  + $"{head.AcceptedCellCount}/{head.CellCount} cells accepted";
        if (refusals.Count != 0)
        {
            // The first reason, not a count of them: a person who reads "13
            // failed" learns nothing, and a person who reads the sentence
            // usually knows what to fix.
            tally += $"; {refusals[0].CellName} failed with: {refusals[0].Sentence}";
        }
        if (head.RequestedState != RegistryOperations.StateRun)
        {
            return $"study {studyName} is {head.RequestedState}, so no cell was claimed: {tally}";
        }
        if (head.AcceptedCellCount == head.CellCount)
        {
            return $"study {studyName} is finished: {tally}";
        }
        if (report.Unplaced.Count != 0)
        {
            var because = report.Unplaced[0].Because;
            return $"study {studyName} has {report.Unplaced.Count} cell(s) no Site can take ({because}): {tally}";
        }
        return $"study {studyName} has cells left but claimed none this cycle: {tally}";
    }

    // CHG 2026-09-11 night (slice 5): "how many slots each Site is already
    // holding, read from the open attempts" moved into the store as
    // IStudyStore.BusySlots, so a supervisor on another machine asks the Home
    // rather than the file.

}
