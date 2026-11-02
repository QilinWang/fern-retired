using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Fern.Site;

/// <summary>
/// The Home's (or a Site's) side of the resident Python cell worker — Program
/// 18, Worker-B/Site-C. The worker itself is <c>python -m fern_stage_runtime
/// serve --root ROOT</c> in the frozen source; this class starts one when none
/// is alive for a root, places cell requests in its inbox, and reads what it
/// leaves behind. The folder is the protocol (see python/fern_stage_runtime/worker.py):
///
///   ROOT/worker.json            who is serving: pid, start time, interpreter, working directory, capability digest
///   ROOT/lifecycle/LAUNCH.worker.json       immutable identity of a replaced lifetime
///   ROOT/lifecycle/LAUNCH.termination.json  typed observation that released that lifetime
///   ROOT/inbox/000017.json      a request, written under a .partial name and renamed
///   ROOT/running/000017.json    claimed by the worker
///   ROOT/receipts/CELL/N-HANDLER.json   one receipt per phase
///   ROOT/log/CELL.txt           the cell's stdout+stderr; tailed for epoch lines, never parsed for control
///   ROOT/stop                   asks the worker to exit after its current cell
///   ROOT/worker.log             the worker's own stdout+stderr
///
/// One root serves one interpreter and one implementation digest; the caller
/// names the root so (…/workers/&lt;environment digest&gt;/&lt;source archive digest&gt;).
/// Nothing here opens a Registry.
/// </summary>
public static class ResidentPythonWorker
{
    public const string WorkerFile = "worker.json";
    public const string InboxDirectory = "inbox";
    public const string RunningDirectory = "running";
    public const string ReceiptsDirectory = "receipts";
    public const string LogDirectory = "log";
    public const string StopFile = "stop";
    public const string WorkerLogFile = "worker.log";
    public const string WorkerLifetimeLockFile = "worker.lock";
    public const string WorkerLifetimeLockProtocol = "posix-flock-exclusive-v1";
    public const string WorkerLifecycleDirectory = "lifecycle";

    /// <summary>
    /// How long <see cref="EnsureRunning"/> waits for <c>worker.json</c> after
    /// spawn. A dead interpreter still fails immediately: process exit is
    /// observed independently. This bound exists so a hung import is a
    /// Site-start failure, not a one-hour training wait.
    ///
    /// CHG 2026-08-31: thirty seconds was shorter than one anomalously slow
    /// common Fern worker-runtime admission on RunPod A4500. The Chronos Cell
    /// merely triggered that fresh worker: before ready, the process imports
    /// fern_train.worker and its shared Torch/Polars/Matplotlib/config graph, not Chronos
    /// weights. spawn.lock 07:53:39Z and ready worker.json 07:55:16Z measured
    /// about 97 seconds; the same day's environment probe was about 121
    /// seconds. The first Cell was mislabeled PhaseTransportLost; its
    /// replacement found the same worker nearly ready. The bound covers that
    /// observed admission tail without declaring it normal model cost.
    /// </summary>
    public static readonly TimeSpan ImportDeadline = TimeSpan.FromMinutes(3);
    public const string WorkerTerminationSchema = "fern.worker_termination.v1";
    const string SpawnLockFile = "spawn.lock";
    const string HighestSequenceFile = ".highest-sequence";
    const int SequenceWidth = 6;

    /// <summary>
    /// What <c>worker.json</c> claims about the process serving a root. It is
    /// knowledge, not current ownership: <see cref="EnsureRunning"/> also
    /// requires matching start ticks and the held process-lifetime lock.
    /// </summary>
    public sealed record WorkerIdentity(
        long Pid,
        long ProcessStartTicks,
        string StartedAt,
        string Interpreter,
        string WorkingDirectory,
        string CapabilityDigest,
        string State,
        string LaunchId,
        string LockProtocol,
        string Root,
        string? Failure);

    /// <summary>
    /// One monotone worker-admission observation. It is process/setup
    /// progress, not a scientific phase receipt. The Site transports it to
    /// Home so the public monitor can distinguish a live cold import from a
    /// missing worker or a lost machine session.
    /// </summary>
    public sealed record WorkerStartupObservation(
        string State,
        WorkerIdentity Identity,
        long ElapsedMs);

    public static bool IsAlive(long pid) => pid > 0 && Directory.Exists($"/proc/{pid}");

