"""Verified evidence-reader doors for historical and V3 campaigns.

WHY NOT OPEN THE DATABASES. Twenty analysis scripts opened lane databases
directly, so what a figure showed depended on which directories happened to be
present. Deleting thirteen folders once broke a script that writes a shipped
number and nothing failed: it read fewer databases and printed different
numbers. A figure must not be able to change because a path changed.

HISTORICAL TABLE. ``accepted_matrix()`` returns one row per accepted cell -- the coordinate, the run selected
for it, its metrics, its protocol axes, the machine, the code revision, and the
artifact directory. Written by `fern-registry export-accepted-matrix`.

WHAT IT DOES NOT REPLACE. Four statistics layers read raw forecast tensors, 151
GB of them, which no export can carry. Those layers take `artifact_dir` from
here and read the tensors from disk. This file is the authority on WHICH runs
count and what they scored; it is not the tensors.

STALENESS IS CHECKED HERE, not in a separate gate. A gate that lives beside the
thing it guards is a gate that can be skipped -- that is how a figure shipped
superseded numbers while its JSON was correct. So every load compares the
campaign's databases against the digests the export recorded, and REFUSES when
they disagree. The check reads the databases; the analysis does not. Those are
different jobs and only one of them is allowed to.

    from fern_analysis.accepted import accepted_matrix
    rows = accepted_matrix()
    gaussian = rows.filter(pl.col("noise_law") == "gaussian")

V3 RELEASE. ``frozen_release_metrics()`` first asks the C# run operator to re-open and
authorize the exact frozen release table, then lets Polars read it. It refuses
the operational campaign ledger. Historical rows stay on the older door until
an explicit policy accepts and freezes their complete V3 release.
"""

from __future__ import annotations

import json
import os
import subprocess
from pathlib import Path

import polars as pl

ROOT = Path(__file__).resolve().parents[2]
CAMPAIGN = ROOT / "results" / "paper_campaign"
ARTIFACT_KEY_RESOLUTION_DEADLINE_SECONDS = 30


class FrozenEvidenceRefused(RuntimeError):
    """Registry Home refused the exact frozen release/table relationship."""


def frozen_release_metrics(
    *,
    registry: Path,
    release_digest: str,
    metrics_table: Path,
) -> pl.DataFrame:
    """Read one release table only after the run operator's authority gate accepts it.

    This is the replacement boundary for paper/statistics readers. It does not
    accept ``campaign_projection.parquet``: that operational file has no frozen
    release identity and `fern-run evidence verify` refuses it before Polars sees rows.

    Example:
        >>> rows = frozen_release_metrics(
        ...     registry=Path("results/registry/current.db"),
        ...     release_digest="evidence-release:sha256:...",
        ...     metrics_table=Path("results/releases/aaai27/release_metrics.parquet"),
        ... )
    """
    # CHG 2026-08-28: the C# fern-run host now serves `evidence verify`, and
    # the Rust `evidence` verb retired the same evening
    # (retired/fern-cli-rs/evidence_commands_2026_08_28.rs), so a hardcoded
    # target/debug/fern-run here would call a binary that no longer has the
    # subcommand. This uses the repository's one locator rather than keeping a
    # second copy of the selection rule.
    from fern_train.train import fern_run_csharp_cmd

    command = [
        *fern_run_csharp_cmd(),
        "evidence",
        "verify",
        "--registry",
        str(registry),
        "--release",
        release_digest,
        "--metrics",
        str(metrics_table),
        "--format",
        "json",
    ]
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0:
        detail = result.stderr.strip() or result.stdout.strip()
        raise FrozenEvidenceRefused(
            "the frozen evidence gate refused this table; no statistics were read:\n"
            f"{detail}"
        )
    return pl.read_parquet(metrics_table)


def evidence_manifest() -> dict:
    """Return the source manifest named and hashed by the Rust request."""
    declared_request = os.environ.get("FERN_ANALYSIS_REQUEST")
    if not declared_request:
        raise RuntimeError(
            "evidence membership belongs to the frozen request; run through `python -m fern_analysis compute <name>`"
        )
    from fern_analysis.receipt import load_analysis_request

    request = load_analysis_request(Path(declared_request))
    evidence = request["evidence"]
    source_manifest = evidence.get("source_manifest")
    if not source_manifest:
        raise ValueError("this analysis request has no accepted-matrix manifest")
    return json.loads((ROOT / source_manifest["path"]).read_text())


def accepted_matrix(*, root: Path | None = None) -> pl.DataFrame:
    """Read only the exact cohort Rust resolved and hashed for this invocation."""
    declared_request = os.environ.get("FERN_ANALYSIS_REQUEST")
    if not declared_request:
        raise RuntimeError(
            "evidence membership belongs to the frozen request; run through `python -m fern_analysis compute <name>`"
        )
    from fern_analysis.receipt import declared_cohort_table, load_analysis_request

    request = load_analysis_request(Path(declared_request))
    # CHG: the declared-analysis executor supplies its explicit root; a test or
    # relocated analysis must not accidentally read this checkout's cohort.
    return pl.read_ipc(declared_cohort_table(request, root=ROOT if root is None else root))


