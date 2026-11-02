//// One supervisor's rounds: offer each free slot to the first cell in queue
//// order that may take it, move on past every refusal, and end each round
//// with an answer a person can read. Slice C5 of the scheduling-loop design.
////
//// The model says what to do and the caller does it: the caller sends a
//// reading, the model answers with a claim; the caller sends the Registry's
//// answer to that claim, the model answers with a dispatch or the next claim;
//// and so on until the round rests. Only the Registry's own claim decides
//// whether a cell was won, and nothing here pretends otherwise.
////
//// What it guarantees:
////
//// - A refused cell does not hold up the queue. The next cell is offered the
////   same slot, and the refused one keeps its reason, its priority and its
////   place: it is skipped, not dropped, and not counted as done.
//// - A refusal is not asked again while nothing it depends on has changed.
////   A supervisor renewing its lease is not a change; a Site gaining the
////   source, a slot freeing, a person's retry are.
//// - Losing a claim to another supervisor is ordinary. After a bounded number
////   of those in one reading the round rests and asks for a fresh reading,
////   instead of chasing cells that others are taking.
//// - A claim that got no answer holds its slot until the Registry says
////   whether it happened; it is not sent again, and the slot is not handed to
////   another cell to "move on". Other slots continue.
//// - A round ends in one of five ways: work was dispatched; work is under way
////   (a training, an install, a claim being settled); waiting, for a named
////   event or the earliest named moment; everything in scope is finished; or
////   everything left is stopped by a person's word. Work that remains is
////   never reported as finished.
////
//// CHG 2026-09-26 (installed sources retired;
//// docs/programs/2026-09-26-source-roots-stop-being-full-copies slice 4): no
//// Site holds or gains a source any more, so neither is a fact a refusal
//// depends on, and no install is ever work under way.

import fern_scheduler/observation.{
  type Cell, type Observation, type Request, type Site, type Study, Open,
}
import fern_scheduler/progress.{type Deadlines, type NoProgress, type Notice}
import fern_scheduler/reason.{
  type CellStatus, type Group, type Reason, type SiteRef, CellStatus,
}
import gleam/dict.{type Dict}
import gleam/int
import gleam/list
import gleam/option.{type Option, None, Some}
import gleam/result

/// What this supervisor may do: claim on these Sites, for this Study or for
/// every Study in state run. It never widens that by itself.
pub type Serving {
  Serving(study_id: Option(Int), site_ids: List(Int))
}

/// `races_per_reading` is how many claims may be lost to another supervisor
/// in one reading before the round rests and asks for a fresh one.
/// `refusal_backoff` is how long, in seconds, a refused claim waits before it
/// is proposed again when nothing it depends on has changed.
pub type Settings {
  Settings(
    serving: Serving,
    races_per_reading: Int,
    refusal_backoff: Int,
    deadlines: Deadlines,
  )
}

/// The Registry's answer to one claim.
pub type ClaimAnswer {
  /// The claim opened this attempt, with this fencing epoch.
  Won(attempt_id: Int, epoch: Int)
  /// Another supervisor claimed the cell first. Not a failure of the cell.
  Taken
  /// The Registry refused, and said why.
  Refused(detail: String)
  /// No answer arrived: the claim may or may not have happened.
  NoAnswer
}

/// What the Registry says, afterwards, about a claim that got no answer.
pub type Settled {
  ClaimHappened(attempt_id: Int, epoch: Int)
  ClaimDidNotHappen
  StillUnknown
}

pub type Message {
  Observed(observation: Observation)
  ClaimAnswered(cell_id: Int, site_id: Int, answer: ClaimAnswer)
  SettleAnswered(cell_id: Int, site_id: Int, settled: Settled)
}

pub type Action {
  /// Ask the Registry to claim this cell on this Site. `request` names this
  /// one claim, so that it can be asked about if its answer is lost.
  Claim(cell_id: Int, site_id: Int, request: String)
  /// The claim was won: run the attempt under its epoch.
  Dispatch(cell_id: Int, site_id: Int, attempt_id: Int, epoch: Int)
  /// Ask the Registry whether this claim happened.
  Settle(cell_id: Int, site_id: Int, request: String)
  /// Tell whoever listens what changed. `at` is the reading's moment.
  Notify(notices: List(Notice), at: Int)
  /// Nothing more to do until the next message.
  Rest(outcome: Outcome)
}

