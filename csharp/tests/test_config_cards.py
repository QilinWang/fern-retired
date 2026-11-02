"""One named configuration, checked changes, and frozen replay before training.

The card compiler is tested separately from the training chain: every resolved
document still goes through the real configuration reader, but no model, GPU,
Registry, or training process is needed.
"""

from __future__ import annotations

import copy
import ast
import json
import subprocess
from pathlib import Path
from typing import Any

import pytest
from fern_train import config_cards as cards
from fern_train.wire.training_v2 import LatestLags

FERN_EXAMPLE = Path(__file__).resolve().parents[1] / "tests/fixtures/training-configurations/v2/valid/fern.json"


@pytest.mark.parametrize("card,channels,lags", [
    ("fern.spd_pe_v4", 3, [0]),
    ("fern.spd_pe_v4_tde8", 3, list(range(8))),
    ("fern.spd_pe_v4_single_channel", 1, None),
])
def test_spd_pe_v4_declares_native_radial_law_and_sample_objectives(card, channels, lags):
    from fern_train.config import ResolvedTrainingConfig
    selection = cards.select_config(card)
    context = cards.ConfigContext(192, 192, channels, "none", "cuda")
    spec, _ = cards.resolve_config(selection, [], context)
    cfg = ResolvedTrainingConfig.from_spec(spec)
    knobs = spec["model"]["knobs"]
    assert spec["action"] == {"kind": "state_only"}
    previous, _ = cards.resolve_config(cards.select_config("fern.spd_pe_v2_reparameterised_three_components"), [], context)
    for name in ("component_distributions", "mixture_component_count", "output_source",
                 "hidden_blocks", "innovation_radius", "output_pre_rotation"):
        assert knobs[name] == previous["model"]["knobs"][name]
    assert cfg.knobs.channel_basis is None
    assert knobs["mixture_component_count"] == len(knobs["component_distributions"]) == 3
    assert (None if cfg.knobs.channel_history is None else knobs["channel_history"]["auxiliary_lags"]) == (
        None if lags is None else {"kind": "latest", "count": len(lags)})
    assert (None if cfg.knobs.channel_history is None else cfg.knobs.channel_history.auxiliary_lags) == (
        None if lags is None else LatestLags(count=len(lags)))
    assert knobs["num_train_samples"] == spec["loss"]["probability"]["num_train_samples"] == 32
    assert knobs["num_eval_samples"] == spec["loss"]["probability"]["num_eval_samples"] == 32
    assert knobs["sampling_gradient"] == "reparameterisation"
    assert cfg.knobs.radial_spline.coordinate.value == "standardized_squared_radius"
    assert cfg.knobs.radial_spline.num_bins == 8
    assert cfg.knobs.radial_spline.tail_power == 1
    assert [t["name"] for t in spec["loss"]["train"] if t["role"] == "active"] == ["huber"]
    assert [t["name"] for t in spec["loss"]["eval"] if t["role"] == "active"] == ["crps"]


def test_default_builder_has_no_python_definition_or_csharp_dispatch():
    """No compatibility parser may keep a second configuration author alive."""
    root = Path(__file__).resolve().parents[1]
    retired_names = {
        "_build_config_request_from_legacy_args", "run_fern_experiment_json",
        "build_config_spec_from_host", "build_default_config_spec", "build_cfg",
    }
    for relative in ("python/fern_train/worker.py", "python/fern_train/zero_shot.py"):
        tree = ast.parse((root / relative).read_text())
        functions = {node.name for node in ast.walk(tree) if isinstance(node, ast.FunctionDef)}
        assert not functions.intersection(retired_names), relative
    dispatch = (root / "src/csharp/Fern.Experiment.Cli/Program.cs").read_text()
    assert "ConfigBuildCli.Execute" not in dispatch
    assert "ConfigOverlayCli.Execute" not in dispatch
    # CHG 2026-09-11: plan-run and show-config-identity retired with the C# training
    # schema (607c08f1); the Go compiler mints identities and no C# verb reads a
    # scientific configuration, so neither dispatch word may return.
    assert "PlanRunCli.Execute" not in dispatch
    assert "ConfigIdentityCli.Execute" not in dispatch


