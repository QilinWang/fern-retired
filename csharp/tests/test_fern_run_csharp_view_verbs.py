"""Current C# run views read an explicitly created temporary scientific lane.

The Registry archive decision does not remove scientific run summaries. These
tests exercise the real C# CLI against fixed coordinates and metrics, without
a retired executable or a production database.
"""

from __future__ import annotations

import json
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "tests"))
from test_db_cli import insert_full_run  # noqa: E402

CSHARP = REPO / "src" / "csharp" / "Fern.Run.Cli" / "bin" / "Debug" / "net11.0" / "fern-run"


def _run(args: list[str]) -> tuple[int, str, str]:
    proc = subprocess.run([str(CSHARP), *args], cwd=REPO, text=True,
                          capture_output=True, check=False, timeout=30)
    return proc.returncode, proc.stdout, proc.stderr


@unittest.skipUnless(CSHARP.is_file(), f"the current C# test host is not built: {CSHARP}")
class ViewVerbTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.db_path = Path(self.tmp.name) / "fern.db"
        created = _run(["open-run-campaign", "--db", str(self.db_path)])
        self.assertEqual(0, created[0], created)
        with sqlite3.connect(self.db_path) as database:
            insert_full_run(database, artifact_dir="/tmp/fake_a", mse=1.25, run_hash="viewhash00000001")
            insert_full_run(database, artifact_dir="/tmp/fake_b", mse=0.75, run_hash="viewhash00000002")

    def test_show_run_text_and_json(self) -> None:
        arguments = ["show-run", "--db", str(self.db_path), "--run-hash", "viewhash00000001"]
        result = _run([*arguments, "--format", "json"])
        self.assertEqual(0, result[0], result)
        report = json.loads(result[1])
        self.assertEqual("fern.run_show.v1", report["schema"])
        self.assertEqual("viewhash00000001", report["run"]["run_hash"])
        self.assertEqual(1.25, report["run"]["mse"])
        text = _run([*arguments, "--format", "text"])
        self.assertEqual(0, text[0], text)
        self.assertIn("run_hash:   viewhash00000001", text[1])
        self.assertIn("mse=1.25000", text[1])

    def test_show_run_unknown_hash_is_refused(self) -> None:
        result = _run(["show-run", "--db", str(self.db_path), "--run-hash", "nothere"])
        self.assertEqual((1, ""), (result[0], result[1]))
        self.assertIn("no run with run_hash=nothere", result[2])

    def test_list_runs_text_and_json(self) -> None:
        arguments = ["list-runs", "--db", str(self.db_path), "--limit", "10"]
        result = _run([*arguments, "--format", "json"])
        self.assertEqual(0, result[0], result)
        rows = json.loads(result[1])["runs"]
        self.assertEqual({"viewhash00000001": 1.25, "viewhash00000002": 0.75},
                         {row["run_hash"]: row["mse"] for row in rows})
        text = _run([*arguments, "--format", "text"])
        self.assertEqual(0, text[0], text)
        for expected in ("viewhash00000001", "viewhash00000002", "1.25000", "0.75000"):
            self.assertIn(expected, text[1])

    def test_compare_runs_text_and_json(self) -> None:
        arguments = ["compare-runs", "--db", str(self.db_path)]
        result = _run([*arguments, "--format", "json"])
        self.assertEqual(0, result[0], result)
        rows = json.loads(result[1])["comparisons"]
        self.assertEqual(1, len(rows))
        self.assertEqual(("fern", 0.0, 2, 1.0),
                         (rows[0]["model_type"], rows[0]["noise_sigma"], rows[0]["n_seeds"], rows[0]["avg_mse"]))
        text = _run([*arguments, "--format", "text"])
        self.assertEqual(0, text[0], text)
        self.assertIn("fern", text[1])
        self.assertIn("1.00000", text[1])

    def test_explain_attempt_unknown_id_does_not_modify_current_registry(self) -> None:
        registry = Path(self.tmp.name) / "current.db"
        initialized = _run(["initialize-registry", "--registry", str(registry)])
        self.assertEqual(0, initialized[0], initialized)
        before = registry.read_bytes()
        result = _run(["explain-attempt", "no-such-attempt", "--registry", str(registry)])
        self.assertEqual((1, ""), (result[0], result[1]))
        self.assertIn("no-such-attempt", result[2])
        self.assertEqual(before, registry.read_bytes())


if __name__ == "__main__":
    unittest.main()
