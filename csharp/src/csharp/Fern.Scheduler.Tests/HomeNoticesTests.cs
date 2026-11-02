using System.Globalization;
using Fern.Registry;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Slices R1, R3, W2 and W3: the Home reads its Registry, asks the Gleam
/// summary what is due, shows it once on the desktop, and remembers it across
/// a restart. The cases are the night of 2026-09-26: requests standing for
/// days with nobody told, three rented machines silent from 00:49 EDT, and a
/// training that should be told, not killed, when it stops moving.
/// </summary>
public sealed class HomeNoticesTests : IDisposable
{
    readonly string _notices = Directory.CreateTempSubdirectory("home-notices-").FullName;

    public void Dispose() => Directory.Delete(_notices, recursive: true);

    sealed class Desktop : HomeNotices.IDesktop
    {
        public List<(string Title, string Body)> Shown { get; } = [];
        public bool Fails { get; set; }
        /// <summary>What WSL throws when it cannot start a Windows program at all.</summary>
        public bool CannotStart { get; set; }

        public void Show(string title, string body)
        {
            if (CannotStart) throw new System.ComponentModel.Win32Exception(8, "Exec format error");
            if (Fails) throw new InvalidOperationException("no desktop");
            Shown.Add((title, body));
        }
    }

    static string Ago(TimeSpan span) => DateTimeOffset.UtcNow.Subtract(span).ToString("o", CultureInfo.InvariantCulture);

