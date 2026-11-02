using System.Text.Json;
using System.Text.Json.Nodes;
using Fern.Experiment;
using Fern.Registry;
using Fern.Site;
using Fern.Install;

namespace Fern.Scheduler;

/// <summary>
/// Runs one cell of a registered study on THIS machine, through the Python
/// study-cell runtime, and records how it ended.
///
/// It is the production <see cref="StudyOrchestrator.ICellWorker"/>. Until it
/// existed the new store could register and schedule a cell but not run one,
/// so the old Stage layer could not retire: a capability table with "absent"
/// against "run a registered cell" is a partial cut, not a finished one.
///
/// What it does: build the cell's request with
/// <see cref="StudyCellRequest"/> from the compiled study the registration
/// stored, write it beside the frozen source, run
/// <c>python -m fern_stage_runtime run --request … --workspace …</c> with the
/// environment that route sets, read the runtime's report, and report it. The
/// process is launched through <see cref="WorkerEnvironmentPreparation.IHost"/>,
/// which is <see cref="BoundedProbe"/>: it caps output bytes, enforces a
/// deadline and kills the whole tree on overrun. A trainer that floods stdout
/// must not be able to grow the supervisor without limit.
/// </summary>
/// <remarks>
/// CHG 2026-09-11: the request was a hand-written <c>fern.worker_request.v1</c>
/// document sent to <c>python -m fern_train.worker</c>. No Python accepted that
/// schema — <c>fern_train.worker</c> serves the retired Rust run-packet
/// contract — so every cell this route launched was refused before it trained,
/// and the tests never saw it because they answer with a fake process. The one
/// Python door that trains a registered study cell is the
/// <c>fern.study.run_cell</c> handler, and the document it reads is the one
/// <see cref="StudyCellRequest"/> writes: the card, the cell's OWN seed, the
/// device, the configuration with the identity the compiler minted, the
/// declared dataset files with their digests, and the home compile's evidence.
/// </remarks>
public sealed class LocalCellWorker : StudyOrchestrator.ICellWorker, StudyOrchestrator.ICodeCheck
{
    /// <summary>The module the runtime is entered through; the same one a Site enters.</summary>
    public const string PythonModule = "fern_stage_runtime";

    /// <summary>The Python handler that trains one registered study cell.</summary>
    public const string Handler = "fern.study.run_cell";

    /// <summary>The envelope the runtime reads, and the protocol it serves.</summary>
    public const string StageRequestSchema = "fern.stage_runner_request.v1";
    public const string StageProtocol = "fern.stage_runner.v1";

    /// <summary>The one output port a study cell declares, and the results document it is.</summary>
    public const string UnitResultsPort = "unit_results";
    // CHG: persistent result declarations belong to StudyCollect, which still
    // reads those documents after this execution worker retires.

    /// <summary>The file the supervisor overwrites each beat, so liveness is answered by a read, not by a process hunt.</summary>
    public const string HeartbeatFile = "heartbeat.json";
    public const string HeartbeatSchema = "fern.cell_heartbeat.v1";

    /// <summary>Where the request, the report and the unit's results sit inside one attempt's workspace.</summary>
    public const string RequestFile = "request/stage_runner_request.v1.json";
    public const string CellRequestFile = "inputs/study_cell_request.json";
    public const string ReportFile = "report/stage_runner_report.v1.json";

    // CHG: execution identity lives in metadata and filenames; all attempts
    // share the configured artifact directory without sharing mutable files.
    public static string ArtifactPrefix(StudyOrchestrator.ClaimedCell cell) =>
        $"study-{cell.StudyId}.cell-{cell.CellId}.attempt-{cell.AttemptId}.epoch-{cell.FencingEpoch}.";

    public static string ArtifactName(StudyOrchestrator.ClaimedCell cell, string file) =>
        ArtifactPrefix(cell) + file.Replace('/', '.');

    /// <summary>Where this worker runs and what it runs with.</summary>
    /// CHG 2026-09-11 night (slice 6, row 31): the source root was a field here ("the frozen
    /// source snapshot: what the cell trains from, verified before it got here"); it is the
    /// cell's now, handed by the claim from the root install-study-source recorded for this Site.
    /// CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): the claim hands over the
    /// Site's repository, and <see cref="CellCode"/> makes the directory the cell runs in.
    /// <param name="Python">The interpreter of the prepared environment.</param>
    /// <param name="FernRunBinary">The installed host the trainer calls back into.</param>
    /// <param name="FernCompileBinary">The Go card compiler, which mints a configuration's identity (Python asks it).</param>
    /// <param name="WorkRoot">Where each attempt's request and result files are written.</param>
    /// <param name="Timeout">How long one cell may take before the tree is killed. CHG 2026-09-26 (slice W3): no longer; see <see cref="NoDeadlineMs"/>.</param>
    /// <param name="ResultsRoot">
    /// The selected results directory this machine writes into, or empty when it has none to
    /// name. When it is present the cell's results document is saved into the science store under
    /// that root the moment the cell ends, and what comes back is a reference — where it went and
    /// the digest of what was saved — instead of the document itself. When it is absent the
    /// runtime hands the document back and this worker records it whole, which is what a remote
    /// Site that cannot reach the results tree does.
    /// </param>
    /// <param name="CheckpointStore">
    /// The Site's checkpoint store root — <c>store/</c> beside the Registry Home, the directory
    /// <c>install-control-plane</c> creates. A cell whose card evaluates a checkpoint BY NAME reads
    /// the store's receipt for the bytes that name is; a machine with no store still names its
    /// intended path, so the refusal a cell gets is "no such checkpoint" and not a missing field.
    /// </param>
    public sealed record Placement(
        string Python,
        string FernRunBinary,
        string FernCompileBinary,
        string WorkRoot,
        TimeSpan Timeout,
        string CheckpointStore,
        string ResultsRoot = "",
        // CHG 2026-09-11 night (slice 4 of the cell port, row 20): drive the
        // warm resident worker by its folder inbox instead of one process per
        // cell. Off until the live canary on the 4070 says the default flips.
        bool WarmWorker = false,
        // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): SourceCompilers,
        // the --source-compiler pairs of a source root and a compiler, is retired with
        // installed sources; a cell gets its study's own compiler (CompilerFor).
        IReadOnlyDictionary<string, string>? RecoveryPrefixes = null,
        // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 3): this
        // supervisor reaches the Registry through the Home, on another machine,
        // so a working-tree or commit cell's code is asked of the Home.
        bool CodeFromHome = false);

    /// <summary>The largest result a cell may write. A trainer that floods is a refusal, not a memory leak.</summary>
    public const long MaximumResultBytes = 8L * 1024 * 1024;

    /// <summary>
    /// How long one cell may take before its process tree is killed. The default
    /// `run-study-cells --cell-timeout-seconds` reads, declared here beside the
    /// worker that enforces it so the number lives in one place.
    /// </summary>
    public static readonly TimeSpan DefaultCellTimeout = TimeSpan.FromHours(6);

    /// <summary>
    /// CHG 2026-09-26 (slice W3 of leases-and-watching-a-running-cell; owner:
    /// a training's own time budget "只能警告永远不能直接删", "直接杀真的不行"):
    /// the time a trainer is given before anything ends it, on every path
    /// (the process this worker starts, a resident worker's cell, the Gleam
    /// watcher): none. The Site's <c>--cell-timeout-seconds</c> is a budget
    /// the Home warns about, once, and nothing more. A training ends when it
    /// ends, or when a person cancels it.
    /// </summary>
    public const long NoDeadlineMs = long.MaxValue;

