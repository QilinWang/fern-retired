//! Declared execution environments for Fern experiment commands.
//!
//! A profile answers operational questions that users previously retyped as
//! `--python`, `--db`, `--artifact-root`, `--work-root`, and `--device` on
//! every command.  It is deliberately separate from a scientific run
//! description: paths and host names say where work happens, not what the
//! experiment means.
//!
//! The current executor validates a local profile. An SSH profile can be
//! declared so its intended host is visible, but a later executor owns the
//! remote probe and lease; this module must not pretend a local file check
//! proves anything about a remote machine.

use std::{
    collections::{BTreeMap, BTreeSet},
    fs,
    io::{BufReader, Read, Write},
    path::{Path, PathBuf},
    time::{SystemTime, UNIX_EPOCH},
};

#[cfg(target_os = "linux")]
use rustix::fs::{CWD, FileType, Mode, OFlags, RenameFlags, ResolveFlags, fstat, open, openat2, renameat_with};

use fern_json_contract::JsonContract;
use schemars::JsonSchema;
use serde::{Deserialize, Serialize, de::DeserializeOwned};
use sha2::{Digest, Sha256};

use fern_experiment_types::DeviceKind;

pub const EXECUTION_PROFILE_SCHEMA: &str = "fern.execution_profile.v5";
pub const EXECUTION_PROFILE_REVISION_SCHEMA: &str = "fern.execution_profile_revision.v1";
pub const OBSERVED_EXECUTION_FINGERPRINT_SCHEMA: &str = "fern.observed_execution_fingerprint.v2";
pub const REMOTE_ATTEMPT_INSTRUCTIONS_SCHEMA: &str = "fern.remote_attempt_instructions.v1";
pub const REMOTE_ATTEMPT_REPORT_SCHEMA: &str = "fern.remote_attempt_report.v1";
pub const REMOTE_ATTEMPT_INSTRUCTIONS_FILE: &str = "remote_attempt_instructions.v1.json";
pub const REMOTE_ATTEMPT_REPORT_FILE: &str = "remote_attempt_report.v1.json";

/// One campaign-wide storage policy shared by preflight and sweep execution.
/// Percentage is intentionally absent: it may be displayed as diagnostics,
/// but it has no authority to block work.
pub const CAMPAIGN_STORAGE_WARN_FREE_GIB: u64 = 50;
/// CHG 2026-08-13: 15 -> 10 GiB, by the author's decision. WSL's virtual disk
/// grows into Windows C:, and freeing space INSIDE WSL does not hand it back --
/// it only stops the file growing. So the local machine settles just under
/// whatever the floor is and every lane stops there. Ten still leaves room
/// for a cell's tensors many times over.
/// CHG 2026-08-16: 10 -> 5 GiB, by the author's decision. The number the gate
/// reads is Windows C: free space, and that number understates what is
/// available: the WSL volume held 701 GiB free at the moment C: showed 7.2.
/// Freeing 58 GiB inside WSL that day moved C: the wrong way, which is the same
/// mechanism the 2026-08-13 note describes -- the gate was measuring the
/// container, not the room inside it. Five keeps a real floor against a genuinely
/// full disk while no longer stopping lanes on a reading that is not the
/// constraint. The absolute-bytes-only rule and the 50 GiB warning are unchanged.
pub const CAMPAIGN_STORAGE_HARD_STOP_FREE_GIB: u64 = 5;

/// One byte object carried between Registry Home and a Worker Site.
///
/// Paths are relative to one Attempt package. They are transport names, not
/// scientific identity or permission to read an arbitrary remote path.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RemoteAttemptFileV1 {
    pub relative_path: String,
    pub byte_count: u64,
    pub byte_digest: String,
}

impl RemoteAttemptFileV1 {
    pub fn from_bytes(relative_path: impl Into<String>, bytes: &[u8]) -> Result<Self, String> {
        let file = Self {
            relative_path: relative_path.into(),
            byte_count: u64::try_from(bytes.len()).map_err(|_| "remote Attempt file is too large".to_string())?,
            byte_digest: format!("sha256:{:x}", Sha256::digest(bytes)),
        };
        file.validate()?;
        Ok(file)
    }

    pub fn verify_bytes(&self, bytes: &[u8]) -> Result<(), String> {
        self.validate()?;
        let observed_count = u64::try_from(bytes.len()).map_err(|_| "remote Attempt file is too large".to_string())?;
        let observed_digest = format!("sha256:{:x}", Sha256::digest(bytes));
        if self.byte_count != observed_count || self.byte_digest != observed_digest {
            return Err(format!(
                "remote Attempt file {:?} changed: expected {} bytes / {}, found {} bytes / {}",
                self.relative_path, self.byte_count, self.byte_digest, observed_count, observed_digest
            ));
        }
        Ok(())
    }

    /// Describe one regular local file without putting its host path in the
    /// transport manifest.
    pub fn from_file(relative_path: impl Into<String>, source_path: &Path) -> Result<Self, String> {
        let relative_path = relative_path.into();
        let metadata = fs::symlink_metadata(source_path).map_err(|error| {
            format!(
                "cannot inspect remote Attempt source {}: {error}",
                source_path.display()
            )
        })?;
        if !metadata.file_type().is_file() || metadata.file_type().is_symlink() {
            return Err(format!(
                "remote Attempt source {} must be a regular non-symlink file",
                source_path.display()
            ));
        }
        let (byte_count, byte_digest) = digest_reader(
            fs::File::open(source_path)
                .map(BufReader::new)
                .map_err(|error| format!("cannot open remote Attempt source {}: {error}", source_path.display()))?,
        )?;
        let file = Self {
            relative_path,
            byte_count,
            byte_digest,
        };
        file.validate()?;
        Ok(file)
    }

    fn validate(&self) -> Result<(), String> {
        let path = Path::new(&self.relative_path);
        if self.relative_path.trim().is_empty()
            || path.is_absolute()
            || self.relative_path.contains('\\')
            || self.relative_path.contains(':')
            || self.relative_path.split('/').any(|component| component.is_empty())
            || path.components().any(|component| {
                matches!(
                    component,
                    std::path::Component::CurDir
                        | std::path::Component::ParentDir
                        | std::path::Component::RootDir
                        | std::path::Component::Prefix(_)
                )
            })
            || matches!(
                self.relative_path.as_str(),
                REMOTE_ATTEMPT_INSTRUCTIONS_FILE | REMOTE_ATTEMPT_REPORT_FILE
            )
        {
            return Err(format!(
                "remote Attempt path {:?} must be a portable package-relative payload path",
                self.relative_path
            ));
        }
        if self.byte_count == 0 {
            return Err(format!(
                "remote Attempt file {:?} must not be empty",
                self.relative_path
            ));
        }
        if !valid_sha256(&self.byte_digest) {
            return Err(format!(
                "remote Attempt file {:?} has an invalid sha256 digest",
                self.relative_path
            ));
        }
        Ok(())
    }
}

/// One local source selected for an Attempt package.
///
/// `source_path` is local transport plumbing and is never serialized. The
/// package manifest carries only `relative_path`, size, and digest.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RemoteAttemptSourceFileV1 {
    pub relative_path: String,
    pub source_path: PathBuf,
}

impl RemoteAttemptSourceFileV1 {
    pub fn new(relative_path: impl Into<String>, source_path: impl Into<PathBuf>) -> Self {
        Self {
            relative_path: relative_path.into(),
            source_path: source_path.into(),
        }
    }
}

/// Temporary transport wrapper around one Home request.
///
/// Identity fields are copied from the request. This envelope is not a second
/// Attempt authority: after return, Registry Home reopens the request and
/// report. It contains no lease capability secret and cannot claim another
/// Coordinate.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RemoteAttemptInstructionsV1 {
    pub schema: String,
    pub matrix_id: String,
    pub coordinate_id: String,
    pub attempt_id: String,
    pub run_hash: String,
    pub execution_profile_revision: String,
    pub worker_request_content_digest: String,
    pub files: Vec<RemoteAttemptFileV1>,
}

impl JsonContract for RemoteAttemptInstructionsV1 {
    const SCHEMA_ID: &'static str = REMOTE_ATTEMPT_INSTRUCTIONS_SCHEMA;
}

impl RemoteAttemptInstructionsV1 {
    pub fn canonical_digest(&self) -> Result<String, String> {
        self.validate()?;
        let mut canonical = self.clone();
        canonical
            .files
            .sort_by(|left, right| left.relative_path.cmp(&right.relative_path));
        let bytes = serde_json::to_vec(&canonical).map_err(|error| error.to_string())?;
        Ok(format!("sha256:{:x}", Sha256::digest(bytes)))
    }

    pub fn validate(&self) -> Result<(), String> {
        if self.schema != REMOTE_ATTEMPT_INSTRUCTIONS_SCHEMA {
            return Err(format!(
                "remote instructions schema must be {REMOTE_ATTEMPT_INSTRUCTIONS_SCHEMA:?}"
            ));
        }
        for (field, value) in [
            ("matrix_id", &self.matrix_id),
            ("coordinate_id", &self.coordinate_id),
            ("attempt_id", &self.attempt_id),
            ("run_hash", &self.run_hash),
        ] {
            if value.trim().is_empty() {
                return Err(format!("remote instructions {field} must be non-empty"));
            }
        }
        if !valid_sha256(&self.execution_profile_revision) || !valid_sha256(&self.worker_request_content_digest) {
            return Err("remote instructions require full sha256 profile and worker-request digests".to_string());
        }
        if self.files.is_empty() {
            return Err("remote instructions require at least one package file".to_string());
        }
        let mut paths = BTreeSet::new();
        for file in &self.files {
            file.validate()?;
            if !paths.insert(file.relative_path.as_str()) {
                return Err(format!(
                    "remote instructions repeat package path {:?}",
                    file.relative_path
                ));
            }
        }
        match request_contract_path(&paths) {
            Ok(_) => Ok(()),
            Err(error) => Err(error),
        }
    }
}

/// Which scientific request this package carries. A worker package and a Stage
/// package share the same envelope; they must not mix request contracts.
pub fn request_contract_path(paths: &BTreeSet<&str>) -> Result<&'static str, String> {
    let worker = paths.contains("worker_request.v1.json");
    let stage = paths.contains("request/stage_runner_request.v1.json");
    match (worker, stage) {
        (true, false) => Ok("worker_request.v1.json"),
        (false, true) => Ok("request/stage_runner_request.v1.json"),
        (true, true) => {
            Err("remote instructions must not mix worker_request.v1.json and a Stage runner request".to_string())
        }
        (false, false) => Err(
            "remote instructions must carry worker_request.v1.json or request/stage_runner_request.v1.json".to_string(),
        ),
    }
}

