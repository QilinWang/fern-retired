//! Durable authority for a declared, ordered Fern run plan.
//!
//! A run plan says only *which Matrix comes next*.  It never owns a GPU and
//! never reimplements a Matrix, Coordinate, or worker lease.  A plan step may
//! advance only after Fern reopens the exact Matrix artifacts and verifies that
//! every declared Coordinate is accepted.  This replaces a shell loop that
//! watched a `queue.status` file and guessed when to start another model.

use std::collections::BTreeSet;
use std::path::Path;

use serde::{Deserialize, Serialize};

use crate::binary_deployment::BinaryVersionIdentity;
use crate::lifecycle::{LifecycleActor, LifecycleError, LifecycleResult, sha256_hex};

pub const RUN_PLAN_SCHEMA: &str = "fern.run_plan.v1";
pub const RUN_PLAN_SCHEMA_V2: &str = "fern.run_plan.v2";
pub const RUN_PLAN_RECIPE_SNAPSHOT_SCHEMA: &str = "fern.run_plan.recipe_snapshot.v1";
/// The exact recipe identity resolved before a Plan enters Registry Home.
///
/// This is the complete data-only recipe identity, not a path to an authoring
/// file. Existing v1 Plans predate the field and remain readable with `None`;
/// every newly resolved CLI Plan records `Some`, and dispatch reconstructs the
/// executable recipe from this frozen value without reopening a declaration or
/// consulting the mutable compiled catalog.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RunPlanRecipeSnapshotV1 {
    pub schema: String,
    pub recipe_hash: String,
    pub identity: serde_json::Value,
}

impl RunPlanRecipeSnapshotV1 {
    pub fn validate(&self) -> LifecycleResult<()> {
        if self.schema != RUN_PLAN_RECIPE_SNAPSHOT_SCHEMA {
            return Err(LifecycleError::InvalidRequest(format!(
                "run-plan recipe snapshot declares schema {:?}, expected {RUN_PLAN_RECIPE_SNAPSHOT_SCHEMA:?}",
                self.schema
            )));
        }
        if self.recipe_hash.len() != 16
            || !self
                .recipe_hash
                .bytes()
                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        {
            return Err(LifecycleError::InvalidRequest(
                "run-plan recipe snapshot recipe_hash must be exactly 16 lowercase hexadecimal characters".to_string(),
            ));
        }
        if self.identity.get("schema").and_then(serde_json::Value::as_str) != Some("fern.recipe_blueprint.rust.v1") {
            return Err(LifecycleError::InvalidRequest(
                "run-plan recipe snapshot must contain a fern.recipe_blueprint.rust.v1 identity preimage".to_string(),
            ));
        }
        let full_digest = sha256_hex(&serde_json::to_vec(&self.identity)?);
        if self.recipe_hash != full_digest[..16] {
            return Err(LifecycleError::InvalidRequest(format!(
                "run-plan recipe snapshot hash {:?} disagrees with its frozen identity; expected {}",
                self.recipe_hash,
                &full_digest[..16]
            )));
        }
        Ok(())
    }
}

/// One named Matrix step in an ordered Fern run plan.
///
/// `preset` and `model` are CLI-boundary strings.  The CLI validates them
/// against the Rust catalog before it can construct the Matrix.  The durable
/// plan still records them verbatim so a resume cannot silently switch models.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RunPlanStepV1 {
    pub step_id: String,
    pub lane: String,
    pub preset: String,
    pub model: String,
    pub limit: usize,
    pub keep_forecast_origins: bool,
    /// Absent only on Plans registered before recipe snapshots existed.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub recipe_snapshot: Option<RunPlanRecipeSnapshotV1>,
    /// Present only for a V2 Plan. This is the complete object produced by
    /// `plan-experiment`; dispatch never reopens authoring cards or presets.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub compiled_experiment: Option<crate::experiment_v2::CompiledExperimentV2>,
}

/// One exact upstream Matrix that must be fully accepted before this plan may
/// launch its first child Matrix. The id is a Registry-Home identity, never a
/// lane path, status file, or process name.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RunPlanMatrixDependencyV1 {
    pub matrix_id: String,
}

/// The immutable intent for one ordered set of Matrix steps.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RunPlanDefinitionV1 {
    pub schema: String,
    pub plan_name: String,
    /// Complete recoverable source provenance for every Matrix in this Plan.
    /// Absent only on plans registered before implementation snapshots were
    /// wired, or on non-experiment legacy/manual routes.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub implementation_source_snapshot_id: Option<String>,
    /// Present only when a V2 Plan is the two-arm execution of one checked
    /// ablation. Keeping the pair at the Plan root prevents registration from
    /// turning its control and treatment into unrelated Experiments.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub compiled_ablation: Option<crate::experiment_v2::CompiledAblationV2>,
    /// Stage-driven V2 Plan. Empty Matrix steps. The compiled Experiment is the
    /// scientific subject; each cell is one Stage Plan instance.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub compiled_experiment: Option<crate::experiment_v2::CompiledExperimentV2>,
    /// Canonical digest of the ordered Stage Plan instance collection.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub stage_plan_set_digest: Option<String>,
    /// Stored 9B one-cell Plans only. New registration writes `stage_plan_set_digest`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub stage_plan_digest: Option<String>,
    /// Stored 9B one-cell Plans only.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub training_worker_request_digest: Option<String>,
    /// V2 source composition. Absent on Plans registered before source-content CAS.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub implementation_id: Option<String>,
    /// Research Design binding for that composition. Does not change implementation bytes.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub implementation_binding_id: Option<String>,
    /// Owner-approved reason for materializing this Plan's bound source below
    /// the deployment storage floor. This never changes the global floor and
    /// applies only to the immutable source named by this Plan.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub owner_storage_override_reason: Option<String>,
    /// Missing from an older persisted definition means it declared no external
    /// Matrix prerequisite. A new dependency changes the definition digest and
    /// therefore creates a distinct plan identity.
    #[serde(default)]
    pub dependencies: Vec<RunPlanMatrixDependencyV1>,
    pub steps: Vec<RunPlanStepV1>,
}

