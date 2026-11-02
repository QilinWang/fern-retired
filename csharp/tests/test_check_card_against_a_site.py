"""Ask whether a card can run where it is about to run, before it is queued.

Running one cell needs four things to agree: the fern-run binary on that
machine, the frozen Python source there, the card, and the runtime selection.
On 2026-09-09 all four skewed independently and every skew was found by running
something and reading a refusal — nineteen queued cells were doomed by a source
that refused a field committed an hour earlier, and nothing said so.
"""
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[1]
CARD = REPO / "studies/fern-lorenz-h192-transport-w-placement-b.json"
CURRENT = REPO / "runtime/deploy/run-operator-binaries/fern-run.1ca03d6274ff74ab5c50fd80f9b113791634961d9a6dedcaee44b88e146dc48e"
OLDER = REPO / "runtime/deploy/run-operator-binaries/fern-run.f09c95fd7732efcf66d5a2dc543c12f66d89d01530ad7936bed312f6c0106200"


def _check(**kw: str) -> tuple[int, dict]:
    args = [sys.executable, "-m", "fern_analysis", "study", "check-card"]
    for key, value in kw.items():
        args += [f"--{key.replace('_', '-')}", value]
    done = subprocess.run(args, capture_output=True, text=True, cwd=REPO)
    return done.returncode, json.loads(done.stdout)


def test_a_missing_piece_is_named_before_anything_runs(tmp_path: Path) -> None:
    code, report = _check(card=str(tmp_path / "absent.json"), source_root=str(REPO), run_binary=str(CURRENT))
    assert code == 2
    assert report["agrees"] is False
    assert report["disagreement"] == "site"
    assert "card" in report["detail"]


@pytest.mark.skipif(not CURRENT.exists(), reason="the current run binary is not installed in this tree")
def test_a_card_its_site_can_run_agrees(tmp_path: Path) -> None:
    code, report = _check(card=str(CARD), source_root=str(REPO), run_binary=str(CURRENT))
    assert code == 0, report
    assert report["agrees"] is True
    assert report["disagreement"] is None and report["detail"] is None


@pytest.mark.skipif(not OLDER.exists(), reason="the older run binary is not in this tree")
def test_a_binary_too_old_for_the_card_is_blamed_by_name() -> None:
    """The Pod's surviving binary refused the cards it had been running all day."""
    code, report = _check(card=str(CARD), source_root=str(REPO), run_binary=str(OLDER))
    assert code == 1
    assert report["agrees"] is False
    assert report["disagreement"] == "binary"
    assert "fern.training.v2" in report["detail"]


def test_a_source_root_that_cannot_compile_the_card_is_blamed_as_source(tmp_path: Path) -> None:
    """The Pod held a root whose study_execution called compile_study with an argument its own study.py refused."""
    root = tmp_path / "root"
    (root / "python" / "fern_analysis").mkdir(parents=True)
    (root / "python" / "fern_analysis" / "__init__.py").write_text("")
    (root / "python" / "fern_analysis" / "__main__.py").write_text(
        "import sys\n"
        "print('ModuleNotFoundError: No module named \\'fern_train\\'', file=sys.stderr)\n"
        "raise SystemExit(1)\n")
    code, report = _check(card=str(CARD), source_root=str(root), run_binary=str(CURRENT))
    assert code == 1
    assert report["disagreement"] == "source"


def test_the_check_spends_nothing(tmp_path: Path) -> None:
    """It plans. A plan trains nothing and writes no result."""
    before = {p for p in (REPO / "results").glob("*")} if (REPO / "results").exists() else set()
    _check(card=str(CARD), source_root=str(REPO), run_binary=str(CURRENT))
    after = {p for p in (REPO / "results").glob("*")} if (REPO / "results").exists() else set()
    assert before == after
