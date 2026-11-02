using System.Text.Json.Serialization;
using Fern.Registry;

namespace Fern.Scheduler;

// The Worker Site vocabulary: what a machine declares about itself when it
// registers, and the answer a caller gets when it asks which machine may run a
// cell. Extracted on 2026-09-11 from StageTypes.cs, whose other half — opening,
// recording and reconciling one Stage of a Stage Plan — retired with the layer.

public static class SitePolicies
{
    public const string WorkerSite = "fern.worker_site.v1";

    /// <summary>
    /// The handlers a Worker Site may be authorized for.
    ///
    /// They are deliberately data, not the result of executing a Python path:
    /// an SSH profile names a Python executable on the Site, which cannot and
    /// must not be spawned on Registry Home. Moved here from the retired stage
    /// worker on 2026-09-11, unchanged, because Site registration is the one
    /// caller that survived it.
    /// </summary>
    public static readonly IReadOnlyList<string> PythonHandlers = Array.AsReadOnly(new[]
    {
        "serie.materialize.cell",
        "fern.train.current",
        "fern.postprocess",
        "oracle.controlled_linear.generate",
        "fern.study.run_cell",
    });
}

public sealed class SiteCapability
{
    public required string Device { get; init; }
    public required string Hostname { get; init; }
    public required bool CudaAvailable { get; init; }
    public string? CudaDeviceName { get; init; }
    public string? CudaComputeCapability { get; init; }
    public long? CudaTotalMemoryBytes { get; init; }
    public required string PythonVersion { get; init; }
    public required string PythonExecutable { get; init; }
    public required string TorchVersion { get; init; }
    public string? WorkerBinarySha256 { get; init; }
    public required string SourceRoot { get; init; }
    public required string ObservedEnvironmentDigest { get; init; }
    public required long ObservedAtMs { get; init; }
    public required string ObservedFingerprintJson { get; init; }

    // CHG 2026-09-11 night (slice 8): the digest helper was the retired SchedulerOffice's.
    static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    public string Digest(string siteId, string profileRevision)
    {
        var worker = WorkerBinarySha256 ?? "";
        return Sha256Hex(System.Text.Encoding.UTF8.GetBytes(string.Join(
            "\u001f",
            "fern.worker_site_capability.v1",
            siteId,
            profileRevision,
            ObservedEnvironmentDigest,
            worker,
            SourceRoot)));
    }
}

public sealed class RegisterSiteRequest
{
    public required string SiteId { get; init; }
    public required string ExecutionProfileId { get; init; }
    public required string ExecutionProfileRevision { get; init; }
    public required string Executor { get; init; }
    public required SiteCapability Capability { get; init; }
    public required long MaxConcurrentStages { get; init; }
    public required long MaxGpuStages { get; init; }
    public required IReadOnlyList<string> AllowedHandlers { get; init; }
    public required bool Enabled { get; init; }
}

public abstract record RegisterSiteOutcome
{
    public sealed record Registered(string SiteId, string CapabilityDigest) : RegisterSiteOutcome;

    public sealed record AlreadyRegistered(string SiteId, string CapabilityDigest) : RegisterSiteOutcome;

    public sealed record Refreshed(string SiteId, string CapabilityDigest, string PreviousCapabilityDigest)
        : RegisterSiteOutcome;

    public sealed record Refused(string Detail) : RegisterSiteOutcome;
}

/// <summary>
/// Why a machine cannot take this cell right now. Every case renders one
/// sentence an operator can act on, which is what <c>site explain-dispatch</c>
/// prints.
/// </summary>
public abstract record SitePlacementRefusal
{
    public abstract string Render();

    public sealed record InvalidRequest(string Detail) : SitePlacementRefusal
    {
        public override string Render() => Detail;
    }

    public sealed record SiteBudget(string PlanId, string SiteId, long InFlight, long MaxConcurrent)
        : SitePlacementRefusal
    {
        public override string Render() =>
            $"Plan {PlanId} execution Site {SiteId} already holds {InFlight} of {MaxConcurrent} Stages";
    }

    public sealed record HandlerNotAuthorized(string PlanId, string SiteId, string Handler) : SitePlacementRefusal
    {
        public override string Render() =>
            $"Plan {PlanId} execution Site {SiteId} is not authorized for Stage handler {Handler}";
    }

    public sealed record LaunchSiteNotRegistered(string PlanId, string SiteId) : SitePlacementRefusal
    {
        public override string Render() =>
            $"Plan {PlanId} execution Site {SiteId} is not registered";
    }

    public sealed record MismatchedFingerprint(string PlanId, string SiteId) : SitePlacementRefusal
    {
        public override string Render() =>
            $"Plan {PlanId} execution Site {SiteId} capability changed; resolve the Site again before opening the Stage";
    }

    public sealed record SiteDisabled(string PlanId, string SiteId, string Reason) : SitePlacementRefusal
    {
        public override string Render() =>
            $"Plan {PlanId} execution Site {SiteId} is disabled: {Reason}";
    }
}

public abstract record ResolveSiteOutcome
{
    public sealed record Resolved(string SiteId, string ExecutionProfileRevision, string CapabilityDigest)
        : ResolveSiteOutcome;

    public sealed record Refused(SitePlacementRefusal Refusal) : ResolveSiteOutcome;
}

public sealed class RegisterSiteRequestWire
{
    [JsonPropertyName("site_id")]
    public required string SiteId { get; init; }

    [JsonPropertyName("execution_profile_id")]
    public required string ExecutionProfileId { get; init; }

    [JsonPropertyName("execution_profile_revision")]
    public required string ExecutionProfileRevision { get; init; }

    [JsonPropertyName("executor")]
    public required string Executor { get; init; }

    [JsonPropertyName("max_concurrent_stages")]
    public required long MaxConcurrentStages { get; init; }

    [JsonPropertyName("max_gpu_stages")]
    public required long MaxGpuStages { get; init; }

    [JsonPropertyName("allowed_handlers")]
    public required IReadOnlyList<string> AllowedHandlers { get; init; }

    [JsonPropertyName("enabled")]
    public required bool Enabled { get; init; }

    [JsonPropertyName("actor")]
    public required ActorWire Actor { get; init; }
}

/// <summary>
/// Who a caller says it is, on the wire. Moved here verbatim from the retired
/// PlanTypes.cs on 2026-09-11: a Site registration request carries it, which is
/// the one reader left now that the Plan vocabulary is gone.
/// </summary>
public sealed class ActorWire
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("owner_host")]
    public required string OwnerHost { get; init; }

    [JsonPropertyName("owner_pid")]
    public required long OwnerPid { get; init; }
}