def test_retired_default_builder_refuses_at_the_real_command_boundary():
    """The current executable must not retain the removed JSON operation."""
    from fern_train.worker import fern_experiment_cmd

    result = subprocess.run(
        [*fern_experiment_cmd(), "build-config"], input="{}", text=True,
        capture_output=True, check=False,
    )
    assert result.returncode == 2
    assert result.stdout == ""
    assert "usage: fern-experiment" in result.stderr
    assert "build-config" not in result.stderr


def test_every_catalog_card_resolves_through_the_real_reader():
    catalog = json.loads(cards.CONFIGS_FILE.read_text())
    for name in catalog["cards"]:
        selection = cards.select_config(name)
        horizon = {"fern.fixed_patch_24": 96, "fern.fixed_patch_25": 50}.get(name, 64)
        resolved, source = cards.resolve_config(selection, [], cards.ConfigContext(336, horizon, 2, "none", "cuda"))
        assert resolved["task"]["pred_len"] == horizon, name
        assert source["selection"]["name"] == name


def test_fixed_patch_25_derives_g_from_horizon_over_p():
    selection = cards.select_config("fern.fixed_patch_25")
    short, _ = cards.resolve_config(
        selection, [], cards.ConfigContext(25, 25, 4, "robust_mad", "cuda")
    )
    assert short["model"]["knobs"]["Brenier_patch_spec"] == {"G": 1, "L": 25, "P": 25}
    assert short["model"]["knobs"]["Coupling_patch_spec"] == {"G": 1, "L": 25, "P": 25}
    assert short["model"]["knobs"]["block_size"] == 25
    long, _ = cards.resolve_config(
        selection, [], cards.ConfigContext(25, 50, 4, "robust_mad", "cuda")
    )
    assert long["model"]["knobs"]["Brenier_patch_spec"] == {"G": 2, "L": 50, "P": 25}
    assert long["model"]["knobs"]["Coupling_patch_spec"] == {"G": 2, "L": 50, "P": 25}
    with pytest.raises(ValueError, match="P must be a positive integer dividing L"):
        cards.resolve_config(
            selection, [], cards.ConfigContext(24, 24, 4, "robust_mad", "cuda")
        )


@pytest.fixture
def fern_card() -> dict[str, Any]:
    config = json.loads(FERN_EXAMPLE.read_text(encoding="utf-8"))
    # A fixed-P card owns P. Only the window's L and G = L/P are outputs;
    # a separately declared automatic-P card can give that choice to a rule.
    config["model"]["knobs"]["Coupling_patch_spec"]["P"] = 96
    derived = [
        "task.seq_len",
        "task.pred_len",
        "task.channels",
        "data.normalization",
        "model.knobs.Brenier_patch_spec.L",
        "model.knobs.Brenier_patch_spec.G",
        "model.knobs.Coupling_patch_spec.L",
        "model.knobs.Coupling_patch_spec.G",
    ]
    for field in derived:
        parts = field.split(".")
        parent = config
        for part in parts[:-1]:
            parent = parent[part]
        del parent[parts[-1]]
    return {
        "alias": "default",
        "config": config,
        "derived": derived,
        "annotations": [
            {
                "fields": ["model.knobs.dim_hidden"],
                "description": "This reference fixes the hidden dimension at 128 for capacity comparisons.",
            },
            {
                "fields": ["model.knobs.Brenier_patch_spec.P", "model.knobs.Brenier_patch_spec.G"],
                "description": "P is an explicit patch length; the window rule computes G from L/P.",
            },
        ],
    }


@pytest.fixture
def context() -> cards.ConfigContext:
    return cards.ConfigContext(history=336, horizon=96, channels=2, normalization="none", device="cpu")


@pytest.fixture
def selection(tmp_path: Path, fern_card: dict[str, Any]) -> dict[str, Any]:
    catalog = tmp_path / "configs.json"
    catalog.write_text(json.dumps({"cards": {"fern.reference": fern_card}}), encoding="utf-8")
    return cards.select_config("fern.default", path=catalog)


def test_checked_change_retains_exact_values_and_reason() -> None:
    changes = [{"field": "model.knobs.dim_hidden", "from": 128, "to": 256, "reason": "Compare hidden capacity."}]
    assert cards.checked_changes(changes) == changes
    assert cards.CheckedChange.__struct_defaults__ == ()
    assert cards.ConfigContext.__struct_defaults__ == ()
    assert cards.ConfigAnnotation.__struct_defaults__ == ()


