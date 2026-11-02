//// Slice C6: a stop is told once, with every cell it affects; work that is
//// alive but not moving is told apart from work that is merely long.

import fern_scheduler/cycle.{
  type Action, type Cycle, Claim, ClaimAnswered, Notify, Observed, Refused,
  Serving, Settings, Won,
}
import fern_scheduler/observation.{
  NotAnswering, Observation, Open, Pause, ProgressAt, ProgressNotObserved,
  Request,
}
import fern_scheduler/progress.{
  Blocked, Deadlines, Lifted, NotDispatched, PersonStopped, Repaired, Resumed,
  Stalled, TrainingSilent,
}
import fern_scheduler/reason.{Group, SiteRef}
import gleam/list
import gleam/option.{None, Some}
import gleeunit
import scheduling_fixtures.{cell, ids, now, reading, site, with_history}

pub fn main() -> Nil {
  gleeunit.main()
}

const deadlines = Deadlines(dispatch: 600, training_progress: 1800)

fn serving() -> Cycle {
  cycle.start(Settings(
    serving: Serving(None, [7]),
    races_per_reading: 2,
    refusal_backoff: 300,
    deadlines: deadlines,
  ))
}

fn step(state: Cycle, message: cycle.Message) -> #(Cycle, List(Action)) {
  let assert Ok(next) = cycle.update(state, message)
  next
}

fn notices(actions: List(Action)) -> List(progress.Notice) {
  list.flat_map(actions, fn(action) {
    case action {
      Notify(told, _) -> told
      _ -> []
    }
  })
}

fn five_cells() -> List(observation.Cell) {
  list.map(ids(1, 5), fn(id) { cell(id, 1, id) })
}

pub fn a_stop_is_told_once_and_its_repair_once_test() {
  // CHG 2026-09-26 (installed sources retired;
  // docs/programs/2026-09-26-source-roots-stop-being-full-copies slice 4): the
  // stop is a Site that does not answer, where it was a source being installed.
  let quiet =
    observation.Site(..site(7, "beijing", 5), contact: NotAnswering(now - 120))
  let first = reading(five_cells(), [quiet])
  let #(state, actions) = step(serving(), Observed(first))
  assert notices(actions)
    == [
      Blocked(Group(
        reason.SiteNotAnswering(SiteRef(7, "beijing"), now - 120),
        ids(1, 5),
      )),
    ]
  // A minute later the Site is still silent. Nothing new to tell.
  let second = Observation(..first, at: now + 60)
  let #(state, actions) = step(state, Observed(second))
  assert notices(actions) == []
  // The Site answers again: told once that it no longer stops them, and the
  // first cell is claimed in the same reading.
  let third =
    Observation(..second, at: now + 120, sites: [site(7, "beijing", 5)])
  let #(_, actions) = step(state, Observed(third))
  let assert [
    Notify([Repaired(Group(reason.SiteNotAnswering(_, _), [1, 2, 3, 4, 5]))], _),
    Claim(1, 7, _),
  ] = actions
}

pub fn a_persons_pause_is_told_and_so_is_its_release_test() {
  let pause =
    Request(
      41,
      Pause,
      observation.ForStudy(1),
      "qilin",
      "reading results",
      now - 5,
    )
  let paused =
    Observation(..reading([cell(1, 1, 1)], [site(7, "local", 1)]), requests: [
      pause,
    ])
  let #(state, actions) = step(serving(), Observed(paused))
  assert notices(actions)
    == [PersonStopped(Group(reason.Requested(pause), [1]))]
  let released = Observation(..paused, at: now + 60, requests: [])
  let #(_, actions) = step(state, Observed(released))
  let assert [
    Notify([Lifted(Group(reason.Requested(_), [1]))], _),
    Claim(1, 7, _),
  ] = actions
}

pub fn room_left_unused_past_the_deadline_is_told_with_the_step_test() {
  let observed = reading([cell(1, 1, 1)], [site(7, "local", 1)])
  let #(state, _) = step(serving(), Observed(observed))
  let #(state, _) = step(state, ClaimAnswered(1, 7, Refused("no free device")))
  // Past both the refusal's backoff and the dispatch deadline: the claim is
  // tried again, and the unused room is told once, naming the refusal.
  let later = Observation(..observed, at: now + 600)
  let #(state, actions) = step(state, Observed(later))
  assert notices(actions)
    == [
      Stalled(NotDispatched(
        SiteRef(7, "local"),
        [1],
        now,
        Some("no free device"),
      )),
    ]
  let assert [_, Claim(1, 7, _)] = actions
  let #(state, _) = step(state, ClaimAnswered(1, 7, Won(12, 2)))
  let running =
    Observation(..later, at: now + 660, cells: [
      cell(1, 1, 1)
      |> with_history(Open(12, 7, now + 600, now + 650, ProgressNotObserved)),
    ])
  let #(_, actions) = step(state, Observed(running))
  let assert [Resumed(NotDispatched(_, _, _, _))] = notices(actions)
}

pub fn a_long_training_is_judged_by_its_progress_not_by_its_end_test() {
  let training = fn(cell_id, progress) {
    cell(cell_id, 1, cell_id)
    |> with_history(Open(cell_id * 10, 7, now - 86_400, now - 10, progress))
  }
  let observed =
    reading(
      [
        // A day of training, a step reported two minutes ago: moving.
        training(1, ProgressAt(now - 120)),
        // Renewed ten seconds ago, no step for an hour and a half: not moving.
        training(2, ProgressAt(now - 5400)),
        // Progress this reading cannot see: not judged at all.
        training(3, ProgressNotObserved),
      ],
      [site(7, "local", 3)],
    )
  assert progress.silent_trainings(observed, [1, 2, 3], deadlines)
    == [TrainingSilent(2, 20, SiteRef(7, "local"), now - 5400, now - 10)]
}
