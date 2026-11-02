using Fern.Registry;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// Slice 3 of the cell port (design note
/// docs/design/original/by_ai/2026-09-11-restore-the-behavior-port-the-input.md,
/// rows 4, 5, 30, 35): a Site's cards are held by `device_grants` rows, the
/// retired DeviceGrantAuthority unchanged, owned by the attempt and released
/// when it ends or is reconciled; an environment install's grant keeps a
/// trainer off the card; and a Site a person disabled claims nothing.
/// </summary>
public sealed class SiteDevicesAndGrantsTests
{
    const string Request = """{"schema":"fern.cell_request.v2","cell":"x"}""";

    static (RegistryWriter Writer, long StudyId, IReadOnlyList<long> CellIds, long SiteId) Home(int cells = 3)
    {
        var path = ProductionHome.CreateRegistry();
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
                VALUES ('two-cards', '{"device":"cuda","devices":["GPU-aaaa","GPU-bbbb"]}', '{"slots":2}', '2026-09-11') RETURNING id
                """;
            siteId = Convert.ToInt64(command.ExecuteScalar());
        }
        var declared = Enumerable.Range(0, cells).Select(ordinal => new RegistryOperations.CompiledCell(
            ordinal, $"cell-{ordinal}", "rossler_param_shock.series", 42,
            """{"schema":"fern.training.v2","model":{"type":"fern"},"runtime":{"device":"cuda"}}""",
            """{"device":"cuda","requires_cuda":true}""",
            """{"unit_results":"retained"}""")).ToList();
        var registered = RegistryOperations.RegisterStudy(writer, "cards", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, declared);
        Assert.True(registered.TryGetValue(out var study));
        ProductionHome.DeclareCodeFrom(writer, siteId);
        return (writer, study.StudyId, study.CellIds, siteId);
    }

    static string Refusal<T>(RegistryResult<T> result) => result.TryGetRefusal(out var refusal) ? refusal.Detail : "";

    static (string Device, long Generation, string? ReleasedReason) Grant(RegistryWriter writer, long attemptId)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT device_id, grant_generation, release_reason FROM device_grants WHERE owner_attempt_id = $owner";
        command.Parameters.AddWithValue("$owner", RegistryOperations.GrantOwner(attemptId));
        using var row = command.ExecuteReader();
        Assert.True(row.Read(), $"attempt {attemptId} holds no grant row");
        return (row.GetString(0), row.GetInt64(1), row.IsDBNull(2) ? null : row.GetString(2));
    }

    [Fact]
    public void EachClaimHoldsOneCardByAGrantRowAndTheThirdIsRefusedNamingTheHolders()
    {
        var (writer, _, cells, siteId) = Home();
        using var _ = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[0], siteId, Request).TryGetValue(out var first));
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[1], siteId, Request).TryGetValue(out var second));
        Assert.Equal("GPU-aaaa", first.Device);
        Assert.Equal("GPU-bbbb", second.Device);
        Assert.Equal(("GPU-aaaa", 1L, (string?)null), Grant(writer, first.AttemptId));
        Assert.Equal(("GPU-bbbb", 1L, (string?)null), Grant(writer, second.AttemptId));

        var third = RegistryOperations.TryStartAttempt(writer, cells[2], siteId, Request);
        Assert.False(third.IsOk);
        Assert.Contains("every one is held", Refusal(third), StringComparison.Ordinal);
    }

    [Fact]
    public void ACardIsHandedBackWhenTheAttemptEndsAndTheNextClaimTakesItAtTheNextGeneration()
    {
        var (writer, _, cells, siteId) = Home();
        using var _ = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[0], siteId, Request).TryGetValue(out var first));
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[1], siteId, Request).TryGetValue(out var second));

        Assert.True(RegistryOperations.FinishAttempt(writer, first.AttemptId, first.FencingEpoch, """{"done":true}""").IsOk);
        Assert.Equal("completed", Grant(writer, first.AttemptId).ReleasedReason);
        Assert.True(RegistryOperations.FailAttempt(writer, second.AttemptId, second.FencingEpoch, RegistryOperations.AttemptFailed,
            """{"detail":"it broke","deterministic":false,"failure_class":"transient"}""").IsOk);
        Assert.Equal("failed", Grant(writer, second.AttemptId).ReleasedReason);

        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[2], siteId, Request).TryGetValue(out var third));
        Assert.Equal("GPU-aaaa", third.Device);
        Assert.Equal(2L, Grant(writer, third.AttemptId).Generation);
    }

    /// <summary>
    /// CHG 2026-09-26 (slice W2): a silent attempt's card stays held; the
    /// reconciler, run by a person, gives it up and hands the grant back.
    /// </summary>
    [Fact]
    public void ASilentAttemptsCardStaysHeldAndTheReconcilerReleasesItsGrant()
    {
        var (writer, _, cells, siteId) = Home();
        using var _ = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[0], siteId, Request).TryGetValue(out var first));
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[1], siteId, Request).TryGetValue(out var second));
        using (var silence = writer.Connection.CreateCommand())
        {
            silence.CommandText = "UPDATE attempts SET started_at = '2000-01-01T00:00:00.0000000+00:00', last_progress_at = NULL WHERE id = $id";
            silence.Parameters.AddWithValue("$id", first.AttemptId);
            Assert.Equal(1, silence.ExecuteNonQuery());
        }
        // The card of a supervisor silent past the bound is still held.
        Assert.Contains("every one is held", Refusal(RegistryOperations.TryStartAttempt(writer, cells[2], siteId, Request)),
            StringComparison.Ordinal);
        // The reconciler, run by a person, hands the old grant back with the reason.
        Assert.True(RegistryOperations.ReconcileSilentAttempts(writer).IsOk);
        Assert.Contains("lost", Grant(writer, first.AttemptId).ReleasedReason ?? "", StringComparison.Ordinal);
        Assert.Null(Grant(writer, second.AttemptId).ReleasedReason);
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[2], siteId, Request).TryGetValue(out var third));
        Assert.Equal("GPU-aaaa", third.Device);
    }

    [Fact]
    public void AnEnvironmentInstallsGrantKeepsATrainerOffTheCard()
    {
        var (writer, _, cells, siteId) = Home();
        using var _ = writer;
        var installing = DeviceGrantAuthority.Acquire(writer, "GPU-aaaa", "two-cards", "environment_install", "install-attempt-1", 1);
        Assert.True(installing.TryGetValue(out var grant), Refusal(installing));

        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[0], siteId, Request).TryGetValue(out var first));
        Assert.Equal("GPU-bbbb", first.Device);
        var second = RegistryOperations.TryStartAttempt(writer, cells[1], siteId, Request);
        Assert.Contains("every one is held", Refusal(second), StringComparison.Ordinal);

        Assert.True(DeviceGrantAuthority.Release(writer, grant.GrantId, "install-attempt-1", "environment_admitted").IsOk);
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[1], siteId, Request).TryGetValue(out var after));
        Assert.Equal("GPU-aaaa", after.Device);
    }

    [Fact]
    public void ADisabledSiteClaimsNothingUntilItIsEnabled()
    {
        var (writer, _, cells, siteId) = Home();
        using var _ = writer;
        Assert.True(RegistryOperations.RequestOperator(writer, RegistryOperations.RequestHold, RegistryOperations.SiteScope(siteId), "fan is dead", "qilin").TryGetValue(out var holdId));
        var refused = RegistryOperations.TryStartAttempt(writer, cells[0], siteId, Request);
        Assert.Contains($"held over site:{siteId} by qilin (fan is dead)", Refusal(refused), StringComparison.Ordinal);
        Assert.True(RegistryOperations.ReleaseRequest(writer, holdId, "qilin", "fan replaced").IsOk);
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[0], siteId, Request).IsOk);
    }
}
