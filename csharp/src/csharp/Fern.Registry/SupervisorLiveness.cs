namespace Fern.Registry;

/// <summary>
/// Whether the process that holds a Site's office still exists.
///
/// A Site has one office, and only one supervisor may hold it, because two
/// supervisors on one GPU is the failure the office exists to prevent. The
/// holder keeps it by renewing a lease, and when it stops renewing the office
/// frees after the lease runs out — about ten minutes. That is the right answer
/// when the holder cannot be seen.
///
/// Four times on the night of 2026-09-25 into 2026-09-26 the holder was a
/// process that no longer existed and everyone waited anyway; the fourth time
/// it blocked the deploy store's cleanup while nothing was training at all.
/// On the machine that hosts the Site this is not an inference. The process
/// either exists or it does not, and the answer is immediate.
///
/// So a lease is the fallback for a holder you cannot see, not the only way to
/// know. This type is the decision; the observation is an argument, so it is
/// testable without a clock, a machine or a process.
///
/// Design: docs/programs/2026-09-22-gleam-site-supervision/2026-09-26-a-dead-office-is-released-by-looking.
/// </summary>
public static class SupervisorLiveness
{
    /// <summary>What can be said about an office's holder.</summary>
    public enum Standing
    {
        /// <summary>The holder's process is there. Never displaced, however old its lease.</summary>
        Held,

        /// <summary>
        /// Looked, on the holder's own host, and the process is not there.
        /// The office may be taken at once, and what was seen is recorded.
        /// </summary>
        HolderGone,

        /// <summary>
        /// The holder is on another host, so nothing local proves anything.
        /// A machine we cannot reach may be training perfectly well — three of
        /// them were, all night. The lease governs.
        /// </summary>
        CannotTellFromHere,

        /// <summary>
        /// Not seen, and the lease has run out. What the system has always
        /// done, kept because it is still the answer for an unreachable host.
        /// </summary>
        LeaseExpired,
    }

    /// <summary>
    /// What one reader can observe about a process on its own machine.
    ///
    /// <paramref name="Exists"/> is whether <c>/proc/&lt;pid&gt;</c> is there.
    /// <paramref name="StartedAt"/> is when that process began, which is what
    /// tells a recycled process id apart from the original: a process that
    /// began after the office row was written cannot be the holder, whatever
    /// its id says.
    /// </summary>
    public sealed record ProcessSighting(bool Exists, DateTimeOffset? StartedAt);

    /// <summary>
    /// The office as the Registry records it.
    ///
    /// CHG 2026-09-26, after an audit: <c>Site</c> and <c>Ended</c> were missing.
    /// Without the Site a refusal could not say which machine was in the way,
    /// which was the whole complaint. Without <c>Ended</c> this type modelled
    /// only half of what the Registry means by holding office — its own reader
    /// requires the row to be not ended AND its lease to stand — so a closed
    /// office whose process was still alive read as Held, and said "which is
    /// running" about a supervisor that had already finished.
    /// </summary>
    public sealed record Office(
        string Site, string Host, long Pid, DateTimeOffset StartedAt, DateTimeOffset LeaseUntil, bool Ended);

    /// <summary>
    /// Where the reader is, and what it saw. <paramref name="Sighting"/> is
    /// null when the reader is not on the office's host and therefore looked at
    /// nothing — which is a different thing from looking and seeing nothing.
    /// </summary>
    public sealed record Look(string Host, ProcessSighting? Sighting, DateTimeOffset At);

    /// <summary>
    /// The standing of an office, given one look at it.
    ///
    /// Example: an office held by pid 1366929 on MSI, looked at from MSI, where
    /// <c>/proc/1366929</c> is gone, is <see cref="Standing.HolderGone"/> — the
    /// answer that took ten minutes of waiting on 2026-09-26.
    /// </summary>
    public static Standing Of(Office office, Look look)
    {
        // A row that ended is not an office at all, whatever its process is
        // doing: the Registry's own reader requires not-ended and a standing
        // lease together.
        if (office.Ended)
        {
            return Standing.HolderGone;
        }
        if (!string.Equals(office.Host, look.Host, StringComparison.Ordinal) || look.Sighting is null)
        {
            return look.At >= office.LeaseUntil ? Standing.LeaseExpired : Standing.CannotTellFromHere;
        }

        var seen = look.Sighting;
        if (!seen.Exists)
        {
            return Standing.HolderGone;
        }

        // The id is there, but an id is reused. A process that began after the
        // office was recorded is a different process wearing the same number,
        // and treating it as the holder would leave a dead office standing
        // forever behind a live stranger.
        if (seen.StartedAt is { } began && began > office.StartedAt)
        {
            return Standing.HolderGone;
        }

        // It exists and it did not begin after the office. A live holder is
        // never displaced, however far past its lease it is: it may be training.
        return Standing.Held;
    }

    /// <summary>
    /// Whether this standing lets a new supervisor take the office now.
    ///
    /// Only a look that saw the holder gone does. An expired lease is the
    /// separate, slower path the Registry already owns, and it is deliberately
    /// not folded in here: this answers "may I take it because I looked".
    /// </summary>
    public static bool MayBeTakenOnThisLook(Standing standing) => standing is Standing.HolderGone;

    /// <summary>
    /// What a refusal should say, so an operator is not left to assemble it
    /// from three other commands. On 2026-09-26 the cleanup said only that one
    /// Site office was open, and every fact needed to act — that it was on this
    /// machine, that its process had already exited, that the wait was for
    /// nothing — had to be found by hand.
    /// </summary>
    public static string Sentence(Office office, Standing standing)
    {
        var site = office.Site;
        return standing switch
        {
            Standing.Held =>
            $"Site {site} is supervised by {office.Host} pid {office.Pid}, which is running; "
                + $"its lease runs to {office.LeaseUntil:u}",
            Standing.HolderGone =>
            $"Site {site} records an office held by {office.Host} pid {office.Pid}, and that process "
                + "is gone on this machine; the office may be taken now",
            Standing.CannotTellFromHere =>
            $"Site {site} is supervised by {office.Host} pid {office.Pid}, which this machine cannot see; "
                + $"its lease runs to {office.LeaseUntil:u} and that machine may still be training",
            Standing.LeaseExpired =>
            $"Site {site} records an office held by {office.Host} pid {office.Pid} whose lease ran out at "
                + $"{office.LeaseUntil:u}; this machine cannot see that host",
            _ => $"Site {site} has an office of unknown standing",
        };
    }
}
