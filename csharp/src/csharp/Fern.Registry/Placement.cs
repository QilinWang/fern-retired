namespace Fern.Registry;

/// <summary>
/// Which Worker Site may run which cell.
///
/// A Site declares a PLACEMENT RULE: the experiment models it admits, and the
/// Stage handlers it may run. A cell is admissible on a Site when the Site
/// admits the cell's model AND allows the cell's Stage handler. Both halves
/// must pass — a machine that can run the handler but not the model would
/// claim work it cannot compute, and a machine that knows the model but lacks
/// the handler cannot execute the Stage at all.
///
/// EMPTY ADMITS NOTHING, in both lists. A Site that has not said what it can
/// run is not a Site that can run anything, and that direction is deliberate:
/// the failure of a too-narrow rule is cells that visibly queue, while the
/// failure of a too-wide default is a machine quietly claiming work it will
/// fail. The first is a question someone asks; the second is a wasted GPU hour.
///
/// This function is PURE. It reads a Site row and a cell and returns a verdict;
/// it opens nothing and writes nothing, so the scheduler and the compiler can
/// both ask it and get the same answer.
/// </summary>
public static class Placement
{
    /// <summary>
    /// Every model name the config resolver knows. GENERATED from the case
    /// labels of `ConfigResolver`'s model switch and checked against that
    /// source by a test, so a model added there and not here is a failure
    /// rather than a Site that can never admit it.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownModelTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "action_state_lstm",
        "action_state_gru",
        "vit_predictor",
        "visual_dynamics",
        "state_probe",
        "conditional_nsf",
        "conditional_gaussian",
        "conditional_profiti",
        "conditional_tactis2",
        "odd_residual",
        "conditional_realnvp",
        "conditional_mdn",
        "conditional_ddpm", "conditional_ddim", "conditional_rectified_flow",
        "robot_dynamics_diffusion",
        "diffusion_policy",
        "attraos",
        "chronos2",
        "chronos_bolt_base",
        "chronos_bolt_small",
        "crossformer",
        "dlinear",
        "duet",
        "fern",
        "film",
        "fits",
        "frets",
        "hi",
        "itransformer",
        "koopa",
        "lightts",
        "moderntcn",
        "mtsmixer",
        "naive",
        "patchtst",
        "pfnn",
        "residual_mlp",
        "mac_chunk_mlp",
        "segrnn",
        "softs",
        "sparsetsf",
        "stemgnn",
        "tide",
        "timemixer",
        "timesfm_2p5_200m",
        "timesnet",
        "timexer",
        "tirex",
        "zeus",
    };

    /// <summary>Why a Site may not run a cell; null when it may.</summary>
    public sealed record Refusal(string CellId, string Model, string StageHandler, string Detail);

    /// <summary>
    /// May this Site run this cell? The refusal names the CELL and the RULE,
    /// never the Site — a Site is not at fault for declaring what it is, and an
    /// operator reading the refusal needs to know which cell cannot be placed
    /// and what the rule said, not that a machine exists.
    /// </summary>
    public static Refusal? Admits(
        IReadOnlyList<string> admittedModels,
        IReadOnlyList<string> allowedHandlers,
        string cellId,
        string model,
        string stageHandler)
    {
        if (!admittedModels.Contains(model, StringComparer.Ordinal))
        {
            var declared = admittedModels.Count == 0
                ? "no models at all"
                : "models " + string.Join(", ", admittedModels.OrderBy(name => name, StringComparer.Ordinal));
            return new Refusal(cellId, model, stageHandler, $"cell {cellId} is model {model}; the rule admits {declared}");
        }
        if (!allowedHandlers.Contains(stageHandler, StringComparer.Ordinal))
        {
            var declared = allowedHandlers.Count == 0
                ? "no handlers at all"
                : "handlers " + string.Join(", ", allowedHandlers.OrderBy(name => name, StringComparer.Ordinal));
            return new Refusal(
                cellId, model, stageHandler,
                $"cell {cellId} needs Stage handler {stageHandler}; the rule allows {declared}");
        }
        return null;
    }
}

