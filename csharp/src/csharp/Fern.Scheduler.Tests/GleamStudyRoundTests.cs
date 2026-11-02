using Fern.Install;
using Fern.Registry;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// The scheduling round run by the Gleam program (slices C4–C6 of the
/// scheduling-loop design), driven through <see cref="StudyOrchestrator.RunUntilIdle"/>
/// and through the real `run-study-cells --scheduler-program` command, with
/// a worker that trains nothing.
///
/// What these hold the round to: a cell that cannot run gives its slot to one
/// that can, and keeps its reason where a person reads it; a failure record
/// with no class is shown, not retried; a serving loop that claims nothing
/// says why once, not every cycle.
/// </summary>
public sealed class GleamStudyRoundTests
{
    const string Site = "local-4070";

    sealed class Worker : StudyOrchestrator.ICellWorker
    {
        public List<string> Ran { get; } = [];

        public StudyOrchestrator.WorkerOutcome Run(StudyOrchestrator.ClaimedCell cell, CancellationToken cancellation)
        {
            Ran.Add(cell.CellName);
            return new(true, $$"""{"cell":"{{cell.CellName}}"}""", "{}");
        }
    }

    /// <summary>A Registry with one Site that holds <paramref name="heldSource"/>, and no Study yet.</summary>
    static (RegistryWriter Writer, long SiteId) Home(int slots = 1)
    {
        var writer = RegistryHome.OpenWriter(TestRegistry.CreateRegistry());
        using var command = writer.Connection.CreateCommand();
        command.CommandText =
            $$"""
            INSERT INTO sites (name, capabilities_json, capacity_json, created_at)
            VALUES ('{{Site}}', '{"device":"cuda","admitted_models":["fern"]}', '{"slots":{{slots}}}', '2026-09-25')
            RETURNING id
            """;
        return (writer, Convert.ToInt64(command.ExecuteScalar()));
    }

