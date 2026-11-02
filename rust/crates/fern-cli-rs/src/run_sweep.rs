//! `fern-worker execute-matrix` — run a bounded slice from a recipe or preset.
//!
//! CHG: the standalone `fern-run run-recipe` command is retired — it was
//! `execute-matrix --recipe` plus `--start-target-idx`, so that flag moved onto
//! `execute-matrix` and this module keeps only the shared batch loop.
//!
//! CHG: the standalone `fern-run run-recipe-target` command is retired (ruled
//! 2026-07-02) — it was `execute-matrix --recipe R --start-target-idx N --limit 1`
//! with a flatter JSON summary, so the clap struct and main.rs route are gone
//! (the way run-recipe was folded) and this module keeps
//! `run_recipe_target_once` plus the shared planning/worker plumbing.
//!
//! This is the first high-level Rust parent process for recipe training. Python
//! receives one resolved worker request and returns metrics; Rust owns the run
//! packet, registry lifecycle, and completion/failure state.
//!
//! CHG (2026-07-03): the engine-only modules run_recipe.rs and
//! run_recipe_target.rs are merged into this file — the command surface is
//! `execute-matrix`, and the batch loop and per-target plumbing now live beside it.
//!
//! CLI Pass 2e worker handoff is centralized in `cli_pass2_parent.rs`; this
//! module keeps recipe selection, skip/resume policy, and DB lifecycle.

use std::collections::{BTreeMap, BTreeSet};
#[cfg(any(fern_role_operator, fern_role_scheduler))]
use std::fs::File;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use clap::Args;
use fern_execution_profile::{
    ExecutionProfile, ExecutionProfileExecutor, ResolvedExecutionProfile, SupervisorExecutionProfile,
};
use fern_experiment_types::{DeclaredReplicaPurpose, DeclaredReplicaV1, DeviceKind};
use fern_json_contract::JsonContract;
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};

#[cfg(any(fern_role_operator, test))]
#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
use crate::deferred_lane_projection::{accepted_candidate_via_registry_home, complete_deferred_lane_projection};
use crate::experiment_front_door::{ReadyExecution, observed_site_capability, resolve_ready_execution};
use crate::plan_recipe_run::{compiled_experiment_target_registrations, recipe_target_registrations_for_device};
use crate::presets::{RecipeBlueprint, RecipeModelName};
use crate::{DrawSeedBaseArgs, Fallible, RunAnnotationArgs, WorkerSpawnArgs, invalid_input};

#[derive(Debug, Serialize, JsonSchema)]
pub(crate) struct RemoteAttemptExecutionReport {
    schema: &'static str,
    status: &'static str,
    return_package: PathBuf,
    report: fern_execution_profile::RemoteAttemptReportV1,
}

impl JsonContract for RemoteAttemptExecutionReport {
    const SCHEMA_ID: &'static str = "fern.remote_attempt_execution.v1";
}

const CONTROL_PLANE_POLICY_REVISION: &str = "fern.control_plane.v3";

#[derive(Debug, Deserialize, Serialize, JsonSchema)]
struct AbandonAttemptReport {
    schema: String,
    status: String,
    attempt_id: String,
    coordinate_id: String,
    revoked_attempt_lease: bool,
    released_coordinate_authority: bool,
    released_coordinator_authority: bool,
    transition_receipt_ids: Vec<String>,
}

impl JsonContract for AbandonAttemptReport {
    const SCHEMA_ID: &'static str = "fern.abandon_attempt.v1";
}

#[derive(Debug, Serialize, JsonSchema)]
struct CancelMatrixReport {
    schema: &'static str,
    status: &'static str,
    matrix_id: String,
    coordinator_lease_ids: Vec<String>,
    abandoned_attempt_ids: Vec<String>,
    transition_receipt_ids: Vec<String>,
}

impl JsonContract for CancelMatrixReport {
    const SCHEMA_ID: &'static str = "fern.cancel_matrix.v1";
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct CsharpCancelMatrixReceipt {
    schema: String,
    status: String,
    matrix_id: String,
    coordinator_lease_ids: Vec<String>,
    abandoned_attempt_ids: Vec<String>,
    transition_receipt_ids: Vec<String>,
}

#[derive(Debug, Deserialize, Serialize, JsonSchema)]
struct CancelPlanReport {
    schema: String,
    status: String,
    plan_id: String,
    plan_attempt_id: Option<String>,
    plan_lease_id: Option<String>,
    plan_transition_receipt_id: Option<String>,
    current_matrix_id: Option<String>,
    matrix_cancellation: Option<Value>,
}

impl JsonContract for CancelPlanReport {
    const SCHEMA_ID: &'static str = "fern.cancel_plan.v1";
}

#[derive(Debug, Serialize, JsonSchema)]
struct TypedAttemptReconciliationReport {
    schema: &'static str,
    status: &'static str,
    coordinate_id: String,
    attempt_id: String,
    run_hash: String,
    committed_directory: PathBuf,
    candidate_selected_now: bool,
    legacy_rows_changed: usize,
}

impl JsonContract for TypedAttemptReconciliationReport {
    const SCHEMA_ID: &'static str = "fern.typed_attempt_reconciliation.v1";
}

#[derive(Debug, Deserialize, Serialize, JsonSchema)]
#[serde(deny_unknown_fields)]
struct AttemptTransitionView {
    operation: String,
    object_kind: String,
    object_id: String,
    sequence: i64,
    prior_state: Option<String>,
    next_state: Option<String>,
    actor_kind: String,
    actor_id: String,
    fencing_epoch: Option<i64>,
    error_category: Option<String>,
    occurred_at_ms: i64,
}

#[derive(Debug, Deserialize, Serialize, JsonSchema)]
#[serde(deny_unknown_fields)]
struct AttemptExplanation {
    schema: String,
    attempt_id: String,
    coordinate_id: String,
    state: String,
    owner_host: String,
    owner_pid: i64,
    claimed_at: String,
    ended_at: Option<String>,
    outcome: Option<String>,
    detail: Option<String>,
    transitions: Vec<AttemptTransitionView>,
}

impl JsonContract for AttemptExplanation {
    const SCHEMA_ID: &'static str = "fern.attempt_explanation.v1";
}

fn validate_csharp_cancel_matrix_receipt(receipt: &CsharpCancelMatrixReceipt, matrix_id: &str) -> Fallible<()> {
    let ids_are_unique_lowercase_sha256 = |ids: &[String]| {
        ids.iter().all(|id| {
            id.len() == 64
                && id
                    .bytes()
                    .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        }) && ids.iter().collect::<BTreeSet<_>>().len() == ids.len()
    };
    let status_shape_is_valid = match receipt.status.as_str() {
        "already-inactive" => {
            receipt.coordinator_lease_ids.is_empty()
                && receipt.abandoned_attempt_ids.is_empty()
                && receipt.transition_receipt_ids.is_empty()
        }
        "cancelled" => {
            !receipt.coordinator_lease_ids.is_empty()
                && !receipt.transition_receipt_ids.is_empty()
                && receipt.transition_receipt_ids.len() >= receipt.coordinator_lease_ids.len()
                && ids_are_unique_lowercase_sha256(&receipt.coordinator_lease_ids)
                && ids_are_unique_lowercase_sha256(&receipt.abandoned_attempt_ids)
                && ids_are_unique_lowercase_sha256(&receipt.transition_receipt_ids)
        }
        _ => false,
    };
    if receipt.schema != CancelMatrixReport::SCHEMA_ID || receipt.matrix_id != matrix_id || !status_shape_is_valid {
        return Err(invalid_input(format!(
            "C# Registry cancel-matrix receipt does not answer this request: schema={:?}, status={:?}, matrix_id={:?}, coordinator_leases={}, abandoned_attempts={}, transition_receipts={}",
            receipt.schema,
            receipt.status,
            receipt.matrix_id,
            receipt.coordinator_lease_ids.len(),
            receipt.abandoned_attempt_ids.len(),
            receipt.transition_receipt_ids.len()
        )));
    }
    Ok(())
}

#[cfg(any(fern_role_operator, fern_role_scheduler, test))]
pub(crate) fn claim_scheduler_lease_via_csharp_registry(
    registry: &Path,
    actor: &fern_run::lifecycle::LifecycleActor,
    lease_seconds: i64,
) -> Fallible<fern_run::scheduler::SchedulerElection> {
    let input = json!({
        "actor": {
            "kind": actor.kind.as_str(),
            "id": actor.actor_id,
            "owner_host": actor.owner_host,
            "owner_pid": actor.owner_pid,
        },
        "lease_seconds": lease_seconds,
    });
    let bytes = crate::run_csharp_registry(
        "claim-scheduler",
        registry,
        &input,
        crate::CSHARP_REGISTRY_PROCESS_DEADLINE,
    )?;
    Ok(serde_json::from_slice(&bytes)?)
}

#[cfg(any(fern_role_operator, fern_role_scheduler, test))]
fn release_scheduler_lease_via_csharp_registry(
    registry: &Path,
    permit: &fern_run::scheduler::SchedulerPermit,
) -> Fallible<bool> {
    let input = json!({
        "permit": {
            "scheduler_lease_id": permit.scheduler_lease_id,
            "fencing_epoch": permit.fencing_epoch,
            "capability": permit.capability,
        }
    });
    let bytes = crate::run_csharp_registry(
        "release-scheduler",
        registry,
        &input,
        crate::CSHARP_REGISTRY_PROCESS_DEADLINE,
    )?;
    let output: Value = serde_json::from_slice(&bytes)?;
    output
        .get("released")
        .and_then(Value::as_bool)
        .ok_or_else(|| invalid_input("Registry Home omitted scheduler release outcome"))
}

#[cfg(any(fern_role_operator, fern_role_scheduler, test))]
fn renew_plan_lease_via_csharp_registry(
    registry: &Path,
    permit: &fern_run::run_plan::RunPlanPermit,
    actor: &fern_run::lifecycle::LifecycleActor,
    lease_seconds: i64,
) -> Fallible<i64> {
    let input = json!({
        "permit": {
            "plan_id": permit.plan_id,
            "plan_attempt_id": permit.plan_attempt_id,
            "plan_lease_id": permit.plan_lease_id,
            "fencing_epoch": permit.fencing_epoch,
            "capability": permit.capability,
        },
        "actor": {
            "kind": actor.kind.as_str(), "id": actor.actor_id,
            "owner_host": actor.owner_host, "owner_pid": actor.owner_pid,
        },
        "lease_seconds": lease_seconds,
    });
    let bytes = crate::run_csharp_registry("renew-plan", registry, &input, crate::CSHARP_REGISTRY_PROCESS_DEADLINE)?;
    let output: Value = serde_json::from_slice(&bytes)?;
    output
        .get("expires_at_ms")
        .and_then(Value::as_i64)
        .ok_or_else(|| invalid_input("Registry Home omitted Plan lease expiry"))
}

#[cfg(any(fern_role_operator, fern_role_scheduler, test))]
pub(crate) fn claim_plan_via_registry_home(
    home_binary: &Path,
    registry: &Path,
    scheduler: &fern_run::scheduler::SchedulerPermit,
    plan_id: &str,
    request: &fern_run::run_plan::StartRunPlanRequest,
) -> Fallible<fern_run::run_plan::StartRunPlanDecision> {
    #[derive(serde::Deserialize)]
    struct Output {
        status: String,
        permit: Option<fern_run::run_plan::RunPlanPermit>,
        plan_attempt_id: String,
        expires_at_ms: i64,
    }
    let input = json!({
        "scheduler": {
            "scheduler_lease_id": scheduler.scheduler_lease_id,
            "fencing_epoch": scheduler.fencing_epoch,
            "capability": scheduler.capability,
        },
        "plan": {
            "plan_id": plan_id,
            "plan_name": request.definition.plan_name,
            "execution_profile_id": request.execution_profile_id,
            "execution_profile_revision": request.execution_profile_revision,
            "registered_at_ms": 0,
        },
        "supervisor": {
            "sha256": request.supervisor_binary.digest().as_str(),
            "path": request.supervisor_binary.executable_path(),
        },
        "actor": {
            "kind": request.actor.kind.as_str(), "id": request.actor.actor_id,
            "owner_host": request.actor.owner_host, "owner_pid": request.actor.owner_pid,
        },
        "lease_seconds": request.lease_seconds,
    });
    let bytes = crate::run_go_home(home_binary, "claim-plan", registry, &input)?;
    let output: Output = serde_json::from_slice(&bytes)?;
    let outcome = match (output.status.as_str(), output.permit) {
        ("started", Some(permit)) => fern_run::run_plan::StartRunPlanOutcome::Started {
            permit,
            supervisor_binary: request.supervisor_binary.clone(),
        },
        ("already_running", None) => fern_run::run_plan::StartRunPlanOutcome::AlreadyRunning {
            plan_id: plan_id.to_string(),
            plan_attempt_id: output.plan_attempt_id,
            lease_expires_at_ms: output.expires_at_ms,
        },
        _ => return Err(invalid_input("Registry Home returned an invalid Plan claim outcome")),
    };
    Ok(fern_run::run_plan::StartRunPlanDecision {
        may_supervise: matches!(outcome, fern_run::run_plan::StartRunPlanOutcome::Started { .. }),
        outcome,
    })
}

#[cfg(any(fern_role_operator, fern_role_scheduler, test))]
#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct SchedulerOfficeProjectionV1 {
    schema: String,
    fencing_epoch: Option<i64>,
}

