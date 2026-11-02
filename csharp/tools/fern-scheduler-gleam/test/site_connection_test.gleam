//// Slice S3: the order of the connection questions, the tunnel rule, and
//// each sentence.

import fern_scheduler/site_connection.{
  Answered, Ask, BinaryOnSite, Failed, HomeFromSite, NotReached, NoSupervisorRunning,
  SupervisorRunning, SupervisorStatusUnknown, ReachSite, Reached,
}
import fern_scheduler/site_files
import gleam/option.{None, Some}
import gleam/string
import gleeunit

pub fn main() -> Nil {
  gleeunit.main()
}

const profile = "{\"profile_id\":\"autodl-bj-5090\",\"executor\":\"ssh\",\"ssh_destination\":\"root@site-b2.example.invalid:50300\",\"python\":\"/root/python\",\"work_root\":\"/root/work\"}"

const supervisor = "{\"schema\":\"fern.site_supervisor.v1\",\"site\":\"autodl-bj-5090\",\"remote_forward\":{\"site_port\":8622,\"home_port\":8621},\"ssh_compression\":true,\"fern_run\":\"/root/bin/fern-run.8e41\",\"process_environment\":{},\"restart\":\"always\"}"

fn declared() -> site_files.Declaration {
  let assert Ok(declaration) = site_files.declare(profile, supervisor)
  declaration
}

const binary = Some("/root/bin/fern-run.8e41")

pub fn the_questions_come_in_order_and_all_answered_is_reached_test() {
  let declaration = declared()
  assert site_connection.next(declaration, SupervisorRunning, binary, [])
    == Ask(ReachSite)
  assert site_connection.next(declaration, SupervisorRunning, binary, [
      #(ReachSite, Answered),
    ])
    == Ask(HomeFromSite(False))
  assert site_connection.next(declaration, SupervisorRunning, binary, [
      #(ReachSite, Answered),
      #(HomeFromSite(False), Answered),
    ])
    == Ask(BinaryOnSite("/root/bin/fern-run.8e41"))
  assert site_connection.next(declaration, SupervisorRunning, binary, [
      #(ReachSite, Answered),
      #(HomeFromSite(False), Answered),
      #(BinaryOnSite("/root/bin/fern-run.8e41"), Answered),
    ])
    == Reached([
      "the Home answers at http://127.0.0.1:8622/ through a tunnel already open",
    ])
}

pub fn an_unreachable_site_stops_at_the_first_question_test() {
  assert site_connection.next(declared(), NoSupervisorRunning, binary, [
      #(ReachSite, Failed("port 50300: Connection refused")),
    ])
    == NotReached(
      "Site autodl-bj-5090: the SSH address root@site-b2.example.invalid:50300 does not answer (port 50300: Connection refused)",
    )
}

pub fn no_forward_is_asked_for_while_a_supervisor_holds_the_office_test() {
  assert site_connection.next(declared(), SupervisorRunning, binary, [
      #(ReachSite, Answered),
      #(HomeFromSite(False), Failed("Connection refused")),
    ])
    == NotReached(
      "Site autodl-bj-5090: the Home does not answer at http://127.0.0.1:8622/ from the Site, although a supervisor holds the Site's office and its session should carry the tunnel (Connection refused)",
    )
}

pub fn a_free_site_is_asked_again_through_the_declared_forward_test() {
  let declaration = declared()
  let without = [
    #(ReachSite, Answered),
    #(HomeFromSite(False), Failed("refused")),
  ]
  assert site_connection.next(declaration, NoSupervisorRunning, binary, without)
    == Ask(HomeFromSite(True))
  assert site_connection.next(declaration, NoSupervisorRunning, binary, [
      #(HomeFromSite(True), Failed("remote port forwarding failed")),
      ..without
    ])
    == NotReached(
      "Site autodl-bj-5090: the Home does not answer at http://127.0.0.1:8622/ from the Site, even through the declared tunnel from the Site's port 8622 to this machine's port 8621 (remote port forwarding failed)",
    )
  assert site_connection.next(declaration, NoSupervisorRunning, None, [
      #(HomeFromSite(True), Answered),
      ..without
    ])
    == Reached([
      "the Home answers at http://127.0.0.1:8622/ through the declared tunnel, as a fresh start would reach it",
      "the runtime binary was not named, so its presence was not checked",
    ])
}

