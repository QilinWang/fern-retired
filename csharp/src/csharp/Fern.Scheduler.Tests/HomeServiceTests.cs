using System.Net;
using System.Net.Sockets;
using Fern.Registry;
using Fern.Scheduler;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Slice 5 of the cell port (design note
/// docs/design/original/by_ai/2026-09-11-restore-the-behavior-port-the-input.md,
/// section 6, the owner's ruling for pull): a supervisor on another machine
/// works the same loop against the same Home through <see cref="HomeClient"/>
/// and <see cref="HomeService"/>, and the wire changes nothing the loop can
/// see — a claim is a claim, a refusal is a refusal, a null is a null.
/// </summary>
public sealed class HomeServiceTests
{
    sealed class CandidateTraffic(bool omitConfiguration = false) : DelegatingHandler(new HttpClientHandler())
    {
        public int PayloadReads { get; private set; }
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/ReadCell", StringComparison.Ordinal))
            {
                PayloadReads++;
                throw new InvalidOperationException("placement must not download a compiled Study");
            }
            var response = base.Send(request, cancellation);
            if (omitConfiguration && request.RequestUri.AbsolutePath.EndsWith("/ListRunnableCells", StringComparison.Ordinal))
            {
                var body = System.Text.Json.Nodes.JsonNode.Parse(response.Content.ReadAsStringAsync(cancellation).GetAwaiter().GetResult())!;
                foreach (var cell in body["value"]!.AsArray()) cell!.AsObject().Remove("ConfigJson");
                response.Content.Dispose();
                response.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            }
            return response;
        }
    }

    /// <summary>
    /// Slice P2: the ranks and the moves reach a supervisor that reads the
    /// Registry over HTTP exactly as a local one reads them, so the Gleam
    /// round on a rented Site orders the queue the claim does.
    /// </summary>
    [Fact]
    public void RanksAndMovesReadOverHttpAreTheRegistrysOwn()
    {
        var (writer, studyId, _) = Home(cells: 2);
        using var scope = writer;
        var cells = RegistryOperations.ListRunnableCells(writer.Connection, studyId);
        Assert.True(RegistryOperations.SetPriority(writer, RegistryOperations.StudyScope(studyId), "urgent", "first", "test").IsOk);
        Assert.True(RegistryOperations.MoveLast(writer, RegistryOperations.CellScope(cells[0].CellId), "later", "test").IsOk);
        var address = FreeListen();
        using var service = new HomeService(writer, address);
        service.Start();
        using var client = new HomeClient(new Uri(address), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(5));

        var remote = client.ReadDispatchOrder();
        var local = RegistryOperations.ReadDispatchOrder(writer.Connection);

        Assert.Equal(local.Priorities, remote.Priorities);
        Assert.Equal("urgent", remote.Priorities[RegistryOperations.StudyScope(studyId)]);
        var move = Assert.Single(remote.Moves);
        Assert.Equal(local.Moves[move.Key], move.Value);
        Assert.Equal(studyId, move.Value.BehindStudyId);
    }

    /// <summary>
    /// Slice W1: the office epoch a remote supervisor's claim carries reaches
    /// the Registry through the Home, so a supervisor displaced between two
    /// renewals claims nothing over HTTP either.
    /// </summary>
    [Fact]
    public void AClaimOverHttpCarriesItsOfficeEpoch()
    {
        var (writer, studyId, _) = Home(cells: 1, slots: 2);
        using var scope = writer;
        var siteId = Count(writer, "SELECT id FROM sites WHERE name = 'pod-5090'");
        var cell = Assert.Single(RegistryOperations.ListRunnableCells(writer.Connection, studyId));
        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "alpha", 100, "sha256:aa").TryGetValue(out var displaced));
        using (var expire = writer.Connection.CreateCommand())
        {
            expire.CommandText = "UPDATE supervisors SET lease_until = '2000-01-01T00:00:00.0000000+00:00' WHERE id = $id";
            expire.Parameters.AddWithValue("$id", displaced.SupervisorId);
            Assert.Equal(1, expire.ExecuteNonQuery());
        }
        Assert.True(RegistryOperations.StartSupervisor(writer, siteId, "beta", 200, "sha256:bb").TryGetValue(out var holder));
        var address = FreeListen();
        using var service = new HomeService(writer, address);
        service.Start();
        using var client = new HomeClient(new Uri(address), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(5));

        var stale = client.TryStartAttempt(cell.CellId, siteId, "{}", RegistryOperations.DefaultMaxAttempts, displaced.Epoch);
        Assert.True(stale.TryGetRefusal(out var refusal));
        Assert.Contains($"the office at epoch {displaced.Epoch} is not Site {siteId}'s current office", refusal.Detail);
        Assert.Equal(0, Count(writer, "SELECT COUNT(*) FROM attempts"));

        var current = client.TryStartAttempt(cell.CellId, siteId, "{}", RegistryOperations.DefaultMaxAttempts, holder.Epoch);
        Assert.True(current.IsOk);
    }

    [Fact]
    public void RemotePlacementPreservesPriorityAndAdmissionWithoutReadingStudyPayloads()
    {
        var (writer, studyId, _) = Home(cells: 3);
        using var scope = writer;
        var cells = RegistryOperations.ListRunnableCells(writer.Connection, studyId);
        using (var change = writer.Connection.CreateCommand())
        {
            change.CommandText = "UPDATE cells SET config_json = json_set(config_json, '$.model.type', 'conditional_nsf') WHERE id = $id";
            change.Parameters.AddWithValue("$id", cells[0].CellId);
            change.ExecuteNonQuery();
        }
        Assert.True(RegistryOperations.SetPriority(writer, $"cell:{cells[0].CellId}", "immediate", "unsupported model first", "test").IsOk);
        Assert.True(RegistryOperations.SetPriority(writer, $"cell:{cells[2].CellId}", "urgent", "supported model second", "test").IsOk);
        var address = FreeListen();
        using var service = new HomeService(writer, address);
        service.Start();
        var traffic = new CandidateTraffic();
        using var client = new HomeClient(new Uri(address), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(5), handler: traffic);
        var ran = new List<long>();
        var worker = new Worker(cell => { ran.Add(cell.CellId); return Completes(cell); });
        StudyOrchestrator.RunOnce(client, worker, studyId, CancellationToken.None, siteName: "pod-5090");
        Assert.Equal(new[] { cells[2].CellId }, ran);
        Assert.Equal(0, traffic.PayloadReads);
        Assert.Equal(1, Count(writer, "SELECT COUNT(*) FROM cells WHERE accepted_attempt_id IS NOT NULL"));
    }

    [Fact]
    public void OlderHomeWithoutCandidateConfigurationIsRefusedExplicitly()
    {
        var (writer, studyId, _) = Home(cells: 1);
        using var scope = writer;
        var address = FreeListen();
        using var service = new HomeService(writer, address);
        service.Start();
        var traffic = new CandidateTraffic(omitConfiguration: true);
        using var client = new HomeClient(new Uri(address), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(5), handler: traffic);
        var error = Assert.Throws<System.Text.Json.JsonException>(() => client.ListRunnableCells(studyId, 1));
        Assert.Contains("ConfigJson", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, traffic.PayloadReads);
    }

    sealed class LostReply(string operation) : DelegatingHandler(new HttpClientHandler())
    {
        public int Dropped { get; private set; }
        public List<string> RequestIds { get; } = [];
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellation)
        {
            var response = base.Send(request, cancellation);
            if (request.RequestUri!.AbsolutePath.EndsWith("/" + operation, StringComparison.Ordinal))
            {
                RequestIds.Add(request.Headers.GetValues("Idempotency-Key").Single());
                if (Dropped++ == 0)
                {
                    response.Dispose();
                    throw new HttpRequestException("the Home committed; its reply was lost");
                }
            }
            return response;
        }
    }

    [Theory]
    [InlineData("TryStartAttempt")]
    [InlineData("StartSupervisor")]
    [InlineData("FinishAttempt")]
    [InlineData("FailAttempt")]
    [InlineData("RecordEvent")]
    public void AReplyLostAfterCommitReplaysTheOriginalOutcome(string operation)
    {
        var (writer, studyId, _) = Home();
        using var scope = writer;
        var address = FreeListen();
        using var service = new HomeService(writer, address);
        service.Start();
        var handler = new LostReply(operation);
        using var client = new HomeClient(new Uri(address), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(5), handler: handler);
        var site = client.ListSites().Single();
        if (operation != "StartSupervisor")
        {
            var cell = client.ListRunnableCells(studyId, 3).First();
            Assert.True(client.TryStartAttempt(cell.CellId, site.SiteId, "{}", 3).TryGetValue(out var attempt));
            if (operation == "FinishAttempt")
                Assert.True(client.FinishAttempt(attempt.AttemptId, attempt.FencingEpoch, "{}").IsOk);
            if (operation == "FailAttempt")
                Assert.True(client.FailAttempt(attempt.AttemptId, attempt.FencingEpoch, "failed", "{}").IsOk);
            if (operation == "RecordEvent")
            {
                Assert.True(client.RecordEvent(attempt.AttemptId, attempt.FencingEpoch, "test_event", null, "{}").IsOk);
                Assert.Equal(1, Count(writer, "SELECT COUNT(*) FROM events WHERE kind = 'test_event'"));
            }
            Assert.Equal(1, Count(writer, "SELECT COUNT(*) FROM attempts"));
        }
        else
        {
            Assert.True(client.StartSupervisor(site.SiteId, "pod", 1, "sha256:one", 600).IsOk);
            Assert.Equal(1, Count(writer, "SELECT COUNT(*) FROM supervisors"));
        }
        Assert.Equal(2, handler.RequestIds.Count);
        Assert.Single(handler.RequestIds.Distinct());
    }
    sealed class Worker(Func<StudyOrchestrator.ClaimedCell, StudyOrchestrator.WorkerOutcome> run)
        : StudyOrchestrator.ICellWorker
    {
        public int Ran { get; private set; }

        public StudyOrchestrator.WorkerOutcome Run(StudyOrchestrator.ClaimedCell cell, CancellationToken cancellation)
        {
            Ran++;
            return run(cell);
        }
    }

    static StudyOrchestrator.WorkerOutcome Completes(StudyOrchestrator.ClaimedCell cell) =>
        new(true, $$"""{"cell":"{{cell.CellName}}"}""", "{}");

    static string FreeListen()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return $"http://127.0.0.1:{port}/";
    }

    static (RegistryWriter Writer, long StudyId, string Name) Home(int cells = 2, int slots = 1)
    {
        var path = TestRegistry.CreateRegistry();
        var writer = RegistryHome.OpenWriter(path);
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
                VALUES ('pod-5090', '{"device":"cuda","admitted_models":["fern"]}', '{"slots":{{slots}}}', '2026-09-11')
                RETURNING id
                """;
            siteId = Convert.ToInt64(command.ExecuteScalar());
        }
        var declared = Enumerable.Range(0, cells).Select(ordinal => new RegistryOperations.CompiledCell(
            ordinal, $"cell-{ordinal}", "rossler_param_shock.series", 42,
            """{"schema":"fern.training.v2","model":{"type":"fern"},"runtime":{"device":"cuda"}}""",
            """{"device":"cuda","requires_cuda":true}""",
            """{"unit_results":"retained"}""")).ToList();
        var registered = RegistryOperations.RegisterStudy(
            writer, "served", 1, """{"schema":"fern.compiled-study.v1"}""", sourceId, declared);
        Assert.True(registered.TryGetValue(out var value));
        // Slice 6 (row 31): the Site holds the study's source, or the claim refuses it.
        TestRegistry.DeclareCodeFrom(writer, siteId);
        return (writer, value.StudyId, "served");
    }

    static long Count(RegistryWriter writer, string sql)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static HomeClient Quick(string address) =>
        new(new Uri(address), retryFor: TimeSpan.FromSeconds(5), backoff: TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// The whole loop, claim to finish, over the wire: the study ends with
    /// every cell accepted and the file on the other side agrees.
    /// </summary>
    [Fact]
    public void ASupervisorOnAnotherMachineRunsTheStudyThroughTheHome()
    {
        var (writer, studyId, name) = Home(cells: 2);
        using var _ = writer;
        var address = FreeListen();
        using var service = new HomeService(writer, address);
        service.Start();
        using var client = Quick(address);
        var worker = new Worker(Completes);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = StudyOrchestrator.RunUntilIdle(
            client, worker, studyId, name, "pod-5090", maxCycles: 0, stdout, stderr,
            supervisor: new StudyOrchestrator.SupervisorIdentity("pod", 77, "sha256:pod"));

        Assert.Equal(0, exit);
        Assert.Equal(2, worker.Ran);
        Assert.Equal(2, Count(writer, "SELECT COUNT(*) FROM attempts WHERE state = 'completed'"));
        Assert.Equal(2, Count(writer, "SELECT COUNT(*) FROM cells WHERE accepted_attempt_id IS NOT NULL"));
        Assert.Equal(1, Count(writer, "SELECT COUNT(*) FROM supervisors WHERE host = 'pod' AND outcome = 'idle'"));
        Assert.Contains("study served is finished", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("2/2 cells accepted", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal("", stderr.ToString());
    }

    /// <summary>A refusal crosses as a refusal with its category, not as an HTTP error and not as success.</summary>
    [Fact]
    public void ARefusalCrossesTheWireAsARefusal()
    {
        var (writer, _, _) = Home();
        using var _ = writer;
        var address = FreeListen();
        using var service = new HomeService(writer, address);
        service.Start();
        using var client = Quick(address);
        var siteId = client.ListSites().Single().SiteId;

        Assert.True(client.StartSupervisor(siteId, "pod", 1, "sha256:one", 600).TryGetValue(out var first));
        var second = client.StartSupervisor(siteId, "pod", 2, "sha256:two", 600);

        Assert.False(second.IsOk);
        Assert.True(second.TryGetRefusal(out var refusal));
        Assert.Equal("held", refusal.ErrorCategory);
        Assert.True(client.RenewSupervisor(first, 600).IsOk);
        Assert.True(client.EndSupervisor(first, "idle", RegistryOperations.DispositionCompleted, 0).IsOk);
        var fenced = client.RenewAttempt(999, 1, null);
        Assert.False(fenced.IsOk);
    }

    /// <summary>Every nullable the loop and the worker send or receive survives the envelope.</summary>
    [Fact]
    public void TheWireCarriesEveryNullTheLoopSends()
    {
        var (writer, studyId, name) = Home(cells: 1);
        using var _ = writer;
        var address = FreeListen();
        using var service = new HomeService(writer, address);
        service.Start();
        using var client = Quick(address);

        Assert.Null(client.FindStudy("nobody"));
        Assert.Equal(studyId, client.FindStudy(name));
        Assert.Null(client.ReadCell(999));
        var cell = client.ListRunnableCells(null, RegistryOperations.DefaultMaxAttempts).Single();
        var registered = client.ReadCell(cell.CellId);
        Assert.NotNull(registered);
        Assert.Contains("fern.training.v2", registered.ConfigJson, StringComparison.Ordinal);
        Assert.Equal(42, registered.Seed);
        Assert.Empty(client.BusySlots());
        Assert.Empty(client.PendingRequests(RegistryOperations.RequestStop, [RegistryOperations.ScopeAll]));
        Assert.True(client.ReconcileSilentAttempts(null).TryGetValue(out var reconciled));
        Assert.Empty(reconciled);
        var siteId = client.ListSites().Single().SiteId;
        Assert.True(client.StartSupervisor(siteId, "pod", 1, "sha256:one", 600).TryGetValue(out var permit));
        Assert.True(client.ConsumeStop(permit).TryGetValue(out var stop));
        Assert.Null(stop);
        Assert.True(client.ReadStudy(studyId).TryGetValue(out var head));
        Assert.Equal(name, head.Name);
        Assert.Equal(RegistryOperations.DefaultMaxAttempts, client.StudyAttemptBudget(studyId));
        Assert.Equal(TestRegistryPath(writer), client.HealthHome());
    }

    static string TestRegistryPath(RegistryWriter writer) => writer.Connection.DataSource;

    /// <summary>
    /// Nothing serves the address: the client tries for its window and then
    /// says so by name, which the command turns into a retryable exit.
    /// </summary>
    [Fact]
    public void AHomeNobodyServesIsUnreachableAfterTheRetryWindow()
    {
        var address = FreeListen();
        using var client = new HomeClient(
            new Uri(address), retryFor: TimeSpan.FromMilliseconds(300), backoff: TimeSpan.FromMilliseconds(50));

        var thrown = Assert.Throws<HomeUnreachableException>(() => client.ListSites());

        Assert.Contains("did not answer ListSites", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>An operation nobody serves is the wire's own error, never a refusal and never retried.</summary>
    [Fact]
    public async Task AnOperationNobodyServesIsAProtocolError()
    {
        var (writer, _, _) = Home();
        using var _ = writer;
        var address = FreeListen();
        using var service = new HomeService(writer, address);
        service.Start();
        using var http = new HttpClient();

        using var response = http.Send(new HttpRequestMessage(HttpMethod.Post, address + "v1/DropEverything")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("no store operation is named DropEverything", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
