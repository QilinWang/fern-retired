//! Shared command-runtime types used by the human CLI and resident scheduler.
//!
//! This module contains plumbing, not a second command surface. Keeping these
//! types in one source file lets `fern-run` and the dedicated
//! `fern-scheduler` compile the same plan/worker modules without copying their
//! defaults or durable-write behavior. The three executables are separate
//! Cargo packages; sharing this file does not share the operator CLI's
//! dependency table.

use std::error::Error;
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

use clap::Args;
use serde::{Deserialize, Serialize};
use serde::de::DeserializeOwned;

/// Crate-wide error alias for fallible command plumbing.
pub(crate) type Fallible<T> = Result<T, Box<dyn Error + Send + Sync>>;

/// One Registry Home request is a bounded process operation. SQLite readers
/// and writers have their own shorter deadlines inside Home; this outer bound
/// also covers a binary that starts but never reaches its command handler.
const GO_HOME_PROCESS_DEADLINE: Duration = Duration::from_secs(30);
const GO_HOME_PROCESS_POLL_INTERVAL: Duration = Duration::from_millis(25);
/// The bound on one ordinary C# Registry handoff: a read or a single write.
pub(crate) const CSHARP_REGISTRY_PROCESS_DEADLINE: Duration = Duration::from_secs(30);

const CSHARP_REGISTRY_PROCESS_POLL_INTERVAL: Duration = Duration::from_millis(25);

/// A caller supplied value is invalid for this command.
#[derive(Debug, thiserror::Error)]
#[error("{message}")]
pub(crate) struct InvalidInput {
    message: String,
}

/// Preserve input failures as a typed cause inside the CLI error chain.
pub(crate) fn invalid_input(message: impl Into<String>) -> Box<dyn Error + Send + Sync> {
    Box::new(InvalidInput {
        message: message.into(),
    })
}

/// Resolve the receipted private Registry Home writer for this Registry.
#[cfg(any(fern_role_operator, fern_role_scheduler))]
pub(crate) fn go_home_binary(registry: &Path) -> Fallible<PathBuf> {
    let deployment_root = crate::run_sweep::reviewed_source_root()?.join(crate::paths::DEFAULT_DEPLOYMENT_ROOT);
    crate::scheduler_service::installed_home_binary_via_csharp_scheduler(registry, &deployment_root)
        .map(|installed| installed.executable_path().to_path_buf())
        .map_err(|error| {
            invalid_input(format!(
                "no verified fern-home installation governs {}: {error}. Run `fern-run install-scheduler-service --registry {}`",
                registry.display(),
                registry.display()
            ))
        })
}

