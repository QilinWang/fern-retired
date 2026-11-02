//! Read-only front door for choosing and checking a Fern experiment.
//!
//! The established execution surface remains intact while this module gives a
//! new user one small, linear route before any worker can start:
//!
//! `catalog` -> `profile validate` / `doctor` -> `plan`.
//!
//! These commands never open a run database, acquire a lease, create a worker
//! directory, or train a model.  Their output is deliberately a report, not a
//! boolean: an agent can see what is blocked and the human can see one concrete
//! next command instead of reconstructing it from a stack trace.

use std::collections::BTreeSet;
use std::env;
#[cfg(fern_role_operator)]
use std::path::{Path, PathBuf};
use std::process::Command as ShellCommand;
use std::time::{SystemTime, UNIX_EPOCH};

use clap::ValueEnum;
#[cfg(fern_role_operator)]
use clap::{Args, Subcommand};
use fern_execution_profile::CAMPAIGN_STORAGE_HARD_STOP_FREE_GIB;
use fern_execution_profile::{
    ExecutionProfile, ExecutionProfileDrift, ExecutionProfileExecutor, OBSERVED_EXECUTION_FINGERPRINT_SCHEMA,
    ObservedCudaDevice, ObservedExecutionFingerprint, ObservedPlatform, ObservedPythonRuntime, ObservedTorchRuntime,
    ResolvedExecutionProfile,
};
use fern_experiment_types::DeviceKind;
use fern_json_contract::JsonContract;
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};

use crate::plan_recipe_run::recipe_target_registrations_for_device;
#[cfg(any(fern_role_operator, test))]
use crate::presets::RecipeModelName;
#[cfg(fern_role_operator)]
use crate::run_views::OutputFormat;
#[cfg(fern_role_operator)]
use crate::{Fallible, invalid_input};

const FRONT_DOOR_SCHEMA: &str = "fern.experiment_preflight.v4";
const RUNPOD_SSH_HELPER: &str = "scripts/connect_runpod_ssh.sh";
/// The remote probe imports torch, the declared worker module, and CUDA.
/// A cold Pod can legitimately need more than the helper's 60-second default,
/// but a read-only preflight must still have a bounded no-response deadline.
const REMOTE_EXECUTION_PROBE_TIMEOUT_SECONDS: u64 = 180;

#[cfg(fern_role_operator)]
#[derive(Debug, Subcommand)]
enum ProfileCommand {
    /// Write an explicit local or SSH template at profiles/<profile-id>.json.
    Init(ProfileInit),
    /// Read a profile and perform the same no-write checks as doctor.
    Validate(ProfileValidate),
}

#[cfg(fern_role_operator)]
#[derive(Debug, Args)]
struct ProfileInit {
    /// New profile id, for example local-4070.
    profile_id: String,
    /// How Fern reaches the worker machine: local or ssh.
    #[arg(long, default_value = "local", value_parser = parse_profile_executor)]
    executor: ExecutionProfileExecutor,
    /// SSH user/host or configured alias. Required only with --executor ssh.
    #[arg(long)]
    ssh_destination: Option<String>,
    /// Local execution profile that owns the one Registry and Artifact Home.
    #[arg(long)]
    registry_home: Option<String>,
    /// Full SHA-256 of the immutable fern-run already deployed on the SSH Worker Site.
    #[arg(long)]
    worker_binary_sha256: Option<String>,
    /// Workspace root on the worker machine. Defaults to the current directory for a local profile.
    #[arg(long)]
    workspace_root: Option<PathBuf>,
    /// Home directory on the worker machine. Defaults to $HOME for a local profile.
    #[arg(long)]
    home_root: Option<PathBuf>,
}

#[cfg(fern_role_operator)]
#[derive(Debug, Args)]
struct ProfileValidate {
    /// Registered execution profile id, for example local-4070.
    profile_id: String,
    /// Where this command is running: host or codex-sandbox.
    #[arg(long, value_enum)]
    observer: ObserverContext,
    /// Output format: text for humans, json for automation.
    #[arg(long, value_enum, default_value_t = OutputFormat::Text)]
    format: OutputFormat,
}

/// The environment that is allowed to make claims from this preflight.
///
/// This is explicit because the Codex sandbox has no stable environment
/// variable that distinguishes it from a host-authorized command. A missing
/// CUDA device or SSH agent inside that sandbox is therefore an observation
/// limit, not evidence that the host or Worker Site is broken.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, JsonSchema, ValueEnum)]
#[serde(rename_all = "snake_case")]
enum ObserverContext {
    Host,
    CodexSandbox,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
enum ExecutionFactsSource {
    Host,
    CodexSandbox,
    RemoteWorkerSite,
}

/// A stable category for a preflight problem.  The category is intentionally
/// smaller than the full lifecycle vocabulary because this front door has not
/// started work yet: it cannot honestly report worker or artifact failures.
#[derive(Debug, Clone, Copy, Serialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
enum PreflightProblemCode {
    InvalidRequest,
    InvalidProfile,
    MissingPrerequisite,
    CapacityBlocked,
    RemoteProbeRequired,
    HostCheckRequired,
    ProfileDrift,
    /// Only the catalog expansion constructed this, and that moved to
    /// csharp/Fern.Run.Cli/PlanVerbs.cs with `plan`. The variant stays because
    /// it is part of the `fern.experiment_preflight.v4` vocabulary the C# owner
    /// still writes, and a reader here must be able to name what it reads.
    #[allow(dead_code)]
    CatalogInconsistency,
}

#[derive(Debug, Clone, Serialize, JsonSchema)]
struct PreflightProblem {
    code: PreflightProblemCode,
    meaning: String,
    fix_command: String,
}

/// The typed probe-failure family (agentic-error-handling pilot, gate 2):
/// each variant keeps the facts its consumers need, and code, remediation,
/// and retry are DERIVED here instead of being reconstructed from prose at
/// the call site. Display stays the human rendering; policy never reads it.
#[derive(Debug, thiserror::Error)]
enum ProbeFailure {
    #[error("cannot start {python}: {source_detail}")]
    InterpreterUnavailable { python: PathBuf, source_detail: String },
    /// The probe script imports torch AND the declared worker module, so a
    /// nonzero exit is "the environment failed the probe", with the
    /// script's own stderr as the detail — not necessarily torch.
    #[error("{python} failed the execution probe: {stderr}")]
    ProbeScriptFailed { python: PathBuf, stderr: String },
    #[error("{python} returned an unreadable Python probe: {detail}")]
    ProbeUnreadable { python: PathBuf, detail: String },
}

/// The remote half of the pilot: the same typed discipline for the SSH
/// probe. Transport and remote-script failures are one variant on purpose
/// — a nonzero helper exit cannot honestly distinguish them, and the fix
/// (restore the transport, then re-probe) is the same. Observer context
/// (sandbox versus host) is the CALLER's fact and enters at derivation
/// time, not inside the failure.
#[derive(Debug, thiserror::Error)]
enum RemoteProbeFailure {
    #[error("the SSH profile has no destination")]
    MissingDestination,
    #[error("cannot run the read-only SSH probe through {helper}: {source_detail}; remote state is unknown")]
    HelperUnavailable { helper: PathBuf, source_detail: String },
    #[error("the read-only SSH probe for {destination} failed: {stderr}; remote state is unknown")]
    TransportOrRemoteFailed { destination: String, stderr: String },
    #[error(
        "the read-only SSH probe for {destination} returned an unreadable report: {detail}; remote state is unknown"
    )]
    ReportUnreadable { destination: String, detail: String },
}

impl RemoteProbeFailure {
    fn code(&self, observer: ObserverContext) -> PreflightProblemCode {
        match self {
            // A missing destination is a profile fault; no observer or
            // transport repair can produce one. (Previously this wore the
            // transport codes and, from the sandbox, even an
            // authentication story.)
            Self::MissingDestination => PreflightProblemCode::InvalidProfile,
            _ if observer == ObserverContext::CodexSandbox => PreflightProblemCode::HostCheckRequired,
            _ => PreflightProblemCode::RemoteProbeRequired,
        }
    }

    fn remediation(&self, observer: ObserverContext) -> ProbeRemediation {
        match self {
            Self::MissingDestination => ProbeRemediation::DeclareSshDestination,
            _ if observer == ObserverContext::CodexSandbox => ProbeRemediation::RepeatFromHostObserver,
            _ => ProbeRemediation::RestoreSshTransport {
                helper: RUNPOD_SSH_HELPER.to_string(),
            },
        }
    }

    fn envelope(&self, observer: ObserverContext) -> TypedPreflightFailureV1 {
        TypedPreflightFailureV1 {
            schema: TYPED_PREFLIGHT_FAILURE_SCHEMA,
            code: self.code(observer),
            message: self.to_string(),
            remediation: self.remediation(observer),
            retry: RetryDisposition::NotRetryableWithoutChange,
        }
    }
}

/// Structured next-step data (never an executable command): the human
/// `fix_command` string is RENDERED FROM this, so an agent consumes the
/// fields and a human reads the sentence, and neither parses the other.
#[derive(Debug, Clone, Serialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case")]
enum ProbeRemediation {
    RepairInterpreter { python: String },
    RepairPythonEnvironment { python: String },
    DeclareSshDestination,
    RepeatFromHostObserver,
    RestoreSshTransport { helper: String },
}

/// Whether retrying without changing anything can ever help. Environment
/// probe failures are deterministic: the answer is no until something is
/// repaired.
#[derive(Debug, Clone, Copy, Serialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
enum RetryDisposition {
    NotRetryableWithoutChange,
}

