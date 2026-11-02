using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fern.Registry;
using Fern.Scheduler;

namespace Fern.Run.Cli;

/// <summary>
/// The public RunPod lifecycle boundary.  It first resolves an SSH endpoint
/// through typed provider inventory, then records the immutable Pod identity
/// in Registry before submitting a bounded GraphQL stop request.  It never
/// SSHes, kills a Fern process, or treats a Worker Site exit as a provider
/// receipt.
/// </summary>
public static class ProviderLifecycleVerbs
{
    const string NetworkUnknownDetail =
        "RunPod submission did not yield a matching typed Pod EXITED receipt; reconcile the stored Pod id";
    const string ExpiredWithoutSubmissionDetail =
        "the durable provider deadline elapsed before a Pod stop submission started";

    public static void Add(Command parent)
    {
        parent.Subcommands.Add(StopRunpodPod());
        parent.Subcommands.Add(ReconcileRunpodPodStop());
    }

    static Command StopRunpodPod()
    {
        var command = new Command(
            "stop-runpod-pod",
            "Resolve one SSH endpoint to a RunPod Pod, request EXITED, and record only its matching typed provider receipt");
        var registry = RequiredOption("--registry", "Registry Home that will own the durable provider request");
        var site = RequiredOption("--site", "Registered SSH Worker Site whose profile revision is frozen into this request");
        var sshEndpoint = RequiredOption("--ssh-endpoint", "Configured SSH endpoint, as host:public-port or user@host:public-port; used only for provider inventory resolution");
        var actorId = RequiredOption("--actor-id", "Operator identity recorded with the provider lifecycle request");
        var idempotencyKey = RequiredOption("--idempotency-key", "Stable operator-generated key for one provider stop request");
        var deadlineSeconds = RequiredOption<int>("--deadline-seconds", "Bound for the provider request, from 1 through 300 seconds");
        var credentials = AddCredentialOptions(command);
        foreach (var option in new Option[] { registry, site, sshEndpoint, actorId, idempotencyKey, deadlineSeconds })
        {
            command.Options.Add(option);
        }

        command.SetAction(parsed =>
        {
            try
            {
                var deadline = ReadDeadline(parsed.GetValue(deadlineSeconds));
                var config = credentials.Read(parsed);
                var endpoint = RunpodSshEndpoint.Parse(parsed.GetValue(sshEndpoint)!);
                using var client = new HttpClient();
                var report = Stop(
                    parsed.GetValue(registry)!,
                    parsed.GetValue(site)!,
                    endpoint,
                    new ProviderLifecycleActor("cli", parsed.GetValue(actorId)!),
                    parsed.GetValue(idempotencyKey)!,
                    deadline,
                    config,
                    new RunpodGraphQlClient(client));
                Console.Out.WriteLine(RegistryJson.Serialize(report));
                return 0;
            }
            catch (ProviderLifecycleRefusal error)
            {
                Console.Error.WriteLine($"stop-runpod-pod failed: {error.Message}");
                return 1;
            }
            catch (RegistryFault error)
            {
                Console.Error.WriteLine($"stop-runpod-pod failed: {error.Message}");
                return 1;
            }
            catch (IOException error)
            {
                Console.Error.WriteLine($"stop-runpod-pod failed: {error.Message}");
                return 1;
            }
        });
        return command;
    }

