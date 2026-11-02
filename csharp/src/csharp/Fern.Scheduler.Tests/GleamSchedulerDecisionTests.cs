using Fern.Scheduler;
using System.Text.Json.Nodes;

namespace Fern.Scheduler.Tests;

/// <summary>
/// The placement decision, once by the C# function and once by the Gleam
/// program behind the same interface, on the same offers and demands.
///
/// Order is part of the answer. A missing program fails this test; it does
/// not skip. Build the shipment first, or point FERN_SCHEDULER_GLEAM_SHIPMENT
/// at one:
///   cd tools/fern-scheduler-gleam &amp;&amp; gleam export erlang-shipment
/// </summary>
public sealed class GleamSchedulerDecisionTests
{
    internal static string Shipment() => Environment.GetEnvironmentVariable("FERN_SCHEDULER_GLEAM_SHIPMENT")
        ?? Path.Combine(TestRegistry.RepoRoot(), "tools", "fern-scheduler-gleam", "build", "erlang-shipment");

    [Fact]
    public void AnInvalidRequestIsAnsweredAndTheNextRequestStillWorks()
    {
        using var program = GleamProgram.FromShipment(Shipment());
        var refused = program.Ask(new JsonObject { ["ask"] = "not_a_known_request" }, TimeSpan.FromSeconds(3), null);
        Assert.False(refused.IsOk);
        Assert.Contains("invalid_request", refused.RefusalDetail, StringComparison.Ordinal);
        var next = program.Ask(new JsonObject { ["ask"] = "known_models" }, TimeSpan.FromSeconds(3), null);
        Assert.True(next.IsOk, next.RefusalDetail);
        Assert.NotNull(next.Value["answer"]?["models"]);
    }

    [Fact]
    public void BorrowedAdaptersDoNotCloseTheSharedProgram()
    {
        using var program = GleamProgram.FromShipment(Shipment());
        var decision = new GleamSchedulerDecision(program);
        var admission = new GleamCellAdmission(program);
        decision.Dispose();
        Assert.Contains("fern", admission.KnownModels());
        admission.Dispose();
        Assert.Empty(new GleamSchedulerDecision(program).Choose([], [], Idle).Placements);
    }

    [Fact]
    public void ALateReplyCannotAnswerTheNextRequest()
    {
        // A finite wire fixture, not a worker: no trainer or Registry exists.
        using var program = GleamProgram.Start("/bin/sh", ["-c",
            "read first; sleep 0.1; echo '{\"id\":\"1\",\"answer\":{\"tag\":\"late\"}}'; "
            + "read second; echo '{\"id\":\"2\",\"answer\":{\"tag\":\"next\"}}'"]);
        var late = program.Ask(new JsonObject(), TimeSpan.FromMilliseconds(10), null);
        Assert.False(late.IsOk);
        var next = program.Ask(new JsonObject(), TimeSpan.FromSeconds(3), null);
        Assert.True(next.IsOk, next.RefusalDetail);
        Assert.Equal("next", next.Value["answer"]!["tag"]!.GetValue<string>());
    }