pub type Started {
  Started(cell_id: Int, site_id: Int, attempt_id: Int, epoch: Int)
}

pub type Underway {
  Training(cell_id: Int, attempt_id: Int, site: SiteRef)
  SettlingClaim(cell_id: Int, site_id: Int)
}

/// How a round ended. Every variant but `ScopeComplete` keeps the reasons of
/// the cells that did not run, grouped, so a round that claimed nothing still
/// says why.
pub type Outcome {
  WorkDispatched(started: List(Started), waiting: List(Group))
  WorkUnderway(underway: List(Underway), waiting: List(Group))
  /// `until` is the earliest moment a reason names; with none, the round
  /// waits for an event, which the caller looks for at its own interval.
  WaitingFor(until: Option(Int), waiting: List(Group))
  ScopeComplete
  StoppedAsAsked(waiting: List(Group))
}

pub opaque type Cycle {
  Cycle(
    settings: Settings,
    reading: Option(Observation),
    statuses: List(CellStatus),
    base: Dict(Int, Int),
    tried: List(#(Int, Int)),
    taken: Int,
    exhausted: Bool,
    refused: List(Remembered),
    unsettled: List(Pending),
    started: List(Started),
    pending: Option(Pending),
    claims: Int,
    room: Dict(Int, #(Int, List(Int))),
    last_answer: Dict(Int, String),
    reported: List(Group),
    stalls: List(NoProgress),
  )
}

type Pending {
  Pending(cell_id: Int, site_id: Int, request: String)
}

/// A refused claim, and the facts it was refused under.
type Remembered {
  Remembered(
    cell_id: Int,
    site_id: Int,
    detail: String,
    facts: Facts,
    retry_at: Int,
  )
}

/// Everything a claim of this cell on this Site depends on, and nothing else:
/// the cell and its attempts, its Study, the requests over either, the Site's
/// declaration and contact, and how many slots the Site is holding. A
/// supervisor's heartbeat is none of these.
type Facts {
  Facts(
    cell: Result(Cell, Nil),
    study: Result(Study, Nil),
    requests: List(Request),
    site: Result(Site, Nil),
    busy: Int,
  )
}

pub fn start(settings: Settings) -> Cycle {
  Cycle(
    settings: settings,
    reading: None,
    statuses: [],
    base: dict.new(),
    tried: [],
    taken: 0,
    exhausted: False,
    refused: [],
    unsettled: [],
    started: [],
    pending: None,
    claims: 0,
    room: dict.new(),
    last_answer: dict.new(),
    reported: [],
    stalls: [],
  )
}

/// The moment of the last reading, which sentences say durations from.
pub fn moment(cycle: Cycle) -> Int {
  case cycle.reading {
    Some(observed) -> observed.at
    None -> 0
  }
}

/// Every cell in scope as the last reading and this round's claims left it.
pub fn statuses(cycle: Cycle) -> List(CellStatus) {
  overlay(cycle)
}

/// A message the model did not ask for is refused: an answer to a claim that
/// was never sent, or a settlement of a claim that is not unsettled, would
/// otherwise start an attempt nobody decided on.
pub fn update(
  cycle: Cycle,
  message: Message,
) -> Result(#(Cycle, List(Action)), String) {
  case message {
    Observed(observed) -> Ok(observe(cycle, observed))
    ClaimAnswered(cell_id, site_id, answer) ->
      answered(cycle, cell_id, site_id, answer)
    SettleAnswered(cell_id, site_id, settled) ->
      settle_answered(cycle, cell_id, site_id, settled)
  }
}

fn observe(cycle: Cycle, observed: Observation) -> #(Cycle, List(Action)) {
  let serving = cycle.settings.serving
  let statuses =
    reason.explain(observed)
    |> list.filter(fn(status) { in_scope(serving, status) })
  let busy = reason.busy_slots(observed)
  let base =
    list.fold(serving.site_ids, dict.new(), fn(base, site_id) {
      let slots = case find_site(observed, site_id) {
        Ok(site) -> site.offer.slots
        Error(_) -> 0
      }
      dict.insert(
        base,
        site_id,
        slots - result.unwrap(dict.get(busy, site_id), 0),
      )
    })
  let refused =
    list.filter(cycle.refused, fn(remembered) {
      observed.at < remembered.retry_at
      && facts(observed, remembered.cell_id, remembered.site_id)
      == remembered.facts
    })
  let read =
    Cycle(
      ..cycle,
      reading: Some(observed),
      statuses: statuses,
      base: base,
      // A claim still waiting for its answer is not proposed a second time
      // in the new reading.
      tried: case cycle.pending {
        Some(pending) -> [#(pending.cell_id, pending.site_id)]
        None -> []
      },
      taken: 0,
      exhausted: False,
      refused: refused,
      started: [],
    )
  let room = track_room(read, cycle.room, observed)
  let in_scope_ids =
    list.map(statuses, fn(status) { status.cell.demand.cell_id })
  let stalls =
    list.flatten([
      progress.silent_trainings(
        observed,
        in_scope_ids,
        cycle.settings.deadlines,
      ),
      progress.undispatched(
        observed,
        room,
        cycle.last_answer,
        cycle.settings.deadlines,
      ),
    ])
  let groups = reason.groups(reason.not_finished(overlay(read)))
  let notices =
    list.append(
      progress.changes(cycle.reported, groups),
      progress.stall_changes(cycle.stalls, stalls),
    )
  let settles =
    list.map(cycle.unsettled, fn(pending) {
      Settle(pending.cell_id, pending.site_id, pending.request)
    })
  let #(read, next) =
    advance(Cycle(..read, room: room, reported: groups, stalls: stalls))
  let told = case notices {
    [] -> []
    _ -> [Notify(notices, observed.at)]
  }
  #(read, list.flatten([told, settles, next]))
}

fn answered(
  cycle: Cycle,
  cell_id: Int,
  site_id: Int,
  answer: ClaimAnswer,
) -> Result(#(Cycle, List(Action)), String) {
  case cycle.pending, cycle.reading {
    Some(pending), Some(observed)
      if pending.cell_id == cell_id && pending.site_id == site_id
    -> {
      let cycle = Cycle(..cycle, pending: None)
      Ok(case answer {
        Won(attempt_id, epoch) -> {
          let cycle = won(cycle, Started(cell_id, site_id, attempt_id, epoch))
          let #(cycle, next) = advance(cycle)
          #(cycle, [Dispatch(cell_id, site_id, attempt_id, epoch), ..next])
        }
        Taken -> {
          let taken = cycle.taken + 1
          advance(
            Cycle(
              ..cycle,
              taken: taken,
              exhausted: taken >= cycle.settings.races_per_reading,
              last_answer: dict.insert(
                cycle.last_answer,
                site_id,
                "another supervisor claimed cell "
                  <> int.to_string(cell_id)
                  <> " first",
              ),
            ),
          )
        }
        Refused(detail) -> {
          let remembered =
            Remembered(
              cell_id,
              site_id,
              detail,
              facts(observed, cell_id, site_id),
              observed.at + cycle.settings.refusal_backoff,
            )
          advance(
            Cycle(
              ..cycle,
              refused: [
                remembered,
                ..list.filter(cycle.refused, fn(earlier) {
                  earlier.cell_id != cell_id || earlier.site_id != site_id
                })
              ],
              last_answer: dict.insert(cycle.last_answer, site_id, detail),
            ),
          )
        }
        NoAnswer -> {
          let cycle =
            Cycle(
              ..cycle,
              unsettled: [pending, ..cycle.unsettled],
              last_answer: dict.insert(
                cycle.last_answer,
                site_id,
                "no answer; whether the claim of cell "
                  <> int.to_string(cell_id)
                  <> " happened is being settled",
              ),
            )
          let #(cycle, next) = advance(cycle)
          #(cycle, [Settle(cell_id, site_id, pending.request), ..next])
        }
      })
    }
    _, _ ->
      Error(
        "no claim of cell "
        <> int.to_string(cell_id)
        <> " on Site "
        <> int.to_string(site_id)
        <> " is waiting for an answer",
      )
  }
}

fn settle_answered(
  cycle: Cycle,
  cell_id: Int,
  site_id: Int,
  settled: Settled,
) -> Result(#(Cycle, List(Action)), String) {
  let matches = fn(pending: Pending) {
    pending.cell_id == cell_id && pending.site_id == site_id
  }
  case list.any(cycle.unsettled, matches) {
    False ->
      Error(
        "no claim of cell "
        <> int.to_string(cell_id)
        <> " on Site "
        <> int.to_string(site_id)
        <> " is waiting to be settled",
      )
    True -> {
      let rest = list.filter(cycle.unsettled, fn(pending) { !matches(pending) })
      Ok(case settled {
        StillUnknown -> #(cycle, [])
        ClaimHappened(attempt_id, epoch) -> {
          let cycle =
            won(
              Cycle(..cycle, unsettled: rest),
              Started(cell_id, site_id, attempt_id, epoch),
            )
          let #(cycle, next) = advance(cycle)
          #(cycle, [Dispatch(cell_id, site_id, attempt_id, epoch), ..next])
        }
        ClaimDidNotHappen -> advance(Cycle(..cycle, unsettled: rest))
      })
    }
  }
}

fn won(cycle: Cycle, started: Started) -> Cycle {
  Cycle(
    ..cycle,
    started: [started, ..cycle.started],
    room: dict.delete(cycle.room, started.site_id),
    last_answer: dict.delete(cycle.last_answer, started.site_id),
  )
}

/// Propose the next claim, or rest. One claim is outstanding at a time, so
/// every answer is read before the next slot is offered.
fn advance(cycle: Cycle) -> #(Cycle, List(Action)) {
  case cycle.pending, cycle.reading {
    Some(_), _ | _, None -> #(cycle, [])
    None, Some(observed) ->
      case cycle.exhausted, next_claim(cycle) {
        False, Some(#(cell_id, site_id)) -> {
          let request =
            "claim/"
            <> int.to_string(cycle.claims + 1)
            <> "/cell/"
            <> int.to_string(cell_id)
            <> "/site/"
            <> int.to_string(site_id)
            <> "/read/"
            <> int.to_string(observed.at)
          #(
            Cycle(
              ..cycle,
              pending: Some(Pending(cell_id, site_id, request)),
              claims: cycle.claims + 1,
              tried: [#(cell_id, site_id), ..cycle.tried],
            ),
            [Claim(cell_id, site_id, request)],
          )
        }
        _, _ -> #(cycle, [Rest(outcome(cycle, observed))])
      }
  }
}

/// The first cell in queue order, and the first Site it may run on, that
/// this supervisor serves, that has a free slot, and that this reading has
/// not already tried or seen refused.
fn next_claim(cycle: Cycle) -> Option(#(Int, Int)) {
  cycle.statuses
  |> list.find_map(fn(status) {
    let cell_id = status.cell.demand.cell_id
    case status.status {
      reason.Claimable(sites) ->
        case holds_claim(cycle, cell_id) {
          True -> Error(Nil)
          False ->
            sites
            |> list.find(fn(site) { may_try(cycle, cell_id, site.site_id) })
            |> result.map(fn(site) { #(cell_id, site.site_id) })
        }
      _ -> Error(Nil)
    }
  })
  |> option.from_result
}

fn holds_claim(cycle: Cycle, cell_id: Int) -> Bool {
  list.any(cycle.unsettled, fn(pending) { pending.cell_id == cell_id })
  || list.any(cycle.started, fn(started) { started.cell_id == cell_id })
}

fn may_try(cycle: Cycle, cell_id: Int, site_id: Int) -> Bool {
  list.contains(cycle.settings.serving.site_ids, site_id)
  && free_on(cycle, site_id) > 0
  && !list.contains(cycle.tried, #(cell_id, site_id))
  && !list.any(cycle.refused, fn(remembered) {
    remembered.cell_id == cell_id && remembered.site_id == site_id
  })
}

fn outcome(cycle: Cycle, observed: Observation) -> Outcome {
  let statuses = overlay(cycle)
  let left = reason.not_finished(statuses)
  let waiting = reason.groups(left)
  case cycle.started, left, unread(observed) {
    [_, ..], _, _ -> WorkDispatched(list.reverse(cycle.started), waiting)
    // A reading the Registry could not give says nothing about whether the
    // scope is finished; it is a wait for the Registry, never an empty queue.
    [], [], Some(cause) -> WaitingFor(None, [reason.Group(cause, [])])
    [], [], None -> ScopeComplete
    [], _, _ ->
      case cycle.exhausted, underway(cycle, statuses) {
        True, _ -> WaitingFor(Some(observed.at), waiting)
        False, [_, ..] as work -> WorkUnderway(work, waiting)
        False, [] ->
          case list.all(left, fn(status) { status.status == reason.Held }) {
            True -> StoppedAsAsked(waiting)
            False -> WaitingFor(earliest(waiting), waiting)
          }
      }
  }
}

fn unread(observed: Observation) -> Option(Reason) {
  case observed.authority {
    observation.Confirmed -> None
    observation.RegistryUnreachable(detail) ->
      Some(reason.CannotReadRegistry(detail))
    observation.OfficeUnconfirmed(detail) ->
      Some(reason.CannotConfirmOffice(detail))
  }
}

fn underway(cycle: Cycle, statuses: List(CellStatus)) -> List(Underway) {
  let trainings =
    list.filter_map(statuses, fn(status) {
      case status.status {
        reason.Running(attempt_id, site) ->
          Ok(Training(status.cell.demand.cell_id, attempt_id, site))
        _ -> Error(Nil)
      }
    })
  let settling =
    list.map(list.reverse(cycle.unsettled), fn(pending) {
      SettlingClaim(pending.cell_id, pending.site_id)
    })
  list.flatten([trainings, settling])
}

fn earliest(groups: List(Group)) -> Option(Int) {
  groups
  |> list.filter_map(fn(group) {
    option.to_result(reason.moment(group.reason), Nil)
  })
  |> list.reduce(int.min)
  |> option.from_result
}

/// The reading's statuses with what this supervisor's own claims added: a
/// claim refused on a Site, a claim whose answer was lost, a cell that only
/// other supervisors' Sites may take, and a Site whose slots this round has
/// already filled. A cell with any of these is no longer claimable here, and
/// says why.
fn overlay(cycle: Cycle) -> List(CellStatus) {
  let site_ids = cycle.settings.serving.site_ids
  list.map(cycle.statuses, fn(status) {
    let cell_id = status.cell.demand.cell_id
    case status.status {
      reason.Claimable(sites) ->
        case
          list.any(cycle.started, fn(started) { started.cell_id == cell_id })
        {
          True -> status
          False -> {
            let added = added_reasons(cycle, cell_id, sites, site_ids)
            case added {
              [] -> status
              _ -> {
                // A cell whose claim is unsettled is not claimable anywhere
                // until the Registry says which way it went.
                let still = case holds_claim(cycle, cell_id) {
                  True -> []
                  False ->
                    list.filter(sites, fn(site) {
                      list.contains(site_ids, site.site_id)
                      && may_take(cycle, cell_id, site.site_id)
                    })
                }
                CellStatus(
                  ..status,
                  status: reason.settle(status.reasons, still, [added]),
                  reasons: list.append(status.reasons, added),
                )
              }
            }
          }
        }
      _ -> status
    }
  })
}

fn added_reasons(
  cycle: Cycle,
  cell_id: Int,
  sites: List(SiteRef),
  site_ids: List(Int),
) -> List(Reason) {
  let unsettled =
    list.filter_map(cycle.unsettled, fn(pending) {
      case pending.cell_id == cell_id {
        True -> Ok(reason.ClaimUnsettled(site_of(sites, pending.site_id)))
        False -> Error(Nil)
      }
    })
  case unsettled {
    [_, ..] -> unsettled
    [] -> {
      let served =
        list.filter(sites, fn(site) { list.contains(site_ids, site.site_id) })
      case served {
        [] -> [reason.OnOtherSites(sites)]
        _ ->
          case
            list.any(served, fn(site) { may_take(cycle, cell_id, site.site_id) })
          {
            // A Site here can still take it: it is only waiting its turn
            // within this round, or was lost to another supervisor.
            True -> []
            False ->
              list.map(served, fn(site) {
                case find_refusal(cycle, cell_id, site.site_id) {
                  Ok(remembered) ->
                    reason.ClaimRefused(
                      site,
                      remembered.detail,
                      remembered.retry_at,
                    )
                  Error(_) -> reason.SiteFull(site)
                }
              })
          }
      }
    }
  }
}

/// Whether this Site could still take the cell in this round: a free slot
/// and no refusal of it there. Losing it to another supervisor is not a
/// reason: the next reading shows who holds it.
fn may_take(cycle: Cycle, cell_id: Int, site_id: Int) -> Bool {
  result.is_error(find_refusal(cycle, cell_id, site_id))
  && {
    free_on(cycle, site_id) > 0
    || list.contains(cycle.tried, #(cell_id, site_id))
  }
}

fn find_refusal(
  cycle: Cycle,
  cell_id: Int,
  site_id: Int,
) -> Result(Remembered, Nil) {
  list.find(cycle.refused, fn(remembered) {
    remembered.cell_id == cell_id && remembered.site_id == site_id
  })
}

fn site_of(sites: List(SiteRef), site_id: Int) -> SiteRef {
  list.find(sites, fn(site) { site.site_id == site_id })
  |> result.unwrap(reason.SiteRef(site_id, "#" <> int.to_string(site_id)))
}

fn in_scope(serving: Serving, status: CellStatus) -> Bool {
  case serving.study_id {
    None -> True
    Some(study_id) -> status.cell.demand.study_id == study_id
  }
}

/// For each served Site, since when claimable cells and a free slot have met
/// without a dispatch there, and which cells could run there now.
fn track_room(
  cycle: Cycle,
  room: Dict(Int, #(Int, List(Int))),
  observed: Observation,
) -> Dict(Int, #(Int, List(Int))) {
  list.fold(cycle.settings.serving.site_ids, dict.new(), fn(tracked, site_id) {
    let waiting =
      list.filter_map(cycle.statuses, fn(status) {
        let cell_id = status.cell.demand.cell_id
        case status.status {
          reason.Claimable(sites) ->
            case
              list.any(sites, fn(site) { site.site_id == site_id })
              && !holds_claim(cycle, cell_id)
            {
              True -> Ok(cell_id)
              False -> Error(Nil)
            }
          _ -> Error(Nil)
        }
      })
    case free_on(cycle, site_id) > 0, waiting {
      True, [_, ..] -> {
        let since = case dict.get(room, site_id) {
          Ok(#(since, _)) -> since
          Error(_) -> observed.at
        }
        dict.insert(tracked, site_id, #(since, waiting))
      }
      _, _ -> tracked
    }
  })
}

/// Free slots on a served Site: what the reading left, less this
/// supervisor's own claims the reading does not show yet, whether won or
/// still unsettled. A claim the reading already shows as an open attempt is
/// counted once, by the reading.
fn free_on(cycle: Cycle, site_id: Int) -> Int {
  case cycle.reading {
    None -> 0
    Some(observed) -> {
      let unseen = fn(cell_id) { !open_on(observed, cell_id, site_id) }
      result.unwrap(dict.get(cycle.base, site_id), 0)
      - list.count(cycle.started, fn(started) {
        started.site_id == site_id && unseen(started.cell_id)
      })
      - list.count(cycle.unsettled, fn(pending) {
        pending.site_id == site_id && unseen(pending.cell_id)
      })
    }
  }
}

fn open_on(observed: Observation, cell_id: Int, site_id: Int) -> Bool {
  list.any(observed.cells, fn(cell) {
    cell.demand.cell_id == cell_id
    && case cell.history {
      Open(_, open_site, _, _, _) -> open_site == site_id
      _ -> False
    }
  })
}

fn facts(observed: Observation, cell_id: Int, site_id: Int) -> Facts {
  let cell =
    list.find(observed.cells, fn(cell) { cell.demand.cell_id == cell_id })
  let study_id = case cell {
    Ok(found) -> found.demand.study_id
    Error(_) -> -1
  }
  Facts(
    cell: cell,
    study: list.find(observed.studies, fn(study) { study.study_id == study_id }),
    requests: list.filter(observed.requests, fn(request) {
      case request.scope {
        observation.Everywhere -> True
        observation.ForStudy(id) -> id == study_id
        observation.ForCell(id) -> id == cell_id
        observation.ForSite(id) -> id == site_id
      }
    }),
    site: find_site(observed, site_id),
    busy: result.unwrap(dict.get(reason.busy_slots(observed), site_id), 0),
  )
}

fn find_site(observed: Observation, site_id: Int) -> Result(Site, Nil) {
  list.find(observed.sites, fn(site) { site.offer.site_id == site_id })
}
