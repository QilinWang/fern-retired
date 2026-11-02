//! Fenced Stage Attempt ledger under an existing Run Plan permit.
//!
//! A Stage is work the Plan asked for. It does not elect a scheduler, mint a
//! lease, or spawn a runner. Closing a checked receipt records consumer
//! `artifact_references` in the same transaction; it does not use holds.
//! Parent fencing is the only write door.

use std::collections::BTreeSet;
use std::path::Path;

use fern_artifact::StageExecutionKey;
use fern_experiment_types::{RetryPolicy, StageInputSourceV1, StagePlanV1};
use serde::{Deserialize, Serialize};

use crate::lifecycle::{LifecycleError, LifecycleResult, sha256_hex};

/// Instance-level external port for a stage-local port name.
///
/// `train` + `training_worker_request` stays `training_worker_request`.
/// `control.train` + `training_worker_request` becomes `control.training_worker_request`.
pub fn qualified_external_input_key(stage_id: &str, port: &str) -> String {
    match stage_id.split_once('.') {
        Some(("control", _)) => arm_external_input_key("control", port),
        Some(("treatment", _)) => arm_external_input_key("treatment", port),
        _ => port.to_string(),
    }
}

pub fn arm_external_input_key(arm: &str, port: &str) -> String {
    format!("{arm}.{port}")
}

/// Secret bound to one Stage Attempt. Registry Home stores only its digest.
#[derive(Clone, PartialEq, Eq)]
pub struct SecretCapability(String);

impl std::fmt::Debug for SecretCapability {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str("SecretCapability([redacted])")
    }
}

/// Permit to write one Stage Attempt while the parent Run Plan lease remains current.
#[derive(Clone, PartialEq, Eq)]
pub struct StagePermit {
    plan_id: String,
    plan_attempt_id: String,
    plan_lease_id: String,
    parent_fencing_epoch: i64,
    stage_plan_instance_id: String,
    stage_id: String,
    stage_attempt_id: String,
    execution_key: StageExecutionKey,
    capability: SecretCapability,
}

/// Secret-bearing permit shape used only across the private Registry Home
/// process boundary. It intentionally has no `Debug` implementation.
#[derive(Serialize)]
pub struct StagePermitWire<'a> {
    pub plan_id: &'a str,
    pub plan_attempt_id: &'a str,
    pub plan_lease_id: &'a str,
    pub parent_fencing_epoch: i64,
    pub stage_plan_instance_id: &'a str,
    pub stage_id: &'a str,
    pub stage_attempt_id: &'a str,
    pub execution_key: &'a str,
    pub capability: &'a str,
}

impl std::fmt::Debug for StagePermit {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("StagePermit")
            .field("plan_id", &self.plan_id)
            .field("plan_attempt_id", &self.plan_attempt_id)
            .field("plan_lease_id", &self.plan_lease_id)
            .field("parent_fencing_epoch", &self.parent_fencing_epoch)
            .field("stage_plan_instance_id", &self.stage_plan_instance_id)
            .field("stage_id", &self.stage_id)
            .field("stage_attempt_id", &self.stage_attempt_id)
            .field("execution_key", &self.execution_key)
            .field("capability", &"[redacted]")
            .finish()
    }
}

impl StagePermit {
    /// Reconstruct the redacting Rust permit returned by the private Registry
    /// Home writer. The Go process is the only mint; Rust keeps the typed
    /// scientific execution key and uses this permit for later result writes.
    #[allow(clippy::too_many_arguments)]
    pub fn from_registry_home(
        plan_id: String,
        plan_attempt_id: String,
        plan_lease_id: String,
        parent_fencing_epoch: i64,
        stage_plan_instance_id: String,
        stage_id: String,
        stage_attempt_id: String,
        execution_key: StageExecutionKey,
        capability: String,
    ) -> LifecycleResult<Self> {
        if plan_id.is_empty()
            || plan_attempt_id.is_empty()
            || plan_lease_id.is_empty()
            || stage_plan_instance_id.is_empty()
            || stage_id.is_empty()
            || stage_attempt_id.is_empty()
            || capability.is_empty()
        {
            return Err(LifecycleError::InvalidRequest(
                "Registry Home returned an incomplete Stage permit".to_string(),
            ));
        }
        Ok(Self {
            plan_id,
            plan_attempt_id,
            plan_lease_id,
            parent_fencing_epoch,
            stage_plan_instance_id,
            stage_id,
            stage_attempt_id,
            execution_key,
            capability: SecretCapability(capability),
        })
    }

