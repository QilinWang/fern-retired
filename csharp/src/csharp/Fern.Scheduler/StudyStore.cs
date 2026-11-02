using Fern.Registry;
using Microsoft.Data.Sqlite;

namespace Fern.Scheduler;

/// <summary>
/// The one cell's registration a worker needs to build its request: its
/// configuration, its study's compiled document, and its own seed.
/// </summary>
public sealed record RegisteredCell(string ConfigJson, string CompiledStudyJson, long Seed);

/// <summary>
/// One cell with no accepted attempt, as a scheduling round reads it: what it
/// declares, its Study's name and requested state, how many attempts it has
/// had, and its last attempt with that attempt's last record.
///
/// The round explains every such cell, not only the ones the claim would take,
/// so that a cell kept out of the offer by a failure, a pause or a spent budget
/// is still named with its reason. The last record's document is passed as it
/// was written: reading a failure's class out of it is the round's, and a
/// record that names no class stays unreadable rather than becoming
/// "transient".
/// </summary>
public sealed record UnfinishedCell(
    long CellId, long StudyId, string StudyName, string StudyState, long SourceId, long Ordinal, string Name,
    string ConfigJson, string RequirementsJson, long Tries,
    long? AttemptId, long? AttemptSiteId, long? FencingEpoch, string? AttemptState,
    string? StartedAt, string? LastProgressAt, string? EndedAt,
    string? LastEventKind, string? LastEventDetailJson, string? LastEventAt,
    string? LastTrainingProgressAt = null, string? RemoteExecutionId = null)
{
    // CHG: this reading's last training-progress timestamp is selected by
    // this persistent event kind, independently of the execution worker.
    public const string TrainingProgressEvent = "training_progress";
}

/// <summary>
/// Every store operation the study loop and the local worker make, and no
/// other: the seam a supervisor on another machine reaches the Home through.
///
/// CHG 2026-09-11 night (slice 5 of the cell port, design note
/// docs/design/original/by_ai/2026-09-11-restore-the-behavior-port-the-input.md,
/// section 6 and the owner's ruling for pull). The Home is one SQLite file on
/// one machine and must not be opened over a network filesystem. A supervisor
/// on that machine uses <see cref="SqliteStudyStore"/>, the file itself; a
/// supervisor on the pod uses <see cref="HomeClient"/>, the same operations
/// as JSON over HTTP against <see cref="HomeService"/>, which is the one
/// process that opens the file. Every write still carries the fencing epoch
/// it was claimed with, so the two paths are one rule.
/// </summary>
public interface IStudyStore
{
    IReadOnlyList<RegistryOperations.RunnableCell> ListRunnableCells(long? studyId, long maxAttempts);
    IReadOnlyList<RegistryOperations.Site> ListSites();
    IReadOnlyList<RegistryOperations.OperatorRequest> PendingRequests(string kind, IReadOnlyList<string> scopes);
    RegistryResult<RegistryOperations.StudyView> ReadStudy(long studyId);
    RegisteredCell? ReadCell(long cellId);
    /// <summary>
    /// Every cell with no accepted attempt of <paramref name="studyId"/>, or of
    /// every Study in state run when it is null, and every cell anywhere whose
    /// last attempt is still open: those hold Site slots whichever Study they
    /// belong to.
    /// </summary>
    IReadOnlyList<UnfinishedCell> ListUnfinishedCells(long? studyId);
    /// <summary>
    /// Each registered Site's most recent supervisor, for the Sites that have
    /// ever had one: whether it still holds the office is read from its lease
    /// and its ending. A Site missing from the list has never been supervised.
    /// </summary>
    IReadOnlyList<RegistryOperations.SupervisorView> ListSiteSupervisors();
    /// <summary>Every priority and every move to the back of a rank, keyed by scope (slice P2).</summary>
    RegistryOperations.DispatchOrder ReadDispatchOrder();
    /// <summary>The compiler that compiled a study, or null when none is recorded (slice M2).</summary>
    RegistryOperations.StudyCompiler? ReadStudyCompiler(long studyId);
    /// <summary>
    /// A registered source's commit and declared files, for a Site whose cells'
    /// code comes from the working tree or the commit (2026-09-26,
    /// source-roots-stop-being-full-copies slice 2). A remote supervisor reads it over HTTP.
    /// </summary>
    RegistryOperations.SourceDeclaration? ReadSource(long sourceId);
    /// <summary>
    /// Why the machine holding the repository cannot give a cell of this source
    /// its code on this Site, or null when it can: asked by a remote worker
    /// before a claim (source-roots-stop-being-full-copies, slice 3).
    /// </summary>
    string? CannotGiveCode(long siteId, long sourceId);
    /// <summary>
    /// One open attempt's code, made on the machine holding the repository the
    /// way the attempt's Site declares, packed for a remote worker to unpack,
    /// run and delete (slice 3). Fenced by the attempt's epoch.
    /// </summary>
    RegistryResult<CellCode.Package> PackCellCode(long attemptId, long fencingEpoch);
    /// <summary>Every Site's recent failures, as the claim reads them (slices M1 and M3).</summary>
    IReadOnlyList<RegistryOperations.SiteFailure> ListSiteFailures();
    IReadOnlyDictionary<long, int> BusySlots();
    long? FindStudy(string name);
    long StudyAttemptBudget(long studyId);

