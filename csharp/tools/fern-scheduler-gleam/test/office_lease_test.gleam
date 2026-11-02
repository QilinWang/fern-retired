//// Slice W1: a Site's office as a lease with states, one test per behaviour
//// the design and its reader named. The cases are the night of 2026-09-25:
//// the AutoDL 4090D's new supervisor exited while the old lease held until
//// 20:15 EDT, and a renewal the Home did not answer cancelled trainings.

import fern_scheduler/office_lease.{
  AskAgainAt, AskForOffice, AskedToStop, Exit, ExpiredUnrenewed, Granted, HeldBy,
  Holding, Lost, NothingRunning, ReleaseOffice, Released, RenewAt, RenewRefused,
  RenewUnanswered, Renewed, ResumeClaiming, Seeking, StopClaiming, SupervisorRef,
  TakenBy, Tick, Unconfirmed,
}
import fern_scheduler/office_lease_wire
import gleam/dynamic/decode
import gleam/json
import gleam/list
import gleam/option.{None, Some}
import gleeunit

pub fn main() -> Nil {
  gleeunit.main()
}

/// The lease every Site supervisor takes today: 600 s, renewed every 200 s.
const lease = 600

const old = SupervisorRef("autodl-container-aa414bbc9b-d1a2cac1", 165_469)

fn exits(actions: List(office_lease.Action)) -> Bool {
  list.any(actions, fn(action) {
    case action {
      Exit(_) -> True
      _ -> False
    }
  })
}

pub fn a_supervisor_that_finds_the_office_held_waits_instead_of_exiting_test() {
  let #(asking, actions) =
    office_lease.update(lease, office_lease.start(1000), Tick(1000))
  assert actions == [AskForOffice]
  let #(waiting, actions) =
    office_lease.update(lease, asking, HeldBy(old, 1001))
  assert waiting == Seeking(Some(old), 1201)
  assert actions == [AskAgainAt(1201)]
  assert !exits(actions)
  assert !office_lease.may_claim(waiting)
}

pub fn it_asks_once_each_cadence_and_takes_the_office_when_granted_test() {
  let waiting = Seeking(Some(old), 1201)
  // Before its time, nothing is asked.
  assert office_lease.update(lease, waiting, Tick(1100)) == #(waiting, [])
  // At its time, once.
  let #(asking, actions) = office_lease.update(lease, waiting, Tick(1201))
  assert actions == [AskForOffice]
  assert office_lease.update(lease, asking, Tick(1202)) == #(asking, [])
  let #(holding, actions) =
    office_lease.update(lease, asking, Granted(epoch: 12, sent_at: 1201))
  assert holding == Holding(12, 1801, False)
  assert actions == [ResumeClaiming, RenewAt(1401)]
  assert office_lease.claim_epoch(holding) == Some(12)
}

pub fn the_lease_is_counted_from_when_the_renewal_was_sent_test() {
  // The renewal sent at 1400 was answered only at 1690, after the client's
  // retries: the lease still counts from 1400, never from the answer.
  let #(holding, actions) =
    office_lease.update(lease, Holding(12, 1801, False), Renewed(sent_at: 1400))
  assert holding == Holding(12, 2000, False)
  assert actions == [RenewAt(1600)]
  // A late answer to an older renewal never shortens the lease.
  let #(still, _) = office_lease.update(lease, holding, Renewed(sent_at: 1300))
  assert still == Holding(12, 2000, False)
}

pub fn a_refused_renewal_loses_the_office_test() {
  let #(lost, actions) =
    office_lease.update(
      lease,
      Holding(12, 2000, False),
      RenewRefused(Some(old)),
    )
  assert lost == Lost(12, TakenBy(Some(old)))
  assert actions
    == [
      StopClaiming,
      Exit("the Registry gave this Site's office to another supervisor"),
    ]
}

pub fn an_unanswered_renewal_only_stops_claiming_test() {
  let #(unconfirmed, actions) =
    office_lease.update(lease, Holding(12, 2000, False), RenewUnanswered(1700))
  assert unconfirmed == Unconfirmed(12, 2000, 1700, False)
  assert actions == [StopClaiming, RenewAt(1900)]
  assert !exits(actions)
  assert office_lease.unconfirmed(unconfirmed)
  assert !office_lease.may_claim(unconfirmed)
  // Still unanswered, still held while the lease lasts: no exit.
  let #(still, actions) =
    office_lease.update(lease, unconfirmed, RenewUnanswered(1900))
  assert still == unconfirmed
  assert !exits(actions)
  assert office_lease.update(lease, still, Tick(1999)) == #(still, [])
  // An answer brings claiming back.
  let #(back, actions) =
    office_lease.update(lease, still, Renewed(sent_at: 1950))
  assert back == Holding(12, 2550, False)
  assert actions == [ResumeClaiming, RenewAt(2150)]
}