@pytest.mark.parametrize("missing", ["field", "from", "to", "reason"])
def test_every_checked_change_field_is_required(missing: str) -> None:
    change = {"field": "train.batch_size", "from": 95, "to": 128, "reason": "Compare batch size."}
    del change[missing]
    with pytest.raises(ValueError, match=missing):
        cards.checked_changes([change])


@pytest.mark.parametrize("bad_reason", ["", " \t\n", None, 1])
def test_change_requires_a_real_experimental_reason(bad_reason: Any) -> None:
    with pytest.raises(ValueError, match="reason"):
        cards.checked_changes([{"field": "train.batch_size", "from": 95, "to": 128, "reason": bad_reason}])


@pytest.mark.parametrize("field", ["", ".train", "train.", "train..epochs", "loss.train[0].weight", "model.*", " train.epochs"])
def test_changes_refuse_non_field_path_syntax(field: str) -> None:
    with pytest.raises(ValueError, match="field"):
        cards.checked_changes([{"field": field, "from": 1, "to": 2, "reason": "Check field spelling."}])


def test_change_does_not_accept_another_edit_language() -> None:
    with pytest.raises(ValueError, match="unknown field"):
        cards.checked_changes([{"field": "train.epochs", "from": 1, "to": 2, "reason": "Compare duration.", "op": "replace"}])


@pytest.mark.parametrize("second_field", ["model.knobs.dim_hidden", "model.knobs", "model"])
@pytest.mark.parametrize("reverse", [False, True])
def test_duplicate_or_ancestor_changes_refuse_in_both_orders(second_field: str, reverse: bool) -> None:
    changes = [
        {"field": "model.knobs.dim_hidden", "from": 128, "to": 256, "reason": "Compare capacity."},
        {"field": second_field, "from": 256, "to": 512, "reason": "A second assignment must not win."},
    ]
    with pytest.raises(ValueError, match="overlapping"):
        cards.checked_changes(changes[::-1] if reverse else changes)


def test_disjoint_changes_are_order_independent_and_leave_inputs_untouched() -> None:
    base = {"model": {"knobs": {"dim_hidden": 128}}, "train": {"batch_size": 95}}
    changes = [
        {"field": "train.batch_size", "from": 95, "to": 128, "reason": "Compare batch size."},
        {"field": "model.knobs.dim_hidden", "from": 128, "to": 256, "reason": "Compare capacity."},
    ]
    before = copy.deepcopy((base, changes))
    forward = cards.apply_changes(base, changes)
    backward = cards.apply_changes(base, changes[::-1])
    assert forward == backward == {"model": {"knobs": {"dim_hidden": 256}}, "train": {"batch_size": 128}}
    assert cards.checked_changes(changes) == cards.checked_changes(changes[::-1])
    assert (base, changes) == before


def test_a_later_failure_cannot_partially_apply_an_earlier_change() -> None:
    base = {"model": {"knobs": {"dim_hidden": 128}}, "train": {"batch_size": 95}}
    changes = [
        {"field": "model.knobs.dim_hidden", "from": 128, "to": 256, "reason": "Compare capacity."},
        {"field": "train.batch_size", "from": 64, "to": 128, "reason": "Stale batch-size assumption."},
    ]
    before = copy.deepcopy((base, changes))
    with pytest.raises(ValueError, match="expected from=64, found 95"):
        cards.apply_changes(base, changes)
    assert (base, changes) == before


@pytest.mark.parametrize("current,expected", [(True, 1), (1, True), (False, 0), (0, False), (128, "128")])
def test_from_compares_typed_json_values_not_python_loose_equality(current: Any, expected: Any) -> None:
    with pytest.raises(ValueError, match="expected from"):
        cards.apply_changes({"value": current}, [{"field": "value", "from": expected, "to": 256, "reason": "Require the exact prior value."}])


def test_already_applied_target_is_not_silently_accepted() -> None:
    change = {"field": "dim_hidden", "from": 128, "to": 256, "reason": "Compare against the 128 reference."}
    with pytest.raises(ValueError, match="expected from=128, found 256"):
        cards.apply_changes({"dim_hidden": 256}, [change])


