//// Slice C5: every free slot goes to the first cell that may take it, a
//// refusal moves the round on, and every round ends with a named outcome.

import fern_scheduler/cycle.{
  type Action, type Cycle, Claim, ClaimAnswered, ClaimDidNotHappen, Dispatch,
  NoAnswer, Notify, Observed, Refused, Rest, ScopeComplete, Serving, Settings,
  Settle, SettleAnswered, Started, StoppedAsAsked, Taken, WaitingFor, Won,
  WorkDispatched, WorkUnderway,
}
import fern_scheduler/decision.{SiteOffer}
import fern_scheduler/observation.{
  Ended, Failed, Hold, Observation, Open, ProgressNotObserved, Request,
  RetryRequested,
}
import fern_protocol/priority.{Immediate, Normal, Slow}
import fern_scheduler/progress.{Deadlines}
import fern_scheduler/reason.{Group, SiteRef}
import gleam/list
import gleam/option.{None, Some}
import gleeunit
import scheduling_fixtures.{at_priority, cell, now, reading, site, with_history}

pub fn main() -> Nil {
  gleeunit.main()
}

fn serving(site_ids: List(Int)) -> Cycle {
  cycle.start(Settings(
    serving: Serving(None, site_ids),
    races_per_reading: 2,
    refusal_backoff: 300,
    deadlines: Deadlines(dispatch: 600, training_progress: 1800),
  ))
}

fn step(state: Cycle, message: cycle.Message) -> #(Cycle, List(Action)) {
  let assert Ok(next) = cycle.update(state, message)
  next
}

/// The actions without notices, which other tests read.
fn acting(actions: List(Action)) -> List(Action) {
  list.filter(actions, fn(action) {
    case action {
      Notify(_, _) -> False
      _ -> True
    }
  })
}

const deterministic = "unknown dataset 'pointmaze.physical_state'"

pub fn the_next_cell_runs_when_higher_ones_cannot_test() {
  // A: immediate, but Site local does not hold its source and is installing
  // it. B: normal, its configuration is refused. C: slow, and can run.
  // CHG 2026-09-26 (installed sources retired;
  // docs/programs/2026-09-26-source-roots-stop-being-full-copies slice 4): A
  // waits out the retry window of a failure that may go differently next
  // time, which the scheduler also lifts by itself; no Site lacks a source.
  let a =
    cell(1, 1, 1)
    |> at_priority(Immediate)
    |> with_history(Ended(
      1,
      Failed(observation.Transient, 7, now - 60, "CUDA out of memory", None),
    ))
  let b =
    cell(2, 1, 2)
    |> at_priority(Normal)
    |> with_history(Ended(
      1,
      Failed(observation.Deterministic, 7, now - 60, deterministic, None),
    ))
  let c = cell(3, 1, 3) |> at_priority(Slow)
  let observed = reading([a, b, c], [site(7, "local", 1)])
  let #(state, actions) = step(serving([7]), Observed(observed))
  let assert [Claim(3, 7, request)] = acting(actions)
  let #(_, actions) = step(state, ClaimAnswered(3, 7, Won(31, 4)))
  assert actions
    == [
      Dispatch(3, 7, 31, 4),
      Rest(
        WorkDispatched([Started(3, 7, 31, 4)], [
          Group(reason.RetryAfter(now + 540, "CUDA out of memory"), [1]),
          Group(reason.RepeatsUnchanged(deterministic), [2]),
        ]),
      ),
    ]
  assert request == "claim/1/cell/3/site/7/read/1000000"
}