    /// <summary>
    /// How often the supervisor says it is still alive while the trainer runs.
    ///
    /// WHY IT EXISTS. `last_progress_at` was stamped when the cell was claimed
    /// and again when it ended, and nothing in between: a six-hour cell and a
    /// supervisor killed one minute in looked identical to every reader. That
    /// made `retry-cell`'s silence bound meaningless — the only safe bound would
    /// have been longer than the longest cell. With a heartbeat,
    /// `last_progress_at` means what its name says: the supervisor was alive at
    /// that time, and silence longer than a few beats means it is not.
    ///
    /// It is the SUPERVISOR's liveness, not the trainer's. A Python process wedged
    /// with no output still has a supervisor renewing for it; what the beat rules
    /// out is a supervisor that is gone, which is the case that strands a cell.
    ///
    /// The cadence itself is <see cref="RegistryOperations.HeartbeatInterval"/>:
    /// the store owns it, because the store is what counts fresh attempts against
    /// a Site's capacity and what `retry-cell` asks whether an attempt is stranded.
    /// </summary>
    public static TimeSpan HeartbeatInterval => RegistryOperations.HeartbeatInterval;

    /// <summary>
    /// Where the attempt runs, written on it at its first beat as its remote
    /// execution id: this supervisor's host and process id, which is what a
    /// monitor prints and what a person kills. CHG 2026-09-11 night (slice 5):
    /// under pull every machine's supervisor is remote to the Home, so the
    /// column that named a pushed session's handle names the machine and the
    /// process holding the cell.
    /// </summary>
    public static string RunningHere { get; } = $"{System.Environment.MachineName}/{System.Environment.ProcessId}";

    /// <summary>How much of a failed process's stderr travels into the Registry, in characters.</summary>
    public const int StderrTailCharacters = 2000;

    readonly Placement _placement;
    readonly WorkerEnvironmentPreparation.IHost _host;
    readonly IResidentWorkerHost _resident;
    readonly IWarmCellWatch _watch;
    readonly IStudyStore _store;
    readonly TimeSpan _heartbeat;
    // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 2): where each
    // claimed cell's code comes from on this Site, and the directory it runs in.
    readonly CellCode _code;

    public LocalCellWorker(
        IStudyStore store,
        Placement placement,
        WorkerEnvironmentPreparation.IHost? host = null,
        TimeSpan? heartbeat = null,
        IResidentWorkerHost? resident = null,
        IWarmCellWatch? watch = null)
    {
        _store = store;
        _placement = placement;
        _host = host ?? new WorkerEnvironmentPreparation.ExecutionHost();
        _resident = resident ?? new ResidentWorkerHost();
        // The watch drives the same host it was given, so a Site that answers
        // its worker host from elsewhere is watched from there too unless the
        // caller says otherwise.
        _watch = watch ?? new ResidentFolderCellWatch(_resident);
        _heartbeat = heartbeat ?? HeartbeatInterval;
        _code = new CellCode(store, placement.WorkRoot, placement.CodeFromHome);
    }