/// Build a transport envelope from Home intent. Do not invent Matrix or handler names.
///
/// A worker request already names Matrix, Coordinate, Attempt, run, and profile.
/// A Stage request names only the Attempt; Matrix/Coordinate wait for Plan
/// registration, so those envelope fields repeat the Attempt id.
pub fn derive_remote_attempt_instructions_from_home_request(
    request: &serde_json::Value,
    request_content_digest: &str,
    files: Vec<RemoteAttemptFileV1>,
    stage_transport_profile_revision: Option<&str>,
) -> Result<RemoteAttemptInstructionsV1, String> {
    let schema = request
        .get("schema")
        .and_then(serde_json::Value::as_str)
        .ok_or_else(|| "Home request must carry a schema tag".to_string())?;
    let (matrix_id, coordinate_id, attempt_id, run_hash, execution_profile_revision) = match schema {
        "fern.worker_request.v1" => (
            required_request_text(request, "matrix_id")?,
            required_request_text(request, "coordinate_id")?,
            required_request_text(request, "attempt_id")?,
            required_request_text(request, "run_hash")?,
            required_request_text(request, "execution_profile_revision")?,
        ),
        "fern.stage_runner_request.v1" => {
            let attempt_id = required_request_text(request, "stage_attempt_id")?;
            let run_hash = required_request_text(request, "execution_key")?;
            let profile = stage_transport_profile_revision
                .ok_or_else(|| {
                    "Stage transport needs the execution-profile revision; the Stage request does not carry it"
                        .to_string()
                })?
                .to_string();
            (attempt_id.clone(), attempt_id.clone(), attempt_id, run_hash, profile)
        }
        other => {
            return Err(format!(
                "remote envelope derives only from fern.worker_request.v1 or fern.stage_runner_request.v1, not {other:?}"
            ));
        }
    };
    let instructions = RemoteAttemptInstructionsV1 {
        schema: REMOTE_ATTEMPT_INSTRUCTIONS_SCHEMA.to_string(),
        matrix_id,
        coordinate_id,
        attempt_id,
        run_hash,
        execution_profile_revision,
        worker_request_content_digest: request_content_digest.to_string(),
        files,
    };
    instructions.validate()?;
    Ok(instructions)
}

/// Build a transport return wrapper from the runner claim.
pub fn derive_remote_attempt_report_from_runner_claim(
    report: &serde_json::Value,
    report_content_digest: &str,
    instructions: &RemoteAttemptInstructionsV1,
    files: Vec<RemoteAttemptFileV1>,
) -> Result<RemoteAttemptReportV1, String> {
    let schema = report
        .get("schema")
        .and_then(serde_json::Value::as_str)
        .ok_or_else(|| "runner claim must carry a schema tag".to_string())?;
    let attempt_id = match schema {
        "fern.worker_result_report.v1" => required_request_text(report, "attempt_id")?,
        "fern.stage_runner_report.v1" => required_request_text(report, "stage_attempt_id")?,
        other => {
            return Err(format!(
                "remote envelope derives only from fern.worker_result_report.v1 or fern.stage_runner_report.v1, not {other:?}"
            ));
        }
    };
    if attempt_id != instructions.attempt_id {
        return Err("runner claim does not answer this Attempt".to_string());
    }
    let remote_report = RemoteAttemptReportV1 {
        schema: REMOTE_ATTEMPT_REPORT_SCHEMA.to_string(),
        instructions_digest: instructions.canonical_digest()?,
        attempt_id,
        worker_result_content_digest: report_content_digest.to_string(),
        files,
    };
    remote_report.validate_against(instructions)?;
    Ok(remote_report)
}

fn required_request_text(value: &serde_json::Value, field: &str) -> Result<String, String> {
    value
        .get(field)
        .and_then(serde_json::Value::as_str)
        .map(str::to_string)
        .filter(|text| !text.trim().is_empty())
        .ok_or_else(|| format!("Home request field {field} must be non-empty text"))
}

/// Which scientific result this return carries. Same envelope, one contract.
pub fn report_contract_path(paths: &BTreeSet<&str>) -> Result<&'static str, String> {
    let worker = paths.contains("worker_result_report.v1.json");
    let stage = paths.contains("report/stage_runner_report.v1.json");
    match (worker, stage) {
        (true, false) => Ok("worker_result_report.v1.json"),
        (false, true) => Ok("report/stage_runner_report.v1.json"),
        (true, true) => {
            Err("remote report must not mix worker_result_report.v1.json and a Stage runner report".to_string())
        }
        (false, false) => Err(
            "remote report must carry worker_result_report.v1.json or report/stage_runner_report.v1.json".to_string(),
        ),
    }
}

/// Temporary return wrapper around one runner report.
///
/// Digests and file lists are copied from the report. Home reopens the report
/// and output bytes; this envelope is not a checked receipt.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RemoteAttemptReportV1 {
    pub schema: String,
    pub instructions_digest: String,
    pub attempt_id: String,
    pub worker_result_content_digest: String,
    pub files: Vec<RemoteAttemptFileV1>,
}

impl JsonContract for RemoteAttemptReportV1 {
    const SCHEMA_ID: &'static str = REMOTE_ATTEMPT_REPORT_SCHEMA;
}

pub fn json_contracts() -> Vec<fern_json_contract::ContractRegistration> {
    vec![
        fern_json_contract::ContractRegistration::of::<RemoteAttemptInstructionsV1>(
            "Registry Home work order for one remote Attempt",
        ),
        fern_json_contract::ContractRegistration::of::<RemoteAttemptReportV1>(
            "Worker Site return envelope for one remote Attempt",
        ),
    ]
}

impl RemoteAttemptReportV1 {
    pub fn validate_against(&self, instructions: &RemoteAttemptInstructionsV1) -> Result<(), String> {
        instructions.validate()?;
        if self.schema != REMOTE_ATTEMPT_REPORT_SCHEMA {
            return Err(format!("remote report schema must be {REMOTE_ATTEMPT_REPORT_SCHEMA:?}"));
        }
        if self.instructions_digest != instructions.canonical_digest()? || self.attempt_id != instructions.attempt_id {
            return Err("remote report does not answer these exact Attempt instructions".to_string());
        }
        if !valid_sha256(&self.worker_result_content_digest) {
            return Err("remote report requires a full worker-result sha256 digest".to_string());
        }
        if self.files.is_empty() {
            return Err("remote report requires at least one returned file".to_string());
        }
        let mut paths = BTreeSet::new();
        for file in &self.files {
            file.validate()?;
            if !paths.insert(file.relative_path.as_str()) {
                return Err(format!("remote report repeats package path {:?}", file.relative_path));
            }
        }
        report_contract_path(&paths)?;
        Ok(())
    }
}

/// Durably materialize one Registry-Home-issued package beside its destination.
///
/// Every payload is copied into a fresh sibling directory, reopened, and
/// matched against the instructions before the directory is atomically
/// renamed. Replaying the identical package is safe; reusing the destination
/// for different bytes is refused.
pub fn materialize_remote_attempt_instructions_package(
    destination: &Path,
    instructions: &RemoteAttemptInstructionsV1,
    sources: &[RemoteAttemptSourceFileV1],
) -> Result<(), String> {
    instructions.validate()?;
    materialize_remote_package(
        destination,
        REMOTE_ATTEMPT_INSTRUCTIONS_FILE,
        instructions,
        &instructions.files,
        sources,
        |root| {
            let existing = verify_remote_attempt_instructions_package(root)?;
            if existing != *instructions {
                return Err("the existing remote Attempt package contains different instructions".to_string());
            }
            Ok(())
        },
    )
}

/// Reopen a Registry-Home-issued package and verify its exact file set.
pub fn verify_remote_attempt_instructions_package(root: &Path) -> Result<RemoteAttemptInstructionsV1, String> {
    let instructions: RemoteAttemptInstructionsV1 = read_package_json(root, REMOTE_ATTEMPT_INSTRUCTIONS_FILE)?;
    instructions.validate()?;
    verify_remote_package_files(root, REMOTE_ATTEMPT_INSTRUCTIONS_FILE, &instructions.files)?;
    Ok(instructions)
}

/// Durably materialize one Worker-Site return package.
pub fn materialize_remote_attempt_report_package(
    destination: &Path,
    report: &RemoteAttemptReportV1,
    instructions: &RemoteAttemptInstructionsV1,
    sources: &[RemoteAttemptSourceFileV1],
) -> Result<(), String> {
    report.validate_against(instructions)?;
    materialize_remote_package(
        destination,
        REMOTE_ATTEMPT_REPORT_FILE,
        report,
        &report.files,
        sources,
        |root| {
            let existing = verify_remote_attempt_report_package(root, instructions)?;
            if existing != *report {
                return Err("the existing remote Attempt return contains a different report".to_string());
            }
            Ok(())
        },
    )
}

/// Reopen a Worker-Site return and prove it answers the exact issued package.
pub fn verify_remote_attempt_report_package(
    root: &Path,
    instructions: &RemoteAttemptInstructionsV1,
) -> Result<RemoteAttemptReportV1, String> {
    let report: RemoteAttemptReportV1 = read_package_json(root, REMOTE_ATTEMPT_REPORT_FILE)?;
    report.validate_against(instructions)?;
    verify_remote_package_files(root, REMOTE_ATTEMPT_REPORT_FILE, &report.files)?;
    Ok(report)
}

/// Read one declared package payload through the same confined file opener.
///
/// Callers normally verify the whole package first, then use this for small
/// JSON contracts. Large tensors should remain files and be reopened by their
/// typed reader instead of being copied into memory.
pub fn read_remote_attempt_package_file(root: &Path, descriptor: &RemoteAttemptFileV1) -> Result<Vec<u8>, String> {
    descriptor.validate()?;
    let bytes = read_confined_package_file(root, &descriptor.relative_path)?;
    descriptor.verify_bytes(&bytes)?;
    Ok(bytes)
}

fn materialize_remote_package<T, Verify>(
    destination: &Path,
    manifest_name: &str,
    manifest: &T,
    declared_files: &[RemoteAttemptFileV1],
    sources: &[RemoteAttemptSourceFileV1],
    verify: Verify,
) -> Result<(), String>
where
    T: JsonContract,
    Verify: Fn(&Path) -> Result<(), String>,
{
    let parent = destination
        .parent()
        .ok_or_else(|| format!("remote Attempt destination {} has no parent", destination.display()))?;
    let destination_name = destination
        .file_name()
        .and_then(|name| name.to_str())
        .filter(|name| !name.is_empty())
        .ok_or_else(|| {
            format!(
                "remote Attempt destination {} needs a portable final name",
                destination.display()
            )
        })?;
    require_regular_directory(parent)?;

    if destination.try_exists().map_err(|error| error.to_string())? {
        verify(destination).map_err(|error| {
            format!(
                "remote Attempt destination {} already exists with different or invalid content: {error}",
                destination.display()
            )
        })?;
        return Ok(());
    }

    let source_by_name = sources
        .iter()
        .map(|source| (source.relative_path.as_str(), source))
        .collect::<BTreeMap<_, _>>();
    if source_by_name.len() != sources.len() || source_by_name.len() != declared_files.len() {
        return Err("remote Attempt sources must name every declared payload exactly once".to_string());
    }
    for declared in declared_files {
        let source = source_by_name.get(declared.relative_path.as_str()).ok_or_else(|| {
            format!(
                "remote Attempt package is missing source for {:?}",
                declared.relative_path
            )
        })?;
        let observed = RemoteAttemptFileV1::from_file(&source.relative_path, &source.source_path)?;
        if observed != *declared {
            return Err(format!(
                "remote Attempt source {} no longer matches its declared size/digest",
                source.source_path.display()
            ));
        }
    }

    let nonce = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|error| format!("system clock cannot name a preparing package: {error}"))?
        .as_nanos();
    let preparing = parent.join(format!(".{destination_name}.preparing-{}-{nonce}", std::process::id()));
    fs::create_dir(&preparing).map_err(|error| {
        format!(
            "cannot create remote Attempt preparing directory {}: {error}",
            preparing.display()
        )
    })?;

    for declared in declared_files {
        let source = source_by_name
            .get(declared.relative_path.as_str())
            .expect("the complete source map was checked above");
        let target = preparing.join(&declared.relative_path);
        create_package_parent_directories(&preparing, &declared.relative_path)?;
        let mut input = BufReader::new(fs::File::open(&source.source_path).map_err(|error| {
            format!(
                "cannot reopen remote Attempt source {}: {error}",
                source.source_path.display()
            )
        })?);
        let mut output = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&target)
            .map_err(|error| format!("cannot create remote Attempt payload {}: {error}", target.display()))?;
        std::io::copy(&mut input, &mut output)
            .map_err(|error| format!("cannot copy remote Attempt payload {}: {error}", target.display()))?;
        output
            .sync_all()
            .map_err(|error| format!("cannot sync remote Attempt payload {}: {error}", target.display()))?;
    }

    let manifest_path = preparing.join(manifest_name);
    let manifest_bytes = manifest
        .checked_pretty_json()
        .map_err(|error| format!("cannot encode remote Attempt manifest: {error}"))?
        .into_bytes();
    let mut manifest_file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&manifest_path)
        .map_err(|error| format!("cannot create {}: {error}", manifest_path.display()))?;
    manifest_file
        .write_all(&manifest_bytes)
        .map_err(|error| format!("cannot write {}: {error}", manifest_path.display()))?;
    manifest_file
        .sync_all()
        .map_err(|error| format!("cannot sync {}: {error}", manifest_path.display()))?;
    sync_package_directories(&preparing)?;
    verify(&preparing)?;
    publish_package_no_replace(&preparing, destination).map_err(|error| {
        format!(
            "cannot atomically publish remote Attempt package {} as {}: {error}",
            preparing.display(),
            destination.display()
        )
    })?;
    sync_directory(parent)?;
    verify(destination)
}

