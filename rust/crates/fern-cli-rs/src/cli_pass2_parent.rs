//! CLI Pass 2 - shared parent-runner spine.
//!
//! This module is the local Pass 2 root: runner commands keep their public
//! clap structs in `run_target.rs` and `run_sweep.rs`,
//! while shared handoff stages live here so the parent runners do not drift.
//!
//! Reading sequence:
//!   Pass 2a/2c  standalone target planners are re-exported for parent loops.
//!   Pass 2e    `prepare_worker_handoff` writes handoff JSON and injects banks.
//!   Pass 2f    `python_training_worker.rs` spawns Python from those paths.

use std::collections::BTreeMap;
use std::fs;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::mpsc;
use std::thread;
use std::time::{Duration, Instant};

use crate::{Fallible, invalid_input};
use fern_artifact::{AttemptArtifactCommitV1, ByteDigest, WorkerRequestV1, WorkerResultReportV1};
use fern_execution_profile::{
    RemoteAttemptFileV1, RemoteAttemptInstructionsV1, RemoteAttemptReportV1, RemoteAttemptSourceFileV1,
    derive_remote_attempt_instructions_from_home_request, derive_remote_attempt_report_from_runner_claim,
    materialize_remote_attempt_instructions_package, materialize_remote_attempt_report_package,
    read_remote_attempt_package_file, verify_remote_attempt_instructions_package,
};
#[cfg(target_os = "linux")]
use rustix::fs::{FileType, Mode, OFlags, ResolveFlags, fstat, open, openat2};
use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};

/// Reopenable shared case material produced before the worker starts.
/// Absolute paths are transport-only and stay outside the hashed manifest.
#[derive(Debug, Clone)]
#[allow(dead_code)]
pub(crate) struct MaterializedScientificCase {
    pub(crate) manifest: fern_artifact::ScientificCaseManifest,
    pub(crate) forecast_origin_plan: fern_artifact::ForecastOriginPlanV1,
    pub(crate) observed_target_values: Vec<f64>,
    pub(crate) clean_target_values: Vec<f64>,
    pub(crate) training_scale_values: Vec<f64>,
}

/// Attempt-scoped paths for the Pass 6B candidate handoff.
///
/// These paths are transport details and therefore stay outside the hashed
/// [`WorkerRequestV1`]. The Python process will eventually receive them from
/// the Rust parent, but it never receives a lease capability secret.
#[derive(Debug, Clone)]
pub(crate) struct WorkerContractFiles {
    pub(crate) request_path: PathBuf,
    pub(crate) result_path: PathBuf,
}

pub(crate) const ATTEMPT_ARTIFACT_COMMIT_FILE: &str = "attempt_artifact_commit.v1.json";

/// Read-only Pass 6C diagnosis of one Attempt directory.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) enum AttemptArtifactState {
    MissingRequest,
    AwaitingWorkerResult,
    ResultNeedsVerification,
    PreparedCommitNeedsPromotion,
    Committed,
    ConflictingLocations,
    Invalid(String),
}

#[cfg(test)]
fn canonical_native_values(
    pred_values: &[f32],
    truth_values: &[f32],
    worker_batches: usize,
    worker_channels: usize,
    origins: usize,
    horizon: usize,
    channels: usize,
) -> Fallible<(Vec<f32>, Vec<f64>)> {
    let mut point = Vec::with_capacity(origins * horizon * channels);
    let mut observed = Vec::with_capacity(origins * horizon * channels);
    match (worker_batches, worker_channels) {
        (n, d) if n == origins && d == channels => {
            point.extend_from_slice(&pred_values);
            observed.extend(truth_values.iter().map(|value| *value as f64));
        }
        (n, 1) if n == origins * channels => {
            for origin in 0..origins {
                for step in 0..horizon {
                    for channel in 0..channels {
                        let index = (origin * channels + channel) * horizon + step;
                        point.push(pred_values[index]);
                        observed.push(truth_values[index] as f64);
                    }
                }
            }
        }
        _ => {
            return Err(invalid_input(format!(
                "cannot map worker [N,H,D]={:?} to case [forecast_origin,horizon,channel]=[{origins},{horizon},{channels}]",
                [worker_batches, horizon, worker_channels]
            )));
        }
    }
    Ok((point, observed))
}

/// Package one already-committed Worker-Site Attempt for transport to Home.
/// This is also the diagnostic seam used to test return transport with a
/// previously committed Attempt without rerunning a model.
// CHG 2026-08-28: the hidden `prepare-remote-attempt` / `prepare-remote-return`
// operator verbs were retired; the Worker Site still runs this through
// `execute_remote_attempt_package`, so the operator build is the only one
// that no longer reaches it.
#[cfg_attr(fern_role_operator, allow(dead_code))]
pub(crate) fn package_committed_remote_attempt_return(
    outbound_package: &Path,
    committed_directory: &Path,
    return_directory: &Path,
) -> Fallible<RemoteAttemptReportV1> {
    let instructions = verify_remote_attempt_instructions_package(outbound_package).map_err(invalid_input)?;
    let request_descriptor = instructions
        .files
        .iter()
        .find(|file| file.relative_path == "worker_request.v1.json")
        .ok_or_else(|| invalid_input("outbound package lacks worker_request.v1.json"))?;
    let request_bytes =
        read_remote_attempt_package_file(outbound_package, request_descriptor).map_err(invalid_input)?;
    let request: WorkerRequestV1 = fern_json_contract::checked_from_slice(&request_bytes)?;
    let result: WorkerResultReportV1 =
        crate::read_json_contract(&committed_directory.join("worker_result_report.v1.json"))?;
    let result_digest = result
        .digest_against(&request)
        .map_err(|error| invalid_input(format!("cannot identify remote worker result: {error}")))?;
    let sources = collect_remote_return_sources(committed_directory)?;
    let files = sources
        .iter()
        .map(|source| RemoteAttemptFileV1::from_file(&source.relative_path, &source.source_path))
        .collect::<Result<Vec<_>, _>>()
        .map_err(invalid_input)?;
    let result_value = serde_json::to_value(&result)?;
    let report =
        derive_remote_attempt_report_from_runner_claim(&result_value, result_digest.as_str(), &instructions, files)
            .map_err(invalid_input)?;
    materialize_remote_attempt_report_package(return_directory, &report, &instructions, &sources)
        .map_err(invalid_input)?;
    Ok(report)
}

#[cfg_attr(fern_role_operator, allow(dead_code))]
fn collect_remote_return_sources(committed_directory: &Path) -> Fallible<Vec<RemoteAttemptSourceFileV1>> {
    fn walk(root: &Path, directory: &Path, sources: &mut Vec<RemoteAttemptSourceFileV1>) -> Fallible<()> {
        let mut entries = fs::read_dir(directory)?.collect::<Result<Vec<_>, _>>()?;
        entries.sort_by_key(|entry| entry.file_name());
        for entry in entries {
            let metadata = fs::symlink_metadata(entry.path())?;
            if metadata.file_type().is_symlink() {
                return Err(invalid_input(format!(
                    "remote committed Attempt contains a symlink at {}",
                    entry.path().display()
                )));
            }
            if metadata.is_dir() {
                walk(root, &entry.path(), sources)?;
            } else if metadata.is_file() {
                let relative = entry.path().strip_prefix(root)?.to_string_lossy().replace('\\', "/");
                sources.push(RemoteAttemptSourceFileV1::new(relative, entry.path()));
            } else {
                return Err(invalid_input(format!(
                    "remote committed Attempt contains a non-regular entry at {}",
                    entry.path().display()
                )));
            }
        }
        Ok(())
    }

    let mut sources = Vec::new();
    walk(committed_directory, committed_directory, &mut sources)?;
    Ok(sources)
}

