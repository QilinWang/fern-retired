//// The document the other implementation hands this one, and the document
//// this one hands back. Parsing lives here so a difference in reading cannot
//// be mistaken for a difference in the decision: the decision itself takes
//// the parsed values.

import fern_scheduler/decision.{
  type CellDemand, type Decision, type Placement, type SiteOffer, type Unplaced,
  CellDemand, SiteOffer,
}
import fern_scheduler/placement.{
  type CompileRefusal, type PlaceableCell, type PlaceableSite, type Refusal,
  CompileRefusal, PlaceableCell, PlaceableSite,
}
import fern_scheduler/retry
import gleam/bit_array
import gleam/dict
import gleam/dynamic/decode
import gleam/json
import gleam/option
import gleam/result
import gleam/string

/// One line of standard input, or nothing when the caller has gone.
pub fn read_line() -> Result(String, Nil) {
  use bits <- result.try(do_read_line())
  bit_array.to_string(bits) |> result.replace_error(Nil)
}

@external(erlang, "fern_scheduler_ffi", "read_line")
fn do_read_line() -> Result(BitArray, Nil)

/// One ask in, one answer out. An ask this program does not know is an error,
/// not an empty decision.
///
/// The answer carries back the `id` the asker put on the ask. That is how a
/// caller holding this program open — which is how it is held, one per
/// scheduler rather than one per question — knows which answer is which.
pub fn answer(document: String) -> Result(String, String) {
  let line = string.trim(document)
  // CHG 2026-09-24: identify the request before decoding its payload. A bad
  // payload with an id receives a refusal, not thirty seconds of silence.
  use id <- result.try(
    json.parse(line, decode.at(["id"], decode.string))
    |> result.map_error(fn(_) { "the request requires a string id" }),
  )
  let reply = case json.parse(line, ask_decoder()) {
    Ok(ask) -> #("answer", asked(ask))
    Error(_) -> #(
      "error",
      json.object([
        #("kind", json.string("invalid_request")),
        #("detail", json.string("the decision could not be read")),
      ]),
    )
  }
  Ok(
    json.to_string(
      json.object([
        #("id", json.string(id)),
        reply,
      ]),
    ),
  )
}