/// <summary>One cell as the placement question sees it: an id, a model, a Stage handler.</summary>
public sealed record PlaceableCell(string CellId, string Model, string StageHandler);

/// <summary>One Site as the placement question sees it: an id and its rule.</summary>
public sealed record PlaceableSite(
    string SiteId,
    IReadOnlyList<string> AdmittedModels,
    IReadOnlyList<string> AllowedHandlers);

/// <summary>Which Sites may run each cell, or the first cell no Site may run.</summary>
public sealed record PlacementResolution(
    IReadOnlyDictionary<string, IReadOnlyList<string>> AdmissibleSitesByCell);

/// <summary>
/// Compile-time placement over a whole Plan.
///
/// Compile stays strict and gets stricter in one way: it asks, for every cell,
/// which enabled Sites may run it, and refuses the WHOLE compile if any cell
/// has none. An unplaceable cell is a mistake in the Plan or in the Site
/// declarations, and it is worth finding when the Plan is compiled rather than
/// by a queue that quietly never drains.
///
/// Pure. It is not wired into `plan-experiment` here; it is the function that
/// wiring will call.
/// </summary>
public static class CompilePlacement
{
    /// <summary>
    /// Resolve every cell against every enabled Site. On success the map holds
    /// one entry per cell, each naming at least one Site, in Site-id order so
    /// two compiles of the same Plan produce the same derived lowerings.
    /// </summary>
    public static RegistryResult<PlacementResolution> Resolve(
        IReadOnlyList<PlaceableCell> cells,
        IReadOnlyList<PlaceableSite> enabledSites,
        ICellAdmission? admission = null)
    {
        // Omitted, the rule is this binary's own. A caller may pass another
        // implementation; the compile does not otherwise change.
        var rule = admission ?? CSharpCellAdmission.Instance;
        if (cells.Count == 0)
        {
            return Reject("a compiled Plan has no cells to place");
        }

        var byCell = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var cell in cells)
        {
            var admissible = new List<string>();
            var refusals = new List<string>();
            foreach (var site in enabledSites.OrderBy(site => site.SiteId, StringComparer.Ordinal))
            {
                var refusal = rule.Admits(
                    site.AdmittedModels, site.AllowedHandlers, cell.CellId, cell.Model, cell.StageHandler);
                if (refusal is null)
                {
                    admissible.Add(site.SiteId);
                }
                else
                {
                    refusals.Add(refusal.Detail);
                }
            }

            if (admissible.Count == 0)
            {
                // The refusal names the cell and every rule that turned it
                // away. Naming only the cell would leave an operator guessing
                // which machine to widen; naming only the Sites would not say
                // what could not be placed.
                var why = refusals.Count == 0
                    ? "no Worker Site is enabled"
                    : string.Join("; ", refusals);
                return Reject($"cell {cell.CellId} has no admissible Worker Site: {why}");
            }

            byCell[cell.CellId] = admissible;
        }

        return RegistryResult<PlacementResolution>.Ok(new PlacementResolution(byCell));
    }

    static RegistryResult<PlacementResolution> Reject(string detail) =>
        RegistryResult<PlacementResolution>.Rejected(new("refused", "invalid_request", detail));
}

/// <summary>
/// Whether one Site's rule admits one cell. <see cref="CSharpCellAdmission"/>
/// is this binary's answer; <see cref="CompilePlacement.Resolve"/> uses whatever
/// it is given, and the C# answer when it is given nothing.
/// </summary>
public interface ICellAdmission
{
    Placement.Refusal? Admits(
        IReadOnlyList<string> admittedModels,
        IReadOnlyList<string> allowedHandlers,
        string cellId,
        string model,
        string stageHandler);
}

/// <summary>The rule as <see cref="Placement.Admits"/> already states it.</summary>
public sealed class CSharpCellAdmission : ICellAdmission
{
    public static readonly CSharpCellAdmission Instance = new();

    public Placement.Refusal? Admits(
        IReadOnlyList<string> admittedModels,
        IReadOnlyList<string> allowedHandlers,
        string cellId,
        string model,
        string stageHandler) =>
        Placement.Admits(admittedModels, allowedHandlers, cellId, model, stageHandler);
}
