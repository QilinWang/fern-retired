//// The `office_lease` ask (slice W1 of
//// docs/programs/2026-09-22-gleam-site-supervision/2026-09-26-leases-and-watching-a-running-cell): one
//// event about a Site's office in, the next state and what to do out.
////
//// The state travels with every ask and comes back with the answer, so this
//// program keeps nothing between lines and a supervisor restarted mid-way
//// starts again from the state it last wrote.
////
//// ```text
//// {"id","ask":"office_lease","lease_seconds":600,"now":int,"state":state,"event":event}
//// state   {"kind":"seeking","holder":null|holder,"ask_at"}
////       | {"kind":"holding","epoch","until","draining"}
////       | {"kind":"unconfirmed","epoch","until","since","draining"}
////       | {"kind":"lost","epoch","why":{"kind":"taken_by","holder":null|holder}|{"kind":"expired_unrenewed"}}
////       | {"kind":"released","epoch"}
//// holder  {"host","pid"}
//// event   {"kind":"tick","now"} | {"kind":"granted","epoch","sent_at"}
////       | {"kind":"held_by","holder","now"} | {"kind":"renewed","sent_at"}
////       | {"kind":"renew_refused","holder":null|holder} | {"kind":"renew_unanswered","now"}
////       | {"kind":"asked_to_stop","now"} | {"kind":"nothing_running","now"}
//// answer  {"state","actions":[action],"may_claim","unconfirmed","claim_epoch":null|int,"sentence"}
//// action  {"kind":"ask_for_office"} | {"kind":"renew_at","at"} | {"kind":"ask_again_at","at"}
////       | {"kind":"stop_claiming"} | {"kind":"resume_claiming"}
////       | {"kind":"release_office","epoch"} | {"kind":"exit","why"}
//// ```
////
//// Every time is the supervisor's own monotonic clock, in whole seconds.

import fern_scheduler/office_lease.{
  type Action, type Event, type OfficeLease, type SupervisorRef,
}
import gleam/dynamic/decode
import gleam/json
import gleam/option.{type Option, None, Some}
import gleam/string

pub fn claims(line: String) -> Bool {
  case json.parse(string.trim(line), decode.at(["ask"], decode.string)) {
    Ok("office_lease") -> True
    _ -> False
  }
}