    static Command ReconcileRunpodPodStop()
    {
        var command = new Command(
            "reconcile-runpod-pod-stop",
            "Re-read one durable RunPod stop request and accept terminal only for its stored Pod id and EXITED provider status");
        var registry = RequiredOption("--registry", "Registry Home that owns the durable provider request");
        var actorId = RequiredOption("--actor-id", "Operator identity that created the provider lifecycle request");
        var idempotencyKey = RequiredOption("--idempotency-key", "The original provider stop request key");
        var deadlineSeconds = RequiredOption<int>("--deadline-seconds", "Bound for the provider status read, from 1 through 300 seconds");
        var credentials = AddCredentialOptions(command);
        foreach (var option in new Option[] { registry, actorId, idempotencyKey, deadlineSeconds })
        {
            command.Options.Add(option);
        }

        command.SetAction(parsed =>
        {
            try
            {
                var deadline = ReadDeadline(parsed.GetValue(deadlineSeconds));
                var config = credentials.Read(parsed);
                using var client = new HttpClient();
                var report = Reconcile(
                    parsed.GetValue(registry)!,
                    new ProviderLifecycleActor("cli", parsed.GetValue(actorId)!),
                    parsed.GetValue(idempotencyKey)!,
                    deadline,
                    config,
                    new RunpodGraphQlClient(client));
                Console.Out.WriteLine(RegistryJson.Serialize(report));
                return report.Outcome == ProviderStopOutcome.ProviderExited ? 0 : 1;
            }
            catch (ProviderLifecycleRefusal error)
            {
                Console.Error.WriteLine($"reconcile-runpod-pod-stop failed: {error.Message}");
                return 1;
            }
            catch (RegistryFault error)
            {
                Console.Error.WriteLine($"reconcile-runpod-pod-stop failed: {error.Message}");
                return 1;
            }
            catch (IOException error)
            {
                Console.Error.WriteLine($"reconcile-runpod-pod-stop failed: {error.Message}");
                return 1;
            }
        });
        return command;
    }

    internal static ProviderStopReport Stop(
        string registryPath,
        string siteId,
        RunpodSshEndpoint sshEndpoint,
        ProviderLifecycleActor actor,
        string idempotencyKey,
        TimeSpan deadline,
        RunpodProviderConfig config,
        RunpodGraphQlClient client)
    {
        // The idempotency key is already a durable request identity.  Once it
        // names an unknown or terminal request, inventory is not an authority
        // to submit another provider mutation: reconciliation must use the
        // immutable stored Pod instead.
        var existing = LoadExistingStopRequest(registryPath, actor, idempotencyKey);
        if (existing is not null)
        {
            if (existing.State == ProviderStopState.Terminal)
            {
                return new ProviderStopReport(
                    "fern.runpod_provider_stop.v1", ProviderStopOutcome.ProviderExited, existing);
            }

            if (existing.State == ProviderStopState.NetworkUnknown)
            {
                throw new ProviderLifecycleRefusal(
                    "provider stop is network-unknown; reconcile-runpod-pod-stop must read the stored Pod id before another provider mutation is considered");
            }

            if (existing.State == ProviderStopState.ExpiredWithoutSubmission)
            {
                return new ProviderStopReport(
                    "fern.runpod_provider_stop.v1", ProviderStopOutcome.ExpiredWithoutSubmission, existing);
            }
        }

        ProviderStopRequest prepared;
        if (existing is null)
        {
            // Inventory is read before any Registry write.  A missing or
            // ambiguous endpoint therefore cannot create a request against a
            // guessed Pod.
            var podId = client.ResolvePodForSshEndpoint(config, sshEndpoint, deadline);
            using var writer = RegistryHome.OpenWriter(registryPath);
            prepared = RequireValue(ProviderStopAuthority.Prepare(
                writer,
                new PrepareProviderStopWrite(
                    ProviderKind.Runpod,
                    podId,
                    siteId,
                    ProviderDesiredStatus.Exited,
                    actor,
                    idempotencyKey,
                    checked((long)deadline.TotalMilliseconds))));
        }
        else
        {
            prepared = existing;
        }

        if (prepared.State == ProviderStopState.Terminal)
        {
            return new ProviderStopReport(
                "fern.runpod_provider_stop.v1", ProviderStopOutcome.ProviderExited, prepared);
        }

        if (prepared.State == ProviderStopState.NetworkUnknown)
        {
            throw new ProviderLifecycleRefusal(
                "provider stop became network-unknown while resolving its idempotency key; reconcile-runpod-pod-stop must read the stored Pod id");
        }

        if (prepared.State == ProviderStopState.ExpiredWithoutSubmission)
        {
            return new ProviderStopReport(
                "fern.runpod_provider_stop.v1", ProviderStopOutcome.ExpiredWithoutSubmission, prepared);
        }

        var remaining = RemainingProviderDeadline(prepared.DeadlineAtMs);
        if (remaining is null)
        {
            using var writer = RegistryHome.OpenWriter(registryPath);
            var expired = RequireValue(ProviderStopAuthority.ExpireWithoutSubmission(
                writer,
                new ExpireProviderStopWithoutSubmissionWrite(
                    prepared.ProviderStopRequestId,
                    actor,
                    ExpiredWithoutSubmissionDetail)));
            return new ProviderStopReport(
                "fern.runpod_provider_stop.v1", ProviderStopOutcome.ExpiredWithoutSubmission, expired);
        }

        try
        {
            var response = client.StopPod(
                config,
                prepared.ProviderPodId,
                remaining.Value);
            using var writer = RegistryHome.OpenWriter(registryPath);
            var terminal = RequireValue(ProviderStopAuthority.RecordTerminal(
                writer,
                new RecordProviderStopTerminalWrite(prepared.ProviderStopRequestId, actor, response)));
            return new ProviderStopReport("fern.runpod_provider_stop.v1", ProviderStopOutcome.ProviderExited, terminal);
        }
        catch (RunpodProviderException error)
        {
            using var writer = RegistryHome.OpenWriter(registryPath);
            _ = RequireValue(ProviderStopAuthority.MarkNetworkUnknown(
                writer,
                new MarkProviderStopNetworkUnknownWrite(prepared.ProviderStopRequestId, actor, NetworkUnknownDetail)));
            throw new ProviderLifecycleRefusal(
                $"{error.Message}; the request is network_unknown and must be reconciled by its stored Pod id");
        }
    }