    /// <inheritdoc />
    /// <remarks>
    /// CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 2). A resident
    /// Python worker imports its code once and keeps it across attempts, which
    /// is neither the working tree at each start nor the registered commit, so a
    /// working-tree or commit Site is not served by one until that is built.
    /// CHG 2026-09-26 (slice 4; owner: keep the resident worker on this machine,
    /// retire it on remote Sites): with installed sources retired it serves no
    /// Site until its commit mode (one resident process per source) is built.
    /// </remarks>
    public string? CannotGiveCode(RegistryOperations.Site site, long sourceId)
    {
        var declared = RegistryOperations.CodeOf(site.CapabilitiesJson);
        if (!declared.TryGetValue(out var code))
        {
            declared.TryGetRefusal(out var refusal);
            return $"Site {site.Name}: {refusal.Detail}";
        }
        if (_placement.WarmWorker)
        {
            return $"Site {site.Name} takes its cells' code from {code.From}, and this supervisor runs a resident "
                + "Python worker, which would keep one import of the code across attempts and has no commit mode yet; "
                + "start it without --warm-worker";
        }
        return _code.CannotGive(site.SiteId, code, sourceId);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> RemoveCodeLeftovers(IReadOnlySet<long> openAttempts) =>
        CellCode.RemoveLeftovers(_placement.WorkRoot, openAttempts);

    /// <summary>
    /// Rule 1 of AGENTS.md, enforced here rather than trusted: unless the cell's
    /// own configuration says <c>cpu</c>, a cell trains on the GPU. A cell that
    /// asks for the CPU without the owner's word is refused by name, not
    /// quietly run slowly — the failure of a silent CPU run is a day of wrong
    /// numbers nobody notices.
    /// </summary>
    public static string? CpuRefusal(string configJson)
    {
        var device = DeviceOf(configJson, out var unreadable);
        if (unreadable is not null)
        {
            return unreadable;
        }
        if (device.Length == 0)
        {
            return "the cell's configuration names no runtime.device; a cell states the device it trains on";
        }
        if (string.Equals(device, "cpu", StringComparison.Ordinal))
        {
            return "runtime.device=cpu: every local run that trains runs on the GPU unless the owner said cpu "
                 + "(AGENTS.md rule 1); this worker refuses rather than run a training cell on the CPU";
        }
        return null;
    }

    /// <summary>The device a configuration names, or the empty string; <paramref name="unreadable"/> says why not.</summary>
    static string DeviceOf(string configJson, out string? unreadable)
    {
        unreadable = null;
        try
        {
            using var config = JsonDocument.Parse(configJson);
            return config.RootElement.TryGetProperty("runtime", out var runtime)
                && runtime.TryGetProperty("device", out var declared) && declared.ValueKind == JsonValueKind.String
                    ? declared.GetString() ?? ""
                    : "";
        }
        catch (JsonException error)
        {
            unreadable = $"the cell's configuration is not readable JSON: {error.Message}";
            return "";
        }
    }

    /// <summary>
    /// The root the claim handed over: the Site's repository (slice 4 of
    /// source-roots-stop-being-full-copies; it was the site_sources row
    /// install-study-source recorded). A claim without one is refused by name.
    /// </summary>
    static string RootOf(StudyOrchestrator.ClaimedCell cell) =>
        cell.SourceRoot ?? throw new InvalidOperationException(
            $"cell {cell.CellName} was claimed without its Site's repository");

    /// <inheritdoc />
    /// <remarks>
    /// CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 2): the cell's
    /// code directory is made first, recorded on the attempt, and released when
    /// the run ends however it ends. The run itself is <see cref="RunIn"/>. A
    /// directory that cannot be made is the machine's failure, so the cell is
    /// not tried on this Site again and nobody is asked whether the cell is at fault.
    /// </remarks>
    public StudyOrchestrator.WorkerOutcome Run(StudyOrchestrator.ClaimedCell cell, CancellationToken cancellation)
    {
        if (cell.SourceRoot is not { Length: > 0 })
        {
            return Refused($"cell {cell.CellName} was claimed without its Site's repository");
        }
        var prepared = _code.Prepare(cell, Path.Combine(_placement.WorkRoot, ArtifactName(cell, CellCode.DifferenceFile)));
        if (!prepared.IsOk)
        {
            return new StudyOrchestrator.WorkerOutcome(false, null, CellFailureDetail.Write(
                prepared.Refusal, deterministic: false, siteSpecific: true, failureClass: CellFailureDetail.ClassSite,
                beforeExecution: true));
        }
        var code = prepared.Value;
        _store.RecordEvent(cell.AttemptId, cell.FencingEpoch, CellCode.EventKind, cell.CodeFrom, code.DetailJson);
        try
        {
            if (code.Directory != cell.SourceRoot && _placement.WarmWorker)
            {
                return new StudyOrchestrator.WorkerOutcome(false, null, CellFailureDetail.Write(
                    $"cell {cell.CellName} runs in {code.Directory}, made for this attempt, and a resident Python worker "
                    + "keeps one import of its code across attempts; start the supervisor without --warm-worker",
                    deterministic: false, siteSpecific: true, failureClass: CellFailureDetail.ClassSite, beforeExecution: true));
            }
            return RunIn(cell, code.Directory, cancellation);
        }
        finally
        {
            CellCode.Release(code);
        }
    }

    /// <summary>Run a claimed cell whose code is in <paramref name="codeDirectory"/>.</summary>
    StudyOrchestrator.WorkerOutcome RunIn(StudyOrchestrator.ClaimedCell cell, string codeDirectory, CancellationToken cancellation)
    {
        var registered = _store.ReadCell(cell.CellId);
        if (registered is null)
        {
            return Refused($"cell {cell.CellName} (#{cell.CellId}) is not in the store with its study's compiled document");
        }
        var (configJson, compiledStudyJson, registeredSeed) = registered;
        if (CpuRefusal(configJson) is { } refusal)
        {
            return Refused(refusal);
        }
        var device = DeviceOf(configJson, out _);

        var assembled = CellRequest(compiledStudyJson, cell.CellName, device, registeredSeed);
        if (!assembled.IsOk)
        {
            return Refused(assembled.Refusal);
        }

        // Every attempt gets its own directory, named by the attempt, so two
        // attempts of one cell never write over each other's request or result.
        // CHG: current attempts share WorkRoot; the explicit attempt prefix
        // provides that separation on each filename, including checkpoints.
        var attemptRoot = _placement.WorkRoot;
        var requestPath = Path.Combine(attemptRoot, ArtifactName(cell, RequestFile));
        var cellRequestPath = Path.Combine(attemptRoot, ArtifactName(cell, CellRequestFile));
        var reportPath = Path.Combine(attemptRoot, ArtifactName(cell, ReportFile));
        Directory.CreateDirectory(Path.GetDirectoryName(requestPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cellRequestPath)!);
        File.WriteAllBytes(cellRequestPath, assembled.Value);
        if (File.Exists(reportPath))
        {
            File.Delete(reportPath);
        }

        var digest = ImplementationDigest(codeDirectory);
        if (!digest.IsOk)
        {
            return Refused(digest.Refusal);
        }
        File.WriteAllText(requestPath, StageRequest(cell, assembled.Value, digest.Value));

        // CHG: explicit recovery uses the frozen source's existing CLI while
        // this worker retains the native heartbeat, cancellation and publication.
        StudyCellRecovery.Invocation? recovery = null;
        if (_placement.RecoveryPrefixes?.TryGetValue(cell.CellName, out var previousPrefix) == true)
        {
            if (_placement.WarmWorker || _placement.ResultsRoot.Length != 0)
                return Refused("explicit recovery currently requires the cold worker and Home-returned summaries");
            try
            {
                recovery = StudyCellRecovery.Prepare(cell with { SourceRoot = codeDirectory }, attemptRoot, previousPrefix, assembled.Value, digest.Value);
                _store.RecordEvent(cell.AttemptId, cell.FencingEpoch, "training_recovery", "restore", recovery.ProvenanceJson);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or ArgumentException)
            {
                return Refused("training recovery refused before launch: " + error.Message);
            }
        }

        if (_placement.WarmWorker)
        {
            return RunWarm(cell, attemptRoot, reportPath, cancellation);
        }

        // The trainer runs here, and while it does this supervisor says so. The
        // beat stops before anything else touches the Registry connection, so
        // the two never write at the same time.
        //
        // CHG 2026-09-11 night (slice 2 of the cell port, row 23): the beat also
        // reads the study's requested state, and a study asked to cancel ends
        // the trainer through the host, which kills the process tree. The
        // retired daemon cancelled into its live Site session; this supervisor
        // cancels into the process it owns. The orchestrator's own token is
        // not used for that on purpose: Ctrl-C ends the loop between cells.
        using var beating = new CancellationTokenSource();
        using var cancelling = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var heartbeat = Task.Run(() => Beat(cell, attemptRoot, beating.Token, cancelling), CancellationToken.None);
        BoundedProbe.Outcome outcome;
        try
        {
            string[] arguments = recovery?.Arguments
                ?? ["-m", PythonModule, "run", "--request", requestPath, "--workspace", attemptRoot];
            outcome = _host is WorkerEnvironmentPreparation.ICancellableHost cancellable
                ? cancellable.Run(
                    _placement.Python, arguments, NoDeadlineMs, MaximumResultBytes,
                    codeDirectory, Environment(codeDirectory, cell), cancelling.Token)
                : _host.Run(
                    _placement.Python, arguments, NoDeadlineMs, MaximumResultBytes,
                    codeDirectory, Environment(codeDirectory, cell));
        }
        finally
        {
            beating.Cancel();
            heartbeat.GetAwaiter().GetResult();
        }

        var authorityLost = heartbeat.Result || cancellation.IsCancellationRequested;
        if (recovery is not null && !authorityLost && outcome is BoundedProbe.Outcome.Completed { ExitCode: 0 })
        {
            try { return new StudyOrchestrator.WorkerOutcome(true, StudyCellRecovery.ReadResult(recovery, assembled.Value), "{}"); }
            catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or ArgumentException)
            { return Failed("recovery result refused: " + error.Message, null); }
        }
        return Read(cell, outcome, attemptRoot, reportPath, authorityLost);
    }