impl RunPlanDefinitionV1 {
    /// Whether this Plan's work is Stage Plan instances rather than Matrix steps.
    ///
    /// Either digest field answers it. `stage_plan_set_digest` names the ordered
    /// instance collection every `run-experiment` registration writes;
    /// `stage_plan_digest` is the stored 9B one-cell form. Validation and
    /// dispatch must ask the SAME question, so they ask it here: dispatch once
    /// branched on the one-cell field alone and sent every set-based Plan into
    /// the Matrix-step preflight, which refuses an empty `steps` with "run plan
    /// has no steps".
    pub fn is_stage_driven(&self) -> bool {
        self.stage_plan_set_digest.is_some() || self.stage_plan_digest.is_some()
    }

    /// Validate a self-contained plan before it may create a lease or worker.
    pub fn validate(&self) -> LifecycleResult<()> {
        if self.schema != RUN_PLAN_SCHEMA && self.schema != RUN_PLAN_SCHEMA_V2 {
            return Err(LifecycleError::InvalidRequest(format!(
                "run plan {:?} declares schema {:?}, but this runtime accepts only {RUN_PLAN_SCHEMA:?} or {RUN_PLAN_SCHEMA_V2:?}; regenerate the definition with the current planner before registering it",
                self.plan_name, self.schema,
            )));
        }
        let is_v2 = self.schema == RUN_PLAN_SCHEMA_V2;
        if self
            .owner_storage_override_reason
            .as_deref()
            .is_some_and(|reason| reason.trim().is_empty())
        {
            return Err(LifecycleError::InvalidRequest(
                "run-plan owner_storage_override_reason must be non-empty when present".to_string(),
            ));
        }
        if !is_v2 && self.compiled_ablation.is_some() {
            return Err(LifecycleError::InvalidRequest(format!(
                "V1 run plan {:?} contains a compiled V2 Ablation; use schema {RUN_PLAN_SCHEMA_V2:?}",
                self.plan_name,
            )));
        }
        let stage_driven = self.is_stage_driven();
        if is_v2 && stage_driven {
            if !self.steps.is_empty() {
                return Err(LifecycleError::InvalidRequest(format!(
                    "V2 run plan {:?} is Stage-driven so it must not declare Matrix steps",
                    self.plan_name,
                )));
            }
            // Exactly one source authority. "Neither" is a Plan that cannot say
            // which program produced its numbers; "both" is two answers to that
            // question with nothing deciding between them, which is the whole
            // reason the Binding exists. Plans registered before Bindings still
            // carry a snapshot and stay dispatchable under the first arm.
            match (
                self.implementation_binding_id.as_deref(),
                self.implementation_source_snapshot_id.as_deref(),
            ) {
                (None, Some(_)) | (Some(_), None) => {}
                (Some(binding_id), Some(snapshot_id)) => {
                    return Err(LifecycleError::InvalidRequest(format!(
                        "V2 Stage-driven run plan {:?} names two source authorities, binding {binding_id} and snapshot {snapshot_id}; register it against the Binding alone",
                        self.plan_name,
                    )));
                }
                (None, None) => {
                    return Err(LifecycleError::InvalidRequest(format!(
                        "V2 Stage-driven run plan {:?} names no implementation source; register it with run-experiment so it carries the Binding its compiled object was planned against",
                        self.plan_name,
                    )));
                }
            }
            match (self.compiled_experiment.as_ref(), self.compiled_ablation.as_ref()) {
                (Some(_), Some(_)) => {
                    return Err(LifecycleError::InvalidRequest(format!(
                        "V2 Stage-driven run plan {:?} cannot carry both a compiled Experiment and a compiled Ablation",
                        self.plan_name,
                    )));
                }
                (Some(compiled), None) => {
                    compiled.validate().map_err(LifecycleError::InvalidRequest)?;
                    if compiled.cells().is_empty() {
                        return Err(LifecycleError::InvalidRequest(format!(
                            "V2 Stage-driven run plan {:?} has a compiled Experiment with no cells",
                            self.plan_name,
                        )));
                    }
                    if self.stage_plan_set_digest.is_none()
                        && (compiled.cells().len() != 1
                            || self
                                .training_worker_request_digest
                                .as_ref()
                                .is_none_or(|digest| digest.trim().is_empty()))
                    {
                        return Err(LifecycleError::InvalidRequest(format!(
                            "stored one-cell Stage Plan {:?} is missing its training_worker_request digest",
                            self.plan_name,
                        )));
                    }
                }
                (None, Some(ablation)) => {
                    ablation.validate().map_err(LifecycleError::InvalidRequest)?;
                    if ablation.control().cells().is_empty() || ablation.treatment().cells().is_empty() {
                        return Err(LifecycleError::InvalidRequest(format!(
                            "V2 Stage-driven run plan {:?} has a compiled Ablation with an empty arm",
                            self.plan_name,
                        )));
                    }
                    if self
                        .stage_plan_set_digest
                        .as_ref()
                        .is_none_or(|digest| digest.trim().is_empty())
                    {
                        return Err(LifecycleError::InvalidRequest(format!(
                            "V2 Stage-driven paired ablation {:?} must name a non-empty stage_plan_set_digest",
                            self.plan_name,
                        )));
                    }
                }
                (None, None) => {
                    return Err(LifecycleError::InvalidRequest(format!(
                        "V2 Stage-driven run plan {:?} must carry a compiled Experiment or a compiled Ablation",
                        self.plan_name,
                    )));
                }
            }
            if let Some(set_digest) = self.stage_plan_set_digest.as_ref() {
                if set_digest.trim().is_empty() {
                    return Err(LifecycleError::InvalidRequest(format!(
                        "V2 Stage-driven run plan {:?} must name a non-empty stage_plan_set_digest",
                        self.plan_name,
                    )));
                }
            }
        } else if self.compiled_experiment.is_some() || self.training_worker_request_digest.is_some() {
            return Err(LifecycleError::InvalidRequest(format!(
                "run plan {:?} carries Stage-driven fields without a Stage Plan set digest",
                self.plan_name,
            )));
        } else {
            let expected_v2_steps = usize::from(is_v2) + usize::from(self.compiled_ablation.is_some());
            if is_v2 && self.steps.len() != expected_v2_steps {
                return Err(LifecycleError::InvalidRequest(format!(
                    "V2 run plan {:?} requires exactly {expected_v2_steps} compiled Experiment step(s) for its declared object, got {}",
                    self.plan_name,
                    self.steps.len(),
                )));
            }
        }
        match (
            self.implementation_id.as_deref(),
            self.implementation_binding_id.as_deref(),
        ) {
            (None, None) => {}
            (Some(implementation_id), Some(binding_id)) => {
                if !implementation_id.starts_with("implementation:")
                    || implementation_id.len() != "implementation:".len() + 64
                {
                    return Err(LifecycleError::InvalidRequest(format!(
                        "run plan {:?} implementation_id is not a full implementation:<sha256> identity",
                        self.plan_name,
                    )));
                }
                if !binding_id.starts_with("implementation-binding:")
                    || binding_id.len() != "implementation-binding:".len() + 64
                {
                    return Err(LifecycleError::InvalidRequest(format!(
                        "run plan {:?} implementation_binding_id is not a full implementation-binding:<sha256> identity",
                        self.plan_name,
                    )));
                }
            }
            _ => {
                return Err(LifecycleError::InvalidRequest(format!(
                    "run plan {:?} must name both implementation_id and implementation_binding_id, or neither",
                    self.plan_name,
                )));
            }
        }
        if self.plan_name.trim().is_empty() {
            return Err(LifecycleError::InvalidRequest(
                "run plan definition has an empty plan_name; give it one stable human name before registering it"
                    .to_string(),
            ));
        }
        // Empty Matrix steps are allowed: that plan is driven by a registered
        // Stage Plan. Dispatch refuses until stage_plans holds bytes. The
        // scheduler still must not parse scientific handlers from those bytes.
        if let Some(snapshot_id) = &self.implementation_source_snapshot_id {
            let digest = snapshot_id
                .strip_prefix("implementation-source-snapshot:")
                .unwrap_or_default();
            if digest.len() != 64
                || !digest
                    .bytes()
                    .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
            {
                return Err(LifecycleError::InvalidRequest(
                    "implementation_source_snapshot_id must be a full implementation-source-snapshot:<sha256> identity"
                        .to_string(),
                ));
            }
        }
        let mut dependency_ids = std::collections::BTreeSet::new();
        for dependency in &self.dependencies {
            if dependency.matrix_id.trim().is_empty() {
                return Err(LifecycleError::InvalidRequest(format!(
                    "run plan {:?} has an empty upstream Matrix dependency; name the exact matrix_id or remove the dependency",
                    self.plan_name,
                )));
            }
            if !dependency_ids.insert(dependency.matrix_id.as_str()) {
                return Err(LifecycleError::InvalidRequest(format!(
                    "run plan {:?} repeats upstream Matrix {:?}; list each prerequisite once so it has one terminal gate",
                    self.plan_name, dependency.matrix_id,
                )));
            }
        }
        let mut ids = std::collections::BTreeSet::new();
        for (ordinal, step) in self.steps.iter().enumerate() {
            let missing = [
                ("step_id", &step.step_id),
                ("lane", &step.lane),
                ("preset", &step.preset),
                ("model", &step.model),
            ]
            .into_iter()
            .filter_map(|(field, value)| value.trim().is_empty().then_some(field))
            .collect::<Vec<_>>();
            if !missing.is_empty() {
                return Err(LifecycleError::InvalidRequest(format!(
                    "run plan {:?} step at ordinal {ordinal} is missing {}; every declared Matrix step needs step_id, lane, preset, and model. Add the missing field(s) before registration",
                    self.plan_name,
                    missing.join(", "),
                )));
            }
            if step.limit == 0 {
                return Err(LifecycleError::InvalidRequest(format!(
                    "run plan {:?} step {:?} has limit 0, so it declares no Coordinates; set a positive limit or remove the step before registration",
                    self.plan_name, step.step_id,
                )));
            }
            if let Some(snapshot) = &step.recipe_snapshot {
                snapshot.validate()?;
            }
            match (&step.compiled_experiment, is_v2) {
                (None, false) => {}
                (Some(_), false) => {
                    return Err(LifecycleError::InvalidRequest(format!(
                        "V1 run plan {:?} step {:?} contains a compiled V2 Experiment; use schema {RUN_PLAN_SCHEMA_V2:?}",
                        self.plan_name, step.step_id,
                    )));
                }
                (None, true) => {
                    return Err(LifecycleError::InvalidRequest(format!(
                        "V2 run plan {:?} step {:?} has no compiled Experiment",
                        self.plan_name, step.step_id,
                    )));
                }
                (Some(compiled), true) => {
                    if step.recipe_snapshot.is_some() {
                        return Err(LifecycleError::InvalidRequest(format!(
                            "V2 run plan {:?} step {:?} cannot carry both a compiled Experiment and a V1 recipe snapshot",
                            self.plan_name, step.step_id,
                        )));
                    }
                    compiled.validate().map_err(LifecycleError::InvalidRequest)?;
                    if step.preset != compiled.manifest_sha256() {
                        return Err(LifecycleError::InvalidRequest(format!(
                            "V2 run plan {:?} step {:?} preset field must carry the compiled manifest identity {:?}, got {:?}",
                            self.plan_name,
                            step.step_id,
                            compiled.manifest_sha256(),
                            step.preset,
                        )));
                    }
                    if step.model != compiled.model_type() {
                        return Err(LifecycleError::InvalidRequest(format!(
                            "V2 run plan {:?} step {:?} declares model {:?}, but its compiled Experiment declares {:?}",
                            self.plan_name,
                            step.step_id,
                            step.model,
                            compiled.model_type(),
                        )));
                    }
                    if step.limit != compiled.cells().len() {
                        return Err(LifecycleError::InvalidRequest(format!(
                            "V2 run plan {:?} step {:?} declares {} Coordinates, but its compiled Experiment contains {}",
                            self.plan_name,
                            step.step_id,
                            step.limit,
                            compiled.cells().len(),
                        )));
                    }
                }
            }
            if !ids.insert(step.step_id.as_str()) {
                return Err(LifecycleError::InvalidRequest(format!(
                    "run plan {:?} repeats step_id {:?}; give each ordered Matrix step one unique ID before registration",
                    self.plan_name, step.step_id,
                )));
            }
        }
        if let Some(ablation) = &self.compiled_ablation {
            ablation.validate().map_err(LifecycleError::InvalidRequest)?;
            if stage_driven {
                if !self.steps.is_empty() {
                    return Err(LifecycleError::InvalidRequest(format!(
                        "V2 Stage-driven paired ablation {:?} must not declare Matrix steps; stored control/treatment steps remain historical reads only",
                        self.plan_name,
                    )));
                }
            } else {
                let expected = [ablation.control(), ablation.treatment()];
                let expected_ids = ["control", "treatment"];
                for ((step, compiled), expected_id) in self.steps.iter().zip(expected).zip(expected_ids) {
                    if step.step_id != expected_id || step.compiled_experiment.as_ref() != Some(compiled) {
                        return Err(LifecycleError::InvalidRequest(format!(
                            "V2 ablation Plan must bind step {expected_id:?} to the pair's exact {expected_id} compiled Experiment"
                        )));
                    }
                }
            }
        }
        Ok(())
    }

