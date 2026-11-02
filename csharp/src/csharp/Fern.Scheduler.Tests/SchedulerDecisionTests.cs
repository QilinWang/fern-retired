using Fern.Scheduler;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// The placement decision, as a pure function. No Registry, no Site, no
/// worker, no clock: every fact arrives as an argument, which is the point of
/// the module and the reason these cases are one line each.
/// </summary>
public sealed class SchedulerDecisionTests
{
    static SchedulerDecision.SiteOffer Site(long id, string name, string device, int slots, params string[] models) =>
        new(id, name, models, device, slots);

    static SchedulerDecision.CellDemand Cell(long id, string model, string device = "cuda", bool cuda = true, long ordinal = 0) =>
        new(id, 1, ordinal, $"cell-{id}", model, device, cuda, 1);

    static readonly Dictionary<long, int> Idle = new();

    [Fact]
    public void ACellGoesToASiteThatAdmitsItsModelAndMatchesItsDevice()
    {
        var decision = SchedulerDecision.Choose(
            [Cell(10, "fern")], [Site(1, "local-4070", "cuda", 1, "fern", "dlinear")], Idle);

        var placement = Assert.Single(decision.Placements);
        Assert.Equal(10L, placement.CellId);
        Assert.Equal(1L, placement.SiteId);
        Assert.Empty(decision.Unplaced);
    }

    /// <summary>
    /// An empty admitted list admits nothing, deliberately. The failure of a
    /// too-narrow rule is a cell that visibly queues; the failure of a too-wide
    /// default is a machine quietly claiming work it will fail.
    /// </summary>
    [Fact]
    public void ASiteThatHasNotSaidWhatItRunsRunsNothing()
    {
        var decision = SchedulerDecision.Choose([Cell(10, "fern")], [Site(1, "mystery", "cuda", 4)], Idle);

        Assert.Empty(decision.Placements);
        Assert.Contains("no Site admits model fern", Assert.Single(decision.Unplaced).Because);
    }

    [Fact]
    public void ACellIsNotPlacedOnASiteOfTheWrongDevice()
    {
        var decision = SchedulerDecision.Choose(
            [Cell(10, "fern", device: "cuda")], [Site(1, "cpu-box", "cpu", 4, "fern")], Idle);

        Assert.Empty(decision.Placements);
        Assert.Contains("is cpu", Assert.Single(decision.Unplaced).Because);
    }

    [Fact]
    public void CapacityIsRespectedAndTheOverflowSaysSo()
    {
        var decision = SchedulerDecision.Choose(
            [Cell(10, "fern", ordinal: 0), Cell(11, "fern", ordinal: 1)],
            [Site(1, "local-4070", "cuda", 1, "fern")], Idle);

        Assert.Equal(10L, Assert.Single(decision.Placements).CellId);
        Assert.Contains("is full", Assert.Single(decision.Unplaced).Because);
    }

    /// <summary>A Site already holding work has fewer free slots, not more.</summary>
    [Fact]
    public void SlotsAlreadyHeldAreSubtracted()
    {
        var busy = new Dictionary<long, int> { [1] = 2 };
        var decision = SchedulerDecision.Choose(
            [Cell(10, "fern")], [Site(1, "local-4070", "cuda", 2, "fern")], busy);

        Assert.Empty(decision.Placements);
        Assert.Contains("is full", Assert.Single(decision.Unplaced).Because);
    }

    /// <summary>
    /// Cell order, not any invented priority: the compiled study fixed an
    /// order, an operator reorders it there, and a scheduler with its own order
    /// would make the study's order a lie.
    /// </summary>
    [Fact]
    public void CellsArePlacedInTheOrderTheStudyDeclaredThem()
    {
        var decision = SchedulerDecision.Choose(
            [Cell(20, "fern", ordinal: 0), Cell(21, "fern", ordinal: 1), Cell(22, "fern", ordinal: 2)],
            [Site(1, "local-4070", "cuda", 2, "fern")], Idle);

        Assert.Equal([20L, 21L], decision.Placements.Select(p => p.CellId).ToArray());
        Assert.Equal(22L, Assert.Single(decision.Unplaced).CellId);
    }

    /// <summary>The same three lists give the same answer; there is no clock and no state.</summary>
    [Fact]
    public void TheDecisionIsDeterministic()
    {
        var cells = new[] { Cell(10, "fern"), Cell(11, "dlinear") };
        var sites = new[] { Site(1, "a", "cuda", 1, "fern"), Site(2, "b", "cuda", 1, "dlinear") };

        var first = SchedulerDecision.Choose(cells, sites, Idle);
        var second = SchedulerDecision.Choose(cells, sites, Idle);

        Assert.Equal(
            first.Placements.Select(p => (p.CellId, p.SiteId)),
            second.Placements.Select(p => (p.CellId, p.SiteId)));
    }

    [Fact]
    public void ReadOfferAndReadDemandTakeTheirFactsFromTheJsonColumns()
    {
        var offer = SchedulerDecision.ReadOffer(
            7, "local-4070", """{"device":"cuda","admitted_models":["fern","dlinear"]}""", """{"slots":3}""");
        Assert.Equal("cuda", offer.Device);
        Assert.Equal(3, offer.Slots);
        Assert.Equal(["fern", "dlinear"], offer.AdmittedModels);

        var demand = SchedulerDecision.ReadDemand(
            9, 1, 0, "reference", """{"model":{"type":"fern"}}""", """{"device":"cuda","requires_cuda":true}""", 1);
        Assert.Equal("fern", demand.ModelType);
        Assert.Equal("cuda", demand.Device);
        Assert.True(demand.RequiresCuda);
    }

    /// <summary>A declaration that cannot be read admits nothing: it queues visibly rather than failing on a machine.</summary>
    [Fact]
    public void AnUnreadableSiteDeclarationAdmitsNothing()
    {
        var offer = SchedulerDecision.ReadOffer(7, "broken", "not json", "not json");
        Assert.Empty(offer.AdmittedModels);
        Assert.Equal(0, offer.Slots);
    }

}
