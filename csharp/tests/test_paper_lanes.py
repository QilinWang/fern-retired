"""One lane derivation per language, and a check that they agree.

`paper_lanes.lane_db` and `just show-lane-paths` compute the same thing for
different callers. Two implementations of one rule is exactly the drift this
repository keeps paying for, so the equality is asserted rather than assumed --
and asserted against the command's real output, not a copy of its logic.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from fern_analysis import lanes as paper_lanes  # noqa: E402
from fern_train.train import fern_experiment_cmd  # noqa: E402


def test_a_named_lane_resolves_to_one_database(tmp_path, monkeypatch):
    monkeypatch.setattr(paper_lanes, "CAMPAIGN_ROOT", tmp_path)
    (tmp_path / "core_h64").mkdir()
    (tmp_path / "core_h64" / "fern.db").write_bytes(b"")
    assert paper_lanes.lane_db("core_h64") == tmp_path / "core_h64" / "fern.db"


def test_a_missing_lane_raises_rather_than_reading_nothing(tmp_path, monkeypatch):
    """The failure this module exists to prevent."""
    monkeypatch.setattr(paper_lanes, "CAMPAIGN_ROOT", tmp_path)
    (tmp_path / "core_h64_banks8").mkdir()
    with pytest.raises(paper_lanes.LaneNotFound) as raised:
        paper_lanes.lane_db("core_h64_banks9")
    assert "core_h64_banks8" in str(raised.value), "a near miss should be named"


def test_absence_is_only_allowed_when_the_caller_says_so(tmp_path, monkeypatch):
    monkeypatch.setattr(paper_lanes, "CAMPAIGN_ROOT", tmp_path)
    assert not paper_lanes.lane_db("not_here", required=False).is_file()


def test_a_lane_name_is_one_path_segment(tmp_path, monkeypatch):
    monkeypatch.setattr(paper_lanes, "CAMPAIGN_ROOT", tmp_path)
    for bad in ("", "a/b", "a b", "../escape"):
        with pytest.raises(ValueError):
            paper_lanes.lane_db(bad)


def test_a_run_can_say_which_lanes_it_read(tmp_path, monkeypatch):
    monkeypatch.setattr(paper_lanes, "CAMPAIGN_ROOT", tmp_path)
    monkeypatch.setattr(paper_lanes, "_opened", {})
    paper_lanes.lane_db("alpha", required=False)
    paper_lanes.lane_db("beta", required=False)
    paper_lanes.lane_db("alpha", required=False)
    assert paper_lanes.lanes_read_so_far() == ["alpha", "beta"]


def test_python_and_csharp_public_route_resolve_the_same_path(tmp_path):
    """The two derivations must not drift apart."""
    lane = "core_h64"
    request = tmp_path / "lane-paths-request.json"
    request.write_text(
        json.dumps(
            {
                "lane": lane,
                "campaign_root": "results/paper_campaign",
                "work_root": "tmp",
                "public_report": True,
            }
        ),
        encoding="utf-8",
    )
    result = subprocess.run(
        [*fern_experiment_cmd(), "show-lane-paths"],
        input=request.read_text(encoding="utf-8"),
        capture_output=True,
        text=True,
        cwd=paper_lanes.ROOT,
        env=dict(os.environ, FERN_EXPERIMENT_BIN=fern_experiment_cmd()[0]),
        check=True,
    )
    from_csharp = Path(json.loads(result.stdout)["db"])
    from_python = paper_lanes.lane_db(lane, required=False)
    assert from_python.resolve() == (paper_lanes.ROOT / from_csharp).resolve()