#[cfg(any(fern_role_operator, fern_role_scheduler, test))]
#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct SchedulerRouteV1 {
    schema: String,
    scheduling_pause: Option<fern_run::lifecycle::SchedulingPause>,
    scheduler_office: SchedulerOfficeProjectionV1,
}

#[cfg(any(fern_role_operator, fern_role_scheduler, test))]
fn scheduler_route_via_csharp_registry(registry: &Path) -> Fallible<SchedulerRouteV1> {
    let bytes = crate::run_csharp_registry(
        "inspect-scheduler-route",
        registry,
        &json!({}),
        crate::CSHARP_REGISTRY_PROCESS_DEADLINE,
    )?;
    let route: SchedulerRouteV1 = serde_json::from_slice(&bytes)?;
    if route.schema != "fern.scheduler_route.v1"
        || route.scheduler_office.schema != "fern.scheduler_office.v1"
    {
        return Err(invalid_input(format!(
            "C# Registry returned scheduler route schemas {:?} and {:?}",
            route.schema, route.scheduler_office.schema
        )));
    }
    Ok(route)
}

#[cfg(test)]
pub(crate) fn go_home_test_binary() -> &'static Path {
    static BINARY: std::sync::OnceLock<PathBuf> = std::sync::OnceLock::new();
    BINARY
        .get_or_init(|| {
            let output = std::env::temp_dir().join(format!("fern-home-scheduler-tests-{}", std::process::id()));
            let status = Command::new("go")
                .args(["build", "-o"])
                .arg(&output)
                .arg("./cmd/fern-home")
                .env("GOCACHE", "/tmp/fern-control-go-cache")
                .current_dir(reviewed_source_root().unwrap().join("tools/fern-control"))
                .status()
                .expect("build fern-home scheduler test writer");
            assert!(status.success(), "go build ./cmd/fern-home failed");
            output
        })
        .as_path()
}

/// The operational authority a runner receives only after its declared profile
/// and every caller-supplied path agree.  This is intentionally not a worker
/// configuration: it carries the registry and parent identity needed to ask
/// for permission to spawn, never the worker's one-attempt capability.
#[derive(Debug, Clone)]
pub(crate) struct LaunchAuthority {
    #[allow(dead_code)]
    profile_id: String,
    worker_profile: ResolvedExecutionProfile,
    registry_home_profile: ResolvedExecutionProfile,
    lifecycle_db: PathBuf,
    #[cfg_attr(not(any(fern_role_matrix_worker, test)), allow(dead_code))]
    registry_home_binary: Option<PathBuf>,
    execution_profile_revision: String,
    #[allow(dead_code)]
    resource_slot_id: String,
    supervisor: SupervisorExecutionProfile,
    // Read by the matrix worker, which records the capability self-report
    // under this identity; the operator and scheduler builds never reach it.
    #[cfg_attr(not(any(fern_role_matrix_worker, test)), allow(dead_code))]
    actor: fern_run::lifecycle::LifecycleActor,
    implementation_source_snapshot_id: Option<String>,
}

impl LaunchAuthority {
    #[allow(dead_code)]
    fn policy_revision(&self) -> &'static str {
        CONTROL_PLANE_POLICY_REVISION
    }

    #[allow(dead_code)]
    pub(crate) fn lifecycle_db(&self) -> &Path {
        &self.lifecycle_db
    }

    pub(crate) fn is_remote(&self) -> bool {
        self.worker_profile.declaration.executor == ExecutionProfileExecutor::Ssh
    }

    pub(crate) fn registry_home_work_root(&self) -> &Path {
        &self.registry_home_profile.declaration.work_root
    }

    fn required_registry_home_binary(&self) -> Fallible<&Path> {
        self.registry_home_binary.as_deref().ok_or_else(|| {
            invalid_input(
                "the Matrix worker did not receive the reviewed fern-home binary from its parent",
            )
        })
    }
}

#[cfg(any(fern_role_operator, fern_role_scheduler))]
fn parse_sha256sum_output(bytes: &[u8]) -> Result<String, String> {
    let text = std::str::from_utf8(bytes).map_err(|error| format!("output is not UTF-8: {error}"))?;
    let digest = text
        .lines()
        .find_map(|line| line.split_whitespace().next())
        .ok_or_else(|| "sha256sum printed no digest".to_string())?;
    if digest.len() != 64
        || !digest
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return Err(format!("expected 64 lowercase hexadecimal characters, got {digest:?}"));
    }
    Ok(digest.to_string())
}

#[cfg(any(fern_role_operator, fern_role_scheduler))]
fn require_matching_binary_deployment(
    profile_id: &str,
    expected: &str,
    local: &str,
    remote: &str,
    remote_path: &Path,
) -> Fallible<()> {
    if expected == local && expected == remote {
        return Ok(());
    }
    Err(invalid_input(format!(
        "SSH profile {profile_id:?} expects fern-run {expected}, the locally retained deployment is {local}, and Worker Site {} is {remote}. This is a deployment mismatch, not a recipe or scientific failure. Run `fern-run deploy --on {profile_id}` before retrying; no Plan lease, Coordinate, or GPU was claimed.",
        remote_path.display()
    )))
}

pub(crate) fn resolve_registry_home_profile(
    worker_profile: &ResolvedExecutionProfile,
) -> Fallible<ResolvedExecutionProfile> {
    if worker_profile.declaration.executor == ExecutionProfileExecutor::Local {
        return Ok(worker_profile.clone());
    }
    let home_id = worker_profile
        .declaration
        .registry_home_profile_id
        .as_deref()
        .ok_or_else(|| invalid_input("an SSH Worker Site has no Registry Home profile; no GPU work started"))?;
    let relative_path = ExecutionProfile::default_path(home_id).map_err(|issue| invalid_input(issue.explanation))?;
    let path = reviewed_source_root()?.join(relative_path);
    let declaration = ExecutionProfile::read(&path).map_err(|error| {
        invalid_input(format!(
            "cannot read Registry Home profile {home_id:?} at {}: {error}. No GPU work started.",
            path.display()
        ))
    })?;
    if declaration.executor != ExecutionProfileExecutor::Local {
        return Err(invalid_input(format!(
            "Registry Home profile {home_id:?} is not local; Fern refuses a remote SQLite authority. No GPU work started."
        )));
    }
    declaration
        .resolve()
        .map_err(|error| invalid_input(format!("Registry Home profile {home_id:?} is invalid: {error}")))
}

/// Keep `--on` optional in clap so an old queue reaches an actionable Fern
/// refusal instead of a generic usage dump. It is still mandatory at the one
/// place that can reach a worker.
pub(crate) fn require_execution_profile_id(profile_id: Option<&str>) -> Fallible<&str> {
    profile_id.filter(|id| !id.trim().is_empty()).ok_or_else(|| {
        invalid_input(
            "cannot start: --on <execution-profile> is required. Run `fern-run doctor --on <profile> --observer host` first; Fern will not infer Python, paths, or a GPU from a copied shell command. No GPU work started.",
        )
    })
}

fn launch_actor() -> fern_run::lifecycle::LifecycleActor {
    let host = local_hostname();
    let pid = i64::from(std::process::id());
    let supervised = std::env::var(RUN_PLAN_CAPABILITY_ENV)
        .ok()
        .is_some_and(|capability| !capability.is_empty());
    let kind = if supervised {
        fern_run::lifecycle::LifecycleActorKind::Supervisor
    } else {
        fern_run::lifecycle::LifecycleActorKind::Cli
    };
    let actor_id = std::env::var("FERN_ACTOR_ID")
        .ok()
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| format!("fern-run:{host}:{pid}"));
    fern_run::lifecycle::LifecycleActor {
        kind,
        actor_id,
        owner_host: host,
        owner_pid: pid,
    }
}

/// A predeclared second execution of the same scientific target.
///
/// Both fields are required together. The bounded purpose enters run identity,
/// while the ordinal joins the matching lifecycle Coordinate. This is not a
/// generic `--force`: an undeclared duplicate still stops before a worker.
#[derive(Debug, Clone, Args, Default)]
pub(crate) struct DeclaredReplicaArgs {
    /// Positive ordinal of the intended repeat. 0 means the primary execution.
    #[arg(long, default_value_t = 0)]
    pub(crate) replica_ordinal: i64,
    /// Why this repeat exists. Valid: forecast-origin-evidence,
    /// model-seed-robustness, execution-platform-robustness.
    #[arg(long, value_parser = DeclaredReplicaPurpose::parse)]
    pub(crate) replica_purpose: Option<DeclaredReplicaPurpose>,
}

impl DeclaredReplicaArgs {
    pub(crate) fn identity(&self) -> Fallible<Option<DeclaredReplicaV1>> {
        match (self.replica_ordinal, self.replica_purpose) {
            (0, None) => Ok(None),
            (0, Some(_)) => Err(invalid_input(
                "--replica-purpose requires a positive --replica-ordinal; ordinal 0 is the primary execution",
            )),
            (ordinal, None) => Err(invalid_input(format!(
                "--replica-ordinal {ordinal} requires --replica-purpose. A repeat without a declared reason is refused."
            ))),
            (ordinal, Some(purpose)) => DeclaredReplicaV1::new(ordinal, purpose)
                .map(Some)
                .map_err(invalid_input),
        }
    }
}

