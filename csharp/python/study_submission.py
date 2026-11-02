"""Save one compiled Study and optionally submit it through the existing Registry CLI.

The ordered boundary is pinned in docs/how-to/study-compilation-and-queue.md.
This module owns files and submission observations, never scheduling or training.
"""
from __future__ import annotations

import json
import os
import subprocess
import tempfile
from collections.abc import Mapping
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any
from uuid import uuid4

TOOL = "fern_analysis.study.compile/v1"
COMMAND_TIMEOUT_SECONDS = 30


@dataclass(frozen=True)
class PublishedStudy:
    path: Path
    # CHG 2026-09-26 (owner: "get rid of 256 entirely"): the compiled bytes themselves,
    # compared before queuing, where this was their SHA-256.
    content: bytes
    provenance: Path
    reference: Path | None


@dataclass(frozen=True)
class QueueRequest:
    command: tuple[str, ...]
    registry: Path
    source: int
    name: str
    revision: int


@dataclass(frozen=True)
class QueueObservation:
    study_id: int
    requested_state: str
    monitor: str
    receipt: Path


def _segment(value: str) -> str:
    if not isinstance(value, str) or not value.strip() or value in {".", ".."} or any(c in value for c in "/\\\x00\n\r"):
        raise ValueError(f"expected one nonempty path segment, got {value!r}")
    return value


def _json_bytes(value: Mapping[str, Any]) -> bytes:
    return (json.dumps(value, indent=2, sort_keys=True, ensure_ascii=False, allow_nan=False) + "\n").encode("utf-8")


def _publish(path: Path, content: bytes) -> None:
    """Atomically publish complete bytes once; an existing different file refuses."""
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=".publishing-", dir=path.parent)
    try:
        with os.fdopen(descriptor, "wb") as target:
            target.write(content)
            target.flush()
            os.fsync(target.fileno())
        try:
            os.link(temporary, path)
        except FileExistsError:
            if path.is_symlink() or path.read_bytes() != content:
                raise ValueError(f"existing artifact has different bytes: {path}") from None
    finally:
        Path(temporary).unlink(missing_ok=True)


def _numbered(home: Path, name: str, content: bytes) -> Path:
    """The numbered folder under home whose `name` holds exactly these bytes, or the next free number.

    Example: _numbered(Path("central/lorenz"), "compiled.json", content) -> central/lorenz/2.
    CHG 2026-09-26: compiled studies were stored under the SHA-256 of their bytes; a number
    and a byte comparison put the same content in the same folder, and a folder is readable.
    """
    numbers = sorted(int(p.name) for p in home.iterdir() if p.is_dir() and p.name.isdigit()) if home.is_dir() else []
    for number in numbers:
        existing = home / str(number) / name
        if existing.is_file() and not existing.is_symlink() and existing.read_bytes() == content:
            return home / str(number)
    return home / str((numbers[-1] if numbers else 0) + 1)


def _numbered_file(folder: Path, prefix: str, content: bytes) -> Path:
    """`<prefix><n>.json` in folder holding exactly these bytes, or the next free number."""
    numbers = sorted(int(p.stem[len(prefix):]) for p in folder.glob(prefix + "*.json")
                     if p.stem[len(prefix):].isdigit()) if folder.is_dir() else []
    for number in numbers:
        if (folder / f"{prefix}{number}.json").read_bytes() == content:
            return folder / f"{prefix}{number}.json"
    return folder / f"{prefix}{(numbers[-1] if numbers else 0) + 1}.json"


def compiled_home(program: Path) -> Path:
    """Where a mini-program's compiled studies live: runtime/compiled/<program>/<mini-program>, out of docs/.

    Example: docs/programs/2026-09-12-fern-ablation/2026-09-24-beyond-nll ->
    runtime/compiled/2026-09-12-fern-ablation/2026-09-24-beyond-nll. A folder outside docs/programs refuses.
    """
    root = Path(__file__).resolve().parents[2]
    folder = program.resolve()
    if not folder.is_relative_to(root / "docs" / "programs"):
        raise ValueError(f"--program names a folder under docs/programs, not {program}")
    return root / "runtime" / "compiled" / folder.relative_to(root / "docs" / "programs")


