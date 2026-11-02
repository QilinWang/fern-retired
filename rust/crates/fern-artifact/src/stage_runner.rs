//! Stage runner request and report contracts.
//!
//! A runner is an unprivileged child. It cannot open Registry Home, hold a
//! StagePermit, accept a Coordinate, release a consumer reference, delete
//! files, or construct a checked receipt. It may only write a reported result
//! under an Attempt workspace. The parent reopens every output and computes
//! digests itself.

use std::collections::BTreeMap;
use std::path::{Component, Path};

use fern_experiment_types::{Sha256Digest, StageOutputSpecV1, StageRunnerV1};
use fern_json_contract::JsonContract;
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};

use super::{
    ContractError, ReportedStageResultV1, ScientificContentDigest, StageExecutionKey, StageInvocationV1, domain_digest,
};

pub const STAGE_RUNNER_PROTOCOL: &str = "fern.stage_runner.v1";
pub const STAGE_RUNNER_REQUEST_SCHEMA: &str = "fern.stage_runner_request.v1";
pub const STAGE_RUNNER_REPORT_SCHEMA: &str = "fern.stage_runner_report.v1";
pub const STAGE_RUNNER_CAPABILITY_SCHEMA: &str = "fern.stage_runner_capability.v1";

pub const REQUEST_FILE: &str = "request/stage_runner_request.v1.json";
pub const REPORT_FILE: &str = "report/stage_runner_report.v1.json";
pub const INPUTS_DIR: &str = "inputs";
pub const OUTPUTS_DIR: &str = "outputs";
pub const LOGS_DIR: &str = "logs";

/// One input file the parent copied into the Attempt workspace.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StageRunnerInputFileV1 {
    /// Workspace-relative path, always under `inputs/`.
    pub relative_path: String,
}

/// Path-free runner request. Absolute host paths are refused.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StageRunnerRequestV1 {
    pub schema: String,
    pub protocol_version: String,
    pub stage_attempt_id: String,
    pub handler: String,
    pub invocation: StageInvocationV1,
    pub invocation_digest: ScientificContentDigest,
    pub execution_key: StageExecutionKey,
    pub implementation_digest: Sha256Digest,
    pub input_files: BTreeMap<String, StageRunnerInputFileV1>,
    pub declared_outputs: BTreeMap<String, StageOutputSpecV1>,
}

impl JsonContract for StageRunnerRequestV1 {
    const SCHEMA_ID: &'static str = STAGE_RUNNER_REQUEST_SCHEMA;
}

