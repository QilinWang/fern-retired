using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fern.Site;

namespace Fern.Scheduler;

/// <summary>
/// A Gleam program held open as a child process: one JSON document per line
/// in, one per line out, with answers matched to their asks by an id this
/// class mints and pushed <c>event</c> lines handed to whoever is waiting.
///
/// **Held open, not started per question.** Starting an Erlang virtual machine
/// costs a fifth of a second; a scheduler that did it per cycle, or a watch
/// that did it per look, would pay that forever for nothing. One program per
/// thing it serves, for as long as that thing runs.
///
/// Scheduler adapters use this transport for placement, admission and other
/// decisions in <c>fern_scheduler</c>. Nothing here interprets an answer; that
/// belongs to the class that asked.
/// </summary>
public sealed class GleamProgram : IDisposable
{
    readonly Process program;
    readonly ConcurrentDictionary<string, Pending> waiting = new(StringComparer.Ordinal);
    readonly Queue<string> saidOnStandardError = new();
    readonly object writing = new();
    readonly object complaining = new();
    long asked;
    volatile bool ended;

    /// <summary>How much of the program's own output to keep for a refusal message.</summary>
    public const int ComplaintLinesKept = 20;

    sealed class Pending : IDisposable
    {
        public readonly ManualResetEventSlim Answered = new(false);
        public JsonObject? Answer;
        public Action<JsonObject>? OnEvent;
        bool retired;

        public void Complete(JsonObject? answer = null)
        {
            // CHG: a late reply may race the caller's timeout and disposal.
            lock (this)
            {
                if (retired) return;
                if (answer is not null) Answer = answer;
                Answered.Set();
            }
        }

        public void Dispose()
        {
            lock (this)
            {
                retired = true;
                Answered.Dispose();
            }
        }
    }

    GleamProgram(Process program) => this.program = program;

    /// <summary>
    /// Start the program from an Erlang shipment directory — what
    /// <c>gleam export erlang-shipment</c> writes, which needs Erlang on the
    /// machine and nothing else.
    /// </summary>
    /// <remarks>
    /// Run through /bin/sh rather than executed directly: the script Gleam
    /// writes carries its licence header above its `#!` line, so the kernel
    /// sees no interpreter at the first byte and refuses to exec it.
    /// </remarks>
    public static GleamProgram FromShipment(string shipmentDirectory)
    {
        // CHG: explicit shipments also work outside the checkout; refuse a
        // missing entrypoint before starting a shell that cannot serve asks.
        var entrypoint = Path.Combine(Path.GetFullPath(shipmentDirectory), "entrypoint.sh");
        if (!File.Exists(entrypoint))
            throw new InvalidOperationException($"no Gleam shipment entrypoint at {entrypoint}");
        return Start("/bin/sh", [entrypoint, "run"]);
    }

    /// <summary>
    /// The scheduler shipment built beside this repository, or the one
    /// <c>FERN_SCHEDULER_GLEAM_SHIPMENT</c> names. Missing is a failure: a
    /// comparison that passes without the other side is not a comparison.
    /// CHG 2026-09-27: written once here; GleamSchedulerDecision and
    /// GleamCellAdmission each carried a copy of this and of RepositoryRoot.
    /// </summary>
    public static GleamProgram FromRepositoryShipment()
    {
        var shipment = Environment.GetEnvironmentVariable("FERN_SCHEDULER_GLEAM_SHIPMENT")
            ?? Path.Combine(RepositoryRoot(), "tools", "fern-scheduler-gleam", "build", "erlang-shipment");
        if (!File.Exists(Path.Combine(shipment, "entrypoint.sh")))
        {
            throw new InvalidOperationException(
                $"no Gleam scheduler shipment at {shipment}; build it with "
                + "`cd tools/fern-scheduler-gleam && gleam export erlang-shipment`, "
                + "or set FERN_SCHEDULER_GLEAM_SHIPMENT");
        }
        return FromShipment(shipment);
    }

    /// <summary>The repository this binary runs under, found by walking up from it to the one directory <see cref="Fern.Registry.RepositoryLayout.IsRoot"/> recognises.</summary>
    public static string RepositoryRoot() =>
        RepositoryRootOrNull() ?? throw new InvalidOperationException("no repository root above " + AppContext.BaseDirectory);

