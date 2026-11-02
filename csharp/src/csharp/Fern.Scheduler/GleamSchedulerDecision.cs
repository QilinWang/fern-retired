using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Fern.Scheduler;

/// <summary>
/// The same placement decision, answered by the Gleam program under
/// <c>tools/fern-scheduler-gleam</c> instead of by <see cref="SchedulerDecision"/>.
///
/// One JSON document per line in, one per line out. What comes back is read as
/// placements and reasons; a program that cannot be started, or that answers
/// with anything else, is a failure here and not an empty decision.
///
/// **The program is held open for as long as this object lives**, through the
/// one transport that also holds the Site's program open
/// (<see cref="GleamProgram"/>). It used to be started afresh for every
/// question, which costs an Erlang virtual machine — about a fifth of a second
/// — each time. A scheduler asks this once per cycle forever, so that was a
/// cost with nothing bought by it. Dispose this and the program ends.
///
/// CHG: that ownership applies to <c>Open()</c>. Direct construction borrows
/// the program by default; the outer command owns and disposes that program.
/// </summary>
public sealed class GleamSchedulerDecision : ISchedulerDecision, IDisposable
{
    readonly GleamProgram program;
    readonly bool ownsProgram;

    // CHG: an injected program belongs to the outer run and may also serve
    // cell admission. Only Open's private program is disposed by this adapter.
    public GleamSchedulerDecision(GleamProgram program, bool ownsProgram = false)
    {
        this.program = program;
        this.ownsProgram = ownsProgram;
    }

    public void Dispose()
    {
        if (ownsProgram) program.Dispose();
    }

    /// <summary>
    /// The shipment built beside this repository, or <c>FERN_SCHEDULER_GLEAM_SHIPMENT</c>
    /// when that is set. Missing is a failure: a comparison that passes without
    /// the other side is not a comparison.
    /// </summary>
    public static GleamSchedulerDecision Open() =>
        new(GleamProgram.FromRepositoryShipment(), ownsProgram: true);

    public SchedulerDecision.Decision Choose(
        IReadOnlyList<SchedulerDecision.CellDemand> runnable,
        IReadOnlyList<SchedulerDecision.SiteOffer> sites,
        IReadOnlyDictionary<long, int> busySlotsBySite)
    {
        var ask = new JsonObject
        {
            ["ask"] = "choose",
            ["runnable"] = Cells(runnable),
            ["sites"] = Sites(sites),
            ["busy"] = Busy(busySlotsBySite),
        };
        var answer = Ask(ask);
        try { return Decision(answer); }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
        {
            throw new GleamProgramException("the Gleam decision returned an invalid answer: " + error.Message);
        }
    }

    /// <summary>The offer parsed by Gleam from the same two JSON texts the C# reader is given.</summary>
    /// <remarks>CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): no held sources.</remarks>
    public SchedulerDecision.SiteOffer ReadOffer(
        long siteId, string name, string capabilitiesJson, string capacityJson)
    {
        var ask = new JsonObject
        {
            ["ask"] = "read_offer",
            ["site_id"] = siteId,
            ["name"] = name,
            ["capabilities"] = capabilitiesJson,
            ["capacity"] = capacityJson,
        };
        var answer = Ask(ask);
        return new SchedulerDecision.SiteOffer(
            NeedLong(answer, "site_id"),
            NeedString(answer, "name"),
            Strings(answer, "admitted_models"),
            NeedString(answer, "device"),
            checked((int)NeedLong(answer, "slots")));
    }

    /// <summary>The demand parsed by Gleam from the same two JSON texts the C# reader is given.</summary>
    public SchedulerDecision.CellDemand ReadDemand(
        long cellId, long studyId, long ordinal, string name, string configJson, string requirementsJson, long sourceId)
    {
        var ask = new JsonObject
        {
            ["ask"] = "read_demand",
            ["cell_id"] = cellId,
            ["study_id"] = studyId,
            ["ordinal"] = ordinal,
            ["name"] = name,
            ["config"] = configJson,
            ["requirements"] = requirementsJson,
            ["source_id"] = sourceId,
        };
        var answer = Ask(ask);
        return new SchedulerDecision.CellDemand(
            NeedLong(answer, "cell_id"),
            NeedLong(answer, "study_id"),
            NeedLong(answer, "ordinal"),
            NeedString(answer, "name"),
            NeedString(answer, "model_type"),
            NeedString(answer, "device"),
            NeedBool(answer, "requires_cuda"),
            NeedLong(answer, "source_id"));
    }