#[derive(Debug, Clone, Args)]
pub struct RunSweep {
    /// Path to the Fern run registry SQLite database.
    #[arg(long)]
    db: PathBuf,
    /// What stdout carries. `compact` omits the per-target config blocks, which
    /// are stored durably in the database and in each run's invocation.json;
    /// that is the difference between a 16 MB log and a few kilobytes. `full`
    /// restores the previous output.
    #[arg(long, value_enum, default_value_t = SweepSummaryFormat::Compact)]
    format: SweepSummaryFormat,
    /// Registered execution profile that owns this launch's paths and device.
    ///
    /// A profile is required for every new worker. It prevents a copied shell
    /// command from silently pointing its DB, artifact root, Python, and GPU at
    /// different machines. Use `fern-run doctor --on <profile> --observer host` to inspect it
    /// without reserving anything.
    #[arg(long)]
    on: Option<String>,
    /// Exact recipe name from `just list-recipes`.
    #[arg(long)]
    recipe: Option<String>,
    /// Frozen recipe identity held in memory while the scheduler prepares a
    /// child. It is never accepted from argv and never reopened from the
    /// mutable authoring declaration.
    #[arg(skip)]
    recipe_snapshot: Option<fern_run::run_plan::RunPlanRecipeSnapshotV1>,
    /// Content-addressed handoff file supplied only by the elected scheduler.
    #[arg(long, hide = true, requires = "recipe_snapshot_sha256")]
    recipe_snapshot_path: Option<PathBuf>,
    /// Full SHA-256 of the exact handoff bytes.
    #[arg(long, hide = true, requires = "recipe_snapshot_path")]
    recipe_snapshot_sha256: Option<String>,
    /// Frozen V2 execution object held only by the elected scheduler.
    #[arg(skip)]
    compiled_experiment: Option<fern_run::experiment_v2::CompiledExperimentV2>,
    /// Authenticated scheduler-to-worker handoff for the compiled object.
    #[arg(long, hide = true, requires = "compiled_experiment_sha256")]
    compiled_experiment_path: Option<PathBuf>,
    #[arg(long, hide = true, requires = "compiled_experiment_path")]
    compiled_experiment_sha256: Option<String>,
    /// Frozen implementation source supplied only by a registered Plan.
    #[arg(long, hide = true)]
    implementation_source_snapshot: Option<String>,
    /// Preset name from `just list-presets`.
    #[arg(long)]
    preset: Option<String>,
    /// Model filter for --preset, for example fern, patchtst, chronos2, or timesfm_2p5_200m.
    #[arg(long, value_parser = RecipeModelName::parse)]
    model: Option<RecipeModelName>,
    /// Existing run campaign id. If omitted, Rust opens a new campaign.
    #[arg(long)]
    campaign_id: Option<i64>,
    /// First zero-based target index to run. Applies to --recipe only.
    #[arg(long, default_value_t = 0)]
    start_target_idx: usize,
    /// Run exactly one recipe target selected by a target_hash prefix (from the
    /// materialized recipe_targets table; each run row records its target_hash).
    /// Discover prefixes with `just list-recipe-run-targets R cpu`:
    /// each row shows the first 8 hex of its target_hash. Applies to --recipe
    /// only; conflicts with --start-target-idx. The normal skip rule still
    /// applies: a target with a done run is skipped unless --retrain.
    #[arg(long, value_name = "HASH_PREFIX", conflicts_with = "start_target_idx")]
    target: Option<String>,
    /// Maximum ATTEMPTED target count (skipped-done targets do not consume the
    /// limit). Default 1 prevents accidental large sweeps.
    #[arg(long)]
    limit: usize,
    /// Re-run targets that already have a done run instead of skipping them.
    #[arg(long)]
    retrain: bool,
    /// Continue after a failed target instead of stopping at the first failure.
    #[arg(long)]
    keep_going: bool,
    /// Root directory for run artifacts. This is provenance, not part of the run hash.
    #[arg(long, default_value = "results/runs")]
    artifact_root: PathBuf,
    #[command(flatten)]
    draw_seed_bases: DrawSeedBaseArgs,
    #[command(flatten)]
    annotations: RunAnnotationArgs,
    #[command(flatten)]
    replica: DeclaredReplicaArgs,
    /// Delete DB rows and artifact directories after successful target runs.
    #[arg(long)]
    delete_after: bool,
    #[command(flatten)]
    worker: WorkerSpawnArgs,
    /// Registry Home that issued the current run-plan permit.
    #[arg(long)]
    plan_registry: Option<PathBuf>,
    /// Receipted private Registry Home writer supplied by the elected parent.
    #[arg(long, hide = true)]
    registry_home_binary: Option<PathBuf>,
    /// Exact run-plan identity whose current step owns this Matrix.
    #[arg(long)]
    plan_id: Option<String>,
    /// Current monotonic run-plan Attempt identity.
    #[arg(long)]
    plan_attempt: Option<String>,
    /// Current run-plan lease identity.
    #[arg(long)]
    plan_lease: Option<String>,
    /// Current run-plan fencing epoch.
    #[arg(long)]
    plan_epoch: Option<i64>,
    /// Exact Matrix predeclared by the current run-plan step.
    #[arg(long)]
    registered_matrix: Option<String>,
}

/// STDOUT IS DATA; STDERR IS WHAT HAPPENED. The progress lines already go to
/// stderr, so a caller can redirect stdout to a file and still watch the sweep.
/// What stdout carries was the problem: the full pretty-printed summary embeds
/// each target's whole resolved config, so an 84-cell sweep printed 84 copies
/// of it. One measured log reached 16.6 MB, 100% JSON, with `fern.cfg.v2`
/// appearing 1,400 times.
///
/// That config is already stored durably TWICE per run -- the `config_json`
/// column and the run's `invocation.json` -- both keyed by run_hash and both
/// queryable. The stdout copy was the third, the only one nobody can query,
/// and the only one that grows without bound. Nothing parses it: the sole
/// script that ever did is in `retired/`.
///
/// So the default projection drops exactly the blocks that live elsewhere and
/// keeps everything else, recursively and by name rather than by position, so
/// it does not break when the summary gains a field. `--format full` restores
/// the previous output for anyone who wants it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, clap::ValueEnum)]
pub(crate) enum SweepSummaryFormat {
    /// Counts, identities and scores. Config blocks are omitted; read them from
    /// the database or the run's invocation.json, which is where they are
    /// authoritative.
    Compact,
    /// Every field, including the per-target resolved config. Large.
    Full,
}

fn valid_lane_name(lane: &str) -> bool {
    let lane_path = Path::new(lane);
    let mut components = lane_path.components();
    matches!(components.next(), Some(std::path::Component::Normal(_)))
        && components.next().is_none()
        && lane != "."
        && lane != ".."
        && !lane.contains('/')
        && !lane.contains('\\')
        && !lane.trim().is_empty()
}

const RUN_PLAN_CAPABILITY_ENV: &str = "FERN_RUN_PLAN_CAPABILITY";

#[cfg(any(fern_role_operator, fern_role_scheduler))]
pub(crate) struct PreparedRunPlan {
    pub(crate) registry: PathBuf,
    pub(crate) execution_profile_revision: String,
    log_root: PathBuf,
    worker_profile: ResolvedExecutionProfile,
    _deployment_guard: Option<File>,
}

#[cfg(any(fern_role_operator, fern_role_scheduler))]
impl PreparedRunPlan {
    /// Registry Home owns the Plan after Attempt insertion; the parent that
    /// drives it (CHG step 5: the scheduler-authorized private Plan child,
    /// formerly the detached public parent) must never inherit the short
    /// deployment/profile hand-off lock.
    fn release_deployment_guard(&mut self) {
        drop(self._deployment_guard.take());
    }
}

/// Resolve one ordered, durable plan before Fern claims its coordinator lease.
///
/// Named built-ins preserve compact commands for recurring campaign work. A
/// repeated `--stage` form replaces `lane.sh queue` for a new declared order:
/// every parsed field becomes part of the stored plan definition before any GPU
/// work can begin. It is deliberately not a shell command list.
pub(crate) fn resolve_run_plan(
    name: &str,
    raw_stages: &[String],
    after_matrices: &[String],
    recipe_files: &[PathBuf],
    source_snapshot: Option<&str>,
) -> Fallible<fern_run::run_plan::RunPlanDefinitionV1> {
    let external_recipes = load_recipe_extensions(recipe_files)?;
    let steps = if raw_stages.is_empty() {
        match name {
            "hugadb-accel-trained" => vec![
                // The plan name names the intent. The lane remains the one existing
                // result projection so the first completed TimeMixer Matrix is
                // reused rather than silently copied into a new database.
                run_plan_step(
                    "timemixer",
                    "real_sensor_h64",
                    "paper-hugadb-h64",
                    "timemixer",
                    25,
                    true,
                ),
                run_plan_step("duet", "real_sensor_h64", "paper-hugadb-h64", "duet", 25, true),
                run_plan_step("patchtst", "real_sensor_h64", "paper-hugadb-h64", "patchtst", 25, true),
            ],
            other => {
                return Err(invalid_input(format!(
                    "unknown run plan {other:?}. Supply one or more --stage STEP_ID:LANE:PRESET:MODEL:LIMIT values, or use built-in hugadb-accel-trained."
                )));
            }
        }
    } else {
        raw_stages
            .iter()
            .map(|raw| parse_run_plan_stage(raw))
            .collect::<Fallible<Vec<_>>>()?
    }
    .into_iter()
    .map(|step| freeze_run_plan_recipe(step, &external_recipes))
    .collect::<Fallible<Vec<_>>>()?;
    let used_recipe_names = steps.iter().map(|step| step.preset.as_str()).collect::<BTreeSet<_>>();
    for recipe_name in external_recipes.keys() {
        if !used_recipe_names.contains(recipe_name.as_str()) {
            return Err(invalid_input(format!(
                "recipe declaration {recipe_name:?} is not named by any --stage; remove the unused --recipe-file or add its explicit stage"
            )));
        }
    }
    Ok(fern_run::run_plan::RunPlanDefinitionV1 {
        schema: fern_run::run_plan::RUN_PLAN_SCHEMA.to_string(),
        plan_name: name.to_string(),
        implementation_source_snapshot_id: source_snapshot.map(str::to_string),
        compiled_ablation: None,
        compiled_experiment: None,
        stage_plan_set_digest: None,
        stage_plan_digest: None,
        training_worker_request_digest: None,
        implementation_id: None,
        implementation_binding_id: None,
        owner_storage_override_reason: None,
        dependencies: after_matrices
            .iter()
            .map(|matrix_id| fern_run::run_plan::RunPlanMatrixDependencyV1 {
                matrix_id: matrix_id.clone(),
            })
            .collect(),
        steps,
    })
}

/// Parse the only user-provided plan boundary and immediately turn it into the
/// typed registered object. Colons keep a repeated command readable without a
/// mutable plan file or an agent-owned shell queue.

fn register_run_plan_via_csharp(
    registry: &Path,
    definition: &fern_run::run_plan::RunPlanDefinitionV1,
    execution_profile_id: &str,
    execution_profile_revision: &str,
    actor: &fern_run::lifecycle::LifecycleActor,
    instances: &[fern_run::stage_attempt::StagePlanInstanceDeclaration],
) -> Fallible<fern_run::run_plan::RegisterRunPlanOutcome> {
    fern_run::run_plan::register_run_plan_with_stage_instances(
        registry,
        &fern_run::run_plan::RegisterRunPlanRequest {
            definition: definition.clone(),
            execution_profile_id: execution_profile_id.to_string(),
            execution_profile_revision: execution_profile_revision.to_string(),
            actor: actor.clone(),
        },
        instances,
    )
    .map_err(|error| invalid_input(error.to_string()))
}

/// Register a Stage-driven Plan with one Stage Plan instance per compiled cell.
#[cfg(any(fern_role_operator, fern_role_scheduler))]
pub(crate) fn register_stage_driven_run_plan_definition(
    definition: fern_run::run_plan::RunPlanDefinitionV1,
    execution_profile_id: String,
    instances: Vec<fern_run::stage_attempt::StagePlanInstanceDeclaration>,
) -> Fallible<()> {
    let plan_name = definition.plan_name.clone();
    let cell_count = instances.len();
    let launch = preflight_stage_driven_run_plan(&definition, &execution_profile_id)?;
    let actor = launch_actor();
    let outcome = register_run_plan_via_csharp(
        &launch.registry,
        &definition,
        &execution_profile_id,
        &launch.execution_profile_revision,
        &actor,
        &instances,
    )?;
    let plan_id = match outcome {
        fern_run::run_plan::RegisterRunPlanOutcome::Registered { plan_id } => {
            println!(
                "registered: plan {plan_id} ({plan_name}) holds {cell_count} Stage Plan instance(s) and waits for the elected scheduler"
            );
            plan_id
        }
        fern_run::run_plan::RegisterRunPlanOutcome::AlreadyRegistered { plan_id } => {
            println!("already registered: plan {plan_id} ({plan_name}); registration is idempotent");
            plan_id
        }
    };
    report_scheduler_office(&launch.registry, &plan_id)
}