struct TemporaryRemoteSources {
    path: PathBuf,
}

impl TemporaryRemoteSources {
    fn new() -> Fallible<Self> {
        let nonce = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_err(|error| invalid_input(format!("system clock cannot name remote package sources: {error}")))?
            .as_nanos();
        let path = std::env::temp_dir().join(format!("fern-remote-attempt-sources-{}-{nonce}", std::process::id()));
        fs::create_dir(&path)?;
        Ok(Self { path })
    }
}

impl Drop for TemporaryRemoteSources {
    fn drop(&mut self) {
        // This directory is created by this process under the OS temp root and
        // contains only rewritten transport JSON. It is never an artifact or
        // evidence directory.
        let expected_prefix = format!("fern-remote-attempt-sources-{}-", std::process::id());
        if self.path.parent() == Some(std::env::temp_dir().as_path())
            && self
                .path
                .file_name()
                .and_then(|name| name.to_str())
                .is_some_and(|name| name.starts_with(&expected_prefix))
        {
            let _ = fs::remove_dir_all(&self.path);
        }
    }
}

fn rewrite_remote_runtime_path(value: &mut Value, pointer: &str, replacement: &str) -> Fallible<()> {
    let field = value.pointer_mut(pointer).ok_or_else(|| {
        invalid_input(format!(
            "remote package requires runtime path {pointer}; refusing to guess a missing worker field"
        ))
    })?;
    if !field.is_string() {
        return Err(invalid_input(format!(
            "remote package runtime path {pointer} must be a string"
        )));
    }
    *field = Value::String(replacement.to_string());
    Ok(())
}

fn reject_absolute_json_strings(value: &Value, label: &str) -> Fallible<()> {
    fn visit(value: &Value, path: &str, violations: &mut Vec<String>) {
        match value {
            Value::Object(fields) => {
                for (name, value) in fields {
                    visit(value, &format!("{path}/{name}"), violations);
                }
            }
            Value::Array(values) => {
                for (index, value) in values.iter().enumerate() {
                    visit(value, &format!("{path}/{index}"), violations);
                }
            }
            Value::String(text)
                if Path::new(text).is_absolute()
                    || text.as_bytes().get(1) == Some(&b':')
                    || text.starts_with("\\\\") =>
            {
                violations.push(format!("{path}={text:?}"));
            }
            _ => {}
        }
    }

    let mut violations = Vec::new();
    visit(value, label, &mut violations);
    if violations.is_empty() {
        Ok(())
    } else {
        Err(invalid_input(format!(
            "remote package still contains controller-absolute paths: {}",
            violations.join(", ")
        )))
    }
}

/// Pass 6B - read the worker's candidate report and prove it answers this request.
///
/// This does not seal artifacts or select evidence. It is the process-boundary
/// check that must succeed before later Pass 6C reconciliation is allowed to
/// examine the described files.
#[cfg_attr(
    not(test),
    expect(dead_code, reason = "Pass 6B live worker cutover follows the dual-write proof")
)]
fn read_worker_contract_result(
    files: &WorkerContractFiles,
    request: &WorkerRequestV1,
) -> Fallible<WorkerResultReportV1> {
    let directory = files
        .result_path
        .parent()
        .ok_or_else(|| invalid_input("Pass 6B worker result has no Attempt directory"))?;
    let result: WorkerResultReportV1 = read_attempt_contract_json(directory, "worker_result_report.v1.json")?;
    result
        .validate_against(request)
        .map_err(|error| invalid_input(format!("Pass 6B worker result was refused: {error}")))?;
    Ok(result)
}

fn attempt_contract_directories(files: &WorkerContractFiles) -> Fallible<(PathBuf, PathBuf)> {
    let current_dir = files
        .request_path
        .parent()
        .ok_or_else(|| invalid_input("Pass 6C worker request has no Attempt directory"))?;
    let attempt_dir = current_dir
        .parent()
        .ok_or_else(|| invalid_input("Pass 6C worker request has no Attempt parent"))?;
    Ok((attempt_dir.join("staging"), attempt_dir.join("committed")))
}

/// Pass 6C - derive the next safe action from durable Attempt directories.
///
/// This function never mutates, repairs, or guesses success. In particular,
/// both `staging` and `committed` existing is a conflict, not permission to
/// delete one of them.
#[cfg_attr(
    not(test),
    expect(dead_code, reason = "Pass 6C inspection is focused-tested before live cutover")
)]
pub(crate) fn inspect_attempt_artifacts(files: &WorkerContractFiles) -> Fallible<AttemptArtifactState> {
    let (staging_dir, committed_dir) = attempt_contract_directories(files)?;
    let staging_exists = staging_dir.try_exists()?;
    let committed_exists = committed_dir.try_exists()?;
    if staging_exists && committed_exists {
        return Ok(AttemptArtifactState::ConflictingLocations);
    }
    if committed_exists {
        return Ok(match read_attempt_contract_at(&committed_dir) {
            Ok((_, _, commit)) => match verify_committed_output_bytes(&committed_dir, &commit) {
                Ok(()) => AttemptArtifactState::Committed,
                Err(error) => AttemptArtifactState::Invalid(error.to_string()),
            },
            Err(error) => AttemptArtifactState::Invalid(error.to_string()),
        });
    }
    if !staging_exists || !staging_dir.join("worker_request.v1.json").is_file() {
        return Ok(AttemptArtifactState::MissingRequest);
    }
    let request: WorkerRequestV1 = match read_attempt_contract_json(&staging_dir, "worker_request.v1.json") {
        Ok(request) => request,
        Err(error) => return Ok(AttemptArtifactState::Invalid(error.to_string())),
    };
    if let Err(error) = request.validate() {
        return Ok(AttemptArtifactState::Invalid(error.to_string()));
    }
    if staging_dir.join(ATTEMPT_ARTIFACT_COMMIT_FILE).is_file() {
        return Ok(match read_attempt_contract_at(&staging_dir) {
            Ok((_, _, commit)) => match verify_committed_output_bytes(&staging_dir, &commit) {
                Ok(()) => AttemptArtifactState::PreparedCommitNeedsPromotion,
                Err(error) => AttemptArtifactState::Invalid(error.to_string()),
            },
            Err(error) => AttemptArtifactState::Invalid(error.to_string()),
        });
    }
    if staging_dir.join("worker_result_report.v1.json").is_file() {
        let result: WorkerResultReportV1 =
            match read_attempt_contract_json(&staging_dir, "worker_result_report.v1.json") {
                Ok(result) => result,
                Err(error) => return Ok(AttemptArtifactState::Invalid(error.to_string())),
            };
        return Ok(match result.validate_against(&request) {
            Ok(()) => AttemptArtifactState::ResultNeedsVerification,
            Err(error) => AttemptArtifactState::Invalid(error.to_string()),
        });
    }
    Ok(AttemptArtifactState::AwaitingWorkerResult)
}

