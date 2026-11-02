"""Compilation storage and the public registration process; no training or new Registry."""
from __future__ import annotations

from fern_train import study_compile

import json
import sys

import pytest
from fern_analysis.study_submission import QueueRequest, publish_compilation, queue_compilation


@pytest.fixture
def compiled():
    return {"schema": "fern.compiled-study.v1", "study": "independent-experiment", "commit": "abc123",
            "device": "cuda", "cells": [{"cell": "control"}], "resolution": {"resolved": {"manifest": {}}}}


def publish(tmp_path, compiled, *, program=None):
    card = tmp_path / "card.json"
    card.write_text('{"study":"independent-experiment"}\n')
    return publish_compilation(compiled, card=card, card_bytes=card.read_bytes(), into=tmp_path / "central",
                               program=program, slice_name="first-experiment" if program else None)


def test_central_content_and_program_reference_share_exact_bytes(tmp_path, compiled):
    program = tmp_path / "program" / "mini-program"
    program.mkdir(parents=True)
    result = publish(tmp_path, compiled, program=program)
    assert json.loads(result.path.read_text()) == compiled
    reference = json.loads(result.reference.read_text())
    assert (result.reference.parent / reference["compiled"]).resolve() == result.path
    assert result.path.parent.name == "1"
    assert reference["slice"] == "first-experiment"
    repeated = publish(tmp_path, compiled, program=program)
    assert repeated.path == result.path
    compiled["cells"].append({"cell": "treatment"})
    changed = publish(tmp_path, compiled, program=program)
    assert changed.path != result.path
    assert changed.path.parent.name == "2"
    assert len(json.loads(result.path.read_text())["cells"]) == 1


def test_changed_card_cannot_be_published_and_an_edited_folder_is_not_reused(tmp_path, compiled):
    # CHG 2026-09-26: folders are numbered, not named by the content's SHA-256, so a
    # folder whose compiled.json was edited no longer matches and is left as it is;
    # the compiler's bytes go to the next number.
    result = publish(tmp_path, compiled)
    result.path.write_text("corrupt")
    again = publish(tmp_path, compiled)
    assert again.path.parent.name == "2" and json.loads(again.path.read_text()) == compiled
    assert result.path.read_text() == "corrupt"
    card = tmp_path / "card.json"
    with pytest.raises(ValueError, match="card changed"):
        publish_compilation(compiled, card=card, card_bytes=b"earlier", into=tmp_path / "other",
                            program=None, slice_name=None)
    assert not (tmp_path / "other").exists()


@pytest.mark.parametrize("name", ["..", "../escape", "a/b", "a\\b", ""])
def test_study_name_cannot_escape_output_directory(tmp_path, compiled, name):
    compiled["study"] = name
    with pytest.raises(ValueError):
        publish(tmp_path, compiled)
    assert not (tmp_path / "central").exists()


def fixture_command(tmp_path, *, register_exit=0, state="run", count=1, monitor_exit=0,
                    list_exit=0, monitor_revision=1, registration_sleep=0):
    """A separate CLI process, not a Registry implementation or training fixture."""
    script = tmp_path / "command.py"
    script.write_text(
        "import json,sys,time\nfrom pathlib import Path\n"
        "command=sys.argv[1]\n"
        "with Path(__file__).with_suffix('.calls').open('a') as f: f.write(command+'\\n')\n"
        "if command=='register-study':\n"
        " p=Path(sys.argv[sys.argv.index('--from')+1])\n"
        " assert p.is_file() and list(p.parent.glob('queue-*.request.json'))\n"
        " assert sys.argv[sys.argv.index('--device')+1]==json.loads(p.read_text())['device']\n"
        f" time.sleep({registration_sleep})\n"
        f" print('registration response'); raise SystemExit({register_exit})\n"
        "if command=='list-studies':\n"
        f" print(json.dumps({{'schema':'fern.registered_studies.v1','studies':[{{'Id':42,'Name':'independent-experiment','Revision':1,'RequestedState':{state!r},'Cells':{count},'AcceptedCells':0}}]}})); raise SystemExit({list_exit})\n"
        "if command=='monitor-study':\n"
        f" print('study independent-experiment revision {monitor_revision}  state={state}  0/1 cells accepted\\ncell control: awaiting a suitable Site'); raise SystemExit({monitor_exit})\n"
    )
    return (sys.executable, str(script))


def request(tmp_path, command):
    registry = tmp_path / "existing-registry.db"
    registry.touch()  # The process fixture does not open or create a database.
    return QueueRequest(command=command, registry=registry, source=7, name="independent-experiment", revision=1)