    /// <summary>The same, or null when this binary runs outside any repository.</summary>
    public static string? RepositoryRootOrNull()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        // CHG 2026-09-26: the solution moved to src/csharp/ (Fern.Registry.RepositoryLayout).
        while (directory is not null && !Fern.Registry.RepositoryLayout.IsRoot(directory.FullName))
        {
            directory = directory.Parent;
        }
        return directory?.FullName;
    }

    public static GleamProgram Start(string program, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var child = Process.Start(start)
            ?? throw new InvalidOperationException($"the operating system started no {program}");
        var site = new GleamProgram(child);
        var reading = new Thread(site.Read) { IsBackground = true, Name = "fern-site-gleam" };
        reading.Start();
        var complaints = new Thread(site.ReadComplaints) { IsBackground = true, Name = "fern-site-gleam-stderr" };
        complaints.Start();
        return site;
    }

    /// <summary>
    /// Ask one thing and wait for its answer, handing every event line that
    /// carries the same id to <paramref name="onEvent"/> as it arrives.
    /// </summary>
    public SiteResult<JsonObject> Ask(JsonObject ask, TimeSpan waitingFor, Action<JsonObject>? onEvent)
    {
        var id = Interlocked.Increment(ref asked).ToString(CultureInfo.InvariantCulture);
        ask["id"] = id;
        var pending = new Pending { OnEvent = onEvent };
        waiting[id] = pending;
        try
        {
            lock (writing)
            {
                if (ended) return Ended<JsonObject>();
                program.StandardInput.Write(ask.ToJsonString());
                program.StandardInput.Write('\n');
                program.StandardInput.Flush();
            }
            if (!pending.Answered.Wait(waitingFor))
            {
                return ended
                    ? Ended<JsonObject>()
                    : SiteResult<JsonObject>.Permanent(
                        $"the site program did not answer ask {id} within {waitingFor}: {ask.ToJsonString()}");
            }
            if (pending.Answer is not { } answer) return Ended<JsonObject>();
            // CHG: protocol refusals are correlated replies too. This is
            // separate from the existing Site worker's domain refusal below.
            if (answer["error"] is JsonObject error)
            {
                return SiteResult<JsonObject>.Permanent(
                    $"the Gleam program refused ask {id}: {error.ToJsonString()}");
            }
            // A refusal is an answer on the wire and a refusal to the caller:
            // the sentence is the one the C# implementation would have
            // recorded, and the kind is what the claim reads.
            if (answer["answer"] is JsonValue said
                && said.TryGetValue<string>(out var word)
                && word == "refused")
            {
                var because = answer["because"] is JsonValue detail
                    && detail.TryGetValue<string>(out var sentence)
                    ? sentence
                    : answer.ToJsonString();
                var kind = answer["kind"] is JsonValue named
                    && named.TryGetValue<string>(out var which)
                    ? which
                    : "permanent";
                return SiteResult<JsonObject>.Refused(kind == "start_deadline"
                    ? SiteFailure.WorkerAdmissionDeadline(because)
                    : SiteFailure.Permanent(because));
            }
            return SiteResult<JsonObject>.Ok(answer);
        }
        catch (IOException error)
        {
            return SiteResult<JsonObject>.Permanent(
                $"the Gleam program could not receive ask {id}: {error.Message}; {Complaints()}");
        }
        finally
        {
            waiting.TryRemove(id, out _);
            pending.Dispose();
        }
    }

    SiteResult<T> Ended<T>() => SiteResult<T>.Permanent(
        "the site program ended; what it said last: " + Complaints());

    /// <summary>The end of the program's own output, which is where its reason is.</summary>
    public string Complaints()
    {
        lock (complaining) return string.Join(" / ", saidOnStandardError);
    }

    void Read()
    {
        try
        {
            while (program.StandardOutput.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonObject? said;
                try
                {
                    said = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    // Anything the program prints that is not a line of this
                    // wire is kept as its own words, not read as an answer.
                    Complain(line);
                    continue;
                }
                if (said is null) continue;
                var id = said["id"] is JsonValue value && value.TryGetValue<string>(out var named) ? named : null;
                if (id is null || !waiting.TryGetValue(id, out var pending))
                {
                    Complain(line);
                    continue;
                }
                if (said.ContainsKey("event"))
                {
                    pending.OnEvent?.Invoke(said);
                    continue;
                }
                pending.Complete(said);
            }
        }
        catch (IOException)
        {
            // The program is gone; every waiting ask is told below.
        }
        finally
        {
            ended = true;
            foreach (var pending in waiting.Values) pending.Complete();
        }
    }

    void ReadComplaints()
    {
        try
        {
            while (program.StandardError.ReadLine() is { } line) Complain(line);
        }
        catch (IOException)
        {
            // Nothing more to hear.
        }
    }

    void Complain(string line)
    {
        lock (complaining)
        {
            saidOnStandardError.Enqueue(line);
            while (saidOnStandardError.Count > ComplaintLinesKept) saidOnStandardError.Dequeue();
        }
    }

    /// <summary>
    /// Close the scheduler program's input and await its exit; end its process
    /// tree if it does not exit within five seconds.
    /// </summary>
    public void Dispose()
    {
        try
        {
            lock (writing)
            {
                ended = true;
                program.StandardInput.Close();
            }
            if (!program.WaitForExit(5000)) program.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        finally
        {
            program.Dispose();
        }
    }
}

public sealed class GleamProgramException(string message) : InvalidOperationException(message);