fn read_attempt_contract_at(
    directory: &Path,
) -> Fallible<(WorkerRequestV1, WorkerResultReportV1, AttemptArtifactCommitV1)> {
    let request: WorkerRequestV1 = read_attempt_contract_json(directory, "worker_request.v1.json")?;
    let result: WorkerResultReportV1 = read_attempt_contract_json(directory, "worker_result_report.v1.json")?;
    let commit: AttemptArtifactCommitV1 = read_attempt_contract_json(directory, ATTEMPT_ARTIFACT_COMMIT_FILE)?;
    commit
        .validate_against(&request, &result)
        .map_err(|error| invalid_input(format!("invalid Pass 6C artifact commit: {error}")))?;
    Ok((request, result, commit))
}

fn read_attempt_contract_json<T>(directory: &Path, relative_path: &str) -> Fallible<T>
where
    T: DeserializeOwned + fern_json_contract::JsonContract,
{
    Ok(fern_json_contract::checked_from_slice(&read_regular_attempt_file(
        directory,
        relative_path,
    )?)?)
}

fn verify_committed_output_bytes(directory: &Path, commit: &AttemptArtifactCommitV1) -> Fallible<()> {
    for output in commit.outputs() {
        let bytes = read_regular_attempt_file(directory, output.relative_path())?;
        if output.artifact().byte_count != u64::try_from(bytes.len())?
            || output.artifact().byte_digest != ByteDigest::from_bytes(&bytes)
        {
            return Err(commit_conflict(format!(
                "artifact {} no longer matches the bytes committed by this Attempt",
                output.artifact().artifact_key.as_str()
            )));
        }
    }
    Ok(())
}

fn commit_conflict(detail: impl Into<String>) -> Box<dyn std::error::Error + Send + Sync> {
    invalid_input(format!("CommitConflict: {}", detail.into()))
}

fn validate_attempt_relative_path(relative_path: &str) -> Fallible<()> {
    if relative_path.is_empty()
        || relative_path.starts_with('/')
        || relative_path.starts_with('\\')
        || relative_path.contains('\\')
        || relative_path
            .split('/')
            .any(|component| component.is_empty() || component == "." || component == ".." || component.contains(':'))
    {
        return Err(invalid_input(format!(
            "Pass 6C artifact path {relative_path:?} is not a portable path beneath its Attempt"
        )));
    }
    Ok(())
}

/// Read one output relative to an already opened Attempt directory.
///
/// On Linux, `openat2(BENEATH|NO_SYMLINKS|NO_MAGICLINKS|NO_XDEV)` makes path
/// resolution and opening one kernel operation. A concurrent process cannot
/// replace a checked component with a symlink between `stat` and `open`.
/// Checking only the final file would be insufficient: `outputs/` itself could
/// point outside the Attempt while `outputs/0` appears regular.
#[cfg(target_os = "linux")]
fn read_regular_attempt_file(directory: &Path, relative_path: &str) -> Fallible<Vec<u8>> {
    validate_attempt_relative_path(relative_path)?;
    let root = open(
        directory,
        OFlags::RDONLY | OFlags::DIRECTORY | OFlags::NOFOLLOW | OFlags::CLOEXEC,
        Mode::empty(),
    )
    .map_err(|error| {
        invalid_input(format!(
            "Pass 6C could not securely open Attempt root {}: {error}",
            directory.display()
        ))
    })?;
    let output = openat2(
        &root,
        relative_path,
        OFlags::RDONLY | OFlags::NOFOLLOW | OFlags::CLOEXEC,
        Mode::empty(),
        ResolveFlags::BENEATH
            | ResolveFlags::NO_SYMLINKS
            | ResolveFlags::NO_MAGICLINKS
            | ResolveFlags::NO_XDEV,
    )
    .map_err(|error| {
        invalid_input(format!(
            "Pass 6C artifact path {relative_path:?} could not be opened beneath {} without symlinks or mount crossings: {error}",
            directory.display()
        ))
    })?;
    let stat = fstat(&output)?;
    if FileType::from_raw_mode(stat.st_mode) != FileType::RegularFile {
        return Err(invalid_input(format!(
            "Pass 6C artifact path {relative_path:?} is not a regular file"
        )));
    }
    let mut file = fs::File::from(output);
    let mut bytes = Vec::new();
    file.read_to_end(&mut bytes)?;
    Ok(bytes)
}

/// Non-Linux compatibility reader. Fern's supported Linux/RunPod path uses
/// the directory-relative `openat2` reader above. Other hosts retain the
/// trusted-worker check and must not run untrusted plugins.
#[cfg(not(target_os = "linux"))]
fn read_regular_attempt_file(directory: &Path, relative_path: &str) -> Fallible<Vec<u8>> {
    validate_attempt_relative_path(relative_path)?;
    let root_metadata = fs::symlink_metadata(directory)?;
    if !root_metadata.file_type().is_dir() || root_metadata.file_type().is_symlink() {
        return Err(invalid_input(format!(
            "Pass 6C Attempt root {} is not a regular directory",
            directory.display()
        )));
    }
    let components = relative_path.split('/').collect::<Vec<_>>();
    let mut current = directory.to_path_buf();
    for (index, component) in components.iter().enumerate() {
        current.push(component);
        let metadata = fs::symlink_metadata(&current)?;
        let is_final = index + 1 == components.len();
        let valid = if is_final {
            metadata.file_type().is_file() && !metadata.file_type().is_symlink()
        } else {
            metadata.file_type().is_dir() && !metadata.file_type().is_symlink()
        };
        if !valid {
            return Err(invalid_input(format!(
                "Pass 6C artifact path {} contains a symlink or wrong file type",
                current.display()
            )));
        }
    }
    Ok(fs::read(current)?)
}