def test_missing_field_is_not_created_by_a_change() -> None:
    with pytest.raises(ValueError, match="does not exist"):
        cards.apply_changes({"model": {"knobs": {}}}, [{"field": "model.knobs.hidden_typo", "from": None, "to": 128, "reason": "A misspelled field is not an extension."}])


@pytest.mark.parametrize("value", [float("nan"), float("inf"), float("-inf")])
def test_nonfinite_replacement_is_refused_without_mutation(value: float) -> None:
    base = {"value": 1.0}
    with pytest.raises(ValueError):
        cards.apply_changes(base, [{"field": "value", "from": 1.0, "to": value, "reason": "The result must be valid JSON."}])
    assert base == {"value": 1.0}


def test_explicit_alias_records_requested_and_canonical_names(selection: dict[str, Any], tmp_path: Path) -> None:
    assert selection["selected"] == "fern.default"
    assert selection["name"] == "fern.reference"
    assert "sha256" not in selection  # CHG 2026-09-26: a selection records the card, no digest
    explicit = cards.select_config("fern.reference", path=tmp_path / "configs.json")
    assert explicit["card"] == selection["card"]
    with pytest.raises(ValueError, match="unknown configuration card"):
        cards.select_config("fern", path=tmp_path / "configs.json")


def test_frozen_selection_replays_after_alias_and_card_content_change(
    tmp_path: Path, fern_card: dict[str, Any], selection: dict[str, Any], context: cards.ConfigContext
) -> None:
    original = copy.deepcopy(selection)
    old_resolved, old_trace = cards.resolve_config(selection, [], context)
    changed_reference = copy.deepcopy(fern_card)
    changed_reference.pop("alias")
    changed_reference["config"]["model"]["knobs"]["dim_hidden"] = 192
    new_default = copy.deepcopy(fern_card)
    new_default["config"]["model"]["knobs"]["dim_hidden"] = 256
    catalog = tmp_path / "configs.json"
    catalog.write_text(json.dumps({"cards": {"fern.reference": changed_reference, "fern.larger": new_default}}), encoding="utf-8")
    moved = cards.select_config("fern.default", path=catalog)
    assert moved["name"] == "fern.larger"
    assert cards.card_differences(original["card"], moved["card"]) == [("config.model.knobs.dim_hidden", 128, 256)]
    assert cards.card_differences(original["card"], cards.select_config("fern.reference", path=catalog)["card"]) == [
        ("alias", "default", cards.ABSENT), ("config.model.knobs.dim_hidden", 128, 192)]
    catalog.unlink()
    assert cards.verify_selection(selection, selected="fern.default") == original
    assert cards.resolve_config(selection, [], context) == (old_resolved, old_trace)
    assert old_resolved["model"]["knobs"]["dim_hidden"] == 128
    assert cards.resolve_config(moved, [], context)[0]["model"]["knobs"]["dim_hidden"] == 256
    assert selection == original


def test_a_frozen_snapshot_is_the_record_and_a_difference_is_named(selection: dict[str, Any], tmp_path: Path) -> None:
    # CHG 2026-09-26: no digest guards the snapshot; it is replayed as written,
    # never re-resolved through its alias, and what it says differently from the
    # catalogue is named field by field.
    selection["card"]["config"]["model"]["knobs"]["dim_hidden"] = 256
    replayed = cards.verify_selection(selection, selected="fern.default")
    assert replayed["card"]["config"]["model"]["knobs"]["dim_hidden"] == 256
    catalogue = cards.select_config("fern.default", path=tmp_path / "configs.json")
    assert cards.card_differences(catalogue["card"], replayed["card"]) == [("config.model.knobs.dim_hidden", 128, 256)]


def test_a_selection_frozen_with_a_digest_is_read_and_the_digest_decides_nothing(selection: dict[str, Any]) -> None:
    old = {**selection, "sha256": "0" * 64}
    assert cards.verify_selection(old, selected="fern.default") == old
    assert cards.card_differences(old, selection) == []


def test_card_differences_ignore_notes_and_descriptions_and_name_fields() -> None:
    before = {"config": {"lr": 0.001, "layers": [1, 2]}, "annotations": [{"fields": ["config.lr"], "description": "old words"}],
              "notes": {"why": "old"}}
    after = {"config": {"lr": 0.001, "layers": [1, 3], "extra": True}, "annotations": [{"fields": ["config.lr"], "description": "new words"}],
             "notes": {"why": "new"}}
    assert cards.card_differences(before, after) == [("config.extra", cards.ABSENT, True), ("config.layers[1]", 2, 3)]
    assert cards.card_differences({"x": 1}, {"x": 1.0}) == [("x", 1, 1.0)]


