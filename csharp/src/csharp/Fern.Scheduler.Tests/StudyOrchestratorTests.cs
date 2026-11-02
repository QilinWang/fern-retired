using Fern.Registry;
using Fern.Scheduler;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// One scheduling cycle over the one-store schema, driven against a real Home
/// with a worker that does not exist: the point of the interface is that the
/// orchestrator can be proved without a Site, a transport or a GPU. Nothing
/// here trains.
/// </summary>
public sealed class StudyOrchestratorTests
{
    sealed class Worker(Func<StudyOrchestrator.ClaimedCell, StudyOrchestrator.WorkerOutcome> run)
        : StudyOrchestrator.ICellWorker
    {
        public List<StudyOrchestrator.ClaimedCell> Ran { get; } = [];

        public StudyOrchestrator.WorkerOutcome Run(StudyOrchestrator.ClaimedCell cell, CancellationToken cancellation)
        {
            Ran.Add(cell);
            return run(cell);
        }
    }

    static StudyOrchestrator.WorkerOutcome Completes(StudyOrchestrator.ClaimedCell cell) =>
        new(true, $$"""{"cell":"{{cell.CellName}}","mse":0.25}""", "{}");

    static (string Path, long StudyId, IReadOnlyList<long> CellIds) Home(int slots = 2, string models = """["fern"]""")
    {
        var path = TestRegistry.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
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

        var cells = new List<RegistryOperations.CompiledCell>();
        for (var ordinal = 0; ordinal < 3; ordinal++)
        {
            cells.Add(new RegistryOperations.CompiledCell(
                ordinal, $"cell-{ordinal}", "rossler_param_shock.series", 42,
                """{"schema":"fern.training.v2","model":{"type":"fern"}}""",
                """{"device":"cuda","requires_cuda":true}""",
                """{"unit_results":"retained"}"""));
        }
        var registered = RegistryOperations.RegisterStudy(
            writer, "orchestrated", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, cells);
        Assert.True(registered.TryGetValue(out var value));
        // Slice 6 (row 31): the Site holds the study's source, or the claim refuses it.
        TestRegistry.DeclareCodeFrom(writer, siteId);
        return (path, value.StudyId, value.CellIds);
    }

    /// <summary>
    /// The whole cycle: read, decide, claim, dispatch, finish. Capacity bounds
    /// how many run, and each finished cell's result is accepted.
    /// </summary>
    [Fact]
    public void OneCycleClaimsUpToCapacityRunsThemAndAcceptsTheirResults()
    {
        var (path, studyId, _) = Home(slots: 2);
        var worker = new Worker(Completes);

        using var writer = RegistryHome.OpenWriter(path);
        var report = StudyOrchestrator.RunOnce(new SqliteStudyStore(writer), worker, studyId);

        Assert.Equal(3, report.Runnable);
        Assert.Equal(2, report.Placed);
        Assert.Equal(2, report.Claimed);
        Assert.Equal(2, report.Completed);
        Assert.Equal(0, report.Failed);
        Assert.Equal(2, worker.Ran.Count);
        // Every claimed cell arrived with its own epoch, which is what the
        // worker must echo back on every progress report.
        Assert.Equal([1L, 1L], worker.Ran.Select(cell => cell.FencingEpoch).ToArray());
        Assert.Single(report.Unplaced);  // the third cell had nowhere to go

        var view = RegistryOperations.ReadStudy(writer.Connection, studyId);
        Assert.True(view.TryGetValue(out var study));
        Assert.Equal(3, study.CellCount);
        Assert.Equal(2, study.AcceptedCellCount);
    }

    /// <summary>A second cycle picks up what the first could not place.</summary>
    [Fact]
    public void TheNextCycleRunsWhatTheLastOneCouldNotPlace()
    {
        var (path, studyId, _) = Home(slots: 2);
        using var writer = RegistryHome.OpenWriter(path);

        StudyOrchestrator.RunOnce(new SqliteStudyStore(writer), new Worker(Completes), studyId);
        var second = StudyOrchestrator.RunOnce(new SqliteStudyStore(writer), new Worker(Completes), studyId);

        Assert.Equal(1, second.Runnable);
        Assert.Equal(1, second.Completed);
        var view = RegistryOperations.ReadStudy(writer.Connection, studyId);
        Assert.True(view.TryGetValue(out var study));
        Assert.Equal(3, study.AcceptedCellCount);
    }

    /// <summary>A cancelled study claims nothing, however much capacity is free.</summary>
    [Fact]
    public void ACancelledStudyRunsNothing()
    {
        var (path, studyId, _) = Home(slots: 4);
        using var writer = RegistryHome.OpenWriter(path);
        Assert.True(RegistryOperations.RequestStudyState(writer, studyId, "cancel").IsOk);

        var worker = new Worker(Completes);
        var report = StudyOrchestrator.RunOnce(new SqliteStudyStore(writer), worker, studyId);

        Assert.Equal(0, report.Runnable);
        Assert.Equal(0, report.Claimed);
        Assert.Empty(worker.Ran);
    }

    /// <summary>
    /// A worker that throws is a failed attempt, not a lost cycle: the failure
    /// is recorded, which is what lets the cell be claimed again.
    /// </summary>
    [Fact]
    public void AWorkerThatThrowsLeavesAFailedAttemptAndAClaimableCell()
    {
        var (path, studyId, _) = Home(slots: 1);
        using var writer = RegistryHome.OpenWriter(path);

        var report = StudyOrchestrator.RunOnce(
            new SqliteStudyStore(writer), new Worker(_ => throw new InvalidOperationException("the pod went away")), studyId);

        Assert.Equal(1, report.Claimed);
        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.Completed);

        // The failure is on the record, and the cell is runnable again once the
        // retry window has passed (CHG 2026-09-11 night, slice 2: a transient
        // failure is not offered again within seconds).
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM attempts WHERE state = 'failed'";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
        Assert.Equal(2, RegistryOperations.ListRunnableCells(writer.Connection, studyId).Count);
        using var age = writer.Connection.CreateCommand();
        age.CommandText = "UPDATE attempts SET ended_at = '2000-01-01T00:00:00.0000000+00:00' WHERE state = 'failed'";
        Assert.Equal(1, age.ExecuteNonQuery());
        Assert.Equal(3, RegistryOperations.ListRunnableCells(writer.Connection, studyId).Count);
    }

    /// <summary>A cell no Site admits is reported unplaced, by name, rather than vanishing.</summary>
    [Fact]
    public void ACellNoSiteAdmitsIsReportedUnplaced()
    {
        var (path, studyId, _) = Home(slots: 4, models: """["dlinear"]""");
        using var writer = RegistryHome.OpenWriter(path);

        var report = StudyOrchestrator.RunOnce(new SqliteStudyStore(writer), new Worker(Completes), studyId);

        Assert.Equal(0, report.Placed);
        Assert.Equal(3, report.Unplaced.Count);
        Assert.All(report.Unplaced, unplaced => Assert.Contains("admits model fern", unplaced.Because));
    }
}
