"""Linux bootstrap process contracts; fake SDK and curl, no network or build."""

from __future__ import annotations

import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import time
import tempfile
import unittest


BASH = r"C:\Program Files\Git\bin\bash.exe" if os.name == "nt" else shutil.which("bash")
SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "bootstrap_fern.sh"
SDK = "11.0.100-preview.7.26381.103"


FAKE_TOOL = r'''
import json
import os
from pathlib import Path
import sys
import tempfile

def native_path(value):
    if os.name == "nt" and value.startswith("/tmp/"):
        return Path(tempfile.gettempdir()) / value[5:]
    if os.name == "nt" and len(value) > 2 and value[0] == "/" and value[2] == "/":
        value = value[1].upper() + ":" + value[2:]
    return Path(value)

def shell_path(value):
    if os.name == "nt" and Path(value).is_relative_to(Path(tempfile.gettempdir())):
        return "/tmp/" + Path(value).relative_to(Path(tempfile.gettempdir())).as_posix()
    value = Path(value).as_posix()
    return "/" + value[0].lower() + value[2:] if os.name == "nt" and len(value) > 1 and value[1] == ":" else value

name = Path(sys.argv[0]).name
args = sys.argv[1:]
if name == "fake-tool.py":
    name, args = args[0], args[1:]
with open(os.environ["FERN_BOOTSTRAP_TEST_LOG"], "a") as stream:
    stream.write(json.dumps({"tool": name, "args": args, "cwd": shell_path(os.getcwd()),
        "dotnet_root": os.environ.get("DOTNET_ROOT"),
        "pythonpath": os.environ.get("PYTHONPATH")}) + "\n")
if name == "curl":
    target = native_path(args[args.index("--output") + 1])
    target.write_text('#!/usr/bin/env bash\nsource "$FERN_BOOTSTRAP_TEST_HANG_SCRIPT"\nfixture_hang install\nexec "$FERN_BOOTSTRAP_TEST_PYTHON" -I "$FERN_BOOTSTRAP_TEST_FAKE" install "$@"\n', encoding="utf-8", newline="\n")
elif name == "install":
    failure = int(os.environ.get("FERN_BOOTSTRAP_TEST_INSTALL_EXIT", "0"))
    if failure:
        raise SystemExit(failure)
    root = native_path(args[args.index("--install-dir") + 1])
    root.mkdir(parents=True, exist_ok=True)
    executable = root / "dotnet"
    executable.write_bytes(Path(os.environ["FERN_BOOTSTRAP_TEST_FAKE_BIN"]).read_bytes())
    executable.chmod(0o755)
    (root / "sdk-version").write_text(os.environ["FERN_BOOTSTRAP_TEST_SDK"])
elif args in (["--list-sdks"], ["--version"]):
    host = native_path(os.environ["FERN_BOOTSTRAP_TEST_HOST"])
    marker = host.parent / "sdk-version"
    version = marker.read_text() if marker.exists() else os.environ.get("FERN_BOOTSTRAP_TEST_INSTALLED", "9.0.100")
    print(version if args == ["--version"] else f"{version} [{host.parent}/sdk]")
elif args[0] == "publish":
    failure = int(os.environ.get("FERN_BOOTSTRAP_TEST_BUILD_EXIT", "0"))
    if failure:
        raise SystemExit(failure)
    output = native_path(args[args.index("--output") + 1])
    output.mkdir(parents=True, exist_ok=True)
    (output / "fern-run.dll").write_text("fake CLI: never a built binary")
    (output / "fern-run").write_text("#!/usr/bin/env bash\nexit 0\n", encoding="utf-8", newline="\n")
    (output / "fern-run").chmod(0o755)
else:
    assert native_path(args[0]).name == "fern-run.dll"
    assert native_path(args[0]).is_file()
    assert native_path(os.environ["FERN_RUN_BIN"]).is_file()
    print(json.dumps({"argv": args[1:], "pythonpath": os.environ["PYTHONPATH"],
        "dotnet_root": os.environ["DOTNET_ROOT"], "cwd": shell_path(os.getcwd()),
        "fern_run_bin": os.environ["FERN_RUN_BIN"]}))
    raise SystemExit(int(os.environ.get("FERN_BOOTSTRAP_TEST_CLI_EXIT", "0")))
'''