#[cfg(any(fern_role_operator, fern_role_scheduler))]
fn preflight_stage_driven_run_plan(
    definition: &fern_run::run_plan::RunPlanDefinitionV1,
    profile_id: &str,
) -> Fallible<PreparedRunPlan> {
    let deployment_guard = crate::binary_deploy::lock_for_plan_start(profile_id)?;
    definition
        .validate()
        .map_err(|error| invalid_input(format!("invalid run plan: {error}")))?;
    let compiled_profile = match (
        definition.compiled_experiment.as_ref(),
        definition.compiled_ablation.as_ref(),
    ) {
        (Some(compiled), None) => compiled.execution_profile_id(),
        (None, Some(ablation)) => ablation.execution_profile_id(),
        (Some(_), Some(_)) => {
            return Err(invalid_input(
                "Stage-driven Plan cannot carry both a compiled Experiment and a compiled Ablation",
            ));
        }
        (None, None) => {
            return Err(invalid_input(
                "Stage-driven Plan must carry a compiled Experiment or a compiled Ablation",
            ));
        }
    };
    if compiled_profile != profile_id {
        return Err(invalid_input(format!(
            "compiled object targets {compiled_profile}, but registration used {profile_id}"
        )));
    }
    let profile = crate::experiment_v2::resolve_model_link_profile(profile_id)?;
    let registry_home = resolve_registry_home_profile(&profile)?;
    // CPU is valid for non-training Stage graphs. New train registrations are
    // refused in experiment_v2, and the Python worker rechecks CUDA at runtime.
    let mut prepared = PreparedRunPlan {
        registry: registry_home.declaration.campaign_root.join("lifecycle.db"),
        execution_profile_revision: profile.revision.revision_hash.clone(),
        log_root: registry_home.declaration.work_root.join("run-plans"),
        worker_profile: profile,
        _deployment_guard: None,
    };
    prepared._deployment_guard = deployment_guard;
    Ok(prepared)
}

/// One line the submitter needs after registering or resuming: is dispatch
/// actually possible? A pause or an absent scheduler leaves the Plan waiting
/// forever, and silence here is how that reads as a launch. An unavailable or
/// unreadable dispatch route FAILS the command (non-zero): "registered" beside
/// an unusable dispatcher is not a determined outcome, and the caller's shell
/// must not read it as one. The registration/receipt is durable either way.
#[cfg(any(fern_role_operator, fern_role_scheduler))]
/// A registered Plan owns no machine. That separation is correct for queueing,
/// but an externally provisioned GPU can keep billing while the Plan waits.
/// Keep the warning identical at submission and in every monitor projection.
fn idle_external_resource_warning() -> &'static str {
    "BILLING WARNING: if this execution profile uses a rented external Worker Site, that machine may be idle and still accruing charges; stop it at the provider until dispatch is possible. Fern has not claimed a GPU."
}

fn report_scheduler_office(registry: &Path, plan_id: &str) -> Fallible<()> {
    let route = scheduler_route_via_csharp_registry(registry).map_err(|error| {
        invalid_input(format!(
            "the request is durably recorded for plan {plan_id}, but the scheduling-pause state cannot be read: {error}. Exiting non-zero because whether anything will dispatch is undetermined."
        ))
    })?;
    if let Some(pause) = route.scheduling_pause {
        return Err(invalid_input(format!(
            "the request is durably recorded for plan {plan_id}, but scheduling is paused by {} ({}), so it has no route to a plan lease. {}",
            pause.paused_by,
            pause.reason,
            idle_external_resource_warning()
        )));
    }
    match route.scheduler_office.fencing_epoch {
        Some(office) => {
            println!(
                "scheduler epoch {} holds office; monitor with: fern-run monitor --plan {plan_id}",
                office
            );
            println!("{}", idle_external_resource_warning());
            Ok(())
        }
        None => Err(invalid_input(format!(
            "the request is durably recorded for plan {plan_id}, but no scheduler holds office, so it has no route to a plan lease. Start `fern-run scheduler-serve` (or enable the installed service). {}",
            idle_external_resource_warning()
        ))),
    }
}

pub(crate) fn parse_run_plan_stage(raw: &str) -> Fallible<fern_run::run_plan::RunPlanStepV1> {
    let parts = raw.split(':').collect::<Vec<_>>();
    // The optional sixth token asks the worker to KEEP `raw_tensors.safetensors`
    // instead of deleting it after scoring. A model that emits its point
    // forecast natively does not need it, but one whose typed point output is
    // derived from those tensors cannot be committed without them: on
    // 2026-08-11 a Solar TimeMixer cell trained to completion on the Pod and
    // then failed at commit with "typed point output requires retained
    // raw_tensors.safetensors", losing the whole cell's compute. The cost is
    // disk, so it stays a per-stage decision rather than a global default.
    let (head, keep_forecast_origins) = match parts.as_slice() {
        [head @ .., last] if KEEP_ORIGINS_STAGE_TOKEN.eq_ignore_ascii_case(last) => (head, true),
        all => (all, false),
    };
    let [step_id, lane, preset, model, limit] = head else {
        return Err(invalid_input(format!(
            "invalid --stage {raw:?}; expected STEP_ID:LANE:PRESET:MODEL:LIMIT[:{KEEP_ORIGINS_STAGE_TOKEN}], \
             for example timemixer:real_sensor_h64:paper-hugadb-h64:timemixer:25"
        )));
    };
    if [step_id, lane, preset, model].iter().any(|part| part.trim().is_empty()) {
        return Err(invalid_input(format!(
            "invalid --stage {raw:?}; STEP_ID, LANE, PRESET, and MODEL must all be non-empty"
        )));
    }
    if !valid_lane_name(lane) {
        return Err(invalid_input(format!(
            "invalid --stage {raw:?}; LANE must be one directory-name segment with no separators"
        )));
    }
    let limit = limit.parse::<usize>().map_err(|_| {
        invalid_input(format!(
            "invalid --stage {raw:?}; LIMIT must be a positive whole number"
        ))
    })?;
    if limit == 0 {
        return Err(invalid_input(format!(
            "invalid --stage {raw:?}; LIMIT must be greater than zero"
        )));
    }
    Ok(run_plan_step(
        step_id,
        lane,
        preset,
        model,
        limit,
        keep_forecast_origins,
    ))
}

/// The token a `--stage` may end with to retain per-origin raw tensors.
const KEEP_ORIGINS_STAGE_TOKEN: &str = "keep-origins";

fn run_plan_step(
    step_id: &str,
    lane: &str,
    preset: &str,
    model: &str,
    limit: usize,
    keep_forecast_origins: bool,
) -> fern_run::run_plan::RunPlanStepV1 {
    fern_run::run_plan::RunPlanStepV1 {
        step_id: step_id.to_string(),
        lane: lane.to_string(),
        preset: preset.to_string(),
        model: model.to_string(),
        limit,
        keep_forecast_origins,
        recipe_snapshot: None,
        compiled_experiment: None,
    }
}

/// The compatibility reader for an old Plan that named only a catalog recipe.
/// C# admits the name and returns its typed immutable identity; Rust merely
/// reconstructs that stored V1 shape for the scheduler's existing snapshot
/// and worker-packet adapters.
fn csharp_recipe_by_name(name: &str) -> Fallible<RecipeBlueprint> {
    fern_run::csharp_experiment::select_catalog_recipes(Some(name), None, &[])
        .map_err(invalid_input)?
        .into_iter()
        .next()
        .ok_or_else(|| invalid_input(format!("C# selected no recipe for exact name {name:?}")))
}

/// C# owns V1 recipe selection and both legacy identities.  The Rust caller
/// keeps the receipt only long enough to feed the historical registry and
/// frozen-plan reader shapes that have not changed with this cutover.
fn csharp_recipe_receipt(recipe: &RecipeBlueprint) -> Fallible<fern_run::csharp_experiment::RecipeTargetsReceipt> {
    fern_run::csharp_experiment::resolve_recipe_run_targets_from_blueprint(recipe, DeviceKind::Cuda, 42, 1000)
        .map_err(invalid_input)
}

fn csharp_recipe_hash(recipe: &RecipeBlueprint) -> Fallible<String> {
    Ok(csharp_recipe_receipt(recipe)?.recipe_hash)
}

fn csharp_recipe_preimage(recipe: &RecipeBlueprint) -> Fallible<Value> {
    serde_json::to_value(csharp_recipe_receipt(recipe)?.recipe_identity).map_err(Into::into)
}

fn load_recipe_extensions(paths: &[PathBuf]) -> Fallible<BTreeMap<String, RecipeBlueprint>> {
    let mut source_names = Vec::with_capacity(paths.len());
    let mut extension_jsons = Vec::with_capacity(paths.len());
    for path in paths {
        let bytes = std::fs::read(path)
            .map_err(|error| invalid_input(format!("cannot read recipe declaration {}: {error}", path.display())))?;
        let extension_json = String::from_utf8(bytes).map_err(|error| {
            invalid_input(format!(
                "recipe declaration {} is not UTF-8 for the C# admission boundary: {error}",
                path.display()
            ))
        })?;
        source_names.push(path.to_string_lossy().into_owned());
        extension_jsons.push(extension_json);
    }

    let documents = source_names
        .iter()
        .zip(&extension_jsons)
        .map(|(source_name, extension_json)| fern_run::csharp_experiment::RecipeExtensionAdmissionSource {
            source_name,
            extension_json,
        })
        .collect::<Vec<_>>();

    let admitted = fern_run::csharp_experiment::admit_recipe_extensions(&documents).map_err(invalid_input)?;
    let mut recipes = BTreeMap::new();
    for declaration in admitted {
        let recipe = fern_run::csharp_experiment::recipe_blueprint_from_csharp_identity(&declaration.identity).map_err(|error| {
            invalid_input(format!(
                "C# admitted recipe declaration {} has an unmaterializable immutable transport: {error}",
                declaration.source_name
            ))
        })?;
        let recipe_name = recipe.name();
        if recipe_name != declaration.recipe_name {
            return Err(invalid_input(format!(
                "C# admitted recipe declaration {} changed identity name {:?} into {:?}",
                declaration.source_name, recipe_name, declaration.recipe_name
            )));
        }
        if recipes.insert(recipe_name.clone(), recipe).is_some() {
            return Err(invalid_input(format!(
                "C# admission receipt repeats recipe {recipe_name:?}; one recipe name has one authority"
            )));
        }
    }
    Ok(recipes)
}

fn recipe_from_snapshot(snapshot: &fern_run::run_plan::RunPlanRecipeSnapshotV1) -> Fallible<RecipeBlueprint> {
    snapshot
        .validate()
        .map_err(|error| invalid_input(format!("invalid frozen recipe snapshot: {error}")))?;
    let identity: fern_recipes::LegacyRecipeIdentityV1 = serde_json::from_value(snapshot.identity.clone())
        .map_err(|error| invalid_input(format!("cannot parse frozen recipe identity: {error}")))?;
    let receipt = fern_run::csharp_experiment::resolve_recipe_run_targets(&identity, DeviceKind::Cuda, 42, 1000)
        .map_err(invalid_input)?;
    if receipt.recipe_hash != snapshot.recipe_hash {
        return Err(invalid_input(format!(
            "C# frozen recipe receipt hash {} disagrees with snapshot {}",
            receipt.recipe_hash, snapshot.recipe_hash
        )));
    }
    fern_run::csharp_experiment::recipe_blueprint_from_csharp_identity(&receipt.recipe_identity)
        .map_err(|error| invalid_input(format!("cannot materialize C# frozen recipe transport: {error}")))
}

