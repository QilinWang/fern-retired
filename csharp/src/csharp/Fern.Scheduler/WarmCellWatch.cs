using Fern.Install;
using Fern.Site;

namespace Fern.Scheduler;

/// <summary>
/// One cell on a warm worker, as a watch is given it: which root feeds the
/// worker, which lifetime must still be serving that root while it runs, what
/// this cell is called inside the root, the request the worker reads, and how
/// long it may take.
/// </summary>
public sealed record WarmCell(
    string Root,
    ResidentPythonWorker.WorkerIdentity Worker,
    string CellId,
    string RequestJson,
    TimeSpan Timeout);

/// <summary>How a watch ended.</summary>
public abstract record WatchResult
{
    /// <summary>The cell reached the worker and was watched to an end.</summary>
    public sealed record Ran(BoundedProbe.Outcome Outcome) : WatchResult;

    /// <summary>
    /// Nothing was executed: the cell never reached the worker's inbox. This
    /// crossed no execution boundary, which is what the attempt's record says.
    /// </summary>
    public sealed record NotPlaced(string Detail) : WatchResult;
}

/// <summary>
/// Put one cell on a warm worker and watch it to its end.
///
/// The watch owns four questions and nothing else: has the worker written the
/// cell's receipt, is the worker still alive, has the time allowed run out, and
/// has somebody asked for this to stop. It reports the trainer's own progress
/// as it reads it, ends the worker lifetime holding a cell that ran out of time
/// or was cancelled, and takes the request off the queue so a successor worker
/// cannot resume an attempt already settled.
///
/// It touches no Registry. Reading a cell's report, recording its events and
/// beating for it belong to <see cref="LocalCellWorker"/>, which is where the
/// Registry connection is and where it stays.
///
/// CHG 2026-09-23 (slice F of docs/programs/2026-09-22-gleam-site-supervision): this is an
/// interface because a second implementation answers it — the Gleam program on
/// the Erlang virtual machine, behind the same wire its worker host already
/// speaks. <see cref="ResidentFolderCellWatch"/> is the loop that was inside
/// <c>LocalCellWorker.RunWarm</c>, moved here unchanged.
/// </summary>
public interface IWarmCellWatch
{
    WatchResult Watch(WarmCell cell, Action<StageCheckpoint> accepted, CancellationToken cancellation);
}

/// <summary>
/// The watch as this binary does it: look at the root's receipts folder four
/// times a second until one of four things has happened.
/// </summary>
public sealed class ResidentFolderCellWatch : IWarmCellWatch
{
    readonly IResidentWorkerHost resident;

    public ResidentFolderCellWatch(IResidentWorkerHost resident) => this.resident = resident;