    /// Full content identity; a changed step list is a different plan.
    pub fn definition_digest(&self) -> LifecycleResult<String> {
        self.validate()?;
        Ok(sha256_hex(&serde_json::to_vec(self)?))
    }
}

/// The read-only status of one declared upstream Matrix prerequisite.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RunPlanDependencyProjection {
    pub matrix_id: String,
    pub state: String,
    pub detail: String,
}

/// The one authoritative answer a plan supervisor needs before it starts a
/// child Matrix. A waiting dependency is normal; an unregistered Matrix or an
/// invalid signed artifact is terminal and must not be polled forever.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RunPlanDependencyGate {
    Ready,
    Waiting(Vec<RunPlanDependencyProjection>),
    Refused(Vec<RunPlanDependencyProjection>),
}

/// Classify all declared upstream Matrix prerequisites for one plan.
pub fn evaluate_run_plan_dependencies(registry: &Path, plan_id: &str) -> LifecycleResult<RunPlanDependencyGate> {
    let projections = run_plan_projection(registry, plan_id)?.dependencies;
    let refused = projections
        .iter()
        .filter(|projection| projection.state == "unregistered" || projection.state == "invalid")
        .cloned()
        .collect::<Vec<_>>();
    if !refused.is_empty() {
        return Ok(RunPlanDependencyGate::Refused(refused));
    }
    let waiting = projections
        .iter()
        .filter(|projection| projection.state == "waiting")
        .cloned()
        .collect::<Vec<_>>();
    if waiting.is_empty() {
        Ok(RunPlanDependencyGate::Ready)
    } else {
        Ok(RunPlanDependencyGate::Waiting(waiting))
    }
}