impl StageRunnerRequestV1 {
    pub fn validate(&self) -> Result<(), ContractError> {
        if self.schema != STAGE_RUNNER_REQUEST_SCHEMA {
            return Err(ContractError::new(
                "stage_runner_request.schema",
                format!("expected {STAGE_RUNNER_REQUEST_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        if self.protocol_version != STAGE_RUNNER_PROTOCOL {
            return Err(ContractError::new(
                "stage_runner_request.protocol_version",
                format!("expected {STAGE_RUNNER_PROTOCOL:?}, found {:?}", self.protocol_version),
            ));
        }
        if self.stage_attempt_id.trim().is_empty() {
            return Err(ContractError::new(
                "stage_runner_request.stage_attempt_id",
                "must be a non-empty Attempt id",
            ));
        }
        if self.handler.trim().is_empty() {
            return Err(ContractError::new(
                "stage_runner_request.handler",
                "must name the scientific handler this runner should execute",
            ));
        }
        self.invocation.validate_standalone()?;
        if self.invocation_digest != self.invocation.digest()? {
            return Err(ContractError::new(
                "stage_runner_request.invocation_digest",
                "does not recompute from the enclosed invocation",
            ));
        }
        if self.execution_key != self.invocation.execution_key {
            return Err(ContractError::new(
                "stage_runner_request.execution_key",
                "must copy the invocation execution key",
            ));
        }
        if self.implementation_digest != self.invocation.implementation.implementation_digest {
            return Err(ContractError::new(
                "stage_runner_request.implementation_digest",
                "must copy the invocation implementation digest",
            ));
        }
        if self.declared_outputs != self.invocation.declared_outputs {
            return Err(ContractError::new(
                "stage_runner_request.declared_outputs",
                "must copy the invocation declared outputs",
            ));
        }
        for (name, file) in &self.input_files {
            require_workspace_relative("inputs", &file.relative_path).map_err(|explanation| {
                ContractError::new(
                    format!("stage_runner_request.input_files.{name}.relative_path"),
                    explanation,
                )
            })?;
        }
        Ok(())
    }

    pub fn digest(&self) -> Result<ScientificContentDigest, ContractError> {
        self.validate()?;
        let bytes = fern_json_contract::canonical_json_bytes(self)
            .map_err(|error| ContractError::new("stage_runner_request", format!("cannot canonicalize: {error}")))?;
        Ok(domain_digest(b"fern.stage_runner_request.v1\0", &bytes))
    }
}

impl StageInvocationV1 {
    fn validate_standalone(&self) -> Result<(), ContractError> {
        self.digest().map(|_| ())
    }
}

/// Tagged runner outcome. Success and failure cannot coexist.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum StageRunnerOutcomeV1 {
    Success(StageRunnerSuccessV1),
    Failure(StageRunnerFailureV1),
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StageRunnerSuccessV1 {
    pub result: ReportedStageResultV1,
    /// Workspace-relative output files, each under `outputs/`.
    pub output_paths: BTreeMap<String, String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StageRunnerFailureV1 {
    pub code: String,
    pub message: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StageRunnerReportV1 {
    pub schema: String,
    pub stage_attempt_id: String,
    pub request_digest: ScientificContentDigest,
    pub outcome: StageRunnerOutcomeV1,
}

impl JsonContract for StageRunnerReportV1 {
    const SCHEMA_ID: &'static str = STAGE_RUNNER_REPORT_SCHEMA;
}

impl StageRunnerReportV1 {
    pub fn digest(&self) -> Result<ScientificContentDigest, ContractError> {
        self.validate()?;
        let bytes = fern_json_contract::canonical_json_bytes(self)
            .map_err(|error| ContractError::new("stage_runner_report", format!("cannot canonicalize: {error}")))?;
        Ok(domain_digest(b"fern.stage_runner_report.v1\0", &bytes))
    }

    pub fn validate(&self) -> Result<(), ContractError> {
        if self.schema != STAGE_RUNNER_REPORT_SCHEMA {
            return Err(ContractError::new(
                "stage_runner_report.schema",
                format!("expected {STAGE_RUNNER_REPORT_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        match &self.outcome {
            StageRunnerOutcomeV1::Success(success) => {
                success.result.validate_standalone()?;
                if success.output_paths.len() != success.result.outputs.len() {
                    return Err(ContractError::new(
                        "stage_runner_report.outcome.output_paths",
                        "must name exactly the reported result outputs",
                    ));
                }
                for port in success.result.outputs.keys() {
                    let Some(relative_path) = success.output_paths.get(port) else {
                        return Err(ContractError::new(
                            "stage_runner_report.outcome.output_paths",
                            format!("missing path for output port {port:?}"),
                        ));
                    };
                    require_workspace_relative("outputs", relative_path).map_err(|explanation| {
                        ContractError::new(format!("stage_runner_report.outcome.output_paths.{port}"), explanation)
                    })?;
                }
            }
            StageRunnerOutcomeV1::Failure(failure) => {
                if failure.code.trim().is_empty() || failure.message.trim().is_empty() {
                    return Err(ContractError::new(
                        "stage_runner_report.outcome",
                        "failure code and message must be non-empty",
                    ));
                }
            }
        }
        Ok(())
    }
}

impl ReportedStageResultV1 {
    fn validate_standalone(&self) -> Result<(), ContractError> {
        self.digest().map(|_| ())
    }
}

/// What a runner binary or Python module actually supports after a live probe.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StageRunnerCapabilityV1 {
    pub schema: String,
    pub protocol_version: String,
    pub implementation_digest: Sha256Digest,
    pub handlers: Vec<String>,
    pub entrypoint: String,
}

impl JsonContract for StageRunnerCapabilityV1 {
    const SCHEMA_ID: &'static str = STAGE_RUNNER_CAPABILITY_SCHEMA;
}

/// Scientific identity of one Stage implementation.
///
/// The artifact digest is the content identity of the published binary or of a
/// frozen Python bundle. Host paths, Attempt directories, and transfer
/// locations are never mixed in.
pub fn stage_implementation_digest(
    runner: StageRunnerV1,
    entrypoint: &str,
    handlers: &[&str],
    artifact_digest: &Sha256Digest,
) -> Sha256Digest {
    let mut material = Vec::new();
    material.extend_from_slice(b"fern.stage.implementation.v1\0");
    material.extend_from_slice(STAGE_RUNNER_PROTOCOL.as_bytes());
    material.push(0);
    material.extend_from_slice(STAGE_RUNNER_REQUEST_SCHEMA.as_bytes());
    material.push(0);
    material.extend_from_slice(STAGE_RUNNER_REPORT_SCHEMA.as_bytes());
    material.push(0);
    material.extend_from_slice(STAGE_RUNNER_CAPABILITY_SCHEMA.as_bytes());
    material.push(0);
    let runner_kind = match runner {
        StageRunnerV1::RustBinary => "rust_binary",
        StageRunnerV1::PythonModule => "python_module",
    };
    material.extend_from_slice(runner_kind.as_bytes());
    material.push(0);
    material.extend_from_slice(entrypoint.as_bytes());
    let mut named = handlers.to_vec();
    named.sort_unstable();
    for handler in named {
        material.push(0);
        material.extend_from_slice(handler.as_bytes());
    }
    material.push(0);
    material.extend_from_slice(artifact_digest.as_str().as_bytes());
    Sha256Digest::from_bytes(&material)
}

/// Content identity of a frozen Python source-plus-lock bundle.
///
/// Each path is a portable relative path. Callers hash file bytes themselves
/// and pass only those digests; this function never sees a host path.
pub fn stage_python_bundle_digest(files: &[(String, Sha256Digest)]) -> Result<Sha256Digest, ContractError> {
    let mut ordered = files.to_vec();
    ordered.sort_by(|left, right| left.0.cmp(&right.0));
    let mut material = Vec::from(b"fern.stage.python_bundle.v1\0");
    let mut previous: Option<&str> = None;
    for (relative_path, digest) in &ordered {
        if previous == Some(relative_path.as_str()) {
            return Err(ContractError::new(
                "python_bundle.relative_path",
                format!("path {relative_path:?} is repeated"),
            ));
        }
        require_portable_bundle_path(relative_path).map_err(|explanation| {
            ContractError::new(format!("python_bundle.relative_path.{relative_path}"), explanation)
        })?;
        material.extend_from_slice(relative_path.as_bytes());
        material.push(0);
        material.extend_from_slice(digest.as_str().as_bytes());
        material.push(0);
        previous = Some(relative_path);
    }
    if ordered.is_empty() {
        return Err(ContractError::new(
            "python_bundle.files",
            "a frozen Python bundle must name at least one file",
        ));
    }
    Ok(Sha256Digest::from_bytes(&material))
}

fn require_portable_bundle_path(relative_path: &str) -> Result<(), String> {
    let path = Path::new(relative_path);
    if path.is_absolute() || relative_path.is_empty() {
        return Err("must be a portable relative path".to_string());
    }
    for component in path.components() {
        match component {
            Component::Normal(_) => {}
            Component::CurDir => return Err("must not contain '.' path components".to_string()),
            Component::ParentDir => return Err("must not contain '..' path components".to_string()),
            Component::RootDir | Component::Prefix(_) => {
                return Err("must be a portable relative path".to_string());
            }
        }
    }
    Ok(())
}

impl StageRunnerCapabilityV1 {
    pub fn validate(&self) -> Result<(), ContractError> {
        if self.schema != STAGE_RUNNER_CAPABILITY_SCHEMA {
            return Err(ContractError::new(
                "stage_runner_capability.schema",
                format!("expected {STAGE_RUNNER_CAPABILITY_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        if self.protocol_version != STAGE_RUNNER_PROTOCOL {
            return Err(ContractError::new(
                "stage_runner_capability.protocol_version",
                format!("expected {STAGE_RUNNER_PROTOCOL:?}, found {:?}", self.protocol_version),
            ));
        }
        if self.handlers.is_empty() {
            return Err(ContractError::new(
                "stage_runner_capability.handlers",
                "a runner must name at least one handler",
            ));
        }
        Ok(())
    }

    pub fn supports(&self, handler: &str) -> bool {
        self.handlers.iter().any(|name| name == handler)
    }
}

/// Refuse host paths, parent escape, and files outside the named workspace directory.
pub fn require_workspace_relative(directory: &str, relative_path: &str) -> Result<(), String> {
    let path = Path::new(relative_path);
    if path.is_absolute() {
        return Err("must be a portable relative path".to_string());
    }
    let mut components = path.components();
    let Some(Component::Normal(first)) = components.next() else {
        return Err(format!("must start with {directory}/"));
    };
    if first != directory {
        return Err(format!("must start with {directory}/"));
    }
    if components.next().is_none() {
        return Err(format!("must name a file under {directory}/"));
    }
    for component in path.components() {
        match component {
            Component::Normal(_) => {}
            Component::CurDir => return Err("must not contain '.' path components".to_string()),
            Component::ParentDir => return Err("must not contain '..' path components".to_string()),
            Component::RootDir | Component::Prefix(_) => {
                return Err("must be a portable relative path".to_string());
            }
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn output_paths_must_stay_under_outputs() {
        assert!(require_workspace_relative("outputs", "outputs/metrics.json").is_ok());
        assert!(require_workspace_relative("outputs", "../outputs/metrics.json").is_err());
        assert!(require_workspace_relative("outputs", "/tmp/metrics.json").is_err());
        assert!(require_workspace_relative("outputs", "outputs/../secret.json").is_err());
        assert!(require_workspace_relative("outputs", "inputs/metrics.json").is_err());
        assert!(require_workspace_relative("outputs", "outputs").is_err());
    }

    #[test]
    fn implementation_digest_binds_artifact_bytes_not_only_names() {
        let left = Sha256Digest::from_bytes(b"binary-a");
        let right = Sha256Digest::from_bytes(b"binary-b");
        let names = ["fern.stage.return.check"];
        let first = stage_implementation_digest(StageRunnerV1::RustBinary, "fern-stage-runner", &names, &left);
        let second = stage_implementation_digest(StageRunnerV1::RustBinary, "fern-stage-runner", &names, &right);
        assert_ne!(first, second);
        let same = stage_implementation_digest(StageRunnerV1::RustBinary, "fern-stage-runner", &names, &left);
        assert_eq!(first, same);
    }

    #[test]
    fn python_bundle_digest_refuses_host_paths_and_repeats() {
        let digest = Sha256Digest::from_bytes(b"file");
        assert!(stage_python_bundle_digest(&[("../secret.py".to_string(), digest.clone())]).is_err());
        assert!(stage_python_bundle_digest(&[("/tmp/fr_tr.py".to_string(), digest.clone())]).is_err());
        assert!(
            stage_python_bundle_digest(&[
                ("fr_tr.py".to_string(), digest.clone()),
                ("fr_tr.py".to_string(), digest),
            ])
            .is_err()
        );
        let a = Sha256Digest::from_bytes(b"a");
        let b = Sha256Digest::from_bytes(b"b");
        let left =
            stage_python_bundle_digest(&[("fr_tr.py".to_string(), a.clone()), ("uv.lock".to_string(), b.clone())])
                .unwrap();
        let right = stage_python_bundle_digest(&[("uv.lock".to_string(), b), ("fr_tr.py".to_string(), a)]).unwrap();
        assert_eq!(left, right);
    }
}