    JsonObject Ask(JsonObject ask)
    {
        var answered = program.Ask(ask, TimeSpan.FromSeconds(30), null);
        if (!answered.IsOk)
        {
            throw new GleamProgramException(
                "the Gleam decision did not answer: " + answered.RefusalDetail);
        }
        // The program answers `{"id":…,"answer":{…}}`: the id is the
        // transport's business, the answer is this class's.
        return answered.Value["answer"] as JsonObject
            ?? throw new GleamProgramException(
                "the Gleam decision answered with no answer: "
                + answered.Value.ToJsonString());
    }

    static JsonArray Cells(IReadOnlyList<SchedulerDecision.CellDemand> cells)
    {
        var array = new JsonArray();
        foreach (var cell in cells)
        {
            array.Add(new JsonObject
            {
                ["cell_id"] = cell.CellId,
                ["study_id"] = cell.StudyId,
                ["ordinal"] = cell.Ordinal,
                ["name"] = cell.Name,
                ["model_type"] = cell.ModelType,
                ["device"] = cell.Device,
                ["requires_cuda"] = cell.RequiresCuda,
                ["source_id"] = cell.SourceId,
            });
        }
        return array;
    }

    static JsonArray Sites(IReadOnlyList<SchedulerDecision.SiteOffer> sites)
    {
        var array = new JsonArray();
        foreach (var site in sites)
        {
            array.Add(new JsonObject
            {
                ["site_id"] = site.SiteId,
                ["name"] = site.Name,
                ["admitted_models"] = Strings(site.AdmittedModels),
                ["device"] = site.Device,
                ["slots"] = site.Slots,
            });
        }
        return array;
    }

    static JsonArray Busy(IReadOnlyDictionary<long, int> busy)
    {
        var array = new JsonArray();
        foreach (var (siteId, count) in busy)
        {
            array.Add(new JsonObject { ["site_id"] = siteId, ["count"] = count });
        }
        return array;
    }

    static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    static SchedulerDecision.Decision Decision(JsonObject answer)
    {
        if (answer["placements"] is not JsonArray placements || answer["unplaced"] is not JsonArray unplaced)
        {
            throw new InvalidOperationException(
                "the Gleam decision answered without placements and unplaced: " + answer.ToJsonString());
        }
        return new SchedulerDecision.Decision(
            placements.Select(ReadPlacement).ToList(),
            unplaced.Select(ReadUnplaced).ToList());
    }

    static SchedulerDecision.Placement ReadPlacement(JsonNode? node)
    {
        if (node is not JsonObject row)
        {
            throw new InvalidOperationException("a placement was not an object");
        }
        return new SchedulerDecision.Placement(NeedLong(row, "cell_id"), NeedLong(row, "site_id"), NeedString(row, "because"));
    }

    static SchedulerDecision.Unplaced ReadUnplaced(JsonNode? node)
    {
        if (node is not JsonObject row)
        {
            throw new InvalidOperationException("an unplaced cell was not an object");
        }
        return new SchedulerDecision.Unplaced(NeedLong(row, "cell_id"), NeedString(row, "because"));
    }

    static IReadOnlyList<string> Strings(JsonObject answer, string field)
    {
        if (answer[field] is not JsonArray array)
        {
            throw new InvalidOperationException($"the Gleam decision's {field} was not a list");
        }
        return array.Select(item => item?.GetValue<string>()
            ?? throw new InvalidOperationException($"the Gleam decision's {field} held a non-string")).ToList();
    }

    static long NeedLong(JsonObject answer, string field)
    {
        if (answer[field] is JsonValue value && value.TryGetValue<long>(out var number)) return number;
        throw new InvalidOperationException($"the Gleam decision's {field} was not an integer: {answer.ToJsonString()}");
    }

    static string NeedString(JsonObject answer, string field)
    {
        if (answer[field] is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        throw new InvalidOperationException($"the Gleam decision's {field} was not a string: {answer.ToJsonString()}");
    }

    static bool NeedBool(JsonObject answer, string field)
    {
        if (answer[field] is JsonValue value && value.TryGetValue<bool>(out var flag)) return flag;
        throw new InvalidOperationException($"the Gleam decision's {field} was not a bool: {answer.ToJsonString()}");
    }
}
