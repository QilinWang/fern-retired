using Fern.Registry;
using Fern.Scheduler;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Which Sites may run a cell, once by the C# rule and once by the Gleam
/// program. A missing program fails this test; it does not skip.
/// </summary>
public sealed class GleamCellAdmissionTests
{
    static readonly string[] Train = ["fern.train"];

    static readonly JsonSerializerOptions DeclarationOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Fact]
    public void AThousandCellsAreCheckedInOneRequest()
    {
        using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
        using var admission = new GleamCellAdmission(program);
        var declarations = new StudyRegistration.PlacementDeclarations(
            Enumerable.Range(0, 1000).Select(i => new PlaceableCell($"cell-{i}", "fern", "fern.train")).ToArray(),
            [new("pod", ["fern"], Train), new("local", ["fern"], Train)]);
        var result = StudyRegistration.CheckPlacement(JsonSerializer.Serialize(declarations, DeclarationOptions), admission);
        Assert.True(result.TryGetValue(out var placed));
        Assert.Equal(1000, placed.AdmissibleSitesByCell.Count);
        Assert.All(placed.AdmissibleSitesByCell.Values, sites => Assert.Equal(["local", "pod"], sites));
        var next = program.Ask(new JsonObject { ["ask"] = "known_models" }, TimeSpan.FromSeconds(3), null);
        Assert.True(next.IsOk, next.RefusalDetail);
        Assert.Equal("2", next.Value["id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"cells\":null,\"sites\":[]}")]
    [InlineData("{\"cells\":[null],\"sites\":[]}")]
    [InlineData("{\"cells\":[],\"sites\":[{\"site_id\":\"pod\",\"admitted_models\":null,\"allowed_handlers\":[]}]}")]
    [InlineData("{\"cells\":[{\"cell_id\":\"a\",\"model\":\"fern\"}],\"sites\":[]}")]
    [InlineData("{\"cells\":[],\"sites\":[],\"unknown\":true}")]
    public void InvalidDeclarationsAreRefusedBeforeAskingGleam(string document)
    {
        using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
        using var admission = new GleamCellAdmission(program);
        var result = StudyRegistration.CheckPlacement(document, admission);
        Assert.True(result.TryGetRefusal(out var refusal));
        Assert.Equal("invalid_request", refusal.ErrorCategory);
        var next = program.Ask(new JsonObject { ["ask"] = "known_models" }, TimeSpan.FromSeconds(3), null);
        Assert.True(next.IsOk, next.RefusalDetail);
        Assert.Equal("1", next.Value["id"]!.GetValue<string>());
    }

    [Fact]
    public void DuplicateIdsAreRefusedInsteadOfOverwritingACell()
    {
        using var admission = GleamCellAdmission.Open();
        var declarations = new StudyRegistration.PlacementDeclarations(
            [new("same", "fern", "fern.train"), new("same", "dlinear", "fern.train")],
            [new("pod", ["fern", "dlinear"], Train)]);
        var result = StudyRegistration.CheckPlacement(JsonSerializer.Serialize(declarations, DeclarationOptions), admission);
        Assert.True(result.TryGetRefusal(out var refusal));
        Assert.Equal("invalid_request", refusal.ErrorCategory);
    }

    [Fact]
    public async Task TheExplicitCommandWorksWithACopiedShipmentOutsideTheCheckoutAndWritesNoStore()
    {
        var folder = Directory.CreateTempSubdirectory("gleam placement check ").FullName;
        try
        {
            var shipment = Path.Combine(folder, "scheduler shipment");
            var original = GleamSchedulerDecisionTests.Shipment();
            // Copy only the exported runtime, never the source tree or compiler.
            foreach (var file in Directory.GetFiles(original, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(shipment, Path.GetRelativePath(original, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            var input = Path.Combine(folder, "placement.json");
            var declarations = new StudyRegistration.PlacementDeclarations(
                [new("fern-0", "fern", "fern.train"), new("dlinear-0", "dlinear", "fern.train")],
                [new("pod", ["fern", "dlinear"], Train), new("local", ["dlinear"], Train)]);
            File.WriteAllText(input, JsonSerializer.Serialize(declarations, DeclarationOptions));
            var before = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order().ToArray();
            var checkedCells = await RunCommand(["check-study-placement", "--from", input, "--scheduler-program", shipment], folder);
            Assert.True(checkedCells.Exit == 0, checkedCells.Error);
            var answer = JsonNode.Parse(checkedCells.Output)!;
            Assert.Equal("pod", answer["admissible_sites_by_cell"]!["fern-0"]![0]!.GetValue<string>());
            Assert.Equal(2, answer["admissible_sites_by_cell"]!["dlinear-0"]!.AsArray().Count);
            Assert.Equal(before, Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order().ToArray());

            declarations = declarations with { Cells = [new("blocked-cell", "zeus", "fern.train")] };
            File.WriteAllText(input, JsonSerializer.Serialize(declarations, DeclarationOptions));
            var refused = await RunCommand(["check-study-placement", "--from", input, "--scheduler-program", shipment], folder);
            Assert.Equal(3, refused.Exit);
            Assert.Empty(refused.Output);
            Assert.Contains("blocked-cell", refused.Error, StringComparison.Ordinal);
            Assert.Equal(before, Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order().ToArray());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    internal static async Task<(int Exit, string Output, string Error)> RunCommand(
        IReadOnlyList<string> arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo(Path.Combine(TestRegistry.RepoRoot(),
            "src", "csharp", "Fern.Run.Cli", "bin", "Debug", "net11.0", "fern-run"))
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // An explicit --scheduler-program must not need an ambient lookup.
        start.Environment.Remove("FERN_SCHEDULER_GLEAM_SHIPMENT");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await output, await error);
    }

    [Fact]
    public void BothAdmitTheSamePairsAndRefuseWithTheSameReason()
    {
        using var gleam = GleamCellAdmission.Open();
        foreach (var sample in Pairs())
        {
            var byCSharp = Placement.Admits(
                sample.Models, sample.Handlers, sample.CellId, sample.Model, sample.Handler);
            var byGleam = gleam.Admits(
                sample.Models, sample.Handlers, sample.CellId, sample.Model, sample.Handler);
            Assert.Equal(Told(sample.Name, byCSharp), Told(sample.Name, byGleam));
        }
    }

    [Fact]
    public void BothRefuseTheSameCompileAndNameTheFirstUnplaceableCell()
    {
        using var gleam = GleamCellAdmission.Open();
        foreach (var sample in Compiles())
        {
            var byCSharp = CompilePlacement.Resolve(sample.Cells, sample.Sites);
            var byGleam = gleam.Resolve(sample.Cells, sample.Sites);
            Assert.Equal(Told(sample.Name, byCSharp), Told(sample.Name, byGleam));
        }
    }

    [Fact]
    public void AModelOutsideTheKnownSetIsOutsideItOnBothSidesAndTheRuleStillDecides()
    {
        using var gleam = GleamCellAdmission.Open();
        var byCSharp = Placement.KnownModelTypes.Order(StringComparer.Ordinal).ToArray();
        var byGleam = gleam.KnownModels().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(byCSharp, byGleam);
        Assert.DoesNotContain("dlinar", byCSharp);

        var admitted = Placement.Admits(["dlinar"], Train, "cell-0", "dlinar", "fern.train");
        var admittedByGleam = gleam.Admits(["dlinar"], Train, "cell-0", "dlinar", "fern.train");
        Assert.Null(admitted);
        Assert.Null(admittedByGleam);
    }

    sealed record Pair(string Name, string[] Models, string[] Handlers, string CellId, string Model, string Handler);

    static IEnumerable<Pair> Pairs()
    {
        yield return new("the rule names other models", ["dlinear", "zeus"], Train, "cell-7", "fern", "fern.train");
        yield return new("the rule names this model", ["zeus", "dlinear"], Train, "cell-0", "dlinear", "fern.train");
        yield return new("zeus is named too", ["dlinear", "zeus"], Train, "cell-1", "zeus", "fern.train");
        yield return new("no models at all", [], Train, "cell-0", "dlinear", "fern.train");
        yield return new("no handlers at all", ["dlinear"], [], "cell-0", "dlinear", "fern.train");
        yield return new("the handler is a different one", ["dlinear"], ["fern.prepare"], "cell-0", "dlinear", "fern.train");
        yield return new("a model the vocabulary does not know, and the rule does not either", ["fern"], Train, "cell-0", "dlinar", "fern.train");
        yield return new("a model the vocabulary does not know, and the rule names it", ["dlinar"], Train, "cell-0", "dlinar", "fern.train");
    }

    static string Told(string name, Placement.Refusal? refusal) =>
        refusal is null
            ? name + " admits"
            : name + $" refuses {refusal.CellId} {refusal.Model} {refusal.StageHandler}: {refusal.Detail}";

    sealed record Compile(string Name, PlaceableCell[] Cells, PlaceableSite[] Sites);

    static IEnumerable<Compile> Compiles()
    {
        yield return new(
            "every cell has a site",
            [new("cell-0", "dlinear", "fern.train"), new("cell-1", "zeus", "fern.train")],
            [new("local-4070", ["dlinear", "zeus"], Train), new("runpod-a4500", ["dlinear", "fern"], Train)]);
        yield return new(
            "the second cell has nowhere",
            [new("cell-0", "dlinear", "fern.train"), new("cell-1", "fern", "fern.train")],
            [new("local-4070", ["dlinear", "zeus"], Train)]);
        yield return new(
            "the first cell has nowhere, so the second is not the refusal",
            [new("cell-0", "fern", "fern.train"), new("cell-1", "dlinear", "fern.train")],
            [new("local-4070", ["dlinear"], Train)]);
        yield return new("no site is enabled", [new("cell-0", "dlinear", "fern.train")], []);
        yield return new("the plan has no cells", [], [new("local-4070", ["dlinear"], Train)]);
        yield return new(
            "site ids come out in order",
            [new("cell-0", "dlinear", "fern.train")],
            [new("runpod-a4500", ["dlinear"], Train), new("local-4070", ["dlinear"], Train)]);
        yield return new(
            "a repeated cell keeps the later sites",
            [new("cell-0", "dlinear", "fern.train"), new("cell-0", "zeus", "fern.train")],
            [new("local", ["dlinear", "zeus"], Train), new("pod", ["zeus"], Train)]);
    }

    static string Told(string name, RegistryResult<PlacementResolution> result)
    {
        if (!result.TryGetValue(out var placed))
        {
            Assert.True(result.TryGetRefusal(out var refusal));
            return name + $" {refusal.OutcomeKind} {refusal.ErrorCategory}: {refusal.Detail}";
        }
        var lines = placed.AdmissibleSitesByCell
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + string.Join(",", pair.Value));
        return name + " " + string.Join(" | ", lines);
    }
}