/// One schema-versioned agent envelope entry (gate 3): the stable contract
/// is code, remediation, and retry; message is the human and fallback
/// rendering, identical to the failure's Display.
#[derive(Debug, Clone, Serialize, JsonSchema)]
struct TypedPreflightFailureV1 {
    schema: &'static str,
    code: PreflightProblemCode,
    message: String,
    remediation: ProbeRemediation,
    retry: RetryDisposition,
}

const TYPED_PREFLIGHT_FAILURE_SCHEMA: &str = "fern.preflight_failure.v1";

impl ProbeFailure {
    fn code(&self) -> PreflightProblemCode {
        // All three are missing-prerequisite shapes today; the value the
        // caller used to hardcode, now derived where the facts live.
        PreflightProblemCode::MissingPrerequisite
    }

    fn remediation(&self) -> ProbeRemediation {
        match self {
            Self::InterpreterUnavailable { python, .. } => ProbeRemediation::RepairInterpreter {
                python: python.display().to_string(),
            },
            Self::ProbeScriptFailed { python, .. } | Self::ProbeUnreadable { python, .. } => {
                ProbeRemediation::RepairPythonEnvironment {
                    python: python.display().to_string(),
                }
            }
        }
    }

    fn retry(&self) -> RetryDisposition {
        RetryDisposition::NotRetryableWithoutChange
    }

    fn envelope(&self) -> TypedPreflightFailureV1 {
        TypedPreflightFailureV1 {
            schema: TYPED_PREFLIGHT_FAILURE_SCHEMA,
            code: self.code(),
            message: self.to_string(),
            remediation: self.remediation(),
            retry: self.retry(),
        }
    }
}

impl ProbeRemediation {
    /// The human sentence, rendered from the data. The old caller told
    /// every failure to "repair … so it can import torch"; a missing
    /// interpreter now gets missing-interpreter advice.
    fn fix_command(&self, profile_id: &str) -> String {
        match self {
            Self::RepairInterpreter { python } => format!(
                "install or restore the Python interpreter at {python}, then run fern-run doctor --on {profile_id} --observer host"
            ),
            Self::RepairPythonEnvironment { python } => format!(
                "repair {python} so the execution probe passes (torch and the worker module must import), then run fern-run doctor --on {profile_id} --observer host"
            ),
            Self::DeclareSshDestination => format!(
                "declare ssh_destination in the {profile_id} profile, then run fern-run doctor --on {profile_id} --observer host"
            ),
            Self::RepeatFromHostObserver => format!(
                "repeat this read-only check with host access: fern-run doctor --on {profile_id} --observer host"
            ),
            Self::RestoreSshTransport { helper } => format!(
                "restore SSH access once with {helper} unlock, verify it with {helper} check, then run fern-run doctor --on {profile_id} --observer host"
            ),
        }
    }
}

#[derive(Debug, Clone, Serialize, JsonSchema)]
struct StorageCheck {
    inspected_path: String,
    filesystem: String,
    available_gib: f64,
    free_percent: f64,
    warning: bool,
    hard_stop: bool,
}

#[derive(Debug, Clone, Serialize, JsonSchema)]
struct PlannedRecipe {
    recipe: String,
    full_matrix_cells: usize,
    selected_cells: usize,
    estimate_status: &'static str,
    comparable_completed_attempts: usize,
    median_seconds_per_cell: Option<f64>,
    estimated_selected_seconds: Option<f64>,
    canary_target_hashes: Vec<String>,
    sources: Vec<String>,
    requires_known_data: bool,
}

/// One exact ordered stage plus the like-for-like timing evidence available
/// before registration. `needs_canary` is an explicit answer, never a missing
/// price field that can be overlooked beside `status: ready`.
#[derive(Debug, Clone, Serialize, JsonSchema)]
struct PlannedStage {
    step_id: String,
    recipe: String,
    model: String,
    recipe_cells: usize,
    estimate_status: &'static str,
    comparable_completed_attempts: usize,
    median_seconds_per_cell: Option<f64>,
    estimated_stage_seconds: Option<f64>,
}

#[derive(Debug, Serialize, JsonSchema)]
struct FrontDoorReport {
    schema: &'static str,
    command: &'static str,
    status: &'static str,
    /// This is an invariant of this module, not merely a best-effort claim.
    gpu_work_started: bool,
    /// Where the controller command ran. This is declared by the caller and
    /// prevents a restricted Codex process from impersonating the host.
    observer: ObserverContext,
    /// Where the execution facts were actually measured. None means the
    /// probe never reached an execution environment.
    execution_facts_from: Option<ExecutionFactsSource>,
    profile_id: String,
    profile_path: String,
    /// The content identity of the validated declaration this report inspected.
    /// None means the declaration was invalid or unavailable, so it must not be
    /// used to launch work.
    profile_revision: Option<String>,
    executor: Option<&'static str>,
    device: Option<&'static str>,
    campaign_root: Option<String>,
    observed_execution: Option<ObservedExecutionFingerprint>,
    profile_drift: Vec<ExecutionProfileDrift>,
    storage: Vec<StorageCheck>,
    selected_preset: Option<String>,
    selected_model: Option<String>,
    canary: Option<bool>,
    recipes: Vec<PlannedRecipe>,
    stages: Vec<PlannedStage>,
    /// Present only when every exact stage has like-for-like timing evidence.
    estimated_total_seconds: Option<f64>,
    problems: Vec<PreflightProblem>,
    /// Gate-3 envelope entries (fern.preflight_failure.v1): populated by
    /// paths whose failures are typed; the pilot covers the local
    /// execution probe. `problems` remains the complete human list.
    typed_failures: Vec<TypedPreflightFailureV1>,
    next_step: String,
}

impl JsonContract for FrontDoorReport {
    const SCHEMA_ID: &'static str = FRONT_DOOR_SCHEMA;
}

#[derive(Debug, Deserialize)]
struct PythonProbe {
    python: PythonProbeRuntime,
    platform: ObservedPlatform,
    torch: ObservedTorchRuntime,
    cuda_available: bool,
    cuda_current_device: Option<u32>,
    cuda_visible_devices: Option<String>,
    cuda_device_order: Option<String>,
    nvidia_visible_devices: Option<String>,
    cuda_devices: Vec<ObservedCudaDevice>,
}

#[derive(Debug, Deserialize)]
struct RemotePythonProbe {
    #[serde(flatten)]
    execution: PythonProbe,
    storage: Vec<RemoteStorageProbe>,
    paths: Vec<RemotePathProbe>,
    nvidia_management_facts: Vec<RemoteNvidiaManagementFact>,
}

#[derive(Debug, Deserialize)]
struct RemoteStorageProbe {
    requested_path: String,
    inspected_path: String,
    filesystem: String,
    available_bytes: u64,
    total_bytes: u64,
}

#[derive(Debug, Deserialize)]
struct RemotePathProbe {
    requested_path: String,
    exists: bool,
    is_dir: bool,
    #[serde(default)]
    executable: bool,
    writable_ancestor: bool,
}

#[derive(Debug, Deserialize)]
struct RemoteNvidiaManagementFact {
    uuid: String,
    driver_version: String,
}

#[derive(Debug, Deserialize)]
struct PythonProbeRuntime {
    sys_executable: PathBuf,
    version: String,
    implementation: String,
    worker_module: String,
    worker_module_available: bool,
}

/// One validated profile plus the machine facts observed immediately before
/// a controlled launch asks the registry for authority.
pub(crate) struct ReadyExecution {
    pub(crate) profile: ResolvedExecutionProfile,
    #[allow(dead_code)]
    pub(crate) observed: ObservedExecutionFingerprint,
}

#[derive(Debug, Clone, PartialEq)]
struct ComparableStageRuntime {
    comparable_attempts: usize,
    median_seconds_per_cell: Option<f64>,
    estimated_stage_seconds: Option<f64>,
}

#[derive(Serialize)]
struct ComparableStageDurationsRequest<'a> {
    recipe_name: &'a str,
    execution_profile_revision: &'a str,
}

#[derive(Deserialize)]
struct ComparableStageDurationsReport {
    schema: String,
    durations_seconds: Vec<f64>,
}

/// C# Matrix Registry selects the like-for-like completed Attempt durations;
/// Rust retains the shared pricing calculation for every public caller.
fn comparable_stage_runtime(
    registry: &Path,
    recipe_name: &str,
    profile_revision: &str,
    requested_cells: usize,
) -> Fallible<ComparableStageRuntime> {
    let response = crate::run_csharp_registry(
        "list-comparable-stage-durations",
        registry,
        &ComparableStageDurationsRequest {
            recipe_name,
            execution_profile_revision: profile_revision,
        },
        crate::CSHARP_REGISTRY_PROCESS_DEADLINE,
    )?;
    let report: ComparableStageDurationsReport = serde_json::from_slice(&response)?;
    if report.schema != "fern.comparable_stage_durations.v1" {
        return Err(invalid_input(format!(
            "C# comparable stage-duration reader returned schema {:?}",
            report.schema
        )));
    }
    Ok(stage_runtime_from_durations(report.durations_seconds, requested_cells))
}

fn stage_runtime_from_durations(mut seconds: Vec<f64>, requested_cells: usize) -> ComparableStageRuntime {
    seconds.sort_by(f64::total_cmp);
    let median_seconds_per_cell = median(&seconds);
    ComparableStageRuntime {
        comparable_attempts: seconds.len(),
        median_seconds_per_cell,
        estimated_stage_seconds: median_seconds_per_cell.map(|seconds| seconds * requested_cells as f64),
    }
}

fn median(values: &[f64]) -> Option<f64> {
    let middle = values.len() / 2;
    match values.len() {
        0 => None,
        length if length % 2 == 1 => Some(values[middle]),
        _ => Some((values[middle - 1] + values[middle]) / 2.0),
    }
}

