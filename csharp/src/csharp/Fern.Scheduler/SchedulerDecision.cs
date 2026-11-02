using System.Text.Json;

namespace Fern.Scheduler;

/// <summary>
/// Which cell goes to which Site, as a pure function.
///
/// It reads three lists and returns placements. It opens nothing, writes
/// nothing, and calls no clock: given the same three lists it returns the same
/// placements, which is why it can be tested without a Registry, a Site or a
/// worker. Every fact it needs — a cell's requirements, a Site's capabilities
/// and capacity, what is already running — arrives as an argument.
///
/// The decision is deliberately small. It answers "may this Site run this
/// cell, and has it room", and nothing about whether the claim will succeed:
/// two schedulers can reach the same placement, and
/// <c>RegistryOperations.TryStartAttempt</c> settles that in one transaction.
/// A decision module that tried to be authoritative would be a second
/// scheduler, and the Registry is the only one.
/// </summary>
public static class SchedulerDecision
{
    /// <summary>A Site as the decision sees it: what it admits, and how much it can hold.</summary>
    /// <remarks>
    /// CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
    /// slice 4; owner: the installed-source way is retired): a Site no longer
    /// "holds" sources. Every Site takes each cell's code from its repository,
    /// so which sources a Site holds is no longer part of what it offers.
    /// </remarks>
    public sealed record SiteOffer(long SiteId, string Name, IReadOnlyList<string> AdmittedModels, string Device, int Slots);

    /// <summary>A cell as the decision sees it: what it is, and what it needs.</summary>
    public sealed record CellDemand(long CellId, long StudyId, long Ordinal, string Name, string ModelType, string Device, bool RequiresCuda, long SourceId);

    /// <summary>One cell placed on one Site, with the reason a person would give.</summary>
    public sealed record Placement(long CellId, long SiteId, string Because);

    /// <summary>A cell that could not be placed, and the one reason nearest to actionable.</summary>
    public sealed record Unplaced(long CellId, string Because);

    /// <summary>What one decision produced.</summary>
    public sealed record Decision(IReadOnlyList<Placement> Placements, IReadOnlyList<Unplaced> Unplaced);

    /// <summary>
    /// Place as many cells as the Sites can hold, in cell order.
    ///
    /// Cell order, not any notion of priority: the compiled study already fixed
    /// an order, an operator can reorder it there, and a scheduler that invents
    /// its own order makes the study's order a lie. Within that, a cell goes to
    /// the first Site that admits its model, matches its device and has a free
    /// slot — first, not best, because "best" needs a cost model nobody has
    /// written and a wrong one is worse than none.
    /// </summary>
    public static Decision Choose(
        IReadOnlyList<CellDemand> runnable,
        IReadOnlyList<SiteOffer> sites,
        IReadOnlyDictionary<long, int> busySlotsBySite)
    {
        var free = new Dictionary<long, int>();
        foreach (var site in sites)
        {
            busySlotsBySite.TryGetValue(site.SiteId, out var busy);
            free[site.SiteId] = site.Slots - busy;
        }

        var placements = new List<Placement>();
        var unplaced = new List<Unplaced>();
        foreach (var cell in runnable)
        {
            string? nearest = null;
            var placed = false;
            foreach (var site in sites)
            {
                if (Refusal(site, cell) is { } objection)
                {
                    // The model objection names no Site, so an earlier and more
                    // specific reason stands over it; the others name the Site
                    // and replace what came before, as they did when inline.
                    if (objection.NamesSite) nearest = objection.Because; else nearest ??= objection.Because;
                    continue;
                }
                if (free[site.SiteId] <= 0)
                {
                    nearest = $"Site {site.Name} admits this cell but is full";
                    continue;
                }
                free[site.SiteId] -= 1;
                placements.Add(new Placement(cell.CellId, site.SiteId,
                    $"Site {site.Name} admits {cell.ModelType} on {site.Device} and has a free slot"));
                placed = true;
                break;
            }
            if (!placed)
            {
                unplaced.Add(new Unplaced(cell.CellId, nearest ?? "no Site is registered"));
            }
        }
        return new Decision(placements, unplaced);
    }

    /// <summary>
    /// Read a Site's declared capability and capacity out of the two JSON
    /// columns the one-store schema keeps them in.
    ///
    /// They are JSON rather than columns because a Site's capability list grows
    /// with the fleet and a column per capability would be a migration per
    /// machine. Nothing joins on them, so nothing is lost.
    /// </summary>
    /// <summary>Why a Site cannot take a cell, and whether the reason names the Site.</summary>
    public sealed record Objection(string Because, bool NamesSite);

