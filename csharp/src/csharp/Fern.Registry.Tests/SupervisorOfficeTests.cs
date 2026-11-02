using Fern.Registry;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// Slice 1 of the cell port (design note
/// docs/design/original/by_ai/2026-09-11-restore-the-behavior-port-the-input.md,
/// rows 24-27): the supervisor office per Site, claimed with a written expiry
/// and a fencing epoch, and the operator requests it consumes. These are the
/// retired SchedulerOffice and SchedulerServiceAttempts falsifiers, re-pointed
/// at one row per Site: a second claimant is refused by name, an expired
/// lease admits a successor and leaves the predecessor unconfirmed, a displaced
/// supervisor's writes touch nothing, an identical terminal replays as zero
/// mutation and a conflicting one is refused, and a stop is consumed once by
/// the office holder and never by a displaced one.
/// </summary>
public sealed class SupervisorOfficeTests
{
    static (string Path, long SiteId) Home()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        using var command = writer.Connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO sites (name, capabilities_json, capacity_json, created_at)
            VALUES ('local-4070', '{"device":"cuda"}', '{"slots":1}', '2026-09-11')
            RETURNING id
            """;
        return (path, Convert.ToInt64(command.ExecuteScalar()));
    }

    static string Refusal<T>(RegistryResult<T> result) =>
        result.TryGetRefusal(out var refusal) ? $"{refusal.ErrorCategory}: {refusal.Detail}" : "";

    static string Code<T>(RegistryResult<T> result) =>
        result.TryGetRefusal(out var refusal) ? refusal.ErrorCategory ?? "" : "ok";

    /// <summary>Move a lease into the past without waiting for a clock: the row is the fact.</summary>
    static void ExpireLease(RegistryWriter writer, long supervisorId)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "UPDATE supervisors SET lease_until = '2000-01-01T00:00:00.0000000+00:00' WHERE id = $id";
        command.Parameters.AddWithValue("$id", supervisorId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    [Fact]
    public void TheFirstSupervisorClaimsTheOfficeAndTheSecondIsRefusedByName()
    {
        var (path, siteId) = Home();
        using var writer = RegistryHome.OpenWriter(path);

        var first = RegistryOperations.StartSupervisor(writer, siteId, "alpha", 100, "sha256:aa");
        Assert.True(first.TryGetValue(out var permit), Refusal(first));
        Assert.Equal(1, permit.Epoch);
        Assert.Equal(siteId, permit.SiteId);

        var second = RegistryOperations.StartSupervisor(writer, siteId, "beta", 200, "sha256:bb");
        Assert.Equal("held", Code(second));
        Assert.Contains("alpha pid 100 at epoch 1", Refusal(second), StringComparison.Ordinal);

        var view = RegistryOperations.ReadSupervisor(writer.Connection, siteId);
        Assert.NotNull(view);
        Assert.Equal("alpha", view!.Host);
        Assert.Null(view.EndedAt);
        Assert.True(view.HoldsOfficeAt(DateTimeOffset.UtcNow.ToString("o")));
    }

    [Fact]
    public void AnExpiredLeaseAdmitsASuccessorAtTheNextEpochAndLeavesThePredecessorUnconfirmed()
    {
        var (path, siteId) = Home();
        using var writer = RegistryHome.OpenWriter(path);
        var first = RegistryOperations.StartSupervisor(writer, siteId, "alpha", 100, "sha256:aa");
        Assert.True(first.TryGetValue(out var permit));
        ExpireLease(writer, permit.SupervisorId);

        var second = RegistryOperations.StartSupervisor(writer, siteId, "beta", 200, "sha256:bb");
        Assert.True(second.TryGetValue(out var successor), Refusal(second));
        Assert.Equal(2, successor.Epoch);

        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT ended_at IS NULL FROM supervisors WHERE id = $id";
        command.Parameters.AddWithValue("$id", permit.SupervisorId);
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
        // The predecessor is unconfirmed: it never said how it ended, and its lease is gone.
        var latest = RegistryOperations.ReadSupervisor(writer.Connection, siteId)!;
        Assert.Equal(2, latest.Epoch);
        Assert.False(latest.IsUnconfirmedAt(DateTimeOffset.UtcNow.ToString("o")));
    }

    /// <summary>A Site that admits the model and holds the source, and one Study of <paramref name="cells"/> cells.</summary>
    static (long SiteId, IReadOnlyList<long> CellIds) SiteWithCells(RegistryWriter writer, int cells)
    {
        long sourceId;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
                VALUES ('acf7022a', '[]', 'commit', '2026-09-26') RETURNING id
                """;
            sourceId = Convert.ToInt64(command.ExecuteScalar());
        }
        Assert.True(RegistryOperations.RegisterSite(
            writer, "autodl-4090d", """{"device":"cuda","admitted_models":["fern"]}""", """{"slots":8}""").TryGetValue(out var site));
        ProductionHome.DeclareCodeFrom(writer, site.SiteId);
        var declared = Enumerable.Range(0, cells).Select(ordinal => new RegistryOperations.CompiledCell(
            ordinal, $"cell-{ordinal}", "lorenz.series", 42,
            """{"schema":"fern.training.v2","model":{"type":"fern"},"runtime":{"device":"cuda"}}""", """{"device":"cuda","requires_cuda":true}""", """{"unit_results":"retained"}""")).ToList();
        Assert.True(RegistryOperations.RegisterStudy(
            writer, "office-epochs", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, declared)
            .TryGetValue(out var registered));
        return (site.SiteId, registered.CellIds);
    }

    /// <summary>
    /// Slice W1 of docs/programs/2026-09-22-gleam-site-supervision/2026-09-26-leases-and-watching-a-running-cell:
    /// a claim that names its office's epoch is refused when nobody holds the
    /// office, when its lease ran out at the Home, when a successor holds it,
    /// and when it ended; the holder's claim goes through, and a claim that
    /// names no epoch is handled as before.
    /// </summary>
    [Fact]
    public void AClaimNamingAnOfficeThatIsNotTheSitesCurrentOneIsRefused()
    {
        using var writer = RegistryHome.OpenWriter(ProductionHome.CreateRegistry());
        var (siteId, cells) = SiteWithCells(writer, cells: 3);

        var unheld = RegistryOperations.TryStartAttempt(writer, cells[0], siteId, "{}", officeEpoch: 1);
        Assert.Contains($"the office at epoch 1 is not Site {siteId}'s current office (nobody holds it)", Refusal(unheld));

        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "alpha", 100, "sha256:aa").TryGetValue(out var displaced));
        ExpireLease(writer, displaced.SupervisorId);
        var expired = RegistryOperations.TryStartAttempt(writer, cells[0], siteId, "{}", officeEpoch: displaced.Epoch);
        Assert.Contains($"(epoch {displaced.Epoch}, held by alpha/100 until 2000-01-01", Refusal(expired));

        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "beta", 200, "sha256:bb").TryGetValue(out var holder));
        var taken = RegistryOperations.TryStartAttempt(writer, cells[0], siteId, "{}", officeEpoch: displaced.Epoch);
        Assert.Contains($"(epoch {holder.Epoch}, held by beta/200", Refusal(taken));

        var claimed = RegistryOperations.TryStartAttempt(writer, cells[0], siteId, "{}", officeEpoch: holder.Epoch);
        Assert.True(claimed.IsOk, Refusal(claimed));
        var unnamed = RegistryOperations.TryStartAttempt(writer, cells[1], siteId, "{}");
        Assert.True(unnamed.IsOk, Refusal(unnamed));

        Assert.True(RegistryOperations.EndSupervisor(writer, holder, "stopped", RegistryOperations.DispositionCompleted, 0).IsOk);
        var ended = RegistryOperations.TryStartAttempt(writer, cells[2], siteId, "{}", officeEpoch: holder.Epoch);
        Assert.Contains($"(epoch {holder.Epoch}, held by beta/200", Refusal(ended));
        Assert.Contains(", ended ", Refusal(ended));
    }

    [Fact]
    public void ADisplacedSupervisorsRenewalTouchesNothingAndTheHoldersMovesTheLease()
    {
        var (path, siteId) = Home();
        using var writer = RegistryHome.OpenWriter(path);
        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "alpha", 100, "sha256:aa").TryGetValue(out var displaced));
        ExpireLease(writer, displaced.SupervisorId);
        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "beta", 200, "sha256:bb").TryGetValue(out var holder));

        var late = RegistryOperations.RenewSupervisor(writer, displaced);
        Assert.Equal("fenced", Code(late));

        var renewed = RegistryOperations.RenewSupervisor(writer, holder, leaseSeconds: 60);
        Assert.True(renewed.TryGetValue(out var until), Refusal(renewed));
        Assert.Equal(until, RegistryOperations.ReadSupervisor(writer.Connection, siteId)!.LeaseUntil);
    }

    [Fact]
    public void ALateRenewalWithNoSuccessorIsAcceptedBecauseNobodyTookTheOffice()
    {
        var (path, siteId) = Home();
        using var writer = RegistryHome.OpenWriter(path);
        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "alpha", 100, "sha256:aa").TryGetValue(out var permit));
        ExpireLease(writer, permit.SupervisorId);

        Assert.True(RegistryOperations.RenewSupervisor(writer, permit).IsOk);
        Assert.True(RegistryOperations.ReadSupervisor(writer.Connection, siteId)!.HoldsOfficeAt(DateTimeOffset.UtcNow.ToString("o")));
    }

    [Fact]
    public void EndingTwiceWithTheSameTerminalIsZeroMutationAndAConflictingTerminalIsRefused()
    {
        var (path, siteId) = Home();
        using var writer = RegistryHome.OpenWriter(path);
        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "alpha", 100, "sha256:aa").TryGetValue(out var permit));

        var ended = RegistryOperations.EndSupervisor(writer, permit, "stopped", RegistryOperations.DispositionCompleted, 0);
        Assert.True(ended.IsOk, Refusal(ended));
        var view = RegistryOperations.ReadSupervisor(writer.Connection, siteId)!;
        Assert.NotNull(view.EndedAt);
        Assert.Equal("stopped", view.Outcome);
        Assert.Equal(RegistryOperations.DispositionCompleted, view.Disposition);
        Assert.Equal(0, view.ExitCode);

        var replay = RegistryOperations.EndSupervisor(writer, permit, "stopped", RegistryOperations.DispositionCompleted, 0);
        Assert.True(replay.IsOk, Refusal(replay));
        Assert.Equal(view.EndedAt, RegistryOperations.ReadSupervisor(writer.Connection, siteId)!.EndedAt);

        var conflict = RegistryOperations.EndSupervisor(writer, permit, "office_lost", RegistryOperations.DispositionRetryable, 1);
        Assert.Equal("conflict", Code(conflict));

        // An ended supervisor holds no office: the next claimant takes epoch 2 at once.
        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "beta", 200, "sha256:bb").TryGetValue(out var next));
        Assert.Equal(2, next.Epoch);
        // And the ended one cannot renew.
        Assert.Equal("fenced", Code(RegistryOperations.RenewSupervisor(writer, permit)));
    }

    [Fact]
    public void AnUnknownDispositionIsRefusedBeforeAnyWrite()
    {
        var (path, siteId) = Home();
        using var writer = RegistryHome.OpenWriter(path);
        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "alpha", 100, "sha256:aa").TryGetValue(out var permit));
        Assert.Equal("invalid_request", Code(RegistryOperations.EndSupervisor(writer, permit, "stopped", "done", 0)));
        Assert.Null(RegistryOperations.ReadSupervisor(writer.Connection, siteId)!.EndedAt);
    }

    [Fact]
    public void AStopAddressedToTheSiteOrToEveryoneIsConsumedOnceOldestFirstAndAnotherSitesStopIsLeftStanding()
    {
        var (path, siteId) = Home();
        using var writer = RegistryHome.OpenWriter(path);
        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "alpha", 100, "sha256:aa").TryGetValue(out var permit));

        Assert.True(RegistryOperations.RequestOperator(writer, RegistryOperations.RequestStop, RegistryOperations.ScopeAll, "maintenance window", "qilin").IsOk);
        Assert.True(RegistryOperations.RequestOperator(writer, RegistryOperations.RequestStop, RegistryOperations.SiteScope(siteId), "this machine reboots", "qilin").IsOk);
        Assert.True(RegistryOperations.RequestOperator(writer, RegistryOperations.RequestStop, RegistryOperations.SiteScope(siteId + 1), "another machine", "qilin").IsOk);

        var first = RegistryOperations.ConsumeStop(writer, permit);
        Assert.True(first.TryGetValue(out var consumed), Refusal(first));
        Assert.NotNull(consumed);
        Assert.Equal(RegistryOperations.ScopeAll, consumed!.Scope);
        Assert.Equal("maintenance window", consumed.Reason);
        Assert.Equal(permit.SupervisorId, consumed.ConsumedBy);

        var second = RegistryOperations.ConsumeStop(writer, permit);
        Assert.True(second.TryGetValue(out var next));
        Assert.Equal(RegistryOperations.SiteScope(siteId), next!.Scope);

        var third = RegistryOperations.ConsumeStop(writer, permit);
        Assert.True(third.TryGetValue(out var none));
        Assert.Null(none);

        var standing = RegistryOperations.PendingRequests(writer.Connection, RegistryOperations.RequestStop, RegistryOperations.SiteScope(siteId + 1));
        Assert.Single(standing);
        Assert.Equal("another machine", standing[0].Reason);
    }

    [Fact]
    public void ADisplacedSupervisorConsumesNoStop()
    {
        var (path, siteId) = Home();
        using var writer = RegistryHome.OpenWriter(path);
        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "alpha", 100, "sha256:aa").TryGetValue(out var displaced));
        ExpireLease(writer, displaced.SupervisorId);
        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "beta", 200, "sha256:bb").TryGetValue(out var holder));
        Assert.True(RegistryOperations.RequestOperator(writer, RegistryOperations.RequestStop, RegistryOperations.ScopeAll, "stop", "qilin").IsOk);

        Assert.Equal("fenced", Code(RegistryOperations.ConsumeStop(writer, displaced)));
        Assert.Single(RegistryOperations.PendingRequests(writer.Connection, RegistryOperations.RequestStop, RegistryOperations.ScopeAll));

        var byHolder = RegistryOperations.ConsumeStop(writer, holder);
        Assert.True(byHolder.TryGetValue(out var consumed));
        Assert.Equal(holder.SupervisorId, consumed!.ConsumedBy);
    }

    [Fact]
    public void AStandingPauseIsReleasedOnceAndAConsumedStopCannotBe()
    {
        var (path, siteId) = Home();
        using var writer = RegistryHome.OpenWriter(path);
        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "alpha", 100, "sha256:aa").TryGetValue(out var permit));
        var pause = RegistryOperations.RequestOperator(writer, RegistryOperations.RequestPause, RegistryOperations.ScopeAll, "hold everything", "qilin");
        Assert.True(pause.TryGetValue(out var pauseId), Refusal(pause));
        Assert.Single(RegistryOperations.PendingRequests(writer.Connection, RegistryOperations.RequestPause, RegistryOperations.ScopeAll));

        Assert.True(RegistryOperations.ReleaseRequest(writer, pauseId, "qilin", "done").IsOk);
        Assert.Empty(RegistryOperations.PendingRequests(writer.Connection, RegistryOperations.RequestPause, RegistryOperations.ScopeAll));
        Assert.Equal("not_found", Code(RegistryOperations.ReleaseRequest(writer, pauseId, "qilin", "again")));

        Assert.True(RegistryOperations.RequestOperator(writer, RegistryOperations.RequestStop, RegistryOperations.ScopeAll, "stop", "qilin").TryGetValue(out var stopId));
        Assert.True(RegistryOperations.ConsumeStop(writer, permit).IsOk);
        Assert.Equal("not_found", Code(RegistryOperations.ReleaseRequest(writer, stopId, "qilin", "undo")));
    }

    [Fact]
    public void AnUnknownKindOrScopeIsRefusedBeforeAnyWrite()
    {
        var (path, _) = Home();
        using var writer = RegistryHome.OpenWriter(path);
        Assert.Equal("invalid_request", Code(RegistryOperations.RequestOperator(writer, "halt", RegistryOperations.ScopeAll, "r", "q")));
        Assert.Equal("invalid_request", Code(RegistryOperations.RequestOperator(writer, RegistryOperations.RequestStop, "machine:1", "r", "q")));
        Assert.Equal("invalid_request", Code(RegistryOperations.RequestOperator(writer, RegistryOperations.RequestStop, RegistryOperations.ScopeAll, "", "q")));
        using var count = writer.Connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM operator_requests";
        Assert.Equal(0L, Convert.ToInt64(count.ExecuteScalar()));
    }

    [Fact]
    public void SupervisorRowsAndRequestsAreNeverDeleted()
    {
        var (path, siteId) = Home();
        using var writer = RegistryHome.OpenWriter(path);
        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "alpha", 100, "sha256:aa").IsOk);
        Assert.True(RegistryOperations.RequestOperator(writer, RegistryOperations.RequestHold, RegistryOperations.SiteScope(siteId), "r", "q").IsOk);
        foreach (var table in new[] { "supervisors", "operator_requests" })
        {
            using var delete = writer.Connection.CreateCommand();
            delete.CommandText = $"DELETE FROM {table}";
            var error = Assert.Throws<SqliteException>(() => delete.ExecuteNonQuery());
            Assert.Contains("never deleted", error.Message, StringComparison.Ordinal);
        }
    }
}