    /// <summary>
    /// Say, every <see cref="HeartbeatInterval"/>, that this supervisor is still
    /// here, fenced by the attempt's own epoch.
    ///
    /// A refused renewal means this supervisor was displaced while the trainer
    /// ran: its epoch is no longer the attempt's, so the beat stops rather than
    /// hammering a row it does not own. Nothing else is done about it here —
    /// the finish or fail at the end of this run is fenced the same way and will
    /// be refused too, which is what records the attempt as lost.
    /// </summary>
    // CHG: losing authority ends the owned process and is a lost attempt,
    // distinct from an operator asking to cancel a study.
    bool Beat(StudyOrchestrator.ClaimedCell cell, string attemptRoot, CancellationToken cancellation, CancellationTokenSource cancelling,
        Action? eachBeat = null)
    {
        WriteHeartbeatFile(cell, attemptRoot);
        // CHG 2026-09-26 (owner: an attempt whose process is gone is given up by
        // looking): say at once which process runs this attempt, so a supervisor
        // started later on this machine can look for it, even if this one dies
        // before its first beat (attempt 2004 was stopped 23 s after its claim).
        // Only the record: whether the attempt is still this supervisor's is the
        // beats' question below, as before.
        RenewAttempt(cell);
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                Task.Delay(_heartbeat, cancellation).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            if (cancellation.IsCancellationRequested)
            {
                return false;
            }
            // CHG: a worker that can no longer renew its authority must stop
            // its owned trainer before the silent attempt's device is reused.
            // CHG 2026-09-26 (slice W3; owner: nothing kills a training for
            // silence): only a REFUSED renewal stops it — the Registry no longer
            // holds this attempt as this supervisor's, because a person gave it
            // up or a later attempt took its place. A renewal nobody answered
            // (the Home or the way to it is silent) keeps the training running;
            // under slice W2 its cell and card stay held meanwhile.
            switch (RenewAttempt(cell))
            {
                case Renewal.Refused:
                    cancelling.Cancel();
                    return true;
                case Renewal.Unanswered:
                    WriteHeartbeatFile(cell, attemptRoot);
                    continue;
            }
            WriteHeartbeatFile(cell, attemptRoot);
            // CHG 2026-09-11 night (slice 4): the warm route records the
            // trainer's progress here, on the one thread that touches the
            // Registry while a cell runs, so no two writes ever race.
            // CHG 2026-09-26 (slice W3): progress the Home cannot take this beat
            // is not recorded; the training goes on.
            try { eachBeat?.Invoke(); }
            catch (HomeUnreachableException) { }
            // CHG 2026-09-11 night (slice 2): a study asked to cancel reaches the
            // cell that is training it, at the next beat, through the host.
            if (!cancelling.IsCancellationRequested && StudyAskedToCancel(cell.StudyId))
            {
                cancelling.Cancel();
            }
        }
        return false;
    }

    // ── the warm route: one resident worker per interpreter, source and card ──
    //
    // CHG 2026-09-11 night (slice 4 of the cell port, design note rows 15, 20,
    // 21). The retired Site session admitted a resident Python worker by its
    // published identity (starting, then ready, under a lifetime lock), fed it
    // cells through a folder inbox, watched the trainer's progress lines and
    // accepted only monotone checkpoints, and treated a worker that stayed
    // `starting` past a deadline as workerless. All of that is here, at cell
    // level, over the same folder protocol (`fern_stage_runtime serve`): the
    // torch import is paid once per worker lifetime rather than once per cell.

    /// <summary>The retired CycleOffice's revocation bound: a worker still starting after this is workerless.</summary>
    public static readonly TimeSpan WorkerStartDeadline = TimeSpan.FromSeconds(300);

    /// <summary>The retired monitor's warning bound: a worker still starting after this is noted as slow.</summary>
    public static readonly TimeSpan WorkerStartWarning = TimeSpan.FromSeconds(60);

    public const string WorkerStartupEvent = "worker_startup";
    public const string CellRequestSchema = "fern.cell_request.v1";

    /// <summary>
    /// Where this machine's worker for one interpreter, one source and one
    /// card lives: `&lt;work root&gt;/workers/&lt;interpreter&gt;/&lt;source&gt;-&lt;card&gt;`,
    /// which is the two-level layout <see cref="ResidentPythonWorker.ListWorkers"/>
    /// reads. A card is part of the key because CUDA_VISIBLE_DEVICES is set
    /// when the worker starts, so one warm process serves one card.
    ///
    /// CHG 2026-09-26 (owner): the names stay 12 characters of SHA-256 of each part.
    /// A version that wrote the paths out (b38befc57) was reverted: "if hash to get
    /// the folder name then it is fine"; "worker path should never have my name in it".
    /// </summary>
    public string WarmWorkerRoot(StudyOrchestrator.ClaimedCell cell)
    {
        static string Short(string text) =>
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..12];
        var card = cell.Device is { Length: > 0 } device ? device : "any-card";
        // CHG: a resident process retains its compiler environment. A different
        // selection for the same source must not reuse that process.
        // CHG 2026-09-26: the cell's own choice, so two studies on one root that
        // name two compilers never share a resident Python process.
        var sourceAndCompiler = RootOf(cell) + "\n" + CompilerFor(cell);
        return Path.Combine(_placement.WorkRoot, "workers", Short(_placement.Python), Short(sourceAndCompiler) + "-" + Short(card));
    }

    /// <summary>The cell id the worker files the attempt under: unique per attempt, no slash.</summary>
    public static string WarmCellId(StudyOrchestrator.ClaimedCell cell) =>
        "attempt-" + cell.AttemptId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The document the resident worker reads: which cell this is, where its
    /// artifacts go, and the ordered phases to run.
    ///
    /// One phase today, because this route sends the whole cell to one handler.
    /// It is a method rather than an expression inside the warm path because
    /// its bytes are pinned from two sides — <c>CellRequestDocumentTests</c>
    /// here, and the Gleam projection that renders the same document from a
    /// typed pipeline (docs/programs/2026-09-23-cells-as-pipelines, slice 3). If either side
    /// moves, one of the two fails.
    /// </summary>
    public static string CellRequestDocument(string cellId, string workspace, string requestPath) =>
        new JsonObject
        {
            ["schema"] = CellRequestSchema,
            ["cell_id"] = cellId,
            ["artifact_prefix"] = cellId + ".",
            ["workspace"] = workspace,
            ["phases"] = new JsonArray(new JsonObject
            {
                ["ordinal"] = 0,
                ["handler"] = Handler,
                ["request"] = requestPath,
            }),
        }.ToJsonString();

    StudyOrchestrator.WorkerOutcome RunWarm(StudyOrchestrator.ClaimedCell cell, string attemptRoot, string reportPath, CancellationToken cancellation)
    {
        var root = WarmWorkerRoot(cell);
        // Admission first, and before the beat: the startup observations are
        // written here, on this thread, while nothing else writes.
        var started = _resident.Ensure(
            root, _placement.Python, RootOf(cell), WorkerStartDeadline,
            observation => NoteStartup(cell, observation), Environment(RootOf(cell), cell));
        if (!started.IsOk)
        {
            var deadline = started.Failure.Category == SiteFailureCategory.WorkerAdmissionDeadline;
            return new StudyOrchestrator.WorkerOutcome(false, null, CellFailureDetail.Write(
                deadline
                    ? $"the resident worker under {root} did not become ready within {WorkerStartDeadline.TotalSeconds:F0} s: {started.RefusalDetail}"
                    : $"the resident worker under {root} could not be started: {started.RefusalDetail}",
                deterministic: false, siteSpecific: true, failureClass: CellFailureDetail.ClassSite, beforeExecution: true));
        }
        var identity = started.Value;
        var cellId = WarmCellId(cell);
        var cellRequest = CellRequestDocument(cellId, attemptRoot, ArtifactName(cell, RequestFile));

        // CHG 2026-09-23 (slice F of docs/programs/2026-09-22-gleam-site-supervision): the
        // watch is an interface now, so a Site may have it answered by the
        // Gleam program instead of by the loop in this binary. What was here
        // is ResidentFolderCellWatch, moved whole.
        //
        // The watch reads the cell's log on its own schedule and hands over
        // each checkpoint that moves forward; this side records them ON THE
        // BEAT'S THREAD, exactly as before, because that is what keeps two
        // threads from writing to the Registry connection at once. The queue
        // between them is what makes "read promptly, record on the beat"
        // possible without either giving up its own clock.
        var reported = new System.Collections.Concurrent.ConcurrentQueue<StageCheckpoint>();
        using var beating = new CancellationTokenSource();
        using var cancelling = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var heartbeat = Task.Run(
            () => Beat(cell, attemptRoot, beating.Token, cancelling, () => RecordProgress(cell, reported)),
            CancellationToken.None);
        WatchResult watched;
        try
        {
            watched = _watch.Watch(
                new WarmCell(root, identity, cellId, cellRequest, _placement.Timeout),
                reported.Enqueue,
                cancelling.Token);
        }
        finally
        {
            beating.Cancel();
            heartbeat.GetAwaiter().GetResult();
        }
        // One last record, on this thread, now that the beat is over.
        if (!heartbeat.Result && !cancellation.IsCancellationRequested) RecordProgress(cell, reported);

        if (watched is WatchResult.NotPlaced notPlaced)
        {
            return new StudyOrchestrator.WorkerOutcome(false, null, CellFailureDetail.Write(
                notPlaced.Detail,
                deterministic: false, siteSpecific: true, failureClass: CellFailureDetail.ClassSite, beforeExecution: true));
        }
        var outcome = ((WatchResult.Ran)watched).Outcome;
        // The phase's workspace is the attempt root (its request lives under
        // request/ there), so the report lands where the per-cell route reads it.
        return Read(cell, outcome, attemptRoot, reportPath, heartbeat.Result || cancellation.IsCancellationRequested);
    }

    /// <summary>A worker's startup, as the store records it: starting, then ready, with how long it took; slow past the warning bound.</summary>
    void NoteStartup(StudyOrchestrator.ClaimedCell cell, ResidentPythonWorker.WorkerStartupObservation observation)
    {
        var slow = observation.ElapsedMs > WorkerStartWarning.TotalMilliseconds;
        _ = _store.RecordEvent(cell.AttemptId, cell.FencingEpoch, WorkerStartupEvent, null,
            new JsonObject
            {
                ["detail"] = slow
                    ? $"resident worker {observation.State} after {observation.ElapsedMs} ms, past the {WorkerStartWarning.TotalSeconds:F0} s warning bound"
                    : $"resident worker {observation.State} after {observation.ElapsedMs} ms",
                ["state"] = observation.State,
                ["launch_id"] = observation.Identity.LaunchId,
                ["pid"] = observation.Identity.Pid,
                ["elapsed_ms"] = observation.ElapsedMs,
                ["slow"] = slow,
            }.ToJsonString());
    }

    /// <summary>
    /// Record every checkpoint the watch has handed over since the last beat.
    ///
    /// Liveness is the beat; progress is only what the trainer itself printed.
    /// Which checkpoints count as moving forward is the watch's reading, and
    /// the rule it applies is the retired transport's, unchanged.
    /// </summary>
    void RecordProgress(
        StudyOrchestrator.ClaimedCell cell,
        System.Collections.Concurrent.ConcurrentQueue<StageCheckpoint> reported)
    {
        while (reported.TryDequeue(out var accepted))
        {
            if (accepted.Phase is not null) continue;
            _ = _store.RecordEvent(cell.AttemptId, cell.FencingEpoch, UnfinishedCell.TrainingProgressEvent, null,
                new JsonObject
                {
                    ["detail"] = $"epoch {accepted.Current} of {accepted.Total}" + (accepted.Terminal ? ", terminal" : ""),
                    ["current"] = accepted.Current,
                    ["total"] = accepted.Total,
                    ["terminal"] = accepted.Terminal,
                    ["training_run"] = accepted.TrainingRun,
                }.ToJsonString());
        }
    }
    /// <summary>
    /// One beat's renewal. A Home this machine cannot reach for the client's
    /// whole retry window counts as a refused renewal: the beat stops, the
    /// trainer runs on, and the attempt goes silent the way it does when a
    /// local supervisor's renewal is refused (reconciled as lost after
    /// <see cref="RegistryOperations.StaleAfter"/>, claimed again). CHG
    /// 2026-09-11 night (slice 5).
    /// </summary>
    /// <remarks>
    /// CHG 2026-09-26 (slice W3): three answers, not two. An unanswered renewal
    /// is no longer counted as a refused one; see <see cref="Beat"/>.
    /// </remarks>
    Renewal RenewAttempt(StudyOrchestrator.ClaimedCell cell)
    {
        try
        {
            return _store.RenewAttempt(cell.AttemptId, cell.FencingEpoch, RunningHere).IsOk ? Renewal.Renewed : Renewal.Refused;
        }
        catch (HomeUnreachableException)
        {
            return Renewal.Unanswered;
        }
    }

    /// <summary>How one beat's renewal of the attempt went.</summary>
    enum Renewal
    {
        Renewed,
        /// <summary>The Registry answered no: the attempt is not this supervisor's any more.</summary>
        Refused,
        /// <summary>Nobody answered within the client's retries.</summary>
        Unanswered,
    }

    bool StudyAskedToCancel(long studyId)
    {
        try
        {
            var view = _store.ReadStudy(studyId);
            return view.TryGetValue(out var head) && head.RequestedState == RegistryOperations.StateCancel;
        }
        catch (HomeUnreachableException)
        {
            // Not knowing is not a cancellation; the next beat asks again.
            return false;
        }
    }

    /// <summary>
    /// The same fact as the row, under the attempt's own folder: one file,
    /// overwritten every beat, never appended to.
    ///
    /// It exists because "is this run alive" should be answerable by reading
    /// something under the run's root — by a person, over SSH, from a machine
    /// with no Registry — rather than by hunting for a process id. It carries
    /// who is beating (this supervisor's process and host), for what (the study,
    /// the cell, the attempt and its epoch), when it last beat, and the cadence,
    /// so a reader can decide staleness without knowing this code's constants.
    ///
    /// A failure to write it is not a failure of the cell: the row is the
    /// authority and the file is the convenience, so this swallows an I/O error
    /// rather than killing a training run over a full disk in a work folder.
    /// </summary>
    void WriteHeartbeatFile(StudyOrchestrator.ClaimedCell cell, string attemptRoot)
    {
        try
        {
            var beat = new JsonObject
            {
                ["schema"] = HeartbeatSchema,
                ["study_id"] = cell.StudyId,
                ["cell"] = cell.CellName,
                ["cell_id"] = cell.CellId,
                ["attempt_id"] = cell.AttemptId,
                ["fencing_epoch"] = cell.FencingEpoch,
                ["at"] = DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                ["interval_seconds"] = (long)_heartbeat.TotalSeconds,
                ["stale_after_seconds"] = (long)RegistryOperations.StaleAfter.TotalSeconds,
                ["supervisor_pid"] = System.Environment.ProcessId,
                ["supervisor_host"] = System.Environment.MachineName,
            };
            var path = Path.Combine(attemptRoot, ArtifactName(cell, HeartbeatFile));
            var partial = path + ".partial";
            File.WriteAllText(partial, beat.ToJsonString() + "\n");
            File.Move(partial, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The Registry row is the authority; this file is how a person reads
            // it without one. Losing it does not lose the run.
        }
    }

    /// <summary>
    /// Turn what the process did and what it left behind into one outcome.
    ///
    /// The report is the authority when it exists: it carries the runtime's own
    /// words for the failure — its kind, its code and its message — and those
    /// travel into the Registry so a person reading the attempt sees WHY
    /// without opening a worker folder. When no report exists the process
    /// itself is the only witness, and its stderr tail is what the reader gets.
    /// </summary>
    StudyOrchestrator.WorkerOutcome Read(
        StudyOrchestrator.ClaimedCell cell, BoundedProbe.Outcome outcome, string attemptRoot, string reportPath, bool authorityLost)
    {
        if (authorityLost)
            return new StudyOrchestrator.WorkerOutcome(false, null,
                CellFailureDetail.Write("execution authority could no longer be renewed; the owned trainer was stopped",
                    deterministic: false, failureClass: CellFailureDetail.ClassTransient),
                EndState: RegistryOperations.AttemptLost);
        var stderr = outcome is BoundedProbe.Outcome.Completed completed ? Tail(completed.Stderr) : null;
        if (outcome is BoundedProbe.Outcome.Cancelled)
        {
            // CHG 2026-09-11 night (slice 2, row 23): the attempt ends as
            // cancelled, and the record is a person's decision, so nothing
            // retries it until a person says otherwise.
            return new StudyOrchestrator.WorkerOutcome(false, null,
                CellFailureDetail.Write(Explain(outcome), deterministic: false, outcomeKind: "cancelled",
                    failureClass: CellFailureDetail.ClassHuman),
                EndState: RegistryOperations.AttemptCancelled);
        }
        if (!File.Exists(reportPath))
        {
            return Failed($"the cell's runtime left no report at {reportPath}: {Explain(outcome)}", stderr);
        }

        JsonDocument report;
        try
        {
            report = JsonDocument.Parse(File.ReadAllText(reportPath));
        }
        catch (JsonException error)
        {
            return Failed($"the cell's report at {reportPath} is not readable JSON: {error.Message}", stderr);
        }
        using (report)
        {
            if (!report.RootElement.TryGetProperty("outcome", out var ended) || ended.ValueKind != JsonValueKind.Object)
            {
                return Failed($"the cell's report at {reportPath} carries no outcome", stderr);
            }
            var kind = ended.TryGetProperty("kind", out var declared) && declared.ValueKind == JsonValueKind.String
                ? declared.GetString() ?? ""
                : "";
            if (kind != "success")
            {
                var code = ended.TryGetProperty("code", out var refused) && refused.ValueKind == JsonValueKind.String
                    ? refused.GetString() ?? ""
                    : "";
                var message = ended.TryGetProperty("message", out var said) && said.ValueKind == JsonValueKind.String
                    ? said.GetString() ?? ""
                    : "";
                // CHG 2026-09-25 (slice M1 of what-stops-work-and-who-lifts-it,
                // the owner's go): the Site's compiler refusing a field of a
                // card is the Site's, since the Study's own compiler accepted it
                // at registration. A runtime that names it says
                // `compiler_refused_field`; an older one says `runner_failed`
                // with the worker's literal sentence, read here as the same.
                var compilerRefused = code == RegistryOperations.CodeCompilerRefusedField
                    || message.StartsWith(CompilerRefusal, StringComparison.Ordinal);
                // CHG 2026-09-25 (the reader's review): whether this Site's
                // compiler differs from the one the study records. The study's
                // own compiler refusing a card is the card's, not the Site's;
                // when nobody can tell (the study records none), it stays the
                // Site's, but its attempt counts against the budget.
                var differs = compilerRefused ? CompilerDiffers(cell) : null;
                var ownCompiler = compilerRefused && differs == false;
                var recordedCode = compilerRefused && !ownCompiler ? RegistryOperations.CodeCompilerRefusedField : code;
                var site = (compilerRefused && !ownCompiler) || SiteSpecific(message);
                var deterministic = ownCompiler || Deterministic(code);
                return new StudyOrchestrator.WorkerOutcome(false, null,
                    CellFailureDetail.Write($"the cell was refused by its runtime: {code}: {message}", deterministic,
                        site ? CellFailureDetail.ClassSite
                            : deterministic ? CellFailureDetail.ClassDeterministic : CellFailureDetail.ClassTransient,
                        outcomeKind: kind, code: recordedCode, runtimeMessage: message, stderrTail: stderr,
                        siteSpecific: site, compilerDiffers: ownCompiler ? null : differs));
            }
            if (!ended.TryGetProperty("output_paths", out var paths)
                || !paths.TryGetProperty(UnitResultsPort, out var relative)
                || relative.ValueKind != JsonValueKind.String)
            {
                return Failed($"the cell reported success without its {UnitResultsPort}", stderr);
            }
            // CHG: a shared directory does not grant access to another attempt's result.
            if (relative.GetString() != ArtifactName(cell, "results.json"))
                return Failed("the cell reported a result outside its execution's named artifacts", stderr);
            var resultPath = Path.Combine(attemptRoot, FromSlash(relative.GetString()!));
            if (!File.Exists(resultPath))
            {
                return Failed($"the cell reported success but wrote no results at {resultPath}", stderr);
            }

            // The runtime was given a results root, so it says what became of the
            // document. Saved: record WHERE it went rather than a second copy of
            // it, after checking the digest against the bytes still on disk, so
            // "the store holds this document" is proved and not claimed. Not
            // saved: the cell still trained — keep its document as before and
            // write the reason where a person will see it.
            if (ended.TryGetProperty("saved_result", out var saved) && saved.ValueKind == JsonValueKind.Object)
            {
                if (SavedResultRefusal(saved) is { } refusal)
                {
                    return Failed(refusal, stderr);
                }
                if (saved.TryGetProperty("saved", out var stored) && stored.ValueKind == JsonValueKind.False)
                {
                    NoteNotSaved(cell, saved);
                    return new StudyOrchestrator.WorkerOutcome(true, File.ReadAllText(resultPath), "{}");
                }
                return new StudyOrchestrator.WorkerOutcome(true, saved.GetRawText(), "{}");
            }
            return new StudyOrchestrator.WorkerOutcome(true, File.ReadAllText(resultPath), "{}");
        }
    }

    /// <summary>
    /// Write, against this attempt, why a trained cell's result is not in the
    /// store. It is an event rather than a failure because the cell did train
    /// and its document is recorded: what is missing is the copy in the science
    /// store, and a person reading the study needs to know that before they
    /// collect it.
    /// </summary>
    void NoteNotSaved(StudyOrchestrator.ClaimedCell cell, JsonElement saved)
    {
        var database = saved.TryGetProperty("database", out var named) && named.ValueKind == JsonValueKind.String
            ? named.GetString() ?? "the selected store"
            : "the selected store";
        var reason = saved.TryGetProperty("reason", out var because) && because.ValueKind == JsonValueKind.String
            ? because.GetString() ?? "no reason given"
            : "no reason given";
        _store.RecordEvent(
            cell.AttemptId, cell.FencingEpoch, StudyCollect.ResultNotSaved, null,
            CellFailureDetail.Write(
                $"result kept by the Home, store refused: {reason} (the store it tried was {database})",
                deterministic: false, CellFailureDetail.ClassSite, runtimeMessage: reason));
    }

    /// <summary>
    /// Why a reported save is not believed, or null when it is.
    ///
    /// A save that says it happened has to name the database, the study, the run
    /// and a digest, and that digest has to be the digest of the results file
    /// this cell actually wrote — without the last one a reference is a promise
    /// that the store holds something, and a promise is what a reader would
    /// later find broken with no way to tell when it broke. A save that says it
    /// did NOT happen has only to name the database it tried and why, because
    /// there are no stored bytes to check.
    ///
    /// CHG 2026-09-26 (docs/programs/2026-09-26-cards-without-hashes, row B16): no
    /// digest is required or recomputed here. The cell reads its stored revision back
    /// and compares it with the results file it wrote, byte for byte, before it reports
    /// (fern_stage_runtime.cli); this check names the four fields a reference needs.
    /// A report that still carries sha256 (from a cell before that change) is accepted.
    /// </summary>
    static string? SavedResultRefusal(JsonElement saved)
    {
        var schema = saved.TryGetProperty("schema", out var declared) && declared.ValueKind == JsonValueKind.String
            ? declared.GetString() ?? ""
            : "";
        if (schema != StudyCollect.SavedResultSchema)
        {
            return $"the cell reported a saved result on schema \"{schema}\", not {StudyCollect.SavedResultSchema}";
        }
        if (!saved.TryGetProperty("saved", out var stored)
            || stored.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return "the cell's saved result does not say whether it was saved; `saved` is true or false";
        }
        if (stored.ValueKind == JsonValueKind.False)
        {
            foreach (var field in new[] { "database", "reason" })
            {
                if (!saved.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return $"the cell says its result was not saved but names no {field}; a refusal to store names "
                         + "the database it tried and why";
                }
            }
            return null;
        }
        foreach (var field in new[] { "database", "study", "run", "reference" })
        {
            if (!saved.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
            {
                return $"the cell's saved result names no {field}; a reference to a stored document names all of "
                     + "database, study, run and reference";
            }
        }
        return null;
    }

    /// <summary>
    /// The compiler a cell's run is given. CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
    /// slice 4; owner: "compile远端其实只是检查，本地compile卡片即可"): no compiler is carried
    /// to a Site and no Site's compiler is compared with the study's before a claim.
    /// A study compiled since slice 3b is not compiled during its run at all; one
    /// compiled before still asks the compiler for two checks, and is given the
    /// compiler it was compiled with when that file is on this machine (the Home's
    /// own published file, which the deploy cleanup keeps), else --fern-compile.
    /// </summary>
    string CompilerFor(StudyOrchestrator.ClaimedCell cell) =>
        _store.ReadStudyCompiler(cell.StudyId) is { } recorded && File.Exists(recorded.Path)
            ? recorded.Path
            : _placement.FernCompileBinary;

    /// <summary>
    /// Whether the compiler this cell was given is not the one its study
    /// records (slice M1: a refusal by another compiler is the Site's, by the
    /// study's own compiler the card's); null when the study records none.
    /// </summary>
    bool? CompilerDiffers(StudyOrchestrator.ClaimedCell cell) =>
        _store.ReadStudyCompiler(cell.StudyId) is { } recorded
            ? !string.Equals(Path.GetFullPath(CompilerFor(cell)), Path.GetFullPath(recorded.Path), StringComparison.Ordinal)
            : null;

    /// <summary>
    /// The environment the old route set, and one variable it did not have:
    /// FERN_COMPILE_BIN. Python asks the Go compiler for a configuration's
    /// identity now (fern_train.worker.config_hash_from_host calls
    /// `fern-compile identity`), so a worker that did not name it would send
    /// the trainer looking for a binary on PATH.
    /// </summary>
    IReadOnlyDictionary<string, string> Environment(string sourceRoot, StudyOrchestrator.ClaimedCell? cell = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // CHG 2026-09-26: src/ joins the import roots (serie/ moved to src/serie/).
            ["PYTHONPATH"] = sourceRoot + Path.PathSeparator + Path.Combine(sourceRoot, "python")
                + Path.PathSeparator + Path.Combine(sourceRoot, "src"),
            ["PYTHONDONTWRITEBYTECODE"] = "1",
            ["PYTHONUNBUFFERED"] = "1",
            ["FERN_SOURCE_ROOT"] = sourceRoot,
            ["FERN_RUN_BIN"] = _placement.FernRunBinary,
            // CHG: new source vocabularies can coexist with queued old sources;
            // the same explicit selection reaches both probe and training.
            // CHG 2026-09-26: a cell's training gets the compiler its study names
            // when this Site holds it (the pair still first); the probe, with no
            // cell, keeps the pair or the default.
            // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 2): the
            // cell's compiler is chosen by its claimed root, the same one the
            // check before the claim used, even when it runs in a directory
            // made for the attempt.
            ["FERN_COMPILE_BIN"] = cell is null ? _placement.FernCompileBinary : CompilerFor(cell),
        };
        // The card the Site granted this attempt. It is passed the way every
        // CUDA program already expects to be told — the visible-devices
        // variable — so nothing in the Python contract has to learn a new field,
        // and the trainer still asks for "cuda" and gets the one card it was
        // given.
        if (cell?.Device is { Length: > 0 } device)
        {
            environment["CUDA_VISIBLE_DEVICES"] = device;
        }
        return environment;
    }

    /// <summary>
    /// The cell's own request: the study chain's formal shape, assembled from
    /// the compiled study the registration stored and the cell's entry in it.
    ///
    /// The seed is checked against the one the Registry stored for this cell,
    /// because that is the field a card with two seeds gets wrong silently: on
    /// 2026-09-10 every cell of a card with seeds 42 and 43 trained at 42
    /// because the request carried the study's head seed. Two stores of one
    /// number disagreeing is a refusal, never a choice between them.
    /// </summary>
    TextResult<byte[]> CellRequest(string compiledStudyJson, string cellName, string device, long registeredSeed)
    {
        JsonDocument compiled;
        try
        {
            compiled = JsonDocument.Parse(compiledStudyJson);
        }
        catch (JsonException error)
        {
            return TextResult<byte[]>.Refused($"the study's compiled document is not readable JSON: {error.Message}");
        }
        using (compiled)
        {
            if (!compiled.RootElement.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array)
            {
                return TextResult<byte[]>.Refused("the study's compiled document carries no cells");
            }
            foreach (var candidate in cells.EnumerateArray())
            {
                if (!candidate.TryGetProperty("cell", out var name) || name.ValueKind != JsonValueKind.String
                    || name.GetString() != cellName)
                {
                    continue;
                }
                var compiledSeed = candidate.TryGetProperty("coordinates", out var coordinates)
                    && coordinates.ValueKind == JsonValueKind.Object
                    && coordinates.TryGetProperty("seed", out var seed) && seed.TryGetInt64(out var value)
                        ? value
                        : (long?)null;
                if (compiledSeed is { } declared && declared != registeredSeed)
                {
                    return TextResult<byte[]>.Refused(
                        $"cell {cellName} is registered at seed {registeredSeed} and compiled at seed {declared}; "
                        + "a cell trains at one seed and the two stores of it must agree");
                }
                return StudyCellRequest.Assemble(
                    compiled.RootElement, candidate, device, _placement.CheckpointStore, _placement.ResultsRoot);
            }
            return TextResult<byte[]>.Refused($"the study's compiled document has no cell named {cellName}");
        }
    }

    /// <summary>
    /// The envelope the Python runtime reads: which handler, which input file,
    /// which output the cell owes. Everything scientific is in the cell request
    /// it points at; this document is the door.
    /// </summary>
    string StageRequest(StudyOrchestrator.ClaimedCell cell, byte[] cellRequest, string implementationDigest)
    {
        var request = new JsonObject
        {
            ["schema"] = StageRequestSchema,
            ["artifact_prefix"] = ArtifactPrefix(cell),
            ["protocol_version"] = StageProtocol,
            ["handler"] = Handler,
            ["stage_attempt_id"] = $"attempt-{cell.AttemptId}",
            ["plan_id"] = cell.StudyId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["cell_ordinal"] = cell.CellId,
            ["invocation"] = new JsonObject
            {
                ["stage_id"] = cell.CellName,
                ["implementation"] = new JsonObject
                {
                    ["runner"] = "python_module",
                    ["entrypoint"] = PythonModule,
                },
            },
            // The invocation's identity is the bytes of the cell request: that
            // document IS what was asked for, so its digest is what the report
            // echoes back.
            ["invocation_digest"] = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(cellRequest)),
            ["execution_key"] = $"study-{cell.StudyId}/cell-{cell.CellId}/attempt-{cell.AttemptId}/epoch-{cell.FencingEpoch}",
            ["implementation_digest"] = implementationDigest,
            ["input_files"] = new JsonObject
            {
                [StudyCellRequest.Port] = new JsonObject
                {
                    ["relative_path"] = ArtifactName(cell, CellRequestFile),
                    ["role"] = StudyCellRequest.Port,
                    ["schema_id"] = StudyCellRequest.Schema,
                },
            },
            ["declared_outputs"] = new JsonObject
            {
                [UnitResultsPort] = new JsonObject
                {
                    ["role"] = UnitResultsPort,
                    ["schema_id"] = StudyCollect.UnitResultsSchema,
                },
            },
        };
        return request.ToJsonString();
    }

    /// <summary>
    /// What the prepared environment says it implements, asked for each attempt.
    ///
    /// The runtime stamps this digest into every report it writes, so the
    /// request has to carry the one the environment actually answers with
    /// rather than a value this side computed from the same formula — a second
    /// implementation of an identity is not a check of it. Asking also proves,
    /// before any cell is claimed to have run, that this interpreter can import
    /// the training package at all.
    /// </summary>
    // CHG 2026-09-26 (source-roots-stop-being-full-copies, slices 2 and 4): it
    // was asked once per installed source root and reused. A working tree
    // changes between attempts and a commit directory is made per attempt, and
    // installed roots are retired, so it is asked for each attempt's directory.
    TextResult<string> ImplementationDigest(string sourceRoot)
    {
        var outcome = _host.Run(
            _placement.Python, ["-m", PythonModule, "probe"],
            (long)TimeSpan.FromMinutes(10).TotalMilliseconds, MaximumResultBytes,
            sourceRoot, Environment(sourceRoot));
        if (outcome is not BoundedProbe.Outcome.Completed completed || completed.ExitCode != 0)
        {
            return TextResult<string>.Refused(
                $"the prepared environment could not report what it implements: {Explain(outcome)}");
        }
        try
        {
            using var receipt = JsonDocument.Parse(completed.Stdout);
            if (!receipt.RootElement.TryGetProperty("implementation_digest", out var digest)
                || digest.ValueKind != JsonValueKind.String)
            {
                return TextResult<string>.Refused("the environment's capability receipt names no implementation_digest");
            }
            return TextResult<string>.Ok(digest.GetString()!);
        }
        catch (JsonException error)
        {
            return TextResult<string>.Refused($"the environment's capability receipt is not readable JSON: {error.Message}");
        }
    }

    // CHG 2026-09-11 night (slice 5): "the cell's configuration, its study's
    // compiled document, and the seed the Registry stored" is IStudyStore.ReadCell
    // now, the same three columns, so a worker on another machine reads them
    // through the Home rather than the file.

    /// <summary>Say what the process did in words an operator can act on.</summary>
    static string Explain(BoundedProbe.Outcome outcome) => outcome switch
    {
        BoundedProbe.Outcome.TimedOut timedOut => $"the cell did not finish within {timedOut.TimeoutMs} ms and its process tree was killed",
        BoundedProbe.Outcome.UnboundedOutput unbounded => $"the cell wrote more than {unbounded.MaxBytes} bytes and was stopped",
        BoundedProbe.Outcome.Missing missing => $"no interpreter at {missing.Path}",
        BoundedProbe.Outcome.Completed completed => $"the process exited {completed.ExitCode}: {Tail(completed.Stderr)}",
        BoundedProbe.Outcome.Cancelled cancelled => $"the study was cancelled while the cell trained; its process tree was killed after {cancelled.AfterMs} ms",
        _ => "the runtime did not start",
    };

    /// <summary>
    /// Which refusals will say the same thing again if the cell is handed back.
    ///
    /// A request the runtime would not accept — a schema, a protocol or a
    /// handler it does not serve — is the same request next time, so a
    /// scheduler that re-claimed such a cell would spend a machine printing one
    /// sentence repeatedly. A failure inside the handler (`runner_failed`) is
    /// not classified here: the runtime reports one code for a refused dataset
    /// and for a card that trained until the GPU fell over, and this side must
    /// not guess which it was from the message.
    /// </summary>
    /// <summary>The sentence <c>python/fern_train/worker.py</c> raises when the compiler refuses a card.</summary>
    const string CompilerRefusal = "the compiler refused the configuration";

    static bool Deterministic(string code) =>
        code is "invalid_request" or "protocol_mismatch" or "unknown_handler";

    /// <summary>
    /// Whether this failure is the MACHINE's rather than the cell's, read from
    /// the runtime's own message. Ported from the retired `OtherProfile` retry
    /// class: the same cell on another machine may well run, so spending the
    /// rest of its budget here would be spending it on the one host that cannot.
    ///
    /// The list is deliberately short and literal — the sentences a worker
    /// actually prints when the HOST is what is wrong: no CUDA device, a
    /// declared dataset input missing from this machine's source tree, an
    /// interpreter or import that is not there. Anything else is a cell-level
    /// failure until somebody has a reason to add to this list.
    /// </summary>
    static bool SiteSpecific(string message) =>
        message.Contains("requires CUDA", StringComparison.OrdinalIgnoreCase)
        || message.Contains("no CUDA", StringComparison.OrdinalIgnoreCase)
        || message.Contains("CUDA unavailable", StringComparison.OrdinalIgnoreCase)
        || message.Contains("dataset input", StringComparison.OrdinalIgnoreCase)
        || message.Contains("ModuleNotFoundError", StringComparison.Ordinal)
        || message.Contains("No such file or directory", StringComparison.Ordinal);

    static string Tail(string text) =>
        text.Length <= StderrTailCharacters ? text.Trim() : text[^StderrTailCharacters..].Trim();

    static string FromSlash(string relative) =>
        relative.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>A refusal this side made, before any process started: it will repeat unchanged.</summary>
    // CHG 2026-09-11 night (slice 2, row 10): a refusal here crossed no
    // execution boundary, and the record says so: it is never a loss and never
    // semantic progress, only a spent try with a deterministic reason.
    static StudyOrchestrator.WorkerOutcome Refused(string message) =>
        new(false, null, CellFailureDetail.Write(
            message, deterministic: true, CellFailureDetail.ClassDeterministic, beforeExecution: true));

    /// <summary>A run that started and did not produce a result. Whether it would repeat is unknown.</summary>
    static StudyOrchestrator.WorkerOutcome Failed(string message, string? stderrTail) =>
        new(false, null, Failure(message, stderrTail));

    static string Failure(string message, string? stderrTail)
    {
        var site = SiteSpecific(message) || SiteSpecific(stderrTail ?? "");
        return CellFailureDetail.Write(
            message, deterministic: false,
            site ? CellFailureDetail.ClassSite : CellFailureDetail.ClassTransient,
            stderrTail: stderrTail, siteSpecific: site);
    }

}
