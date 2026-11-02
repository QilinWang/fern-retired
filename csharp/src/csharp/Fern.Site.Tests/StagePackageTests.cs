using System.Text;
using Xunit;

namespace Fern.Site.Tests;

/// <summary>
/// What Home issued, what the Site is willing to run, and what it publishes
/// back. Ported from tools/fern-control/site/attempt_test.go at 79af3ca5; each
/// test keeps the assertion of the Go test it is named for.
/// </summary>
public sealed class StagePackageTests
{
    [InlineData("none")]
    [InlineData("empty_file")]
    [InlineData("undeclared_file")]
    [InlineData("parent_escape")]
    [InlineData("absolute_path")]
    [InlineData("duplicate_path")]
    [InlineData("root_link")]
    [InlineData("ancestor_link")]
    [InlineData("directory_link")]
    [InlineData("file_link")]
    [InlineData("invalid_digest")]
    [InlineData("negative_byte_count")]
    [InlineData("backslash_path")]
    [InlineData("empty_path")]
    [InlineData("nul_path")]
    [InlineData("file_directory")]
    [InlineData("late_invalid_declaration")]
    [Theory(Timeout = 30000)]
    public async Task InstalledSourceVerificationUsesDeclaredRegularFiles(string defect)
    {
        await Task.Yield();
        using var home = new SiteFixture.TemporaryDirectory();
        var sourceRoot = home.Join("installed-source");
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(sourceRoot, "python")).FullName;
        var content = defect == "empty_file" ? Array.Empty<byte>() : "declared source bytes"u8.ToArray();
        var source = Path.Combine(sourceDirectory, "worker.py");
        File.WriteAllBytes(source, content);
        var declaration = new AttemptFile("python/worker.py", content.LongLength, SiteFixture.Sha256(content));
        var declared = new List<AttemptFile> { declaration };
        switch (defect)
        {
            case "undeclared_file": File.WriteAllText(Path.Combine(sourceRoot, "extra.txt"), "ignored"); break;
            case "parent_escape":
                File.WriteAllBytes(home.Join("outside.py"), content);
                declared[0] = declaration with { RelativePath = "../outside.py" };
                break;
            case "absolute_path": declared[0] = declaration with { RelativePath = source }; break;
            case "duplicate_path": declared.Add(declaration); break;
            case "invalid_digest": declared[0] = declaration with { ByteDigest = "sha256:invalid" }; break;
            case "negative_byte_count": declared[0] = declaration with { ByteCount = -1 }; break;
            case "backslash_path": declared[0] = declaration with { RelativePath = "python\\worker.py" }; break;
            case "empty_path": declared[0] = declaration with { RelativePath = "" }; break;
            case "nul_path": declared[0] = declaration with { RelativePath = "python/worker\0.py" }; break;
            case "file_directory":
                File.Delete(source);
                Directory.CreateDirectory(source);
                break;
            case "late_invalid_declaration":
                File.Delete(source);
                declared.Add(declaration with { RelativePath = "../outside.py" });
                break;
            case "root_link":
                Directory.Move(sourceRoot, home.Join("actual-source"));
                Directory.CreateSymbolicLink(sourceRoot, home.Join("actual-source"));
                break;
            case "ancestor_link":
                Directory.CreateSymbolicLink(home.Join("alias"), home.Path);
                sourceRoot = Path.Combine(home.Join("alias"), "installed-source");
                break;
            case "directory_link":
                Directory.Move(sourceDirectory, home.Join("actual-python"));
                Directory.CreateSymbolicLink(sourceDirectory, home.Join("actual-python"));
                break;
            case "file_link":
                File.Move(source, home.Join("actual-worker.py"));
                File.CreateSymbolicLink(source, home.Join("actual-worker.py"));
                break;
        }

        var verified = StageAttemptSite.VerifyInstalledSource(sourceRoot, declared);