const DGP_BANK_REQUEST_MAX_BYTES: usize = 65_536;
const DGP_BANK_CHILD_OUTPUT_MAX_BYTES: usize = 65_536;
const DGP_BANK_CHILD_DEADLINE: Duration = Duration::from_secs(120);
const DGP_BANK_CHILD_DRAIN_GRACE: Duration = Duration::from_secs(2);
const DGP_BANK_SAFETENSORS_MAX_HEADER_BYTES: usize = 1024 * 1024;

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct DgpBankWorkerArtifactV1 {
    schema_id: String,
    path: String,
    sha256: String,
    byte_count: u64,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct DgpBankWorkerResultV1 {
    schema: String,
    data_engine: String,
    source_name: String,
    trajectory_seed_base: u64,
    noise_seed_base: u64,
    n_trajectory_draws: u64,
    n_noise_draws: u64,
    noise_kind: String,
    noise_temporal_process: String,
    noise_ar1_phi: f64,
    clean_shape: [u64; 3],
    noise_shape: [u64; 3],
    feature_names: Vec<String>,
    artifact: DgpBankWorkerArtifactV1,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct DgpBankWorkerEnvelopeV1 {
    schema: String,
    status: String,
    #[serde(default)]
    result: Option<DgpBankWorkerResultV1>,
    #[serde(default)]
    category: Option<String>,
    #[serde(default)]
    detail: Option<String>,
}

/// Read only the eight-byte safetensors header length and JSON `__metadata__`.
/// Provenance callers do not load any tensor to obtain source-asset identity.
pub(crate) fn load_dgp_bank_metadata(path: &Path) -> Fallible<BTreeMap<String, String>> {
    let file_metadata = fs::symlink_metadata(path)?;
    if file_metadata.file_type().is_symlink() || !file_metadata.file_type().is_file() {
        return Err(invalid_input(format!(
            "DGP bank {} must be a regular non-symlink file",
            path.display()
        )));
    }
    if file_metadata.len() < 8 {
        return Err(invalid_input("DGP bank is shorter than its safetensors header length"));
    }
    let mut bank_file = fs::File::open(path)?;
    let mut encoded_length = [0u8; 8];
    bank_file.read_exact(&mut encoded_length)?;
    let header_length = usize::try_from(u64::from_le_bytes(encoded_length))
        .map_err(|_| invalid_input("DGP bank safetensors header length overflows usize"))?;
    if header_length > DGP_BANK_SAFETENSORS_MAX_HEADER_BYTES {
        return Err(invalid_input(format!(
            "DGP bank safetensors header exceeds {DGP_BANK_SAFETENSORS_MAX_HEADER_BYTES} bytes"
        )));
    }
    if u64::try_from(header_length)
        .ok()
        .and_then(|length| length.checked_add(8))
        .is_none_or(|total| total > file_metadata.len())
    {
        return Err(invalid_input("DGP bank safetensors header length exceeds file size"));
    }
    let mut header_bytes = vec![0u8; header_length];
    bank_file.read_exact(&mut header_bytes)?;
    let header: Value = serde_json::from_slice(&header_bytes)
        .map_err(|error| invalid_input(format!("DGP bank safetensors header is not JSON: {error}")))?;
    let metadata = header
        .get("__metadata__")
        .and_then(Value::as_object)
        .ok_or_else(|| invalid_input("DGP bank safetensors header has no __metadata__ object"))?;
    metadata
        .iter()
        .map(|(key, value)| {
            value
                .as_str()
                .map(|value| (key.clone(), value.to_string()))
                .ok_or_else(|| invalid_input(format!("DGP bank metadata {key:?} must be a string")))
        })
        .collect()
}

fn read_bounded_child_stream<R: Read>(mut stream: R, stream_name: &'static str) -> Fallible<Vec<u8>> {
    let mut bytes = Vec::new();
    stream
        .by_ref()
        .take((DGP_BANK_CHILD_OUTPUT_MAX_BYTES + 1) as u64)
        .read_to_end(&mut bytes)?;
    if bytes.len() > DGP_BANK_CHILD_OUTPUT_MAX_BYTES {
        return Err(invalid_input(format!(
            "Python DGP-bank child {stream_name} exceeds {DGP_BANK_CHILD_OUTPUT_MAX_BYTES} bytes"
        )));
    }
    Ok(bytes)
}

fn stop_dgp_bank_child(child: &mut std::process::Child) {
    let _ = child.kill();
    let _ = child.wait();
}

fn take_dgp_bank_child_stream(receiver: &mpsc::Receiver<Fallible<Vec<u8>>>, stream_name: &str) -> Fallible<Vec<u8>> {
    receiver.recv_timeout(DGP_BANK_CHILD_DRAIN_GRACE).map_err(|error| invalid_input(format!(
        "Python DGP-bank child {stream_name} did not close within {:?} after direct-child exit: {error}; descendant-owned pipes are not accepted", DGP_BANK_CHILD_DRAIN_GRACE
    )))?
}

fn run_python_dgp_bank_worker(python: &str, worker_request: &Value) -> Fallible<DgpBankWorkerEnvelopeV1> {
    let request_bytes = serde_json::to_vec(worker_request)?;
    if request_bytes.len() > DGP_BANK_REQUEST_MAX_BYTES {
        return Err(invalid_input(format!(
            "Python DGP-bank request exceeds {DGP_BANK_REQUEST_MAX_BYTES} bytes"
        )));
    }
    let mut child = Command::new(python)
        .args(["-m", "serie.bank_artifact"])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| invalid_input(format!("cannot start Python DGP-bank child {python:?}: {error}")))?;
    let mut stdin = child
        .stdin
        .take()
        .ok_or_else(|| invalid_input("Python DGP-bank child has no stdin pipe"))?;
    stdin.write_all(&request_bytes)?;
    drop(stdin);
    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| invalid_input("Python DGP-bank child has no stdout pipe"))?;
    let stderr = child
        .stderr
        .take()
        .ok_or_else(|| invalid_input("Python DGP-bank child has no stderr pipe"))?;
    let (stdout_sender, stdout_receiver) = mpsc::sync_channel(1);
    let (stderr_sender, stderr_receiver) = mpsc::sync_channel(1);
    thread::spawn(move || {
        let _ = stdout_sender.send(read_bounded_child_stream(stdout, "stdout"));
    });
    thread::spawn(move || {
        let _ = stderr_sender.send(read_bounded_child_stream(stderr, "stderr"));
    });
    let started = Instant::now();
    let mut stdout_complete: Option<Fallible<Vec<u8>>> = None;
    let mut stderr_complete: Option<Fallible<Vec<u8>>> = None;
    let status = loop {
        if stdout_complete.is_none()
            && let Ok(result) = stdout_receiver.try_recv()
        {
            match result {
                Ok(bytes) => stdout_complete = Some(Ok(bytes)),
                Err(error) => {
                    stop_dgp_bank_child(&mut child);
                    return Err(error);
                }
            }
        }
        if stderr_complete.is_none()
            && let Ok(result) = stderr_receiver.try_recv()
        {
            match result {
                Ok(bytes) => stderr_complete = Some(Ok(bytes)),
                Err(error) => {
                    stop_dgp_bank_child(&mut child);
                    return Err(error);
                }
            }
        }
        if let Some(status) = child.try_wait()? {
            break status;
        }
        if started.elapsed() >= DGP_BANK_CHILD_DEADLINE {
            stop_dgp_bank_child(&mut child);
            return Err(invalid_input(format!(
                "Python DGP-bank child exceeded {:?}; direct child killed and reaped",
                DGP_BANK_CHILD_DEADLINE
            )));
        }
        thread::sleep(Duration::from_millis(5));
    };
    let stdout = match stdout_complete {
        Some(result) => result?,
        None => take_dgp_bank_child_stream(&stdout_receiver, "stdout")?,
    };
    let stderr = match stderr_complete {
        Some(result) => result?,
        None => take_dgp_bank_child_stream(&stderr_receiver, "stderr")?,
    };
    if !stderr.is_empty() {
        return Err(invalid_input(format!(
            "Python DGP-bank child wrote stderr: {}",
            String::from_utf8_lossy(&stderr)
        )));
    }
    if !status.success() {
        return Err(invalid_input(format!(
            "Python DGP-bank child exited {status} without an accepted result: {}",
            String::from_utf8_lossy(&stdout)
        )));
    }
    if stdout.iter().filter(|byte| **byte == b'\n').count() != 1 || !stdout.ends_with(b"\n") {
        return Err(invalid_input(
            "Python DGP-bank child must emit exactly one newline-terminated envelope",
        ));
    }
    serde_json::from_slice(&stdout)
        .map_err(|error| invalid_input(format!("Python DGP-bank child emitted invalid envelope JSON: {error}")))
}