    /// <summary>One Site holding the source, one study of <paramref name="cells"/> cells in state run.</summary>
    static (RegistryWriter Writer, long StudyId, IReadOnlyList<long> CellIds, long SiteId) Home(int cells = 2)
    {
        var writer = RegistryHome.OpenWriter(TestRegistry.CreateRegistry());
        long sourceId;
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO sources (commit_sha, declared_files_json, registered_from, created_at) VALUES ('0a', '[]', 'commit', '2026-09-26') RETURNING id";
            sourceId = Convert.ToInt64(command.ExecuteScalar());
        }
        Assert.True(RegistryOperations.RegisterSite(
            writer, "local-4070", """{"device":"cuda","admitted_models":["fern"]}""", """{"slots":1}""").TryGetValue(out var site));
        TestRegistry.DeclareCodeFrom(writer, site.SiteId);
        var declared = Enumerable.Range(0, cells).Select(ordinal => new RegistryOperations.CompiledCell(
            ordinal, $"cell-{ordinal}", "lorenz.series", 42,
            """{"schema":"fern.training.v2","model":{"type":"fern"},"runtime":{"device":"cuda"}}""", """{"device":"cuda","requires_cuda":true}""", """{"unit_results":"retained"}""")).ToList();
        Assert.True(RegistryOperations.RegisterStudy(writer, "density-suite", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, declared)
            .TryGetValue(out var registered));
        // The Site's supervisor holds its office, as a running Site's does.
        Assert.True(RegistryOperations.StartSupervisor(writer, site.SiteId, "MSI", 4242, "sha256:aa").IsOk);
        return (writer, registered.StudyId, registered.CellIds, site.SiteId);
    }

    static void Hold(RegistryWriter writer, long cellId, string by, TimeSpan placedAgo)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText =
            "INSERT INTO operator_requests (kind, scope, reason, requested_by, requested_at) VALUES ('hold', $scope, 'waits for a check', $by, $at)";
        command.Parameters.AddWithValue("$scope", RegistryOperations.CellScope(cellId));
        command.Parameters.AddWithValue("$by", by);
        command.Parameters.AddWithValue("$at", Ago(placedAgo));
        command.ExecuteNonQuery();
    }

    static HomeNotices.Summary Tick(RegistryWriter writer, HomeNotices.Notifier notifier, GleamProgram program) =>
        notifier.Tick(HomeNotices.ReadingOfEverySite(new SqliteStudyStore(writer), DateTimeOffset.UtcNow), program);

    [Fact]
    public void AHoldSkippingWaitingCellsForTwoHoursIsToldOnceEvenAfterARestart()
    {
        var (writer, _, cells, _) = Home();
        using var _ = writer;
        Hold(writer, cells[0], "codex", TimeSpan.FromHours(3));
        using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
        var desktop = new Desktop();

        var first = Tick(writer, new HomeNotices.Notifier(_notices, desktop, TextWriter.Null), program);
        var (title, body) = Assert.Single(desktop.Shown);
        Assert.Equal("Fern: one thing needs you", title);
        Assert.Contains("1 pause or hold has stood two hours or more", body);
        Assert.Contains("by codex (3 h, 1 cell)", body);
        Assert.Contains(first.Items, item => item.WaitsFor == "who_asked" && item.By == "codex");

        Tick(writer, new HomeNotices.Notifier(_notices, desktop, TextWriter.Null), program);
        Assert.Single(desktop.Shown);
    }

    [Fact]
    public void AHoldPlacedAnHourAgoIsShownButNotYetTold()
    {
        var (writer, _, cells, _) = Home();
        using var _ = writer;
        Hold(writer, cells[0], "codex", TimeSpan.FromHours(1));
        using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
        var desktop = new Desktop();

        var summary = Tick(writer, new HomeNotices.Notifier(_notices, desktop, TextWriter.Null), program);

        Assert.Empty(desktop.Shown);
        Assert.Contains(summary.Items, item => item.By == "codex");
    }

    /// <summary>Slice W2: an attempt with no heartbeat for 40 minutes is told, and "let it run" (wait for it) silences it.</summary>
    [Fact]
    public void ASilentAttemptIsToldAndALetRunAnswerSilencesIt()
    {
        var (writer, _, cells, siteId) = Home();
        using var _ = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[0], siteId, "{}").TryGetValue(out var attempt));
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText = "UPDATE attempts SET started_at = $started, last_progress_at = $beat WHERE id = $id";
            command.Parameters.AddWithValue("$started", Ago(TimeSpan.FromHours(2)));
            command.Parameters.AddWithValue("$beat", Ago(TimeSpan.FromMinutes(40)));
            command.Parameters.AddWithValue("$id", attempt.AttemptId);
            command.ExecuteNonQuery();
        }
        using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());

        var quiet = new Desktop();
        HomeNotices.Notifier.LetRun(_notices, $"silent_attempt:{attempt.AttemptId}", "qilin", DateTimeOffset.UtcNow);
        Tick(writer, new HomeNotices.Notifier(_notices, quiet, TextWriter.Null), program);
        Assert.Empty(quiet.Shown);

        File.Delete(Path.Combine(_notices, HomeNotices.Notifier.LetRunFile));
        var desktop = new Desktop();
        Tick(writer, new HomeNotices.Notifier(_notices, desktop, TextWriter.Null), program);
        var (_, body) = Assert.Single(desktop.Shown);
        Assert.Contains("has sent no heartbeat for 40 min", body);
        Assert.Contains("given up and its cell offered again when the silence reaches an hour", body);
    }

    /// <summary>Slice W3: the reading carries the trainer's own last epoch, apart from the heartbeat.</summary>
    [Fact]
    public void ATrainingWhoseEpochsStoppedWhileItsSupervisorBeatsIsToldAsMaybeStuck()
    {
        var (writer, _, cells, siteId) = Home();
        using var _ = writer;
        Assert.True(RegistryOperations.TryStartAttempt(writer, cells[0], siteId, "{}").TryGetValue(out var attempt));
        using (var command = writer.Connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE attempts SET started_at = $started, last_progress_at = $beat WHERE id = $id; "
                + "INSERT INTO events (attempt_id, at, kind, phase, detail_json) VALUES ($id, $epoch, 'training_progress', NULL, '{\"current\":12,\"total\":50}')";
            command.Parameters.AddWithValue("$started", Ago(TimeSpan.FromHours(2)));
            command.Parameters.AddWithValue("$beat", Ago(TimeSpan.FromMinutes(1)));
            command.Parameters.AddWithValue("$epoch", Ago(TimeSpan.FromMinutes(45)));
            command.Parameters.AddWithValue("$id", attempt.AttemptId);
            command.ExecuteNonQuery();
        }
        using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
        var desktop = new Desktop();

        Tick(writer, new HomeNotices.Notifier(_notices, desktop, TextWriter.Null), program);

        var (_, body) = Assert.Single(desktop.Shown);
        Assert.Contains("has reported no progress for 45 min", body);
        Assert.Contains("keeps running until a person cancels it or says to let it run", body);
    }

    [Fact]
    public void ANotificationTheDesktopDidNotShowIsDueAgain()
    {
        var (writer, _, cells, _) = Home();
        using var _ = writer;
        Hold(writer, cells[0], "codex", TimeSpan.FromHours(3));
        using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
        var desktop = new Desktop { Fails = true };
        var notifier = new HomeNotices.Notifier(_notices, desktop, TextWriter.Null);

        Tick(writer, notifier, program);
        desktop.Fails = false;
        Tick(writer, notifier, program);

        Assert.Single(desktop.Shown);
    }

    /// <summary>Review, 2026-09-26: a Windows program WSL cannot start at all does not end the Home, and the notice is due again.</summary>
    [Fact]
    public void ADesktopThatCannotStartDoesNotEndTheHome()
    {
        var (writer, _, cells, _) = Home();
        using var _ = writer;
        Hold(writer, cells[0], "codex", TimeSpan.FromHours(3));
        using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
        var desktop = new Desktop { CannotStart = true };
        var notifier = new HomeNotices.Notifier(_notices, desktop, TextWriter.Null);

        Tick(writer, notifier, program);
        desktop.CannotStart = false;
        Tick(writer, notifier, program);

        Assert.Single(desktop.Shown);
    }

    /// <summary>Review, 2026-09-26: a problem that went away and came back is told again.</summary>
    [Fact]
    public void AProblemThatComesBackIsToldAgain()
    {
        var (writer, _, cells, _) = Home();
        using var _ = writer;
        Hold(writer, cells[0], "codex", TimeSpan.FromHours(3));
        using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
        var desktop = new Desktop();
        var notifier = new HomeNotices.Notifier(_notices, desktop, TextWriter.Null);
        Tick(writer, notifier, program);
        Assert.Single(desktop.Shown);

        using (var release = writer.Connection.CreateCommand())
        {
            release.CommandText = "UPDATE operator_requests SET released_at = $at, released_by = 'qilin'";
            release.Parameters.AddWithValue("$at", Ago(TimeSpan.Zero));
            release.ExecuteNonQuery();
        }
        Tick(writer, notifier, program);
        Assert.Single(desktop.Shown);

        Hold(writer, cells[1], "claude", TimeSpan.FromHours(3));
        Tick(writer, notifier, program);
        Assert.Equal(2, desktop.Shown.Count);
        Assert.Contains("by claude", desktop.Shown[1].Body);
    }

    static long SilentAttempt(RegistryWriter writer, long cellId, long siteId, TimeSpan silentFor, out long epoch)
    {
        Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, siteId, "{}").TryGetValue(out var attempt));
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "UPDATE attempts SET started_at = $started, last_progress_at = $beat, remote_execution_id = $remote WHERE id = $id";
        command.Parameters.AddWithValue("$started", Ago(silentFor + TimeSpan.FromHours(1)));
        command.Parameters.AddWithValue("$beat", Ago(silentFor));
        command.Parameters.AddWithValue("$remote", "autodl-container-bf16235c/1574");
        command.Parameters.AddWithValue("$id", attempt.AttemptId);
        command.ExecuteNonQuery();
        epoch = attempt.FencingEpoch;
        return attempt.AttemptId;
    }

    static string StateOf(RegistryWriter writer, long attemptId)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "SELECT state FROM attempts WHERE id = $id";
        command.Parameters.AddWithValue("$id", attemptId);
        return (string)command.ExecuteScalar()!;
    }

    /// <summary>
    /// The owner, 2026-09-26: "通知后30分钟通知我，30分钟我不管你就通知，然后重新派发". An attempt silent
    /// for an hour that nobody answered is given up by the Home, which says so, and its cell is offered again.
    /// </summary>
    [Fact]
    public void AnAttemptSilentForAnHourNobodyAnsweredIsGivenUpAndTold()
    {
        var (writer, _, cells, siteId) = Home();
        using var _ = writer;
        var attemptId = SilentAttempt(writer, cells[0], siteId, TimeSpan.FromMinutes(65), out var epoch);
        using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
        var desktop = new Desktop();
        var store = new SqliteStudyStore(writer);
        var notifier = new HomeNotices.Notifier(_notices, desktop, TextWriter.Null,
            which => HomeNotices.GiveUpSilent(store, which));

        Tick(writer, notifier, program);

        Assert.Equal(RegistryOperations.AttemptLost, StateOf(writer, attemptId));
        var (_, body) = Assert.Single(desktop.Shown);
        Assert.Contains($"attempt {attemptId}", body);
        Assert.Contains("it is given up, and its cell is offered again", body);
        Assert.Contains(RegistryOperations.ListRunnableCells(writer.Connection), cell => cell.CellId == cells[0]);
    }

    /// <summary>An hour-silent attempt a person answered "wait" to is kept.</summary>
    [Fact]
    public void AnAttemptAPersonSaidToWaitForIsKept()
    {
        var (writer, _, cells, siteId) = Home();
        using var _ = writer;
        var attemptId = SilentAttempt(writer, cells[0], siteId, TimeSpan.FromMinutes(65), out var epoch);
        HomeNotices.Notifier.LetRun(_notices, $"silent_attempt:{attemptId}", "qilin", DateTimeOffset.UtcNow);
        using var program = GleamProgram.FromShipment(GleamSchedulerDecisionTests.Shipment());
        var store = new SqliteStudyStore(writer);
        var notifier = new HomeNotices.Notifier(_notices, new Desktop(), TextWriter.Null,
            which => HomeNotices.GiveUpSilent(store, which));

        Tick(writer, notifier, program);

        Assert.Equal(RegistryOperations.AttemptOpened, StateOf(writer, attemptId));
    }

    /// <summary>
    /// The owner, 2026-09-26: an attempt on this machine whose process is gone is given up by
    /// looking, at once; one whose process lives, or that another machine ran, is kept.
    /// </summary>
    [Fact]
    public void ThisMachinesAttemptWhoseProcessIsGoneIsGivenUpByLooking()
    {
        var (writer, _, cells, siteId) = Home(cells: 3);
        using var _ = writer;
        var store = new SqliteStudyStore(writer);
        long Started(long cellId, string remote)
        {
            Assert.True(RegistryOperations.TryStartAttempt(writer, cellId, siteId, "{}").TryGetValue(out var attempt));
            Assert.True(RegistryOperations.RenewAttempt(writer, attempt.AttemptId, attempt.FencingEpoch, remote).IsOk);
            return attempt.AttemptId;
        }
        // One slot per cell here: the fixture's Site has one, so widen it.
        using (var widen = writer.Connection.CreateCommand())
        {
            widen.CommandText = "UPDATE sites SET capacity_json = '{\"slots\":3}' WHERE id = $id";
            widen.Parameters.AddWithValue("$id", siteId);
            widen.ExecuteNonQuery();
        }
        var gone = Started(cells[0], "MSI/1366929");
        var living = Started(cells[1], "MSI/4242");
        var elsewhere = Started(cells[2], "autodl-container/1366929");

        var given = StudyOrchestrator.GiveUpAttemptsWhoseProcessIsGone(
            store, siteId, "MSI", ownPid: 99, alive: pid => pid == 4242);

        Assert.Single(given);
        Assert.Contains("was run by process 1366929 on MSI, which is gone", given[0]);
        Assert.Equal(RegistryOperations.AttemptLost, StateOf(writer, gone));
        Assert.Equal(RegistryOperations.AttemptRunning, StateOf(writer, living));
        Assert.Equal(RegistryOperations.AttemptRunning, StateOf(writer, elsewhere));
    }
}