pub fn an_unread_office_is_never_guessed_free_test() {
  let assert NotReached(sentence) =
    site_connection.next(declared(), SupervisorStatusUnknown, binary, [
      #(ReachSite, Answered),
      #(HomeFromSite(False), Failed("refused")),
    ])
  assert sentence
    == "Site autodl-bj-5090: the Home does not answer at http://127.0.0.1:8622/ from the Site without a tunnel of this check's own, and whether a supervisor holds the Site's office was not read; give the Registry to check through the declared tunnel (refused)"
}

pub fn a_missing_binary_is_named_test() {
  assert site_connection.next(declared(), SupervisorRunning, binary, [
      #(ReachSite, Answered),
      #(HomeFromSite(False), Answered),
      #(BinaryOnSite("/root/bin/fern-run.8e41"), Failed("fern-binary: absent")),
    ])
    == NotReached(
      "Site autodl-bj-5090: the runtime binary /root/bin/fern-run.8e41 is not on the Site (fern-binary: absent)",
    )
}

// ── Slice F5: one supervisor on a Site ───────────────────────────────────

const lease = 600

pub fn a_held_office_starts_nothing_test() {
  let held = site_connection.HeldBy("autodl-container", 75_934, 2000)
  let assert site_connection.NothingToDo(_) =
    site_connection.ensure(
      held,
      site_connection.Alive,
      site_connection.NotStarted,
      lease,
      1000,
    )
  let assert site_connection.NothingToDo(_) =
    site_connection.ensure(
      held,
      site_connection.CannotSee,
      site_connection.NotStarted,
      lease,
      1000,
    )
}

pub fn a_holder_whose_process_is_gone_is_released_by_looking_test() {
  // 2026-09-26 04:05 EDT: pid 1360234 was gone and its lease held ten minutes.
  assert site_connection.ensure(
      site_connection.HeldBy("MSI", 1_360_234, 1600),
      site_connection.Gone,
      site_connection.NotStarted,
      lease,
      1000,
    )
    == site_connection.ReleaseByLooking("MSI", 1_360_234)
}

pub fn an_empty_office_starts_one_then_waits_then_tells_a_person_test() {
  let empty = site_connection.NoHolder
  assert site_connection.ensure(
      empty,
      site_connection.CannotSee,
      site_connection.NotStarted,
      lease,
      1000,
    )
    == site_connection.StartOne
  let started = site_connection.StartedAt(1000, 4242)
  assert site_connection.ensure(
      empty,
      site_connection.CannotSee,
      started,
      lease,
      1599,
    )
    == site_connection.WaitForOffice(1600)
  let assert site_connection.TellPerson(sentence) =
    site_connection.ensure(
      empty,
      site_connection.CannotSee,
      started,
      lease,
      1600,
    )
  assert string.contains(
    sentence,
    "pid 4242 600 s ago has not taken the office",
  )
}

pub fn a_lapsed_holder_still_alive_is_not_replaced_test() {
  // Beijing, 2026-09-26: the holder lives on the Site but cannot reach the Home.
  let lapsed = site_connection.LapsedHolder("autodl-container", 69_639, 900)
  let assert site_connection.TellPerson(sentence) =
    site_connection.ensure(
      lapsed,
      site_connection.Alive,
      site_connection.NotStarted,
      lease,
      1000,
    )
  assert string.contains(sentence, "cannot reach the Home")
  // Gone, or not visible: the lease has run out, so one is started.
  assert site_connection.ensure(
      lapsed,
      site_connection.Gone,
      site_connection.NotStarted,
      lease,
      1000,
    )
    == site_connection.StartOne
  assert site_connection.ensure(
      lapsed,
      site_connection.CannotSee,
      site_connection.NotStarted,
      lease,
      1000,
    )
    == site_connection.StartOne
}

pub fn the_started_supervisor_taking_the_office_resets_the_start_test() {
  let started = site_connection.StartedAt(1000, 4242)
  assert site_connection.observe_start(
      started,
      site_connection.HeldBy("s", 4242, 1800),
    )
    == site_connection.NotStarted
  assert site_connection.observe_start(
      started,
      site_connection.HeldBy("s", 1, 1800),
    )
    == started
}