    RegistryResult<IReadOnlyList<RegistryOperations.ReconciledAttempt>> ReconcileSilentAttempts(long? studyId);
    /// <remarks>CHG 2026-09-26 (slice W1): <paramref name="officeEpoch"/>, the claiming supervisor's office; see RegistryOperations.TryStartAttempt.</remarks>
    RegistryResult<RegistryOperations.StartedAttempt> TryStartAttempt(
        long cellId, long siteId, string requestJson, long maxAttempts, long? officeEpoch = null);
    RegistryResult<long> RenewAttempt(long attemptId, long fencingEpoch, string? remoteExecutionId);
    RegistryResult<long> RecordEvent(long attemptId, long fencingEpoch, string kind, string? phase, string detailJson);
    RegistryResult<long> FinishAttempt(long attemptId, long fencingEpoch, string resultJson);
    RegistryResult<long> FailAttempt(long attemptId, long fencingEpoch, string state, string detailJson);
    RegistryResult<RegistryOperations.SupervisorPermit> StartSupervisor(long siteId, string host, long pid, string binarySha256, long leaseSeconds);
    RegistryResult<string> RenewSupervisor(RegistryOperations.SupervisorPermit permit, long leaseSeconds);
    RegistryResult<long> EndSupervisor(RegistryOperations.SupervisorPermit permit, string outcome, string disposition, long exitCode);
    RegistryResult<RegistryOperations.OperatorRequest?> ConsumeStop(RegistryOperations.SupervisorPermit permit);
}

/// <summary>The store as the file: every call forwards to <see cref="RegistryOperations"/> over the open writer.</summary>
public sealed class SqliteStudyStore(RegistryWriter writer) : IStudyStore
{
    public RegistryWriter Writer { get; } = writer;

    // CHG: heartbeat and supervisor renewal share one connection, serialized per operation.
    T Access<T>(Func<T> action) { lock (Writer.Connection) return action(); }

    public IReadOnlyList<RegistryOperations.RunnableCell> ListRunnableCells(long? studyId, long maxAttempts) =>
        Access(() => RegistryOperations.ListRunnableCells(Writer.Connection, studyId, maxAttempts));

    public IReadOnlyList<RegistryOperations.Site> ListSites() => Access(() => RegistryOperations.ListSites(Writer.Connection));


    public IReadOnlyList<RegistryOperations.OperatorRequest> PendingRequests(string kind, IReadOnlyList<string> scopes) =>
        Access(() => RegistryOperations.PendingRequests(Writer.Connection, kind, [.. scopes]));

    public RegistryResult<RegistryOperations.StudyView> ReadStudy(long studyId) =>
        Access(() => RegistryOperations.ReadStudy(Writer.Connection, studyId));

    public RegisteredCell? ReadCell(long cellId)
    {
        lock (Writer.Connection)
        {
            using var command = Writer.Connection.CreateCommand();
            command.CommandText =
                """
                SELECT c.config_json, s.compiled_study_json, c.seed
                FROM cells c JOIN studies s ON s.id = c.study_id
                WHERE c.id = $id
                """;
            command.Parameters.AddWithValue("$id", cellId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? new RegisteredCell(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)) : null;
        }
    }