    /// <summary>
    /// Why a Site cannot take a cell by the two declarations alone (its
    /// admitted models, its device, the cell's CUDA requirement), or null when
    /// it can; the sources it holds and its free slots are the decision's own.
    /// CHG 2026-09-11 night (slice 6, row 32): lifted out of Choose so that
    /// install-study-source --all-sites selects by the same rule.
    /// </summary>
    public static Objection? Refusal(SiteOffer site, CellDemand cell)
    {
        if (!site.AdmittedModels.Contains(cell.ModelType, StringComparer.Ordinal))
        {
            // An empty list admits nothing, deliberately: a Site that has
            // not said what it can run is not a Site that can run
            // anything. The failure of a too-narrow rule is cells that
            // visibly queue; the failure of a too-wide default is a
            // machine quietly claiming work it will fail.
            return new Objection($"no Site admits model {cell.ModelType}", NamesSite: false);
        }
        if (!string.Equals(site.Device, cell.Device, StringComparison.Ordinal))
        {
            return new Objection($"Site {site.Name} admits {cell.ModelType} but is {site.Device}, and the cell asks for {cell.Device}", NamesSite: true);
        }
        if (cell.RequiresCuda && !string.Equals(site.Device, "cuda", StringComparison.Ordinal))
        {
            return new Objection($"Site {site.Name} is {site.Device} and the cell requires CUDA", NamesSite: true);
        }
        return null;
    }

    public static SiteOffer ReadOffer(long siteId, string name, string capabilitiesJson, string capacityJson)
    {
        var models = new List<string>();
        var device = "";
        try
        {
            using var capabilities = JsonDocument.Parse(capabilitiesJson);
            if (capabilities.RootElement.TryGetProperty("device", out var declared) && declared.ValueKind == JsonValueKind.String)
            {
                device = declared.GetString() ?? "";
            }
            // CHG 2026-09-11: the field is `admitted_models`, which is what a
            // profile declares and what SiteRegistration writes. This reader
            // asked for `models`, so every Site registered through
            // `register-site` admitted nothing and every cell was unplaced.
            if (capabilities.RootElement.TryGetProperty("admitted_models", out var admitted) && admitted.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in admitted.EnumerateArray())
                {
                    if (model.ValueKind == JsonValueKind.String) models.Add(model.GetString()!);
                }
            }
        }
        catch (JsonException)
        {
            // A Site whose declaration cannot be read admits nothing, which is
            // the same direction as an empty list: it queues visibly.
        }

        var slots = 0;
        try
        {
            using var capacity = JsonDocument.Parse(capacityJson);
            if (capacity.RootElement.TryGetProperty("slots", out var declared) && declared.TryGetInt32(out var value))
            {
                slots = value;
            }
        }
        catch (JsonException)
        {
        }
        return new SiteOffer(siteId, name, models, device, slots);
    }

    /// <summary>Read a cell's model type, device and CUDA requirement out of its two JSON columns.</summary>
    public static CellDemand ReadDemand(long cellId, long studyId, long ordinal, string name, string configJson, string requirementsJson, long sourceId)
    {
        var modelType = "";
        try
        {
            using var config = JsonDocument.Parse(configJson);
            if (config.RootElement.TryGetProperty("model", out var model)
                && model.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                modelType = type.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
        }

        var device = "";
        var requiresCuda = false;
        try
        {
            using var requirements = JsonDocument.Parse(requirementsJson);
            if (requirements.RootElement.TryGetProperty("device", out var declared) && declared.ValueKind == JsonValueKind.String)
            {
                device = declared.GetString() ?? "";
            }
            if (requirements.RootElement.TryGetProperty("requires_cuda", out var cuda)
                && cuda.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                requiresCuda = cuda.GetBoolean();
            }
        }
        catch (JsonException)
        {
        }
        return new CellDemand(cellId, studyId, ordinal, name, modelType, device, requiresCuda, sourceId);
    }
}

/// <summary>
/// Which cell goes where. <see cref="CSharpSchedulerDecision"/> is this
/// binary's own answer; a caller of <see cref="StudyOrchestrator"/> can pass
/// another one, and the cycle does not otherwise change.
/// </summary>
public interface ISchedulerDecision
{
    SchedulerDecision.Decision Choose(
        IReadOnlyList<SchedulerDecision.CellDemand> runnable,
        IReadOnlyList<SchedulerDecision.SiteOffer> sites,
        IReadOnlyDictionary<long, int> busySlotsBySite);
}

/// <summary>The decision as <see cref="SchedulerDecision"/> already makes it.</summary>
public sealed class CSharpSchedulerDecision : ISchedulerDecision
{
    public static readonly CSharpSchedulerDecision Instance = new();

    public SchedulerDecision.Decision Choose(
        IReadOnlyList<SchedulerDecision.CellDemand> runnable,
        IReadOnlyList<SchedulerDecision.SiteOffer> sites,
        IReadOnlyDictionary<long, int> busySlotsBySite) =>
        SchedulerDecision.Choose(runnable, sites, busySlotsBySite);
}
