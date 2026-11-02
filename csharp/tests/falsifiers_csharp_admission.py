# --------------------------------------------------------------------------- compiled-study admission (C#)


def _fern_run() -> list[str]:
    from fern_train.worker import fern_run_csharp_cmd

    return fern_run_csharp_cmd()


def _admit(compiled: dict, folder: Path) -> subprocess.CompletedProcess:
    path = folder / "compiled.json"
    path.write_text(json.dumps(compiled, sort_keys=True, default=str), encoding="utf-8")
    return subprocess.run([*_fern_run(), "check-compiled-study", "--from", str(path)], capture_output=True, text=True, cwd=os.getcwd(), check=False)


def test_run_study_admits_the_good_compiled_study(tmp_path: Path) -> None:
    manifest = _manifest("run-study-admit")
    card = tomllib.loads(Path(manifest["good"]).read_text(encoding="utf-8"))
    run = _admit(_compile(card), tmp_path)
    assert run.returncode == 0, run.stderr
    assert _tree_listing(tmp_path) == {"compiled.json"}


@pytest.mark.parametrize("attack", _attacks("run-study-admit"))
def test_run_study_refuses_each_attack_by_name_and_writes_nothing(attack: dict, tmp_path: Path) -> None:
    manifest = _manifest("run-study-admit")
    card = tomllib.loads(Path(manifest["good"]).read_text(encoding="utf-8"))
    faulty = _mutated(_compile(card), attack)
    run = _admit(faulty, tmp_path)
    assert run.returncode != 0, f"{attack['name']} ({attack['class']}): admitted\n{run.stdout}"
    assert attack["refusal"] in run.stderr + run.stdout, f"{attack['name']}: refused, but not by the named text:\n{run.stderr}{run.stdout}"
    assert _tree_listing(tmp_path) == {"compiled.json"}, f"{attack['name']}: the refusal left files behind"