    public IReadOnlyDictionary<long, int> BusySlots()
    {
        lock (Writer.Connection)
        {
            using var command = Writer.Connection.CreateCommand();
            command.CommandText =
                "SELECT site_id, COUNT(*) FROM attempts WHERE state IN ('opened', 'running') GROUP BY site_id";
            var busy = new Dictionary<long, int>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                busy[reader.GetInt64(0)] = reader.GetInt32(1);
            }
            return busy;
        }
    }

    public IReadOnlyList<UnfinishedCell> ListUnfinishedCells(long? studyId)
    {
        lock (Writer.Connection)
        {
            using var command = Writer.Connection.CreateCommand();
            command.CommandText =
                """
                SELECT c.id, c.study_id, p.name, p.requested_state, p.source_id, c.ordinal, c.name,
                       c.config_json, c.requirements_json,
                       COUNTED_ATTEMPTS,
                       a.id, a.site_id, a.fencing_epoch, a.state, a.started_at, a.last_progress_at, a.ended_at,
                       e.kind, e.detail_json, e.at,
                       (SELECT MAX(at) FROM events WHERE attempt_id = a.id AND kind = $training),
                       a.remote_execution_id
                FROM cells c
                JOIN studies p ON p.id = c.study_id
                LEFT JOIN attempts a ON a.id = (SELECT id FROM attempts WHERE cell_id = c.id ORDER BY ordinal DESC LIMIT 1)
                LEFT JOIN events e ON e.id = (SELECT MAX(id) FROM events WHERE attempt_id = a.id)
                WHERE c.accepted_attempt_id IS NULL
                  AND (($study IS NULL AND p.requested_state = 'run')
                       OR c.study_id = $study
                       OR a.state IN ('opened', 'running'))
                ORDER BY c.study_id, c.ordinal
                """;
            // CHG 2026-09-25 (slice M1): the tries the round compares with the budget are the counted ones.
            command.CommandText = command.CommandText.Replace(
                "COUNTED_ATTEMPTS", RegistryOperations.CountedAttempts("c.id"), StringComparison.Ordinal);
            command.Parameters.AddWithValue("$study", studyId is { } id ? id : DBNull.Value);
            // CHG 2026-09-26 (slice W3): the trainer's last epoch report, not the heartbeat.
            command.Parameters.AddWithValue("$training", UnfinishedCell.TrainingProgressEvent);
            var cells = new List<UnfinishedCell>();
            using var reader = command.ExecuteReader();
            string? Text(int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
            long? Number(int column) => reader.IsDBNull(column) ? null : reader.GetInt64(column);
            while (reader.Read())
            {
                cells.Add(new UnfinishedCell(
                    reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
                    reader.GetInt64(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                    reader.GetInt64(9), Number(10), Number(11), Number(12), Text(13), Text(14), Text(15), Text(16),
                    Text(17), Text(18), Text(19), Text(20), Text(21)));
            }
            return cells;
        }
    }

    public IReadOnlyList<RegistryOperations.SupervisorView> ListSiteSupervisors() =>
        Access(() => RegistryOperations.ListSites(Writer.Connection)
            .Select(site => RegistryOperations.ReadSupervisor(Writer.Connection, site.SiteId))
            .OfType<RegistryOperations.SupervisorView>()
            .ToList());

    public RegistryOperations.DispatchOrder ReadDispatchOrder() =>
        Access(() => RegistryOperations.ReadDispatchOrder(Writer.Connection));

    public RegistryOperations.StudyCompiler? ReadStudyCompiler(long studyId) =>
        Access(() => RegistryOperations.ReadStudyCompiler(Writer.Connection, studyId));


    public RegistryOperations.SourceDeclaration? ReadSource(long sourceId) =>
        Access(() => RegistryOperations.ReadSource(Writer.Connection, sourceId));

    // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 3): this store
    // is the Home's, on the machine that holds the repository, so it makes the
    // code a remote worker asks for. Created on first use; it caches only which
    // commits the repository holds and which sources were read.
    CellCode? _homeCode;
    CellCode HomeCode => _homeCode ??= new CellCode(this, Path.GetTempPath(), codeFromHome: false);

    public string? CannotGiveCode(long siteId, long sourceId)
    {
        if (ListSites().FirstOrDefault(site => site.SiteId == siteId) is not { } site)
        {
            return $"Site {siteId} is not registered";
        }
        var declared = RegistryOperations.CodeOf(site.CapabilitiesJson);
        if (!declared.TryGetValue(out var code))
        {
            declared.TryGetRefusal(out var refusal);
            return $"Site {site.Name}: {refusal.Detail}";
        }
        return HomeCode.CannotGive(siteId, code, sourceId);
    }

    public RegistryResult<CellCode.Package> PackCellCode(long attemptId, long fencingEpoch)
    {
        var view = Access(() => RegistryOperations.ReadOpenAttemptCode(Writer.Connection, attemptId, fencingEpoch));
        if (!view.TryGetValue(out var attempt))
        {
            view.TryGetRefusal(out var refusal);
            return RegistryResult<CellCode.Package>.Rejected(refusal);
        }
        var declared = RegistryOperations.CodeOf(attempt.SiteCapabilitiesJson);
        if (!declared.TryGetValue(out var code))
        {
            declared.TryGetRefusal(out var refusal);
            return RegistryResult<CellCode.Package>.Rejected(refusal);
        }
        if (ReadSource(attempt.SourceId) is not { } source)
        {
            return RegistryResult<CellCode.Package>.Rejected(new RegistryRefusal("refused", "not_found",
                $"source {attempt.SourceId} of attempt {attemptId} is not registered"));
        }
        var packed = HomeCode.Pack(code, source);
        return packed.IsOk
            ? RegistryResult<CellCode.Package>.Ok(packed.Value)
            : RegistryResult<CellCode.Package>.Rejected(new RegistryRefusal("refused", "invalid_request", packed.Refusal));
    }

    public IReadOnlyList<RegistryOperations.SiteFailure> ListSiteFailures() =>
        Access(() => RegistryOperations.ListSiteFailures(Writer.Connection));

    public long? FindStudy(string name)
    {
        lock (Writer.Connection)
        {
            using var command = Writer.Connection.CreateCommand();
            command.CommandText = "SELECT id FROM studies WHERE name = $name ORDER BY revision DESC LIMIT 1";
            command.Parameters.AddWithValue("$name", name);
            var found = command.ExecuteScalar();
            return found is null or DBNull ? null : Convert.ToInt64(found, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public long StudyAttemptBudget(long studyId) => Access(() => Fern.Scheduler.StudyAttemptBudget.Of(Writer.Connection, studyId));

    public RegistryResult<IReadOnlyList<RegistryOperations.ReconciledAttempt>> ReconcileSilentAttempts(long? studyId) =>
        Access(() => RegistryOperations.ReconcileSilentAttempts(Writer, studyId));

    public RegistryResult<RegistryOperations.StartedAttempt> TryStartAttempt(
        long cellId, long siteId, string requestJson, long maxAttempts, long? officeEpoch = null) =>
        Access(() => RegistryOperations.TryStartAttempt(Writer, cellId, siteId, requestJson, maxAttempts, officeEpoch));

    public RegistryResult<long> RenewAttempt(long attemptId, long fencingEpoch, string? remoteExecutionId) =>
        Access(() => RegistryOperations.RenewAttempt(Writer, attemptId, fencingEpoch, remoteExecutionId));

    public RegistryResult<long> RecordEvent(long attemptId, long fencingEpoch, string kind, string? phase, string detailJson) =>
        Access(() => RegistryOperations.RecordEvent(Writer, attemptId, fencingEpoch, kind, phase, detailJson));

    public RegistryResult<long> FinishAttempt(long attemptId, long fencingEpoch, string resultJson) =>
        Access(() => RegistryOperations.FinishAttempt(Writer, attemptId, fencingEpoch, resultJson));

    public RegistryResult<long> FailAttempt(long attemptId, long fencingEpoch, string state, string detailJson) =>
        Access(() => RegistryOperations.FailAttempt(Writer, attemptId, fencingEpoch, state, detailJson));

    public RegistryResult<RegistryOperations.SupervisorPermit> StartSupervisor(long siteId, string host, long pid, string binarySha256, long leaseSeconds) =>
        Access(() => RegistryOperations.StartSupervisor(Writer, siteId, host, pid, binarySha256, leaseSeconds));

    public RegistryResult<string> RenewSupervisor(RegistryOperations.SupervisorPermit permit, long leaseSeconds) =>
        Access(() => RegistryOperations.RenewSupervisor(Writer, permit, leaseSeconds));

    public RegistryResult<long> EndSupervisor(RegistryOperations.SupervisorPermit permit, string outcome, string disposition, long exitCode) =>
        Access(() => RegistryOperations.EndSupervisor(Writer, permit, outcome, disposition, exitCode));

    public RegistryResult<RegistryOperations.OperatorRequest?> ConsumeStop(RegistryOperations.SupervisorPermit permit) =>
        Access(() => RegistryOperations.ConsumeStop(Writer, permit));
}