class AmbiguousCohort(RuntimeError):
    """The selection still holds one scientific cell more than once."""


def one_estimand(
    rows: pl.DataFrame,
    *,
    noise_law: str = "gaussian",
    split_treatment_scheme: str | None = None,
    world: str | None = None,
    grids: tuple[str, ...] | None = None,
    device: str | None = None,
    model_variant: str | None = None,
    include_replicas: bool = False,
) -> pl.DataFrame:
    """Select one estimand: protocol axes first, then the declared cohort.

    Keying on (world, model, dose, bank) before selecting the protocol is how a
    headline figure once showed a dose crossing that does not exist: 933 of
    4,071 coordinates hold more than one protocol, so the last row read wins.

    PROTOCOL IS NOT ENOUGH, and this is what the first converted script found.
    Seven lanes hold rossler gaussian noise-matched cells -- the four-bank grid,
    the eight-bank grid that extends it, the correlated-noise grid, four
    single-model expansions -- and they legitimately share bank ordinals 0..3.
    Every one of those runs is valid; they belong to DIFFERENT grids. So an
    analysis names the grids it is reading. "Everything currently in the
    databases" is not a cohort, it is whatever has finished.

    Raises rather than silently returning a doubled cell, because a doubled cell
    averages two experiments and looks like a normal number.
    """
    selected = rows.filter(pl.col("noise_law") == noise_law)
    if split_treatment_scheme is not None:
        selected = selected.filter(
            pl.col("split_treatment_scheme") == split_treatment_scheme)
    if world is not None:
        selected = selected.filter(pl.col("dataset") == world)
    if grids is not None:
        selected = selected.filter(pl.col("preset").is_in(list(grids)))
    if model_variant is not None:
        # NAME THE BUILD instead of deleting a grid. model_type says `fern` for
        # two different models, so an analysis wanting the paper's build used to
        # have to drop the other one's whole grid -- losing 28 real runs to
        # remove an ambiguity. Selecting the build keeps every run and says
        # which one is meant.
        selected = with_model_variant(selected).filter(
            pl.col("model_variant") == model_variant)
    if device is not None:
        # Device is part of run identity because it changes the number: one
        # Lorenz cell scores 71.2916 on cpu and 71.288 on cuda. So a cell run on
        # both is two legitimate results, and an analysis that does not say which
        # one it reports is reporting whichever it happened to open.
        selected = selected.filter(pl.col("runtime_device") == device)
    if not include_replicas:
        selected = selected.filter(pl.col("replica_ordinal") == 0)

    # THE CELL KEY INCLUDES THE PROTOCOL AXES, because an analysis may be
    # deliberately reading two of them at one coordinate -- the adaptation ratio
    # is clean-selected over noise-matched at the SAME cell, and a key without
    # the treatment calls that a duplicate. It fired on 60 legitimate pairs.
    cell = ["dataset", "model_type", "noise_sigma",
            "trajectory_draw_ordinal", "noise_draw_ordinal",
            "noise_law", "split_treatment_scheme", "noise_ar1_phi",
            "relative_dose", "checkpoint_execution_mode", "runtime_device",
            "replica_ordinal",
            # The BUILD, because model_type says `fern` for two different models
            # and without this the duplicate check calls them one cell run twice.
            "model_config_hash"]
    doubled = (
        selected.group_by(cell).agg(pl.len().alias("n"), pl.col("preset").unique())
        .filter(pl.col("n") > 1)
    )
    # ONE STATED ORDER, so a sum is reproducible.
    #
    # The export's row order is not SQLite's scan order, so the same mean came
    # out differing in the fifteenth digit -- irrelevant to any reported number
    # and fatal to "this artifact reproduces byte for byte", which is the only
    # cheap way to tell a real change from a rewrite. Sorting here fixes it for
    # every analysis at once rather than each one remembering.
    selected = selected.sort(
        ["dataset", "model_type", "noise_sigma", "split_treatment_scheme",
         "trajectory_draw_ordinal", "noise_draw_ordinal", "replica_ordinal", "run_hash"]
    )

    if doubled.height:
        first = doubled.row(0, named=True)
        raise AmbiguousCohort(
            f"{doubled.height} cell(s) appear more than once after selecting the "
            f"protocol, so this is not one estimand. First: "
            f"{ {k: first[k] for k in cell} } appears in {first['preset']}.\n"
            f"  Name the grids you mean with grids=(...); every one of these runs "
            f"is valid, they just belong to different experiments."
        )
    return selected


def registry_binary() -> Path:
    """Return the explicit Registry executable given to this analysis."""
    value = os.environ.get("FERN_REGISTRY")
    if not value:
        raise RuntimeError(
            "artifact-key resolution requires FERN_REGISTRY from the frozen statistic execution"
        )
    path = Path(value)
    if not path.is_file():
        raise RuntimeError(f"FERN_REGISTRY does not name a file: {path}")
    return path