#[cfg(target_os = "linux")]
fn publish_package_no_replace(source: &Path, destination: &Path) -> Result<(), String> {
    renameat_with(CWD, source, CWD, destination, RenameFlags::NOREPLACE).map_err(|error| error.to_string())
}

#[cfg(not(target_os = "linux"))]
fn publish_package_no_replace(source: &Path, destination: &Path) -> Result<(), String> {
    if destination.try_exists().map_err(|error| error.to_string())? {
        return Err("destination appeared while the package was being prepared".to_string());
    }
    fs::rename(source, destination).map_err(|error| error.to_string())
}

fn verify_remote_package_files(
    root: &Path,
    manifest_name: &str,
    declared_files: &[RemoteAttemptFileV1],
) -> Result<(), String> {
    require_regular_directory(root)?;
    let expected = declared_files
        .iter()
        .map(|file| file.relative_path.clone())
        .chain(std::iter::once(manifest_name.to_string()))
        .collect::<BTreeSet<_>>();
    let observed = list_regular_package_files(root)?;
    if observed != expected {
        return Err(format!(
            "remote Attempt package file set changed: expected {:?}, found {:?}",
            expected, observed
        ));
    }
    for declared in declared_files {
        let (observed_count, observed_digest) =
            digest_reader(open_confined_package_file(root, &declared.relative_path)?)?;
        if declared.byte_count != observed_count || declared.byte_digest != observed_digest {
            return Err(format!(
                "remote Attempt file {:?} changed: expected {} bytes / {}, found {} bytes / {}",
                declared.relative_path, declared.byte_count, declared.byte_digest, observed_count, observed_digest
            ));
        }
    }
    Ok(())
}

fn read_package_json<T: DeserializeOwned + JsonContract>(root: &Path, relative_path: &str) -> Result<T, String> {
    let bytes = read_confined_package_file(root, relative_path)?;
    fern_json_contract::checked_from_slice(&bytes).map_err(|error| {
        format!(
            "cannot decode remote Attempt manifest {} beneath {}: {error}",
            relative_path,
            root.display()
        )
    })
}

fn digest_reader(mut reader: impl Read) -> Result<(u64, String), String> {
    let mut digest = Sha256::new();
    let mut byte_count = 0_u64;
    let mut buffer = [0_u8; 64 * 1024];
    loop {
        let read = reader
            .read(&mut buffer)
            .map_err(|error| format!("cannot hash remote Attempt file: {error}"))?;
        if read == 0 {
            break;
        }
        byte_count = byte_count
            .checked_add(u64::try_from(read).map_err(|_| "remote Attempt file is too large".to_string())?)
            .ok_or_else(|| "remote Attempt file is too large".to_string())?;
        digest.update(&buffer[..read]);
    }
    Ok((byte_count, format!("sha256:{:x}", digest.finalize())))
}

fn create_package_parent_directories(root: &Path, relative_path: &str) -> Result<(), String> {
    let mut current = root.to_path_buf();
    let components = Path::new(relative_path).components().collect::<Vec<_>>();
    for component in components.iter().take(components.len().saturating_sub(1)) {
        let std::path::Component::Normal(component) = component else {
            return Err(format!("invalid remote Attempt payload path {relative_path:?}"));
        };
        current.push(component);
        match fs::create_dir(&current) {
            Ok(()) => {}
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => require_regular_directory(&current)?,
            Err(error) => {
                return Err(format!(
                    "cannot create package directory {}: {error}",
                    current.display()
                ));
            }
        }
    }
    Ok(())
}

fn list_regular_package_files(root: &Path) -> Result<BTreeSet<String>, String> {
    fn visit(root: &Path, current: &Path, files: &mut BTreeSet<String>) -> Result<(), String> {
        for entry in fs::read_dir(current)
            .map_err(|error| format!("cannot list remote Attempt package {}: {error}", current.display()))?
        {
            let entry = entry.map_err(|error| format!("cannot read remote Attempt directory entry: {error}"))?;
            let path = entry.path();
            let metadata = fs::symlink_metadata(&path)
                .map_err(|error| format!("cannot inspect remote Attempt package {}: {error}", path.display()))?;
            if metadata.file_type().is_symlink() {
                return Err(format!("remote Attempt package contains symlink {}", path.display()));
            }
            if metadata.file_type().is_dir() {
                visit(root, &path, files)?;
            } else if metadata.file_type().is_file() {
                let relative = path.strip_prefix(root).map_err(|error| error.to_string())?;
                let relative = relative
                    .to_str()
                    .ok_or_else(|| format!("remote Attempt package path {} is not UTF-8", path.display()))?
                    .replace(std::path::MAIN_SEPARATOR, "/");
                files.insert(relative);
            } else {
                return Err(format!(
                    "remote Attempt package contains unsupported file type {}",
                    path.display()
                ));
            }
        }
        Ok(())
    }

    let mut files = BTreeSet::new();
    visit(root, root, &mut files)?;
    Ok(files)
}

fn sync_package_directories(root: &Path) -> Result<(), String> {
    fn visit(current: &Path) -> Result<(), String> {
        for entry in fs::read_dir(current)
            .map_err(|error| format!("cannot list package directory {}: {error}", current.display()))?
        {
            let path = entry.map_err(|error| error.to_string())?.path();
            if fs::symlink_metadata(&path)
                .map_err(|error| error.to_string())?
                .file_type()
                .is_dir()
            {
                visit(&path)?;
            }
        }
        sync_directory(current)
    }
    visit(root)
}

fn require_regular_directory(directory: &Path) -> Result<(), String> {
    let metadata = fs::symlink_metadata(directory)
        .map_err(|error| format!("cannot inspect directory {}: {error}", directory.display()))?;
    if !metadata.file_type().is_dir() || metadata.file_type().is_symlink() {
        return Err(format!(
            "{} is not a regular non-symlink directory",
            directory.display()
        ));
    }
    Ok(())
}

fn sync_directory(directory: &Path) -> Result<(), String> {
    require_regular_directory(directory)?;
    fs::File::open(directory)
        .and_then(|file| file.sync_all())
        .map_err(|error| format!("cannot sync directory {}: {error}", directory.display()))
}

#[cfg(target_os = "linux")]
fn open_confined_package_file(root: &Path, relative_path: &str) -> Result<fs::File, String> {
    let root_handle = open(
        root,
        OFlags::RDONLY | OFlags::DIRECTORY | OFlags::NOFOLLOW | OFlags::CLOEXEC,
        Mode::empty(),
    )
    .map_err(|error| format!("cannot securely open remote Attempt root {}: {error}", root.display()))?;
    let file = openat2(
        &root_handle,
        relative_path,
        OFlags::RDONLY | OFlags::NOFOLLOW | OFlags::CLOEXEC,
        Mode::empty(),
        ResolveFlags::BENEATH | ResolveFlags::NO_SYMLINKS | ResolveFlags::NO_MAGICLINKS | ResolveFlags::NO_XDEV,
    )
    .map_err(|error| {
        format!(
            "cannot open remote Attempt file {relative_path:?} beneath {} without symlinks or mount crossings: {error}",
            root.display()
        )
    })?;
    let stat = fstat(&file).map_err(|error| format!("cannot inspect remote Attempt file: {error}"))?;
    if FileType::from_raw_mode(stat.st_mode) != FileType::RegularFile {
        return Err(format!("remote Attempt path {relative_path:?} is not a regular file"));
    }
    Ok(fs::File::from(file))
}

#[cfg(not(target_os = "linux"))]
fn open_confined_package_file(root: &Path, relative_path: &str) -> Result<fs::File, String> {
    let descriptor = RemoteAttemptFileV1 {
        relative_path: relative_path.to_string(),
        byte_count: 1,
        byte_digest: format!("sha256:{}", "0".repeat(64)),
    };
    descriptor.validate()?;
    require_regular_directory(root)?;
    let mut current = root.to_path_buf();
    let components = Path::new(relative_path).components().collect::<Vec<_>>();
    for (index, component) in components.iter().enumerate() {
        let std::path::Component::Normal(component) = component else {
            return Err(format!("invalid remote Attempt path {relative_path:?}"));
        };
        current.push(component);
        let metadata = fs::symlink_metadata(&current).map_err(|error| error.to_string())?;
        let valid = if index + 1 == components.len() {
            metadata.file_type().is_file() && !metadata.file_type().is_symlink()
        } else {
            metadata.file_type().is_dir() && !metadata.file_type().is_symlink()
        };
        if !valid {
            return Err(format!(
                "remote Attempt path {} contains a symlink or wrong file type",
                current.display()
            ));
        }
    }
    fs::File::open(&current).map_err(|error| format!("cannot read {}: {error}", current.display()))
}

fn read_confined_package_file(root: &Path, relative_path: &str) -> Result<Vec<u8>, String> {
    let mut bytes = Vec::new();
    open_confined_package_file(root, relative_path)?
        .read_to_end(&mut bytes)
        .map_err(|error| format!("cannot read remote Attempt file {relative_path:?}: {error}"))?;
    Ok(bytes)
}

fn valid_sha256(value: &str) -> bool {
    value
        .strip_prefix("sha256:")
        .is_some_and(|hex| hex.len() == 64 && hex.bytes().all(|byte| byte.is_ascii_hexdigit()))
}

fn valid_lower_sha256(value: &str) -> bool {
    value.len() == 64
        && value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

/// How an execution profile reaches the machine that will do the work.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ExecutionProfileExecutor {
    Local,
    Ssh,
}

impl ExecutionProfileExecutor {
    pub fn value(self) -> &'static str {
        match self {
            Self::Local => "local",
            Self::Ssh => "ssh",
        }
    }
}

/// The one worker program and concurrency contract for a profile revision.
///
/// Fern currently supports one capacity-one slot per local profile. Keeping
/// the value explicit prevents a second caller from treating "one GPU" as an
/// informal convention; a future multi-slot executor can widen this contract
/// deliberately rather than interpreting a missing field.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct WorkerExecutionProfile {
    pub module: String,
    pub slots: u16,
}

