using Fern.Registry;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// A machine runs what its capacity declares, and the claim is where that is
/// settled.
///
/// WHY. `sites.capacity_json` was written by `register-site` and read only by the
/// placement, which counts busy slots outside any transaction. Two
/// `run-study-cells` invocations on one machine therefore each read "nothing
/// busy" and each claimed, and two trainings landed on the one 4070 — which
/// AGENTS.md rule 1 forbids. The count that decides now lives inside the claim's
/// own transaction, and an attempt whose supervisor has stopped beating does not
/// hold a slot, because the alternative is a machine that never takes work again
/// after one crash.
/// </summary>
public sealed class SiteCapacityClaimTests
{
    const string Config = """{"schema":"fern.training.v2","model":{"type":"fern"},"runtime":{"device":"cuda"}}""";

    static (RegistryWriter Writer, IReadOnlyList<long> CellIds, long SiteId) Home(long slots, int cells = 2)
    {
        var writer = RegistryHome.OpenWriter(ProductionHome.CreateRegistry());
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
            writer, "local-4070", """{"device":"cuda","admitted_models":["fern"]}""",
            $$"""{"slots":{{slots}}}""");
        Assert.True(site.TryGetValue(out var registeredSite));
        var declared = Enumerable.Range(0, cells).Select(ordinal => new RegistryOperations.CompiledCell(
            ordinal, $"cell-{ordinal}", "lorenz.series", 42, Config, """{"device":"cuda","requires_cuda":true}""", """{"unit_results":"retained"}""")).ToList();
        var study = RegistryOperations.RegisterStudy(
            writer, "shared-card", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, declared);
        Assert.True(study.TryGetValue(out var registered));
        // Slice 6 (row 31): the Site holds the study's source, or the claim refuses it.
        ProductionHome.DeclareCodeFrom(writer, registeredSite.SiteId);
        return (writer, registered.CellIds, registeredSite.SiteId);
    }

    /// <summary>Slice 6: the newest source, held by one more Site.</summary>
    static void Hold(RegistryWriter writer, long siteId)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT MAX(id) FROM sources";
        var sourceId = Convert.ToInt64(command.ExecuteScalar());
        ProductionHome.DeclareCodeFrom(writer, siteId);
    }


    static string Refusal<T>(RegistryResult<T> result) =>
        result.TryGetRefusal(out var refusal) ? refusal.Detail : "";

    static void Silent(RegistryWriter writer, long attemptId, TimeSpan ago)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "UPDATE attempts SET last_progress_at = $at WHERE id = $id";
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.Subtract(ago).ToString("o"));
        command.Parameters.AddWithValue("$id", attemptId);
        command.ExecuteNonQuery();
    }

    /// <summary>The failure this exists for: two claimants, one card, one slot.</summary>
    [Fact]
    public void TwoClaimantsOnAOneSlotSiteGetOneTrainingBetweenThem()
    {
        var (writer, cellIds, siteId) = Home(slots: 1);
        using var _ = writer;

        var first = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        var second = RegistryOperations.TryStartAttempt(writer, cellIds[1], siteId, "{}");

        Assert.True(first.IsOk, Refusal(first));
        Assert.False(second.IsOk, "a one-slot machine does not take a second cell");
        Assert.Contains("already holds 1 open attempt(s)", Refusal(second), StringComparison.Ordinal);
        Assert.Contains("its capacity is 1", Refusal(second), StringComparison.Ordinal);
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM attempts";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    /// <summary>A machine that declares two slots takes two, and refuses the third.</summary>
    [Fact]
    public void ASiteTakesAsManyCellsAsItDeclaresAndNoMore()
    {
        var (writer, cellIds, siteId) = Home(slots: 2, cells: 3);
        using var _ = writer;

        Assert.True(RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}").IsOk);
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellIds[1], siteId, "{}").IsOk);
        var third = RegistryOperations.TryStartAttempt(writer, cellIds[2], siteId, "{}");

        Assert.False(third.IsOk);
        Assert.Contains("already holds 2 open attempt(s)", Refusal(third), StringComparison.Ordinal);
    }

    /// <summary>
    /// CHG 2026-09-26 (slice W2 of leases-and-watching-a-running-cell, the
    /// owner's choice: notify, do not re-dispatch). An attempt whose supervisor
    /// stopped beating may still be training on a machine nobody can reach
    /// (three AutoDL machines went silent together at 00:49 EDT that night, two
    /// of them training), so silence alone frees nothing: a person gives the
    /// attempt up, and until then its machine is occupied. The Home tells the
    /// owner after 30 minutes. This replaces the earlier rule that "the cell
    /// comes back by itself", which re-ran such trainings from the start.
    /// </summary>
    [Fact]
    public void ASilentAttemptKeepsItsSlotUntilAPersonGivesItUp()
    {
        var (writer, cellIds, siteId) = Home(slots: 1);
        using var _ = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}").TryGetValue(out var attempt));
        Silent(writer, attempt.AttemptId, RegistryOperations.StaleAfter + TimeSpan.FromMinutes(1));

        var refused = RegistryOperations.TryStartAttempt(writer, cellIds[1], siteId, "{}");
        Assert.False(refused.IsOk);
        Assert.Contains("already holds 1 open attempt(s)", Refusal(refused), StringComparison.Ordinal);

        Assert.True(RegistryOperations.FailAttempt(writer, attempt.AttemptId, attempt.FencingEpoch,
            RegistryOperations.AttemptLost, """{"detail":"given up by a person"}""").IsOk);
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellIds[1], siteId, "{}").IsOk);
    }

    /// <summary>
    /// A renewal keeps the slot: the attempt is only free when nobody is behind
    /// it, and a supervisor that beats is somebody.
    /// </summary>
    [Fact]
    public void ARenewedAttemptKeepsItsSlot()
    {
        var (writer, cellIds, siteId) = Home(slots: 1);
        using var _ = writer;
        var first = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        Assert.True(first.TryGetValue(out var attempt));
        Silent(writer, attempt.AttemptId, RegistryOperations.StaleAfter + TimeSpan.FromMinutes(1));
        Assert.True(RegistryOperations.RenewAttempt(writer, attempt.AttemptId, attempt.FencingEpoch).IsOk);

        var second = RegistryOperations.TryStartAttempt(writer, cellIds[1], siteId, "{}");

        Assert.False(second.IsOk, "an attempt that just beat is still holding the machine");
    }

    /// <summary>
    /// CHG 2026-09-26 (slice W2 of leases-and-watching-a-running-cell, the
    /// owner's choice: notify, do not re-dispatch). An attempt whose supervisor
    /// stopped beating may still be training on a machine nobody can reach
    /// (three AutoDL machines went silent together at 00:49 EDT that night, two
    /// of them training), so silence alone frees nothing: a person gives the
    /// attempt up, and until then its machine is occupied. The Home tells the
    /// owner after 30 minutes. This replaces the earlier rule that "the cell
    /// comes back by itself", which re-ran such trainings from the start.
    /// A silent attempt keeps its cell out of the runnable list and refuses a
    /// second claim of it; its supervisor, if it was only unreachable, can still
    /// renew and finish, and its result is accepted.
    /// </summary>
    [Fact]
    public void ASilentAttemptKeepsItsCellAndCanStillFinish()
    {
        var (writer, cellIds, siteId) = Home(slots: 1, cells: 1);
        using var _ = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}").TryGetValue(out var unreachable));
        Silent(writer, unreachable.AttemptId, RegistryOperations.StaleAfter + TimeSpan.FromMinutes(1));

        Assert.Empty(RegistryOperations.ListRunnableCells(writer.Connection));
        var second = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        Assert.Contains("already has an open attempt", Refusal(second), StringComparison.Ordinal);

        // The machine comes back: its supervisor renews and finishes, and the result stands.
        Assert.True(RegistryOperations.RenewAttempt(writer, unreachable.AttemptId, unreachable.FencingEpoch).IsOk);
        Assert.True(RegistryOperations.FinishAttempt(
            writer, unreachable.AttemptId, unreachable.FencingEpoch, """{"schema":"fern.study_unit_results.v4"}""").IsOk);
    }

    /// <summary>
    /// Slice W2: the cell runs again only after a person gave its silent attempt
    /// up, as `retry-cell` does, and the attempt that was given up can no longer
    /// write.
    /// </summary>
    [Fact]
    public void ACellWhoseSilentAttemptAPersonGaveUpRunsAgain()
    {
        var (writer, cellIds, siteId) = Home(slots: 1, cells: 1);
        using var _ = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}").TryGetValue(out var stranded));
        Silent(writer, stranded.AttemptId, RegistryOperations.StaleAfter + TimeSpan.FromMinutes(1));

        Assert.True(RegistryOperations.FailAttempt(writer, stranded.AttemptId, stranded.FencingEpoch,
            RegistryOperations.AttemptLost, """{"detail":"given up by a person"}""").IsOk);

        Assert.Single(RegistryOperations.ListRunnableCells(writer.Connection));
        var second = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        Assert.True(second.TryGetValue(out var fresh), Refusal(second));
        Assert.Equal(stranded.Ordinal + 1, fresh.Ordinal);
        Assert.False(RegistryOperations.RenewAttempt(writer, stranded.AttemptId, stranded.FencingEpoch).IsOk,
            "an attempt a person gave up must not be able to renew");
    }

    /// <summary>An attempt that is still beating keeps its cell: only silence frees it.</summary>
    [Fact]
    public void AnAttemptThatIsStillBeatingKeepsItsCellOutOfTheRunnableList()
    {
        var (writer, cellIds, siteId) = Home(slots: 1, cells: 1);
        using var _ = writer;
        var first = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        Assert.True(first.TryGetValue(out var attempt));
        Silent(writer, attempt.AttemptId, RegistryOperations.StaleAfter + TimeSpan.FromMinutes(1));
        Assert.True(RegistryOperations.RenewAttempt(writer, attempt.AttemptId, attempt.FencingEpoch).IsOk);

        Assert.Empty(RegistryOperations.ListRunnableCells(writer.Connection));
        Assert.False(RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}").IsOk);
    }

    /// <summary>
    /// A cell gets a bounded number of tries and then waits for a person. The
    /// count is `attempts.ordinal`, which the schema already kept; what is new
    /// is acting on it, in the claim, where a stale projection cannot let two
    /// supervisors spend the same try.
    /// </summary>
    [Fact]
    public void ACellSpendsItsAttemptsAndThenWaitsForAPerson()
    {
        var (writer, cellIds, siteId) = Home(slots: 1, cells: 1);
        using var _ = writer;
        for (var spent = 0; spent < 3; spent++)
        {
            var started = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}", maxAttempts: 3);
            Assert.True(started.TryGetValue(out var attempt), $"try {spent + 1} should be allowed");
            Assert.True(RegistryOperations.FailAttempt(
                writer, attempt.AttemptId, attempt.FencingEpoch, RegistryOperations.AttemptFailed,
                """{"detail":"the pod went away","deterministic":false}""").IsOk);
            // CHG 2026-09-11 night (slice 2): a transient failure waits out the
            // retry window before the next try; the budget is what this test is
            // about, so each failure is aged past the window.
            using var age = writer.Connection.CreateCommand();
            age.CommandText = "UPDATE attempts SET ended_at = '2000-01-01T00:00:00.0000000+00:00' WHERE id = $id";
            age.Parameters.AddWithValue("$id", attempt.AttemptId);
            Assert.Equal(1, age.ExecuteNonQuery());
        }

        Assert.Empty(RegistryOperations.ListRunnableCells(writer.Connection, null, 3));
        var fourth = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}", maxAttempts: 3);
        Assert.False(fourth.IsOk);
        Assert.Contains("has spent its 3 attempts", Refusal(fourth), StringComparison.Ordinal);
        Assert.Contains("retry-cell", Refusal(fourth), StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusal that would repeat unchanged is not retried at all, budget or no
    /// budget: retrying a card the compiler refuses burns a machine printing one
    /// sentence.
    /// </summary>
    [Fact]
    public void ADeterministicRefusalIsNotRetriedEvenWithBudgetLeft()
    {
        var (writer, cellIds, siteId) = Home(slots: 1, cells: 1);
        using var _ = writer;
        var started = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}", maxAttempts: 3);
        Assert.True(started.TryGetValue(out var attempt));
        Assert.True(RegistryOperations.FailAttempt(
            writer, attempt.AttemptId, attempt.FencingEpoch, RegistryOperations.AttemptFailed,
            """{"detail":"use: unknown dataset 'pointmaze.physical_state'","deterministic":true}""").IsOk);

        Assert.Empty(RegistryOperations.ListRunnableCells(writer.Connection, null, 3));
    }

    /// <summary>
    /// A failure that belongs to the MACHINE is not tried again on that machine.
    /// Ported from the retired OtherProfile class: the cell is still worth
    /// running, and spending the rest of its budget on the one host that cannot
    /// run it is how a cell ends up waiting for a person while other machines
    /// sit idle.
    /// </summary>
    [Fact]
    public void AMachineSpecificFailureIsNotTriedAgainOnThatMachine()
    {
        var (writer, cellIds, siteId) = Home(slots: 1, cells: 1);
        using var _ = writer;
        long otherSite;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sites (name, capabilities_json, capacity_json, created_at)
                VALUES ('pod-5090', '{"device":"cuda","admitted_models":["fern"]}', '{"slots":1}', '2026-09-11')
                RETURNING id
                """;
            otherSite = Convert.ToInt64(command.ExecuteScalar());
        }
        Hold(writer, otherSite);
        var started = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        Assert.True(started.TryGetValue(out var attempt));
        Assert.True(RegistryOperations.FailAttempt(
            writer, attempt.AttemptId, attempt.FencingEpoch, RegistryOperations.AttemptFailed,
            // The worker's own failure document, written literally here: this
            // project does not reference the scheduler, and the field is the
            // contract between them.
            """
            {"detail":"the cell was refused by its runtime: runner_failed: a study cell requires CUDA",
             "deterministic":false,"site_specific":true}
            """).IsOk);

        var again = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        Assert.False(again.IsOk, "the machine that could not run it must not get another try");
        Assert.Contains("belongs to the machine, not to the cell", Refusal(again), StringComparison.Ordinal);

        // Another machine may still have it, with its budget intact.
        var elsewhere = RegistryOperations.TryStartAttempt(writer, cellIds[0], otherSite, "{}");
        Assert.True(elsewhere.TryGetValue(out var moved), Refusal(elsewhere));
        Assert.Equal(2, moved.Ordinal);
    }

    /// <summary>
    /// A transient failure with budget left is runnable again, with no person
    /// involved. CHG 2026-09-11 night (slice 2): after the retry window, not
    /// within seconds; the retired dispatch block gave a refused unit ten
    /// minutes before offering it again, and so does this.
    /// </summary>
    [Fact]
    public void ATransientFailureWithBudgetLeftIsRunnableAgainAfterTheWindow()
    {
        var (writer, cellIds, siteId) = Home(slots: 1, cells: 1);
        using var _ = writer;
        var started = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}", maxAttempts: 3);
        Assert.True(started.TryGetValue(out var attempt));
        Assert.True(RegistryOperations.FailAttempt(
            writer, attempt.AttemptId, attempt.FencingEpoch, RegistryOperations.AttemptFailed,
            """{"detail":"CUDA out of memory","deterministic":false}""").IsOk);

        Assert.Empty(RegistryOperations.ListRunnableCells(writer.Connection, null, 3));
        using (var age = writer.Connection.CreateCommand())
        {
            age.CommandText = "UPDATE attempts SET ended_at = '2000-01-01T00:00:00.0000000+00:00' WHERE id = $id";
            age.Parameters.AddWithValue("$id", attempt.AttemptId);
            Assert.Equal(1, age.ExecuteNonQuery());
        }
        Assert.Single(RegistryOperations.ListRunnableCells(writer.Connection, null, 3));
    }

    /// <summary>
    /// The reconciler closes a silent attempt even when nobody is claiming its
    /// cell, so a study no Site can place still tells the truth about itself.
    /// </summary>
    [Fact]
    public void TheReconcilerClosesASilentAttemptWithoutAClaim()
    {
        var (writer, cellIds, siteId) = Home(slots: 1, cells: 1);
        using var _ = writer;
        var started = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        Assert.True(started.TryGetValue(out var attempt));
        Silent(writer, attempt.AttemptId, RegistryOperations.StaleAfter + TimeSpan.FromMinutes(1));

        var reconciled = RegistryOperations.ReconcileSilentAttempts(writer);

        Assert.True(reconciled.TryGetValue(out var lost));
        var one = Assert.Single(lost);
        Assert.Equal(attempt.AttemptId, one.AttemptId);
        Assert.Equal("cell-0", one.CellName);
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT state FROM attempts WHERE id = $id";
        command.Parameters.AddWithValue("$id", attempt.AttemptId);
        Assert.Equal("lost", command.ExecuteScalar() as string);
        // Running it again finds nothing: a closed attempt is closed once.
        Assert.True(RegistryOperations.ReconcileSilentAttempts(writer).TryGetValue(out var again));
        Assert.Empty(again);
    }

    /// <summary>A finished attempt frees the machine at once; capacity counts only open work.</summary>
    [Fact]
    public void AFinishedCellFreesTheMachine()
    {
        var (writer, cellIds, siteId) = Home(slots: 1);
        using var _ = writer;
        var first = RegistryOperations.TryStartAttempt(writer, cellIds[0], siteId, "{}");
        Assert.True(first.TryGetValue(out var attempt));
        Assert.True(RegistryOperations.FinishAttempt(
            writer, attempt.AttemptId, attempt.FencingEpoch, """{"schema":"fern.study_unit_results.v4"}""").IsOk);

        Assert.True(RegistryOperations.TryStartAttempt(writer, cellIds[1], siteId, "{}").IsOk);
    }

    /// <summary>
    /// A Site that declares its cards runs one cell per card, and each attempt
    /// records which card it holds. This is the three-Site reality: one of them
    /// has several GPUs, and a count alone cannot say which run is on which.
    /// </summary>
    [Fact]
    public void ASiteWithTwoCardsRunsTwoCellsOnTwoCardsAndRefusesTheThird()
    {
        var writer = RegistryHome.OpenWriter(ProductionHome.CreateRegistry());
        using var _ = writer;
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
            writer, "pod-two-cards",
            """{"device":"cuda","admitted_models":["fern"],"devices":["GPU-aaaa","GPU-bbbb"]}""",
            """{"slots":8}""");
        Assert.True(site.TryGetValue(out var registered));
        var declared = Enumerable.Range(0, 3).Select(ordinal => new RegistryOperations.CompiledCell(
            ordinal, $"cell-{ordinal}", "lorenz.series", 42, Config, """{"device":"cuda","requires_cuda":true}""", """{"unit_results":"retained"}""")).ToList();
        var study = RegistryOperations.RegisterStudy(
            writer, "two-cards", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, declared);
        Assert.True(study.TryGetValue(out var cells));
        ProductionHome.DeclareCodeFrom(writer, registered.SiteId);

        var first = RegistryOperations.TryStartAttempt(writer, cells.CellIds[0], registered.SiteId, "{}");
        var second = RegistryOperations.TryStartAttempt(writer, cells.CellIds[1], registered.SiteId, "{}");
        var third = RegistryOperations.TryStartAttempt(writer, cells.CellIds[2], registered.SiteId, "{}");

        Assert.True(first.TryGetValue(out var one));
        Assert.True(second.TryGetValue(out var two));
        Assert.Equal("GPU-aaaa", one.Device);
        Assert.Equal("GPU-bbbb", two.Device);
        Assert.False(third.IsOk, "a two-card machine does not take a third cell even with eight slots declared");
        Assert.Contains("every one is held by a live attempt", Refusal(third), StringComparison.Ordinal);

        // The grant is on the attempt, with the generation of the Site's own
        // declaration, so a re-registered machine's old grants are readable as
        // belonging to the machine it was.
        using var command2 = writer.Connection.CreateCommand();
        command2.CommandText =
            "SELECT detail_json FROM events WHERE attempt_id = $id AND kind = $kind";
        command2.Parameters.AddWithValue("$id", one.AttemptId);
        command2.Parameters.AddWithValue("$kind", RegistryOperations.DeviceGrantEvent);
        var grant = command2.ExecuteScalar() as string ?? "";
        Assert.Contains("\"device\":\"GPU-aaaa\"", grant, StringComparison.Ordinal);
        Assert.Contains("\"generation\":", grant, StringComparison.Ordinal);
        Assert.Contains("pod-two-cards", grant, StringComparison.Ordinal);
    }

    /// <summary>
    /// Slice W2: a card held by an attempt whose supervisor went silent stays
    /// held until a person gives the attempt up.
    /// </summary>
    [Fact]
    public void ACardHeldByASilentAttemptStaysHeldUntilAPersonGivesItUp()
    {
        var writer = RegistryHome.OpenWriter(ProductionHome.CreateRegistry());
        using var _ = writer;
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
            writer, "one-card", """{"device":"cuda","admitted_models":["fern"],"devices":["GPU-only"]}""",
            """{"slots":1}""");
        Assert.True(site.TryGetValue(out var registered));
        var declared = Enumerable.Range(0, 2).Select(ordinal => new RegistryOperations.CompiledCell(
            ordinal, $"cell-{ordinal}", "lorenz.series", 42, Config, """{"device":"cuda","requires_cuda":true}""", """{"unit_results":"retained"}""")).ToList();
        var study = RegistryOperations.RegisterStudy(
            writer, "one-card-study", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, declared);
        Assert.True(study.TryGetValue(out var cells));
        ProductionHome.DeclareCodeFrom(writer, registered.SiteId);
        var first = RegistryOperations.TryStartAttempt(writer, cells.CellIds[0], registered.SiteId, "{}");
        Assert.True(first.TryGetValue(out var held));
        Assert.Equal("GPU-only", held.Device);

        Silent(writer, held.AttemptId, RegistryOperations.StaleAfter + TimeSpan.FromMinutes(1));
        Assert.False(RegistryOperations.TryStartAttempt(writer, cells.CellIds[1], registered.SiteId, "{}").IsOk);

        Assert.True(RegistryOperations.FailAttempt(writer, held.AttemptId, held.FencingEpoch,
            RegistryOperations.AttemptLost, """{"detail":"given up by a person"}""").IsOk);
        var second = RegistryOperations.TryStartAttempt(writer, cells.CellIds[1], registered.SiteId, "{}");
        Assert.True(second.TryGetValue(out var granted), Refusal(second));
        Assert.Equal("GPU-only", granted.Device);
    }

    /// <summary>
    /// A capacity that says nothing means one. A single-GPU host whose
    /// declaration cannot be read must not be assumed to take many.
    /// </summary>
    [Fact]
    public void ASiteWithNoReadableCapacityTakesOneCell()
    {
        var writer = RegistryHome.OpenWriter(ProductionHome.CreateRegistry());
        using var _ = writer;
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
            writer, "unsaid", """{"device":"cuda","admitted_models":["fern"]}""", """{"not_slots":"nonsense"}""");
        Assert.True(site.TryGetValue(out var registeredSite));
        var declared = Enumerable.Range(0, 2).Select(ordinal => new RegistryOperations.CompiledCell(
            ordinal, $"cell-{ordinal}", "lorenz.series", 42, Config, """{"device":"cuda","requires_cuda":true}""", """{"unit_results":"retained"}""")).ToList();
        var study = RegistryOperations.RegisterStudy(
            writer, "unsaid-card", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, declared);
        Assert.True(study.TryGetValue(out var registered));
        ProductionHome.DeclareCodeFrom(writer, registeredSite.SiteId);

        Assert.True(RegistryOperations.TryStartAttempt(writer, registered.CellIds[0], registeredSite.SiteId, "{}").IsOk);
        var second = RegistryOperations.TryStartAttempt(writer, registered.CellIds[1], registeredSite.SiteId, "{}");

        Assert.False(second.IsOk);
        Assert.Contains("its capacity is 1", Refusal(second), StringComparison.Ordinal);
    }
}