/// Send one strict JSON request across the private Registry Home boundary.
pub(crate) fn run_go_home<T: Serialize>(
    binary: &Path,
    operation: &str,
    registry: &Path,
    input: &T,
) -> Fallible<Vec<u8>> {
    let input_json = serde_json::to_vec(input)?;
    let output = invoke_go_home_process(binary, operation, registry, input_json, GO_HOME_PROCESS_DEADLINE)?;
    if !output.status.success() {
        return Err(invalid_input(format!(
            "Go Home writer refused {operation}: {}",
            String::from_utf8_lossy(&output.stderr).trim()
        )));
    }
    Ok(output.stdout)
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct RegistryMigrationReceipt {
    schema: String,
    registry: String,
    applied_migrations: usize,
}

/// Create or upgrade one Registry/RunData database through the sole embedded
/// production migration chain. Callers may pin the already-reviewed Home
/// executable; operator and scheduler builds otherwise resolve the installed
/// binary that governs this Registry. Private worker builds must receive the
/// executable from their parent instead of discovering authority themselves.
pub(crate) fn apply_registry_migrations_via_home(
    home_binary: Option<&Path>,
    registry: &Path,
) -> Fallible<()> {
    let resolved_home;
    let home_binary = match home_binary {
        Some(binary) => binary,
        None => {
            #[cfg(any(fern_role_operator, fern_role_scheduler))]
            {
                resolved_home = go_home_binary(registry)?;
                resolved_home.as_path()
            }
            #[cfg(not(any(fern_role_operator, fern_role_scheduler)))]
            {
                return Err(invalid_input(format!(
                    "the private worker requires its parent to supply the reviewed fern-home binary for {}",
                    registry.display()
                )));
            }
        }
    };
    let bytes = run_go_home(
        home_binary,
        "registry-apply-migrations",
        registry,
        &serde_json::json!({}),
    )?;
    let receipt: RegistryMigrationReceipt = serde_json::from_slice(&bytes)?;
    if receipt.schema != "fern.registry_migrations.v1"
        || receipt.registry != registry.display().to_string()
        || receipt.applied_migrations == 0
    {
        return Err(invalid_input(
            "Registry Home migration receipt changed schema/database identity or reported no applied production migrations",
        ));
    }
    Ok(())
}

/// Start one Home child and bound the whole process handoff, including a child
/// that starts but never reads its request pipe. The writer runs separately so
/// a request larger than the kernel pipe buffer cannot block before the
/// deadline begins.
fn invoke_go_home_process(
    binary: &Path,
    operation: &str,
    registry: &Path,
    input_json: Vec<u8>,
    deadline: Duration,
) -> Fallible<std::process::Output> {
    let started = Instant::now();
    let mut child = Command::new(binary)
        .args([operation, "--registry"])
        .arg(registry)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| invalid_input(format!("cannot start {}: {error}", binary.display())))?;
    let mut stdin = child
        .stdin
        .take()
        .ok_or_else(|| invalid_input("Go Home writer did not expose its request input"))?;
    let input_writer = std::thread::spawn(move || stdin.write_all(&input_json));
    // Drain both pipes while Home is running. Waiting for exit first deadlocks
    // when a valid response fills the operating-system pipe: Home cannot exit
    // until its write completes, while the parent does not read until exit.
    let mut stdout_pipe = child
        .stdout
        .take()
        .ok_or_else(|| invalid_input("Go Home writer did not expose its output"))?;
    let mut stderr_pipe = child
        .stderr
        .take()
        .ok_or_else(|| invalid_input("Go Home writer did not expose its error output"))?;
    let stdout_reader = std::thread::spawn(move || {
        let mut bytes = Vec::new();
        std::io::Read::read_to_end(&mut stdout_pipe, &mut bytes).map(|_| bytes)
    });
    let stderr_reader = std::thread::spawn(move || {
        let mut bytes = Vec::new();
        std::io::Read::read_to_end(&mut stderr_pipe, &mut bytes).map(|_| bytes)
    });
    let status = collect_go_home_exit_before(child, operation, started, deadline);
    let input_result = input_writer.join();
    let stdout = stdout_reader
        .join()
        .map_err(|_| invalid_input("Go Home stdout reader panicked"))?;
    let stderr = stderr_reader
        .join()
        .map_err(|_| invalid_input("Go Home stderr reader panicked"))?;
    let output = status.and_then(|status| {
        Ok(std::process::Output {
            status,
            stdout: stdout.map_err(|error| invalid_input(format!("cannot read Go Home output: {error}")))?,
            stderr: stderr.map_err(|error| invalid_input(format!("cannot read Go Home error output: {error}")))?,
        })
    });

    let output = output?;
    let input_result = input_result.map_err(|_| invalid_input("Go Home request writer panicked"))?;
    if output.status.success() {
        input_result.map_err(|error| invalid_input(format!("cannot write Go Home request: {error}")))?;
    }
    Ok(output)
}

/// Collect one already-started Home process or kill and reap it at the named
/// boundary.
/// Keeping the timer around the real child means the deadline covers process
/// startup, stdin handoff, SQLite work, and receipt production as one unit.
fn collect_go_home_exit_before(
    mut child: std::process::Child,
    operation: &str,
    started: Instant,
    deadline: Duration,
) -> Fallible<std::process::ExitStatus> {
    // The caller drains stdout and stderr concurrently. On deadline, `kill`
    // closes the child's pipe ends and `wait` reaps it before this boundary
    // returns, so neither the child nor its reader threads are left behind.
    loop {
        match child.try_wait()? {
            Some(status) => return Ok(status),
            None if started.elapsed() >= deadline => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(invalid_input(format!(
                    "Go Home writer deadline {deadline:?} exhausted while running {operation}"
                )));
            }
            None => std::thread::sleep(GO_HOME_PROCESS_POLL_INTERVAL),
        }
    }
}

