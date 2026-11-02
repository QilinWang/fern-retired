using System.Text.Json;
using Fern.Experiment;
using Fern.Scheduler;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Collecting a finished study, now that a cell can save its own result.
///
/// The case worth reading first is the mixed one: a study half of whose cells
/// saved themselves and half of which handed their documents back has no single
/// answer, and the failure of guessing is a collection that silently contains
/// half a study.
/// </summary>
public sealed class StudyCollectTests
{
    [Fact]
    public async Task TheCollectionCommandWritesPlainFilesIntoAFreshSharedFolder()
    {
        var registry = TestRegistry.CreateRegistry();
        var root = Path.GetDirectoryName(registry)!;
        using (var writer = Fern.Registry.RegistryHome.OpenWriter(registry))
        {
            using var source = writer.Connection.CreateCommand();
            source.CommandText = "INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at) VALUES ('test', '[]', 'commit', 'now') RETURNING id";
            var sourceId = Convert.ToInt64(source.ExecuteScalar());
            Assert.True(Fern.Registry.RegistryOperations.RegisterSite(writer, "local", "{\"device\":\"cuda\",\"admitted_models\":[\"fern\"]}", "{\"slots\":1}").TryGetValue(out var site));
            TestRegistry.DeclareCodeFrom(writer, site.SiteId);
            var cell = new Fern.Registry.RegistryOperations.CompiledCell(0, "one", "data", 42,
                "{\"schema\":\"fern.training.v2\",\"model\":{\"type\":\"fern\"}}", "{\"device\":\"cuda\",\"requires_cuda\":true}", "{\"unit_results\":\"retained\"}");
            Assert.True(Fern.Registry.RegistryOperations.RegisterStudy(writer, "worked", 1, "{}", sourceId, [cell]).TryGetValue(out var study));
            Assert.True(Fern.Registry.RegistryOperations.TryStartAttempt(writer, study.CellIds[0], site.SiteId, "{}").TryGetValue(out var attempt));
            Assert.True(Fern.Registry.RegistryOperations.FinishAttempt(writer, attempt.AttemptId, attempt.FencingEpoch, Document).IsOk);
        }
        var destination = Path.Combine(root, "shared");
        var binary = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Fern.Run.Cli/bin/Debug/net11.0/fern-run.dll"));
        Assert.True(File.Exists(binary), $"missing built CLI: {binary}");
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { binary, "collect-study-results", "--registry", registry, "--study", "worked", "--as-documents", "--into", destination }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(15000);
        if (!exited) process.Kill(entireProcessTree: true);
        Assert.True(exited, "collection did not finish");
        Assert.True(process.ExitCode == 0, await stderr);
        Assert.Contains("collected 1 cell", await stdout, StringComparison.Ordinal);
        Assert.Equal(Document, File.ReadAllText(Path.Combine(destination, "one.results.json")));
        Assert.Empty(Directory.GetDirectories(destination));
    }

    // ── slice 2 of the command-line design (2026-09-26): a study still running ──

