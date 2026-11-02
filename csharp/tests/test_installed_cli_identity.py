"""Read installed CLI identities without the development environment.

Environment isolation removes HOME/PATH/SDK discovery. The optional bwrap
case also hides the checkout's C# tree and SDK directories in a child mount
namespace; it never renames or writes those shared directories. These probes
exercise version reporting, not Registry access, migration, or execution.
"""

import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tomllib

import pytest


ROOT = Path(__file__).resolve().parents[1]
PROTOCOL = "fern.run_plan_parent_child.v2"
pytestmark = pytest.mark.skipif(sys.platform != "linux", reason="installed Fern binaries target Linux")


@pytest.mark.parametrize("newest", ["Debug", "Release"])
def test_python_cli_bridge_preserves_existing_checkout_fallback(tmp_path, monkeypatch, newest):
    from fern_train import worker

    for configuration in ("Debug", "Release"):
        binary = tmp_path / "csharp/Fern.Run.Cli/bin" / configuration / "net11.0/fern-run"
        binary.parent.mkdir(parents=True)
        binary.write_text("unselected development executable")
        os.utime(binary, (200 if configuration == newest else 100,) * 2)
    monkeypatch.setattr(worker, "REPO_ROOT", tmp_path)
    monkeypatch.delenv("FERN_RUN_BIN", raising=False)
    assert worker.fern_run_csharp_cmd() == [str(tmp_path / "csharp/Fern.Run.Cli/bin" / newest / "net11.0/fern-run")]


@pytest.mark.parametrize("value", ["", "   ", '""'])
def test_python_cli_bridge_refuses_an_empty_explicit_command(value, monkeypatch):
    from fern_train import worker

    monkeypatch.setenv("FERN_RUN_BIN", value)
    with pytest.raises(ValueError, match="nonempty"):
        worker.fern_run_csharp_cmd()


def test_python_cli_bridge_preserves_explicit_argv(monkeypatch):
    from fern_train import worker

    monkeypatch.setenv("FERN_RUN_BIN", '"/selected binary/fern-run" --fixed-argument value')
    assert worker.fern_run_csharp_cmd() == ["/selected binary/fern-run", "--fixed-argument", "value"]


def _work_registry() -> dict:
    return tomllib.loads((ROOT / "work.toml").read_text(encoding="utf-8"))["registry"]


def _registry_installation() -> tuple[Path, Path, dict]:
    binary = Path(_work_registry()["facts"][0])
    assert binary.is_absolute(), "Work must select the installed Registry by absolute path"
    assert re.fullmatch(r"fern-registry\.[0-9a-f]{64}", binary.name), binary
    receipt_path = Path(str(binary) + ".receipt.json")
    if not os.path.lexists(binary) and not os.path.lexists(receipt_path):
        pytest.skip(f"machine-local Work Registry installation is absent: {binary}")
    assert binary.is_file(), f"installed Registry binary is missing or not a file: {binary}"
    assert receipt_path.is_file(), f"installed Registry has no receipt: {receipt_path}"
    receipt = json.loads(receipt_path.read_bytes())
    # v3 (2026-09-11 night) adds input_digest; an earlier v2 publish is still a clean build.
    assert receipt["schema"] in {"fern.registry-binary-publish.v2", "fern.registry-binary-publish.v3"}, receipt_path
    assert receipt["published_path"] == str(binary)
    assert receipt["provenance"] == "fern-registry-version-json"
    assert receipt["source_version_probe"] == "ok"
    return binary, receipt_path, receipt


def _run_installation() -> tuple[Path, Path, dict]:
    registry = _work_registry()
    deploy = Path(registry["facts"][0]).parent.parent
    receipts = []
    for path in (deploy / "scheduler-services").glob("*/*.json"):
        receipt = json.loads(path.read_bytes())
        if receipt["schema"] == "fern.scheduler_service_installation.v10" and receipt["registry"] == registry["db"]:
            receipts.append((receipt["installed_at_unix_ms"], path, receipt))
    if not receipts:
        pytest.skip("no machine-local v10 Scheduler installation receipt for Work's Registry")
    # Materialization time selects this proof's receipt, not a running service
    # or BuildInventory's active pointer. No service-state claim is made here.
    _, receipt_path, receipt = max(receipts, key=lambda item: (item[0], str(item[1])))
    binary = Path(receipt["run_binary_path"])
    assert binary.is_absolute() and binary.is_file(), f"receipted fern-run is missing: {binary}"
    assert binary.name == "fern-run." + receipt["run_binary_sha256"]
    assert receipt["binary_set_id"] == "sha256:" + receipt_path.stem
    assert receipt["run_plan_protocol"] == PROTOCOL
    return binary, receipt_path, receipt