fn freeze_run_plan_recipe(
    mut step: fern_run::run_plan::RunPlanStepV1,
    external_recipes: &BTreeMap<String, RecipeBlueprint>,
) -> Fallible<fern_run::run_plan::RunPlanStepV1> {
    let recipe = match external_recipes.get(&step.preset) {
        Some(recipe) => recipe.clone(),
        None => exact_recipe_for_plan_step(&step)?,
    };
    step.recipe_snapshot = Some(fern_run::run_plan::RunPlanRecipeSnapshotV1 {
        schema: fern_run::run_plan::RUN_PLAN_RECIPE_SNAPSHOT_SCHEMA.to_string(),
        recipe_hash: csharp_recipe_hash(&recipe)?,
        identity: csharp_recipe_preimage(&recipe)?,
    });
    // Presets are authoring shortcuts. The durable Plan names the exact recipe
    // it froze, so dispatch never needs the mutable preset or declaration.
    step.preset = recipe.name();
    Ok(step)
}

/// Identify an immutable source store from a Matrix/scheduler WorkingDirectory.
///
/// An ordinary worker has no embedded source archive, but a V2 Matrix still
/// starts inside its Plan-pinned implementation root. Treating that directory
/// as a workspace would append `deploy/implementation-source-roots` a second
/// time. A source-bound legacy binary additionally proves that the directory
/// name agrees with the archive embedded in that binary.
fn implementation_source_store(current: &Path, observed_archive_sha256: Option<&str>) -> Fallible<Option<PathBuf>> {
    let source_store = current
        .parent()
        .filter(|parent| parent.file_name().and_then(|name| name.to_str()) == Some("implementation-source-roots"));
    match (source_store, observed_archive_sha256) {
        (Some(source_store), Some(observed_archive_sha256)) => {
            if current.file_name().and_then(|name| name.to_str()) != Some(observed_archive_sha256) {
                return Err(invalid_input(format!(
                    "installed binary reports source archive {observed_archive_sha256}, but its WorkingDirectory is {}; the installation receipt is inconsistent",
                    current.display()
                )));
            }
            Ok(Some(source_store.to_path_buf()))
        }
        (Some(source_store), None) => Ok(Some(source_store.to_path_buf())),
        (None, Some(observed_archive_sha256)) => Err(invalid_input(format!(
            "installed binary reports source archive {observed_archive_sha256}, but its WorkingDirectory {} is not beneath an implementation-source-roots directory",
            current.display()
        ))),
        (None, None) => Ok(None),
    }
}

/// The two static things a stage needs that used to be discovered by failing.
///
/// The source tree and retention requirement are decidable before any worker
/// starts. A recorded source's path belongs to the explicit Python DGP-bank
/// request: that request performs the owner-side validation and returns a
/// typed refusal before the training worker, rather than this recipe-only
/// preflight reopening a retired Rust path adapter.
pub(crate) fn preflight_stage_inputs(
    step: &fern_run::run_plan::RunPlanStepV1,
    recipe: &RecipeBlueprint,
) -> Fallible<()> {
    // 1. The reviewed Python source, which is hashed into implementation
    //    identity. A binary deployed away from its build tree cannot find it,
    //    and that failure used to arrive once per cell.
    reviewed_source_root()?;

    // 2. Retention. A trained cell's per-origin tensors are deleted by the
    //    parent unless retention was asked for, and its typed point forecast is
    //    derived from them. Saying so here costs nothing; discovering it at
    //    commit costs the whole cell.
    if recipe.model.fits_weights() && !step.keep_forecast_origins {
        return Err(invalid_input(format!(
            "run plan step {} runs {}, whose typed point forecast is derived from the retained per-origin tensors, \
             but the stage does not ask to keep them: the worker would train to completion and then fail to commit. \
             End the stage with `:{KEEP_ORIGINS_STAGE_TOKEN}`. No worker started.",
            step.step_id,
            recipe.model.value()
        )));
    }
    Ok(())
}

/// The single recipe a stage names, whether it named a preset or the recipe.
///
/// A stage IS one Matrix, and a Matrix is expanded from one recipe -- one world
/// and one model. A preset naming a single world resolves to exactly that, which
/// is why `paper-solar-h64` works. A preset spanning several worlds resolves to
/// one recipe PER WORLD for the same model, and there is no way for this
/// function to know which of them the stage meant, so it refused.
///
/// That refusal made every multi-world preset unrunnable through the plan route
/// -- the tail-law lanes (four worlds each) and the OU/seasonal lane among them.
/// The missing piece was never in the Matrix model; it was that a stage could
/// only say "preset", and a preset is not always one Matrix. A recipe always is,
/// so a stage may now name one directly and a multi-world preset simply becomes
/// several stages, which is what it always was.
pub(crate) fn exact_recipe_for_plan_step(step: &fern_run::run_plan::RunPlanStepV1) -> Fallible<RecipeBlueprint> {
    if let Some(snapshot) = &step.recipe_snapshot {
        let recipe = recipe_from_snapshot(snapshot)?;
        if recipe.name() != step.preset {
            return Err(invalid_input(format!(
                "run plan step {} names recipe {:?}, but its frozen identity names {:?}",
                step.step_id,
                step.preset,
                recipe.name()
            )));
        }
        if recipe.model.value() != step.model {
            return Err(invalid_input(format!(
                "run plan step {} declares model {:?}, but its frozen recipe declares {:?}",
                step.step_id,
                step.model,
                recipe.model.value()
            )));
        }
        return Ok(recipe);
    }

    // Compatibility reader for Plans registered before recipe snapshots. New
    // Plans never reach this catalog-dependent branch.
    let recipe = if let Ok(recipe) = csharp_recipe_by_name(&step.preset) {
        let model = RecipeModelName::parse(&step.model).map_err(invalid_input)?;
        if recipe.model != model {
            return Err(invalid_input(format!(
                "run plan step {} names recipe {:?}, which is a {:?} recipe, but the step declares model {:?}",
                step.step_id, step.preset, recipe.model, step.model
            )));
        }
        recipe
    } else {
        let model = RecipeModelName::parse(&step.model).map_err(invalid_input)?;
        let mut recipes = fern_run::csharp_experiment::select_catalog_recipes(
            None,
            Some(&step.preset),
            &[model.value()],
        )
        .map_err(invalid_input)?;
        if recipes.len() != 1 {
            let names = recipes.iter().map(RecipeBlueprint::name).collect::<Vec<_>>();
            return Err(invalid_input(format!(
                "run plan step {} expands to {} recipes; every ordered step must identify exactly one Matrix. \
                 This preset spans several worlds, so name one recipe per stage instead: {}",
                step.step_id,
                recipes.len(),
                names.join(", ")
            )));
        }
        recipes.remove(0)
    };
    Ok(recipe)
}

/// Expand the exact recipe and refuse a stage that presents LIMIT as a hidden
/// sampler. A stage is one complete scientific Matrix; a deliberately smaller
/// experiment needs a recipe whose grid declares that smaller coordinate set.
///
/// Previously `LIMIT=5` on a 28-cell recipe registered only the deterministic
/// prefix and silently omitted 23 declared cells. The queue still carried the
/// full recipe name, so the durable object claimed a different experiment from
/// the one it would execute.
pub(crate) fn complete_recipe_stage_targets(
    step: &fern_run::run_plan::RunPlanStepV1,
    recipe: &RecipeBlueprint,
    device: DeviceKind,
) -> Fallible<Vec<fern_run::db::RecipeTargetRegistration>> {
    let targets = recipe_target_registrations_for_device(recipe, 42, 1000, device)?;
    if step.limit != targets.len() {
        let mismatch = if step.limit < targets.len() {
            format!("would drop {} declared Coordinates", targets.len() - step.limit)
        } else {
            format!(
                "requests {} Coordinates that the recipe does not contain",
                step.limit - targets.len()
            )
        };
        return Err(invalid_input(format!(
            "run plan step {} declares LIMIT {}, but recipe {} expands to exactly {} Coordinates; this {mismatch}. \
             LIMIT must equal the complete recipe expansion. Declare a recipe with the intended smaller grid instead. \
             No worker started.",
            step.step_id,
            step.limit,
            recipe.name(),
            targets.len()
        )));
    }
    Ok(targets)
}

#[cfg(any(fern_role_operator, fern_role_scheduler))]
fn publish_run_plan_supervisor_binary(
    log_root: &Path,
    profile: &ResolvedExecutionProfile,
    installed_local_worker: &fern_run::binary_deployment::BinaryVersionIdentity,
) -> Fallible<fern_run::binary_deployment::BinaryPublication> {
    fern_run::binary_deployment::verify_binary_version(installed_local_worker).map_err(|error| {
        invalid_input(format!(
            "installed fern-worker failed byte verification before Plan dispatch: {error}"
        ))
    })?;
    if profile.declaration.executor == ExecutionProfileExecutor::Ssh {
        let remote_digest =
            profile.declaration.worker_binary_sha256.as_deref().ok_or_else(|| {
                invalid_input("SSH profile has no deployed binary digest; no Plan authority was claimed")
            })?;
        if remote_digest != installed_local_worker.digest().as_str() {
            return Err(invalid_input(format!(
                "SSH profile worker digest {remote_digest} differs from scheduler binary-set worker digest {}; the profile may attest the remote mirror but cannot choose Plan supervisor bytes, so no Plan authority was claimed",
                installed_local_worker.digest()
            )));
        }
    }
    let publication = fern_run::binary_deployment::publish_binary_version(
        installed_local_worker.executable_path(),
        &log_root.join("binaries"),
        fern_run::binary_deployment::BinaryExecutableKind::Worker,
    )
    .map_err(|error| {
        invalid_input(format!(
            "cannot publish the immutable Fern worker before claiming Plan authority: {error}"
        ))
    })?;
    if publication.identity().digest() != installed_local_worker.digest() {
        return Err(invalid_input(
            "installed fern-worker changed while it was being published; no Plan authority was claimed",
        ));
    }
    Ok(publication)
}

pub(crate) fn local_hostname() -> String {
    std::env::var("HOSTNAME")
        .ok()
        .filter(|hostname| !hostname.trim().is_empty())
        .or_else(|| {
            std::fs::read_to_string("/proc/sys/kernel/hostname")
                .ok()
                .map(|hostname| hostname.trim().to_string())
                .filter(|hostname| !hostname.is_empty())
        })
        .unwrap_or_else(|| "unknown-host".to_string())
}

/// A Worker Site has no access to Registry Home's live database.  Its one
/// returned package must therefore keep forecast-origin tensors until the
/// Registry Home has reopened and sealed the typed evidence.  Rejecting this
/// before a lease or worker exists turns a late artifact-commit failure into a
/// clear preflight refusal.
fn require_remote_typed_evidence_retention(is_remote: bool, keeps_forecast_origins: bool) -> Fallible<()> {
    if !is_remote || keeps_forecast_origins {
        return Ok(());
    }
    Err(invalid_input(
        "an SSH Worker Site returns typed forecast evidence and requires --keep-forecast-origins; \
         without it worker cleanup would discard tensors before Fern can seal them. \
         No GPU work started.",
    ))
}

fn unix_seconds() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}

/// The one Matrix-registration path shared by a one-Matrix run and a declared
/// ordered run plan.  The plan may choose the next Matrix; only this function
/// turns that declared choice into durable Coordinates.
fn declare_registered_recipe_matrix(
    lifecycle_db: &Path,
    registry_home_binary: &Path,
    lane_db: &Path,
    recipe_name: &str,
    target_rows: &[fern_run::db::RecipeTargetRegistration],
    declared_replica: Option<&DeclaredReplicaV1>,
    implementation_source_snapshot_id: Option<&str>,
    actor: &str,
) -> Fallible<String> {
    use fern_run::lifecycle::DeclaredCoordinate;

    let coordinates: Vec<DeclaredCoordinate> = target_rows
        .iter()
        .map(|row| match declared_replica {
            Some(replica) => DeclaredCoordinate::replica(
                row.target_hash.clone(),
                replica.ordinal,
                replica.purpose.reason(),
                row.target_json.clone(),
            ),
            None => DeclaredCoordinate::unit(row.target_hash.clone(), row.target_json.clone()),
        })
        .map(|coordinate| match implementation_source_snapshot_id {
            Some(snapshot_id) => coordinate.with_matrix_implementation_source(snapshot_id),
            None => coordinate,
        })
        .collect();
    let lane = lane_db
        .parent()
        .and_then(|path| path.file_name())
        .map(|name| name.to_string_lossy().to_string())
        .unwrap_or_else(|| "unnamed_lane".to_string());
    crate::apply_registry_migrations_via_home(Some(registry_home_binary), lifecycle_db)?;
    fern_run::lifecycle::declare_matrix(
        lifecycle_db,
        &lane,
        Some(recipe_name),
        target_rows.first().map(|row| row.recipe_hash.as_str()),
        &coordinates,
        actor,
        None,
    )
    .map_err(|error| invalid_input(format!("could not declare the sweep Matrix: {error}")))
}