def publish_compilation(compiled: Mapping[str, Any], *, card: Path, card_bytes: bytes,
                        into: Path, program: Path | None, slice_name: str | None) -> PublishedStudy:
    """Save compiled bytes in a numbered folder and, with a program, a reference beside them naming it.

    CHG 2026-09-26: the reference sits beside the content (compiled studies are
    generated, so none of them is written into docs/); the mini-program finds its
    compiled studies under compiled_home(program), the default of `study compile`.

    Example: into=Path('studies/compiled'), program=Path('docs/programs/research/experiment'),
    slice_name='two-seeds'. The reference carries provenance, not another editable card.
    """
    # Pass 1: validate the observation and destination before writing anything.
    name = _segment(compiled["study"])
    if compiled["schema"] != "fern.compiled-study.v1":
        raise ValueError("expected fern.compiled-study.v1")
    if (program is None) != (slice_name is None):
        raise ValueError("--program and --slice must be provided together")
    if program is not None:
        _segment(slice_name)
        if not program.is_dir():
            raise ValueError(f"name an existing Program or mini-program directory: {program}")
        program = program.resolve()
    if card.read_bytes() != card_bytes:
        raise ValueError(f"card changed during compilation: {card}")
    content = _json_bytes(compiled)
    folder = _numbered(into.resolve() / name, "compiled.json", content)
    if not folder.resolve().is_relative_to(into.resolve()):
        raise ValueError("compiled destination escapes the selected output directory through a symlink")
    path = folder / "compiled.json"
    provenance_data = {
        "schema": "fern.study_compilation.v1", "generated_by": TOOL,
        "commit": compiled["commit"],  # CHG 2026-09-26: beside compiled.json; no digest of it
        "card": str(card.resolve()),  # CHG 2026-09-26: no digest of the card's bytes; the path and the commit name it
        "program": str(program) if program else None, "slice": slice_name,
    }
    provenance_bytes = _json_bytes(provenance_data)
    provenance = _numbered_file(folder, "provenance-", provenance_bytes)
    reference = None
    if program is not None:
        # CHG 2026-09-26: the reference is generated, so it sits beside the
        # compiled content it points to, not inside the program's docs folder.
        reference = folder / provenance.name.replace("provenance-", "reference-", 1)
    # Pass 2: content first, then this invocation's provenance, then the Program reference.
    _publish(path, content)
    _publish(provenance, provenance_bytes)
    if reference is not None:
        _publish(reference, _json_bytes({
            **provenance_data,
            "compiled": os.path.relpath(path, reference.parent),
            "provenance": os.path.relpath(provenance, reference.parent),
        }))
    return PublishedStudy(path, content, provenance, reference)