/// Build one Python-owned bank before training, then bind only reopened artifact
/// identity into the immutable request. Rust does not generate trajectory/noise.
pub(crate) fn ensure_request_dgp_bank_with_python(
    request: &mut Value,
    work_root: &Path,
    python: Option<&str>,
) -> Fallible<Option<PathBuf>> {
    let data_engine = request["run_packet"]["context"]["experiment"]["data_engine"]
        .as_str()
        .ok_or_else(|| invalid_input("worker request run_packet.context.experiment.data_engine is missing"))?;
    if data_engine != "serie_py" {
        return Err(invalid_input(format!(
            "worker request data_engine must be 'serie_py' for the Python bank producer, found {data_engine:?}"
        )));
    }
    let python = python.ok_or_else(|| {
        invalid_input("the worker interpreter was never resolved against a profile; Python DGP bank was not started")
    })?;
    let cell = request
        .get("cell")
        .and_then(Value::as_object)
        .ok_or_else(|| invalid_input("worker request cell must be an object"))?;
    let required_string = |field: &str| {
        cell.get(field)
            .and_then(Value::as_str)
            .map(str::to_string)
            .ok_or_else(|| invalid_input(format!("worker request cell.{field} must be a string")))
    };
    let required_u64 = |field: &str| {
        cell.get(field)
            .and_then(Value::as_u64)
            .ok_or_else(|| invalid_input(format!("worker request cell.{field} must be a non-negative integer")))
    };
    let source_name = required_string("source_name")?;
    let trajectory_seed_base = required_u64("trajectory_seed_base")?;
    let noise_seed_base = required_u64("noise_seed_base")?;
    let n_trajectory_draws = required_u64("trajectory_draw_ordinal")?
        .checked_add(1)
        .ok_or_else(|| invalid_input("trajectory_draw_ordinal cannot be u64::MAX"))?;
    let n_noise_draws = required_u64("noise_draw_ordinal")?
        .checked_add(1)
        .ok_or_else(|| invalid_input("noise_draw_ordinal cannot be u64::MAX"))?;
    let data = request
        .pointer("/run_packet/config/data")
        .and_then(Value::as_object)
        .ok_or_else(|| invalid_input("worker request run_packet.config.data must be an object"))?;
    let noise_kind = data
        .get("noise_kind")
        .and_then(Value::as_str)
        .ok_or_else(|| invalid_input("worker request run_packet.config.data.noise_kind must be a string"))?
        .to_string();
    let noise_ar1_phi = data
        .get("noise_ar1_phi")
        .and_then(Value::as_f64)
        .ok_or_else(|| invalid_input("worker request run_packet.config.data.noise_ar1_phi must be a finite number"))?;
    if !noise_ar1_phi.is_finite() {
        return Err(invalid_input(
            "worker request run_packet.config.data.noise_ar1_phi must be finite",
        ));
    }
    let absolute_work_root = if work_root.is_absolute() {
        work_root.to_path_buf()
    } else {
        std::env::current_dir()?.join(work_root)
    };
    let cache_identity = json!({"schema":"fern.serie_py_dgp_bank_request.v1","source_name":source_name,"trajectory_seed_base":trajectory_seed_base,"noise_seed_base":noise_seed_base,"n_trajectory_draws":n_trajectory_draws,"n_noise_draws":n_noise_draws,"noise_kind":noise_kind,"noise_ar1_phi":noise_ar1_phi});
    let cache_digest = ByteDigest::from_bytes(&serde_json::to_vec(&cache_identity)?);
    let bank_path = absolute_work_root.join("banks").join(format!(
        "{}.safetensors",
        cache_digest.as_str().trim_start_matches("sha256:")
    ));
    let worker_request = json!({"data_engine":"serie_py","source_name":source_name,"trajectory_seed_base":trajectory_seed_base,"noise_seed_base":noise_seed_base,"n_trajectory_draws":n_trajectory_draws,"n_noise_draws":n_noise_draws,"noise_kind":noise_kind,"noise_ar1_phi":noise_ar1_phi,"out_file":bank_path});
    let envelope = run_python_dgp_bank_worker(python, &worker_request)?;
    if envelope.schema != "serie.dgp_bank_worker.v1" || envelope.status != "ok" {
        return Err(invalid_input(format!(
            "Python DGP-bank child refused the request: category={:?}, detail={:?}",
            envelope.category, envelope.detail
        )));
    }
    let result = envelope
        .result
        .ok_or_else(|| invalid_input("Python DGP-bank success envelope has no result"))?;
    if result.schema != "serie.dgp_bank_worker_result.v1"
        || result.data_engine != "serie_py"
        || result.source_name != source_name
        || result.trajectory_seed_base != trajectory_seed_base
        || result.noise_seed_base != noise_seed_base
        || result.n_trajectory_draws != n_trajectory_draws
        || result.n_noise_draws != n_noise_draws
        || result.noise_kind != noise_kind
        || result.noise_temporal_process != if noise_ar1_phi == 0.0 { "iid" } else { "ar1" }
        || result.noise_ar1_phi != noise_ar1_phi
    {
        return Err(invalid_input(
            "Python DGP-bank result does not echo the requested scientific identity",
        ));
    }
    let bank_file = fs::symlink_metadata(&bank_path)?;
    if bank_file.file_type().is_symlink() || !bank_file.file_type().is_file() {
        return Err(invalid_input(format!(
            "DGP bank {} must be a regular non-symlink file",
            bank_path.display()
        )));
    }
    let bank_bytes = fs::read(&bank_path)?;
    let artifact_sha256 = ByteDigest::from_bytes(&bank_bytes).as_str().to_string();
    if result.artifact.schema_id != "serie.dgp_bank_safetensors.v1"
        || result.artifact.path != bank_path.display().to_string()
        || result.artifact.sha256 != artifact_sha256
        || result.artifact.byte_count != bank_file.len()
        || result.clean_shape[0] != n_trajectory_draws
        || result.noise_shape[0] != n_noise_draws
        || result.clean_shape[1..] != result.noise_shape[1..]
        || result.feature_names.len() != usize::try_from(result.clean_shape[2]).unwrap_or(usize::MAX)
    {
        return Err(invalid_input(
            "Python DGP-bank result does not match its reopened artifact",
        ));
    }
    let metadata = load_dgp_bank_metadata(&bank_path)?;
    if metadata.get("schema").map(String::as_str) != Some("serie.dgp_bank_safetensors.v1") {
        return Err(invalid_input(
            "Python DGP-bank artifact has the wrong safetensors schema",
        ));
    }
    let artifact_binding =
        json!({"schema_id":"serie.dgp_bank_safetensors.v1","sha256":artifact_sha256,"byte_count":bank_file.len()});
    let request_object = request
        .as_object_mut()
        .ok_or_else(|| invalid_input("worker request must be a JSON object"))?;
    if let Some(bound) = request_object.get("dgp_bank_artifact")
        && bound != &artifact_binding
    {
        return Err(invalid_input(
            "DGP bank bytes disagree with the artifact already bound in this request",
        ));
    }
    request_object.insert("dgp_bank_artifact".to_string(), artifact_binding);
    request_object.insert(
        "dgp_bank_path".to_string(),
        Value::String(bank_path.display().to_string()),
    );
    Ok(Some(bank_path))
}

