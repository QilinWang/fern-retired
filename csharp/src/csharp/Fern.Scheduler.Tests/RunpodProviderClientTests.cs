using System.Net;
using System.Text;
using Fern.Registry;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// HTTP-adapter tests only.  The handler is a bounded protocol double, not a
/// provider canary: it proves the request/response reduction before any live
/// credential or Pod mutation is authorized.
/// </summary>
public sealed class RunpodProviderClientTests
{
    [Fact]
    public void inventory_maps_the_configured_public_endpoint_to_one_immutable_pod_id()
    {
        using var handler = new ScriptedHandler(
            JsonResponse(
                """
                {"data":{"myself":{"pods":[
                  {"id":"pod-a","runtime":{"ports":[{"ip":"198.51.100.42","publicPort":22133}]}},
                  {"id":"pod-b","runtime":{"ports":[{"ip":"198.51.100.43","publicPort":22133}]}}
                ]}}}
                """));
        using var http = new HttpClient(handler);
        var client = new RunpodGraphQlClient(http);

        var resolved = client.ResolvePodForSshEndpoint(
            Config(),
            RunpodSshEndpoint.Parse("fern@198.51.100.42:22133"),
            TimeSpan.FromSeconds(3));

        Assert.Equal("pod-a", resolved);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/graphql", request.Uri.AbsolutePath);
        Assert.Contains("api_key=fixture-token", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("ResolvePodForSshEndpoint", request.Body, StringComparison.Ordinal);
        Assert.Contains("publicPort", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void inventory_refuses_an_ssh_endpoint_that_maps_to_multiple_pods()
    {
        using var handler = new ScriptedHandler(
            JsonResponse(
                """
                {"data":{"myself":{"pods":[
                  {"id":"pod-a","runtime":{"ports":[{"ip":"198.51.100.42","publicPort":22133}]}},
                  {"id":"pod-b","runtime":{"ports":[{"ip":"198.51.100.42","publicPort":22133}]}}
                ]}}}
                """));
        using var http = new HttpClient(handler);
        var client = new RunpodGraphQlClient(http);

        var error = Assert.Throws<RunpodProviderProtocolException>(() => client.ResolvePodForSshEndpoint(
            Config(),
            RunpodSshEndpoint.Parse("198.51.100.42:22133"),
            TimeSpan.FromSeconds(3)));

        Assert.Contains("more than one Pod", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void stop_accepts_only_the_matching_pod_id_and_exited_provider_response()
    {
        using var handler = new ScriptedHandler(
            JsonResponse("""{"data":{"podStop":{"id":"pod-a","desiredStatus":"EXITED"}}}"""));
        using var http = new HttpClient(handler);
        var client = new RunpodGraphQlClient(http);

        var terminal = client.StopPod(Config(), "pod-a", TimeSpan.FromSeconds(3));

        Assert.Equal("pod-a", terminal.ProviderPodId);
        Assert.Equal(ProviderDesiredStatus.Exited, terminal.DesiredStatus);
        var request = Assert.Single(handler.Requests);
        Assert.Contains("mutation StopPod", request.Body, StringComparison.Ordinal);
        Assert.Contains("podStop", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"podId\":\"pod-a\"", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"pod_id\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void stop_does_not_treat_a_nonterminal_provider_status_as_a_terminal_receipt()
    {
        using var handler = new ScriptedHandler(
            JsonResponse("""{"data":{"podStop":{"id":"pod-a","desiredStatus":"RUNNING"}}}"""));
        using var http = new HttpClient(handler);
        var client = new RunpodGraphQlClient(http);

        var error = Assert.Throws<RunpodProviderProtocolException>(() =>
            client.StopPod(Config(), "pod-a", TimeSpan.FromSeconds(3)));

        Assert.Contains("id and desiredStatus EXITED", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void reconciliation_observes_only_the_stored_pod_id_and_never_inferrs_exit_from_absence()
    {
        using var handler = new ScriptedHandler(
            JsonResponse(
                """
                {"data":{"myself":{"pods":[
                  {"id":"pod-a","desiredStatus":"EXITED"},
                  {"id":"pod-b","desiredStatus":"RUNNING"}
                ]}}}
                """),
            JsonResponse("""{"data":{"myself":{"pods":[]}}}"""));
        using var http = new HttpClient(handler);
        var client = new RunpodGraphQlClient(http);

        Assert.Equal(
            RunpodPodObservation.Exited,
            client.ObserveStoredPod(Config(), "pod-a", TimeSpan.FromSeconds(3)));
        Assert.Equal(
            RunpodPodObservation.Missing,
            client.ObserveStoredPod(Config(), "pod-missing", TimeSpan.FromSeconds(3)));
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Contains("query ObservePod", request.Body, StringComparison.Ordinal));
    }

    [Fact]
    public void post_dispatch_unknown_reconciles_only_the_stored_pod_id_to_typed_exited()
    {
        var registry = TestRegistry.CreateRegistry();
        using (var writer = RegistryHome.OpenWriter(registry))
        {
            RegisterSshSite(writer, "runpod-site");
        }

        using (var stopHandler = new InventoryThenNetworkFailureHandler())
        using (var stopHttp = new HttpClient(stopHandler))
        {
            var error = Assert.Throws<ProviderLifecycleRefusal>(() => ProviderLifecycle.Stop(
                registry,
                "runpod-site",
                RunpodSshEndpoint.Parse("198.51.100.42:22133"),
                new ProviderLifecycleActor("cli", "operator-a"),
                "stop-key-a",
                TimeSpan.FromSeconds(3),
                Config(),
                new RunpodGraphQlClient(stopHttp)));
            Assert.Contains("network_unknown", error.Message, StringComparison.Ordinal);
        }

        using (var reader = RegistryHome.OpenReader(registry))
        {
            var unknown = ProviderStopAuthority.LoadForReconcile(
                reader.Connection,
                new ProviderStopLookup(
                    ProviderKind.Runpod,
                    new ProviderLifecycleActor("cli", "operator-a"),
                    "stop-key-a"));
            Assert.True(unknown.TryGetValue(out var request));
            Assert.Equal("pod-a", request.ProviderPodId);
            Assert.Equal(ProviderStopState.NetworkUnknown, request.State);
        }

        using var reconcileHandler = new ScriptedHandler(
            JsonResponse("""{"data":{"myself":{"pods":[{"id":"pod-a","desiredStatus":"EXITED"}]}}}"""));
        using var reconcileHttp = new HttpClient(reconcileHandler);
        var report = ProviderLifecycle.Reconcile(
            registry,
            new ProviderLifecycleActor("cli", "operator-a"),
            "stop-key-a",
            TimeSpan.FromSeconds(3),
            Config(),
            new RunpodGraphQlClient(reconcileHttp));

        Assert.Equal(ProviderStopOutcome.ProviderExited, report.Outcome);
        Assert.Equal("pod-a", report.Request.ProviderPodId);
        Assert.Equal(ProviderStopState.Terminal, report.Request.State);
    }

    [Fact]
    public void reconciliation_refuses_a_prepared_request_without_contacting_provider_inventory()
    {
        var registry = TestRegistry.CreateRegistry();
        using (var writer = RegistryHome.OpenWriter(registry))
        {
            RegisterSshSite(writer, "runpod-site");
            var prepared = ProviderStopAuthority.Prepare(
                writer,
                new PrepareProviderStopWrite(
                    ProviderKind.Runpod,
                    "pod-a",
                    "runpod-site",
                    ProviderDesiredStatus.Exited,
                    new ProviderLifecycleActor("cli", "operator-a"),
                    "stop-key-prepared",
                    30_000));
            Assert.True(prepared.IsOk);
        }

        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler);
        var error = Assert.Throws<ProviderLifecycleRefusal>(() => ProviderLifecycle.Reconcile(
            registry,
            new ProviderLifecycleActor("cli", "operator-a"),
            "stop-key-prepared",
            TimeSpan.FromSeconds(3),
            Config(),
            new RunpodGraphQlClient(http)));

        Assert.Contains("prepared, not network-unknown", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void network_unknown_stop_replay_requires_reconciliation_without_another_provider_request()
    {
        var registry = TestRegistry.CreateRegistry();
        var actor = new ProviderLifecycleActor("cli", "operator-a");
        using (var writer = RegistryHome.OpenWriter(registry))
        {
            RegisterSshSite(writer, "runpod-site");
            var prepared = ProviderStopAuthority.Prepare(
                writer,
                new PrepareProviderStopWrite(
                    ProviderKind.Runpod,
                    "pod-a",
                    "runpod-site",
                    ProviderDesiredStatus.Exited,
                    actor,
                    "stop-key-network-unknown",
                    30_000));
            Assert.True(prepared.TryGetValue(out var preparedRequest));
            Assert.True(ProviderStopAuthority.MarkNetworkUnknown(
                writer,
                new MarkProviderStopNetworkUnknownWrite(
                    preparedRequest.ProviderStopRequestId,
                    actor,
                    "the provider response was unavailable after dispatch")).IsOk);
        }

        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler);
        var error = Assert.Throws<ProviderLifecycleRefusal>(() => ProviderLifecycle.Stop(
            registry,
            "runpod-site",
            RunpodSshEndpoint.Parse("198.51.100.42:22133"),
            actor,
            "stop-key-network-unknown",
            TimeSpan.FromSeconds(3),
            Config(),
            new RunpodGraphQlClient(http)));

        Assert.Contains("reconcile-runpod-pod-stop", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
        using var reader = RegistryHome.OpenReader(registry);
        var stored = ProviderStopAuthority.LoadForReconcile(
            reader.Connection,
            new ProviderStopLookup(ProviderKind.Runpod, actor, "stop-key-network-unknown"));
        Assert.True(stored.TryGetValue(out var request));
        Assert.Equal(ProviderStopState.NetworkUnknown, request.State);
    }

    [Fact]
    public void network_unknown_pod_refuses_a_new_idempotency_key_before_second_provider_mutation()
    {
        var registry = TestRegistry.CreateRegistry();
        var actor = new ProviderLifecycleActor("cli", "operator-a");
        using (var writer = RegistryHome.OpenWriter(registry))
        {
            RegisterSshSite(writer, "runpod-site");
            var prepared = ProviderStopAuthority.Prepare(
                writer,
                new PrepareProviderStopWrite(
                    ProviderKind.Runpod,
                    "pod-a",
                    "runpod-site",
                    ProviderDesiredStatus.Exited,
                    actor,
                    "stop-key-first",
                    30_000));
            Assert.True(prepared.TryGetValue(out var preparedRequest));
            Assert.True(ProviderStopAuthority.MarkNetworkUnknown(
                writer,
                new MarkProviderStopNetworkUnknownWrite(
                    preparedRequest.ProviderStopRequestId,
                    actor,
                    "the provider response was unavailable after dispatch")).IsOk);
        }

        using var handler = new ScriptedHandler(JsonResponse(
            """{"data":{"myself":{"pods":[{"id":"pod-a","runtime":{"ports":[{"ip":"198.51.100.42","publicPort":22133}]}}]}}}"""));
        using var http = new HttpClient(handler);
        var error = Assert.Throws<ProviderLifecycleRefusal>(() => ProviderLifecycle.Stop(
            registry,
            "runpod-site",
            RunpodSshEndpoint.Parse("198.51.100.42:22133"),
            actor,
            "stop-key-second",
            TimeSpan.FromSeconds(3),
            Config(),
            new RunpodGraphQlClient(http)));

        Assert.Contains("network_unknown", error.Message, StringComparison.Ordinal);
        Assert.Contains("reconcile", error.Message, StringComparison.Ordinal);
        var request = Assert.Single(handler.Requests);
        Assert.Contains("ResolvePodForSshEndpoint", request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("mutation StopPod", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void terminal_stop_replay_returns_the_durable_receipt_without_provider_inventory_or_mutation()
    {
        var registry = TestRegistry.CreateRegistry();
        var actor = new ProviderLifecycleActor("cli", "operator-a");
        using (var writer = RegistryHome.OpenWriter(registry))
        {
            RegisterSshSite(writer, "runpod-site");
            var prepared = ProviderStopAuthority.Prepare(
                writer,
                new PrepareProviderStopWrite(
                    ProviderKind.Runpod,
                    "pod-a",
                    "runpod-site",
                    ProviderDesiredStatus.Exited,
                    actor,
                    "stop-key-terminal",
                    30_000));
            Assert.True(prepared.TryGetValue(out var preparedRequest));
            Assert.True(ProviderStopAuthority.RecordTerminal(
                writer,
                new RecordProviderStopTerminalWrite(
                    preparedRequest.ProviderStopRequestId,
                    actor,
                    new ProviderTerminalResponse("pod-a", ProviderDesiredStatus.Exited))).IsOk);
        }

        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler);
        var replay = ProviderLifecycle.Stop(
            registry,
            "runpod-site",
            RunpodSshEndpoint.Parse("198.51.100.42:22133"),
            actor,
            "stop-key-terminal",
            TimeSpan.FromSeconds(3),
            Config(),
            new RunpodGraphQlClient(http));

        Assert.Equal(ProviderStopOutcome.ProviderExited, replay.Outcome);
        Assert.Equal(ProviderStopState.Terminal, replay.Request.State);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task expired_without_submission_replay_and_reconcile_return_the_durable_outcome_without_provider_traffic()
    {
        var registry = TestRegistry.CreateRegistry();
        var actor = new ProviderLifecycleActor("cli", "operator-a");
        ProviderStopRequest prepared;
        using (var writer = RegistryHome.OpenWriter(registry))
        {
            RegisterSshSite(writer, "runpod-site");
            prepared = Require(ProviderStopAuthority.Prepare(
                writer,
                new PrepareProviderStopWrite(
                    ProviderKind.Runpod,
                    "pod-a",
                    "runpod-site",
                    ProviderDesiredStatus.Exited,
                    actor,
                    "stop-key-expired",
                    1_000)));
        }

        await Task.Delay(TimeSpan.FromMilliseconds(1_100));

        using (var writer = RegistryHome.OpenWriter(registry))
        {
            var expired = ProviderStopAuthority.ExpireWithoutSubmission(
                writer,
                new ExpireProviderStopWithoutSubmissionWrite(
                    prepared.ProviderStopRequestId,
                    actor,
                    "deadline elapsed before a provider submission started"));
            Assert.True(expired.TryGetValue(out var request));
            Assert.Equal(ProviderStopState.ExpiredWithoutSubmission, request.State);
        }

        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler);
        var client = new RunpodGraphQlClient(http);
        var stopReplay = ProviderLifecycle.Stop(
            registry,
            "runpod-site",
            RunpodSshEndpoint.Parse("198.51.100.42:22133"),
            actor,
            "stop-key-expired",
            TimeSpan.FromSeconds(3),
            Config(),
            client);
        Assert.Equal(ProviderStopOutcome.ExpiredWithoutSubmission, stopReplay.Outcome);
        Assert.Equal(ProviderStopState.ExpiredWithoutSubmission, stopReplay.Request.State);

        var reconcile = ProviderLifecycle.Reconcile(
            registry,
            actor,
            "stop-key-expired",
            TimeSpan.FromSeconds(3),
            Config(),
            client);
        Assert.Equal(ProviderStopOutcome.ExpiredWithoutSubmission, reconcile.Outcome);
        Assert.Equal(ProviderStopState.ExpiredWithoutSubmission, reconcile.Request.State);
        Assert.Empty(handler.Requests);

        using var after = RegistryHome.OpenWriter(registry);
        var laterRequest = ProviderStopAuthority.Prepare(
            after,
            new PrepareProviderStopWrite(
                ProviderKind.Runpod,
                "pod-a",
                "runpod-site",
                ProviderDesiredStatus.Exited,
                actor,
                "stop-key-after-expiry",
                30_000));
        Assert.True(laterRequest.TryGetValue(out var later));
        Assert.Equal(ProviderStopState.Prepared, later.State);
    }

    [Fact]
    public void explicit_credential_injection_and_existing_toml_both_keep_the_token_out_of_registry_types()
    {
        var directory = Directory.CreateTempSubdirectory("fern-runpod-config-");
        var configPath = Path.Combine(directory.FullName, "config.toml");
        File.WriteAllText(
            configPath,
            "apikey = \"fixture-token\" # local secret, never a Registry value\n"
            + "apiurl = \"https://provider.test/graphql\"\n");
        var priorToken = Environment.GetEnvironmentVariable("FERN_RUNPOD_TEST_TOKEN");
        Environment.SetEnvironmentVariable("FERN_RUNPOD_TEST_TOKEN", "fixture-token");
        try
        {
            var fromFile = RunpodProviderConfig.ReadExisting(configPath);
            var fromEnvironment = RunpodProviderConfig.ReadExplicitEnvironment(
                "FERN_RUNPOD_TEST_TOKEN",
                "https://provider.test/graphql");
            Assert.Equal(fromFile.ApiUrl, fromEnvironment.ApiUrl);
            Assert.Equal("fixture-token", fromFile.ApiKey);
            var receiptJson = RegistryJson.Serialize(new ProviderStopRequest(
                "request-a",
                ProviderKind.Runpod,
                "pod-a",
                "site-a",
                "revision-a",
                ProviderDesiredStatus.Exited,
                new ProviderLifecycleActor("cli", "operator-a"),
                "key-a",
                30_000,
                1_755_000_030_000,
                ProviderStopState.Prepared,
                null,
                null,
                null,
                null,
                null,
                null));
            Assert.DoesNotContain("fixture-token", receiptJson, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FERN_RUNPOD_TEST_TOKEN", priorToken);
        }
    }

    static RunpodProviderConfig Config() =>
        new("fixture-token", new Uri("https://provider.test/graphql"));

    static T Require<T>(RegistryResult<T> result)
    {
        Assert.True(result.TryGetValue(out var value));
        return value;
    }

    // CHG 2026-09-11 night (slice 9): the Site is the `sites` row register-site mints.
    static void RegisterSshSite(RegistryWriter writer, string siteId)
    {
        var registered = RegistryOperations.RegisterSite(
            writer, siteId, """{"executor":"ssh","device":"cuda","admitted_models":["fern"]}""", """{"slots":1}""");
        Assert.True(registered.IsOk);
    }

    static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        readonly Queue<HttpResponseMessage> responses = new(responses);

        public List<RecordedRequest> Requests { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var stream = request.Content?.ReadAsStream(cancellationToken)
                ?? throw new InvalidOperationException("GraphQL request did not have a body");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri ?? throw new InvalidOperationException("missing URI"), reader.ReadToEnd()));
            return responses.Count != 0
                ? responses.Dequeue()
                : throw new InvalidOperationException("test handler had no scripted provider response");
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    sealed class InventoryThenNetworkFailureHandler : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var stream = request.Content?.ReadAsStream(cancellationToken)
                ?? throw new InvalidOperationException("GraphQL request did not have a body");
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            var body = reader.ReadToEnd();
            if (body.Contains("ResolvePodForSshEndpoint", StringComparison.Ordinal))
            {
                return JsonResponse(
                    """{"data":{"myself":{"pods":[{"id":"pod-a","runtime":{"ports":[{"ip":"198.51.100.42","publicPort":22133}]}}]}}}""");
            }

            if (body.Contains("mutation StopPod", StringComparison.Ordinal))
            {
                throw new HttpRequestException("test network loss after dispatch");
            }

            throw new InvalidOperationException("unexpected GraphQL operation");
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);
}