/// Locate the C# Task subprocess. Same lookup order as other Fern binaries:
/// explicit env, then PATH, then the newer of the Debug and Release builds.
pub(crate) fn csharp_registry_binary() -> Fallible<PathBuf> {
    if let Ok(path) = std::env::var("FERN_REGISTRY") {
        let path = PathBuf::from(path);
        if path.is_file() {
            return Ok(path);
        }
        return Err(invalid_input(format!(
            "FERN_REGISTRY is not a file: {}",
            path.display()
        )));
    }
    if let Some(path) = look_path("fern-registry") {
        return Ok(path);
    }
    let root = repo_root()
        .ok_or_else(|| invalid_input("cannot locate fern-registry; set FERN_REGISTRY to the C# Task subprocess"))?;
    let debug = root.join("csharp/Fern.Registry.Cli/bin/Debug/net9.0/fern-registry");
    let release = root.join("csharp/Fern.Registry.Cli/bin/Release/net9.0/fern-registry");
    match (debug.is_file(), release.is_file()) {
        (true, true) => {
            let debug_time = std::fs::metadata(&debug)?.modified()?;
            let release_time = std::fs::metadata(&release)?.modified()?;
            Ok(if release_time >= debug_time { release } else { debug })
        }
        (true, false) => Ok(debug),
        (false, true) => Ok(release),
        (false, false) => Err(invalid_input(format!(
            "fern-registry is not on PATH and was not built under {}",
            root.join("csharp/Fern.Registry.Cli/bin").display()
        ))),
    }
}

/// Locate the C# scheduler subprocess used for operator-side installation
/// inspection. Installed scheduler authority remains in the child; this
/// resolver only selects the executable that answers the typed read.
pub(crate) fn csharp_scheduler_binary() -> Fallible<PathBuf> {
    if let Ok(path) = std::env::var("FERN_SCHEDULER") {
        let path = PathBuf::from(path);
        if path.is_file() {
            return Ok(path);
        }
        return Err(invalid_input(format!(
            "FERN_SCHEDULER is not a file: {}",
            path.display()
        )));
    }
    if let Some(path) = look_path("fern-scheduler-cs") {
        return Ok(path);
    }
    let root = repo_root().ok_or_else(|| {
        invalid_input("cannot locate fern-scheduler-cs; set FERN_SCHEDULER to the C# scheduler subprocess")
    })?;
    let debug = root.join("csharp/Fern.Scheduler.Serve/bin/Debug/net9.0/fern-scheduler-cs");
    let release = root.join("csharp/Fern.Scheduler.Serve/bin/Release/net9.0/fern-scheduler-cs");
    match (debug.is_file(), release.is_file()) {
        (true, true) => {
            let debug_time = std::fs::metadata(&debug)?.modified()?;
            let release_time = std::fs::metadata(&release)?.modified()?;
            Ok(if release_time >= debug_time { release } else { debug })
        }
        (true, false) => Ok(debug),
        (false, true) => Ok(release),
        (false, false) => Err(invalid_input(format!(
            "fern-scheduler-cs is not on PATH and was not built under {}",
            root.join("csharp/Fern.Scheduler.Serve/bin").display()
        ))),
    }
}

/// Send one strict JSON request to the C# Task subprocess and return its receipt bytes.
pub(crate) fn run_csharp_registry<T: Serialize>(
    operation: &str,
    registry: &Path,
    input: &T,
    deadline: Duration,
) -> Fallible<Vec<u8>> {
    let output = spawn_csharp_registry(operation, registry, input, deadline)?;
    if !output.status.success() {
        return Err(invalid_input(format!(
            "C# Task writer refused {operation}: {}",
            String::from_utf8_lossy(&output.stderr).trim()
        )));
    }
    Ok(output.stdout)
}

fn spawn_csharp_registry<T: Serialize>(
    operation: &str,
    registry: &Path,
    input: &T,
    deadline: Duration,
) -> Fallible<std::process::Output> {
    let binary = csharp_registry_binary()?;
    let input_json = serde_json::to_vec(input)?;
    invoke_csharp_registry_process(&binary, operation, registry, input_json, deadline)
}

