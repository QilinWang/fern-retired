using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fern.Registry;
using Fern.Scheduler;
using Xunit;
using Xunit.Abstractions;

namespace Fern.Scheduler.Tests;

/// <summary>
/// The registered route, end to end, with the real interpreter on this machine.
///
/// Every other test of this route answers with a fake process, which is how a
/// worker that wrote a request no Python accepts stayed green for a week: the
/// C# side was asserted against itself. This one compiles a card with Python,
/// freezes the source, registers a Site and the study in a temporary Registry
/// Home, runs the cell, and reads the study back the way `monitor-study` does.
/// It TRAINS — one epoch of a 16-wide residual MLP on twenty short LGSSM
/// trajectories, on the GPU, in one process — because a route that cannot
/// train is exactly the failure it exists to catch.
///
/// It returns without asserting, naming the reason, when this machine has no
/// CUDA device, no prepared interpreter, or no installed runtime to take the
/// Go compiler from. xunit 2 has no runtime skip, so the reason is written to
/// the test's output instead of a skip mark.
/// </summary>
[Collection(TimingSensitiveCollection.Name)]
public sealed class RegisteredRouteTrainsOneCellTests(ITestOutputHelper output)
{
    const string Python = "/home/user/.venv/bin/python";
    const string Card = "src/csharp/Fern.Scheduler.Tests/Fixtures/registered-route-one-cell.toml";
    const string StudyName = "registered-route-one-cell";
    const string CellName = "one";

    /// <summary>How long the whole cell may take before its process tree is killed.</summary>
    static readonly TimeSpan CellTimeout = TimeSpan.FromMinutes(30);

