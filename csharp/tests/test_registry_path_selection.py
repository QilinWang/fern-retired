"""Registry addresses are independent of campaign storage; no implicit caller returns."""
import json
import os
from pathlib import Path
import re
import subprocess
import tomllib

ROOT = Path(__file__).resolve().parents[1]


def test_production_callers_cannot_derive_the_central_registry_from_a_campaign():
    violations = []
    for directory, children, files in os.walk(ROOT / "src" / "csharp"):
        children[:] = [
            child for child in children
            if child not in {"bin", "obj", "Fixtures"} and not child.endswith(".Tests")
        ]
        for name in files:
            if not name.endswith(".cs"):
                continue
            path = Path(directory) / name
            source = path.read_text()
            if re.search(r"\bCampaignLifecycleDb\s*\(", source):
                violations.append(str(path.relative_to(ROOT)))
            if re.search(r"Path\.(?:Combine|Join)\([^;]*CampaignRoot[^;]*[\"']lifecycle\.db", source):
                violations.append(str(path.relative_to(ROOT)))
            # Current and destination addresses belong to RegistryHome, not
            # another CLI default. Historical comments remain readable.
            if path != ROOT / "src/csharp/Fern.Registry/RegistryHome.cs":
                for line_number, line in enumerate(source.splitlines(), 1):
                    if line.lstrip().startswith(("//", "*")):
                        continue
                    if re.search(r'"[^"\n]*results/(?:paper_campaign/lifecycle|registry)\.db"', line):
                        violations.append(f"{path.relative_to(ROOT)}:{line_number}: duplicate Registry address")
    assert not violations, violations


def test_study_verbs_name_their_home_and_take_their_binaries_from_the_selection():
    # CHG 2026-09-11: the Plan layer and its StudyLaunch left the tree; a study
    # verb names its Registry Home explicitly and never derives one, and the
    # cell runner takes fern-run and fern-compile from the runtime selection
    # unless a caller names them.
    store = (ROOT / "src/csharp/Fern.Run.Cli/StudyStoreVerbs.cs").read_text()
    cells = (ROOT / "src/csharp/Fern.Run.Cli/RunStudyCellsVerbs.cs").read_text()
    site = (ROOT / "src/csharp/Fern.Run.Cli/SiteAndAdmissionVerbs.cs").read_text()
    assert 'new("--registry") { Description = "Registry Home holding the study.", Required = true }' in store
    assert 'new Option<string>("--store") { Description = "Registry Home holding the study.", Required = true }' in cells
    assert "RuntimeSelection.Read().CompileBinary" in cells and "RuntimeSelection.Read().RunBinary" in cells
    assert "RegistryHome.OpenWriter(parsed.GetValue(store)!)" in cells and "DefaultRegistryPath" not in cells  # the runner opens the Home it was given, never a default
    assert "registry = home.RegistryPath;" in site
    assert "RequiredProfilePaths(ready.Value.Profile)" in site


def test_monitor_study_names_its_home_and_follows_without_opening_a_database():
    binary = os.environ.get("FERN_TEST_RUN_BIN", str(ROOT / "runtime/deploy/current/fern-run"))
    result = subprocess.run([binary, "monitor-study", "--help"], capture_output=True, text=True, check=False, timeout=10)
    assert result.returncode == 0, result.stderr
    assert "--registry" in result.stdout and "--follow" in result.stdout


def test_monitor_uses_the_existing_registry_default_owner():
    # CHG 2026-09-11: `watch` retired; `monitor-study --follow` is its successor,
    # and the preparation monitor keeps the one default-owner shape.
    source = (ROOT / "src/csharp/Fern.Run.Cli/MonitorVerbs.cs").read_text()
    assert "DefaultValueFactory = _ => string.Empty" in source
    assert "RegistryHome.DefaultRegistryPath" in (ROOT / "src/csharp/Fern.Run.Cli/Program.cs").read_text()


def test_repository_consumers_use_one_runtime_selection():
    """Check default ownership; explicit independent Registry selections remain legal."""
    source = (ROOT / "src/csharp/Fern.Registry/RegistryHome.cs").read_text()
    assert "DefaultRegistryPath => RuntimeSelection.Read().Registry" in source
    work = tomllib.loads((ROOT / "work.toml").read_text())
    assert work["registry"]["facts"][0].endswith("/runtime/deploy/current/fern-registry")
    assert "--registry" not in work["registry"]["facts"]
    assert "db" not in work["registry"]
    for name in ("local-4070", "local-4070-zeus"):
        profile = json.loads((ROOT / f"config/profiles/{name}.json").read_text())
        assert "registry_path" not in profile
    assert not (ROOT / "scripts/watch_registry_plans.py").exists()
