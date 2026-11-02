using System.Diagnostics;
using System.Text.Json.Nodes;
using Fern.Registry;

namespace Fern.Scheduler;

/// <summary>
/// Whether a Site may run a cell, answered by the Gleam program under
/// <c>tools/fern-scheduler-gleam</c> instead of by <see cref="Placement"/>.
///
/// One JSON document per line in, one per line out. A program that cannot be
/// started is a failure here, not an admission.
///
/// **The program is held open for as long as this object lives**, through the
/// one transport that also holds the Site's program and the placement decision
/// open (<see cref="GleamProgram"/>). Starting an Erlang virtual machine per
/// question costs about a fifth of a second and buys nothing. Dispose this and
/// the program ends.
///
/// CHG: that ownership applies to <c>Open()</c>. Direct construction borrows
/// the program by default; the outer command owns and disposes that program.
/// </summary>
public sealed class GleamCellAdmission : ICellAdmission, IDisposable
{
    readonly GleamProgram program;
    readonly bool ownsProgram;

    // CHG: adapters borrow a shared program; Open owns the program it starts.
    public GleamCellAdmission(GleamProgram program, bool ownsProgram = false)
    {
        this.program = program;
        this.ownsProgram = ownsProgram;
    }

    public void Dispose()
    {
        if (ownsProgram) program.Dispose();
    }

    /// <summary>
    /// The shipment beside this repository, or <c>FERN_SCHEDULER_GLEAM_SHIPMENT</c>.
    /// Missing fails the comparison; it does not skip it.
    /// </summary>
    public static GleamCellAdmission Open() =>
        new(GleamProgram.FromRepositoryShipment(), ownsProgram: true);

    public Placement.Refusal? Admits(
        IReadOnlyList<string> admittedModels,
        IReadOnlyList<string> allowedHandlers,
        string cellId,
        string model,
        string stageHandler)
    {
        var answer = Ask(new JsonObject
        {
            ["ask"] = "admits",
            ["admitted_models"] = Strings(admittedModels),
            ["allowed_handlers"] = Strings(allowedHandlers),
            ["cell_id"] = cellId,
            ["model"] = model,
            ["stage_handler"] = stageHandler,
        });
        if (answer["admitted"]?.GetValue<bool>() == true) return null;
        return new Placement.Refusal(
            NeedString(answer, "cell_id"),
            NeedString(answer, "model"),
            NeedString(answer, "stage_handler"),
            NeedString(answer, "detail"));
    }

    /// <summary>The compile-time resolution, as the Gleam program computes it.</summary>
    public RegistryResult<PlacementResolution> Resolve(
        IReadOnlyList<PlaceableCell> cells,
        IReadOnlyList<PlaceableSite> sites)
    {
        var answer = Ask(new JsonObject
        {
            ["ask"] = "resolve",
            ["cells"] = Cells(cells),
            ["sites"] = Sites(sites),
        });
        if (answer["ok"]?.GetValue<bool>() == true)
        {
            return RegistryResult<PlacementResolution>.Ok(new PlacementResolution(Map(answer)));
        }
        return RegistryResult<PlacementResolution>.Rejected(new RegistryRefusal(
            NeedString(answer, "outcome_kind"),
            NeedString(answer, "error_category"),
            NeedString(answer, "detail")));
    }

    /// <summary>The fixed model vocabulary, in the order the Gleam program lists it.</summary>
    public IReadOnlyList<string> KnownModels()
    {
        var answer = Ask(new JsonObject { ["ask"] = "known_models" });
        if (answer["models"] is not JsonArray models)
        {
            throw new InvalidOperationException("the Gleam admission answered known models without a list");
        }
        return models.Select(item => item?.GetValue<string>()
            ?? throw new InvalidOperationException("a known model was not a string")).ToList();
    }

    JsonObject Ask(JsonObject ask)
    {
        var answered = program.Ask(ask, TimeSpan.FromSeconds(30), null);
        if (!answered.IsOk)
        {
            throw new GleamProgramException(
                "the Gleam admission did not answer: " + answered.RefusalDetail);
        }
        // The program answers `{"id":…,"answer":{…}}`: the id is the
        // transport's business, the answer is this class's.
        return answered.Value["answer"] as JsonObject
            ?? throw new GleamProgramException(
                "the Gleam admission answered with no answer: "
                + answered.Value.ToJsonString());
    }

    static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    static JsonArray Cells(IReadOnlyList<PlaceableCell> cells)
    {
        var array = new JsonArray();
        foreach (var cell in cells)
        {
            array.Add(new JsonObject
            {
                ["cell_id"] = cell.CellId,
                ["model"] = cell.Model,
                ["stage_handler"] = cell.StageHandler,
            });
        }
        return array;
    }

    static JsonArray Sites(IReadOnlyList<PlaceableSite> sites)
    {
        var array = new JsonArray();
        foreach (var site in sites)
        {
            array.Add(new JsonObject
            {
                ["site_id"] = site.SiteId,
                ["admitted_models"] = Strings(site.AdmittedModels),
                ["allowed_handlers"] = Strings(site.AllowedHandlers),
            });
        }
        return array;
    }

    static IReadOnlyDictionary<string, IReadOnlyList<string>> Map(JsonObject answer)
    {
        if (answer["sites_by_cell"] is not JsonArray rows)
        {
            throw new InvalidOperationException("the Gleam admission resolved without sites_by_cell");
        }
        var byCell = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is not JsonObject cell)
            {
                throw new InvalidOperationException("a resolved cell was not an object");
            }
            if (cell["sites"] is not JsonArray sites)
            {
                throw new InvalidOperationException("a resolved cell named no sites");
            }
            byCell[NeedString(cell, "cell_id")] = sites.Select(item => item?.GetValue<string>()
                ?? throw new InvalidOperationException("a site id was not a string")).ToList();
        }
        return byCell;
    }

    static string NeedString(JsonObject answer, string field)
    {
        if (answer[field] is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        throw new InvalidOperationException($"the Gleam admission's {field} was not a string: {answer.ToJsonString()}");
    }
}