pub fn a_lease_that_runs_out_unrenewed_is_lost_test() {
  let #(lost, actions) =
    office_lease.update(lease, Unconfirmed(12, 2000, 1700, False), Tick(2000))
  assert lost == Lost(12, ExpiredUnrenewed)
  assert exits(actions)
}

pub fn asked_to_stop_while_training_it_stops_claiming_and_leaves_after_test() {
  let #(draining, actions) =
    office_lease.update(lease, Holding(12, 2000, False), AskedToStop(1500))
  assert draining == Holding(12, 2000, True)
  assert actions == [StopClaiming]
  assert !office_lease.may_claim(draining)
  // Renewals go on while the training runs.
  let #(renewed, _) =
    office_lease.update(lease, draining, Renewed(sent_at: 1600))
  assert renewed == Holding(12, 2200, True)
  let #(released, actions) =
    office_lease.update(lease, renewed, NothingRunning(1700))
  assert released == Released(12)
  assert actions
    == [
      ReleaseOffice(12),
      Exit("asked to stop, and nothing runs here any more"),
    ]
  // An answer after an unanswered renewal does not bring claiming back while draining.
  let #(_, actions) =
    office_lease.update(
      lease,
      Unconfirmed(12, 2000, 1700, True),
      Renewed(sent_at: 1750),
    )
  assert actions == [RenewAt(1950)]
}

pub fn nothing_revives_a_lost_or_released_office_test() {
  let lost = Lost(12, ExpiredUnrenewed)
  assert office_lease.update(lease, lost, Renewed(sent_at: 3000)) == #(lost, [])
  assert office_lease.update(lease, Released(12), Granted(13, 3000))
    == #(Released(12), [])
  assert office_lease.claim_epoch(lost) == None
}

// ── The `office_lease` ask, as a supervisor sends it ────────────────────

fn ask(state: String, event: String) -> String {
  let line =
    "{\"id\":\"o\",\"ask\":\"office_lease\",\"lease_seconds\":600,\"now\":1001,\"state\":"
    <> state
    <> ",\"event\":"
    <> event
    <> "}"
  assert office_lease_wire.claims(line)
  let assert Ok(said) = office_lease_wire.answer(line)
  said
}

pub fn the_ask_carries_the_state_and_says_what_to_do_test() {
  let said =
    ask(
      "{\"kind\":\"seeking\",\"holder\":null,\"ask_at\":1000}",
      "{\"kind\":\"held_by\",\"holder\":{\"host\":\"autodl-container\",\"pid\":69639},\"now\":1001}",
    )
  assert json.parse(said, decode.at(["answer", "state", "kind"], decode.string))
    == Ok("seeking")
  assert json.parse(
      said,
      decode.at(
        ["answer", "actions"],
        decode.list(decode.at(["kind"], decode.string)),
      ),
    )
    == Ok(["ask_again_at"])
  assert json.parse(said, decode.at(["answer", "may_claim"], decode.bool))
    == Ok(False)
  let unanswered =
    ask(
      "{\"kind\":\"holding\",\"epoch\":12,\"until\":2000,\"draining\":false}",
      "{\"kind\":\"renew_unanswered\",\"now\":1700}",
    )
  assert json.parse(
      unanswered,
      decode.at(["answer", "unconfirmed"], decode.bool),
    )
    == Ok(True)
  assert json.parse(
      unanswered,
      decode.at(
        ["answer", "actions"],
        decode.list(decode.at(["kind"], decode.string)),
      ),
    )
    == Ok(["stop_claiming", "renew_at"])
}

pub fn an_unreadable_ask_is_refused_not_guessed_test() {
  let line =
    "{\"id\":\"o\",\"ask\":\"office_lease\",\"lease_seconds\":600,\"now\":1,\"state\":{\"kind\":\"floating\"},\"event\":{\"kind\":\"tick\",\"now\":1}}"
  let assert Ok(said) = office_lease_wire.answer(line)
  assert json.parse(said, decode.at(["error", "kind"], decode.string))
    == Ok("invalid_request")
}
