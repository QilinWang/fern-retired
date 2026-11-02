//! Stage invocation, result, and checked receipt.
//!
//! A runner may report a successful result. It cannot construct or deserialize
//! a checked invocation or a checked receipt. Those exist only after Fern
//! reopens artifacts against a stage plan.

use std::collections::{BTreeMap, BTreeSet};

use fern_experiment_types::{
    ContentAddressedSubjectV1, MaterializationRequirementV1, Sha256Digest, StageImplementationRefV1,
    StageInputSourceV1, StageOutputSpecV1, StagePlanError, StagePlanV1, StageSpecV1,
};
use fern_json_contract::JsonContract;
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};

use super::{
    ArtifactReference, CheckedArtifactV1, ContractError, ReportedArtifactV1, ScientificContentDigest,
    StageExecutionKey, StageRunnerOutcomeV1, StageRunnerReportV1, StageRunnerRequestV1, domain_digest,
};

pub const STAGE_INVOCATION_SCHEMA: &str = "fern.stage_invocation.v1";
pub const REPORTED_STAGE_RESULT_SCHEMA: &str = "fern.reported_stage_result.v1";
pub const STAGE_RECEIPT_SCHEMA: &str = "fern.stage_receipt.v1";

const EXECUTION_KEY_DOMAIN: &[u8] = b"fern.stage_execution.v1\0";
const INVOCATION_DIGEST_DOMAIN: &[u8] = b"fern.stage_invocation.v1\0";
const RESULT_DIGEST_DOMAIN: &[u8] = b"fern.reported_stage_result.v1\0";
const RECEIPT_DIGEST_DOMAIN: &[u8] = b"fern.stage_receipt.v1\0";

/// Path-free invocation of one stage in one plan.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StageInvocationV1 {
    pub schema: String,
    pub plan_digest: Sha256Digest,
    pub stage_id: String,
    pub stage_spec_digest: Sha256Digest,
    pub execution_key: StageExecutionKey,
    pub implementation: StageImplementationRefV1,
    pub parameters: Value,
    pub resolved_inputs: BTreeMap<String, ReportedArtifactV1>,
    pub declared_outputs: BTreeMap<String, StageOutputSpecV1>,
}

impl JsonContract for StageInvocationV1 {
    const SCHEMA_ID: &'static str = STAGE_INVOCATION_SCHEMA;
}

/// Invocation that was constructed from a plan and independently checked inputs.
///
/// ```compile_fail
/// fn decode(bytes: &[u8]) -> fern_artifact::CheckedStageInvocationV1 {
///     serde_json::from_slice(bytes).unwrap()
/// }
/// ```
#[derive(Debug, Clone, PartialEq)]
pub struct CheckedStageInvocationV1 {
    reported: StageInvocationV1,
}

impl CheckedStageInvocationV1 {
    /// Bind independently checked artifacts onto one stage of a plan.
    pub fn from_checked_inputs(
        plan: &StagePlanV1,
        stage_id: &str,
        resolved_inputs: BTreeMap<String, CheckedArtifactV1>,
    ) -> Result<Self, ContractError> {
        let stage = plan.stage(stage_id).map_err(plan_error)?;
        let reported_inputs = reported_inputs_matching_ports(stage, &resolved_inputs)?;
        let invocation = StageInvocationV1 {
            schema: STAGE_INVOCATION_SCHEMA.to_string(),
            plan_digest: plan.digest().map_err(plan_error)?,
            stage_id: stage.id.clone(),
            stage_spec_digest: stage.digest().map_err(plan_error)?,
            execution_key: stage_execution_key(&plan.subject, stage, &reported_inputs)?,
            implementation: stage.implementation.clone(),
            parameters: stage.parameters.clone(),
            resolved_inputs: reported_inputs,
            declared_outputs: stage.outputs.clone(),
        };
        invocation.validate_against_plan(plan)?;
        Ok(Self { reported: invocation })
    }

    pub fn reported(&self) -> &StageInvocationV1 {
        &self.reported
    }

    pub fn into_reported(self) -> StageInvocationV1 {
        self.reported
    }
}

impl StageInvocationV1 {
    pub fn digest(&self) -> Result<ScientificContentDigest, ContractError> {
        self.validate()?;
        hash_contract(INVOCATION_DIGEST_DOMAIN, self)
    }