/// Crash-restart and cost limits owned by the execution environment.
///
/// These values used to be retyped on every `start-sweep` command. Recording
/// them in the immutable profile revision makes a restart use the same limits
/// as the initial launch and makes the receipt explain which budget applied.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct SupervisorExecutionProfile {
    pub max_restarts: u32,
    pub restart_delay_seconds: u64,
    pub max_target_seconds: u64,
    pub max_sweep_seconds: u64,
}

/// Minimum accelerator facts a CUDA profile promises to provide.
///
/// Marketing names are deliberately absent: `RTX 4070` is a string and can
/// mean either a laptop or desktop part. Compatibility is expressed by memory
/// and CUDA compute capability. A deployment that must use one physical card
/// may additionally pin its NVIDIA UUID; the observed model name remains
/// provenance, not scientific identity.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct AcceleratorRequirement {
    pub min_memory_gib: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub min_compute_capability: Option<[u32; 2]>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub required_device_uuid: Option<String>,
}

/// Python process facts observed by the no-write preflight probe.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ObservedPythonRuntime {
    /// Exact path Fern asked the operating system to execute.
    pub requested_executable: PathBuf,
    /// `sys.executable` reported inside that process.
    pub sys_executable: PathBuf,
    pub version: String,
    pub implementation: String,
    pub worker_module: String,
    pub worker_module_available: bool,
}

/// Host facts that explain where one Attempt actually ran.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ObservedPlatform {
    pub hostname: String,
    pub system: String,
    pub release: String,
    pub machine: String,
}

/// Numerical-library facts that may change results without changing the
/// scientific Coordinate.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ObservedTorchRuntime {
    pub version: String,
    pub cuda_runtime_version: Option<String>,
    pub cudnn_version: Option<u64>,
    pub deterministic_algorithms: bool,
    pub float32_matmul_precision: String,
    pub cuda_matmul_allow_tf32: bool,
    pub cudnn_allow_tf32: bool,
    pub cudnn_deterministic: bool,
    pub cudnn_benchmark: bool,
}

/// One GPU visible to the probed Python process.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ObservedCudaDevice {
    pub index: u32,
    pub name: String,
    pub capability: [u32; 2],
    pub total_memory_bytes: u64,
    pub multi_processor_count: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub uuid: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub driver_version: Option<String>,
}

/// Immutable, hashable facts observed before a worker receives authority.
///
/// This is provenance, not the scientific experiment identity. It lets Fern
/// say “the profile required at least 7 GiB and CUDA capability 8.9, while this
/// Attempt observed a laptop 4070” without making every marketing model name a
/// different research question.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ObservedExecutionFingerprint {
    pub schema: String,
    pub observed_at_unix_ms: u64,
    pub fern_version: String,
    pub platform: ObservedPlatform,
    pub python: ObservedPythonRuntime,
    pub torch: ObservedTorchRuntime,
    pub cuda_available: bool,
    pub cuda_current_device: Option<u32>,
    /// The logical-device mask seen by the probed Python process.
    /// It is recorded because CUDA index 0 need not mean physical GPU 0.
    pub cuda_visible_devices: Option<String>,
    /// CUDA's device-enumeration policy, which can make a numeric visibility
    /// token differ from `nvidia-smi`'s management index.
    pub cuda_device_order: Option<String>,
    /// The container-runtime GPU exposure rule, when present.
    pub nvidia_visible_devices: Option<String>,
    pub cuda_devices: Vec<ObservedCudaDevice>,
}

/// One declared-vs-observed mismatch that must be resolved before launch.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ExecutionProfileDrift {
    pub field: String,
    pub expected: String,
    pub observed: String,
}

impl ObservedExecutionFingerprint {
    /// Hash the complete observation receipt, including when it was sampled.
    ///
    /// This answers “are these the same recorded bytes/facts?” It must not be
    /// used as an idempotent operation identity because a later probe has a
    /// different timestamp even when the environment is unchanged.
    pub fn observation_digest(&self) -> Result<String, serde_json::Error> {
        Ok(format!("sha256:{:x}", Sha256::digest(serde_json::to_vec(self)?)))
    }

    /// Hash stable environment content while excluding the observation time.
    ///
    /// A lost-response retry may probe the same machine one minute later and
    /// must still replay the first start instead of creating an idempotency
    /// conflict. Actual runtime, host, GPU, or numerical-policy changes remain
    /// visible because every field except `observed_at_unix_ms` is included.
    pub fn environment_content_digest(&self) -> Result<String, serde_json::Error> {
        #[derive(Serialize)]
        struct EnvironmentContent<'a> {
            schema: &'a str,
            fern_version: &'a str,
            platform: &'a ObservedPlatform,
            python: &'a ObservedPythonRuntime,
            torch: &'a ObservedTorchRuntime,
            cuda_available: bool,
            cuda_current_device: Option<u32>,
            cuda_visible_devices: &'a Option<String>,
            cuda_device_order: &'a Option<String>,
            nvidia_visible_devices: &'a Option<String>,
            cuda_devices: &'a [ObservedCudaDevice],
        }

        let content = EnvironmentContent {
            schema: &self.schema,
            fern_version: &self.fern_version,
            platform: &self.platform,
            python: &self.python,
            torch: &self.torch,
            cuda_available: self.cuda_available,
            cuda_current_device: self.cuda_current_device,
            cuda_visible_devices: &self.cuda_visible_devices,
            cuda_device_order: &self.cuda_device_order,
            nvidia_visible_devices: &self.nvidia_visible_devices,
            cuda_devices: &self.cuda_devices,
        };
        Ok(format!("sha256:{:x}", Sha256::digest(serde_json::to_vec(&content)?)))
    }

    /// Validate the internal consistency required at the registry boundary.
    /// Deserializing a struct proves field shape; this method proves that the
    /// fields describe one possible observation rather than contradictory
    /// facts such as “CUDA unavailable” plus a selected CUDA device.
    pub fn validation_issues(&self) -> Vec<String> {
        let mut issues = Vec::new();
        if self.schema != OBSERVED_EXECUTION_FINGERPRINT_SCHEMA {
            issues.push(format!(
                "schema must be {OBSERVED_EXECUTION_FINGERPRINT_SCHEMA:?}, found {:?}",
                self.schema
            ));
        }
        if self.observed_at_unix_ms == 0 {
            issues.push("observed_at_unix_ms must be positive".to_string());
        }
        if self.fern_version.trim().is_empty() {
            issues.push("fern_version must be non-empty".to_string());
        }
        if self.python.worker_module.trim().is_empty() {
            issues.push("python.worker_module must be non-empty".to_string());
        }
        if self.cuda_available {
            let Some(selected_index) = self.cuda_current_device else {
                issues.push("cuda_current_device is required when CUDA is available".to_string());
                return issues;
            };
            if !self.cuda_devices.iter().any(|device| device.index == selected_index) {
                issues.push(format!(
                    "cuda_current_device {selected_index} is absent from cuda_devices"
                ));
            }
        } else {
            if self.cuda_current_device.is_some() {
                issues.push("cuda_current_device must be absent when CUDA is unavailable".to_string());
            }
            if !self.cuda_devices.is_empty() {
                issues.push("cuda_devices must be empty when CUDA is unavailable".to_string());
            }
        }
        let mut indices = BTreeSet::new();
        for device in &self.cuda_devices {
            if !indices.insert(device.index) {
                issues.push(format!("cuda device index {} is duplicated", device.index));
            }
            if device.name.trim().is_empty() {
                issues.push(format!("cuda device {} has an empty name", device.index));
            }
            if device.total_memory_bytes == 0 {
                issues.push(format!("cuda device {} reports zero memory", device.index));
            }
            if device.capability[0] == 0 {
                issues.push(format!(
                    "cuda device {} has an invalid compute capability",
                    device.index
                ));
            }
            if device
                .uuid
                .as_deref()
                .is_some_and(|uuid| !valid_nvidia_gpu_uuid_selector(uuid))
            {
                issues.push(format!(
                    "cuda device {} uuid must use the NVIDIA GPU- prefix without surrounding whitespace",
                    device.index
                ));
            }
        }
        issues
    }

    /// Compare the small set of profile promises that can be checked before
    /// GPU work. Additional observed facts remain provenance for later audit.
    pub fn drift_from(&self, profile: &ExecutionProfile) -> Vec<ExecutionProfileDrift> {
        let mut drift = Vec::new();
        if self.python.requested_executable != profile.python {
            drift.push(ExecutionProfileDrift {
                field: "python".to_string(),
                expected: profile.python.display().to_string(),
                observed: self.python.requested_executable.display().to_string(),
            });
        }
        if self.python.worker_module != profile.worker.module || !self.python.worker_module_available {
            drift.push(ExecutionProfileDrift {
                field: "worker.module".to_string(),
                expected: format!("{} (importable)", profile.worker.module),
                observed: format!(
                    "{} (importable={})",
                    self.python.worker_module, self.python.worker_module_available
                ),
            });
        }
        if profile.device == DeviceKind::Cuda {
            if !self.cuda_available {
                drift.push(ExecutionProfileDrift {
                    field: "device".to_string(),
                    expected: "cuda available".to_string(),
                    observed: "cuda unavailable".to_string(),
                });
                return drift;
            }
            let selected = self
                .cuda_current_device
                .and_then(|index| self.cuda_devices.iter().find(|device| device.index == index));
            let Some(selected) = selected else {
                drift.push(ExecutionProfileDrift {
                    field: "accelerator".to_string(),
                    expected: "one selected CUDA device".to_string(),
                    observed: "CUDA reported available but no selected device was described".to_string(),
                });
                return drift;
            };
            if let Some(requirement) = profile.accelerator.as_ref() {
                if let Some(required_capability) = requirement.min_compute_capability
                    && selected.capability < required_capability
                {
                    drift.push(ExecutionProfileDrift {
                        field: "accelerator.compute_capability".to_string(),
                        expected: format!("at least {}.{}", required_capability[0], required_capability[1]),
                        observed: format!("{}.{}", selected.capability[0], selected.capability[1]),
                    });
                }
                let required_bytes = requirement.min_memory_gib.saturating_mul(1_073_741_824);
                if selected.total_memory_bytes < required_bytes {
                    drift.push(ExecutionProfileDrift {
                        field: "accelerator.memory".to_string(),
                        expected: format!("at least {} GiB", requirement.min_memory_gib),
                        observed: format!("{} bytes", selected.total_memory_bytes),
                    });
                }
                if let Some(required_uuid) = requirement.required_device_uuid.as_deref()
                    && selected.uuid.as_deref() != Some(required_uuid)
                {
                    drift.push(ExecutionProfileDrift {
                        field: "accelerator.device_uuid".to_string(),
                        expected: required_uuid.to_string(),
                        observed: selected.uuid.clone().unwrap_or_else(|| "unavailable".to_string()),
                    });
                }
            }
        }
        drift
    }
}

