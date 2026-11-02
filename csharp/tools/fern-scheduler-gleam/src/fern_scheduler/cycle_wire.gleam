//// The two asks of the scheduling round, as JSON lines: `explain` and
//// `cycle`; and `what_stops_work` (slices R3 and R4 of
//// what-stops-work-and-who-lifts-it), which reads one reading and the keys
//// already told, and answers the summary and what to tell now:
////
//// ```text
//// {"id","ask":"what_stops_work","observation":{...},"told":[{"key","notices":[notice]}],
////  "let_run"?:[item key],"seen"?:[{"key","since"}]}
//// -> {"id","answer":{"items":[{"key","sentence","cell_ids","waits_for":{"kind":"a_person"}
////                              |{"kind":"who_asked","by"},"since":null|int,"tell_from":null|int,
////                              "repeat_every":null|int,"continuous_for":null|int}],
////                   "to_tell":[{"key","notices":[notice]}],"give_up":[{"attempt_id","sentence"}],
////                   "oldest_request":null|{"request_id","by","reason","at"}}}
////
//// CHG 2026-09-26: the caller keeps, per item, the notices it sent, and
//// forgets an item's notices once the item is gone from a reading, so a
//// problem that comes back is told again (`stuck.due_notices`). `seen` is
//// since when each item has been in every reading.
//// ```
////
//// `explain` reads one Registry reading and answers, for every cell, where it
//// stands and every reason it does not run, grouped the way a person reads
//// them. It keeps nothing.
////
//// `cycle` carries one message of a supervisor's round and answers with the
//// actions the round takes. The round's memory — which claims were refused
//// under which facts, which answers were lost, what was already told — stays
//// in this program, keyed by the `supervisor` the ask names, and is not sent
//// back and forth. A restarted program has forgotten it: the next reading
//// rebuilds what matters, at the cost of one claim asked again and one notice
//// told again.
////
//// ```text
//// {"id":"1","ask":"cycle","supervisor":"local","message":{"kind":"start","settings":{...}}}
//// {"id":"2","ask":"cycle","supervisor":"local","message":{"kind":"observed","observation":{...}}}
//// {"id":"3","ask":"cycle","supervisor":"local","message":{"kind":"claim_answered","cell_id":3,"site_id":7,"answer":{"kind":"won","attempt_id":31,"epoch":4}}}
//// ```
////
//// A reading's JSON, field by field:
////
//// ```text
//// at             whole seconds since 1970-01-01 UTC
//// authority      {"kind":"confirmed"} | {"kind":"registry_unreachable","detail"} | {"kind":"office_unconfirmed","detail"}
//// policy         {"max_attempts","retry_window","stale_after","burst_failures","burst_window"}
//// studies[]      {"study_id","name","intent":"run|pause|cancel","priority":null|word,"moved_last"?:null|move}
//// cells[]        {"cell_id","study_id","ordinal","name","model_type","device","requires_cuda",
////                 "source_id","priority":null|word,"moved_last"?:null|move,"after":[cell ids],"history"}
//// move           {"behind": the latest Study id when it was made, "at": whole seconds since 1970-01-01 UTC}
//// history        {"kind":"never_tried"} | {"kind":"accepted"}
////                | {"kind":"open","attempt_id","site_id","started_at","beat_at","progress_at":null|int}
////                | {"kind":"ended","tries","last"}
//// last           {"kind":"failed","class":null|word,"site_id","ended_at","sentence","code"?:null|word}
////                | {"kind":"lost","site_id","ended_at"} | {"kind":"cancelled","ended_at"}
////                | {"kind":"retry_requested","by","reason","at"}
//// sites[]        {"site_id","name","admitted_models","device","slots","contact",
////                 "failures"?:[{"source_id","cell_id","ended_at","sentence","code"?}],
////                 "supervision"?:{"since","serves":"every"|[study ids]},"last_ended_at"?:null|int}
//// contact        {"kind":"answering"} | {"kind":"not_answering","since"} | {"kind":"never_supervised"}
//// requests[]     {"request_id","kind":"pause|hold|stop","scope":"all|study:N|cell:N|site:N","by","reason","at"}
//// studies[].recompiled? {"kind":"not_compared"} | {"kind":"same_identities"}
////                | {"kind":"identities_changed","compiler_then","compiler_now","cells":[{"cell_id","regions":[word]}]}
////                | {"kind":"run_as_compiled","by","at"}
//// ```
////
//// CHG 2026-09-26 (installed sources retired;
//// docs/programs/2026-09-26-source-roots-stop-being-full-copies slice 4): a
//// Site no longer carries `held_sources`, and a reading no longer carries
//// `preparations` with their `state`. A reading from an older Home that still
//// sends them is read without them.
////
//// A failure's `class` is the record's own `failure_class`, or, for a record
//// older than that field, the class its booleans imply. It is never filled
//// in with a default: an absent class is shown as unreadable and waits for a
//// person, instead of being offered again as if it were transient.

