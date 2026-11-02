using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fern.Registry;
using Fern.Scheduler;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

/// <summary>
/// The production cell worker, against a FAKE process.
///
/// Nothing here trains and nothing here starts an interpreter: the worker
/// launches through <see cref="WorkerEnvironmentPreparation.IHost"/>, and these
/// tests hand it a host that records the call and answers. A test that trained
/// would be refused by AGENTS.md rule 1 anyway — training runs on the GPU, and
/// a suite is not where that is decided. What a fake host cannot check is that
/// Python accepts the document; the end-to-end test in
/// <see cref="RegisteredRouteTrainsOneCellTests"/> runs the real
/// interpreter for exactly that reason.
/// </summary>
public sealed class LocalCellWorkerTests
{
    sealed class UnreachableRenewal() : DelegatingHandler(new HttpClientHandler())
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellation) =>
            request.RequestUri!.AbsolutePath.EndsWith("/RenewAttempt", StringComparison.Ordinal)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                : base.Send(request, cancellation);
    }

    /// <summary>
    /// CHG 2026-09-26 (slice W3; owner: nothing kills a training for silence): this test asserted
    /// that a Home which answers no renewal stops the trainer. It no longer does: the training runs
    /// on, and its cell and card stay held (slice W2) until it reports again or a person gives it up.
    /// </summary>
    [Fact]
    public void AHomeThatAnswersNoRenewalDoesNotStopTheTrainer()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var scope = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, 1, "{}").TryGetValue(out var attempt));
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var address = $"http://127.0.0.1:{port}/";
        using var service = new HomeService(writer, address);
        service.Start();
        using var client = new HomeClient(new Uri(address), TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(5), handler: new UnreachableRenewal());
        var host = new BlockingHost();
        var worker = new LocalCellWorker(client, Placement(work), host, heartbeat: TimeSpan.FromMilliseconds(10));
        var claimed = new StudyOrchestrator.ClaimedCell(studyId, cellId, "one", attempt.AttemptId, attempt.FencingEpoch, 1, "{}", null, work, RegistryOperations.CodeFromWorkingTree, 1);
        var outcome = worker.Run(claimed, default);
        Assert.False(host.WasCancelled, "an unanswered renewal must not end the training");
    }

    /// <summary>
    /// The owner, 2026-09-26 (give up by looking): the attempt names the process running it as soon
    /// as it is dispatched, not only at its first beat, so a later supervisor on this machine can look.
    /// </summary>
    [Fact]
    public void TheAttemptNamesItsProcessAsSoonAsItRuns()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var scope = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, 1, "{}").TryGetValue(out var attempt));
        var host = new FakeHost((_, arguments, _) => Answer(arguments, _ => new BoundedProbe.Outcome.Cancelled(1)));
        var worker = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host);
        var claimed = new StudyOrchestrator.ClaimedCell(studyId, cellId, "one", attempt.AttemptId, attempt.FencingEpoch, 1, "{}", null, work, RegistryOperations.CodeFromWorkingTree, 1);
        worker.Run(claimed, default);
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT remote_execution_id FROM attempts WHERE id = $id";
        command.Parameters.AddWithValue("$id", attempt.AttemptId);
        Assert.Equal(LocalCellWorker.RunningHere, command.ExecuteScalar() as string);
    }

    /// <summary>
    /// Slice W3: a renewal the Registry REFUSES — a person gave the attempt up — does stop the
    /// trainer: that is the Registry's revocation after a person's decision.
    /// </summary>
    [Fact]
    public void ARenewalTheRegistryRefusesStopsTheTrainer()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var scope = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, 1, "{}").TryGetValue(out var attempt));
        Assert.True(RegistryOperations.FailAttempt(writer, attempt.AttemptId, attempt.FencingEpoch,
            RegistryOperations.AttemptLost, """{"detail":"given up by a person"}""").IsOk);
        var host = new BlockingHost();
        var worker = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host, heartbeat: TimeSpan.FromMilliseconds(10));
        var claimed = new StudyOrchestrator.ClaimedCell(studyId, cellId, "one", attempt.AttemptId, attempt.FencingEpoch, 1, "{}", null, work, RegistryOperations.CodeFromWorkingTree, 1);
        worker.Run(claimed, default);
        Assert.True(host.WasCancelled);
    }
    sealed class FakeHost(Func<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, BoundedProbe.Outcome> answer)
        : WorkerEnvironmentPreparation.IHost
    {
        public bool IsLinux => true;
        public string? Program { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public IReadOnlyDictionary<string, string> Environment { get; private set; } = new Dictionary<string, string>();
        public string? WorkingDirectory { get; private set; }
        public int Calls { get; private set; }
        public int Runs { get; private set; }
        /// <summary>The time the last RUN was given before it would be ended (slice W3).</summary>
        public long RunTimeoutMs { get; private set; }

        public BoundedProbe.Outcome Run(string program, IReadOnlyList<string> arguments, long timeoutMs, long maxBytes,
            string workingDirectory, IReadOnlyDictionary<string, string> environment)
        {
            Calls++;
            if (arguments.Contains("run") || arguments.Contains("run-cell"))
            {
                // The probe is not the call a test is asserting about; keeping
                // the last RUN is what makes every assertion below read as "the
                // cell was launched like this".
                Runs++;
                RunTimeoutMs = timeoutMs;
                Program = program;
                Arguments = arguments;
                Environment = environment;
                WorkingDirectory = workingDirectory;
            }
            return answer(program, arguments, environment);
        }
    }

    /// <summary>The capability receipt the runtime prints for `probe`, and the digest inside it.</summary>
    const string ImplementationDigest = "sha256:" + "9f" + "00000000000000000000000000000000000000000000000000000000000000";

    static BoundedProbe.Outcome Answer(IReadOnlyList<string> arguments, Func<string, BoundedProbe.Outcome> whenRunning)
    {
        if (arguments.Contains("probe"))
        {
            var receipt = $$"""
                {"schema":"fern.stage_runner_capability.v1","protocol_version":"fern.stage_runner.v1",
                 "implementation_digest":"{{ImplementationDigest}}","handlers":["fern.study.run_cell"],
                 "entrypoint":"fern_train.worker"}
                """;
            return new BoundedProbe.Outcome.Completed(0, Encoding.UTF8.GetBytes(receipt), "");
        }
        return whenRunning(arguments[arguments.ToList().IndexOf("--workspace") + 1]);
    }

    /// <summary>A runtime that trained the cell and left the unit's results where its report says.</summary>
    static BoundedProbe.Outcome Trains(IReadOnlyList<string> arguments, string results) =>
        Answer(arguments, workspace =>
        {
            Directory.CreateDirectory(Path.Combine(workspace, "outputs"));
            File.WriteAllText(Path.Combine(workspace, "outputs", "results.json"), results);
            WriteReport(workspace,
                """
                {"schema":"fern.stage_runner_report.v1","stage_attempt_id":"attempt-7",
                 "outcome":{"kind":"success","output_paths":{"unit_results":"outputs/results.json"}}}
                """, JsonDocument.Parse(File.ReadAllText(arguments[4])).RootElement.GetProperty("artifact_prefix").GetString());
            return new BoundedProbe.Outcome.Completed(0, [], "");
        });

    /// <summary>A runtime that refused the cell and said why, in its own words.</summary>
    static BoundedProbe.Outcome Refuses(IReadOnlyList<string> arguments, string code, string message) =>
        Answer(arguments, workspace =>
        {
            WriteReport(workspace, JsonSerializer.Serialize(new
            {
                schema = "fern.stage_runner_report.v1",
                stage_attempt_id = "attempt-7",
                outcome = new { kind = "failure", code, message },
            }));
            return new BoundedProbe.Outcome.Completed(1, [], message);
        });

    static string PrefixOf(string workspace)
    {
        var path = Directory.GetFiles(workspace, "*.request.stage_runner_request.v1.json").Single();
        using var request = JsonDocument.Parse(File.ReadAllText(path));
        return request.RootElement.GetProperty("artifact_prefix").GetString()!;
    }

    static void WriteReport(string workspace, string report, string? prefix = null)
    {
        prefix ??= PrefixOf(workspace);
        var legacy = Path.Combine(workspace, "outputs", "results.json");
        if (File.Exists(legacy))
        {
            File.Move(legacy, Path.Combine(workspace, prefix + "results.json"));
            Directory.Delete(Path.GetDirectoryName(legacy)!);
            report = report.Replace("outputs/results.json", prefix + "results.json", StringComparison.Ordinal);
        }
        File.WriteAllText(Path.Combine(workspace, prefix + "report.stage_runner_report.v1.json"), report);
    }

    const string CudaConfig = """{"schema":"fern.training.v2","model":{"type":"fern"},"runtime":{"device":"cuda"}}""";
    const string CpuConfig = """{"schema":"fern.training.v2","model":{"type":"fern"},"runtime":{"device":"cpu"}}""";
    const string ConfigHash = "0123456789abcdef";
    const string DatasetDigest = "1111111111111111111111111111111111111111111111111111111111111111";

    /// <summary>
    /// A compiled study of one cell, as `study compile` writes one: the card
    /// under its resolution, the dataset's declared file with its digest, the
    /// home compile's evidence, and a cell whose OWN seed is not the study's.
    /// </summary>
    static string CompiledStudy(string configJson, long cellSeed = 43) =>
        new JsonObject
        {
            ["schema"] = "fern.compiled-study.v1",
            ["study"] = "worked",
            ["seed"] = 42,
            ["device"] = "cuda",
            ["resolution"] = new JsonObject
            {
                ["resolved"] = new JsonObject
                {
                    ["specification"] = "study:sha256:" + new string('a', 64),
                    ["manifest"] = new JsonObject
                    {
                        ["study"] = new JsonObject { ["name"] = "worked", ["question"] = "does the registered route train" },
                        ["stage"] = new JsonArray(),
                    },
                },
            },
            ["dataset"] = new JsonObject
            {
                ["name"] = "unit.controlled",
                ["inputs"] = new JsonObject
                {
                    ["bank"] = new JsonObject
                    {
                        ["path"] = "serie/tests/fixtures/bank.safetensors",
                        ["sha256"] = DatasetDigest,
                        ["byte_count"] = 4,
                    },
                },
            },
            ["promotion_evidence"] = new JsonObject
            {
                ["control"] = new JsonObject { ["completed"] = true, ["implementation_fault"] = false },
            },
            ["cells"] = new JsonArray(new JsonObject
            {
                ["cell"] = "one",
                ["ordinal"] = 0,
                ["coordinates"] = new JsonObject { ["seed"] = cellSeed, ["dataset"] = "unit.controlled" },
                ["config_hash"] = ConfigHash,
                ["config"] = JsonNode.Parse(configJson),
                ["unit_outputs"] = new JsonObject { ["unit_results"] = new JsonObject() },
            }),
        }.ToJsonString();

    static (RegistryWriter Writer, long StudyId, long CellId, string Work) Home(
        string configJson, long registeredSeed = 43, string? compiledStudy = null)
    {
        var path = TestRegistry.CreateRegistry();
        var writer = RegistryHome.OpenWriter(path);
        long sourceId;
        long siteId;
        using (var command = writer.Connection.CreateCommand())
        {
            // CHG 2026-09-26: the source declares the data input the compiled
            // study names (serie/tests/fixtures/bank.safetensors), because since
            // commit 54fd3db0b RegisterStudy refuses a study whose declared data
            // the source does not carry, and with '[]' here every test of this
            // class failed at registration.
            command.CommandText =
                """
                INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
                VALUES ('5e87434e', $declared, 'commit', '2026-09-11') RETURNING id
                """;
            command.Parameters.AddWithValue("$declared",
                $$"""[{"path":"serie/tests/fixtures/bank.safetensors","kind":"regular_file","byte_digest":"sha256:{{DatasetDigest}}","bytes":4}]""");
            sourceId = Convert.ToInt64(command.ExecuteScalar());
        }
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sites (name, capabilities_json, capacity_json, created_at)
                VALUES ('local-4070', '{"device":"cuda","admitted_models":["fern"]}', '{"slots":2}', '2026-09-11')
                RETURNING id
                """;
            siteId = Convert.ToInt64(command.ExecuteScalar());
        }
        var registered = RegistryOperations.RegisterStudy(
            writer, "worked", 1, compiledStudy ?? CompiledStudy(configJson), sourceId,
            [new RegistryOperations.CompiledCell(0, "one", "unit.controlled", registeredSeed, configJson,
                """{"device":"cuda","requires_cuda":true}""",
                """{"unit_results":"retained"}""")]);
        Assert.True(registered.TryGetValue(out var value));
        var work = Directory.CreateTempSubdirectory("fern-cell-work-").FullName;
        // Slice 6 (row 31): the Site holds the study's source at the work root these tests train from.
        // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): installed sources are
        // retired; the Site takes its cells' code from the working tree of a repository at the
        // work root, which is where these tests have always trained from.
        TestRegistry.Git(work, "init", "--quiet");
        TestRegistry.Git(work, "-c", "user.email=test@example.invalid", "-c", "user.name=worker test",
            "commit", "--quiet", "--allow-empty", "-m", "the work root");
        TestRegistry.DeclareCodeFrom(writer, siteId, RegistryOperations.CodeFromWorkingTree, work);
        return (writer, value.StudyId, value.CellIds[0], work);
    }

    static LocalCellWorker.Placement Placement(string work) => new(
        Python: "/home/user/.venv/bin/python", FernRunBinary: "/opt/fern-run",
        FernCompileBinary: "/opt/fern-compile", WorkRoot: work, Timeout: TimeSpan.FromMinutes(5),
        CheckpointStore: Path.Combine(work, "store"));

    // CHG 2026-09-26 (slice 4): a claim names where the Site takes the code from and the source.
    static StudyOrchestrator.ClaimedCell Claimed(long studyId, long cellId, string sourceRoot) =>
        new(studyId, cellId, "one", 7, 3, 1, "{}", null, sourceRoot, RegistryOperations.CodeFromWorkingTree, 1);

    // ── the launch ───────────────────────────────────────────────────────────

    static string PrepareStoppedRecovery(string work, StudyOrchestrator.ClaimedCell cell, string compiled)
    {
        var prefix = $"study-{cell.StudyId}.cell-{cell.CellId}.attempt-6.epoch-2.";
        using var document = JsonDocument.Parse(compiled);
        var input = Fern.Experiment.StudyCellRequest.Assemble(document.RootElement,
            document.RootElement.GetProperty("cells")[0], "cuda", Path.Combine(work, "store"), "").Value;
        File.WriteAllBytes(Path.Combine(work, prefix + "inputs.study_cell_request.json"), input);
        File.WriteAllText(Path.Combine(work, prefix + "request.stage_runner_request.v1.json"),
            JsonSerializer.Serialize(new { implementation_digest = ImplementationDigest, artifact_prefix = prefix }));
        File.WriteAllText(Path.Combine(work, prefix + "compiled-study.json"), compiled);
        File.WriteAllText(Path.Combine(work, prefix + "heartbeat.json"), JsonSerializer.Serialize(new
        { supervisor_host = System.Environment.MachineName, supervisor_pid = int.MaxValue }));
        File.WriteAllText(Path.Combine(work, prefix + "one.training-recovery.pt"), "complete epoch bytes");
        File.WriteAllText(Path.Combine(work, prefix + "one.model.safetensors"), "selected checkpoint bytes");
        return prefix;
    }

    [Fact]
    public void ExplicitRecoveryUsesNativeCliAndPreservesOriginalArtifacts()
    {
        var document = JsonNode.Parse(CompiledStudy(CudaConfig))!;
        document["dataset"]!["inputs"] = new JsonObject();
        var compiled = document.ToJsonString();
        var (writer, studyId, cellId, work) = Home(CudaConfig, compiledStudy: compiled);
        using var _ = writer;
        var claimed = Claimed(studyId, cellId, work);
        var prefix = PrepareStoppedRecovery(work, claimed, compiled);
        var host = new FakeHost((_, arguments, _) =>
        {
            if (arguments.Contains("probe")) return Answer(arguments, _ => throw new InvalidOperationException());
            Assert.Contains("run-cell", arguments);
            Assert.Contains("--resume", arguments);
            var actualPrefix = arguments[arguments.ToList().IndexOf("--artifact-prefix") + 1];
            Assert.Equal(LocalCellWorker.ArtifactPrefix(claimed), actualPrefix);
            Assert.Equal("complete epoch bytes", File.ReadAllText(Path.Combine(work, actualPrefix + "one.training-recovery.pt")));
            File.WriteAllText(Path.Combine(work, actualPrefix + "results.json"), new JsonObject
            {
                ["schema"] = StudyCollect.UnitResultsSchema, ["cell"] = "one", ["seed"] = 43, ["device"] = "cuda",
                ["config_hash"] = ConfigHash, ["config"] = JsonNode.Parse(CudaConfig),
            }.ToJsonString());
            return new BoundedProbe.Outcome.Completed(0, [], "");
        });
        var worker = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work) with
        { RecoveryPrefixes = new Dictionary<string, string> { ["one"] = prefix } }, host);
        var outcome = worker.Run(claimed, default);
        Assert.True(outcome.Completed, outcome.DetailJson);
        Assert.Equal("fern_analysis", host.Arguments[1]);
        Assert.Equal("complete epoch bytes", File.ReadAllText(Path.Combine(work, prefix + "one.training-recovery.pt")));
        Assert.True(File.Exists(Path.Combine(work, LocalCellWorker.ArtifactPrefix(claimed) + "recovery.json")));
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("configuration")]
    [InlineData("frozen_configuration")]
    [InlineData("implementation")]
    [InlineData("missing_checkpoint")]
    [InlineData("completed")]
    [InlineData("live_producer")]
    [InlineData("other_cell")]
    public void ExplicitRecoveryRefusesMismatchBeforeTraining(string fault)
    {
        var document = JsonNode.Parse(CompiledStudy(CudaConfig))!;
        document["dataset"]!["inputs"] = new JsonObject();
        var compiled = document.ToJsonString();
        var (writer, studyId, cellId, work) = Home(CudaConfig, compiledStudy: compiled);
        using var _ = writer;
        var claimed = Claimed(studyId, cellId, work);
        var prefix = PrepareStoppedRecovery(work, claimed, compiled);
        string Named(string suffix) => Path.Combine(work, prefix + suffix);
        if (fault == "seed")
        {
            var unit = JsonNode.Parse(File.ReadAllText(Named("inputs.study_cell_request.json")))!;
            unit["seed"] = 99;
            File.WriteAllText(Named("inputs.study_cell_request.json"), unit.ToJsonString());
        }
        if (fault == "configuration")
        {
            var unit = JsonNode.Parse(File.ReadAllText(Named("inputs.study_cell_request.json")))!;
            unit["config"]!["model"]!["type"] = "another_model";
            File.WriteAllText(Named("inputs.study_cell_request.json"), unit.ToJsonString());
        }
        if (fault == "frozen_configuration")
        {
            // CHG 2026-09-27 (config-hash-leaves, slice 1): the frozen cell's configuration
            // itself differs; a differing hash beside an unchanged one is no longer a fault.
            var unit = JsonNode.Parse(File.ReadAllText(Named("compiled-study.json")))!;
            unit["cells"]![0]!["config"]!["model"]!["type"] = "another_model";
            File.WriteAllText(Named("compiled-study.json"), unit.ToJsonString());
        }
        if (fault == "implementation")
            File.WriteAllText(Named("request.stage_runner_request.v1.json"), "{\"implementation_digest\":\"wrong\"}");
        if (fault == "missing_checkpoint") File.Delete(Named("one.training-recovery.pt"));
        if (fault == "completed") File.WriteAllText(Named("results.json"), "{}");
        if (fault == "live_producer") File.WriteAllText(Named("heartbeat.json"), JsonSerializer.Serialize(new
        { supervisor_host = System.Environment.MachineName, supervisor_pid = System.Environment.ProcessId }));
        if (fault == "other_cell") prefix = prefix.Replace($"cell-{cellId}.", $"cell-{cellId + 1}.", StringComparison.Ordinal);
        var host = new FakeHost((_, arguments, _) => Answer(arguments, _ => throw new InvalidOperationException("must not train")));
        var result = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work) with
        { RecoveryPrefixes = new Dictionary<string, string> { ["one"] = prefix } }, host).Run(claimed, default);
        Assert.False(result.Completed);
        Assert.Contains("recovery", result.DetailJson);
        Assert.Equal(0, host.Runs);
    }

    [Theory]
    [InlineData("cell")]
    [InlineData("seed")]
    [InlineData("config")]
    [InlineData("device")]
    public void RecoveryResultMustMatchTheRegisteredIdentity(string fault)
    {
        var root = Directory.CreateTempSubdirectory("fern-recovery-result-").FullName;
        var request = new JsonObject
        {
            ["cell"] = "one", ["seed"] = 43, ["config_hash"] = ConfigHash,
            ["device"] = "cuda", ["config"] = JsonNode.Parse(CudaConfig),
        };
        var result = request.DeepClone();
        result["schema"] = StudyCollect.UnitResultsSchema;
        // CHG 2026-09-27 (config-hash-leaves, slice 1): the configuration is compared whole.
        if (fault == "seed") result[fault] = 99;
        else if (fault == "config") result["config"]!["runtime"]!["dtype"] = "wrong";
        else result[fault] = "wrong";
        var path = Path.Combine(root, "results.json");
        File.WriteAllText(path, result.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => StudyCellRecovery.ReadResult(
            new StudyCellRecovery.Invocation([], path, "{}"), Encoding.UTF8.GetBytes(request.ToJsonString())));
    }

    /// <summary>
    /// The worker enters the Python study-cell runtime the way a Site does: the
    /// module, the `run` subcommand, the request and the workspace, the frozen
    /// source as the working directory, and the environment that route sets —
    /// plus FERN_COMPILE_BIN, because Python asks the Go compiler for a
    /// configuration's identity now.
    /// </summary>
    [Fact]
    public void ItEntersTheStudyCellRuntimeTheWayASiteDoes()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var host = new FakeHost((_, arguments, _) => Trains(arguments, """{"schema":"fern.study_unit_results.v4"}"""));
        var worker = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host);

        var outcome = worker.Run(Claimed(studyId, cellId, work), default);

        Assert.True(outcome.Completed);
        Assert.Equal("""{"schema":"fern.study_unit_results.v4"}""", outcome.ResultJson);
        Assert.Equal("/home/user/.venv/bin/python", host.Program);
        Assert.Equal(
            ["-m", "fern_stage_runtime", "run", "--request", host.Arguments[4], "--workspace", host.Arguments[6]],
            host.Arguments);
        Assert.Equal(work, host.Arguments[6]);
        Assert.DoesNotContain(Directory.GetDirectories(work), path => Path.GetFileName(path) == "one");
        Assert.Equal(work, host.WorkingDirectory);
        Assert.Equal("1", host.Environment["PYTHONDONTWRITEBYTECODE"]);
        // CHG 2026-09-26: src/ joined the import roots in 8552b7b6e (serie moved to src/serie/).
        Assert.Equal(work + Path.PathSeparator + Path.Combine(work, "python") + Path.PathSeparator + Path.Combine(work, "src"),
            host.Environment["PYTHONPATH"]);
        Assert.Equal("/opt/fern-run", host.Environment["FERN_RUN_BIN"]);
        Assert.Equal("/opt/fern-compile", host.Environment["FERN_COMPILE_BIN"]);
    }

    // ── the compiler a cell is given (2026-09-26) ─────────────────────────
    // CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
    // slice 4): the tests of carrying a study's compiler to a Site and of
    // --source-compiler pairs are retired with them. A cell is given the
    // compiler its study was compiled with when that file is on this machine.

    const string SiteName = "local-4070";

    /// <summary>A compiler file named by its digest, as publish-binary and the helper name them, whose bytes are the text given.</summary>
    static (string Path, string Sha256) CompilerFile(string directory, string contents)
    {
        Directory.CreateDirectory(directory);
        var bytes = Encoding.UTF8.GetBytes(contents);
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var path = Path.Combine(directory, "fern-compile." + digest);
        File.WriteAllBytes(path, bytes);
        return (path, digest);
    }

    /// <summary>
    /// The document the cell is: the study chain's formal shape, with the
    /// CELL's own seed rather than the study's head. A card declaring two seeds
    /// compiles one cell per seed, and on 2026-09-10 every cell of such a card
    /// trained at the first because the request read the study.
    /// </summary>
    [Fact]
    public void TheCellRequestCarriesTheCardTheSeedTheDataAndTheEvidence()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var host = new FakeHost((_, arguments, _) => Trains(arguments, "{}"));

        new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host).Run(Claimed(studyId, cellId, work), default);

        var request = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(host.Arguments[6], PrefixOf(host.Arguments[6]) + "inputs.study_cell_request.json"))).RootElement;
        Assert.Equal("fern.study_cell_request.v1", request.GetProperty("schema").GetString());
        Assert.Equal("worked", request.GetProperty("study").GetString());
        Assert.Equal("one", request.GetProperty("cell").GetString());
        Assert.Equal(43, request.GetProperty("seed").GetInt64());
        Assert.Equal("cuda", request.GetProperty("device").GetString());
        // CHG 2026-09-27 (config-hash-leaves): the request carries the configuration, no hash of it.
        Assert.False(request.TryGetProperty("config_hash", out JsonElement unusedHash), unusedHash.ToString());
        Assert.Equal("cuda", request.GetProperty("config").GetProperty("runtime").GetProperty("device").GetString());
        Assert.Equal("worked", request.GetProperty("card").GetProperty("study").GetProperty("name").GetString());
        Assert.Equal(DatasetDigest,
            request.GetProperty("dataset_inputs").GetProperty("bank").GetProperty("sha256").GetString());
        Assert.True(request.GetProperty("compile_evidence").GetProperty("control").GetProperty("completed").GetBoolean());
        // CHG 2026-09-26 (slice 3b): the study as compiled at home travels with the cell, so the worker compiles nothing.
        Assert.Equal("fern.compiled-study.v1", request.GetProperty("compiled_study").GetProperty("schema").GetString());
        Assert.Equal(ConfigHash, request.GetProperty("compiled_study").GetProperty("cells")[0].GetProperty("config_hash").GetString());
        Assert.Equal(Path.Combine(work, "store"), request.GetProperty("checkpoint_store").GetString());
    }

    /// <summary>
    /// The envelope names the handler that trains a study cell, the one input
    /// port that handler opens, the one output it owes, and the digest the
    /// environment itself reported — not one this side recomputed.
    /// </summary>
    [Fact]
    public void TheEnvelopeNamesTheHandlerThePortAndTheEnvironmentsOwnDigest()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var host = new FakeHost((_, arguments, _) => Trains(arguments, "{}"));

        new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host).Run(Claimed(studyId, cellId, work), default);

        var envelope = JsonDocument.Parse(File.ReadAllText(host.Arguments[4])).RootElement;
        Assert.Equal("fern.stage_runner_request.v1", envelope.GetProperty("schema").GetString());
        Assert.Equal("fern.stage_runner.v1", envelope.GetProperty("protocol_version").GetString());
        Assert.Equal("fern.study.run_cell", envelope.GetProperty("handler").GetString());
        Assert.Equal(ImplementationDigest, envelope.GetProperty("implementation_digest").GetString());
        Assert.Equal(LocalCellWorker.ArtifactName(Claimed(studyId, cellId, work), LocalCellWorker.CellRequestFile),
            envelope.GetProperty("input_files").GetProperty("study_cell_request").GetProperty("relative_path").GetString());
        Assert.Equal("fern.study_unit_results.v4",
            envelope.GetProperty("declared_outputs").GetProperty("unit_results").GetProperty("schema_id").GetString());
    }

    // ── the cell that saved its own result ───────────────────────────────────

    /// <summary>
    /// When the machine names a results root, the cell is told: it saves its
    /// document into the science store under that root the moment it ends. C#
    /// names the ROOT and never a database — which file under it a result
    /// belongs in is Python's to decide.
    /// </summary>
    [Fact]
    public void TheRequestCarriesTheResultsRootOnlyWhenTheMachineHasOne()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var host = new FakeHost((_, arguments, _) => Trains(arguments, "{}"));
        var selected = Path.Combine(work, "selected-results");

        new LocalCellWorker(new SqliteStudyStore(writer), Placement(work) with { ResultsRoot = selected }, host)
            .Run(Claimed(studyId, cellId, work), default);
        var told = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(host.Arguments[6], PrefixOf(host.Arguments[6]) + "inputs.study_cell_request.json"))).RootElement;
        Assert.Equal(selected, told.GetProperty("results_root").GetString());

        var (writer2, studyId2, cellId2, work2) = Home(CudaConfig);
        using var _2 = writer2;
        var silent = new FakeHost((_, arguments, _) => Trains(arguments, "{}"));
        new LocalCellWorker(new SqliteStudyStore(writer2), Placement(work2), silent).Run(Claimed(studyId2, cellId2, work2), default);
        var untold = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(silent.Arguments[6], PrefixOf(silent.Arguments[6]) + "inputs.study_cell_request.json"))).RootElement;
        Assert.False(untold.TryGetProperty("results_root", out var unused),
            $"a machine that cannot reach the results tree sends no root, and the cell hands its document back; "
            + $"this request carried {unused}");
    }

    /// <summary>
    /// A cell that saved itself is recorded as a REFERENCE — a few hundred bytes
    /// naming the database, the run and the digest — instead of a second copy of
    /// a document the store already holds.
    /// </summary>
    [Fact]
    public void ACellThatSavedItsOwnResultIsRecordedAsTheReference()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        const string results = """{"schema":"fern.study_unit_results.v4","cell":"one","study":"worked"}""";
        var host = new FakeHost((_, arguments, _) => Saves(arguments, results, Sha256Of(results)));

        var outcome = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work) with { ResultsRoot = work }, host)
            .Run(Claimed(studyId, cellId, work), default);

        Assert.True(outcome.Completed);
        using var recorded = JsonDocument.Parse(outcome.ResultJson!);
        Assert.Equal("fern.saved_cell_result.v1", recorded.RootElement.GetProperty("schema").GetString());
        Assert.Equal(Sha256Of(results), recorded.RootElement.GetProperty("sha256").GetString());
        Assert.Contains("science.db", recorded.RootElement.GetProperty("database").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain("study_unit_results", outcome.ResultJson!, StringComparison.Ordinal);
    }

    /// <summary>
    /// CHG 2026-09-26 (cards-without-hashes, row B16): the worker no longer hashes
    /// the results file; the cell compares its stored revision with the file it wrote
    /// before it reports. A save named by its revision alone, with no sha256, is
    /// recorded as the reference. (This replaces the test that a digest disagreeing
    /// with the results was refused, which is now the cell's check.)
    /// </summary>
    [Fact]
    public void ASavedResultNamedByItsRevisionWithoutADigestIsRecorded()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var host = new FakeHost((_, arguments, _) => Answer(arguments, workspace =>
        {
            Directory.CreateDirectory(Path.Combine(workspace, "outputs"));
            File.WriteAllText(Path.Combine(workspace, "outputs", "results.json"), "{}");
            WriteReport(workspace,
                """
                {"schema":"fern.stage_runner_report.v1","outcome":{"kind":"success",
                 "output_paths":{"unit_results":"outputs/results.json"},
                 "saved_result":{"schema":"fern.saved_cell_result.v1","saved":true,
                                 "database":"/r/science.db","study":"worked","run":"one","revision":3,
                                 "reference":"/r/science.db#study=worked&run=one&revision=3"}}}
                """);
            return new BoundedProbe.Outcome.Completed(0, [], "");
        }));

        var outcome = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work) with { ResultsRoot = work }, host)
            .Run(Claimed(studyId, cellId, work), default);

        Assert.True(outcome.Completed);
        using var recorded = JsonDocument.Parse(outcome.ResultJson!);
        Assert.Equal(3, recorded.RootElement.GetProperty("revision").GetInt64());
        Assert.False(recorded.RootElement.TryGetProperty("sha256", out var noDigest), noDigest.ToString());
    }

    [Fact]
    public void ASavedResultMissingAFieldIsRefusedByName()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var host = new FakeHost((_, arguments, _) => Answer(arguments, workspace =>
        {
            Directory.CreateDirectory(Path.Combine(workspace, "outputs"));
            File.WriteAllText(Path.Combine(workspace, "outputs", "results.json"), "{}");
            WriteReport(workspace,
                """
                {"schema":"fern.stage_runner_report.v1","outcome":{"kind":"success",
                 "output_paths":{"unit_results":"outputs/results.json"},
                 "saved_result":{"schema":"fern.saved_cell_result.v1","saved":true,
                                 "database":"/r/science.db","study":"worked"}}}
                """);
            return new BoundedProbe.Outcome.Completed(0, [], "");
        }));

        var outcome = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work) with { ResultsRoot = work }, host)
            .Run(Claimed(studyId, cellId, work), default);

        Assert.False(outcome.Completed);
        Assert.Contains("names no run", outcome.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other shape: the store would not take the document. The cell trained,
    /// so it is not a failure — the document is recorded as it always was, and
    /// the reason is written against the attempt where `monitor-study` shows it.
    /// </summary>
    [Fact]
    public void ACellWhoseStoreRefusedTheDocumentKeepsItAndSaysWhy()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        const string results = """{"schema":"fern.study_unit_results.v4","cell":"one","study":"worked"}""";
        var attempt = Claim(writer, cellId);
        var host = new FakeHost((_, arguments, _) => Answer(arguments, workspace =>
        {
            Directory.CreateDirectory(Path.Combine(workspace, "outputs"));
            File.WriteAllText(Path.Combine(workspace, "outputs", "results.json"), results);
            WriteReport(workspace,
                """
                {"schema":"fern.stage_runner_report.v1","outcome":{"kind":"success",
                 "output_paths":{"unit_results":"outputs/results.json"},
                 "saved_result":{"schema":"fern.saved_cell_result.v1","saved":false,
                                 "database":"/results/science.db",
                                 "reason":"science.db is on a read-only mount"}}}
                """);
            return new BoundedProbe.Outcome.Completed(0, [], "");
        }));

        var outcome = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work) with { ResultsRoot = work }, host).Run(
            Claimed(studyId, cellId, work) with { AttemptId = attempt.AttemptId, FencingEpoch = attempt.FencingEpoch },
            default);

        Assert.True(outcome.Completed);
        Assert.Equal(results, outcome.ResultJson);
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT kind, detail_json FROM events WHERE attempt_id = $id ORDER BY id DESC LIMIT 1";
        command.Parameters.AddWithValue("$id", attempt.AttemptId);
        using var row = command.ExecuteReader();
        Assert.True(row.Read(), "a cell whose result did not reach the store records why");
        Assert.Equal(StudyCollect.ResultNotSaved, row.GetString(0));
        Assert.Contains("read-only mount", row.GetString(1), StringComparison.Ordinal);
        Assert.Contains("result kept by the Home, store refused",
            CellFailureDetail.Sentence(row.GetString(1)), StringComparison.Ordinal);
    }

    [Fact]
    public void ASavedResultThatDoesNotSayWhetherItSavedIsRefused()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var host = new FakeHost((_, arguments, _) => Answer(arguments, workspace =>
        {
            Directory.CreateDirectory(Path.Combine(workspace, "outputs"));
            File.WriteAllText(Path.Combine(workspace, "outputs", "results.json"), "{}");
            WriteReport(workspace,
                """
                {"schema":"fern.stage_runner_report.v1","outcome":{"kind":"success",
                 "output_paths":{"unit_results":"outputs/results.json"},
                 "saved_result":{"schema":"fern.saved_cell_result.v1","database":"/r/science.db"}}}
                """);
            return new BoundedProbe.Outcome.Completed(0, [], "");
        }));

        var outcome = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work) with { ResultsRoot = work }, host)
            .Run(Claimed(studyId, cellId, work), default);

        Assert.False(outcome.Completed);
        Assert.Contains("does not say whether it was saved", outcome.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>A runtime that saved the cell's results and says where they went and what they hash to.</summary>
    static BoundedProbe.Outcome Saves(IReadOnlyList<string> arguments, string results, string sha) =>
        Answer(arguments, workspace =>
        {
            Directory.CreateDirectory(Path.Combine(workspace, "outputs"));
            File.WriteAllText(Path.Combine(workspace, "outputs", "results.json"), results);
            var reference = $"/results/science.db#study=worked&run=one&sha256={sha}";
            WriteReport(workspace, JsonSerializer.Serialize(new
            {
                schema = "fern.stage_runner_report.v1",
                outcome = new
                {
                    kind = "success",
                    output_paths = new Dictionary<string, string> { ["unit_results"] = "outputs/results.json" },
                    saved_result = new
                    {
                        schema = "fern.saved_cell_result.v1",
                        saved = true,
                        database = "/results/science.db",
                        study = "worked",
                        run = "one",
                        sha256 = sha,
                        reference,
                    },
                },
            }));
            return new BoundedProbe.Outcome.Completed(0, [], "");
        });

    static string Sha256Of(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ── the heartbeat ────────────────────────────────────────────────────────

    /// <summary>
    /// While the trainer runs, the supervisor says it is still there.
    ///
    /// Before this, `last_progress_at` was stamped at the claim and again at the
    /// end and never in between, so a six-hour cell and a supervisor killed one
    /// minute in looked the same to every reader — which is what made
    /// `retry-cell`'s silence bound unusable.
    /// </summary>
    [Fact]
    public void ASupervisorRenewsItsAttemptWhileTheTrainerRuns()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var attempt = Claim(writer, cellId);
        var database = writer.Connection.DataSource;
        var before = LastProgress(writer, attempt.AttemptId);
        var beat = false;
        var host = new FakeHost((_, arguments, _) =>
        {
            if (arguments.Contains("run"))
            {
                // Hold the "trainer" until the supervisor's beat lands, read
                // through a SECOND connection so this thread never touches the
                // one the beat is writing on. Waiting for the fact rather than
                // sleeping a guessed interval is what keeps this test from being
                // a timing race under a loaded suite.
                beat = WaitForBeat(database, attempt.AttemptId, before, TimeSpan.FromSeconds(20));
            }
            return Trains(arguments, "{}");
        });
        var worker = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host, TimeSpan.FromMilliseconds(50));

        Assert.True(worker.Run(Claimed(studyId, cellId, work) with
        {
            AttemptId = attempt.AttemptId,
            FencingEpoch = attempt.FencingEpoch,
        }, default).Completed);

        Assert.True(beat, "the supervisor never renewed its attempt while the trainer ran");
        Assert.NotEqual(before, LastProgress(writer, attempt.AttemptId));
        Assert.Equal("running", StateOf(writer, attempt.AttemptId));

        // The same fact under the attempt's own folder, so "is this alive" is a
        // read rather than a process hunt.
        var receipt = Path.Combine(host.Arguments[6], PrefixOf(host.Arguments[6]) + LocalCellWorker.HeartbeatFile);
        Assert.True(File.Exists(receipt), $"no heartbeat receipt at {receipt}");
        var beaten = JsonDocument.Parse(File.ReadAllText(receipt)).RootElement;
        Assert.Equal(LocalCellWorker.HeartbeatSchema, beaten.GetProperty("schema").GetString());
        Assert.Equal("one", beaten.GetProperty("cell").GetString());
        Assert.Equal(attempt.AttemptId, beaten.GetProperty("attempt_id").GetInt64());
        Assert.Equal(attempt.FencingEpoch, beaten.GetProperty("fencing_epoch").GetInt64());
        Assert.Equal(System.Environment.ProcessId, beaten.GetProperty("supervisor_pid").GetInt32());
        Assert.Equal(System.Environment.MachineName, beaten.GetProperty("supervisor_host").GetString());
        Assert.True(beaten.GetProperty("stale_after_seconds").GetInt64() > 0);
        // One file, overwritten: a beat is a current fact, not a history.
        Assert.Single(Directory.GetFiles(host.Arguments[6], "*.heartbeat.json"));
    }

    /// <summary>
    /// The card the Site granted reaches the trainer the way every CUDA program
    /// already expects to be told, so two cells on one machine land on two cards
    /// and nothing in the Python contract has to learn a new field.
    /// </summary>
    [Fact]
    public void TheGrantedCardIsTheOnlyOneTheTrainerCanSee()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var host = new FakeHost((_, arguments, _) => Trains(arguments, "{}"));

        new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host)
            .Run(Claimed(studyId, cellId, work) with { Device = "GPU-aaaa" }, default);

        Assert.Equal("GPU-aaaa", host.Environment["CUDA_VISIBLE_DEVICES"]);
    }

    /// <summary>A Site that declares no cards grants none, and the trainer sees what the machine gives it.</summary>
    [Fact]
    public void ASiteThatGrantsNoCardSetsNoVisibleDevices()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var host = new FakeHost((_, arguments, _) => Trains(arguments, "{}"));

        new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host).Run(Claimed(studyId, cellId, work), default);

        Assert.False(host.Environment.ContainsKey("CUDA_VISIBLE_DEVICES"));
    }

    /// <summary>
    /// Slice M1 of what-stops-work-and-who-lifts-it: the Site's compiler
    /// refusing a card is the machine's. An older runtime says `runner_failed`
    /// with the worker's sentence; a newer one says `compiler_refused_field`.
    /// Both are recorded the same way, class site with that code, which the
    /// claim reads for the whole source and leaves out of the attempt count.
    /// </summary>
    [Fact]
    public void ACompilerRefusalIsTheMachinesUnderOneCode()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        const string sentence =
            "the compiler refused the configuration: fern-compile identity: model.knobs.predictive_source: is not a field of ConditionalNsfKnobs";
        foreach (var code in new[] { "runner_failed", RegistryOperations.CodeCompilerRefusedField })
        {
            var host = new FakeHost((_, arguments, _) => Refuses(arguments, code, sentence));
            var detail = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host)
                .Run(Claimed(studyId, cellId, work), default).DetailJson;
            using var document = JsonDocument.Parse(detail!);
            Assert.Equal(CellFailureDetail.ClassSite, document.RootElement.GetProperty("failure_class").GetString());
            Assert.Equal(RegistryOperations.CodeCompilerRefusedField, document.RootElement.GetProperty("code").GetString());
        }
    }

    /// <summary>
    /// The reader's review of slice M1: the worker compares its compiler for the
    /// source with the one the study records. Another compiler: the Site's, and
    /// said so (the attempt then does not count). The study's own compiler: the
    /// card's, a deterministic failure for a person. None recorded: the Site's,
    /// with no claim either way (the attempt counts).
    /// </summary>
    [Fact]
    public void ACompilerRefusalIsTheSitesOnlyWhenItsCompilerDiffersFromTheStudys()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var compiler = Path.Combine(work, "fern-compile.site");
        File.WriteAllText(compiler, "the Site's compiler");
        var siteDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(compiler)));
        var placement = Placement(work) with { FernCompileBinary = compiler };
        const string sentence =
            "the compiler refused the configuration: fern-compile identity: model.knobs.predictive_source: is not a field of ConditionalNsfKnobs";
        JsonElement Run()
        {
            var host = new FakeHost((_, arguments, _) => Refuses(arguments, "runner_failed", sentence));
            var detail = new LocalCellWorker(new SqliteStudyStore(writer), placement, host)
                .Run(Claimed(studyId, cellId, work), default).DetailJson;
            return JsonDocument.Parse(detail!).RootElement.Clone();
        }

        var unknown = Run();
        Assert.Equal(CellFailureDetail.ClassSite, unknown.GetProperty("failure_class").GetString());
        Assert.False(unknown.TryGetProperty("compiler_differs", out var none), none.ToString());

        Assert.True(RegistryOperations.SetStudyCompiler(writer, studyId, new string('c', 64), "/elsewhere/fern-compile", "test").IsOk);
        var other = Run();
        Assert.Equal(RegistryOperations.CodeCompilerRefusedField, other.GetProperty("code").GetString());
        Assert.True(other.GetProperty("compiler_differs").GetBoolean());

        Assert.True(RegistryOperations.SetStudyCompiler(writer, studyId, siteDigest, compiler, "test").IsOk);
        var own = Run();
        Assert.Equal(CellFailureDetail.ClassDeterministic, own.GetProperty("failure_class").GetString());
        Assert.Equal("runner_failed", own.GetProperty("code").GetString());
    }

    /// <summary>
    /// A failure whose sentence is about the MACHINE is marked as the machine's,
    /// so the claim can send the cell elsewhere instead of spending its budget
    /// on the one host that cannot run it.
    /// </summary>
    [Fact]
    public void AFailureAboutTheMachineIsMarkedAsTheMachines()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var cuda = new FakeHost((_, arguments, _) =>
            Refuses(arguments, "runner_failed", "a study cell requires CUDA; no CPU fallback"));
        var card = new FakeHost((_, arguments, _) =>
            Refuses(arguments, "runner_failed", "dataset input bank is missing at /data/bank.safetensors"));
        var cell = new FakeHost((_, arguments, _) =>
            Refuses(arguments, "runner_failed", "use: unknown dataset 'pointmaze.physical_state'"));

        Assert.True(CellFailureDetail.IsSiteSpecific(
            new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), cuda).Run(Claimed(studyId, cellId, work), default).DetailJson));
        Assert.True(CellFailureDetail.IsSiteSpecific(
            new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), card).Run(Claimed(studyId, cellId, work), default).DetailJson));
        Assert.False(CellFailureDetail.IsSiteSpecific(
            new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), cell).Run(Claimed(studyId, cellId, work), default).DetailJson),
            "an unknown dataset is the card's problem, not the machine's");
    }

    /// <summary>The cadence the owner set, and the bound that is ten of them.</summary>
    [Fact]
    public void TheCadenceIsTwoMinutesAndTheBoundIsTenBeats()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), LocalCellWorker.HeartbeatInterval);
        Assert.Equal(LocalCellWorker.HeartbeatInterval * 10, RegistryOperations.StaleAfter);
    }

    /// <summary>Watch one attempt's last progress through its own connection until it moves.</summary>
    static bool WaitForBeat(string database, long attemptId, string? before, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={database};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT last_progress_at FROM attempts WHERE id = $id";
            command.Parameters.AddWithValue("$id", attemptId);
            if (command.ExecuteScalar() as string != before)
            {
                return true;
            }
            Thread.Sleep(10);
        }
        return false;
    }

    /// <summary>
    /// And it stops when the run does: the beat is the supervisor's own
    /// liveness, so a supervisor that has returned — or died — renews nothing,
    /// which is what lets `retry-cell` tell a stranded cell from a busy one.
    /// </summary>
    [Fact]
    public void TheHeartbeatStopsWhenTheRunDoes()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var attempt = Claim(writer, cellId);
        var host = new FakeHost((_, arguments, _) => Trains(arguments, "{}"));
        var worker = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host, TimeSpan.FromMilliseconds(20));

        worker.Run(Claimed(studyId, cellId, work) with
        {
            AttemptId = attempt.AttemptId,
            FencingEpoch = attempt.FencingEpoch,
        }, default);

        var settled = LastProgress(writer, attempt.AttemptId);
        Thread.Sleep(TimeSpan.FromMilliseconds(200));
        Assert.Equal(settled, LastProgress(writer, attempt.AttemptId));
    }

    static RegistryOperations.StartedAttempt Claim(RegistryWriter writer, long cellId)
    {
        var started = RegistryOperations.TryStartAttempt(writer, cellId, 1, "{}");
        Assert.True(started.TryGetValue(out var attempt));
        return attempt;
    }

    static string? LastProgress(RegistryWriter writer, long attemptId) =>
        Column(writer, attemptId, "last_progress_at");

    static string? StateOf(RegistryWriter writer, long attemptId) =>
        Column(writer, attemptId, "state");

    static string? Column(RegistryWriter writer, long attemptId, string column)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM attempts WHERE id = $id";
        command.Parameters.AddWithValue("$id", attemptId);
        return command.ExecuteScalar() as string;
    }

    /// <summary>The probe is asked once and its answer reused, so a study of many cells starts one extra process.</summary>
    [Fact]
    // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): the answer was
    // remembered per installed source root; a working tree changes between attempts
    // and a commit directory is made per attempt, so each attempt asks.
    public void TheEnvironmentIsAskedWhatItImplementsForEachAttempt()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var host = new FakeHost((_, arguments, _) => Trains(arguments, "{}"));
        var worker = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host);

        worker.Run(Claimed(studyId, cellId, work), default);
        worker.Run(Claimed(studyId, cellId, work) with { AttemptId = 8 }, default);

        Assert.Equal(2, host.Runs);
        Assert.Equal(4, host.Calls);
        Assert.Equal(2, Directory.GetFiles(work, "*.results.json").Length);
        Assert.DoesNotContain(Directory.GetDirectories(work), path => Path.GetFileName(path) == "one");
    }

    // ── what the worker refuses before it starts anything ────────────────────

    /// <summary>
    /// AGENTS.md rule 1: unless the owner said cpu, a cell trains on the GPU.
    /// The worker refuses BEFORE it starts a process, and the refusal names the
    /// field, because the failure of a silent CPU run is a day of wrong numbers
    /// nobody notices.
    /// </summary>
    [Fact]
    public void ACellThatNamesTheCpuIsRefusedWithoutStartingAProcess()
    {
        var (writer, studyId, cellId, work) = Home(CpuConfig);
        using var _ = writer;
        var host = new FakeHost((_, _, _) => throw new InvalidOperationException("no process may start"));

        var outcome = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host).Run(Claimed(studyId, cellId, work), default);

        Assert.False(outcome.Completed);
        Assert.Equal(0, host.Calls);
        Assert.Contains("runtime.device=cpu", outcome.DetailJson, StringComparison.Ordinal);
        Assert.Contains("rule 1", outcome.DetailJson, StringComparison.Ordinal);
        Assert.True(CellFailureDetail.IsDeterministic(outcome.DetailJson));
    }

    [Fact]
    public void ACellThatNamesNoDeviceIsRefused()
    {
        Assert.Contains("names no runtime.device",
            LocalCellWorker.CpuRefusal("""{"schema":"fern.training.v2"}""") ?? "", StringComparison.Ordinal);
        Assert.Null(LocalCellWorker.CpuRefusal(CudaConfig));
    }

    /// <summary>
    /// The seed is stored twice — as a column on the cell and inside the
    /// compiled study — and a worker that found them disagreeing would have to
    /// choose which one the result was labelled with. It refuses instead.
    /// </summary>
    [Fact]
    public void ASeedTheRegistryAndTheCompiledStudyDisagreeOnIsRefusedByName()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig, registeredSeed: 42);
        using var _ = writer;
        var host = new FakeHost((_, arguments, _) => Trains(arguments, "{}"));

        var outcome = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host).Run(Claimed(studyId, cellId, work), default);

        Assert.False(outcome.Completed);
        Assert.Equal(0, host.Runs);
        Assert.Contains("registered at seed 42 and compiled at seed 43", outcome.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>A cell the compiled study does not carry cannot be assembled, and says so by name.</summary>
    [Fact]
    public void ACellTheCompiledStudyDoesNotCarryIsRefusedByName()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var host = new FakeHost((_, arguments, _) => Trains(arguments, "{}"));

        var outcome = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host)
            .Run(Claimed(studyId, cellId, work) with { CellName = "elsewhere" }, default);

        Assert.False(outcome.Completed);
        Assert.Contains("no cell named elsewhere", outcome.DetailJson, StringComparison.Ordinal);
    }

    // ── what a failure looks like ────────────────────────────────────────────

    /// <summary>
    /// A refusal the runtime wrote travels whole: its kind, its code and its
    /// message, beside the sentence a monitor prints. On 2026-09-11 thirteen
    /// cells were refused with "unknown dataset" and every reader said "failed".
    /// </summary>
    [Fact]
    public void ARefusedCellCarriesTheRuntimesOwnWords()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var host = new FakeHost((_, arguments, _) =>
            Refuses(arguments, "runner_failed", "use: unknown dataset 'pointmaze.physical_state'; datasets: []"));

        var outcome = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host).Run(Claimed(studyId, cellId, work), default);

        Assert.False(outcome.Completed);
        var detail = JsonDocument.Parse(outcome.DetailJson).RootElement;
        Assert.Equal("failure", detail.GetProperty("outcome_kind").GetString());
        Assert.Equal("runner_failed", detail.GetProperty("code").GetString());
        Assert.Contains("unknown dataset", detail.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Contains("unknown dataset", CellFailureDetail.Sentence(outcome.DetailJson), StringComparison.Ordinal);
    }

    /// <summary>
    /// A request the runtime will not accept is the same request next time, so
    /// it is marked as one that would repeat; a failure inside the handler is
    /// not, because this side cannot tell a refused dataset from a GPU that
    /// fell over.
    /// </summary>
    [Fact]
    public void ARefusedRequestSaysItWouldRepeatAndAFailedRunDoesNot()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var invalid = new FakeHost((_, arguments, _) => Refuses(arguments, "invalid_request", "request schema is not fern.stage_runner_request.v1"));
        var broke = new FakeHost((_, arguments, _) => Refuses(arguments, "runner_failed", "CUDA out of memory"));

        Assert.True(CellFailureDetail.IsDeterministic(
            new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), invalid).Run(Claimed(studyId, cellId, work), default).DetailJson));
        Assert.False(CellFailureDetail.IsDeterministic(
            new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), broke).Run(Claimed(studyId, cellId, work), default).DetailJson));
    }

    /// <summary>A run that left no report is a failure that hands the reader the process's own last words.</summary>
    [Fact]
    public void ARunThatLeftNoReportCarriesTheStderrTail()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var host = new FakeHost((_, arguments, _) =>
            Answer(arguments, _ => new BoundedProbe.Outcome.Completed(1, [], "ModuleNotFoundError: No module named 'fern_stage_runtime'")));

        var outcome = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host).Run(Claimed(studyId, cellId, work), default);

        Assert.False(outcome.Completed);
        Assert.Contains("left no report", outcome.DetailJson, StringComparison.Ordinal);
        Assert.Contains("ModuleNotFoundError",
            JsonDocument.Parse(outcome.DetailJson).RootElement.GetProperty("stderr_tail").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>A report that claims success without the unit's results is not a completed cell.</summary>
    [Fact]
    public void ASuccessWithoutResultsIsAFailure()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var host = new FakeHost((_, arguments, _) => Answer(arguments, workspace =>
        {
            WriteReport(workspace,
                """{"schema":"fern.stage_runner_report.v1","outcome":{"kind":"success","output_paths":{}}}""");
            return new BoundedProbe.Outcome.Completed(0, [], "");
        }));

        var outcome = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host).Run(Claimed(studyId, cellId, work), default);

        Assert.False(outcome.Completed);
        Assert.Contains("without its unit_results", outcome.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// Slice W3 (owner, 2026-09-26: a training's time budget only warns, "直接杀
    /// 真的不行"): the trainer is given no deadline, whatever the Site's
    /// --cell-timeout-seconds; the Home warns once past 24 hours instead.
    /// </summary>
    [Fact]
    public void TheTrainerIsGivenNoDeadline()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var host = new FakeHost((_, arguments, _) => Answer(arguments, _ => new BoundedProbe.Outcome.Cancelled(1)));

        new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host).Run(Claimed(studyId, cellId, work), default);

        Assert.Equal(1, host.Runs);
        Assert.Equal(LocalCellWorker.NoDeadlineMs, host.RunTimeoutMs);
    }

    [Fact]
    public void ATimeoutSaysHowLongItWaited()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var host = new FakeHost((_, arguments, _) => Answer(arguments, _ => new BoundedProbe.Outcome.TimedOut(1234)));

        var outcome = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host).Run(Claimed(studyId, cellId, work), default);

        Assert.False(outcome.Completed);
        Assert.Contains("1234 ms", outcome.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>An environment that cannot say what it implements runs no cell, and the refusal names it.</summary>
    [Fact]
    public void AnEnvironmentThatCannotReportItselfRunsNoCell()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var host = new FakeHost((_, _, _) => new BoundedProbe.Outcome.Missing("/home/user/.venv/bin/python"));

        var outcome = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host).Run(Claimed(studyId, cellId, work), default);

        Assert.False(outcome.Completed);
        Assert.Equal(0, host.Runs);
        Assert.Contains("could not report what it implements", outcome.DetailJson, StringComparison.Ordinal);
    }

    // ── slice 2 of the cell port: a cancelled study reaches the trainer ───────

    /// <summary>
    /// A host whose `run` blocks until the caller cancels it, the way a trainer
    /// holds the GPU until it is killed. The probe still answers at once.
    /// </summary>
    sealed class BlockingHost : WorkerEnvironmentPreparation.ICancellableHost
    {
        public bool IsLinux => true;
        public bool WasCancelled { get; private set; }

        public BoundedProbe.Outcome Run(string program, IReadOnlyList<string> arguments, long timeoutMs, long maxBytes,
            string workingDirectory, IReadOnlyDictionary<string, string> environment) =>
            Run(program, arguments, timeoutMs, maxBytes, workingDirectory, environment, CancellationToken.None);

        public BoundedProbe.Outcome Run(string program, IReadOnlyList<string> arguments, long timeoutMs, long maxBytes,
            string workingDirectory, IReadOnlyDictionary<string, string> environment, CancellationToken cancellation)
        {
            if (arguments.Contains("probe"))
            {
                return Answer(arguments, _ => throw new InvalidOperationException("probe only"));
            }
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
            {
                WasCancelled = true;
                return new BoundedProbe.Outcome.Cancelled(stopwatch.ElapsedMilliseconds);
            }
            return new BoundedProbe.Outcome.TimedOut(timeoutMs);
        }
    }

    /// <summary>
    /// The retired daemon cancelled into its live session; this supervisor
    /// cancels into the process it owns. A study asked to cancel while its cell
    /// trains reaches the trainer at the next beat: the host is told to end the
    /// process, and the attempt ends as cancelled, a person's decision that the
    /// machine does not retry.
    /// </summary>
    [Fact]
    public void ACancelledStudyReachesTheTrainerAtTheNextBeatAndTheAttemptEndsCancelled()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        using var _ = writer;
        var started = RegistryOperations.TryStartAttempt(writer, cellId, 1, """{"schema":"fern.cell_request.v2","cell":"one"}""");
        Assert.True(started.TryGetValue(out var attempt));
        Assert.True(RegistryOperations.RequestStudyState(writer, studyId, RegistryOperations.StateCancel).IsOk);
        var host = new BlockingHost();
        var worker = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host, heartbeat: TimeSpan.FromMilliseconds(50));
        var claimed = new StudyOrchestrator.ClaimedCell(studyId, cellId, "one", attempt.AttemptId, attempt.FencingEpoch, 1, "{}", null, work, RegistryOperations.CodeFromWorkingTree, 1);

        var outcome = worker.Run(claimed, default);

        Assert.True(host.WasCancelled);
        Assert.False(outcome.Completed);
        Assert.Equal(RegistryOperations.AttemptCancelled, outcome.EndState);
        Assert.Equal(CellFailureDetail.ClassHuman, CellFailureDetail.ClassOf(outcome.DetailJson));
        Assert.Contains("cancelled while the cell trained", CellFailureDetail.Sentence(outcome.DetailJson), StringComparison.Ordinal);
    }

    // ── slice 4 of the cell port: the warm resident worker by its folder inbox ──

    /// <summary>
    /// A resident worker that answers with files, the way the real one does:
    /// ensure publishes starting then ready, submit files the request under
    /// inbox/ and runs the script the test gave it, which writes receipts.
    /// </summary>
    sealed class FakeResident(Action<string, string, string> serve, bool alive = true, Fern.Site.SiteFailure? admission = null)
        : IResidentWorkerHost
    {
        public bool Killed { get; private set; }
        public IReadOnlyDictionary<string, string> Environment { get; private set; } = new Dictionary<string, string>();
        public string? Root { get; private set; }

        public Fern.Site.SiteResult<Fern.Site.ResidentPythonWorker.WorkerIdentity> Ensure(
            string root, string python, string sourceRoot, TimeSpan startDeadline,
            Action<Fern.Site.ResidentPythonWorker.WorkerStartupObservation>? observeStartup,
            IReadOnlyDictionary<string, string> environment)
        {
            Root = root;
            Environment = environment;
            if (admission is not null)
            {
                return Fern.Site.SiteResult<Fern.Site.ResidentPythonWorker.WorkerIdentity>.Refused(admission);
            }
            var identity = new Fern.Site.ResidentPythonWorker.WorkerIdentity(
                System.Environment.ProcessId, 1, "2026-09-11T00:00:00Z", python, sourceRoot, "sha256:cap", "starting",
                "launch-1", Fern.Site.ResidentPythonWorker.WorkerLifetimeLockProtocol, root, null);
            observeStartup?.Invoke(new Fern.Site.ResidentPythonWorker.WorkerStartupObservation("starting", identity, 10));
            observeStartup?.Invoke(new Fern.Site.ResidentPythonWorker.WorkerStartupObservation("ready", identity with { State = "ready" }, 70_000));
            return Fern.Site.SiteResult<Fern.Site.ResidentPythonWorker.WorkerIdentity>.Ok(identity with { State = "ready" });
        }

        public Fern.Site.SiteResult<string> Submit(string root, string cellRequestJson)
        {
            var inbox = Path.Combine(root, Fern.Site.ResidentPythonWorker.InboxDirectory);
            Directory.CreateDirectory(inbox);
            var name = Fern.Site.ResidentPythonWorker.SequenceName(1);
            File.WriteAllText(Path.Combine(inbox, name), cellRequestJson);
            using var document = JsonDocument.Parse(cellRequestJson);
            var cellId = document.RootElement.GetProperty("cell_id").GetString()!;
            var workspace = document.RootElement.GetProperty("workspace").GetString()!;
            serve(root, cellId, workspace);
            return Fern.Site.SiteResult<string>.Ok(name);
        }

        public bool IsAlive(Fern.Site.ResidentPythonWorker.WorkerIdentity identity) => alive && !Killed;

        public Fern.Site.SiteResult<bool> Kill(Fern.Site.ResidentPythonWorker.WorkerIdentity identity)
        {
            Killed = true;
            return Fern.Site.SiteResult<bool>.Ok(true);
        }
    }

    static string ReceiptsOf(string root, string cellId) =>
        Path.Combine(root, Fern.Site.ResidentPythonWorker.ReceiptsDirectory);

    static void Finish(string root, string cellId, string workspace, bool success, string? results = null)
    {
        var receipts = ReceiptsOf(root, cellId);
        Directory.CreateDirectory(receipts);
        if (success)
        {
            Directory.CreateDirectory(Path.Combine(workspace, "outputs"));
            File.WriteAllText(Path.Combine(workspace, "outputs", "results.json"), results ?? """{"schema":"fern.study_unit_results.v4"}""");
            WriteReport(workspace,
                """
                {"schema":"fern.stage_runner_report.v1","stage_attempt_id":"attempt-7",
                 "outcome":{"kind":"success","output_paths":{"unit_results":"outputs/results.json"}}}
                """);
            File.WriteAllText(Path.Combine(receipts, cellId + ".done"), "2026-09-11T00:00:01Z\n");
        }
        else
        {
            File.WriteAllText(Path.Combine(receipts, cellId + ".failed.json"),
                """{"schema":"fern.phase_receipt.v1.failed","cell_id":"x","ordinal":0,"handler":"fern.study.run_cell","code":"worker_refused","message":"no CUDA device is visible to this worker","log":"/tmp/x.txt"}""");
        }
    }

    static (RegistryWriter Writer, StudyOrchestrator.ClaimedCell Cell, string Work) WarmHome()
    {
        var (writer, studyId, cellId, work) = Home(CudaConfig);
        var started = RegistryOperations.TryStartAttempt(writer, cellId, 1, """{"schema":"fern.cell_request.v2","cell":"one"}""");
        Assert.True(started.TryGetValue(out var attempt));
        return (writer, new StudyOrchestrator.ClaimedCell(studyId, cellId, "one", attempt.AttemptId, attempt.FencingEpoch, 1, "{}", "GPU-1111", work, RegistryOperations.CodeFromWorkingTree, 1), work);
    }

    static LocalCellWorker Warm(RegistryWriter writer, string work, FakeResident resident, TimeSpan? heartbeat = null) =>
        new(new SqliteStudyStore(writer), Placement(work) with { WarmWorker = true, Timeout = TimeSpan.FromSeconds(5) },
            new FakeHost((_, arguments, _) => Answer(arguments, _ => throw new InvalidOperationException("the warm route runs no process per cell"))),
            heartbeat ?? TimeSpan.FromMilliseconds(50), resident);

    static IReadOnlyList<(string Kind, string Detail)> Events(RegistryWriter writer, long attemptId, string kind)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT kind, detail_json FROM events WHERE attempt_id = $attempt AND kind = $kind ORDER BY id";
        command.Parameters.AddWithValue("$attempt", attemptId);
        command.Parameters.AddWithValue("$kind", kind);
        var rows = new List<(string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    [Fact]
    public void TheWarmRouteAdmitsTheWorkerSubmitsOneCellAndReadsItsReportWhereThePerCellRouteDoes()
    {
        var (writer, cell, work) = WarmHome();
        using var _ = writer;
        var resident = new FakeResident((root, cellId, workspace) => Finish(root, cellId, workspace, success: true));

        var outcome = Warm(writer, work, resident).Run(cell, default);

        Assert.True(outcome.Completed, outcome.DetailJson);
        Assert.Equal("""{"schema":"fern.study_unit_results.v4"}""", outcome.ResultJson);
        Assert.Equal(Path.Combine(work, "workers"), Path.GetDirectoryName(Path.GetDirectoryName(resident.Root!)));
        Assert.Equal("GPU-1111", resident.Environment["CUDA_VISIBLE_DEVICES"]);
        Assert.Equal("/opt/fern-compile", resident.Environment["FERN_COMPILE_BIN"]);
        Assert.True(File.Exists(Path.Combine(resident.Root!, "inbox", "000001.json")));
        var startup = Events(writer, cell.AttemptId, LocalCellWorker.WorkerStartupEvent);
        Assert.Equal(2, startup.Count);
        Assert.Contains("\"state\":\"ready\"", startup[1].Detail, StringComparison.Ordinal);
        Assert.Contains("past the 60 s warning bound", startup[1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedPhaseIsReadFromTheWorkersFailedReceipt()
    {
        var (writer, cell, work) = WarmHome();
        using var _ = writer;
        var resident = new FakeResident((root, cellId, workspace) => Finish(root, cellId, workspace, success: false));

        var outcome = Warm(writer, work, resident).Run(cell, default);

        Assert.False(outcome.Completed);
        Assert.Contains("worker_refused: no CUDA device is visible", CellFailureDetail.Sentence(outcome.DetailJson), StringComparison.Ordinal);
        Assert.Equal(CellFailureDetail.ClassSite, CellFailureDetail.ClassOf(outcome.DetailJson));
    }

    [Fact]
    public void ProgressLinesInTheCellsLogBecomeMonotoneProgressEvents()
    {
        var (writer, cell, work) = WarmHome();
        using var _ = writer;
        var resident = new FakeResident((root, cellId, workspace) =>
        {
            var log = Fern.Site.ResidentPythonWorker.CellLogPath(root, cellId);
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.WriteAllText(log,
                "some trainer chatter\n"
                + "FERN_TRAIN_PROGRESS {\"schema\":\"fern.training_progress.v1\",\"kind\":\"training_epoch\",\"current\":1,\"total\":3,\"terminal\":false}\n"
                + "FERN_TRAIN_PROGRESS {\"schema\":\"fern.training_progress.v1\",\"kind\":\"training_epoch\",\"current\":1,\"total\":3,\"terminal\":false}\n"
                + "FERN_TRAIN_PROGRESS {\"schema\":\"fern.training_progress.v1\",\"kind\":\"training_epoch\",\"current\":3,\"total\":3,\"terminal\":true}\n"
                + "FERN_TRAIN_PROGRESS {\"schema\":\"fern.training_progress.v1\",\"kind\":\"training_epoch\",\"current\":2,\"total\":3,\"terminal\":false}\n");
            Finish(root, cellId, workspace, success: true);
        });

        var outcome = Warm(writer, work, resident).Run(cell, default);

        Assert.True(outcome.Completed, outcome.DetailJson);
        var progress = Events(writer, cell.AttemptId, UnfinishedCell.TrainingProgressEvent);
        Assert.Equal(2, progress.Count);
        Assert.Contains("\"current\":1", progress[0].Detail, StringComparison.Ordinal);
        Assert.Contains("\"current\":3", progress[1].Detail, StringComparison.Ordinal);
        Assert.Contains("\"terminal\":true", progress[1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ACancelledStudyEndsTheWorkerLifetimeAndTakesTheCellOffItsQueue()
    {
        var (writer, cell, work) = WarmHome();
        using var _ = writer;
        Assert.True(RegistryOperations.RequestStudyState(writer, cell.StudyId, RegistryOperations.StateCancel).IsOk);
        var resident = new FakeResident((_, _, _) => { });

        var outcome = Warm(writer, work, resident).Run(cell, default);

        Assert.True(resident.Killed);
        Assert.Equal(RegistryOperations.AttemptCancelled, outcome.EndState);
        Assert.False(File.Exists(Path.Combine(resident.Root!, "inbox", "000001.json")));
        Assert.True(File.Exists(Path.Combine(ReceiptsOf(resident.Root!, LocalCellWorker.WarmCellId(cell)), LocalCellWorker.WarmCellId(cell) + ".request-000001.json")));
    }

    [Fact]
    public void AWorkerThatDiesMidCellIsATransientFailureAndTheCellLeavesItsQueue()
    {
        var (writer, cell, work) = WarmHome();
        using var _ = writer;
        var resident = new FakeResident((_, _, _) => { }, alive: false);

        var outcome = Warm(writer, work, resident).Run(cell, default);

        Assert.False(outcome.Completed);
        Assert.Null(outcome.EndState);
        Assert.Equal(CellFailureDetail.ClassTransient, CellFailureDetail.ClassOf(outcome.DetailJson));
        Assert.Contains("died while the cell ran", CellFailureDetail.Sentence(outcome.DetailJson), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(resident.Root!, "inbox", "000001.json")));
    }

    [Fact]
    public void AWorkerStillStartingPastTheDeadlineIsAWorkerlessSiteFailureBeforeExecution()
    {
        var (writer, cell, work) = WarmHome();
        using var _ = writer;
        var resident = new FakeResident((_, _, _) => { },
            admission: Fern.Site.SiteFailure.WorkerAdmissionDeadline("launch-1 remained starting; process_liveness=alive"));

        var outcome = Warm(writer, work, resident).Run(cell, default);

        Assert.False(outcome.Completed);
        Assert.Equal(CellFailureDetail.ClassSite, CellFailureDetail.ClassOf(outcome.DetailJson));
        Assert.Contains("did not become ready within 300 s", CellFailureDetail.Sentence(outcome.DetailJson), StringComparison.Ordinal);
        Assert.Contains("\"before_execution\":true", outcome.DetailJson, StringComparison.Ordinal);
    }

    // ── where the cell's code comes from (docs/programs/2026-09-26-source-roots-stop-being-full-copies, slice 2) ──

    const string MarkerPath = "python/fern_train/marker.py";

    /// <summary>
    /// A git repository at one commit holding the declared files, a Site that
    /// declares <paramref name="codeFrom"/> with that repository, and one
    /// registered cell. No installed source is recorded for the Site.
    /// </summary>
    static (RegistryWriter Writer, long StudyId, long CellId, long SiteId, string Work, string Repository, string Commit) CodeHome(
        string codeFrom)
    {
        var repository = Directory.CreateTempSubdirectory("fern-cell-code-repository-").FullName;
        TestRegistry.Git(repository, "init", "--quiet");
        TestRegistry.Git(repository, "config", "user.email", "test@example.invalid");
        TestRegistry.Git(repository, "config", "user.name", "cell code test");
        var files = new[] { ("serie/tests/fixtures/bank.safetensors", "bank"), (MarkerPath, "registered") };
        foreach (var (path, contents) in files)
        {
            var full = Path.Combine(repository, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, contents);
            TestRegistry.Git(repository, "add", "--", path);
        }
        TestRegistry.Git(repository, "commit", "--quiet", "-m", "registered");
        var commit = TestRegistry.Git(repository, "rev-parse", "--verify", "HEAD^{commit}").Trim();
        var declared = "[" + string.Join(",", files.Select(file =>
            $$"""{"path":"{{file.Item1}}","kind":"regular_file","byte_digest":"sha256:{{Sha256Of(file.Item2)}}","bytes":{{Encoding.UTF8.GetByteCount(file.Item2)}}}""")) + "]";

        var writer = RegistryHome.OpenWriter(TestRegistry.CreateRegistry());
        long sourceId;
        long siteId;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
                VALUES ($commit, $declared, 'commit', '2026-09-26') RETURNING id
                """;
            command.Parameters.AddWithValue("$commit", commit);
            command.Parameters.AddWithValue("$declared", declared);
            sourceId = Convert.ToInt64(command.ExecuteScalar());
        }
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sites (name, capabilities_json, capacity_json, created_at)
                VALUES ('local-4070', $capabilities, '{"slots":2}', '2026-09-26') RETURNING id
                """;
            command.Parameters.AddWithValue("$capabilities", new JsonObject
            {
                ["device"] = "cuda",
                ["admitted_models"] = new JsonArray("fern"),
                ["code_from"] = codeFrom,
                ["repository"] = repository,
            }.ToJsonString());
            siteId = Convert.ToInt64(command.ExecuteScalar());
        }
        var registered = RegistryOperations.RegisterStudy(
            writer, "worked", 1, CompiledStudy(CudaConfig).Replace(DatasetDigest, Sha256Of("bank")), sourceId,
            [new RegistryOperations.CompiledCell(0, "one", "unit.controlled", 43, CudaConfig,
                """{"device":"cuda","requires_cuda":true}""",
                """{"unit_results":"retained"}""")]);
        Assert.True(registered.TryGetValue(out var value), registered.IsOk ? "" : "registration refused");
        var work = Directory.CreateTempSubdirectory("fern-cell-code-work-").FullName;
        return (writer, value.StudyId, value.CellIds[0], siteId, work, repository, commit);
    }

    static StudyOrchestrator.ClaimedCell ClaimedFrom(long studyId, long cellId, long siteId, RegistryOperations.StartedAttempt attempt, long sourceId) =>
        new(studyId, cellId, "one", attempt.AttemptId, attempt.FencingEpoch, siteId, "{}", attempt.Device,
            attempt.SourceRoot, attempt.CodeFrom, sourceId);

    static JsonObject CellCodeEvent(RegistryWriter writer, long attemptId)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT phase, detail_json FROM events WHERE attempt_id = $id AND kind = 'cell_code'";
        command.Parameters.AddWithValue("$id", attemptId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), "the attempt records where its code came from");
        var detail = JsonNode.Parse(reader.GetString(1))!.AsObject();
        Assert.Equal(reader.GetString(0), detail["code_from"]!.GetValue<string>());
        return detail;
    }

    /// <summary>
    /// Claim 1: a working-tree or commit Site is claimed without an installed
    /// source, and the attempt carries the repository and where the code comes from.
    /// Before slice 2 this claim was refused with "does not hold source".
    /// </summary>
    [Theory]
    [InlineData(RegistryOperations.CodeFromWorkingTree)]
    [InlineData(RegistryOperations.CodeFromCommit)]
    public void ASiteThatTakesCodeFromItsRepositoryIsClaimedWithoutAnInstalledSource(string codeFrom)
    {
        var (writer, _, cellId, siteId, _, repository, _) = CodeHome(codeFrom);
        using var scope = writer;
        var started = RegistryOperations.TryStartAttempt(writer, cellId, siteId, "{}");
        Assert.True(started.TryGetValue(out var attempt), started.IsOk ? "" : "refused");
        Assert.Equal(repository, attempt.SourceRoot);
        Assert.Equal(codeFrom, attempt.CodeFrom);
    }

    /// <summary>
    /// Claim 2, after slice 4: installed sources are retired, so a Site that
    /// declares no code_from is refused by name at the claim.
    /// </summary>
    [Fact]
    public void ASiteThatDeclaresNoCodeFromIsRefusedAtTheClaim()
    {
        var (writer, _, cellId, siteId, _, _, _) = CodeHome(RegistryOperations.CodeFromCommit);
        using var scope = writer;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText = "UPDATE sites SET capabilities_json = json_remove(capabilities_json, '$.code_from') WHERE id = $id";
            command.Parameters.AddWithValue("$id", siteId);
            command.ExecuteNonQuery();
        }
        var started = RegistryOperations.TryStartAttempt(writer, cellId, siteId, "{}");
        Assert.False(started.IsOk);
        started.TryGetRefusal(out var refusal);
        Assert.Contains("declares no code_from", refusal.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Claim 3: under commit, the trainer runs in a directory holding the
    /// registered commit's bytes, not a later commit's, and that directory is
    /// gone once the attempt ends.
    /// </summary>
    [Fact]
    public void UnderCommitTheTrainerRunsTheRegisteredCommitAndItsDirectoryIsGoneAfter()
    {
        var (writer, studyId, cellId, siteId, work, repository, commit) = CodeHome(RegistryOperations.CodeFromCommit);
        using var scope = writer;
        File.WriteAllText(Path.Combine(repository, MarkerPath), "committed later");
        TestRegistry.Git(repository, "commit", "--quiet", "-am", "later");
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, siteId, "{}").TryGetValue(out var attempt));
        string? seen = null;
        var host = new FakeHost((_, arguments, environment) =>
        {
            if (arguments.Contains("run"))
            {
                seen = File.ReadAllText(Path.Combine(environment["FERN_SOURCE_ROOT"], MarkerPath));
            }
            return Trains(arguments, """{"schema":"fern.unit_results.v1"}""");
        });
        var worker = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host);
        var sourceId = RegistryOperations.ReadStudy(writer.Connection, studyId).TryGetValue(out var study) ? study.SourceId : 0;
        worker.Run(ClaimedFrom(studyId, cellId, siteId, attempt, sourceId), default);

        Assert.Equal("registered", seen);
        Assert.Equal(CellCode.AttemptDirectory(work, attempt.AttemptId), host.WorkingDirectory);
        Assert.False(Directory.Exists(host.WorkingDirectory), "the directory made for the attempt is deleted when it ends");
        var detail = CellCodeEvent(writer, attempt.AttemptId);
        Assert.Equal(commit, detail["registered_commit"]!.GetValue<string>());
        Assert.Equal(2, detail["declared_files"]!.GetValue<int>());
    }

    /// <summary>
    /// Claim 4: under working-tree, the trainer runs in the repository as it is,
    /// and the attempt names the declared files that differ from the registered
    /// commit and keeps the difference beside its other files.
    /// </summary>
    [Fact]
    public void UnderWorkingTreeTheTrainerRunsTheRepositoryAndTheAttemptNamesWhatDiffers()
    {
        var (writer, studyId, cellId, siteId, work, repository, commit) = CodeHome(RegistryOperations.CodeFromWorkingTree);
        using var scope = writer;
        File.WriteAllText(Path.Combine(repository, MarkerPath), "edited, not committed");
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, siteId, "{}").TryGetValue(out var attempt));
        string? seen = null;
        var host = new FakeHost((_, arguments, environment) =>
        {
            if (arguments.Contains("run"))
            {
                seen = File.ReadAllText(Path.Combine(environment["FERN_SOURCE_ROOT"], MarkerPath));
            }
            return Trains(arguments, """{"schema":"fern.unit_results.v1"}""");
        });
        var worker = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host);
        var sourceId = RegistryOperations.ReadStudy(writer.Connection, studyId).TryGetValue(out var study) ? study.SourceId : 0;
        worker.Run(ClaimedFrom(studyId, cellId, siteId, attempt, sourceId), default);

        Assert.Equal("edited, not committed", seen);
        Assert.Equal(repository, host.WorkingDirectory);
        Assert.True(Directory.Exists(repository), "the working tree is never deleted");
        var detail = CellCodeEvent(writer, attempt.AttemptId);
        Assert.Equal(commit, detail["registered_commit"]!.GetValue<string>());
        Assert.Equal(commit, detail["head"]!.GetValue<string>());
        Assert.Equal([MarkerPath], detail["differing_files"]!.AsArray().Select(path => path!.GetValue<string>()));
        Assert.Contains("edited, not committed", File.ReadAllText(detail["difference"]!.GetValue<string>()), StringComparison.Ordinal);
    }

    /// <summary>
    /// Claim 5: a commit the repository does not hold is not claimed; the cell
    /// is reported unplaced with the commit, and no attempt is opened.
    /// </summary>
    [Fact]
    public void ACommitTheRepositoryDoesNotHoldIsNotClaimed()
    {
        var (writer, studyId, _, _, work, _, _) = CodeHome(RegistryOperations.CodeFromCommit);
        using var scope = writer;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText = "UPDATE sources SET commit_sha = 'dddddddddddddddddddddddddddddddddddddddd'";
            command.ExecuteNonQuery();
        }
        var host = new FakeHost((_, arguments, _) => Trains(arguments, """{"schema":"fern.unit_results.v1"}"""));
        var worker = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host);
        var report = StudyOrchestrator.RunOnce(new SqliteStudyStore(writer), worker, studyId, siteName: "local-4070");

        Assert.Equal(0, report.Claimed);
        Assert.Contains(report.Unplaced, unplaced => unplaced.Because.Contains("does not hold commit dddddddddddd", StringComparison.Ordinal));
        using var attempts = writer.Connection.CreateCommand();
        attempts.CommandText = "SELECT COUNT(*) FROM attempts";
        Assert.Equal(0L, attempts.ExecuteScalar());
        Assert.Equal(0, host.Calls);
    }

    /// <summary>
    /// The owner, 2026-09-26: a file taken from the commit a source was
    /// registered at is not hashed against its declaration. The declared digest
    /// here is deliberately wrong; the cell still runs the commit's bytes.
    /// </summary>
    [Fact]
    public void ACommitRegisteredSourceIsTakenAsGitReturnsItWithoutHashing()
    {
        var (writer, studyId, cellId, siteId, work, _, _) = CodeHome(RegistryOperations.CodeFromCommit);
        using var scope = writer;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText = "UPDATE sources SET declared_files_json = replace(declared_files_json, $right, $wrong)";
            command.Parameters.AddWithValue("$right", Sha256Of("registered"));
            command.Parameters.AddWithValue("$wrong", new string('0', 64));
            command.ExecuteNonQuery();
        }
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, siteId, "{}").TryGetValue(out var attempt));
        string? seen = null;
        var host = new FakeHost((_, arguments, environment) =>
        {
            if (arguments.Contains("run")) seen = File.ReadAllText(Path.Combine(environment["FERN_SOURCE_ROOT"], MarkerPath));
            return Trains(arguments, """{"schema":"fern.unit_results.v1"}""");
        });
        var worker = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host);
        var sourceId = RegistryOperations.ReadStudy(writer.Connection, studyId).TryGetValue(out var study) ? study.SourceId : 0;
        worker.Run(ClaimedFrom(studyId, cellId, siteId, attempt, sourceId), default);
        Assert.Equal("registered", seen);
    }

    /// <summary>
    /// A source registered from a commit runs the commit's version of a file, whatever
    /// the working tree holds now.
    /// </summary>
    [Fact]
    public void ASourceRegisteredFromACommitRunsItsCommitsVersionOfAFile()
    {
        var (writer, studyId, cellId, siteId, work, repository, _) = CodeHome(RegistryOperations.CodeFromCommit);
        using var scope = writer;
        File.WriteAllText(Path.Combine(repository, MarkerPath), "changed since");
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, siteId, "{}").TryGetValue(out var attempt));
        string? seen = null;
        var host = new FakeHost((_, arguments, environment) =>
        {
            if (arguments.Contains("run")) seen = File.ReadAllText(Path.Combine(environment["FERN_SOURCE_ROOT"], MarkerPath));
            return Trains(arguments, """{"schema":"fern.unit_results.v1"}""");
        });
        var worker = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host);
        var sourceId = RegistryOperations.ReadStudy(writer.Connection, studyId).TryGetValue(out var study) ? study.SourceId : 0;
        var outcome = worker.Run(ClaimedFrom(studyId, cellId, siteId, attempt, sourceId), default);
        Assert.True(outcome.Completed, outcome.DetailJson);
        Assert.Equal("registered", seen);
    }

    /// <summary>
    /// A source registered with bytes no commit carries (--from-worktree) is
    /// still checked byte for byte: the commit's file is not the declared one,
    /// the working tree's no longer matches either, so the attempt fails as the
    /// machine's failure and the trainer never starts.
    /// CHG 2026-09-26 (after 搬迁2号's review): restored for declarations written
    /// before schema 21, which carry each file's digest; registered_from where the
    /// bundle prefix was. How a source registered since is treated is the owner's.
    /// CHG 2026-09-27 (owner: "fix all of them"): the byte-for-byte search is gone;
    /// a source registered from a working tree is refused before any file is read,
    /// with the remedy in the sentence (commit, register again), and nothing is left.
    /// </summary>
    [Fact]
    public void AWorkingTreeSourceIsRefusedBeforeAnyFileIsReadAndNamesTheRemedy()
    {
        var (writer, studyId, cellId, siteId, work, repository, _) = CodeHome(RegistryOperations.CodeFromCommit);
        using var scope = writer;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText = "UPDATE sources SET registered_from = 'working-tree'";
            command.ExecuteNonQuery();
        }
        File.WriteAllText(Path.Combine(repository, MarkerPath), "changed since");
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, siteId, "{}").TryGetValue(out var attempt));
        var host = new FakeHost((_, arguments, _) => Trains(arguments, """{"schema":"fern.unit_results.v1"}"""));
        var worker = new LocalCellWorker(new SqliteStudyStore(writer), Placement(work), host);
        var sourceId = RegistryOperations.ReadStudy(writer.Connection, studyId).TryGetValue(out var study) ? study.SourceId : 0;
        var outcome = worker.Run(ClaimedFrom(studyId, cellId, siteId, attempt, sourceId), default);
        Assert.False(outcome.Completed);
        Assert.Equal(CellFailureDetail.ClassSite, CellFailureDetail.ClassOf(outcome.DetailJson));
        Assert.Contains("register the source again", CellFailureDetail.Sentence(outcome.DetailJson), StringComparison.Ordinal);
        Assert.Equal(0, host.Runs);
        Assert.False(Directory.Exists(CellCode.AttemptDirectory(work, attempt.AttemptId)));
    }

    /// <summary>
    /// A declared file the commit never held (a study input that was not committed) is
    /// taken from the working tree as it is, without a check, and named so the attempt
    /// records it; a declared file that is nowhere is refused by name.
    /// </summary>
    [Fact]
    public void ADeclaredFileTheCommitDoesNotHoldComesFromTheWorkingTreeAndIsNamed()
    {
        var (writer, _, _, _, _, repository, commit) = CodeHome(RegistryOperations.CodeFromCommit);
        using var scope = writer;
        Directory.CreateDirectory(Path.Combine(repository, "data"));
        File.WriteAllText(Path.Combine(repository, "data", "never-committed.txt"), "study input");
        CellCode.DeclaredFile[] declared =
            [new(MarkerPath, "regular_file", 10), new("data/never-committed.txt", "regular_file", 11)];
        var directory = Directory.CreateTempSubdirectory("fern-cell-code-written-").FullName;

        var taken = CellCode.WriteDeclaredFiles(directory, repository, 1, commit, declared);

        Assert.Equal(["data/never-committed.txt"], taken);
        Assert.Equal("registered", File.ReadAllText(Path.Combine(directory, MarkerPath)));
        Assert.Equal("study input", File.ReadAllText(Path.Combine(directory, "data", "never-committed.txt")));

        var nowhere = Directory.CreateTempSubdirectory("fern-cell-code-written-").FullName;
        var refused = Assert.Throws<InvalidOperationException>(() => CellCode.WriteDeclaredFiles(
            nowhere, repository, 1, commit, [new("data/absent.txt", "regular_file", 1)]));
        Assert.Contains("nor this workspace holds it", refused.Message, StringComparison.Ordinal);
    }

    // ── a remote Site gets its code from the Home (slice 3, owner: "做法1合理") ──

    static string FreeHomeAddress()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}/";
    }

    /// <summary>
    /// A supervisor on another machine has no repository: its worker asks the
    /// Home for the attempt's code, runs the registered commit's bytes in a
    /// directory of its own, and deletes it when the attempt ends.
    /// </summary>
    [Fact]
    public void ARemoteWorkerRunsTheCommitTheHomeSendsAndDeletesIt()
    {
        var (writer, studyId, cellId, siteId, work, repository, commit) = CodeHome(RegistryOperations.CodeFromCommit);
        using var scope = writer;
        File.WriteAllText(Path.Combine(repository, MarkerPath), "committed later");
        TestRegistry.Git(repository, "commit", "--quiet", "-am", "later");
        var address = FreeHomeAddress();
        using var service = new HomeService(writer, address);
        service.Start();
        using var client = new HomeClient(new Uri(address), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20));
        Assert.True(client.TryStartAttempt(cellId, siteId, "{}", RegistryOperations.DefaultMaxAttempts, null).TryGetValue(out var attempt));
        string? seen = null;
        var host = new FakeHost((_, arguments, environment) =>
        {
            if (arguments.Contains("run")) seen = File.ReadAllText(Path.Combine(environment["FERN_SOURCE_ROOT"], MarkerPath));
            return Trains(arguments, """{"schema":"fern.unit_results.v1"}""");
        });
        var remoteWork = Directory.CreateTempSubdirectory("fern-remote-work-").FullName;
        var worker = new LocalCellWorker(client, Placement(remoteWork) with { CodeFromHome = true }, host);
        var sourceId = RegistryOperations.ReadStudy(writer.Connection, studyId).TryGetValue(out var study) ? study.SourceId : 0;
        worker.Run(ClaimedFrom(studyId, cellId, siteId, attempt, sourceId), default);

        Assert.Equal("registered", seen);
        Assert.Equal(CellCode.AttemptDirectory(remoteWork, attempt.AttemptId), host.WorkingDirectory);
        Assert.False(Directory.Exists(host.WorkingDirectory), "the unpacked code is deleted when the attempt ends");
        var detail = CellCodeEvent(writer, attempt.AttemptId);
        Assert.Equal("home", detail["made_by"]!.GetValue<string>());
        Assert.Equal(commit, detail["registered_commit"]!.GetValue<string>());
        _ = work;
    }

    /// <summary>
    /// Under working-tree, the Home sends the runtime files as its working tree
    /// holds them, and the remote attempt keeps how they differ from the
    /// registered commit beside its other files.
    /// </summary>
    [Fact]
    public void ARemoteWorkerRunsTheWorkingTreeTheHomeSends()
    {
        var (writer, studyId, cellId, siteId, _, repository, _) = CodeHome(RegistryOperations.CodeFromWorkingTree);
        using var scope = writer;
        File.WriteAllText(Path.Combine(repository, MarkerPath), "edited on the Home, not committed");
        var address = FreeHomeAddress();
        using var service = new HomeService(writer, address);
        service.Start();
        using var client = new HomeClient(new Uri(address), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20));
        Assert.True(client.TryStartAttempt(cellId, siteId, "{}", RegistryOperations.DefaultMaxAttempts, null).TryGetValue(out var attempt));
        string? seen = null;
        var host = new FakeHost((_, arguments, environment) =>
        {
            if (arguments.Contains("run")) seen = File.ReadAllText(Path.Combine(environment["FERN_SOURCE_ROOT"], MarkerPath));
            return Trains(arguments, """{"schema":"fern.unit_results.v1"}""");
        });
        var remoteWork = Directory.CreateTempSubdirectory("fern-remote-work-").FullName;
        var worker = new LocalCellWorker(client, Placement(remoteWork) with { CodeFromHome = true }, host);
        var sourceId = RegistryOperations.ReadStudy(writer.Connection, studyId).TryGetValue(out var study) ? study.SourceId : 0;
        worker.Run(ClaimedFrom(studyId, cellId, siteId, attempt, sourceId), default);

        Assert.Equal("edited on the Home, not committed", seen);
        Assert.False(Directory.Exists(host.WorkingDirectory));
        var detail = CellCodeEvent(writer, attempt.AttemptId);
        Assert.Equal([MarkerPath], detail["differing_files"]!.AsArray().Select(path => path!.GetValue<string>()));
        var difference = detail["difference"]!.GetValue<string>();
        Assert.StartsWith(remoteWork, difference, StringComparison.Ordinal);
        Assert.Contains("edited on the Home, not committed", File.ReadAllText(difference), StringComparison.Ordinal);
    }

    /// <summary>
    /// Before a claim, a remote worker asks the Home whether the code can be
    /// given; and the Home hands out code only for an open attempt at its epoch.
    /// </summary>
    [Fact]
    public void TheHomeAnswersForTheRepositoryAndOnlyForAnOpenAttempt()
    {
        var (writer, _, cellId, siteId, _, _, _) = CodeHome(RegistryOperations.CodeFromCommit);
        using var scope = writer;
        var address = FreeHomeAddress();
        using var service = new HomeService(writer, address);
        service.Start();
        using var client = new HomeClient(new Uri(address), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20));
        var remoteWork = Directory.CreateTempSubdirectory("fern-remote-work-").FullName;
        var worker = new LocalCellWorker(client, Placement(remoteWork) with { CodeFromHome = true }, new FakeHost((_, _, _) =>
            new BoundedProbe.Outcome.Completed(0, [], "")));
        var site = client.ListSites().Single(one => one.SiteId == siteId);
        Assert.Null(worker.CannotGiveCode(site, 1));
        // A registered source never changes its commit, so a second source
        // stands for one registered at a commit this repository does not hold.
        long missing;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
                SELECT 'dddddddddddddddddddddddddddddddddddddddd', declared_files_json, registered_from, created_at
                FROM sources WHERE id = 1 RETURNING id
                """;
            missing = Convert.ToInt64(command.ExecuteScalar());
        }
        Assert.Contains("does not hold commit dddddddddddd", worker.CannotGiveCode(site, missing), StringComparison.Ordinal);

        Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, siteId, "{}").TryGetValue(out var attempt));
        var stale = client.PackCellCode(attempt.AttemptId, attempt.FencingEpoch + 1);
        Assert.False(stale.IsOk);
        Assert.True(RegistryOperations.FailAttempt(writer, attempt.AttemptId, attempt.FencingEpoch, RegistryOperations.AttemptFailed, "{}").IsOk);
        var closed = client.PackCellCode(attempt.AttemptId, attempt.FencingEpoch);
        Assert.True(closed.TryGetRefusal(out var refusal));
        Assert.Contains("not open", refusal.Detail, StringComparison.Ordinal);
    }

    /// <summary>A supervisor taking office deletes the commit directories of attempts that are no longer open, and only those.</summary>
    [Fact]
    public void LeftoverCodeDirectoriesOfClosedAttemptsAreRemoved()
    {
        var work = Directory.CreateTempSubdirectory("fern-cell-code-leftovers-").FullName;
        Directory.CreateDirectory(CellCode.AttemptDirectory(work, 5));
        Directory.CreateDirectory(CellCode.AttemptDirectory(work, 6));
        var removed = CellCode.RemoveLeftovers(work, new HashSet<long> { 6 });
        Assert.Equal([CellCode.AttemptDirectory(work, 5)], removed);
        Assert.False(Directory.Exists(CellCode.AttemptDirectory(work, 5)));
        Assert.True(Directory.Exists(CellCode.AttemptDirectory(work, 6)));
    }
}