    pub fn plan_id(&self) -> &str {
        &self.plan_id
    }

    pub fn stage_plan_instance_id(&self) -> &str {
        &self.stage_plan_instance_id
    }

    pub fn stage_id(&self) -> &str {
        &self.stage_id
    }

    pub fn stage_attempt_id(&self) -> &str {
        &self.stage_attempt_id
    }

    pub fn execution_key(&self) -> &StageExecutionKey {
        &self.execution_key
    }

    pub fn parent_fencing_epoch(&self) -> i64 {
        self.parent_fencing_epoch
    }

    /// Cross the private `fern-home` stdin boundary without making the secret
    /// capability available to ordinary formatting.
    pub fn registry_home_wire(&self) -> StagePermitWire<'_> {
        StagePermitWire {
            plan_id: &self.plan_id,
            plan_attempt_id: &self.plan_attempt_id,
            plan_lease_id: &self.plan_lease_id,
            parent_fencing_epoch: self.parent_fencing_epoch,
            stage_plan_instance_id: &self.stage_plan_instance_id,
            stage_id: &self.stage_id,
            stage_attempt_id: &self.stage_attempt_id,
            execution_key: self.execution_key.as_str(),
            capability: &self.capability.0,
        }
    }
}

/// Node projection derived from Attempt history. It is not a writable field.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum StageNodeState {
    Satisfied,
    Running,
    Ready,
    Blocked,
    Waiting,
}

/// What `StageLastFailure.summary` says when the receipt bytes will not parse.
///
/// One name so the projection, the monitor and a test cannot describe the same
/// defect three ways.
pub const UNREADABLE_ERROR_RECEIPT_SUMMARY: &str = "failed Attempt carries an unreadable ErrorReceipt";

/// Why a node is Blocked, read from its latest terminal Attempt and its budget.
///
/// Blocked means no automatic path remains inside this plan. It is not "an
/// attempt failed": a failure whose receipt says `SameProfile` leaves the node
/// Ready while attempts remain, and `lost`/`expired` attempts retry the same way.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum StageBlockReason {
    /// The latest receipt says `Never`: repeating this exact request cannot make it valid.
    IntegrityFailure,
    /// The latest receipt says `RequiresHuman`, or the failed row carries no readable receipt.
    // CHG: an ErrorReceipt that is PRESENT but unparsable is now its own reason
    // below; this arm keeps the receipt that genuinely asks for a person, and
    // the failed row that carries no receipt json at all.
    RequiresHuman,
    /// The failed row carries ErrorReceipt bytes that will not parse, so the
    /// policy the worker recorded cannot be read at all.
    ///
    /// Separated from `RequiresHuman` because it is a different fact about a
    /// different party: the worker did answer, and the answer is corrupt or
    /// written by an older schema. Projecting it as a human decision hid a
    /// defect behind a queue of cells that looked deliberately parked.
    UnreadableErrorReceipt,
    /// The latest receipt says `OtherProfile`, and no admitted different Site exists to take it.
    OtherProfileRequired,
    /// Every attempt in the budget is spent without a checked success.
    RetryBudgetExhausted,
    /// A predecessor is Blocked, so this node can never become Ready.
    PredecessorBlocked,
}

impl StageBlockReason {
    pub fn sentence(self) -> &'static str {
        match self {
            Self::IntegrityFailure => "the latest attempt failed with retry policy Never",
            Self::RequiresHuman => "the latest attempt needs a human decision before any retry",
            Self::UnreadableErrorReceipt => "the failed attempt carries an unreadable ErrorReceipt",
            Self::OtherProfileRequired => "the latest attempt needs a different execution profile and none is admitted",
            Self::RetryBudgetExhausted => "every attempt in the budget is spent without a checked success",
            Self::PredecessorBlocked => "an upstream stage is blocked",
        }
    }
}