pub fn a_prepared_cell_comes_back_at_its_own_priority_test() {
  // The install has finished; C is training; one slot is free again. A is
  // offered it before another slow cell that has waited longer.
  // CHG 2026-09-26 (installed sources retired;
  // docs/programs/2026-09-26-source-roots-stop-being-full-copies slice 4): no
  // install any more; A's wait is over, whatever it was.
  let a = cell(1, 1, 1) |> at_priority(Immediate)
  let c =
    cell(3, 1, 3)
    |> at_priority(Slow)
    |> with_history(Open(31, 7, now - 100, now - 5, ProgressNotObserved))
  let d = cell(4, 1, 0) |> at_priority(Slow)
  let #(_, actions) =
    step(serving([7]), Observed(reading([a, c, d], [site(7, "local", 2)])))
  assert acting(actions) == [Claim(1, 7, "claim/1/cell/1/site/7/read/1000000")]
}

pub fn a_refused_claim_moves_on_to_the_next_cell_test() {
  let #(state, actions) =
    step(
      serving([7]),
      Observed(reading([cell(1, 1, 1), cell(2, 1, 2)], [site(7, "local", 1)])),
    )
  let assert [Claim(1, 7, _)] = acting(actions)
  let #(_, actions) =
    step(state, ClaimAnswered(1, 7, Refused("study 1 is paused")))
  let assert [Claim(2, 7, _)] = actions
}

pub fn a_refusal_is_not_asked_again_while_its_facts_stand_test() {
  let observed = reading([cell(1, 1, 1)], [site(7, "local", 1)])
  let #(state, _) = step(serving([7]), Observed(observed))
  let #(state, actions) =
    step(state, ClaimAnswered(1, 7, Refused("no free device")))
  let assert [
    Rest(WaitingFor(
      Some(until),
      [Group(reason.ClaimRefused(_, "no free device", _), [1])],
    )),
  ] = actions
  assert until == now + 300
  // The same facts a minute later, and another cell's supervisor renewing
  // its lease: nothing it depends on changed, so nothing is claimed.
  let beating =
    cell(9, 2, 1)
    |> with_history(Open(90, 8, now - 500, now + 55, ProgressNotObserved))
  let again =
    Observation(
      ..observed,
      at: now + 60,
      cells: [cell(1, 1, 1), beating],
      sites: [site(7, "local", 1), site(8, "pod", 1)],
    )
  let #(state, actions) = step(state, Observed(again))
  let assert [
    Rest(WorkUnderway(
      [cycle.Training(9, 90, SiteRef(8, "pod"))],
      [Group(reason.ClaimRefused(_, "no free device", _), [1])],
    )),
  ] = acting(actions)
  // A slot is added to the Site: what the refusal depended on changed.
  let changed =
    Observation(..again, sites: [
      site(7, "local", 2),
      site(8, "pod", 1),
    ])
  let #(_, actions) = step(state, Observed(changed))
  let assert [Claim(1, 7, _)] = acting(actions)
}

pub fn a_refusal_is_asked_again_when_its_time_comes_test() {
  let observed = reading([cell(1, 1, 1)], [site(7, "local", 1)])
  let #(state, _) = step(serving([7]), Observed(observed))
  let #(state, _) = step(state, ClaimAnswered(1, 7, Refused("no free device")))
  let #(_, actions) =
    step(state, Observed(Observation(..observed, at: now + 300)))
  let assert [Claim(1, 7, _)] = acting(actions)
}

pub fn claims_lost_to_other_supervisors_end_the_round_after_the_bound_test() {
  let observed =
    reading([cell(1, 1, 1), cell(2, 1, 2), cell(3, 1, 3)], [
      site(7, "local", 3),
    ])
  let #(state, _) = step(serving([7]), Observed(observed))
  let #(state, actions) = step(state, ClaimAnswered(1, 7, Taken))
  let assert [Claim(2, 7, _)] = actions
  let #(_, actions) = step(state, ClaimAnswered(2, 7, Taken))
  // Read again now, rather than chase cells others are taking.
  let assert [Rest(WaitingFor(Some(moment), _))] = actions
  assert moment == now
}

