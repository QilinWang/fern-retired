using System.Text.Json;
using Xunit;

namespace Fern.Experiment.Tests;

public sealed class LanePathsCliTests
{
    static TextResult<byte[]> Execute(string lane, string campaignRoot = "results/paper_campaign", string workRoot = "tmp") =>
        LanePathsCli.Execute(JsonSerializer.SerializeToUtf8Bytes(new
        {
            lane,
            campaign_root = campaignRoot,
            work_root = workRoot,
        }));

    [Fact]
    public void show_lane_paths_derives_the_database_artifact_and_work_paths_together()
    {
        var result = Execute("lorenz-primary", "results/campaign", "scratch");
        Assert.True(result.IsOk, result.IsOk ? "" : result.Refusal);
        using var report = JsonDocument.Parse(result.Value);
        Assert.Equal(LanePathsCli.ReportSchema, report.RootElement.GetProperty("schema").GetString());
        Assert.Equal("lorenz-primary", report.RootElement.GetProperty("lane").GetString());
        Assert.Equal(Path.Combine("results/campaign", "lorenz-primary", "fern.db"), report.RootElement.GetProperty("db").GetString());
        Assert.Equal(Path.Combine("results/campaign", "lorenz-primary", "runs"), report.RootElement.GetProperty("artifact_root").GetString());
        Assert.Equal(Path.Combine("scratch", "lorenz-primary"), report.RootElement.GetProperty("work_root").GetString());
    }

    [Fact]
    public void public_lane_paths_receipt_matches_the_complete_rust_golden()
    {
        var result = LanePathsCli.Execute(JsonSerializer.SerializeToUtf8Bytes(new
        {
            lane = "lorenz-primary",
            campaign_root = "results/campaign",
            work_root = "scratch",
            public_report = true,
        }));
        Assert.True(result.IsOk, result.IsOk ? "" : result.Refusal);
        Assert.Equal(
            "{\n  \"artifact_root\": \"results/campaign/lorenz-primary/runs\",\n"
            + "  \"db\": \"results/campaign/lorenz-primary/fern.db\",\n"
            + "  \"lane\": \"lorenz-primary\",\n"
            + "  \"note\": \"derived here so no caller composes these by hand\",\n"
            + "  \"schema\": \"fern.lane_paths.v1\",\n"
            + "  \"work_root\": \"scratch/lorenz-primary\"\n}",
            System.Text.Encoding.UTF8.GetString(result.Value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has/slash")]
    [InlineData("has\\backslash")]
    [InlineData("has space")]
    public void an_invalid_lane_refuses_before_any_effect(string lane)
    {
        var result = Execute(lane);
        Assert.False(result.IsOk);
        Assert.Contains("one path segment", result.Refusal);
    }
}