/// Bound the complete C# Registry handoff, including a child that starts but
/// never reads a request larger than the operating-system pipe buffer.
fn invoke_csharp_registry_process(
    binary: &Path,
    operation: &str,
    registry: &Path,
    input_json: Vec<u8>,
    deadline: Duration,
) -> Fallible<std::process::Output> {
    let arguments = vec![
        std::ffi::OsString::from(operation),
        std::ffi::OsString::from("--registry"),
        registry.as_os_str().to_os_string(),
    ];
    invoke_csharp_process(
        binary,
        &arguments,
        operation,
        input_json,
        deadline,
        "Registry",
    )
}

fn invoke_csharp_process(
    binary: &Path,
    arguments: &[std::ffi::OsString],
    operation: &str,
    input_json: Vec<u8>,
    deadline: Duration,
    boundary_name: &str,
) -> Fallible<std::process::Output> {
    let started = Instant::now();
    let mut child = Command::new(binary)
        .args(arguments)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| invalid_input(format!("cannot start {}: {error}", binary.display())))?;
    let mut stdin = child
        .stdin
        .take()
        .ok_or_else(|| invalid_input(format!("C# {boundary_name} did not expose its request input")))?;
    let input_writer = std::thread::spawn(move || stdin.write_all(&input_json));
    // CHG 2026-08-27: drain both output pipes on their own threads BEFORE
    // polling for exit. The previous shape polled `try_wait` and only then
    // called `wait_with_output`, so a child whose response exceeded the
    // 64 KiB pipe buffer blocked on its write, never exited, and was killed
    // at the deadline: a 483 KB runs read for one lane hung for 30 s. Found
    // by the accept-sweep port while capturing its golden.
    let mut stdout_pipe = child
        .stdout
        .take()
        .ok_or_else(|| invalid_input(format!("C# {boundary_name} did not expose its output")))?;
    let mut stderr_pipe = child
        .stderr
        .take()
        .ok_or_else(|| invalid_input(format!("C# {boundary_name} did not expose its error output")))?;
    let stdout_reader = std::thread::spawn(move || {
        let mut bytes = Vec::new();
        std::io::Read::read_to_end(&mut stdout_pipe, &mut bytes).map(|_| bytes)
    });
    let stderr_reader = std::thread::spawn(move || {
        let mut bytes = Vec::new();
        std::io::Read::read_to_end(&mut stderr_pipe, &mut bytes).map(|_| bytes)
    });
    let status = collect_csharp_exit_before(child, operation, started, deadline, boundary_name);
    let input_result = input_writer.join();
    let stdout = stdout_reader
        .join()
        .map_err(|_| invalid_input(format!("C# {boundary_name} stdout reader panicked")))?;
    let stderr = stderr_reader
        .join()
        .map_err(|_| invalid_input(format!("C# {boundary_name} stderr reader panicked")))?;
    let output = status.and_then(|status| {
        Ok(std::process::Output {
            status,
            stdout: stdout
                .map_err(|error| invalid_input(format!("cannot read C# {boundary_name} output: {error}")))?,
            stderr: stderr.map_err(|error| {
                invalid_input(format!("cannot read C# {boundary_name} error output: {error}"))
            })?,
        })
    });

    let output = output?;
    let input_result = input_result
        .map_err(|_| invalid_input(format!("C# {boundary_name} request writer panicked")))?;
    if output.status.success() {
        input_result
            .map_err(|error| invalid_input(format!("cannot write C# {boundary_name} request: {error}")))?;
    }
    Ok(output)
}

fn collect_csharp_exit_before(
    mut child: std::process::Child,
    operation: &str,
    started: Instant,
    deadline: Duration,
    boundary_name: &str,
) -> Fallible<std::process::ExitStatus> {
    // The output pipes are already being drained by the caller's reader
    // threads, so waiting here cannot deadlock on a full pipe; killing at the
    // deadline closes the pipes and lets those readers finish.
    loop {
        match child.try_wait()? {
            Some(status) => return Ok(status),
            None if started.elapsed() >= deadline => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(invalid_input(format!(
                    "C# {boundary_name} deadline {deadline:?} exhausted while running {operation}"
                )));
            }
            None => std::thread::sleep(CSHARP_REGISTRY_PROCESS_POLL_INTERVAL),
        }
    }
}

fn look_path(name: &str) -> Option<PathBuf> {
    let path = std::env::var_os("PATH")?;
    std::env::split_paths(&path).find_map(|directory| {
        let candidate = directory.join(name);
        candidate.is_file().then_some(candidate)
    })
}

