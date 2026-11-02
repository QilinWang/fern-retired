import fern_scheduler/placement.{
  type PlaceableCell, type PlaceableSite, AdmittedModels, AllowedHandlers,
  CompileRefusal, PlaceableCell, PlaceableSite, Refusal,
}
import gleam/list
import gleam/option.{None, Some}
import gleeunit

pub fn main() -> Nil {
  gleeunit.main()
}

fn cell(id: String, model: String) -> PlaceableCell {
  PlaceableCell(id, model, "fern.train")
}

fn site(id: String, models: List(String)) -> PlaceableSite {
  PlaceableSite(id, models, ["fern.train"])
}

pub fn an_empty_model_list_admits_nothing_test() {
  let refusal =
    placement.admits([], ["fern.train"], "cell-0", "dlinear", "fern.train")
  assert refusal
    == Some(Refusal("cell-0", "dlinear", "fern.train", AdmittedModels, []))
  assert placement.detail(
      Refusal("cell-0", "dlinear", "fern.train", AdmittedModels, []),
    )
    == "cell cell-0 is model dlinear; the rule admits no models at all"
}

pub fn a_named_model_with_the_handler_is_no_refusal_test() {
  assert placement.admits(
      ["zeus", "dlinear"],
      ["fern.train"],
      "cell-0",
      "dlinear",
      "fern.train",
    )
    == None
}

pub fn the_refusal_lists_the_rule_in_ordinal_order_test() {
  let refusal =
    placement.admits(
      ["zeus", "dlinear"],
      ["fern.train"],
      "cell-7",
      "fern",
      "fern.train",
    )
  assert refusal
    == Some(
      Refusal("cell-7", "fern", "fern.train", AdmittedModels, [
        "zeus",
        "dlinear",
      ]),
    )
  assert placement.detail(
      Refusal("cell-7", "fern", "fern.train", AdmittedModels, [
        "zeus",
        "dlinear",
      ]),
    )
    == "cell cell-7 is model fern; the rule admits models dlinear, zeus"
}

pub fn the_handler_half_is_its_own_reason_test() {
  assert placement.admits(["dlinear"], [], "cell-0", "dlinear", "fern.train")
    == Some(Refusal("cell-0", "dlinear", "fern.train", AllowedHandlers, []))
}

pub fn a_model_outside_the_known_set_is_still_admitted_when_the_rule_names_it_test() {
  assert placement.known_model("dlinar") == False
  assert placement.known_model("fern") == True
  assert placement.admits(
      ["dlinar"],
      ["fern.train"],
      "cell-0",
      "dlinar",
      "fern.train",
    )
    == None
}

pub fn the_first_unplaceable_cell_refuses_the_compile_and_a_later_one_is_not_named_test() {
  let assert Error(CompileRefusal(detail)) =
    placement.resolve([cell("cell-0", "fern"), cell("cell-1", "also-fern")], [
      site("local", ["dlinear"]),
    ])
  assert detail
    == "cell cell-0 has no admissible Worker Site: cell cell-0 is model fern; the rule admits models dlinear"
}

pub fn sites_come_out_in_id_order_whichever_way_they_were_given_test() {
  let forward =
    placement.resolve([cell("cell-0", "dlinear")], [
      site("runpod-a4500", ["dlinear"]),
      site("local-4070", ["dlinear"]),
    ])
  let reversed =
    placement.resolve([cell("cell-0", "dlinear")], [
      site("local-4070", ["dlinear"]),
      site("runpod-a4500", ["dlinear"]),
    ])
  let expected = Ok([#("cell-0", ["local-4070", "runpod-a4500"])])
  assert forward == expected
  assert reversed == expected
}

pub fn no_enabled_site_names_that_and_not_a_rule_test() {
  let assert Error(CompileRefusal(detail)) =
    placement.resolve([cell("cell-0", "dlinear")], [])
  assert detail
    == "cell cell-0 has no admissible Worker Site: no Worker Site is enabled"
}

pub fn a_plan_with_no_cells_is_refused_before_any_site_is_asked_test() {
  let assert Error(CompileRefusal(detail)) =
    placement.resolve([], [site("local", ["fern"])])
  assert detail == "a compiled Plan has no cells to place"
}

pub fn equal_site_ids_keep_the_order_they_were_given_test() {
  let sites = [
    PlaceableSite("same", ["dlinear"], ["fern.train"]),
    PlaceableSite("same", ["dlinear"], ["fern.train"]),
  ]
  let assert Ok(found) = placement.resolve([cell("cell-0", "dlinear")], sites)
  assert found == [#("cell-0", ["same", "same"])]
}

pub fn a_repeated_cell_keeps_the_later_answer_test() {
  let assert Ok(found) =
    placement.resolve(
      [cell("cell-0", "dlinear"), PlaceableCell("cell-0", "zeus", "fern.train")],
      [
        site("local", ["dlinear", "zeus"]),
        PlaceableSite("pod", ["zeus"], ["fern.train"]),
      ],
    )
  assert list.length(found) == 1
  assert found == [#("cell-0", ["local", "pod"])]
}
