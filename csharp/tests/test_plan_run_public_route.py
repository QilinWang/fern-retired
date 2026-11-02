"""The public typed PlanRun route sends its complete packet request to C#."""

from __future__ import annotations

import json
import os
import subprocess
from pathlib import Path

from fern_train.train import fern_experiment_cmd
from fern_train.study import base_spec

ROOT = Path(__file__).resolve().parents[1]


def test_plan_run_just_routes_complete_typed_packet_to_csharp(tmp_path) -> None:
    config = base_spec("fern.default", seq_len=24, pred_len=24, device="cpu", normalization="none", channels=None, changes=[{"field": "runtime.device", "from": "cuda", "to": "cpu", "reason": "Preserve the test's explicit CPU configuration."}])
    context = {
        "schema": "fern.run_context.v3",
        "experiment": {"name": "lorenz", "version": "v1"},
        "model_architecture": {"schema": "fern.model_architecture.test.v1"},
        "protocol": {
            "schema": "fern.experiment_protocol.v1",
            "truth_kind": "simulator_trajectory",
            "noise_placement": "additive_post_generation_observation",
            "noise_law": "gaussian",
            "relative_dose": False,
            "noise_bank_seed": 1000,
            "training_scale_hash": "1234567890abcdef",
            "training_scale_hash_kind": "derivation_preimage",
            "training_scale_preimage": {"schema": "fern.training_scale_derivation.v1"},
            "checkpoint_revision": None,
            "quantile_crossing_policy": "not_applicable",
        },
        "run": {
            "seed": 42,
            "run_name": "fern_lorenz",
            "dataset_base": None,
            "dataset_id": None,
            "cell": {"source_name": None, "noise_level": 0.2},
            "flags": {
                "run_whiten": False,
                "truncated_history_error": False,
                "scale_noise_by_std": False,
                "max_epochs_override": None,
            },
        },
        "catalog": {"config_name": "lorenz"},
        "ablation": {"study": None, "name": None},
        "artifact": {
            "run_collection": "standalone",
            "dataset": None,
            "pred_len": 24,
            "noise_sigma": 0.2,
            "trajectory_draw_ordinal": 0,
            "noise_draw_ordinal": 0,
            "short_id_format": "single-run",
        },
    }
    request = tmp_path / "plan-run-request.json"
    request.write_text(
        json.dumps(
            {
                "config": json.dumps(config),
                "context": context,
                "artifact_root": "results/runs",
                "declared_replica": None,
            }
        ),
        encoding="utf-8",
    )

    result = subprocess.run(
        [*fern_experiment_cmd(), "plan-run"],
        input=request.read_text(encoding="utf-8"),
        check=True,
        capture_output=True,
        cwd=ROOT,
        env=dict(os.environ, FERN_EXPERIMENT_BIN=fern_experiment_cmd()[0]),
        text=True,
    )
    packet = json.loads(result.stdout)
    assert packet["schema"] == "fern.run_packet.v2"
    assert packet["config_hash"] == packet["identity"]["config_hash"]
    assert packet["artifact_dir"].startswith("results/runs/standalone/")