fn repo_root() -> Option<PathBuf> {
    let mut directory = std::env::current_dir().ok()?;
    loop {
        if directory.join("csharp/Fern.Registry.Cli").is_dir() && directory.join("fern_run/src/db.rs").is_file() {
            return Some(directory);
        }
        if !directory.pop() {
            return None;
        }
    }
}

/// Write one file so a crash cannot leave it half-written.
///
/// A plain `fs::write` returns as soon as the kernel has the bytes in page
/// cache. If the machine loses power or the process is hard-killed before
/// those pages reach disk, ext4's delayed allocation can leave the file
/// present but ZERO BYTES — the directory entry was journaled, the data was
/// not. That is exactly what happened to two laplace chronos2 runs on
/// 2026-07-26: `raw_tensors.safetensors` survived at 47 MB while
/// `metrics.ipc`, `diagnostics.json`, `execution_environment.json`, and
/// `quantile_diag.json` were all 0 bytes, and the SQLite row was intact
/// because SQLite fsyncs and we did not.
///
/// The fix is the standard durable-replace sequence:
///
///   1. write the bytes to a sibling temp file (same directory, so the rename
///      below stays inside one filesystem and is therefore atomic),
///   2. fsync that file, so its DATA is on disk, not just in cache,
///   3. rename it onto the final path — POSIX rename is atomic, so a reader
///      sees either no file or the complete file, never a partial one,
///   4. fsync the DIRECTORY, so the rename itself survives a crash.
///
/// After this, a zero-byte receipt is not a state the filesystem can produce.
/// The cost is one fsync per receipt, which is negligible beside a training
/// run.
pub(crate) fn write_file_durably(path: &Path, bytes: &[u8]) -> Fallible<()> {
    let directory = path
        .parent()
        .ok_or_else(|| invalid_input(format!("{path:?} has no parent directory")))?;
    let mut temp_name = path.file_name().unwrap_or_default().to_os_string();
    temp_name.push(".partial");
    let temp_path = directory.join(temp_name);

    let mut file = fs::File::create(&temp_path)?;
    file.write_all(bytes)?;
    file.sync_all()?;
    drop(file);

    fs::rename(&temp_path, path)?;
    // Opening a directory read-only and syncing it is how POSIX flushes the
    // rename. On filesystems that refuse the open, the rename is still atomic;
    // only its durability across a power cut is lost, so this is best effort.
    if let Ok(directory_handle) = fs::File::open(directory) {
        let _ = directory_handle.sync_all();
    }
    Ok(())
}

/// Write one pretty-printed JSON file with a trailing newline.
pub(crate) fn write_json_file<T: Serialize>(path: &Path, value: &T) -> Fallible<()> {
    write_file_durably(path, (serde_json::to_string_pretty(value)? + "\n").as_bytes())
}

/// Read and deserialize one JSON file.
pub(crate) fn read_json_file<T: DeserializeOwned>(path: &Path) -> Fallible<T> {
    Ok(serde_json::from_str(&fs::read_to_string(path)?)?)
}

/// Read a registered Fern JSON contract through both its Rust type and schema tag.
pub(crate) fn read_json_contract<T>(path: &Path) -> Fallible<T>
where
    T: DeserializeOwned + fern_json_contract::JsonContract,
{
    Ok(fern_json_contract::checked_from_slice(&fs::read(path)?)?)
}

/// Base seeds for Rust draw expansion, shared by the run commands.
#[derive(Debug, Clone, Args)]
pub(crate) struct DrawSeedBaseArgs {
    /// Base seed used for Rust target expansion; Python consumes the derived draw seed.
    #[arg(long, default_value_t = 42)]
    pub(crate) trajectory_seed_base: u64,
    /// Base seed used for Rust observation-noise draw seeds.
    #[arg(long, default_value_t = 1000)]
    pub(crate) noise_seed_base: u64,
}

