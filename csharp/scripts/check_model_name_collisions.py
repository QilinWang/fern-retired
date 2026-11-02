# Status: live — repository guard; runs in or beside fern-stat refresh
"""Fail when one model_type value covers two different models.

WHY. Fern was run twice in this campaign -- once as the plain recipe and once as
the ICML-pinned one -- and the worker records BOTH as `model_type='fern'`. The
two variants are only distinguishable by recipe name. Any analysis that groups by
model_type therefore averages two different models, silently.

Today exactly one (lane, world) carries both variants, and no published artifact
reports a fern number there, so nothing is currently wrong. That is luck, not
design: the next analysis to include fern in that lane inherits the defect with
no warning. This check makes the collision visible, and it also catches the same
shape for any future model whose recipe family splits under one name.

    python scripts/check_model_name_collisions.py --root .

CHG 2026-08-26: restored from
retired/scripts/check_model_name_collisions_2026_08_12.py, replacing the Rust
port (retired/crates/fern-statistics/src/model_name_collisions_2026_08_26.rs)
that ran from 2026-08-12 to today. THREE gains of the Rust survive the return,
and the first is the important one:

  1. It asks `fern-run list-runs`, the command that OWNS run rows, instead of
     opening each lane's fern.db with a second SQL driver. A check that reads
     rows by opening their files has to know where lanes live and which schema
     they carry, and a sibling check got exactly that wrong on 2026-08-12 and
     reported an empty campaign with total confidence (correction 316).
  2. Zero groups examined is a FAILURE, not a pass. The original printed "every
     model_type value covers exactly one model" over zero rows, which is true,
     useless, and reads as green.
  3. The root is a --root argument, and the audit is a pure function returning a
     value, separate from the printing.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import subprocess
import sys
from collections import defaultdict
from dataclasses import dataclass

CAMPAIGN_ROOT = "results/paper_campaign"

# The losses a recipe name can carry, which is what bounds the model segment.
LOSS_NAMES = frozenset({"huber", "mse", "mae", "nll", "crps"})

# What a bare recipe variant is called in the report, since "" reads as a bug.
BARE_VARIANT = "<none>"


@dataclass(frozen=True)
class Collision:
    """One `model_type` that several models answer to, in one lane and world."""

    lane: str
    world: str
    model_type: str
    # variant -> how many completed rows carry it, in sorted variant order.
    rows_by_variant: dict[str, int]


def _variant_before_a_loss(rest: str) -> str | None:
    """The `_word` segments between the model name and the loss name.

    Returns None when `rest` is not made of `_word` segments ending at a loss,
    which means the marker we matched was not the model segment after all.
    """
    variant = ""
    cursor = rest
    while True:
        if not cursor.startswith("_"):
            return None
        body = cursor[1:]
        word = ""
        for character in body:
            if character.islower() and character.isascii() or character.isdigit():
                word += character
            else:
                break
        if not word:
            return None
        if word in LOSS_NAMES:
            return variant
        variant += "_" + word
        cursor = body[len(word):]
        # A recipe name is bounded; a variant of many segments is not one.
        if len(variant) > 64:
            return None


def recipe_variant(model: str, recipe: str) -> str:
    """The part of the recipe name that identifies the model, past its bare name.

    A recipe is `<world>_<model>_<loss>_...`, so the model segment is what
    follows the world. When two rows share a model_type but differ here, the
    model_type is covering two things.
    """
    if not recipe:
        return "unknown"
    marker = "_" + model
    search_from = 0
    while (offset := recipe.find(marker, search_from)) != -1:
        start = offset + len(marker)
        variant = _variant_before_a_loss(recipe[start:])
        if variant is not None:
            return variant if variant else BARE_VARIANT
        search_from = start
    return "unparsed"


def completed_rows(fern_run: list[str], root: pathlib.Path) -> list[tuple[str, str, str, str]]:
    """Every completed row's (lane, world, model_type, recipe).

    Asked of the command that owns run rows, never of the database files.

    CHG 2026-08-28: `list-runs` left the Rust CLI for the C# host, so this asks
    the host. `fern_run_csharp_cmd` is the one locator; it is not copied here.
    """
    try:
        finished = subprocess.run(
            [*fern_run, "list-runs", "--campaign-root", CAMPAIGN_ROOT,
             "--status", "done", "--limit", "0", "--format", "json"],
            cwd=root, capture_output=True, check=False,
        )
    except OSError:
        return []
    try:
        report = json.loads(finished.stdout)
    except (json.JSONDecodeError, UnicodeDecodeError):
        return []
    rows = []
    for run in report.get("runs") or []:
        model = run.get("model_type") or ""
        if not model:
            continue
        # The lane is provenance and comes from the artifact path the registry
        # published: `<campaign>/<lane>/runs/<recipe>/<cell>`.
        parts = pathlib.PurePath(run.get("artifact_dir") or "").parts
        lane = parts[-4] if len(parts) >= 4 else ""
        rows.append((lane, run.get("dataset") or "", model, run.get("recipe_name") or ""))
    return rows


def audit(fern_run: list[str], root: pathlib.Path) -> tuple[int, list[Collision]]:
    """Groups examined, and the ones where a name covers more than one model."""
    variants: dict[tuple[str, str, str], dict[str, int]] = defaultdict(lambda: defaultdict(int))
    for lane, world, model, recipe in completed_rows(fern_run, root):
        variants[(lane, world, model)][recipe_variant(model, recipe)] += 1

    collisions = [
        Collision(lane=lane, world=world, model_type=model,
                  rows_by_variant=dict(sorted(rows_by_variant.items())))
        for (lane, world, model), rows_by_variant in sorted(variants.items())
        if len(rows_by_variant) > 1
    ]
    return len(variants), collisions


def render(groups: int, collisions: list[Collision]) -> tuple[bool, str]:
    """The report a human reads, identical in shape to the script this began as."""
    text = f"{groups} (lane, world, model) groups; {len(collisions)} carry more than one variant"
    if groups == 0:
        # A check that looked at nothing is not a check that found nothing.
        text += ("\nexamined no completed run: the registry owner returned nothing. "
                 "That is a wrong root or a stale binary, not a clean campaign.")
        return False, text
    if not collisions:
        return True, text + "\nevery model_type value covers exactly one model"

    text += "\n\nCOLLISIONS -- one model_type, several models:"
    for collision in collisions:
        text += f"\n  {collision.lane}/{collision.world}  model_type={collision.model_type}"
        for variant, rows in collision.rows_by_variant.items():
            text += f"\n      {rows:4d} rows  recipe variant {variant}"
    text += ("\n\nAny analysis grouping by model_type in these lanes averages different "
             "models. Select on recipe as well, or give the variants distinct names.")
    return False, text


def main(argv: list[str] | None = None) -> int:
    summary = __doc__.splitlines()[0] if __doc__ else ""
    parser = argparse.ArgumentParser(description=summary)
    parser.add_argument("--root", type=pathlib.Path, default=pathlib.Path("."),
                        help="repository root (default: the working directory)")
    parser.add_argument("--fern-run", type=pathlib.Path, default=None,
                        help="the command that owns run rows (default: the built C# fern-run)")
    arguments = parser.parse_args(argv)

    # Resolved after parsing, not as an argparse default: the locator imports
    # the trainer module, and `--help` should not pay for that.
    if arguments.fern_run is None:
        from fern_train.train import fern_run_csharp_cmd

        fern_run = fern_run_csharp_cmd()
    else:
        fern_run = [str(arguments.fern_run)]

    groups, collisions = audit(fern_run, arguments.root)
    passed, text = render(groups, collisions)
    print(text)
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
