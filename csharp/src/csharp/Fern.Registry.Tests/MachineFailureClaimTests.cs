using Fern.Registry;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// Slices M1 and M3 of
/// docs/programs/2026-09-24-gleam-scheduler-decisions/2026-09-25-what-stops-work-and-who-lifts-it: a
/// failure that belongs to a machine is not charged to the cells. On
/// 2026-09-25 the AutoDL 4090D's compiler did not know a field added that day,
/// and eight cells of one source each failed there as a transient failure of
/// its own; one of them was tried there again four minutes later.
/// </summary>
public sealed class MachineFailureClaimTests
{
    const string Config = """{"schema":"fern.training.v2","model":{"type":"fern"},"runtime":{"device":"cuda"}}""";

    const string Refusal =
        "the cell was refused by its runtime: runner_failed: the compiler refused the configuration: fern-compile identity: "
        + "model.knobs.predictive_source: is not a field of ConditionalNsfKnobs";

    static readonly string CompilerRefused =
        $$"""{"detail":"{{Refusal}}","deterministic":false,"site_specific":true,"failure_class":"site","before_execution":false,"code":"{{RegistryOperations.CodeCompilerRefusedField}}"}""";

    /// <summary>The same refusal, where the worker found the Site's compiler differs from the study's recorded one.</summary>
    static readonly string CompilerDiffers =
        $$"""{"detail":"{{Refusal}}","deterministic":false,"site_specific":true,"failure_class":"site","before_execution":false,"code":"{{RegistryOperations.CodeCompilerRefusedField}}","compiler_differs":true}""";

    /// <summary>
    /// The 2026-09-26 failure: a recording that never travelled with the code.
    /// The worker classes it as the machine's, as it classes a missing CUDA
    /// device or module, and it carries no compiler code.
    /// </summary>
    static readonly string DataNotOnTheMachine =
        $$"""{"detail":"the cell was refused by its runtime: runner_failed: dataset input manifest_file is missing at results/corpus/controller_recordings/short/pendulum-250925/normal/manifest.json","deterministic":false,"site_specific":true,"failure_class":"site","before_execution":false,"code":"runner_failed"}""";

    static string Transient(string sentence) =>
        $$"""{"detail":"{{sentence}}","deterministic":false,"site_specific":false,"failure_class":"transient","before_execution":false}""";

