using Fern.Registry;
using Fern.Scheduler;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

/// <summary>
/// The `run-study-cells` loop, against a fake worker and no process.
///
/// The loop's whole job is to keep cycling until a cycle claims nothing, and
/// then to say WHY it stopped. Four endings look identical from the outside —
/// finished, paused, cancelled, nowhere to run — and a loop that just exited
/// would leave an operator to guess which. Each is asserted here by the words
/// it prints.
/// </summary>
public sealed class RunStudyCellsLoopTests
{
    sealed class CountedDecision(ISchedulerDecision inner) : ISchedulerDecision
    {
        public int Calls { get; private set; }
        public SchedulerDecision.Decision Choose(IReadOnlyList<SchedulerDecision.CellDemand> runnable,
            IReadOnlyList<SchedulerDecision.SiteOffer> sites, IReadOnlyDictionary<long, int> busySlotsBySite)
        {
            Calls++;
            return inner.Choose(runnable, sites, busySlotsBySite);
        }
    }

    [Fact]
    public void TheSelectedGleamDecisionIsUsedAcrossEveryCycle()
    {
        var (writer, studyId, name) = Home(cells: 3, slots: 1);
        using var owned = writer;
        using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
        var decision = new CountedDecision(new GleamSchedulerDecision(program));
        var worker = new Worker(Completes);
        var exit = StudyOrchestrator.RunUntilIdle(new SqliteStudyStore(writer), worker, studyId, name,
            "local-4070", 0, new StringWriter(), new StringWriter(), implementedBy: decision);
        Assert.Equal(0, exit);
        Assert.Equal(3, decision.Calls);
        Assert.Equal(3, worker.Ran);
        Assert.True(RegistryOperations.ReadStudy(writer.Connection, studyId).TryGetValue(out var study));
        Assert.Equal(3, study.AcceptedCellCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("read request\nprintf '%s\\n' '{\"id\":\"1\",\"answer\":{}}'\n")]
    public async Task TheCommandNeverFallsBackWhenTheSelectedProgramFails(string entrypoint)
    {
        var (writer, studyId, name) = Home(cells: 1, slots: 1);
        using var owned = writer;
        var folder = Directory.CreateTempSubdirectory("gleam-empty-shipment-").FullName;
        try
        {
            // Invalid shipment fixtures either exit silently or return an invalid answer.
            File.WriteAllText(Path.Combine(folder, "entrypoint.sh"), entrypoint);
            var run = await GleamCellAdmissionTests.RunCommand([
                "run-study-cells", "--store", writer.Connection.DataSource, "--study", name,
                "--site", "local-4070", "--python", "/no-trainer-in-this-test", "--fern-run", "/unused",
                "--fern-compile", "/unused", "--work-root", folder, "--max-cycles", "1", "--scheduler-program", folder], folder);
            Assert.Equal(1, run.Exit);
            Assert.Contains("selected Gleam scheduler failed", run.Error, StringComparison.Ordinal);
            Assert.True(RegistryOperations.ReadStudy(writer.Connection, studyId).TryGetValue(out var study));
            Assert.Equal(0, study.AcceptedCellCount);
            using var attempts = writer.Connection.CreateCommand();
            attempts.CommandText = "SELECT COUNT(*) FROM attempts";
            Assert.Equal(0L, (long)attempts.ExecuteScalar()!);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void OfficeRenewsWhileAWorkerBlocksBeyondItsOriginalLease()
    {
        var (writer, studyId, name) = Home(cells: 1);
        using var scope = writer;
        var worker = new Worker(cell =>
        {
            Thread.Sleep(1600);
            var competing = RegistryOperations.StartSupervisor(writer, cell.SiteId, "other", 2, "sha256:other", 1);
            Assert.False(competing.IsOk);
            return Completes(cell);
        });
        var exit = StudyOrchestrator.RunUntilIdle(new SqliteStudyStore(writer), worker, studyId, name,
            "local-4070", 0, new StringWriter(), new StringWriter(),
            supervisor: new StudyOrchestrator.SupervisorIdentity("first", 1, "sha256:first"), leaseSeconds: 1);
        Assert.Equal(0, exit);
        Assert.Equal(1, worker.Ran);
    }
    sealed class DisplacingWorker(RegistryWriter writer) : StudyOrchestrator.ICellWorker
    {
        public bool Stopped { get; private set; }
        public StudyOrchestrator.WorkerOutcome Run(StudyOrchestrator.ClaimedCell cell, CancellationToken cancellation)
        {
            writer.WriteImmediate(connection =>
            {
                using var expire = connection.CreateCommand();
                expire.CommandText = "UPDATE supervisors SET lease_until = '2000-01-01T00:00:00Z' WHERE ended_at IS NULL";
                expire.ExecuteNonQuery();
                return RegistryOperations.StartSupervisor(writer, cell.SiteId, "successor", 2, "sha256:successor", 10);
            });
            Stopped = cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(3));
            return new(false, null, CellFailureDetail.Write("office displaced", deterministic: false, CellFailureDetail.ClassTransient),
                EndState: RegistryOperations.AttemptLost);
        }
    }

    [Fact]
    public void LosingTheOfficeCancelsWorkBeforeTheNextCycle()
    {
        var (writer, studyId, name) = Home(cells: 1);
        using var scope = writer;
        var worker = new DisplacingWorker(writer);
        var exit = StudyOrchestrator.RunUntilIdle(new SqliteStudyStore(writer), worker, studyId, name,
            "local-4070", 0, new StringWriter(), new StringWriter(),
            supervisor: new StudyOrchestrator.SupervisorIdentity("first", 1, "sha256:first"), leaseSeconds: 1);
        Assert.True(worker.Stopped, "the displaced supervisor left its owned worker running");
        Assert.Equal(StudyOrchestrator.ExitRetryable, exit);
    }

    sealed class Worker(Func<StudyOrchestrator.ClaimedCell, StudyOrchestrator.WorkerOutcome> run)
        : StudyOrchestrator.ICellWorker
    {
        public int Ran { get; private set; }

        public StudyOrchestrator.WorkerOutcome Run(StudyOrchestrator.ClaimedCell cell, CancellationToken cancellation)
        {
            Ran++;
            return run(cell);
        }
    }

    static StudyOrchestrator.WorkerOutcome Completes(StudyOrchestrator.ClaimedCell cell) =>
        new(true, $$"""{"cell":"{{cell.CellName}}"}""", "{}");

    static StudyOrchestrator.WorkerOutcome Fails(StudyOrchestrator.ClaimedCell cell) =>
        new(false, null, CellFailureDetail.Write("the pod went away", deterministic: false, CellFailureDetail.ClassTransient));

    static (RegistryWriter Writer, long StudyId, string Name) Home(int cells = 3, int slots = 1, string models = """["fern"]""")
    {
        var path = TestRegistry.CreateRegistry();
        var writer = RegistryHome.OpenWriter(path);
        long sourceId;
        long siteId;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
                VALUES ('5e87434e', '[]', 'commit', '2026-09-11') RETURNING id
                """;
            sourceId = Convert.ToInt64(command.ExecuteScalar());
        }
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                $$"""
                INSERT INTO sites (name, capabilities_json, capacity_json, created_at)
                VALUES ('local-4070', '{"device":"cuda","admitted_models":{{models}}}', '{"slots":{{slots}}}', '2026-09-11')
                RETURNING id
                """;
            siteId = Convert.ToInt64(command.ExecuteScalar());
        }
        var declared = Enumerable.Range(0, cells).Select(ordinal => new RegistryOperations.CompiledCell(
            ordinal, $"cell-{ordinal}", "rossler_param_shock.series", 42,
            """{"schema":"fern.training.v2","model":{"type":"fern"},"runtime":{"device":"cuda"}}""",
            """{"device":"cuda","requires_cuda":true}""",
            """{"unit_results":"retained"}""")).ToList();
        var registered = RegistryOperations.RegisterStudy(
            writer, "looped", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, declared);
        Assert.True(registered.TryGetValue(out var value));
        // Slice 6 (row 31): the Site holds the study's source, or the claim refuses it.
        TestRegistry.DeclareCodeFrom(writer, siteId);
        return (writer, value.StudyId, "looped");
    }

    static (int Exit, string Out) Drive(
        RegistryWriter writer, StudyOrchestrator.ICellWorker worker, long studyId, string name,
        int maxCycles = 0, string site = "local-4070")
    {
        var stdout = new StringWriter();
        var exit = StudyOrchestrator.RunUntilIdle(new SqliteStudyStore(writer), worker, studyId, name, site, maxCycles, stdout, new StringWriter());
        return (exit, stdout.ToString());
    }

    /// <summary>
    /// This command runs on one machine what it claims, so it claims only cells
    /// placed on the Site that machine is. A name no Site carries is a stop
    /// with the reason, not a loop that quietly claims nothing.
    /// </summary>
    [Fact]
    public void ALoopStartedAsASiteNobodyRegisteredStopsAndSaysSo()
    {
        var (writer, studyId, name) = Home(cells: 1, slots: 1);
        using var _ = writer;
        var worker = new Worker(Completes);
        var stderr = new StringWriter();

        var exit = StudyOrchestrator.RunUntilIdle(
            new SqliteStudyStore(writer), worker, studyId, name, "pod-5090", maxCycles: 0, new StringWriter(), stderr);

        Assert.Equal(3, exit);
        Assert.Equal(0, worker.Ran);
        Assert.Contains("no Site named pod-5090 is registered", stderr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A cell placed on another machine is left where it was placed. The local
    /// loop would otherwise train it here and record an attempt naming a Site
    /// that never saw the cell.
    /// </summary>
    [Fact]
    public void ACellOnlyAnotherSiteAdmitsIsNotClaimedHere()
    {
        var (writer, studyId, name) = Home(cells: 1, slots: 1);
        using var _ = writer;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sites (name, capabilities_json, capacity_json, created_at)
                VALUES ('pod-5090', '{"device":"cuda","admitted_models":["fern"]}', '{"slots":1}', '2026-09-11')
                """;
            command.ExecuteNonQuery();
        }
        using (var command = writer.Connection.CreateCommand())
        {
            // The local machine admits no model this study uses; only the pod does.
            command.CommandText =
                """
                UPDATE sites SET capabilities_json = '{"device":"cuda","admitted_models":["dlinear"]}'
                WHERE name = 'local-4070'
                """;
            command.ExecuteNonQuery();
        }
        var worker = new Worker(Completes);

        var (exit, output) = Drive(writer, worker, studyId, name);

        Assert.Equal(0, exit);
        Assert.Equal(0, worker.Ran);
        Assert.Contains("no Site can take", output, StringComparison.Ordinal);
    }

    /// <summary>One slot, three cells: the loop cycles until all three are accepted.</summary>
    [Fact]
    public void TheLoopRunsEveryCellAndSaysTheStudyIsFinished()
    {
        var (writer, studyId, name) = Home(cells: 3, slots: 1);
        using var _ = writer;
        var worker = new Worker(Completes);

        var (exit, output) = Drive(writer, worker, studyId, name);

        Assert.Equal(0, exit);
        Assert.Equal(3, worker.Ran);
        Assert.Contains("study looped is finished", output, StringComparison.Ordinal);
        Assert.Contains("3/3 cells accepted", output, StringComparison.Ordinal);
        var view = RegistryOperations.ReadStudy(writer.Connection, studyId);
        Assert.True(view.TryGetValue(out var head));
        Assert.Equal(3, head.AcceptedCellCount);
    }

    /// <summary>A cancelled study claims nothing, and the loop says so rather than exiting silently.</summary>
    [Fact]
    public void ACancelledStudyEndsTheLoopWithItsState()
    {
        var (writer, studyId, name) = Home();
        using var _ = writer;
        Assert.True(RegistryOperations.RequestStudyState(writer, studyId, "cancel").IsOk);
        var worker = new Worker(Completes);

        var (exit, output) = Drive(writer, worker, studyId, name);

        Assert.Equal(0, exit);
        Assert.Equal(0, worker.Ran);
        Assert.Contains("study looped is cancel", output, StringComparison.Ordinal);
    }

    /// <summary>A cell no Site admits stops the loop with the decision's own reason.</summary>
    [Fact]
    public void ACellNoSiteAdmitsEndsTheLoopWithTheReason()
    {
        var (writer, studyId, name) = Home(cells: 2, models: """["dlinear"]""");
        using var _ = writer;
        var worker = new Worker(Completes);

        var (exit, output) = Drive(writer, worker, studyId, name);

        Assert.Equal(0, exit);
        Assert.Equal(0, worker.Ran);
        Assert.Contains("no Site can take", output, StringComparison.Ordinal);
        Assert.Contains("admits model fern", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cell that failed is not handed back to the same loop.
    ///
    /// The store makes a failed cell runnable again on purpose — a person, or
    /// another supervisor, may retry it — but before this the loop re-claimed
    /// its own failure every cycle and only --max-cycles ever stopped it. The
    /// cell is attempted once, the loop ends on its own, the exit code says a
    /// cell failed, and the worker's sentence is in the conclusion.
    /// </summary>
    [Fact]
    public void AFailedCellIsNotClaimedAgainByTheLoopThatFailedIt()
    {
        var (writer, studyId, name) = Home(cells: 1, slots: 1);
        using var _ = writer;
        var worker = new Worker(Fails);

        var (exit, output) = Drive(writer, worker, studyId, name);

        Assert.Equal(4, exit);
        Assert.Equal(1, worker.Ran);
        Assert.Contains("cell-0 failed with: the pod went away", output, StringComparison.Ordinal);
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM attempts WHERE state = 'failed'";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    /// <summary>
    /// One cell failing does not stop the others: the loop drops that cell and
    /// keeps going, and the study ends with two accepted and one failed.
    /// </summary>
    [Fact]
    public void TheOtherCellsStillRunWhenOneFails()
    {
        var (writer, studyId, name) = Home(cells: 3, slots: 1);
        using var _ = writer;
        var worker = new Worker(cell => cell.CellName == "cell-1" ? Fails(cell) : Completes(cell));

        var (exit, output) = Drive(writer, worker, studyId, name);

        Assert.Equal(4, exit);
        Assert.Equal(3, worker.Ran);
        Assert.Contains("2/3 cells accepted", output, StringComparison.Ordinal);
        Assert.Contains("cell-1 failed with: the pod went away", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The verb refuses a cell whose configuration names the CPU: the loop
    /// claims it, the production worker refuses it before starting a process,
    /// and the refusal is on the record as a failed attempt naming rule 1.
    /// </summary>
    [Fact]
    public void TheLoopRefusesACellWhoseConfigurationNamesTheCpu()
    {
        var (writer, studyId, name) = Home(cells: 1, slots: 1);
        using var _ = writer;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE cells SET config_json =
                  '{"schema":"fern.training.v2","model":{"type":"fern"},"runtime":{"device":"cpu"}}'
                WHERE study_id = $study
                """;
            command.Parameters.AddWithValue("$study", studyId);
            command.ExecuteNonQuery();
        }
        var work = Directory.CreateTempSubdirectory("fern-cpu-refusal-").FullName;
        // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): the real worker
        // gives a cell its code before the claim and before the refusal, so the Site's
        // repository is a real one here.
        var repository = Directory.CreateTempSubdirectory("fern-cpu-refusal-repository-").FullName;
        TestRegistry.Git(repository, "init", "--quiet");
        TestRegistry.Git(repository, "-c", "user.email=test@example.invalid", "-c", "user.name=loop test",
            "commit", "--quiet", "--allow-empty", "-m", "the repository");
        TestRegistry.DeclareCodeFrom(writer, 1, RegistryOperations.CodeFromWorkingTree, repository);
        var worker = new LocalCellWorker(new SqliteStudyStore(writer), new LocalCellWorker.Placement(
            "/home/user/.venv/bin/python", "/opt/fern-run", "/opt/fern-compile", work, TimeSpan.FromMinutes(1),
            Path.Combine(work, "store")),
            new ThrowingHost());

        var (exit, _) = Drive(writer, worker, studyId, name, maxCycles: 1);

        Assert.Equal(0, exit);
        using var failed = writer.Connection.CreateCommand();
        failed.CommandText = "SELECT detail_json FROM events WHERE kind = 'failed' LIMIT 1";
        var detail = failed.ExecuteScalar() as string ?? "";
        Assert.Contains("runtime.device=cpu", detail, StringComparison.Ordinal);
        Assert.Contains("rule 1", detail, StringComparison.Ordinal);
    }

    /// <summary>A host that must never be called: if it is, the CPU refusal did not fire.</summary>
    sealed class ThrowingHost : WorkerEnvironmentPreparation.IHost
    {
        public bool IsLinux => true;
        public BoundedProbe.Outcome Run(string program, IReadOnlyList<string> arguments, long timeoutMs, long maxBytes,
            string workingDirectory, IReadOnlyDictionary<string, string> environment) =>
            throw new InvalidOperationException("a CPU cell must be refused before any process starts");
    }

    // ── slice 1 of the cell port: the loop as a resident service ──────────────
    //
    // The retired daemon's service falsifiers (resident-service-obligation.md),
    // re-pointed at the study loop holding a Site's office: contention exits
    // retryable, a stop consumed between cells exits completed and stays down,
    // a displaced supervisor exits retryable at its next cycle, a cancellation
    // ends as completed, a Site nobody registered is a terminal configuration,
    // and a loop without an identity holds no office at all.

    static readonly StudyOrchestrator.SupervisorIdentity Identity = new("this-machine", 4242, "sha256:cafe");

    static long SiteId(RegistryWriter writer, string name = "local-4070")
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT id FROM sites WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static (int Exit, string Out, string Err) Serve(
        RegistryWriter writer, StudyOrchestrator.ICellWorker worker, long? studyId, string? name,
        CancellationToken cancellation = default, string site = "local-4070")
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = StudyOrchestrator.RunUntilIdle(
            new SqliteStudyStore(writer), worker, studyId, name, site, maxCycles: 0, stdout, stderr,
            serveInterval: TimeSpan.FromMilliseconds(50), cancellation: cancellation,
            supervisor: Identity, leaseSeconds: 60);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void ALoopWithoutAnIdentityHoldsNoOffice()
    {
        var (writer, studyId, name) = Home(cells: 1);
        using var _ = writer;
        Drive(writer, new Worker(Completes), studyId, name);
        Assert.Null(RegistryOperations.ReadSupervisor(writer.Connection, SiteId(writer)));
    }

    [Fact]
    public void ASecondSupervisorOfTheSameSiteIsRefusedByNameAndExitsRetryable()
    {
        var (writer, studyId, name) = Home(cells: 1);
        using var _ = writer;
        Assert.True(RegistryOperations.StartSupervisor(writer, SiteId(writer), "other-machine", 7, "sha256:beef").IsOk);
        var worker = new Worker(Completes);

        var (exit, _, err) = Serve(writer, worker, studyId, name);

        Assert.Equal(StudyOrchestrator.ExitRetryable, exit);
        Assert.Equal(0, worker.Ran);
        Assert.Contains("supervised by other-machine pid 7 at epoch 1", err, StringComparison.Ordinal);
        // The holder's row is untouched: one lifetime, unended.
        var holder = RegistryOperations.ReadSupervisor(writer.Connection, SiteId(writer))!;
        Assert.Equal("other-machine", holder.Host);
        Assert.Null(holder.EndedAt);
    }

    [Fact]
    public void AStopRequestIsConsumedBetweenCyclesAndTheSupervisorExitsCompletedAndRecordsIt()
    {
        var (writer, studyId, name) = Home(cells: 1);
        using var _ = writer;
        Assert.True(RegistryOperations.RequestOperator(
            writer, RegistryOperations.RequestStop, RegistryOperations.SiteScope(SiteId(writer)), "reboot for a driver", "qilin").IsOk);
        var worker = new Worker(Completes);

        var (exit, output, _) = Serve(writer, worker, studyId, name);

        // The first cycle claims and runs the one cell; the stop is consumed only
        // by the cycle that claims nothing, so the cell was never interrupted.
        // CHG 2026-09-22: that was the idle-only bug. A standing stop must
        // prevent the first claim; there is no active worker to drain here.
        Assert.Equal(StudyOrchestrator.ExitCompleted, exit);
        Assert.Equal(0, worker.Ran);
        Assert.Contains("stop requested by qilin (reboot for a driver)", output, StringComparison.Ordinal);
        var office = RegistryOperations.ReadSupervisor(writer.Connection, SiteId(writer))!;
        Assert.Equal("this-machine", office.Host);
        Assert.Equal(4242, office.Pid);
        Assert.Equal("stopped", office.Outcome);
        Assert.Equal(RegistryOperations.DispositionCompleted, office.Disposition);
        Assert.Equal(0, office.ExitCode);
        Assert.NotNull(office.EndedAt);
        Assert.Empty(RegistryOperations.PendingRequests(writer.Connection, RegistryOperations.RequestStop, RegistryOperations.SiteScope(SiteId(writer))));
    }

    [Fact]
    public void ASupervisorServingEveryStudyRunsWhatIsRunnableAndEndsOnlyOnAStop()
    {
        var (writer, _, _) = Home(cells: 2);
        using var _ = writer;
        Assert.True(RegistryOperations.RequestOperator(
            writer, RegistryOperations.RequestStop, RegistryOperations.ScopeAll, "end of day", "qilin").IsOk);
        var worker = new Worker(Completes);

        var (exit, output, _) = Serve(writer, worker, studyId: null, name: null);

        Assert.Equal(StudyOrchestrator.ExitCompleted, exit);
        Assert.Equal(0, worker.Ran);
        Assert.Contains("serving every study in state run", output, StringComparison.Ordinal);
        Assert.Contains("stop requested by qilin (end of day)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AStopDuringOneCellDrainsItAndPreventsClaimingTheNextRunnableCell()
    {
        var (writer, studyId, name) = Home(cells: 2);
        using var scope = writer;
        var worker = new Worker(cell =>
        {
            Assert.True(RegistryOperations.RequestOperator(writer,
                RegistryOperations.RequestStop, RegistryOperations.SiteScope(SiteId(writer)),
                "install the new compiler after this cell", "qilin").IsOk);
            return Completes(cell);
        });

        var (exit, output, _) = Serve(writer, worker, studyId, name);

        Assert.Equal(StudyOrchestrator.ExitCompleted, exit);
        Assert.Equal(1, worker.Ran);
        Assert.Contains("1 completed, 0 failed", output, StringComparison.Ordinal);
        Assert.Contains("stop requested by qilin", output, StringComparison.Ordinal);
        Assert.Empty(RegistryOperations.PendingRequests(writer.Connection,
            RegistryOperations.RequestStop, RegistryOperations.SiteScope(SiteId(writer))));
        // A fresh supervisor can still claim the untouched second condition.
        var successor = new Worker(Completes);
        var resumed = Drive(writer, successor, studyId, name);
        Assert.Equal(0, resumed.Exit);
        Assert.Equal(1, successor.Ran);
    }

    [Fact]
    public void ADisplacedSupervisorExitsRetryableAtItsNextCycleAndSaysWho()
    {
        var (writer, studyId, name) = Home(cells: 3, slots: 1);
        using var _ = writer;
        var siteId = SiteId(writer);
        var worker = new Worker(cell =>
        {
            // While the first cell runs, the office lease lapses and another
            // machine takes the Site: the durable fact the next renewal meets.
            using (var expire = writer.Connection.CreateCommand())
            {
                expire.CommandText = "UPDATE supervisors SET lease_until = '2000-01-01T00:00:00.0000000+00:00' WHERE ended_at IS NULL";
                expire.ExecuteNonQuery();
            }
            Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "other-machine", 7, "sha256:beef").IsOk);
            return Completes(cell);
        });

        var (exit, _, err) = Serve(writer, worker, studyId, name);

        Assert.Equal(StudyOrchestrator.ExitRetryable, exit);
        Assert.Equal(1, worker.Ran);
        Assert.Contains("taken by another supervisor", err, StringComparison.Ordinal);
        // Epoch 1 ended as office_lost; epoch 2 is the other machine's, untouched.
        using var rows = writer.Connection.CreateCommand();
        rows.CommandText = "SELECT epoch, host, outcome, disposition, exit_code FROM supervisors ORDER BY epoch";
        using var reader = rows.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal((1L, "this-machine", "office_lost", RegistryOperations.DispositionRetryable, 1L),
            (reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
        Assert.True(reader.Read());
        Assert.Equal("other-machine", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
    }

    [Fact]
    public void ACancelledServeEndsTheOfficeAsCompleted()
    {
        var (writer, studyId, name) = Home(cells: 1);
        using var _ = writer;
        Assert.True(RegistryOperations.RequestStudyState(writer, studyId, "pause").IsOk);
        // Long enough that the token fires inside an idle wait rather than
        // inside a cycle's Registry writes, which under a loaded suite can take
        // longer than a short timer; a token that fires mid-cycle ends the loop
        // as idle, which is also completed but not what this test is about.
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));

        // A paused study is settled for a one-study serve, so make it wait on
        // every study instead, where only a stop or a cancellation ends it.
        var (exit, output, _) = Serve(writer, new Worker(Completes), studyId: null, name: null, cancellation: cancel.Token);

        Assert.Equal(StudyOrchestrator.ExitCompleted, exit);
        Assert.Contains("stopped serving every study in state run as asked", output, StringComparison.Ordinal);
        var office = RegistryOperations.ReadSupervisor(writer.Connection, SiteId(writer))!;
        Assert.Equal("cancelled", office.Outcome);
        Assert.Equal(RegistryOperations.DispositionCompleted, office.Disposition);
        Assert.Equal(0, office.ExitCode);
    }

    [Fact]
    public void ASiteNobodyRegisteredIsATerminalConfigurationForASupervisor()
    {
        var (writer, studyId, name) = Home(cells: 1);
        using var _ = writer;
        var (exit, _, err) = Serve(writer, new Worker(Completes), studyId, name, site: "pod-5090");
        Assert.Equal(StudyOrchestrator.ExitTerminalConfiguration, exit);
        Assert.Contains("no Site named pod-5090", err, StringComparison.Ordinal);
    }

    [Fact]
    public void AOneShotRunWithoutAStudyIsRefused()
    {
        var (writer, _, _) = Home(cells: 1);
        using var _ = writer;
        var stderr = new StringWriter();
        var exit = StudyOrchestrator.RunUntilIdle(
            new SqliteStudyStore(writer), new Worker(Completes), null, null, "local-4070", 0, new StringWriter(), stderr);
        Assert.Equal(3, exit);
        Assert.Contains("a one-shot run is one study's", stderr.ToString(), StringComparison.Ordinal);
    }

    // ── slice 2 of the cell port: pause, and a cancellation that reached the trainer ──

    [Fact]
    public void APauseKeepsEveryCellOutOfTheOfferUntilItIsReleased()
    {
        var (writer, studyId, name) = Home(cells: 2);
        using var _ = writer;
        Assert.True(RegistryOperations.RequestOperator(
            writer, RegistryOperations.RequestPause, RegistryOperations.ScopeAll, "quiet hours", "qilin").TryGetValue(out var pauseId));
        var worker = new Worker(Completes);

        var (pausedExit, pausedOut) = Drive(writer, worker, studyId, name);
        Assert.Equal(0, pausedExit);
        Assert.Equal(0, worker.Ran);
        Assert.Contains("claimed none", pausedOut, StringComparison.Ordinal);

        Assert.True(RegistryOperations.ReleaseRequest(writer, pauseId, "qilin", "morning").IsOk);
        var (exit, output) = Drive(writer, worker, studyId, name);
        Assert.Equal(0, exit);
        Assert.Equal(2, worker.Ran);
        Assert.Contains("study looped is finished", output, StringComparison.Ordinal);
    }

    /// <summary>Slice 3: a Site a person disabled offers nothing to the loop until it is enabled again.</summary>
    [Fact]
    public void ADisabledSiteOffersNothingUntilItIsEnabled()
    {
        var (writer, studyId, name) = Home(cells: 1);
        using var _ = writer;
        Assert.True(RegistryOperations.RequestOperator(
            writer, RegistryOperations.RequestHold, RegistryOperations.SiteScope(SiteId(writer)), "fan is dead", "qilin").TryGetValue(out var holdId));
        var worker = new Worker(Completes);

        var (disabledExit, disabledOut) = Drive(writer, worker, studyId, name);
        Assert.Equal(0, disabledExit);
        Assert.Equal(0, worker.Ran);
        Assert.Contains("claimed none", disabledOut, StringComparison.Ordinal);

        Assert.True(RegistryOperations.ReleaseRequest(writer, holdId, "qilin", "fan replaced").IsOk);
        var (exit, output) = Drive(writer, worker, studyId, name);
        Assert.Equal(0, exit);
        Assert.Equal(1, worker.Ran);
        Assert.Contains("study looped is finished", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkerThatWasCancelledEndsTheAttemptAsCancelledNotFailed()
    {
        var (writer, studyId, name) = Home(cells: 1);
        using var _ = writer;
        var worker = new Worker(_ => new StudyOrchestrator.WorkerOutcome(false, null,
            CellFailureDetail.Write("the study was cancelled while the cell trained", deterministic: false,
                failureClass: CellFailureDetail.ClassHuman),
            EndState: RegistryOperations.AttemptCancelled));

        var (exit, _) = Drive(writer, worker, studyId, name);

        Assert.Equal(4, exit);
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT state FROM attempts ORDER BY id DESC LIMIT 1";
        Assert.Equal("cancelled", command.ExecuteScalar());
        // A person's decision: the machine offers the cell to nobody until retry-cell.
        Assert.Empty(RegistryOperations.ListRunnableCells(writer.Connection, studyId));
    }
}
