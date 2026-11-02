using Fern.Registry;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// Gates 2 and 3 of the site-neutral Plan card: which Site may run which cell,
/// and what compile does when no Site may run one.
///
/// Both halves of the rule are tested for the same reason: a machine that can
/// run the Stage handler but not the model would claim work it cannot compute,
/// and a machine that knows the model but lacks the handler cannot execute the
/// Stage at all. Passing one half is not passing the rule.
/// </summary>
public sealed class PlacementTests
{
    static readonly string[] Train = ["fern.train"];

    static PlaceableSite Site(string id, string[] models, string[]? handlers = null) =>
        new(id, models, handlers ?? Train);

    static PlaceableCell Cell(string id, string model) => new(id, model, "fern.train");

    // ── gate 2: a rule that names DLinear and Zeus refuses a Fern cell ──────

    [Fact]
    public void ARuleNamingDlinearAndZeusRefusesAFernCellAndNamesTheCellAndTheRule()
    {
        var refusal = Placement.Admits(
            ["dlinear", "zeus"], Train, "cell-7", "fern", "fern.train");

        Assert.NotNull(refusal);
        // The cell, because that is what could not be placed.
        Assert.Contains("cell-7", refusal!.Detail, StringComparison.Ordinal);
        Assert.Contains("model fern", refusal.Detail, StringComparison.Ordinal);
        // The rule, because that is what an operator would widen.
        Assert.Contains("dlinear", refusal.Detail, StringComparison.Ordinal);
        Assert.Contains("zeus", refusal.Detail, StringComparison.Ordinal);
        // Not the Site: a Site is not at fault for declaring what it is.
        Assert.DoesNotContain("local-4070", refusal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameRuleAdmitsTheModelsItNames()
    {
        Assert.Null(Placement.Admits(["dlinear", "zeus"], Train, "cell-0", "dlinear", "fern.train"));
        Assert.Null(Placement.Admits(["dlinear", "zeus"], Train, "cell-1", "zeus", "fern.train"));
    }

    [Fact]
    public void AnEmptyRuleAdmitsNothingRatherThanEverything()
    {
        var refusal = Placement.Admits([], Train, "cell-0", "dlinear", "fern.train");
        Assert.NotNull(refusal);
        Assert.Contains("no models at all", refusal!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BothHalvesOfTheRuleMustPass()
    {
        // Model admitted, handler not.
        var handlerRefusal = Placement.Admits(["dlinear"], [], "cell-0", "dlinear", "fern.train");
        Assert.NotNull(handlerRefusal);
        Assert.Contains("no handlers at all", handlerRefusal!.Detail, StringComparison.Ordinal);
        Assert.Contains("fern.train", handlerRefusal.Detail, StringComparison.Ordinal);

        // Handler allowed, model not.
        Assert.NotNull(Placement.Admits([], Train, "cell-0", "dlinear", "fern.train"));
    }

    [Fact]
    public void TheModelVocabularyIsTheOneTheConfigResolverKnows()
    {
        foreach (var model in new[] { "dlinear", "fern", "zeus", "patchtst", "timemixer", "duet", "conditional_nsf", "conditional_mdn", "conditional_ddpm", "conditional_ddim", "conditional_rectified_flow", "action_state_gru", "mac_chunk_mlp" })
        {
            Assert.Contains(model, Placement.KnownModelTypes);
        }
        Assert.Contains("robot_dynamics_diffusion", Placement.KnownModelTypes);
        Assert.Contains("diffusion_policy", Placement.KnownModelTypes);
        Assert.Contains("conditional_gaussian", Placement.KnownModelTypes);
        Assert.Contains("conditional_realnvp", Placement.KnownModelTypes);
        Assert.Contains("conditional_profiti", Placement.KnownModelTypes);
        Assert.Contains("conditional_tactis2", Placement.KnownModelTypes);
        Assert.Contains("odd_residual", Placement.KnownModelTypes);
        Assert.DoesNotContain("dlinar", Placement.KnownModelTypes);
    }

    // ── gate 3: compile places every cell, or refuses and names the one it cannot ──

    [Fact]
    public void APlanWhoseEveryCellHasASiteResolvesToThoseSites()
    {
        var result = CompilePlacement.Resolve(
            [Cell("cell-0", "dlinear"), Cell("cell-1", "zeus")],
            [Site("local-4070", ["dlinear", "zeus"]), Site("runpod-a4500", ["dlinear", "fern"])]);

        Assert.True(result.TryGetValue(out var placement));
        // The DLinear cell may run on both; the Zeus cell only on the card.
        Assert.Equal(["local-4070", "runpod-a4500"], placement.AdmissibleSitesByCell["cell-0"]);
        Assert.Equal(["local-4070"], placement.AdmissibleSitesByCell["cell-1"]);
    }

    [Fact]
    public void OneUnplaceableCellRefusesTheWholeCompileAndNamesThatCell()
    {
        var result = CompilePlacement.Resolve(
            [Cell("cell-0", "dlinear"), Cell("cell-1", "fern")],
            [Site("local-4070", ["dlinear", "zeus"])]);

        Assert.False(result.TryGetValue(out _));
        Assert.True(result.TryGetRefusal(out var refusal));
        Assert.Contains("cell-1", refusal.Detail, StringComparison.Ordinal);
        Assert.Contains("no admissible Worker Site", refusal.Detail, StringComparison.Ordinal);
        // And it says what the rule was, so an operator knows what to widen.
        Assert.Contains("dlinear", refusal.Detail, StringComparison.Ordinal);
        // The placeable cell is not mentioned: the refusal is about the one that failed.
        Assert.DoesNotContain("cell-0", refusal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NoEnabledSiteAtAllSaysSoRatherThanBlamingTheRule()
    {
        var result = CompilePlacement.Resolve([Cell("cell-0", "dlinear")], []);
        Assert.True(result.TryGetRefusal(out var refusal));
        Assert.Contains("no Worker Site is enabled", refusal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SiteOrderIsStableSoTwoCompilesDeriveTheSameLowerings()
    {
        var forward = CompilePlacement.Resolve(
            [Cell("cell-0", "dlinear")],
            [Site("runpod-a4500", ["dlinear"]), Site("local-4070", ["dlinear"])]);
        var reversed = CompilePlacement.Resolve(
            [Cell("cell-0", "dlinear")],
            [Site("local-4070", ["dlinear"]), Site("runpod-a4500", ["dlinear"])]);

        Assert.True(forward.TryGetValue(out var first));
        Assert.True(reversed.TryGetValue(out var second));
        Assert.Equal(first.AdmissibleSitesByCell["cell-0"], second.AdmissibleSitesByCell["cell-0"]);
    }
}
