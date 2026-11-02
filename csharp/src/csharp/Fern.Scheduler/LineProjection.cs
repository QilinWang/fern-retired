using System.Globalization;
using Fern.Registry;
using Microsoft.Data.Sqlite;

namespace Fern.Scheduler;

/// <summary>
/// One cell in the offer, in the position the store would hand it out.
///
/// <c>Waiting</c> is the store's own sentence when nothing can take it right now — every admitting
/// Site full, no Site holding its source, a supervisor absent. Being in the offer and being placeable
/// this second are different facts, and a queue that showed only the first would say a cell is next
/// while nothing is able to start it.
/// </summary>
/// CHG 2026-09-25 (slice P2): <c>Priority</c> is the rank the cell is offered at — its own, else its
/// study's, else normal — and <c>MovedLast</c> says a person moved it (or its study) to the back of
/// that rank.
public sealed record QueuedCell(int Position, string Study, string Cell, long Ordinal, string Dataset,
                                string Priority, bool MovedLast, string? Waiting);

/// <summary>One attempt open or running right now, and who is holding it.</summary>
public sealed record RunningCell(
    string Study, string Cell, string Site, long Ordinal, long Epoch, string LastProgress, string Remote, bool Silent);

/// <summary>
/// A cell that is neither accepted nor offered, and is waiting on something: a person to fix what
/// stopped it, a Site to hold its source, a supervisor to take the office.
///
/// An obstacle, in other words, which is what makes it different from a cell whose study was set
/// aside. Something could still change and let it run.
/// </summary>
public sealed record BlockedCell(string Study, string Cell, string State, string Waiting);

/// <summary>
/// A cell of a study that was asked to stop: it will not be claimed, and nothing is waiting on it.
///
/// Calling this blocked was wrong (owner, 2026-09-14). Nothing obstructs it; a person decided. It is
/// also not discarded in the sense of lost: a study set aside keeps every result already accepted,
/// and on this Home eight cancelled studies hold seven accepted cells between them. Which is why
/// nothing here deletes anything — a cancelled study is not an empty one.
/// </summary>
public sealed record SetAsideCell(string Study, string Cell, string State, string StudyState);

/// <summary>
/// A cell whose result the store has accepted: finished, and not coming back.
///
/// It is in no other band — not queued, not running, not blocked — so without this a reader cannot
/// tell a finished cell from one this Home never heard of, and a drawing of a study's chain would
/// leave its completed arms blank.
/// </summary>
public sealed record AcceptedCell(string Study, string Cell, string Site, long Ordinal);

/// <summary>A pause or a hold standing right now: who asked, over what, why, and what lifts it.</summary>
public sealed record StandingRequest(long RequestId, string Kind, string Scope, string RequestedBy, string Reason, string RequestedAt)
{
    /// <summary>The command that lifts this one, printed so a panel never has to compose it.</summary>
    public string Lift => $"release-request --request {RequestId}";
}

/// <summary>
/// One registered Site: what it can take, what it is taking, and whether the offer may reach it.
///
/// <c>Available</c> is about a standing request, not about capacity: a Site with a pause or a hold
/// over it takes nothing however free it is. Fullness is <c>Busy</c> against <c>Slots</c>, and the
/// two must not be run together — a Site can be free and forbidden, or busy and perfectly healthy.
/// </summary>
public sealed record SiteLoad(string Site, string Device, int Slots, int Busy, bool Available, string Supervisor,
                              string StopKind = "", long StopRequestId = 0, string StopScope = "")
{
    public bool Full => Busy >= Slots;

    /// <summary>
    /// A Site-scoped hold is what <c>disable-site</c> wrote, and <c>enable-site</c> is what lifts it.
    /// A pause, or a hold over every Site, is a standing request: releasing that request is the recovery.
    /// </summary>
    public bool StoppedBySiteHold =>
        StopKind == RegistryOperations.RequestHold
        && StopScope.StartsWith("site:", StringComparison.Ordinal);
}

