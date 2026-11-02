"""The model-name collision check: one model_type must cover one model.

These tests are the Rust port's unit tests brought home. The check began as
scripts/check_model_name_collisions.py with no tests, became Rust on 2026-08-12
and gained five, and returned to Python on 2026-08-26 (the Rust is preserved at
retired/crates/fern-statistics/src/model_name_collisions_2026_08_26.rs). Each
test below names the Rust test function it ports, so the mapping is checkable by
grep.
"""

from __future__ import annotations

import json
import pathlib
import stat
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent / "scripts"))
import check_model_name_collisions as collision_check  # noqa: E402

BARE = collision_check.BARE_VARIANT


def lane_with_rows(rows: list[dict], lane: str, entries: list[tuple[str, str, str]]) -> None:
    """Ports the Rust `lane_with_rows` helper.

    The Rust fixture used to write a real SQLite file, which tested the check
    against a database shape it no longer reads. It imitates the command it
    actually calls -- including the artifact path, since that is where the lane
    comes from now.
    """
    for world, model, recipe in entries:
        rows.append({
            "dataset": world,
            "model_type": model,
            "recipe_name": recipe,
            "artifact_dir": f"results/paper_campaign/{lane}/runs/{recipe}/cell",
            "status": "done",
        })


# CHG 2026-08-28: `audit` takes the fern-run ARGV VECTOR, not one path, since
# `list-runs` moved to the C# host (a host can be two words, `dotnet X.dll`).
# These calls still handed it a single Path, which `[*fern_run, ...]` refused.
def fake_fern_run(root: pathlib.Path, rows: list[dict]) -> pathlib.Path:
    """Ports the Rust `fake_fern_run` helper: a fern-run that answers list-runs."""
    path = root / "fake-fern-run"
    path.write_text("#!/bin/sh\ncat <<'RUNS'\n" + json.dumps({"runs": rows}) + "\nRUNS\n")
    path.chmod(path.stat().st_mode | stat.S_IEXEC | stat.S_IXGRP | stat.S_IXOTH)
    return path


def fixture(root: pathlib.Path) -> pathlib.Path:
    """Ports the Rust `fixture` helper."""
    (root / collision_check.CAMPAIGN_ROOT).mkdir(parents=True, exist_ok=True)
    return root


def test_the_variant_is_what_sits_between_the_model_and_the_loss():
    """Ports the_variant_is_what_sits_between_the_model_and_the_loss.

    The parse is the whole check; everything else is counting.
    """
    variant = collision_check.recipe_variant
    assert variant("fern", "rossler_param_shock_fern_huber_p70_20_10_none_bs95_titration_h64") == BARE
    assert variant(
        "fern", "rossler_param_shock_fern_icml_pinned_huber_p70_20_10_none_bs95_titration_h64"
    ) == "_icml_pinned"
    assert variant("fern", "") == "unknown"
    # A model name that appears in the world half must not be read as the model
    # segment: `lorenz_base_dlinear_huber_...` has no `dlinear` in its world,
    # but a world containing the model's name would.
    assert variant("dlinear", "lorenz_base_dlinear_huber_p70_20_10_none") == BARE
    assert variant("fern", "something_with_no_loss_at_all") == "unparsed"


def test_two_recipe_variants_under_one_model_type_are_a_collision(tmp_path):
    """Ports two_recipe_variants_under_one_model_type_are_a_collision.

    The real campaign's shape: two Fern builds under one name.
    """
    root = fixture(tmp_path)
    rows: list[dict] = []
    lane_with_rows(rows, "expansion_rossler_param_shift_h64", [
        ("rossler_param_shock", "fern", "rossler_param_shock_fern_huber_p70_20_10_none"),
        ("rossler_param_shock", "fern", "rossler_param_shock_fern_icml_pinned_huber_p70_20_10_none"),
        ("rossler_param_shock", "fern", "rossler_param_shock_fern_huber_p70_20_10_none"),
    ])
    fern_run = fake_fern_run(root, rows)

    groups, collisions = collision_check.audit([str(fern_run)], root)
    assert groups == 1
    assert len(collisions) == 1
    assert collisions[0].model_type == "fern"
    assert collisions[0].rows_by_variant[BARE] == 2
    assert collisions[0].rows_by_variant["_icml_pinned"] == 1

    passed, text = collision_check.render(groups, collisions)
    assert not passed
    assert "COLLISIONS" in text


def test_one_variant_per_model_type_passes(tmp_path):
    """Ports one_variant_per_model_type_passes.

    One name, one model, across many lanes: nothing to say.
    """
    root = fixture(tmp_path)
    rows: list[dict] = []
    lane_with_rows(rows, "core_h64", [
        ("lg_ssm_base", "dlinear", "lg_ssm_base_dlinear_huber_p70_20_10_none"),
        ("lg_ssm_base", "tirex", "lg_ssm_base_tirex_huber_p70_20_10_none"),
    ])
    lane_with_rows(rows, "extended_t3_h64", [
        ("lg_ssm_base", "dlinear", "lg_ssm_base_dlinear_huber_p70_20_10_none_t3"),
    ])
    fern_run = fake_fern_run(root, rows)

    groups, collisions = collision_check.audit([str(fern_run)], root)
    assert groups == 3, "two models in one lane, one in the other"
    assert collisions == []
    assert collision_check.render(groups, collisions)[0]


def test_examining_no_rows_is_reported_as_examining_no_rows(tmp_path):
    """Ports examining_no_rows_is_reported_as_examining_no_rows.

    A check that examined NOTHING must not read as a pass. This replaced a Rust
    test about an unreadable lane file, which no longer applies because the
    check does not open lane files. The failure that does apply is the one that
    cost an evening on 2026-08-12: the source answers with nothing, and the
    check prints its clean-sweep line.
    """
    root = fixture(tmp_path)
    fern_run = fake_fern_run(root, [])

    groups, collisions = collision_check.audit([str(fern_run)], root)
    assert groups == 0
    assert collisions == []

    passed, text = collision_check.render(groups, collisions)
    assert not passed, "zero groups examined is not a clean sweep"
    assert "examined no completed run" in text


def test_an_unrunnable_registry_owner_is_not_a_pass(tmp_path):
    """Ports an_unrunnable_registry_owner_is_not_a_pass.

    The same, when the command itself cannot run at all.
    """
    root = fixture(tmp_path)
    groups, collisions = collision_check.audit([str(root / "there-is-no-fern-run")], root)

    passed, text = collision_check.render(groups, collisions)
    assert not passed
    assert "examined no completed run" in text