#[cfg(test)]
mod tests {
    use super::*;
    use fern_artifact::{
        ArtifactKey, ArtifactReference, ArtifactRole, ByteDigest, CASE_REALIZATION_SCHEMA, COMPACT_METRICS_SCHEMA,
        CaseRealization, CompactMetricsCaseV1, CompactMetricsV1, DECLARED_FORECAST_OUTPUT_SCHEMA,
        DeclaredForecastMeaningV1, DeclaredForecastOutputV1, DeclaredPointDerivationV1, ExactRatio, ForecastMeaningV1,
        ForecastValueSpace, LedgerDiagnosticV1, METRIC_VALUE_SCHEMA, MetricAggregationV1, MetricContractV1,
        MetricSourceRepresentation, MetricSpecV1, MetricTargetKind, MetricValueSpace, MetricValueV1,
        ModelImplementationHash, NumericalToleranceV1, OutputContract, PointKind, REPORTED_FORECAST_MEANING_SCHEMA,
        ReportedArtifactV1, ReportedForecastMeaningV1, ReportedPointDerivationV1, RiskPrimitiveV1,
        SCIENTIFIC_CASE_SCHEMA, ScientificCaseId, ScientificCaseReference, ScientificContentDigest,
        TENSOR_LAYOUT_SCHEMA, TensorAxis, TensorByteOrder, TensorDtype, TensorLayout, TensorStorageOrder,
        WORKER_REQUEST_SCHEMA, WORKER_RESULT_REPORT_SCHEMA, WorkerAuthorityReference,
    };
    use std::process::Command;

    fn pass_six_model_implementation() -> ModelImplementationHash {
        ModelImplementationHash::parse(format!("implementation:sha256:{}", "88".repeat(32))).unwrap()
    }

    #[test]
    fn remote_runtime_rewrite_removes_controller_paths_and_rejects_unknown_ones() {
        let mut request = serde_json::json!({
            "dgp_bank_path": "/home/user/study/tmp/bank.safetensors",
            "results_root": "/home/user/study/results/runs",
            "run_packet": {
                "artifact_dir": "/home/user/study/results/runs/one",
                "artifact_root": "/home/user/study/results/runs"
            }
        });
        rewrite_remote_runtime_path(&mut request, "/dgp_bank_path", "inputs/dgp_bank.safetensors").unwrap();
        rewrite_remote_runtime_path(&mut request, "/results_root", "worker_output/results").unwrap();
        rewrite_remote_runtime_path(&mut request, "/run_packet/artifact_dir", "worker_output/artifacts").unwrap();
        rewrite_remote_runtime_path(&mut request, "/run_packet/artifact_root", "worker_output/results").unwrap();
        reject_absolute_json_strings(&request, "request").unwrap();
        assert_eq!(request["dgp_bank_path"], "inputs/dgp_bank.safetensors");

        request["unregistered_path"] = serde_json::json!("/home/user/secret");
        let error = reject_absolute_json_strings(&request, "request")
            .unwrap_err()
            .to_string();
        assert!(error.contains("unregistered_path"), "got: {error}");
    }

    fn pass_six_b_layout() -> TensorLayout {
        TensorLayout {
            schema: TENSOR_LAYOUT_SCHEMA.to_string(),
            dtype: TensorDtype::Float32,
            byte_order: TensorByteOrder::LittleEndian,
            storage_order: TensorStorageOrder::RowMajor,
            axes: ["case", "forecast_origin", "horizon", "channel"]
                .into_iter()
                .map(|name| TensorAxis::try_new(name, 1).unwrap())
                .collect(),
        }
    }

    fn pass_six_b_point_reported_meaning() -> ReportedForecastMeaningV1 {
        ReportedForecastMeaningV1 {
            schema: REPORTED_FORECAST_MEANING_SCHEMA.to_string(),
            scientific_cases: vec![ScientificCaseReference {
                case_id: ScientificCaseId::parse("case:pass-6b").unwrap(),
                manifest_digest: ScientificContentDigest::from_bytes(SCIENTIFIC_CASE_SCHEMA.as_bytes()),
            }],
            forecast_origin_plan: ArtifactReference {
                artifact_key: ArtifactKey::parse("artifact:pass-6b:forecast-origins").unwrap(),
                role: ArtifactRole::ForecastOriginPlan,
                content_digest: ScientificContentDigest::from_bytes(b"forecast-origins"),
            },
            channel_order: vec!["x".to_string()],
            value_space: ForecastValueSpace::Physical,
            scale_reference: None,
            meaning: ForecastMeaningV1::DensePoint {
                point_kind: PointKind::NativePointHead,
                derivation: ReportedPointDerivationV1::NativePointHead {
                    producer_implementation: pass_six_model_implementation(),
                },
            },
        }
    }

    fn pass_six_b_point_contract() -> DeclaredForecastOutputV1 {
        let reported_meaning = pass_six_b_point_reported_meaning();
        DeclaredForecastOutputV1 {
            schema: DECLARED_FORECAST_OUTPUT_SCHEMA.to_string(),
            scientific_cases: reported_meaning.scientific_cases,
            forecast_origin_plan: reported_meaning.forecast_origin_plan,
            channel_order: reported_meaning.channel_order,
            value_space: reported_meaning.value_space,
            scale_reference: reported_meaning.scale_reference,
            meaning: DeclaredForecastMeaningV1::DensePoint {
                point_kind: PointKind::NativePointHead,
                derivation: DeclaredPointDerivationV1::NativePointHead {
                    producer_implementation: pass_six_model_implementation(),
                },
            },
        }
    }

    fn pass_six_b_request() -> WorkerRequestV1 {
        WorkerRequestV1 {
            schema: WORKER_REQUEST_SCHEMA.to_string(),
            matrix_id: "matrix-pass-6b".to_string(),
            coordinate_id: "coordinate-pass-6b".to_string(),
            attempt_id: "attempt-pass-6b".to_string(),
            run_hash: "run-pass-6b".to_string(),
            scientific_case: ScientificCaseReference {
                case_id: ScientificCaseId::parse("case:pass-6b").unwrap(),
                manifest_digest: ScientificContentDigest::from_bytes(SCIENTIFIC_CASE_SCHEMA.as_bytes()),
            },
            realization: CaseRealization {
                schema: CASE_REALIZATION_SCHEMA.to_string(),
                dose: ExactRatio::new(1, 8).unwrap(),
                final_input_content_digest: ScientificContentDigest::from_bytes(b"model-input"),
                retained_final_input: None,
            },
            model_architecture: fern_artifact::ModelArchitectureHash::parse(format!(
                "architecture:sha256:{}",
                "33".repeat(32)
            ))
            .unwrap(),
            implementation: pass_six_model_implementation(),
            execution_profile_revision: ByteDigest::from_bytes(b"cpu-test-profile").as_str().to_string(),
            authority: WorkerAuthorityReference {
                coordinator_lease_id: "coordinator-lease-pass-6b".to_string(),
                coordinator_epoch: 2,
                coordinate_lease_id: "coordinate-lease-pass-6b".to_string(),
                coordinate_epoch: 4,
                resource_lease_id: "resource-lease-pass-6b".to_string(),
                resource_epoch: 7,
            },
            metric_contract: MetricContractV1::time_series_v1(vec![MetricTargetKind::ObservedCorrupted]).unwrap(),
            outputs: vec![
                OutputContract::required(
                    ArtifactKey::parse("artifact:pass-6b:1").unwrap(),
                    ArtifactRole::CompactMetrics,
                    None,
                ),
                OutputContract::required(
                    ArtifactKey::parse("artifact:pass-6b:2").unwrap(),
                    ArtifactRole::InvocationReceipt,
                    None,
                ),
                OutputContract::required(
                    ArtifactKey::parse("artifact:pass-6b:3").unwrap(),
                    ArtifactRole::ExecutionEnvironment,
                    None,
                ),
                OutputContract::required_forecast(
                    ArtifactKey::parse("artifact:pass-6b:4").unwrap(),
                    ArtifactRole::DensePointForecast,
                    pass_six_b_layout(),
                    pass_six_b_point_contract(),
                ),
            ],
        }
    }