/// <summary>
/// The line: what the store would run next, across every study, in its own order.
///
/// Before this, every read of a Registry Home needed a study name or a Site name. `monitor-study`
/// answers about one study and `monitor-site` about one machine, so nothing answered the question a
/// queue exists to answer — what is the order across everything, and what is stuck. This assembles
/// that from the same facts the scheduler decides on, and writes nothing.
///
/// It does not compute an order. `RegistryOperations.ListRunnableCells` already returns the offer in
/// the order the claim would make it — the rank (a cell's own, else its study's), then registration
/// order with any move to the back of the rank, then compilation order — so the position here is
/// that list's own position. A panel
/// that computed its own order would be a second scheduler, disagreeing with the real one at exactly
/// the moment it matters.
/// </summary>
public static class LineProjection
{
    public sealed record Line(
        string ReadAt,
        IReadOnlyList<QueuedCell> Queued,
        IReadOnlyList<RunningCell> Running,
        IReadOnlyList<AcceptedCell> Accepted,
        IReadOnlyList<BlockedCell> Blocked,
        IReadOnlyList<SetAsideCell> SetAside,
        IReadOnlyList<StandingRequest> Standing,
        IReadOnlyList<SiteLoad> Sites,
        IReadOnlyList<string> Studies)
    {
        public string Schema => "fern.registry_line.v1";
    }

    public static Line Read(SqliteConnection connection, long maxAttempts = RegistryOperations.DefaultMaxAttempts,
        ISchedulerDecision? placement = null)
    {
        // One snapshot: the offer, the cells elsewhere, the studies and the standing requests
        // are one moment. Two autocommit reads can place a cell in both queued and running if a
        // worker claims it between them, and the panel would then offer a control on a lie.
        using var transaction = connection.BeginTransaction(deferred: true);
        var studies = Studies(connection);
        var busy = BusyBySite(connection);
        var sites = ReadSites(connection, busy);
        var standing = Standings(connection);

        var runnable = RegistryOperations.ListRunnableCells(connection, null, maxAttempts);
        var offered = runnable.Select(cell => cell.CellId).ToHashSet();
        var configs = Configs(connection, runnable.Select(cell => cell.CellId));
        var order = RegistryOperations.ReadDispatchOrder(connection);
        var queued = new List<QueuedCell>();
        var position = 0;
        var supervisors = new Dictionary<long, RegistryOperations.SupervisorView?>();
        StudyProjection.OfferView? offer = null;
        foreach (var cell in runnable)
        {
            if (!studies.TryGetValue(cell.StudyId, out var study)) continue;
            offer ??= StudyProjection.ReadOffer(connection);
            queued.Add(new QueuedCell(++position, study.Name, cell.Name, cell.Ordinal, cell.Dataset,
                Priority(order, cell.StudyId, cell.CellId), MovedLast(order, cell.StudyId, cell.CellId),
                StudyProjection.WaitingReason(connection, study, cell.CellId,
                    configs.GetValueOrDefault(cell.CellId, "{}"), cell.RequirementsJson, cell.Ordinal,
                    offer, supervisors, placement)));
        }

        var (running, accepted, blocked, setAside) = Elsewhere(connection, studies, offered, placement);
        var names = studies.Values.Select(study => study.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();
        transaction.Commit();
        return new Line(DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            queued, running, accepted, blocked, setAside, standing, sites, names);
    }

    static Dictionary<long, RegistryOperations.StudyView> Studies(SqliteConnection connection)
    {
        var studies = new Dictionary<long, RegistryOperations.StudyView>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM studies";
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        reader.Close();
        foreach (var id in ids)
        {
            // A study the Home cannot read is left out rather than guessed at; its cells then
            // appear in no band, which is visible, where a half-read study would not be.
            if (RegistryOperations.ReadStudy(connection, id).TryGetValue(out var study)) studies[id] = study;
        }
        return studies;
    }

    // A person's priority on the cell, else on the study, else normal: the rank the offer orders by.
    static string Priority(RegistryOperations.DispatchOrder order, long studyId, long cellId) =>
        order.Priorities.TryGetValue(RegistryOperations.CellScope(cellId), out var cell) ? cell
        : order.Priorities.TryGetValue(RegistryOperations.StudyScope(studyId), out var study) ? study : "normal";

    static bool MovedLast(RegistryOperations.DispatchOrder order, long studyId, long cellId) =>
        order.Moves.ContainsKey(RegistryOperations.CellScope(cellId))
        || order.Moves.ContainsKey(RegistryOperations.StudyScope(studyId));

    /// <summary>The configuration of each named cell, which the placement decision reads.</summary>
    static Dictionary<long, string> Configs(SqliteConnection connection, IEnumerable<long> cellIds)
    {
        var wanted = cellIds.ToHashSet();
        var configs = new Dictionary<long, string>();
        if (wanted.Count == 0) return configs;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, config_json FROM cells";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (wanted.Contains(id)) configs[id] = reader.GetString(1);
        }
        return configs;
    }

