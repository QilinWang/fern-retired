//// Whether a Site can be reached and can reach the Home: slice S3 of
//// docs/programs/2026-09-22-gleam-site-supervision/2026-09-25-how-a-site-is-reached-and-started.
////
//// Three questions, in order, each asked of the Site through the maintained
//// SSH helper by the caller: does its SSH address answer; does the Registry
//// Home answer at the address the Site uses for it; is the runtime binary the
//// supervisor would start there. The first that fails is the answer, as one
//// sentence.
////
//// The Home's address on the Site exists only while some SSH session carries
//// the tunnel's remote forward. So the Home is first asked without a forward
//// of the check's own: that succeeds when a running supervisor's session
//// already carries it. Only when that fails and no supervisor holds the
//// Site's office is it asked again through the declared forward, which is how
//// a fresh start would reach it. It is never asked through a forward while a
//// supervisor holds the office: the SSH entry sets `ExitOnForwardFailure`, and
//// a second session asking for the same port fails although the supervisor
//// is healthy. On 2026-09-25 exactly that failure kept the Beijing unit
//// restarting every 15 seconds.
////
//// This module decides the next question and reads the answers; it runs
//// nothing.

import fern_scheduler/site_files.{type Declaration}
import gleam/int
import gleam/list
import gleam/option.{type Option, None, Some}

pub type Probe {
  ReachSite
  /// `through_forward`: ask through the declared forward of this check's own.
  HomeFromSite(through_forward: Bool)
  BinaryOnSite(path: String)
}

pub type Answer {
  Answered
  Failed(detail: String)
}

/// Whether a Site supervisor is running, read from the Registry.
pub type SupervisorAvailability {
  SupervisorRunning
  NoSupervisorRunning
  SupervisorStatusUnknown
}

pub type Step {
  Ask(probe: Probe)
  Reached(notes: List(String))
  NotReached(sentence: String)
}