import fern_protocol/study_intent as intent
import fern_scheduler/observation.{type Observation}

import fern_scheduler/cycle.{type Action, type Cycle, type Outcome}
import fern_scheduler/decision.{CellDemand, SiteOffer}

import fern_protocol/priority.{type Priority}
import fern_scheduler/progress.{type NoProgress, type Notice}
import fern_scheduler/reason.{
  type CellStatus, type Group, type Reason, type Scope, type SiteRef,
  type Status, type Wake,
}
import fern_scheduler/stuck
import gleam/dict.{type Dict}
import gleam/dynamic/decode
import gleam/int
import gleam/json.{type Json}
import gleam/option.{None, Some}
import gleam/string

/// Every supervisor's round, by the name its asks give.
pub opaque type Memory {
  Memory(rounds: Dict(String, Cycle))
}

pub fn memory() -> Memory {
  Memory(dict.new())
}

/// Whether this line is one of the two asks this module answers.
pub fn claims(line: String) -> Bool {
  case json.parse(string.trim(line), decode.at(["ask"], decode.string)) {
    Ok("explain") | Ok("cycle") | Ok("what_stops_work") -> True
    _ -> False
  }
}

/// One ask in, one answer out, and the memory as the answer left it. An ask
/// without a string id is refused without an answer line, as every other ask
/// of this program is.
pub fn answer(
  memory: Memory,
  document: String,
) -> #(Memory, Result(String, String)) {
  let line = string.trim(document)
  case json.parse(line, decode.at(["id"], decode.string)) {
    Error(_) -> #(memory, Error("the request requires a string id"))
    Ok(id) -> {
      let #(memory, reply) = case json.parse(line, ask_decoder()) {
        Error(error) -> #(memory, refusal("invalid_request", unreadable(error)))
        Ok(Explain(observed)) -> #(memory, #("answer", explained(observed)))
        Ok(Round(supervisor, message)) -> round(memory, supervisor, message)
        Ok(WhatStopsWork(observed, told, let_run, seen)) -> #(
          memory,
          #("answer", what_stops_work(observed, told, let_run, seen)),
        )
      }
      #(
        memory,
        Ok(json.to_string(json.object([#("id", json.string(id)), reply]))),
      )
    }
  }
}

type Ask {
  Explain(observation: Observation)
  Round(supervisor: String, message: RoundMessage)
  WhatStopsWork(
    observation: Observation,
    told: List(stuck.Told),
    let_run: List(String),
    seen: List(#(String, Int)),
  )
}

fn what_stops_work(
  observed: Observation,
  told: List(stuck.Told),
  let_run: List(String),
  seen: List(#(String, Int)),
) -> Json {
  let items = stuck.stuck(observed)
  json.object([
    #(
      "items",
      json.array(items, fn(item) {
        json.object([
          #("key", json.string(item.key)),
          #("sentence", json.string(item.sentence)),
          #("cell_ids", json.array(item.cell_ids, json.int)),
          #("waits_for", case item.waits_for {
            stuck.APerson -> json.object([#("kind", json.string("a_person"))])
            stuck.WhoAsked(by) ->
              json.object([
                #("kind", json.string("who_asked")),
                #("by", json.string(by)),
              ])
          }),
          #("since", json.nullable(item.since, json.int)),
          #("tell_from", json.nullable(item.tell_from, json.int)),
          #("repeat_every", json.nullable(item.repeat_every, json.int)),
          #("continuous_for", json.nullable(item.continuous_for, json.int)),
        ])
      }),
    ),
    #(
      "to_tell",
      json.array(
        stuck.to_tell(items, told, let_run, seen, observed.at),
        fn(pair) {
          json.object([
            #("key", json.string({ pair.0 }.key)),
            #("notices", json.array(pair.1, json.string)),
          ])
        },
      ),
    ),
    // CHG 2026-09-26 (owner): attempts silent for an hour that nobody answered.
    #(
      "give_up",
      json.array(stuck.to_give_up(observed, let_run), fn(one) {
        json.object([
          #("attempt_id", json.int(one.attempt_id)),
          #("sentence", json.string(one.sentence)),
        ])
      }),
    ),
    #(
      "oldest_request",
      json.nullable(stuck.oldest_request(observed), fn(request) {
        json.object([
          #("request_id", json.int(request.request_id)),
          #("by", json.string(request.by)),
          #("reason", json.string(request.reason)),
          #("at", json.int(request.at)),
        ])
      }),
    ),
  ])
}