    static ProviderStopRequest? LoadExistingStopRequest(
        string registryPath,
        ProviderLifecycleActor actor,
        string idempotencyKey)
    {
        using var reader = RegistryHome.OpenReader(registryPath);
        var loaded = ProviderStopAuthority.LoadForReconcile(
            reader.Connection,
            new ProviderStopLookup(ProviderKind.Runpod, actor, idempotencyKey));
        if (loaded.TryGetValue(out var request))
        {
            return request;
        }

        if (loaded.TryGetRefusal(out var refusal))
        {
            if (string.Equals(refusal.ErrorCategory, "unknown_request", StringComparison.Ordinal))
            {
                return null;
            }

            throw new ProviderLifecycleRefusal(refusal.Detail);
        }

        throw new RegistryFault("provider stop lookup returned neither a request nor a refusal");
    }

    internal static ProviderStopReport Reconcile(
        string registryPath,
        ProviderLifecycleActor actor,
        string idempotencyKey,
        TimeSpan deadline,
        RunpodProviderConfig config,
        RunpodGraphQlClient client)
    {
        ProviderStopRequest request;
        using (var reader = RegistryHome.OpenReader(registryPath))
        {
            request = RequireValue(ProviderStopAuthority.LoadForReconcile(
                reader.Connection,
                new ProviderStopLookup(ProviderKind.Runpod, actor, idempotencyKey)));
        }

        if (request.State == ProviderStopState.Terminal)
        {
            return new ProviderStopReport("fern.runpod_provider_stop.v1", ProviderStopOutcome.ProviderExited, request);
        }

        if (request.State == ProviderStopState.ExpiredWithoutSubmission)
        {
            return new ProviderStopReport(
                "fern.runpod_provider_stop.v1", ProviderStopOutcome.ExpiredWithoutSubmission, request);
        }

        if (request.State != ProviderStopState.NetworkUnknown)
        {
            throw new ProviderLifecycleRefusal(
                "provider stop is prepared, not network-unknown; replay stop-runpod-pod with the same idempotency key to obtain the mutation receipt");
        }

        RunpodPodObservation observation;
        try
        {
            observation = client.ObserveStoredPod(config, request.ProviderPodId, deadline);
        }
        catch (RunpodProviderException error)
        {
            throw new ProviderLifecycleRefusal(
                $"{error.Message}; no terminal receipt was written and the request remains {request.State}");
        }

        if (observation != RunpodPodObservation.Exited)
        {
            return new ProviderStopReport("fern.runpod_provider_stop.v1", ProviderStopOutcome.NotTerminal, request);
        }

        using var writer = RegistryHome.OpenWriter(registryPath);
        var terminal = RequireValue(ProviderStopAuthority.RecordTerminal(
            writer,
            new RecordProviderStopTerminalWrite(
                request.ProviderStopRequestId,
                actor,
                new ProviderTerminalResponse(request.ProviderPodId, ProviderDesiredStatus.Exited))));
        return new ProviderStopReport("fern.runpod_provider_stop.v1", ProviderStopOutcome.ProviderExited, terminal);
    }