    [Fact]
    public void ARegisteredCellTrainsOnThisMachineAndIsAcceptedWithItsResults()
    {
        var repository = TestRegistry.RepoRoot();
        if (!File.Exists(Python))
        {
            output.WriteLine($"not run: no prepared interpreter at {Python}");
            return;
        }
        if (!CudaIsAvailable(repository))
        {
            output.WriteLine("not run: this machine reports no CUDA device, and a cell trains on the GPU");
            return;
        }
        RuntimeSelection selection;
        try
        {
            selection = RuntimeSelection.Read(Path.Combine(repository, RepositoryLayout.DeployDirectory, "current", "runtime.json"));
        }
        catch (RegistryFault refusal)
        {
            output.WriteLine($"not run: no installed runtime to take the Go card compiler from ({refusal.Message})");
            return;
        }

        // One process, on the GPU, with two threads: this suite shares the
        // machine with whatever else is training, and an unbounded thread pool
        // in a proof run is what starves the lanes beside it.
        Environment.SetEnvironmentVariable("OMP_NUM_THREADS", "2");
        Environment.SetEnvironmentVariable("MKL_NUM_THREADS", "2");

        var work = Directory.CreateTempSubdirectory("fern-registered-route-").FullName;
        var compiled = Path.Combine(work, "compiled-study.json");
        // The selected results tree for this run, outside the repository: the
        // cell saves its own document into the science store under it the moment
        // it ends, and `science_database_for` decides which file that is. C#
        // names the root and never a database.
        var results = Path.Combine(work, "results");

        // 1. Python compiles the card. Compilation is Python's, and the
        //    compiled document is what the store registers and the worker runs.
        var compilation = Run(Python,
            ["-m", "fern_analysis", "study", "compile", Path.Combine(repository, Card), "--out", compiled],
            repository, TimeSpan.FromMinutes(10));
        Assert.True(compilation.ExitCode == 0, $"study compile exited {compilation.ExitCode}: {compilation.Stderr}");
        Assert.True(File.Exists(compiled));

        // 2. A temporary Registry Home, initialized with the current schema.
        var registryPath = TestRegistry.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(registryPath);

        // 3. register-study-source: the commit the study's code is at, and the
        //    declaration of every file that code is. CHG 2026-09-13: this was
        //    freeze-study-source, which copied the tree into a content store;
        //    git holds those bytes, so the copy went. --from-worktree because a
        //    repository under test is a working tree like any other, and its
        //    declared files may differ from its commit.
        var source = StudySourceRegistration.Register(writer, repository, compiled, fromWorktree: true);
        Assert.True(source.SourceId > 0);

        // 4. register-site. The row is the one `register-site --profile` mints;
        //    it is written here rather than read from profiles/local-4070.json
        //    because that profile declares no admitted_models, and a Site that
        //    admits no model is placed no cell.
        //    CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): the Site
        //    takes each cell's code from the registered commit of this repository.
        var site = RegistryOperations.RegisterSite(
            writer, "local-4070",
            $$"""{"device":"cuda","admitted_models":["residual_mlp"],"executor":"local","code_from":"commit","repository":"{{repository}}"}""",
            """{"slots":1}""");
        Assert.True(site.TryGetValue(out var registeredSite));

        // 5. register-study: one studies row, one cells row.
        var registered = StudyRegistration.Register(
            writer, File.ReadAllText(compiled), StudyName, revision: 1, sourceId: source.SourceId, device: "cuda");
        Assert.True(registered.TryGetValue(out var study), Refusal(registered));
        Assert.Single(study.CellIds);

        // 5b. CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): the
        //     install that wrote a frozen view under the deploy root is retired. The
        //     worker writes the registered commit's declared files for the attempt,
        //     so the working tree is still not what a cell trains from.

        // 6. run-study-cells, with the production worker and a real interpreter.
        var worker = new LocalCellWorker(new SqliteStudyStore(writer), new LocalCellWorker.Placement(
            Python: Python,
            FernRunBinary: selection.RunBinary,
            FernCompileBinary: selection.CompileBinary,
            WorkRoot: Path.Combine(work, "cells"),
            Timeout: CellTimeout,
            CheckpointStore: Path.Combine(work, "store"),
            ResultsRoot: results));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = StudyOrchestrator.RunUntilIdle(
            new SqliteStudyStore(writer), worker, study.StudyId, StudyName, "local-4070", maxCycles: 0, stdout, stderr);
        output.WriteLine(stdout.ToString());
        output.WriteLine(stderr.ToString());
        Assert.True(exit == 0, $"run-study-cells exited {exit}: {stderr}");
        Assert.Contains($"study {StudyName} is finished", stdout.ToString(), StringComparison.Ordinal);

        // 7. monitor-study: the attempt is accepted, on this Site.
        var head = RegistryOperations.ReadStudy(writer.Connection, study.StudyId);
        Assert.True(head.TryGetValue(out var view));
        Assert.Equal(1, view.AcceptedCellCount);
        var rendered = StudyProjection.Render(writer.Connection, view);
        output.WriteLine(rendered);
        Assert.Contains("1/1 cells accepted", rendered, StringComparison.Ordinal);
        Assert.Contains("accepted", rendered, StringComparison.Ordinal);
        Assert.Contains("local-4070", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("why:", rendered, StringComparison.Ordinal);

        // 8. The accepted result is a REFERENCE: the cell put its document in the
        //    science store itself, and the attempt records where it went and the
        //    digest of what was stored, not a second copy.
        using var accepted = writer.Connection.CreateCommand();
        accepted.CommandText =
            """
            SELECT a.result_json FROM cells c JOIN attempts a ON a.id = c.accepted_attempt_id
            WHERE c.study_id = $study
            """;
        accepted.Parameters.AddWithValue("$study", study.StudyId);
        var recordedText = (string)accepted.ExecuteScalar()!;
        var recorded = JsonDocument.Parse(recordedText).RootElement;
        output.WriteLine("accepted attempt records: " + recordedText);
        Assert.Equal(StudyCollect.SavedResultSchema, recorded.GetProperty("schema").GetString());
        Assert.Equal(StudyName, recorded.GetProperty("study").GetString());
        // NOT asserted as the cell's name, and that is a finding rather than a
        // preference: the store takes `run` from the results document, and the
        // document names the artifact FOLDER, which this route always calls
        // "outputs". Every cell of every study therefore saves as
        // (study, run="outputs"), so a second cell lands as revision 2 of the
        // first one's run instead of as its own result. The reference is
        // self-consistent — it points at the row that exists — so the route is
        // provable today; what it points AT collides. Reported to the Python
        // half on 2026-09-11; when the document names the cell, this becomes
        // Assert.Equal(CellName, ...) and the worker gains the matching refusal.
        var run = recorded.GetProperty("run").GetString()!;
        // CHG 2026-09-13: the collision described above is now refused by this
        // assertion: the producer names the cell execution with a UUID, so both
        // different cells and later executions save independently of outputs/.
        Assert.StartsWith(CellName + "-", run, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(run[(CellName.Length + 1)..], "N", out _));
        var database = recorded.GetProperty("database").GetString()!;
        // CHG 2026-09-26 (cards-without-hashes, row B16): the store names a saved
        // document by its revision; a cell may still report a sha256 for older
        // fern-run builds, and nothing below depends on it.
        long? revision = recorded.TryGetProperty("revision", out var named) ? named.GetInt64() : null;
        Assert.StartsWith(results, database, StringComparison.Ordinal);
        Assert.True(File.Exists(database), $"the science store {database} was not created");
        // A reference costs a fraction of the document it stands for.
        Assert.True(recordedText.Length < 1000, $"a reference should be small; this one is {recordedText.Length} bytes");

        // 9. The store really holds that document under that digest. Read
        //    directly and read-only: the assertion is that a row exists with
        //    these three values, and `fern_analysis.scientific_store` owns the
        //    schema and its own tests.
        using (var science = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={database};Mode=ReadOnly;Pooling=False"))
        {
            science.Open();
            using var row = science.CreateCommand();
            row.CommandText =
                "SELECT revision, length(document) FROM study_results WHERE study = $study AND run = $run "
                + "ORDER BY revision DESC LIMIT 1";
            row.Parameters.AddWithValue("$study", StudyName);
            row.Parameters.AddWithValue("$run", run);
            using var read = row.ExecuteReader();
            Assert.True(read.Read(), $"the store {database} holds no result for {StudyName}/{run}");
            if (revision is { } saved) Assert.Equal(saved, read.GetInt64(0));
            var storedBytes = read.GetInt64(1);
            output.WriteLine($"stored document: {storedBytes} bytes, revision {read.GetInt64(0)}, reference in {recordedText.Length} bytes");
            Assert.True(storedBytes > recordedText.Length,
                "the stored document should be larger than the reference that stands for it");
        }

        // 10. collect-study-results is now a read: one file of references.
        var collected = Path.Combine(work, "accepted.json");
        var plan = StudyCollect.Decide(StudyName, [new StudyCollect.Accepted(CellName, recordedText)], [], asDocuments: false);
        Assert.True(plan.IsOk, plan.IsOk ? "" : plan.Refusal);
        File.WriteAllText(collected, Assert.IsType<StudyCollect.Plan.References>(plan.Value).Document);
        var acceptedFile = JsonDocument.Parse(File.ReadAllText(collected)).RootElement;
        Assert.Equal(StudyCollect.AcceptedCellResultsSchema, acceptedFile.GetProperty("schema").GetString());
        Assert.Equal(recorded.GetProperty("reference").GetString(),
            acceptedFile.GetProperty("cells").GetProperty(CellName).GetProperty("reference").GetString());
        output.WriteLine("accepted file: " + File.ReadAllText(collected));
    }

    static string Refusal<T>(RegistryResult<T> result) =>
        result.TryGetRefusal(out var refusal) ? refusal.Detail : "";

    /// <summary>Ask the prepared interpreter, because a CUDA device is a property of the machine and not of this process.</summary>
    static bool CudaIsAvailable(string repository)
    {
        var probe = Run(Python, ["-c", "import torch; print(torch.cuda.is_available())"], repository, TimeSpan.FromMinutes(5));
        return probe.ExitCode == 0 && probe.Stdout.Trim() == "True";
    }

    static (int ExitCode, string Stdout, string Stderr) Run(
        string program, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(program)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        start.Environment["PYTHONPATH"] = workingDirectory + Path.PathSeparator + Path.Combine(workingDirectory, "python");
        using var process = Process.Start(start)!;
        var outText = new StringBuilder();
        var errText = new StringBuilder();
        var reading = Task.WhenAll(
            Task.Run(() => outText.Append(process.StandardOutput.ReadToEnd())),
            Task.Run(() => errText.Append(process.StandardError.ReadToEnd())));
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            return (-1, outText.ToString(), $"{program} did not finish within {timeout}");
        }
        reading.GetAwaiter().GetResult();
        return (process.ExitCode, outText.ToString(), errText.ToString());
    }
}