type RoundMessage {
  Start(settings: cycle.Settings)
  Step(message: cycle.Message)
}

fn round(
  memory: Memory,
  supervisor: String,
  message: RoundMessage,
) -> #(Memory, #(String, Json)) {
  case message, dict.get(memory.rounds, supervisor) {
    Start(settings), _ -> #(
      Memory(dict.insert(memory.rounds, supervisor, cycle.start(settings))),
      #("answer", json.object([#("actions", json.array([], json.object))])),
    )
    Step(_), Error(_) -> #(
      memory,
      refusal(
        "refused",
        "supervisor "
          <> supervisor
          <> " has not started a round; send start first",
      ),
    )
    Step(step), Ok(state) ->
      case cycle.update(state, step) {
        Error(detail) -> #(memory, refusal("refused", detail))
        Ok(#(state, actions)) -> #(
          Memory(dict.insert(memory.rounds, supervisor, state)),
          #(
            "answer",
            json.object([
              #(
                "actions",
                json.array(actions, fn(action) {
                  action_json(action, cycle.moment(state))
                }),
              ),
            ]),
          ),
        )
      }
  }
}

fn refusal(kind: String, detail: String) -> #(String, Json) {
  #(
    "error",
    json.object([
      #("kind", json.string(kind)),
      #("detail", json.string(detail)),
    ]),
  )
}

fn unreadable(error: json.DecodeError) -> String {
  case error {
    json.UnableToDecode([decode.DecodeError(expected, found, path), ..]) ->
      "expected "
      <> expected
      <> ", found "
      <> found
      <> " at "
      <> case path {
        [] -> "the top"
        _ -> string.join(path, ".")
      }
    _ -> "the request is not JSON"
  }
}

// ── Reading the asks ─────────────────────────────────────────────────────