def test_snapshot_must_match_the_requested_selection(selection: dict[str, Any]) -> None:
    with pytest.raises(ValueError, match="requested base"):
        cards.verify_selection(selection, selected="fern.another")


@pytest.mark.parametrize("collision", ["alias", "canonical"])
def test_ambiguous_aliases_are_refused(tmp_path: Path, fern_card: dict[str, Any], collision: str) -> None:
    other = copy.deepcopy(fern_card)
    name = "fern.another" if collision == "alias" else "fern.default"
    if collision == "canonical":
        other.pop("alias")
    catalog = tmp_path / "configs.json"
    catalog.write_text(json.dumps({"cards": {"fern.reference": fern_card, name: other}}), encoding="utf-8")
    with pytest.raises(ValueError, match="colliding alias"):
        cards.select_config("fern.default", path=catalog)


@pytest.mark.parametrize("key", ["presets", "base", "components"])
def test_configuration_cards_cannot_reference_more_cards(tmp_path: Path, fern_card: dict[str, Any], key: str) -> None:
    fern_card[key] = "fern.other"
    catalog = tmp_path / "configs.json"
    catalog.write_text(json.dumps({"cards": {"fern.reference": fern_card}}), encoding="utf-8")
    with pytest.raises(ValueError, match="no inheritance or nested presets"):
        cards.select_config("fern.default", path=catalog)


def test_annotation_paths_are_real_and_metadata_does_not_become_config(
    selection: dict[str, Any], context: cards.ConfigContext
) -> None:
    change = {"field": "model.knobs.dim_hidden", "from": 128, "to": 256, "reason": "Increase capacity while keeping the reference otherwise fixed."}
    resolved, trace = cards.resolve_config(selection, [change], context)
    assert resolved["model"]["knobs"]["dim_hidden"] == 256
    assert not {"annotations", "changes", "reason", "selection"} & resolved.keys()
    assert trace["changes"] == [change]
    annotated = trace["annotations"][0]
    assert annotated["description"] == selection["card"]["annotations"][0]["description"]
    assert "128" in annotated["description"]
    assert annotated["changed_fields"] == ["model.knobs.dim_hidden"]
    assert trace["annotations"][1]["changed_fields"] == []


def test_annotation_typo_refuses_at_compile(tmp_path: Path, fern_card: dict[str, Any], context: cards.ConfigContext) -> None:
    fern_card["annotations"][0]["fields"] = ["model.knobs.hidden_typo"]
    catalog = tmp_path / "configs.json"
    catalog.write_text(json.dumps({"cards": {"fern.reference": fern_card}}), encoding="utf-8")
    selection = cards.select_config("fern.default", path=catalog)
    with pytest.raises(ValueError, match="hidden_typo.*does not exist"):
        cards.resolve_config(selection, [], context)


@pytest.mark.parametrize("fields,description", [([], "Explain a field."), (["train.batch_size"], " ")])
def test_annotations_require_fields_and_an_explanation(tmp_path: Path, fern_card: dict[str, Any], fields: list[str], description: str) -> None:
    fern_card["annotations"] = [{"fields": fields, "description": description}]
    catalog = tmp_path / "configs.json"
    catalog.write_text(json.dumps({"cards": {"fern.reference": fern_card}}), encoding="utf-8")
    with pytest.raises(ValueError, match="real fields and a nonempty description"):
        cards.select_config("fern.default", path=catalog)


def test_explicit_patch_change_shows_derived_geometry_separately(selection: dict[str, Any], context: cards.ConfigContext) -> None:
    change = {"field": "model.knobs.Brenier_patch_spec.P", "from": 48, "to": 24, "reason": "Compare shorter patches at the same prediction horizon."}
    before = copy.deepcopy(selection)
    resolved, trace = cards.resolve_config(selection, [change], context)
    assert resolved["model"]["knobs"]["Brenier_patch_spec"] == {"L": 96, "P": 24, "G": 4}
    assert resolved["model"]["knobs"]["Coupling_patch_spec"] == {"L": 96, "P": 96, "G": 1}
    assert trace["changes"] == [change]
    changes_by_field = {item["field"]: item for item in trace["derived"]}
    assert changes_by_field["model.knobs.Brenier_patch_spec.G"] == {"field": "model.knobs.Brenier_patch_spec.G", "from": 2, "to": 4}
    assert trace["context"] == {"history": 336, "horizon": 96, "channels": 2, "normalization": "none", "device": "cpu"}
    assert selection == before