    static long Source(RegistryWriter writer, string commit)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText =
            $"""
            INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
            VALUES ('{commit}', '[]', 'commit', '2026-09-25') RETURNING id
            """;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): the Site no
    // longer holds a source; it declares where its cells take their code from.
    static void Hold(RegistryWriter writer, long siteId, long sourceId) =>
        TestRegistry.DeclareCodeFrom(writer, siteId);

    static (long StudyId, IReadOnlyList<long> CellIds) Study(RegistryWriter writer, string name, long sourceId, int cells)
    {
        var declared = Enumerable.Range(0, cells).Select(ordinal => new RegistryOperations.CompiledCell(
            ordinal, $"{name}-{ordinal}", "rossler_param_shock.series", 42,
            """{"schema":"fern.training.v2","model":{"type":"fern"},"runtime":{"device":"cuda"}}""",
            """{"device":"cuda","requires_cuda":true}""",
            """{"unit_results":"retained"}""")).ToList();
        Assert.True(RegistryOperations.RegisterStudy(
            writer, name, 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, declared).TryGetValue(out var study));
        return (study.StudyId, study.CellIds);
    }

    /// <summary>Claim the cell once and end that attempt as failed, with this record.</summary>
    static void Failed(RegistryWriter writer, long cellId, long siteId, string detailJson)
    {
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, siteId, """{"schema":"fern.cell_request.v2"}""")
            .TryGetValue(out var attempt));
        Assert.True(RegistryOperations.FailAttempt(writer, attempt.AttemptId, attempt.FencingEpoch,
            RegistryOperations.AttemptFailed, detailJson).IsOk);
    }

    static int Occurrences(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    static (int Exit, string Out) Drive(
        RegistryWriter writer, Worker worker, long? studyId, string? name, int maxCycles, TimeSpan? serve = null)
    {
        using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
        var stdout = new StringWriter();
        var exit = StudyOrchestrator.RunUntilIdle(new SqliteStudyStore(writer), worker, studyId, name, Site, maxCycles,
            stdout, new StringWriter(), serveInterval: serve, round: new GleamStudyRound(program, Site));
        return (exit, stdout.ToString());
    }

    [Fact]
    public void ARefusedCellGivesItsSlotToTheNextAndKeepsItsReason()
    {
        var (writer, siteId) = Home();
        using var owned = writer;
        var source = Source(writer, "5e87434e");
        Hold(writer, siteId, source);
        var (studyId, cells) = Study(writer, "looped", source, cells: 2);
        Failed(writer, cells[0], siteId, CellFailureDetail.Write(
            "unknown dataset 'pointmaze.physical_state'", deterministic: true, CellFailureDetail.ClassDeterministic));

        var worker = new Worker();
        var (exit, stdout) = Drive(writer, worker, studyId, "looped", maxCycles: 0);

        Assert.Equal(0, exit);
        Assert.Equal(["looped-1"], worker.Ran);
        Assert.Contains(
            $"cell {cells[0]}: its last failure would repeat unchanged (unknown dataset 'pointmaze.physical_state')",
            stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// Slice P2: the round and the Registry's claim order the same queue. The
    /// ranks (a cell's own over its study's), a move to the back of a rank,
    /// and a study registered after the move are all in play; with one slot
    /// the cells run one at a time, so the order they ran in is the round's.
    /// </summary>
    [Fact]
    public void TheRoundOffersCellsInTheOrderTheRegistrysClaimDoes()
    {
        var (writer, siteId) = Home();
        using var owned = writer;
        var source = Source(writer, "5e87434e");
        Hold(writer, siteId, source);
        var (alpha, _) = Study(writer, "alpha", source, cells: 2);
        var (beta, _) = Study(writer, "beta", source, cells: 1);
        var (_, gammaCells) = Study(writer, "gamma", source, cells: 2);
        Assert.True(RegistryOperations.SetPriority(writer, RegistryOperations.StudyScope(beta), "urgent", "first", "test").IsOk);
        Assert.True(RegistryOperations.SetPriority(writer, RegistryOperations.CellScope(gammaCells[1]), "immediate", "the reference", "test").IsOk);
        Assert.True(RegistryOperations.SetPriority(writer, RegistryOperations.CellScope(gammaCells[0]), "slow", "can wait", "test").IsOk);
        Assert.True(RegistryOperations.MoveLast(writer, RegistryOperations.StudyScope(alpha), "behind what is here now", "test").IsOk);
        Study(writer, "delta", source, cells: 1);

        var claimOrder = RegistryOperations.ListRunnableCells(writer.Connection).Select(cell => cell.Name).ToList();
        Assert.Equal(["gamma-1", "beta-0", "alpha-0", "alpha-1", "delta-0", "gamma-0"], claimOrder);

        var worker = new Worker();
        var (exit, _) = Drive(writer, worker, null, null, maxCycles: 8, serve: TimeSpan.FromMilliseconds(10));
        Assert.Equal(0, exit);
        Assert.Equal(claimOrder, worker.Ran);
    }

    /// <summary>
    /// Slice P2, the reader's two further cases: a cell's own move overrides
    /// its study's, and moves made behind the same study keep the order they
    /// were made in (to the second, in both orders).
    /// </summary>
    [Fact]
    public void ACellsOwnMoveAndTwoMovesBehindOneStudyOrderTheRoundAsTheClaim()
    {
        var (writer, siteId) = Home();
        using var owned = writer;
        var source = Source(writer, "5e87434e");
        Hold(writer, siteId, source);
        var (alpha, _) = Study(writer, "alpha", source, cells: 2);
        var (beta, betaCells) = Study(writer, "beta", source, cells: 2);
        Study(writer, "gamma", source, cells: 1);
        Assert.True(RegistryOperations.MoveLast(writer, RegistryOperations.StudyScope(beta), "first move", "test").IsOk);
        Thread.Sleep(1100);
        Assert.True(RegistryOperations.MoveLast(writer, RegistryOperations.StudyScope(alpha), "second move", "test").IsOk);
        Thread.Sleep(1100);
        Assert.True(RegistryOperations.MoveLast(writer, RegistryOperations.CellScope(betaCells[0]), "third move", "test").IsOk);

        var claimOrder = RegistryOperations.ListRunnableCells(writer.Connection).Select(cell => cell.Name).ToList();
        Assert.Equal(["gamma-0", "beta-1", "alpha-0", "alpha-1", "beta-0"], claimOrder);

        var worker = new Worker();
        var (exit, _) = Drive(writer, worker, null, null, maxCycles: 8, serve: TimeSpan.FromMilliseconds(10));
        Assert.Equal(0, exit);
        Assert.Equal(claimOrder, worker.Ran);
    }

    /// <summary>
    /// The Gleam round's view of one cell, from a reading of this Registry as the
    /// supervisor of <see cref="Site"/> builds it: its status and its reasons' kinds.
    /// </summary>
    static (string Status, IReadOnlyList<string> Reasons) Explained(RegistryWriter writer, long siteId, long sourceId, string cellName)
    {
        var store = new SqliteStudyStore(writer);
        var reading = GleamStudyRound.Reading(
            store, store.ListUnfinishedCells(null), store.ReadDispatchOrder(), store.ListSites(),
            store.ListSiteSupervisors(), [siteId], 3, DateTimeOffset.UtcNow,
            store.ListSiteFailures());
        using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
        var answer = program.Ask(new System.Text.Json.Nodes.JsonObject { ["ask"] = "explain", ["observation"] = reading },
            TimeSpan.FromSeconds(30), null);
        Assert.True(answer.IsOk, answer.IsOk ? "" : answer.RefusalDetail);
        var cell = answer.Value["answer"]!["cells"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == cellName)!;
        return (cell["status"]!["kind"]!.GetValue<string>(),
            cell["reasons"]!.AsArray().Select(reason => reason!["kind"]!.GetValue<string>()).ToList());
    }

    /// <summary>
    /// The reader's review of slices M1 and M3: the claim and the round decide from
    /// the same rows. A supervisor holds the Site from before the failures, as on
    /// 2026-09-25; then a compiler refusal holds the source there in both, and three
    /// failures whose records carry only a message form one burst in both.
    /// </summary>
    [Fact]
    public void TheClaimAndTheRoundReadASitesFailuresAlike()
    {
        var (writer, siteId) = Home();
        using var owned = writer;
        var source = Source(writer, "5e87434e");
        Hold(writer, siteId, source);
        var (_, cells) = Study(writer, "refused", source, cells: 2);
        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "MSI", 41, "sha256:old").IsOk);
        Thread.Sleep(20);
        Failed(writer, cells[0], siteId, CellFailureDetail.Write(
            "the cell was refused by its runtime: runner_failed: the compiler refused the configuration: field x",
            deterministic: false, CellFailureDetail.ClassSite, code: RegistryOperations.CodeCompilerRefusedField,
            siteSpecific: true));

        Assert.False(RegistryOperations.TryStartAttempt(writer, cells[1], siteId, "{}").IsOk);
        var (status, reasons) = Explained(writer, siteId, source, "refused-1");
        Assert.Equal("needs_person", status);
        Assert.Equal(["compiler_does_not_know"], reasons);

        var other = Source(writer, "6f98545f");
        Hold(writer, siteId, other);
        var (_, burst) = Study(writer, "burst", other, cells: 4);
        foreach (var cellId in burst.Take(3))
        {
            // No `detail`: the sentence is the runtime's message, in both readers.
            Failed(writer, cellId, siteId, """{"message":"CUDA error: an illegal memory access","failure_class":"transient"}""");
        }
        Assert.Contains("failed the same way", Refusal(RegistryOperations.TryStartAttempt(writer, burst[3], siteId, "{}")),
            StringComparison.Ordinal);
        Assert.Equal(["repeated_on_site"], Explained(writer, siteId, other, "burst-3").Reasons);
    }

    static string Refusal<T>(RegistryResult<T> result) => result.TryGetRefusal(out var refusal) ? refusal.Detail : "(claimed)";

    // ── Slice W1: the office kept through the Gleam lease ───────────────

    /// <summary>A store whose renewals go unanswered while <see cref="Unreachable"/> is set, as a Home behind a broken tunnel.</summary>
    public class SilentRenewals : System.Reflection.DispatchProxy
    {
        public IStudyStore Inner { get; set; } = null!;
        public bool Unreachable { get; set; }
        public int Unanswered { get; private set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (Unreachable && targetMethod!.Name == nameof(IStudyStore.RenewSupervisor))
            {
                Unanswered++;
                throw new HomeUnreachableException("the Home did not answer within the client's retries");
            }
            try { return targetMethod!.Invoke(Inner, args); }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }

    /// <summary>A worker whose cell trains for a while and says whether it was cancelled.</summary>
    sealed class SlowWorker(Action onStart, TimeSpan trains) : StudyOrchestrator.ICellWorker
    {
        public bool Cancelled { get; private set; }

        public StudyOrchestrator.WorkerOutcome Run(StudyOrchestrator.ClaimedCell cell, CancellationToken cancellation)
        {
            onStart();
            Cancelled = cancellation.WaitHandle.WaitOne(trains);
            return Cancelled
                ? new(false, null, """{"detail":"cancelled"}""")
                : new(true, $$"""{"cell":"{{cell.CellName}}"}""", "{}");
        }
    }

    /// <summary>
    /// Slice W1, the 4090D on 2026-09-25: a supervisor that finds the office held
    /// waits for the other's lease to lapse and takes it, instead of exiting to
    /// be restarted. Without the Gleam program it exits, as before.
    /// </summary>
    [Fact]
    public void ASupervisorFindingTheOfficeHeldWaitsAndTakesItWhenTheLeaseLapses()
    {
        var (writer, siteId) = Home();
        using var owned = writer;
        var source = Source(writer, "5e87434e");
        Hold(writer, siteId, source);
        var (studyId, _) = Study(writer, "held", source, cells: 1);
        // Another supervisor holds the office for three seconds and never renews.
        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "old-host", 41, "sha256:old", leaseSeconds: 3).IsOk);

        // Without the lease: it exits at once, to be restarted.
        var plain = StudyOrchestrator.RunUntilIdle(new SqliteStudyStore(writer), new Worker(), studyId, "held", Site, 1,
            new StringWriter(), new StringWriter(), supervisor: new("new-host", 42, "sha256:new"), leaseSeconds: 3);
        Assert.Equal(StudyOrchestrator.ExitRetryable, plain);

        using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
        var worker = new Worker();
        var stdout = new StringWriter();
        var exit = StudyOrchestrator.RunUntilIdle(new SqliteStudyStore(writer), worker, studyId, "held", Site, 1,
            stdout, new StringWriter(), supervisor: new("new-host", 42, "sha256:new"), leaseSeconds: 3,
            round: new GleamStudyRound(program, Site));

        Assert.Equal(["held-0"], worker.Ran);
        Assert.Contains("the office is held by old-host/41; asking again in", stdout.ToString(), StringComparison.Ordinal);
        Assert.NotEqual(StudyOrchestrator.ExitRetryable, exit);
    }

    /// <summary>
    /// Slice W1, the night of 2026-09-25: renewals the Home does not answer while
    /// a cell trains no longer cancel the training; within the lease it finishes
    /// and is accepted. Without the Gleam program the same silence cancels it.
    /// </summary>
    [Fact]
    public void RenewalsNobodyAnswersDoNotCancelATrainingWithinTheLease()
    {
        (IStudyStore Store, SilentRenewals Proxy) Silent(RegistryWriter writer)
        {
            var store = System.Reflection.DispatchProxy.Create<IStudyStore, SilentRenewals>();
            var proxy = (SilentRenewals)(object)store;
            proxy.Inner = new SqliteStudyStore(writer);
            return (store, proxy);
        }

        // Without the lease: the first unanswered renewal cancels the cell.
        {
            var (writer, siteId) = Home();
            using var owned = writer;
            var source = Source(writer, "5e87434e");
            Hold(writer, siteId, source);
            var (studyId, _) = Study(writer, "plain", source, cells: 1);
            var (store, proxy) = Silent(writer);
            var worker = new SlowWorker(() => proxy.Unreachable = true, TimeSpan.FromSeconds(2.5));
            StudyOrchestrator.RunUntilIdle(store, worker, studyId, "plain", Site, 1, new StringWriter(), new StringWriter(),
                supervisor: new("MSI", 43, "sha256:new"), leaseSeconds: 6);
            Assert.True(worker.Cancelled);
        }

        // Through the lease: the cell trains to its end and is accepted.
        {
            var (writer, siteId) = Home();
            using var owned = writer;
            var source = Source(writer, "5e87434e");
            Hold(writer, siteId, source);
            var (studyId, cells) = Study(writer, "leased", source, cells: 1);
            var (store, proxy) = Silent(writer);
            var worker = new SlowWorker(() => proxy.Unreachable = true, TimeSpan.FromSeconds(2.5));
            using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
            StudyOrchestrator.RunUntilIdle(store, worker, studyId, "leased", Site, 1, new StringWriter(), new StringWriter(),
                supervisor: new("MSI", 44, "sha256:new"), leaseSeconds: 6, round: new GleamStudyRound(program, Site));
            Assert.False(worker.Cancelled);
            Assert.True(proxy.Unanswered > 0);
            using var command = writer.Connection.CreateCommand();
            command.CommandText = "SELECT accepted_attempt_id IS NOT NULL FROM cells WHERE id = $cell";
            command.Parameters.AddWithValue("$cell", cells[0]);
            Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
        }
    }

    [Fact]
    public void AFailureRecordThatNamesNoClassIsShownAndNotRetriedByItself()
    {
        var (writer, siteId) = Home();
        using var owned = writer;
        var source = Source(writer, "5e87434e");
        Hold(writer, siteId, source);
        var (studyId, cells) = Study(writer, "unread", source, cells: 1);
        Failed(writer, cells[0], siteId, "{}");
        // Long past the Registry's retry window, so the C# cycle, which reads
        // a missing class as transient, would offer it again now.
        using (var aged = writer.Connection.CreateCommand())
        {
            aged.CommandText = "UPDATE attempts SET ended_at = '2026-01-01T00:00:00.0000000+00:00'";
            aged.ExecuteNonQuery();
        }

        var worker = new Worker();
        var (_, stdout) = Drive(writer, worker, studyId, "unread", maxCycles: 0);

        Assert.Empty(worker.Ran);
        Assert.Contains(
            $"cell {cells[0]}: its last failure record names no class this scheduler knows (no class at all)",
            stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AServingRoundThatClaimsNothingSaysWhyOnce()
    {
        var (writer, siteId) = Home();
        using var owned = writer;
        var source = Source(writer, "5e87434e");
        Hold(writer, siteId, source);
        var (studyId, cells) = Study(writer, "stuck", source, cells: 1);
        Failed(writer, cells[0], siteId, CellFailureDetail.Write(
            "the card names no dataset", deterministic: true, CellFailureDetail.ClassDeterministic));

        var (exit, stdout) = Drive(writer, new Worker(), studyId, "stuck", maxCycles: 4, serve: TimeSpan.FromMilliseconds(10));

        Assert.Equal(0, exit);
        Assert.Contains("stopped after 4 cycle(s)", stdout, StringComparison.Ordinal);
        // Once as a notice and once in what the round rested on, in the first
        // cycle; the three cycles after it add nothing.
        Assert.Equal(2, Occurrences(stdout, "its last failure would repeat unchanged (the card names no dataset)"));
        Assert.Equal(1, Occurrences(stdout, "nothing can be claimed now"));
    }

    [Fact]
    public async Task TheCommandRunsTheRoundWhenTheSchedulerProgramIsNamed()
    {
        var (writer, siteId) = Home();
        using var owned = writer;
        var source = Source(writer, "5e87434e");
        Hold(writer, siteId, source);
        var (_, cells) = Study(writer, "named", source, cells: 1);
        Failed(writer, cells[0], siteId, CellFailureDetail.Write(
            "the card names no dataset", deterministic: true, CellFailureDetail.ClassDeterministic));
        var folder = Directory.CreateTempSubdirectory("gleam-round-").FullName;
        try
        {
            var run = await GleamCellAdmissionTests.RunCommand([
                "run-study-cells", "--store", writer.Connection.DataSource, "--study", "named",
                "--site", Site, "--python", "/no-trainer-in-this-test", "--fern-run", "/unused",
                "--fern-compile", "/unused", "--work-root", folder,
                "--scheduler-program", GleamSchedulerDecisionTests.Shipment()], folder);
            Assert.Equal(0, run.Exit);
            Assert.Contains($"cell {cells[0]}: its last failure would repeat unchanged (the card names no dataset)",
                run.Output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    /// <summary>A second Site, pod, that admits fern and holds the source this Site lacks.</summary>
    static long Pod(RegistryWriter writer, long sourceId)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO sites (name, capabilities_json, capacity_json, created_at)
            VALUES ('pod', '{"device":"cuda","admitted_models":["fern"]}', '{"slots":1}', '2026-09-25')
            RETURNING id
            """;
        var podId = Convert.ToInt64(command.ExecuteScalar());
        Hold(writer, podId, sourceId);
        return podId;
    }

    /// <summary>
    /// Slice S2 of how-a-site-is-reached-and-started. A cell only pod can take,
    /// and pod's supervisor stopped renewing twenty minutes ago: the round on
    /// this Site says so, with the moment. Before S2 every other Site was sent
    /// as answering, and nothing was said.
    /// </summary>
    /// <summary>
    /// CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): the tests
    /// of a cell only one other Site can take used to leave the local Site
    /// without the source; no Site holds sources now, so the local Site admits
    /// no model instead.
    /// </summary>
    static void AdmitNothingHere(RegistryWriter writer)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "UPDATE sites SET capabilities_json = json_set(capabilities_json, '$.admitted_models', json('[]')) WHERE name = $site";
        command.Parameters.AddWithValue("$site", Site);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    [Fact]
    public void ACellOnlyASilentSiteCanTakeIsNamedWithTheMoment()
    {
        var (writer, _) = Home();
        using var owned = writer;
        AdmitNothingHere(writer);
        var source = Source(writer, "5e87434e");
        var podId = Pod(writer, source);
        Assert.True(RegistryOperations.StartSupervisor(writer, podId, "pod-host", 7, "sha256:pod", 60).IsOk);
        using (var expire = writer.Connection.CreateCommand())
        {
            expire.CommandText = "UPDATE supervisors SET lease_until = $until";
            expire.Parameters.AddWithValue("$until",
                DateTimeOffset.UtcNow.AddMinutes(-20).ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            expire.ExecuteNonQuery();
        }
        var (studyId, cells) = Study(writer, "elsewhere", source, cells: 1);

        var worker = new Worker();
        var (_, stdout) = Drive(writer, worker, studyId, "elsewhere", maxCycles: 0);

        Assert.Empty(worker.Ran);
        Assert.Contains("Site pod has not answered for 20 min", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ACellOnlyAnUnsupervisedSiteCanTakeNamesThatNoSupervisorEverRan()
    {
        var (writer, _) = Home();
        using var owned = writer;
        AdmitNothingHere(writer);
        var source = Source(writer, "5e87434e");
        Pod(writer, source);
        var (studyId, _) = Study(writer, "unsupervised", source, cells: 1);

        var (_, stdout) = Drive(writer, new Worker(), studyId, "unsupervised", maxCycles: 0);

        Assert.Contains("Site pod has never had a supervisor", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"failure_class":"site"}""", "site")]
    [InlineData("""{"deterministic":true}""", "deterministic")]
    [InlineData("""{"deterministic":false,"site_specific":true}""", "site")]
    [InlineData("""{"deterministic":false}""", "transient")]
    [InlineData("{}", null)]
    [InlineData("not json", null)]
    [InlineData(null, null)]
    public void AFailureClassIsReadAsWrittenAndNeverDefaulted(string? detail, string? expected) =>
        Assert.Equal(expected, GleamStudyRound.FailureClass(detail));
}