fn ask_decoder() -> decode.Decoder(Ask) {
  use ask <- decode.field("ask", decode.string)
  case ask {
    "explain" -> {
      use observed <- decode.field("observation", observation_decoder())
      decode.success(Explain(observed))
    }
    "cycle" -> {
      use supervisor <- decode.field("supervisor", decode.string)
      use message <- decode.field("message", round_message_decoder())
      decode.success(Round(supervisor, message))
    }
    "what_stops_work" -> {
      use observed <- decode.field("observation", observation_decoder())
      use told <- decode.optional_field(
        "told",
        [],
        decode.list({
          use key <- decode.field("key", decode.string)
          use notices <- decode.field("notices", decode.list(decode.string))
          decode.success(stuck.Told(key, notices))
        }),
      )
      use seen <- decode.optional_field(
        "seen",
        [],
        decode.list({
          use key <- decode.field("key", decode.string)
          use since <- decode.field("since", decode.int)
          decode.success(#(key, since))
        }),
      )
      use let_run <- decode.optional_field(
        "let_run",
        [],
        decode.list(decode.string),
      )
      decode.success(WhatStopsWork(observed, told, let_run, seen))
    }
    _ ->
      decode.failure(
        Explain(empty_reading()),
        "explain, cycle or what_stops_work",
      )
  }
}

fn round_message_decoder() -> decode.Decoder(RoundMessage) {
  use kind <- decode.field("kind", decode.string)
  case kind {
    "start" -> {
      use settings <- decode.field("settings", settings_decoder())
      decode.success(Start(settings))
    }
    "observed" -> {
      use observed <- decode.field("observation", observation_decoder())
      decode.success(Step(cycle.Observed(observed)))
    }
    "claim_answered" -> {
      use cell_id <- decode.field("cell_id", decode.int)
      use site_id <- decode.field("site_id", decode.int)
      use answer <- decode.field("answer", claim_answer_decoder())
      decode.success(Step(cycle.ClaimAnswered(cell_id, site_id, answer)))
    }
    "settle_answered" -> {
      use cell_id <- decode.field("cell_id", decode.int)
      use site_id <- decode.field("site_id", decode.int)
      use settled <- decode.field("settled", settled_decoder())
      decode.success(Step(cycle.SettleAnswered(cell_id, site_id, settled)))
    }
    _ ->
      decode.failure(
        Step(cycle.Observed(empty_reading())),
        "start, observed, claim_answered or settle_answered",
      )
  }
}

fn settings_decoder() -> decode.Decoder(cycle.Settings) {
  use study_id <- decode.field("study_id", decode.optional(decode.int))
  use site_ids <- decode.field("site_ids", decode.list(decode.int))
  use races <- decode.field("races_per_reading", decode.int)
  use backoff <- decode.field("refusal_backoff", decode.int)
  use deadlines <- decode.field("deadlines", {
    use dispatch <- decode.field("dispatch", decode.int)
    use training <- decode.field("training_progress", decode.int)
    decode.success(progress.Deadlines(dispatch, training))
  })
  decode.success(cycle.Settings(
    cycle.Serving(study_id, site_ids),
    races,
    backoff,
    deadlines,
  ))
}

fn claim_answer_decoder() -> decode.Decoder(cycle.ClaimAnswer) {
  use kind <- decode.field("kind", decode.string)
  case kind {
    "won" -> {
      use attempt_id <- decode.field("attempt_id", decode.int)
      use epoch <- decode.field("epoch", decode.int)
      decode.success(cycle.Won(attempt_id, epoch))
    }
    "taken" -> decode.success(cycle.Taken)
    "refused" -> {
      use detail <- decode.field("detail", decode.string)
      decode.success(cycle.Refused(detail))
    }
    "no_answer" -> decode.success(cycle.NoAnswer)
    _ -> decode.failure(cycle.NoAnswer, "won, taken, refused or no_answer")
  }
}

fn settled_decoder() -> decode.Decoder(cycle.Settled) {
  use kind <- decode.field("kind", decode.string)
  case kind {
    "happened" -> {
      use attempt_id <- decode.field("attempt_id", decode.int)
      use epoch <- decode.field("epoch", decode.int)
      decode.success(cycle.ClaimHappened(attempt_id, epoch))
    }
    "did_not_happen" -> decode.success(cycle.ClaimDidNotHappen)
    "still_unknown" -> decode.success(cycle.StillUnknown)
    _ ->
      decode.failure(
        cycle.StillUnknown,
        "happened, did_not_happen or still_unknown",
      )
  }
}

fn empty_reading() -> Observation {
  observation.Observation(
    0,
    observation.Confirmed,
    observation.Policy(0, 0, 0, 0, 0),
    [],
    [],
    [],
    [],
  )
}

fn observation_decoder() -> decode.Decoder(Observation) {
  use at <- decode.field("at", decode.int)
  use authority <- decode.field("authority", authority_decoder())
  use policy <- decode.field("policy", {
    use max_attempts <- decode.field("max_attempts", decode.int)
    use retry_window <- decode.field("retry_window", decode.int)
    use stale_after <- decode.field("stale_after", decode.int)
    use burst_failures <- decode.field("burst_failures", decode.int)
    use burst_window <- decode.field("burst_window", decode.int)
    decode.success(observation.Policy(
      max_attempts,
      retry_window,
      stale_after,
      burst_failures,
      burst_window,
    ))
  })
  use studies <- decode.field("studies", decode.list(study_decoder()))
  use cells <- decode.field("cells", decode.list(cell_decoder()))
  use sites <- decode.field("sites", decode.list(site_decoder()))
  use requests <- decode.field("requests", decode.list(request_decoder()))
  decode.success(observation.Observation(
    at,
    authority,
    policy,
    studies,
    cells,
    sites,
    requests,
  ))
}

fn authority_decoder() -> decode.Decoder(observation.Authority) {
  use kind <- decode.field("kind", decode.string)
  case kind {
    "confirmed" -> decode.success(observation.Confirmed)
    "registry_unreachable" -> {
      use detail <- decode.field("detail", decode.string)
      decode.success(observation.RegistryUnreachable(detail))
    }
    "office_unconfirmed" -> {
      use detail <- decode.field("detail", decode.string)
      decode.success(observation.OfficeUnconfirmed(detail))
    }
    _ ->
      decode.failure(
        observation.Confirmed,
        "confirmed, registry_unreachable or office_unconfirmed",
      )
  }
}

/// Only the four words. A number or another word refuses the whole reading:
/// a priority this program would have to guess is not read as normal.
fn priority_decoder() -> decode.Decoder(Priority) {
  use word <- decode.then(decode.string)
  case priority.parse(word) {
    Ok(read) -> decode.success(read)
    Error(_) ->
      decode.failure(priority.Normal, "immediate, urgent, normal or slow")
  }
}

/// A move to the back of the rank: `{"behind": latest Study id, "at": seconds}`.
fn move_decoder() -> decode.Decoder(priority.Move) {
  use behind <- decode.field("behind", decode.int)
  use at <- decode.field("at", decode.int)
  decode.success(priority.Move(behind, at))
}

fn study_decoder() -> decode.Decoder(observation.Study) {
  use study_id <- decode.field("study_id", decode.int)
  use name <- decode.field("name", decode.string)
  use intent <- decode.field("intent", {
    use word <- decode.then(decode.string)
    case intent.parse(word) {
      Ok(value) -> decode.success(value)
      Error(_) -> decode.failure(intent.ToRun, "run, pause or cancel")
    }
  })
  use priority <- decode.field("priority", decode.optional(priority_decoder()))
  use moved_last <- decode.optional_field(
    "moved_last",
    None,
    decode.optional(move_decoder()),
  )
  // CHG 2026-09-26 (slice M4): the Study's cards compiled again with the
  // compiler its source is declared with now; an older Home sends none.
  use recompiled <- decode.optional_field(
    "recompiled",
    observation.NotCompared,
    recompiled_decoder(),
  )
  decode.success(observation.Study(
    study_id,
    name,
    intent,
    priority,
    moved_last,
    recompiled,
  ))
}

fn recompiled_decoder() -> decode.Decoder(observation.Recompiled) {
  use kind <- decode.field("kind", decode.string)
  case kind {
    "not_compared" -> decode.success(observation.NotCompared)
    "same_identities" -> decode.success(observation.SameIdentities)
    "identities_changed" -> {
      use then <- decode.field("compiler_then", decode.string)
      use now <- decode.field("compiler_now", decode.string)
      use cells <- decode.field(
        "cells",
        decode.list({
          use cell_id <- decode.field("cell_id", decode.int)
          use regions <- decode.field("regions", decode.list(decode.string))
          decode.success(observation.ChangedCell(cell_id, regions))
        }),
      )
      decode.success(observation.IdentitiesChanged(then, now, cells))
    }
    "run_as_compiled" -> {
      use by <- decode.field("by", decode.string)
      use at <- decode.field("at", decode.int)
      decode.success(observation.RunAsCompiled(by, at))
    }
    _ ->
      decode.failure(
        observation.NotCompared,
        "not_compared, same_identities, identities_changed or run_as_compiled",
      )
  }
}

fn cell_decoder() -> decode.Decoder(observation.Cell) {
  use cell_id <- decode.field("cell_id", decode.int)
  use study_id <- decode.field("study_id", decode.int)
  use ordinal <- decode.field("ordinal", decode.int)
  use name <- decode.field("name", decode.string)
  use model_type <- decode.field("model_type", decode.string)
  use device <- decode.field("device", decode.string)
  use requires_cuda <- decode.field("requires_cuda", decode.bool)
  use source_id <- decode.field("source_id", decode.int)
  use priority <- decode.field("priority", decode.optional(priority_decoder()))
  use moved_last <- decode.optional_field(
    "moved_last",
    None,
    decode.optional(move_decoder()),
  )
  use after <- decode.field("after", decode.list(decode.int))
  use history <- decode.field("history", history_decoder())
  decode.success(observation.Cell(
    CellDemand(
      cell_id,
      study_id,
      ordinal,
      name,
      model_type,
      device,
      requires_cuda,
      source_id,
    ),
    priority,
    moved_last,
    after,
    history,
  ))
}

fn history_decoder() -> decode.Decoder(observation.History) {
  use kind <- decode.field("kind", decode.string)
  case kind {
    "never_tried" -> decode.success(observation.NeverTried)
    "accepted" -> decode.success(observation.Accepted)
    "open" -> {
      use attempt_id <- decode.field("attempt_id", decode.int)
      use site_id <- decode.field("site_id", decode.int)
      use started_at <- decode.field("started_at", decode.int)
      use beat_at <- decode.field("beat_at", decode.int)
      use progress_at <- decode.field(
        "progress_at",
        decode.optional(decode.int),
      )
      decode.success(
        observation.Open(
          attempt_id,
          site_id,
          started_at,
          beat_at,
          case progress_at {
            Some(at) -> observation.ProgressAt(at)
            None -> observation.ProgressNotObserved
          },
        ),
      )
    }
    "ended" -> {
      use tries <- decode.field("tries", decode.int)
      use last <- decode.field("last", ending_decoder())
      decode.success(observation.Ended(tries, last))
    }
    _ ->
      decode.failure(
        observation.NeverTried,
        "never_tried, accepted, open or ended",
      )
  }
}

fn ending_decoder() -> decode.Decoder(observation.Ending) {
  use kind <- decode.field("kind", decode.string)
  case kind {
    "failed" -> {
      use class <- decode.field("class", decode.optional(decode.string))
      use site_id <- decode.field("site_id", decode.int)
      use ended_at <- decode.field("ended_at", decode.int)
      use sentence <- decode.field("sentence", decode.string)
      // A record written before failures carried a code has none.
      use code <- decode.optional_field(
        "code",
        None,
        decode.optional(decode.string),
      )
      decode.success(observation.Failed(
        observation.failure_class(class),
        site_id,
        ended_at,
        sentence,
        code,
      ))
    }
    "lost" -> {
      use site_id <- decode.field("site_id", decode.int)
      use ended_at <- decode.field("ended_at", decode.int)
      decode.success(observation.Lost(site_id, ended_at))
    }
    "cancelled" -> {
      use ended_at <- decode.field("ended_at", decode.int)
      decode.success(observation.Cancelled(ended_at))
    }
    "retry_requested" -> {
      use by <- decode.field("by", decode.string)
      use reason <- decode.field("reason", decode.string)
      use at <- decode.field("at", decode.int)
      decode.success(observation.RetryRequested(by, reason, at))
    }
    _ ->
      decode.failure(
        observation.Cancelled(0),
        "failed, lost, cancelled or retry_requested",
      )
  }
}

fn site_decoder() -> decode.Decoder(observation.Site) {
  use site_id <- decode.field("site_id", decode.int)
  use name <- decode.field("name", decode.string)
  use admitted_models <- decode.field(
    "admitted_models",
    decode.list(decode.string),
  )
  use device <- decode.field("device", decode.string)
  use slots <- decode.field("slots", decode.int)
  use contact <- decode.field("contact", {
    use kind <- decode.field("kind", decode.string)
    case kind {
      "answering" -> decode.success(observation.Answering)
      "not_answering" -> {
        use since <- decode.field("since", decode.int)
        decode.success(observation.NotAnswering(since))
      }
      "never_supervised" -> decode.success(observation.NeverSupervised)
      _ ->
        decode.failure(
          observation.Answering,
          "answering, not_answering or never_supervised",
        )
    }
  })
  use failures <- decode.optional_field(
    "failures",
    [],
    decode.list({
      use source_id <- decode.field("source_id", decode.int)
      use cell_id <- decode.field("cell_id", decode.int)
      use ended_at <- decode.field("ended_at", decode.int)
      use sentence <- decode.field("sentence", decode.string)
      use code <- decode.optional_field(
        "code",
        None,
        decode.optional(decode.string),
      )
      decode.success(observation.SiteFailure(
        source_id,
        cell_id,
        ended_at,
        sentence,
        code,
      ))
    }),
  )
  // CHG 2026-09-26 (slice R4): what the supervisor recorded when it took its
  // office, and when the Site's last attempt ended; a reading from an older
  // Home carries neither.
  use supervision <- decode.optional_field(
    "supervision",
    observation.NotRecorded,
    {
      use since <- decode.field("since", decode.int)
      use serves <- decode.field(
        "serves",
        decode.one_of(
          decode.string
            |> decode.then(fn(word) {
              case word {
                "every" -> decode.success(observation.EveryStudy)
                _ -> decode.failure(observation.EveryStudy, "every")
              }
            }),
          [decode.list(decode.int) |> decode.map(observation.OnlyStudies)],
        ),
      )
      decode.success(observation.Supervision(since, serves))
    },
  )
  use last_ended_at <- decode.optional_field(
    "last_ended_at",
    None,
    decode.optional(decode.int),
  )
  // CHG 2026-09-27: the Site's own cell timeout, from its supervisor
  // declaration, is the budget its trainings are measured against.
  use training_budget <- decode.optional_field(
    "cell_timeout_seconds",
    None,
    decode.optional(decode.int),
  )
  decode.success(observation.Site(
    SiteOffer(site_id, name, admitted_models, device, slots),
    contact,
    failures,
    supervision,
    last_ended_at,
    training_budget,
  ))
}

fn request_decoder() -> decode.Decoder(observation.Request) {
  use request_id <- decode.field("request_id", decode.int)
  use kind <- decode.field("kind", {
    use word <- decode.then(decode.string)
    case word {
      "pause" -> decode.success(observation.Pause)
      "hold" -> decode.success(observation.Hold)
      "stop" -> decode.success(observation.Stop)
      _ -> decode.failure(observation.Pause, "pause, hold or stop")
    }
  })
  use scope <- decode.field("scope", request_scope_decoder())
  use by <- decode.field("by", decode.string)
  use reason <- decode.field("reason", decode.string)
  use at <- decode.field("at", decode.int)
  decode.success(observation.Request(request_id, kind, scope, by, reason, at))
}

/// The Registry's own spelling: `all`, `study:N`, `cell:N`, `site:N`. A scope
/// this program cannot read refuses the reading rather than being widened or
/// narrowed by a guess.
fn request_scope_decoder() -> decode.Decoder(observation.RequestScope) {
  use word <- decode.then(decode.string)
  let numbered = fn(rest, make) {
    case int.parse(rest) {
      Ok(id) -> decode.success(make(id))
      Error(_) ->
        decode.failure(observation.Everywhere, "all, study:N, cell:N or site:N")
    }
  }
  case word {
    "all" -> decode.success(observation.Everywhere)
    "study:" <> rest -> numbered(rest, observation.ForStudy)
    "cell:" <> rest -> numbered(rest, observation.ForCell)
    "site:" <> rest -> numbered(rest, observation.ForSite)
    _ ->
      decode.failure(observation.Everywhere, "all, study:N, cell:N or site:N")
  }
}

// ── Writing the answers ──────────────────────────────────────────────────

fn explained(observed: Observation) -> Json {
  let statuses = reason.explain(observed)
  json.object([
    #("at", json.int(observed.at)),
    #(
      "cells",
      json.array(statuses, fn(status) { cell_status_json(status, observed.at) }),
    ),
    #(
      "groups",
      json.array(reason.groups(reason.not_finished(statuses)), fn(group) {
        group_json(group, observed.at)
      }),
    ),
  ])
}

