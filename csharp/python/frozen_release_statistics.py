"""Summarize only the metrics authorized by one frozen Fern release.

This is the first paper/statistics reader on the V3 evidence path.  It cannot
fall back to a lane database, the historical accepted-matrix export, or a raw
artifact directory. ``paper_accepted.frozen_release_metrics`` asks the C# run
operator to reopen and authorize the exact release/table pair before Polars
sees a number.

The output is descriptive: one row per metric definition, target, and EPT
threshold, with its count and value range.  It proves the reader boundary; it
does not promote this bounded closeout canary into a paper claim.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import polars as pl

from fern_analysis.accepted import frozen_release_metrics

REQUIRED_COLUMNS = {
    "metric_id",
    "metric_target_kind",
    "metric_source_representation",
    "ept_threshold_numerator",
    "ept_threshold_denominator",
    "metric_value",
}


def summarize_release(metrics: pl.DataFrame) -> list[dict[str, object]]:
    """Return deterministic descriptive groups from the authorized rows."""
    missing = sorted(REQUIRED_COLUMNS - set(metrics.columns))
    if missing:
        raise ValueError(f"frozen release is missing required metric columns: {missing}")

    grouped = (
        metrics.group_by(
            [
                "metric_id",
                "metric_target_kind",
                "metric_source_representation",
                "ept_threshold_numerator",
                "ept_threshold_denominator",
            ]
        )
        .agg(
            pl.len().alias("value_count"),
            pl.col("metric_value").mean().alias("mean_value"),
            pl.col("metric_value").min().alias("minimum_value"),
            pl.col("metric_value").max().alias("maximum_value"),
        )
        .sort(
            [
                "metric_id",
                "metric_target_kind",
                "ept_threshold_numerator",
                "ept_threshold_denominator",
            ],
            nulls_last=True,
        )
    )
    return grouped.to_dicts()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--registry", type=Path, required=True)
    parser.add_argument("--release", required=True, help="Full evidence-release:sha256:... digest")
    parser.add_argument("--metrics", type=Path, required=True)
    args = parser.parse_args()

    metrics = frozen_release_metrics(
        registry=args.registry,
        release_digest=args.release,
        metrics_table=args.metrics,
    )
    report = {
        "schema": "fern.frozen_release_statistics.v1",
        "release_digest": args.release,
        "source": str(args.metrics),
        "metric_row_count": metrics.height,
        "summaries": summarize_release(metrics),
    }
    print(json.dumps(report, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
