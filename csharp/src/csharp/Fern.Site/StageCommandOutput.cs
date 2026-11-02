using System.Text;
using System.Text.Json;

namespace Fern.Site;

/// <summary>
/// Keeps the useful END of a Stage runner's output without letting a noisy
/// runner grow the reading process without bound. The end is the part that says
/// why the run failed; the beginning is banners.
///
/// PUBLIC, AND THE ONE OWNER OF THIS FACT. Two routes run a Stage runner: a
/// Worker Site runs the packaged runner and returns its tail inside the return
/// package, and Registry Home runs one directly for a local Site. They keep the
/// same 16 KiB, under the same name, in the same place — and if that lived in
/// two classes it would stop being the same the first time either moved. This
/// type lives in Fern.Site because Fern.Site already owns the cap
/// (<see cref="SitePaths.MaximumCommandOutputDetailBytes"/>) and the file name
/// (<see cref="SitePaths.RunnerOutputTailFile"/>), and Fern.Scheduler already
/// references Fern.Site.
/// </summary>
public sealed class CommandOutputTail
{
    readonly List<byte> retained = new();

    public IReadOnlyList<byte> Bytes => retained;

    /// <summary>
    /// Persist this tail beside the runner's report, under the attempt's own
    /// root, on EVERY attempt — success or failure.
    ///
    /// A silent runner leaves no file: the return package refuses an empty
    /// member, and absence already says "nothing was printed". A write failure
    /// is not a Stage failure — the receipt and the outputs decide the attempt,
    /// and this is diagnostics beside them.
    /// </summary>
    public void WriteBeside(string attemptRoot)
    {
        if (retained.Count == 0)
        {
            return;
        }

        var path = Path.Combine(
            attemptRoot,
            SitePaths.RunnerOutputTailFile.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, retained.ToArray());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    public string Detail
    {
        get
        {
            var text = Encoding.UTF8.GetString(retained.ToArray()).Trim();
            return text.Length == 0 ? string.Empty : ": " + text;
        }
    }

    public void Write(ReadOnlySpan<byte> incoming)
    {
        if (incoming.Length >= SitePaths.MaximumCommandOutputDetailBytes)
        {
            retained.Clear();
            retained.AddRange(incoming[^SitePaths.MaximumCommandOutputDetailBytes..].ToArray());
            return;
        }
        var overflow = retained.Count + incoming.Length - SitePaths.MaximumCommandOutputDetailBytes;
        if (overflow > 0)
        {
            retained.RemoveRange(0, overflow);
        }
        retained.AddRange(incoming.ToArray());
    }
}

/// <summary>
/// Retains bounded failure detail while forwarding ONLY the small typed
/// progress protocol. Arbitrary trainer prose never crosses the Site boundary,
/// and a partial write cannot turn half a JSON object into progress: a line is
/// assembled across writes, and a line longer than its cap is dropped whole
/// rather than truncated into something that might parse.
/// </summary>
sealed class StageCommandOutput
{
    readonly List<byte> pending = new();
    bool droppingLine;
    string lastForwarded = string.Empty;

    internal CommandOutputTail Tail { get; } = new();

    /// <summary>Where a recognised progress line is repeated. Null forwards nothing.</summary>
    internal TextWriter? Progress { get; init; }

    internal void Write(ReadOnlySpan<byte> incoming)
    {
        Tail.Write(incoming);
        while (incoming.Length > 0)
        {
            var newline = incoming.IndexOf((byte)'\n');
            if (newline < 0)
            {
                if (!droppingLine && pending.Count + incoming.Length <= SitePaths.MaximumStageProgressLineBytes)
                {
                    pending.AddRange(incoming.ToArray());
                }
                else
                {
                    pending.Clear();
                    droppingLine = true;
                }
                break;
            }
            var piece = incoming[..newline];
            incoming = incoming[(newline + 1)..];
            if (droppingLine || pending.Count + piece.Length > SitePaths.MaximumStageProgressLineBytes)
            {
                pending.Clear();
                droppingLine = false;
                continue;
            }
            pending.AddRange(piece.ToArray());
            ForwardProgressLine(pending);
            pending.Clear();
        }
    }

    /// <summary>
    /// Offer the last unterminated line. A runner that exits without a final
    /// newline still reports the epoch it reached.
    /// </summary>
    internal void Flush()
    {
        if (!droppingLine && pending.Count > 0)
        {
            ForwardProgressLine(pending);
        }
        pending.Clear();
        droppingLine = false;
    }

    void ForwardProgressLine(List<byte> line)
    {
        var text = Encoding.UTF8.GetString(line.ToArray());
        if (Progress is null || !text.StartsWith(SitePaths.StageProgressPrefix, StringComparison.Ordinal))
        {
            return;
        }
        var body = text[SitePaths.StageProgressPrefix.Length..];
        string? schema;
        string? kind;
        long current;
        long total;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            schema = ReadString(document.RootElement, "schema");
            kind = ReadString(document.RootElement, "kind");
            current = ReadNumber(document.RootElement, "current");
            total = ReadNumber(document.RootElement, "total");
        }
        catch (JsonException)
        {
            // Go's Unmarshal failing is silence, not a Site failure: the runner
            // is allowed to print whatever it likes on stdout.
            return;
        }
        // CHG: phase clocks have their own schema and never fabricate epoch counters.
        if (StagePhaseObservation.Parse(body) is null &&
            (schema != "fern.training_progress.v1" || kind != "training_epoch" || current < 1 || total < current))
        {
            return;
        }
        if (text == lastForwarded)
        {
            return;
        }
        Progress.Write(text + "\n");
        lastForwarded = text;
    }

    static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static long ReadNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : 0;
}

/// <summary>Finite scientific/operational milestones, independent of training epochs.</summary>
public enum StageWorkPhase
{
    compilation, preparation, training, validation, first_epoch_diagnostics,
    epoch_test, checkpoint_save, final_evaluation, finalization, summary,
    publication, collection, queue_wait, admission, dispatch, transfer
}
public enum StagePhaseState { started, completed, failed }

public sealed record StagePhaseObservation(string OperationId, long Sequence, long Span,
    StageWorkPhase Phase, StagePhaseState State, double Seconds)
{
    public static StagePhaseObservation? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema)
                || schema.GetString() != "fern.phase_progress.v1") return null;
            var operation = root.GetProperty("operation_id").GetString();
            if (string.IsNullOrWhiteSpace(operation) || operation.Length > 256
                || !root.GetProperty("sequence").TryGetInt64(out var sequence) || sequence < 1
                || !root.GetProperty("span").TryGetInt64(out var span) || span < 1 || span > sequence
                || !Enum.TryParse<StageWorkPhase>(root.GetProperty("phase").GetString(), out var phase) || !Enum.IsDefined(phase)
                || !Enum.TryParse<StagePhaseState>(root.GetProperty("state").GetString(), out var state) || !Enum.IsDefined(state)
                || !root.GetProperty("seconds").TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds < 0
                || state == StagePhaseState.started && (seconds != 0 || span != sequence)) return null;
            return new(operation, sequence, span, phase, state, seconds);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }
}