    static Dictionary<long, int> BusyBySite(SqliteConnection connection)
    {
        var busy = new Dictionary<long, int>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT site_id, COUNT(*) FROM attempts WHERE state IN ('opened', 'running') GROUP BY site_id";
        using var reader = command.ExecuteReader();
        while (reader.Read()) busy[reader.GetInt64(0)] = reader.GetInt32(1);
        return busy;
    }

    /// <summary>
    /// Every registered Site with its load and whether the offer may reach it.
    ///
    /// Unavailable is a fact about a standing request, not about the machine: a Site with a pause or
    /// a hold over it takes no new cell however healthy it is, and a panel that showed only capacity
    /// would say a Site is free while nothing is being given to it.
    /// </summary>
    static IReadOnlyList<SiteLoad> ReadSites(SqliteConnection connection, IReadOnlyDictionary<long, int> busy)
    {
        var loads = new List<SiteLoad>();
        var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        // A pause over every Site is a fact about each of them. Checking only the Site's own
        // scope left "stop new claims everywhere" looking as if every machine was still taking cells.
        var pauseAll = RegistryOperations.PendingRequests(connection, RegistryOperations.RequestPause, RegistryOperations.ScopeAll);
        var holdAll = RegistryOperations.PendingRequests(connection, RegistryOperations.RequestHold, RegistryOperations.ScopeAll);
        foreach (var site in RegistryOperations.ListSites(connection))
        {
            var scope = RegistryOperations.SiteScope(site.SiteId);
            var siteHold = RegistryOperations.PendingRequests(connection, RegistryOperations.RequestHold, scope);
            var sitePause = RegistryOperations.PendingRequests(connection, RegistryOperations.RequestPause, scope);
            // The Site-specific request is the one the Site's own button can lift without
            // surprising every other machine; a standing request over everything is next.
            var stop = siteHold.FirstOrDefault() ?? sitePause.FirstOrDefault()
                ?? holdAll.FirstOrDefault() ?? pauseAll.FirstOrDefault();
            var offer = SchedulerDecision.ReadOffer(site.SiteId, site.Name, site.CapabilitiesJson, site.CapacityJson);
            var supervisor = RegistryOperations.ReadSupervisor(connection, site.SiteId);
            loads.Add(new SiteLoad(site.Name, offer.Device, offer.Slots,
                busy.TryGetValue(site.SiteId, out var count) ? count : 0, stop is null,
                supervisor is null ? "nobody has held this Site's office"
                    : supervisor.HoldsOfficeAt(now) ? $"{supervisor.Host}/{supervisor.Pid} holds the office"
                    : supervisor.IsUnconfirmedAt(now) ? $"{supervisor.Host}/{supervisor.Pid} is unconfirmed since {supervisor.LeaseUntil}"
                    : $"{supervisor.Host}/{supervisor.Pid} ended {supervisor.EndedAt}",
                stop?.Kind ?? "", stop?.RequestId ?? 0, stop?.Scope ?? ""));
        }
        return loads;
    }

    static IReadOnlyList<StandingRequest> Standings(SqliteConnection connection)
    {
        var standing = new List<StandingRequest>();
        foreach (var kind in new[] { RegistryOperations.RequestPause, RegistryOperations.RequestHold })
        {
            foreach (var request in RegistryOperations.PendingRequests(connection, kind, AllScopes(connection)))
            {
                standing.Add(new StandingRequest(request.RequestId, request.Kind, request.Scope,
                    request.RequestedBy, request.Reason, request.RequestedAt));
            }
        }
        return standing.OrderBy(request => request.RequestId).ToList();
    }