    fn pass_six_b_materialized_case() -> MaterializedScientificCase {
        let case_id = ScientificCaseId::parse("case:pass-6b").unwrap();
        let artifact = |suffix: &str, role, content: &[u8]| ArtifactReference {
            artifact_key: ArtifactKey::parse(format!("artifact:pass-6b:{suffix}")).unwrap(),
            role,
            content_digest: ScientificContentDigest::from_bytes(content),
        };
        let forecast_origin_plan = fern_artifact::ForecastOriginPlanV1 {
            schema: fern_artifact::FORECAST_ORIGIN_PLAN_SCHEMA.to_string(),
            case_id: case_id.clone(),
            context_length: 1,
            horizon_length: 1,
            stride: 1,
            origins: vec![fern_artifact::ForecastOriginV1 {
                origin_id: "origin-0".to_string(),
                origin_index: 0,
                history_start: 0,
                history_end: 1,
                forecast_start: 1,
                forecast_end: 2,
            }],
        };
        forecast_origin_plan.validate().unwrap();
        let manifest = fern_artifact::ScientificCaseManifest {
            schema: SCIENTIFIC_CASE_SCHEMA.to_string(),
            case_id,
            scientific_target_hash: ScientificContentDigest::from_bytes(b"target"),
            world_parameters: artifact("world", ArtifactRole::WorldParameters, b"world"),
            reference_trajectory: artifact("reference", ArtifactRole::LatentCleanTrajectory, b"reference"),
            unit_noise: artifact("noise", ArtifactRole::UnitNoise, b"noise"),
            training_scale: artifact("scale", ArtifactRole::TrainingScale, b"scale"),
            split_definition: artifact("split", ArtifactRole::SplitDefinition, b"split"),
            forecast_origin_plan: artifact(
                "forecast-origins",
                ArtifactRole::ForecastOriginPlan,
                b"forecast-origins",
            ),
            masks: Vec::new(),
            pipeline_implementation: fern_artifact::ImplementationDigest::from_bytes(b"pipeline"),
            channel_order: vec!["x".to_string()],
            operation_order: vec![
                fern_artifact::CaseOperation::ScaleFromTrain,
                fern_artifact::CaseOperation::InjectNoise,
                fern_artifact::CaseOperation::Window,
            ],
        };
        manifest.validate().unwrap();
        MaterializedScientificCase {
            manifest,
            forecast_origin_plan,
            observed_target_values: vec![1.0],
            clean_target_values: vec![1.0],
            training_scale_values: vec![1.0],
        }
    }

    fn pass_six_b_reported_artifact(
        role: ArtifactRole,
        ordinal: u8,
        layout: Option<TensorLayout>,
    ) -> ReportedArtifactV1 {
        let stored_bytes: &[u8] = if layout.is_some() { b"fake-container" } else { b"{}" };
        let canonical_content: &[u8] = if layout.is_some() { &[0, 0, 0, 0] } else { b"{}" };
        ReportedArtifactV1::from_payloads(
            ArtifactKey::parse(format!("artifact:pass-6b:{ordinal}")).unwrap(),
            role,
            if layout.is_some() {
                "application/x-safetensors".to_string()
            } else {
                "application/json".to_string()
            },
            stored_bytes,
            canonical_content,
            layout,
            (role == ArtifactRole::DensePointForecast).then(pass_six_b_point_reported_meaning),
        )
        .unwrap()
    }

    fn pass_six_b_json_reported_artifact(
        artifact_key: ArtifactKey,
        role: ArtifactRole,
        stored_bytes: &[u8],
        canonical_content: &[u8],
    ) -> ReportedArtifactV1 {
        ReportedArtifactV1::from_payloads(
            artifact_key,
            role,
            "application/json",
            stored_bytes,
            canonical_content,
            None,
            None,
        )
        .unwrap()
    }

    fn pass_six_b_compact_metrics(request: &WorkerRequestV1, point: &ReportedArtifactV1) -> CompactMetricsV1 {
        let observed = ArtifactReference {
            artifact_key: ArtifactKey::parse("artifact:pass-6b:observed").unwrap(),
            role: ArtifactRole::FinalNoisyInput,
            content_digest: ScientificContentDigest::from_bytes(b"observed"),
        };
        let scale = ArtifactReference {
            artifact_key: ArtifactKey::parse("artifact:pass-6b:scale").unwrap(),
            role: ArtifactRole::TrainingScale,
            content_digest: ScientificContentDigest::from_bytes(b"scale"),
        };
        let point = ArtifactReference {
            artifact_key: point.artifact_key.clone(),
            role: point.role,
            content_digest: point.content_digest.clone(),
        };
        let metric_values = [
            (ExactRatio::new(1, 1).unwrap(), 1.0),
            (ExactRatio::new(3, 2).unwrap(), 1.0),
            (ExactRatio::new(2, 1).unwrap(), 1.0),
        ]
        .into_iter()
        .map(|(threshold_multiplier, value)| MetricValueV1 {
            schema: METRIC_VALUE_SCHEMA.to_string(),
            spec: MetricSpecV1::EffectivePredictionTime {
                metric_version: 1,
                target_kind: MetricTargetKind::ObservedCorrupted,
                target: observed.clone(),
                threshold_source: scale.clone(),
                threshold_multiplier,
                horizon_length: 1,
                source_representation: MetricSourceRepresentation::Point,
                source: point.clone(),
                aggregation: MetricAggregationV1::point_default(),
            },
            value,
        })
        .chain(std::iter::once(MetricValueV1 {
            schema: METRIC_VALUE_SCHEMA.to_string(),
            spec: MetricSpecV1::PerChannelSquaredWasserstein {
                metric_version: 1,
                target_kind: MetricTargetKind::ObservedCorrupted,
                target: observed.clone(),
                horizon_length: 1,
                value_space: MetricValueSpace::Physical,
                scale_reference: None,
                source_representation: MetricSourceRepresentation::Point,
                source: point.clone(),
                aggregation: MetricAggregationV1::point_default(),
            },
            value: 0.0,
        }))
        .collect();
        CompactMetricsV1 {
            schema: COMPACT_METRICS_SCHEMA.to_string(),
            attempt_id: request.attempt_id.clone(),
            request_digest: request.digest().unwrap(),
            metric_contract: request.metric_contract.clone(),
            numerical_tolerance: NumericalToleranceV1::f64_default(),
            checked_source_artifacts: vec![observed, point, scale],
            cases: vec![CompactMetricsCaseV1 {
                case_id: request.scientific_case.case_id.clone(),
                case_realization_digest: request.realization.digest().unwrap(),
                risk_primitive: RiskPrimitiveV1 {
                    element_count: 1,
                    observed_squared_error_sum: 1.0,
                    observed_absolute_error_sum: 1.0,
                    reference_target_kind: None,
                    reference_squared_error_sum: None,
                    injected_noise_squared_sum: None,
                    reference_residual_noise_inner_product_sum: None,
                },
                derived_diagnostic: LedgerDiagnosticV1 {
                    ledger_residual_sum: None,
                },
                metric_values,
            }],
        }
    }