def test_queue_observes_registry_identity_and_does_not_claim_training(tmp_path, compiled):
    result = publish(tmp_path, compiled)
    observed = queue_compilation(result, request(tmp_path, fixture_command(tmp_path)))
    assert observed.study_id == 42
    assert observed.requested_state == "run"
    assert "awaiting" in observed.monitor
    assert (tmp_path / "command.calls").read_text().splitlines() == ["register-study", "list-studies", "monitor-study"]
    saved = json.loads(observed.receipt.read_text())
    assert saved["study_id"] == 42
    assert saved["compiled"] == str(result.path)
    assert "running" not in saved


def test_registration_refusal_is_recorded_without_claiming_queue_success(tmp_path, compiled):
    result = publish(tmp_path, compiled)
    with pytest.raises(ValueError, match="registration refused"):
        queue_compilation(result, request(tmp_path, fixture_command(tmp_path, register_exit=3)))
    assert (tmp_path / "command.calls").read_text().splitlines() == ["register-study"]
    assert not list(result.path.parent.glob("queue-*.observed.json"))
    assert list(result.path.parent.glob("queue-*.refused.json"))


@pytest.mark.parametrize("state,count,monitor_exit", [("pause", 1, 0), ("run", 2, 0), ("run", 1, 3)])
def test_paused_mismatched_or_unreadable_registration_is_not_queue_success(tmp_path, compiled, state, count, monitor_exit):
    result = publish(tmp_path, compiled)
    with pytest.raises(ValueError):
        queue_compilation(result, request(tmp_path, fixture_command(tmp_path, state=state, count=count, monitor_exit=monitor_exit)))
    assert not list(result.path.parent.glob("queue-*.observed.json"))
    assert "release-request" not in (tmp_path / "command.calls").read_text()


def test_missing_registry_is_refused_without_spawning_or_creating_it(tmp_path, compiled):
    result = publish(tmp_path, compiled)
    missing = tmp_path / "missing.db"
    req = QueueRequest(command=("must-not-run",), registry=missing, source=7, name="independent-experiment", revision=1)
    with pytest.raises(ValueError, match="existing Registry"):
        queue_compilation(result, req)
    assert not missing.exists()


def test_same_compiled_content_can_record_a_second_card_origin(tmp_path, compiled):
    program = tmp_path / "mini-program"
    program.mkdir()
    first = publish(tmp_path, compiled, program=program)
    card = tmp_path / "second-card.json"
    card.write_text('{ "study": "independent-experiment" }')
    second = publish_compilation(compiled, card=card, card_bytes=card.read_bytes(), into=tmp_path / "central",
                                 program=program, slice_name="first-experiment")
    assert first.path == second.path
    assert first.provenance != second.provenance
    assert first.reference != second.reference
    assert json.loads(second.provenance.read_text())["card"] == str(card)


def test_changed_compiled_bytes_refuse_before_process_start(tmp_path, compiled):
    published = publish(tmp_path, compiled)
    published.path.write_text("changed")
    with pytest.raises(ValueError, match="changed before queue"):
        queue_compilation(published, request(tmp_path, fixture_command(tmp_path)))
    assert not (tmp_path / "command.calls").exists()


def test_registration_succeeded_but_read_failed_preserves_the_distinction(tmp_path, compiled):
    published = publish(tmp_path, compiled)
    with pytest.raises(ValueError, match="registration command succeeded"):
        queue_compilation(published, request(tmp_path, fixture_command(tmp_path, list_exit=3)))
    registration, = published.path.parent.glob("queue-*.registration.json")
    assert json.loads(registration.read_text())["exit_code"] == 0
    assert not list(published.path.parent.glob("queue-*.observed.json"))


def test_monitor_selecting_another_revision_is_not_attributed_to_submission(tmp_path, compiled):
    published = publish(tmp_path, compiled)
    with pytest.raises(ValueError, match="requested revision"):
        queue_compilation(published, request(tmp_path, fixture_command(tmp_path, monitor_revision=2)))
    assert not list(published.path.parent.glob("queue-*.observed.json"))


def test_timeout_records_uncertainty_and_never_retries(tmp_path, compiled, monkeypatch):
    import fern_analysis.study_submission as submission

    published = publish(tmp_path, compiled)
    req = request(tmp_path, fixture_command(tmp_path, registration_sleep=2))
    monkeypatch.setattr(submission, "COMMAND_TIMEOUT_SECONDS", 0.2)
    with pytest.raises(ValueError, match="unconfirmed"):
        queue_compilation(published, req)
    assert len(list(published.path.parent.glob("queue-*.request.json"))) == 1
    assert list(published.path.parent.glob("queue-*.unconfirmed.json"))
    assert not list(published.path.parent.glob("queue-*.observed.json"))
    assert not list(published.path.parent.glob("queue-*.refused.json"))


