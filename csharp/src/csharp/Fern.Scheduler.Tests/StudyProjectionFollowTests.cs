using Fern.Registry;
using Fern.Scheduler;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// `monitor-study --follow`: the study → cell → attempt projection, re-read on
/// an interval until the study has nothing left to wait for.
///
/// The interval here is one millisecond and there is no process, no worker and
/// no GPU: the loop is separated from the option parsing precisely so it can be
/// driven like this.
/// </summary>
public sealed class StudyProjectionFollowTests
{
    static (RegistryWriter Writer, long StudyId, IReadOnlyList<long> CellIds, long SiteId) Home(int cells = 2)
    {
        var path = TestRegistry.CreateRegistry();
        var writer = RegistryHome.OpenWriter(path);
        long sourceId, siteId;
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
                """
                INSERT INTO sites (name, capabilities_json, capacity_json, created_at)
                VALUES ('local-4070', '{"device":"cuda","admitted_models":["fern"]}', '{"slots":2}', '2026-09-11') RETURNING id
                """;
            siteId = Convert.ToInt64(command.ExecuteScalar());
        }
        var declared = Enumerable.Range(0, cells).Select(ordinal => new RegistryOperations.CompiledCell(
            ordinal, $"cell-{ordinal}", "rossler_param_shock.series", 42,
            """{"schema":"fern.training.v2","model":{"type":"fern"}}""",
            """{"device":"cuda","requires_cuda":true}""", """{"unit_results":"retained"}""")).ToList();
        var registered = RegistryOperations.RegisterStudy(
            writer, "watched", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, declared);
        Assert.True(registered.TryGetValue(out var value));
        TestRegistry.DeclareCodeFrom(writer, siteId);
        return (writer, value.StudyId, value.CellIds, siteId);
    }

    static (int Exit, string Out) Run(RegistryWriter writer, long studyId, bool follow, CancellationToken cancellation = default)
    {
        var stdout = new StringWriter();
        var exit = StudyProjection.Follow(writer.Connection, studyId, follow, 1, stdout, new StringWriter(), cancellation);
        return (exit, stdout.ToString());
    }

    // ── the projection ───────────────────────────────────────────────────────

    /// <summary>
    /// The fields PlanCellWatch re-read that still exist: state, Site, attempt
    /// ordinal, fencing epoch, how many tries the cell has had, and when it
    /// last reported.
    /// </summary>
    [Fact]
    public void TheProjectionCarriesTheAttemptFactsThatSurvived()
    {
        var (writer, studyId, cellIds, siteId) = Home(cells: 1);
        using var _ = writer;
        var started = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        Assert.True(started.TryGetValue(out var attempt));
        Assert.True(RegistryOperations.RenewAttempt(writer, attempt.AttemptId, attempt.FencingEpoch, "pod-run-7").IsOk);

        var (exit, output) = Run(writer, studyId, follow: false);

        Assert.Equal(0, exit);
        Assert.Contains("study watched revision 1", output, StringComparison.Ordinal);
        Assert.Contains("0/1 cells accepted", output, StringComparison.Ordinal);
        Assert.Contains("cell-0", output, StringComparison.Ordinal);
        Assert.Contains("running", output, StringComparison.Ordinal);
        Assert.Contains("local-4070", output, StringComparison.Ordinal);
        Assert.Contains("remote=pod-run-7", output, StringComparison.Ordinal);
        // No stage word reaches this surface.
        Assert.DoesNotContain("stage", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Plan", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failed cell says WHY on the next line.
    ///
    /// On 2026-09-11 a study refused thirteen cells in a millisecond each with
    /// "unknown dataset ... datasets: []" and this projection said only
    /// "failed", which sends a person to a worker folder to learn something the
    /// Registry already held.
    /// </summary>
    [Fact]
    public void AFailedCellSaysWhy()
    {
        var (writer, studyId, cellIds, siteId) = Home(cells: 1);
        using var _ = writer;
        var started = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        Assert.True(started.TryGetValue(out var attempt));
        Assert.True(RegistryOperations.FailAttempt(
            writer, attempt.AttemptId, attempt.FencingEpoch, RegistryOperations.AttemptFailed,
            CellFailureDetail.Write(
                "the cell was refused by its runtime: runner_failed: use: unknown dataset 'pointmaze.physical_state'; datasets: []",
                deterministic: false, CellFailureDetail.ClassTransient, outcomeKind: "failure", code: "runner_failed")).IsOk);

        var (exit, output) = Run(writer, studyId, follow: false);

        Assert.Equal(0, exit);
        Assert.Contains("failed", output, StringComparison.Ordinal);
        Assert.Contains("why: the cell was refused by its runtime", output, StringComparison.Ordinal);
        Assert.Contains("unknown dataset 'pointmaze.physical_state'", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cell can be accepted and still have its result only in the attempt's
    /// folder, because the science store would not take it. That is not a
    /// failure and must not read as one — but a person about to collect the
    /// study has to see it on the cell's line.
    /// </summary>
    [Fact]
    public void AnAcceptedCellWhoseResultTheStoreRefusedSaysSoOnItsLine()
    {
        var (writer, studyId, cellIds, siteId) = Home(cells: 1);
        using var _ = writer;
        var started = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        Assert.True(started.TryGetValue(out var attempt));
        Assert.True(RegistryOperations.RecordEvent(
            writer, attempt.AttemptId, attempt.FencingEpoch, StudyCollect.ResultNotSaved, null,
            CellFailureDetail.Write(
                "result kept by the Home, store refused: science.db is on a read-only mount (the store it tried was /results/science.db)",
                deterministic: false, CellFailureDetail.ClassSite)).IsOk);
        Assert.True(RegistryOperations.FinishAttempt(
            writer, attempt.AttemptId, attempt.FencingEpoch, """{"schema":"fern.study_unit_results.v4"}""").IsOk);

        var (exit, output) = Run(writer, studyId, follow: false);

        Assert.Equal(0, exit);
        Assert.Contains("accepted", output, StringComparison.Ordinal);
        Assert.Contains("note: result kept by the Home, store refused", output, StringComparison.Ordinal);
        Assert.Contains("read-only mount", output, StringComparison.Ordinal);
        Assert.DoesNotContain("why:", output, StringComparison.Ordinal);
    }

    /// <summary>A cell nobody has claimed is `waiting`, not missing.</summary>
    [Fact]
    public void AnUnclaimedCellIsWaiting()
    {
        var (writer, studyId, _, _) = Home(cells: 2);
        using var _1 = writer;
        var (_, output) = Run(writer, studyId, follow: false);
        var lines = output.Split('\n');
        Assert.Equal(2, lines.Count(line => line.StartsWith("  cell-", StringComparison.Ordinal) && line.Contains("  waiting  ", StringComparison.Ordinal)));
        // CHG 2026-09-11 night (slice 7, row 36): each waiting cell says why.
        Assert.Equal(2, lines.Count(line => line.StartsWith("      waiting: ", StringComparison.Ordinal)));
    }

    // ── the follow ───────────────────────────────────────────────────────────

    /// <summary>
    /// A finished study ends the follow on its own. A follow that never
    /// returned would be a command an operator has to remember to kill.
    /// </summary>
    [Fact]
    public void AFollowReturnsWhenEveryCellIsAccepted()
    {
        var (writer, studyId, cellIds, siteId) = Home(cells: 1);
        using var _ = writer;
        var started = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        Assert.True(started.TryGetValue(out var attempt));
        Assert.True(RegistryOperations.FinishAttempt(writer, attempt.AttemptId, attempt.FencingEpoch, "{}").IsOk);

        var (exit, output) = Run(writer, studyId, follow: true);

        Assert.Equal(0, exit);
        Assert.Contains("1/1 cells accepted", output, StringComparison.Ordinal);
        Assert.Contains("accepted", output, StringComparison.Ordinal);
    }

    /// <summary>A paused or cancelled study also ends it: no new cell will be claimed.</summary>
    [Theory]
    [InlineData("pause")]
    [InlineData("cancel")]
    public void AFollowReturnsWhenTheStudyIsNotRunning(string state)
    {
        var (writer, studyId, _, _) = Home();
        using var _1 = writer;
        Assert.True(RegistryOperations.RequestStudyState(writer, studyId, state).IsOk);

        var (exit, output) = Run(writer, studyId, follow: true);

        Assert.Equal(0, exit);
        Assert.Contains($"state={state}", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// It prints only when the projection CHANGED. A lease renewal or a
    /// progress timestamp is not a change a person is watching for, and a
    /// follow that reprinted every tick would bury the one line that matters
    /// under a thousand identical ones.
    /// </summary>
    [Fact]
    public void AFollowPrintsOnceWhenNothingMoves()
    {
        var (writer, studyId, _, _) = Home();
        using var _1 = writer;
        Assert.True(RegistryOperations.RequestStudyState(writer, studyId, "pause").IsOk);

        var (_, output) = Run(writer, studyId, follow: true);

        Assert.Equal(1, output.Split("study watched").Length - 1);
    }

    /// <summary>Stopping the follow is the user's, and it returns 0 rather than throwing.</summary>
    [Fact]
    public void ACancelledFollowReturnsCleanly()
    {
        var (writer, studyId, _, _) = Home();
        using var _1 = writer;
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(30));

        var (exit, output) = Run(writer, studyId, follow: true, cancellation.Token);

        Assert.Equal(0, exit);
        Assert.Contains("study watched", output, StringComparison.Ordinal);
    }

    /// <summary>A follow that meets a moving study prints each distinct state once.</summary>
    [Fact]
    public async Task AFollowPrintsAgainWhenACellMoves()
    {
        var (writer, studyId, cellIds, siteId) = Home(cells: 1);
        using var _ = writer;
        var stdout = new StringWriter();

        // Move the cell on a second thread while the follow runs.
        var started = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        Assert.True(started.TryGetValue(out var attempt));
        using var cancellation = new CancellationTokenSource();
        var mover = Task.Run(() =>
        {
            Thread.Sleep(40);
            RegistryOperations.FinishAttempt(writer, attempt.AttemptId, attempt.FencingEpoch, "{}");
        });
        var exit = StudyProjection.Follow(writer.Connection, studyId, true, 1, stdout, new StringWriter(), cancellation.Token);
        await mover;

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("0/1 cells accepted", output, StringComparison.Ordinal);
        Assert.Contains("1/1 cells accepted", output, StringComparison.Ordinal);
    }

    // ── slice 7 (row 36): why a cell is not running, who holds the one that is, and its events ──

    static string Rendered(RegistryWriter writer, long studyId, bool events = false)
    {
        Assert.True(RegistryOperations.ReadStudy(writer.Connection, studyId).TryGetValue(out var head));
        return StudyProjection.Render(writer.Connection, head, events: events);
    }

    [Fact]
    public void AHeldCellSaysWhoHoldsItAndHowToReleaseIt()
    {
        var (writer, studyId, cellIds, _) = Home(cells: 1);
        using var _ = writer;
        Assert.True(RegistryOperations.RequestOperator(
            writer, RegistryOperations.RequestHold, RegistryOperations.CellScope(cellIds[0]), "look at its card first", "qilin").TryGetValue(out var request));

        var rendered = Rendered(writer, studyId);

        Assert.Contains($"waiting: held by qilin: look at its card first (release-request --request {request})", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void APlaceableCellWaitsForTheSupervisorOfItsSiteAndSaysWhetherAnyoneHoldsTheOffice()
    {
        var (writer, studyId, _, siteId) = Home(cells: 1);
        using var _ = writer;

        Assert.Contains(
            "waiting: placeable on Site local-4070, whose office no supervisor has ever held (install-supervisor-service",
            Rendered(writer, studyId), StringComparison.Ordinal);

        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "this-machine", 4242, "sha256:cafe", 600).IsOk);
        Assert.Contains(
            "waiting: placeable on Site local-4070; its supervisor this-machine/4242 claims at its next cycle",
            Rendered(writer, studyId), StringComparison.Ordinal);
    }

    [Fact]
    public void ARunningAttemptNamesTheProcessHoldingItAndTheSitesSupervisorAndListsItsEvents()
    {
        var (writer, studyId, cellIds, siteId) = Home(cells: 1);
        using var _ = writer;
        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "this-machine", 4242, "sha256:cafe", 600).IsOk);
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}").TryGetValue(out var attempt));
        Assert.True(RegistryOperations.RenewAttempt(writer, attempt.AttemptId, attempt.FencingEpoch, "this-machine/5151").IsOk);
        Assert.True(RegistryOperations.RecordEvent(
            writer, attempt.AttemptId, attempt.FencingEpoch, "training_progress", null, """{"detail":"epoch 3 of 10"}""").IsOk);

        var plain = Rendered(writer, studyId);
        Assert.Contains("held by this-machine/5151; supervisor this-machine/4242 epoch 1 holds the office", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("training_progress", plain, StringComparison.Ordinal);

        var withEvents = Rendered(writer, studyId, events: true);
        Assert.Contains("training_progress  epoch 3 of 10", withEvents, StringComparison.Ordinal);
    }

    // ── slice 2 of the command-line design (2026-09-26): what monitor-study says about a cell ──

    /// <summary>The cell's own line: its name, then the state column.</summary>
    static string CellLine(string rendered, string cell) =>
        rendered.Split('\n').Single(line => line.StartsWith($"  {cell} ", StringComparison.Ordinal));

    static string StateOf(string rendered, string cell) =>
        CellLine(rendered, cell)[32..].TrimStart().Split(' ')[0];

    /// <summary>
    /// A cell whose last attempt failed and which a person has since made
    /// claimable again with retry-cell is waiting, not failed: on 2026-09-26
    /// monitor-study printed `failed` for such a cell while it sat in the queue.
    /// The line below it says who asked and why, and how many tries it has had.
    /// </summary>
    [Fact]
    public void ARetriedCellIsWaitingNotFailed()
    {
        var (writer, studyId, cellIds, siteId) = Home(cells: 1);
        using var _ = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}").TryGetValue(out var attempt));
        Assert.True(RegistryOperations.FailAttempt(
            writer, attempt.AttemptId, attempt.FencingEpoch, RegistryOperations.AttemptFailed,
            CellFailureDetail.Write("unknown dataset 'pointmaze.physical_state'", deterministic: true, CellFailureDetail.ClassDeterministic)).IsOk);
        Assert.Equal("failed", StateOf(Rendered(writer, studyId), "cell-0"));

        Assert.True(StudyCellRetry.Request(writer, studyId, "cell-0", "qilin", "installed the PointMaze dataset").IsOk);
        var rendered = Rendered(writer, studyId);

        Assert.Equal("waiting", StateOf(rendered, "cell-0"));
        var lines = rendered.Split('\n');
        var below = lines[Array.FindIndex(lines, line => line.StartsWith("  cell-0 ", StringComparison.Ordinal)) + 1];
        Assert.Contains("retry requested by qilin: installed the PointMaze dataset", below, StringComparison.Ordinal);
        Assert.Contains("1 of 3 tries spent", below, StringComparison.Ordinal);
        Assert.Contains("attempt 2", below, StringComparison.Ordinal);
    }

    /// <summary>
    /// What stays `failed`: a cell that failed with no retry, and a retried cell
    /// that no machine may claim, here because a person holds it. The state
    /// column says `waiting` only when the retry made the cell claimable.
    /// </summary>
    [Fact]
    public void AFailedCellWithNoRetryOrNoClaimStillPrintsFailed()
    {
        var (writer, studyId, cellIds, siteId) = Home(cells: 2);
        using var _ = writer;
        foreach (var cellId in cellIds)
        {
            Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, siteId, "{}").TryGetValue(out var attempt));
            Assert.True(RegistryOperations.FailAttempt(
                writer, attempt.AttemptId, attempt.FencingEpoch, RegistryOperations.AttemptFailed,
                CellFailureDetail.Write("the card names no horizon", deterministic: true, CellFailureDetail.ClassDeterministic)).IsOk);
        }
        Assert.True(StudyCellRetry.Request(writer, studyId, "cell-1", "qilin", "corrected the card").IsOk);
        Assert.True(RegistryOperations.RequestOperator(
            writer, RegistryOperations.RequestHold, RegistryOperations.CellScope(cellIds[1]), "read it first", "qilin").IsOk);

        var rendered = Rendered(writer, studyId);

        Assert.Equal("failed", StateOf(rendered, "cell-0"));
        Assert.Equal("failed", StateOf(rendered, "cell-1"));
    }

    /// <summary>
    /// On 2026-09-26 the waiting sentence named autodl-bj-5090-2, a Site that did
    /// not hold the study's source and was unreachable, while local-4070 held the
    /// source, admitted the cell, and was only busy with other cells. The Site
    /// that holds the source and admits the cell is named first, with what it
    /// is running; the Sites that could take the cell after an install come
    /// after it, each with its install line.
    /// CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): no Site
    /// holds a source now, so there is no install line and a second admitting
    /// Site with room would simply take the cell; the test keeps the one Site,
    /// busy, and its sentence.
    /// </summary>
    [Fact]
    public void TheWaitingSentenceNamesTheSiteThatWillTakeTheCellFirst()
    {
        var (writer, studyId, _, siteId) = Home(cells: 1);
        using var _ = writer;
        Assert.True(RegistryOperations.ReadStudy(writer.Connection, studyId).TryGetValue(out var head));
        // local-4070 declares two slots; a second study on the same source fills both.
        var busy = RegistryOperations.RegisterStudy(
            writer, "busy", 1, """{"schema":"fern.compiled-study.v1"}""", head.SourceId,
            [.. Enumerable.Range(0, 2).Select(ordinal => new RegistryOperations.CompiledCell(
                ordinal, $"busy-{ordinal}", "rossler_param_shock.series", 7,
                """{"schema":"fern.training.v2","model":{"type":"fern"}}""",
                """{"device":"cuda","requires_cuda":true}""", """{"unit_results":"retained"}"""))]);
        Assert.True(busy.TryGetValue(out var busyStudy));
        foreach (var cellId in busyStudy.CellIds)
        {
            Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, siteId, "{}").TryGetValue(out var attempt));
            Assert.True(RegistryOperations.RenewAttempt(writer, attempt.AttemptId, attempt.FencingEpoch, "this-machine/5151").IsOk);
        }
        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "this-machine", 4242, "sha256:cafe", 600).IsOk);

        var rendered = Rendered(writer, studyId);

        var waiting = rendered.Split('\n').Single(line => line.StartsWith("      waiting: ", StringComparison.Ordinal));
        Assert.StartsWith("      waiting: waits for Site local-4070, running busy-0 of busy, busy-1 of busy", waiting, StringComparison.Ordinal);
        Assert.DoesNotContain("install", waiting, StringComparison.Ordinal);

        // The Site's supervisor gone quiet is said the way the placeable sentence already says it.
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText = "UPDATE supervisors SET lease_until = '2026-09-26T04:00:00.0000000+00:00'";
            command.ExecuteNonQuery();
        }
        Assert.Contains(
            "waiting: waits for Site local-4070, running busy-0 of busy, busy-1 of busy, whose supervisor this-machine/4242 is unconfirmed since 2026-09-26T04:00:00",
            Rendered(writer, studyId), StringComparison.Ordinal);
    }

    [Fact]
    public void AStudyThatIsNotRunningSaysSoOnceAtTheTopAndOnEveryWaitingCell()
    {
        var (writer, studyId, _, _) = Home(cells: 1);
        using var _ = writer;
        Assert.True(RegistryOperations.RequestOperator(
            writer, RegistryOperations.RequestPause, RegistryOperations.StudyScope(studyId), "power work", "qilin").IsOk);

        var rendered = Rendered(writer, studyId);

        Assert.Contains($"paused by qilin over study:{studyId}: power work", rendered, StringComparison.Ordinal);
        Assert.Contains("waiting: paused by qilin: power work", rendered, StringComparison.Ordinal);
    }
}
