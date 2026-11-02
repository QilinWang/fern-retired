"""``python -m fern_refresh``: run the campaign refresh chain and report.

Ported from the ``refresh`` subcommand of ``crates/fern-statistics/src/main.rs``
(``fn refresh``) and its interpreter resolver (``python_interpreter`` in
``lib.rs``) on 2026-08-27. It preserves the dry-run table, JSON schema
(``fern.statistics_refresh.v1``), advisory-failure lines, and exit decision;
its explicit authority selectors differ where the Python process cannot inherit
the retired executable's identity. Scientific layers now call the typed Python
``fern_analysis compute`` front door directly; there is no second Rust wrapper.

Lab citations run through the explicit digest-fixed ``--fern-registry`` binary;
The corrections digest runs through ``--fern-run``. Every external check receives the
chain's declared thirty-second subprocess deadline.
"""

from __future__ import annotations

import json
import os
import shutil
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Annotated, Literal

import cappa

from fern_refresh.chain import COMPUTE_DRIFT_HELP, STALE_ARTIFACT_HELP, OnFailure, campaign_refresh_chain
from fern_refresh.runner import StepOutcome, chain_is_trustworthy, run_chain


class BinaryNotFound(Exception):
    """A required interpreter or binary could not be resolved.

    The message names every place that was looked at, because "cannot run X"
    with no path is the error that reads like a missing data file.
    """


def python_interpreter(asked_for: Path | None) -> Path:
    """Which Python runs the analyses and the statistics layers.

    Resolved, never defaulted. `<repository>/.venv/bin/python` was compiled into
    three commands: correct on one laptop and wrong on every other machine,
    including the Worker Site (correction 281). And a default string makes
    "not supplied" indistinguishable from "supplied that exact value", which is
    how an omitted `--python` came to be refused for disagreeing with a profile
    (correction 284).

    Order: what the caller asked for, then `$FERN_PYTHON`, then the interpreter
    on PATH. The failure names every place it looked.
    """
    if asked_for is not None:
        if asked_for.is_file():
            return asked_for
        raise BinaryNotFound(f"--python is {asked_for}, which is not a file")
    declared = os.environ.get("FERN_PYTHON")
    if declared is not None:
        path = Path(declared)
        if path.is_file():
            return path
        raise BinaryNotFound(f"FERN_PYTHON is {declared!r}, which is not a file")
    on_path = shutil.which("python3") or shutil.which("python")
    if on_path is not None:
        return Path(on_path)
    raise BinaryNotFound("no Python: pass --python, set FERN_PYTHON, or put python3 on PATH")


@cappa.command(
    name="fern-refresh",
    help="Recompute every paper statistic in dependency order, then check the results.",
    description=(
        "The chain is declared once in fern_refresh.chain and runs in dependency order: the "
        "injection audit first, because if a lane's injected energy has stopped tracking the "
        "dose then every number computed from that lane afterwards must be read with that in "
        "mind; the checks last, because they judge what the layers just produced. Use --plan to "
        "see the whole chain, what each step would run, and whether its failure is fatal or "
        "advisory, without running anything."
    ),
)
@dataclass(frozen=True)
class Refresh:
    """The campaign refresh chain: run it, or print what it would run.

    Three arguments select the data, the machine, or the identity, and none of
    them carries a default. A default here makes "not supplied" indistinguishable
    from "supplied that exact value", which is how an omitted `--python` came to
    be refused for disagreeing with a profile. `--root` keeps its `.` default,
    because it selects none of those three.
    """

    fern_registry: Annotated[
        Path,
        cappa.Arg(
            long=True,
            # CHG 2026-09-26 (Registry schema 20): it admits nothing now; compute
            # passes it to the accepted-matrix export freshness check.
            help="fern-registry binary that checks an accepted-matrix export is current",
        ),
    ]
    fern_run: Annotated[
        Path,
        cappa.Arg(
            long=True,
            help="digest-fixed fern-run binary that checks the corrections digest",
        ),
    ]
    python: Annotated[
        Path | None,
        cappa.Arg(
            long=True,
            help="interpreter that runs the statistics layers; otherwise $FERN_PYTHON, otherwise python3 on PATH",
        ),
    ] = None
    root: Annotated[
        Path,
        cappa.Arg(long=True, help="repository root; every step runs with this as its working directory"),
    ] = Path(".")
    plan: Annotated[
        bool,
        cappa.Arg(
            long=True,
            help="print the chain in order and exit, changing nothing",
        ),
    ] = False
    format: Annotated[
        Literal["text", "json"],
        cappa.Arg(long=True, help="how to report each step: a table, or fern.statistics_refresh.v1 JSON"),
    ] = "text"
    only: Annotated[
        str | None,
        cappa.Arg(
            long=True,
            help=(
                "run only the steps whose program or description contains this text, for re-running "
                "one step after fixing it. The run says how many steps it skipped, so a filtered run "
                "cannot be read as a full refresh."
            ),
        ),
    ] = None

    def __call__(self) -> int:
        return _run(self)


