//// The `explain` and `cycle` asks as JSON lines, the way the scheduler that
//// holds this program open sends them.

import fern_scheduler/cycle_wire
import gleam/dynamic/decode
import gleam/json
import gleam/list
import gleam/string
import gleeunit

pub fn main() -> Nil {
  gleeunit.main()
}

const site = "{\"site_id\":7,\"name\":\"local\",\"admitted_models\":[\"fern\"],\"device\":\"cuda\",\"slots\":1,\"contact\":{\"kind\":\"answering\"}}"

fn cell(cell_id: String, priority: String, history: String) -> String {
  "{\"cell_id\":"
  <> cell_id
  <> ",\"study_id\":1,\"ordinal\":"
  <> cell_id
  <> ",\"name\":\"c"
  <> cell_id
  <> "\",\"model_type\":\"fern\",\"device\":\"cuda\",\"requires_cuda\":true,\"source_id\":152,\"priority\":"
  <> priority
  <> ",\"after\":[],\"history\":"
  <> history
  <> "}"
}

fn reading(cells: String, requests: String) -> String {
  "{\"at\":1000000,\"authority\":{\"kind\":\"confirmed\"},\"policy\":{\"max_attempts\":3,\"retry_window\":600,\"stale_after\":300,\"burst_failures\":3,\"burst_window\":600},"
  <> "\"studies\":[{\"study_id\":1,\"name\":\"s1\",\"intent\":\"run\",\"priority\":null}],"
  <> "\"cells\":["
  <> cells
  <> "],\"sites\":["
  <> site
  <> "],\"requests\":["
  <> requests
  <> "]}"
}

const never = "{\"kind\":\"never_tried\"}"

const unreadable_failure = "{\"kind\":\"ended\",\"tries\":1,\"last\":{\"kind\":\"failed\",\"class\":null,\"site_id\":7,\"ended_at\":999000,\"sentence\":\"exit 1\"}}"

fn ask(
  memory: cycle_wire.Memory,
  line: String,
) -> #(cycle_wire.Memory, String) {
  let #(memory, said) = cycle_wire.answer(memory, line)
  let assert Ok(said) = said
  #(memory, said)
}

fn at(said: String, path: List(String)) -> Result(String, json.DecodeError) {
  json.parse(said, decode.at(path, decode.string))
}

pub fn explain_names_every_cell_with_its_reasons_test() {
  let line =
    "{\"id\":\"e1\",\"ask\":\"explain\",\"observation\":"
    <> reading(
      cell("1", "null", unreadable_failure)
        <> ","
        <> cell("2", "\"urgent\"", never),
      "",
    )
    <> "}"
  assert cycle_wire.claims(line)
  let #(_, said) = ask(cycle_wire.memory(), line)
  assert at(said, ["id"]) == Ok("e1")
  // The urgent cell comes first, and may run.
  assert json.parse(
      said,
      decode.at(
        ["answer", "cells"],
        decode.list(decode.at(["name"], decode.string)),
      ),
    )
    == Ok(["c2", "c1"])
  assert json.parse(
      said,
      decode.at(
        ["answer", "cells"],
        decode.list(decode.at(["status", "kind"], decode.string)),
      ),
    )
    == Ok(["claimable", "needs_person"])
  let assert Ok([group]) =
    json.parse(
      said,
      decode.at(["answer", "groups"], decode.list(decode.dynamic)),
    )
  let assert Ok(kind) =
    decode.run(group, decode.at(["reason", "kind"], decode.string))
  assert kind == "failure_unreadable"
  let assert Ok(actor) =
    decode.run(group, decode.at(["reason", "actor"], decode.string))
  assert actor == "person_must_act"
  let assert Ok(scope) =
    decode.run(group, decode.at(["reason", "scope", "kind"], decode.string))
  assert scope == "these_cells"
}

pub fn a_round_claims_dispatches_and_rests_test() {
  let memory = cycle_wire.memory()
  let #(memory, said) =
    ask(
      memory,
      "{\"id\":\"s\",\"ask\":\"cycle\",\"supervisor\":\"local\",\"message\":{\"kind\":\"start\",\"settings\":{\"study_id\":null,\"site_ids\":[7],\"races_per_reading\":2,\"refusal_backoff\":300,\"deadlines\":{\"dispatch\":600,\"training_progress\":1800}}}}",
    )
  assert string.contains(said, "\"actions\":[]")
  let #(memory, said) =
    ask(
      memory,
      "{\"id\":\"o\",\"ask\":\"cycle\",\"supervisor\":\"local\",\"message\":{\"kind\":\"observed\",\"observation\":"
        <> reading(cell("1", "null", never), "")
        <> "}}",
    )
  assert json.parse(
      said,
      decode.at(
        ["answer", "actions"],
        decode.list(decode.at(["kind"], decode.string)),
      ),
    )
    == Ok(["claim"])
  let #(_, said) =
    ask(
      memory,
      "{\"id\":\"a\",\"ask\":\"cycle\",\"supervisor\":\"local\",\"message\":{\"kind\":\"claim_answered\",\"cell_id\":1,\"site_id\":7,\"answer\":{\"kind\":\"won\",\"attempt_id\":31,\"epoch\":4}}}",
    )
  assert json.parse(
      said,
      decode.at(
        ["answer", "actions"],
        decode.list(decode.at(["kind"], decode.string)),
      ),
    )
    == Ok(["dispatch", "rest"])
  assert json.parse(
      said,
      decode.at(
        ["answer", "actions"],
        decode.list(decode.optionally_at(["outcome", "kind"], "", decode.string)),
      ),
    )
    == Ok(["", "work_dispatched"])
}

