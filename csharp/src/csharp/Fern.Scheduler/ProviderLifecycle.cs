using Fern.Registry;

namespace Fern.Scheduler;

/// <summary>
/// The RunPod lifecycle application boundary. It records the immutable Pod
/// identity before mutation and accepts only the matching typed provider
/// terminal receipt.
/// </summary>
public static class ProviderLifecycle
{
    const string NetworkUnknownDetail =
        "RunPod submission did not yield a matching typed Pod EXITED receipt; reconcile the stored Pod id";
    const string ExpiredWithoutSubmissionDetail =
        "the durable provider deadline elapsed before a Pod stop submission started";

    public static ProviderStopReport Stop(
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

    public static ProviderStopReport Reconcile(
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

}

public enum ProviderStopOutcome
{
    ProviderExited,
    ExpiredWithoutSubmission,
    NotTerminal,
}

public sealed record ProviderStopReport(
    string Schema,
    ProviderStopOutcome Outcome,
    ProviderStopRequest Request);
