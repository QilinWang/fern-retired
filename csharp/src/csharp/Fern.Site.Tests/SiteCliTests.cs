using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Fern.Site.Tests;

/// <summary>
/// The active `fern-site` executable's command and resident-session surface.
///
/// The Go tests re-executed their own test binary to get a process; here the
/// real executable is built by the project reference and spawned, so the
/// process tests exercise the same program the scheduler will spawn rather
/// than a stand-in for it.
/// </summary>
public sealed class SiteCliTests
{
    [Fact]
    public void InstallationIdentityNamesWorkerSiteRole()
    {
        var process = Spawn(["installation-identity"]);

        Assert.Equal(0, process.ExitStatus);
        Assert.Equal(string.Empty, process.Standard.Error);
        using var identity = JsonDocument.Parse(process.Standard.Output);
        Assert.Equal("fern.installation_identity.v4", identity.RootElement.GetProperty("schema").GetString());
        Assert.Equal("worker-site", identity.RootElement.GetProperty("role").GetString());
        Assert.Equal(
            "fern.run_plan_parent_child.v2",
            identity.RootElement.GetProperty("run_plan_protocol").GetString());
        // The installer parses this with unknown fields forbidden, so an added
        // field is a refusal on the other side, not a harmless extra.
        Assert.Equal(3, identity.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void PerStageAttemptCommandIsRetiredFromSourceAndBuiltExecutable()
    {
        var process = Spawn(["execute-stage-attempt"]);
        Assert.Equal(1, process.ExitStatus);
        Assert.Equal(string.Empty, process.Standard.Output);
        Assert.DoesNotContain("execute-stage-attempt", process.Standard.Error, StringComparison.Ordinal);
        // CHG 2026-09-10: execute-cell-phase is retired for the same reason, one
        // generation later. It called the three functions `serve` calls and could
        // move no bytes, so it was an entrance rather than a capability.
        Assert.DoesNotContain("execute-cell-phase", process.Standard.Error, StringComparison.Ordinal);
        Assert.Contains("serve --stdio", process.Standard.Error, StringComparison.Ordinal);
        Assert.Contains("workers --work-root", process.Standard.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void OneSiteStdioProcessServesSeveralFramedContentOperationsUntilHomeClosesIt()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var start = new ProcessStartInfo(SiteExecutable())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            "serve", "--stdio", "--work-root", home.Join("site"),
            "--worker-root", home.Join("worker"), "--python", "/not-used-before-a-cell",
            "--timeout-seconds", "30",
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("the operating system started no fern-site process");

        var hello = Exchange(process, new JsonObject
        {
            ["schema"] = CellPhaseSite.SessionSchema,
            ["operation"] = "hello",
            ["request_id"] = "1",
        });
        Assert.Equal("hello", hello.Header["operation"]!.GetValue<string>());
        Assert.False(process.HasExited, "the resident Site exited after its handshake");

        var content = Encoding.UTF8.GetBytes("one immutable bank");
        var digest = SiteFixture.Sha256(content)["sha256:".Length..];
        var stored = Exchange(process, new JsonObject
        {
            ["schema"] = CellPhaseSite.SessionSchema,
            ["operation"] = "put_content",
            ["request_id"] = "2",
            ["sha256"] = digest,
        }, content);
        Assert.Equal("content_stored", stored.Header["operation"]!.GetValue<string>());
        JsonObject Begin(string requestId, long epoch) => new()
        {
            ["schema"] = CellPhaseSite.SessionSchema,
            ["operation"] = "begin_cell",
            ["request_id"] = requestId,
            ["cell_attempt_id"] = "cell-attempt-1",
            ["remote_execution_id"] = "remote-execution-1",
            ["parent_plan_epoch"] = epoch,
            ["stage_plan_instance_id"] = "instance-1",
            ["site_id"] = "site-a",
            ["presented_capability_digest"] = "sha256:" + new string('c', 64),
            ["cell_request_digest"] = "sha256:" + new string('d', 64),
            ["cell_submission_digest"] = "sha256:" + new string('e', 64),
        };
        var begun = Exchange(process, Begin("begin-1", 7));
        Assert.Equal("cell_begun", begun.Header["operation"]!.GetValue<string>());
        var replayedBegin = Exchange(process, Begin("begin-2", 7));
        Assert.Equal("cell_begun", replayedBegin.Header["operation"]!.GetValue<string>());
        var conflictingBegin = Exchange(process, Begin("begin-3", 8));
        Assert.Equal("refused", conflictingBegin.Header["operation"]!.GetValue<string>());
        Assert.Contains("another permit", conflictingBegin.Header["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(process.HasExited, "a conflicting cell replay must not destroy the machine session");
        var refused = Exchange(process, new JsonObject
        {
            ["schema"] = CellPhaseSite.SessionSchema,
            ["operation"] = "put_content",
            ["request_id"] = "bad",
            ["sha256"] = new string('0', 64),
        }, content);
        Assert.Equal("refused", refused.Header["operation"]!.GetValue<string>());
        Assert.Contains("does not hash", refused.Header["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(process.HasExited, "one refused message must not destroy the machine session");
        var present = Exchange(process, new JsonObject
        {
            ["schema"] = CellPhaseSite.SessionSchema,
            ["operation"] = "probe_content",
            ["request_id"] = "3",
            ["sha256"] = digest,
        });
        Assert.True(present.Header["present"]!.GetValue<bool>());
        Assert.False(process.HasExited, "the resident Site exited between operations");

        process.StandardInput.Close();
        Assert.True(process.WaitForExit(10_000), "fern-site did not accept EOF as the session end");
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, process.StandardError.ReadToEnd());
    }

    static CellPhaseSite.SessionEnvelope Exchange(Process process, JsonObject header, byte[]? body = null)
    {
        CellPhaseSite.WriteSessionEnvelope(process.StandardInput.BaseStream, header, body ?? []);
        var response = CellPhaseSite.ReadSessionEnvelope(process.StandardOutput.BaseStream);
        Assert.True(response.IsOk, response.RefusalDetail);
        return Assert.IsType<CellPhaseSite.SessionEnvelope>(response.Value);
    }

    // ---- spawning the built executable ----

    internal readonly record struct StandardStreams(string Output, string Error);

    internal readonly record struct CompletedRun(int ExitStatus, StandardStreams Standard);

    internal static CompletedRun Spawn(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(SiteExecutable())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("the operating system started no fern-site process");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "fern-site did not exit within two minutes");
        return new CompletedRun(process.ExitCode, new StandardStreams(output.Result, error.Result));
    }

    /// <summary>
    /// The built `fern-site`, chosen the way every Fern tool chooses a binary:
    /// the NEWER of the debug and release outputs by build time, never the one
    /// that happens to be called release. A missing binary FAILS -- a skipped
    /// process test reads exactly like a passing one.
    /// </summary>
    internal static string SiteExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "csharp", "Fern.sln")) /* CHG 2026-08-28: Cargo.toml retired with the last crate; the C# solution marks the root */)
        {
            directory = directory.Parent;
        }
        var root = directory?.FullName
            ?? throw new InvalidOperationException("no repository root above " + AppContext.BaseDirectory);
        var candidates = new[] { "Debug", "Release" }
            .Select(configuration => new FileInfo(Path.Combine(
                root, "src/csharp/Fern.Site.Cli/bin", configuration, "net11.0", "fern-site")))
            .Where(file => file.Exists)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();
        if (candidates.Count == 0)
        {
            throw new FileNotFoundException(
                "fern-site is not built under src/csharp/Fern.Site.Cli/bin. The process boundary "
                + "cannot be exercised without it, so this fails rather than skips.");
        }
        return candidates[0].FullName;
    }
}