/// The only request allowed to claim an ordered run plan.
#[derive(Debug, Clone)]
pub struct StartRunPlanRequest {
    pub definition: RunPlanDefinitionV1,
    pub execution_profile_id: String,
    pub execution_profile_revision: String,
    pub supervisor_binary: BinaryVersionIdentity,
    pub actor: LifecycleActor,
    pub lease_seconds: i64,
}

/// An explicit operator stop for the ordered plan coordinator.
///
/// A run plan owns only the next-step decision. A child Matrix remains a
/// separate authority object, and the CLI cascades to `cancel-matrix` only
/// when this outcome names one current Matrix.
#[derive(Debug, Clone)]
pub struct CancelRunPlanRequest {
    pub plan_id: String,
    pub reason: String,
    pub actor: LifecycleActor,
    pub idempotency_key: String,
    pub policy_revision: String,
}

/// The replay-safe outcome of cancelling an ordered plan.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum CancelRunPlanOutcome {
    AlreadyInactive {
        plan_id: String,
    },
    Cancelled {
        plan_id: String,
        plan_attempt_id: String,
        plan_lease_id: String,
        current_matrix_id: Option<String>,
        transition_receipt_id: String,
    },
}

/// Private capability for the one plan coordinator that may move to the next
/// step.  It is intentionally separate from every Matrix/worker capability.
#[derive(Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RunPlanPermit {
    pub plan_id: String,
    pub plan_attempt_id: String,
    pub plan_lease_id: String,
    pub fencing_epoch: i64,
    pub capability: String,
}