@pytest.mark.parametrize(
    "field,before,after",
    [
        ("model.knobs.Brenier_patch_spec.G", 2, 4),
        ("model.knobs.Brenier_patch_spec", {"L": 96, "P": 48, "G": 2}, {"L": 96, "P": 24, "G": 4}),
        ("task.pred_len", 96, 48),
    ],
)
def test_derived_outputs_and_their_parents_cannot_be_overridden(
    selection: dict[str, Any], context: cards.ConfigContext, field: str, before: Any, after: Any
) -> None:
    with pytest.raises(ValueError, match="derived output cannot be authored"):
        cards.resolve_config(selection, [{"field": field, "from": before, "to": after, "reason": "A second output writer must refuse."}], context)


def test_a_card_cannot_both_author_and_derive_the_same_value(tmp_path: Path, fern_card: dict[str, Any]) -> None:
    fern_card["config"]["model"]["knobs"]["Brenier_patch_spec"]["G"] = 2
    catalog = tmp_path / "configs.json"
    catalog.write_text(json.dumps({"cards": {"fern.reference": fern_card}}), encoding="utf-8")
    with pytest.raises(ValueError, match="also has an authored value"):
        cards.select_config("fern.default", path=catalog)


@pytest.mark.parametrize("value", [0, -1, 25, True])
def test_patch_input_must_be_a_positive_integer_dividing_length(selection: dict[str, Any], context: cards.ConfigContext, value: Any) -> None:
    with pytest.raises(ValueError, match="positive integer dividing L"):
        cards.resolve_config(selection, [{"field": "model.knobs.Brenier_patch_spec.P", "from": 48, "to": value, "reason": "Reject invalid patch geometry."}], context)


@pytest.mark.parametrize("value", [-1, 0, "256", True])
def test_new_values_pass_through_the_actual_configuration_reader(selection: dict[str, Any], context: cards.ConfigContext, value: Any) -> None:
    before = copy.deepcopy(selection)
    with pytest.raises((ValueError, TypeError)):
        cards.resolve_config(selection, [{"field": "model.knobs.dim_hidden", "from": 128, "to": value, "reason": "Reject an invalid hidden dimension."}], context)
    assert selection == before


def test_derived_only_annotation_is_marked_when_its_input_changes(tmp_path, fern_card, context):
    fern_card["annotations"] = [{"fields": ["model.knobs.Brenier_patch_spec.G"], "description": "Two groups cover the reference horizon."}]
    path = tmp_path / "configs.json"
    path.write_text(json.dumps({"cards": {"fern.reference": fern_card}}))
    selection = cards.select_config("fern.default", path=path)
    _, source = cards.resolve_config(selection, [{"field": "model.knobs.Brenier_patch_spec.P", "from": 48, "to": 24, "reason": "Compare finer patch geometry."}], context)
    assert source["annotations"][0]["changed_fields"] == ["model.knobs.Brenier_patch_spec.G"]


def test_requested_device_does_not_silently_overwrite_the_card(selection: dict[str, Any]) -> None:
    context = cards.ConfigContext(history=336, horizon=96, channels=2, normalization="none", device="cuda")
    with pytest.raises(ValueError, match="does not match requested device"):
        cards.resolve_config(selection, [], context)
    resolved, trace = cards.resolve_config(selection, [{"field": "runtime.device", "from": "cpu", "to": "cuda", "reason": "Select the execution device explicitly."}], context)
    assert resolved["runtime"]["device"] == "cuda"
    assert trace["changes"][0]["field"] == "runtime.device"


# ------------------------------------------------- a card stated as a difference

