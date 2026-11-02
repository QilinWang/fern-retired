"""`profile init` on the C# host writes the same template bytes the Rust binary wrote.

The template is an operator-edited declaration, so the port reproduces the
serde field order and two-space layout by hand; this test is the proof, for
both executors, plus the four refusals. It runs both binaries in a scratch
working directory (the C# verb writes `config/profiles/<id>.json` and the Rust one
wrote `profiles/<id>.json`, relative to the cwd)
and skips, with the reason, when a binary is absent or the Rust one no longer
serves the verb.
"""

from __future__ import annotations

import subprocess
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
RUST = REPO / "target" / "debug" / "fern-run"
CSHARP = REPO / "src" / "csharp" / "Fern.Run.Cli" / "bin" / "Debug" / "net11.0" / "fern-run"
SHA = "a" * 64


def _run(binary: Path, cwd: Path, args: list[str]) -> tuple[int, str, str]:
    proc = subprocess.run([str(binary), *args], cwd=cwd, text=True, capture_output=True, check=False)
    return proc.returncode, proc.stdout, proc.stderr


def _rust_serves_profile() -> bool:
    return RUST.exists() and "profile" in _run(RUST, REPO, ["--help"])[1]


@unittest.skipUnless(CSHARP.exists(), f"C# host {CSHARP} is not built")
class ProfileInitParityTests(unittest.TestCase):
    LOCAL = ["profile", "init", "golden-local", "--workspace-root", "/w/space", "--home-root", "/h/ome"]
    SSH = [
        "profile", "init", "golden-ssh", "--executor", "ssh", "--ssh-destination", "user@host",
        "--registry-home", "local-4070", "--worker-binary-sha256", SHA,
        "--workspace-root", "/remote/ws", "--home-root", "/remote/home",
    ]

    def test_templates_match_the_old_executable(self) -> None:
        if not _rust_serves_profile():
            self.skipTest("the Rust fern-run no longer serves `profile`; its side was recorded while both existed")
        for args in (self.LOCAL, self.SSH):
            with tempfile.TemporaryDirectory() as rust_dir, tempfile.TemporaryDirectory() as cs_dir:
                rust = _run(RUST, Path(rust_dir), args)
                csharp = _run(CSHARP, Path(cs_dir), args)
                self.assertEqual((rust[0], rust[1]), (csharp[0], csharp[1]), args)
                name = args[2] + ".json"
                self.assertEqual(
                    (Path(rust_dir) / "profiles" / name).read_bytes(),
                    (Path(cs_dir) / "config" / "profiles" / name).read_bytes(),
                    f"template bytes differ for {name}",
                )

    def test_local_template_is_the_frozen_layout(self) -> None:
        """The C#-only half: the local template's exact bytes, independent of the Rust binary."""
        with tempfile.TemporaryDirectory() as cs_dir:
            code, out, _ = _run(CSHARP, Path(cs_dir), self.LOCAL)
            self.assertEqual(0, code, out)
            written = (Path(cs_dir) / "config" / "profiles" / "golden-local.json").read_text()
        self.assertTrue(written.startswith('{\n  "schema": "fern.execution_profile.v5",\n  "profile_id": "golden-local",\n  "executor": "local",\n  "device": "cuda",\n'))
        self.assertIn('  "python": "/h/ome/.venv/bin/python",\n', written)
        self.assertTrue(written.endswith('  "warn_free_gib": 50,\n  "warn_free_percent": 10\n}\n'))

    def test_refusals_match_the_old_executable(self) -> None:
        if not _rust_serves_profile():
            self.skipTest("the Rust fern-run no longer serves `profile`")
        cases = [
            ["profile", "init", "Bad_Id"],
            ["profile", "init", "loc", "--ssh-destination", "h"],
            ["profile", "init", "rem", "--executor", "ssh"],
        ]
        for args in cases:
            with tempfile.TemporaryDirectory() as rust_dir, tempfile.TemporaryDirectory() as cs_dir:
                rust = _run(RUST, Path(rust_dir), args)
                csharp = _run(CSHARP, Path(cs_dir), args)
                self.assertEqual(rust[0], csharp[0], args)
                self.assertEqual(rust[2].strip(), csharp[2].strip(), args)
                self.assertFalse((Path(cs_dir) / "config" / "profiles").exists(), "a refusal must write nothing")

    def test_existing_profile_is_refused_without_rewrite(self) -> None:
        with tempfile.TemporaryDirectory() as cs_dir:
            first = _run(CSHARP, Path(cs_dir), self.LOCAL)
            self.assertEqual(0, first[0], first)
            before = (Path(cs_dir) / "config" / "profiles" / "golden-local.json").read_bytes()
            again = _run(CSHARP, Path(cs_dir), self.LOCAL)
            self.assertEqual(1, again[0])
            self.assertIn("already exists; edit it deliberately or choose a new profile id", again[2])
            self.assertEqual(before, (Path(cs_dir) / "config" / "profiles" / "golden-local.json").read_bytes())


if __name__ == "__main__":
    unittest.main()
