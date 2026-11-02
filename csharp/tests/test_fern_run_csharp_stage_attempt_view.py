"""Stage-attempt CLI refusals use explicit temporary Registry databases.

The positive projection and rendering cases live in C# StageAttemptViewTests
and FernRunHostContractTests. No test in this module reads the old production
demo, selects a default Registry, or copies Registry creation SQL.
"""

from __future__ import annotations

import sqlite3
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
CSHARP = REPO_ROOT / "src/csharp/Fern.Run.Cli/bin/Debug/net11.0/fern-run"


def invoke(arguments: list[str]) -> tuple[int, str, str]:
    finished = subprocess.run([str(CSHARP), *arguments], cwd=REPO_ROOT,
                              text=True, capture_output=True, check=False, timeout=30)
    return finished.returncode, finished.stdout, finished.stderr


@unittest.skipUnless(CSHARP.is_file(), f"the current C# test host is not built: {CSHARP}")
class StageAttemptViewTests(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.registry = Path(self.tmp.name) / "current.db"
        result = invoke(["initialize-registry", "--registry", str(self.registry)])
        self.assertEqual(0, result[0], result)

    def test_unknown_plan_is_refused_without_changing_current_registry(self) -> None:
        before = self.registry.read_bytes()
        result = invoke(["show-stage-attempts", "--registry", str(self.registry),
                         "--plan", "no-such-plan-anywhere", "--format", "json"])
        self.assertEqual((1, ""), (result[0], result[1]))
        self.assertIn("no registered plan", result[2])
        self.assertEqual(before, self.registry.read_bytes())

    def test_missing_registry_is_not_created_by_a_view(self) -> None:
        missing = Path(self.tmp.name) / "missing.db"
        result = invoke(["show-stage-attempts", "--registry", str(missing),
                         "--plan", "no-such-plan-anywhere", "--format", "json"])
        self.assertEqual((1, ""), (result[0], result[1]))
        self.assertFalse(missing.exists())
        self.assertFalse(missing.with_name(missing.name + "-wal").exists())
        self.assertFalse(missing.with_name(missing.name + "-shm").exists())

    def test_noncurrent_database_is_refused_without_upgrade_or_import(self) -> None:
        prior = Path(self.tmp.name) / "prior.db"
        # An unrelated preexisting database is a refusal fixture, not a copy
        # of a historical Registry migration or production data.
        with sqlite3.connect(prior) as connection:
            connection.execute("CREATE TABLE prior_data (value TEXT NOT NULL)")
            connection.execute("INSERT INTO prior_data VALUES ('must remain unchanged')")
        before = prior.read_bytes()
        files_before = {path.name for path in Path(self.tmp.name).iterdir()}
        for command in (
            ["initialize-registry", "--registry", str(prior)],
            ["show-stage-attempts", "--registry", str(prior),
             "--plan", "no-such-plan-anywhere", "--format", "json"],
        ):
            with self.subTest(command=command[0]):
                result = invoke(command)
                self.assertNotEqual(0, result[0], result)
                self.assertEqual("", result[1])
                self.assertIn("archiv", result[2].lower())
                self.assertEqual(before, prior.read_bytes())
                self.assertEqual(files_before, {path.name for path in Path(self.tmp.name).iterdir()})


if __name__ == "__main__":
    unittest.main()