/// Build a canonical manifest from reviewed, logical workspace paths. This is
/// deliberately not a recursive filesystem walk: caches, results, and local
/// scratch files must never enter implementation identity accidentally.
///
/// Where the reviewed Python source actually lives, at run time.
///
/// This used to be `env!("CARGO_MANIFEST_DIR")`, which is the directory the
/// binary was COMPILED in, baked in as a literal. That is right on the machine
/// that built it and wrong everywhere else: a binary built on the laptop and
/// deployed to the Pod looked for `/home/user/study/fr.py` on the Pod and
/// refused to start every cell, naming a path that machine had never had.
///
/// The source that gets hashed must be the source sitting beside the run, so it
/// is found from the working directory, walking up until a tree carrying the
/// reviewed root files appears. `FERN_SOURCE_ROOT` overrides it for a caller
/// that runs from somewhere else entirely. Failing with the directories it
/// searched beats hashing whatever happens to be at a compile-time path.
/// THE FILES WHOSE BYTES ARE THE MODEL IMPLEMENTATION'S IDENTITY.
///
/// A Worker Site must carry the same bytes before Python starts. These are
/// compute inputs, not the whole Fern workspace: changing a dashboard or a
/// knowledge crate must not manufacture a new numerical model implementation.
///
/// This is a whole class of bug and it bit four different ways on 2026-08-12,
/// none of which looked like each other:
///
/// - a Python fix (`fr_tr_exp.py`, Zeus's median) — expected, and still killed
///   a lane because nobody re-pushed;
/// - `Cargo.lock`, from REMOVING an unrelated Rust dependency. No Python
///   moved. That incident is why Rust build identity is now deployment
///   metadata instead of model implementation identity;
/// - `cargo build` while a plan supervisor still had to exec
///   `target/debug/fern-run` at its next stage boundary (correction 279);
/// - an execution-profile edit, which mints a new resource slot while the old
///   slot's lease still holds the card (correction 287).
///
/// The shape: an edit here creates a new model implementation for future work.
/// A plan must eventually pin one immutable deployment rather than reread the
/// author's live checkout at every stage boundary.
const REVIEWED_WORKER_SOURCE: &[&str] = &[
    "fr.py",
    "fr_core.py",
    "fr_cfg.py",
    "fr_ext_action.py",
    "fr_ext_density.py",
    "fr_loss.py",
    "fr_specs.py",
    "fr_tr.py",
    "fr_tr_data.py",
    "fr_tr_exp.py",
    "serie/noise.py",
    "serie/pipeline.py",
];

/// Dependency locks that can change Python/Serie numerical behavior.
///
/// Root `Cargo.lock` instead belongs to the Rust deployment bundle. Including
/// it here made adding `fern-knowledge` look like a different trained model.
const REVIEWED_MODEL_DEPENDENCY_LOCKS: &[&str] = &["uv.lock", "serie/uv.lock"];

/// Rust build inputs checked when comparing this checkout with a deployed
/// Worker Site. They do not enter [`ModelImplementationCardV1`].
const REVIEWED_RUST_DEPLOYMENT_INPUTS: &[&str] = &["Cargo.lock"];

/// The Python a worker executes, without the dependency locks.
///
/// The Stage bundle separates these two halves because only one of them has to
/// be present. Missing worker source means the bundle runs a different program;
/// a missing lock does not, and `serie/uv.lock` is ignored by Git, so a frozen
/// SourceContent can never hold it.
#[cfg(any(fern_role_operator, fern_role_scheduler, fern_role_matrix_worker, test))]
pub(crate) fn reviewed_worker_source() -> Vec<String> {
    REVIEWED_WORKER_SOURCE.iter().map(|path| (*path).to_string()).collect()
}

/// Resolve the reviewed Fern workspace used for worker inputs and service units.
pub(crate) fn reviewed_source_root() -> Fallible<PathBuf> {
    // One file that exists in the reviewed set and nowhere else; finding it is
    // what makes a directory the workspace rather than a directory above it.
    const ROOT_MARKER: &str = "fr_core.py";
    if let Ok(declared) = std::env::var("FERN_SOURCE_ROOT") {
        let root = PathBuf::from(&declared);
        if root.join(ROOT_MARKER).is_file() {
            return Ok(root);
        }
        return Err(invalid_input(format!(
            "FERN_SOURCE_ROOT={declared} does not contain {ROOT_MARKER}; it must name the reviewed Fern source tree"
        )));
    }
    let start =
        std::env::current_dir().map_err(|error| invalid_input(format!("cannot read working directory: {error}")))?;
    let mut searched = Vec::new();
    for candidate in start.ancestors() {
        if candidate.join(ROOT_MARKER).is_file() {
            return Ok(candidate.to_path_buf());
        }
        searched.push(candidate.display().to_string());
    }
    Err(invalid_input(format!(
        "cannot find the reviewed Fern source tree ({ROOT_MARKER}) from {} or any parent ({}); \
         run from the repository or set FERN_SOURCE_ROOT",
        start.display(),
        searched.join(", ")
    )))
}

#[cfg(any(fern_role_operator, fern_role_scheduler, fern_role_matrix_worker, test))]
fn require_unchanged_profile_revision(
    profile_id: &str,
    subject: &str,
    expected_revision: &str,
    observed_revision: &str,
) -> Fallible<()> {
    if expected_revision == observed_revision {
        return Ok(());
    }
    Err(invalid_input(format!(
        "cannot start {subject}: execution profile {profile_id:?} changed after authority was acquired (expected {expected_revision}, now {observed_revision}). Stop and deliberately resume under the new revision; no worker started."
    )))
}

/// The commit rename above is atomic only within one filesystem. A profile
/// pairing a work root and an artifact root on two devices would train a cell
/// and then fail at the exact moment the result exists nowhere durable, so
/// the pairing is refused at launch, before any authority is claimed. The
/// roots need not exist yet — the nearest existing ancestor names the device
/// the directory will be created on.
fn require_one_filesystem_for_commit_rename(work_root: &Path, artifact_root: &Path) -> Fallible<()> {
    let work_device = nearest_existing_ancestor_device(work_root)?;
    let artifact_device = nearest_existing_ancestor_device(artifact_root)?;
    if work_device != artifact_device {
        return Err(invalid_input(format!(
            "cannot start: work root {} and artifact root {} live on different filesystems \
             (device {work_device} vs {artifact_device}), so the atomic commit rename that moves \
             a verified result to its durable home cannot work there. Declare both roots on one \
             filesystem. No GPU work started.",
            work_root.display(),
            artifact_root.display()
        )));
    }
    Ok(())
}

fn nearest_existing_ancestor_device(path: &Path) -> Fallible<u64> {
    use std::os::unix::fs::MetadataExt;
    let mut candidate = path;
    loop {
        match std::fs::metadata(candidate) {
            Ok(metadata) => return Ok(metadata.dev()),
            Err(_) => {
                candidate = candidate.parent().ok_or_else(|| {
                    invalid_input(format!(
                        "no existing ancestor of {} to read a filesystem device from",
                        path.display()
                    ))
                })?;
            }
        }
    }
}

pub(crate) fn remote_ssh_helper_path() -> Fallible<PathBuf> {
    let workspace_root = reviewed_source_root()?;
    let helper = workspace_root.join("scripts/connect_runpod_ssh.sh");
    if !helper.is_file() {
        return Err(invalid_input(format!(
            "maintained RunPod SSH helper {} is missing; Fern will not construct an ad-hoc ssh command",
            helper.display()
        )));
    }
    Ok(helper)
}

#[cfg(test)]
mod tests {
    use super::*;
    use clap::CommandFactory;

