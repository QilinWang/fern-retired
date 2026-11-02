using Fern.Registry;
using Fern.Scheduler;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// `retry-cell`: the way back for a cell that is stuck or was fixed.
///
/// The case that made it necessary is the first test here: an attempt left open
/// by a supervisor that was killed excludes its cell from every later claim, and
/// before this verb no command could free it.
/// </summary>
public sealed class StudyCellRetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableProgressDoesNotPermitClosingAnOpenAttempt(bool useGleam)
    {
        var (writer, studyId, cells, siteId) = Home();
        using var owned = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[0], siteId, "{}").IsOk);
        using var change = writer.Connection.CreateCommand();
        change.CommandText = "UPDATE attempts SET last_progress_at = 'unreadable'";
        change.ExecuteNonQuery();
        using var program = useGleam ? GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment()) : null;
        var result = StudyCellRetry.Request(writer, studyId, "cell-0", "owner", "check the silent supervisor",
            implementedBy: program is null ? null : new GleamStudyCellRetryDecision(program));
        Assert.False(result.IsOk);
        Assert.Contains("silence is unknown", RefusalOf(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("failed", false)]
    [InlineData("lost", false)]
    [InlineData("cancelled", false)]
    [InlineData("failed", true)]
    [InlineData("lost", true)]
    [InlineData("cancelled", true)]
    public void ExplicitRetryGrantsOneAttemptAfterTheAutomaticBudgetIsSpent(string endState, bool useGleam)
    {
        var (writer, studyId, cells, siteId) = Home();
        using var scope = writer;
        for (var i = 0; i < RegistryOperations.DefaultMaxAttempts; i++)
        {
            var started = RegistryOperations.TryStartAttempt(writer, cells[0], siteId, "{}");
            Assert.True(started.TryGetValue(out var attempt));
            Assert.True(RegistryOperations.FailAttempt(writer, attempt.AttemptId, attempt.FencingEpoch,
                i == RegistryOperations.DefaultMaxAttempts - 1 ? endState : "failed", "{}").IsOk);
            using var elapsed = writer.Connection.CreateCommand();
            elapsed.CommandText = "UPDATE attempts SET ended_at = '2020-01-01T00:00:00Z' WHERE id = $id";
            elapsed.Parameters.AddWithValue("$id", attempt.AttemptId);
            elapsed.ExecuteNonQuery();
        }
        Assert.Empty(RegistryOperations.ListRunnableCells(writer.Connection, studyId));
        using var program = useGleam ? GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment()) : null;
        Assert.True(StudyCellRetry.Request(writer, studyId, "cell-0", "owner", "store fixed",
            implementedBy: program is null ? null : new GleamStudyCellRetryDecision(program)).IsOk);
        Assert.Single(RegistryOperations.ListRunnableCells(writer.Connection, studyId));
        var next = RegistryOperations.TryStartAttempt(writer, cells[0], siteId, "{}");
        Assert.True(next.TryGetValue(out var granted));
        Assert.Equal(RegistryOperations.DefaultMaxAttempts + 1, granted.Ordinal);
        Assert.True(RegistryOperations.FailAttempt(writer, granted.AttemptId, granted.FencingEpoch, "failed", "{}").IsOk);
        Assert.Empty(RegistryOperations.ListRunnableCells(writer.Connection, studyId));
        Assert.False(RegistryOperations.TryStartAttempt(writer, cells[0], siteId, "{}").IsOk);
    }
    const string Config = """{"schema":"fern.training.v2","model":{"type":"fern"},"runtime":{"device":"cuda"}}""";

    [Fact]
    public void BothDecideFromTheSameCompleteObservationAndShareTheSchedulerProgram()
    {
        using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
        var gleam = new GleamStudyCellRetryDecision(program);
        var attempted = new StudyCellRetry.CellRow(1, null, 10, 1, 1, "opened", "pod", "2026-09-24", null, null);
        StudyCellRetry.CellRow?[] cells = [null,
            attempted with { AcceptedAttemptId = 10 }, attempted with { LatestAttemptId = null }, attempted,
            attempted with { LatestAttemptState = "running" }, attempted with { LatestAttemptState = "failed" },
            attempted with { LatestAttemptState = "lost" }, attempted with { LatestAttemptState = "cancelled" }];
        foreach (var state in new[] { "run", "cancel", "pause" })
        foreach (var cell in cells)
        foreach (var silent in new long?[] { null, -1, 0, 5999999999L, 6000000000L, 6000000001L })
        foreach (var force in new[] { false, true })
        {
            var observation = new StudyCellRetry.Observation(state, cell, silent, 6000000000L, force);
            Assert.Equal(CSharpStudyCellRetryDecision.Instance.Decide(observation), gleam.Decide(observation));
        }
        Assert.Empty(new GleamSchedulerDecision(program).Choose([], [], new Dictionary<long, int>()).Placements);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForcingUnknownSilenceRecordsTheOverrideHonestly(bool useGleam)
    {
        var (writer, studyId, cells, siteId) = Home();
        using var owned = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[0], siteId, "{}").IsOk);
        using var change = writer.Connection.CreateCommand();
        change.CommandText = "UPDATE attempts SET last_progress_at = 'unreadable'";
        change.ExecuteNonQuery();
        using var program = useGleam ? GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment()) : null;
        var result = StudyCellRetry.Request(writer, studyId, "cell-0", "owner", "supervisor was checked", force: true,
            implementedBy: program is null ? null : new GleamStudyCellRetryDecision(program));
        Assert.True(result.TryGetValue(out var retried));
        Assert.True(retried.Forced);
        Assert.Null(retried.SilentFor);
        Assert.Contains("forced: supervisor silence was unknown", StudyCellRetry.Sentence(retried, "stuck"), StringComparison.Ordinal);
        change.CommandText = "SELECT detail_json FROM events ORDER BY id DESC LIMIT 1";
        Assert.Contains("\"forced\":true", (string)change.ExecuteScalar()!, StringComparison.Ordinal);
    }

    sealed class ChangingDecision(IStudyCellRetryDecision inner, Action changed) : IStudyCellRetryDecision
    {
        public StudyCellRetry.Decision Decide(StudyCellRetry.Observation observed)
        {
            var answer = inner.Decide(observed);
            changed();
            return answer;
        }
    }

    [Theory]
    [InlineData("heartbeat")]
    [InlineData("cancel")]
    [InlineData("accepted")]
    [InlineData("epoch")]
    [InlineData("replacement")]
    public void ChangedFactsCannotBeOverriddenByAnEarlierGleamPermission(string mutation)
    {
        var (writer, studyId, cells, siteId) = Home();
        using var owned = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[0], siteId, "{}").TryGetValue(out var attempt));
        Silent(writer, attempt.AttemptId, TimeSpan.FromHours(2));
        using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
        // A second connection proves the decision is requested outside a write transaction.
        using var competing = RegistryHome.OpenWriter(writer.Connection.DataSource);
        long eventsAfterMutation = -1;
        var decision = new ChangingDecision(new GleamStudyCellRetryDecision(program), () =>
        {
            switch (mutation)
            {
                case "heartbeat":
                    Silent(competing, attempt.AttemptId, TimeSpan.Zero);
                    break;
                case "cancel":
                    Assert.True(RegistryOperations.RequestStudyState(competing, studyId, "cancel").IsOk);
                    break;
                case "accepted":
                    Assert.True(RegistryOperations.FinishAttempt(competing, attempt.AttemptId, attempt.FencingEpoch,
                        """{"schema":"fern.study_unit_results.v4"}""").IsOk);
                    break;
                case "epoch":
                    using (var update = competing.Connection.CreateCommand())
                    {
                        update.CommandText = "UPDATE attempts SET fencing_epoch = fencing_epoch + 1";
                        update.ExecuteNonQuery();
                    }
                    break;
                case "replacement":
                    Assert.True(RegistryOperations.FailAttempt(competing, attempt.AttemptId, attempt.FencingEpoch, "lost", "{}").IsOk);
                    using (var update = competing.Connection.CreateCommand())
                    {
                        update.CommandText = "UPDATE attempts SET ended_at = '2020-01-01T00:00:00Z'";
                        update.ExecuteNonQuery();
                    }
                    Assert.True(RegistryOperations.TryStartAttempt(competing, cells[0], siteId, "{}").IsOk);
                    break;
            }
            eventsAfterMutation = EventCount(competing);
        });
        var result = StudyCellRetry.Request(writer, studyId, "cell-0", "owner", "supervisor vanished", implementedBy: decision);
        Assert.Contains("changed while its retry was being decided", RefusalOf(result), StringComparison.Ordinal);
        Assert.Equal(eventsAfterMutation, EventCount(writer));
    }

    static long EventCount(RegistryWriter writer)
    {
        using var read = writer.Connection.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM events";
        return (long)read.ExecuteScalar()!;
    }

    [Fact]
    public async Task TheGleamCommandRefusesWithoutWritingThenRecordsAnExplicitForce()
    {
        var (writer, studyId, cells, siteId) = Home();
        using var owned = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[0], siteId, "{}").IsOk);
        using var change = writer.Connection.CreateCommand();
        change.CommandText = "UPDATE attempts SET last_progress_at = 'unreadable'";
        change.ExecuteNonQuery();
        var before = EventCount(writer);
        var expected = RefusalOf(StudyCellRetry.Request(writer, studyId, "cell-0", "owner", "checked"));
        string[] arguments = ["retry-cell", "--registry", writer.Connection.DataSource, "--study", "stuck", "--cell", "cell-0",
            "--by", "owner", "--reason", "checked", "--scheduler-program", GleamSchedulerDecisionTests.Shipment()];
        var refused = await GleamCellAdmissionTests.RunCommand(arguments, Path.GetTempPath());
        Assert.Equal(3, refused.Exit);
        Assert.Empty(refused.Output);
        Assert.Equal($"retry-cell: {expected}{Environment.NewLine}", refused.Error);
        Assert.Equal(before, EventCount(writer));
        var forced = await GleamCellAdmissionTests.RunCommand([.. arguments, "--force"], Path.GetTempPath());
        Assert.True(forced.Exit == 0, forced.Error);
        Assert.Contains("is now lost", forced.Output, StringComparison.Ordinal);
        Assert.Contains("forced: supervisor silence was unknown", forced.Output, StringComparison.Ordinal);
        Assert.Single(RegistryOperations.ListRunnableCells(writer.Connection, studyId));
        Assert.Equal(before + 1, EventCount(writer));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("read request\nprintf '%s\\n' '{\"id\":\"1\",\"answer\":{\"kind\":\"allowed\"}}'\n")]
    public async Task AFailedSelectedProgramCannotRecordARetry(string? entrypoint)
    {
        var (writer, _, cells, siteId) = Home();
        using var owned = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[0], siteId, "{}").TryGetValue(out var attempt));
        Assert.True(RegistryOperations.FailAttempt(writer, attempt.AttemptId, attempt.FencingEpoch, "failed", "{}").IsOk);
        var before = EventCount(writer);
        var folder = Directory.CreateTempSubdirectory("failed-retry-program-").FullName;
        try
        {
            if (entrypoint is not null) File.WriteAllText(Path.Combine(folder, "entrypoint.sh"), entrypoint);
            var result = await GleamCellAdmissionTests.RunCommand(["retry-cell", "--registry", writer.Connection.DataSource,
                "--study", "stuck", "--cell", "cell-0", "--reason", "checked", "--scheduler-program", folder], folder);
            Assert.Equal(1, result.Exit);
            Assert.Contains("selected Gleam retry decision failed", result.Error, StringComparison.Ordinal);
            Assert.Equal(before, EventCount(writer));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    static (RegistryWriter Writer, long StudyId, IReadOnlyList<long> CellIds, long SiteId) Home(int cells = 1)
    {
        var writer = RegistryHome.OpenWriter(TestRegistry.CreateRegistry());
        long sourceId;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
                VALUES ('5e87434e', '[]', 'commit', '2026-09-11') RETURNING id
                """;
            sourceId = Convert.ToInt64(command.ExecuteScalar());
        }
        var site = RegistryOperations.RegisterSite(
            writer, "local-4070", """{"device":"cuda","admitted_models":["fern"]}""", """{"slots":1}""");
        Assert.True(site.TryGetValue(out var registeredSite));
        var declared = Enumerable.Range(0, cells).Select(ordinal => new RegistryOperations.CompiledCell(
            ordinal, $"cell-{ordinal}", "lorenz.series", 42, Config, """{"device":"cuda","requires_cuda":true}""", """{"unit_results":"retained"}""")).ToList();
        var study = RegistryOperations.RegisterStudy(
            writer, "stuck", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, declared);
        Assert.True(study.TryGetValue(out var registered));
        TestRegistry.DeclareCodeFrom(writer, registeredSite.SiteId);
        return (writer, registered.StudyId, registered.CellIds, registeredSite.SiteId);
    }

    static string RefusalOf(RegistryResult<StudyCellRetry.Retried> result)
    {
        Assert.False(result.IsOk);
        result.TryGetRefusal(out var refusal);
        return refusal.Detail;
    }

    /// <summary>Backdate an attempt's last progress, the way a supervisor that died hours ago leaves it.</summary>
    static void Silent(RegistryWriter writer, long attemptId, TimeSpan ago)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "UPDATE attempts SET last_progress_at = $at WHERE id = $id";
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.Subtract(ago).ToString("o"));
        command.Parameters.AddWithValue("$id", attemptId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A supervisor that was killed leaves its attempt open, and the loop itself
    /// takes the cell back once the silence passes the bound — nobody types
    /// anything. What the verb adds for that case is the RECORD: who decided the
    /// machine was gone and why, closed explicitly rather than by a timer.
    ///
    /// CHG 2026-09-11: this test used to assert the cell was unclaimable until
    /// the verb ran. It is claimable on its own now, and that is the fix, not a
    /// regression.
    ///
    /// CHG 2026-09-26 (slice W2 of leases-and-watching-a-running-cell, the
    /// owner's choice: notify, do not re-dispatch): unclaimable until the verb
    /// runs, again. A silent attempt may still be training on a machine nobody
    /// can reach; the Home tells the owner after 30 minutes, and this verb is
    /// how a person gives the attempt up.
    /// </summary>
    [Fact]
    public void ASilentAttemptWaitsForTheVerbWhichRecordsTheDecision()
    {
        var (writer, studyId, cellIds, siteId) = Home();
        using var _ = writer;
        var started = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        Assert.True(started.TryGetValue(out var attempt));
        Silent(writer, attempt.AttemptId, TimeSpan.FromHours(9));
        Assert.Empty(RegistryOperations.ListRunnableCells(writer.Connection, studyId));

        var requested = StudyCellRetry.Request(writer, studyId, "cell-0", "qilin", "the supervisor was killed");

        Assert.True(requested.TryGetValue(out var retried));
        Assert.True(retried.ClosedOpenAttempt);
        Assert.False(retried.Forced);
        Assert.Equal("opened", retried.PreviousState);
        Assert.Single(RegistryOperations.ListRunnableCells(writer.Connection, studyId));
        var sentence = StudyCellRetry.Sentence(retried, "stuck");
        Assert.Contains("is now lost", sentence, StringComparison.Ordinal);
        Assert.Contains("local-4070", sentence, StringComparison.Ordinal);
        Assert.Contains("last progress", sentence, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure this guard exists for: an attempt that is still reporting
    /// progress belongs to a process that is probably still training, and taking
    /// its cell away would let a second machine claim the same cell. The refusal
    /// prints what the store knows about the attempt, including that it knows no
    /// pid or host.
    /// </summary>
    [Fact]
    public void AnAttemptThatIsStillReportingProgressIsRefusedWithItsFacts()
    {
        var (writer, studyId, cellIds, siteId) = Home();
        using var _ = writer;
        var started = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        Assert.True(started.TryGetValue(out var attempt));
        Assert.True(RegistryOperations.RenewAttempt(
            writer, attempt.AttemptId, attempt.FencingEpoch, "pod-run-7").IsOk);

        var refusal = RefusalOf(StudyCellRetry.Request(
            writer, studyId, "cell-0", "qilin", "impatient", TimeSpan.FromHours(6)));

        Assert.Contains("a supervisor may still be training it", refusal, StringComparison.Ordinal);
        Assert.Contains("local-4070", refusal, StringComparison.Ordinal);
        Assert.Contains("remote execution pod-run-7", refusal, StringComparison.Ordinal);
        Assert.Contains("no runner pid or host", refusal, StringComparison.Ordinal);
        Assert.Contains("--force", refusal, StringComparison.Ordinal);
        Assert.Empty(RegistryOperations.ListRunnableCells(writer.Connection, studyId));
    }

    /// <summary>
    /// The operator who has read those facts and knows the machine is gone can
    /// still say so. The record says it was forced, so what was overridden is
    /// readable afterwards.
    /// </summary>
    [Fact]
    public void ForceDeclaresALiveLookingAttemptLostAndSaysItWasForced()
    {
        var (writer, studyId, cellIds, siteId) = Home();
        using var _ = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}").IsOk);

        var requested = StudyCellRetry.Request(
            writer, studyId, "cell-0", "qilin", "the pod was destroyed", TimeSpan.FromHours(6), force: true);

        Assert.True(requested.TryGetValue(out var retried));
        Assert.True(retried.ClosedOpenAttempt);
        Assert.True(retried.Forced);
        Assert.Single(RegistryOperations.ListRunnableCells(writer.Connection, studyId));
        Assert.Contains("(forced: it was still inside the silence bound)",
            StudyCellRetry.Sentence(retried, "stuck"), StringComparison.Ordinal);
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT detail_json FROM events ORDER BY id DESC LIMIT 1";
        var detail = command.ExecuteScalar() as string ?? "";
        Assert.Contains("\"forced\":true", detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failed cell is already claimable by the next run of the command, so the
    /// verb changes no state — it records the decision, which is the part that
    /// did not exist, and monitor-study prints it beside the cell.
    /// </summary>
    [Fact]
    public void AFailedCellKeepsItsStateAndGainsTheReasonAPersonGave()
    {
        var (writer, studyId, cellIds, siteId) = Home();
        using var _ = writer;
        var started = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        Assert.True(started.TryGetValue(out var attempt));
        Assert.True(RegistryOperations.FailAttempt(
            writer, attempt.AttemptId, attempt.FencingEpoch, RegistryOperations.AttemptFailed,
            CellFailureDetail.Write("unknown dataset 'pointmaze.physical_state'", deterministic: false, CellFailureDetail.ClassTransient)).IsOk);

        // A terminal attempt is not alive, so no bound applies to it.
        var requested = StudyCellRetry.Request(
            writer, studyId, "cell-0", "qilin", "installed the PointMaze dataset");

        Assert.True(requested.TryGetValue(out var retried));
        Assert.False(retried.ClosedOpenAttempt);
        Assert.Equal("failed", retried.PreviousState);
        var view = RegistryOperations.ReadStudy(writer.Connection, studyId);
        Assert.True(view.TryGetValue(out var head));
        var rendered = StudyProjection.Render(writer.Connection, head);
        Assert.Contains("why: retry requested by qilin: installed the PointMaze dataset", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ACellWhoseResultWasAcceptedIsRefusedByName()
    {
        var (writer, studyId, cellIds, siteId) = Home();
        using var _ = writer;
        var started = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        Assert.True(started.TryGetValue(out var attempt));
        Assert.True(RegistryOperations.FinishAttempt(
            writer, attempt.AttemptId, attempt.FencingEpoch, """{"schema":"fern.study_unit_results.v4"}""").IsOk);

        var refusal = RefusalOf(StudyCellRetry.Request(writer, studyId, "cell-0", "qilin", "second thoughts"));

        Assert.Contains("already has accepted attempt", refusal, StringComparison.Ordinal);
        Assert.Contains("a cell accepts one result", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ACellOfACancelledStudyIsRefusedByName()
    {
        var (writer, studyId, cellIds, siteId) = Home();
        using var _ = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}").IsOk);
        Assert.True(RegistryOperations.RequestStudyState(writer, studyId, "cancel").IsOk);

        var refusal = RefusalOf(StudyCellRetry.Request(writer, studyId, "cell-0", "qilin", "anything"));

        Assert.Contains("is cancelled", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ACellNobodyHasAttemptedIsToldItIsAlreadyClaimable()
    {
        var (writer, studyId, _, _) = Home();
        using var _1 = writer;

        var refusal = RefusalOf(StudyCellRetry.Request(writer, studyId, "cell-0", "qilin", "impatience"));

        Assert.Contains("has never been attempted", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCellAndAnEmptyReasonAreBothRefusedByName()
    {
        var (writer, studyId, cellIds, siteId) = Home();
        using var _ = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}").IsOk);

        Assert.Contains("has no cell named elsewhere",
            RefusalOf(StudyCellRetry.Request(writer, studyId, "elsewhere", "qilin", "why not")), StringComparison.Ordinal);
        Assert.Contains("records why it was asked for",
            RefusalOf(StudyCellRetry.Request(writer, studyId, "cell-0", "qilin", "  ")), StringComparison.Ordinal);
    }

    /// <summary>An earlier attempt's result is never touched: the history is what it was, with one record on the end.</summary>
    [Fact]
    public void EarlierAttemptsKeepTheirRowsAndTheirResults()
    {
        var (writer, studyId, cellIds, siteId) = Home();
        using var _ = writer;
        var first = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        Assert.True(first.TryGetValue(out var one));
        Assert.True(RegistryOperations.FailAttempt(
            writer, one.AttemptId, one.FencingEpoch, RegistryOperations.AttemptFailed, """{"detail":"first"}""").IsOk);

        Assert.True(StudyCellRetry.Request(writer, studyId, "cell-0", "qilin", "fixed it").IsOk);

        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT state FROM attempts WHERE id = $id";
        command.Parameters.AddWithValue("$id", one.AttemptId);
        Assert.Equal("failed", command.ExecuteScalar() as string);
    }
}