def _isolated_environment(tmp_path: Path) -> tuple[Path, dict[str, str]]:
    paths = {name: tmp_path / name for name in ("cwd", "home", "empty-path", "empty-sdk", "extract", "tmp", "cache")}
    for path in paths.values():
        path.mkdir()
    return paths["cwd"], {
        "HOME": str(paths["home"]),
        "PATH": str(paths["empty-path"]),
        "DOTNET_ROOT": str(paths["empty-sdk"]),
        "DOTNET_ROOT_X64": str(paths["empty-sdk"]),
        "DOTNET_CLI_HOME": str(paths["home"]),
        "DOTNET_MULTILEVEL_LOOKUP": "0",
        "DOTNET_BUNDLE_EXTRACT_BASE_DIR": str(paths["extract"]),
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_NOLOGO": "1",
        "TMPDIR": str(paths["tmp"]),
        "XDG_CACHE_HOME": str(paths["cache"]),
        "LANG": "C.UTF-8",
    }


def _namespace_prefix(tmp_path: Path, cwd: Path, env: dict[str, str]) -> list[str]:
    bwrap = shutil.which("bwrap")
    if bwrap is None:
        pytest.skip("bwrap is unavailable; only the separate environment-isolation case is proved")
    prefix = [bwrap, "--die-with-parent", "--unshare-user", "--unshare-pid",
              "--ro-bind", "/", "/", "--bind", str(tmp_path), str(tmp_path),
              "--proc", "/proc", "--dev", "/dev"]
    hidden = {ROOT / "src" / "csharp", Path.home() / ".dotnet", Path("/usr/share/dotnet"), Path("/usr/lib/dotnet")}
    for path in sorted({path.resolve() for path in hidden if path.is_dir()}):
        prefix.extend(["--tmpfs", str(path)])
    prefix.extend(["--chdir", str(cwd), "--"])
    probe = subprocess.run([*prefix, "/usr/bin/true"], env=env, cwd=cwd,
                           capture_output=True, text=True, timeout=10, check=False)
    if probe.returncode != 0 and any(detail in probe.stderr.lower() for detail in (
        "no permissions to create new namespace", "creating new namespace failed: operation not permitted",
        "unprivileged user namespaces are not enabled",
    )):
        pytest.skip(f"kernel refuses bwrap namespaces; environment-isolation case remains required: {probe.stderr.strip()}")
    assert probe.returncode == 0, probe.stderr
    return prefix


def _digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


@pytest.mark.parametrize("role", ["registry", "run"])
@pytest.mark.parametrize("isolation", ["environment", "mount_namespace"])
def test_installed_cli_identity_matches_its_receipt(role: str, isolation: str, tmp_path: Path) -> None:
    binary, receipt_path, receipt = _registry_installation() if role == "registry" else _run_installation()
    receipt_bytes = receipt_path.read_bytes()
    digest = _digest(binary)
    assert digest == receipt["sha256" if role == "registry" else "run_binary_sha256"]
    assert binary.name == f"fern-{role}.{digest}"
    if role == "registry":
        assert type(receipt["byte_size"]) is int
        assert binary.stat().st_size == receipt["byte_size"]
    cwd, env = _isolated_environment(tmp_path)
    prefix = _namespace_prefix(tmp_path, cwd, env) if isolation == "mount_namespace" else []
    arguments = [str(binary), "version", "--json"] if role == "registry" else [str(binary), "version"]
    result = subprocess.run([*prefix, *arguments], cwd=cwd, env=env,
                            capture_output=True, text=True, timeout=10, check=False)
    assert result.returncode == 0, result.stderr[-3000:]
    assert len(result.stdout.encode()) <= 65536 and len(result.stderr.encode()) <= 65536
    report = json.loads(result.stdout)
    assert report["schema"] == ("fern.registry_binary_version.v1" if role == "registry" else "fern.binary_version.v1")
    assert report["run_plan_protocol"] == PROTOCOL
    assert re.fullmatch(r"[0-9a-f]{40}", report["built_from_commit"])
    assert type(report["built_from_dirty_tree"]) is bool
    archive = report.get("source_archive_sha256")
    assert archive is None or re.fullmatch(r"[0-9a-f]{64}", archive)
    if role == "registry":
        assert report["role"] == "registry-home"
        for field in ("built_from_commit", "built_from_dirty_tree", "source_archive_sha256", "dotnet_runtime"):
            assert report[field] == receipt[field], field
    else:
        # The v10 set receipt binds executable bytes and protocol, and does not
        # carry build commit, dirty flag, archive digest, size, or runtime.
        assert report["run_plan_protocol"] == receipt["run_plan_protocol"]
    assert _digest(binary) == digest, "version reporting changed the installed executable"
    assert receipt_path.read_bytes() == receipt_bytes, "version reporting changed its install receipt"