fn cell_status_json(status: CellStatus, at: Int) -> Json {
  let demand = status.cell.demand
  json.object([
    #("cell_id", json.int(demand.cell_id)),
    #("study_id", json.int(demand.study_id)),
    #("name", json.string(demand.name)),
    #("priority", json.string(priority.word(status.priority))),
    #("status", status_json(status.status)),
    #("reasons", json.array(status.reasons, fn(one) { reason_json(one, at) })),
  ])
}

fn status_json(status: Status) -> Json {
  let kind = #("kind", json.string(reason.status_word(status)))
  case status {
    reason.Running(attempt_id, site) ->
      json.object([
        kind,
        #("attempt_id", json.int(attempt_id)),
        #("site", site_json(site)),
      ])
    reason.Claimable(sites) ->
      json.object([kind, #("sites", json.array(sites, site_json))])
    _ -> json.object([kind])
  }
}

fn site_json(site: SiteRef) -> Json {
  json.object([
    #("site_id", json.int(site.site_id)),
    #("name", json.string(site.name)),
  ])
}

fn group_json(group: Group, at: Int) -> Json {
  json.object([
    #("reason", reason_json(group.reason, at)),
    #("cell_ids", json.array(group.cell_ids, json.int)),
  ])
}

/// The reason as values a program branches on, and the sentence a person
/// reads. Nothing that reads this should parse the sentence.
fn reason_json(one: Reason, at: Int) -> Json {
  json.object([
    #("kind", json.string(reason.kind(one))),
    #("key", json.string(reason.key(one))),
    #("actor", json.string(actor_word(reason.actor(one)))),
    #("scope", scope_json(reason.scope(one))),
    #("wake", wake_json(reason.wake(one))),
    #("sentence", json.string(reason.describe(one, at))),
  ])
}

fn actor_word(actor: reason.Actor) -> String {
  case actor {
    reason.SchedulerResumes -> "scheduler_resumes"
    reason.PersonMustAct -> "person_must_act"
    reason.PersonAsked -> "person_asked"
  }
}

fn scope_json(scope: Scope) -> Json {
  case scope {
    reason.TheseCells -> kinded("these_cells", [])
    reason.TheseCellsOnSite(site_id) ->
      kinded("these_cells_on_site", [#("site_id", json.int(site_id))])
    reason.SourceOnSite(site_id, source_id) ->
      kinded("source_on_site", [
        #("site_id", json.int(site_id)),
        #("source_id", json.int(source_id)),
      ])
    reason.WholeSite(site_id) ->
      kinded("whole_site", [#("site_id", json.int(site_id))])
    reason.WholeStudy(study_id) ->
      kinded("whole_study", [#("study_id", json.int(study_id))])
    reason.Everything -> kinded("everything", [])
  }
}

fn wake_json(wake: Wake) -> Json {
  case wake {
    reason.At(moment) -> kinded("at", [#("moment", json.int(moment))])
    reason.RequestReleased(request_id) ->
      kinded("request_released", [#("request_id", json.int(request_id))])
    reason.StudySetToRun(study_id) ->
      kinded("study_set_to_run", [#("study_id", json.int(study_id))])
    reason.SlotFrees(site_id) ->
      kinded("slot_frees", [#("site_id", json.int(site_id))])
    reason.SiteAnswers(site_id) ->
      kinded("site_answers", [#("site_id", json.int(site_id))])
    reason.CellsAccepted(cell_ids) ->
      kinded("cells_accepted", [#("cell_ids", json.array(cell_ids, json.int))])
    reason.OutcomeSettled(attempt_id) ->
      kinded("outcome_settled", [#("attempt_id", json.int(attempt_id))])
    reason.ClaimSettled(site_id) ->
      kinded("claim_settled", [#("site_id", json.int(site_id))])
    reason.SiteAdmitsCell -> kinded("site_admits_cell", [])
    reason.PersonRetries -> kinded("person_retries", [])
    reason.FactsChangeOrAt(moment) ->
      kinded("facts_change_or_at", [#("moment", json.int(moment))])
    reason.SupervisorStartsAfter(site_id, moment) ->
      kinded("supervisor_starts_after", [
        #("site_id", json.int(site_id)),
        #("moment", json.int(moment)),
      ])
    reason.StudyDecided(study_id) ->
      kinded("study_decided", [#("study_id", json.int(study_id))])
    reason.NextReading -> kinded("next_reading", [])
    reason.RegistryAnswers -> kinded("registry_answers", [])
    reason.OfficeConfirmed -> kinded("office_confirmed", [])
  }
}

fn kinded(kind: String, fields: List(#(String, Json))) -> Json {
  json.object([#("kind", json.string(kind)), ..fields])
}

fn action_json(action: Action, at: Int) -> Json {
  case action {
    cycle.Claim(cell_id, site_id, request) ->
      kinded("claim", [
        #("cell_id", json.int(cell_id)),
        #("site_id", json.int(site_id)),
        #("request", json.string(request)),
      ])
    cycle.Dispatch(cell_id, site_id, attempt_id, epoch) ->
      kinded("dispatch", [
        #("cell_id", json.int(cell_id)),
        #("site_id", json.int(site_id)),
        #("attempt_id", json.int(attempt_id)),
        #("epoch", json.int(epoch)),
      ])
    cycle.Settle(cell_id, site_id, request) ->
      kinded("settle", [
        #("cell_id", json.int(cell_id)),
        #("site_id", json.int(site_id)),
        #("request", json.string(request)),
      ])
    cycle.Notify(notices, at) ->
      kinded("notify", [
        #(
          "notices",
          json.array(notices, fn(notice) { notice_json(notice, at) }),
        ),
      ])
    cycle.Rest(outcome) ->
      kinded("rest", [#("outcome", outcome_json(outcome, at))])
  }
}

fn notice_json(notice: Notice, at: Int) -> Json {
  let cell_ids = case notice {
    progress.Blocked(group)
    | progress.PersonNeeded(group)
    | progress.PersonStopped(group)
    | progress.Repaired(group)
    | progress.Lifted(group) -> group.cell_ids
    progress.Stalled(stall) | progress.Resumed(stall) -> stall_cells(stall)
  }
  kinded(progress.notice_kind(notice), [
    #("key", json.string(progress.notice_key(notice))),
    #("cell_ids", json.array(cell_ids, json.int)),
    #("sentence", json.string(progress.describe_notice(notice, at))),
  ])
}

fn stall_cells(stall: NoProgress) -> List(Int) {
  case stall {
    progress.NotDispatched(_, cell_ids, _, _) -> cell_ids
    progress.TrainingSilent(cell_id, _, _, _, _) -> [cell_id]
  }
}

fn outcome_json(outcome: Outcome, at: Int) -> Json {
  let waiting_json = fn(groups) {
    json.array(groups, fn(group) { group_json(group, at) })
  }
  case outcome {
    cycle.WorkDispatched(started, waiting) ->
      kinded("work_dispatched", [
        #("started", json.array(started, started_json)),
        #("waiting", waiting_json(waiting)),
      ])
    cycle.WorkUnderway(underway, waiting) ->
      kinded("work_underway", [
        #("underway", json.array(underway, underway_json)),
        #("waiting", waiting_json(waiting)),
      ])
    cycle.WaitingFor(until, waiting) ->
      kinded("waiting_for", [
        #("until", json.nullable(until, json.int)),
        #("waiting", waiting_json(waiting)),
      ])
    cycle.ScopeComplete -> kinded("scope_complete", [])
    cycle.StoppedAsAsked(waiting) ->
      kinded("stopped_as_asked", [#("waiting", waiting_json(waiting))])
  }
}

fn started_json(started: cycle.Started) -> Json {
  json.object([
    #("cell_id", json.int(started.cell_id)),
    #("site_id", json.int(started.site_id)),
    #("attempt_id", json.int(started.attempt_id)),
    #("epoch", json.int(started.epoch)),
  ])
}

fn underway_json(underway: cycle.Underway) -> Json {
  case underway {
    cycle.Training(cell_id, attempt_id, site) ->
      kinded("training", [
        #("cell_id", json.int(cell_id)),
        #("attempt_id", json.int(attempt_id)),
        #("site", site_json(site)),
      ])
    cycle.SettlingClaim(cell_id, site_id) ->
      kinded("settling_claim", [
        #("cell_id", json.int(cell_id)),
        #("site_id", json.int(site_id)),
      ])
  }
}