    /// <summary>
    /// The case the owner hit on 2026-09-26: eight accepted cells of two running
    /// studies could be read only from worker files, because the verb refused a
    /// study while any other cell of it still waited. A study with one accepted
    /// cell and three that are waiting, running and failed collects the one, and
    /// names the three with their states on standard error.
    /// </summary>
    [Fact]
    public async Task AStudyWhoseOtherCellsWaitStillCollectsItsAcceptedCells()
    {
        var registry = TestRegistry.CreateRegistry();
        var root = Path.GetDirectoryName(registry)!;
        using (var writer = Fern.Registry.RegistryHome.OpenWriter(registry))
        {
            var (siteId, cellIds) = RegisterCells(writer, root, "accepted-one", "failed-two", "running-three", "waiting-four");
            Assert.True(Fern.Registry.RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}").TryGetValue(out var accepted));
            Assert.True(Fern.Registry.RegistryOperations.FinishAttempt(
                writer, accepted.AttemptId, accepted.FencingEpoch, Reference("accepted-one", Sha)).IsOk);
            Assert.True(Fern.Registry.RegistryOperations.TryStartAttempt(writer, cellIds[1], siteId, "{}").TryGetValue(out var failed));
            Assert.True(Fern.Registry.RegistryOperations.FailAttempt(
                writer, failed.AttemptId, failed.FencingEpoch, Fern.Registry.RegistryOperations.AttemptFailed,
                CellFailureDetail.Write("unknown dataset", deterministic: true, CellFailureDetail.ClassDeterministic)).IsOk);
            Assert.True(Fern.Registry.RegistryOperations.TryStartAttempt(writer, cellIds[2], siteId, "{}").TryGetValue(out var running));
            Assert.True(Fern.Registry.RegistryOperations.RenewAttempt(writer, running.AttemptId, running.FencingEpoch, "this-machine/5151").IsOk);
        }
        var into = Path.Combine(root, "shared", "accepted.json");

        var (exit, stdout, stderr) = await Collect(registry, "--into", into);

        Assert.True(exit == 0, stderr);
        Assert.Contains("collected 1 cell reference(s)", stdout, StringComparison.Ordinal);
        using (var document = JsonDocument.Parse(File.ReadAllText(into)))
        {
            var cells = document.RootElement.GetProperty("cells");
            Assert.Equal("accepted-one", Assert.Single(cells.EnumerateObject()).Value.GetProperty("run").GetString());
        }
        // One line per cell not collected, each with the state monitor-study prints for it.
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Contains(lines, line => line.Contains("failed-two", StringComparison.Ordinal) && line.Contains("failed", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("running-three", StringComparison.Ordinal) && line.Contains("running", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("waiting-four", StringComparison.Ordinal) && line.Contains("waiting", StringComparison.Ordinal));
        Assert.DoesNotContain("accepted-one", stderr, StringComparison.Ordinal);
    }

    /// <summary>With nothing accepted there is nothing to collect: the refusal of today stands, exit 3, and names every cell.</summary>
    [Fact]
    public async Task AStudyWithNoAcceptedCellStillRefuses()
    {
        var registry = TestRegistry.CreateRegistry();
        var root = Path.GetDirectoryName(registry)!;
        using (var writer = Fern.Registry.RegistryHome.OpenWriter(registry))
        {
            var (siteId, cellIds) = RegisterCells(writer, root, "running-one", "waiting-two");
            Assert.True(Fern.Registry.RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}").TryGetValue(out var running));
            Assert.True(Fern.Registry.RegistryOperations.RenewAttempt(writer, running.AttemptId, running.FencingEpoch, "this-machine/5151").IsOk);
        }
        var into = Path.Combine(root, "shared", "accepted.json");

        var (exit, _, stderr) = await Collect(registry, "--into", into);

        Assert.Equal(3, exit);
        Assert.Contains("2 cell(s) have no accepted attempt", stderr, StringComparison.Ordinal);
        Assert.Contains("running-one", stderr, StringComparison.Ordinal);
        Assert.Contains("waiting-two", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(into));
    }

    /// <summary>
    /// The decision itself: the accepted cells are collected by the rules that
    /// were there, the others ride on the plan with their states for the verb to
    /// name, and with nothing accepted the refusal names every cell.
    /// </summary>
    [Fact]
    public void TheDecisionCollectsWhatIsAcceptedAndNamesTheRest()
    {
        StudyCollect.Unaccepted[] others = [new("two", "waiting"), new("three", "failed")];

        var decided = StudyCollect.Decide("worked", [new StudyCollect.Accepted("one", Reference("one", Sha))], others, asDocuments: false);

        var references = Assert.IsType<StudyCollect.Plan.References>(decided.Value);
        Assert.Equal(1, references.CellCount);
        Assert.Equal(others, references.NotCollected);
        Assert.Equal("cell two has no accepted attempt and was not collected; its state is waiting", references.NotCollected[0].Sentence);
        var documents = Assert.IsType<StudyCollect.Plan.Documents>(
            StudyCollect.Decide("worked", [new StudyCollect.Accepted("one", Document)], others, asDocuments: true).Value);
        Assert.Equal(others, documents.NotCollected);

        var refusal = Refusal(StudyCollect.Decide("worked", [], others, asDocuments: false));
        Assert.Contains("no accepted cell to collect; 2 cell(s) have no accepted attempt: two (waiting), three (failed)", refusal, StringComparison.Ordinal);
    }

    /// <summary>A study named "worked" of the given cells, on one Site that holds its source.</summary>
    static (long SiteId, IReadOnlyList<long> CellIds) RegisterCells(Fern.Registry.RegistryWriter writer, string root, params string[] names)
    {
        using var source = writer.Connection.CreateCommand();
        source.CommandText = "INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at) VALUES ('test', '[]', 'commit', 'now') RETURNING id";
        var sourceId = Convert.ToInt64(source.ExecuteScalar());
        Assert.True(Fern.Registry.RegistryOperations.RegisterSite(writer, "local", "{\"device\":\"cuda\",\"admitted_models\":[\"fern\"]}", "{\"slots\":1}").TryGetValue(out var site));
        TestRegistry.DeclareCodeFrom(writer, site.SiteId);
        var cells = names.Select((name, ordinal) => new Fern.Registry.RegistryOperations.CompiledCell(ordinal, name, "data", 42,
            "{\"schema\":\"fern.training.v2\",\"model\":{\"type\":\"fern\"}}", 
            "{\"device\":\"cuda\",\"requires_cuda\":true}", "{\"unit_results\":\"retained\"}")).ToList();
        Assert.True(Fern.Registry.RegistryOperations.RegisterStudy(writer, "worked", 1, "{}", sourceId, cells).TryGetValue(out var study));
        return (site.SiteId, study.CellIds);
    }

    /// <summary>The built verb, run as a person runs it: `collect-study-results --registry R --study worked` and the given flags.</summary>
    static async Task<(int Exit, string Stdout, string Stderr)> Collect(string registry, params string[] flags)
    {
        var binary = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Fern.Run.Cli/bin/Debug/net11.0/fern-run.dll"));
        Assert.True(File.Exists(binary), $"missing built CLI: {binary}");
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { binary, "collect-study-results", "--registry", registry, "--study", "worked" }.Concat(flags)) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(15000);
        if (!exited) process.Kill(entireProcessTree: true);
        Assert.True(exited, "collection did not finish");
        return (process.ExitCode, await stdout, await stderr);
    }

    static string Reference(string cell, string sha) =>
        $$"""
        {"schema":"fern.saved_cell_result.v1","database":"/results/science.db","study":"worked","run":"{{cell}}",
         "sha256":"{{sha}}","reference":"/results/science.db#study=worked&run={{cell}}&sha256={{sha}}"}
        """;

    const string Document = """{"schema":"fern.study_unit_results.v4","cell":"one","study":"worked"}""";
    const string Sha = "1111111111111111111111111111111111111111111111111111111111111111";

    static string Refusal(TextResult<StudyCollect.Plan> result)
    {
        Assert.False(result.IsOk);
        return result.Refusal;
    }

    [Fact]
    public void AStudyWhoseCellsAllSavedThemselvesCollectsIntoOneFileOfReferences()
    {
        var decided = StudyCollect.Decide("worked",
            [new StudyCollect.Accepted("one", Reference("one", Sha)),
             new StudyCollect.Accepted("two", Reference("two", Sha))], [], asDocuments: false);

        var references = Assert.IsType<StudyCollect.Plan.References>(decided.Value);
        Assert.Equal(2, references.CellCount);
        using var document = JsonDocument.Parse(references.Document);
        Assert.Equal(StudyCollect.AcceptedCellResultsSchema, document.RootElement.GetProperty("schema").GetString());
        Assert.Equal("worked", document.RootElement.GetProperty("study").GetString());
        var cells = document.RootElement.GetProperty("cells");
        Assert.Equal("/results/science.db", cells.GetProperty("one").GetProperty("database").GetString());
        Assert.Equal("two", cells.GetProperty("two").GetProperty("run").GetString());
        Assert.Equal(Sha, cells.GetProperty("one").GetProperty("sha256").GetString());
        Assert.Contains("study=worked", cells.GetProperty("two").GetProperty("reference").GetString()!, StringComparison.Ordinal);
        // The document carries references and nothing else: no copy of a result.
        Assert.DoesNotContain("study_unit_results", references.Document, StringComparison.Ordinal);
    }

    [Fact]
    public void AStudyWhoseCellsCarriedTheirDocumentsCollectsIntoFoldersAsBefore()
    {
        var decided = StudyCollect.Decide("worked",
            [new StudyCollect.Accepted("one", Document), new StudyCollect.Accepted("two", Document)], [], asDocuments: false);

        var documents = Assert.IsType<StudyCollect.Plan.Documents>(decided.Value);
        Assert.Equal(2, documents.Units.Count);
        Assert.Equal(Document, documents.Units[0].Result);
    }

    /// <summary>
    /// Half and half is refused with both lists, because one file of references
    /// and a tree of folders are different things and a reader who asked for one
    /// would silently get half of it.
    /// </summary>
    [Fact]
    public void AStudyThatIsHalfSavedAndHalfCarriedIsRefusedNamingBothSides()
    {
        var refusal = Refusal(StudyCollect.Decide("worked",
            [new StudyCollect.Accepted("saved-one", Reference("saved-one", Sha)),
             new StudyCollect.Accepted("carried-two", Document)], [], asDocuments: false));

        Assert.Contains("half saved and half carried", refusal, StringComparison.Ordinal);
        Assert.Contains("Saved into a store: saved-one", refusal, StringComparison.Ordinal);
        Assert.Contains("Carried as documents: carried-two", refusal, StringComparison.Ordinal);
        Assert.Contains("--results-root", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceMissingAFieldIsRefusedByCellAndField()
    {
        var refusal = Refusal(StudyCollect.Decide("worked",
            [new StudyCollect.Accepted("one",
                """{"schema":"fern.saved_cell_result.v1","database":"/results/science.db","study":"worked"}""")], [], asDocuments: false));

        Assert.Contains("cell one", refusal, StringComparison.Ordinal);
        Assert.Contains("names no run", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// The degradation that must never be quiet: a store nobody could reach for
    /// a whole card is not twenty green cells, it is twenty results that are not
    /// where the study says they are.
    /// </summary>
    [Fact]
    public void ACollectionIsRefusedWhileAnyResultNeverReachedTheStore()
    {
        var refusal = Refusal(StudyCollect.Decide("worked",
            [new StudyCollect.Accepted("one", Document, "result kept by the Home, store refused: read-only mount"),
             new StudyCollect.Accepted("two", Document, "result kept by the Home, store refused: read-only mount"),
             new StudyCollect.Accepted("three", Document)], [], asDocuments: false));

        Assert.Contains("2 accepted cell(s) whose result never reached the store", refusal, StringComparison.Ordinal);
        Assert.Contains("one (result kept by the Home", refusal, StringComparison.Ordinal);
        Assert.Contains("two (result kept by the Home", refusal, StringComparison.Ordinal);
        Assert.Contains("read-only mount", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("three (", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AStudyWithNothingAcceptedIsRefusedRatherThanCollectedEmpty()
    {
        Assert.Contains("no accepted cell", Refusal(StudyCollect.Decide("worked", [], [], asDocuments: false)), StringComparison.Ordinal);
        Assert.Contains("no accepted cell", Refusal(StudyCollect.Decide("worked", [], [], asDocuments: true)), StringComparison.Ordinal);
    }

    /// <summary>A result that is not JSON at all is a document, not a reference: it is copied, never parsed for fields.</summary>
    [Fact]
    public void AResultThatIsNotJsonIsTreatedAsADocument()
    {
        var decided = StudyCollect.Decide("worked", [new StudyCollect.Accepted("one", "not json at all")], [], asDocuments: false);

        Assert.IsType<StudyCollect.Plan.Documents>(decided.Value);
    }

    [Fact]
    public void ExplicitDocumentsRecoverEveryAcceptedResultWithoutChangingStoredBytes()
    {
        StudyCollect.Accepted[] accepted =
        [
            new("saved", Reference("saved", Sha)),
            new("unsaved", Document, "science store refused the second run named outputs"),
        ];
        Assert.False(StudyCollect.Decide("worked", accepted, [], asDocuments: false).IsOk);

        var decided = StudyCollect.Decide("worked", accepted, [], asDocuments: true);
        var documents = Assert.IsType<StudyCollect.Plan.Documents>(decided.Value);
        Assert.Equal(accepted, documents.Units);
        Assert.Equal(Reference("saved", Sha), documents.Units[0].Result);
        Assert.Equal(Document, documents.Units[1].Result);
        Assert.Equal("science store refused the second run named outputs", documents.Units[1].StoreRefusedBecause);
    }
}