/// The next question, or the verdict, from the answers so far.
pub fn next(
  declaration: Declaration,
  supervisor_term: SupervisorAvailability,
  binary: Option(String),
  answers: List(#(Probe, Answer)),
) -> Step {
  case declaration.address {
    None ->
      Reached(["Site " <> declaration.site <> " is not reached through SSH"])
    Some(address) -> {
      let destination =
        address.user
        <> "@"
        <> address.host
        <> ":"
        <> int.to_string(address.port)
      let home = site_files.home_on_site(declaration.remote_forward)
      case list.key_find(answers, ReachSite) {
        Error(_) -> Ask(ReachSite)
        Ok(Failed(detail)) ->
          NotReached(
            "Site "
            <> declaration.site
            <> ": the SSH address "
            <> destination
            <> " does not answer ("
            <> detail
            <> ")",
          )
        Ok(Answered) ->
          case home_step(declaration, supervisor_term, home, answers) {
            Error(step) -> step
            Ok(notes) ->
              case binary {
                None ->
                  Reached(
                    list.append(notes, [
                      "the runtime binary was not named, so its presence was not checked",
                    ]),
                  )
                Some(path) ->
                  case list.key_find(answers, BinaryOnSite(path)) {
                    Error(_) -> Ask(BinaryOnSite(path))
                    Ok(Answered) -> Reached(notes)
                    Ok(Failed(detail)) ->
                      NotReached(
                        "Site "
                        <> declaration.site
                        <> ": the runtime binary "
                        <> path
                        <> " is not on the Site ("
                        <> detail
                        <> ")",
                      )
                  }
              }
          }
      }
    }
  }
}

/// `Ok(notes)` once the Home has answered; otherwise the next step.
fn home_step(
  declaration: Declaration,
  supervisor_term: SupervisorAvailability,
  home: String,
  answers: List(#(Probe, Answer)),
) -> Result(List(String), Step) {
  let site = "Site " <> declaration.site
  case list.key_find(answers, HomeFromSite(False)) {
    Error(_) -> Error(Ask(HomeFromSite(False)))
    Ok(Answered) ->
      Ok(["the Home answers at " <> home <> " through a tunnel already open"])
    Ok(Failed(detail)) ->
      case supervisor_term {
        SupervisorRunning ->
          Error(NotReached(
            site
            <> ": the Home does not answer at "
            <> home
            <> " from the Site, although a supervisor holds the Site's office and its session should carry the tunnel ("
            <> detail
            <> ")",
          ))
        SupervisorStatusUnknown ->
          Error(NotReached(
            site
            <> ": the Home does not answer at "
            <> home
            <> " from the Site without a tunnel of this check's own, and whether a supervisor holds the Site's office was not read; give the Registry to check through the declared tunnel ("
            <> detail
            <> ")",
          ))
        NoSupervisorRunning ->
          case list.key_find(answers, HomeFromSite(True)) {
            Error(_) -> Error(Ask(HomeFromSite(True)))
            Ok(Answered) ->
              Ok([
                "the Home answers at "
                <> home
                <> " through the declared tunnel, as a fresh start would reach it",
              ])
            Ok(Failed(forwarded)) ->
              Error(NotReached(
                site
                <> ": the Home does not answer at "
                <> home
                <> " from the Site, even through the declared tunnel from the Site's port "
                <> int.to_string(declaration.remote_forward.site_port)
                <> " to this machine's port "
                <> int.to_string(declaration.remote_forward.home_port)
                <> " ("
                <> forwarded
                <> ")",
              ))
          }
      }
  }
}

// ── Ensuring one supervisor on a Site (slice F5) ─────────────────────────

/// What the Registry says of a Site's office, for deciding whether to start
/// a supervisor there. Times are the Home's, in whole seconds.
pub type Holder {
  NoHolder
  /// The office is held and its lease has not run out.
  HeldBy(host: String, pid: Int, until: Int)
  /// The row still names a holder, but its lease ran out at `until`.
  LapsedHolder(host: String, pid: Int, until: Int)
}

/// What a look at the holder's own process found: on this machine when it is
/// the holder's host (a dead office is released by looking), or on the Site
/// through a short SSH command. `CannotSee` when neither was possible; the
/// lease then decides.
pub type HolderProcess {
  Alive
  Gone
  CannotSee
}

/// What this machine last started on the Site.
pub type Started {
  NotStarted
  StartedAt(at: Int, pid: Int)
}

pub type EnsureStep {
  NothingToDo(sentence: String)
  /// The holder's process is gone although its lease stands: release the
  /// office by that look, then start one.
  ReleaseByLooking(host: String, pid: Int)
  StartOne
  /// One was started; wait until it takes the office or a lease passes.
  WaitForOffice(until: Int)
  /// Starting another would do harm or has not worked: a person looks.
  TellPerson(sentence: String)
}

/// The supervisor this machine started took the office: the next time the
/// office is empty, starting one is right again.
pub fn observe_start(started: Started, holder: Holder) -> Started {
  case started, holder {
    StartedAt(_, pid), HeldBy(_, held, _) if pid == held -> NotStarted
    _, _ -> started
  }
}

/// Whether to start a supervisor on the Site now. A Site has one: starting
/// a second while the first still lives would take its office, and the
/// first's exit would end the training it runs.
pub fn ensure(
  holder: Holder,
  process: HolderProcess,
  started: Started,
  lease_seconds: Int,
  now: Int,
) -> EnsureStep {
  case holder, process {
    HeldBy(host, pid, until), Alive | HeldBy(host, pid, until), CannotSee ->
      NothingToDo(
        "held by "
        <> host
        <> "/"
        <> int.to_string(pid)
        <> " for "
        <> int.to_string(until - now)
        <> " s more",
      )
    HeldBy(host, pid, _), Gone -> ReleaseByLooking(host, pid)
    LapsedHolder(host, pid, until), Alive ->
      TellPerson(
        "the office's holder "
        <> host
        <> "/"
        <> int.to_string(pid)
        <> " is alive on the Site but its lease ran out "
        <> int.to_string(now - until)
        <> " s ago: it cannot reach the Home. Starting another would take its office and end the training it runs; repair the route to the Home, or stop it through the Registry.",
      )
    _, _ ->
      case started {
        NotStarted -> StartOne
        StartedAt(at, _) if now < at + lease_seconds ->
          WaitForOffice(at + lease_seconds)
        StartedAt(at, pid) ->
          TellPerson(
            "a supervisor started on the Site as pid "
            <> int.to_string(pid)
            <> " "
            <> int.to_string(now - at)
            <> " s ago has not taken the office; nothing more is started until a person reads its log",
          )
      }
  }
}