    /// <summary>Two Sites holding one source, and a Study of <paramref name="cells"/> cells that needs it.</summary>
    static (RegistryWriter Writer, IReadOnlyList<long> CellIds, long Old, long Other) Home(int cells)
    {
        var writer = RegistryHome.OpenWriter(ProductionHome.CreateRegistry());
        long sourceId;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
                VALUES ('5e87434e', '[]', 'commit', '2026-09-25') RETURNING id
                """;
            sourceId = Convert.ToInt64(command.ExecuteScalar());
        }
        long Site(string name)
        {
            Assert.True(RegistryOperations.RegisterSite(
                writer, name, """{"device":"cuda","admitted_models":["fern"]}""", """{"slots":8}""").TryGetValue(out var site));
            ProductionHome.DeclareCodeFrom(writer, site.SiteId);
            return site.SiteId;
        }
        var old = Site("autodl-4090d");
        var other = Site("local-4070");
        var declared = Enumerable.Range(0, cells).Select(ordinal => new RegistryOperations.CompiledCell(
            ordinal, $"cell-{ordinal}", "lorenz.series", 42, Config, """{"device":"cuda","requires_cuda":true}""", """{"unit_results":"retained"}""")).ToList();
        Assert.True(RegistryOperations.RegisterStudy(
            writer, "predictive-sources", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, declared)
            .TryGetValue(out var registered));
        return (writer, registered.CellIds, old, other);
    }

    static void Fail(RegistryWriter writer, long cellId, long siteId, string detailJson)
    {
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, siteId, "{}").TryGetValue(out var attempt));
        Assert.True(RegistryOperations.FailAttempt(writer, attempt.AttemptId, attempt.FencingEpoch,
            RegistryOperations.AttemptFailed, detailJson).IsOk);
    }

    static string Refused<T>(RegistryResult<T> result) =>
        result.TryGetRefusal(out var refusal) ? refusal.Detail : "(claimed)";

    [Fact]
    public void ARecordingThatNeverReachedTheMachineSpendsNoneOfTheCellsAttempts()
    {
        // One attempt is the budget these supervisors run with, so a single
        // machine fault would otherwise retire the cell for good. On
        // 2026-09-26 that happened to a whole suite in about two seconds a
        // cell, and 65 of them had to be released by hand.
        var (writer, cells, old, other) = Home(1);
        Fail(writer, cells[0], old, DataNotOnTheMachine);
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[0], other, "{}").IsOk,
            "the cell keeps its chance on a Site that holds the recording");
    }

    [Fact]
    public void AMissingCudaDeviceSpendsNoneOfTheCellsAttempts()
    {
        var (writer, cells, old, other) = Home(1);
        Fail(writer, cells[0], old, $$"""{"detail":"the cell was refused by its runtime: runner_failed: this build requires CUDA","deterministic":false,"site_specific":true,"failure_class":"site","before_execution":false,"code":"runner_failed"}""");
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[0], other, "{}").IsOk);
    }

    [Fact]
    public void AFailureOfTheScienceStillSpendsTheCellsAttempt()
    {
        // The rule this must not swallow: a model that will not fit is the
        // cell's own failure, and the cell does not simply carry on elsewhere.
        // Within ten minutes of the failure the retry window refuses first and
        // the budget refuses after it; what matters here is that a science
        // failure refuses at all where a machine failure did not.
        var (writer, cells, old, other) = Home(1);
        Fail(writer, cells[0], old, Transient("the loss became NaN at epoch 3"));
        var refused = Refused(RegistryOperations.TryStartAttempt(writer, cells[0], other, "{}"));
        Assert.NotEqual("(claimed)", refused);
        Assert.Contains("failed at", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void ACompilerRefusalHoldsTheSourceOnThatSiteAndSpendsNoAttemptWhenTheCompilerDiffers()
    {
        var (writer, cells, old, other) = Home(cells: 2);
        using var _ = writer;
        Fail(writer, cells[0], old, CompilerDiffers);

        // The cell that never met it is not claimed on that Site either.
        var there = RegistryOperations.TryStartAttempt(writer, cells[1], old, "{}");
        Assert.Contains("the compiler on Site autodl-4090d refused a card of source", Refused(there), StringComparison.Ordinal);
        // The attempt spent nothing: with one attempt allowed, the cell is still offered, and another Site takes it.
        Assert.Contains(cells[0], RegistryOperations.ListRunnableCells(writer.Connection, null, maxAttempts: 1).Select(cell => cell.CellId));
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[0], other, "{}", maxAttempts: 1).IsOk);
    }

    /// <summary>
    /// The reader's review: when the worker could not tell whether the Site's
    /// compiler differs (the study records none), the attempt counts, so a card
    /// no compiler accepts spends its budget and reaches a person. The source
    /// is still held on that Site.
    /// </summary>
    [Fact]
    public void ACompilerRefusalCountsWhenNobodyCouldTellTheCompilersDiffer()
    {
        var (writer, cells, old, _) = Home(cells: 2);
        using var _ = writer;
        Fail(writer, cells[0], old, CompilerRefused);

        Assert.DoesNotContain(cells[0], RegistryOperations.ListRunnableCells(writer.Connection, null, maxAttempts: 1).Select(cell => cell.CellId));
        Assert.False(RegistryOperations.TryStartAttempt(writer, cells[1], old, "{}").IsOk);
    }

    /// <summary>
    /// Tonight's case, which the first tests did not hold: the supervisor took
    /// the office BEFORE the failures and still holds it. The refusal stands
    /// until a supervisor starts after it.
    /// </summary>
    [Fact]
    public void ASupervisorRunningSinceBeforeTheFailuresDoesNotLiftIt()
    {
        var (writer, cells, old, _) = Home(cells: 2);
        using var _ = writer;
        Assert.True(RegistryOperations.StartSupervisor(writer, old, "autodl-container", 41, "sha256:old").IsOk);
        Thread.Sleep(20);
        Fail(writer, cells[0], old, CompilerRefused);

        Assert.Contains("the compiler on Site autodl-4090d refused", Refused(RegistryOperations.TryStartAttempt(writer, cells[1], old, "{}")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ASupervisorStartedAfterTheRefusalLiftsIt()
    {
        var (writer, cells, old, _) = Home(cells: 2);
        using var _ = writer;
        Fail(writer, cells[0], old, CompilerRefused);
        Assert.False(RegistryOperations.TryStartAttempt(writer, cells[1], old, "{}").IsOk);

        // A new compiler reaches a Site with a new supervisor.
        Thread.Sleep(20);
        Assert.True(RegistryOperations.StartSupervisor(writer, old, "autodl-container", 42, "sha256:new").IsOk);
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[1], old, "{}").IsOk);
    }

    [Fact]
    public void ThreeFailuresAlikeWithinTenMinutesStopTheSiteTakingTheSource()
    {
        var (writer, cells, old, other) = Home(cells: 4);
        using var _ = writer;
        const string alike = "the cell was refused by its runtime: runner_failed: CUDA error: an illegal memory access";
        Fail(writer, cells[0], old, Transient(alike));
        Fail(writer, cells[1], old, Transient(alike));
        // Two alike are not yet a burst.
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[2], old, "{}").TryGetValue(out var third));
        Assert.True(RegistryOperations.FailAttempt(writer, third.AttemptId, third.FencingEpoch,
            RegistryOperations.AttemptFailed, Transient(alike)).IsOk);

        var fourth = RegistryOperations.TryStartAttempt(writer, cells[3], old, "{}");
        Assert.Contains("3 cells of source", Refused(fourth), StringComparison.Ordinal);
        Assert.Contains("failed the same way on Site autodl-4090d within 10 minutes", Refused(fourth), StringComparison.Ordinal);
        // Another Site still takes it.
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[3], other, "{}").IsOk);
    }

    /// <summary>Slice M2: which compiler compiled a study is recorded, replaced in place, and refused malformed.</summary>
    [Fact]
    public void AStudysCompilerIsRecordedAndReadBack()
    {
        var (writer, cells, _, _) = Home(cells: 1);
        using var _ = writer;
        long studyId;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText = "SELECT study_id FROM cells WHERE id = $cell";
            command.Parameters.AddWithValue("$cell", cells[0]);
            studyId = Convert.ToInt64(command.ExecuteScalar());
        }
        Assert.Null(RegistryOperations.ReadStudyCompiler(writer.Connection, studyId));
        var sha = new string('a', 64);
        Assert.True(RegistryOperations.SetStudyCompiler(writer, studyId, sha, "/root/bin/fern-compile.aaaa", "test").IsOk);
        Assert.Equal(sha, RegistryOperations.ReadStudyCompiler(writer.Connection, studyId)!.Sha256);
        Assert.True(RegistryOperations.SetStudyCompiler(writer, studyId, new string('b', 64), "/root/bin/fern-compile.bbbb", "test").IsOk);
        Assert.Equal("/root/bin/fern-compile.bbbb", RegistryOperations.ReadStudyCompiler(writer.Connection, studyId)!.Path);
        // Slice F6: the installer's cleanup keeps every compiler a study was compiled with.
        Assert.Equal([new string('b', 64)], RegistryOperations.ListStudyCompilerDigests(writer.Connection));
        Assert.False(RegistryOperations.SetStudyCompiler(writer, studyId, "abc", "/x", "test").IsOk);
        Assert.False(RegistryOperations.SetStudyCompiler(writer, studyId + 99, sha, "/x", "test").IsOk);
    }

    [Fact]
    public void FailuresThatDifferDoNotGroup()
    {
        var (writer, cells, old, _) = Home(cells: 4);
        using var _ = writer;
        Fail(writer, cells[0], old, Transient("exit 1"));
        Fail(writer, cells[1], old, Transient("exit 2"));
        Fail(writer, cells[2], old, Transient("exit 3"));

        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[3], old, "{}").IsOk);
    }
}