    static T RequireValue<T>(RegistryResult<T> result)
    {
        if (result.TryGetValue(out var value))
        {
            return value;
        }

        if (result.TryGetRefusal(out var refusal))
        {
            throw new ProviderLifecycleRefusal(refusal.Detail);
        }

        throw new RegistryFault("provider lifecycle authority returned neither a receipt nor a refusal");
    }

    static TimeSpan ReadDeadline(int seconds)
    {
        if (seconds is < 1 or > 300)
        {
            throw new ProviderLifecycleRefusal("--deadline-seconds must be between 1 and 300");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// The pre-write inventory lookup is read-only. Once Registry writes an
    /// absolute request deadline, retries cannot obtain a fresh submission
    /// window by reusing the same idempotency key.
    /// </summary>
    static TimeSpan? RemainingProviderDeadline(long deadlineAtMs)
    {
        var remainingMs = deadlineAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (remainingMs <= 0)
        {
            return null;
        }

        return TimeSpan.FromMilliseconds(remainingMs);
    }

    static Option<string> RequiredOption(string name, string description) =>
        new(name) { Description = description, Required = true };

    static Option<T> RequiredOption<T>(string name, string description) =>
        new(name) { Description = description, Required = true };

    sealed class CredentialOptions
    {
        readonly Option<string?> runpodConfig;
        readonly Option<string?> apiTokenEnvironment;
        readonly Option<string?> apiUrl;

        public CredentialOptions(
            Option<string?> runpodConfig,
            Option<string?> apiTokenEnvironment,
            Option<string?> apiUrl)
        {
            this.runpodConfig = runpodConfig;
            this.apiTokenEnvironment = apiTokenEnvironment;
            this.apiUrl = apiUrl;
        }

        public RunpodProviderConfig Read(System.CommandLine.ParseResult parsed)
        {
            var configPath = parsed.GetValue(runpodConfig);
            var tokenEnvironment = parsed.GetValue(apiTokenEnvironment);
            var configuredUrl = parsed.GetValue(apiUrl);
            if (!string.IsNullOrWhiteSpace(configPath))
            {
                if (!string.IsNullOrWhiteSpace(tokenEnvironment) || !string.IsNullOrWhiteSpace(configuredUrl))
                {
                    throw new ProviderLifecycleRefusal(
                        "--runpod-config cannot be combined with --api-token-env or --api-url");
                }

                return RunpodProviderConfig.ReadExisting(configPath);
            }

            if (string.IsNullOrWhiteSpace(tokenEnvironment) || string.IsNullOrWhiteSpace(configuredUrl))
            {
                throw new ProviderLifecycleRefusal(
                    "provide --runpod-config, or both --api-token-env and --api-url; no provider credential is stored in Registry");
            }

            return RunpodProviderConfig.ReadExplicitEnvironment(tokenEnvironment, configuredUrl);
        }
    }

    static CredentialOptions AddCredentialOptions(Command command)
    {
        var runpodConfig = new Option<string?>("--runpod-config")
        {
            Description = "Existing RunPod TOML containing apikey and apiurl; its secret is read locally and never persisted",
            HelpName = "PATH",
        };
        var apiTokenEnvironment = new Option<string?>("--api-token-env")
        {
            Description = "Environment variable holding an explicitly injected RunPod API token; requires --api-url",
            HelpName = "NAME",
        };
        var apiUrl = new Option<string?>("--api-url")
        {
            Description = "HTTPS RunPod GraphQL endpoint used with --api-token-env",
            HelpName = "URL",
        };
        command.Options.Add(runpodConfig);
        command.Options.Add(apiTokenEnvironment);
        command.Options.Add(apiUrl);
        return new CredentialOptions(runpodConfig, apiTokenEnvironment, apiUrl);
    }
}

internal enum ProviderStopOutcome
{
    ProviderExited,
    ExpiredWithoutSubmission,
    NotTerminal,
}

internal sealed record ProviderStopReport(
    string Schema,
    ProviderStopOutcome Outcome,
    ProviderStopRequest Request);

/// <summary>A parsed SSH endpoint used only as the provider-inventory lookup key.</summary>
