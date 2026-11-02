using System.Diagnostics;
using System.Text.Json.Nodes;
using Fern.Registry;

namespace Fern.Scheduler;

/// <summary>
/// A Site's office kept through the Gleam <c>OfficeLease</c> state machine
/// (<c>tools/fern-scheduler-gleam/src/fern_scheduler/office_lease.gleam</c>),
/// slice W1 of docs/programs/2026-09-22-gleam-site-supervision/2026-09-26-leases-and-watching-a-running-cell.
///
/// The Gleam side decides; this class sends it one event at a time, keeps the
/// state it answers with, and hands back the actions for the supervisor loop
/// to carry out. Three things are told apart that the loop used to treat as
/// one: a renewal the Registry refused (the office is lost), a renewal nobody
/// answered (the office is held until its lease runs out; only claiming stops),
/// and an office another supervisor holds (wait and ask again, do not exit).
///
/// Every time is this process's own monotonic clock in whole seconds, counted
/// from when this object was made; a lease is counted from when the renewal
/// that succeeded was sent, so it is never longer than the Home's.
/// </summary>
public sealed class GleamOfficeLease(GleamProgram program, long leaseSeconds)
{
    readonly Stopwatch _clock = Stopwatch.StartNew();
    // The loop renews at its top and, while a cell runs, from a second task.
    readonly Lock _gate = new();
    JsonNode _state = new JsonObject { ["kind"] = "seeking", ["holder"] = null, ["ask_at"] = 0 };

    /// <summary>Seconds on this process's monotonic clock.</summary>
    public long Now => (long)_clock.Elapsed.TotalSeconds;

    public bool MayClaim { get; private set; }
    public bool Unconfirmed { get; private set; }
    public long? ClaimEpoch { get; private set; }

    /// <summary>The Gleam side's sentence for the current state, for the supervisor's log.</summary>
    public string Sentence { get; private set; } = "without the office";

    /// <summary>What the Gleam side asked the loop to do after the last event.</summary>
    public sealed record Actions(IReadOnlyList<JsonObject> All)
    {
        public string? Exit => All.FirstOrDefault(action => Kind(action) == "exit")?["why"]?.GetValue<string>();

        /// <summary>When to ask for the office again, when the office is held by another.</summary>
        public long? AskAgainAt => All.FirstOrDefault(action => Kind(action) == "ask_again_at")?["at"]?.GetValue<long>();

        static string? Kind(JsonObject action) => action["kind"]?.GetValue<string>();
    }

    public Actions Granted(long epoch, long sentAt) =>
        Tell(new JsonObject { ["kind"] = "granted", ["epoch"] = epoch, ["sent_at"] = sentAt });

    public Actions HeldBy(string host, long pid) =>
        Tell(new JsonObject
        {
            ["kind"] = "held_by",
            ["holder"] = new JsonObject { ["host"] = host, ["pid"] = pid },
            ["now"] = Now,
        });

    public Actions Renewed(long sentAt) => Tell(new JsonObject { ["kind"] = "renewed", ["sent_at"] = sentAt });

    public Actions RenewRefused() => Tell(new JsonObject { ["kind"] = "renew_refused", ["holder"] = null });

    public Actions RenewUnanswered() => Tell(new JsonObject { ["kind"] = "renew_unanswered", ["now"] = Now });

    public Actions Tick() => Tell(new JsonObject { ["kind"] = "tick", ["now"] = Now });

    /// <summary>
    /// Renew the office through <paramref name="store"/> and tell the Gleam
    /// side how it went: renewed, refused, or unanswered (the Home did not
    /// answer within the client's retries). The one place the loop renews.
    /// </summary>
    public Actions Renew(IStudyStore store, RegistryOperations.SupervisorPermit office)
    {
        var sentAt = Now;
        try
        {
            return store.RenewSupervisor(office, leaseSeconds).IsOk ? Renewed(sentAt) : RenewRefused();
        }
        catch (HomeUnreachableException)
        {
            return RenewUnanswered();
        }
    }

    Actions Tell(JsonObject @event)
    {
        lock (_gate) return TellLocked(@event);
    }

    Actions TellLocked(JsonObject @event)
    {
        var reply = program.Ask(new JsonObject
        {
            ["ask"] = "office_lease",
            ["lease_seconds"] = leaseSeconds,
            ["now"] = Now,
            ["state"] = _state.DeepClone(),
            ["event"] = @event,
        }, TimeSpan.FromSeconds(30), null);
        if (!reply.IsOk)
        {
            throw new GleamProgramException("the Gleam office lease did not answer: " + reply.RefusalDetail);
        }
        var answer = reply.Value["answer"] as JsonObject
            ?? throw new GleamProgramException("the Gleam office lease answered with no answer: " + reply.Value.ToJsonString());
        _state = answer["state"]!.DeepClone();
        MayClaim = answer["may_claim"]!.GetValue<bool>();
        Unconfirmed = answer["unconfirmed"]!.GetValue<bool>();
        ClaimEpoch = answer["claim_epoch"] is JsonValue epoch ? epoch.GetValue<long>() : null;
        Sentence = answer["sentence"]!.GetValue<string>();
        return new Actions((answer["actions"] as JsonArray ?? [])
            .Select(action => action as JsonObject ?? throw new GleamProgramException("an office lease action was not an object"))
            .ToList());
    }
}