pub fn a_round_that_was_never_started_is_refused_test() {
  let #(_, said) =
    ask(
      cycle_wire.memory(),
      "{\"id\":\"x\",\"ask\":\"cycle\",\"supervisor\":\"pod\",\"message\":{\"kind\":\"claim_answered\",\"cell_id\":1,\"site_id\":7,\"answer\":{\"kind\":\"taken\"}}}",
    )
  assert at(said, ["error", "kind"]) == Ok("refused")
  assert at(said, ["error", "detail"])
    == Ok("supervisor pod has not started a round; send start first")
}

pub fn a_number_is_not_a_priority_test() {
  let #(_, said) =
    ask(
      cycle_wire.memory(),
      "{\"id\":\"p\",\"ask\":\"explain\",\"observation\":"
        <> reading(cell("1", "3000", never), "")
        <> "}",
    )
  assert at(said, ["error", "kind"]) == Ok("invalid_request")
}

pub fn a_request_scope_that_cannot_be_read_refuses_the_reading_test() {
  let request =
    "{\"request_id\":4,\"kind\":\"hold\",\"scope\":\"planet:3\",\"by\":\"q\",\"reason\":\"r\",\"at\":999999}"
  let #(_, said) =
    ask(
      cycle_wire.memory(),
      "{\"id\":\"r\",\"ask\":\"explain\",\"observation\":"
        <> reading(cell("1", "null", never), request)
        <> "}",
    )
  assert at(said, ["error", "kind"]) == Ok("invalid_request")
}

pub fn a_hold_in_the_registrys_spelling_is_read_test() {
  let request =
    "{\"request_id\":4,\"kind\":\"hold\",\"scope\":\"cell:1\",\"by\":\"qilin\",\"reason\":\"checking\",\"at\":999940}"
  let #(_, said) =
    ask(
      cycle_wire.memory(),
      "{\"id\":\"h\",\"ask\":\"explain\",\"observation\":"
        <> reading(cell("1", "null", never), request)
        <> "}",
    )
  assert json.parse(
      said,
      decode.at(
        ["answer", "cells"],
        decode.list(decode.at(["status", "kind"], decode.string)),
      ),
    )
    == Ok(["held"])
}

pub fn other_asks_are_left_to_the_placement_wire_test() {
  assert !cycle_wire.claims("{\"id\":\"k\",\"ask\":\"known_models\"}")
  let #(_, said) =
    cycle_wire.answer(cycle_wire.memory(), "{\"ask\":\"explain\"}")
  let assert Error(_) = said
}

pub fn a_cell_moved_last_is_read_and_waits_behind_its_rank_test() {
  // Cell 1 would go first by its compiled order; a person moved it last.
  let moved =
    string.replace(
      cell("1", "null", never),
      ",\"after\"",
      ",\"moved_last\":{\"behind\":1,\"at\":999000},\"after\"",
    )
  let #(_, said) =
    ask(
      cycle_wire.memory(),
      "{\"id\":\"m\",\"ask\":\"explain\",\"observation\":"
        <> reading(moved <> "," <> cell("2", "null", never), "")
        <> "}",
    )
  assert json.parse(
      said,
      decode.at(
        ["answer", "cells"],
        decode.list(decode.at(["name"], decode.string)),
      ),
    )
    == Ok(["c2", "c1"])
}

pub fn a_move_without_its_time_refuses_the_reading_test() {
  let moved =
    string.replace(
      cell("1", "null", never),
      ",\"after\"",
      ",\"moved_last\":5,\"after\"",
    )
  let #(_, said) =
    ask(
      cycle_wire.memory(),
      "{\"id\":\"m\",\"ask\":\"explain\",\"observation\":"
        <> reading(moved, "")
        <> "}",
    )
  assert at(said, ["error", "kind"]) == Ok("invalid_request")
}

// ── what_stops_work (slices R3 and R4) ───────────────────────────────────

fn supervised_reading(serves: String, cells: String) -> String {
  string.replace(
    reading(cells, ""),
    "\"contact\":{\"kind\":\"answering\"}}",
    "\"contact\":{\"kind\":\"answering\"},\"supervision\":{\"since\":990000,\"serves\":"
      <> serves
      <> "},\"last_ended_at\":998500}",
  )
}