    [Fact]
    public void EndOfOutputReleasesAWaitingRequest()
    {
        using var program = GleamProgram.Start("/bin/sh", ["-c", "read request"]);
        var ended = program.Ask(new JsonObject(), TimeSpan.FromSeconds(3), null);
        Assert.False(ended.IsOk);
        Assert.Contains("program ended", ended.RefusalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnswersInReverseOrderReachTheRightCaller()
    {
        using var program = GleamProgram.Start("/bin/sh", ["-c",
            "read first; read second; echo '{\"id\":\"2\",\"answer\":{}}'; echo '{\"id\":\"1\",\"answer\":{}}'"]);
        var requests = new[] { new JsonObject(), new JsonObject() };
        var replies = await Task.WhenAll(requests.Select(request => Task.Run(() =>
            program.Ask(request, TimeSpan.FromSeconds(3), null))));
        for (var i = 0; i < requests.Length; i++)
        {
            Assert.True(replies[i].IsOk, replies[i].RefusalDetail);
            Assert.Equal(requests[i]["id"]!.GetValue<string>(), replies[i].Value["id"]!.GetValue<string>());
        }
    }

    static SchedulerDecision.SiteOffer Site(long id, string name, string device, int slots, params string[] models) =>
        new(id, name, models, device, slots);

    static SchedulerDecision.CellDemand Cell(long id, string model, string device = "cuda", bool cuda = true, long ordinal = 0) =>
        new(id, 1, ordinal, $"cell-{id}", model, device, cuda, 1);

    static readonly Dictionary<long, int> Idle = new();

    [Fact]
    public void BothChooseTheSamePlacementsInTheSameOrderAndTheSameReasons()
    {
        using var gleam = GleamSchedulerDecision.Open();
        foreach (var sample in Samples())
        {
            var byCSharp = SchedulerDecision.Choose(sample.Cells, sample.Sites, sample.Busy);
            var byGleam = gleam.Choose(sample.Cells, sample.Sites, sample.Busy);
            Assert.Equal(Lines(sample.Name, byCSharp), Lines(sample.Name, byGleam));
        }
    }

    [Fact]
    public void BothReadTheSameOfferAndTheSameDemandFromTheJsonColumns()
    {
        using var gleam = GleamSchedulerDecision.Open();
        foreach (var sample in Readings())
        {
            var offer = SchedulerDecision.ReadOffer(7, "local-4070", sample.Capabilities, sample.Capacity);
            var byGleam = gleam.ReadOffer(7, "local-4070", sample.Capabilities, sample.Capacity);
            Assert.Equal(offer.Device, byGleam.Device);
            Assert.Equal(offer.Slots, byGleam.Slots);
            Assert.Equal(offer.AdmittedModels, byGleam.AdmittedModels);

            var demand = SchedulerDecision.ReadDemand(9, 1, 0, "reference", sample.Config, sample.Requirements, 1);
            var demandByGleam = gleam.ReadDemand(9, 1, 0, "reference", sample.Config, sample.Requirements, 1);
            Assert.Equal(demand, demandByGleam);
        }
    }

    sealed record Sample(string Name, SchedulerDecision.CellDemand[] Cells, SchedulerDecision.SiteOffer[] Sites, Dictionary<long, int> Busy);

    static IEnumerable<Sample> Samples()
    {
        yield return new("admits the model and the device", [Cell(10, "fern")], [Site(1, "local-4070", "cuda", 1, "fern", "dlinear")], Idle);
        yield return new("an empty admission list admits nothing", [Cell(10, "fern")], [Site(1, "mystery", "cuda", 4)], Idle);
        yield return new("the device differs", [Cell(10, "fern")], [Site(1, "cpu-box", "cpu", 4, "fern")], Idle);
        yield return new("one slot, two cells", [Cell(10, "fern", ordinal: 0), Cell(11, "fern", ordinal: 1)], [Site(1, "local-4070", "cuda", 1, "fern")], Idle);
        yield return new("slots already held", [Cell(10, "fern")], [Site(1, "local-4070", "cuda", 2, "fern")], new() { [1] = 2 });
        yield return new("study order, not a new one", [Cell(20, "fern"), Cell(21, "fern"), Cell(22, "fern")], [Site(1, "local-4070", "cuda", 2, "fern")], Idle);
        yield return new("each model on the site that admits it", [Cell(10, "fern"), Cell(11, "dlinear")], [Site(1, "a", "cuda", 1, "fern"), Site(2, "b", "cuda", 1, "dlinear")], Idle);
        yield return new("first site, not the one with more room", [Cell(10, "fern")], [Site(1, "first", "cuda", 1, "fern"), Site(2, "second", "cuda", 8, "fern")], Idle);
        yield return new("no free slot anywhere", [Cell(10, "fern")], [Site(1, "local", "cuda", 0, "fern")], Idle);
        yield return new("busy past the slots", [Cell(10, "fern")], [Site(1, "local", "cuda", 2, "fern")], new() { [1] = 5 });
        yield return new("a full site is skipped for a later one with room", [Cell(10, "fern")], [Site(1, "full", "cuda", 1, "fern"), Site(2, "room", "cuda", 1, "fern")], new() { [1] = 1 });
        yield return new("a device reason stands over a later model reason", [Cell(10, "fern")], [Site(2, "cpu-box", "cpu", 1, "fern"), Site(1, "mystery", "cuda", 1)], Idle);
        yield return new("a device reason replaces an earlier model reason", [Cell(10, "fern")], [Site(1, "mystery", "cuda", 1), Site(2, "cpu-box", "cpu", 1, "fern")], Idle);
        yield return new("no site is registered", [Cell(10, "fern")], [], Idle);
        yield return new("cuda is required of a site whose device matches and is not cuda", [Cell(10, "fern", device: "cpu", cuda: true)], [Site(1, "cpu-box", "cpu", 1, "fern")], Idle);
        yield return new("list order, not the ordinal", [Cell(20, "fern", ordinal: 5), Cell(21, "fern", ordinal: 0)], [Site(1, "local", "cuda", 1, "fern")], Idle);
    }

    static IEnumerable<string> Lines(string name, SchedulerDecision.Decision decision)
    {
        yield return name;
        foreach (var placement in decision.Placements)
        {
            yield return $"place {placement.CellId} on {placement.SiteId}: {placement.Because}";
        }
        foreach (var unplaced in decision.Unplaced)
        {
            yield return $"wait {unplaced.CellId}: {unplaced.Because}";
        }
    }

    sealed record Reading(string Capabilities, string Capacity, string Config, string Requirements);

    static IEnumerable<Reading> Readings()
    {
        yield return new(
            """{"device":"cuda","admitted_models":["fern","dlinear"]}""",
            """{"slots":3}""",
            """{"model":{"type":"fern"}}""",
            """{"device":"cuda","requires_cuda":true}""");
        yield return new("not json", "not json", "not json", "not json");
        yield return new("{}", "{}", "{}", "{}");
        yield return new(
            """{"device":1,"admitted_models":["fern",2,"dlinear"]}""",
            """{"slots":3000000000}""",
            """{"model":{"type":1}}""",
            """{"device":"cpu","requires_cuda":"yes"}""");
        yield return new("{}", """{"slots":-1}""", "{}", """{"requires_cuda":false}""");
    }
}