    fn fixture_observed_execution() -> fern_execution_profile::ObservedExecutionFingerprint {
        use fern_execution_profile::{
            OBSERVED_EXECUTION_FINGERPRINT_SCHEMA, ObservedCudaDevice, ObservedExecutionFingerprint, ObservedPlatform,
            ObservedPythonRuntime, ObservedTorchRuntime,
        };

        ObservedExecutionFingerprint {
            schema: OBSERVED_EXECUTION_FINGERPRINT_SCHEMA.to_string(),
            observed_at_unix_ms: 1_700_000_000_000,
            fern_version: "test-version".to_string(),
            platform: ObservedPlatform {
                hostname: "test-host".to_string(),
                system: "Linux".to_string(),
                release: "test-release".to_string(),
                machine: "x86_64".to_string(),
            },
            python: ObservedPythonRuntime {
                requested_executable: "/test/python".into(),
                sys_executable: "/test/python".into(),
                version: "3.12.0".to_string(),
                implementation: "CPython".to_string(),
                worker_module: "fr_tr".to_string(),
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
                name: "NVIDIA test GPU".to_string(),
                capability: [8, 9],
                total_memory_bytes: 8 * 1_073_741_824,
                multi_processor_count: 36,
                uuid: Some("GPU-test".to_string()),
                driver_version: Some("600.1".to_string()),
            }],
        }
    }

    #[test]
    fn no_plan_lease_names_external_gpu_billing_risk() {
        let warning = idle_external_resource_warning();
        assert!(warning.starts_with("BILLING WARNING:"), "got: {warning}");
        assert!(warning.contains("idle and still accruing charges"), "got: {warning}");
        assert!(warning.contains("stop it at the provider"), "got: {warning}");
        assert!(warning.contains("has not claimed a GPU"), "got: {warning}");
    }

    #[test]
    fn rust_cancel_matrix_caller_refuses_malformed_csharp_receipts() {
        let matrix_id = "19670880c0f9abe3";
        let coordinator_id = "a".repeat(64);
        let attempt_id = "b".repeat(64);
        let receipt_id = "c".repeat(64);
        let make_receipt = |schema: &str,
                            status: &str,
                            matrix: &str,
                            coordinator_lease_ids: Vec<String>,
                            abandoned_attempt_ids: Vec<String>,
                            transition_receipt_ids: Vec<String>| CsharpCancelMatrixReceipt {
            schema: schema.to_string(),
            status: status.to_string(),
            matrix_id: matrix.to_string(),
            coordinator_lease_ids,
            abandoned_attempt_ids,
            transition_receipt_ids,
        };

        validate_csharp_cancel_matrix_receipt(
            &make_receipt(
                CancelMatrixReport::SCHEMA_ID,
                "already-inactive",
                matrix_id,
                vec![],
                vec![],
                vec![],
            ),
            matrix_id,
        )
        .unwrap();
        validate_csharp_cancel_matrix_receipt(
            &make_receipt(
                CancelMatrixReport::SCHEMA_ID,
                "cancelled",
                matrix_id,
                vec![coordinator_id.clone()],
                vec![attempt_id.clone()],
                vec![receipt_id.clone()],
            ),
            matrix_id,
        )
        .unwrap();

        let malformed = [
            make_receipt(
                "fern.cancel_matrix.v0",
                "already-inactive",
                matrix_id,
                vec![],
                vec![],
                vec![],
            ),
            make_receipt(
                CancelMatrixReport::SCHEMA_ID,
                "already-inactive",
                "wrong-matrix",
                vec![],
                vec![],
                vec![],
            ),
            make_receipt(
                CancelMatrixReport::SCHEMA_ID,
                "unknown",
                matrix_id,
                vec![],
                vec![],
                vec![],
            ),
            make_receipt(
                CancelMatrixReport::SCHEMA_ID,
                "already-inactive",
                matrix_id,
                vec![coordinator_id.clone()],
                vec![],
                vec![],
            ),
            make_receipt(
                CancelMatrixReport::SCHEMA_ID,
                "cancelled",
                matrix_id,
                vec![],
                vec![],
                vec![],
            ),
            make_receipt(
                CancelMatrixReport::SCHEMA_ID,
                "cancelled",
                matrix_id,
                vec![coordinator_id.to_ascii_uppercase()],
                vec![attempt_id],
                vec![receipt_id.clone()],
            ),
            make_receipt(
                CancelMatrixReport::SCHEMA_ID,
                "cancelled",
                matrix_id,
                vec![coordinator_id],
                vec![],
                vec![receipt_id.clone(), receipt_id],
            ),
        ];
        for receipt in malformed {
            assert!(validate_csharp_cancel_matrix_receipt(&receipt, matrix_id).is_err());
        }
    }

    #[test]
    fn registered_plan_route_caller_crosses_one_csharp_process_for_an_empty_office() {
        let root = std::env::temp_dir().join(format!(
            "fern-no-scheduler-submission-billing-warning-{}-{}",
            std::process::id(),
            unix_seconds()
        ));
        std::fs::create_dir_all(&root).unwrap();
        let registry = root.join("lifecycle.db");
        crate::apply_registry_migrations_via_home(Some(go_home_test_binary()), &registry).unwrap();

        let error = report_scheduler_office(&registry, "plan-test")
            .expect_err("an absent scheduler must not read as a successful launch")
            .to_string();
        assert!(error.contains("durably recorded"), "got: {error}");
        assert!(error.contains("no scheduler holds office"), "got: {error}");
        assert!(error.contains("BILLING WARNING:"), "got: {error}");

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    #[cfg(unix)]
    fn ssh_profile_cannot_choose_a_plan_supervisor_outside_the_scheduler_binary_set() {
        let root = std::env::temp_dir().join(format!(
            "fern-ssh-worker-selection-{}-{}",
            std::process::id(),
            unix_seconds()
        ));
        let installed = fern_run::binary_deployment::publish_binary_version(
            Path::new("/bin/true"),
            &root.join("installed-worker"),
            fern_run::binary_deployment::BinaryExecutableKind::Worker,
        )
        .unwrap();
        let installed_identity = installed.identity();
        let _installed = installed.finish_registry_handoff();
        let profile = ExecutionProfile::ssh_template(
            "ssh-test".to_string(),
            "worker@example.invalid".to_string(),
            "local-test".to_string(),
            "f".repeat(64),
            Path::new("/remote/fern"),
            Path::new("/remote/home"),
        )
        .resolve()
        .unwrap();

        let error = publish_run_plan_supervisor_binary(&root.join("plan"), &profile, &installed_identity)
            .unwrap_err()
            .to_string();
        assert!(error.contains("SSH profile worker digest"), "{error}");
        assert!(error.contains("scheduler binary-set worker digest"), "{error}");
        assert!(error.contains(&"f".repeat(64)), "{error}");
        assert!(error.contains(installed_identity.digest().as_str()), "{error}");
        assert!(error.contains("no Plan authority was claimed"), "{error}");
        assert!(!root.join("plan/binaries").exists());

        let matching_profile = ExecutionProfile::ssh_template(
            "ssh-test".to_string(),
            "worker@example.invalid".to_string(),
            "local-test".to_string(),
            installed_identity.digest().as_str().to_string(),
            Path::new("/remote/fern"),
            Path::new("/remote/home"),
        )
        .resolve()
        .unwrap();
        let published =
            publish_run_plan_supervisor_binary(&root.join("plan"), &matching_profile, &installed_identity).unwrap();
        assert_eq!(published.identity().digest(), installed_identity.digest());
        let _published = published.finish_registry_handoff();

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn deployment_mismatch_names_all_three_binary_facts_before_launch() {
        let error = require_matching_binary_deployment(
            "runpod-5090",
            &"a".repeat(64),
            &"b".repeat(64),
            &"c".repeat(64),
            Path::new("/remote/bin/fern-worker.a"),
        )
        .unwrap_err()
        .to_string();

        assert!(error.contains(&"a".repeat(64)), "{error}");
        assert!(error.contains(&"b".repeat(64)), "{error}");
        assert!(error.contains(&"c".repeat(64)), "{error}");
        assert!(error.contains("deployment mismatch"), "{error}");
        assert!(
            error.contains("no Plan lease, Coordinate, or GPU was claimed"),
            "{error}"
        );
    }

    #[test]
    fn remote_sha256_reader_refuses_noise_instead_of_guessing() {
        let digest = "d".repeat(64);
        assert_eq!(
            parse_sha256sum_output(format!("{digest}  /remote/fern-run\n").as_bytes()).unwrap(),
            digest
        );
        assert!(parse_sha256sum_output(b"NOT_A_DIGEST /remote/fern-run\n").is_err());
        assert!(parse_sha256sum_output(b"").is_err());
    }

    /// The numerical and deployment lists are written out in full so a change
    /// cannot silently move either identity boundary.
    ///
    /// Model source and Python/Serie locks enter the implementation hash.
    /// `Cargo.lock` stays in the wider deployment diagnostic because it builds
    /// the Rust executable, but it cannot turn an unrelated CLI edit into a new
    /// trained-model implementation. On 2026-08-12 that conflation killed a
    /// running Pod plan even though no Python had changed.
    ///
    /// If you are here because this test failed: that is the test working. Sync
    /// the source tree to every Worker Site, update the profile, then update
    /// this list in the same commit.
    #[test]
    fn the_reviewed_worker_inputs_are_exactly_these() {
        assert_eq!(
            super::REVIEWED_WORKER_SOURCE,
            [
                "fr.py",
                "fr_core.py",
                "fr_cfg.py",
                "fr_ext_action.py",
                "fr_ext_density.py",
                "fr_loss.py",
                "fr_specs.py",
                "fr_tr.py",
                "fr_tr_data.py",
                "fr_tr_exp.py",
                "serie/noise.py",
                "serie/pipeline.py",
            ],
            "the worker's source identity changed; re-sync every Worker Site"
        );
        assert_eq!(
            super::REVIEWED_MODEL_DEPENDENCY_LOCKS,
            ["uv.lock", "serie/uv.lock"],
            "the model's dependency identity changed; review its numerical effect"
        );
        assert_eq!(
            super::REVIEWED_RUST_DEPLOYMENT_INPUTS,
            ["Cargo.lock"],
            "the Rust deployment inputs changed; rebuild every future Worker-Site deployment"
        );
    }

    #[test]
    fn v2_matrix_declaration_uses_the_pinned_source_snapshot() {
        let root = std::env::temp_dir().join(format!(
            "fern-source-matrix-test-{}-{}",
            std::process::id(),
            unix_seconds()
        ));
        let lifecycle_db = root.join("lifecycle.db");
        let lane_db = root.join("lane").join("runs.db");
        let targets = [fern_run::db::RecipeTargetRegistration {
            recipe_hash: "recipe-hash".to_string(),
            target_hash: "same-scientific-target".to_string(),
            target_idx: 0,
            target_json: r#"{"target":"same-scientific-target"}"#.to_string(),
            dataset: "controlled_pendulum".to_string(),
            seq_len: 24,
            pred_len: 24,
            noise_sigma: 0.0,
            trajectory_draw_ordinal: 0,
            noise_draw_ordinal: 0,
            seed: 1,
            config_hash: "config-hash".to_string(),
        }];

        let first = declare_registered_recipe_matrix(
            &lifecycle_db,
            go_home_test_binary(),
            &lane_db,
            "recipe-name",
            &targets,
            None,
            Some("implementation-source-snapshot:aaaaaaaa"),
            "test",
        )
        .unwrap();
        let repaired = declare_registered_recipe_matrix(
            &lifecycle_db,
            go_home_test_binary(),
            &lane_db,
            "recipe-name",
            &targets,
            None,
            Some("implementation-source-snapshot:bbbbbbbb"),
            "test",
        )
        .unwrap();

        assert_ne!(first, repaired, "a repaired source must not reuse the old Matrix");
        std::fs::remove_dir_all(&root).unwrap();
    }

    /// Both directions: two roots on one filesystem pass; a root on tmpfs
    /// (/dev/shm) against one on the main disk is refused with both paths and
    /// both devices named. The roots deliberately do not exist — the check
    /// must read the device from the nearest existing ancestor, because at
    /// launch time neither directory may have been created yet.
    #[test]
    fn a_cross_filesystem_root_pairing_is_refused_at_preflight() {
        let same_a = std::env::temp_dir().join("fern-same-fs-a-does-not-exist");
        let same_b = std::env::temp_dir().join("fern-same-fs-b-does-not-exist");
        require_one_filesystem_for_commit_rename(&same_a, &same_b).expect("one filesystem passes");

        let shm = Path::new("/dev/shm");
        if !shm.is_dir()
            || nearest_existing_ancestor_device(shm).unwrap() == nearest_existing_ancestor_device(&same_a).unwrap()
        {
            panic!(
                "this test needs /dev/shm on a different filesystem from the temp dir; \
                 pick another pair of mounts for this machine"
            );
        }
        let error = require_one_filesystem_for_commit_rename(&shm.join("fern-cross-fs-does-not-exist"), &same_b)
            .unwrap_err()
            .to_string();
        assert!(error.contains("different filesystems"), "{error}");
        assert!(error.contains("No GPU work started"), "{error}");
    }

    #[test]
    fn a_profile_edit_between_coordinates_blocks_before_spawn() {
        assert!(
            require_unchanged_profile_revision("local-4070", "target target-a", "sha256:same", "sha256:same").is_ok()
        );

        let error = require_unchanged_profile_revision(
            "local-4070",
            "Stage postprocess",
            "sha256:plan-start",
            "sha256:stage-start",
        )
        .unwrap_err()
        .to_string();
        assert!(error.contains("cannot start Stage postprocess"));
        assert!(error.contains("changed after authority was acquired"));
        assert!(error.contains("no worker started"));
    }

    #[test]
    fn public_lane_is_one_name_not_an_execution_path() {
        for accepted in ["lorenz_noise_laws", "paper-core-h64", "runpod5090"] {
            assert!(valid_lane_name(accepted), "{accepted}");
        }
        for refused in ["", ".", "..", "a/b", "a\\b", "/tmp/lane"] {
            assert!(!valid_lane_name(refused), "{refused}");
        }
    }

    #[test]
    fn worker_site_refuses_missing_forecast_tensors_before_authority_or_gpu_work() {
        require_remote_typed_evidence_retention(false, false).unwrap();
        require_remote_typed_evidence_retention(true, true).unwrap();

        let error = require_remote_typed_evidence_retention(true, false)
            .unwrap_err()
            .to_string();
        assert!(error.contains("SSH Worker Site"), "got: {error}");
        assert!(error.contains("--keep-forecast-origins"), "got: {error}");
        assert!(error.contains("No GPU work started"), "got: {error}");
    }

    #[test]
    fn ordinary_matrix_worker_reuses_its_current_implementation_source_store() {
        let current = Path::new("/tmp/implementation-source-roots/archive-a");

        let ordinary_worker_store = implementation_source_store(current, None).unwrap().unwrap();
        assert_eq!(ordinary_worker_store, Path::new("/tmp/implementation-source-roots"));

        let source_bound_store = implementation_source_store(current, Some("archive-a"))
            .unwrap()
            .unwrap();
        assert_eq!(source_bound_store, ordinary_worker_store);

        let error = implementation_source_store(current, Some("archive-b"))
            .unwrap_err()
            .to_string();
        assert!(error.contains("installation receipt is inconsistent"));
    }

    #[test]
    fn run_plan_stage_refuses_a_lane_path() {
        let error = parse_run_plan_stage("fern:../outside:paper-core:fern:1")
            .unwrap_err()
            .to_string();
        assert!(error.contains("LANE must be one directory-name segment"));
    }

    /// A trained stage that does not keep its per-origin tensors is refused
    /// before a worker starts, not after it has trained.
    ///
    /// On 2026-08-11 a Solar TimeMixer cell ran twenty minutes on a paid GPU
    /// and failed at commit, because the typed point forecast is derived from
    /// tensors the parent had already deleted. A pretrained model returns
    /// through a path that keeps them, which is why the local TiREx lane never
    /// showed this -- so both directions are asserted here. The refusal must
    /// also say the words that fix it.
    #[test]
    fn a_trained_stage_without_retention_is_refused_before_any_worker_starts() {
        let trained = parse_run_plan_stage("1:solar_h64:paper-solar-h64:timemixer:28").unwrap();
        let recipe = exact_recipe_for_plan_step(&trained).unwrap();
        let error = preflight_stage_inputs(&trained, &recipe).unwrap_err().to_string();
        assert!(
            error.contains(KEEP_ORIGINS_STAGE_TOKEN),
            "the fix must be named: {error}"
        );
        assert!(error.contains("No worker started"), "got: {error}");

        let declared = parse_run_plan_stage("1:solar_h64:paper-solar-h64:timemixer:28:keep-origins").unwrap();
        assert!(declared.keep_forecast_origins);

        // A pretrained model keeps the tensors on its own, so the same stage
        // shape without the token must NOT be refused for retention.
        let pretrained = parse_run_plan_stage("1:solar_h64:paper-solar-h64:chronos2:28").unwrap();
        let pretrained_recipe = exact_recipe_for_plan_step(&pretrained).unwrap();
        assert!(!pretrained_recipe.model.fits_weights());
        let outcome = preflight_stage_inputs(&pretrained, &pretrained_recipe);
        if let Err(error) = outcome {
            assert!(
                !error.to_string().contains(KEEP_ORIGINS_STAGE_TOKEN),
                "a pretrained stage must never be refused for retention: {error}"
            );
        }
    }

    #[test]
    fn a_stage_limit_cannot_truncate_the_recipe_matrix() {
        let truncated = parse_run_plan_stage("1:solar_h64:paper-solar-h64:timemixer:5:keep-origins").unwrap();
        let recipe = exact_recipe_for_plan_step(&truncated).unwrap();
        let error = complete_recipe_stage_targets(&truncated, &recipe, DeviceKind::Cpu)
            .unwrap_err()
            .to_string();
        assert!(error.contains("expands to exactly 28 Coordinates"), "{error}");
        assert!(error.contains("drop 23 declared Coordinates"), "{error}");
        assert!(error.contains("No worker started"), "{error}");

        let complete = parse_run_plan_stage("1:solar_h64:paper-solar-h64:timemixer:28:keep-origins").unwrap();
        let targets = complete_recipe_stage_targets(&complete, &recipe, DeviceKind::Cpu).unwrap();
        assert_eq!(targets.len(), 28);
    }

    /// A preset spanning several worlds is not one Matrix, and the refusal
    /// hands back the recipe names that ARE, so the caller can split it.
    #[test]
    fn a_multi_world_preset_names_the_recipes_that_would_each_be_one_matrix() {
        let step = parse_run_plan_stage("1:x:paper-extended-laplace-h64:timemixer:28:keep-origins").unwrap();
        let error = exact_recipe_for_plan_step(&step).unwrap_err().to_string();
        assert!(error.contains("spans several worlds"), "got: {error}");
        assert!(
            error.contains("lorenz_base_timemixer"),
            "the recipes must be listed: {error}"
        );

        // And naming one of them directly resolves, which is the way out.
        let named = parse_run_plan_stage(
            "1:x:lorenz_base_timemixer_huber_p70_20_10_none_bs95_titration_h64_relative_doses_laplace_noise_matched_retraining:timemixer:28:keep-origins",
        )
        .unwrap();
        let recipe = exact_recipe_for_plan_step(&named).expect("a recipe is always exactly one Matrix");
        assert_eq!(recipe.model, RecipeModelName::Timemixer);
    }

    #[test]
    fn registered_plan_executes_its_frozen_recipe_without_reopening_the_declaration() {
        let legacy: fern_run::run_plan::RunPlanStepV1 = serde_json::from_value(serde_json::json!({
            "step_id": "legacy",
            "lane": "legacy",
            "preset": "legacy",
            "model": "fern",
            "limit": 1,
            "keep_forecast_origins": false
        }))
        .unwrap();
        assert!(
            legacy.recipe_snapshot.is_none(),
            "persisted pre-snapshot Plans remain readable"
        );

        let definition = resolve_run_plan(
            "snapshot-canary",
            &["lgssm:fixture:radial-nll-calibration-lgssm-h64:fern:2:keep-origins".to_string()],
            &[],
            &[],
            None,
        )
        .unwrap();
        let step = &definition.steps[0];
        let snapshot = step.recipe_snapshot.as_ref().expect("new Plan recipe snapshot");
        assert_eq!(snapshot.recipe_hash, "07358cab08fcfaac");
        exact_recipe_for_plan_step(step).expect("dispatch reconstructs the frozen recipe");

        let other_recipe = fern_run::csharp_experiment::select_catalog_recipes(
            None,
            Some("radial-nll-calibration-rossler-h64"),
            &[RecipeModelName::Fern.value()],
        )
        .unwrap()[0]
            .clone();
        let mut changed = step.clone();
        let changed_snapshot = changed.recipe_snapshot.as_mut().unwrap();
        changed_snapshot.recipe_hash = csharp_recipe_hash(&other_recipe).unwrap();
        changed_snapshot.identity = csharp_recipe_preimage(&other_recipe).unwrap();
        let error = exact_recipe_for_plan_step(&changed).unwrap_err().to_string();
        assert!(error.contains("frozen identity names"), "got: {error}");
    }

    #[test]
    fn external_shadow_must_equal_the_compiled_recipe_it_is_replacing() {
        let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).parent().unwrap().to_path_buf();
        let fixture: Value = serde_json::from_slice(
            &std::fs::read(root.join("crates/fern-recipes/fixtures/recipe_extension_v1_rust_golden.json")).unwrap(),
        )
        .unwrap();
        let mut extension = fixture["entries"]
            .as_array()
            .unwrap()
            .iter()
            .find(|entry| {
                entry["extension"]["identity"]["recipe"]["name"]
                    == "lg_ssm_base_fern_eig_nll_s16e32_no_swd_p70_20_10_none_bs95_titration_h64_relative_doses_noise_matched_retraining"
            })
            .unwrap()["extension"]
            .clone();
        extension["identity"]["recipe"]["run_whiten"] = Value::Bool(true);
        let changed_identity: fern_recipes::LegacyRecipeIdentityV1 =
            serde_json::from_value(extension["identity"].clone()).unwrap();
        extension["recipe_hash"] = Value::String(
            fern_run::csharp_experiment::resolve_recipe_run_targets(&changed_identity, DeviceKind::Cuda, 42, 1000)
                .unwrap()
                .recipe_hash,
        );
        let root = std::env::temp_dir().join(format!("fern-recipe-shadow-{}-{}", std::process::id(), unix_seconds()));
        std::fs::create_dir_all(&root).unwrap();
        let declaration_path = root.join("recipe.json");
        std::fs::write(&declaration_path, serde_json::to_vec_pretty(&extension).unwrap()).unwrap();

        let error = load_recipe_extensions(std::slice::from_ref(&declaration_path))
            .unwrap_err()
            .to_string();
        assert!(error.contains("compiled recipe"), "got: {error}");
        assert!(error.contains("disagree"), "got: {error}");

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn pass_four_refuses_an_unprofiled_launch_before_it_can_open_or_spawn_anything() {
        let error = require_execution_profile_id(None).unwrap_err().to_string();
        assert!(error.contains("--on <execution-profile> is required"), "got: {error}");
        assert!(error.contains("No GPU work started"), "got: {error}");
    }

    #[test]
    fn pass_four_has_no_legacy_coordinate_claim_in_active_worker_entrypoint() {
        // This is a deliberately narrow source-level regression test. It does
        // not prove the authority transaction (run_lifecycle does that); it
        // prevents the practical mistake of reintroducing a private legacy
        // claim path beside the one V3 launch adapter.
        let sweep = include_str!("run_sweep.rs");
        let legacy_claim = [".claim", "_coordinate("].concat();
        let legacy_result = [".record", "_worker_result("].concat();
        let legacy_accept = [".accept", "_candidate("].concat();
        assert!(!sweep.contains(&legacy_claim));
        assert!(!sweep.contains(&legacy_result));
        assert!(!sweep.contains(&legacy_accept));
        assert!(sweep.contains("start_authorized_coordinate("));
    }

}

#[cfg(test)]
mod adoption_pin_tests {
    use super::*;

    fn plan_definition(name: &str) -> fern_run::run_plan::RunPlanDefinitionV1 {
        fern_run::run_plan::RunPlanDefinitionV1 {
            schema: fern_run::run_plan::RUN_PLAN_SCHEMA.to_string(),
            plan_name: name.to_string(),
            implementation_source_snapshot_id: None,
            compiled_ablation: None,
            compiled_experiment: None,
            stage_plan_set_digest: None,
            stage_plan_digest: None,
            training_worker_request_digest: None,
            implementation_id: None,
            implementation_binding_id: None,
            owner_storage_override_reason: None,
            dependencies: Vec::new(),
            steps: vec![fern_run::run_plan::RunPlanStepV1 {
                step_id: "step-0".to_string(),
                lane: "pin-test-lane".to_string(),
                preset: "pin-test-preset".to_string(),
                model: "timemixer".to_string(),
                limit: 1,
                keep_forecast_origins: false,
                recipe_snapshot: None,
                compiled_experiment: None,
            }],
        }
    }

    fn actor(name: &str) -> fern_run::lifecycle::LifecycleActor {
        fern_run::lifecycle::LifecycleActor {
            kind: fern_run::lifecycle::LifecycleActorKind::Cli,
            actor_id: name.to_string(),
            owner_host: "test-host".to_string(),
            owner_pid: 7,
        }
    }

    fn scheduler_test_registry(label: &str) -> std::path::PathBuf {
        let root = std::env::temp_dir().join(format!(
            "fern-{label}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        std::fs::create_dir_all(&root).unwrap();
        let registry = root.join("lifecycle.db");
        crate::apply_registry_migrations_via_home(Some(go_home_test_binary()), &registry).unwrap();
        registry
    }

    fn start_test_plan(
        registry: &Path,
        request: &fern_run::run_plan::StartRunPlanRequest,
    ) -> fern_run::run_plan::StartRunPlanDecision {
        let registered = fern_run::run_plan::register_run_plan(
            registry,
            &fern_run::run_plan::RegisterRunPlanRequest {
                definition: request.definition.clone(),
                execution_profile_id: request.execution_profile_id.clone(),
                execution_profile_revision: request.execution_profile_revision.clone(),
                actor: request.actor.clone(),
            },
        )
        .unwrap();
        let plan_id = match registered {
            fern_run::run_plan::RegisterRunPlanOutcome::Registered { plan_id }
            | fern_run::run_plan::RegisterRunPlanOutcome::AlreadyRegistered { plan_id } => plan_id,
        };
        let election = claim_scheduler_lease_via_csharp_registry(registry, &actor("test-plan-issuer"), 120).unwrap();
        let fern_run::scheduler::SchedulerElection::Won { permit: scheduler, .. } = election else {
            panic!("test Plan issuer could not take scheduler office")
        };
        let decision =
            claim_plan_via_registry_home(go_home_test_binary(), registry, &scheduler, &plan_id, request).unwrap();
        if let fern_run::run_plan::StartRunPlanOutcome::Started { permit, .. } = &decision.outcome {
            renew_plan_lease_via_csharp_registry(registry, permit, &request.actor, 120).unwrap();
        }
        release_scheduler_lease_via_csharp_registry(registry, &scheduler).unwrap();
        decision
    }

}