def _run(args: Refresh) -> int:
    try:
        python = python_interpreter(args.python)
    except BinaryNotFound as refusal:
        print(refusal, file=sys.stderr)
        return 1

    steps = campaign_refresh_chain(
        str(python),
        str(args.fern_registry),
        str(args.fern_run),
    )
    declared = len(steps)
    if args.only is not None:
        steps = [
            step
            for step in steps
            if args.only in " ".join(step.executable_argv()) or args.only in step.description
        ]
        if not steps:
            print(f"--only {args.only} matched none of the {declared} steps", file=sys.stderr)
            return 1

    if args.plan:
        print(f"{len(steps)} steps, in order:")
        for step in steps:
            kind = "layer" if step.artifact is not None else "check"
            gate = "fatal" if step.on_failure is OnFailure.STOP else "advisory"
            print(f"  {kind:<6} {gate:<9} {' '.join(step.executable_argv()):<52} {step.description}")
        return 0

    try:
        reports = run_chain(steps, args.root)
    except OSError as error:
        print(f"cannot run the refresh chain: {error}", file=sys.stderr)
        return 1

    if args.format == "json":
        print(
            json.dumps(
                {
                    "schema": "fern.statistics_refresh.v1",
                    "steps": [report.model_dump(mode="json") for report in reports],
                },
                indent=2,
            )
        )
    else:
        print(f"{len(reports)} steps")
        print("-" * 96)
        for report in reports:
            name = report.artifact if report.artifact is not None else report.program
            print(
                f"  {report.outcome.label():<9} {report.seconds:>4}s  {name.rsplit('/', 1)[-1]:<48} {report.description}"
            )
            if report.failure_output is not None:
                for line in report.failure_output.splitlines():
                    print(f"        {line}")
        print("-" * 96)

    for step, report in zip(steps, reports):
        if report.outcome is StepOutcome.FAILED and step.on_failure is OnFailure.REPORT:
            print(f"reported, not fatal: {report.description}", file=sys.stderr)
    if any(r.outcome is StepOutcome.FAILED and "code did not change" in r.description for r in reports):
        print(COMPUTE_DRIFT_HELP, file=sys.stderr)
    if any(r.outcome is StepOutcome.FAILED and "newer than its data" in r.description for r in reports):
        print(STALE_ARTIFACT_HELP, file=sys.stderr)

    if chain_is_trustworthy(steps, reports):
        changed = sum(1 for r in reports if r.outcome is StepOutcome.CHANGED)
        if args.only is not None:
            # A filtered run proves nothing about the steps it did not run, and
            # the line it prints must not be mistakable for the one above.
            print(
                f"{len(steps)} of {declared} steps run (--only {args.only}); "
                f"{changed} artifact(s) moved. This is NOT a full refresh."
            )
        else:
            print(f"all layers current ({changed} artifact(s) moved)")
        return 0
    print("the chain failed; its outputs are not current", file=sys.stderr)
    return 1


def main(argv: list[str] | None = None) -> int:
    """Kept so a caller can invoke the chain without a process boundary."""
    return cappa.invoke(Refresh, argv=argv)


if __name__ == "__main__":
    raise SystemExit(main())
