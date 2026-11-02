using Fern.Registry;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// A study whose declared data inputs are not in the source it runs from is
/// refused when it is registered.
///
/// This is the failure of 2026-09-26. A source registered without its compiled
/// study holds the code alone; a study that declares a controller recording and
/// runs from such a source produces cells that reach a machine, look for a file
/// that was never shipped, and fail in about two seconds each. The whole
/// density suite did that, was re-offered, and did it again, while four
/// machines looked busy. Both kinds of source are valid and nothing
/// distinguishes them by name, so the study is what must be checked against the
/// source it names.
/// </summary>
public sealed class StudyDeclaredInputsTests
{
    const string OneRecording = """
        {"schema":"fern.compiled-study.v1","dataset":{"name":"ball_in_cup","inputs":{
          "manifest_file":{"path":"results/corpus/controller_recordings/short/ball_in_cup/normal/manifest.json",
                           "sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","byte_count":704000},
          "world_file":{"path":"results/corpus/controller_recordings/short/ball_in_cup/normal/world.npz",
                        "sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","byte_count":6800000}}}}
        """;

    const string NoRecording = """{"schema":"fern.compiled-study.v1","dataset":{"name":"lorenz"}}""";

    static readonly string CodeOnly = Files("python/fern_train/train.py", "serie/pipeline.py");

    static readonly string CodeAndBothRecordings = Files(
        "python/fern_train/train.py",
        "serie/pipeline.py",
        "results/corpus/controller_recordings/short/ball_in_cup/normal/manifest.json",
        "results/corpus/controller_recordings/short/ball_in_cup/normal/world.npz");

    static readonly string CodeAndOneRecording = Files(
        "python/fern_train/train.py",
        "results/corpus/controller_recordings/short/ball_in_cup/normal/manifest.json");

    static string Files(params string[] paths) =>
        "[" + string.Join(",", paths.Select(path =>
            $$"""{"path":"{{path}}","byte_digest":"{{new string('c', 64)}}"}""")) + "]";

    [Fact]
    public void AStudyDeclaringARecordingIsRefusedAgainstASourceOfCodeAlone()
    {
        var refusal = Register(CodeOnly, OneRecording);
        Assert.NotNull(refusal);
        Assert.Contains("does not carry the data this study declares", refusal, StringComparison.Ordinal);
        // Both missing files are named, so a person reads what to ship.
        Assert.Contains("manifest.json", refusal, StringComparison.Ordinal);
        Assert.Contains("world.npz", refusal, StringComparison.Ordinal);
        // And it says what to do, because the flag's own help invites the mistake.
        Assert.Contains("--study", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ASourceCarryingEveryDeclaredRecordingIsAccepted()
    {
        Assert.Null(Register(CodeAndBothRecordings, OneRecording));
    }

    [Fact]
    public void APartlyCarriedStudyNamesOnlyTheFileThatIsMissing()
    {
        var refusal = Register(CodeAndOneRecording, OneRecording);
        Assert.NotNull(refusal);
        Assert.Contains("world.npz", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("manifest.json", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AStudyThatDeclaresNoDataRunsFromCodeAlone()
    {
        // The ordinary case, and the reason this is checked per study rather
        // than by refusing code-only sources: most studies need no recording.
        Assert.Null(Register(CodeOnly, NoRecording));
    }

    /// <summary>Register one study against a source holding those files; the refusal, or null.</summary>
    static string? Register(string declaredFilesJson, string compiledStudyJson)
    {
        var writer = RegistryHome.OpenWriter(ProductionHome.CreateRegistry());
        long sourceId;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
                VALUES ('5e87434e', $files, 'commit', '2026-09-26') RETURNING id
                """;
            command.Parameters.AddWithValue("$files", declaredFilesJson);
            sourceId = Convert.ToInt64(command.ExecuteScalar());
        }
        var cell = new RegistryOperations.CompiledCell(
            0, "conditional_gaussian", "dm_control.ball_in_cup.forcing", 42,
            """{"schema":"fern.training.v2","model":{"type":"fern"},"runtime":{"device":"cuda"}}""",
            """{"device":"cuda","requires_cuda":true}""",
            """{"unit_results":"retained"}""");
        var registered = RegistryOperations.RegisterStudy(
            writer, "density-suite", 1, compiledStudyJson, sourceId, [cell]);
        if (registered.TryGetValue(out _)) return null;
        registered.TryGetRefusal(out var refusal);
        return refusal.Detail;
    }
}