fn parse_profile_executor(value: &str) -> Result<ExecutionProfileExecutor, String> {
    match value {
        "local" => Ok(ExecutionProfileExecutor::Local),
        "ssh" => Ok(ExecutionProfileExecutor::Ssh),
        other => Err(format!("unknown executor {other:?}; valid values are local and ssh")),
    }
}

fn profile_at(profile_id: &str) -> (PathBuf, Result<ExecutionProfile, PreflightProblem>) {
    let path = match ExecutionProfile::default_path(profile_id) {
        Ok(path) => path,
        Err(issue) => {
            return (
                PathBuf::from("profiles/(invalid).json"),
                Err(PreflightProblem {
                    code: PreflightProblemCode::InvalidRequest,
                    meaning: format!("profile id {:?}: {}", profile_id, issue.explanation),
                    fix_command: "fern-run profile init local-4070".to_string(),
                }),
            );
        }
    };
    let profile = if !path.is_file() {
        Err(PreflightProblem {
            code: PreflightProblemCode::MissingPrerequisite,
            meaning: format!("the execution profile {} does not exist", path.display()),
            fix_command: format!("fern-run profile init {profile_id}"),
        })
    } else {
        ExecutionProfile::read(&path).map_err(|message| PreflightProblem {
            code: PreflightProblemCode::InvalidProfile,
            meaning: message,
            fix_command: format!(
                "repair {} and run fern-run profile validate {profile_id} --observer host",
                path.display()
            ),
        })
    };
    (path, profile)
}

/// Resolve the one local execution declaration a mutating runner may use.
///
/// `doctor` explains the same checks interactively.  A runner repeats them at
/// the actual launch boundary because a successful doctor from ten minutes ago
/// cannot authorize a later command after a profile or machine has changed.
/// This helper still does not claim a Coordinate, open a run database, or
/// create a worker directory; callers can truthfully report that no GPU work
/// started when it refuses.
pub(crate) fn resolve_ready_execution(profile_id: &str) -> Fallible<ReadyExecution> {
    let (profile_path, profile_result) = profile_at(profile_id);
    let profile = profile_result.map_err(|problem| {
        invalid_input(format!(
            "cannot start on profile {profile_id:?}: {}\nFix: {}\nNo GPU work started.",
            problem.meaning, problem.fix_command
        ))
    })?;
    let report = profile_report("run", profile_id, profile_path, &profile, ObserverContext::Host);
    if report.status != "ready" {
        let problems = report
            .problems
            .iter()
            .map(|problem| format!("- {}\n  Fix: {}", problem.meaning, problem.fix_command))
            .collect::<Vec<_>>()
            .join("\n");
        return Err(invalid_input(format!(
            "cannot start on profile {profile_id:?}:\n{problems}\nNo GPU work started."
        )));
    }
    let resolved_profile = profile.resolve().map_err(|error| {
        invalid_input(format!(
            "cannot start on profile {profile_id:?}: {error}\nNo GPU work started."
        ))
    })?;
    let observed = report.observed_execution.ok_or_else(|| {
        invalid_input(format!(
            "cannot start on profile {profile_id:?}: preflight produced no observed execution fingerprint. \
             No GPU work started."
        ))
    })?;
    apply_declared_known_data_root(&resolved_profile)?;
    Ok(ReadyExecution {
        profile: resolved_profile,
        observed,
    })
}

/// Reduce the launch-time observation to the exact capability Registry Home
/// records for a Worker Site. Registration and launch refresh share this one
/// mapping so a profile edit cannot be interpreted differently by two callers.
pub(crate) fn observed_site_capability(ready: &ReadyExecution) -> Fallible<fern_run::worker_site::SiteCapabilityFacts> {
    let declaration = &ready.profile.declaration;
    let observed = &ready.observed;
    let selected_device = observed
        .cuda_current_device
        .and_then(|index| observed.cuda_devices.iter().find(|device| device.index == index))
        .or_else(|| observed.cuda_devices.first());
    let device = match declaration.device {
        DeviceKind::Cpu => "cpu",
        DeviceKind::Cuda => "cuda",
    };
    Ok(fern_run::worker_site::SiteCapabilityFacts {
        device: device.to_string(),
        hostname: observed.platform.hostname.clone(),
        cuda_available: observed.cuda_available,
        cuda_device_name: selected_device.map(|gpu| gpu.name.clone()),
        cuda_compute_capability: selected_device.map(|gpu| format!("{}.{}", gpu.capability[0], gpu.capability[1])),
        cuda_total_memory_bytes: selected_device.and_then(|gpu| i64::try_from(gpu.total_memory_bytes).ok()),
        python_version: observed.python.version.clone(),
        python_executable: observed.python.sys_executable.display().to_string(),
        torch_version: observed.torch.version.clone(),
        worker_binary_sha256: declaration.worker_binary_sha256.clone(),
        source_root: declaration.source_root.display().to_string(),
        observed_environment_digest: observed
            .environment_content_digest()
            .map_err(|error| invalid_input(format!("cannot digest the observed environment: {error}")))?,
        observed_at_ms: i64::try_from(observed.observed_at_unix_ms)
            .map_err(|_| invalid_input("the probe reported an observation time outside the recordable range"))?,
        observed_fingerprint_json: serde_json::to_string(observed)?,
    })
}

/// Make the profile's declared known-data root the one that actually gets read.
///
/// The field existed and nothing consumed it. serie-rs resolves recorded assets
/// from `SERIE_KNOWN_ROOT` or `~/.cache/serie/known`, so a profile could declare
/// `/workspace/known`, be validated as ready, and then have every recorded cell
/// look somewhere else entirely -- which is what happened on the Pod on
/// 2026-08-11, where the asset had been staged exactly where the profile said.
/// A declaration nothing reads is worse than no declaration: it is believed.
///
/// Omitting the variable resolves to the profile. Setting it to the SAME
/// directory is accepted, because a script that exports it is not wrong. Setting
/// it to a DIFFERENT one fails closed rather than silently picking a winner --
/// two sources of truth for where the data lives is the shape this repairs.
fn apply_declared_known_data_root(profile: &ResolvedExecutionProfile) -> Fallible<()> {
    let Some(declared) = profile.declaration.known_data_root.as_ref() else {
        return Ok(());
    };
    match env::var_os(SERIE_KNOWN_ROOT_ENV) {
        Some(existing) if Path::new(&existing) != declared.as_path() => Err(invalid_input(format!(
            "{SERIE_KNOWN_ROOT_ENV}={} disagrees with the execution profile, which declares {}. \
             Unset it to use the profile, or change the profile deliberately. No GPU work started.",
            Path::new(&existing).display(),
            declared.display()
        ))),
        Some(_) => Ok(()),
        None => {
            // SAFETY: this runs on the launch path before any worker thread is
            // spawned, and it is the only writer of this variable. It must be an
            // environment write rather than an argument because the reader is
            // serie-rs's own root resolution, shared with the Python side, which
            // both sides read from the environment by design.
            unsafe { env::set_var(SERIE_KNOWN_ROOT_ENV, declared) };
            Ok(())
        }
    }
}

const SERIE_KNOWN_ROOT_ENV: &str = "SERIE_KNOWN_ROOT";

fn profile_report(
    command: &'static str,
    profile_id: &str,
    profile_path: PathBuf,
    profile: &ExecutionProfile,
    observer: ObserverContext,
) -> FrontDoorReport {
    profile_report_with_remote_helper(
        command,
        profile_id,
        profile_path,
        profile,
        observer,
        Path::new(RUNPOD_SSH_HELPER),
    )
}

fn profile_report_with_remote_helper(
    command: &'static str,
    profile_id: &str,
    profile_path: PathBuf,
    profile: &ExecutionProfile,
    observer: ObserverContext,
    remote_helper: &Path,
) -> FrontDoorReport {
    let profile_revision = profile.resolve().ok().map(|resolved| resolved.revision.revision_hash);
    let mut report = FrontDoorReport {
        schema: FRONT_DOOR_SCHEMA,
        command,
        status: "ready",
        gpu_work_started: false,
        observer,
        execution_facts_from: None,
        profile_id: profile_id.to_string(),
        profile_path: profile_path.display().to_string(),
        profile_revision,
        executor: Some(profile.executor.value()),
        device: Some(device_name(profile.device)),
        campaign_root: Some(profile.campaign_root.display().to_string()),
        observed_execution: None,
        profile_drift: Vec::new(),
        storage: Vec::new(),
        selected_preset: None,
        selected_model: None,
        canary: None,
        recipes: Vec::new(),
        stages: Vec::new(),
        estimated_total_seconds: None,
        problems: Vec::new(),
        typed_failures: Vec::new(),
        next_step: "just catalog".to_string(),
    };
    for issue in profile.declaration_issues() {
        let fix_command = if profile.executor == ExecutionProfileExecutor::Ssh
            && matches!(issue.field, "schema" | "worker_binary_sha256")
        {
            format!(
                "deploy a content-addressed fern-worker and update {} to v5 in the same deliberate operation; do not hand-edit a digest into a mutable binary path",
                profile_path.display()
            )
        } else {
            format!(
                "edit {} and run fern-run profile validate {profile_id} --observer host",
                profile_path.display()
            )
        };
        report.problems.push(PreflightProblem {
            code: PreflightProblemCode::InvalidProfile,
            meaning: format!("profile field {}: {}", issue.field, issue.explanation),
            fix_command,
        });
    }
    match profile.executor {
        ExecutionProfileExecutor::Ssh => inspect_remote_profile(&mut report, profile, observer, remote_helper),
        ExecutionProfileExecutor::Local => inspect_local_profile(&mut report, profile, observer),
    }
    close_report(report)
}