    /// <summary>How often the receipts folder is looked at again.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public WatchResult Watch(WarmCell cell, Action<StageCheckpoint> accepted, CancellationToken cancellation)
    {
        var submitted = resident.Submit(cell.Root, cell.RequestJson);
        if (!submitted.IsOk)
        {
            return new WatchResult.NotPlaced(
                $"the cell could not be placed in the resident worker's inbox under {cell.Root}: {submitted.RefusalDetail}");
        }
        var sequence = submitted.Value;
        var receipts = Path.Combine(cell.Root, ResidentPythonWorker.ReceiptsDirectory);
        var done = Path.Combine(receipts, cell.CellId + ".done");
        var failedPath = Path.Combine(receipts, cell.CellId + ".failed.json");
        var log = ResidentPythonWorker.CellLogPath(cell.Root, cell.CellId);
        var progress = new ProgressTail(log);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            // Read the log before deciding, so a cell that finishes between two
            // looks still has its last progress reported before its end.
            ForwardProgress(progress, accepted);
            if (File.Exists(done))
            {
                return new WatchResult.Ran(new BoundedProbe.Outcome.Completed(0, [], ""));
            }
            if (File.Exists(failedPath))
            {
                return new WatchResult.Ran(new BoundedProbe.Outcome.Completed(1, [], FailedMessage(failedPath)));
            }
            if (cancellation.IsCancellationRequested)
            {
                EndWorkerCell(cell, sequence);
                return new WatchResult.Ran(new BoundedProbe.Outcome.Cancelled(clock.ElapsedMilliseconds));
            }
            if (!resident.IsAlive(cell.Worker))
            {
                RetireRunningRequest(cell.Root, sequence, cell.CellId);
                return new WatchResult.Ran(new BoundedProbe.Outcome.Completed(137, [],
                    $"the resident worker pid {cell.Worker.Pid} died while the cell ran; its log is {log}"));
            }
            // CHG 2026-09-26 (slice W3 of leases-and-watching-a-running-cell):
            // the cell's time no longer ends its worker; a training ends when it
            // ends, or when a person cancels it (LocalCellWorker.NoDeadlineMs).
            Thread.Sleep(PollInterval);
        }
    }

    /// <summary>End the worker lifetime that holds this cell, and take the cell off its inbox so a successor does not resume it.</summary>
    void EndWorkerCell(WarmCell cell, string sequence)
    {
        _ = resident.Kill(cell.Worker);
        RetireRunningRequest(cell.Root, sequence, cell.CellId);
    }

    /// <summary>
    /// A request still under running/ after its worker died or was ended would
    /// be resumed by the next worker of that root, and the attempt it belonged
    /// to has already been settled; move it beside the cell's receipts as the
    /// worker itself does when a cell ends.
    /// </summary>
    internal static void RetireRunningRequest(string root, string sequence, string cellId)
    {
        var running = Path.Combine(root, ResidentPythonWorker.RunningDirectory, sequence);
        var inbox = Path.Combine(root, ResidentPythonWorker.InboxDirectory, sequence);
        var receipts = Path.Combine(root, ResidentPythonWorker.ReceiptsDirectory);
        foreach (var candidate in new[] { running, inbox })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                Directory.CreateDirectory(receipts);
                File.Move(candidate, Path.Combine(receipts, cellId + ".request-" + Path.GetFileNameWithoutExtension(sequence) + ".json"), overwrite: true);
            }
            catch (IOException)
            {
                // The worker moved it first; either way it is off the queue.
            }
        }
    }

    internal static string FailedMessage(string failedPath)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(failedPath));
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String ? c.GetString() : null;
            var message = root.TryGetProperty("message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String ? m.GetString() : null;
            return $"{code ?? "failure"}: {message ?? "the worker recorded no message"}";
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException)
        {
            return $"the worker's failed.json could not be read: {error.Message}";
        }
    }

    /// <summary>What has been read of the cell's log, and the last checkpoint accepted from it.</summary>
    sealed class ProgressTail(string path)
    {
        public string Path { get; } = path;
        public long Offset;
        public StageCheckpoint? Accepted;
    }

    /// <summary>
    /// Read the log's new bytes and report every checkpoint that moves
    /// forward. Liveness is the beat; progress is only what the trainer
    /// itself printed, and a checkpoint that does not advance is ignored,
    /// exactly as the retired transport accepted them.
    /// </summary>
    static void ForwardProgress(ProgressTail tail, Action<StageCheckpoint> accepted)
    {
        if (!File.Exists(tail.Path)) return;
        string chunk;
        try
        {
            using var stream = new FileStream(tail.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= tail.Offset) return;
            stream.Position = tail.Offset;
            var buffer = new byte[stream.Length - tail.Offset];
            var read = stream.Read(buffer, 0, buffer.Length);
            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', Math.Max(0, read - 1));
            if (lastNewline < 0) return;
            chunk = System.Text.Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
            tail.Offset += lastNewline + 1;
        }
        catch (IOException)
        {
            return;
        }
        foreach (var line in chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (StagePackageTransport.ParseStageCheckpoint(line.TrimEnd('\r')) is not { } observed) continue;
            if (StagePackageTransport.AcceptMonotoneCheckpoint(tail.Accepted, observed) is not { } moved) continue;
            tail.Accepted = moved;
            accepted(moved);
        }
    }
}
