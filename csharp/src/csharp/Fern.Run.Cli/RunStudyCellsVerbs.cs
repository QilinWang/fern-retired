using System.CommandLine;
using Fern.Registry;
using Fern.Scheduler;

namespace Fern.Run.Cli;

/// <summary>
/// `run-study-cells`: run a registered study's cells on this machine until it
/// has none left to run.
///
/// It is the loop over <see cref="StudyOrchestrator.RunOnce"/> with the
/// production worker. One cycle reads the runnable cells, decides where each
/// goes, claims, runs and records; the verb repeats that until a cycle claims
/// nothing, which is the study being finished, paused, cancelled, or blocked
/// on a Site that admits none of its cells. Each of those four is said in
/// words rather than left as an empty loop.
///
/// The name is subject-verb-object and says the unit: it RUNS the CELLS of a
/// STUDY. `run-study` already registers one; this is what makes the registered
/// cells run.
/// </summary>
public static class RunStudyCellsVerbs
{
    public static void Add(Command parent)
    {
        parent.Subcommands.Add(RunStudyCells());
    }

    static Command RunStudyCells()
    {
        var store = new Option<string?>("--store")
        {
            Description = "Registry Home holding the study, as the file on this machine. One of --store and --home.",
        };
        // CHG 2026-09-11 night (slice 5, pull): a supervisor on another machine
        // names the Home by the address serve-registry-home answers at, and
        // every store operation crosses that wire instead of opening the file.
        var home = new Option<string?>("--home")
        {
            Description = "Registry Home holding the study, as the address serve-registry-home answers at on the machine "
                        + "that holds the file; on the pod that is the SSH tunnel's remote forward, "
                        + $"{HomeService.DefaultListen}. One of --store and --home: a supervisor on another machine "
                        + "pulls its cells through the Home rather than opening the file, which SQLite forbids over a "
                        + "network filesystem.",
        };
        var study = new Option<string?>("--study")
        {
            Description = "The registered study's name. Required without --serve; a resident supervisor (--serve) "
                        + "may omit it and then serves every study in state run, because the office it holds is "
                        + "the Site's, not a study's.",
        };
        var site = new Option<string>("--site")
        {
            Description = "The registered Site this machine is. Only cells placed on it are claimed, because this command runs what it claims here.",
            Required = true,
        };
        var python = new Option<string>("--python") { Description = "The prepared environment's interpreter.", Required = true };
        var fernRun = new Option<string?>("--fern-run") { Description = "The installed fern-run the trainer calls back into. Default: the selected runtime's." };
        var fernCompile = new Option<string?>("--fern-compile") { Description = "The card compiler (the packed Gleam program), which mints a configuration's identity. Default: the selected runtime's, so a machine never mints with a compiler from another commit." };
        // CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
        // slice 4): --source-compiler (a source root and the compiler for it) and
        // --deploy-root (install a missing source before the next cycle) are
        // retired with installed sources and carried compilers.
        var workRoot = new Option<string>("--work-root") { Description = "Where each attempt's request and result are written.", Required = true };
        var resumeCell = new Option<string[]>("--resume-cell")
        {
            Description = "Explicit CELL=ARTIFACT_PREFIX of a stopped attempt on this Site; copies its complete epoch into a new native attempt without changing the registered source or configuration. Requires --study.",
            AllowMultipleArgumentsPerToken = true,
        };
        var resultsRoot = new Option<string?>("--results-root")
        {
            Description = "The selected results directory this machine writes into. When given, each cell's results "
                        + "document is saved into the science store under it the moment the cell ends, and the "
                        + "attempt records where it went rather than a copy. Omitted means not sent: the cell hands "
                        + "its document back, which is what a machine that cannot reach the results tree does. "
                        + "There is no default — a results root is a selection, never a guess.",
        };
        var checkpointStore = new Option<string?>("--checkpoint-store")
        {
            Description = "Root of the checkpoint store a cell that evaluates a checkpoint by name reads. "
                        + "Default: store/ beside the Registry Home, which is where install-control-plane puts it.",
        };
        var cellTimeout = new Option<int>("--cell-timeout-seconds")
        {
            // CHG 2026-09-26 (slice W3): the budget no longer ends a cell.
            Description = "The time one cell is given. Nothing ends a cell for running past it (owner, 2026-09-26: "
                        + "a time budget only warns); the Home tells a person once when a training passes 24 hours.",
            DefaultValueFactory = _ => (int)LocalCellWorker.DefaultCellTimeout.TotalSeconds,
        };
        var serve = new Option<bool>("--serve")
        {
            Description = "Stay and keep working: reconcile, claim and run every cycle, waiting when there is nothing "
                        + "to claim, until the study is finished or an operator pauses or cancels it. Without it the "
                        + "command returns as soon as a cycle claims nothing, which is what a script and the route "
                        + "test want. NOTE: serving re-claims a cell it just failed, so a cell that keeps failing "
                        + "spends its --max-attempts budget within seconds rather than across separate runs; that is "
                        + "the automatic retry working, and the budget is what bounds it.",
        };
        var idleSeconds = new Option<int>("--idle-seconds")
        {
            Description = "How long --serve waits after an idle cycle before looking again.",
            DefaultValueFactory = _ => 30,
        };
        // CHG 2026-09-11 night (slice 4 of the cell port): the warm resident
        // worker, fed by its folder inbox, so the torch import is paid once per
        // machine and card rather than once per cell. Off until the live canary
        // on the 4070 says the default flips.
        var warmWorker = new Option<bool>("--warm-worker")
        {
            Description = "Run cells through one resident Python worker per interpreter, source and card (fern_stage_runtime serve) "
                        + "instead of one process per cell; the worker's startup and the trainer's progress are recorded as events.",
        };
        var schedulerProgram = new Option<string?>("--scheduler-program")
        {
            Description = "Explicit Erlang shipment for scheduler decisions: the whole scheduling round, which cell is claimed next, "
                        + "what a refusal changes and why a cell waits. Held open for this run; failure never falls back to C#. Omit to use C#.",
        };
        // CHG 2026-09-11 night (slice 1 of the cell port): --serve claims the
        // Site's office and holds it under a lease renewed every cycle; a
        // second supervisor of the same Site is refused by name and exits 1,
        // which the unit retries; a stop addressed to the Site is consumed
        // between cells and exits 0, which stays down.
        var leaseSeconds = new Option<int>("--lease-seconds")
        {
            Description = "How long the office of this Site stays this process's between renewals under --serve. "
                        + "A supervisor that stops renewing for this long is unconfirmed, and a successor may take the Site.",
            DefaultValueFactory = _ => (int)RegistryOperations.DefaultSupervisorLeaseSeconds,
        };
        var maxAttempts = new Option<int>("--max-attempts")
        {
            Description = "How many attempts one cell may spend before a person decides. Overrides the study's own "
                        + "declaration; without either, three.",
            DefaultValueFactory = _ => 0,
        };
        var maxCycles = new Option<int>("--max-cycles")
        {
            Description = "Stop after this many cycles even if cells remain; 0 runs until nothing is claimed.",
            DefaultValueFactory = _ => 0,
        };

        var command = new Command("run-study-cells",
            "Run a registered study's cells on this machine until none is runnable. Every outcome is recorded in the store.");
        command.Options.Add(resumeCell);
        foreach (var option in new Option[] { store, home, study, site, python, fernRun, fernCompile, workRoot, resultsRoot, checkpointStore, cellTimeout, serve, idleSeconds, leaseSeconds, warmWorker, schedulerProgram, maxAttempts, maxCycles })
        {
            command.Options.Add(option);
        }

        command.SetAction(parsed =>
        {
            var recoveryPrefixes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var selection in parsed.GetValue(resumeCell) ?? [])
            {
                var separator = selection.IndexOf('=');
                if (string.IsNullOrWhiteSpace(parsed.GetValue(study)) || separator <= 0
                    || separator == selection.Length - 1
                    || !recoveryPrefixes.TryAdd(selection[..separator], selection[(separator + 1)..]))
                {
                    Console.Error.WriteLine("run-study-cells: --resume-cell requires --study and a unique CELL=ARTIFACT_PREFIX");
                    return 3;
                }
            }
            var storePath = parsed.GetValue(store);
            var homeAddress = parsed.GetValue(home);
            if ((storePath is null) == (homeAddress is null))
            {
                Console.Error.WriteLine(
                    "run-study-cells: name the Home once, as --store <file> on the machine that holds it or as --home <url> from another machine");
                return 3;
            }
            using var writer = storePath is null ? null : RegistryHome.OpenWriter(storePath);
            using var client = homeAddress is null ? null : new HomeClient(new Uri(homeAddress), log: Console.Error);
            IStudyStore selectedStore = writer is not null ? new SqliteStudyStore(writer) : client!;
            var serving = parsed.GetValue(serve);
            var studyName = parsed.GetValue(study);
            if (studyName is null && !serving)
            {
                Console.Error.WriteLine("run-study-cells: --study is required without --serve; a one-shot run is one study's");
                return 3;
            }
            long? id = null;
            if (studyName is not null)
            {
                id = selectedStore.FindStudy(studyName);
                if (id is null)
                {
                    Console.Error.WriteLine($"run-study-cells: no study named {studyName} is registered");
                    return 3;
                }
            }

            if (recoveryPrefixes.Count != 0)
            {
                if (parsed.GetValue(warmWorker) || !string.IsNullOrEmpty(parsed.GetValue(resultsRoot))
                    || !selectedStore.ReadStudy(id!.Value).TryGetValue(out var recoveryStudy))
                {
                    Console.Error.WriteLine("run-study-cells: recovery requires a readable Study, cold worker and Home-returned summaries");
                    return 3;
                }
                using var recoveryDocument = System.Text.Json.JsonDocument.Parse(recoveryStudy.CompiledStudyJson);
                var names = recoveryDocument.RootElement.GetProperty("cells").EnumerateArray()
                    .Select(cell => cell.GetProperty("cell").GetString()).ToHashSet(StringComparer.Ordinal);
                if (recoveryPrefixes.Keys.Any(name => !names.Contains(name)))
                {
                    Console.Error.WriteLine("run-study-cells: a recovery selection names a cell absent from the registered Study");
                    return 3;
                }
            }

            // The selected runtime supplies whichever binary the caller did not name;
            // read once, so both come from the same selection.
            var selected = parsed.GetValue(fernRun) is null || parsed.GetValue(fernCompile) is null
                ? Fern.Registry.RuntimeSelection.Read() : null;
            // CHG: the command owns one decision program across every cycle.
            // Adapters borrow it; no per-cell VM and no implicit checkout path.
            var schedulerShipment = parsed.GetValue(schedulerProgram);
            if (schedulerShipment is not null && !File.Exists(Path.Combine(schedulerShipment, "entrypoint.sh")))
            {
                Console.Error.WriteLine($"run-study-cells: no scheduler program at {schedulerShipment}");
                return 3;
            }
            using var decisionProgram = schedulerShipment is null ? null : GleamProgram.FromShipment(schedulerShipment);
            ISchedulerDecision? decision = decisionProgram is null ? null : new GleamSchedulerDecision(decisionProgram);
            // CHG 2026-09-25: the same program also runs the scheduling round
            // (slices C4–C6): with it, every choice after the reading is Gleam's.
            var round = decisionProgram is null ? null : new GleamStudyRound(decisionProgram, parsed.GetValue(site)!);
            var worker = new LocalCellWorker(selectedStore, new LocalCellWorker.Placement(
                Python: parsed.GetValue(python)!,
                FernRunBinary: parsed.GetValue(fernRun) ?? selected!.RunBinary,
                FernCompileBinary: parsed.GetValue(fernCompile) ?? selected!.CompileBinary,
                WorkRoot: parsed.GetValue(workRoot)!,
                Timeout: TimeSpan.FromSeconds(parsed.GetValue(cellTimeout)),
                CheckpointStore: parsed.GetValue(checkpointStore) ?? DefaultCheckpointStore(storePath, parsed.GetValue(workRoot)!),
                ResultsRoot: parsed.GetValue(resultsRoot) ?? "",
                WarmWorker: parsed.GetValue(warmWorker),
                RecoveryPrefixes: recoveryPrefixes,
                // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 3).
                CodeFromHome: writer is null));

            var declared = parsed.GetValue(maxAttempts) > 0
                ? parsed.GetValue(maxAttempts)
                : id is { } one ? selectedStore.StudyAttemptBudget(one) : RegistryOperations.DefaultMaxAttempts;
            using var stopping = new CancellationTokenSource();
            Console.CancelKeyPress += (_, key) =>
            {
                // Ctrl-C ends the serving loop between cycles instead of killing
                // a training cell: the attempt stays the supervisor's until it
                // finishes or goes silent.
                key.Cancel = true;
                stopping.Cancel();
            };
            // The office is claimed only when serving: a one-shot run is a script's
            // or a test's, and holds no duty a successor would have to take over.
            var identity = serving
                ? new StudyOrchestrator.SupervisorIdentity(Environment.MachineName, Environment.ProcessId, RunningBinaryDigest())
                : null;
            if (client is not null)
            {
                try
                {
                    Console.Out.WriteLine($"run-study-cells: the Home at {client.Home} serves the file {client.HealthHome()}");
                }
                catch (HomeUnreachableException error)
                {
                    Console.Error.WriteLine($"run-study-cells: {error.Message}");
                    return StudyOrchestrator.ExitRetryable;
                }
            }
            try
            {
                return StudyOrchestrator.RunUntilIdle(
                    selectedStore, worker, id, studyName, parsed.GetValue(site)!, parsed.GetValue(maxCycles),
                    Console.Out, Console.Error, declared,
                    serving ? TimeSpan.FromSeconds(parsed.GetValue(idleSeconds)) : null,
                    stopping.Token, identity, parsed.GetValue(leaseSeconds), implementedBy: decision,
                    round: round);
            }
            catch (HomeUnreachableException error)
            {
                // The wire went away for the whole retry window. A retryable
                // exit: the unit restarts this supervisor, which claims the
                // office again when the tunnel is back; the cells it held go
                // silent and are reconciled as lost, the same as a crash.
                Console.Error.WriteLine($"run-study-cells: {error.Message}; exiting to be retried");
                return StudyOrchestrator.ExitRetryable;
            }
            catch (GleamProgramException error)
            {
                Console.Error.WriteLine($"run-study-cells: selected Gleam scheduler failed: {error.Message}");
                return StudyOrchestrator.ExitRetryable;
            }
        });
        return command;
    }

    /// <summary>
    /// Where a checkpoint named by a card is looked up when the caller names no
    /// store: <c>store/</c> beside the Registry Home, the directory
    /// <c>install-control-plane</c> creates from one root. A machine that never
    /// installed one still names this path, so a cell asking for a checkpoint is
    /// told there is no such checkpoint rather than that a field is missing.
    /// </summary>
    /// <summary>
    /// store/ beside the Registry Home, which is where install-control-plane
    /// puts it; a machine that reaches the Home by address has no file beside
    /// which to look, so its default is store/ under its own work root.
    /// </summary>
    static string DefaultCheckpointStore(string? registryPath, string workRoot) =>
        registryPath is null
            ? Path.Combine(workRoot, "store")
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(registryPath)) ?? ".", "store");

    /// <summary>
    /// The digest of the executable this supervisor runs as, recorded on its
    /// office row so a reader can tell which build minded a machine. Unknown
    /// when the runtime does not expose its own path, never invented.
    /// </summary>
    static string RunningBinaryDigest()
    {
        var path = Environment.ProcessPath;
        if (path is null || !File.Exists(path))
        {
            return "unknown";
        }
        using var stream = File.OpenRead(path);
        return "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