/// Run registry annotations shared by the run commands.
#[derive(Debug, Clone, Args)]
pub(crate) struct RunAnnotationArgs {
    /// Override the training epoch count for each target.
    #[arg(long)]
    pub(crate) max_epochs: Option<u64>,
    /// Runtime device recorded in config and run identity.
    #[arg(long, value_parser = crate::build_config::parse_device_kind)]
    pub(crate) device: fern_run::resolved_cfg::DeviceKind,
    /// Human note recorded in the run registry.
    #[arg(long = "note")]
    pub(crate) run_note: Option<String>,
    /// Keep raw_tensors.safetensors: the complete horizon-by-channel forecast
    /// and target for every forecast origin, which is what the run was scored on.
    ///
    /// Off by default because keeping it for every run is what filled 183 GB.
    /// Without it the parent writes the stitched sequence and deletes the
    /// origin tensors, and the stitched file is NOT the scoring basis -- recomputing
    /// MSE from it gives 0.0061 where the run recorded 0.0301 -- so any
    /// per-origin question (which forecast starts a model wins, whether a gap is
    /// carried by a few spikes) cannot be asked of a finished run at all.
    ///
    /// Unlike the config's slim/full switch this retention choice is not hashed
    /// by itself. A declared replica supplies a separate run identity when old
    /// origin tensors must be recomputed without overwriting the primary run.
    #[arg(long)]
    pub(crate) keep_forecast_origins: bool,
}

/// The values clap fills in when a worker flag is omitted. Named so the launch
/// path can tell "the caller said nothing" from "the caller asked for this",
/// and so the two places can never drift apart.
pub(crate) const DEFAULT_WORKER_WORK_ROOT: &str = "tmp/fern-run-worker";
pub(crate) const DEFAULT_WORKER_MODULE: &str = "fr_tr";

/// Python training-worker spawn options shared by the run commands.
#[derive(Debug, Clone, Args)]
pub(crate) struct WorkerSpawnArgs {
    /// Root for transient run_packet/request/registration/result JSON handoff files.
    #[arg(long, default_value = DEFAULT_WORKER_WORK_ROOT)]
    pub(crate) work_root: PathBuf,
    /// Python executable used to spawn the training worker.
    ///
    /// No default. Omitted means "use what the execution profile declares",
    /// and that has to be distinguishable from someone typing the placeholder:
    /// with `default_value` it was not, so a command supplying no interpreter
    /// was refused for disagreeing with the profile, and the branch that
    /// accepted it would have spawned the literal string (correction 284).
    #[arg(long, env = "FERN_PYTHON")]
    pub(crate) python: Option<String>,
    /// Python module implementing the DB-free training worker.
    #[arg(long, default_value = DEFAULT_WORKER_MODULE)]
    pub(crate) module: String,
    /// Suppress live worker stdout/stderr and Rust's compact metric summary;
    /// logs are still written beside the worker handoff files.
    #[arg(long)]
    pub(crate) quiet_worker: bool,
}

#[cfg(all(test, unix))]
mod tests {
    use std::os::unix::fs::PermissionsExt;

    use super::*;

    #[test]
    fn go_home_process_start_refusal_is_named_before_the_deadline() {
        let missing_home = std::env::temp_dir().join(format!(
            "missing-fern-home-{}-{}",
            std::process::id(),
            std::thread::current().name().unwrap_or("test")
        ));
        let started = Instant::now();
        let error = invoke_go_home_process(
            &missing_home,
            "register-architecture",
            Path::new("unused.db"),
            b"{}".to_vec(),
            Duration::from_millis(75),
        )
        .unwrap_err()
        .to_string();

        assert!(
            error.contains(&format!("cannot start {}", missing_home.display())),
            "got: {error}"
        );
        assert!(started.elapsed() < Duration::from_secs(1));
    }