/// One user-declared place where Fern may prepare or execute an experiment.
///
/// This record contains no credentials.  An SSH destination is only a host
/// address or alias; authentication remains the concern of the user's SSH
/// agent or of a later executor implementation.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ExecutionProfile {
    pub schema: String,
    pub profile_id: String,
    pub executor: ExecutionProfileExecutor,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ssh_destination: Option<String>,
    /// Local profile whose campaign registry and committed artifact roots are
    /// authoritative for this Worker Site. Required for SSH and absent for a
    /// local profile, where Home and Site are the same declaration.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub registry_home_profile_id: Option<String>,
    pub device: DeviceKind,
    /// Dedicated Fern binary that executes the typed Worker-Site adapter.
    ///
    /// This is explicit because a remote workspace may not share the local
    /// `target/` layout, and launching whichever `fern-worker` happens to be on
    /// `PATH` would detach execution from the resolved profile revision.
    pub worker_binary: PathBuf,
    /// Full SHA-256 of the immutable deployed Fern binary.
    ///
    /// Required for SSH Worker Sites and absent for a local development
    /// profile. A remote path without its expected bytes is not a deployment.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub worker_binary_sha256: Option<String>,
    /// Reviewed Fern source tree used to derive implementation identity.
    /// A deployed binary may live on a fast local disk while the source stays
    /// on persistent storage, so this path cannot be inferred from the binary.
    pub source_root: PathBuf,
    pub python: PathBuf,
    pub worker: WorkerExecutionProfile,
    pub supervisor: SupervisorExecutionProfile,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub accelerator: Option<AcceleratorRequirement>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub known_data_root: Option<PathBuf>,
    /// Root under which a later matrix registration derives its SQLite
    /// database. A profile never names a lane itself: lane names are
    /// scientific registrations, while this path is an operational choice.
    pub campaign_root: PathBuf,
    pub artifact_root: PathBuf,
    pub work_root: PathBuf,
    /// Storage starts warning at this free-space threshold. The campaign's
    /// absolute hard stop is enforced separately by the runner.
    pub warn_free_gib: u64,
    /// Storage starts warning at this free-space percentage. The campaign's
    /// current hard stop is enforced separately by the runner from absolute
    /// free space; this percentage remains an early warning.
    pub warn_free_percent: u8,
}

/// One declaration problem, named by field so a CLI can explain how to fix it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct ExecutionProfileIssue {
    pub field: &'static str,
    pub explanation: String,
}

/// An immutable description of the exact profile declaration a command read.
///
/// `profile_id` remains the friendly alias users type. `revision_hash` is the
/// identity a later launch receipt stores: editing `profiles/local-4070.json`
/// changes the hash instead of making two different machine declarations look
/// like the same profile.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ExecutionProfileRevision {
    pub schema: String,
    pub profile_id: String,
    pub revision_hash: String,
}

/// A validated, frozen-in-memory profile declaration plus its content identity.
///
/// The source path is deliberately absent. A profile copied to another host is
/// still the same operating declaration if its validated JSON is identical.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ResolvedExecutionProfile {
    pub declaration: ExecutionProfile,
    pub revision: ExecutionProfileRevision,
}

/// Why a profile cannot become a launchable, identity-bearing declaration.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ExecutionProfileResolutionError {
    InvalidDeclaration(Vec<ExecutionProfileIssue>),
    Canonicalization(String),
}

impl std::fmt::Display for ExecutionProfileResolutionError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::InvalidDeclaration(issues) => write!(
                formatter,
                "execution profile declaration is invalid: {}",
                issues
                    .iter()
                    .map(|issue| format!("{}: {}", issue.field, issue.explanation))
                    .collect::<Vec<_>>()
                    .join("; ")
            ),
            Self::Canonicalization(explanation) => {
                write!(formatter, "cannot canonicalize execution profile: {explanation}")
            }
        }
    }
}

impl std::error::Error for ExecutionProfileResolutionError {}

impl ExecutionProfile {
    /// Build a local template rooted at `workspace_root` without writing it.
    pub fn local_template(profile_id: String, workspace_root: &Path, home: &Path) -> Self {
        Self {
            schema: EXECUTION_PROFILE_SCHEMA.to_string(),
            profile_id,
            executor: ExecutionProfileExecutor::Local,
            ssh_destination: None,
            registry_home_profile_id: None,
            device: DeviceKind::Cuda,
            worker_binary: workspace_root.join("target/debug/fern-worker"),
            worker_binary_sha256: None,
            source_root: workspace_root.to_path_buf(),
            python: home.join(".venv/bin/python"),
            worker: WorkerExecutionProfile {
                module: "fr_tr".to_string(),
                slots: 1,
            },
            supervisor: SupervisorExecutionProfile {
                max_restarts: 3,
                restart_delay_seconds: 5,
                max_target_seconds: 3_600,
                max_sweep_seconds: 86_400,
            },
            accelerator: Some(AcceleratorRequirement {
                min_memory_gib: 1,
                min_compute_capability: None,
                required_device_uuid: None,
            }),
            known_data_root: Some(home.join(".cache/serie/known")),
            campaign_root: workspace_root.join("results/paper_campaign"),
            artifact_root: workspace_root.join("results/runs"),
            work_root: workspace_root.join("tmp/fern-run-worker"),
            warn_free_gib: 50,
            warn_free_percent: 10,
        }
    }

    /// Build an SSH Worker-Site template without contacting or modifying it.
    ///
    /// The paths belong to the remote machine. Authentication remains outside
    /// the profile; `doctor` must observe the remote host before this declaration
    /// can be used. The live runner additionally requires its named local
    /// Registry Home and transports only Attempt-scoped immutable packages.
    pub fn ssh_template(
        profile_id: String,
        ssh_destination: String,
        registry_home_profile_id: String,
        worker_binary_sha256: String,
        remote_workspace_root: &Path,
        remote_home: &Path,
    ) -> Self {
        let mut profile = Self::local_template(profile_id, remote_workspace_root, remote_home);
        profile.executor = ExecutionProfileExecutor::Ssh;
        profile.ssh_destination = Some(ssh_destination);
        profile.registry_home_profile_id = Some(registry_home_profile_id);
        profile.worker_binary = remote_workspace_root
            .join("bin")
            .join(format!("fern-worker.{worker_binary_sha256}"));
        profile.worker_binary_sha256 = Some(worker_binary_sha256);
        profile
    }

    /// The one predictable file location for a named profile.
    pub fn default_path(profile_id: &str) -> Result<PathBuf, ExecutionProfileIssue> {
        validate_profile_id(profile_id).map_err(|explanation| ExecutionProfileIssue {
            field: "profile_id",
            explanation,
        })?;
        Ok(PathBuf::from("profiles").join(format!("{profile_id}.json")))
    }

    /// Parse one profile file.  Declaration errors are returned separately so
    /// callers can distinguish invalid JSON from a syntactically valid but
    /// unsafe profile.
    pub fn read(path: &Path) -> Result<Self, String> {
        let bytes = std::fs::read(path).map_err(|error| format!("cannot read {}: {error}", path.display()))?;
        serde_json::from_slice(&bytes).map_err(|error| format!("cannot parse {}: {error}", path.display()))
    }

    /// Freeze this validated declaration into the profile revision a future
    /// attempt receipt can name. This is pure: it never writes the profile,
    /// database, or machine state.
    pub fn resolve(&self) -> Result<ResolvedExecutionProfile, ExecutionProfileResolutionError> {
        let issues = self.declaration_issues();
        if !issues.is_empty() {
            return Err(ExecutionProfileResolutionError::InvalidDeclaration(issues));
        }
        let canonical_json = serde_json::to_vec(self)
            .map_err(|error| ExecutionProfileResolutionError::Canonicalization(error.to_string()))?;
        let revision_hash = format!("sha256:{:x}", Sha256::digest(canonical_json));
        Ok(ResolvedExecutionProfile {
            declaration: self.clone(),
            revision: ExecutionProfileRevision {
                schema: EXECUTION_PROFILE_REVISION_SCHEMA.to_string(),
                profile_id: self.profile_id.clone(),
                revision_hash,
            },
        })
    }

