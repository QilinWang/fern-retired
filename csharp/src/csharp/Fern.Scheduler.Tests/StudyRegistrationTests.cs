using Fern.Registry;
using Fern.Scheduler;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Registering a compiled study on the one-store schema, with no lowering.
/// Uses a real compiled study Python wrote, so the shape under test is the
/// shape the live route produces.
/// </summary>
public sealed class StudyRegistrationTests
{
    [Theory]
    [InlineData(1L, 0, "cancel", "run")]
    [InlineData(99L, 3, "run", "run")]
    [InlineData(null, 0, "run", "cancel")]
    public async Task CancellingAnExactRevisionLeavesTheOtherRevisionUntouched(
        long? revision, int expectedExit, string firstState, string latestState)
    {
        var (path, sourceId) = Home();
        using var writer = RegistryHome.OpenWriter(path);
        var compiled = ACompiledStudy();
        Assert.True(StudyRegistration.Register(writer, compiled, "revised", 1, sourceId, "cuda").TryGetValue(out var first));
        Assert.True(StudyRegistration.Register(writer, compiled, "revised", 2, sourceId, "cuda").TryGetValue(out var latest));
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(TestRegistry.RepoRoot(),
            "src", "csharp", "Fern.Run.Cli", "bin", "Debug", "net11.0", "fern-run"))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var argument in new[] { "cancel-study", "--registry", path, "--study", "revised", "--state", "cancel" })
            start.ArgumentList.Add(argument);
        if (revision is not null)
        {
            start.ArgumentList.Add("--revision");
            start.ArgumentList.Add(revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        var message = await output + await error;
        Assert.True(process.ExitCode == expectedExit, message);
        Assert.True(RegistryOperations.ReadStudy(writer.Connection, first.StudyId).TryGetValue(out var firstView));
        Assert.True(RegistryOperations.ReadStudy(writer.Connection, latest.StudyId).TryGetValue(out var latestView));
        Assert.Equal(firstState, firstView.RequestedState);
        Assert.Equal(latestState, latestView.RequestedState);
        if (revision is not null)
            Assert.Contains($"revision {revision}", message, StringComparison.Ordinal);
    }

    // CHG 2026-09-26: one more level, since the test output sits under src/csharp/.
    // CHG 2026-09-27: the recorded studies moved with the live compiler.
    const string GoldenRoot = "../../../../../../tools/fern-compile-gleam/testdata/goldens";

    static string ACompiledStudy()
    {
        var root = Path.Combine(AppContext.BaseDirectory, GoldenRoot);
        // CHG: this projected Python golden retains the complete cells the
        // registration reads; missing evidence is a failure, never a pass.
        var path = Path.Combine(root, "lg-ssm-dlinear-horizon-64--20260911-634816c-python-compiled", "cell-identities.json");
        Assert.True(File.Exists(path), $"missing Python compiled-cell golden: {path}");
        return File.ReadAllText(path);
    }

    static (string Path, long SourceId) Home()
    {
        var path = TestRegistry.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        using var command = writer.Connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
            VALUES ('5e87434e', '[]', 'commit', '2026-09-11') RETURNING id
            """;
        return (path, Convert.ToInt64(command.ExecuteScalar()));
    }

    /// <summary>
    /// One cell row per compiled cell, each carrying the config document, the
    /// sha that verifies the bytes it will be given, what it needs to run and
    /// what of its output is kept. No stage, no graph, no second digest.
    /// </summary>
    [Fact]
    public void ACompiledStudyRegistersAsCellsWithNoLowering()
    {
        var compiled = ACompiledStudy();
        var (path, sourceId) = Home();
        using var writer = RegistryHome.OpenWriter(path);

        var registered = StudyRegistration.Register(writer, compiled, "from-a-golden", 1, sourceId, "cuda");
        Assert.True(registered.TryGetValue(out var value),
            registered.TryGetRefusal(out var refusal) ? refusal.Detail : "");
        Assert.NotEmpty(value.CellIds);

        using var command = writer.Connection.CreateCommand();
        command.CommandText =
            "SELECT name, requirements_json, retention_json, config_json FROM cells WHERE study_id = $study ORDER BY ordinal";
        command.Parameters.AddWithValue("$study", value.StudyId);
        using var rows = command.ExecuteReader();
        Assert.True(rows.Read());
        Assert.NotEmpty(rows.GetString(0));
        Assert.Contains("requires_cuda", rows.GetString(1));
        Assert.Contains("retained", rows.GetString(2));              // retention is declared, not derived from a graph
        Assert.Contains("fern.training.v2", rows.GetString(3));

        // And no stage table was written: this route does not lower.
        using var stages = writer.Connection.CreateCommand();
        // CHG: retirement removes the table itself, rather than leaving it empty.
        stages.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'stage_plan_instances'";
        Assert.Equal(0L, Convert.ToInt64(stages.ExecuteScalar()));
    }

    /// <summary>
    /// Slice M2 of what-stops-work-and-who-lifts-it: the compiler a compiled
    /// study names is recorded beside it; a malformed one refuses the study
    /// before anything is written; a study that names none records none.
    /// </summary>
    [Fact]
    public void TheCompilerACompiledStudyNamesIsRecorded()
    {
        var (path, sourceId) = Home();
        using var writer = RegistryHome.OpenWriter(path);
        var named = System.Text.Json.Nodes.JsonNode.Parse(ACompiledStudy())!.AsObject();
        named["compiler"] = new System.Text.Json.Nodes.JsonObject
        {
            ["path"] = "/home/user/study/deploy/compile-binaries/fern-compile.b68b",
            ["sha256"] = new string('b', 64),
        };
        Assert.True(StudyRegistration.Register(writer, named.ToJsonString(), "named", 1, sourceId, "cuda").TryGetValue(out var study));
        var recorded = RegistryOperations.ReadStudyCompiler(writer.Connection, study.StudyId);
        Assert.Equal(new string('b', 64), recorded!.Sha256);
        Assert.Equal("register-study", recorded.RecordedBy);

        named["compiler"] = new System.Text.Json.Nodes.JsonObject { ["path"] = "/x", ["sha256"] = "short" };
        var refused = StudyRegistration.Register(writer, named.ToJsonString(), "malformed", 1, sourceId, "cuda");
        Assert.False(refused.IsOk);
        using (var count = writer.Connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM studies WHERE name = 'malformed'";
            Assert.Equal(0L, Convert.ToInt64(count.ExecuteScalar()));
        }

        Assert.True(StudyRegistration.Register(writer, ACompiledStudy(), "unnamed", 1, sourceId, "cuda").TryGetValue(out var plain));
        Assert.Null(RegistryOperations.ReadStudyCompiler(writer.Connection, plain.StudyId));
    }

    /// <summary>
    /// The two facts C# reads out of a cell's configuration, each refused by
    /// name. It reads no others: the scientific words belong to the compiler,
    /// which owns the vocabulary. CHG 2026-09-27 (config-hash-leaves, slice 4):
    /// config_hash was the third and is no longer read.
    /// </summary>
    [Theory]
    [InlineData("""{"model":{"type":"fern"},"schema":"fern.training.v1"}""", "config.schema")]
    [InlineData("""{"schema":"fern.training.v2"}""", "config.model.type")]
    public void TheTwoAdmittedFactsAreRefusedByName(string config, string named)
    {
        var (path, sourceId) = Home();
        using var writer = RegistryHome.OpenWriter(path);
        var compiled = $$"""
            {"schema":"fern.compiled-study.v1","cells":[{"cell":"a","config":{{config}}}]}
            """;

        var registered = StudyRegistration.Register(writer, compiled, "admission", 1, sourceId, "cuda");

        Assert.True(registered.TryGetRefusal(out var refusal));
        Assert.Contains(named, refusal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentThatIsNotACompiledStudyIsRefused()
    {
        var (path, sourceId) = Home();
        using var writer = RegistryHome.OpenWriter(path);

        var registered = StudyRegistration.Register(writer, """{"schema":"something.else"}""", "wrong", 1, sourceId, "cuda");

        Assert.False(registered.IsOk);
        Assert.True(registered.TryGetRefusal(out var refusal));
        Assert.Contains("fern.compiled-study.v1", refusal.Detail, StringComparison.Ordinal);
    }
}