    fn validate(&self) -> Result<(), ContractError> {
        if self.schema != STAGE_INVOCATION_SCHEMA {
            return Err(ContractError::new(
                "stage_invocation.schema",
                format!("expected {STAGE_INVOCATION_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        for artifact in self.resolved_inputs.values() {
            artifact.validate()?;
        }
        Ok(())
    }

    fn validate_against_plan(&self, plan: &StagePlanV1) -> Result<(), ContractError> {
        self.validate()?;
        let plan_digest = plan.digest().map_err(plan_error)?;
        if self.plan_digest != plan_digest {
            return Err(ContractError::new(
                "stage_invocation.plan_digest",
                "does not recompute from the supplied stage plan",
            ));
        }
        let stage = plan.stage(&self.stage_id).map_err(plan_error)?;
        let spec_digest = stage.digest().map_err(plan_error)?;
        if self.stage_spec_digest != spec_digest {
            return Err(ContractError::new(
                "stage_invocation.stage_spec_digest",
                "does not recompute from the named StageSpec",
            ));
        }
        if self.implementation != stage.implementation || self.parameters != stage.parameters {
            return Err(ContractError::new(
                "stage_invocation.implementation",
                "must be copied from the named StageSpec",
            ));
        }
        require_exact_ports(
            "stage_invocation.resolved_inputs",
            self.resolved_inputs.keys(),
            stage.inputs.keys(),
        )?;
        require_exact_ports(
            "stage_invocation.declared_outputs",
            self.declared_outputs.keys(),
            stage.outputs.keys(),
        )?;
        if self.declared_outputs != stage.outputs {
            return Err(ContractError::new(
                "stage_invocation.declared_outputs",
                "must be copied from the named StageSpec",
            ));
        }
        for (port, artifact) in &self.resolved_inputs {
            let declared = &stage.inputs[port];
            require_role_agreement(
                "stage_invocation.resolved_inputs",
                port,
                declared.role.as_str(),
                artifact,
            )?;
        }
        let expected_key = stage_execution_key(&plan.subject, stage, &self.resolved_inputs)?;
        if self.execution_key != expected_key {
            return Err(ContractError::new(
                "stage_invocation.execution_key",
                "does not recompute from the cell subject, StageSpec, and resolved inputs",
            ));
        }
        Ok(())
    }
}

/// Successful stage result reported by a runner.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ReportedStageResultV1 {
    pub schema: String,
    pub invocation_digest: ScientificContentDigest,
    pub execution_key: StageExecutionKey,
    pub implementation_digest: Sha256Digest,
    pub outputs: BTreeMap<String, ReportedArtifactV1>,
}

impl JsonContract for ReportedStageResultV1 {
    const SCHEMA_ID: &'static str = REPORTED_STAGE_RESULT_SCHEMA;
}

impl ReportedStageResultV1 {
    pub fn digest(&self) -> Result<ScientificContentDigest, ContractError> {
        self.validate()?;
        hash_contract(RESULT_DIGEST_DOMAIN, self)
    }

    pub fn from_checked_outputs(
        invocation: &CheckedStageInvocationV1,
        outputs: BTreeMap<String, CheckedArtifactV1>,
    ) -> Result<Self, ContractError> {
        let reported_outputs = checked_outputs_matching_ports(invocation, &outputs)?;
        let result = Self {
            schema: REPORTED_STAGE_RESULT_SCHEMA.to_string(),
            invocation_digest: invocation.reported().digest()?,
            execution_key: invocation.reported().execution_key.clone(),
            implementation_digest: invocation.reported().implementation.implementation_digest.clone(),
            outputs: reported_outputs,
        };
        result.validate_against_invocation(invocation.reported())?;
        Ok(result)
    }

    fn validate(&self) -> Result<(), ContractError> {
        if self.schema != REPORTED_STAGE_RESULT_SCHEMA {
            return Err(ContractError::new(
                "reported_stage_result.schema",
                format!("expected {REPORTED_STAGE_RESULT_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        let mut keys = BTreeSet::new();
        for artifact in self.outputs.values() {
            artifact.validate()?;
            if !keys.insert(artifact.artifact_key.as_str()) {
                return Err(ContractError::new(
                    "reported_stage_result.outputs.artifact_key",
                    format!(
                        "artifact key {:?} is reused for more than one output port",
                        artifact.artifact_key.as_str()
                    ),
                ));
            }
        }
        Ok(())
    }

    fn validate_against_invocation(&self, invocation: &StageInvocationV1) -> Result<(), ContractError> {
        self.validate()?;
        let expected_digest = invocation.digest()?;
        if self.invocation_digest != expected_digest {
            return Err(ContractError::new(
                "reported_stage_result.invocation_digest",
                "does not match the invocation",
            ));
        }
        if self.execution_key != invocation.execution_key {
            return Err(ContractError::new(
                "reported_stage_result.execution_key",
                "does not match the invocation",
            ));
        }
        if self.implementation_digest != invocation.implementation.implementation_digest {
            return Err(ContractError::new(
                "reported_stage_result.implementation_digest",
                "does not match the invocation implementation",
            ));
        }
        require_exact_ports(
            "reported_stage_result.outputs",
            self.outputs.keys(),
            invocation.declared_outputs.keys(),
        )?;
        for (port, artifact) in &self.outputs {
            let declared = &invocation.declared_outputs[port];
            require_role_agreement("reported_stage_result.outputs", port, declared.role.as_str(), artifact)?;
        }
        Ok(())
    }
}

/// Serializable receipt. Parsing this object does not make it trusted.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StageReceiptV1 {
    pub schema: String,
    pub plan_digest: Sha256Digest,
    pub stage_id: String,
    pub stage_spec_digest: Sha256Digest,
    pub execution_key: StageExecutionKey,
    pub invocation_digest: ScientificContentDigest,
    pub result_digest: ScientificContentDigest,
    pub implementation_digest: Sha256Digest,
    pub inputs: BTreeMap<String, ArtifactReference>,
    pub outputs: BTreeMap<String, ReportedArtifactV1>,
}

impl JsonContract for StageReceiptV1 {
    const SCHEMA_ID: &'static str = STAGE_RECEIPT_SCHEMA;
}

impl StageReceiptV1 {
    pub fn digest(&self) -> Result<ScientificContentDigest, ContractError> {
        if self.schema != STAGE_RECEIPT_SCHEMA {
            return Err(ContractError::new(
                "stage_receipt.schema",
                format!("expected {STAGE_RECEIPT_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        hash_contract(RECEIPT_DIGEST_DOMAIN, self)
    }
}

/// Receipt constructed only after every independent check succeeded.
///
/// ```compile_fail
/// fn decode(bytes: &[u8]) -> fern_artifact::CheckedStageReceiptV1 {
///     serde_json::from_slice(bytes).unwrap()
/// }
/// ```
#[derive(Debug, Clone, PartialEq)]
pub struct CheckedStageReceiptV1 {
    reported: StageReceiptV1,
}

impl CheckedStageReceiptV1 {
    pub fn reported(&self) -> &StageReceiptV1 {
        &self.reported
    }

    pub fn into_reported(self) -> StageReceiptV1 {
        self.reported
    }
}

/// Reopen reported outputs against a checked invocation and produce a receipt.
pub fn check_stage_result(
    checked_invocation: &CheckedStageInvocationV1,
    reported_result: &ReportedStageResultV1,
    independently_checked_outputs: BTreeMap<String, CheckedArtifactV1>,
) -> Result<CheckedStageReceiptV1, ContractError> {
    let invocation = checked_invocation.reported();
    reported_result.validate_against_invocation(invocation)?;
    let expected_outputs = checked_outputs_matching_ports(checked_invocation, &independently_checked_outputs)?;
    if expected_outputs != reported_result.outputs {
        return Err(ContractError::new(
            "reported_stage_result.outputs",
            "do not match the independently reopened checked artifacts",
        ));
    }
    let receipt = StageReceiptV1 {
        schema: STAGE_RECEIPT_SCHEMA.to_string(),
        plan_digest: invocation.plan_digest.clone(),
        stage_id: invocation.stage_id.clone(),
        stage_spec_digest: invocation.stage_spec_digest.clone(),
        execution_key: invocation.execution_key.clone(),
        invocation_digest: invocation.digest()?,
        result_digest: reported_result.digest()?,
        implementation_digest: invocation.implementation.implementation_digest.clone(),
        inputs: invocation
            .resolved_inputs
            .iter()
            .map(|(port, artifact)| (port.clone(), artifact.artifact_reference()))
            .collect(),
        outputs: reported_result.outputs.clone(),
    };
    Ok(CheckedStageReceiptV1 { reported: receipt })
}

/// Check the report from one exact Home-issued runner request.
///
/// The caller must supply the immutable request already bound to the Stage
/// Attempt; this pure function does not authenticate that provenance. It
/// revalidates every internal request binding and its digest before checking
/// output bytes.
pub fn check_stage_runner_report(
    request: &StageRunnerRequestV1,
    report: &StageRunnerReportV1,
    independently_checked_outputs: BTreeMap<String, CheckedArtifactV1>,
) -> Result<CheckedStageReceiptV1, ContractError> {
    request.validate()?;
    report.validate()?;
    if report.stage_attempt_id != request.stage_attempt_id {
        return Err(ContractError::new(
            "stage_runner_report.stage_attempt_id",
            "does not answer the Home-issued request",
        ));
    }
    if report.request_digest != request.digest()? {
        return Err(ContractError::new(
            "stage_runner_report.request_digest",
            "does not bind the Home-issued request bytes",
        ));
    }
    let success = match &report.outcome {
        StageRunnerOutcomeV1::Success(success) => success,
        StageRunnerOutcomeV1::Failure(failure) => {
            return Err(ContractError::new(
                "stage_runner_report.outcome",
                format!("runner failed with {}: {}", failure.code, failure.message),
            ));
        }
    };
    let checked_invocation = CheckedStageInvocationV1 {
        reported: request.invocation.clone(),
    };
    check_stage_result(&checked_invocation, &success.result, independently_checked_outputs)
}

pub const STAGE_ARTIFACT_MATERIALIZATION_SCHEMA: &str = "fern.stage_artifact_materialization.v1";
const MATERIALIZATION_DIGEST_DOMAIN: &[u8] = b"fern.stage_artifact_materialization.v1\0";
const LEGACY_LOCAL_STORAGE_SITE: &str = "legacy-local";

/// Storage fact for one stage output. Site and path are not scientific identity.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StageArtifactMaterializationV1 {
    pub schema: String,
    pub stage_receipt_digest: ScientificContentDigest,
    pub output_port: String,
    pub storage_site_id: String,
    pub relative_path: String,
    pub artifact: ReportedArtifactV1,
}

impl JsonContract for StageArtifactMaterializationV1 {
    const SCHEMA_ID: &'static str = STAGE_ARTIFACT_MATERIALIZATION_SCHEMA;
}

impl StageArtifactMaterializationV1 {
    pub fn digest(&self) -> Result<ScientificContentDigest, ContractError> {
        if self.schema != STAGE_ARTIFACT_MATERIALIZATION_SCHEMA {
            return Err(ContractError::new(
                "stage_artifact_materialization.schema",
                format!(
                    "expected {STAGE_ARTIFACT_MATERIALIZATION_SCHEMA:?}, found {:?}",
                    self.schema
                ),
            ));
        }
        hash_contract(MATERIALIZATION_DIGEST_DOMAIN, self)
    }
}

/// Materialization constructed only after the checked receipt and artifact agree.
///
/// Site and path are stored as location facts. They do not enter artifact identity.
///
/// ```compile_fail
/// fn decode(bytes: &[u8]) -> fern_artifact::CheckedStageArtifactCommitV1 {
///     serde_json::from_slice(bytes).unwrap()
/// }
/// ```
#[derive(Debug, Clone, PartialEq)]
pub struct CheckedStageArtifactCommitV1 {
    reported: StageArtifactMaterializationV1,
    checked_artifact: CheckedArtifactV1,
}

impl CheckedStageArtifactCommitV1 {
    pub fn from_checked(
        receipt: &CheckedStageReceiptV1,
        output_port: &str,
        artifact: CheckedArtifactV1,
        storage_site_id: impl Into<String>,
        relative_path: impl Into<String>,
    ) -> Result<Self, ContractError> {
        let reported_receipt = receipt.reported();
        let Some(declared) = reported_receipt.outputs.get(output_port) else {
            return Err(ContractError::new(
                "stage_artifact_materialization.output_port",
                format!("checked receipt does not declare output {output_port:?}"),
            ));
        };
        if artifact.reported_artifact() != declared {
            return Err(ContractError::new(
                "stage_artifact_materialization.artifact",
                "does not match the checked receipt output",
            ));
        }
        let storage_site_id = storage_site_id.into();
        validate_storage_site_id(&storage_site_id)?;
        let relative_path = relative_path.into();
        validate_stage_relative_path(&relative_path)?;
        let reported = StageArtifactMaterializationV1 {
            schema: STAGE_ARTIFACT_MATERIALIZATION_SCHEMA.to_string(),
            stage_receipt_digest: reported_receipt.digest()?,
            output_port: output_port.to_string(),
            storage_site_id,
            relative_path,
            artifact: artifact.reported_artifact().clone(),
        };
        reported.digest()?;
        Ok(Self {
            reported,
            checked_artifact: artifact,
        })
    }

    pub fn reported(&self) -> &StageArtifactMaterializationV1 {
        &self.reported
    }

    pub fn checked_artifact(&self) -> &CheckedArtifactV1 {
        &self.checked_artifact
    }

    pub fn byte_digest(&self) -> &crate::ByteDigest {
        &self.reported.artifact.byte_digest
    }
}

pub const STAGE_RETURN_VALIDATION_SCHEMA: &str = "fern.stage_return_validation.v1";

/// Operational artifact-retention reference checked from the immutable StagePlan.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StageReturnReferenceV1 {
    pub owner_kind: String,
    pub owner_id: String,
    pub purpose: String,
    pub materialization_requirement: MaterializationRequirementV1,
}

/// One checked output location and byte identity handed to the Go control plane.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StageReturnArtifactV1 {
    pub output_port: String,
    pub storage_site_id: String,
    pub relative_path: String,
    pub byte_digest: crate::ByteDigest,
    pub byte_count: u64,
    pub references: Vec<StageReturnReferenceV1>,
}

/// Fixed-checker receipt joining scientific identity to one operational Stage Attempt.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StageReturnValidationV1 {
    pub schema: String,
    pub stage_attempt_id: String,
    pub plan_id: String,
    pub stage_plan_instance_id: String,
    pub stage_plan_digest: Sha256Digest,
    pub stage_id: String,
    pub stage_spec_digest: Sha256Digest,
    pub execution_key: StageExecutionKey,
    pub invocation_digest: ScientificContentDigest,
    pub transport_manifest_digest: Sha256Digest,
    pub scientific_result_digest: ScientificContentDigest,
    pub checked_receipt_digest: ScientificContentDigest,
    pub checked_receipt_transport_digest: Sha256Digest,
    pub artifact_manifest_digest: Sha256Digest,
    pub artifacts: Vec<StageReturnArtifactV1>,
}

impl JsonContract for StageReturnValidationV1 {
    const SCHEMA_ID: &'static str = STAGE_RETURN_VALIDATION_SCHEMA;
}

/// A Stage return that can only be built from a checked scientific receipt,
/// checked output bytes, and the immutable StagePlan that owns retention.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CheckedStageReturnV1 {
    reported: StageReturnValidationV1,
}

impl CheckedStageReturnV1 {
    pub fn reported(&self) -> &StageReturnValidationV1 {
        &self.reported
    }

    /// Exact compact JSON bytes transferred to Go.
    pub fn json_bytes(&self) -> Result<Vec<u8>, ContractError> {
        serde_json::to_vec(&self.reported)
            .map_err(|error| ContractError::new("stage_return_validation", format!("cannot encode: {error}")))
    }

    /// Transport identity of the exact validation receipt bytes.
    pub fn digest(&self) -> Result<Sha256Digest, ContractError> {
        Ok(Sha256Digest::from_bytes(&self.json_bytes()?))
    }
}

/// Join the Rust-owned scientific checks to the operational facts Go accepts.
pub fn check_stage_return_for_commit(
    stage_attempt_id: &str,
    plan_id: &str,
    stage_plan_instance_id: &str,
    stage_plan: &StagePlanV1,
    transport_manifest_digest: Sha256Digest,
    receipt: &CheckedStageReceiptV1,
    commits: &[CheckedStageArtifactCommitV1],
) -> Result<CheckedStageReturnV1, ContractError> {
    for (field, value) in [
        ("stage_return_validation.stage_attempt_id", stage_attempt_id),
        ("stage_return_validation.plan_id", plan_id),
        ("stage_return_validation.stage_plan_instance_id", stage_plan_instance_id),
    ] {
        if value.is_empty() || value.trim() != value {
            return Err(ContractError::new(field, "must be a non-empty trimmed id"));
        }
    }

    let reported_receipt = receipt.reported();
    let checked_receipt_json = serde_json::to_vec(reported_receipt).map_err(|error| {
        ContractError::new(
            "stage_return_validation.checked_receipt",
            format!("cannot encode: {error}"),
        )
    })?;
    let plan_digest = stage_plan.digest().map_err(plan_error)?;
    if reported_receipt.plan_digest != plan_digest {
        return Err(ContractError::new(
            "stage_return_validation.stage_plan_digest",
            "checked receipt does not belong to the supplied StagePlan",
        ));
    }
    let stage = stage_plan.stage(&reported_receipt.stage_id).map_err(plan_error)?;
    if stage.digest().map_err(plan_error)? != reported_receipt.stage_spec_digest {
        return Err(ContractError::new(
            "stage_return_validation.stage_spec_digest",
            "checked receipt does not match the StageSpec",
        ));
    }

    let receipt_digest = reported_receipt.digest()?;
    let expected_ports: BTreeSet<_> = reported_receipt.outputs.keys().cloned().collect();
    let mut seen_ports = BTreeSet::new();
    let mut artifacts = Vec::with_capacity(commits.len());
    for commit in commits {
        let materialization = commit.reported();
        if materialization.stage_receipt_digest != receipt_digest {
            return Err(ContractError::new(
                "stage_return_validation.artifacts",
                "materialization does not belong to the checked receipt",
            ));
        }
        if !seen_ports.insert(materialization.output_port.clone()) {
            return Err(ContractError::new(
                "stage_return_validation.artifacts.output_port",
                format!("repeats output port {:?}", materialization.output_port),
            ));
        }
        if !expected_ports.contains(&materialization.output_port) {
            return Err(ContractError::new(
                "stage_return_validation.artifacts.output_port",
                format!("unknown output port {:?}", materialization.output_port),
            ));
        }
        if materialization.artifact.byte_count > i64::MAX as u64 {
            return Err(ContractError::new(
                "stage_return_validation.artifacts.byte_count",
                "does not fit the Registry artifact catalog",
            ));
        }

        let mut references = Vec::new();
        for consumer in &stage_plan.stages {
            for (input_port, input) in &consumer.inputs {
                let StageInputSourceV1::StageOutput { stage_id, output } = &input.source else {
                    continue;
                };
                if stage_id == &reported_receipt.stage_id && output == &materialization.output_port {
                    references.push(StageReturnReferenceV1 {
                        owner_kind: "stage_consumer".to_string(),
                        owner_id: format!("{plan_id}/{stage_plan_instance_id}/{}/{input_port}", consumer.id),
                        purpose: "stage_input".to_string(),
                        materialization_requirement: input.materialization_requirement,
                    });
                }
            }
        }
        for (export_name, export) in &stage_plan.exports {
            if export.stage_id == reported_receipt.stage_id && export.output == materialization.output_port {
                references.push(StageReturnReferenceV1 {
                    owner_kind: "stage_plan_export".to_string(),
                    owner_id: format!("{plan_id}/{stage_plan_instance_id}/{export_name}"),
                    purpose: "plan_export".to_string(),
                    materialization_requirement: export.materialization_requirement,
                });
            }
        }
        references.sort_by(|left, right| {
            (&left.owner_kind, &left.owner_id, &left.purpose).cmp(&(&right.owner_kind, &right.owner_id, &right.purpose))
        });
        artifacts.push(StageReturnArtifactV1 {
            output_port: materialization.output_port.clone(),
            storage_site_id: materialization.storage_site_id.clone(),
            relative_path: materialization.relative_path.clone(),
            byte_digest: materialization.artifact.byte_digest.clone(),
            byte_count: materialization.artifact.byte_count,
            references,
        });
    }
    if seen_ports != expected_ports {
        return Err(ContractError::new(
            "stage_return_validation.artifacts",
            "checked materializations must cover every receipt output port",
        ));
    }
    artifacts.sort_by(|left, right| left.output_port.cmp(&right.output_port));
    let artifact_manifest_bytes = serde_json::to_vec(&artifacts)
        .map_err(|error| ContractError::new("stage_return_validation.artifacts", format!("cannot encode: {error}")))?;
    let reported = StageReturnValidationV1 {
        schema: STAGE_RETURN_VALIDATION_SCHEMA.to_string(),
        stage_attempt_id: stage_attempt_id.to_string(),
        plan_id: plan_id.to_string(),
        stage_plan_instance_id: stage_plan_instance_id.to_string(),
        stage_plan_digest: plan_digest,
        stage_id: reported_receipt.stage_id.clone(),
        stage_spec_digest: reported_receipt.stage_spec_digest.clone(),
        execution_key: reported_receipt.execution_key.clone(),
        invocation_digest: reported_receipt.invocation_digest.clone(),
        transport_manifest_digest,
        scientific_result_digest: reported_receipt.result_digest.clone(),
        checked_receipt_digest: receipt_digest,
        checked_receipt_transport_digest: Sha256Digest::from_bytes(&checked_receipt_json),
        artifact_manifest_digest: Sha256Digest::from_bytes(&artifact_manifest_bytes),
        artifacts,
    };
    Ok(CheckedStageReturnV1 { reported })
}

fn validate_storage_site_id(value: &str) -> Result<(), ContractError> {
    if value.is_empty() || value.trim() != value {
        return Err(ContractError::new(
            "stage_artifact_materialization.storage_site_id",
            "must be a non-empty trimmed name",
        ));
    }
    if value == LEGACY_LOCAL_STORAGE_SITE {
        return Err(ContractError::new(
            "stage_artifact_materialization.storage_site_id",
            "legacy-local is only for migrated pre-site locations; a Stage commit must name a real site",
        ));
    }
    if value.starts_with('/') || value.starts_with('\\') || value.contains('/') || value.contains('\\') {
        return Err(ContractError::new(
            "stage_artifact_materialization.storage_site_id",
            "must be a site name, not a filesystem path",
        ));
    }
    Ok(())
}

fn validate_stage_relative_path(relative_path: &str) -> Result<(), ContractError> {
    if relative_path.is_empty() {
        return Err(ContractError::new(
            "stage_artifact_materialization.relative_path",
            "must be a non-empty relative path",
        ));
    }
    if relative_path.starts_with('/') || relative_path.starts_with('\\') {
        return Err(ContractError::new(
            "stage_artifact_materialization.relative_path",
            "must be relative, not absolute",
        ));
    }
    if relative_path
        .split(['/', '\\'])
        .any(|component| component.is_empty() || component == "." || component == "..")
    {
        return Err(ContractError::new(
            "stage_artifact_materialization.relative_path",
            "must not contain empty, dot, or parent components",
        ));
    }
    Ok(())
}

fn stage_execution_key(
    subject: &ContentAddressedSubjectV1,
    stage: &StageSpecV1,
    resolved_inputs: &BTreeMap<String, ReportedArtifactV1>,
) -> Result<StageExecutionKey, ContractError> {
    let spec_digest = stage.digest().map_err(plan_error)?;
    let mut hasher = Sha256::new();
    hasher.update(EXECUTION_KEY_DOMAIN);
    hasher.update(subject.content_digest.as_str().as_bytes());
    hasher.update(b"\0");
    hasher.update(spec_digest.as_str().as_bytes());
    for (port, artifact) in resolved_inputs {
        hasher.update(b"\0");
        hasher.update(port.as_bytes());
        hasher.update(b"\0");
        hasher.update(&canonical_bytes("stage_execution_key.input", artifact)?);
    }
    StageExecutionKey::parse(format!("sha256:{:x}", hasher.finalize()))
}

fn reported_inputs_matching_ports(
    stage: &StageSpecV1,
    resolved_inputs: &BTreeMap<String, CheckedArtifactV1>,
) -> Result<BTreeMap<String, ReportedArtifactV1>, ContractError> {
    require_exact_ports(
        "stage_invocation.resolved_inputs",
        resolved_inputs.keys(),
        stage.inputs.keys(),
    )?;
    let mut reported = BTreeMap::new();
    for (port, checked) in resolved_inputs {
        let artifact = checked.reported_artifact();
        artifact.validate()?;
        require_role_agreement(
            "stage_invocation.resolved_inputs",
            port,
            stage.inputs[port].role.as_str(),
            artifact,
        )?;
        reported.insert(port.clone(), artifact.clone());
    }
    Ok(reported)
}

fn checked_outputs_matching_ports(
    invocation: &CheckedStageInvocationV1,
    outputs: &BTreeMap<String, CheckedArtifactV1>,
) -> Result<BTreeMap<String, ReportedArtifactV1>, ContractError> {
    require_exact_ports(
        "reported_stage_result.outputs",
        outputs.keys(),
        invocation.reported().declared_outputs.keys(),
    )?;
    let mut reported = BTreeMap::new();
    for (port, checked) in outputs {
        let artifact = checked.reported_artifact();
        artifact.validate()?;
        require_role_agreement(
            "reported_stage_result.outputs",
            port,
            invocation.reported().declared_outputs[port].role.as_str(),
            artifact,
        )?;
        reported.insert(port.clone(), artifact.clone());
    }
    Ok(reported)
}

fn require_role_agreement(
    field: &str,
    port: &str,
    declared_role: &str,
    artifact: &ReportedArtifactV1,
) -> Result<(), ContractError> {
    if artifact.role.as_str() != declared_role {
        return Err(ContractError::new(
            field,
            format!(
                "port {port:?} declares role {declared_role:?}, found {:?}",
                artifact.role.as_str()
            ),
        ));
    }
    Ok(())
}

fn require_exact_ports<'a, I, J>(field: &str, observed: I, expected: J) -> Result<(), ContractError>
where
    I: IntoIterator<Item = &'a String>,
    J: IntoIterator<Item = &'a String>,
{
    let observed: BTreeSet<&str> = observed.into_iter().map(String::as_str).collect();
    let expected: BTreeSet<&str> = expected.into_iter().map(String::as_str).collect();
    if observed == expected {
        return Ok(());
    }
    let missing: Vec<_> = expected.difference(&observed).cloned().collect();
    let extra: Vec<_> = observed.difference(&expected).cloned().collect();
    Err(ContractError::new(
        field,
        format!("port set must match the StageSpec exactly; missing {missing:?}, extra {extra:?}"),
    ))
}

fn hash_contract<T: Serialize>(domain: &[u8], value: &T) -> Result<ScientificContentDigest, ContractError> {
    Ok(domain_digest(domain, &canonical_bytes("digest", value)?))
}

fn canonical_bytes<T: Serialize>(field: &str, value: &T) -> Result<Vec<u8>, ContractError> {
    fern_json_contract::canonical_json_bytes(value)
        .map_err(|error| ContractError::new(field, format!("cannot canonicalize: {error}")))
}

fn plan_error(error: StagePlanError) -> ContractError {
    ContractError::new(error.field, error.explanation)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{ArtifactKey, ArtifactRole, ByteDigest};
    use serde_json::json;

    const CANONICAL_PLAN: &str = include_str!("../../fern-experiment-types/fixtures/stage_plan_v1_canonical.json");
    const COMPILED_EXPERIMENT_BYTES: &[u8] = b"canonical compiled experiment fixture";
    const RAW_FORECAST_BYTES: &[u8] = b"canonical train raw forecast bytes";
    const FORGED_FORECAST_BYTES: &[u8] = b"forged train raw forecast bytes!!";

    fn canonical_plan() -> StagePlanV1 {
        StagePlanV1::parse_json(CANONICAL_PLAN.as_bytes()).expect("canonical stage plan fixture must parse")
    }

    fn analysis_fork(plan: &StagePlanV1) -> StagePlanV1 {
        let mut value = serde_json::to_value(plan).unwrap();
        value["stages"][2]["implementation"]["implementation_digest"] =
            json!("sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        StagePlanV1::parse_json(serde_json::to_vec(&value).unwrap().as_slice()).unwrap()
    }

    fn checked_artifact(
        key: &str,
        role: ArtifactRole,
        stored_bytes: &[u8],
        canonical_content_bytes: &[u8],
    ) -> CheckedArtifactV1 {
        let reported = ReportedArtifactV1::from_payloads(
            ArtifactKey::parse(key).unwrap(),
            role,
            "application/octet-stream",
            stored_bytes,
            canonical_content_bytes,
            None,
            None,
        )
        .unwrap();
        reported
            .check_reopened_payloads(stored_bytes, canonical_content_bytes)
            .unwrap()
    }

    fn compiled_experiment() -> CheckedArtifactV1 {
        checked_artifact(
            "artifact:compiled-experiment",
            ArtifactRole::CompiledExperiment,
            COMPILED_EXPERIMENT_BYTES,
            COMPILED_EXPERIMENT_BYTES,
        )
    }

    fn raw_forecast() -> CheckedArtifactV1 {
        checked_artifact(
            "artifact:train-raw-forecast",
            ArtifactRole::RawForecast,
            RAW_FORECAST_BYTES,
            RAW_FORECAST_BYTES,
        )
    }

    fn train_inputs() -> BTreeMap<String, CheckedArtifactV1> {
        BTreeMap::from([("compiled_experiment".to_string(), compiled_experiment())])
    }

    fn train_outputs() -> BTreeMap<String, CheckedArtifactV1> {
        BTreeMap::from([("raw_forecast".to_string(), raw_forecast())])
    }

    fn train_invocation(plan: &StagePlanV1) -> CheckedStageInvocationV1 {
        CheckedStageInvocationV1::from_checked_inputs(plan, "train", train_inputs()).unwrap()
    }

    fn train_result(invocation: &CheckedStageInvocationV1) -> ReportedStageResultV1 {
        ReportedStageResultV1::from_checked_outputs(invocation, train_outputs()).unwrap()
    }

    fn train_receipt(invocation: &CheckedStageInvocationV1, result: &ReportedStageResultV1) -> CheckedStageReceiptV1 {
        check_stage_result(invocation, result, train_outputs()).unwrap()
    }

    fn train_return(plan: &StagePlanV1) -> CheckedStageReturnV1 {
        let invocation = train_invocation(plan);
        let result = train_result(&invocation);
        let receipt = train_receipt(&invocation, &result);
        let commit = CheckedStageArtifactCommitV1::from_checked(
            &receipt,
            "raw_forecast",
            raw_forecast(),
            "pod-a",
            "attempt-1/raw.pt",
        )
        .unwrap();
        check_stage_return_for_commit(
            "stage-attempt-1",
            "plan-1",
            "instance-1",
            plan,
            Sha256Digest::from_bytes(b"transport manifest"),
            &receipt,
            &[commit],
        )
        .unwrap()
    }

    fn json_keys(value: &Value, keys: &mut BTreeSet<String>) {
        match value {
            Value::Object(entries) => {
                for (key, child) in entries {
                    keys.insert(key.clone());
                    json_keys(child, keys);
                }
            }
            Value::Array(values) => {
                for child in values {
                    json_keys(child, keys);
                }
            }
            _ => {}
        }
    }

    #[test]
    fn checked_inputs_construct_a_checked_invocation() {
        let plan = canonical_plan();
        let invocation = train_invocation(&plan);
        let reported = invocation.reported();
        assert_eq!(reported.stage_id, "train");
        assert_eq!(reported.plan_digest, plan.digest().unwrap());
        assert_eq!(
            reported.stage_spec_digest,
            plan.stage("train").unwrap().digest().unwrap()
        );
        assert_eq!(
            reported.resolved_inputs["compiled_experiment"].role,
            ArtifactRole::CompiledExperiment
        );
        assert!(reported.digest().is_ok());
    }

    #[test]
    fn a_plain_reported_artifact_cannot_construct_the_checked_invocation() {
        let plan = canonical_plan();
        let reported = compiled_experiment().reported_artifact().clone();
        let error = CheckedStageInvocationV1::from_checked_inputs(&plan, "train", train_inputs()).unwrap();
        assert_eq!(error.reported().resolved_inputs["compiled_experiment"], reported);
        fn requires_checked(_: BTreeMap<String, CheckedArtifactV1>) {}
        requires_checked(train_inputs());
    }

    #[test]
    fn execution_key_changes_when_input_byte_or_content_digest_changes() {
        let plan = canonical_plan();
        let original = train_invocation(&plan).reported().execution_key.clone();
        let mut mutated = compiled_experiment().reported_artifact().clone();
        mutated.byte_digest = ByteDigest::from_bytes(FORGED_FORECAST_BYTES);
        mutated.byte_count = u64::try_from(FORGED_FORECAST_BYTES.len()).unwrap();
        let changed = stage_execution_key(
            &plan.subject,
            plan.stage("train").unwrap(),
            &BTreeMap::from([("compiled_experiment".to_string(), mutated)]),
        )
        .unwrap();
        assert_ne!(original, changed);
    }

    #[test]
    fn execution_key_changes_when_cell_subject_changes() {
        let plan = canonical_plan();
        let original = train_invocation(&plan).reported().execution_key.clone();
        let mut changed = serde_json::to_value(&plan).unwrap();
        changed["subject"]["content_digest"] =
            json!("sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        changed["subject"]["identity"] = json!("other-cell");
        let changed = StagePlanV1::parse_json(serde_json::to_vec(&changed).unwrap().as_slice()).unwrap();
        assert_ne!(original, train_invocation(&changed).reported().execution_key);
    }

    #[test]
    fn execution_key_changes_when_implementation_or_parameters_change() {
        let plan = canonical_plan();
        let original = train_invocation(&plan).reported().execution_key.clone();
        let mut changed_impl = serde_json::to_value(&plan).unwrap();
        changed_impl["stages"][0]["implementation"]["implementation_digest"] =
            json!("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        let changed_impl = StagePlanV1::parse_json(serde_json::to_vec(&changed_impl).unwrap().as_slice()).unwrap();
        assert_ne!(original, train_invocation(&changed_impl).reported().execution_key);

        let mut changed_params = serde_json::to_value(&plan).unwrap();
        changed_params["stages"][0]["parameters"]["replica"] = json!(1);
        let changed_params = StagePlanV1::parse_json(serde_json::to_vec(&changed_params).unwrap().as_slice()).unwrap();
        assert_ne!(original, train_invocation(&changed_params).reported().execution_key);
    }

    #[test]
    fn downstream_plan_changes_leave_upstream_execution_key_unchanged() {
        let plan_a = canonical_plan();
        let plan_b = analysis_fork(&plan_a);
        assert_ne!(plan_a.digest().unwrap(), plan_b.digest().unwrap());
        assert_ne!(
            plan_a.stage("scientific_analysis").unwrap().digest().unwrap(),
            plan_b.stage("scientific_analysis").unwrap().digest().unwrap()
        );
        assert_eq!(
            plan_a.stage("train").unwrap().digest().unwrap(),
            plan_b.stage("train").unwrap().digest().unwrap()
        );
        let invocation_a = train_invocation(&plan_a);
        let invocation_b = train_invocation(&plan_b);
        assert_eq!(
            invocation_a.reported().execution_key,
            invocation_b.reported().execution_key
        );
        assert_ne!(
            invocation_a.reported().digest().unwrap(),
            invocation_b.reported().digest().unwrap()
        );
        assert_ne!(invocation_a.reported().plan_digest, invocation_b.reported().plan_digest);
    }

    #[test]
    fn invocation_digest_binds_the_exact_plan() {
        let plan_a = canonical_plan();
        let plan_b = analysis_fork(&plan_a);
        let digest_a = train_invocation(&plan_a).reported().digest().unwrap();
        let digest_b = train_invocation(&plan_b).reported().digest().unwrap();
        assert_ne!(digest_a, digest_b);
    }

    #[test]
    fn missing_extra_or_wrong_role_output_is_refused() {
        let plan = canonical_plan();
        let invocation = train_invocation(&plan);
        let missing = ReportedStageResultV1::from_checked_outputs(&invocation, BTreeMap::new()).unwrap_err();
        assert!(missing.to_string().contains("missing"), "{missing}");

        let mut extra = train_outputs();
        extra.insert(
            "score_receipt".to_string(),
            checked_artifact("artifact:extra-score", ArtifactRole::ScoreReceipt, b"extra", b"extra"),
        );
        let extra = ReportedStageResultV1::from_checked_outputs(&invocation, extra).unwrap_err();
        assert!(extra.to_string().contains("extra"), "{extra}");

        let wrong_role = BTreeMap::from([(
            "raw_forecast".to_string(),
            checked_artifact(
                "artifact:train-raw-forecast",
                ArtifactRole::ScoreReceipt,
                RAW_FORECAST_BYTES,
                RAW_FORECAST_BYTES,
            ),
        )]);
        let wrong = ReportedStageResultV1::from_checked_outputs(&invocation, wrong_role).unwrap_err();
        assert!(wrong.to_string().contains("role"), "{wrong}");
    }

    #[test]
    fn changed_output_bytes_fail_independent_checked_artifact_verification() {
        let reported = raw_forecast().reported_artifact().clone();
        let error = reported
            .check_reopened_payloads(FORGED_FORECAST_BYTES, FORGED_FORECAST_BYTES)
            .unwrap_err();
        assert!(
            error.to_string().contains("byte_digest") || error.to_string().contains("byte_count"),
            "{error}"
        );

        let plan = canonical_plan();
        let invocation = train_invocation(&plan);
        let result = train_result(&invocation);
        let forged_output = BTreeMap::from([(
            "raw_forecast".to_string(),
            checked_artifact(
                "artifact:train-raw-forecast",
                ArtifactRole::RawForecast,
                FORGED_FORECAST_BYTES,
                FORGED_FORECAST_BYTES,
            ),
        )]);
        let error = check_stage_result(&invocation, &result, forged_output).unwrap_err();
        assert!(
            error.to_string().contains("independently reopened") || error.to_string().contains("outputs"),
            "{error}"
        );
    }

    #[test]
    fn a_forged_result_or_receipt_digest_is_refused() {
        let plan = canonical_plan();
        let invocation = train_invocation(&plan);
        let mut forged = train_result(&invocation);
        forged.invocation_digest = ScientificContentDigest::from_bytes(b"forged invocation digest");
        let error = check_stage_result(&invocation, &forged, train_outputs()).unwrap_err();
        assert!(error.to_string().contains("invocation_digest"), "{error}");

        let receipt = train_receipt(&invocation, &train_result(&invocation)).into_reported();
        let parsed: StageReceiptV1 = serde_json::from_value(serde_json::to_value(&receipt).unwrap()).unwrap();
        assert_eq!(parsed.schema, STAGE_RECEIPT_SCHEMA);
        assert_ne!(
            parsed.digest().unwrap(),
            ScientificContentDigest::from_bytes(b"forged receipt digest")
        );
    }

    #[test]
    fn exact_replay_produces_the_same_execution_result_and_receipt_digests() {
        let plan = canonical_plan();
        let first_invocation = train_invocation(&plan);
        let first_result = train_result(&first_invocation);
        let first_receipt = train_receipt(&first_invocation, &first_result);
        let second_invocation = train_invocation(&plan);
        let second_result = train_result(&second_invocation);
        let second_receipt = train_receipt(&second_invocation, &second_result);
        assert_eq!(
            first_invocation.reported().execution_key,
            second_invocation.reported().execution_key
        );
        assert_eq!(first_result.digest().unwrap(), second_result.digest().unwrap());
        assert_eq!(
            first_receipt.reported().digest().unwrap(),
            second_receipt.reported().digest().unwrap()
        );
    }

    #[test]
    fn unknown_json_fields_are_refused() {
        let plan = canonical_plan();
        let invocation = train_invocation(&plan).into_reported();
        let mut value = serde_json::to_value(&invocation).unwrap();
        value["surprise"] = json!(true);
        let error = serde_json::from_value::<StageInvocationV1>(value)
            .unwrap_err()
            .to_string();
        assert!(error.contains("unknown field"), "{error}");

        let mut result = serde_json::to_value(&train_result(&train_invocation(&plan))).unwrap();
        result["surprise"] = json!(true);
        let error = serde_json::from_value::<ReportedStageResultV1>(result)
            .unwrap_err()
            .to_string();
        assert!(error.contains("unknown field"), "{error}");
    }

    #[test]
    fn checked_stage_return_binds_attempt_science_bytes_and_plan_owned_retention() {
        let plan = canonical_plan();
        let checked = train_return(&plan);
        let returned = checked.reported();
        assert_eq!(returned.stage_attempt_id, "stage-attempt-1");
        assert_eq!(returned.plan_id, "plan-1");
        assert_eq!(returned.stage_plan_instance_id, "instance-1");
        assert_eq!(returned.stage_plan_digest, plan.digest().unwrap());
        assert_eq!(
            returned.stage_spec_digest,
            plan.stage("train").unwrap().digest().unwrap()
        );
        assert_eq!(
            returned.scientific_result_digest,
            train_result(&train_invocation(&plan)).digest().unwrap()
        );
        assert_eq!(returned.artifacts.len(), 1);
        assert_eq!(
            returned.artifacts[0].byte_digest,
            ByteDigest::from_bytes(RAW_FORECAST_BYTES)
        );
        assert_eq!(
            returned.artifacts[0].references,
            vec![StageReturnReferenceV1 {
                owner_kind: "stage_consumer".to_string(),
                owner_id: "plan-1/instance-1/acceptance_score/raw_forecast".to_string(),
                purpose: "stage_input".to_string(),
                materialization_requirement: MaterializationRequirementV1::BytesRequired,
            }]
        );
        assert_eq!(
            checked.digest().unwrap(),
            Sha256Digest::from_bytes(&checked.json_bytes().unwrap())
        );
    }

    #[test]
    fn checked_stage_return_refuses_incomplete_or_wrong_plan_materializations() {
        let plan = canonical_plan();
        let invocation = train_invocation(&plan);
        let result = train_result(&invocation);
        let receipt = train_receipt(&invocation, &result);
        let missing = check_stage_return_for_commit(
            "stage-attempt-1",
            "plan-1",
            "instance-1",
            &plan,
            Sha256Digest::from_bytes(b"transport manifest"),
            &receipt,
            &[],
        )
        .unwrap_err();
        assert!(missing.to_string().contains("every receipt output port"), "{missing}");

        let changed_plan = analysis_fork(&plan);
        let wrong_plan = check_stage_return_for_commit(
            "stage-attempt-1",
            "plan-1",
            "instance-1",
            &changed_plan,
            Sha256Digest::from_bytes(b"transport manifest"),
            &receipt,
            &[],
        )
        .unwrap_err();
        assert!(wrong_plan.to_string().contains("does not belong"), "{wrong_plan}");
    }

    #[test]
    fn json_contract_registry_includes_the_new_wire_objects() {
        let ids: BTreeSet<_> = crate::json_contracts()
            .into_iter()
            .map(|contract| contract.schema_id)
            .collect();
        assert!(ids.contains(STAGE_INVOCATION_SCHEMA));
        assert!(ids.contains(REPORTED_STAGE_RESULT_SCHEMA));
        assert!(ids.contains(STAGE_RECEIPT_SCHEMA));
        assert!(ids.contains(STAGE_ARTIFACT_MATERIALIZATION_SCHEMA));
        assert!(ids.contains(STAGE_RETURN_VALIDATION_SCHEMA));
        for schema_id in [
            STAGE_INVOCATION_SCHEMA,
            REPORTED_STAGE_RESULT_SCHEMA,
            STAGE_RECEIPT_SCHEMA,
            STAGE_ARTIFACT_MATERIALIZATION_SCHEMA,
            STAGE_RETURN_VALIDATION_SCHEMA,
        ] {
            let registration = crate::json_contracts()
                .into_iter()
                .find(|contract| contract.schema_id == schema_id)
                .unwrap();
            let document = (registration.schema_document)().unwrap();
            assert_eq!(document["$id"], schema_id);
        }
    }

    #[test]
    fn no_contract_contains_location_or_control_plane_fields() {
        let plan = canonical_plan();
        let invocation = train_invocation(&plan);
        let result = train_result(&invocation);
        let receipt = train_receipt(&invocation, &result);
        for document in [
            serde_json::to_value(invocation.reported()).unwrap(),
            serde_json::to_value(&result).unwrap(),
            serde_json::to_value(receipt.reported()).unwrap(),
        ] {
            let mut keys = BTreeSet::new();
            json_keys(&document, &mut keys);
            for key in &keys {
                let lowered = key.to_ascii_lowercase();
                for forbidden in ["path", "host", "pid", "lane", "lease", "epoch", "timestamp"] {
                    assert_ne!(lowered.as_str(), forbidden, "contract key {key:?} is forbidden");
                }
            }
        }
    }

    #[test]
    fn canonical_train_fixtures_round_trip() {
        let plan = canonical_plan();
        let invocation = train_invocation(&plan).into_reported();
        let result = train_result(&train_invocation(&plan));
        let receipt = train_receipt(&train_invocation(&plan), &result).into_reported();
        let returned = train_return(&plan);
        // Regenerate the frozen fixtures from the current constructors,
        // then fail on purpose so the diff is read, never silently blessed.
        // Needed when an upstream identity moves deliberately (Plan B moved
        // the plan digest and these fixtures were not regenerated with it).
        if std::env::var_os("FERN_ARTIFACT_REGENERATE_FIXTURES").is_some() {
            let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("fixtures");
            std::fs::write(
                dir.join("stage_invocation_v1_train.json"),
                serde_json::to_string_pretty(&invocation).unwrap() + "\n",
            )
            .unwrap();
            std::fs::write(
                dir.join("reported_stage_result_v1_train.json"),
                serde_json::to_string_pretty(&result).unwrap() + "\n",
            )
            .unwrap();
            std::fs::write(
                dir.join("stage_receipt_v1_train.json"),
                serde_json::to_string_pretty(&receipt).unwrap() + "\n",
            )
            .unwrap();
            std::fs::write(
                dir.join("stage_return_validation_v1_train.json"),
                serde_json::to_string_pretty(returned.reported()).unwrap() + "\n",
            )
            .unwrap();
            panic!(
                "train fixtures regenerated from the current constructors; rerun without the flag and READ THE DIFF"
            );
        }
        let invocation_fixture: StageInvocationV1 =
            serde_json::from_str(include_str!("../fixtures/stage_invocation_v1_train.json")).unwrap();
        let result_fixture: ReportedStageResultV1 =
            serde_json::from_str(include_str!("../fixtures/reported_stage_result_v1_train.json")).unwrap();
        let receipt_fixture: StageReceiptV1 =
            serde_json::from_str(include_str!("../fixtures/stage_receipt_v1_train.json")).unwrap();
        let returned_fixture: StageReturnValidationV1 = serde_json::from_str(
            &std::fs::read_to_string(
                std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("fixtures/stage_return_validation_v1_train.json"),
            )
            .unwrap(),
        )
        .unwrap();
        assert_eq!(invocation, invocation_fixture);
        assert_eq!(result, result_fixture);
        assert_eq!(receipt, receipt_fixture);
        assert_eq!(returned.reported(), &returned_fixture);
    }

    #[test]
    fn materialization_site_and_path_do_not_enter_artifact_identity() {
        let plan = canonical_plan();
        let invocation = train_invocation(&plan);
        let result = train_result(&invocation);
        let receipt = train_receipt(&invocation, &result);
        let artifact = raw_forecast();
        let pod_a = CheckedStageArtifactCommitV1::from_checked(
            &receipt,
            "raw_forecast",
            artifact.clone(),
            "pod-a",
            "train/raw.pt",
        )
        .unwrap();
        let pod_b =
            CheckedStageArtifactCommitV1::from_checked(&receipt, "raw_forecast", artifact, "pod-b", "train/raw.pt")
                .unwrap();
        assert_eq!(pod_a.byte_digest(), pod_b.byte_digest());
        assert_eq!(
            pod_a.checked_artifact().reported_artifact().content_digest,
            pod_b.checked_artifact().reported_artifact().content_digest
        );
        assert_ne!(pod_a.reported().digest().unwrap(), pod_b.reported().digest().unwrap());
        assert!(
            CheckedStageArtifactCommitV1::from_checked(
                &receipt,
                "raw_forecast",
                raw_forecast(),
                "legacy-local",
                "train/raw.pt",
            )
            .is_err()
        );
    }
}