def queue_compilation(published: PublishedStudy, request: QueueRequest) -> QueueObservation:
    """Register once, then observe that exact name/revision without releasing pauses.

    A failed or uncertain response leaves its request and process evidence on disk.
    No retry, revision increment, Site installation or supervisor launch is implicit.
    """
    # Pass 3: explicit existing Home, unchanged compiled bytes, and durable request.
    _segment(request.name)
    if not request.registry.is_file():
        raise ValueError(f"--queue requires an existing Registry Home: {request.registry}")
    if not request.command or request.source <= 0 or request.revision <= 0:
        raise ValueError("--queue requires a selected fern-run, positive source and revision")
    content = published.path.read_bytes()
    if content != published.content:
        raise ValueError("compiled content changed before queue submission")
    compiled = json.loads(content)
    if not compiled["cells"]:
        raise ValueError("--queue requires at least one runnable cell")
    token = "queue-" + uuid4().hex
    base = published.path.parent
    registry_args = ["--registry", str(request.registry.resolve())]
    command = [*request.command, "register-study", "--from", str(published.path), *registry_args,
               "--source", str(request.source), "--name", request.name, "--revision", str(request.revision),
               "--device", compiled["device"]]
    evidence = {
        "schema": "fern.study_queue_observation.v1", "generated_by": TOOL,
        "requested_at": datetime.now(UTC).isoformat(),
        "compiled": str(published.path), "provenance": str(published.provenance),
        "registry": str(request.registry.resolve()), "source": request.source,
        "name": request.name, "revision": request.revision, "command": command,
    }
    _publish(base / f"{token}.request.json", _json_bytes(evidence))

    def call(arguments: list[str], stage: str) -> subprocess.CompletedProcess:
        try:
            answered = subprocess.run(arguments, capture_output=True, text=True, check=False, timeout=COMMAND_TIMEOUT_SECONDS,
                                      env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"})
        except (OSError, subprocess.TimeoutExpired) as error:
            _publish(base / f"{token}.unconfirmed.json", _json_bytes({**evidence, "stage": stage, "detail": str(error)}))
            raise ValueError(f"queue observation unconfirmed during {stage}; inspect {base / (token + '.request.json')}; {error}") from error
        _publish(base / f"{token}.{stage}.json", _json_bytes({
            **evidence, "exit_code": answered.returncode, "stdout": answered.stdout, "stderr": answered.stderr,
        }))
        return answered

    # Pass 4: the existing C# owner performs the write exactly once.
    registered = call(command, "registration")
    if registered.returncode != 0:
        if registered.returncode not in {2, 3}:
            _publish(base / f"{token}.unconfirmed.json", _json_bytes({**evidence, "exit_code": registered.returncode}))
            raise ValueError(f"registration outcome unconfirmed (exit {registered.returncode}); inspect the saved request before any retry")
        _publish(base / f"{token}.refused.json", _json_bytes({**evidence, "exit_code": registered.returncode}))
        raise ValueError(f"registration refused (exit {registered.returncode}); no automatic retry: {registered.stderr.strip() or registered.stdout.strip()}")

    # Pass 5: read actual Registry identity and state; the current monitor selects by name.
    listed = call([*request.command, "list-studies", *registry_args, "--json"], "listing")
    if listed.returncode != 0:
        raise ValueError(f"registration command succeeded; Registry observation failed: {listed.stderr.strip()}")
    try:
        listing = json.loads(listed.stdout)
        if listing["schema"] != "fern.registered_studies.v1":
            raise ValueError("unexpected Registry listing schema")
        rows = [row for row in listing["studies"] if row["Name"] == request.name and row["Revision"] == request.revision]
        if len(rows) != 1 or rows[0]["Cells"] != len(compiled["cells"]):
            raise ValueError("registered name, revision or cell count does not match submission")
        row = rows[0]
        if row["RequestedState"] != "run":
            raise ValueError(f"registered study is {row['RequestedState']}; that request was preserved")
        if any(r["Name"] == request.name and r["Revision"] > request.revision for r in listing["studies"]):
            raise ValueError("registration succeeded, but monitor-study selects a newer revision; exact monitoring is unavailable")
        monitored = call([*request.command, "monitor-study", *registry_args, "--study", request.name], "monitor")
        expected = f"study {request.name} revision {request.revision}  state=run  "
        if monitored.returncode != 0 or not monitored.stdout.startswith(expected):
            raise ValueError("registration succeeded; monitor did not confirm the requested revision and run state")
    except (KeyError, TypeError, json.JSONDecodeError) as error:
        raise ValueError(f"registration succeeded; unreadable Registry observation: {error}") from error
    receipt = base / f"{token}.observed.json"
    _publish(receipt, _json_bytes({**evidence, "observed_at": datetime.now(UTC).isoformat(),
                                 "study_id": row["Id"], "requested_state": row["RequestedState"],
                                 "cells": row["Cells"], "accepted_cells": row["AcceptedCells"],
                                 "monitor": monitored.stdout}))
    return QueueObservation(row["Id"], row["RequestedState"], monitored.stdout, receipt)