    /// Check invariants that need no machine access and never change state.
    pub fn declaration_issues(&self) -> Vec<ExecutionProfileIssue> {
        let mut issues = Vec::new();
        if self.schema != EXECUTION_PROFILE_SCHEMA {
            issues.push(ExecutionProfileIssue {
                field: "schema",
                explanation: format!(
                    "expected {EXECUTION_PROFILE_SCHEMA:?}, found {:?}; v5 gives the dedicated worker binary one explicit identity",
                    self.schema
                ),
            });
        }
        if let Err(explanation) = validate_profile_id(&self.profile_id) {
            issues.push(ExecutionProfileIssue {
                field: "profile_id",
                explanation,
            });
        }
        match (self.executor, self.ssh_destination.as_deref()) {
            (ExecutionProfileExecutor::Local, Some(destination)) if !destination.trim().is_empty() => {
                issues.push(ExecutionProfileIssue {
                    field: "ssh_destination",
                    explanation: "a local profile must not name an SSH destination".to_string(),
                });
            }
            (ExecutionProfileExecutor::Ssh, None) => {
                issues.push(ExecutionProfileIssue {
                    field: "ssh_destination",
                    explanation: "an SSH profile needs a non-empty host alias or destination".to_string(),
                });
            }
            (ExecutionProfileExecutor::Ssh, Some(destination)) if destination.trim().is_empty() => {
                issues.push(ExecutionProfileIssue {
                    field: "ssh_destination",
                    explanation: "an SSH profile needs a non-empty host alias or destination".to_string(),
                });
            }
            _ => {}
        }
        match (self.executor, self.registry_home_profile_id.as_deref()) {
            (ExecutionProfileExecutor::Local, Some(_)) => issues.push(ExecutionProfileIssue {
                field: "registry_home_profile_id",
                explanation: "a local profile is already its own Registry Home and must not name another profile"
                    .to_string(),
            }),
            (ExecutionProfileExecutor::Ssh, None) => issues.push(ExecutionProfileIssue {
                field: "registry_home_profile_id",
                explanation: "an SSH Worker Site must name the local Registry Home profile".to_string(),
            }),
            (ExecutionProfileExecutor::Ssh, Some(profile_id)) => {
                if let Err(explanation) = validate_profile_id(profile_id) {
                    issues.push(ExecutionProfileIssue {
                        field: "registry_home_profile_id",
                        explanation,
                    });
                }
            }
            _ => {}
        }
        match (self.executor, self.worker_binary_sha256.as_deref()) {
            (ExecutionProfileExecutor::Local, Some(_)) => issues.push(ExecutionProfileIssue {
                field: "worker_binary_sha256",
                explanation: "a local development profile must not pin a constantly rebuilt binary digest".to_string(),
            }),
            (ExecutionProfileExecutor::Ssh, None) => issues.push(ExecutionProfileIssue {
                field: "worker_binary_sha256",
                explanation: "an SSH Worker Site must pin the full SHA-256 of its deployed fern-worker binary"
                    .to_string(),
            }),
            (ExecutionProfileExecutor::Ssh, Some(digest)) if !valid_lower_sha256(digest) => {
                issues.push(ExecutionProfileIssue {
                    field: "worker_binary_sha256",
                    explanation: format!("must be exactly 64 lowercase hexadecimal characters, found {digest:?}"),
                });
            }
            _ => {}
        }
        if let (ExecutionProfileExecutor::Ssh, Some(digest)) = (self.executor, self.worker_binary_sha256.as_deref())
            && valid_lower_sha256(digest)
        {
            let expected_name = format!("fern-worker.{digest}");
            if self.worker_binary.file_name().and_then(|name| name.to_str()) != Some(expected_name.as_str()) {
                issues.push(ExecutionProfileIssue {
                    field: "worker_binary",
                    explanation: format!(
                        "an SSH profile must execute immutable content path {expected_name:?}, found {}",
                        self.worker_binary.display()
                    ),
                });
            }
        }
        for (field, path) in [
            ("worker_binary", &self.worker_binary),
            ("source_root", &self.source_root),
            ("python", &self.python),
            ("campaign_root", &self.campaign_root),
            ("artifact_root", &self.artifact_root),
            ("work_root", &self.work_root),
        ] {
            if !path.is_absolute() {
                issues.push(ExecutionProfileIssue {
                    field,
                    explanation: format!(
                        "{} must be absolute so a command behaves the same from every working directory",
                        path.display()
                    ),
                });
            }
        }
        if let Some(known_data_root) = &self.known_data_root
            && !known_data_root.is_absolute()
        {
            issues.push(ExecutionProfileIssue {
                field: "known_data_root",
                explanation: format!(
                    "{} must be absolute so recorded-data preparation is unambiguous",
                    known_data_root.display()
                ),
            });
        }
        if !valid_python_module(&self.worker.module) {
            issues.push(ExecutionProfileIssue {
                field: "worker.module",
                explanation: format!(
                    "{:?} is not a dotted Python module name such as fr_tr or package.worker",
                    self.worker.module
                ),
            });
        }
        if self.worker.slots != 1 {
            issues.push(ExecutionProfileIssue {
                field: "worker.slots",
                explanation:
                    "the current local executor supports exactly one capacity-one slot; declare 1 instead of relying on accidental GPU sharing"
                        .to_string(),
            });
        }
        if self.supervisor.max_target_seconds == 0 {
            issues.push(ExecutionProfileIssue {
                field: "supervisor.max_target_seconds",
                explanation: "must be positive; an unbounded target can consume a rented machine indefinitely"
                    .to_string(),
            });
        }
        if self.supervisor.max_sweep_seconds < self.supervisor.max_target_seconds {
            issues.push(ExecutionProfileIssue {
                field: "supervisor.max_sweep_seconds",
                explanation: "must be at least max_target_seconds so the declared target budget can actually be used"
                    .to_string(),
            });
        }
        if self.supervisor.max_restarts > 0 && self.supervisor.restart_delay_seconds == 0 {
            issues.push(ExecutionProfileIssue {
                field: "supervisor.restart_delay_seconds",
                explanation: "must be positive when restarts are enabled; immediate crash loops waste compute"
                    .to_string(),
            });
        }
        match (self.device, self.accelerator.as_ref()) {
            (DeviceKind::Cpu, Some(_)) => issues.push(ExecutionProfileIssue {
                field: "accelerator",
                explanation: "a CPU profile cannot promise a CUDA accelerator".to_string(),
            }),
            (DeviceKind::Cuda, None) => issues.push(ExecutionProfileIssue {
                field: "accelerator",
                explanation: "a CUDA profile must declare at least its minimum GPU memory".to_string(),
            }),
            (DeviceKind::Cuda, Some(requirement)) => {
                if requirement.min_memory_gib == 0 {
                    issues.push(ExecutionProfileIssue {
                        field: "accelerator.min_memory_gib",
                        explanation: "must be positive for a CUDA profile".to_string(),
                    });
                }
                if requirement
                    .min_compute_capability
                    .is_some_and(|capability| capability[0] == 0)
                {
                    issues.push(ExecutionProfileIssue {
                        field: "accelerator.min_compute_capability",
                        explanation: "must be omitted or name a positive CUDA major capability".to_string(),
                    });
                }
                if requirement
                    .required_device_uuid
                    .as_deref()
                    .is_some_and(|uuid| !valid_nvidia_gpu_uuid_selector(uuid))
                {
                    issues.push(ExecutionProfileIssue {
                        field: "accelerator.required_device_uuid",
                        explanation: "must be omitted or use the exact NVIDIA GPU- UUID reported by nvidia-smi"
                            .to_string(),
                    });
                }
            }
            _ => {}
        }
        if self.warn_free_gib < CAMPAIGN_STORAGE_HARD_STOP_FREE_GIB {
            issues.push(ExecutionProfileIssue {
                field: "warn_free_gib",
                explanation: format!(
                    "must be at least {CAMPAIGN_STORAGE_HARD_STOP_FREE_GIB} GiB; a warning threshold may be stricter but cannot sit below the campaign hard stop"
                ),
            });
        }
        if !(1..=100).contains(&self.warn_free_percent) {
            issues.push(ExecutionProfileIssue {
                field: "warn_free_percent",
                explanation: format!(
                    "must be between 1 and 100; this is an early warning only, while the campaign hard stop is {CAMPAIGN_STORAGE_HARD_STOP_FREE_GIB} GiB absolute free space"
                ),
            });
        }
        issues
    }
}

fn valid_nvidia_gpu_uuid_selector(uuid: &str) -> bool {
    uuid == uuid.trim() && uuid.strip_prefix("GPU-").is_some_and(|suffix| !suffix.is_empty())
}

fn valid_python_module(module: &str) -> bool {
    !module.is_empty()
        && module.split('.').all(|segment| {
            let mut chars = segment.chars();
            chars
                .next()
                .is_some_and(|first| first == '_' || first.is_ascii_alphabetic())
                && chars.all(|character| character == '_' || character.is_ascii_alphanumeric())
        })
}

