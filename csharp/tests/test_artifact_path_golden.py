from __future__ import annotations

import json
import os
import subprocess
from pathlib import Path

from fern_train.train import fern_experiment_cmd


def test_csharp_public_artifact_paths_match_shared_golden_vectors(tmp_path) -> None:
    vectors = json.loads((Path(__file__).parent / "golden" / "fern_artifact_paths.json").read_text())
    request = tmp_path / "artifact-path-request.json"
    command_env = dict(os.environ, FERN_EXPERIMENT_BIN=fern_experiment_cmd()[0])
    for vector in vectors:
        request.write_text(
            json.dumps(
                {
                    "root": vector["root"],
                    "run_collection": vector["run_collection"],
                    "dataset": vector["dataset"],
                    "model_name": vector["model_name"],
                    "pred_len": vector["pred_len"],
                    "seed": vector["seed"],
                    "noise_sigma": vector["noise_sigma"],
                    "trajectory_draw_ordinal": vector["trajectory_draw_ordinal"],
                    "noise_draw_ordinal": vector["noise_draw_ordinal"],
                    "run_hash": vector["run_hash"],
                    "short_id_format": vector["short_id_format"],
                }
            ),
            encoding="utf-8",
        )
        result = subprocess.run(
            [*fern_experiment_cmd(), "build-artifact-dir"],
            input=request.read_text(encoding="utf-8"),
            check=True,
            capture_output=True,
            cwd=Path(__file__).resolve().parents[1],
            env=command_env,
            text=True,
        )
        got = Path(json.loads(result.stdout)["artifact_dir"])
        assert got == Path(vector["expected"]), vector["name"]