impl std::fmt::Debug for RunPlanPermit {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("RunPlanPermit")
            .field("plan_id", &self.plan_id)
            .field("plan_attempt_id", &self.plan_attempt_id)
            .field("plan_lease_id", &self.plan_lease_id)
            .field("fencing_epoch", &self.fencing_epoch)
            .field("capability", &"[redacted]")
            .finish()
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum StartRunPlanOutcome {
    Started {
        permit: RunPlanPermit,
        supervisor_binary: BinaryVersionIdentity,
    },
    AlreadyRunning {
        plan_id: String,
        plan_attempt_id: String,
        lease_expires_at_ms: i64,
    },
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StartRunPlanDecision {
    pub outcome: StartRunPlanOutcome,
    /// Only `Started` grants a fresh right to spawn the native plan supervisor.
    pub may_supervise: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum RunPlanStepAction {
    Start,
    Resume,
    AlreadySatisfied,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RunPlanStepProjection {
    pub ordinal: i64,
    pub step_id: String,
    pub lane: String,
    pub preset: String,
    pub model: String,
    pub matrix_id: Option<String>,
    pub state: String,
    pub failure_detail: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RunPlanProjection {
    pub plan_id: String,
    pub plan_name: String,
    pub execution_profile_id: String,
    pub execution_profile_revision: String,
    pub current_attempt_id: Option<String>,
    pub current_lease_expires_at_ms: Option<i64>,
    /// Last durable plan Attempt fact, including an explicit operator stop.
    pub latest_attempt_state: Option<String>,
    pub latest_attempt_detail: Option<String>,
    pub dependencies: Vec<RunPlanDependencyProjection>,
    pub steps: Vec<RunPlanStepProjection>,
    pub stage_nodes: Vec<crate::stage_attempt::StageNodeProjection>,
}

/// Register/replay one exact plan definition and atomically claim its current
/// coordinator lease.  The first caller may supervise; every concurrent caller
/// receives the durable current owner instead of a second spawn permit.
/// One derivation of a plan's durable identity, shared by registration and
/// start so a plan registered by one caller and dispatched by another is the
/// same object. The preimage is the immutable declaration: name, definition
/// digest, and the execution profile it was declared for.
pub fn run_plan_identity(
    schema: &str,
    plan_name: &str,
    definition_digest: &str,
    execution_profile_id: &str,
    execution_profile_revision: &str,
) -> String {
    sha256_hex(
        format!(
            "{schema}\x1f{plan_name}\x1f{definition_digest}\x1f{execution_profile_id}\x1f{execution_profile_revision}"
        )
        .as_bytes(),
    )
}

/// Register a plan without owning it (resident_scheduler step 2).
///
/// This is the submission half of the submission/ownership split: any agent
/// may call it, it writes the immutable plan and its `planned` steps, and it
/// returns holding NOTHING — no attempt, no lease, no supervisor. A
/// registered-but-undispatched plan is a row in a queue; authority over it
/// is a separate act (`start_run_plan` today, the elected scheduler's
/// dispatch tomorrow). The plan row itself, with created_by and
/// created_at_ms, is the durable record of the submission — the same record
/// a start-created plan leaves.
#[derive(Debug, Clone)]
pub struct RegisterRunPlanRequest {
    pub definition: RunPlanDefinitionV1,
    pub execution_profile_id: String,
    pub execution_profile_revision: String,
    pub actor: LifecycleActor,
}

impl RegisterRunPlanRequest {
    fn validate(&self) -> LifecycleResult<()> {
        self.definition.validate()?;
        if self.execution_profile_id.trim().is_empty()
            || self.execution_profile_revision.trim().is_empty()
            || self.actor.actor_id.trim().is_empty()
        {
            return Err(LifecycleError::InvalidRequest(
                "registering a run plan requires an execution profile id, its revision, and an actor".to_string(),
            ));
        }
        Ok(())
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub enum RegisterRunPlanOutcome {
    Registered {
        plan_id: String,
    },
    /// The identical declaration already exists; registration is idempotent.
    AlreadyRegistered {
        plan_id: String,
    },
}

pub fn register_run_plan(registry: &Path, request: &RegisterRunPlanRequest) -> LifecycleResult<RegisterRunPlanOutcome> {
    register_run_plan_with_stage_instances(registry, request, &[])
}

pub fn register_run_plan_with_stage_instances(
    registry: &Path,
    request: &RegisterRunPlanRequest,
    instances: &[crate::stage_attempt::StagePlanInstanceDeclaration],
) -> LifecycleResult<RegisterRunPlanOutcome> {
    request.validate()?;
    if request.definition.stage_plan_set_digest.is_some() {
        if instances.is_empty() {
            return Err(LifecycleError::InvalidRequest(
                "Stage-driven Plan registration requires the Stage Plan instance collection in the same transaction; refusing a Plan without them"
                    .to_string(),
            ));
        }
        let mut records = Vec::with_capacity(instances.len());
        for declaration in instances {
            records.push(crate::stage_attempt::declared_instance_record(declaration)?);
        }
        let set_digest = crate::stage_attempt::stage_plan_set_digest(&records)?;
        if request.definition.stage_plan_set_digest.as_deref() != Some(set_digest.as_str()) {
            return Err(LifecycleError::InvalidRequest(
                "stage_plan_set_digest does not match the declared instance collection".to_string(),
            ));
        }
        if let Some(compiled) = request.definition.compiled_experiment.as_ref() {
            if compiled.cells().len() != instances.len() {
                return Err(LifecycleError::InvalidRequest(format!(
                    "compiled Experiment has {} cells but {} Stage Plan instances were declared",
                    compiled.cells().len(),
                    instances.len()
                )));
            }
            for (cell, declaration) in compiled.cells().iter().zip(instances.iter()) {
                if !declaration.paired_unit_id.is_empty() {
                    return Err(LifecycleError::InvalidRequest(
                        "compiled Experiment cells cannot register as paired-unit Stage Plan instances".to_string(),
                    ));
                }
                if cell.target_hash != declaration.target_hash || cell.config_hash != declaration.config_hash {
                    return Err(LifecycleError::InvalidRequest(
                        "Stage Plan instance target/config does not match the compiled cell".to_string(),
                    ));
                }
            }
        }
        if let Some(ablation) = request.definition.compiled_ablation.as_ref() {
            let control_count = ablation.control().cells().len();
            let treatment_count = ablation.treatment().cells().len();
            if control_count != treatment_count || control_count != instances.len() {
                return Err(LifecycleError::InvalidRequest(format!(
                    "compiled Ablation has {control_count} control cells and {treatment_count} treatment cells but {} Stage Plan instances were declared",
                    instances.len()
                )));
            }
            let mut paired_unit_ids = BTreeSet::new();
            for declaration in instances {
                if declaration.paired_unit_id.trim().is_empty() {
                    return Err(LifecycleError::InvalidRequest(
                        "compiled Ablation Stage Plan instances must name a non-empty paired_unit_id".to_string(),
                    ));
                }
                if !paired_unit_ids.insert(declaration.paired_unit_id.as_str()) {
                    return Err(LifecycleError::InvalidRequest(format!(
                        "compiled Ablation Stage Plan instances repeat paired_unit_id {:?}",
                        declaration.paired_unit_id
                    )));
                }
            }
        }
    } else if !instances.is_empty() {
        return Err(LifecycleError::InvalidRequest(
            "Stage Plan instances were supplied for a Plan that does not declare a stage_plan_set_digest".to_string(),
        ));
    }
    let definition_json = serde_json::to_string(&request.definition)?;
    let definition_digest = request.definition.definition_digest()?;
    let plan_id = run_plan_identity(
        &request.definition.schema,
        &request.definition.plan_name,
        &definition_digest,
        &request.execution_profile_id,
        &request.execution_profile_revision,
    );
    let receipt: RegisterRunPlanReceipt = crate::csharp_registry::run_json(
        "register-run-plan",
        registry,
        &serde_json::json!({
            "plan_id": plan_id,
            "plan_name": request.definition.plan_name,
            "definition_json": definition_json,
            "definition_digest": definition_digest,
            "execution_profile_id": request.execution_profile_id,
            "execution_profile_revision": request.execution_profile_revision,
            "created_by": request.actor.actor_id,
            "actor_kind": request.actor.kind.as_str(),
            "actor_id": request.actor.actor_id,
            "implementation_id": request.definition.implementation_id,
            "binding_id": request.definition.implementation_binding_id,
            "source_snapshot_id": request.definition.implementation_source_snapshot_id,
            "steps": request.definition.steps.iter().map(|step| {
                serde_json::json!({
                    "step_id": step.step_id,
                    "step_json": serde_json::to_string(step).expect("run plan step serializes"),
                })
            }).collect::<Vec<_>>(),
            "instances": stage_instance_writes(instances)?,
        }),
    )
    .map_err(LifecycleError::InvalidRequest)?;
    match receipt.status.as_str() {
        "already_registered" => Ok(RegisterRunPlanOutcome::AlreadyRegistered {
            plan_id: receipt.plan_id,
        }),
        "registered" => Ok(RegisterRunPlanOutcome::Registered {
            plan_id: receipt.plan_id,
        }),
        other => Err(LifecycleError::InvalidRequest(format!(
            "register-run-plan returned unknown status {other:?}"
        ))),
    }
}

#[derive(Deserialize)]
struct RegisterRunPlanReceipt {
    plan_id: String,
    status: String,
}

fn stage_instance_writes(
    instances: &[crate::stage_attempt::StagePlanInstanceDeclaration],
) -> LifecycleResult<Vec<serde_json::Value>> {
    let mut writes = Vec::with_capacity(instances.len());
    for declaration in instances {
        let record = crate::stage_attempt::declared_instance_record(declaration)?;
        let stage_plan_json = String::from_utf8(
            fern_json_contract::canonical_json_bytes(&declaration.stage_plan)
                .map_err(|error| LifecycleError::InvalidRequest(error.to_string()))?,
        )
        .map_err(|error| LifecycleError::InvalidRequest(error.to_string()))?;
        let mut nodes = Vec::new();
        for (ordinal, stage) in declaration.stage_plan.stages.iter().enumerate() {
            let spec_digest = stage
                .digest()
                .map_err(|error| LifecycleError::InvalidRequest(error.to_string()))?;
            let predecessors: Vec<String> = stage
                .inputs
                .values()
                .filter_map(|input| match &input.source {
                    fern_experiment_types::StageInputSourceV1::StageOutput { stage_id, .. } => Some(stage_id.clone()),
                    _ => None,
                })
                .collect();
            nodes.push(serde_json::json!({
                "stage_id": stage.id,
                "stage_spec_digest": spec_digest.as_str(),
                "handler": stage.handler,
                "ordinal": ordinal,
                "predecessor_stage_ids_json": serde_json::to_string(&predecessors)?,
                "max_total_attempts": declaration.stage_plan.retry_spec(&stage.id).max_total_attempts,
            }));
        }
        writes.push(serde_json::json!({
            "cell_ordinal": record.cell_ordinal,
            "stage_plan_instance_id": record.stage_plan_instance_id,
            "compiled_cell_subject_digest": record.compiled_cell_subject_digest,
            "stage_plan_digest": record.stage_plan_digest,
            "training_worker_request_digest": record.training_worker_request_digest,
            "target_hash": record.target_hash,
            "config_hash": record.config_hash,
            "paired_unit_id": record.paired_unit_id,
            "stage_plan_json": stage_plan_json,
            "storage_site_id": declaration.storage_site_id,
            "nodes": nodes,
            "inputs": declaration.inputs.iter().map(|input| {
                serde_json::json!({
                    "input_key": input.input_key,
                    "sha256": input.sha256,
                    "path": input.path,
                    "byte_count": input.byte_count,
                    "role": input.role,
                    "schema_id": input.schema_id,
                })
            }).collect::<Vec<_>>(),
        }));
    }
    Ok(writes)
}

/// One dispatchable plan, in the deterministic order two observers agree on.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, serde::Deserialize)]
pub struct ReadyRunPlan {
    pub plan_id: String,
    pub plan_name: String,
    pub execution_profile_id: String,
    pub execution_profile_revision: String,
    pub registered_at_ms: i64,
}

#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RunPlanSurveyIdentity {
    pub plan_id: String,
    pub plan_name: String,
    pub execution_profile_id: String,
    pub updated_at_ms: i64,
}

/// Whether the plan's latest attempt is parked by an operator's cancel
/// (resume-run-plan re-arms it). The submitter-facing read behind
/// `fern-run run`'s already-registered report: "registered and a scheduler
/// serves" still means "never runs" for a parked plan, and printing
/// idempotent success there is the silent no-op #156 bans.
pub fn plan_is_operator_parked(registry: &Path, plan_id: &str) -> LifecycleResult<bool> {
    crate::csharp_registry::run_json(
        "plan-is-operator-parked",
        registry,
        &serde_json::json!({ "plan_id": plan_id }),
    )
    .map_err(|error| LifecycleError::InvalidRequest(error))
}

/// One plan attempt marked lost by the reconciler, with the fact that
/// decided it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct LostPlanAttempt {
    pub plan_attempt_id: String,
    pub plan_id: String,
    pub last_lease_expired_at_ms: i64,
}

/// A plan NAME is stable user intent, unique per Registry Home (the -b/-c/-d
/// sibling disease turned 25 lanes into 107 plan rows). The identity hash
/// includes the definition digest, so a changed declaration under an
/// existing name would silently mint a SIBLING plan; both `register_run_plan`
/// and `start_run_plan` refuse it through THIS one check before inserting.
/// The scope is deliberately name-global: a different execution profile is a
/// different intent and takes a different name. Retrying a failed plan is a
/// plan attempt (or a declared definition revision), never a fresh same-name
/// registration.
/// A dispatch/adoption failure recorded as a registry fact: why, by whom,
/// and when to retry. Survives scheduler restarts; visible to any monitor.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PlanDispatchBlock {
    pub plan_id: String,
    pub reason: String,
    pub blocked_by: String,
    pub blocked_at_ms: i64,
    pub retry_at_ms: i64,
}

/// No first heartbeat within this window means the spawn never happened or
/// the supervisor died instantly; the authorization is revocable and the
/// plan re-queues instead of waiting out a two-hour lease. Ten minutes sits
/// safely beyond the supervisor's renew cadence, so a slow spawn is never
/// killed while a truly dead one blocks the queue for a bounded, stated
/// time.
pub const UNSPAWNED_AUTHORIZATION_REVOKE_AFTER_MS: i64 = 600_000;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RevokedAuthorization {
    pub plan_id: String,
    pub plan_attempt_id: String,
    pub plan_lease_id: String,
}

/// The advisory read behind revocation: scheduler-issued leases past the
/// confirmation window with no first heartbeat, still the current pointer,
/// their attempt still `running`. Law 3 of the worker-boundary pin needs
/// this list BEFORE any revocation, so the execution boundary can be asked
/// whether a live worker unit already wears each attempt's name.
pub fn unspawned_authorization_candidates(
    registry: &Path,
    older_than_ms: i64,
) -> LifecycleResult<Vec<RevokedAuthorization>> {
    crate::csharp_registry::run_json(
        "unspawned-authorization-candidates",
        registry,
        &serde_json::json!({ "older_than_ms": older_than_ms }),
    )
    .map_err(LifecycleError::InvalidRequest)
}

/// Adoption: a newly elected scheduler takes over ADVANCEMENT of a plan
/// whose previous holder is provably stale (resident_scheduler B2/B3).
///
/// Strict compare-and-swap: the caller names the exact predecessor lease and
/// epoch it observed; if the pointer moved, the adoption is refused and the
/// caller re-reads — a stale adopter can never overwrite a newer permit.
/// The predecessor must be EXPIRED (a live lease is someone's authority) —
/// or, CHG second rehearsal: scheduler-issued with a SUPERSEDED epoch,
/// which proves the holder dead mid-TTL since every door refuses it — and
/// its plan attempt still `running` (a terminal attempt resumes through
/// `start_run_plan`, not adoption). What adoption takes is the plan's
/// advancement right ONLY: the same plan attempt continues under a successor
/// lease with a higher epoch, and the current Matrix and every worker lease
/// are untouched — the live worker never notices its parent changed.
#[derive(Debug, Clone)]
pub struct AdoptRunPlanRequest {
    pub plan_id: String,
    pub expected_current_lease_id: String,
    pub expected_fencing_epoch: i64,
    pub actor: LifecycleActor,
    pub lease_seconds: i64,
}

/// Read-only projection for terminal and TUI clients.  It reports declared
/// work and current authority but gives neither a capability nor a mutation
/// route.
pub fn run_plan_projection(registry: &Path, plan_id: &str) -> LifecycleResult<RunPlanProjection> {
    // The SQL read belongs to C#, while Rust still owns the complete nested
    // scientific definition type during this cutover. Reopen it first so a
    // malformed compiled Experiment/Ablation cannot be hidden by C#'s
    // deliberately top-level projection decoder. Retire this pure guard only
    // when the nested C# admission types themselves move, not as part of SQL
    // evacuation.
    let _ = load_run_plan_definition(registry, plan_id)?;
    crate::csharp_registry::run_json("project-run-plan", registry, &serde_json::json!({ "plan_id": plan_id }))
        .map_err(LifecycleError::InvalidRequest)
}

/// Read-only fencing check for a native plan parent.
///
/// Monitors and dependency waits use it to stop promptly after `cancel-plan`;
/// it has no mutation authority and may run through a read-only Registry Home
/// connection.
pub fn require_current_run_plan_permit(registry: &Path, permit: &RunPlanPermit) -> LifecycleResult<()> {
    let accepted: bool = crate::csharp_registry::run_json("require-current-run-plan-permit", registry, permit)
        .map_err(LifecycleError::InvalidRequest)?;
    if !accepted {
        return Err(LifecycleError::InvalidRequest(
            "C# Registry returned false after a run-plan permit check instead of accepting or refusing it".to_string(),
        ));
    }
    Ok(())
}

/// Registry roots that must survive content-addressed binary collection.
///
/// `running` includes dependency waits and `resource_occupied` waits. A legacy
/// running Attempt with no recorded version blocks the entire answer: silently
/// omitting it would authorize exactly the deletion that killed waiting Plans.
pub fn active_run_plan_binary_versions(registry: &Path) -> LifecycleResult<Vec<BinaryVersionIdentity>> {
    #[derive(Deserialize)]
    struct StoredBinaryVersion {
        digest: String,
        executable_path: String,
    }

    let versions: Vec<StoredBinaryVersion> =
        crate::csharp_registry::run_json("active-run-plan-binary-versions", registry, &serde_json::json!({}))
            .map_err(LifecycleError::InvalidRequest)?;
    versions
        .into_iter()
        .map(|version| {
            BinaryVersionIdentity::parse(version.digest, version.executable_path).map_err(|error| {
                LifecycleError::InvalidRequest(format!(
                    "C# Registry returned an invalid immutable binary identity; reconciliation is required: {error}"
                ))
            })
        })
        .collect()
}

/// Running or waiting Plans with a current unexpired lease on one profile.
///
/// Deploy uses this read-only gate before building or contacting a Worker
/// Site. Changing the profile under one of these Attempts would make its next
/// Matrix boundary reject the revision, so the deliberate cutover waits for
/// live authority instead of turning deployment into a mid-plan failure. An
/// expired lease cannot advance and is not allowed to block deploy forever;
/// its old Attempt still remains a conservative binary-collection root until
/// explicit reconciliation.
pub fn active_run_plan_ids_for_profile(registry: &Path, execution_profile_id: &str) -> LifecycleResult<Vec<String>> {
    crate::csharp_registry::run_json(
        "active-run-plan-ids-for-profile",
        registry,
        &serde_json::json!({ "execution_profile_id": execution_profile_id }),
    )
    .map_err(LifecycleError::InvalidRequest)
}

pub fn list_run_plan_survey_identities(registry: &Path) -> LifecycleResult<Vec<RunPlanSurveyIdentity>> {
    crate::csharp_registry::run_json("list-run-plan-survey-identities", registry, &serde_json::json!({}))
        .map_err(LifecycleError::InvalidRequest)
}

/// Reopen the immutable definition and profile identity for a named plan.
/// The caller still receives no capability; only `start_run_plan` may issue one.
pub fn load_run_plan_definition(
    registry: &Path,
    plan_id: &str,
) -> LifecycleResult<(RunPlanDefinitionV1, String, String)> {
    #[derive(Deserialize)]
    struct Loaded {
        definition_json: String,
        definition_digest: String,
        execution_profile_id: String,
        execution_profile_revision: String,
    }
    let loaded: Loaded = crate::csharp_registry::run_json(
        "load-run-plan-definition",
        registry,
        &serde_json::json!({ "plan_id": plan_id }),
    )
    .map_err(LifecycleError::InvalidRequest)?;
    let definition =
        crate::stored_json::parse_stored::<RunPlanDefinitionV1>(&loaded.definition_json).map_err(|error| {
            LifecycleError::InvalidRequest(format!(
                "stored run plan {plan_id} definition is unreadable; reconciliation is required: {error}"
            ))
        })?;
    let frozen_bytes_digest = crate::lifecycle::sha256_hex(loaded.definition_json.as_bytes());
    let projection_digest = definition.definition_digest().ok();
    if projection_digest.as_ref() != Some(&loaded.definition_digest) && frozen_bytes_digest != loaded.definition_digest
    {
        return Err(LifecycleError::InvalidRequest(format!(
            "stored run plan {plan_id} definition digest disagrees with its content; reconciliation is required"
        )));
    }
    Ok((
        definition,
        loaded.execution_profile_id,
        loaded.execution_profile_revision,
    ))
}