# 85 of the 120 cards are stored as a base and the fields that differ from it.
# The expanded body is what every reader sees. These bodies were recorded from
# the catalogue (CHG 2026-09-26: bodies, where digests were recorded before), so
# a card whose meaning moved fails here, named field by field, rather than in a
# study six weeks later.
#
# The Go twin is tools/fern-compile/catalogue_test.go, reading the same fixture
# and re-recording it under FERN_RECAPTURE_CARD_BODIES=1, so the two languages
# cannot agree with each other and disagree with the recorded bodies.
BODIES = Path(__file__).resolve().parents[1] / "tests" / "fixtures" / "config_card_bodies.json"


def test_every_card_expands_to_its_recorded_body() -> None:
    """Every card expands; every recorded card expands to its recorded body, or the fields that moved are named.

    A card written since the recording is held to the first claim only.
    """
    from fern_train.config import upgrade_legacy_lag_lists

    recorded = {name: body for name, body in json.loads(BODIES.read_text(encoding="utf-8"))["bodies"].items() if body is not None}
    # The historical fixture remains as recorded. Upgrade only its config's
    # old lag lists in memory; all other fields still face the exact comparison.
    for body in recorded.values():
        upgrade_legacy_lag_lists(body["config"])
    catalog = json.loads(cards.CONFIGS_FILE.read_text(encoding="utf-8"))["cards"]
    assert len(catalog) >= len(recorded) > 0
    expanded = {name: cards.select_config(name)["card"] for name in sorted(catalog)}
    assert set(recorded) <= set(expanded), sorted(set(recorded) - set(expanded))
    moved = {name: cards.card_differences(recorded[name], expanded[name]) for name in recorded}
    assert {name: fields for name, fields in moved.items() if fields} == {}


def test_a_card_written_against_a_moved_base_is_refused(tmp_path: Path, fern_card: dict[str, Any]) -> None:
    catalog = tmp_path / "configs.json"
    catalog.write_text(json.dumps({"cards": {
        "fern.reference": fern_card,
        "fern.variant": {"from": "fern.reference", "changes": [
            {"field": "model.knobs.dim_hidden", "from": 7777, "to": 256}]},
    }}), encoding="utf-8")
    with pytest.raises(ValueError, match="written against 7777"):
        cards.select_config("fern.variant", path=catalog)


def test_a_change_may_carry_a_reason_sentence(tmp_path: Path, fern_card: dict[str, Any]) -> None:
    """CHG 2026-09-26: a catalogue change may say why, as a study cell's change does.

    The owner's visual cards of c742a5f61 state a reason; the sentence explains the
    change and never enters the body, and anything but a sentence is refused.
    """
    catalog = tmp_path / "configs.json"
    stated = fern_card["config"]["model"]["knobs"]["dim_hidden"]
    catalog.write_text(json.dumps({"cards": {
        "fern.reference": fern_card,
        "fern.variant": {"from": "fern.reference", "changes": [
            {"field": "model.knobs.dim_hidden", "from": stated, "to": 256, "reason": "a width ablation"}]},
        "fern.numbered": {"from": "fern.reference", "changes": [
            {"field": "model.knobs.dim_hidden", "from": stated, "to": 256, "reason": 7}]},
    }}), encoding="utf-8")
    body = cards.select_config("fern.variant", path=catalog)["card"]
    assert body["config"]["model"]["knobs"]["dim_hidden"] == 256
    assert "reason" not in json.dumps(body)
    with pytest.raises(ValueError, match="reason is a sentence"):
        cards.select_config("fern.numbered", path=catalog)


def test_a_difference_inherits_what_it_does_not_state(tmp_path: Path, fern_card: dict[str, Any]) -> None:
    catalog = tmp_path / "configs.json"
    catalog.write_text(json.dumps({"cards": {
        "fern.reference": fern_card,
        "fern.variant": {"from": "fern.reference", "changes": [
            {"field": "model.knobs.dim_hidden", "from": fern_card["config"]["model"]["knobs"]["dim_hidden"], "to": 256}]},
    }}), encoding="utf-8")
    body = cards.select_config("fern.variant", path=catalog)["card"]
    assert body["config"]["model"]["knobs"]["dim_hidden"] == 256
    assert body["derived"] == fern_card["derived"]
    assert body["annotations"] == fern_card["annotations"]
    # The alias is the base's name for itself and is not inherited: two cards
    # answering to one alias is exactly what the catalogue refuses.
    assert "alias" not in body