    /// <summary>Every scope a standing request could name, so none is missed by asking about one study.</summary>
    static string[] AllScopes(SqliteConnection connection)
    {
        var scopes = new List<string> { RegistryOperations.ScopeAll };
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT scope FROM operator_requests
            WHERE consumed_at IS NULL AND released_at IS NULL
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read()) scopes.Add(reader.GetString(0));
        return scopes.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// What is running, and what is neither running, accepted, nor in the offer.
    ///
    /// The second half is the one that matters: a cell missing from the offer has a reason, the
    /// store computes it, and `StudyProjection.WaitingReason` is that computation. This calls it
    /// rather than restating it, because two accounts of why a cell will not run is exactly the
    /// disagreement a control panel must never have.
    /// </summary>
    static (IReadOnlyList<RunningCell> Running, IReadOnlyList<AcceptedCell> Accepted,
            IReadOnlyList<BlockedCell> Blocked, IReadOnlyList<SetAsideCell> SetAside)
        Elsewhere(SqliteConnection connection, IReadOnlyDictionary<long, RegistryOperations.StudyView> studies,
                  HashSet<long> offered, ISchedulerDecision? placement)
    {
        var running = new List<RunningCell>();
        var accepted = new List<AcceptedCell>();
        var blocked = new List<BlockedCell>();
        var setAside = new List<SetAsideCell>();
        var supervisors = new Dictionary<long, RegistryOperations.SupervisorView?>();
        StudyProjection.OfferView? offer = null;

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.id, c.study_id, c.name, c.ordinal, c.config_json, c.requirements_json,
                   CASE WHEN c.accepted_attempt_id IS NOT NULL THEN 'accepted'
                        ELSE COALESCE(a.state, 'waiting') END,
                   COALESCE(s.name, '-'), COALESCE(a.ordinal, 0), COALESCE(a.fencing_epoch, 0),
                   COALESCE(a.last_progress_at, a.started_at, '-'), COALESCE(a.remote_execution_id, '')
            FROM cells c
            LEFT JOIN attempts a ON a.id = COALESCE(
                c.accepted_attempt_id,
                (SELECT id FROM attempts WHERE cell_id = c.id ORDER BY ordinal DESC LIMIT 1))
            LEFT JOIN sites s ON s.id = a.site_id
            ORDER BY c.study_id, c.ordinal
            """;
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            var cellId = rows.GetInt64(0);
            if (!studies.TryGetValue(rows.GetInt64(1), out var study)) continue;
            var cell = rows.GetString(2);
            var state = rows.GetString(6);
            if (state is RegistryOperations.AttemptOpened or RegistryOperations.AttemptRunning)
            {
                var lastProgress = rows.GetString(10);
                running.Add(new RunningCell(study.Name, cell, rows.GetString(7), rows.GetInt64(8), rows.GetInt64(9),
                    lastProgress, rows.GetString(11), StudyProjection.IsSilent(lastProgress)));
                continue;
            }
            if (state == "accepted")
            {
                accepted.Add(new AcceptedCell(study.Name, cell, rows.GetString(7), rows.GetInt64(8)));
                continue;
            }
            if (offered.Contains(cellId)) continue;
            // A study asked to stop is a decision, not an obstacle. Its unclaimed cells wait on
            // nothing and no reason of the store's would tell a reader anything but that.
            if (study.RequestedState != RegistryOperations.StateRun)
            {
                setAside.Add(new SetAsideCell(study.Name, cell, state, study.RequestedState));
                continue;
            }
            offer ??= StudyProjection.ReadOffer(connection);
            var why = StudyProjection.WaitingReason(
                connection, study, cellId, rows.GetString(4), rows.GetString(5), rows.GetInt64(3), offer, supervisors, placement);
            blocked.Add(new BlockedCell(study.Name, cell, state, why ?? "no reason recorded"));
        }
        return (running, accepted, blocked, setAside);
    }
}