fn inspect_local_profile(report: &mut FrontDoorReport, profile: &ExecutionProfile, observer: ObserverContext) {
    if !profile.worker_binary.is_file() {
        report.problems.push(PreflightProblem {
            code: PreflightProblemCode::MissingPrerequisite,
            meaning: format!(
                "the declared Fern runner {} does not exist",
                profile.worker_binary.display()
            ),
            fix_command: format!(
                "build the intended fern-worker binary or edit the profile, then run fern-run profile validate {} --observer host",
                profile.profile_id
            ),
        });
    }
    if !profile.python.is_file() {
        report.problems.push(PreflightProblem {
            code: PreflightProblemCode::MissingPrerequisite,
            meaning: format!(
                "the declared Python executable {} does not exist",
                profile.python.display()
            ),
            fix_command: format!(
                "install or select the intended interpreter, edit the profile, then run fern-run profile validate {} --observer host",
                profile.profile_id
            ),
        });
    } else {
        match probe_execution(profile) {
            Ok(observed) => record_local_execution_observation(report, profile, observer, observed),
            // Gate 2: code, remediation, and retry come FROM the typed
            // failure; nothing here re-derives semantics out of prose,
            // and the human fix text is rendered from the same data the
            // agent envelope carries.
            Err(failure) => {
                report.problems.push(PreflightProblem {
                    code: failure.code(),
                    meaning: failure.to_string(),
                    fix_command: failure.remediation().fix_command(&profile.profile_id),
                });
                report.typed_failures.push(failure.envelope());
            }
        }
    }
    let mut seen_filesystems = BTreeSet::new();
    for path in [&profile.campaign_root, &profile.artifact_root, &profile.work_root] {
        match probe_storage(path) {
            Ok(check) => {
                if seen_filesystems.insert(check.filesystem.clone()) {
                    if check.hard_stop {
                        report.problems.push(PreflightProblem {
                            code: PreflightProblemCode::CapacityBlocked,
                            meaning: format!(
                                "{} has only {:.1} GiB / {:.1}% free; Fern must not start a sweep below {CAMPAIGN_STORAGE_HARD_STOP_FREE_GIB} GiB",
                                check.filesystem, check.available_gib, check.free_percent
                            ),
                            fix_command: format!(
                                "free space on that volume, then run fern-run doctor --on {} --observer host",
                                profile.profile_id
                            ),
                        });
                    }
                    report.storage.push(check);
                }
            }
            Err(message) => report.problems.push(PreflightProblem {
                code: PreflightProblemCode::MissingPrerequisite,
                meaning: message,
                fix_command: format!(
                    "make the parent of {} available, then run fern-run doctor --on {} --observer host",
                    path.display(),
                    profile.profile_id
                ),
            }),
        }
    }
    let windows_volume = Path::new("/mnt/c");
    if windows_volume.is_dir() {
        match probe_storage(windows_volume) {
            Ok(check) if seen_filesystems.insert(check.filesystem.clone()) => {
                if check.hard_stop {
                    report.problems.push(PreflightProblem {
                        code: PreflightProblemCode::CapacityBlocked,
                        meaning: format!(
                            "{} has only {:.1} GiB / {:.1}% free; WSL cannot safely grow its backing disk",
                            check.filesystem, check.available_gib, check.free_percent
                        ),
                        fix_command: format!(
                            "free Windows C: space, then run fern-run doctor --on {} --observer host",
                            profile.profile_id
                        ),
                    });
                }
                report.storage.push(check);
            }
            Ok(_) => {}
            Err(message) => report.problems.push(PreflightProblem {
                code: PreflightProblemCode::MissingPrerequisite,
                meaning: message,
                fix_command: "make /mnt/c readable before starting a broad build or sweep".to_string(),
            }),
        }
    }
}

fn record_local_execution_observation(
    report: &mut FrontDoorReport,
    profile: &ExecutionProfile,
    observer: ObserverContext,
    observed: ObservedExecutionFingerprint,
) {
    let drift = observed.drift_from(profile);
    let sandbox_cannot_see_declared_cuda =
        observer == ObserverContext::CodexSandbox && profile.device == DeviceKind::Cuda && !observed.cuda_available;
    if sandbox_cannot_see_declared_cuda {
        report.problems.push(PreflightProblem {
            code: PreflightProblemCode::HostCheckRequired,
            meaning: "this Codex sandbox cannot see the declared CUDA device; this does not establish that the host GPU is unavailable"
                .to_string(),
            fix_command: format!(
                "repeat this read-only check with host access: fern-run doctor --on {} --observer host",
                profile.profile_id
            ),
        });
    } else {
        append_profile_drift_problems(
            report,
            &drift,
            "execution profile drift",
            "repair the declared environment or create a new profile revision for the machine actually being used",
        );
    }
    report.profile_drift = drift;
    report.execution_facts_from = Some(match observer {
        ObserverContext::Host => ExecutionFactsSource::Host,
        ObserverContext::CodexSandbox => ExecutionFactsSource::CodexSandbox,
    });
    report.observed_execution = Some(observed);
}

/// Inspect the declared Worker Site through the one maintained SSH entrance.
///
/// The helper authenticates and transports one read-only probe. It does not
/// own a Matrix, claim a Coordinate, inspect process names, or start a worker.
/// A failed probe therefore means only "remote state unknown" and blocks the
/// caller before any GPU work begins.
fn inspect_remote_profile(
    report: &mut FrontDoorReport,
    profile: &ExecutionProfile,
    observer: ObserverContext,
    helper: &Path,
) {
    let mut probe = match probe_remote_execution(profile, helper) {
        Ok(probe) => probe,
        // Gate 2, remote half: code and remediation are derived from the
        // typed failure with the observer as the caller's fact; the
        // sandbox wrapper sentence survives as rendering, prefixed onto
        // the failure's own message rather than replacing it.
        Err(failure) => {
            let meaning = if observer == ObserverContext::CodexSandbox {
                format!(
                    "the Codex sandbox could not establish a trustworthy remote read-only observation; RunPod state remains unknown. {failure}"
                )
            } else {
                failure.to_string()
            };
            report.problems.push(PreflightProblem {
                code: failure.code(observer),
                meaning,
                fix_command: failure.remediation(observer).fix_command(&profile.profile_id),
            });
            report.typed_failures.push(failure.envelope(observer));
            return;
        }
    };

    let management_facts = probe
        .nvidia_management_facts
        .iter()
        .map(|fact| NvidiaManagementFact {
            uuid: fact.uuid.clone(),
            driver_version: fact.driver_version.clone(),
        })
        .collect::<Vec<_>>();
    attach_known_nvidia_management_facts(
        &mut probe.execution.cuda_devices,
        probe.execution.cuda_visible_devices.as_deref(),
        probe.execution.nvidia_visible_devices.as_deref(),
        &management_facts,
    );
    let observed = observed_execution_from_probe(profile, probe.execution);
    let drift = observed.drift_from(profile);
    append_profile_drift_problems(
        report,
        &drift,
        "remote execution profile drift",
        "repair the Worker Site or create a new profile revision for the remote machine actually being used",
    );
    report.profile_drift = drift;
    report.execution_facts_from = Some(ExecutionFactsSource::RemoteWorkerSite);
    report.observed_execution = Some(observed);

    let mut seen_filesystems = BTreeSet::new();
    for remote in probe.storage {
        if remote.total_bytes == 0 {
            report.problems.push(PreflightProblem {
                code: PreflightProblemCode::MissingPrerequisite,
                meaning: format!(
                    "remote storage probe returned zero total bytes for {}",
                    remote.requested_path
                ),
                fix_command: format!(
                    "repair the remote mount, then run fern-run doctor --on {} --observer host",
                    profile.profile_id
                ),
            });
            continue;
        }
        let available_gib = remote.available_bytes as f64 / (1024.0 * 1024.0 * 1024.0);
        let free_percent = 100.0 * remote.available_bytes as f64 / remote.total_bytes as f64;
        let check = StorageCheck {
            inspected_path: remote.inspected_path,
            filesystem: remote.filesystem,
            available_gib,
            free_percent,
            warning: available_gib < profile.warn_free_gib as f64 || free_percent < profile.warn_free_percent as f64,
            hard_stop: available_gib < CAMPAIGN_STORAGE_HARD_STOP_FREE_GIB as f64,
        };
        if seen_filesystems.insert(check.filesystem.clone()) {
            if check.hard_stop {
                report.problems.push(PreflightProblem {
                    code: PreflightProblemCode::CapacityBlocked,
                    meaning: format!(
                        "remote filesystem {} has only {:.1} GiB / {:.1}% free; Fern must not start a sweep below {CAMPAIGN_STORAGE_HARD_STOP_FREE_GIB} GiB",
                        check.filesystem, check.available_gib, check.free_percent
                    ),
                    fix_command: format!(
                        "free space on the Worker Site, then run fern-run doctor --on {} --observer host",
                        profile.profile_id
                    ),
                });
            }
            report.storage.push(check);
        }
    }

    for path in probe.paths {
        let is_known_data = profile
            .known_data_root
            .as_ref()
            .is_some_and(|known| known == Path::new(&path.requested_path));
        let is_worker_binary = profile.worker_binary == Path::new(&path.requested_path);
        let usable = if is_worker_binary {
            path.exists && !path.is_dir && path.executable
        } else if is_known_data {
            path.exists && path.is_dir
        } else {
            path.writable_ancestor
        };
        if !usable {
            report.problems.push(PreflightProblem {
                code: PreflightProblemCode::MissingPrerequisite,
                meaning: if is_worker_binary {
                    format!(
                        "the remote Fern runner {} is absent, a directory, or not executable",
                        path.requested_path
                    )
                } else if is_known_data {
                    format!(
                        "the remote recorded-data root {} is absent or is not a directory",
                        path.requested_path
                    )
                } else {
                    format!(
                        "the remote path {} has no writable existing ancestor",
                        path.requested_path
                    )
                },
                fix_command: format!(
                    "prepare that path on {}, then repeat fern-run doctor --on {} --observer host",
                    profile.ssh_destination.as_deref().unwrap_or("the Worker Site"),
                    profile.profile_id
                ),
            });
        }
    }
}