def shell_path(value):
    if os.name == "nt" and Path(value).is_relative_to(Path(tempfile.gettempdir())):
        return "/tmp/" + Path(value).relative_to(Path(tempfile.gettempdir())).as_posix()
    value = Path(value).as_posix()
    return "/" + value[0].lower() + value[2:] if os.name == "nt" and len(value) > 1 and value[1] == ":" else value


def native_path(value):
    if os.name == "nt" and value.startswith("/tmp/"):
        return Path(tempfile.gettempdir()) / value[5:]
    if os.name == "nt" and len(value) > 2 and value[0] == "/" and value[2] == "/":
        value = value[1].upper() + ":" + value[2:]
    return Path(value)


def make_bootstrap(tmp_path):
    source = tmp_path / "verified source"
    project = source / "src" / "csharp" / "Fern.Run.Cli" / "Fern.Run.Cli.csproj"
    project.parent.mkdir(parents=True)
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"/>')
    (source / "global.json").write_text(json.dumps({"sdk": {"version": SDK, "rollForward": "disable"}}))
    for directory in ("bin", "obj"):
        (project.parent / directory).mkdir()
        (project.parent / directory / "foreign.txt").write_text("must survive unchanged")
    fake_bin = tmp_path / "fake-bin"
    fake_bin.mkdir()
    executable = '#!/usr/bin/env bash\nexport FERN_BOOTSTRAP_TEST_HOST="$0"\nsource "$FERN_BOOTSTRAP_TEST_HANG_SCRIPT"\n'
    executable += 'case "$1" in publish) fixture_hang publish ;; */fern-run.dll) fixture_hang cli ;; esac\nexec '
    executable += shlex.quote(Path(sys.executable).as_posix()) + " -I " + shlex.quote((tmp_path / "fake-tool.py").as_posix())
    (tmp_path / "fake-tool.py").write_text(FAKE_TOOL)
    (tmp_path / "hang.sh").write_text(
        'fixture_hang() {\n'
        '  [[ "${FERN_BOOTSTRAP_TEST_HANG_PHASE:-}" == "$1" ]] || return 0\n'
        '  printf started >"$FERN_BOOTSTRAP_TEST_STARTED"\n'
        "  trap '' TERM\n"
        '  (trap \'\' TERM; sleep 5; printf leaked >"$FERN_BOOTSTRAP_TEST_LEAKED") &\n'
        '  wait\n'
        '}\n', encoding="utf-8", newline="\n",
    )
    for name in ("curl", "dotnet"):
        path = fake_bin / name
        path.write_text(executable + " " + name + ' "$@"\n', encoding="utf-8", newline="\n")
        path.chmod(0o755)
    (fake_bin / "uname").write_text('#!/usr/bin/env bash\nprintf "Linux\\n"\n', encoding="utf-8", newline="\n")
    (fake_bin / "uname").chmod(0o755)
    (fake_bin / "python3").write_text(
        '#!/usr/bin/env bash\nargs=("$@")\n'
        'if command -v cygpath >/dev/null; then\n'
        '  for ((i=0; i<$#; i++)); do [[ "${args[i]}" == /* && -e "${args[i]}" ]] && args[i]="$(cygpath -w "${args[i]}")"; done\n'
        'fi\nexec ' + shlex.quote(Path(sys.executable).as_posix()) + ' "${args[@]}"\n',
        encoding="utf-8", newline="\n",
    )
    (fake_bin / "python3").chmod(0o755)
    runtime = tmp_path / "runtime"
    log = tmp_path / "calls.jsonl"
    env = dict(os.environ, TEST_BIN=fake_bin.as_posix(), TEST_HELPER=SCRIPT.as_posix(),
               MSYS2_ARG_CONV_EXCL="*", MSYS2_ENV_CONV_EXCL="PYTHONPATH;DOTNET_ROOT;FERN_RUN_BIN",
               PYTHONPATH="/wrong/inherited/path", FERN_BOOTSTRAP_TEST_LOG=str(log),
               FERN_BOOTSTRAP_TEST_PYTHON=Path(sys.executable).as_posix(),
               FERN_BOOTSTRAP_TEST_FAKE=(tmp_path / "fake-tool.py").as_posix(),
               FERN_BOOTSTRAP_TEST_FAKE_BIN=str(fake_bin / "dotnet"),
               FERN_BOOTSTRAP_TEST_HANG_SCRIPT=(tmp_path / "hang.sh").as_posix(),
               FERN_BOOTSTRAP_TEST_STARTED=(tmp_path / "started").as_posix(),
               FERN_BOOTSTRAP_TEST_LEAKED=(tmp_path / "leaked").as_posix(),
               FERN_BOOTSTRAP_TEST_SDK=SDK)

    def run(*args, changes=None, source_path=None):
        return subprocess.run(
            [BASH, "--noprofile", "--norc", "-c",
             'export PATH="$(cygpath -u "$TEST_BIN" 2>/dev/null || printf %s "$TEST_BIN"):$PATH"; '
             'source_arg="$(cygpath -u "$1" 2>/dev/null || printf %s "$1")"; '
             'runtime_arg="$(cygpath -u "$2" 2>/dev/null || printf %s "$2")"; shift 2; '
             'exec bash "$TEST_HELPER" "$source_arg" "$runtime_arg" "$@"', "test",
             Path(source_path or source).as_posix(), runtime.as_posix(), "--", *args],
            env=env | (changes or {}), text=True, capture_output=True, timeout=20, check=False,
        )

    def calls():
        return [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []

    return source, runtime, fake_bin, run, calls


def check_installed_exact_sdk_skips_download_and_install(bootstrap):
    source, runtime, fake_bin, run, calls = bootstrap
    result = run("--help", changes={"FERN_BOOTSTRAP_TEST_INSTALLED": SDK})
    assert result.returncode == 0, result.stderr
    assert all(call["tool"] == "dotnet" for call in calls())
    assert json.loads(result.stdout)["dotnet_root"] == shell_path(fake_bin), (result.stdout, shell_path(fake_bin))
    assert not list(runtime.glob("bootstrap-build.*"))


def check_missing_sdk_installs_only_the_source_json_version(bootstrap):
    source, runtime, _, run, calls = bootstrap
    result = run("--help")
    assert result.returncode == 0, result.stderr
    curl = next(call for call in calls() if call["tool"] == "curl")
    assert "https://dot.net/v1/dotnet-install.sh" in curl["args"]
    installer = next(call for call in calls() if call["tool"] == "install")
    assert installer["args"] == ["--jsonfile", shell_path(source / "global.json"), "--install-dir", shell_path(runtime / "dotnet"), "--no-path"], installer["args"]
    assert json.loads(result.stdout)["dotnet_root"] == shell_path(runtime / "dotnet")


def check_install_failure_stops_before_build_and_cleans_private_files(bootstrap):
    _, runtime, _, run, calls = bootstrap
    result = run("--help", changes={"FERN_BOOTSTRAP_TEST_INSTALL_EXIT": "23"})
    assert result.returncode == 23
    assert not result.stdout
    assert not any(call["args"] and call["args"][0] == "publish" for call in calls())
    assert not list(runtime.glob("bootstrap-build.*"))


def check_missing_source_or_global_json_refuses_before_external_commands(bootstrap, missing):
    source, runtime, _, run, calls = bootstrap
    if missing == "global.json":
        (source / "global.json").unlink()
        result = run("--help")
    else:
        result = run("--help", source_path=source.parent / "absent")
    assert result.returncode != 0
    assert not calls()
    assert not runtime.exists()


def check_build_failure_never_invokes_cli(bootstrap):
    _, runtime, _, run, calls = bootstrap
    result = run("--help", changes={"FERN_BOOTSTRAP_TEST_INSTALLED": SDK, "FERN_BOOTSTRAP_TEST_BUILD_EXIT": "31"})
    assert result.returncode == 31
    assert calls()[-1]["args"][0] == "publish"
    assert not result.stdout
    assert not list(runtime.glob("bootstrap-build.*"))


def check_cli_arguments_environment_and_exit_status_are_preserved(bootstrap):
    source, runtime, fake_bin, run, calls = bootstrap
    argv = ["show-config-identity", "name with spaces", "", "$(touch forbidden)", "--literal=a;b"]
    result = run(*argv, changes={"FERN_BOOTSTRAP_TEST_INSTALLED": SDK, "FERN_BOOTSTRAP_TEST_CLI_EXIT": "19"})
    assert result.returncode == 19
    report = json.loads(result.stdout)
    assert report["argv"] == argv
    assert report["pythonpath"] == f"{shell_path(source)}:{shell_path(source)}/python:{shell_path(source)}/src", (report["pythonpath"], shell_path(source))
    assert report["dotnet_root"] == shell_path(fake_bin)
    assert report["cwd"] == shell_path(source)
    apphost = native_path(report["fern_run_bin"])
    assert apphost.name == "fern-run"
    assert apphost.is_relative_to(runtime)
    assert not apphost.exists()
    publish = next(call for call in calls() if call["args"] and call["args"][0] == "publish")
    arguments = publish["args"]
    assert "--disable-build-servers" in arguments
    assert "-p:UseAppHost=true" in arguments
    artifact_path = native_path(arguments[arguments.index("--artifacts-path") + 1])
    assert artifact_path.is_relative_to(runtime)
    assert not artifact_path.exists()
    for directory in ("bin", "obj"):
        assert (source / "src" / "csharp" / "Fern.Run.Cli" / directory / "foreign.txt").read_text() == "must survive unchanged"
    assert not (source / "forbidden").exists()


def check_total_deadline_kills_stubborn_tool_and_its_child(bootstrap, phase):
    source, runtime, _, run, _ = bootstrap
    start = time.monotonic()
    result = run("--help", changes={
        "FERN_BOOTSTRAP_TIMEOUT_SECONDS": "2",
        "FERN_BOOTSTRAP_TEST_HANG_PHASE": phase,
        "FERN_BOOTSTRAP_TEST_INSTALLED": SDK if phase != "install" else "9.0.100",
    })
    assert result.returncode in {124, 137}, result.stderr
    assert time.monotonic() - start < 8
    assert (source.parent / "started").exists(), result.stderr
    assert not list(runtime.glob("bootstrap-build.*"))
    time.sleep(3)
    assert not (source.parent / "leaked").exists(), "a child outlived bootstrap's bounded process group"


@unittest.skipUnless(BASH and Path(BASH).is_file(), "Bash is required for the process fixture")
class BootstrapTests(unittest.TestCase):
    """One fixture per case; standard-library runner needs no package install."""

    def test_installed_exact_sdk_skips_download_and_install(self):
        with tempfile.TemporaryDirectory() as directory:
            check_installed_exact_sdk_skips_download_and_install(make_bootstrap(Path(directory)))

    def test_missing_sdk_installs_only_the_source_json_version(self):
        with tempfile.TemporaryDirectory() as directory:
            check_missing_sdk_installs_only_the_source_json_version(make_bootstrap(Path(directory)))

    def test_install_failure_stops_before_build_and_cleans_private_files(self):
        with tempfile.TemporaryDirectory() as directory:
            check_install_failure_stops_before_build_and_cleans_private_files(make_bootstrap(Path(directory)))

    def test_missing_source_or_global_json_refuses_before_external_commands(self):
        for missing in ("source", "global.json"):
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as directory:
                check_missing_source_or_global_json_refuses_before_external_commands(make_bootstrap(Path(directory)), missing)

    def test_build_failure_never_invokes_cli(self):
        with tempfile.TemporaryDirectory() as directory:
            check_build_failure_never_invokes_cli(make_bootstrap(Path(directory)))

    def test_cli_arguments_environment_and_exit_status_are_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            check_cli_arguments_environment_and_exit_status_are_preserved(make_bootstrap(Path(directory)))

    def test_total_deadline_kills_stubborn_tool_and_its_child(self):
        for phase in ("install", "publish", "cli"):
            with self.subTest(phase=phase), tempfile.TemporaryDirectory() as directory:
                check_total_deadline_kills_stubborn_tool_and_its_child(make_bootstrap(Path(directory)), phase)


if __name__ == "__main__":
    unittest.main()