    #[test]
    fn go_home_process_handoff_is_bounded_by_the_named_deadline() {
        let root = std::env::temp_dir().join(format!(
            "fern-go-home-deadline-{}-{}",
            std::process::id(),
            std::thread::current().name().unwrap_or("test")
        ));
        std::fs::create_dir_all(&root).unwrap();
        let hanging_home = root.join("hanging-fern-home");
        std::fs::write(&hanging_home, "#!/bin/sh\nwhile :; do :; done\n").unwrap();
        let mut permissions = std::fs::metadata(&hanging_home).unwrap().permissions();
        permissions.set_mode(0o755);
        std::fs::set_permissions(&hanging_home, permissions).unwrap();

        let deadline = Duration::from_millis(75);
        let started = Instant::now();
        // This is deliberately larger than a normal Unix pipe. The old direct
        // stdin write blocked before its output deadline could even start.
        let input_json = vec![b'x'; 1024 * 1024];
        let error = invoke_go_home_process(
            &hanging_home,
            "register-architecture",
            &root.join("unused.db"),
            input_json,
            deadline,
        )
        .unwrap_err()
        .to_string();

        assert!(
            error.contains("Go Home writer deadline 75ms exhausted while running register-architecture"),
            "got: {error}"
        );
        assert!(started.elapsed() < Duration::from_secs(1));
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn csharp_registry_process_start_refusal_is_named_before_the_deadline() {
        let missing_registry = std::env::temp_dir().join(format!(
            "missing-fern-registry-{}-{}",
            std::process::id(),
            std::thread::current().name().unwrap_or("test")
        ));
        let started = Instant::now();
        let error = invoke_csharp_registry_process(
            &missing_registry,
            "release-coordinator",
            Path::new("unused.db"),
            b"{}".to_vec(),
            Duration::from_millis(75),
        )
        .unwrap_err()
        .to_string();

        assert!(
            error.contains(&format!("cannot start {}", missing_registry.display())),
            "got: {error}"
        );
        assert!(started.elapsed() < Duration::from_secs(1));
    }

    #[test]
    fn csharp_registry_process_handoff_is_bounded_by_the_named_deadline() {
        let root = std::env::temp_dir().join(format!(
            "fern-csharp-registry-deadline-{}-{}",
            std::process::id(),
            std::thread::current().name().unwrap_or("test")
        ));
        std::fs::create_dir_all(&root).unwrap();
        let hanging_registry = root.join("hanging-fern-registry");
        std::fs::write(&hanging_registry, "#!/bin/sh\nwhile :; do :; done\n").unwrap();
        let mut permissions = std::fs::metadata(&hanging_registry).unwrap().permissions();
        permissions.set_mode(0o755);
        std::fs::set_permissions(&hanging_registry, permissions).unwrap();

        let deadline = Duration::from_millis(75);
        let started = Instant::now();
        let input_json = vec![b'x'; 1024 * 1024];
        let error = invoke_csharp_registry_process(
            &hanging_registry,
            "release-coordinator",
            &root.join("unused.db"),
            input_json,
            deadline,
        )
        .unwrap_err()
        .to_string();

        assert!(
            error.contains("C# Registry deadline 75ms exhausted while running release-coordinator"),
            "got: {error}"
        );
        assert!(started.elapsed() < Duration::from_secs(1));
        std::fs::remove_dir_all(root).unwrap();
    }

    /// A response larger than the operating-system pipe buffer (64 KiB on
    /// Linux) must arrive whole. Before 2026-08-27 the parent polled the
    /// child for exit before draining stdout, so the child blocked on its
    /// write and was killed at the deadline; the accept-sweep port hit it
    /// with a 483 KB runs read.
    #[test]
    fn csharp_registry_response_larger_than_the_pipe_buffer_arrives_whole() {
        let root = std::env::temp_dir().join(format!(
            "fern-csharp-registry-flood-{}-{}",
            std::process::id(),
            std::thread::current().name().unwrap_or("test")
        ));
        std::fs::create_dir_all(&root).unwrap();
        let flooding_registry = root.join("flooding-fern-registry");
        std::fs::write(
            &flooding_registry,
            "#!/bin/sh\ncat >/dev/null\nhead -c 1048576 /dev/zero | tr '\\0' 'y'\necho flood >&2\n",
        )
        .unwrap();
        let mut permissions = std::fs::metadata(&flooding_registry).unwrap().permissions();
        permissions.set_mode(0o755);
        std::fs::set_permissions(&flooding_registry, permissions).unwrap();

        let started = Instant::now();
        let output = invoke_csharp_registry_process(
            &flooding_registry,
            "accept-sweep",
            &root.join("unused.db"),
            b"{}".to_vec(),
            Duration::from_secs(10),
        )
        .unwrap();

        assert!(output.status.success());
        assert_eq!(output.stdout.len(), 1024 * 1024);
        assert!(output.stdout.iter().all(|byte| *byte == b'y'));
        assert_eq!(String::from_utf8_lossy(&output.stderr).trim(), "flood");
        assert!(started.elapsed() < Duration::from_secs(5));
        std::fs::remove_dir_all(root).unwrap();
    }
}