pub fn a_lost_answer_holds_its_slot_and_other_slots_go_on_test() {
  let observed =
    reading([cell(1, 1, 1), cell(2, 1, 2), cell(3, 1, 3)], [
      site(7, "local", 2),
    ])
  let #(state, _) = step(serving([7]), Observed(observed))
  let #(state, actions) = step(state, ClaimAnswered(1, 7, NoAnswer))
  let assert [Settle(1, 7, request), Claim(2, 7, _)] = actions
  let #(state, actions) = step(state, ClaimAnswered(2, 7, Won(21, 1)))
  // Two slots: one won, one held by the claim nobody answered. Cell 3 waits.
  let assert [
    Dispatch(2, 7, 21, 1),
    Rest(WorkDispatched([Started(2, 7, 21, 1)], waiting)),
  ] = actions
  assert list.contains(
    waiting,
    Group(reason.ClaimUnsettled(SiteRef(7, "local")), [1]),
  )
  assert list.contains(
    waiting,
    Group(reason.SiteFull(SiteRef(7, "local")), [3]),
  )
  // The Registry says the lost claim never happened: its slot goes to cell 3,
  // and cell 1 is not sent a second claim in the same reading.
  let #(_, actions) = step(state, SettleAnswered(1, 7, ClaimDidNotHappen))
  let assert [Claim(3, 7, _)] = actions
  assert request == "claim/1/cell/1/site/7/read/1000000"
}

pub fn a_cell_with_an_unsettled_claim_is_not_shown_claimable_test() {
  let observed =
    reading([cell(1, 1, 1)], [
      site(7, "local", 2),
      site(8, "pod", 1),
    ])
  let #(state, _) = step(serving([7, 8]), Observed(observed))
  let #(state, _) = step(state, ClaimAnswered(1, 7, NoAnswer))
  let assert [status] = cycle.statuses(state)
  assert status.status == reason.Waiting
  assert status.reasons == [reason.ClaimUnsettled(SiteRef(7, "local"))]
}

pub fn a_settled_claim_is_dispatched_once_test() {
  let observed = reading([cell(1, 1, 1)], [site(7, "local", 1)])
  let #(state, _) = step(serving([7]), Observed(observed))
  let #(state, actions) = step(state, ClaimAnswered(1, 7, NoAnswer))
  let assert [
    Settle(1, 7, _),
    Rest(WorkUnderway([cycle.SettlingClaim(1, 7)], _)),
  ] = actions
  let #(state, actions) =
    step(state, SettleAnswered(1, 7, cycle.ClaimHappened(12, 3)))
  let assert [Dispatch(1, 7, 12, 3), Rest(WorkDispatched(_, _))] = actions
  assert cycle.update(state, SettleAnswered(1, 7, ClaimDidNotHappen))
    == Error("no claim of cell 1 on Site 7 is waiting to be settled")
}

pub fn an_answer_to_a_claim_never_sent_is_refused_test() {
  let #(state, _) =
    step(
      serving([7]),
      Observed(reading([cell(1, 1, 1)], [site(7, "local", 1)])),
    )
  assert cycle.update(state, ClaimAnswered(2, 7, Won(5, 1)))
    == Error("no claim of cell 2 on Site 7 is waiting for an answer")
}

pub fn every_refused_cell_is_still_named_when_nothing_can_run_test() {
  let held =
    Request(40, Hold, observation.ForCell(3), "qilin", "checking", now - 60)
  let observed =
    Observation(
      ..reading(
        [
          cell(1, 1, 1)
            |> with_history(Ended(
              1,
              Failed(
                observation.Deterministic,
                7,
                now - 60,
                deterministic,
                None,
              ),
            )),
          // CHG 2026-09-26 (installed sources retired;
          // docs/programs/2026-09-26-source-roots-stop-being-full-copies
          // slice 4): cell 2 failed in a way that belongs to Site local,
          // where Site local once lacked its source.
          cell(2, 1, 2)
            |> with_history(Ended(
              1,
              Failed(observation.SiteSpecific, 7, now - 60, "no CUDA", None),
            )),
          cell(3, 1, 3),
        ],
        [site(7, "local", 1)],
      ),
      requests: [held],
    )
  let #(_, actions) = step(serving([7]), Observed(observed))
  let assert [Rest(WaitingFor(None, waiting))] = acting(actions)
  assert waiting
    == [
      Group(reason.RepeatsUnchanged(deterministic), [1]),
      Group(reason.FailedOnSite(SiteRef(7, "local"), "no CUDA"), [2]),
      Group(reason.Requested(held), [3]),
    ]
}