pub fn what_stops_work_names_an_idle_site_and_tells_it_once_test() {
  let line = fn(told: String) {
    "{\"id\":\"w\",\"ask\":\"what_stops_work\",\"observation\":"
    <> supervised_reading("[2]", cell("1", "null", never))
    <> ",\"told\":["
    <> told
    <> "],\"seen\":[{\"key\":\"idle_site:7\",\"since\":998800}]}"
  }
  assert cycle_wire.claims(line(""))
  let #(_, said) = ask(cycle_wire.memory(), line(""))
  assert json.parse(
      said,
      decode.at(
        ["answer", "items"],
        decode.list(decode.at(["key"], decode.string)),
      ),
    )
    == Ok(["idle_site:7"])
  let assert Ok([sentence]) =
    json.parse(
      said,
      decode.at(
        ["answer", "items"],
        decode.list(decode.at(["sentence"], decode.string)),
      ),
    )
  assert string.contains(sentence, "serves only Study #2")
  assert string.contains(sentence, "it has taken nothing for 25 min")
  // Seen idle in every reading since 998800, twenty minutes before this one.
  assert json.parse(
      said,
      decode.at(
        ["answer", "to_tell"],
        decode.list(decode.at(["notices"], decode.list(decode.string))),
      ),
    )
    == Ok([["once"]])
  let #(_, again) =
    ask(
      cycle_wire.memory(),
      line("{\"key\":\"idle_site:7\",\"notices\":[\"once\"]}"),
    )
  assert json.parse(
      again,
      decode.at(
        ["answer", "to_tell"],
        decode.list(decode.at(["key"], decode.string)),
      ),
    )
    == Ok([])
}

pub fn a_reading_from_an_older_home_reads_as_not_recorded_test() {
  let line =
    "{\"id\":\"o\",\"ask\":\"what_stops_work\",\"observation\":"
    <> reading(cell("1", "null", never), "")
    <> "}"
  let #(_, said) = ask(cycle_wire.memory(), line)
  let assert Ok([sentence]) =
    json.parse(
      said,
      decode.at(
        ["answer", "items"],
        decode.list(decode.at(["sentence"], decode.string)),
      ),
    )
  assert string.contains(sentence, "did not record which Studies it serves")
  assert json.parse(
      said,
      decode.at(
        ["answer", "to_tell"],
        decode.list(decode.at(["notice"], decode.string)),
      ),
    )
    == Ok([])
  let every =
    "{\"id\":\"e\",\"ask\":\"what_stops_work\",\"observation\":"
    <> supervised_reading("\"every\"", cell("1", "null", never))
    <> "}"
  let #(_, said) = ask(cycle_wire.memory(), every)
  let assert Ok([sentence]) =
    json.parse(
      said,
      decode.at(
        ["answer", "items"],
        decode.list(decode.at(["sentence"], decode.string)),
      ),
    )
  assert string.contains(sentence, "serves every Study in state run")
}

// ── installed sources retired (2026-09-26) ───────────────────────────────

/// Slice 4 of docs/programs/2026-09-26-source-roots-stop-being-full-copies:
/// no Site holds sources, so a reading carries neither a Site's
/// `held_sources` nor the `preparations` of installs, and no cell waits for
/// a source to be installed.
pub fn a_reading_without_held_sources_names_no_missing_source_test() {
  // Cell 1 needs source 153, which no Site was ever said to hold.
  let observed =
    reading(
      string.replace(
        cell("1", "null", never),
        "\"source_id\":152",
        "\"source_id\":153",
      ),
      "",
    )
  assert !string.contains(observed, "held_sources")
  assert !string.contains(observed, "preparations")
  let explain = fn(observed) {
    let #(_, said) =
      ask(
        cycle_wire.memory(),
        "{\"id\":\"n\",\"ask\":\"explain\",\"observation\":" <> observed <> "}",
      )
    said
  }
  let said = explain(observed)
  assert json.parse(
      said,
      decode.at(
        ["answer", "cells"],
        decode.list(decode.at(["status", "kind"], decode.string)),
      ),
    )
    == Ok(["claimable"])
  assert json.parse(
      said,
      decode.at(["answer", "groups"], decode.list(decode.dynamic)),
    )
    == Ok([])
  // What stops work names no missing source either.
  let #(_, said) =
    ask(
      cycle_wire.memory(),
      "{\"id\":\"w\",\"ask\":\"what_stops_work\",\"observation\":"
        <> observed
        <> "}",
    )
  let assert Ok(keys) =
    json.parse(
      said,
      decode.at(
        ["answer", "items"],
        decode.list(decode.at(["key"], decode.string)),
      ),
    )
  assert !list.any(keys, fn(key) { string.starts_with(key, "source_missing:") })
  // A reading from an older Home that still sends both fields, one of them
  // an install that needs a person, is read the same way.
  let older =
    string.replace(observed, "\"contact\":", "\"held_sources\":[],\"contact\":")
    |> string.drop_end(1)
    |> string.append(
      ",\"preparations\":[{\"site_id\":7,\"source_id\":153,\"state\":{\"kind\":\"needs_person\",\"reason\":\"no blob\"}}]}",
    )
  assert string.contains(older, "held_sources")
  assert explain(older) == explain(observed)
}