type Ask {
  Choose(
    runnable: List(CellDemand),
    sites: List(SiteOffer),
    busy: List(#(Int, Int)),
  )
  ReadOffer(site_id: Int, name: String, capabilities: String, capacity: String)
  ReadDemand(
    cell_id: Int,
    study_id: Int,
    ordinal: Int,
    name: String,
    config: String,
    requirements: String,
    source_id: Int,
  )
  Admits(
    admitted_models: List(String),
    allowed_handlers: List(String),
    cell_id: String,
    model: String,
    stage_handler: String,
  )
  Resolve(cells: List(PlaceableCell), sites: List(PlaceableSite))
  KnownModels
  RetryCell(retry.Observation)
}

fn asked(ask: Ask) -> json.Json {
  case ask {
    Choose(runnable, sites, busy) ->
      decision.choose(runnable, sites, dict.from_list(busy)) |> decision_json
    ReadOffer(site_id, name, capabilities, capacity) ->
      decision.read_offer(site_id, name, capabilities, capacity)
      |> site_json
    ReadDemand(
      cell_id,
      study_id,
      ordinal,
      name,
      config,
      requirements,
      source_id,
    ) ->
      decision.read_demand(
        cell_id,
        study_id,
        ordinal,
        name,
        config,
        requirements,
        source_id,
      )
      |> cell_json
    Admits(admitted_models, allowed_handlers, cell_id, model, stage_handler) ->
      admits_json(placement.admits(
        admitted_models,
        allowed_handlers,
        cell_id,
        model,
        stage_handler,
      ))
    Resolve(cells, sites) -> resolve_json(placement.resolve(cells, sites))
    KnownModels ->
      json.object([
        #("models", json.array(placement.known_model_types, json.string)),
      ])
    RetryCell(observed) -> retry.decide(observed) |> retry_json
  }
}

fn ask_decoder() -> decode.Decoder(Ask) {
  use ask <- decode.field("ask", decode.string)
  case ask {
    "choose" -> {
      use runnable <- decode.field("runnable", decode.list(cell_decoder()))
      use sites <- decode.field("sites", decode.list(site_decoder()))
      use busy <- decode.field("busy", decode.list(busy_decoder()))
      decode.success(Choose(runnable, sites, busy))
    }
    "read_offer" -> {
      use site_id <- decode.field("site_id", decode.int)
      use name <- decode.field("name", decode.string)
      use capabilities <- decode.field("capabilities", decode.string)
      use capacity <- decode.field("capacity", decode.string)
      decode.success(ReadOffer(site_id, name, capabilities, capacity))
    }
    "read_demand" -> {
      use cell_id <- decode.field("cell_id", decode.int)
      use study_id <- decode.field("study_id", decode.int)
      use ordinal <- decode.field("ordinal", decode.int)
      use name <- decode.field("name", decode.string)
      use config <- decode.field("config", decode.string)
      use requirements <- decode.field("requirements", decode.string)
      use source_id <- decode.field("source_id", decode.int)
      decode.success(ReadDemand(
        cell_id,
        study_id,
        ordinal,
        name,
        config,
        requirements,
        source_id,
      ))
    }
    "admits" -> {
      use admitted_models <- decode.field(
        "admitted_models",
        decode.list(decode.string),
      )
      use allowed_handlers <- decode.field(
        "allowed_handlers",
        decode.list(decode.string),
      )
      use cell_id <- decode.field("cell_id", decode.string)
      use model <- decode.field("model", decode.string)
      use stage_handler <- decode.field("stage_handler", decode.string)
      decode.success(Admits(
        admitted_models,
        allowed_handlers,
        cell_id,
        model,
        stage_handler,
      ))
    }
    "resolve" -> {
      use cells <- decode.field("cells", decode.list(placeable_cell()))
      use sites <- decode.field("sites", decode.list(placeable_site()))
      decode.success(Resolve(cells, sites))
    }
    "known_models" -> decode.success(KnownModels)
    "retry_cell" -> {
      use study <- decode.field("study_state", decode.string)
      use cell <- decode.field("cell", decode.optional(retry_cell_decoder()))
      use silent <- decode.field("silent_ticks", decode.optional(decode.int))
      use stale <- decode.field("stale_ticks", decode.int)
      use force <- decode.field("force", decode.bool)
      decode.success(
        RetryCell(retry.Observation(
          retry.study_state(study),
          cell,
          silent,
          stale,
          force,
        )),
      )
    }
    _ ->
      decode.failure(
        Choose([], [], []),
        "choose, read_offer, read_demand, admits, resolve, known_models or retry_cell",
      )
  }
}

fn retry_cell_decoder() -> decode.Decoder(retry.Cell) {
  use accepted <- decode.field("accepted", decode.optional(decode.int))
  use latest <- decode.field("latest", decode.optional(decode.int))
  use state <- decode.field("state", decode.string)
  decode.success(retry.Cell(accepted, latest, retry.attempt_state(state)))
}

fn retry_json(decision: retry.Decision) -> json.Json {
  case decision {
    // CHG 2026-09-27: one word says what the retry closes, where two
    // booleans (`close_open`, `forced`) could contradict each other.
    retry.AllowedAfterEnd -> allowed("none")
    retry.AllowedClosingSilent -> allowed("silent")
    retry.AllowedClosingForced -> allowed("forced")
    retry.Refused(reason) -> {
      let word = case reason {
        retry.CancelledStudy -> "cancelled_study"
        retry.MissingCell -> "missing_cell"
        retry.AcceptedCell -> "accepted_cell"
        retry.UnattemptedCell -> "unattempted_cell"
        retry.RecentProgress -> "recent_progress"
        retry.UnknownSilence -> "unknown_silence"
      }
      json.object([
        #("kind", json.string("refused")),
        #("reason", json.string(word)),
      ])
    }
  }
}

fn allowed(closing: String) -> json.Json {
  json.object([
    #("kind", json.string("allowed")),
    #("closing", json.string(closing)),
  ])
}

fn cell_decoder() -> decode.Decoder(CellDemand) {
  use cell_id <- decode.field("cell_id", decode.int)
  use study_id <- decode.field("study_id", decode.int)
  use ordinal <- decode.field("ordinal", decode.int)
  use name <- decode.field("name", decode.string)
  use model_type <- decode.field("model_type", decode.string)
  use device <- decode.field("device", decode.string)
  use requires_cuda <- decode.field("requires_cuda", decode.bool)
  use source_id <- decode.field("source_id", decode.int)
  decode.success(CellDemand(
    cell_id,
    study_id,
    ordinal,
    name,
    model_type,
    device,
    requires_cuda,
    source_id,
  ))
}

fn site_decoder() -> decode.Decoder(SiteOffer) {
  use site_id <- decode.field("site_id", decode.int)
  use name <- decode.field("name", decode.string)
  use admitted_models <- decode.field(
    "admitted_models",
    decode.list(decode.string),
  )
  use device <- decode.field("device", decode.string)
  use slots <- decode.field("slots", decode.int)
  decode.success(SiteOffer(site_id, name, admitted_models, device, slots))
}

fn busy_decoder() -> decode.Decoder(#(Int, Int)) {
  use site_id <- decode.field("site_id", decode.int)
  use count <- decode.field("count", decode.int)
  decode.success(#(site_id, count))
}

fn decision_json(decision: Decision) -> json.Json {
  json.object([
    #("placements", json.array(decision.placements, placement_json)),
    #("unplaced", json.array(decision.unplaced, unplaced_json)),
  ])
}

fn placement_json(placement: Placement) -> json.Json {
  json.object([
    #("cell_id", json.int(placement.cell_id)),
    #("site_id", json.int(placement.site_id)),
    #("because", json.string(placement.because)),
  ])
}

fn unplaced_json(unplaced: Unplaced) -> json.Json {
  json.object([
    #("cell_id", json.int(unplaced.cell_id)),
    #("because", json.string(unplaced.because)),
  ])
}

fn site_json(site: SiteOffer) -> json.Json {
  json.object([
    #("site_id", json.int(site.site_id)),
    #("name", json.string(site.name)),
    #("admitted_models", json.array(site.admitted_models, json.string)),
    #("device", json.string(site.device)),
    #("slots", json.int(site.slots)),
  ])
}

fn placeable_cell() -> decode.Decoder(PlaceableCell) {
  use cell_id <- decode.field("cell_id", decode.string)
  use model <- decode.field("model", decode.string)
  use stage_handler <- decode.field("stage_handler", decode.string)
  decode.success(PlaceableCell(cell_id, model, stage_handler))
}

fn placeable_site() -> decode.Decoder(PlaceableSite) {
  use site_id <- decode.field("site_id", decode.string)
  use admitted_models <- decode.field(
    "admitted_models",
    decode.list(decode.string),
  )
  use allowed_handlers <- decode.field(
    "allowed_handlers",
    decode.list(decode.string),
  )
  decode.success(PlaceableSite(site_id, admitted_models, allowed_handlers))
}

fn admits_json(refusal: option.Option(Refusal)) -> json.Json {
  case refusal {
    option.None -> json.object([#("admitted", json.bool(True))])
    option.Some(refusal) ->
      json.object([
        #("admitted", json.bool(False)),
        #("cell_id", json.string(refusal.cell_id)),
        #("model", json.string(refusal.model)),
        #("stage_handler", json.string(refusal.handler)),
        #("detail", json.string(placement.detail(refusal))),
      ])
  }
}

fn resolve_json(
  resolved: Result(List(#(String, List(String))), CompileRefusal),
) -> json.Json {
  case resolved {
    Ok(pairs) ->
      json.object([
        #("ok", json.bool(True)),
        #(
          "sites_by_cell",
          json.array(pairs, fn(pair: #(String, List(String))) {
            json.object([
              #("cell_id", json.string(pair.0)),
              #("sites", json.array(pair.1, json.string)),
            ])
          }),
        ),
      ])
    Error(CompileRefusal(detail)) ->
      json.object([
        #("ok", json.bool(False)),
        #("outcome_kind", json.string("refused")),
        #("error_category", json.string("invalid_request")),
        #("detail", json.string(detail)),
      ])
  }
}

fn cell_json(cell: CellDemand) -> json.Json {
  json.object([
    #("cell_id", json.int(cell.cell_id)),
    #("study_id", json.int(cell.study_id)),
    #("ordinal", json.int(cell.ordinal)),
    #("name", json.string(cell.name)),
    #("model_type", json.string(cell.model_type)),
    #("device", json.string(cell.device)),
    #("requires_cuda", json.bool(cell.requires_cuda)),
    #("source_id", json.int(cell.source_id)),
  ])
}