def artifact_directories(
    rows: pl.DataFrame,
    fern_registry: Path,
    campaign_root: Path,
) -> dict[str, Path]:
    """Turn this selection's artifact keys into directories on THIS machine.

    WHY NOT JUST JOIN THE STRINGS. The table carries `lane/run_hash` rather than
    a path, because a path is a fact about one machine -- the same run lives
    somewhere else on a rented pod, so a table full of paths stops being true the
    moment it moves. Composing the path here would put the layout rule in a
    second place, and the reason there is a rule at all is that a key must not be
    able to point outside the campaign it was given. C# Registry owns both.

    Batch, in one subprocess: the four tensor-reading layers resolve thousands of
    keys, and one process each would cost more than reading the tensors.

        for key, directory in artifact_directories(
            cells, registry_binary(), CAMPAIGN
        ).items():
            tensors = safetensors.load_file(directory / "raw_tensors.safetensors")
    """
    keys = sorted({k for k in rows["artifact_key"].drop_nulls().to_list()})
    if not keys:
        return {}
    try:
        result = subprocess.run(
            [
                str(fern_registry),
                "resolve-artifact-keys",
                "--campaign-root",
                str(campaign_root),
                "--keys-from",
                "-",
            ],
            input="\n".join(keys),
            capture_output=True,
            text=True,
            timeout=ARTIFACT_KEY_RESOLUTION_DEADLINE_SECONDS,
            check=False,
        )
    except subprocess.TimeoutExpired as error:
        raise RuntimeError(
            "artifact-key resolution exceeded its 30 second deadline"
        ) from error
    if result.returncode != 0:
        raise RuntimeError(f"artifact keys were refused: {result.stderr.strip()}")
    try:
        receipt = json.loads(result.stdout)
    except json.JSONDecodeError as error:
        raise RuntimeError("artifact-key resolution returned invalid JSON") from error
    resolved = receipt.get("resolved") if isinstance(receipt, dict) else None
    if (
        not isinstance(receipt, dict)
        or receipt.get("schema") != "fern.resolved_artifact_keys.v1"
        or receipt.get("campaign_root") != str(campaign_root)
        or not isinstance(resolved, dict)
        or set(resolved) != set(keys)
        or not all(isinstance(key, str) and isinstance(path, str) for key, path in resolved.items())
    ):
        raise RuntimeError("artifact-key resolution returned an invalid receipt")
    return {key: Path(path) for key, path in resolved.items()}


def artifact_directories_that_exist(
    rows: pl.DataFrame,
    fern_registry: Path,
    campaign_root: Path,
) -> dict[str, Path]:
    """The same, dropping keys whose directory is not on this machine.

    Separate from `artifact_directories` on purpose. A tensor layer that finds
    half its artifacts missing should decide what to do about that, and a
    function that silently returns fewer entries than it was asked for is how a
    statistic ends up computed over whatever happened to be present.
    """
    return {
        key: path
        for key, path in artifact_directories(rows, fern_registry, campaign_root).items()
        if path.is_dir()
    }


# ---------------------------------------------------------------- variants ---
# WHY THIS EXISTS. `model_type` says `fern` for two different models: the paper's
# ICML-pinned build and the current experimental one. The worker writes the
# family, not the build, so nothing grouping by model_type can tell them apart --
# and the campaign will have more variants, not fewer.
#
# The row records a FACT (which model block ran, as a hash). The registry records
# a JUDGEMENT (what we call that block, and when two blocks are the same model).
# Keeping them apart is what makes ten variants cost nothing: a new build is a
# new hash and one registry line, with no schema change and no row rewritten.
# Renaming `model_type` instead would need a rewrite every time, and would assert
# that old runs used a name they did not.

VARIANTS = ROOT / ".ai" / "model_variants.json"


def model_variant_names() -> dict[str, str]:
    """Model-block hash to registered variant name, aliases already followed."""
    if not VARIANTS.is_file():
        return {}
    registry = json.loads(VARIANTS.read_text())
    variants = {h: entry["name"] for h, entry in registry.get("variants", {}).items()}
    for hash_value, alias in registry.get("aliases", {}).items():
        target = alias.get("same_as")
        if target in variants:
            # One hop only: the registry forbids an alias pointing at an alias,
            # so following further would be covering for a file that is invalid.
            variants[hash_value] = variants[target]
    return variants


def with_model_variant(rows: pl.DataFrame) -> pl.DataFrame:
    """Add `model_variant`: the registered build, or the model name when there is
    only one of it.

    Falls back to `model_type` rather than to null, because for most models the
    family IS the build and a null would make every caller write the same
    coalesce. A Fern row whose hash is unregistered stays `fern`, which is the
    truth about it: nobody has said which build that is.
    """
    if "model_config_hash" not in rows.columns:
        # The column arrives with the next migration. Until then the family is
        # the best available answer, and saying so beats inventing one.
        return rows.with_columns(pl.col("model_type").alias("model_variant"))
    names = model_variant_names()
    return rows.with_columns(
        pl.col("model_config_hash")
        .replace_strict(names, default=None)
        .fill_null(pl.col("model_type"))
        .alias("model_variant")
    )