def test_a_card_cannot_inherit_from_itself(tmp_path: Path, fern_card: dict[str, Any]) -> None:
    catalog = tmp_path / "configs.json"
    catalog.write_text(json.dumps({"cards": {
        "fern.first": {"from": "fern.second", "changes": []},
        "fern.second": {"from": "fern.first", "changes": []},
    }}), encoding="utf-8")
    with pytest.raises(ValueError, match="inherits from itself"):
        cards.select_config("fern.first", path=catalog)


def test_a_card_states_its_config_or_the_card_it_differs_from(tmp_path: Path, fern_card: dict[str, Any]) -> None:
    catalog = tmp_path / "configs.json"
    both = {key: value for key, value in fern_card.items() if key != "alias"}
    both["from"] = "fern.reference"
    catalog.write_text(json.dumps({"cards": {"fern.reference": fern_card, "fern.both": both}}), encoding="utf-8")
    with pytest.raises(ValueError, match="never both"):
        cards.select_config("fern.both", path=catalog)


@pytest.mark.parametrize("card,knob,lags", [
    # CHG 2026-09-26: fern.fern_v6 now declares its own derived list (the prepared entry of
    # docs/programs/2026-09-12-fern-ablation/2026-09-25-pre-encoder-coupling), so the strict
    # xfail that recorded the inherited list's refusal is gone and the card is held to resolving.
    ("fern.fern_v6", "temporal_spline", [0, 1]),
    ("fern.spd_pe_v5", "channel_history", [0, 1]),
])
def test_the_2026_09_25_standards_resolve_on_physical_channels_with_two_other_channel_lags(card, knob, lags):
    """The two cards named 'next standard' on 2026-09-25 must resolve as written: single physical
    channels (no channel basis) and the other channels' last two observations. fern.fern_v6 was refused
    until it declared its own derived list, because the list it inherited from fern.fern_v3 re-created
    the channel basis it had deleted (config_cards derive_config writes every derived field back)."""
    from fern_train.config import ResolvedTrainingConfig
    context = cards.ConfigContext(192, 192, 3, "none", "cuda")
    spec, _ = cards.resolve_config(cards.select_config(card), [], context)
    cfg = ResolvedTrainingConfig.from_spec(spec)
    knobs = spec["model"]["knobs"]
    assert cfg.knobs.channel_basis is None
    assert "channel_basis" not in knobs or knobs["channel_basis"] is None
    assert knobs[knob]["auxiliary_lags"] == {"kind": "latest", "count": len(lags)}
    if card == "fern.fern_v6":
        assert knobs["encoder_coupling"] == "temporal_odd_even" and knobs["output_source"] == "persistence_gaussian"
        assert knobs["random_radial"]["mixing_layout"] == "per_channel"
    else:
        assert knobs["mixture_component_count"] == 3 and knobs["radial_spline"]["num_bins"] == 8


def test_a_result_record_names_how_its_card_differs_and_old_records_still_match() -> None:
    # CHG 2026-09-26: records carry the card; records written before carry its
    # SHA-256 over the card with each selection's former digest, and still match.
    from fern_train import study as fs

    body = {"alias": "default", "config": {"model": {"knobs": {"dim_hidden": 128}}}, "derived": [], "annotations": []}
    card = {"study": {"name": "s"}, "stage": [{"stage": "train.cell.v1", "selection": {"selected": "fern.default", "name": "fern.reference", "card": body}}]}
    assert fs.record_card_differences({"card": card}, card) == []
    changed = {"study": {"name": "s"}, "stage": [{"stage": "train.cell.v1", "selection": {
        "selected": "fern.default", "name": "fern.reference", "card": {**body, "config": {"model": {"knobs": {"dim_hidden": 256}}}}}}]}
    assert fs.record_card_differences({"card": card}, changed) == [("stage[0].selection.card.config.model.knobs.dim_hidden", 128, 256)]
    old = {"card_sha256": fs.sha256_of({"study": {"name": "s"}, "stage": [{"stage": "train.cell.v1", "selection": {
        "selected": "fern.default", "name": "fern.reference", "card": body,
        "sha256": __import__("hashlib").sha256(cards._bytes(body)).hexdigest()}}]})}
    assert fs.record_card_differences(old, card) == []
    assert fs.record_card_differences(old, changed)[0][0] == "card_sha256"