pub fn a_study_whose_cells_are_all_held_ends_as_asked_test() {
  let held = Request(40, Hold, observation.ForStudy(1), "qilin", "stop", now)
  let observed =
    Observation(
      ..reading([cell(1, 1, 1), cell(2, 1, 2)], [site(7, "local", 1)]),
      requests: [held],
    )
  let #(_, actions) = step(serving([7]), Observed(observed))
  let assert [Rest(StoppedAsAsked([Group(reason.Requested(_), [1, 2])]))] =
    acting(actions)
}

pub fn a_finished_scope_says_so_and_only_then_test() {
  let accepted = cell(1, 1, 1) |> with_history(observation.Accepted)
  let #(_, actions) =
    step(serving([7]), Observed(reading([accepted], [site(7, "local", 1)])))
  assert acting(actions) == [Rest(ScopeComplete)]
  let spent =
    cell(2, 1, 2)
    |> with_history(Ended(
      3,
      Failed(observation.Transient, 7, now - 9000, "x", None),
    ))
  let #(_, actions) =
    step(
      serving([7]),
      Observed(reading([accepted, spent], [site(7, "local", 1)])),
    )
  let assert [Rest(WaitingFor(None, [Group(reason.AttemptsSpent(3, 3), [2])]))] =
    acting(actions)
}

pub fn an_unreadable_registry_is_never_a_finished_scope_test() {
  let unread =
    Observation(
      ..reading([], [site(7, "local", 1)]),
      authority: observation.RegistryUnreachable("connection refused"),
    )
  let #(_, actions) = step(serving([7]), Observed(unread))
  assert acting(actions)
    == [
      Rest(
        WaitingFor(None, [
          Group(reason.CannotReadRegistry("connection refused"), []),
        ]),
      ),
    ]
}

pub fn a_cell_only_another_site_may_take_is_named_not_claimed_test() {
  // CHG 2026-09-26 (installed sources retired;
  // docs/programs/2026-09-26-source-roots-stop-being-full-copies slice 4):
  // Site local cannot take the cell because it is a CPU machine and the cell
  // requires CUDA, where it once lacked the cell's source.
  let local = site(7, "local", 1)
  let cpu =
    observation.Site(..local, offer: SiteOffer(..local.offer, device: "cpu"))
  let #(_, actions) =
    step(
      serving([7]),
      Observed(reading([cell(1, 1, 1)], [cpu, site(8, "pod", 1)])),
    )
  assert acting(actions)
    == [
      Rest(
        WaitingFor(None, [
          Group(reason.OnOtherSites([SiteRef(8, "pod")]), [1]),
        ]),
      ),
    ]
}

pub fn a_one_study_supervisor_does_not_claim_for_another_study_test() {
  let one_study =
    cycle.start(Settings(
      serving: Serving(Some(2), [7]),
      races_per_reading: 2,
      refusal_backoff: 300,
      deadlines: Deadlines(dispatch: 600, training_progress: 1800),
    ))
  let #(_, actions) =
    step(
      one_study,
      Observed(reading([cell(1, 1, 1), cell(5, 2, 1)], [site(7, "local", 1)])),
    )
  let assert [Claim(5, 7, _)] = acting(actions)
}

pub fn a_retry_request_puts_a_spent_cell_back_in_the_round_test() {
  let retried =
    cell(1, 1, 1)
    |> with_history(Ended(3, RetryRequested("qilin", "fixed the card", now - 5)))
  let #(_, actions) =
    step(serving([7]), Observed(reading([retried], [site(7, "local", 1)])))
  let assert [Claim(1, 7, _)] = acting(actions)
}
