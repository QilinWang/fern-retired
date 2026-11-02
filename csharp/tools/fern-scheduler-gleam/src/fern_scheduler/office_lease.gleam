//// Slice W1 of docs/programs/2026-09-22-gleam-site-supervision/2026-09-26-leases-and-watching-a-running-cell:
//// a Site's supervisor and its office, as a lease with states.
////
//// The office is the right to claim new cells on one Site. The Registry grants
//// it for `lease_seconds` and the supervisor renews it every third of that.
//// Three things are kept apart that the C# loop keeps together:
////
//// - a renewal the Registry REFUSED (another supervisor holds the office now):
////   the office is lost;
//// - a renewal NOBODY ANSWERED (the Home, or the way to it, is silent): the
////   office is still held until its lease runs out; the supervisor only stops
////   claiming new cells;
//// - a supervisor that finds the office held by another: it waits and asks
////   again, instead of exiting to be restarted by systemd.
////
//// Every time here is this Site's own monotonic clock, in whole seconds. A
//// lease is counted from when the renewal that succeeded was SENT: the Home
//// fixes its expiry when it handles the request, which is later, so the lease
//// this Site believes it holds is never longer than the one the Home granted
//// (Gray and Cheriton, "Leases", SOSP 1989). The Home's own expiry time is
//// never compared with this clock; the two machines' clocks may differ.
////
//// What this does not do: when the office is lost the supervisor still exits,
//// and today its exit still takes the training it started. Keeping a training
//// alive past its supervisor is slices W3 and W4.

import gleam/int
import gleam/option.{type Option, None, Some}

/// A supervisor as the Registry names it.
pub type SupervisorRef {
  SupervisorRef(host: String, pid: Int)
}

pub type LostOffice {
  /// The Registry refused a renewal: this supervisor's epoch is no longer the
  /// office's.
  TakenBy(holder: Option(SupervisorRef))
  /// No renewal succeeded before the lease ran out on this Site's clock.
  ExpiredUnrenewed
}

pub type OfficeLease {
  /// Without the office. `holder` is who the Registry last said holds it;
  /// `ask_at` is when to ask for it again.
  Seeking(holder: Option(SupervisorRef), ask_at: Int)
  /// Held until `until` on this Site's clock. `draining`: a person asked this
  /// supervisor to stop, so it claims nothing new and leaves once nothing runs.
  Holding(epoch: Int, until: Int, draining: Bool)
  /// Held until `until`, but the last renewal was not answered, since `since`.
  Unconfirmed(epoch: Int, until: Int, since: Int, draining: Bool)
  Lost(epoch: Int, why: LostOffice)
  Released(epoch: Int)
}

pub type Event {
  Tick(now: Int)
  /// A request for the office, sent at `sent_at`, was granted.
  Granted(epoch: Int, sent_at: Int)
  /// A request for the office found it held by another supervisor.
  HeldBy(holder: SupervisorRef, now: Int)
  /// A renewal sent at `sent_at` succeeded.
  Renewed(sent_at: Int)
  /// A renewal was refused: the office is another supervisor's now.
  RenewRefused(holder: Option(SupervisorRef))
  /// A renewal got no answer (the Home, or the way to it, is silent).
  RenewUnanswered(now: Int)
  /// A person's `stop-supervisor` request reached this supervisor.
  AskedToStop(now: Int)
  /// Nothing this supervisor started is running any more.
  NothingRunning(now: Int)
}

pub type Action {
  AskForOffice
  /// Send a renewal at this moment.
  RenewAt(at: Int)
  /// Ask for the office again at this moment.
  AskAgainAt(at: Int)
  StopClaiming
  ResumeClaiming
  ReleaseOffice(epoch: Int)
  Exit(why: String)
}

/// Renewals and asks come every third of the lease, as the C# loop renews today.
pub fn cadence(lease_seconds: Int) -> Int {
  int.max(1, lease_seconds / 3)
}

/// The state a supervisor starts in: without the office, asking now.
pub fn start(now: Int) -> OfficeLease {
  Seeking(None, now)
}