/// The latest terminal Attempt of one node, as the projection read it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct StageLastFailure {
    pub stage_attempt_id: String,
    pub attempt_ordinal: i64,
    /// `failed`, `lost` or `expired`.
    pub state: String,
    /// Absent for `lost`/`expired`, which carry no ErrorReceipt.
    pub retry_policy: Option<RetryPolicy>,
    pub summary: Option<String>,
}

/// One node's state with the facts it was derived from, for monitors and the
/// authority that refuses to open an attempt on a Blocked node.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct StageNodeDetail {
    pub state: StageNodeState,
    /// Attempts ever opened for this node, whatever their state.
    pub attempts_used: i64,
    pub max_total_attempts: i64,
    pub last_failure: Option<StageLastFailure>,
    pub block_reason: Option<StageBlockReason>,
    /// The Worker Site of this node's most recent Attempt: which machine is
    /// running the cell now, or which one ran it. `None` is an Attempt no Site
    /// claimed -- the single-machine path -- or a node with no Attempt yet.
    /// This is provenance a reader needs and no part of any identity.
    pub site_id: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct StagePlanInstanceInput {
    pub input_key: String,
    pub path: String,
    pub sha256: String,
    pub byte_count: i64,
    pub role: String,
    pub schema_id: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct StagePlanInstanceDeclaration {
    pub cell_ordinal: i64,
    pub compiled_cell_subject_digest: String,
    pub target_hash: String,
    pub config_hash: String,
    pub stage_plan: StagePlanV1,
    pub storage_site_id: String,
    /// Empty for compiled-cell instances. Set for one DAG instance per paired unit.
    pub paired_unit_id: String,
    pub inputs: Vec<StagePlanInstanceInput>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RegisteredStagePlanInstance {
    pub stage_plan_instance_id: String,
    pub cell_ordinal: i64,
    pub compiled_cell_subject_digest: String,
    pub target_hash: String,
    pub config_hash: String,
    pub stage_plan_digest: String,
    pub training_worker_request_digest: String,
    pub paired_unit_id: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ReadyStage {
    pub stage_plan_instance_id: String,
    pub stage_id: String,
    pub stage_plan_digest: String,
}

/// Ownership identity of one Stage Plan instance. Not an execution key.
pub fn stage_plan_instance_id(
    compiled_cell_subject_digest: &str,
    cell_ordinal: i64,
    target_hash: &str,
    config_hash: &str,
    paired_unit_id: &str,
) -> LifecycleResult<String> {
    let material = if paired_unit_id.is_empty() {
        serde_json::json!({
            "compiled_cell_subject_digest": compiled_cell_subject_digest,
            "cell_ordinal": cell_ordinal,
            "target_hash": target_hash,
            "config_hash": config_hash,
        })
    } else {
        serde_json::json!({
            "compiled_cell_subject_digest": compiled_cell_subject_digest,
            "cell_ordinal": cell_ordinal,
            "target_hash": target_hash,
            "config_hash": config_hash,
            "paired_unit_id": paired_unit_id,
        })
    };
    let bytes = fern_json_contract::canonical_json_bytes(&material)
        .map_err(|error| LifecycleError::InvalidRequest(error.to_string()))?;
    Ok(format!("sha256:{}", sha256_hex(&bytes)))
}

pub fn stage_plan_set_digest(instances: &[RegisteredStagePlanInstance]) -> LifecycleResult<String> {
    let mut ordered = instances.to_vec();
    ordered.sort_by_key(|instance| instance.cell_ordinal);
    let material = serde_json::json!({
        "schema": "fern.stage_plan_set.v1",
        "instances": ordered.iter().map(|instance| {
            let mut entry = serde_json::json!({
                "stage_plan_instance_id": instance.stage_plan_instance_id,
                "cell_ordinal": instance.cell_ordinal,
                "compiled_cell_subject_digest": instance.compiled_cell_subject_digest,
                "target_hash": instance.target_hash,
                "config_hash": instance.config_hash,
                "stage_plan_digest": instance.stage_plan_digest,
                "training_worker_request_digest": instance.training_worker_request_digest,
            });
            if !instance.paired_unit_id.is_empty() {
                entry
                    .as_object_mut()
                    .expect("instance set entry is an object")
                    .insert(
                        "paired_unit_id".to_string(),
                        serde_json::Value::String(instance.paired_unit_id.clone()),
                    );
            }
            entry
        }).collect::<Vec<_>>(),
    });
    let bytes = fern_json_contract::canonical_json_bytes(&material)
        .map_err(|error| LifecycleError::InvalidRequest(error.to_string()))?;
    Ok(format!("sha256:{}", sha256_hex(&bytes)))
}

pub fn declared_instance_record(
    declaration: &StagePlanInstanceDeclaration,
) -> LifecycleResult<RegisteredStagePlanInstance> {
    validate_declaration_external_inputs(declaration)?;
    let stage_plan_digest = declaration
        .stage_plan
        .digest()
        .map_err(|error| LifecycleError::InvalidRequest(error.to_string()))?;
    Ok(RegisteredStagePlanInstance {
        stage_plan_instance_id: stage_plan_instance_id(
            &declaration.compiled_cell_subject_digest,
            declaration.cell_ordinal,
            &declaration.target_hash,
            &declaration.config_hash,
            &declaration.paired_unit_id,
        )?,
        cell_ordinal: declaration.cell_ordinal,
        compiled_cell_subject_digest: declaration.compiled_cell_subject_digest.clone(),
        target_hash: declaration.target_hash.clone(),
        config_hash: declaration.config_hash.clone(),
        stage_plan_digest: stage_plan_digest.as_str().to_string(),
        training_worker_request_digest: instance_input_set_digest(&declaration.inputs)?,
        paired_unit_id: declaration.paired_unit_id.clone(),
    })
}

pub fn instance_input_set_digest(inputs: &[StagePlanInstanceInput]) -> LifecycleResult<String> {
    let mut seen = BTreeSet::new();
    for input in inputs {
        if input.input_key.trim().is_empty() {
            return Err(LifecycleError::InvalidRequest(
                "Stage Plan instance input_key must be a non-empty name".to_string(),
            ));
        }
        if !seen.insert(input.input_key.as_str()) {
            return Err(LifecycleError::InvalidRequest(format!(
                "duplicate Stage Plan instance input_key {}",
                input.input_key
            )));
        }
        if input.byte_count < 0 {
            return Err(LifecycleError::InvalidRequest(format!(
                "Stage Plan instance input {} has a negative byte_count",
                input.input_key
            )));
        }
    }
    if inputs.len() == 1 && inputs[0].input_key == "training_worker_request" {
        return Ok(prefixed_sha256(&inputs[0].sha256));
    }
    let mut ordered = inputs.to_vec();
    ordered.sort_by(|left, right| left.input_key.cmp(&right.input_key));
    let material = serde_json::json!({
        "schema": "fern.stage_plan_instance_inputs.v1",
        "inputs": ordered.iter().map(|input| serde_json::json!({
            "input_key": input.input_key,
            "sha256": prefixed_sha256(&input.sha256),
        })).collect::<Vec<_>>(),
    });
    let bytes = fern_json_contract::canonical_json_bytes(&material)
        .map_err(|error| LifecycleError::InvalidRequest(error.to_string()))?;
    Ok(format!("sha256:{}", sha256_hex(&bytes)))
}

fn prefixed_sha256(value: &str) -> String {
    if value.starts_with("sha256:") {
        value.to_string()
    } else {
        format!("sha256:{value}")
    }
}

fn validate_declaration_external_inputs(declaration: &StagePlanInstanceDeclaration) -> LifecycleResult<()> {
    let keys = declaration
        .inputs
        .iter()
        .map(|input| input.input_key.clone())
        .collect::<BTreeSet<_>>();
    let external_ports = declared_external_port_keys(&declaration.stage_plan);
    if keys != external_ports {
        return Err(LifecycleError::InvalidRequest(format!(
            "Stage Plan instance inputs must match declared external ports exactly; expected {external_ports:?}, got {keys:?}"
        )));
    }
    for stage in &declaration.stage_plan.stages {
        let Some(parameters) = stage.parameters.as_object() else {
            continue;
        };
        for (port, spec) in &stage.inputs {
            if !matches!(spec.source, StageInputSourceV1::ExternalArtifact) {
                continue;
            }
            let Some(expected) = parameters
                .get(&format!("{port}_digest"))
                .and_then(serde_json::Value::as_str)
            else {
                continue;
            };
            let key = qualified_external_input_key(&stage.id, port);
            let input = declaration
                .inputs
                .iter()
                .find(|item| item.input_key == key)
                .ok_or_else(|| {
                    LifecycleError::InvalidRequest(format!(
                        "Stage Plan instance is missing checked bytes for declared external port {key}"
                    ))
                })?;
            if prefixed_sha256(&input.sha256) != prefixed_sha256(expected) {
                return Err(LifecycleError::InvalidRequest(format!(
                    "declared external port {key} digest does not match the Stage Plan bound digest"
                )));
            }
        }
    }
    Ok(())
}

fn declared_external_port_keys(stage_plan: &StagePlanV1) -> BTreeSet<String> {
    let mut keys = BTreeSet::new();
    for stage in &stage_plan.stages {
        for (port, spec) in &stage.inputs {
            if matches!(spec.source, StageInputSourceV1::ExternalArtifact) {
                keys.insert(qualified_external_input_key(&stage.id, port));
            }
        }
    }
    keys
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ReconcileStageAttemptsOutcome {
    pub lost_attempt_ids: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CheckedProducerOutputSource {
    pub reported_artifact_json: String,
    pub relative_paths: Vec<String>,
}

/// One V2 evaluation cell's immutable checkpoint producer identity. The
/// consumer names bytes and their producer, never a mutable filesystem path.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct TrainedCheckpointSource {
    pub artifact_digest: String,
    pub producer_stage_attempt_id: String,
    pub training_config_digest: String,
    pub implementation_bundle_digest: String,
}

/// Registry evidence selected for exactly one requested checkpoint source.
/// `producer_definition_json` and `checked_receipt_json` remain raw because
/// their owning Rust contracts validate their full wire shape after this
/// immediate Registry transaction has selected the durable bytes.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct TrainedCheckpointEvidence {
    pub artifact_digest: String,
    pub producer_stage_attempt_id: String,
    pub target_hash: String,
    pub config_hash: String,
    pub producer_definition_json: String,
    pub checked_receipt_json: String,
    pub relative_paths: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct TrainedCheckpointPathReceipt {
    pub schema: String,
    pub checkpoints: Vec<TrainedCheckpointEvidence>,
}

/// Resolve every trained-checkpoint source through one typed Registry child.
/// The child owns the SQLite snapshot and current site-local locations; this
/// caller owns only the typed request and later pure contract decoding.
pub fn resolve_trained_checkpoint_paths(
    registry: &Path,
    storage_site_id: &str,
    sources: Vec<TrainedCheckpointSource>,
) -> LifecycleResult<TrainedCheckpointPathReceipt> {
    #[derive(Serialize)]
    struct Request {
        schema: &'static str,
        storage_site_id: String,
        sources: Vec<TrainedCheckpointSource>,
    }

    crate::csharp_registry::run_json(
        "resolve-trained-checkpoint-paths",
        registry,
        &Request {
            schema: "fern.trained_checkpoint_paths.request.v1",
            storage_site_id: storage_site_id.to_string(),
            sources,
        },
    )
    .map_err(LifecycleError::InvalidRequest)
}

/// Which Worker Site holds one Attempt, when a Site claimed it at all.
pub fn stage_attempt_site(registry: &Path, stage_attempt_id: &str) -> LifecycleResult<Option<String>> {
    crate::csharp_registry::run_optional_json(
        "stage-attempt-site",
        registry,
        &serde_json::json!({ "stage_attempt_id": stage_attempt_id }),
    )
    .map_err(LifecycleError::InvalidRequest)
}

pub fn project_stage_node(
    registry: &Path,
    plan_id: &str,
    stage_plan_instance_id: &str,
    stage_id: &str,
) -> LifecycleResult<StageNodeState> {
    Ok(project_stage_node_detail(registry, plan_id, stage_plan_instance_id, stage_id)?.state)
}

/// Derive one node's state from its Attempt history, its budget and its predecessors.
///
/// Order matters and is the contract:
/// 1. a closed, checked Attempt satisfies the node for good;
/// 2. an active Attempt means Running;
/// 3. the latest terminal Attempt decides whether an automatic path remains —
///    `Never`/`RequiresHuman`/`OtherProfile` block, `SameProfile` and the
///    receipt-less `lost`/`expired` states retry while the budget lasts;
/// 4. only then do predecessors matter: a Blocked one blocks, an unfinished one
///    means Waiting, and otherwise the node is Ready.
pub fn project_stage_node_detail(
    registry: &Path,
    plan_id: &str,
    stage_plan_instance_id: &str,
    stage_id: &str,
) -> LifecycleResult<StageNodeDetail> {
    crate::csharp_registry::run_json(
        "project-stage-node-detail",
        registry,
        &serde_json::json!({
            "plan_id": plan_id,
            "stage_plan_instance_id": stage_plan_instance_id,
            "stage_id": stage_id,
        }),
    )
    .map_err(LifecycleError::InvalidRequest)
}

/// Reopen the registered Stage Plan bytes and prove they still hash to the stored digest.
pub fn load_registered_stage_plan(
    registry: &Path,
    plan_id: &str,
    stage_plan_instance_id: &str,
    expected_digest: &str,
) -> LifecycleResult<StagePlanV1> {
    let bytes = crate::csharp_registry::run_bytes(
        "load-registered-stage-plan",
        registry,
        &serde_json::json!({
            "plan_id": plan_id,
            "stage_plan_instance_id": stage_plan_instance_id,
            "expected_digest": expected_digest,
        }),
    )
    .map_err(LifecycleError::InvalidRequest)?;
    let plan = StagePlanV1::parse_json(bytes.trim_ascii())
        .map_err(|error| LifecycleError::InvalidRequest(error.to_string()))?;
    let observed = plan
        .digest()
        .map_err(|error| LifecycleError::InvalidRequest(error.to_string()))?;
    if observed.as_str() != expected_digest {
        return Err(LifecycleError::InvalidRequest(format!(
            "stored Stage Plan bytes for {plan_id} instance {stage_plan_instance_id} no longer hash to {expected_digest}"
        )));
    }
    Ok(plan)
}

pub fn registered_stage_plan_digest(
    registry: &Path,
    plan_id: &str,
    stage_plan_instance_id: &str,
) -> LifecycleResult<Option<String>> {
    crate::csharp_registry::run_optional_json(
        "registered-stage-plan-digest",
        registry,
        &serde_json::json!({
            "plan_id": plan_id,
            "stage_plan_instance_id": stage_plan_instance_id,
        }),
    )
    .map_err(LifecycleError::InvalidRequest)
}

pub fn training_worker_request_digest(
    registry: &Path,
    plan_id: &str,
    stage_plan_instance_id: &str,
) -> LifecycleResult<Option<String>> {
    crate::csharp_registry::run_optional_json(
        "training-worker-request-digest",
        registry,
        &serde_json::json!({
            "plan_id": plan_id,
            "stage_plan_instance_id": stage_plan_instance_id,
        }),
    )
    .map_err(LifecycleError::InvalidRequest)
}

pub fn stage_plan_instance_input_location(
    registry: &Path,
    plan_id: &str,
    stage_plan_instance_id: &str,
    input_key: &str,
) -> LifecycleResult<Option<(String, String)>> {
    Ok(
        stage_plan_instance_input(registry, plan_id, stage_plan_instance_id, input_key)?
            .map(|input| (input.sha256, input.path)),
    )
}

pub fn stage_plan_instance_input(
    registry: &Path,
    plan_id: &str,
    stage_plan_instance_id: &str,
    input_key: &str,
) -> LifecycleResult<Option<StagePlanInstanceInput>> {
    crate::csharp_registry::run_optional_json(
        "load-stage-plan-instance-input",
        registry,
        &serde_json::json!({
            "plan_id": plan_id,
            "stage_plan_instance_id": stage_plan_instance_id,
            "input_key": input_key,
        }),
    )
    .map_err(LifecycleError::InvalidRequest)
}

pub fn checked_producer_output_source(
    registry: &Path,
    plan_id: &str,
    stage_plan_instance_id: &str,
    producer_stage_id: &str,
    output_port: &str,
) -> LifecycleResult<Option<CheckedProducerOutputSource>> {
    crate::csharp_registry::run_optional_json(
        "load-checked-producer-output-source",
        registry,
        &serde_json::json!({
            "plan_id": plan_id,
            "stage_plan_instance_id": stage_plan_instance_id,
            "producer_stage_id": producer_stage_id,
            "output_port": output_port,
        }),
    )
    .map_err(LifecycleError::InvalidRequest)
}

/// First Ready Stage across instances. A blocked branch does not hide a Ready
/// sibling in the same instance, and a blocked instance does not hide a later
/// Ready cell. One Running Stage on the Plan still occupies the only worker slot.
pub fn next_ready_stage(registry: &Path, plan_id: &str) -> LifecycleResult<Option<ReadyStage>> {
    crate::csharp_registry::run_optional_json("next-ready-stage", registry, &serde_json::json!({ "plan_id": plan_id }))
        .map_err(LifecycleError::InvalidRequest)
}

pub fn stage_plan_is_complete(registry: &Path, plan_id: &str) -> LifecycleResult<bool> {
    crate::csharp_registry::run_json(
        "stage-plan-is-complete",
        registry,
        &serde_json::json!({ "plan_id": plan_id }),
    )
    .map_err(LifecycleError::InvalidRequest)
}

pub fn stage_plan_has_blocked_instance(registry: &Path, plan_id: &str) -> LifecycleResult<bool> {
    crate::csharp_registry::run_json(
        "stage-plan-has-blocked-instance",
        registry,
        &serde_json::json!({ "plan_id": plan_id }),
    )
    .map_err(LifecycleError::InvalidRequest)
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct StageNodeProjection {
    pub ordinal: i64,
    pub stage_plan_instance_id: String,
    pub cell_ordinal: i64,
    pub target_hash: String,
    pub paired_unit_id: String,
    pub stage_id: String,
    pub state: StageNodeState,
    pub attempts_used: i64,
    pub max_total_attempts: i64,
    pub last_failure: Option<StageLastFailure>,
    pub block_reason: Option<StageBlockReason>,
    /// Which machine this cell's Stage is on, from its newest Attempt. `None`
    /// until a Worker Site claims one.
    pub site_id: Option<String>,
}

pub fn project_stage_plan_nodes(registry: &Path, plan_id: &str) -> LifecycleResult<Vec<StageNodeProjection>> {
    crate::csharp_registry::run_json(
        "project-stage-plan-nodes",
        registry,
        &serde_json::json!({ "plan_id": plan_id }),
    )
    .map_err(LifecycleError::InvalidRequest)
}

#[cfg(test)]
mod tests {
    use super::{TrainedCheckpointSource, resolve_trained_checkpoint_paths};

    #[test]
    fn trained_checkpoint_request_crosses_the_real_csharp_child_and_refuses_conflicting_identity_without_sql_mutation()
    {
        let home = tempfile::tempdir().expect("create temporary Registry Home");
        let registry = home.path().join("lifecycle.db");
        crate::csharp_registry::migrate_test_registry_via_home(&registry);
        let digest = format!("sha256:{}", "a".repeat(64));
        let config = format!("sha256:{}", "b".repeat(64));
        let bundle = format!("sha256:{}", "c".repeat(64));

        let refusal = resolve_trained_checkpoint_paths(
            &registry,
            "test-site",
            vec![
                TrainedCheckpointSource {
                    artifact_digest: digest.clone(),
                    producer_stage_attempt_id: "producer-one".to_string(),
                    training_config_digest: config.clone(),
                    implementation_bundle_digest: bundle.clone(),
                },
                TrainedCheckpointSource {
                    artifact_digest: digest,
                    producer_stage_attempt_id: "producer-two".to_string(),
                    training_config_digest: config,
                    implementation_bundle_digest: bundle,
                },
            ],
        )
        .expect_err("the typed C# Registry child must refuse conflicting checkpoint producers");
        assert!(
            refusal
                .to_string()
                .contains("one checkpoint artifact digest was assigned conflicting producer identities"),
            "{refusal}"
        );
    }
}
