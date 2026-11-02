using Fern.Registry;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// The line: what a Registry Home would run next, across every study, in its own order.
///
/// What is pinned here is that the line does not decide anything. The order is the offer's own, so a
/// priority set on a study moves its cells here exactly as it would move them in a claim; a pause
/// takes cells out of the offer rather than being applied by this projection; and the sentence about
/// why a cell waits is the store's, not a second account. A control panel that computed any of these
/// would disagree with the scheduler at the moment it matters most.
/// </summary>
public sealed class LineProjectionTests
{
    /// <summary>A Home with one Site of the given capacity and two studies of two cells each.</summary>
    static string Home(int slots = 1)
    {
        var path = TestRegistry.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        long sourceId;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at)
                VALUES ('5e87434e', '[]', 'commit', '2026-09-14') RETURNING id
                """;
            sourceId = Convert.ToInt64(command.ExecuteScalar());
        }
        long siteId;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                $$"""
                INSERT INTO sites (name, capabilities_json, capacity_json, created_at)
                VALUES ('local-4070', '{"device":"cuda","admitted_models":["fern"]}', '{"slots":{{slots}}}', '2026-09-14')
                RETURNING id
                """;
            siteId = Convert.ToInt64(command.ExecuteScalar());
        }
        foreach (var study in new[] { "first", "second" })
        {
            var cells = new List<RegistryOperations.CompiledCell>();
            for (var ordinal = 0; ordinal < 2; ordinal++)
            {
                cells.Add(new RegistryOperations.CompiledCell(
                    ordinal, $"{study}-cell-{ordinal}", "rossler_param_shock.series", 42,
                    """{"schema":"fern.training.v2","model":{"type":"fern"}}""",
                    """{"device":"cuda","requires_cuda":true}""",
                    """{"unit_results":"retained"}"""));
            }
            Assert.True(RegistryOperations.RegisterStudy(
                writer, study, 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, cells).IsOk);
        }
        TestRegistry.DeclareCodeFrom(writer, siteId);
        return path;
    }

    static LineProjection.Line Read(string path)
    {
        using var reader = RegistryHome.OpenReader(path);
        return LineProjection.Read(reader.Connection);
    }

    [Fact]
    public void EveryCellOfEveryStudyIsInTheOfferWithAPositionOfItsOwn()
    {
        var line = Read(Home());

        Assert.Equal("fern.registry_line.v1", line.Schema);
        Assert.Equal(4, line.Queued.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, line.Queued.Select(cell => cell.Position));
        Assert.Empty(line.Running);
        Assert.Empty(line.Blocked);
        Assert.Empty(line.SetAside);
        Assert.Empty(line.Standing);
        Assert.Equal(2, line.Queued.Select(cell => cell.Study).Distinct().Count());
    }

    [Fact]
    public void APriorityOnAStudyMovesItsCellsUpTheLine()
    {
        // The order is the offer's own, so setting a priority must move the line without this
        // projection knowing anything about priorities beyond reporting them.
        var path = Home();
        long secondId;
        using (var writer = RegistryHome.OpenWriter(path))
        {
            using var command = writer.Connection.CreateCommand();
            command.CommandText = "SELECT id FROM studies WHERE name = 'second'";
            secondId = Convert.ToInt64(command.ExecuteScalar());
            Assert.True(RegistryOperations.SetPriority(writer, $"study:{secondId}", "urgent", "it goes first", "test").IsOk);
        }

        var line = Read(path);

        Assert.Equal("second", line.Queued[0].Study);
        Assert.Equal("urgent", line.Queued[0].Priority);
        Assert.False(line.Queued[0].MovedLast);
        Assert.Equal("first", line.Queued[^1].Study);
        Assert.Equal("normal", line.Queued[^1].Priority);
    }

    [Fact]
    public void AStudyMovedLastIsShownBehindTheOthersAndSaysSo()
    {
        var path = Home();
        using (var writer = RegistryHome.OpenWriter(path))
        {
            using var command = writer.Connection.CreateCommand();
            command.CommandText = "SELECT id FROM studies WHERE name = 'first'";
            var firstId = Convert.ToInt64(command.ExecuteScalar());
            Assert.True(RegistryOperations.MoveLast(writer, $"study:{firstId}", "it can wait", "test").IsOk);
        }

        var line = Read(path);

        Assert.Equal("second", line.Queued[0].Study);
        Assert.Equal("first", line.Queued[^1].Study);
        Assert.True(line.Queued[^1].MovedLast);
        Assert.Equal("normal", line.Queued[^1].Priority);
    }

    [Fact]
    public void APauseTakesCellsOutOfTheOfferAndStandsWhereItCanBeSeenAndLifted()
    {
        var path = Home();
        using (var writer = RegistryHome.OpenWriter(path))
        {
            Assert.True(RegistryOperations.RequestOperator(writer, RegistryOperations.RequestPause, RegistryOperations.ScopeAll, "the pod is down", "qilin").IsOk);
        }

        var line = Read(path);

        Assert.Empty(line.Queued);
        var standing = Assert.Single(line.Standing);
        Assert.Equal("pause", standing.Kind);
        Assert.Equal("qilin", standing.RequestedBy);
        Assert.Equal("the pod is down", standing.Reason);
        Assert.StartsWith("release-request --request ", standing.Lift);
        // Out of the offer is not the same as invisible: every cell is now blocked, with the reason.
        Assert.Equal(4, line.Blocked.Count);
        Assert.All(line.Blocked, cell => Assert.Contains("paused by qilin", cell.Waiting));
        // A pause over every Site is a fact about each of them, not only a standing row.
        var site = Assert.Single(line.Sites);
        Assert.False(site.Available);
        Assert.Equal("pause", site.StopKind);
        Assert.Equal("all", site.StopScope);
        Assert.False(site.StoppedBySiteHold);
    }

    [Fact]
    public void ACellInTheOfferThatNothingCanTakeSaysSo()
    {
        // In the offer and placeable right now are different facts. With no slots, all four are
        // still offered and none can start, and the line must say the second thing too.
        var line = Read(Home(slots: 0));

        Assert.Equal(4, line.Queued.Count);
        Assert.All(line.Queued, cell => Assert.False(string.IsNullOrEmpty(cell.Waiting)));
        Assert.Contains("full", line.Queued[0].Waiting);
    }

    [Fact]
    public void ASiteIsFullAndForbiddenSeparately()
    {
        var path = Home(slots: 2);

        var free = Read(path);
        var site = Assert.Single(free.Sites);
        Assert.Equal("local-4070", site.Site);
        Assert.Equal("cuda", site.Device);
        Assert.Equal(2, site.Slots);
        Assert.Equal(0, site.Busy);
        Assert.False(site.Full);
        Assert.True(site.Available);
        Assert.Contains("nobody has held", site.Supervisor);

        long siteId;
        using (var writer = RegistryHome.OpenWriter(path))
        {
            using var command = writer.Connection.CreateCommand();
            command.CommandText = "SELECT id FROM sites WHERE name = 'local-4070'";
            siteId = Convert.ToInt64(command.ExecuteScalar());
            Assert.True(RegistryOperations.RequestOperator(
                writer, RegistryOperations.RequestPause, RegistryOperations.SiteScope(siteId),
                "cooling it down", "qilin").IsOk);
        }

        var stopped = Assert.Single(Read(path).Sites);

        // Nothing about its capacity changed; what changed is that the offer may not reach it.
        Assert.False(stopped.Available);
        Assert.False(stopped.Full);
        Assert.Equal(0, stopped.Busy);
        Assert.Equal("pause", stopped.StopKind);
        Assert.StartsWith("site:", stopped.StopScope);
        Assert.True(stopped.StopRequestId > 0);
        Assert.False(stopped.StoppedBySiteHold);
    }

    [Fact]
    public void AHoldOnASiteIsTheRequestEnableSiteLifts()
    {
        var path = Home();
        long siteId;
        using (var writer = RegistryHome.OpenWriter(path))
        {
            using var command = writer.Connection.CreateCommand();
            command.CommandText = "SELECT id FROM sites WHERE name = 'local-4070'";
            siteId = Convert.ToInt64(command.ExecuteScalar());
            Assert.True(RegistryOperations.RequestOperator(
                writer, RegistryOperations.RequestHold, RegistryOperations.SiteScope(siteId),
                "the card is out", "qilin").IsOk);
        }

        var stopped = Assert.Single(Read(path).Sites);

        Assert.False(stopped.Available);
        Assert.Equal("hold", stopped.StopKind);
        Assert.True(stopped.StoppedBySiteHold);
    }

    [Fact]
    public void AStudyAskedToStopIsSetAsideRatherThanBlocked()
    {
        // Owner, 2026-09-14: reading the two together was confusing. A study somebody decided to
        // stop is not an obstacle — nothing is waiting on it — and saying "blocked" invited the
        // reader to look for what was in its way.
        var path = Home();
        long firstId;
        using (var writer = RegistryHome.OpenWriter(path))
        {
            using var command = writer.Connection.CreateCommand();
            command.CommandText = "SELECT id FROM studies WHERE name = 'first'";
            firstId = Convert.ToInt64(command.ExecuteScalar());
            Assert.True(RegistryOperations.RequestStudyState(writer, firstId, RegistryOperations.StateCancel).IsOk);
        }

        var line = Read(path);

        Assert.Equal(2, line.SetAside.Count);
        Assert.All(line.SetAside, cell => Assert.Equal("first", cell.Study));
        Assert.All(line.SetAside, cell => Assert.Equal("cancel", cell.StudyState));
        // Nothing is obstructed, and the other study is untouched.
        Assert.Empty(line.Blocked);
        Assert.All(line.Queued, cell => Assert.Equal("second", cell.Study));
        // The registered list still names the stopped study, so a resume control can find it.
        Assert.Equal(new[] { "first", "second" }, line.Studies);
    }

    [Fact]
    public void ASetAsideStudyKeepsWhatItAlreadyAccepted()
    {
        // Set aside is not discarded. Deleting these rows would delete scientific evidence, which
        // is why nothing here removes anything: a cancelled study is not an empty one.
        var path = Home();
        using (var writer = RegistryHome.OpenWriter(path))
        {
            using var command = writer.Connection.CreateCommand();
            command.CommandText =
                """
                UPDATE cells SET accepted_attempt_id = (
                    SELECT id FROM attempts LIMIT 1)
                WHERE name = 'first-cell-0'
                """;
            command.ExecuteNonQuery();
            using var state = writer.Connection.CreateCommand();
            state.CommandText = "UPDATE studies SET requested_state = 'cancel' WHERE name = 'first'";
            state.ExecuteNonQuery();
        }

        var line = Read(path);

        // Whatever the accepted band holds, the cancelled study's unfinished cell is set aside and
        // not reported as an obstacle.
        Assert.All(line.SetAside, cell => Assert.Equal("first", cell.Study));
        Assert.Empty(line.Blocked);
    }

    [Fact]
    public void AnEmptyHomeIsAnEmptyLineRatherThanARefusal()
    {
        var line = Read(TestRegistry.CreateRegistry());

        Assert.Empty(line.Queued);
        Assert.Empty(line.Running);
        Assert.Empty(line.Blocked);
        Assert.Empty(line.Sites);
        Assert.NotEmpty(line.ReadAt);
    }
}