        if (defect is "none" or "empty_file" or "undeclared_file")
            Assert.True(verified.IsOk, verified.RefusalDetail);
        else
        {
            Assert.False(verified.IsOk);
            Assert.Equal(SiteRetryPolicy.Never, verified.Failure.RetryPolicy);
            if (defect == "late_invalid_declaration")
                Assert.Contains("portable relative path", verified.Failure.Detail, StringComparison.Ordinal);
        }
    }

    static readonly byte[] BareRequest =
        Encoding.UTF8.GetBytes("""{"schema":"fern.stage_runner_request.v1"}""");

    [Fact]
    public void MissingPackagedStageRequestIsNeverRetried()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        SiteFixture.WriteInstructions(home.Path, SitePaths.StageRequestFile, BareRequest, writeRequest: false);

        var verified = StageAttemptSite.VerifyStagePackage(home.Path);

        Assert.False(verified.IsOk);
        // Identical bytes cannot become valid on a second attempt, so this is
        // never retried however much capacity the Site has.
        Assert.Equal(SiteRetryPolicy.Never, verified.Failure.RetryPolicy);
        Assert.Contains("cannot read packaged Stage file", verified.Failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void StageSiteRefusesWholeWorkerPackage()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        SiteFixture.WriteInstructions(
            home.Path,
            SitePaths.WorkerRequestFile,
            Encoding.UTF8.GetBytes("""{"schema":"fern.worker_request.v1"}"""),
            writeRequest: true);

        var verified = StageAttemptSite.VerifyStagePackage(home.Path);

        Assert.False(verified.IsOk);
        Assert.Equal(SiteRetryPolicy.Never, verified.Failure.RetryPolicy);
        Assert.Contains("whole-worker", verified.Failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DigestCheckHappensBeforeRunnerSpawn()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var outbound = home.Join("outbound");
        Directory.CreateDirectory(outbound);
        SiteFixture.WriteInstructions(outbound, SitePaths.StageRequestFile, BareRequest, writeRequest: true);
        SiteFixture.WriteRuntimeFile(outbound, SitePaths.StageRequestFile, Encoding.UTF8.GetBytes("changed"));
        var runtime = home.Join("runtime");

        var run = StageAttemptSite.RunStagePackage(outbound, runtime, python: string.Empty, TimeSpan.FromSeconds(1));

        Assert.False(run.IsOk);
        Assert.Contains("changed", run.Failure.Detail, StringComparison.Ordinal);
        // The point of the test: refusing early means no runtime exists at all.
        Assert.False(Directory.Exists(runtime));
    }

    [Fact]
    public void ExactVerifiedStagePackageReachesRunnerOnce()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var outbound = SiteFixture.RunnableRustStagePackage(home);
        var runtime = home.Join("runtime");

        var first = StageAttemptSite.RunStagePackage(
            outbound, runtime, python: string.Empty, TimeSpan.FromSeconds(20));
        Assert.True(first.IsOk, first.RefusalDetail);
        Assert.Equal(
            SiteFixture.Sha256(File.ReadAllBytes(Path.Combine(runtime, "report", "stage_runner_report.v1.json"))),
            first.Value.RunnerReportContentDigest);

        // A completed runtime replays by reopening the report; the runner does not run again.
        var replay = StageAttemptSite.RunStagePackage(
            outbound, runtime, python: string.Empty, TimeSpan.FromSeconds(20));
        Assert.True(replay.IsOk, replay.RefusalDetail);
        Assert.Equal("run\n", File.ReadAllText(Path.Combine(runtime, "logs", "runner-started")));

        // With the report gone the runtime is incomplete, and an incomplete
        // runtime is not retried in place: that would be a second execution of
        // the same Attempt.
        File.Delete(Path.Combine(runtime, SitePaths.StageReportFile.Replace('/', Path.DirectorySeparatorChar)));
        var incomplete = StageAttemptSite.RunStagePackage(
            outbound, runtime, python: string.Empty, TimeSpan.FromSeconds(20));
        Assert.False(incomplete.IsOk);
        Assert.Contains("new Attempt runtime", incomplete.Failure.Detail, StringComparison.Ordinal);
        Assert.Equal("run\n", File.ReadAllText(Path.Combine(runtime, "logs", "runner-started")));
    }

    [Fact]
    public void UnknownStageRunnerIsRefusedBeforeSpawn()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var outbound = home.Join("outbound");
        Directory.CreateDirectory(outbound);
        SiteFixture.WriteInstructions(
            outbound,
            SitePaths.StageRequestFile,
            Encoding.UTF8.GetBytes(
                """{"schema":"fern.stage_runner_request.v1","protocol_version":"fern.stage_runner.v1","invocation":{"implementation":{"runner":"invented","entrypoint":"invented"}}}"""),
            writeRequest: true);

        var run = StageAttemptSite.RunStagePackage(
            outbound, home.Join("runtime"), python: string.Empty, TimeSpan.FromSeconds(1));

        Assert.False(run.IsOk);
        Assert.Equal(SiteRetryPolicy.Never, run.Failure.RetryPolicy);
        Assert.Contains("unsupported Stage runner", run.Failure.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// CHG 2026-08-29 (v2): the Python source is INSTALLED on the Site, not
    /// packaged. The instructions name the root and every file with its digest;
    /// a root that is missing, or a file whose bytes differ from the declared
    /// digest, is refused before any interpreter starts.
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("tampered")]
    public void PythonStageRunnerRequiresTheInstalledSourceToMatchItsDeclaration(string defect)
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var outbound = home.Join("outbound");
        Directory.CreateDirectory(outbound);
        var declared = Encoding.UTF8.GetBytes("# installed Python source\n");
        var sourceRoot = home.Join("installed-source");
        if (defect == "tampered")
        {
            Directory.CreateDirectory(Path.Combine(sourceRoot, "python", "fern_train", "components"));
            File.WriteAllBytes(
                Path.Combine(sourceRoot, "python", "fern_train", "components", "core.py"),
                Encoding.UTF8.GetBytes("# not the declared bytes\n"));
        }
        SiteFixture.WriteInstructions(
            outbound,
            SitePaths.StageRequestFile,
            Encoding.UTF8.GetBytes(
                """{"schema":"fern.stage_runner_request.v1","protocol_version":"fern.stage_runner.v1","invocation":{"implementation":{"runner":"python_module","entrypoint":"fern_train.worker"}}}"""),
            writeRequest: true,
            sourceRoot: sourceRoot,
            sourceFiles: [new AttemptFile("python/fern_train/components/core.py", declared.LongLength, SiteFixture.Sha256(declared))]);

        var run = StageAttemptSite.RunStagePackage(
            outbound, home.Join("runtime"), python: "/bin/true", TimeSpan.FromSeconds(1));

        Assert.False(run.IsOk);
        Assert.Equal(SiteRetryPolicy.Never, run.Failure.RetryPolicy);
        Assert.Contains(defect == "missing" ? "is not a directory on this Site" : "changed: expected", run.Failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void PythonStageRunnerUsesTheInstalledSource()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var outbound = home.Join("outbound");
        Directory.CreateDirectory(outbound);
        var source = Encoding.UTF8.GetBytes("# installed Python source\n");
        var sourceRoot = home.Join("installed-source");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "python", "fern_train", "components"));
        File.WriteAllBytes(Path.Combine(sourceRoot, "python", "fern_train", "components", "core.py"), source);
        SiteFixture.WriteInstructions(
            outbound,
            SitePaths.StageRequestFile,
            Encoding.UTF8.GetBytes(
                """{"schema":"fern.stage_runner_request.v1","protocol_version":"fern.stage_runner.v1","invocation":{"implementation":{"runner":"python_module","entrypoint":"fern_train.worker"}}}"""),
            writeRequest: true,
            sourceRoot: sourceRoot,
            sourceFiles: [new AttemptFile("python/fern_train/components/core.py", source.LongLength, SiteFixture.Sha256(source))]);
        // The "interpreter" is a program that behaves like the runner would.
        var interpreterHome = home.Join("interpreter");
        Directory.CreateDirectory(interpreterHome);
        var interpreter = Path.Combine(interpreterHome, "python");
        File.WriteAllBytes(interpreter, SiteFixture.RunnerScript());
        SiteFixture.MakeExecutable(interpreter);
        var runtime = home.Join("runtime");

        var run = StageAttemptSite.RunStagePackage(outbound, runtime, interpreter, TimeSpan.FromSeconds(20));

        Assert.True(run.IsOk, run.RefusalDetail);
        // The runner's working directory and PYTHONPATH are the INSTALLED root,
        // which no package copied: nothing under runtime/implementation exists.
        // CHG 2026-09-26: src/ joined the import roots in 8552b7b6e (serie moved to src/serie/).
        Assert.Equal(
            sourceRoot + Path.PathSeparator + Path.Combine(sourceRoot, "python") + Path.PathSeparator + Path.Combine(sourceRoot, "src"),
            File.ReadAllText(Path.Combine(runtime, "logs", "python-path")));
        Assert.False(Directory.Exists(Path.Combine(runtime, "implementation", "python")));
    }

    [Fact]
    public void StageReturnPackageIsBoundAndReplayable()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var outbound = home.Join("outbound");
        Directory.CreateDirectory(outbound);
        SiteFixture.WriteInstructions(outbound, SitePaths.StageRequestFile, BareRequest, writeRequest: true);
        var issued = StageAttemptSite.VerifyStagePackage(outbound);
        Assert.True(issued.IsOk, issued.RefusalDetail);
        var runtime = CompletedRuntime(home);
        var destination = home.Join("return");
        var result = CompletedRuntimeResult(runtime);

        var first = StageAttemptSite.PackageStageReturn(runtime, destination, issued.Value.Instructions, result);
        Assert.True(first.IsOk, first.RefusalDetail);
        var second = StageAttemptSite.PackageStageReturn(runtime, destination, issued.Value.Instructions, result);
        Assert.True(second.IsOk, second.RefusalDetail);
        Assert.Equal(first.Value.Report.InstructionsDigest, second.Value.Report.InstructionsDigest);
        Assert.Equal(first.Value.Report.RunnerReportContentDigest, second.Value.Report.RunnerReportContentDigest);
        Assert.Equal(first.Value.Report.Files.Count, second.Value.Report.Files.Count);
        Assert.Equal(result.RunnerReportContentDigest, first.Value.Report.RunnerReportContentDigest);

        // The return answers ONE Attempt. Another Attempt's instructions hash
        // differently, so the same directory does not verify against them.
        var otherAttempt = issued.Value.Instructions with { AttemptId = "attempt-2" };
        var foreign = StageAttemptSite.VerifyStageReturnPackage(destination, otherAttempt);
        Assert.False(foreign.IsOk);
        Assert.Contains("does not answer", foreign.Failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void StageReturnPackageDetectsChangedAndExtraFiles()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var outbound = home.Join("outbound");
        Directory.CreateDirectory(outbound);
        SiteFixture.WriteInstructions(outbound, SitePaths.StageRequestFile, BareRequest, writeRequest: true);
        var issued = StageAttemptSite.VerifyStagePackage(outbound);
        Assert.True(issued.IsOk, issued.RefusalDetail);
        var runtime = CompletedRuntime(home);
        var destination = home.Join("return");
        var result = CompletedRuntimeResult(runtime);

        // A digest that does not bind the runner report is refused, and
        // refusing publishes nothing at all.
        var conflicting = result with
        {
            RunnerReportContentDigest = "sha256:" + new string('b', 64),
        };
        var refused = StageAttemptSite.PackageStageReturn(
            runtime, destination, issued.Value.Instructions, conflicting);
        Assert.False(refused.IsOk);
        Assert.Contains("changed", refused.Failure.Detail, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));

        var published = StageAttemptSite.PackageStageReturn(
            runtime, destination, issued.Value.Instructions, result);
        Assert.True(published.IsOk, published.RefusalDetail);

        var outputPath = Path.Combine(destination, "outputs", "result.bin");
        File.WriteAllText(outputPath, "changed");
        var changed = StageAttemptSite.VerifyStageReturnPackage(destination, issued.Value.Instructions);
        Assert.False(changed.IsOk);
        Assert.Contains("changed", changed.Failure.Detail, StringComparison.Ordinal);

        File.WriteAllText(outputPath, "scientific-result");
        File.WriteAllText(Path.Combine(destination, "not-declared.txt"), "extra");
        var extra = StageAttemptSite.VerifyStageReturnPackage(destination, issued.Value.Instructions);
        Assert.False(extra.IsOk);
        Assert.Contains("file set", extra.Failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void MaterializedStageRuntimeReplaysIdenticalBytes()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var outbound = home.Join("outbound");
        Directory.CreateDirectory(outbound);
        var request = Encoding.UTF8.GetBytes(
            """{"schema":"fern.stage_runner_request.v1","attempt":"first"}""");
        SiteFixture.WriteInstructions(outbound, SitePaths.StageRequestFile, request, writeRequest: true);
        var runtime = home.Join("runtime");

        var first = StageAttemptSite.MaterializeStagePackage(outbound, runtime);
        var second = StageAttemptSite.MaterializeStagePackage(outbound, runtime);

        Assert.True(first.IsOk, first.RefusalDetail);
        Assert.True(second.IsOk, second.RefusalDetail);
        Assert.Equal(runtime, first.Value.Root);
        Assert.Equal(runtime, second.Value.Root);
        Assert.Equal(
            request,
            File.ReadAllBytes(Path.Combine(
                runtime, SitePaths.StageRequestFile.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void MaterializedStageRuntimeRefusesDifferentReplay()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var firstOutbound = home.Join("first");
        Directory.CreateDirectory(firstOutbound);
        var firstRequest = Encoding.UTF8.GetBytes(
            """{"schema":"fern.stage_runner_request.v1","attempt":"first"}""");
        SiteFixture.WriteInstructions(firstOutbound, SitePaths.StageRequestFile, firstRequest, writeRequest: true);
        var runtime = home.Join("runtime");
        Assert.True(StageAttemptSite.MaterializeStagePackage(firstOutbound, runtime).IsOk);

        var secondOutbound = home.Join("second");
        Directory.CreateDirectory(secondOutbound);
        SiteFixture.WriteInstructions(
            secondOutbound,
            SitePaths.StageRequestFile,
            Encoding.UTF8.GetBytes("""{"schema":"fern.stage_runner_request.v1","attempt":"second"}"""),
            writeRequest: true);

        var refused = StageAttemptSite.MaterializeStagePackage(secondOutbound, runtime);

        Assert.False(refused.IsOk);
        Assert.Contains("different instructions", refused.Failure.Detail, StringComparison.Ordinal);
        // The refusal left the published runtime exactly as it was.
        Assert.Equal(
            firstRequest,
            File.ReadAllBytes(Path.Combine(
                runtime, SitePaths.StageRequestFile.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Theory]
    [InlineData("before-temporary")]
    [InlineData("after-temporary")]
    [InlineData("after-copy")]
    [InlineData("after-rename")]
    public void MaterializationCrashSnapshotsPublishOnlyCompleteRuntime(string crashPoint)
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var request = Encoding.UTF8.GetBytes(
            """{"schema":"fern.stage_runner_request.v1","attempt":"crash-boundary"}""");
        var outbound = home.Join("outbound");
        Directory.CreateDirectory(outbound);
        SiteFixture.WriteInstructions(outbound, SitePaths.StageRequestFile, request, writeRequest: true);
        var parent = home.Join("parent");
        Directory.CreateDirectory(parent);
        var runtime = Path.Combine(parent, "runtime");
        var temporary = Path.Combine(parent, ".runtime.tmp-crashed-process");

        switch (crashPoint)
        {
            case "before-temporary":
                break;
            case "after-temporary":
                Directory.CreateDirectory(temporary);
                SiteFixture.WriteRuntimeFile(
                    temporary, SitePaths.InstructionsFile, Encoding.UTF8.GetBytes("""{"incomplete":true}"""));
                break;
            case "after-copy":
                SiteFixture.WriteRuntimeFile(
                    temporary,
                    SitePaths.InstructionsFile,
                    File.ReadAllBytes(Path.Combine(outbound, SitePaths.InstructionsFile)));
                SiteFixture.WriteRuntimeFile(temporary, SitePaths.StageRequestFile, request);
                break;
            case "after-rename":
                Assert.True(StageAttemptSite.MaterializeStagePackage(outbound, runtime).IsOk);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(crashPoint), crashPoint, "no such crash point");
        }

        var materialized = StageAttemptSite.MaterializeStagePackage(outbound, runtime);

        Assert.True(materialized.IsOk, materialized.RefusalDetail);
        Assert.Equal(runtime, materialized.Value.Root);
        Assert.Equal(
            request,
            File.ReadAllBytes(Path.Combine(
                runtime, SitePaths.StageRequestFile.Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(StageAttemptSite.VerifyStagePackage(runtime).IsOk);
    }

    [Fact]
    public void StageTransportRootIsCreatedBeforePackagePublication()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var outbound = home.Join("outbound");
        Directory.CreateDirectory(outbound);
        SiteFixture.WriteInstructions(outbound, SitePaths.StageRequestFile, BareRequest, writeRequest: true);
        var attemptRoot = home.Join("remote-attempts", "attempt-1");
        Assert.False(Directory.Exists(attemptRoot));

        var materialized = StageAttemptSite.MaterializeStagePackage(outbound, Path.Combine(attemptRoot, "runtime"));

        Assert.True(materialized.IsOk, materialized.RefusalDetail);
        Assert.True(Directory.Exists(attemptRoot));
        Assert.Null(new DirectoryInfo(attemptRoot).LinkTarget);
    }

    [Fact]
    public void StageTransportRootRefusesSymlinkLeaf()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var outbound = home.Join("outbound");
        Directory.CreateDirectory(outbound);
        SiteFixture.WriteInstructions(outbound, SitePaths.StageRequestFile, BareRequest, writeRequest: true);
        var realRoot = home.Join("real");
        var aliasRoot = home.Join("alias");
        Directory.CreateDirectory(realRoot);
        Directory.CreateSymbolicLink(aliasRoot, realRoot);

        var refused = StageAttemptSite.MaterializeStagePackage(outbound, Path.Combine(aliasRoot, "runtime"));

        Assert.False(refused.IsOk);
        Assert.Contains("not a regular non-symlink directory", refused.Failure.Detail, StringComparison.Ordinal);
        // Writing THROUGH the symlink is the failure this guards against, so
        // the directory it points at must still be empty.
        Assert.Empty(Directory.GetFileSystemEntries(realRoot));
    }

    // ---- fixtures shared by the return-package tests ----

    /// <summary>A runtime as a finished run leaves it: a runner report and one output.</summary>
    static string CompletedRuntime(SiteFixture.TemporaryDirectory home)
    {
        var runtime = home.Join("runtime");
        Directory.CreateDirectory(runtime);
        SiteFixture.WriteRuntimeFile(
            runtime,
            SitePaths.StageReportFile,
            Encoding.UTF8.GetBytes("""{"schema":"fern.stage_runner_report.v1","stage_attempt_id":"attempt-1"}"""));
        SiteFixture.WriteRuntimeFile(runtime, "outputs/result.bin", Encoding.UTF8.GetBytes("scientific-result"));
        return runtime;
    }

    static StageExecutionResult CompletedRuntimeResult(string runtime) =>
        new(SiteFixture.Sha256(File.ReadAllBytes(Path.Combine(
            runtime, SitePaths.StageReportFile.Replace('/', Path.DirectorySeparatorChar)))));
}
