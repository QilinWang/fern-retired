using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Fern.Site.Tests;

/// <summary>
/// Program 18, Site-C: a Site runs one phase of a cell on its resident Python
/// worker and returns the report and its own reading of the outputs, never the
/// bytes. Driven with the real worker and the CPU canary handler
/// (`oracle.controlled_linear.generate`), against this repository as the
/// installed source root — a missing interpreter FAILS rather than skips.
/// </summary>
public sealed class CellPhaseSiteTests
{
    [Fact]
    public void WorkerThatExitsBeforePublishingIdentityRefusesWithoutWaitingForTheImportDeadline()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var workerRoot = home.Join("worker");
        var sourceRoot = home.Join("source");
        Directory.CreateDirectory(sourceRoot);
        var elapsed = Stopwatch.StartNew();

        var started = ResidentPythonWorker.EnsureRunning(
            workerRoot,
            "/bin/false",
            sourceRoot,
            TimeSpan.FromSeconds(30),
            observeStartup: null);

        Assert.False(started.IsOk);
        Assert.Contains("exited before writing worker.json", started.Failure.Detail, StringComparison.Ordinal);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(5),
            $"dead worker startup consumed {elapsed.Elapsed} of the import deadline");
    }

    [Fact]
    public void ImportDeadlineCoversAMeasuredChronosTorchImport()
    {
        Assert.True(ResidentPythonWorker.ImportDeadline >= TimeSpan.FromMinutes(2));
        Assert.True(ResidentPythonWorker.ImportDeadline < TimeSpan.FromMinutes(10));
        var source = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "csharp", "Fern.Site", "CellPhaseSite.cs"));
        Assert.Contains("ResidentPythonWorker.ImportDeadline", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeSpan.FromSeconds(30)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RealResidentWorkerPublishesStartingThenReadyObservations()
    {
        var python = TestPython();
        var repository = RepositoryRoot();
        using var home = new SiteFixture.TemporaryDirectory();
        var workerRoot = home.Join("worker-observed");
        var observations = new List<ResidentPythonWorker.WorkerStartupObservation>();
        ResidentPythonWorker.WorkerIdentity? identity = null;
        try
        {
            var started = ResidentPythonWorker.EnsureRunning(
                workerRoot,
                python,
                repository,
                TimeSpan.FromSeconds(90),
                observations.Add);
            Assert.True(started.IsOk, started.RefusalDetail);
            identity = started.Value;

            var starting = Assert.Single(observations, value => value.State == "starting");
            var ready = Assert.Single(observations, value => value.State == "ready");
            Assert.Equal(starting.Identity.LaunchId, ready.Identity.LaunchId);
            Assert.Equal(starting.Identity.Pid, ready.Identity.Pid);
            Assert.True(ready.ElapsedMs >= starting.ElapsedMs);
        }
        finally
        {
            ResidentPythonWorker.RequestStop(workerRoot);
            StopWorker(identity);
        }
    }

    [Fact]
    public void StartingWorkerThatMissesAdmissionDeadlineReturnsTypedAliveDeadline()
    {
        var python = TestPython();
        var repository = RepositoryRoot();
        using var home = new SiteFixture.TemporaryDirectory();
        var workerRoot = home.Join("worker-deadline");
        ResidentPythonWorker.WorkerIdentity? identity = null;
        byte[]? readyIdentity = null;
        try
        {
            var started = ResidentPythonWorker.EnsureRunning(
                workerRoot,
                python,
                repository,
                TimeSpan.FromSeconds(90),
                observeStartup: null);
            Assert.True(started.IsOk, started.RefusalDetail);
            identity = started.Value;
            var workerPath = Path.Combine(workerRoot, ResidentPythonWorker.WorkerFile);
            readyIdentity = File.ReadAllBytes(workerPath);
            var starting = JsonNode.Parse(readyIdentity)!.AsObject();
            starting["state"] = "starting";
            File.WriteAllText(workerPath, starting.ToJsonString());

            var observations = new List<ResidentPythonWorker.WorkerStartupObservation>();
            var timedOut = ResidentPythonWorker.EnsureRunning(
                workerRoot,
                python,
                repository,
                TimeSpan.FromMilliseconds(250),
                observations.Add);

            Assert.False(timedOut.IsOk);
            Assert.Equal(SiteFailureCategory.WorkerAdmissionDeadline, timedOut.Failure.Category);
            Assert.Contains("process_liveness=alive", timedOut.Failure.Detail, StringComparison.Ordinal);
            Assert.Equal(identity.ProcessStartTicks, ResidentPythonWorker.ReadProcessStartTicks(identity.Pid));
            Assert.Single(observations, value => value.State == "starting");
        }
        finally
        {
            if (readyIdentity is not null)
            {
                File.WriteAllBytes(Path.Combine(workerRoot, ResidentPythonWorker.WorkerFile), readyIdentity);
            }
            ResidentPythonWorker.RequestStop(workerRoot);
            StopWorker(identity);
        }
    }

    static void StopWorker(ResidentPythonWorker.WorkerIdentity? identity)
    {
        if (identity is null)
        {
            return;
        }
        for (var waited = 0;
             waited < 100 && ResidentPythonWorker.ReadProcessStartTicks(identity.Pid) == identity.ProcessStartTicks;
             waited++)
        {
            Thread.Sleep(50);
        }
        if (ResidentPythonWorker.ReadProcessStartTicks(identity.Pid) == identity.ProcessStartTicks)
        {
            _ = ResidentPythonWorker.Kill(identity);
        }
    }

    [Fact]
    public void OneSiteProcessRunsTwoHomeMintedPhasesUnderOneCellAndOneResidentWorker()
    {
        var python = TestPython();
        var repository = RepositoryRoot();
        using var home = new SiteFixture.TemporaryDirectory();
        var siteRoot = home.Join("site");
        var workerRoot = home.Join("worker");
        var sourceDigest = new string('a', 64);
        var sourceRoot = Path.Combine(siteRoot, "implementation", sourceDigest);
        CopyTree(Path.Combine(repository, "python", "fern_stage_runtime"), Path.Combine(sourceRoot, "python", "fern_stage_runtime"));
        File.WriteAllText(Path.Combine(sourceRoot, ".fern-source-archive.sha256"), sourceDigest);

        var firstPackage = home.Join("phase-0");
        var secondPackage = home.Join("phase-1");
        Directory.CreateDirectory(firstPackage);
        Directory.CreateDirectory(secondPackage);
        WritePhasePackage(firstPackage, sourceRoot, OracleRequest("historical-stage-0", link: false),
            "declared-cell", 0, "historical-stage-0", link: false, storeSha: null);
        WritePhasePackage(secondPackage, sourceRoot, OracleRequest("historical-stage-1", link: true),
            "declared-cell", 1, "historical-stage-1", link: true, storeSha: null);

        var start = new ProcessStartInfo(SiteCliTests.SiteExecutable())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            "serve", "--stdio", "--work-root", siteRoot,
            "--worker-root", workerRoot, "--python", python, "--timeout-seconds", "60",
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("the operating system started no fern-site process");
        try
        {
            var hello = SessionExchange(process, new JsonObject
            {
                ["schema"] = CellPhaseSite.SessionSchema,
                ["operation"] = "hello",
                ["request_id"] = "hello",
            });
            Assert.Equal("hello", hello.Header["operation"]!.GetValue<string>());

            JsonObject PermitHeader(string operation, string requestId) => new()
            {
                ["schema"] = CellPhaseSite.SessionSchema,
                ["operation"] = operation,
                ["request_id"] = requestId,
                ["cell_attempt_id"] = "cell-attempt-1",
                ["remote_execution_id"] = "remote-execution-1",
                ["parent_plan_epoch"] = 7,
                ["stage_plan_instance_id"] = "instance-1",
                ["site_id"] = "site-a",
                ["presented_capability_digest"] = "sha256:" + new string('b', 64),
                ["cell_request_digest"] = "sha256:" + new string('c', 64),
                ["cell_submission_digest"] = "sha256:" + new string('d', 64),
            };
            var begun = SessionExchange(process, PermitHeader("begin_cell", "begin"));
            Assert.Equal("cell_begun", begun.Header["operation"]!.GetValue<string>());

            var first = RunSessionPhase(process, PermitHeader, firstPackage, sourceRoot, ordinal: 0,
                stageId: "generate-0", historicalAttemptId: "historical-stage-0");
            var second = RunSessionPhase(process, PermitHeader, secondPackage, sourceRoot, ordinal: 1,
                stageId: "generate-1", historicalAttemptId: "historical-stage-1");

            Assert.Equal(first.WorkerPid, second.WorkerPid);
            Assert.Equal("completed", first.Event["status"]!.GetValue<string>());
            Assert.Equal("completed", second.Event["status"]!.GetValue<string>());
            Assert.Equal("fern.site_phase_receipt.v1",
                JsonNode.Parse(first.Event["receipt_json"]!.GetValue<string>())!["schema"]!.GetValue<string>());
            var firstReceipt = JsonNode.Parse(first.Event["receipt_json"]!.GetValue<string>())!.AsObject();
            var embeddedReport = firstReceipt["runner_report_json"]!.GetValue<string>();
            Assert.Equal("fern.stage_runner_report.v1", JsonNode.Parse(embeddedReport)!["schema"]!.GetValue<string>());
            Assert.Equal(
                firstReceipt["runner_report_sha256"]!.GetValue<string>(),
                SiteFixture.Sha256(Encoding.UTF8.GetBytes(embeddedReport)));
            Assert.Equal("horizon_end_response", Assert.Single(first.Event["outputs"]!.AsArray())!["port"]!.GetValue<string>());
            Assert.Equal("home", Assert.Single(second.Event["outputs"]!.AsArray())!["storage_disposition"]!.GetValue<string>());

            var refused = SessionExchange(process, new JsonObject
            {
                ["schema"] = CellPhaseSite.SessionSchema,
                ["operation"] = "invented_after_cell",
                ["request_id"] = "bad",
            });
            Assert.Equal("refused", refused.Header["operation"]!.GetValue<string>());
            Assert.False(process.HasExited, "a recoverable protocol refusal killed the resident Site session");
            var stillHere = SessionExchange(process, new JsonObject
            {
                ["schema"] = CellPhaseSite.SessionSchema,
                ["operation"] = "hello",
                ["request_id"] = "after-refusal",
            });
            Assert.Equal("hello", stillHere.Header["operation"]!.GetValue<string>());
        }
        finally
        {
            process.StandardInput.Close();
            Assert.True(process.WaitForExit(10_000), "fern-site did not end after Home closed the session");
            var implementationWorkerRoot = Path.Combine(workerRoot, sourceDigest);
            var identity = ResidentPythonWorker.ReadIdentity(implementationWorkerRoot);
            ResidentPythonWorker.RequestStop(implementationWorkerRoot);
            if (identity.IsOk)
            {
                for (var waited = 0;
                     waited < 100 && ResidentPythonWorker.ReadProcessStartTicks(identity.Value.Pid) == identity.Value.ProcessStartTicks;
                     waited++) Thread.Sleep(100);
                if (ResidentPythonWorker.ReadProcessStartTicks(identity.Value.Pid) == identity.Value.ProcessStartTicks)
                {
                    _ = ResidentPythonWorker.Kill(identity.Value);
                }
            }
        }
    }

    /// <summary>
    /// CHG 2026-09-11: a failed phase's record carries the worker's own reason. The
    /// Site used to rebuild the failure receipt with only "failed" in it, and the one
    /// sentence explaining a refusal lived in a worker folder nobody opened; the
    /// PointMaze refusals of 2026-09-11 hid that way for two days.
    /// </summary>
    [Fact]
    public void AFailedPhaseRecordCarriesTheWorkersReason()
    {
        var python = TestPython();
        var repository = RepositoryRoot();
        using var home = new SiteFixture.TemporaryDirectory();
        var siteRoot = home.Join("site");
        var workerRoot = home.Join("worker");
        var sourceDigest = new string('e', 64);
        var sourceRoot = Path.Combine(siteRoot, "implementation", sourceDigest);
        CopyTree(Path.Combine(repository, "python", "fern_stage_runtime"), Path.Combine(sourceRoot, "python", "fern_stage_runtime"));
        File.WriteAllText(Path.Combine(sourceRoot, ".fern-source-archive.sha256"), sourceDigest);
        var package = home.Join("phase-0");
        Directory.CreateDirectory(package);
        WritePhasePackage(package, sourceRoot, FailingOracleRequest("historical-stage-0"),
            "declared-cell", 0, "historical-stage-0", link: false, storeSha: null);

        var start = new ProcessStartInfo(SiteCliTests.SiteExecutable())
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            "serve", "--stdio", "--work-root", siteRoot, "--worker-root", workerRoot, "--python", python, "--timeout-seconds", "60",
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("the operating system started no fern-site process");
        try
        {
            SessionExchange(process, new JsonObject { ["schema"] = CellPhaseSite.SessionSchema, ["operation"] = "hello", ["request_id"] = "hello" });
            JsonObject PermitHeader(string operation, string requestId) => new()
            {
                ["schema"] = CellPhaseSite.SessionSchema, ["operation"] = operation, ["request_id"] = requestId,
                ["cell_attempt_id"] = "cell-attempt-9", ["remote_execution_id"] = "remote-execution-9",
                ["parent_plan_epoch"] = 7, ["stage_plan_instance_id"] = "instance-9", ["site_id"] = "site-a",
                ["presented_capability_digest"] = "sha256:" + new string('b', 64),
                ["cell_request_digest"] = "sha256:" + new string('c', 64),
                ["cell_submission_digest"] = "sha256:" + new string('d', 64),
            };
            SessionExchange(process, PermitHeader("begin_cell", "begin"));
            var failed = RunSessionPhase(process, PermitHeader, package, sourceRoot, ordinal: 0,
                stageId: "generate-0", historicalAttemptId: "historical-stage-0");

            Assert.Equal("failed", failed.Event["status"]!.GetValue<string>());
            var record = JsonNode.Parse(failed.Event["receipt_json"]!.GetValue<string>())!.AsObject();
            Assert.Equal("fern.site_phase_failure.v2", record["schema"]!.GetValue<string>());
            var outcome = record["outcome"]!.AsObject();
            Assert.NotEqual("success", outcome["kind"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(outcome["message"]?.GetValue<string>()), "the worker's reason did not survive into the failure record");
            Assert.False(outcome["message_truncated"]!.GetValue<bool>());
            var reportRecord = record["report"]!.AsObject();
            var reportFile = Path.Combine(siteRoot, "cells", reportRecord["relative_path"]!.GetValue<string>());
            Assert.True(File.Exists(reportFile), "the failure record must point at a report that exists");
            Assert.Equal(SiteFixture.Sha256(File.ReadAllBytes(reportFile)).Replace("sha256:", string.Empty), reportRecord["sha256"]!.GetValue<string>());
            Assert.Equal(SiteFixture.Sha256(Encoding.UTF8.GetBytes(failed.Event["receipt_json"]!.GetValue<string>())).Replace("sha256:", string.Empty),
                failed.Event["receipt_sha256"]!.GetValue<string>());  // the record's bytes are what the ledger hashes
        }
        finally
        {
            process.StandardInput.Close();
            Assert.True(process.WaitForExit(10_000), "fern-site did not end after Home closed the session");
            var implementationWorkerRoot = Path.Combine(workerRoot, sourceDigest);
            var identity = ResidentPythonWorker.ReadIdentity(implementationWorkerRoot);
            ResidentPythonWorker.RequestStop(implementationWorkerRoot);
            if (identity.IsOk)
            {
                for (var waited = 0;
                     waited < 100 && ResidentPythonWorker.ReadProcessStartTicks(identity.Value.Pid) == identity.Value.ProcessStartTicks;
                     waited++) Thread.Sleep(100);
                if (ResidentPythonWorker.ReadProcessStartTicks(identity.Value.Pid) == identity.Value.ProcessStartTicks)
                {
                    _ = ResidentPythonWorker.Kill(identity.Value);
                }
            }
        }
    }

    [Fact]
    public void DroppedSiteResponseReplaysSameAttemptReceiptWithoutRunningScienceAgain()
    {
        var python = TestPython();
        var repository = RepositoryRoot();
        using var home = new SiteFixture.TemporaryDirectory();
        var siteRoot = home.Join("site");
        var workerRoot = home.Join("worker");
        var sourceDigest = new string('a', 64);
        var sourceRoot = Path.Combine(siteRoot, "implementation", sourceDigest);
        CopyTree(Path.Combine(repository, "python", "fern_stage_runtime"), Path.Combine(sourceRoot, "python", "fern_stage_runtime"));
        File.WriteAllText(Path.Combine(sourceRoot, ".fern-source-archive.sha256"), sourceDigest);

        var package = home.Join("phase-0");
        Directory.CreateDirectory(package);
        WritePhasePackage(package, sourceRoot, OracleRequest("historical-stage-0", link: false),
            "declared-cell", 0, "historical-stage-0", link: false, storeSha: null);

        var start = new ProcessStartInfo(SiteCliTests.SiteExecutable())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            "serve", "--stdio", "--work-root", siteRoot,
            "--worker-root", workerRoot, "--python", python, "--timeout-seconds", "60",
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("the operating system started no fern-site process");
        try
        {
            JsonObject PermitHeader(string operation, string requestId) => new()
            {
                ["schema"] = CellPhaseSite.SessionSchema,
                ["operation"] = operation,
                ["request_id"] = requestId,
                ["cell_attempt_id"] = "cell-attempt-replay",
                ["remote_execution_id"] = "remote-execution-replay",
                ["parent_plan_epoch"] = 7,
                ["stage_plan_instance_id"] = "instance-1",
                ["site_id"] = "site-a",
                ["presented_capability_digest"] = "sha256:" + new string('b', 64),
                ["cell_request_digest"] = "sha256:" + new string('c', 64),
                ["cell_submission_digest"] = "sha256:" + new string('d', 64),
            };
            var begun = SessionExchange(process, PermitHeader("begin_cell", "begin"));
            Assert.Equal("cell_begun", begun.Header["operation"]!.GetValue<string>());

            var changedExecution = PermitHeader("begin_cell", "changed-execution");
            changedExecution["remote_execution_id"] = "remote-execution-replacement";
            var changedExecutionRefusal = SessionExchange(process, changedExecution);
            Assert.Equal("refused", changedExecutionRefusal.Header["operation"]!.GetValue<string>());
            Assert.Contains(
                "already names another permit",
                changedExecutionRefusal.Header["detail"]!.GetValue<string>(),
                StringComparison.Ordinal);

            // The remote Site completed the phase, but the outer transport lost
            // its frames. Deliberately consume and discard them here rather
            // than treating a received frame as a caller-visible completion.
            SubmitSessionPhase(process, PermitHeader, package, sourceRoot, ordinal: 0,
                stageId: "generate-0", historicalAttemptId: "historical-stage-0", requestId: "phase-first");
            DiscardSessionPhaseResult(process);

            var implementationWorkerRoot = Path.Combine(workerRoot, sourceDigest);
            var workerReceiptPath = ResidentPythonWorker.PhaseReceiptPath(
                implementationWorkerRoot, "cell-attempt-replay", 0, "oracle.controlled_linear.generate");
            Assert.True(File.Exists(workerReceiptPath), "the resident worker did not publish a phase receipt before the Site response was dropped");
            var firstWorkerReceipt = File.ReadAllBytes(workerReceiptPath);
            using var workerReceipt = JsonDocument.Parse(firstWorkerReceipt);
            var recordedSeconds = workerReceipt.RootElement.GetProperty("seconds").GetDouble();
            var requestsBeforeReplay = Directory.EnumerateFiles(Path.GetDirectoryName(workerReceiptPath)!, "request-*.json").Count();
            Assert.Equal(1, requestsBeforeReplay);

            var replayed = RunSessionPhase(process, PermitHeader, package, sourceRoot, ordinal: 0,
                stageId: "generate-0", historicalAttemptId: "historical-stage-0", requestId: "phase-replay");
            var replayedReceipt = JsonNode.Parse(replayed.Event["receipt_json"]!.GetValue<string>())!.AsObject();
            Assert.Equal(recordedSeconds, replayedReceipt["phase"]!["seconds"]!.GetValue<double>());
            Assert.Equal(firstWorkerReceipt, File.ReadAllBytes(workerReceiptPath));
            Assert.Equal(requestsBeforeReplay,
                Directory.EnumerateFiles(Path.GetDirectoryName(workerReceiptPath)!, "request-*.json").Count());

            // Transport correlation can change, immutable stage meaning cannot.
            // The same cell/phase therefore rejects the altered submission
            // without consuming or replacing the completed worker receipt.
            SubmitSessionPhase(process, PermitHeader, package, sourceRoot, ordinal: 0,
                stageId: "generate-0", historicalAttemptId: "different-historical-stage-0", requestId: "phase-tampered");
            var refusal = CellPhaseSite.ReadSessionEnvelope(process.StandardOutput.BaseStream);
            Assert.True(refusal.IsOk, refusal.RefusalDetail);
            Assert.Equal("refused", refusal.Value!.Header["operation"]!.GetValue<string>());
            Assert.Contains("different immutable bytes", refusal.Value.Header["detail"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal(firstWorkerReceipt, File.ReadAllBytes(workerReceiptPath));
        }
        finally
        {
            process.StandardInput.Close();
            Assert.True(process.WaitForExit(10_000), "fern-site did not end after Home closed the session");
            var implementationWorkerRoot = Path.Combine(workerRoot, sourceDigest);
            var identity = ResidentPythonWorker.ReadIdentity(implementationWorkerRoot);
            ResidentPythonWorker.RequestStop(implementationWorkerRoot);
            if (identity.IsOk)
            {
                for (var waited = 0;
                     waited < 100 && ResidentPythonWorker.ReadProcessStartTicks(identity.Value.Pid) == identity.Value.ProcessStartTicks;
                     waited++) Thread.Sleep(100);
                if (ResidentPythonWorker.ReadProcessStartTicks(identity.Value.Pid) == identity.Value.ProcessStartTicks)
                {
                    _ = ResidentPythonWorker.Kill(identity.Value);
                }
            }
        }
    }

    sealed record SessionPhaseResult(long WorkerPid, JsonObject Event);

    static SessionPhaseResult RunSessionPhase(
        Process process,
        Func<string, string, JsonObject> permitHeader,
        string package,
        string sourceRoot,
        long ordinal,
        string stageId,
        string historicalAttemptId,
        string? requestId = null)
    {
        SubmitSessionPhase(
            process, permitHeader, package, sourceRoot, ordinal, stageId, historicalAttemptId,
            requestId ?? "phase-" + ordinal.ToString());
        JsonObject? phaseEvent = null;
        while (true)
        {
            var response = CellPhaseSite.ReadSessionEnvelope(process.StandardOutput.BaseStream);
            Assert.True(response.IsOk, response.RefusalDetail);
            var envelope = Assert.IsType<CellPhaseSite.SessionEnvelope>(response.Value);
            var operation = envelope.Header["operation"]!.GetValue<string>();
            if (operation == "phase_event") phaseEvent = envelope.Header;
            if (operation == "phase_complete")
            {
                Assert.NotNull(phaseEvent);
                return new SessionPhaseResult(envelope.Header["worker_pid"]!.GetValue<long>(), phaseEvent);
            }
            Assert.Contains(operation, new[] { "worker_startup", "liveness", "phase_event" });
        }
    }

    static void SubmitSessionPhase(
        Process process,
        Func<string, string, JsonObject> permitHeader,
        string package,
        string sourceRoot,
        long ordinal,
        string stageId,
        string historicalAttemptId,
        string requestId)
    {
        var files = new JsonArray();
        foreach (var path in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(path);
            var digest = SiteFixture.Sha256(bytes)["sha256:".Length..];
            var relative = $"packages/{ordinal}/" + Path.GetRelativePath(package, path).Replace('\\', '/');
            var stored = SessionExchange(process, new JsonObject
            {
                ["schema"] = CellPhaseSite.SessionSchema,
                ["operation"] = "put_content",
                ["request_id"] = $"put-{ordinal}-{files.Count}",
                ["sha256"] = digest,
            }, bytes);
            Assert.Equal("content_stored", stored.Header["operation"]!.GetValue<string>());
            files.Add(new JsonObject { ["relative_path"] = relative, ["sha256"] = digest });
        }
        var run = permitHeader("run_phase", requestId);
        run["ordinal"] = ordinal;
        run["stage_id"] = stageId;
        run["handler"] = "oracle.controlled_linear.generate";
        run["historical_stage_attempt_id"] = historicalAttemptId;
        run["package_relative_path"] = $"packages/{ordinal}";
        run["source_root"] = sourceRoot;
        run["files"] = files;
        run["outputs"] = new JsonArray(new JsonObject
        {
            ["phase"] = ordinal,
            ["port"] = "horizon_end_response",
            ["storage_disposition"] = "home",
        });
        CellPhaseSite.WriteSessionEnvelope(process.StandardInput.BaseStream, run);
    }

    static void DiscardSessionPhaseResult(Process process)
    {
        while (true)
        {
            var response = CellPhaseSite.ReadSessionEnvelope(process.StandardOutput.BaseStream);
            Assert.True(response.IsOk, response.RefusalDetail);
            var envelope = Assert.IsType<CellPhaseSite.SessionEnvelope>(response.Value);
            var operation = envelope.Header["operation"]!.GetValue<string>();
            if (operation == "phase_complete") return;
            Assert.Contains(operation, new[] { "worker_startup", "liveness", "phase_event" });
        }
    }

    static CellPhaseSite.SessionEnvelope SessionExchange(Process process, JsonObject header, byte[]? body = null)
    {
        CellPhaseSite.WriteSessionEnvelope(process.StandardInput.BaseStream, header, body ?? []);
        var response = CellPhaseSite.ReadSessionEnvelope(process.StandardOutput.BaseStream);
        Assert.True(response.IsOk, response.RefusalDetail);
        return Assert.IsType<CellPhaseSite.SessionEnvelope>(response.Value);
    }

    static void CopyTree(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("nested/port")]
    public void StoreInputPortMustBeOneContainedPathComponent(string port)
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var runtime = home.Join("runtime");
        Directory.CreateDirectory(runtime);
        var phaseDirectory = Path.Combine(runtime, "phase");
        Directory.CreateDirectory(phaseDirectory);
        File.WriteAllText(
            Path.Combine(phaseDirectory, "cell_phase.v1.json"),
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["schema"] = SitePaths.PhaseSchema,
                ["cell_id"] = "cell-a",
                ["ordinal"] = 0,
                ["handler"] = "oracle.controlled_linear.generate",
                ["inputs_from_phases"] = new Dictionary<string, object>(),
                ["inputs_from_store"] = new Dictionary<string, object>
                {
                    [port] = new Dictionary<string, object>
                    {
                        ["sha256"] = new string('a', 64),
                        ["file_name"] = "bank.safetensors",
                    },
                },
            }));

        var result = CellPhaseSite.ReadPhaseInstructions(runtime);

        Assert.False(result.IsOk);
        Assert.Contains("input port", result.RefusalDetail, StringComparison.Ordinal);
        Assert.Contains("one path component", result.RefusalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoPhasesOfOneCellRunOnOneWorkerAndReturnReceiptsWithoutOutputBytes()
    {
        var python = TestPython();
        var repository = RepositoryRoot();
        using var home = new SiteFixture.TemporaryDirectory();
        var workerRoot = home.Join("workers", "env", "impl");
        var storeRoot = home.Join("store");
        var cellWorkspace = home.Join("remote-cells", "cell-a");
        Directory.CreateDirectory(cellWorkspace);
        Directory.CreateDirectory(storeRoot);
        // Space-F: an external input the Site holds in its store by digest.
        var bank = Encoding.UTF8.GetBytes("a bank the phase never reads, placed by digest");
        var bankSha = SiteFixture.Sha256(bank)["sha256:".Length..];
        File.WriteAllBytes(Path.Combine(storeRoot, bankSha), bank);
        try
        {
            var first = RunPhase(home, repository, python, workerRoot, storeRoot, cellWorkspace, "cell-a", ordinal: 0, attemptId: "attempt-p0", link: false, storeSha: bankSha);
            var second = RunPhase(home, repository, python, workerRoot, storeRoot, cellWorkspace, "cell-a", ordinal: 1, attemptId: "attempt-p1", link: true, storeSha: null);
            Assert.True(File.Exists(Path.Combine(first.Runtime, "inputs", "generated_dataset", "bank.safetensors")), "the store input was not linked into inputs/");
            Assert.Equal(bank, File.ReadAllBytes(Path.Combine(first.Runtime, "inputs", "generated_dataset", "bank.safetensors")));

            // A store entry whose bytes do not hash to its name refuses by port, before anything runs.
            File.WriteAllBytes(Path.Combine(storeRoot, new string('b', 64)), bank);
            var badPackage = home.Join("outbound-bad");
            Directory.CreateDirectory(badPackage);
            WritePhasePackage(badPackage, repository, OracleRequest("attempt-bad", link: false), "cell-a", 2, "attempt-bad", link: false, storeSha: new string('b', 64));
            var refused = CellPhaseSite.RunCellPhase(
                badPackage, Path.Combine(cellWorkspace, "attempt-bad"), workerRoot, storeRoot, python,
                TimeSpan.FromSeconds(60), progress: null, workerStartup: null, authorityCellId: null);
            Assert.False(refused.IsOk);
            Assert.Contains("input port generated_dataset", refused.RefusalDetail, StringComparison.Ordinal);

            // One worker served both phases.
            var identity = ResidentPythonWorker.ReadIdentity(workerRoot);
            Assert.True(identity.IsOk, identity.RefusalDetail);
            Assert.True(ResidentPythonWorker.IsAlive(identity.Value.Pid));
            Assert.Equal(first.WorkerPid, second.WorkerPid);
            Assert.Equal(identity.Value.Pid, first.WorkerPid);

            // The return carries the report and the Site receipt, and no output bytes.
            foreach (var returned in new[] { first.Return, second.Return })
            {
                var names = returned.Report.Files.Select(file => file.RelativePath).ToList();
                Assert.Contains(SitePaths.StageReportFile, names);
                Assert.Contains(SitePaths.SitePhaseReceiptFile, names);
                Assert.DoesNotContain(names, name => name.StartsWith("outputs/", StringComparison.Ordinal));
                Assert.False(Directory.Exists(Path.Combine(returned.Root, "outputs")));
            }

            // The Site's reading agrees with the runner's report, on the Site.
            var receipt = CellPhaseSite.ReadSiteReceipt(first.Runtime);
            Assert.True(receipt.IsOk, receipt.RefusalDetail);
            var reading = Assert.Single(receipt.Value);
            Assert.Equal("horizon_end_response", reading.Key);
            using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(first.Runtime, SitePaths.StageReportFile)));
            var runnerReported = report.RootElement.GetProperty("outcome").GetProperty("result").GetProperty("outputs")
                .GetProperty("horizon_end_response");
            Assert.Equal(runnerReported.GetProperty("byte_digest").GetString(), reading.Value.ByteDigest);
            Assert.Equal(runnerReported.GetProperty("byte_count").GetInt64(), reading.Value.ByteCount);
            Assert.True(File.Exists(Path.Combine(first.Runtime, reading.Value.RelativePath)), "the output stayed on the Site");
            // Registry-D: the receipt carries the phase's cost from the worker's own receipt.
            using var siteReceipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(first.Runtime, SitePaths.SitePhaseReceiptFile)));
            Assert.True(siteReceipt.RootElement.GetProperty("phase").GetProperty("seconds").GetDouble() >= 0);
            Assert.Equal(first.WorkerPid, siteReceipt.RootElement.GetProperty("worker").GetProperty("pid").GetInt64());

            // Phase 1 declared its input from phase 0's output: the worker linked it in by digest.
            var linked = Path.Combine(second.Runtime, "inputs", "training_worker_request", "horizon_end_response.json");
            Assert.True(File.Exists(linked), "phase 1 did not receive phase 0's output through the worker");
        }
        finally
        {
            var identity = ResidentPythonWorker.ReadIdentity(workerRoot);
            ResidentPythonWorker.RequestStop(workerRoot);
            if (identity.IsOk)
            {
                for (var waited = 0;
                     waited < 300 && ResidentPythonWorker.ReadProcessStartTicks(identity.Value.Pid) == identity.Value.ProcessStartTicks;
                     waited++)
                {
                    Thread.Sleep(100);
                }
                if (ResidentPythonWorker.ReadProcessStartTicks(identity.Value.Pid) == identity.Value.ProcessStartTicks)
                {
                    _ = ResidentPythonWorker.Kill(identity.Value);
                }
            }
        }
    }

    sealed record PhaseRun(string Runtime, StageReturnPackage Return, long WorkerPid);

    static PhaseRun RunPhase(
        SiteFixture.TemporaryDirectory home,
        string repository,
        string python,
        string workerRoot,
        string storeRoot,
        string cellWorkspace,
        string cellId,
        long ordinal,
        string attemptId,
        bool link,
        string? storeSha)
    {
        var package = home.Join("outbound-" + attemptId);
        Directory.CreateDirectory(package);
        var request = OracleRequest(attemptId, link);
        var instructions = WritePhasePackage(package, repository, request, cellId, ordinal, attemptId, link, storeSha);

        var runtime = Path.Combine(cellWorkspace, attemptId);
        var progress = new StringWriter();
        var result = CellPhaseSite.RunCellPhase(
            package, runtime, workerRoot, storeRoot, python, TimeSpan.FromSeconds(300),
            progress, workerStartup: null, authorityCellId: null);
        Assert.True(result.IsOk, result.RefusalDetail);
        var returned = StageAttemptSite.PackageStageReturn(
            runtime, home.Join("return-" + attemptId), instructions, result.Value, includeOutputs: false);
        Assert.True(returned.IsOk, returned.RefusalDetail);
        var identity = ResidentPythonWorker.ReadIdentity(workerRoot);
        Assert.True(identity.IsOk, identity.RefusalDetail);
        return new PhaseRun(runtime, returned.Value, identity.Value.Pid);
    }

    static AttemptInstructions WritePhasePackage(
        string package, string repository, byte[] request, string cellId, long ordinal, string attemptId, bool link, string? storeSha)
    {
        var phase = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schema"] = SitePaths.PhaseSchema,
            ["cell_id"] = cellId,
            ["ordinal"] = ordinal,
            ["handler"] = "oracle.controlled_linear.generate",
            ["inputs_from_phases"] = link
                ? new Dictionary<string, object> { ["training_worker_request"] = new Dictionary<string, object> { ["phase"] = 0L, ["output"] = "horizon_end_response" } }
                : new Dictionary<string, object>(),
            ["inputs_from_store"] = storeSha is null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object> { ["generated_dataset"] = new Dictionary<string, object> { ["sha256"] = storeSha, ["file_name"] = "bank.safetensors" } },
        }));
        var sourceFiles = new[]
        {
            "python/fern_stage_runtime/__init__.py", "python/fern_stage_runtime/__main__.py", "python/fern_stage_runtime/cli.py",
            "python/fern_stage_runtime/worker.py", "python/fern_stage_runtime/postprocess.py",
        }.Select(relative =>
        {
            var bytes = File.ReadAllBytes(Path.Combine(repository, relative));
            return new AttemptFile(relative, bytes.LongLength, SiteFixture.Sha256(bytes));
        }).ToList();
        var instructions = SiteFixture.Instructions(SitePaths.StageRequestFile, request, repository, sourceFiles) with
        {
            AttemptId = attemptId,
            Files = [
                new AttemptFile(SitePaths.StageRequestFile, request.LongLength, SiteFixture.Sha256(request)),
                new AttemptFile(SitePaths.PhaseFile, phase.LongLength, SiteFixture.Sha256(phase)),
            ],
        };
        SiteFixture.WriteInstructionsRecord(package, instructions);
        SiteFixture.WriteRuntimeFile(package, SitePaths.StageRequestFile, request);
        SiteFixture.WriteRuntimeFile(package, SitePaths.PhaseFile, phase);
        return instructions;
    }

    /// <summary>
    /// A request the oracle handler cannot serve: a state transition that is not a
    /// square matrix. The worker reports the failure with its own sentence; the
    /// test below checks the Site keeps that sentence instead of the bare word "failed".
    /// </summary>
    static byte[] FailingOracleRequest(string attemptId) =>
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(OracleRequest(attemptId, link: false))
            .Replace("\"state_transition\":[[2.0]]", "\"state_transition\":[[2.0,1.0]]"));

    static byte[] OracleRequest(string attemptId, bool link)
    {
        var inputs = link
            ? """{"training_worker_request":{"relative_path":"inputs/training_worker_request/horizon_end_response.json"}}"""
            : "{}";
        return Encoding.UTF8.GetBytes(
            $$$$"""
            {"schema":"fern.stage_runner_request.v1","protocol_version":"fern.stage_runner.v1",
            "stage_attempt_id":"{{{{attemptId}}}}","handler":"oracle.controlled_linear.generate",
            "invocation":{"schema":"fern.stage_invocation.v1","stage_id":"generate",
            "implementation":{"runner":"python_module","entrypoint":"fern_train.worker"},
            "parameters":{"state_transition":[[2.0]],"action_response":[[3.0]],"process_covariance":[[5.0]],
            "initial_state":[7.0],"future_actions":[[1.0],[4.0]],"counterfactual_actions":[[1.0],[5.0]]}},
            "invocation_digest":"sha256:{{{{new string('1', 64)}}}}","execution_key":"execution-key-{{{{attemptId}}}}",
            "implementation_digest":"sha256:{{{{new string('2', 64)}}}}","input_files":{{{{inputs}}}},
            "declared_outputs":{"horizon_end_response":{"role":"horizon_end_response","schema_id":"fern.horizon_end_response.v1"}}}
            """.ReplaceLineEndings(string.Empty));
    }

    /// <summary>
    /// The actual Python child, not a forged PID, dies while its root remains.
    /// Its exact ready identity must become immutable lifecycle evidence before
    /// the replacement publishes a distinct process/launch identity. The test
    /// intentionally starts no Cell: worker ownership is established before
    /// request admission.
    /// </summary>
    [Fact]
    public void DeadResidentWorkerArchivesPriorLifecycleBeforeReplacementPublishesNewIdentity()
    {
        var python = TestPython();
        var repository = RepositoryRoot();
        using var home = new SiteFixture.TemporaryDirectory();
        var workerRoot = home.Join("worker");
        ResidentPythonWorker.WorkerIdentity? replacement = null;
        try
        {
            var first = ResidentPythonWorker.EnsureRunning(
                workerRoot,
                python,
                repository,
                TimeSpan.FromSeconds(90),
                observeStartup: null);
            Assert.True(first.IsOk, first.RefusalDetail);
            var firstIdentity = first.Value;
            var firstWorkerJson = File.ReadAllBytes(Path.Combine(workerRoot, ResidentPythonWorker.WorkerFile));
            Assert.Equal(firstIdentity.ProcessStartTicks, ResidentPythonWorker.ReadProcessStartTicks(firstIdentity.Pid));

            var killedFirst = ResidentPythonWorker.Kill(firstIdentity);
            Assert.True(killedFirst.IsOk, killedFirst.RefusalDetail);
            for (var waited = 0;
                 waited < 100 && ResidentPythonWorker.ReadProcessStartTicks(firstIdentity.Pid) is not null;
                 waited++)
            {
                Thread.Sleep(50);
            }
            Assert.True(
                ResidentPythonWorker.ReadProcessStartTicks(firstIdentity.Pid) is null,
                "the killed Python child still has a live Linux process-start identity");

            var second = ResidentPythonWorker.EnsureRunning(
                workerRoot,
                python,
                repository,
                TimeSpan.FromSeconds(90),
                observeStartup: null);
            Assert.True(second.IsOk, second.RefusalDetail);
            replacement = second.Value;

            Assert.NotEqual(firstIdentity.LaunchId, replacement.LaunchId);
            Assert.True(
                firstIdentity.Pid != replacement.Pid
                    || firstIdentity.ProcessStartTicks != replacement.ProcessStartTicks,
                "a replacement cannot reuse both the old PID and its process-start identity");
            var current = ResidentPythonWorker.ReadIdentity(workerRoot);
            Assert.True(current.IsOk, current.RefusalDetail);
            Assert.Equal(replacement.LaunchId, current.Value.LaunchId);

            var lifecycle = Path.Combine(workerRoot, ResidentPythonWorker.WorkerLifecycleDirectory);
            var identityEvidence = Path.Combine(lifecycle, firstIdentity.LaunchId + ".worker.json");
            var terminationEvidence = Path.Combine(lifecycle, firstIdentity.LaunchId + ".termination.json");
            Assert.Equal(firstWorkerJson, File.ReadAllBytes(identityEvidence));
            using var termination = JsonDocument.Parse(File.ReadAllBytes(terminationEvidence));
            Assert.Equal(ResidentPythonWorker.WorkerTerminationSchema, termination.RootElement.GetProperty("schema").GetString());
            Assert.Equal(firstIdentity.LaunchId, termination.RootElement.GetProperty("launch_id").GetString());
            Assert.Equal(firstIdentity.Pid, termination.RootElement.GetProperty("pid").GetInt64());
            Assert.Equal(firstIdentity.ProcessStartTicks, termination.RootElement.GetProperty("process_start_ticks").GetInt64());
            Assert.Equal("process_exited", termination.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(firstIdentity.LaunchId + ".worker.json", termination.RootElement.GetProperty("worker_identity_file").GetString());
        }
        finally
        {
            ResidentPythonWorker.RequestStop(workerRoot);
            if (replacement is not null)
            {
                for (var waited = 0;
                     waited < 100 && ResidentPythonWorker.ReadProcessStartTicks(replacement.Pid) == replacement.ProcessStartTicks;
                     waited++)
                {
                    Thread.Sleep(50);
                }
                if (ResidentPythonWorker.ReadProcessStartTicks(replacement.Pid) == replacement.ProcessStartTicks)
                {
                    _ = ResidentPythonWorker.Kill(replacement);
                }
            }
        }
    }

    /// <summary>
    /// Cancellation targets the actual child lifetime, never a naked PID. A
    /// stale launch or a worker.json that claims a reused PID must leave the
    /// current child alive; restoring its exact identity permits termination.
    /// </summary>
    [Fact]
    public void CancellationRefusesStaleLifetimeBeforeTerminatingExactRealPythonChild()
    {
        var python = TestPython();
        var repository = RepositoryRoot();
        using var home = new SiteFixture.TemporaryDirectory();
        var workerRoot = home.Join("worker");
        var workerJsonPath = Path.Combine(workerRoot, ResidentPythonWorker.WorkerFile);
        byte[]? exactWorkerJson = null;
        ResidentPythonWorker.WorkerIdentity? exactIdentity = null;
        try
        {
            var started = ResidentPythonWorker.EnsureRunning(
                workerRoot,
                python,
                repository,
                TimeSpan.FromSeconds(90),
                observeStartup: null);
            Assert.True(started.IsOk, started.RefusalDetail);
            exactIdentity = started.Value;
            exactWorkerJson = File.ReadAllBytes(workerJsonPath);

            var staleLaunch = exactIdentity with { LaunchId = "obsolete-" + exactIdentity.LaunchId };
            var staleLaunchCancellation = ResidentPythonWorker.Kill(staleLaunch);
            Assert.False(staleLaunchCancellation.IsOk);
            Assert.Contains("now names launch", staleLaunchCancellation.Failure.Detail, StringComparison.Ordinal);
            Assert.Equal(
                exactIdentity.ProcessStartTicks,
                ResidentPythonWorker.ReadProcessStartTicks(exactIdentity.Pid));

            var forged = JsonNode.Parse(Encoding.UTF8.GetString(exactWorkerJson))!.AsObject();
            forged["process_start_ticks"] = exactIdentity.ProcessStartTicks - 1;
            File.WriteAllText(workerJsonPath, forged.ToJsonString());
            var staleTicks = ResidentPythonWorker.ReadIdentity(workerRoot);
            Assert.True(staleTicks.IsOk, staleTicks.RefusalDetail);
            var staleTicksCancellation = ResidentPythonWorker.Kill(staleTicks.Value);
            Assert.False(staleTicksCancellation.IsOk);
            Assert.Contains("process-start ticks", staleTicksCancellation.Failure.Detail, StringComparison.Ordinal);
            Assert.Equal(
                exactIdentity.ProcessStartTicks,
                ResidentPythonWorker.ReadProcessStartTicks(exactIdentity.Pid));

            File.WriteAllBytes(workerJsonPath, exactWorkerJson);
            var restored = ResidentPythonWorker.ReadIdentity(workerRoot);
            Assert.True(restored.IsOk, restored.RefusalDetail);
            var exactCancellation = ResidentPythonWorker.Kill(restored.Value);
            Assert.True(exactCancellation.IsOk, exactCancellation.RefusalDetail);
            for (var waited = 0;
                 waited < 100 && ResidentPythonWorker.ReadProcessStartTicks(exactIdentity.Pid) == exactIdentity.ProcessStartTicks;
                 waited++)
            {
                Thread.Sleep(50);
            }
            Assert.True(
                ResidentPythonWorker.ReadProcessStartTicks(exactIdentity.Pid) != exactIdentity.ProcessStartTicks,
                "the exact resident-worker lifetime survived its accepted termination");
        }
        finally
        {
            if (exactWorkerJson is not null)
            {
                File.WriteAllBytes(workerJsonPath, exactWorkerJson);
            }
            ResidentPythonWorker.RequestStop(workerRoot);
            var current = ResidentPythonWorker.ReadIdentity(workerRoot);
            if (current.IsOk
                && ResidentPythonWorker.ReadProcessStartTicks(current.Value.Pid) == current.Value.ProcessStartTicks)
            {
                _ = ResidentPythonWorker.Kill(current.Value);
            }
        }
    }

    static string TestPython()
    {
        var python = Environment.GetEnvironmentVariable("FERN_TEST_PYTHON") ?? "/home/user/.venv/bin/python";
        if (!File.Exists(python))
        {
            throw new InvalidOperationException($"no Python interpreter at {python}; set FERN_TEST_PYTHON");
        }
        return python;
    }

    static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "csharp", "Fern.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("no repository root above " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// Worker-root ownership is a process-lifetime protocol, not just a startup
/// de-duplication check. These tests construct the filesystem state a
/// contender sees; they do not start the Python worker.
/// </summary>
public sealed class ResidentWorkerLifetimeOwnerTests
{
    [Fact]
    public void WorkerJsonReadyWithoutLifetimeLockIsRefusedBeforeOverwritingIdentityOrClaimingInbox()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var root = home.Join("worker");
        var sourceRoot = home.Join("source");
        Directory.CreateDirectory(sourceRoot);
        PrepareWorkerRootWithWaitingCell(root);
        WriteWorkerJson(root, sourceRoot, state: "ready", includeLifetimeFields: true);
        var originalIdentity = File.ReadAllBytes(Path.Combine(root, ResidentPythonWorker.WorkerFile));

        var contender = ResidentPythonWorker.EnsureRunning(
            root,
            "/bin/false",
            sourceRoot,
            TimeSpan.FromMilliseconds(250),
            observeStartup: null);

        Assert.False(contender.IsOk, "identity without the lifetime lock must not be treated as a reusable owner");
        Assert.Contains("lifetime", contender.Failure.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalIdentity, File.ReadAllBytes(Path.Combine(root, ResidentPythonWorker.WorkerFile)));
        Assert.Equal(["000001.json"], Directory.EnumerateFiles(Path.Combine(root, ResidentPythonWorker.InboxDirectory)).Select(Path.GetFileName).ToList());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, ResidentPythonWorker.RunningDirectory)));
    }

    [Fact]
    public void WorkerJsonStartingOwnerRefusesContenderBeforeOverwritingIdentityOrClaimingInbox()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var root = home.Join("worker");
        var sourceRoot = home.Join("source");
        Directory.CreateDirectory(sourceRoot);
        PrepareWorkerRootWithWaitingCell(root);
        WriteWorkerJson(root, sourceRoot, state: "starting", includeLifetimeFields: true);
        var originalIdentity = File.ReadAllBytes(Path.Combine(root, ResidentPythonWorker.WorkerFile));

        var contender = ResidentPythonWorker.EnsureRunning(
            root,
            "/bin/false",
            sourceRoot,
            TimeSpan.FromMilliseconds(250),
            observeStartup: null);

        Assert.False(contender.IsOk, "a worker that is still starting owns the root until its lifetime lock is released");
        Assert.Contains("starting", contender.Failure.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalIdentity, File.ReadAllBytes(Path.Combine(root, ResidentPythonWorker.WorkerFile)));
        Assert.Equal(["000001.json"], Directory.EnumerateFiles(Path.Combine(root, ResidentPythonWorker.InboxDirectory)).Select(Path.GetFileName).ToList());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, ResidentPythonWorker.RunningDirectory)));
    }

    [Fact]
    public void LegacyWorkerJsonWithoutLifetimeOwnerFieldsIsRefusedBeforeOverwriteOrInboxClaim()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var root = home.Join("worker");
        var sourceRoot = home.Join("source");
        Directory.CreateDirectory(sourceRoot);
        PrepareWorkerRootWithWaitingCell(root);
        WriteWorkerJson(root, sourceRoot, state: "ready", includeLifetimeFields: false);
        var originalIdentity = File.ReadAllBytes(Path.Combine(root, ResidentPythonWorker.WorkerFile));

        var contender = ResidentPythonWorker.EnsureRunning(
            root,
            "/bin/false",
            sourceRoot,
            TimeSpan.FromMilliseconds(250),
            observeStartup: null);

        Assert.False(contender.IsOk, "legacy worker.json must not be accepted as a current lifetime-owner proof");
        Assert.Contains("worker.json", contender.Failure.Detail, StringComparison.Ordinal);
        Assert.Contains("lifetime", contender.Failure.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalIdentity, File.ReadAllBytes(Path.Combine(root, ResidentPythonWorker.WorkerFile)));
        Assert.Equal(["000001.json"], Directory.EnumerateFiles(Path.Combine(root, ResidentPythonWorker.InboxDirectory)).Select(Path.GetFileName).ToList());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, ResidentPythonWorker.RunningDirectory)));
    }

    [Fact]
    public void CurrentIdentityWhosePidWasReusedIsStaleRatherThanDowngradedToLegacyLiveOwner()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var root = home.Join("worker");
        var sourceRoot = home.Join("source");
        Directory.CreateDirectory(sourceRoot);
        PrepareWorkerRootWithWaitingCell(root);
        var currentTicks = ResidentPythonWorker.ReadProcessStartTicks(Environment.ProcessId)
            ?? throw new InvalidOperationException("the test process has no Linux start identity");
        WriteWorkerJson(
            root,
            sourceRoot,
            state: "ready",
            includeLifetimeFields: true,
            processStartTicks: currentTicks - 1);

        var replacement = ResidentPythonWorker.EnsureRunning(
            root,
            "/bin/false",
            sourceRoot,
            TimeSpan.FromSeconds(2),
            observeStartup: null);

        Assert.False(replacement.IsOk);
        Assert.Contains("exited before writing worker.json", replacement.Failure.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy", replacement.Failure.Detail, StringComparison.OrdinalIgnoreCase);
    }

    static void PrepareWorkerRootWithWaitingCell(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, ResidentPythonWorker.InboxDirectory));
        Directory.CreateDirectory(Path.Combine(root, ResidentPythonWorker.RunningDirectory));
        File.WriteAllText(Path.Combine(root, ResidentPythonWorker.InboxDirectory, "000001.json"), "{}");
    }

    static void WriteWorkerJson(
        string root,
        string sourceRoot,
        string state,
        bool includeLifetimeFields,
        long? processStartTicks = null)
    {
        var identity = new Dictionary<string, object?>
        {
            ["pid"] = Environment.ProcessId,
            ["started_at"] = "2026-08-30T00:00:00.000+00:00",
            ["interpreter"] = "/usr/bin/python3",
            ["working_directory"] = sourceRoot,
            ["capability_digest"] = "sha256:" + new string('a', 64),
        };
        if (includeLifetimeFields)
        {
            identity["state"] = state;
            identity["launch_id"] = "launch-" + Guid.NewGuid().ToString("n");
            identity["lock_protocol"] = ResidentPythonWorker.WorkerLifetimeLockProtocol;
            identity["process_start_ticks"] = processStartTicks
                ?? ResidentPythonWorker.ReadProcessStartTicks(Environment.ProcessId);
            identity["root"] = root;
        }
        File.WriteAllText(
            Path.Combine(root, ResidentPythonWorker.WorkerFile),
            JsonSerializer.Serialize(identity, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Registry-D: `fern-site workers` is a listing of the worker folders, nothing more.</summary>
public sealed class WorkerListingTests
{
    [Fact]
    public void TheListingReadsWorkerJsonInboxRunningAndTheNewestReceipt()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        var root = home.Join("workers", new string('e', 64), new string('1', 64));
        Directory.CreateDirectory(Path.Combine(root, "inbox"));
        Directory.CreateDirectory(Path.Combine(root, "running"));
        Directory.CreateDirectory(Path.Combine(root, "receipts", "cell-z"));
        File.WriteAllText(
            Path.Combine(root, "worker.json"),
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["pid"] = 1,
                ["process_start_ticks"] = ResidentPythonWorker.ReadProcessStartTicks(1),
                ["started_at"] = "2026-08-29T00:00:00.000+00:00",
                ["interpreter"] = "/usr/bin/python3",
                ["working_directory"] = "/src",
                ["capability_digest"] = "sha256:aa",
                ["state"] = "ready",
                ["launch_id"] = "listing-fixture",
                ["lock_protocol"] = ResidentPythonWorker.WorkerLifetimeLockProtocol,
                ["root"] = root,
            }));
        File.WriteAllText(Path.Combine(root, "inbox", "000002.json"), "{}");
        File.WriteAllText(Path.Combine(root, "inbox", "000003.json.partial"), "{");
        File.WriteAllText(Path.Combine(root, "running", "000001.json"), """{"schema":"fern.cell_request.v1","cell_id":"cell-z"}""");
        File.WriteAllText(Path.Combine(root, "receipts", "cell-z", "0-serie.materialize.cell.json"), "{}");
        File.WriteAllText(Path.Combine(root, "receipts", "cell-z", "request-000001.json"), "{}");

        var listing = Assert.Single(ResidentPythonWorker.ListWorkers(home.Join()));

        Assert.Equal(new string('e', 64), listing.InterpreterFolder);
        Assert.Equal(new string('1', 64), listing.ImplementationFolder);
        Assert.NotNull(listing.Identity);
        Assert.True(listing.Alive, "pid 1 is init and is always alive");
        Assert.Equal(1, listing.Waiting);
        Assert.Equal("cell-z", listing.RunningCell);
        Assert.StartsWith("cell-z/0-serie.materialize.cell.json at ", listing.LastReceipt);

        var process = SiteCliTests.Spawn(["workers", "--work-root", home.Join()]);
        Assert.Equal(0, process.ExitStatus);
        Assert.Equal(string.Empty, process.Standard.Error);
        Assert.Contains("running cell-z; 1 waiting", process.Standard.Output);
    }
}