fn append_profile_drift_problems(
    report: &mut FrontDoorReport,
    drift: &[ExecutionProfileDrift],
    label: &str,
    fix_command: &str,
) {
    for mismatch in drift {
        report.problems.push(PreflightProblem {
            code: PreflightProblemCode::ProfileDrift,
            meaning: format!(
                "{label} at {}: expected {}; observed {}",
                mismatch.field, mismatch.expected, mismatch.observed
            ),
            fix_command: fix_command.to_string(),
        });
    }
}

const PYTHON_EXECUTION_PROBE_SOURCE: &str = r#"
import importlib
import importlib.util
import json
import os
import platform
import shutil
import subprocess
import sys
sys.dont_write_bytecode = True
import traceback

module = sys.argv[1]
requested_paths = sys.argv[2:]

if shutil.which("uv") is None:
    print("uv is unavailable; install uv before preparing or repairing this execution environment", file=sys.stderr)
    sys.exit(1)

import torch

# IMPORT the worker, do not merely locate it.
#
# This asked `find_spec`, which answers "is there a file of that name" and
# nothing else. A module that exists and raises on import passed preflight
# unharmed, and the failure then surfaced at the FIRST CELL -- for a zero-shot
# model, one line into scoring, after the forecast had already been computed and
# thrown away. On 2026-08-12 a narrowed re-export list in fr_cfg broke two
# imports inside fr_tr_exp.evaluate and killed the local Zeus lane twice that
# way, each time paying a full inference to learn it.
#
# Importing costs about eleven seconds beyond the torch import already above,
# once per Matrix, and it runs before any authority is claimed. The worker
# module is guarded by `if __name__ == "__main__"`, so importing it starts no
# work.
try:
    importlib.import_module(module)
except BaseException:
    print(f"cannot import the declared worker module {module!r}:", file=sys.stderr)
    traceback.print_exc()
    sys.exit(1)
cuda_available = torch.cuda.is_available()
devices = []
if cuda_available:
    for index in range(torch.cuda.device_count()):
        props = torch.cuda.get_device_properties(index)
        devices.append({
            "index": index,
            "name": props.name,
            "capability": list(torch.cuda.get_device_capability(index)),
            "total_memory_bytes": int(props.total_memory),
            "multi_processor_count": int(props.multi_processor_count),
            "uuid": None,
            "driver_version": None,
        })

storage = []
paths = []
seen_devices = set()
for requested in requested_paths:
    existing = requested
    while not os.path.exists(existing):
        parent = os.path.dirname(existing)
        if parent == existing:
            break
        existing = parent
    exists = os.path.exists(requested)
    is_dir = os.path.isdir(requested)
    writable_ancestor = os.path.exists(existing) and os.access(existing, os.W_OK)
    paths.append({
        "requested_path": requested,
        "exists": exists,
        "is_dir": is_dir,
        "executable": exists and os.access(requested, os.X_OK),
        "writable_ancestor": writable_ancestor,
    })
    if os.path.exists(existing):
        device = str(os.stat(existing).st_dev)
        if device not in seen_devices:
            seen_devices.add(device)
            usage = shutil.disk_usage(existing)
            storage.append({
                "requested_path": requested,
                "inspected_path": existing,
                "filesystem": "device:" + device,
                "available_bytes": int(usage.free),
                "total_bytes": int(usage.total),
            })

nvidia_management_facts = []
try:
    query = subprocess.run(
        ["nvidia-smi", "--query-gpu=uuid,driver_version", "--format=csv,noheader,nounits"],
        check=True,
        capture_output=True,
        text=True,
    )
    for line in query.stdout.splitlines():
        fields = [field.strip() for field in line.split(",")]
        if len(fields) == 2 and all(fields):
            nvidia_management_facts.append({"uuid": fields[0], "driver_version": fields[1]})
except (FileNotFoundError, subprocess.SubprocessError):
    pass

report = json.dumps({
    "python": {
        "sys_executable": sys.executable,
        "version": platform.python_version(),
        "implementation": platform.python_implementation(),
        "worker_module": module,
        "worker_module_available": importlib.util.find_spec(module) is not None,
    },
    "platform": {
        "hostname": platform.node(),
        "system": platform.system(),
        "release": platform.release(),
        "machine": platform.machine(),
    },
    "torch": {
        "version": str(torch.__version__),
        "cuda_runtime_version": torch.version.cuda,
        "cudnn_version": torch.backends.cudnn.version(),
        "deterministic_algorithms": torch.are_deterministic_algorithms_enabled(),
        "float32_matmul_precision": torch.get_float32_matmul_precision(),
        "cuda_matmul_allow_tf32": bool(torch.backends.cuda.matmul.allow_tf32),
        "cudnn_allow_tf32": bool(torch.backends.cudnn.allow_tf32),
        "cudnn_deterministic": bool(torch.backends.cudnn.deterministic),
        "cudnn_benchmark": bool(torch.backends.cudnn.benchmark),
    },
    "cuda_available": cuda_available,
    "cuda_current_device": torch.cuda.current_device() if cuda_available else None,
    "cuda_visible_devices": os.environ.get("CUDA_VISIBLE_DEVICES"),
    "cuda_device_order": os.environ.get("CUDA_DEVICE_ORDER"),
    "nvidia_visible_devices": os.environ.get("NVIDIA_VISIBLE_DEVICES"),
    "cuda_devices": devices,
    "storage": storage,
    "paths": paths,
    "nvidia_management_facts": nvidia_management_facts,
})
sys.stdout.write(report)
sys.stdout.flush()

# This process is a read-only environment probe, not a training worker.  WSL
# NVIDIA teardown can hang after every requested fact has already been
# collected and serialized.  Exit without running CUDA finalizers so doctor
# reports the observed host state instead of waiting forever after success.
os._exit(0)
"#;

fn probe_execution(profile: &ExecutionProfile) -> Result<ObservedExecutionFingerprint, ProbeFailure> {
    let source_root = profile.source_root.to_string_lossy();
    let output = ShellCommand::new(&profile.python)
        .env("PYTHONDONTWRITEBYTECODE", "1")
        .env("FERN_PROBE_SOURCE_ROOT", source_root.as_ref())
        .env("PYTHONPATH", format!("{source_root}:{source_root}/python"))
        .args(["-c", PYTHON_EXECUTION_PROBE_SOURCE, &profile.worker.module])
        .output()
        .map_err(|error| ProbeFailure::InterpreterUnavailable {
            python: profile.python.clone(),
            source_detail: error.to_string(),
        })?;
    if !output.status.success() {
        return Err(ProbeFailure::ProbeScriptFailed {
            python: profile.python.clone(),
            stderr: String::from_utf8_lossy(&output.stderr).trim().to_string(),
        });
    }
    let mut probe: PythonProbe =
        serde_json::from_slice(&output.stdout).map_err(|error| ProbeFailure::ProbeUnreadable {
            python: profile.python.clone(),
            detail: error.to_string(),
        })?;
    attach_nvidia_management_facts(
        &mut probe.cuda_devices,
        probe.cuda_visible_devices.as_deref(),
        probe.nvidia_visible_devices.as_deref(),
    );
    Ok(observed_execution_from_probe(profile, probe))
}

fn observed_execution_from_probe(profile: &ExecutionProfile, probe: PythonProbe) -> ObservedExecutionFingerprint {
    ObservedExecutionFingerprint {
        schema: OBSERVED_EXECUTION_FINGERPRINT_SCHEMA.to_string(),
        observed_at_unix_ms: SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis()
            .try_into()
            .unwrap_or(u64::MAX),
        fern_version: env!("CARGO_PKG_VERSION").to_string(),
        platform: probe.platform,
        python: ObservedPythonRuntime {
            requested_executable: profile.python.clone(),
            sys_executable: probe.python.sys_executable,
            version: probe.python.version,
            implementation: probe.python.implementation,
            worker_module: probe.python.worker_module,
            worker_module_available: probe.python.worker_module_available,
        },
        torch: probe.torch,
        cuda_available: probe.cuda_available,
        cuda_current_device: probe.cuda_current_device,
        cuda_visible_devices: probe.cuda_visible_devices,
        cuda_device_order: probe.cuda_device_order,
        nvidia_visible_devices: probe.nvidia_visible_devices,
        cuda_devices: probe.cuda_devices,
    }
}

