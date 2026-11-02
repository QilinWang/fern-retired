//// Which Sites may run a cell. This is admissibility, not selection: a cell
//// is admitted when the Site's rule names its model and its one Stage
//// handler. Empty admits nothing, on both lists.
////
//// A cell has one handler. A list of handlers would be a different question.

import gleam/int
import gleam/list
import gleam/option.{type Option, None, Some}
import gleam/order
import gleam/string

/// Why a Site's rule does not admit a cell. Absence is `None`: the Site may
/// run the cell. The reason names the cell and the rule, never the Site.
///
/// CHG 2026-09-27: one record naming the cell, its model and its handler,
/// with the rule that refused it and what that rule declares. The two
/// constructors this replaced (`ModelRefused`, `HandlerRefused`) repeated
/// the three shared fields, and the wire needed a function per field to
/// read them across both.
pub type Refusal {
  Refusal(
    cell_id: String,
    model: String,
    handler: String,
    rule: Rule,
    declared: List(String),
  )
}

/// Which half of a Site's rule refused: the models it admits, or the Stage
/// handlers it allows. `declared` on the refusal is that half's list.
pub type Rule {
  AdmittedModels
  AllowedHandlers
}

pub type PlaceableCell {
  PlaceableCell(cell_id: String, model: String, stage_handler: String)
}

pub type PlaceableSite {
  PlaceableSite(
    site_id: String,
    admitted_models: List(String),
    allowed_handlers: List(String),
  )
}

/// The whole compile was refused. The detail is the sentence a person reads.
pub type CompileRefusal {
  CompileRefusal(detail: String)
}

pub fn admits(
  admitted_models: List(String),
  allowed_handlers: List(String),
  cell_id: String,
  model: String,
  stage_handler: String,
) -> Option(Refusal) {
  case list.contains(admitted_models, model) {
    False ->
      Some(Refusal(
        cell_id,
        model,
        stage_handler,
        AdmittedModels,
        admitted_models,
      ))
    True ->
      case list.contains(allowed_handlers, stage_handler) {
        False ->
          Some(Refusal(
            cell_id,
            model,
            stage_handler,
            AllowedHandlers,
            allowed_handlers,
          ))
        True -> None
      }
  }
}

pub fn detail(refusal: Refusal) -> String {
  case refusal.rule {
    AdmittedModels ->
      "cell "
      <> refusal.cell_id
      <> " is model "
      <> refusal.model
      <> "; the rule admits "
      <> declared_as("models", "no models at all", refusal.declared)
    AllowedHandlers ->
      "cell "
      <> refusal.cell_id
      <> " needs Stage handler "
      <> refusal.handler
      <> "; the rule allows "
      <> declared_as("handlers", "no handlers at all", refusal.declared)
  }
}

/// Ordinal order, the same order `StringComparer.Ordinal` uses for these names.
fn declared_as(kind: String, empty: String, names: List(String)) -> String {
  case names {
    [] -> empty
    _ -> kind <> " " <> string.join(list.sort(names, string.compare), ", ")
  }
}

/// Every cell against every enabled Site, or the first cell none may run.
///
/// Sites are visited in Site-id order, ties broken by the order they were
/// given, so two compiles of the same Plan list the same Sites. Cells are
/// visited in the order given: the first with nowhere to go refuses the
/// whole compile, and a later one is not consulted.
pub fn resolve(
  cells: List(PlaceableCell),
  enabled_sites: List(PlaceableSite),
) -> Result(List(#(String, List(String))), CompileRefusal) {
  case cells {
    [] -> Error(CompileRefusal("a compiled Plan has no cells to place"))
    _ -> walk(cells, by_site_id(enabled_sites), [])
  }
}

fn walk(
  cells: List(PlaceableCell),
  sites: List(PlaceableSite),
  found: List(#(String, List(String))),
) -> Result(List(#(String, List(String))), CompileRefusal) {
  case cells {
    [] -> Ok(last_wins(list.reverse(found)))
    [cell, ..rest] -> {
      let #(admissible, refusals) = classify(cell, sites, [], [])
      case admissible {
        [] -> Error(CompileRefusal(unplaceable(cell.cell_id, refusals)))
        _ -> walk(rest, sites, [#(cell.cell_id, admissible), ..found])
      }
    }
  }
}

/// A repeated cell id keeps the later answer, as a dictionary does.
fn last_wins(
  pairs: List(#(String, List(String))),
) -> List(#(String, List(String))) {
  list.fold(pairs, [], fn(kept, pair) {
    list.append(
      list.filter(kept, fn(earlier: #(String, List(String))) {
        earlier.0 != pair.0
      }),
      [pair],
    )
  })
}

fn unplaceable(cell_id: String, refusals: List(String)) -> String {
  let why = case refusals {
    [] -> "no Worker Site is enabled"
    _ -> string.join(refusals, "; ")
  }
  "cell " <> cell_id <> " has no admissible Worker Site: " <> why
}

fn classify(
  cell: PlaceableCell,
  sites: List(PlaceableSite),
  admissible: List(String),
  refusals: List(String),
) -> #(List(String), List(String)) {
  case sites {
    [] -> #(list.reverse(admissible), list.reverse(refusals))
    [site, ..rest] ->
      case
        admits(
          site.admitted_models,
          site.allowed_handlers,
          cell.cell_id,
          cell.model,
          cell.stage_handler,
        )
      {
        None -> classify(cell, rest, [site.site_id, ..admissible], refusals)
        Some(refusal) ->
          classify(cell, rest, admissible, [detail(refusal), ..refusals])
      }
  }
}

/// Stable: equal Site ids keep the order they were given, as `OrderBy` does.
fn by_site_id(sites: List(PlaceableSite)) -> List(PlaceableSite) {
  sites
  |> list.index_map(fn(site, index) { #(site, index) })
  |> list.sort(fn(left, right) {
    case string.compare(left.0.site_id, right.0.site_id) {
      order.Eq -> int.compare(left.1, right.1)
      compared -> compared
    }
  })
  |> list.map(fn(pair) { pair.0 })
}

/// The model names `Placement.KnownModelTypes` lists. Membership is the
/// question; a Site's rule, not this set, decides whether a cell is admitted.
pub const known_model_types: List(String) = [
  "action_state_lstm",
  "action_state_gru",
  "vit_predictor",
  "visual_dynamics",
  "state_probe",
  "conditional_nsf",
  // CHG 2026-09-25: the five names C#'s KnownModelTypes gained on 2026-09-25
  // (commits d5d68be1b and 25cf28f50) without this list.
  "conditional_gaussian",
  "conditional_profiti",
  "conditional_tactis2",
  "odd_residual",
  "conditional_realnvp",
  "conditional_mdn",
  "conditional_ddpm",
  "conditional_ddim",
  "conditional_rectified_flow",
  "attraos",
  "chronos2",
  "chronos_bolt_base",
  "chronos_bolt_small",
  "crossformer",
  "diffusion_policy",
  "dlinear",
  "duet",
  "fern",
  "film",
  "fits",
  "frets",
  "hi",
  "itransformer",
  "koopa",
  "lightts",
  "moderntcn",
  "mtsmixer",
  "naive",
  "patchtst",
  "pfnn",
  "residual_mlp",
  "robot_dynamics_diffusion",
  "mac_chunk_mlp",
  "segrnn",
  "softs",
  "sparsetsf",
  "stemgnn",
  "tide",
  "timemixer",
  "timesfm_2p5_200m",
  "timesnet",
  "timexer",
  "tirex",
  "zeus",
]

pub fn known_model(model: String) -> Bool {
  list.contains(known_model_types, model)
}
