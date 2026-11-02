using System.Diagnostics;
using System.Text;
using Xunit;

namespace Fern.Site.Tests;

/// <summary>
/// The process boundary: what crosses it, what is bounded, and what happens
/// when the child misbehaves. Every test here starts a REAL child process --
/// a fork, two pipes and an exit status -- because a fake writer proves nothing
/// about the thing that actually runs on a rented machine.
/// </summary>
public sealed class StageProcessTests
{
    [Fact]
    public void PhaseTimingCrossesTheBoundedProgressStream()
    {
        const string line = """FERN_TRAIN_PROGRESS {"schema":"fern.phase_progress.v1","operation_id":"unit-1","sequence":1,"span":1,"phase":"preparation","state":"started","seconds":0}""";
        var forwarded = new StringWriter();
        var output = new StageCommandOutput { Progress = forwarded };
        output.Write(Encoding.UTF8.GetBytes(line[..31]));
        output.Write(Encoding.UTF8.GetBytes(line[31..] + "\n"));
        output.Flush();
        Assert.Equal(line + "\n", forwarded.ToString());
    }

    [Fact]
    public void CommandOutputTailStaysBoundedAndKeepsTheFailureEnd()
    {
        var output = new CommandOutputTail();
        output.Write(Encoding.UTF8.GetBytes(new string('x', SitePaths.MaximumCommandOutputDetailBytes)));
        output.Write(Encoding.UTF8.GetBytes("\nfinal failure"));

        Assert.Equal(SitePaths.MaximumCommandOutputDetailBytes, output.Bytes.Count);
        // The END is the part that says why it failed; the beginning is banners.
        Assert.EndsWith("final failure", output.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void StageProgressStreamPreservesSplitBoundedMessages()
    {
        var forwarded = new StringWriter();
        var output = new StageCommandOutput { Progress = forwarded };
        const string valid =
            """FERN_TRAIN_PROGRESS {"schema":"fern.training_progress.v1","kind":"training_epoch","current":10,"total":100}""";
        string[] chunks =
        [
            "ordinary trainer prose\nFERN_TRAIN_PRO",
            "GRESS {\"schema\":\"fern.training_progress.v1\",\"kind\":\"training_epoch\",",
            "\"current\":10,\"total\":100}\n",
            valid + "\n",
            """FERN_TRAIN_PROGRESS {"schema":"wrong","kind":"training_epoch","current":20,"total":100}""" + "\n",
            "FERN_TRAIN_PROGRESS " + new string('x', SitePaths.MaximumStageProgressLineBytes + 1) + "\n",
        ];
        foreach (var chunk in chunks)
        {
            output.Write(Encoding.UTF8.GetBytes(chunk));
        }
        output.Flush();

        // One line forwarded, once: the message split across three writes is
        // reassembled, the identical repeat is not repeated, the wrong schema
        // is not protocol, and the over-long line is dropped whole rather than
        // truncated into something that might parse.
        Assert.Equal(valid + "\n", forwarded.ToString());
        Assert.True(output.Tail.Bytes.Count <= SitePaths.MaximumCommandOutputDetailBytes);
    }

    [Fact]
    public void StageProgressCrossesARealChildPipeBeforeExit()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var child = BoundaryChild(home, "progress");
        var forwarded = new StringWriter();
        var output = new StageCommandOutput { Progress = forwarded };

        var failure = StageAttemptSite.RunBoundedStageCommand(
            new ProcessStartInfo(child), output.Write, TimeSpan.FromSeconds(20));
        output.Flush();

        Assert.Null(failure);
        Assert.Contains("\"current\":10", forwarded.ToString(), StringComparison.Ordinal);
        // The banner the child printed on the same pipe never crosses.
        Assert.DoesNotContain("trainer banner", forwarded.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("success", 20, null, "boundary success")]
    [InlineData("failure", 20, "exit status 17", "boundary declared failure")]
    [InlineData("hang", 2, "timed out", "boundary entered but never returned")]
    [InlineData("flood", 20, "exit status 19", "final boundary failure")]
    public void StageProcessBoundaryEnumeratesExitHangAndOutputFlood(
        string mode,
        int timeoutSeconds,
        string? expectedFailure,
        string expectedOutput)
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var child = BoundaryChild(home, mode);
        var output = new CommandOutputTail();
        var started = Stopwatch.StartNew();

        var failure = StageAttemptSite.RunBoundedStageCommand(
            new ProcessStartInfo(child), output.Write, TimeSpan.FromSeconds(timeoutSeconds));

        if (expectedFailure is null)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.NotNull(failure);
            Assert.Contains(expectedFailure, failure!.Value.Detail, StringComparison.Ordinal);
        }
        Assert.Contains(
            expectedOutput,
            Encoding.UTF8.GetString(output.Bytes.ToArray()),
            StringComparison.Ordinal);
        if (mode == "hang")
        {
            // The deadline is the point: a hung child must not hold the Site.
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"hung child returned after {started.Elapsed}");
        }
        Assert.True(output.Bytes.Count <= SitePaths.MaximumCommandOutputDetailBytes);
    }

    [Fact]
    public void RunStagePackageTimeoutReportsBoundedProcessOutput()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var outbound = home.Join("outbound");
        Directory.CreateDirectory(outbound);
        SiteFixture.WriteInstructions(
            outbound,
            SitePaths.StageRequestFile,
            Encoding.UTF8.GetBytes(
                """{"schema":"fern.stage_runner_request.v1","protocol_version":"fern.stage_runner.v1","invocation":{"implementation":{"runner":"rust_binary","entrypoint":"fern-stage-runner"}}}"""),
            writeRequest: true);
        SiteFixture.PlaceExecutable(outbound, SitePaths.PackagedRunnerFile, SiteFixture.BoundaryScript("hang"));
        var started = Stopwatch.StartNew();

        var run = StageAttemptSite.RunStagePackage(
            outbound, home.Join("runtime"), python: string.Empty, TimeSpan.FromSeconds(2));

        Assert.False(run.IsOk);
        Assert.Contains("Stage process timed out", run.Failure.Detail, StringComparison.Ordinal);
        // The child's own last words survive the timeout; without them the
        // operator is told only that something took too long.
        Assert.Contains("boundary entered but never returned", run.Failure.Detail, StringComparison.Ordinal);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"timeout returned after {started.Elapsed}");
    }

    static string BoundaryChild(SiteFixture.TemporaryDirectory home, string mode)
    {
        var path = home.Join("boundary-" + mode);
        File.WriteAllBytes(path, SiteFixture.BoundaryScript(mode));
        SiteFixture.MakeExecutable(path);
        return path;
    }

    [Fact]
    public void RunnerOutputTailIsWrittenBesideTheReportOnlyWhenTheRunnerPrinted()
    {
        using var root = new SiteFixture.TemporaryDirectory();
        var tail = new CommandOutputTail();
        tail.WriteBeside(root.Path);
        var path = Path.Combine(root.Path, "report", "runner_output.tail");
        Assert.False(File.Exists(path), "a silent runner leaves no tail file: the return package refuses an empty member");

        tail.Write(Encoding.UTF8.GetBytes("banner\nTraceback: the real reason\n"));
        tail.WriteBeside(root.Path);
        Assert.True(File.Exists(path));
        Assert.Equal("banner\nTraceback: the real reason\n", File.ReadAllText(path));
        Assert.Equal(SitePaths.RunnerOutputTailFile, "report/runner_output.tail");
    }
}