/// One event in; the next state and what to do.
pub fn update(
  lease_seconds: Int,
  state: OfficeLease,
  event: Event,
) -> #(OfficeLease, List(Action)) {
  let every = cadence(lease_seconds)
  case state, event {
    // ── without the office ─────────────────────────────────────────────
    Seeking(holder, ask_at), Tick(now) if now >= ask_at -> #(
      Seeking(holder, now + every),
      [AskForOffice],
    )
    Seeking(_, _), Granted(epoch, sent_at) -> #(
      Holding(epoch, sent_at + lease_seconds, False),
      [ResumeClaiming, RenewAt(sent_at + every)],
    )
    // Held by another: wait and ask again, instead of exiting.
    Seeking(_, _), HeldBy(holder, now) -> #(Seeking(Some(holder), now + every), [
      AskAgainAt(now + every),
    ])
    Seeking(_, _), AskedToStop(_) -> #(Released(0), [
      Exit("asked to stop before this supervisor held the office"),
    ])

    // ── holding it ─────────────────────────────────────────────────────
    Holding(epoch, until, draining), Renewed(sent_at) -> #(
      Holding(epoch, int.max(until, sent_at + lease_seconds), draining),
      [RenewAt(sent_at + every)],
    )
    Holding(epoch, until, draining), RenewUnanswered(now) -> #(
      Unconfirmed(epoch, until, now, draining),
      [StopClaiming, RenewAt(now + every)],
    )
    Holding(epoch, _, _), RenewRefused(holder)
    | Unconfirmed(epoch, _, _, _), RenewRefused(holder)
    -> #(Lost(epoch, TakenBy(holder)), [
      StopClaiming,
      Exit("the Registry gave this Site's office to another supervisor"),
    ])
    Holding(epoch, until, _), Tick(now)
    | Unconfirmed(epoch, until, _, _), Tick(now)
      if now >= until
    -> #(Lost(epoch, ExpiredUnrenewed), [
      StopClaiming,
      Exit(
        "no renewal of this Site's office succeeded before its lease ran out",
      ),
    ])
    Holding(epoch, until, False), AskedToStop(_) -> #(
      Holding(epoch, until, True),
      [StopClaiming],
    )
    Holding(epoch, _, True), NothingRunning(_) -> #(Released(epoch), [
      ReleaseOffice(epoch),
      Exit("asked to stop, and nothing runs here any more"),
    ])

    // ── held, the last renewal unanswered ──────────────────────────────
    Unconfirmed(epoch, until, _, draining), Renewed(sent_at) -> #(
      Holding(epoch, int.max(until, sent_at + lease_seconds), draining),
      case draining {
        True -> [RenewAt(sent_at + every)]
        False -> [ResumeClaiming, RenewAt(sent_at + every)]
      },
    )
    Unconfirmed(epoch, until, since, draining), RenewUnanswered(now) -> #(
      Unconfirmed(epoch, until, since, draining),
      [RenewAt(now + every)],
    )
    Unconfirmed(epoch, until, since, False), AskedToStop(_) -> #(
      Unconfirmed(epoch, until, since, True),
      [],
    )
    Unconfirmed(epoch, _, _, True), NothingRunning(_) -> #(Released(epoch), [
      ReleaseOffice(epoch),
      Exit("asked to stop, and nothing runs here any more"),
    ])

    // Everything else changes nothing: a lost or released office stays so.
    _, _ -> #(state, [])
  }
}

/// Whether this supervisor may claim a new cell now.
pub fn may_claim(state: OfficeLease) -> Bool {
  case state {
    Holding(_, _, False) -> True
    _ -> False
  }
}

/// The epoch a claim carries, when this supervisor may claim.
pub fn claim_epoch(state: OfficeLease) -> Option(Int) {
  case state {
    Holding(epoch, _, False) -> Some(epoch)
    _ -> None
  }
}

/// Whether the scheduling round should read its office as unconfirmed
/// (`observation.OfficeUnconfirmed`, which stops every new claim).
pub fn unconfirmed(state: OfficeLease) -> Bool {
  case state {
    Unconfirmed(_, _, _, _) -> True
    _ -> False
  }
}

/// One sentence for the supervisor's log.
pub fn describe(state: OfficeLease, now: Int) -> String {
  case state {
    Seeking(None, ask_at) ->
      "without the office; asking in " <> seconds(ask_at - now)
    Seeking(Some(holder), ask_at) ->
      "the office is held by "
      <> holder.host
      <> "/"
      <> int.to_string(holder.pid)
      <> "; asking again in "
      <> seconds(ask_at - now)
    Holding(epoch, until, draining) ->
      "holding the office at epoch "
      <> int.to_string(epoch)
      <> " for "
      <> seconds(until - now)
      <> case draining {
        True -> "; asked to stop, claiming nothing new"
        False -> ""
      }
    Unconfirmed(epoch, until, since, _) ->
      "holding the office at epoch "
      <> int.to_string(epoch)
      <> " for "
      <> seconds(until - now)
      <> ", but no renewal has been answered for "
      <> seconds(now - since)
      <> "; claiming nothing new, training continues"
    Lost(epoch, TakenBy(_)) ->
      "lost the office at epoch "
      <> int.to_string(epoch)
      <> ": another supervisor holds it"
    Lost(epoch, ExpiredUnrenewed) ->
      "lost the office at epoch "
      <> int.to_string(epoch)
      <> ": its lease ran out unrenewed"
    Released(epoch) -> "released the office at epoch " <> int.to_string(epoch)
  }
}

fn seconds(count: Int) -> String {
  int.to_string(int.max(0, count)) <> " s"
}