fn validate_profile_id(profile_id: &str) -> Result<(), String> {
    let valid = !profile_id.is_empty()
        && profile_id.len() <= 80
        && !profile_id.starts_with('-')
        && !profile_id.ends_with('-')
        && profile_id
            .bytes()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'-');
    if valid {
        Ok(())
    } else {
        Err(
            "must be 1–80 lowercase letters, digits, and interior hyphens; this keeps its file path predictable"
                .to_string(),
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    struct TestDirectory(PathBuf);

    impl TestDirectory {
        fn new(label: &str) -> Self {
            let nonce = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
            let path =
                std::env::temp_dir().join(format!("fern-execution-profile-{label}-{}-{nonce}", std::process::id()));
            fs::create_dir(&path).unwrap();
            Self(path)
        }

        fn path(&self) -> &Path {
            &self.0
        }

        fn write(&self, relative_path: &str, bytes: &[u8]) -> PathBuf {
            let path = self.0.join(relative_path);
            fs::create_dir_all(path.parent().unwrap()).unwrap();
            fs::write(&path, bytes).unwrap();
            path
        }
    }

    impl Drop for TestDirectory {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn remote_instructions_from_sources(sources: &[RemoteAttemptSourceFileV1]) -> RemoteAttemptInstructionsV1 {
        let files = sources
            .iter()
            .map(|source| RemoteAttemptFileV1::from_file(&source.relative_path, &source.source_path).unwrap())
            .collect::<Vec<_>>();
        let worker_request_content_digest = format!("sha256:{}", "22".repeat(32));
        RemoteAttemptInstructionsV1 {
            schema: REMOTE_ATTEMPT_INSTRUCTIONS_SCHEMA.to_string(),
            matrix_id: "matrix-1".to_string(),
            coordinate_id: "coordinate-4".to_string(),
            attempt_id: "attempt-7".to_string(),
            run_hash: "run-9".to_string(),
            execution_profile_revision: format!("sha256:{}", "11".repeat(32)),
            worker_request_content_digest,
            files,
        }
    }

    fn observed_4070(profile: &ExecutionProfile) -> ObservedExecutionFingerprint {
        ObservedExecutionFingerprint {
            schema: OBSERVED_EXECUTION_FINGERPRINT_SCHEMA.to_string(),
            observed_at_unix_ms: 1_700_000_000_000,
            fern_version: "0.1.0".to_string(),
            platform: ObservedPlatform {
                hostname: "local".to_string(),
                system: "Linux".to_string(),
                release: "test".to_string(),
                machine: "x86_64".to_string(),
            },
            python: ObservedPythonRuntime {
                requested_executable: profile.python.clone(),
                sys_executable: profile.python.clone(),
                version: "3.12.0".to_string(),
                implementation: "CPython".to_string(),
                worker_module: profile.worker.module.clone(),
                worker_module_available: true,
            },
            torch: ObservedTorchRuntime {
                version: "2.9.0".to_string(),
                cuda_runtime_version: Some("12.8".to_string()),
                cudnn_version: Some(9_100),
                deterministic_algorithms: false,
                float32_matmul_precision: "highest".to_string(),
                cuda_matmul_allow_tf32: false,
                cudnn_allow_tf32: true,
                cudnn_deterministic: false,
                cudnn_benchmark: false,
            },
            cuda_available: true,
            cuda_current_device: Some(0),
            cuda_visible_devices: None,
            cuda_device_order: None,
            nvidia_visible_devices: None,
            cuda_devices: vec![ObservedCudaDevice {
                index: 0,
                name: "NVIDIA GeForce RTX 4070 Laptop GPU".to_string(),
                capability: [8, 9],
                total_memory_bytes: 8 * 1_073_741_824,
                multi_processor_count: 36,
                uuid: Some("GPU-test".to_string()),
                driver_version: Some("591.74".to_string()),
            }],
        }
    }

    #[test]
    fn local_template_is_a_valid_declaration() {
        let profile = ExecutionProfile::local_template(
            "local-4070".to_string(),
            Path::new("/workspace/study"),
            Path::new("/home/user"),
        );
        assert!(profile.declaration_issues().is_empty());
        assert_eq!(
            profile.worker_binary,
            PathBuf::from("/workspace/study/target/debug/fern-worker")
        );
        assert_eq!(profile.python, PathBuf::from("/home/user/.venv/bin/python"));
        assert_eq!(profile.worker.module, "fr_tr");
        assert_eq!(profile.worker.slots, 1);
        assert_eq!(profile.supervisor.max_target_seconds, 3_600);
    }

    #[test]
    fn ssh_template_uses_remote_paths_without_storing_authentication() {
        let profile = ExecutionProfile::ssh_template(
            "runpod-a40".to_string(),
            "pod-user@ssh.example.invalid".to_string(),
            "local-4070".to_string(),
            "a".repeat(64),
            Path::new("/workspace/fern-study"),
            Path::new("/root"),
        );

        assert!(profile.declaration_issues().is_empty());
        assert_eq!(profile.executor, ExecutionProfileExecutor::Ssh);
        assert_eq!(profile.ssh_destination.as_deref(), Some("pod-user@ssh.example.invalid"));
        assert_eq!(profile.registry_home_profile_id.as_deref(), Some("local-4070"));
        assert_eq!(profile.worker_binary_sha256.as_deref(), Some("a".repeat(64).as_str()));
        assert_eq!(profile.python, PathBuf::from("/root/.venv/bin/python"));
        assert_eq!(
            profile.worker_binary,
            PathBuf::from(format!("/workspace/fern-study/bin/fern-worker.{}", "a".repeat(64)))
        );
        assert_eq!(
            profile.campaign_root,
            PathBuf::from("/workspace/fern-study/results/paper_campaign")
        );
        let encoded = serde_json::to_string(&profile).unwrap();
        assert!(!encoded.contains("private_key"));
        assert!(!encoded.contains("passphrase"));
    }

    #[test]
    fn ssh_profiles_require_v5_and_one_full_lowercase_binary_digest() {
        let mut profile = ExecutionProfile::ssh_template(
            "runpod-a40".to_string(),
            "pod-user@ssh.example.invalid".to_string(),
            "local-4070".to_string(),
            "a".repeat(64),
            Path::new("/workspace/fern-study"),
            Path::new("/root"),
        );
        assert!(profile.declaration_issues().is_empty());

        profile.schema = "fern.execution_profile.v3".to_string();
        assert!(profile.declaration_issues().iter().any(|issue| issue.field == "schema"));
        profile.schema = EXECUTION_PROFILE_SCHEMA.to_string();
        profile.worker_binary_sha256 = None;
        assert!(
            profile
                .declaration_issues()
                .iter()
                .any(|issue| issue.field == "worker_binary_sha256")
        );
        profile.worker_binary_sha256 = Some("A".repeat(64));
        assert!(
            profile
                .declaration_issues()
                .iter()
                .any(|issue| issue.field == "worker_binary_sha256")
        );
        profile.worker_binary_sha256 = Some("b".repeat(64));
        assert!(
            profile
                .declaration_issues()
                .iter()
                .any(|issue| issue.field == "worker_binary")
        );
    }

    #[test]
    fn legacy_v3_is_rejected_by_the_new_reader_without_rewriting_its_file() {
        let mut profile = ExecutionProfile::local_template(
            "local-4070".to_string(),
            Path::new("/workspace/study"),
            Path::new("/home/user"),
        );
        profile.schema = "fern.execution_profile.v3".to_string();
        let fields = profile
            .declaration_issues()
            .into_iter()
            .map(|issue| issue.field)
            .collect::<Vec<_>>();
        assert!(fields.contains(&"schema"));
        assert!(!fields.contains(&"worker_binary_sha256"));
    }

    #[test]
    fn profile_id_becomes_one_safe_file_name() {
        assert_eq!(
            ExecutionProfile::default_path("runpod-a40-2026-08-05").unwrap(),
            PathBuf::from("profiles/runpod-a40-2026-08-05.json")
        );
        assert!(ExecutionProfile::default_path("../outside").is_err());
    }

    #[test]
    fn declaration_enforces_the_absolute_hard_stop_but_allows_a_one_percent_early_warning() {
        let mut profile = ExecutionProfile::local_template(
            "local-4070".to_string(),
            Path::new("/workspace/study"),
            Path::new("/home/user"),
        );
        profile.warn_free_gib = 1;
        profile.warn_free_percent = 1;
        let fields = profile
            .declaration_issues()
            .into_iter()
            .map(|issue| issue.field)
            .collect::<Vec<_>>();
        assert_eq!(fields, vec!["warn_free_gib"]);

        profile.warn_free_gib = CAMPAIGN_STORAGE_HARD_STOP_FREE_GIB;
        profile.warn_free_percent = 0;
        let fields = profile
            .declaration_issues()
            .into_iter()
            .map(|issue| issue.field)
            .collect::<Vec<_>>();
        assert_eq!(fields, vec!["warn_free_percent"]);
    }

    #[test]
    fn declaration_rejects_unsafe_worker_supervisor_and_accelerator_contracts() {
        let mut profile = ExecutionProfile::local_template(
            "local-4070".to_string(),
            Path::new("/workspace/study"),
            Path::new("/home/user"),
        );
        profile.worker.module = "not-a-module".to_string();
        profile.worker.slots = 2;
        profile.supervisor.max_target_seconds = 0;
        profile.supervisor.max_sweep_seconds = 0;
        profile.supervisor.restart_delay_seconds = 0;
        profile.accelerator = Some(AcceleratorRequirement {
            min_memory_gib: 0,
            min_compute_capability: Some([0, 0]),
            required_device_uuid: Some("4070".to_string()),
        });

        let fields = profile
            .declaration_issues()
            .into_iter()
            .map(|issue| issue.field)
            .collect::<Vec<_>>();
        assert!(fields.contains(&"worker.module"));
        assert!(fields.contains(&"worker.slots"));
        assert!(fields.contains(&"supervisor.max_target_seconds"));
        assert!(fields.contains(&"supervisor.restart_delay_seconds"));
        assert!(fields.contains(&"accelerator.min_memory_gib"));
        assert!(fields.contains(&"accelerator.min_compute_capability"));
        assert!(fields.contains(&"accelerator.required_device_uuid"));
    }

    #[test]
    fn profile_rejects_an_unknown_field_instead_of_ignoring_a_typo() {
        let profile = ExecutionProfile::local_template(
            "local-4070".to_string(),
            Path::new("/workspace/study"),
            Path::new("/home/user"),
        );
        let mut profile_json = serde_json::to_value(profile).unwrap();
        profile_json["pyhton"] = serde_json::Value::String("/mistyped/path".to_string());
        assert!(serde_json::from_value::<ExecutionProfile>(profile_json).is_err());
    }

    #[test]
    fn resolved_profile_uses_content_not_its_file_location_as_its_revision() {
        let profile = ExecutionProfile::local_template(
            "local-4070".to_string(),
            Path::new("/workspace/study"),
            Path::new("/home/user"),
        );

        let first = profile.resolve().unwrap();
        let copied_elsewhere = profile.resolve().unwrap();

        assert_eq!(first.revision.schema, EXECUTION_PROFILE_REVISION_SCHEMA);
        assert_eq!(first.revision.profile_id, "local-4070");
        assert_eq!(first.revision.revision_hash, copied_elsewhere.revision.revision_hash);
        assert!(first.revision.revision_hash.starts_with("sha256:"));
    }

    #[test]
    fn runpod_5090_profile_revision_is_the_cross_language_v5_golden() {
        let path = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../profiles/runpod-5090.json");
        let profile = ExecutionProfile::read(&path).unwrap();
        let resolved = profile.resolve().unwrap();

        assert_eq!(
            resolved.revision.revision_hash,
            "sha256:3af4d5854b66e1f247280b57d4abe115a0af7d2327299f4dffe1ae9e71ab8b43"
        );
    }

    #[test]
    fn resolved_profile_revision_changes_when_the_operating_declaration_changes() {
        let profile = ExecutionProfile::local_template(
            "local-4070".to_string(),
            Path::new("/workspace/study"),
            Path::new("/home/user"),
        );
        let mut changed = profile.clone();
        changed.work_root = PathBuf::from("/workspace/another-work-root");

        assert_ne!(
            profile.resolve().unwrap().revision.revision_hash,
            changed.resolve().unwrap().revision.revision_hash
        );

        let mut accelerator_changed = profile.clone();
        accelerator_changed.accelerator.as_mut().unwrap().min_memory_gib = 7;
        accelerator_changed.accelerator.as_mut().unwrap().min_compute_capability = Some([8, 9]);
        assert_ne!(
            profile.resolve().unwrap().revision.revision_hash,
            accelerator_changed.resolve().unwrap().revision.revision_hash,
            "accelerator compatibility is part of the immutable execution-profile revision"
        );
    }

    #[test]
    fn invalid_profile_cannot_be_resolved_into_a_launchable_revision() {
        let mut profile = ExecutionProfile::local_template(
            "local-4070".to_string(),
            Path::new("/workspace/study"),
            Path::new("/home/user"),
        );
        profile.work_root = PathBuf::from("relative-work-root");

        assert!(matches!(
            profile.resolve(),
            Err(ExecutionProfileResolutionError::InvalidDeclaration(_))
        ));
    }

    #[test]
    fn observed_fingerprint_accepts_matching_profile_and_has_content_identity() {
        let mut profile = ExecutionProfile::local_template(
            "local-4070".to_string(),
            Path::new("/workspace/study"),
            Path::new("/home/user"),
        );
        profile.accelerator = Some(AcceleratorRequirement {
            min_memory_gib: 7,
            min_compute_capability: Some([8, 9]),
            required_device_uuid: Some("GPU-test".to_string()),
        });
        let observed = observed_4070(&profile);

        assert!(observed.drift_from(&profile).is_empty());
        assert!(observed.environment_content_digest().unwrap().starts_with("sha256:"));
        assert!(observed.observation_digest().unwrap().starts_with("sha256:"));
        assert!(observed.validation_issues().is_empty());

        let mut observed_one_minute_later = observed.clone();
        observed_one_minute_later.observed_at_unix_ms += 60_000;
        assert_eq!(
            observed.environment_content_digest().unwrap(),
            observed_one_minute_later.environment_content_digest().unwrap(),
            "the environment content identity must not change merely because the same probe ran later"
        );
        assert_ne!(
            observed.observation_digest().unwrap(),
            observed_one_minute_later.observation_digest().unwrap(),
            "the full observation receipt must still preserve when it was sampled"
        );
    }

    #[test]
    fn observed_fingerprint_names_gpu_memory_and_worker_module_drift() {
        let mut profile = ExecutionProfile::local_template(
            "local-a40".to_string(),
            Path::new("/workspace/study"),
            Path::new("/home/user"),
        );
        let observed = observed_4070(&profile);
        profile.worker.module = "different_worker".to_string();
        profile.accelerator = Some(AcceleratorRequirement {
            min_memory_gib: 40,
            min_compute_capability: Some([9, 0]),
            required_device_uuid: Some("GPU-other".to_string()),
        });
        let fields = observed
            .drift_from(&profile)
            .into_iter()
            .map(|drift| drift.field)
            .collect::<Vec<_>>();

        assert_eq!(
            fields,
            vec![
                "worker.module",
                "accelerator.compute_capability",
                "accelerator.memory",
                "accelerator.device_uuid"
            ]
        );
    }

    #[test]
    fn marketing_name_does_not_decide_compatibility() {
        let mut profile = ExecutionProfile::local_template(
            "local-cuda-8gb".to_string(),
            Path::new("/workspace/study"),
            Path::new("/home/user"),
        );
        profile.accelerator = Some(AcceleratorRequirement {
            min_memory_gib: 7,
            min_compute_capability: Some([8, 9]),
            required_device_uuid: None,
        });
        let laptop = observed_4070(&profile);
        let mut desktop = laptop.clone();
        desktop.cuda_devices[0].name = "NVIDIA GeForce RTX 4070".to_string();

        assert!(laptop.drift_from(&profile).is_empty());
        assert!(desktop.drift_from(&profile).is_empty());
        assert_ne!(
            laptop.environment_content_digest().unwrap(),
            desktop.environment_content_digest().unwrap(),
            "the receipt records the model name even though compatibility does not trust it"
        );
    }

    #[test]
    fn contradictory_cuda_observation_is_rejected_before_authority() {
        let profile = ExecutionProfile::local_template(
            "local-cuda".to_string(),
            Path::new("/workspace/study"),
            Path::new("/home/user"),
        );
        let mut observed = observed_4070(&profile);
        observed.cuda_available = false;

        let issues = observed.validation_issues();
        assert!(
            issues
                .iter()
                .any(|issue| issue.contains("cuda_current_device must be absent"))
        );
        assert!(issues.iter().any(|issue| issue.contains("cuda_devices must be empty")));
    }

    #[test]
    fn malformed_observed_gpu_uuid_is_rejected_before_authority() {
        let profile = ExecutionProfile::local_template(
            "local-cuda".to_string(),
            Path::new("/workspace/study"),
            Path::new("/home/user"),
        );
        let mut observed = observed_4070(&profile);
        observed.cuda_devices[0].uuid = Some("4070".to_string());

        assert!(
            observed
                .validation_issues()
                .iter()
                .any(|issue| issue.contains("uuid must use the NVIDIA GPU- prefix"))
        );
    }

    #[test]
    fn remote_attempt_report_is_bound_to_one_exact_home_issued_package() {
        let request = br#"{"schema":"fern.worker_request.v1","attempt_id":"attempt-7"}"#;
        let instructions = RemoteAttemptInstructionsV1 {
            schema: REMOTE_ATTEMPT_INSTRUCTIONS_SCHEMA.to_string(),
            matrix_id: "matrix-1".to_string(),
            coordinate_id: "coordinate-4".to_string(),
            attempt_id: "attempt-7".to_string(),
            run_hash: "run-9".to_string(),
            execution_profile_revision: format!("sha256:{}", "11".repeat(32)),
            worker_request_content_digest: format!("sha256:{:x}", Sha256::digest(request)),
            files: vec![RemoteAttemptFileV1::from_bytes("worker_request.v1.json", request).unwrap()],
        };
        let result = br#"{"schema":"fern.worker_result_report.v1","attempt_id":"attempt-7"}"#;
        let report = RemoteAttemptReportV1 {
            schema: REMOTE_ATTEMPT_REPORT_SCHEMA.to_string(),
            instructions_digest: instructions.canonical_digest().unwrap(),
            attempt_id: "attempt-7".to_string(),
            worker_result_content_digest: format!("sha256:{:x}", Sha256::digest(result)),
            files: vec![RemoteAttemptFileV1::from_bytes("worker_result_report.v1.json", result).unwrap()],
        };

        report.validate_against(&instructions).unwrap();
        let mut another_attempt = instructions.clone();
        another_attempt.attempt_id = "attempt-8".to_string();
        assert!(
            report
                .validate_against(&another_attempt)
                .unwrap_err()
                .contains("does not answer")
        );
    }

    #[test]
    fn remote_attempt_files_cannot_escape_the_attempt_package_or_change_in_transit() {
        assert!(RemoteAttemptFileV1::from_bytes("../worker_request.v1.json", b"request").is_err());
        assert!(RemoteAttemptFileV1::from_bytes("/tmp/worker_request.v1.json", b"request").is_err());
        assert!(RemoteAttemptFileV1::from_bytes("inputs\\case.safetensors", b"request").is_err());
        assert!(RemoteAttemptFileV1::from_bytes(REMOTE_ATTEMPT_INSTRUCTIONS_FILE, b"request").is_err());
        let file = RemoteAttemptFileV1::from_bytes("inputs/case.safetensors", b"case-v1").unwrap();
        file.verify_bytes(b"case-v1").unwrap();
        assert!(file.verify_bytes(b"case-v2").unwrap_err().contains("changed"));
    }

    #[test]
    fn remote_instruction_digest_canonicalizes_nested_file_order() {
        let first = RemoteAttemptFileV1::from_bytes("worker_request.v1.json", b"request").unwrap();
        let second = RemoteAttemptFileV1::from_bytes("inputs/case.safetensors", b"case").unwrap();
        let left = RemoteAttemptInstructionsV1 {
            schema: REMOTE_ATTEMPT_INSTRUCTIONS_SCHEMA.to_string(),
            matrix_id: "matrix-1".to_string(),
            coordinate_id: "coordinate-4".to_string(),
            attempt_id: "attempt-7".to_string(),
            run_hash: "run-9".to_string(),
            execution_profile_revision: format!("sha256:{}", "11".repeat(32)),
            worker_request_content_digest: format!("sha256:{}", "22".repeat(32)),
            files: vec![first.clone(), second.clone()],
        };
        let mut right = left.clone();
        right.files = vec![second, first];

        assert_eq!(left.canonical_digest().unwrap(), right.canonical_digest().unwrap());
    }

    #[test]
    fn remote_instruction_digest_matches_go_stage_transport() {
        let request = b"stage-request";
        let input = b"case-bytes";
        let instructions = RemoteAttemptInstructionsV1 {
            schema: REMOTE_ATTEMPT_INSTRUCTIONS_SCHEMA.to_string(),
            matrix_id: "matrix-1".to_string(),
            coordinate_id: "coordinate-4".to_string(),
            attempt_id: "attempt-7".to_string(),
            run_hash: "run-9".to_string(),
            execution_profile_revision: format!("sha256:{}", "1".repeat(64)),
            worker_request_content_digest: format!("sha256:{:x}", Sha256::digest(request)),
            files: vec![
                RemoteAttemptFileV1::from_bytes("inputs/case.safetensors", input).unwrap(),
                RemoteAttemptFileV1::from_bytes("request/stage_runner_request.v1.json", request).unwrap(),
            ],
        };

        assert_eq!(
            instructions.canonical_digest().unwrap(),
            "sha256:b2eec13800845c16459086f260e998a7d83d701108e5e4edee468f576b21871e"
        );
    }

    #[test]
    fn remote_instruction_package_is_durable_idempotent_and_tamper_evident() {
        let temp = TestDirectory::new("instruction-package");
        let sources = vec![
            RemoteAttemptSourceFileV1::new(
                "worker_request.v1.json",
                temp.write("sources/worker_request.v1.json", br#"{"attempt":"attempt-7"}"#),
            ),
            RemoteAttemptSourceFileV1::new(
                "inputs/case.safetensors",
                temp.write("sources/case.safetensors", b"case-bytes-v1"),
            ),
        ];
        let instructions = remote_instructions_from_sources(&sources);
        let package = temp.path().join("outbound-attempt-7");

        materialize_remote_attempt_instructions_package(&package, &instructions, &sources).unwrap();
        assert_eq!(
            verify_remote_attempt_instructions_package(&package).unwrap(),
            instructions
        );
        materialize_remote_attempt_instructions_package(&package, &instructions, &sources).unwrap();

        fs::write(package.join("inputs/case.safetensors"), b"changed-in-transit").unwrap();
        let error = verify_remote_attempt_instructions_package(&package).unwrap_err();
        assert!(error.contains("changed"), "{error}");
    }

    #[test]
    fn remote_package_rejects_extra_files_and_existing_different_manifest() {
        let temp = TestDirectory::new("package-conflict");
        let sources = vec![RemoteAttemptSourceFileV1::new(
            "worker_request.v1.json",
            temp.write("sources/worker_request.v1.json", b"request-v1"),
        )];
        let instructions = remote_instructions_from_sources(&sources);
        let package = temp.path().join("outbound-attempt-7");
        materialize_remote_attempt_instructions_package(&package, &instructions, &sources).unwrap();

        fs::write(package.join("not-declared.txt"), b"hidden payload").unwrap();
        let error = verify_remote_attempt_instructions_package(&package).unwrap_err();
        assert!(error.contains("file set changed"), "{error}");

        fs::remove_file(package.join("not-declared.txt")).unwrap();
        let mut different = instructions.clone();
        different.run_hash = "run-other".to_string();
        let error = materialize_remote_attempt_instructions_package(&package, &different, &sources).unwrap_err();
        assert!(error.contains("different instructions"), "{error}");
    }

    #[test]
    fn remote_return_package_answers_only_the_issued_attempt() {
        let temp = TestDirectory::new("return-package");
        let outbound_sources = vec![RemoteAttemptSourceFileV1::new(
            "worker_request.v1.json",
            temp.write("sources/worker_request.v1.json", b"request-v1"),
        )];
        let instructions = remote_instructions_from_sources(&outbound_sources);
        let result_path = temp.write("worker/worker_result_report.v1.json", b"result-v1");
        let return_sources = vec![RemoteAttemptSourceFileV1::new(
            "worker_result_report.v1.json",
            result_path,
        )];
        let report = RemoteAttemptReportV1 {
            schema: REMOTE_ATTEMPT_REPORT_SCHEMA.to_string(),
            instructions_digest: instructions.canonical_digest().unwrap(),
            attempt_id: instructions.attempt_id.clone(),
            files: return_sources
                .iter()
                .map(|source| RemoteAttemptFileV1::from_file(&source.relative_path, &source.source_path).unwrap())
                .collect::<Vec<_>>(),
            worker_result_content_digest: format!("sha256:{}", "33".repeat(32)),
        };
        let package = temp.path().join("return-attempt-7");

        materialize_remote_attempt_report_package(&package, &report, &instructions, &return_sources).unwrap();
        assert_eq!(
            verify_remote_attempt_report_package(&package, &instructions).unwrap(),
            report
        );
        let mut another_attempt = instructions.clone();
        another_attempt.attempt_id = "attempt-8".to_string();
        assert!(verify_remote_attempt_report_package(&package, &another_attempt).is_err());
    }

    #[test]
    fn remote_instructions_accept_exactly_one_request_contract() {
        let worker = ["worker_request.v1.json"].into_iter().collect::<BTreeSet<_>>();
        let stage = ["request/stage_runner_request.v1.json"]
            .into_iter()
            .collect::<BTreeSet<_>>();
        let both = ["worker_request.v1.json", "request/stage_runner_request.v1.json"]
            .into_iter()
            .collect::<BTreeSet<_>>();
        let neither = ["inputs/case.safetensors"].into_iter().collect::<BTreeSet<_>>();
        assert_eq!(request_contract_path(&worker).unwrap(), "worker_request.v1.json");
        assert_eq!(
            request_contract_path(&stage).unwrap(),
            "request/stage_runner_request.v1.json"
        );
        assert!(request_contract_path(&both).unwrap_err().contains("must not mix"));
        assert!(request_contract_path(&neither).unwrap_err().contains("must carry"));
    }

    #[test]
    fn stage_envelope_derives_attempt_identity_not_handler_names() {
        let request = serde_json::json!({
            "schema": "fern.stage_runner_request.v1",
            "stage_attempt_id": "attempt-stage-1",
            "execution_key": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "handler": "fern.train.current",
            "invocation": {"stage_id": "train"}
        });
        let files = vec![RemoteAttemptFileV1::from_bytes("request/stage_runner_request.v1.json", b"request").unwrap()];
        let instructions = derive_remote_attempt_instructions_from_home_request(
            &request,
            &format!("sha256:{}", "22".repeat(32)),
            files,
            Some(&format!("sha256:{}", "11".repeat(32))),
        )
        .unwrap();
        assert_eq!(instructions.attempt_id, "attempt-stage-1");
        assert_eq!(instructions.matrix_id, "attempt-stage-1");
        assert_eq!(instructions.coordinate_id, "attempt-stage-1");
        assert_ne!(instructions.coordinate_id, "fern.train.current");
        assert_ne!(instructions.matrix_id, "train");
        let report = serde_json::json!({
            "schema": "fern.stage_runner_report.v1",
            "stage_attempt_id": "attempt-stage-1"
        });
        let return_files =
            vec![RemoteAttemptFileV1::from_bytes("report/stage_runner_report.v1.json", b"report").unwrap()];
        let wrapped = derive_remote_attempt_report_from_runner_claim(
            &report,
            &format!("sha256:{}", "33".repeat(32)),
            &instructions,
            return_files,
        )
        .unwrap();
        assert_eq!(wrapped.attempt_id, "attempt-stage-1");
    }

    #[test]
    fn worker_envelope_copies_request_identity() {
        let request = serde_json::json!({
            "schema": "fern.worker_request.v1",
            "matrix_id": "matrix-1",
            "coordinate_id": "coordinate-4",
            "attempt_id": "attempt-7",
            "run_hash": "run-9",
            "execution_profile_revision": format!("sha256:{}", "11".repeat(32))
        });
        let files = vec![RemoteAttemptFileV1::from_bytes("worker_request.v1.json", b"request").unwrap()];
        let instructions = derive_remote_attempt_instructions_from_home_request(
            &request,
            &format!("sha256:{}", "22".repeat(32)),
            files,
            None,
        )
        .unwrap();
        assert_eq!(instructions.matrix_id, "matrix-1");
        assert_eq!(instructions.coordinate_id, "coordinate-4");
        assert_eq!(instructions.attempt_id, "attempt-7");
        assert_eq!(instructions.run_hash, "run-9");
    }
}
