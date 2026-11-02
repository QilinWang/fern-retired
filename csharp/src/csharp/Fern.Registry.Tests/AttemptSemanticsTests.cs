using Fern.Registry;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// Slice 2 of the cell port (design note
/// docs/design/original/by_ai/2026-09-11-restore-the-behavior-port-the-input.md,
/// rows 8, 11, 13): the four retired retry classes as the failure record's
/// class, read by the projection and settled by the claim; the ten-minute
/// window before a transient failure is offered again; a person's retry-cell
/// record lifting both; and the operator's pause and hold refusing a claim.
/// </summary>
public sealed class AttemptSemanticsTests
{
    static (RegistryWriter Writer, long StudyId, long CellId, long SiteId, long OtherSiteId) Home()
    {
        var path = ProductionHome.CreateRegistry();
        var writer = RegistryHome.OpenWriter(path);
        long sourceId, siteId, otherSiteId;
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
                VALUES ('local-4070', '{"device":"cuda"}', '{"slots":1}', '2026-09-11') RETURNING id
                """;
            siteId = Convert.ToInt64(command.ExecuteScalar());
        }
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sites (name, capabilities_json, capacity_json, created_at)
                VALUES ('pod-5090', '{"device":"cuda"}', '{"slots":1}', '2026-09-11') RETURNING id
                """;
            otherSiteId = Convert.ToInt64(command.ExecuteScalar());
        }
        var registered = RegistryOperations.RegisterStudy(
            writer, "semantics", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId,
            [new RegistryOperations.CompiledCell(0, "cell-0", "rossler_param_shock.series", 42,
                """{"schema":"fern.training.v2","model":{"type":"fern"},"runtime":{"device":"cuda"}}""",
                """{"device":"cuda","requires_cuda":true}""", """{"unit_results":"retained"}""")]);
        Assert.True(registered.TryGetValue(out var study));
        // Slice 6 (row 31): both Sites hold the study's source, or the claim refuses them.
        ProductionHome.DeclareCodeFrom(writer, siteId);
        ProductionHome.DeclareCodeFrom(writer, otherSiteId);
        return (writer, study.StudyId, study.CellIds[0], siteId, otherSiteId);
    }

    const string Request = """{"schema":"fern.cell_request.v2","cell":"cell-0"}""";

    static string Failure(string failureClass) =>
        $$"""{"detail":"it broke","deterministic":{{(failureClass == "deterministic" ? "true" : "false")}},"site_specific":{{(failureClass == "site" ? "true" : "false")}},"failure_class":"{{failureClass}}"}""";

    static long FailOnce(RegistryWriter writer, long cellId, long siteId, string failureClass, long maxAttempts = 10)
    {
        var started = RegistryOperations.TryStartAttempt(writer, cellId, siteId, Request, maxAttempts);
        Assert.True(started.TryGetValue(out var attempt), started.TryGetRefusal(out var r) ? r.Detail : "");
        Assert.True(RegistryOperations.FailAttempt(writer, attempt.AttemptId, attempt.FencingEpoch, RegistryOperations.AttemptFailed, Failure(failureClass)).IsOk);
        return attempt.AttemptId;
    }

    /// <summary>Push the last attempt's ending back past the retry window: the row is the fact, not the clock.</summary>
    static void AgeLastFailure(RegistryWriter writer, long cellId)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText =
            "UPDATE attempts SET ended_at = '2000-01-01T00:00:00.0000000+00:00' WHERE id = (SELECT id FROM attempts WHERE cell_id = $cell ORDER BY ordinal DESC LIMIT 1)";
        command.Parameters.AddWithValue("$cell", cellId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    static string Refusal<T>(RegistryResult<T> result) => result.TryGetRefusal(out var refusal) ? refusal.Detail : "";

    static bool Runnable(RegistryWriter writer, long cellId) =>
        RegistryOperations.ListRunnableCells(writer.Connection, maxAttempts: 10).Any(cell => cell.CellId == cellId);

    [Fact]
    public void ADeterministicFailureIsNeverOfferedOrClaimedAgainUntilAPersonRetries()
    {
        var (writer, _, cellId, siteId, otherSiteId) = Home();
        using var _ = writer;
        var attemptId = FailOnce(writer, cellId, siteId, RegistryOperations.FailureDeterministic);

        Assert.False(Runnable(writer, cellId));
        var claim = RegistryOperations.TryStartAttempt(writer, cellId, otherSiteId, Request, 10);
        Assert.False(claim.IsOk);
        Assert.Contains("would repeat unchanged", Refusal(claim), StringComparison.Ordinal);

        // A person's retry record is the last record now, and it lifts the class.
        Assert.True(RegistryOperations.RecordEvent(writer, attemptId, 1, RegistryOperations.RetryRequestedEvent, null,
            """{"detail":"retry requested by qilin: fixed the card"}""").IsOk);
        Assert.True(Runnable(writer, cellId));
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, otherSiteId, Request, 10).IsOk);
    }

    [Fact]
    public void AHumanFailureWaitsForAPerson()
    {
        var (writer, _, cellId, siteId, _) = Home();
        using var _ = writer;
        FailOnce(writer, cellId, siteId, RegistryOperations.FailureHuman);
        Assert.False(Runnable(writer, cellId));
        var claim = RegistryOperations.TryStartAttempt(writer, cellId, siteId, Request, 10);
        Assert.Contains("a person's to judge", Refusal(claim), StringComparison.Ordinal);
    }

    [Fact]
    public void ASiteFailureIsClaimableElsewhereAtOnceAndNeverAgainWhereItFailed()
    {
        var (writer, _, cellId, siteId, otherSiteId) = Home();
        using var _ = writer;
        FailOnce(writer, cellId, siteId, RegistryOperations.FailureSite);

        Assert.True(Runnable(writer, cellId));
        var same = RegistryOperations.TryStartAttempt(writer, cellId, siteId, Request, 10);
        Assert.Contains("belongs to the machine", Refusal(same), StringComparison.Ordinal);
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, otherSiteId, Request, 10).IsOk);
    }

    [Fact]
    public void ATransientFailureWaitsOutTheWindowUnlessAPersonRetriesNow()
    {
        var (writer, _, cellId, siteId, _) = Home();
        using var _ = writer;
        var attemptId = FailOnce(writer, cellId, siteId, RegistryOperations.FailureTransient);

        Assert.False(Runnable(writer, cellId));
        var early = RegistryOperations.TryStartAttempt(writer, cellId, siteId, Request, 10);
        Assert.Contains("10-minute window", Refusal(early), StringComparison.Ordinal);

        AgeLastFailure(writer, cellId);
        Assert.True(Runnable(writer, cellId));
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, siteId, Request, 10).IsOk);
    }

    [Fact]
    public void ARetryRecordLiftsTheWindowAtOnce()
    {
        var (writer, _, cellId, siteId, _) = Home();
        using var _ = writer;
        var attemptId = FailOnce(writer, cellId, siteId, RegistryOperations.FailureTransient);
        Assert.False(Runnable(writer, cellId));
        Assert.True(RegistryOperations.RecordEvent(writer, attemptId, 1, RegistryOperations.RetryRequestedEvent, null,
            """{"detail":"retry requested by qilin: now"}""").IsOk);
        Assert.True(Runnable(writer, cellId));
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, siteId, Request, 10).IsOk);
    }

    [Fact]
    public void AnOlderRecordWithoutAClassIsReadThroughItsFlags()
    {
        var (writer, _, cellId, siteId, otherSiteId) = Home();
        using var _ = writer;
        var started = RegistryOperations.TryStartAttempt(writer, cellId, siteId, Request, 10);
        Assert.True(started.TryGetValue(out var attempt));
        Assert.True(RegistryOperations.FailAttempt(writer, attempt.AttemptId, attempt.FencingEpoch, RegistryOperations.AttemptFailed,
            """{"detail":"no CUDA","deterministic":false,"site_specific":true}""").IsOk);
        Assert.Contains("belongs to the machine", Refusal(RegistryOperations.TryStartAttempt(writer, cellId, siteId, Request, 10)), StringComparison.Ordinal);
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, otherSiteId, Request, 10).IsOk);
    }

    [Fact]
    public void APauseOverEverySiteOrTheStudyKeepsTheCellOutOfTheOfferAndRefusesTheClaim()
    {
        var (writer, studyId, cellId, siteId, _) = Home();
        using var _ = writer;
        Assert.True(RegistryOperations.RequestOperator(writer, RegistryOperations.RequestPause, RegistryOperations.ScopeAll, "quiet hours", "qilin").TryGetValue(out var pauseId));

        Assert.False(Runnable(writer, cellId));
        var claim = RegistryOperations.TryStartAttempt(writer, cellId, siteId, Request, 10);
        Assert.Contains("paused over all by qilin (quiet hours)", Refusal(claim), StringComparison.Ordinal);

        Assert.True(RegistryOperations.ReleaseRequest(writer, pauseId, "qilin", "morning").IsOk);
        Assert.True(Runnable(writer, cellId));

        Assert.True(RegistryOperations.RequestOperator(writer, RegistryOperations.RequestPause, RegistryOperations.StudyScope(studyId), "this study waits", "qilin").IsOk);
        Assert.False(Runnable(writer, cellId));
        Assert.Contains($"paused over study:{studyId}", Refusal(RegistryOperations.TryStartAttempt(writer, cellId, siteId, Request, 10)), StringComparison.Ordinal);
    }

    [Fact]
    public void ASitePauseIsTheClaimsToRefuse()
    {
        var (writer, _, cellId, siteId, otherSiteId) = Home();
        using var _ = writer;
        Assert.True(RegistryOperations.RequestOperator(writer, RegistryOperations.RequestPause, RegistryOperations.SiteScope(siteId), "driver upgrade", "qilin").IsOk);

        // The projection names no Site, so the cell is still offered; the paused Site's claim refuses, another Site's succeeds.
        Assert.True(Runnable(writer, cellId));
        Assert.Contains($"paused over site:{siteId}", Refusal(RegistryOperations.TryStartAttempt(writer, cellId, siteId, Request, 10)), StringComparison.Ordinal);
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, otherSiteId, Request, 10).IsOk);
    }

    [Fact]
    public void AHoldOnTheCellOrItsStudyIsNotClaimedUntilReleased()
    {
        var (writer, studyId, cellId, siteId, _) = Home();
        using var _ = writer;
        Assert.True(RegistryOperations.RequestOperator(writer, RegistryOperations.RequestHold, RegistryOperations.CellScope(cellId), "look at it first", "qilin").TryGetValue(out var holdId));
        Assert.False(Runnable(writer, cellId));
        Assert.Contains("is held over", Refusal(RegistryOperations.TryStartAttempt(writer, cellId, siteId, Request, 10)), StringComparison.Ordinal);
        Assert.True(RegistryOperations.ReleaseRequest(writer, holdId, "qilin", "looked").IsOk);
        Assert.True(Runnable(writer, cellId));

        Assert.True(RegistryOperations.RequestOperator(writer, RegistryOperations.RequestHold, RegistryOperations.StudyScope(studyId), "whole study", "qilin").IsOk);
        Assert.False(Runnable(writer, cellId));
    }

    [Fact]
    public void ACancelledAttemptIsAPersonsDecisionAndIsNotReclaimedByTheMachine()
    {
        var (writer, _, cellId, siteId, otherSiteId) = Home();
        using var _ = writer;
        var started = RegistryOperations.TryStartAttempt(writer, cellId, siteId, Request, 10);
        Assert.True(started.TryGetValue(out var attempt));
        Assert.True(RegistryOperations.FailAttempt(writer, attempt.AttemptId, attempt.FencingEpoch, RegistryOperations.AttemptCancelled, Failure(RegistryOperations.FailureHuman)).IsOk);
        Assert.False(Runnable(writer, cellId));
        Assert.Contains("a person's to judge", Refusal(RegistryOperations.TryStartAttempt(writer, cellId, otherSiteId, Request, 10)), StringComparison.Ordinal);
    }
}
