using System.Text;
using System.Text.Json;
using Xunit;

namespace Fern.Experiment.Tests;

/// <summary>
/// The retired Rust path kernel's four exact source-test functions now run
/// against the C# Experiment owner.  The vector test is a path meaning
/// contract, not a JSON-byte compatibility claim.
/// </summary>
public sealed class ArtifactPathCliTests
{
    static ArtifactPathCli.Request Request(
        string root = "results/runs",
        string runCollection = "standalone",
        string? dataset = null,
        string modelName = "fern",
        long predLen = 336,
        long seed = 42,
        double noiseSigma = 0.0,
        long trajectoryDrawOrdinal = 0,
        long noiseDrawOrdinal = 0,
        string? runHash = null,
        string shortIdFormat = "single-run") =>
        new(root, runCollection, dataset, modelName, predLen, seed, noiseSigma, trajectoryDrawOrdinal, noiseDrawOrdinal, runHash, shortIdFormat);

    static string Execute(ArtifactPathCli.Request request)
    {
        var input = JsonSerializer.SerializeToUtf8Bytes(request, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });
        var receipt = ArtifactPathCli.Execute(input);
        Assert.True(receipt.IsOk, receipt.IsOk ? "" : receipt.Refusal);
        using var document = JsonDocument.Parse(receipt.Value);
        Assert.Equal(ArtifactPathCli.ReportSchema, document.RootElement.GetProperty("schema").GetString());
        return Native(document.RootElement.GetProperty("artifact_dir").GetString()!);
    }

    static string Native(string slashPath) => slashPath.Replace('/', Path.DirectorySeparatorChar);

    [Fact]
    public void sigma_path_matches_python_examples()
    {
        Assert.Equal("0p001", ArtifactPathCli.SigmaToPath(0.001));
        Assert.Equal("0p01", ArtifactPathCli.SigmaToPath(0.01));
        Assert.Equal("0p25", ArtifactPathCli.SigmaToPath(0.25));
        Assert.Equal("1", ArtifactPathCli.SigmaToPath(1.0));
        Assert.Equal("10", ArtifactPathCli.SigmaToPath(10.0));
    }

    [Fact]
    public void single_run_artifact_dir_uses_model_short_id()
    {
        Assert.Equal(
            Native("results/runs/lorenz/fern_h64_clean_s42_abcd"),
            Execute(Request(runCollection: "Lorenz", predLen: 64, runHash: "abcd1234efef5678")));
    }

    [Fact]
    public void grid_artifact_dir_uses_dataset_and_draw_ordinals()
    {
        Assert.Equal(
            Native("results/runs/icml-shock-8/lorenz_param_shock_h336_traj2_sig0p25_noise3_s7_feed"),
            Execute(Request(
                runCollection: "icml-shock-8",
                dataset: "lorenz_param_shock",
                predLen: 336,
                seed: 7,
                noiseSigma: 0.25,
                trajectoryDrawOrdinal: 2,
                noiseDrawOrdinal: 3,
                runHash: "feed1234feed5678",
                shortIdFormat: "grid-draw")));
    }

    [Fact]
    public void artifact_paths_match_shared_golden_vectors()
    {
        var path = Path.Combine(RepositoryFiles.Root(), "tests", "golden", "fern_artifact_paths.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var vector in document.RootElement.EnumerateArray())
        {
            var actual = Execute(Request(
                root: vector.GetProperty("root").GetString()!,
                runCollection: vector.GetProperty("run_collection").GetString()!,
                dataset: vector.TryGetProperty("dataset", out var dataset) && dataset.ValueKind != JsonValueKind.Null ? dataset.GetString() : null,
                modelName: vector.GetProperty("model_name").GetString()!,
                predLen: vector.GetProperty("pred_len").GetInt64(),
                seed: vector.GetProperty("seed").GetInt64(),
                noiseSigma: vector.GetProperty("noise_sigma").GetDouble(),
                trajectoryDrawOrdinal: vector.GetProperty("trajectory_draw_ordinal").GetInt64(),
                noiseDrawOrdinal: vector.GetProperty("noise_draw_ordinal").GetInt64(),
                runHash: vector.GetProperty("run_hash").GetString(),
                shortIdFormat: vector.GetProperty("short_id_format").GetString()!));
            Assert.Equal(Native(vector.GetProperty("expected").GetString()!), actual);
        }
    }

    [Fact]
    public void unknown_short_id_format_is_refused_before_any_effect()
    {
        var result = ArtifactPathCli.Execute(Encoding.UTF8.GetBytes("""
            {"root":"results/runs","run_collection":"x","dataset":null,"model_name":"fern","pred_len":1,"seed":1,"noise_sigma":0,"trajectory_draw_ordinal":0,"noise_draw_ordinal":0,"run_hash":null,"short_id_format":"unknown"}
            """));
        Assert.False(result.IsOk);
        Assert.Contains("short_id_format", result.Refusal);
    }
}