    /// <summary>The Linux process-start tick used with PID to reject PID reuse.</summary>
    public static long? ReadProcessStartTicks(long pid)
    {
        if (pid <= 0) return null;
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var close = stat.LastIndexOf(')');
            if (close < 0 || close + 2 >= stat.Length) return null;
            var fieldsAfterName = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fieldsAfterName.Length > 19
                && long.TryParse(fieldsAfterName[19], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                    ? ticks
                    : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    static bool IsSameLiveProcess(WorkerIdentity identity) =>
        ReadProcessStartTicks(identity.Pid) is long observed && observed == identity.ProcessStartTicks;

    public static SiteResult<WorkerIdentity> ReadIdentity(string root)
    {
        var path = Path.Combine(root, WorkerFile);
        if (!File.Exists(path))
        {
            return SiteResult<WorkerIdentity>.Permanent($"no worker.json under {root}");
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var element = document.RootElement;
            var identity = new WorkerIdentity(
                element.GetProperty("pid").GetInt64(),
                element.GetProperty("process_start_ticks").GetInt64(),
                element.GetProperty("started_at").GetString() ?? string.Empty,
                element.GetProperty("interpreter").GetString() ?? string.Empty,
                element.GetProperty("working_directory").GetString() ?? string.Empty,
                element.GetProperty("capability_digest").ValueKind is JsonValueKind.Null
                    ? string.Empty
                    : element.GetProperty("capability_digest").GetString() ?? string.Empty,
                element.GetProperty("state").GetString() ?? string.Empty,
                element.GetProperty("launch_id").GetString() ?? string.Empty,
                element.GetProperty("lock_protocol").GetString() ?? string.Empty,
                element.GetProperty("root").GetString() ?? string.Empty,
                element.TryGetProperty("failure", out var failure) && failure.ValueKind == JsonValueKind.String
                    ? failure.GetString()
                    : null);
            if (identity.ProcessStartTicks <= 0
                || string.IsNullOrWhiteSpace(identity.LaunchId)
                || identity.LockProtocol != WorkerLifetimeLockProtocol
                || identity.State is not ("starting" or "ready" or "failed")
                || Path.GetFullPath(identity.Root) != Path.GetFullPath(root)
                || (identity.State == "ready" && string.IsNullOrWhiteSpace(identity.CapabilityDigest)))
            {
                return SiteResult<WorkerIdentity>.Permanent(
                    $"worker.json under {root} has no valid {WorkerLifetimeLockProtocol} lifetime-owner identity");
            }
            return SiteResult<WorkerIdentity>.Ok(identity);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or IOException or InvalidOperationException)
        {
            return SiteResult<WorkerIdentity>.Permanent($"worker.json under {root} is not readable: {error.Message}");
        }
    }

    enum LifetimeOwnership
    {
        Free,
        HeldByAnotherProcess,
        Unavailable,
    }

    // Linux flock(2), shared with fern_stage_runtime.worker. Fern's resident
    // Site worker already depends on /proc and /bin/sh, so this is an explicit
    // executor boundary rather than a portable-filesystem pretense.
    const int LockExclusive = 2;
    const int LockNonBlocking = 4;
    const int LockUnlock = 8;
    const int TryAgain = 11;
    const int AccessDenied = 13;
    const int OpenReadWrite = 2;
    const int OpenCreate = 64;
    const int OwnerReadWrite = 384; // 0600

    [DllImport("libc", SetLastError = true, EntryPoint = "flock")]
    static extern int Flock(int fileDescriptor, int operation);

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    static extern int Open(string path, int flags, int mode);

    [DllImport("libc", SetLastError = true, EntryPoint = "close")]
    static extern int Close(int fileDescriptor);

    static LifetimeOwnership ProbeLifetimeOwnership(string root)
    {
        try
        {
            var descriptor = Open(
                Path.Combine(root, WorkerLifetimeLockFile),
                OpenReadWrite | OpenCreate,
                OwnerReadWrite);
            if (descriptor < 0) return LifetimeOwnership.Unavailable;
            try
            {
                if (Flock(descriptor, LockExclusive | LockNonBlocking) == 0)
                {
                    _ = Flock(descriptor, LockUnlock);
                    return LifetimeOwnership.Free;
                }
                var error = Marshal.GetLastPInvokeError();
                return error is TryAgain or AccessDenied
                    ? LifetimeOwnership.HeldByAnotherProcess
                    : LifetimeOwnership.Unavailable;
            }
            finally
            {
                _ = Close(descriptor);
            }
        }
        catch (IOException)
        {
            return LifetimeOwnership.Unavailable;
        }
        catch (UnauthorizedAccessException)
        {
            return LifetimeOwnership.Unavailable;
        }
    }

    static long? DeclaredLivePid(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var pid = document.RootElement.GetProperty("pid").GetInt64();
            return IsAlive(pid) ? pid : null;
        }
        catch (Exception error) when (
            error is JsonException or KeyNotFoundException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Preserve the exact stale identity before a replacement launch overwrites
    /// <c>worker.json</c>. The retained identity and typed observation say why
    /// its process-lifetime ownership ended; a later launch has a distinct
    /// launch id and cannot make the old process look current again.
    /// </summary>
    static SiteResult<bool> ArchiveEndedWorkerLifecycle(string root, WorkerIdentity identity)
    {
        try
        {
            var workerPath = Path.Combine(root, WorkerFile);
            var workerBytes = File.ReadAllBytes(workerPath);
            using (var document = JsonDocument.Parse(workerBytes))
            {
                var element = document.RootElement;
                if (element.GetProperty("pid").GetInt64() != identity.Pid
                    || element.GetProperty("process_start_ticks").GetInt64() != identity.ProcessStartTicks
                    || !string.Equals(
                        element.GetProperty("launch_id").GetString(),
                        identity.LaunchId,
                        StringComparison.Ordinal))
                {
                    return SiteResult<bool>.Permanent(
                        "worker.json changed while its stale lifecycle was being archived; reconcile the root before replacement");
                }
            }

            var observedStartTicks = ReadProcessStartTicks(identity.Pid);
            if (observedStartTicks == identity.ProcessStartTicks)
            {
                return SiteResult<bool>.Permanent(
                    $"worker {identity.Pid} became live again while its lifecycle was being archived; reconcile the root before replacement");
            }

            var lifecycleDirectory = Path.Combine(root, WorkerLifecycleDirectory);
            Directory.CreateDirectory(lifecycleDirectory);
            var identityFileName = identity.LaunchId + ".worker.json";
            WriteImmutableFile(Path.Combine(lifecycleDirectory, identityFileName), workerBytes);
            var outcome = observedStartTicks is null ? "process_exited" : "pid_reused";
            var terminationBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = WorkerTerminationSchema,
                launch_id = identity.LaunchId,
                pid = identity.Pid,
                process_start_ticks = identity.ProcessStartTicks,
                observed_process_start_ticks = observedStartTicks,
                outcome,
                worker_identity_file = identityFileName,
            });
            WriteImmutableFile(
                Path.Combine(lifecycleDirectory, identity.LaunchId + ".termination.json"),
                terminationBytes);
            return SiteResult<bool>.Ok(true);
        }
        catch (Exception error) when (
            error is IOException
                or JsonException
                or KeyNotFoundException
                or InvalidOperationException
                or UnauthorizedAccessException)
        {
            return SiteResult<bool>.Permanent(
                $"cannot archive stale worker lifecycle under {root}: {error.Message}");
        }
    }

    static void WriteImmutableFile(string path, byte[] bytes)
    {
        if (File.Exists(path))
        {
            RequireSameBytes(path, bytes);
            return;
        }

        var partial = path + "." + Guid.NewGuid().ToString("n") + ".partial";
        try
        {
            File.WriteAllBytes(partial, bytes);
            try
            {
                File.Move(partial, path, overwrite: false);
            }
            catch (IOException)
            {
                if (!File.Exists(path))
                {
                    throw;
                }
                RequireSameBytes(path, bytes);
            }
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    static void RequireSameBytes(string path, byte[] expected)
    {
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
        {
            throw new IOException($"immutable lifecycle evidence at {path} differs from the worker identity it names");
        }
    }

    /// <summary>
    /// A live worker for this root, serving exactly <paramref name="sourceRoot"/>:
    /// the ready process-lifetime owner already there, or one started now and
    /// waited for through its starting-to-ready identity (the wait covers the
    /// torch import). The short C# spawn lock serializes admission and launch;
    /// the separate POSIX worker.lock belongs to the Python process for its
    /// whole lifetime and alone permits identity publication or request claim.
    /// </summary>
    public static SiteResult<WorkerIdentity> EnsureRunning(
        string root,
        string python,
        string sourceRoot,
        TimeSpan startDeadline,
        Action<WorkerStartupObservation>? observeStartup,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        Directory.CreateDirectory(root);
        var expectedRoot = Path.GetFullPath(sourceRoot);
        var deadline = Stopwatch.StartNew();
        string? lastObservedLaunch = null;
        string? lastObservedState = null;
        WorkerIdentity? lastStartingIdentity = null;

        void Observe(WorkerIdentity identity)
        {
            if (identity.State == "starting")
            {
                lastStartingIdentity = identity;
            }
            if (identity.LaunchId == lastObservedLaunch && identity.State == lastObservedState)
            {
                return;
            }
            lastObservedLaunch = identity.LaunchId;
            lastObservedState = identity.State;
            observeStartup?.Invoke(new WorkerStartupObservation(
                identity.State,
                identity,
                (long)deadline.Elapsed.TotalMilliseconds));
        }
        FileStream? held = null;
        try
        {
            while (held is null)
            {
                try
                {
                    held = new FileStream(
                        Path.Combine(root, SpawnLockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    if (deadline.Elapsed > startDeadline)
                    {
                        return SiteResult<WorkerIdentity>.Permanent(
                            $"another process has held the worker spawn lock under {root} for {startDeadline}");
                    }
                    Thread.Sleep(100);
                }
            }

            var existing = ReadIdentity(root);
            if (existing.IsOk && IsSameLiveProcess(existing.Value))
            {
                if (Path.GetFullPath(existing.Value.WorkingDirectory) != expectedRoot)
                {
                    return SiteResult<WorkerIdentity>.Permanent(
                        $"worker root {root} is served by pid {existing.Value.Pid} from {existing.Value.WorkingDirectory}, "
                        + $"not {expectedRoot}; one root serves one implementation");
                }
                var ownership = ProbeLifetimeOwnership(root);
                if (ownership != LifetimeOwnership.HeldByAnotherProcess)
                {
                    return SiteResult<WorkerIdentity>.Permanent(
                        $"worker.json under {root} names live {existing.Value.State} pid {existing.Value.Pid}, "
                        + "but its process-lifetime owner lock is not held; reconcile the root before starting another worker");
                }
                Observe(existing.Value);
                if (existing.Value.State == "ready") return existing;
                if (existing.Value.State == "failed")
                {
                    return SiteResult<WorkerIdentity>.Permanent(
                        $"worker root {root} is held by failed worker pid {existing.Value.Pid}; wait for exit or reconcile it");
                }
            }
            else if (!existing.IsOk && DeclaredLivePid(Path.Combine(root, WorkerFile)) is long legacyPid)
            {
                return SiteResult<WorkerIdentity>.Permanent(
                    $"worker.json under {root} names live pid {legacyPid} but has no valid lifetime-owner identity; "
                    + "stop or reconcile that legacy worker before replacement");
            }
            else if (existing.IsOk)
            {
                var archive = ArchiveEndedWorkerLifecycle(root, existing.Value);
                if (!archive.IsOk)
                {
                    return SiteResult<WorkerIdentity>.Permanent(archive.Failure.Detail);
                }
            }

            var existingStarting = existing.IsOk
                && IsSameLiveProcess(existing.Value)
                && existing.Value.State == "starting";
            var previousLaunch = existing.IsOk ? existing.Value.LaunchId : string.Empty;

            if (!existingStarting)
            {
                var ownership = ProbeLifetimeOwnership(root);
                if (ownership == LifetimeOwnership.HeldByAnotherProcess)
                {
                    return SiteResult<WorkerIdentity>.Permanent(
                        $"worker root {root} has an active process-lifetime owner but no readable ready identity; reconcile it");
                }
                if (ownership == LifetimeOwnership.Unavailable)
                {
                    return SiteResult<WorkerIdentity>.Permanent(
                        $"the process-lifetime owner lock under {root} could not be inspected");
                }
            }

            var log = Path.Combine(root, WorkerLogFile);
            long launchedPid;
            if (existingStarting)
            {
                launchedPid = existing.Value.Pid;
            }
            else
            {
                File.Delete(Path.Combine(root, StopFile));
                var start = new ProcessStartInfo("/bin/sh")
                {
                    UseShellExecute = false,
                    WorkingDirectory = expectedRoot,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                };
                // Detached: its own session when setsid exists, stdin from /dev/null,
                // both streams appended to worker.log. The worker outlives this
                // process by design; `stop` in the root is what ends it.
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(
                    "if command -v setsid >/dev/null 2>&1; then "
                    + "exec setsid \"$0\" -m fern_stage_runtime serve --root \"$1\" </dev/null >>\"$2\" 2>&1; "
                    + "else exec \"$0\" -m fern_stage_runtime serve --root \"$1\" </dev/null >>\"$2\" 2>&1; fi");
                start.ArgumentList.Add(python);
                start.ArgumentList.Add(root);
                start.ArgumentList.Add(log);
                start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
                // Redirected stdout is fully buffered unless this is set, so a
                // timeout used to report "its log ends:" with an empty file
                // while torch was still importing.
                start.Environment["PYTHONUNBUFFERED"] = "1";
                // CHG 2026-09-26: src/ joins the import roots, because the Serie
                // package moved from serie/ to src/serie/; a source installed
                // before the move still finds serie/ at its root first.
                start.Environment["PYTHONPATH"] = expectedRoot + Path.PathSeparator + Path.Combine(expectedRoot, "python")
                    + Path.PathSeparator + Path.Combine(expectedRoot, "src");
                // CHG 2026-09-11 night (slice 4 of the cell port, row 20): the
                // launcher may add what the runtime reads from its environment
                // (the installed fern-run and fern-compile it calls back into,
                // the card it may see); the three above stay the worker's own.
                if (environment is not null)
                {
                    foreach (var (name, value) in environment)
                    {
                        if (name is "PYTHONDONTWRITEBYTECODE" or "PYTHONUNBUFFERED" or "PYTHONPATH") continue;
                        start.Environment[name] = value;
                    }
                }
                using var process = Process.Start(start);
                if (process is null)
                {
                    return SiteResult<WorkerIdentity>.Permanent("the operating system started no shell for the worker");
                }
                // The shell immediately execs setsid (or Python itself), so
                // this remains the worker pid until worker.json publishes the
                // authoritative identity.
                launchedPid = process.Id;
            }

            while (true)
            {
                var identity = ReadIdentity(root);
                if (identity.IsOk
                    && (existingStarting || identity.Value.LaunchId != previousLaunch)
                    && identity.Value.Pid == launchedPid)
                {
                    Observe(identity.Value);
                }
                if (identity.IsOk
                    && (existingStarting || identity.Value.LaunchId != previousLaunch)
                    && identity.Value.Pid == launchedPid
                    && identity.Value.State == "failed")
                {
                    return SiteResult<WorkerIdentity>.Permanent(
                        $"resident worker {identity.Value.Pid} published failed startup identity"
                        + (string.IsNullOrWhiteSpace(identity.Value.Failure) ? string.Empty : $": {identity.Value.Failure}")
                        + $"; its log ends: {TailOf(log, 2048)}");
                }
                if (identity.IsOk
                    && (existingStarting || identity.Value.LaunchId != previousLaunch)
                    && IsSameLiveProcess(identity.Value))
                {
                    if (Path.GetFullPath(identity.Value.WorkingDirectory) != expectedRoot)
                    {
                        return SiteResult<WorkerIdentity>.Permanent(
                            $"the worker started under {root} serves {identity.Value.WorkingDirectory}, not {expectedRoot}");
                    }
                    if (ProbeLifetimeOwnership(root) != LifetimeOwnership.HeldByAnotherProcess)
                    {
                        return SiteResult<WorkerIdentity>.Permanent(
                            $"worker {identity.Value.Pid} published {identity.Value.State} identity without holding "
                            + "the process-lifetime owner lock");
                    }
                    if (identity.Value.State == "ready") return identity;
                    if (identity.Value.State == "failed")
                    {
                        return SiteResult<WorkerIdentity>.Permanent(
                            $"resident worker {identity.Value.Pid} failed during startup; its log ends: " + TailOf(log, 2048));
                    }
                }
                if (!IsAlive(launchedPid))
                {
                    return SiteResult<WorkerIdentity>.Permanent(
                        $"the resident worker process {launchedPid} exited before writing worker.json; its log ends: "
                        + TailOf(log, 2048));
                }
                if (deadline.Elapsed > startDeadline)
                {
                    if (lastStartingIdentity is { } starting && IsSameLiveProcess(starting))
                    {
                        return SiteResult<WorkerIdentity>.Refused(SiteFailure.WorkerAdmissionDeadline(
                            $"resident worker launch {starting.LaunchId} pid {starting.Pid} remained starting "
                            + $"and did not become ready within {startDeadline}; process_liveness=alive; its log ends: "
                            + TailOf(log, 2048)));
                    }
                    return SiteResult<WorkerIdentity>.Permanent(
                        $"the worker under {root} wrote no worker.json within {startDeadline}; its log ends: "
                        + TailOf(log, 2048));
                }
                Thread.Sleep(200);
            }
        }
        finally
        {
            held?.Dispose();
        }
    }

    /// <summary>
    /// Place one cell request in the inbox under the next sequence number:
    /// written as .partial, moved to its final name (a move refuses an
    /// existing name, so two writers never share a number).
    /// </summary>
    public static SiteResult<string> Submit(string root, string cellRequestJson)
    {
        var inbox = Path.Combine(root, InboxDirectory);
        var running = Path.Combine(root, RunningDirectory);
        var receipts = Path.Combine(root, ReceiptsDirectory);
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(running);
        Directory.CreateDirectory(receipts);
        var taken = new List<long>();
        foreach (var folder in new[] { inbox, running })
        {
            foreach (var name in Directory.EnumerateFiles(folder).Select(Path.GetFileName))
            {
                if (name is not null && TryParseSequence(name, out var number))
                {
                    taken.Add(number);
                }
            }
        }
        var highest = Path.Combine(receipts, HighestSequenceFile);
        if (File.Exists(highest) && long.TryParse(File.ReadAllText(highest).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var recorded))
        {
            taken.Add(recorded);
        }
        var next = (taken.Count == 0 ? 0 : taken.Max()) + 1;
        while (true)
        {
            var final = Path.Combine(inbox, SequenceName(next));
            var partial = final + ".partial";
            File.WriteAllText(partial, cellRequestJson);
            try
            {
                File.Move(partial, final, overwrite: false);
            }
            catch (IOException)
            {
                File.Delete(partial);
                next++;
                continue;
            }
            File.WriteAllText(highest + ".partial", next.ToString(CultureInfo.InvariantCulture) + "\n");
            File.Move(highest + ".partial", highest, overwrite: true);
            return SiteResult<string>.Ok(final);
        }
    }

    public static string SequenceName(long number) => number.ToString(CultureInfo.InvariantCulture).PadLeft(SequenceWidth, '0') + ".json";

    static bool TryParseSequence(string name, out long number)
    {
        number = 0;
        return name.Length == SequenceWidth + 5
            && name.EndsWith(".json", StringComparison.Ordinal)
            && long.TryParse(name.AsSpan(0, SequenceWidth), NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    public static string PhaseReceiptPath(string root, string cellId, long ordinal, string handler) =>
        Path.Combine(root, ReceiptsDirectory, cellId, $"{ordinal}-{handler}.json");

    public static string CellLogPath(string root, string cellId) => Path.Combine(root, LogDirectory, cellId + ".txt");

    /// <summary>Ask the worker to exit after its current cell.</summary>
    public static void RequestStop(string root) => File.WriteAllText(Path.Combine(root, StopFile), string.Empty);

    /// <summary>
    /// End exactly the resident-worker lifetime captured by the caller. A PID
    /// alone is never cancellation authority: immediately before each signal,
    /// the current root identity, Linux start ticks, and held worker lock must
    /// still name this launch. Drift is a typed refusal, never a signal to the
    /// process currently using a recycled PID.
    /// </summary>
    public static SiteResult<bool> Kill(WorkerIdentity expected)
    {
        var beforeTerm = VerifyCurrentLifetimeForCancellation(expected);
        if (!beforeTerm.IsOk)
        {
            return beforeTerm;
        }
        var term = Signal(expected.Pid, "TERM");
        if (!term.IsOk)
        {
            return term;
        }

        for (var waited = 0;
             waited < 50 && ReadProcessStartTicks(expected.Pid) == expected.ProcessStartTicks;
             waited++)
        {
            Thread.Sleep(100);
        }
        if (ReadProcessStartTicks(expected.Pid) != expected.ProcessStartTicks)
        {
            return SiteResult<bool>.Ok(true);
        }

        var beforeKill = VerifyCurrentLifetimeForCancellation(expected);
        if (!beforeKill.IsOk)
        {
            return beforeKill;
        }
        return Signal(expected.Pid, "KILL");
    }

    static SiteResult<bool> VerifyCurrentLifetimeForCancellation(WorkerIdentity expected)
    {
        if (string.IsNullOrWhiteSpace(expected.Root)
            || !Path.IsPathFullyQualified(expected.Root)
            || string.IsNullOrWhiteSpace(expected.LaunchId)
            || expected.Pid <= 0
            || expected.ProcessStartTicks <= 0)
        {
            return SiteResult<bool>.Refused(SiteFailure.Unclassified(
                "resident worker cancellation needs an absolute root, launch id, PID, and process-start ticks"));
        }

        var current = ReadIdentity(expected.Root);
        if (!current.IsOk)
        {
            return SiteResult<bool>.Refused(SiteFailure.Unclassified(
                "resident worker cancellation cannot read its current lifetime identity: " + current.Failure.Detail));
        }
        if (!SameWorkerLifetime(expected, current.Value))
        {
            return SiteResult<bool>.Refused(SiteFailure.Unclassified(
                $"resident worker cancellation refused: root {expected.Root} now names launch {current.Value.LaunchId} "
                + $"pid {current.Value.Pid} start ticks {current.Value.ProcessStartTicks}, not requested launch "
                + $"{expected.LaunchId} pid {expected.Pid} start ticks {expected.ProcessStartTicks}"));
        }

        var observedStartTicks = ReadProcessStartTicks(expected.Pid);
        if (observedStartTicks != expected.ProcessStartTicks)
        {
            return SiteResult<bool>.Refused(SiteFailure.Unclassified(
                $"resident worker cancellation refused: pid {expected.Pid} has process-start ticks "
                + $"{observedStartTicks?.ToString(CultureInfo.InvariantCulture) ?? "absent"}, not {expected.ProcessStartTicks}"));
        }

        var ownership = ProbeLifetimeOwnership(expected.Root);
        if (ownership != LifetimeOwnership.HeldByAnotherProcess)
        {
            return SiteResult<bool>.Refused(SiteFailure.Unclassified(
                $"resident worker cancellation refused: launch {expected.LaunchId} has no held {WorkerLifetimeLockFile} owner"));
        }
        return SiteResult<bool>.Ok(true);
    }

    static bool SameWorkerLifetime(WorkerIdentity expected, WorkerIdentity current) =>
        expected.Pid == current.Pid
        && expected.ProcessStartTicks == current.ProcessStartTicks
        && string.Equals(expected.LaunchId, current.LaunchId, StringComparison.Ordinal)
        && string.Equals(expected.LockProtocol, current.LockProtocol, StringComparison.Ordinal)
        && string.Equals(
            Path.GetFullPath(expected.Root),
            Path.GetFullPath(current.Root),
            StringComparison.Ordinal);

    static SiteResult<bool> Signal(long pid, string signal)
    {
        try
        {
            var start = new ProcessStartInfo("/bin/kill") { UseShellExecute = false };
            start.ArgumentList.Add("-s");
            start.ArgumentList.Add(signal);
            start.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
            using var process = Process.Start(start);
            if (process is null)
            {
                return SiteResult<bool>.Refused(SiteFailure.Unclassified(
                    $"the operating system started no /bin/kill process for worker pid {pid}"));
            }
            if (!process.WaitForExit(5000))
            {
                return SiteResult<bool>.Refused(SiteFailure.Unclassified(
                    $"/bin/kill did not return within five seconds for worker pid {pid}"));
            }
            if (process.ExitCode != 0)
            {
                return SiteResult<bool>.Refused(SiteFailure.Unclassified(
                    $"/bin/kill refused {signal} for worker pid {pid} with exit {process.ExitCode}"));
            }
            return SiteResult<bool>.Ok(true);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return SiteResult<bool>.Refused(SiteFailure.Unclassified(
                $"cannot start /bin/kill for worker pid {pid}: {error.Message}"));
        }
    }

    /// <summary>One resident worker as a person sees it (Registry-D, `fern-site workers`).</summary>
    public sealed record WorkerListing(
        string Root,
        string InterpreterFolder,
        string ImplementationFolder,
        WorkerIdentity? Identity,
        bool Alive,
        int Waiting,
        string? RunningCell,
        string? LastReceipt);

    /// <summary>
    /// Every worker root under a machine's work root, from the files alone:
    /// worker.json, inbox/, running/ and the newest receipt. Readers only —
    /// this is the listing the folder protocol makes trivial.
    /// </summary>
    public static IReadOnlyList<WorkerListing> ListWorkers(string workRoot)
    {
        var workers = Path.Combine(workRoot, "workers");
        var listings = new List<WorkerListing>();
        if (!Directory.Exists(workers))
        {
            return listings;
        }
        foreach (var interpreter in Directory.EnumerateDirectories(workers).Order(StringComparer.Ordinal))
        {
            foreach (var root in Directory.EnumerateDirectories(interpreter).Order(StringComparer.Ordinal))
            {
                var identity = ReadIdentity(root);
                var inbox = Path.Combine(root, InboxDirectory);
                var running = Path.Combine(root, RunningDirectory);
                var waiting = Directory.Exists(inbox)
                    ? Directory.EnumerateFiles(inbox, "*.json").Count(file => !file.EndsWith(".partial", StringComparison.Ordinal))
                    : 0;
                string? runningCell = null;
                if (Directory.Exists(running))
                {
                    var claimed = Directory.EnumerateFiles(running, "*.json").Order(StringComparer.Ordinal).FirstOrDefault();
                    if (claimed is not null)
                    {
                        try
                        {
                            using var document = JsonDocument.Parse(File.ReadAllBytes(claimed));
                            runningCell = document.RootElement.GetProperty("cell_id").GetString();
                        }
                        catch (Exception error) when (error is JsonException or IOException or KeyNotFoundException)
                        {
                            runningCell = Path.GetFileName(claimed);
                        }
                    }
                }
                string? lastReceipt = null;
                var receipts = Path.Combine(root, ReceiptsDirectory);
                if (Directory.Exists(receipts))
                {
                    var newest = Directory.EnumerateFiles(receipts, "*.json", SearchOption.AllDirectories)
                        .Where(file => !Path.GetFileName(file).StartsWith("request-", StringComparison.Ordinal))
                        .Select(file => new FileInfo(file))
                        .OrderByDescending(file => file.LastWriteTimeUtc)
                        .FirstOrDefault();
                    if (newest is not null)
                    {
                        lastReceipt = Path.GetRelativePath(receipts, newest.FullName).Replace(Path.DirectorySeparatorChar, '/')
                            + " at " + newest.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture);
                    }
                }
                listings.Add(new WorkerListing(
                    root,
                    Path.GetFileName(interpreter),
                    Path.GetFileName(root),
                    identity.IsOk ? identity.Value : null,
                    identity.IsOk && IsAlive(identity.Value.Pid),
                    waiting,
                    runningCell,
                    lastReceipt));
            }
        }
        return listings;
    }

    /// <summary>A hard link when the filesystem allows it, a copy otherwise; never a symlink, which the runner refuses.</summary>
    public static SiteResult<bool> HardLinkOrCopy(string source, string destination)
    {
        try
        {
            // .NET has no hard-link call; libc's is the one the earlier Site
            // code used. A non-zero return (cross-device, a filesystem without
            // links) falls back to a copy.
            if (NativeLink(source, destination) != 0)
            {
                File.Copy(source, destination, overwrite: true);
            }
            return SiteResult<bool>.Ok(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return SiteResult<bool>.Permanent($"cannot place {source} at {destination}: {error.Message}");
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "link", SetLastError = true)]
    static extern int NativeLink(string from, string to);

    public static string TailOf(string path, int bytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = stream.Length;
            var take = (int)Math.Min(bytes, length);
            stream.Seek(length - take, SeekOrigin.Begin);
            var buffer = new byte[take];
            var read = stream.Read(buffer, 0, take);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Reads what a growing file gained since the last call. Starts at the
    /// file's current end when constructed with <paramref name="fromEnd"/>, so
    /// a cell that is appended to an existing log is read from its own start.
    /// </summary>
    public sealed class GrowingFileReader
    {
        readonly string path;
        long offset;

        public GrowingFileReader(string path, bool fromEnd)
        {
            this.path = path;
            if (fromEnd && File.Exists(path))
            {
                offset = new FileInfo(path).Length;
            }
        }

        public byte[] ReadNew()
        {
            if (!File.Exists(path))
            {
                return [];
            }
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length <= offset)
                {
                    return [];
                }
                stream.Seek(offset, SeekOrigin.Begin);
                var buffer = new byte[stream.Length - offset];
                var read = stream.Read(buffer, 0, buffer.Length);
                offset += read;
                return read == buffer.Length ? buffer : buffer[..read];
            }
            catch (IOException)
            {
                return [];
            }
        }
    }
}