    fn python_for_dgp_bank_process_test() -> String {
        std::env::var("FERN_PYTHON").expect("focused DGP-bank process tests require FERN_PYTHON at the test boundary")
    }

    #[test]
    fn serie_py_request_binds_one_reopened_python_bank_and_header_provenance() {
        let root = std::env::temp_dir().join(format!(
            "fern_bound_bank_{}_{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let mut request = json!({
            "run_packet": {
                "context": {"experiment": {"data_engine": "serie_py"}},
                "config": {"data": {"noise_kind": "gaussian", "noise_ar1_phi": 0.5}}
            },
            "cell": {
                "source_name": "logistic",
                "trajectory_seed_base": 42,
                "noise_seed_base": 1000,
                "trajectory_draw_ordinal": 0,
                "noise_draw_ordinal": 0
            }
        });

        let python = python_for_dgp_bank_process_test();
        let bank_path = ensure_request_dgp_bank_with_python(&mut request, &root, Some(&python))
            .unwrap()
            .unwrap();
        let bank_bytes = fs::read(&bank_path).unwrap();
        assert_eq!(
            request["dgp_bank_artifact"],
            json!({
                "schema_id": "serie.dgp_bank_safetensors.v1",
                "sha256": ByteDigest::from_bytes(&bank_bytes).as_str(),
                "byte_count": bank_bytes.len(),
            })
        );
        assert_eq!(request["dgp_bank_path"], bank_path.display().to_string());
        let metadata = load_dgp_bank_metadata(&bank_path).unwrap();
        assert_eq!(metadata.get("source_name").map(String::as_str), Some("logistic"));
        assert_eq!(
            metadata.get("schema").map(String::as_str),
            Some("serie.dgp_bank_safetensors.v1")
        );
        request["dgp_bank_artifact"]["sha256"] = json!(format!("sha256:{}", "0".repeat(64)));
        let error = ensure_request_dgp_bank_with_python(&mut request, &root, Some(&python))
            .unwrap_err()
            .to_string();
        assert!(error.contains("already bound"), "got: {error}");
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn non_python_engine_refuses_before_the_generator_starts() {
        let root = std::env::temp_dir().join(format!("fern_wrong_bank_engine_{}", std::process::id()));
        let mut request = json!({
            "run_packet": {"context": {"experiment": {"data_engine": "retired_engine"}}}
        });
        let error = ensure_request_dgp_bank_with_python(&mut request, &root, Some("unused-python"))
            .unwrap_err()
            .to_string();
        assert!(error.contains("must be 'serie_py'"), "got: {error}");
        assert!(!root.exists(), "refusal must happen before any generator placement");
    }

    #[test]
    fn bounded_dgp_bank_stream_reader_refuses_an_oversized_stream() {
        let oversized = vec![b'x'; DGP_BANK_CHILD_OUTPUT_MAX_BYTES + 1];
        let error = read_bounded_child_stream(std::io::Cursor::new(oversized), "stdout")
            .unwrap_err()
            .to_string();
        assert!(error.contains("stdout exceeds"), "got: {error}");
    }

    #[test]
    fn python_dgp_bank_refusal_exits_before_any_artifact_is_bound() {
        let root = std::env::temp_dir().join(format!("fern_bad_bank_source_{}", std::process::id()));
        let mut request = json!({
            "run_packet": {
                "context": {"experiment": {"data_engine": "serie_py"}},
                "config": {"data": {"noise_kind": "gaussian", "noise_ar1_phi": 0.0}}
            },
            "cell": {
                "source_name": "no_such_serie_source",
                "trajectory_seed_base": 42,
                "noise_seed_base": 1000,
                "trajectory_draw_ordinal": 0,
                "noise_draw_ordinal": 0
            }
        });
        let python = python_for_dgp_bank_process_test();
        let error = ensure_request_dgp_bank_with_python(&mut request, &root, Some(&python))
            .unwrap_err()
            .to_string();
        assert!(error.contains("exited"), "got: {error}");
        assert!(request.get("dgp_bank_path").is_none());
        assert!(
            !root
                .join("banks")
                .read_dir()
                .unwrap()
                .any(|entry| entry.unwrap().path().is_file()),
            "refusal must leave no completed bank artifact behind"
        );
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn typed_native_adapter_unfolds_channel_independent_batches_without_transposing_time() {
        // Worker order is [origin * channel, horizon, 1]. Canonical order is
        // [origin, horizon, channel]. Distinct decimal places make every axis
        // swap visible in one short fixture.
        let pred = vec![10.0, 11.0, 20.0, 21.0, 30.0, 31.0, 40.0, 41.0];
        let truth = vec![110.0, 111.0, 120.0, 121.0, 130.0, 131.0, 140.0, 141.0];
        let (point, observed) = canonical_native_values(&pred, &truth, 4, 1, 2, 2, 2).unwrap();
        assert_eq!(point, vec![10.0, 20.0, 11.0, 21.0, 30.0, 40.0, 31.0, 41.0]);
        assert_eq!(observed, vec![110.0, 120.0, 111.0, 121.0, 130.0, 140.0, 131.0, 141.0]);
    }

    #[cfg(unix)]
    #[test]
    fn pass_six_c_refuses_a_symlinked_parent_directory() {
        use std::os::unix::fs::symlink;

        let root = std::env::temp_dir().join(format!(
            "fern_pass_6c_symlink_{}_{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let attempt = root.join("attempt");
        let outside = root.join("outside");
        fs::create_dir_all(&attempt).unwrap();
        fs::create_dir_all(&outside).unwrap();
        fs::write(outside.join("forecast.bin"), b"outside").unwrap();
        symlink(&outside, attempt.join("outputs")).unwrap();

        let error = read_regular_attempt_file(&attempt, "outputs/forecast.bin")
            .unwrap_err()
            .to_string();
        assert!(error.contains("without symlinks or mount crossings"), "got: {error}");
        let error = read_regular_attempt_file(&attempt, "../outside/forecast.bin")
            .unwrap_err()
            .to_string();
        assert!(error.contains("not a portable path beneath"), "got: {error}");
        fs::remove_dir_all(root).unwrap();
    }

}
