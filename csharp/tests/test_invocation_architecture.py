"""Canonical JSON hashing tests.

Rust mints run identity and run hash through the `fern-run` parent commands,
and (Phase 3) the Rust parent also writes `invocation.json` at enroll time —
the Python envelope writers `assemble_run_invocation()` / `write_invocation()`
are frozen at retired/fr_tr/python_receipt_writers_2026_07_01.py, their
envelope tests at retired/fr_tr/python_invocation_envelope_tests_2026_07_01.py,
and the envelope assertions now live in Rust
(fern-cli-rs/tests/cli/db_lifecycle_cli.rs). What stays active in `fr_tr.py` is the
canonical JSON hashing surface (`canonical_value` / `canonical_json_bytes` /
`canonical_hash_16`), tested here for key-order stability.
"""
from __future__ import annotations

import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

from fern_train.specs import DataLoaderSpec, DeviceSpec
from fern_train.train import canonical_hash_16
from fern_train.worker import canonical_json_bytes


class CanonicalHashTests(unittest.TestCase):
    def test_migrated_runtime_specs_keep_field_based_canonical_bytes(self) -> None:
        values = {
            "hw": DeviceSpec(device_type="cpu", dtype_type="float32", amp_dtype="none"),
            "loader": DataLoaderSpec(batch_size=7, num_workers=0),
        }
        self.assertEqual(
            canonical_json_bytes(values),
            b'{"hw":{"amp_dtype":"none","device_type":"cpu","dtype_type":"float32"},'
            b'"loader":{"batch_size":7,"num_workers":0}}',
        )

    def test_canonical_hash_is_order_stable_for_recipe_hashes(self) -> None:
        left = {"schema": "fern.titration_recipe.v1", "recipe": {"b": 2, "a": [1, 2]}}
        right = {"recipe": {"a": [1, 2], "b": 2}, "schema": "fern.titration_recipe.v1"}
        self.assertEqual(canonical_hash_16(left), canonical_hash_16(right))


class CSharpCompositionRootTests(unittest.TestCase):
    def test_cli_projects_are_terminal_composition_roots(self) -> None:
        repository = Path(__file__).resolve().parents[1]
        violations: list[str] = []
        for project in sorted((repository / "src" / "csharp").glob("*/*.csproj")):
            source_name = project.stem
            for reference in ET.parse(project).getroot().iter("ProjectReference"):
                target_name = Path(reference.attrib["Include"].replace("\\", "/")).stem
                if target_name.endswith(".Cli") and not source_name.endswith(".Cli"):
                    violations.append(f"{source_name} -> {target_name}")
                if source_name == "Fern.Registry.Cli" and target_name == "Fern.Scheduler":
                    violations.append(f"{source_name} -> {target_name}")

        self.assertEqual(
            [],
            violations,
            "CLI projects are terminal adapters: no library or test project may reference one",
        )


if __name__ == "__main__":
    unittest.main()