pub fn answer(document: String) -> Result(String, String) {
  let line = string.trim(document)
  case json.parse(line, decode.at(["id"], decode.string)) {
    Error(_) -> Error("the request requires a string id")
    Ok(id) -> {
      let reply = case json.parse(line, ask_decoder()) {
        Error(_) -> #(
          "error",
          json.object([
            #("kind", json.string("invalid_request")),
            #(
              "detail",
              json.string(
                "the office lease ask could not be read from the request",
              ),
            ),
          ]),
        )
        Ok(#(lease_seconds, now, state, event)) -> {
          let #(next, actions) =
            office_lease.update(lease_seconds, state, event)
          #(
            "answer",
            json.object([
              #("state", state_json(next)),
              #("actions", json.array(actions, action_json)),
              #("may_claim", json.bool(office_lease.may_claim(next))),
              #("unconfirmed", json.bool(office_lease.unconfirmed(next))),
              #(
                "claim_epoch",
                json.nullable(office_lease.claim_epoch(next), json.int),
              ),
              #("sentence", json.string(office_lease.describe(next, now))),
            ]),
          )
        }
      }
      Ok(json.to_string(json.object([#("id", json.string(id)), reply])))
    }
  }
}

fn ask_decoder() -> decode.Decoder(#(Int, Int, OfficeLease, Event)) {
  use lease_seconds <- decode.field("lease_seconds", decode.int)
  use now <- decode.field("now", decode.int)
  use state <- decode.field("state", state_decoder())
  use event <- decode.field("event", event_decoder())
  decode.success(#(lease_seconds, now, state, event))
}

fn holder_decoder() -> decode.Decoder(SupervisorRef) {
  use host <- decode.field("host", decode.string)
  use pid <- decode.field("pid", decode.int)
  decode.success(office_lease.SupervisorRef(host, pid))
}

fn state_decoder() -> decode.Decoder(OfficeLease) {
  use kind <- decode.field("kind", decode.string)
  case kind {
    "seeking" -> {
      use holder <- decode.field("holder", decode.optional(holder_decoder()))
      use ask_at <- decode.field("ask_at", decode.int)
      decode.success(office_lease.Seeking(holder, ask_at))
    }
    "holding" -> {
      use epoch <- decode.field("epoch", decode.int)
      use until <- decode.field("until", decode.int)
      use draining <- decode.field("draining", decode.bool)
      decode.success(office_lease.Holding(epoch, until, draining))
    }
    "unconfirmed" -> {
      use epoch <- decode.field("epoch", decode.int)
      use until <- decode.field("until", decode.int)
      use since <- decode.field("since", decode.int)
      use draining <- decode.field("draining", decode.bool)
      decode.success(office_lease.Unconfirmed(epoch, until, since, draining))
    }
    "lost" -> {
      use epoch <- decode.field("epoch", decode.int)
      use why <- decode.field("why", {
        use why <- decode.field("kind", decode.string)
        case why {
          "taken_by" -> {
            use holder <- decode.field(
              "holder",
              decode.optional(holder_decoder()),
            )
            decode.success(office_lease.TakenBy(holder))
          }
          "expired_unrenewed" -> decode.success(office_lease.ExpiredUnrenewed)
          _ ->
            decode.failure(
              office_lease.ExpiredUnrenewed,
              "taken_by or expired_unrenewed",
            )
        }
      })
      decode.success(office_lease.Lost(epoch, why))
    }
    "released" -> {
      use epoch <- decode.field("epoch", decode.int)
      decode.success(office_lease.Released(epoch))
    }
    _ ->
      decode.failure(
        office_lease.Released(0),
        "seeking, holding, unconfirmed, lost or released",
      )
  }
}

fn event_decoder() -> decode.Decoder(Event) {
  use kind <- decode.field("kind", decode.string)
  case kind {
    "tick" -> {
      use now <- decode.field("now", decode.int)
      decode.success(office_lease.Tick(now))
    }
    "granted" -> {
      use epoch <- decode.field("epoch", decode.int)
      use sent_at <- decode.field("sent_at", decode.int)
      decode.success(office_lease.Granted(epoch, sent_at))
    }
    "held_by" -> {
      use holder <- decode.field("holder", holder_decoder())
      use now <- decode.field("now", decode.int)
      decode.success(office_lease.HeldBy(holder, now))
    }
    "renewed" -> {
      use sent_at <- decode.field("sent_at", decode.int)
      decode.success(office_lease.Renewed(sent_at))
    }
    "renew_refused" -> {
      use holder <- decode.field("holder", decode.optional(holder_decoder()))
      decode.success(office_lease.RenewRefused(holder))
    }
    "renew_unanswered" -> {
      use now <- decode.field("now", decode.int)
      decode.success(office_lease.RenewUnanswered(now))
    }
    "asked_to_stop" -> {
      use now <- decode.field("now", decode.int)
      decode.success(office_lease.AskedToStop(now))
    }
    "nothing_running" -> {
      use now <- decode.field("now", decode.int)
      decode.success(office_lease.NothingRunning(now))
    }
    _ ->
      decode.failure(
        office_lease.Tick(0),
        "tick, granted, held_by, renewed, renew_refused, renew_unanswered, asked_to_stop or nothing_running",
      )
  }
}

fn holder_json(holder: SupervisorRef) -> json.Json {
  json.object([
    #("host", json.string(holder.host)),
    #("pid", json.int(holder.pid)),
  ])
}

fn optional_holder(holder: Option(SupervisorRef)) -> json.Json {
  case holder {
    Some(found) -> holder_json(found)
    None -> json.null()
  }
}

pub fn state_json(state: OfficeLease) -> json.Json {
  case state {
    office_lease.Seeking(holder, ask_at) ->
      json.object([
        #("kind", json.string("seeking")),
        #("holder", optional_holder(holder)),
        #("ask_at", json.int(ask_at)),
      ])
    office_lease.Holding(epoch, until, draining) ->
      json.object([
        #("kind", json.string("holding")),
        #("epoch", json.int(epoch)),
        #("until", json.int(until)),
        #("draining", json.bool(draining)),
      ])
    office_lease.Unconfirmed(epoch, until, since, draining) ->
      json.object([
        #("kind", json.string("unconfirmed")),
        #("epoch", json.int(epoch)),
        #("until", json.int(until)),
        #("since", json.int(since)),
        #("draining", json.bool(draining)),
      ])
    office_lease.Lost(epoch, why) ->
      json.object([
        #("kind", json.string("lost")),
        #("epoch", json.int(epoch)),
        #("why", case why {
          office_lease.TakenBy(holder) ->
            json.object([
              #("kind", json.string("taken_by")),
              #("holder", optional_holder(holder)),
            ])
          office_lease.ExpiredUnrenewed ->
            json.object([#("kind", json.string("expired_unrenewed"))])
        }),
      ])
    office_lease.Released(epoch) ->
      json.object([
        #("kind", json.string("released")),
        #("epoch", json.int(epoch)),
      ])
  }
}

fn action_json(action: Action) -> json.Json {
  case action {
    office_lease.AskForOffice ->
      json.object([#("kind", json.string("ask_for_office"))])
    office_lease.RenewAt(at) ->
      json.object([#("kind", json.string("renew_at")), #("at", json.int(at))])
    office_lease.AskAgainAt(at) ->
      json.object([
        #("kind", json.string("ask_again_at")),
        #("at", json.int(at)),
      ])
    office_lease.StopClaiming ->
      json.object([#("kind", json.string("stop_claiming"))])
    office_lease.ResumeClaiming ->
      json.object([#("kind", json.string("resume_claiming"))])
    office_lease.ReleaseOffice(epoch) ->
      json.object([
        #("kind", json.string("release_office")),
        #("epoch", json.int(epoch)),
      ])
    office_lease.Exit(why) ->
      json.object([#("kind", json.string("exit")), #("why", json.string(why))])
  }
}