def test_device_in_compiled_input_is_forwarded_without_defaulting(tmp_path, compiled):
    compiled["device"] = "cpu"  # Process protocol only; this fixture does not train.
    published = publish(tmp_path, compiled)
    queue_compilation(published, request(tmp_path, fixture_command(tmp_path)))


def test_unexpected_register_exit_is_uncertain_not_a_registry_refusal(tmp_path, compiled):
    published = publish(tmp_path, compiled)
    with pytest.raises(ValueError, match="outcome unconfirmed"):
        queue_compilation(published, request(tmp_path, fixture_command(tmp_path, register_exit=1)))
    assert list(published.path.parent.glob("queue-*.unconfirmed.json"))
    assert not list(published.path.parent.glob("queue-*.refused.json"))


def test_output_symlink_cannot_redirect_publication_outside_selected_directory(tmp_path, compiled):
    central = tmp_path / "central"
    central.mkdir()
    outside = tmp_path / "outside"
    outside.mkdir()
    (central / compiled["study"]).symlink_to(outside, target_is_directory=True)
    with pytest.raises(ValueError, match="escapes"):
        publish(tmp_path, compiled)
    assert not list(outside.iterdir())


def test_cli_cannot_overwrite_compiled_artifacts_with_golden_output(tmp_path):
    import subprocess

    into = tmp_path / "central"
    done = subprocess.run([sys.executable, "-m", "fern_analysis", "study", "compile", "not-read.json",
                           "--into", str(into), "--golden-out", str(into / "compiled.json")],
                          capture_output=True, text=True, check=False)
    assert done.returncode == 2
    assert "outside the compiled artifact" in done.stderr
    assert not into.exists()


def test_cli_directory_publication_and_queue_use_the_same_compiled_bytes(tmp_path, compiled, monkeypatch, capsys):
    from types import SimpleNamespace

    import fern_analysis.__main__ as cli
    import fern_analysis.study_submission as submission
    from fern_train import study, study_cards

    program = tmp_path / "mini-program"
    program.mkdir()
    card = tmp_path / "card.json"
    card.write_text("{}")
    compiled["resolution"]["resolved"]["specification"] = f"study:{compiled['study']}"
    monkeypatch.setattr(study_cards, "load_card", lambda path: SimpleNamespace(name=compiled["study"]))
    monkeypatch.setattr(study_compile, "compile_study", lambda *a, **kw: compiled)
    # CHG 2026-09-26: compiled studies live out of docs/ (runtime/compiled by
    # default, which needs a program under docs/programs); a temporary program
    # names its output folder.
    home = tmp_path / "compiled-home"
    args = [str(card), "--program", str(program), "--slice", "independent-experiment", "--into", str(home)]
    assert cli._compile_study(args) == 0
    saved, = home.glob("**/compiled.json")
    assert json.loads(saved.read_text())["cells"] == compiled["cells"]
    assert not list(home.glob("**/queue-*"))
    seen = []
    def queue(published, request):
        seen.append((published.path, request))
        return submission.QueueObservation(42, "run", "awaiting Site\n", tmp_path / "receipt.json")
    monkeypatch.setattr(submission, "queue_compilation", queue)
    registry = tmp_path / "existing.db"
    registry.touch()
    assert cli._compile_study(args + ["--queue", "--registry", str(registry), "--source", "7", "--fern-run", sys.executable]) == 0
    assert seen[0][0] == saved
    assert seen[0][1].name == compiled["study"]
    assert "Study 42" in capsys.readouterr().out


def test_compiled_home_is_out_of_docs_and_refuses_a_folder_outside_the_programs() -> None:
    # CHG 2026-09-26: compiled studies are generated, so they live under runtime/compiled.
    from pathlib import Path as _Path

    from fern_analysis.study_submission import compiled_home

    root = _Path(__file__).resolve().parents[1]
    mini = root / "docs" / "programs" / "2026-09-12-fern-ablation" / "2026-09-24-beyond-nll"
    assert compiled_home(mini) == root / "runtime" / "compiled" / "2026-09-12-fern-ablation" / "2026-09-24-beyond-nll"
    with pytest.raises(ValueError, match="under docs/programs"):
        compiled_home(root / "studies")
