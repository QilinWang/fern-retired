#!/usr/bin/env bash
# Status: entry — bootstrap the source-pinned SDK and invoke Fern.Run.Cli.
# The caller supplies a source root already verified by the source transport.
# Bootstrap does not verify or attest that source identity itself.
# C# owns deployment, installation receipts, scheduling and training.
set -euo pipefail

die() {
    printf 'fern-bootstrap: %s\n' "$*" >&2
    exit 2
}

timeout_seconds="${FERN_BOOTSTRAP_TIMEOUT_SECONDS:-1800}"
[[ "$timeout_seconds" =~ ^[1-9][0-9]*$ ]] || die 'FERN_BOOTSTRAP_TIMEOUT_SECONDS must be a positive integer'
command -v timeout >/dev/null || die 'GNU timeout is required to bound bootstrap child processes'
started_at=$SECONDS
run_timed() {
    local remaining=$((timeout_seconds - (SECONDS - started_at)))
    if ((remaining <= 0)); then
        printf 'fern-bootstrap: total deadline exceeded (%s seconds)\n' "$timeout_seconds" >&2
        return 124
    fi
    # Each tool receives only the time left in this invocation. Do not use
    # --foreground: the process group must include installer/build descendants.
    # SIGKILL follows after one second if a child ignores SIGTERM.
    timeout --kill-after=1 "$remaining" "$@"
}

[[ $# -ge 4 && "$3" == -- ]] ||
    die 'usage: bootstrap_fern.sh VERIFIED_SOURCE_ROOT RUNTIME_ROOT -- FERN_RUN_ARGUMENT [ARG ...]'
[[ "$(uname -s)" == Linux ]] || die 'bootstrap requires a Linux host'
[[ -d "$1" ]] || die "verified source root does not exist: $1"
source_root="$(cd -- "$1" && pwd -P)"
[[ -f "$source_root/global.json" ]] || die "verified source has no global.json: $source_root"
project="$source_root/src/csharp/Fern.Run.Cli/Fern.Run.Cli.csproj"
[[ -f "$project" ]] || die "verified source has no Fern.Run.Cli project: $project"
command -v python3 >/dev/null || die 'python3 is required to read the source SDK declaration'
sdk_version="$(run_timed python3 -I - "$source_root/global.json" <<'PY'
import json
import re
import sys

try:
    with open(sys.argv[1], encoding="utf-8") as source:
        version = json.load(source)["sdk"]["version"]
    if not isinstance(version, str) or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?", version):
        raise ValueError("sdk.version must be one fixed .NET SDK version")
except (OSError, ValueError, KeyError, TypeError) as error:
    print(f"fern-bootstrap: invalid global.json: {error}", file=sys.stderr)
    raise SystemExit(2)
print(version)
PY
)"
runtime_root="$(realpath -m -- "$2")"
case "$runtime_root/" in
    "$source_root/"*) die 'runtime root must be outside the verified source tree' ;;
esac
shift 3

mkdir -p -- "$runtime_root"
build_root="$(mktemp -d "$runtime_root/bootstrap-build.XXXXXXXX")"
cleanup() {
    # Only this invocation's mktemp result belongs to bootstrap.
    case "$build_root" in
        "$runtime_root"/bootstrap-build.*) rm -rf -- "$build_root" ;;
    esac
}
trap cleanup EXIT
export DOTNET_CLI_HOME="$runtime_root/dotnet-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
export MSBUILDDISABLENODEREUSE=1

has_required_sdk() {
    [[ -x "$1" ]] || return 1
    run_timed "$1" --list-sdks 2>/dev/null |
        awk -v required="$sdk_version" '$1 == required { found = 1 } END { exit !found }'
}

dotnet_host="$runtime_root/dotnet/dotnet"
if ! has_required_sdk "$dotnet_host"; then
    path_host="$(command -v dotnet || true)"
    if [[ -n "$path_host" ]] && has_required_sdk "$path_host"; then
        dotnet_host="$(readlink -f -- "$path_host")"
    else
        command -v curl >/dev/null || die 'curl is required to install the source-pinned SDK'
        printf 'fern-bootstrap: installing SDK %s into %s\n' "$sdk_version" "$runtime_root/dotnet" >&2
        run_timed curl --fail --silent --show-error --location --connect-timeout 15 --max-time 120 \
            --output "$build_root/dotnet-install.sh" https://dot.net/v1/dotnet-install.sh
        if run_timed bash "$build_root/dotnet-install.sh" --jsonfile "$source_root/global.json" \
            --install-dir "$runtime_root/dotnet" --no-path >&2; then
            :
        else
            install_status=$?
            printf 'fern-bootstrap: SDK installation failed (exit %s)\n' "$install_status" >&2
            exit "$install_status"
        fi
        has_required_sdk "$dotnet_host" || die "installer did not provide SDK $sdk_version"
    fi
fi

dotnet_host="$(readlink -f -- "$dotnet_host")"
export DOTNET_ROOT="$(dirname -- "$dotnet_host")"
export PATH="$DOTNET_ROOT:$PATH"
export PYTHONPATH="$source_root:$source_root/python:$source_root/src"
cd -- "$source_root"
selected_version="$(run_timed "$dotnet_host" --version)"
[[ "$selected_version" == "$sdk_version" ]] ||
    die "global.json selected SDK $selected_version instead of its fixed version $sdk_version"

printf 'fern-bootstrap: building temporary Fern.Run.Cli with SDK %s\n' "$sdk_version" >&2
if run_timed "$dotnet_host" publish "$project" --disable-build-servers --nologo --configuration Release \
    --self-contained false -p:UseAppHost=true --artifacts-path "$build_root/artifacts" \
    --output "$build_root/cli" --verbosity quiet -maxcpucount:1 -nodeReuse:false \
    -p:UseSharedCompilation=false >&2; then
    :
else
    build_status=$?
    printf 'fern-bootstrap: Fern.Run.Cli build failed (exit %s)\n' "$build_status" >&2
    exit "$build_status"
fi
[[ -f "$build_root/cli/fern-run.dll" ]] || die 'build produced no fern-run.dll'
[[ -x "$build_root/cli/fern-run" ]] || die 'build produced no executable fern-run apphost'
export FERN_RUN_BIN="$build_root/cli/fern-run"

# Keep the temporary runtime alive until the existing CLI has returned; its
# stdout, arguments and exit status belong to that CLI, not to bootstrap.
if run_timed "$dotnet_host" "$build_root/cli/fern-run.dll" "$@"; then
    exit 0
else
    exit "$?"
fi
