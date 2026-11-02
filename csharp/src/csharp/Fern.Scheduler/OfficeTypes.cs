namespace Fern.Scheduler;

public enum LifecycleActorKind
{
    Cli,
    Tui,
    Agent,
    Human,

    /// <summary>
    /// The resident duty officer itself. Go's scheduler writes its receipts
    /// under this kind and the production Registry already holds those rows,
    /// so it is part of the durable vocabulary, not a new one.
    /// </summary>
    Scheduler,
    Supervisor,
    Worker,
    LegacyImport,
}

public sealed record LifecycleActor(
    LifecycleActorKind Kind,
    string ActorId,
    string OwnerHost,
    long OwnerPid)
{
    public string KindWire => Kind switch
    {
        LifecycleActorKind.Cli => "cli",
        LifecycleActorKind.Tui => "tui",
        LifecycleActorKind.Agent => "agent",
        LifecycleActorKind.Human => "human",
        LifecycleActorKind.Scheduler => "scheduler",
        LifecycleActorKind.Supervisor => "supervisor",
        LifecycleActorKind.Worker => "worker",
        LifecycleActorKind.LegacyImport => "legacy-import",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "unknown actor kind"),
    };
}

public sealed class SchedulerPermit
{
    public required string SchedulerLeaseId { get; init; }
    public required long FencingEpoch { get; init; }
    public required string Capability { get; init; }

    public override string ToString() =>
        $"SchedulerPermit {{ SchedulerLeaseId = {SchedulerLeaseId}, FencingEpoch = {FencingEpoch}, Capability = [redacted] }}";
}

public sealed record SchedulerOfficeRefusal(
    string HolderActor,
    string HolderHost,
    string SchedulerLeaseId,
    long NoLongerCurrentEpoch,
    long SuccessorEpoch,
    long ExpiresAtMs)
{
    public string Render() =>
        $"scheduler lease {SchedulerLeaseId} at epoch {NoLongerCurrentEpoch} is no longer current; a successor was elected — stop this scheduler";
}

public abstract record SchedulerElection
{
    public sealed record Won(SchedulerPermit Permit, long ExpiresAtMs) : SchedulerElection;

    public sealed record Refused(SchedulerOfficeRefusal Refusal) : SchedulerElection;

    public sealed record Corrupted(string Detail) : SchedulerElection;
}

public abstract record SchedulerRenewal
{
    public sealed record Renewed(long ExpiresAtMs) : SchedulerRenewal;

    public sealed record Refused(CurrentSchedulerCheck Check) : SchedulerRenewal;
}

public abstract record CurrentSchedulerCheck
{
    public sealed record Current : CurrentSchedulerCheck;

    public sealed record Refused(SchedulerOfficeRefusal Refusal) : CurrentSchedulerCheck;

    public sealed record ForgedPermit(string Detail) : CurrentSchedulerCheck;

    public sealed record Expired(string SchedulerLeaseId, long ExpiredAtMs) : CurrentSchedulerCheck;

    public sealed record NoneCurrent(string Detail) : CurrentSchedulerCheck;
}