fn probe_remote_execution(profile: &ExecutionProfile, helper: &Path) -> Result<RemotePythonProbe, RemoteProbeFailure> {
    let destination = profile
        .ssh_destination
        .as_deref()
        .ok_or(RemoteProbeFailure::MissingDestination)?;
    let mut command = ShellCommand::new(helper);
    let source_root = profile.source_root.to_string_lossy();
    command
        .env("RUNPOD_SSH_TARGET", destination)
        .env(
            "RUNPOD_COMMAND_TIMEOUT_SECONDS",
            REMOTE_EXECUTION_PROBE_TIMEOUT_SECONDS.to_string(),
        )
        .args(["exec", "--"])
        .arg("env")
        .arg("PYTHONDONTWRITEBYTECODE=1")
        .arg(format!("FERN_PROBE_SOURCE_ROOT={source_root}"))
        .arg(format!("PYTHONPATH={source_root}:{source_root}/python"))
        .arg(&profile.python)
        .args(["-c", PYTHON_EXECUTION_PROBE_SOURCE, &profile.worker.module])
        .arg(&profile.campaign_root)
        .arg(&profile.artifact_root)
        .arg(&profile.work_root)
        .arg(&profile.worker_binary);
    if let Some(known_data_root) = &profile.known_data_root {
        command.arg(known_data_root);
    }
    let output = command
        .output()
        .map_err(|error| RemoteProbeFailure::HelperUnavailable {
            helper: helper.to_path_buf(),
            source_detail: error.to_string(),
        })?;
    if !output.status.success() {
        return Err(RemoteProbeFailure::TransportOrRemoteFailed {
            destination: destination.to_string(),
            stderr: String::from_utf8_lossy(&output.stderr).trim().to_string(),
        });
    }
    serde_json::from_slice(&output.stdout).map_err(|error| RemoteProbeFailure::ReportUnreadable {
        destination: destination.to_string(),
        detail: error.to_string(),
    })
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct NvidiaManagementFact {
    uuid: String,
    driver_version: String,
}

/// Add UUID and driver facts only when logical-to-physical mapping is proven.
///
/// PyTorch indexes devices after `CUDA_VISIBLE_DEVICES` is applied, while
/// `nvidia-smi` reports management-layer devices. Equating their integer
/// indices can therefore attach GPU 0's UUID to work that actually ran on GPU
/// 1. An ambiguous mapping stays `null`; missing provenance is safer than a
/// confident false identity.
fn attach_nvidia_management_facts(
    devices: &mut [ObservedCudaDevice],
    cuda_visible_devices: Option<&str>,
    nvidia_visible_devices: Option<&str>,
) {
    let Ok(output) = ShellCommand::new("nvidia-smi")
        .args(["--query-gpu=uuid,driver_version", "--format=csv,noheader,nounits"])
        .output()
    else {
        return;
    };
    if !output.status.success() {
        return;
    }
    let facts = parse_nvidia_management_facts(&String::from_utf8_lossy(&output.stdout));
    attach_known_nvidia_management_facts(devices, cuda_visible_devices, nvidia_visible_devices, &facts);
}

fn parse_nvidia_management_facts(output: &str) -> Vec<NvidiaManagementFact> {
    output
        .lines()
        .filter_map(|line| {
            let fields = line.split(',').map(str::trim).collect::<Vec<_>>();
            if fields.len() != 2 {
                return None;
            }
            if fields[0].is_empty() || fields[1].is_empty() {
                return None;
            }
            Some(NvidiaManagementFact {
                uuid: fields[0].to_string(),
                driver_version: fields[1].to_string(),
            })
        })
        .collect()
}

fn attach_known_nvidia_management_facts(
    devices: &mut [ObservedCudaDevice],
    cuda_visible_devices: Option<&str>,
    _nvidia_visible_devices: Option<&str>,
    facts: &[NvidiaManagementFact],
) {
    let cuda_mask = cuda_visible_devices.map(str::trim).filter(|mask| !mask.is_empty());
    let one_to_one_inventory = devices.len() == 1 && facts.len() == 1;

    for device in devices {
        let selector = cuda_mask
            .and_then(|mask| mask.split(',').nth(device.index as usize))
            .map(str::trim);
        let fact = match selector {
            Some(token) if token.starts_with("GPU-") => {
                let matches = facts
                    .iter()
                    .filter(|fact| fact.uuid == token || fact.uuid.starts_with(token))
                    .collect::<Vec<_>>();
                if matches.len() == 1 { Some(matches[0]) } else { None }
            }
            _ if one_to_one_inventory => facts.first(),
            _ => None,
        };
        if let Some(fact) = fact {
            device.uuid = Some(fact.uuid.clone());
            device.driver_version = Some(fact.driver_version.clone());
        }
    }
}

fn probe_storage(requested_path: &Path) -> Result<StorageCheck, String> {
    let existing_path = requested_path
        .ancestors()
        .find(|path| path.exists())
        .ok_or_else(|| format!("no existing ancestor for declared path {}", requested_path.display()))?;
    let output = ShellCommand::new("df")
        .args(["-Pk", &existing_path.display().to_string()])
        .output()
        .map_err(|error| format!("cannot inspect storage for {}: {error}", existing_path.display()))?;
    if !output.status.success() {
        return Err(format!(
            "df cannot inspect {}: {}",
            existing_path.display(),
            String::from_utf8_lossy(&output.stderr).trim()
        ));
    }
    let output_text = String::from_utf8_lossy(&output.stdout);
    let line = output_text
        .lines()
        .skip(1)
        .find(|line| !line.trim().is_empty())
        .ok_or_else(|| format!("df returned no data row for {}", existing_path.display()))?;
    let fields = line.split_whitespace().collect::<Vec<_>>();
    if fields.len() < 6 {
        return Err(format!("df returned an incomplete row for {}", existing_path.display()));
    }
    let available_kib = fields[3].parse::<f64>().map_err(|error| {
        format!(
            "df returned invalid available space for {}: {error}",
            existing_path.display()
        )
    })?;
    let used_percent = fields[4].trim_end_matches('%').parse::<f64>().map_err(|error| {
        format!(
            "df returned invalid usage percent for {}: {error}",
            existing_path.display()
        )
    })?;
    let available_gib = available_kib / (1024.0 * 1024.0);
    let free_percent = (100.0 - used_percent).max(0.0);
    Ok(StorageCheck {
        inspected_path: existing_path.display().to_string(),
        filesystem: fields[0].to_string(),
        available_gib,
        free_percent,
        warning: available_gib < 50.0 || free_percent < 10.0,
        hard_stop: available_gib < CAMPAIGN_STORAGE_HARD_STOP_FREE_GIB as f64,
    })
}

fn blocked_report(
    command: &'static str,
    profile_id: &str,
    profile_path: PathBuf,
    observer: ObserverContext,
    problems: Vec<PreflightProblem>,
) -> FrontDoorReport {
    FrontDoorReport {
        schema: FRONT_DOOR_SCHEMA,
        command,
        status: "blocked",
        gpu_work_started: false,
        observer,
        execution_facts_from: None,
        profile_id: profile_id.to_string(),
        profile_path: profile_path.display().to_string(),
        profile_revision: None,
        executor: None,
        device: None,
        campaign_root: None,
        observed_execution: None,
        profile_drift: Vec::new(),
        storage: Vec::new(),
        selected_preset: None,
        selected_model: None,
        canary: None,
        recipes: Vec::new(),
        stages: Vec::new(),
        estimated_total_seconds: None,
        problems,
        typed_failures: Vec::new(),
        next_step: "Fix the listed prerequisite, then repeat this command. No GPU work has started.".to_string(),
    }
}

fn close_report(mut report: FrontDoorReport) -> FrontDoorReport {
    if !report.problems.is_empty() {
        let needs_only_host_check = report
            .problems
            .iter()
            .all(|problem| matches!(problem.code, PreflightProblemCode::HostCheckRequired));
        report.status = if needs_only_host_check {
            "needs_host_check"
        } else {
            "blocked"
        };
        if needs_only_host_check {
            report.next_step = format!(
                "Repeat this read-only check outside the Codex sandbox: fern-run doctor --on {} --observer host. No GPU work has started.",
                report.profile_id
            );
        } else if report.next_step == "just catalog" {
            report.next_step =
                "Fix the listed prerequisite, then repeat this command. No GPU work has started.".to_string();
        }
    }
    report
}

fn device_name(device: DeviceKind) -> &'static str {
    match device {
        DeviceKind::Cpu => "cpu",
        DeviceKind::Cuda => "cuda",
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use fern_execution_profile::EXECUTION_PROFILE_SCHEMA;
    use std::os::unix::fs::PermissionsExt;

    const TEST_REMOTE_BINARY_SHA256: &str = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    #[test]
    fn plan_uses_the_same_preset_model_selection_as_the_runner() {
        let recipes = fern_run::csharp_experiment::select_catalog_recipes(
            None,
            Some("paper-core-h64"),
            &[RecipeModelName::Dlinear.value()],
        )
        .unwrap();
        assert!(!recipes.is_empty());
        assert!(recipes.iter().all(|recipe| recipe.model == RecipeModelName::Dlinear));
    }

    #[test]
    fn canary_selection_is_one_cell_per_recipe() {
        let recipes = fern_run::csharp_experiment::select_catalog_recipes(
            None,
            Some("paper-core-h64"),
            &[RecipeModelName::Dlinear.value()],
        )
        .unwrap();
        for recipe in recipes {
            let cells = recipe_target_registrations_for_device(&recipe, 42, 1000, DeviceKind::Cpu).unwrap();
            assert!(!cells.is_empty());
            assert_eq!(cells.iter().take(1).count(), 1);
        }
    }

    #[test]
    fn profile_schema_constant_is_not_an_implicit_default() {
        assert_eq!(EXECUTION_PROFILE_SCHEMA, "fern.execution_profile.v5");
    }

    #[test]
    fn execution_probe_disables_bytecode_before_importing_the_worker() {
        let disable = PYTHON_EXECUTION_PROBE_SOURCE
            .find("sys.dont_write_bytecode = True")
            .unwrap();
        let worker_import = PYTHON_EXECUTION_PROBE_SOURCE
            .find("importlib.import_module(module)")
            .unwrap();
        assert!(disable < worker_import);
    }

    fn logical_cuda_devices(count: u32) -> Vec<ObservedCudaDevice> {
        (0..count)
            .map(|index| ObservedCudaDevice {
                index,
                name: format!("logical CUDA device {index}"),
                capability: [8, 9],
                total_memory_bytes: 8 * 1_073_741_824,
                multi_processor_count: 36,
                uuid: None,
                driver_version: None,
            })
            .collect()
    }

    fn nvidia_facts() -> Vec<NvidiaManagementFact> {
        vec![
            NvidiaManagementFact {
                uuid: "GPU-physical-zero".to_string(),
                driver_version: "600.1".to_string(),
            },
            NvidiaManagementFact {
                uuid: "GPU-physical-one".to_string(),
                driver_version: "600.1".to_string(),
            },
        ]
    }

    fn observed_without_cuda(profile: &ExecutionProfile) -> ObservedExecutionFingerprint {
        ObservedExecutionFingerprint {
            schema: OBSERVED_EXECUTION_FINGERPRINT_SCHEMA.to_string(),
            observed_at_unix_ms: 1_700_000_000_000,
            fern_version: "0.1.0".to_string(),
            platform: ObservedPlatform {
                hostname: "restricted-process".to_string(),
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
            cuda_available: false,
            cuda_current_device: None,
            cuda_visible_devices: None,
            cuda_device_order: None,
            nvidia_visible_devices: None,
            cuda_devices: Vec::new(),
        }
    }

    fn empty_report(observer: ObserverContext) -> FrontDoorReport {
        blocked_report(
            "doctor",
            "local-4070",
            PathBuf::from("profiles/local-4070.json"),
            observer,
            Vec::new(),
        )
    }

    #[test]
    fn cuda_hidden_from_codex_sandbox_requires_host_check_instead_of_condemning_the_gpu() {
        let profile = ExecutionProfile::local_template(
            "local-4070".to_string(),
            Path::new("/home/user/study"),
            Path::new("/home/user"),
        );
        let mut report = empty_report(ObserverContext::CodexSandbox);

        record_local_execution_observation(
            &mut report,
            &profile,
            ObserverContext::CodexSandbox,
            observed_without_cuda(&profile),
        );
        let report = close_report(report);

        assert_eq!(report.status, "needs_host_check");
        assert_eq!(report.execution_facts_from, Some(ExecutionFactsSource::CodexSandbox));
        assert!(matches!(
            report.problems[0].code,
            PreflightProblemCode::HostCheckRequired
        ));
        assert!(report.problems[0].meaning.contains("does not establish"));
        assert!(report.next_step.contains("--observer host"));
    }

    #[test]
    fn cuda_missing_from_a_declared_host_check_is_real_profile_drift() {
        let profile = ExecutionProfile::local_template(
            "local-4070".to_string(),
            Path::new("/home/user/study"),
            Path::new("/home/user"),
        );
        let mut report = empty_report(ObserverContext::Host);

        record_local_execution_observation(
            &mut report,
            &profile,
            ObserverContext::Host,
            observed_without_cuda(&profile),
        );
        let report = close_report(report);

        assert_eq!(report.status, "blocked");
        assert_eq!(report.execution_facts_from, Some(ExecutionFactsSource::Host));
        assert!(matches!(report.problems[0].code, PreflightProblemCode::ProfileDrift));
    }

    #[test]
    fn numeric_cuda_visibility_does_not_guess_a_management_index_on_multi_gpu_hosts() {
        let mut devices = logical_cuda_devices(1);
        attach_known_nvidia_management_facts(&mut devices, Some("1"), None, &nvidia_facts());

        assert_eq!(devices[0].uuid, None);
    }

    #[test]
    fn explicit_container_visibility_without_a_proven_mapping_leaves_uuid_unknown() {
        let mut devices = logical_cuda_devices(1);
        attach_known_nvidia_management_facts(&mut devices, None, Some("1"), &nvidia_facts());

        assert_eq!(devices[0].uuid, None);
        assert_eq!(devices[0].driver_version, None);
    }

    #[test]
    fn cuda_uuid_visibility_maps_without_trusting_an_ordinal() {
        let mut devices = logical_cuda_devices(1);
        attach_known_nvidia_management_facts(
            &mut devices,
            Some("GPU-physical-one"),
            Some("GPU-physical-one"),
            &nvidia_facts(),
        );

        assert_eq!(devices[0].uuid.as_deref(), Some("GPU-physical-one"));
    }

    #[test]
    fn one_visible_cuda_device_and_one_management_device_can_be_joined() {
        let mut devices = logical_cuda_devices(1);
        let one_fact = vec![NvidiaManagementFact {
            uuid: "GPU-only-visible-card".to_string(),
            driver_version: "600.1".to_string(),
        }];
        attach_known_nvidia_management_facts(&mut devices, Some("7"), Some("7"), &one_fact);

        assert_eq!(devices[0].uuid.as_deref(), Some("GPU-only-visible-card"));
    }

    #[test]
    fn ambiguous_cuda_uuid_prefix_does_not_invent_a_physical_identity() {
        let mut devices = logical_cuda_devices(1);
        let facts = vec![
            NvidiaManagementFact {
                uuid: "GPU-shared-prefix-a".to_string(),
                driver_version: "600.1".to_string(),
            },
            NvidiaManagementFact {
                uuid: "GPU-shared-prefix-b".to_string(),
                driver_version: "600.1".to_string(),
            },
        ];
        attach_known_nvidia_management_facts(&mut devices, Some("GPU-shared-prefix"), None, &facts);

        assert_eq!(devices[0].uuid, None);
    }

    #[test]
    fn malformed_nvidia_management_rows_are_ignored_instead_of_half_parsed() {
        let facts =
            parse_nvidia_management_facts("bad row\nGPU-valid, 600.1\nGPU-missing-driver,\nGPU-too,many,fields\n");

        assert_eq!(
            facts,
            vec![NvidiaManagementFact {
                uuid: "GPU-valid".to_string(),
                driver_version: "600.1".to_string(),
            }]
        );
    }

    #[test]
    fn plan_pricing_uses_the_same_median_as_estimate_stage() {
        let estimate = stage_runtime_from_durations(vec![660.0, 680.0, 688.0, 720.0], 28);
        assert_eq!(estimate.comparable_attempts, 4);
        assert_eq!(estimate.median_seconds_per_cell, Some(684.0));
        assert_eq!(estimate.estimated_stage_seconds, Some(19_152.0));
        assert_eq!(estimate.estimated_stage_seconds.unwrap() / 60.0, 319.2);
    }

    #[test]
    fn stage_runtime_history_reader_crosses_csharp_matrix_process() {
        let nanos = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
        let root = std::env::temp_dir().join(format!("fern_stage_runtime_reader_{nanos}"));
        std::fs::create_dir_all(&root).unwrap();
        let registry = root.join("lifecycle.db");
        crate::apply_registry_migrations_via_home(None, &registry).unwrap();

        let estimate = comparable_stage_runtime(&registry, "recipe-a", "profile-revision", 3).unwrap();
        assert_eq!(estimate.comparable_attempts, 0);
        assert_eq!(estimate.median_seconds_per_cell, None);
        assert_eq!(estimate.estimated_stage_seconds, None);

        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn ssh_profile_fails_closed_when_the_remote_probe_cannot_start() {
        let profile = ExecutionProfile::ssh_template(
            "runpod-a40".to_string(),
            "runpod".to_string(),
            "local-4070".to_string(),
            TEST_REMOTE_BINARY_SHA256.to_string(),
            Path::new("/workspace/study"),
            Path::new("/home/user"),
        );
        let report = profile_report_with_remote_helper(
            "doctor",
            "runpod-a40",
            PathBuf::from("profiles/runpod-a40.json"),
            &profile,
            ObserverContext::Host,
            Path::new("/definitely/missing/fern-runpod-ssh-helper"),
        );
        let expected_revision = profile.resolve().unwrap().revision.revision_hash;
        assert_eq!(report.schema, "fern.experiment_preflight.v4");
        assert_eq!(report.profile_revision.as_deref(), Some(expected_revision.as_str()));
        assert_eq!(report.status, "blocked");
        assert!(
            report
                .problems
                .iter()
                .any(|problem| matches!(problem.code, PreflightProblemCode::RemoteProbeRequired))
        );
        assert!(!report.gpu_work_started);
    }

    #[test]
    fn ssh_failure_inside_codex_sandbox_requests_host_recheck_without_claiming_remote_state() {
        let profile = ExecutionProfile::ssh_template(
            "runpod-a40".to_string(),
            "runpod".to_string(),
            "local-4070".to_string(),
            TEST_REMOTE_BINARY_SHA256.to_string(),
            Path::new("/workspace/study"),
            Path::new("/home/user"),
        );

        let report = profile_report_with_remote_helper(
            "doctor",
            "runpod-a40",
            PathBuf::from("profiles/runpod-a40.json"),
            &profile,
            ObserverContext::CodexSandbox,
            Path::new("/definitely/missing/fern-runpod-ssh-helper"),
        );

        assert_eq!(report.status, "needs_host_check");
        assert_eq!(report.execution_facts_from, None);
        assert!(matches!(
            report.problems[0].code,
            PreflightProblemCode::HostCheckRequired
        ));
        let meaning = &report.problems[0].meaning;
        assert!(
            meaning.contains("trustworthy remote read-only observation"),
            "{meaning}"
        );
        assert!(meaning.contains("RunPod state remains unknown"), "{meaning}");
        assert!(!meaning.contains("authenticate"), "{meaning}");
        assert!(report.next_step.contains("--observer host"));
    }

    #[test]
    fn execution_probe_checks_uv_before_importing_torch_or_the_worker() {
        let uv_check = PYTHON_EXECUTION_PROBE_SOURCE
            .find("shutil.which(\"uv\")")
            .expect("probe checks uv");
        let torch_import = PYTHON_EXECUTION_PROBE_SOURCE
            .find("import torch")
            .expect("probe imports torch");
        let worker_import = PYTHON_EXECUTION_PROBE_SOURCE
            .find("importlib.import_module(module)")
            .expect("probe imports the worker");

        assert!(uv_check < torch_import);
        assert!(uv_check < worker_import);
    }

    #[test]
    fn ssh_profile_uses_remote_facts_instead_of_local_paths_or_gpu_names() {
        let profile = ExecutionProfile::ssh_template(
            "runpod-a40".to_string(),
            "runpod".to_string(),
            "local-4070".to_string(),
            TEST_REMOTE_BINARY_SHA256.to_string(),
            Path::new("/workspace/study"),
            Path::new("/home/user"),
        );
        let unique = format!(
            "fern-remote-probe-{}-{}",
            std::process::id(),
            SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos()
        );
        let temp_dir = std::env::temp_dir().join(unique);
        std::fs::create_dir_all(&temp_dir).unwrap();
        let helper = temp_dir.join("remote-probe-stub.sh");
        let script = r#"#!/bin/sh
test "$RUNPOD_SSH_TARGET" = "runpod" || exit 41
test "$RUNPOD_COMMAND_TIMEOUT_SECONDS" = "180" || exit 52
test "$1" = "exec" || exit 42
test "$2" = "--" || exit 43
test "$3" = "env" || exit 44
test "$4" = "PYTHONDONTWRITEBYTECODE=1" || exit 45
test "$5" = "FERN_PROBE_SOURCE_ROOT=/workspace/study" || exit 46
test "$6" = "PYTHONPATH=/workspace/study:/workspace/study/python" || exit 47
test "$7" = "/home/user/.venv/bin/python" || exit 48
test "$8" = "-c" || exit 49
test "${10}" = "fr_tr" || exit 50
test "${11}" = "/workspace/study/results/paper_campaign" || exit 51
test "${12}" = "/workspace/study/results/runs" || exit 53
test "${13}" = "/workspace/study/tmp/fern-run-worker" || exit 54
test "${14}" = "__FERN_WORKER_BINARY__" || exit 55
test "${15}" = "/home/user/.cache/serie/known" || exit 56
cat <<'JSON'
{"python":{"sys_executable":"/home/user/.venv/bin/python","version":"3.12.1","implementation":"CPython","worker_module":"fr_tr","worker_module_available":true},"platform":{"hostname":"remote-worker","system":"Linux","release":"test","machine":"x86_64"},"torch":{"version":"2.9.0","cuda_runtime_version":"12.8","cudnn_version":9100,"deterministic_algorithms":false,"float32_matmul_precision":"highest","cuda_matmul_allow_tf32":false,"cudnn_allow_tf32":true,"cudnn_deterministic":false,"cudnn_benchmark":false},"cuda_available":true,"cuda_current_device":0,"cuda_visible_devices":null,"cuda_device_order":null,"nvidia_visible_devices":null,"cuda_devices":[{"index":0,"name":"NVIDIA A40","capability":[8,6],"total_memory_bytes":51539607552,"multi_processor_count":84,"uuid":null,"driver_version":null}],"storage":[{"requested_path":"/workspace/study/results/paper_campaign","inspected_path":"/workspace/study","filesystem":"device:7","available_bytes":107374182400,"total_bytes":214748364800}],"paths":[{"requested_path":"/workspace/study/results/paper_campaign","exists":true,"is_dir":true,"executable":true,"writable_ancestor":true},{"requested_path":"/workspace/study/results/runs","exists":false,"is_dir":false,"executable":false,"writable_ancestor":true},{"requested_path":"/workspace/study/tmp/fern-run-worker","exists":false,"is_dir":false,"executable":false,"writable_ancestor":true},{"requested_path":"__FERN_WORKER_BINARY__","exists":true,"is_dir":false,"executable":true,"writable_ancestor":true},{"requested_path":"/home/user/.cache/serie/known","exists":true,"is_dir":true,"executable":true,"writable_ancestor":true}],"nvidia_management_facts":[{"uuid":"GPU-remote-a40","driver_version":"600.1"}]}
JSON
"#;
        let script = script.replace("__FERN_WORKER_BINARY__", &profile.worker_binary.display().to_string());
        std::fs::write(&helper, script).unwrap();
        let mut permissions = std::fs::metadata(&helper).unwrap().permissions();
        permissions.set_mode(0o755);
        std::fs::set_permissions(&helper, permissions).unwrap();

        let report = profile_report_with_remote_helper(
            "doctor",
            "runpod-a40",
            PathBuf::from("profiles/runpod-a40.json"),
            &profile,
            ObserverContext::Host,
            &helper,
        );

        std::fs::remove_dir_all(&temp_dir).unwrap();
        assert_eq!(report.status, "ready");
        assert_eq!(report.observer, ObserverContext::Host);
        assert_eq!(
            report.execution_facts_from,
            Some(ExecutionFactsSource::RemoteWorkerSite)
        );
        assert!(report.problems.is_empty());
        assert_eq!(
            report.observed_execution.as_ref().unwrap().platform.hostname,
            "remote-worker"
        );
        assert_eq!(
            report.observed_execution.as_ref().unwrap().cuda_devices[0]
                .uuid
                .as_deref(),
            Some("GPU-remote-a40")
        );
        assert_eq!(report.storage.len(), 1);
        assert!(!report.gpu_work_started);
    }

    #[test]
    fn stage_estimate_median_is_stable_for_odd_and_even_samples() {
        assert_eq!(median(&[]), None);
        assert_eq!(median(&[8.0]), Some(8.0));
        assert_eq!(median(&[8.0, 10.0]), Some(9.0));
        assert_eq!(median(&[8.0, 10.0, 400.0]), Some(10.0));
    }

    /// Pilot gates 2-4: each probe failure derives its own code,
    /// remediation, and retry; the envelope's message equals Display; the
    /// fix text is rendered FROM the remediation data (a missing
    /// interpreter no longer gets torch advice); and nothing parses a
    /// formatted message back into policy.
    #[test]
    fn probe_failures_derive_distinct_remediation_and_the_envelope_mirrors_display() {
        let missing = ProbeFailure::InterpreterUnavailable {
            python: PathBuf::from("/venv/bin/python"),
            source_detail: "No such file or directory (os error 2)".to_string(),
        };
        let broken = ProbeFailure::ProbeScriptFailed {
            python: PathBuf::from("/venv/bin/python"),
            stderr: "ModuleNotFoundError: No module named 'fr_tr'".to_string(),
        };
        let unreadable = ProbeFailure::ProbeUnreadable {
            python: PathBuf::from("/venv/bin/python"),
            detail: "expected value at line 1 column 1".to_string(),
        };

        for failure in [&missing, &broken, &unreadable] {
            let envelope = failure.envelope();
            assert_eq!(envelope.schema, TYPED_PREFLIGHT_FAILURE_SCHEMA);
            assert_eq!(
                envelope.message,
                failure.to_string(),
                "message IS the Display rendering"
            );
            assert!(matches!(envelope.retry, RetryDisposition::NotRetryableWithoutChange));
            assert!(matches!(envelope.code, PreflightProblemCode::MissingPrerequisite));
        }

        // The remediation is data, distinct per variant, and the human fix
        // text is rendered from it: a missing interpreter is told to
        // restore the interpreter, never to "repair … torch".
        let missing_fix = missing.remediation().fix_command("local-4070");
        assert!(
            missing_fix.contains("install or restore the Python interpreter"),
            "{missing_fix}"
        );
        assert!(!missing_fix.contains("torch"), "{missing_fix}");
        let broken_fix = broken.remediation().fix_command("local-4070");
        assert!(broken_fix.contains("execution probe passes"), "{broken_fix}");
        assert!(missing_fix.contains("--on local-4070"), "{missing_fix}");

        // The envelope serializes remediation as tagged fields, so an
        // agent reads kind and paths without touching prose, and there is
        // no executable command hidden in the structured half.
        let json = serde_json::to_value(broken.envelope()).unwrap();
        assert_eq!(json["remediation"]["kind"], "repair_python_environment");
        assert_eq!(json["remediation"]["python"], "/venv/bin/python");
        assert_eq!(json["retry"], "not_retryable_without_change");
        assert!(
            json["remediation"].get("command").is_none(),
            "remediation carries no shell command"
        );
    }

    /// The remote half: observer context enters at derivation time; a
    /// missing destination is a profile fault (not a transport story, and
    /// never the sandbox's authentication story); transport failures get
    /// the unlock/check/doctor path on host and the repeat-from-host path
    /// in the sandbox.
    #[test]
    fn remote_probe_failures_derive_by_observer_and_a_missing_destination_is_a_profile_fault() {
        let transport = RemoteProbeFailure::TransportOrRemoteFailed {
            destination: "root@pod:22".to_string(),
            stderr: "Connection refused".to_string(),
        };
        assert!(matches!(
            transport.code(ObserverContext::Host),
            PreflightProblemCode::RemoteProbeRequired
        ));
        assert!(matches!(
            transport.code(ObserverContext::CodexSandbox),
            PreflightProblemCode::HostCheckRequired
        ));
        let host_fix = transport.remediation(ObserverContext::Host).fix_command("runpod-5090");
        assert!(host_fix.contains("unlock"), "{host_fix}");
        assert!(host_fix.contains(RUNPOD_SSH_HELPER), "{host_fix}");
        let sandbox_fix = transport
            .remediation(ObserverContext::CodexSandbox)
            .fix_command("runpod-5090");
        assert!(sandbox_fix.contains("host access"), "{sandbox_fix}");

        let missing = RemoteProbeFailure::MissingDestination;
        assert!(matches!(
            missing.code(ObserverContext::CodexSandbox),
            PreflightProblemCode::InvalidProfile
        ));
        let missing_fix = missing.remediation(ObserverContext::Host).fix_command("runpod-5090");
        assert!(missing_fix.contains("declare ssh_destination"), "{missing_fix}");

        let envelope = transport.envelope(ObserverContext::Host);
        assert_eq!(envelope.message, transport.to_string());
        let json = serde_json::to_value(&envelope).unwrap();
        assert_eq!(json["remediation"]["kind"], "restore_ssh_transport");
        assert_eq!(json["remediation"]["helper"], RUNPOD_SSH_HELPER);
    }
}
