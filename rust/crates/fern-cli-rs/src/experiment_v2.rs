//! V2 Experiment compile front door.
//!
//! `plan-experiment` is deliberately the only route from compositional
//! authoring to an executable object. It rechecks the frozen implementation
//! from the named Implementation Binding with the profile's actual Python,
//! lowers every cell to `fern.cfg.v2`, runs the production model-link probe,
//! and then writes one immutable compiled object. It reads Registry Home to
//! resolve that Binding but starts no GPU worker.

use std::collections::{BTreeMap, BTreeSet};
use std::io::Read;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};

use crate::{Fallible, invalid_input, write_json_file};
use clap::Args;
use fern_execution_profile::{ExecutionProfile, ExecutionProfileExecutor, ResolvedExecutionProfile};
use fern_experiment_types::{Sha256Digest, StageImplementationRefV1, StagePipelineTemplateV1, StageRunnerV1};
use fern_json_contract::{JsonContract, checked_from_slice};
use fern_recipes::{
    AblationResolutionV2, DrawRef, ExperimentForkResolutionV2, ExperimentResolutionV2,
    ImplementationBundleResolutionV2, ImplementationRuntimeV2, ModelLinkReceiptV1,
};
use fern_run::experiment_v2::{
    AblationArmV2, CompiledAblationV2, CompiledExperimentV2, experiment_resolution_for_ablation_arm_v2,
    lower_experiment_v2,
};
use fern_run::resolved_cfg::{ExecutionContract, ResolvedConfigSpec};

const REMOTE_PROBE_MAX_OUTPUT_BYTES: usize = 4 * 1024 * 1024;
const REMOTE_PROBE_TIMEOUT: Duration = Duration::from_secs(200);

#[derive(Debug, Args)]
pub struct PlanExperiment {
    /// Checked fern.experiment-resolution.v2 JSON from resolve-experiment.
    #[arg(long = "from")]
    source: PathBuf,
    /// Declared local execution profile whose Python and device will execute it.
    #[arg(long)]
    on: String,
    /// Checked Implementation Binding. The sole source authority for new V2
    /// objects: its Implementation names one SourceContent, whose CAS bytes
    /// supply the production model-link code and, later, the Stage bundle.
    /// V1 snapshots stay readable but can no longer plan.
    #[arg(long)]
    implementation_binding: String,
    /// Registry Home that owns the Binding.
    #[arg(long, default_value = "results/paper_campaign/lifecycle.db")]
    registry: PathBuf,
    /// Owner-approved reason for allowing this one source materialization
    /// below the deployment storage floor. Other commands remain blocked.
    #[arg(long = "owner-storage-override-reason", value_name = "REASON")]
    owner_storage_override_reason: Option<String>,
    /// Print the lowered worker configs (`fern.cfg.v2`, one per cell) to
    /// stdout before the model-link probe runs.
    ///
    /// A dump for reading, never a source authority: the probe still runs and
    /// a refusal still exits non-zero, so this shows the configuration the
    /// probe is about to reject rather than replacing the check.
    #[arg(long)]
    print_lowered_config: bool,
    /// Atomically write the only executable V2 object here. Without --out the
    /// checked object is printed and nothing is persisted.
    #[arg(long)]
    out: Option<PathBuf>,
}

#[derive(Debug, Args)]
pub struct RunExperiment {
    /// Frozen fern.compiled-experiment.v2 written by plan-experiment.
    #[arg(long = "from")]
    source: PathBuf,
    /// Stable human Plan name recorded in Registry Home.
    #[arg(long)]
    plan: String,
    /// Compatibility lane projection for this one V2 Matrix.
    #[arg(long)]
    lane: String,
    /// The exact profile already bound into the compiled object.
    #[arg(long)]
    on: String,
    /// Retain tensors required to commit trained forecast evidence.
    #[arg(long)]
    keep_forecast_origins: bool,
    /// Exact upstream Matrix identity; repeat for multiple prerequisites.
    #[arg(long = "after-matrix")]
    after_matrices: Vec<String>,
    /// Owner-approved reason for allowing this one source materialization
    /// below the deployment storage floor. Scheduler and worker gates remain.
    #[arg(long = "owner-storage-override-reason", value_name = "REASON")]
    owner_storage_override_reason: Option<String>,
}

#[derive(Debug, Args)]
pub struct PlanAblation {
    /// Checked fern.ablation-resolution.v2 JSON from resolve-ablation.
    #[arg(long = "from")]
    source: PathBuf,
    /// Declared local execution profile shared by both arms.
    #[arg(long)]
    on: String,
    /// Checked Implementation Binding shared by both arms. One Binding is what
    /// makes "control and treatment ran the same code" structural rather than
    /// a rule the two arms are separately checked against.
    #[arg(long)]
    implementation_binding: String,
    /// Registry Home that owns the Binding.
    #[arg(long, default_value = "results/paper_campaign/lifecycle.db")]
    registry: PathBuf,
    /// Print the lowered worker configs (`fern.cfg.v2`, one per cell) to
    /// stdout before the model-link probe runs.
    ///
    /// A dump for reading, never a source authority: the probe still runs and
    /// a refusal still exits non-zero, so this shows the configuration the
    /// probe is about to reject rather than replacing the check.
    #[arg(long)]
    print_lowered_config: bool,
    /// Atomically write the one executable paired object here. Without --out
    /// the checked object is printed and nothing is persisted.
    #[arg(long)]
    out: Option<PathBuf>,
}

#[derive(Debug, Args)]
pub struct RunAblation {
    /// Frozen fern.compiled-ablation.v2 written by plan-ablation.
    #[arg(long = "from")]
    source: PathBuf,
    /// Stable human Plan name recorded in Registry Home.
    #[arg(long)]
    plan: String,
    /// Historical Matrix lane name. Stage-driven paired ablation ignores it.
    #[arg(long, default_value = "")]
    control_lane: String,
    /// Historical Matrix lane name. Stage-driven paired ablation ignores it.
    #[arg(long, default_value = "")]
    treatment_lane: String,
    /// The exact profile already bound into both compiled arms.
    #[arg(long)]
    on: String,
    /// Retain tensors required to commit trained forecast evidence.
    #[arg(long)]
    keep_forecast_origins: bool,
    /// Exact upstream Matrix identity; repeat for multiple prerequisites.
    #[arg(long = "after-matrix")]
    after_matrices: Vec<String>,
}

/// Both source identities a compiled object must carry, or a refusal.
///
/// One without the other is a compiled object written by a planner that did not
/// finish the job, so the answer is "recompile" rather than a guess about which
/// half to believe.
#[cfg(fern_role_operator)]
fn compiled_source_identities<'a>(
    compiled_implementation_id: Option<&'a str>,
    implementation_binding_id: Option<&'a str>,
) -> Fallible<(&'a str, &'a str)> {
    match (compiled_implementation_id, implementation_binding_id) {
        (Some(implementation), Some(binding)) => Ok((implementation, binding)),
        _ => Err(invalid_input(
            "compiled object names no Implementation Binding; recompile it with plan-experiment so the Plan can prove which source it runs",
        )),
    }
}

/// Require the compiled object and the Registry to name one implementation.
///
/// Resolving only the Binding would let a forged compiled object name one
/// implementation and silently execute another: the mismatch would be discarded
/// rather than refused, and the Plan would record what the Binding resolved to
/// while every reader of the compiled bytes believed the other one.
#[cfg(fern_role_operator)]
fn require_compiled_implementation_matches_binding(
    compiled_implementation_id: &str,
    binding_id: &str,
    resolved_implementation_id: &str,
) -> Fallible<()> {
    if resolved_implementation_id != compiled_implementation_id {
        return Err(invalid_input(format!(
            "compiled object names implementation {compiled_implementation_id}, but its binding {binding_id} resolves to {resolved_implementation_id}; the compiled object and Registry Home disagree about which source this Plan runs"
        )));
    }
    Ok(())
}

/// Require an authored Design reference to be the one the Binding records.
///
/// A Binding exists to say which Research Design a body of code was approved
/// for. If the resolution names a Design too and they differ, the compiled
/// object would carry one answer and the Registry another, with nothing
/// deciding between them.
///
/// `design_ref = None` stays legal on purpose: a scratch experiment should not
/// have to open a Card before it can be run once.
fn require_design_ref_matches_binding(
    design_ref: Option<&str>,
    binding: &fern_run::source_content::ImplementationBindingV2,
) -> Fallible<()> {
    let Some(design_ref) = design_ref else {
        return Ok(());
    };
    if design_ref != binding.research_design_id {
        return Err(invalid_input(format!(
            "resolution names Research Design {design_ref}, but binding {} was recorded against {}; \
             bind the implementation to that Design or drop design_ref for a scratch experiment",
            binding.binding_id, binding.research_design_id
        )));
    }
    Ok(())
}

fn lower_first_compiled_cell_with_csharp(
    compiled: &CompiledExperimentV2,
    template: &StagePipelineTemplateV1,
    python: &StageImplementationRefV1,
    training_worker_request_json: &str,
) -> Fallible<fern_run::csharp_experiment::LoweredCell> {
    if compiled.cells().is_empty() {
        return Err(invalid_input("compiled Experiment has no cells"));
    }
    let request = fern_run::csharp_experiment::LowerCellBatchRequest {
        schema: "fern.lower_cell_batch.request.v1".to_string(),
        compiled: fern_run::csharp_experiment::compiled_experiment_projection(compiled).map_err(invalid_input)?,
        template_json: fern_run::csharp_experiment::template_json(template).map_err(invalid_input)?,
        python: python_projection(python),
        requests: vec![fern_run::csharp_experiment::CellRequestProjection {
            cell_ordinal: 0,
            training_worker_request_json: training_worker_request_json.to_string(),
        }],
    };
    let mut receipt = fern_run::csharp_experiment::lower_cells(&request).map_err(invalid_input)?;
    if receipt.schema != "fern.lower_cell_batch.receipt.v1"
        || receipt.lowered.len() != 1
        || receipt.lowered[0].cell_ordinal != 0
    {
        return Err(invalid_input("C# lowering returned the wrong one-cell receipt"));
    }
    Ok(receipt.lowered.remove(0))
}

fn python_projection(python: &StageImplementationRefV1) -> fern_run::csharp_experiment::PythonImplementationProjection {
    fern_run::csharp_experiment::PythonImplementationProjection {
        runner: "python_module".to_string(),
        entrypoint: python.entrypoint.clone(),
        implementation_digest: python.implementation_digest.as_str().to_string(),
    }
}

fn hashed_python_source(source_root: &Path) -> Fallible<StageImplementationRefV1> {
    if !source_root.join("fr_tr.py").is_file() {
        return Err(invalid_input(format!(
            "python source root {} has no fr_tr.py",
            source_root.display()
        )));
    }
    let mut files = Vec::new();
    collect_python_source_files(source_root, source_root, &mut files)?;
    let artifact =
        fern_artifact::stage_python_bundle_digest(&files).map_err(|error| invalid_input(error.to_string()))?;
    Ok(StageImplementationRefV1 {
        runner: StageRunnerV1::PythonModule,
        entrypoint: "fr_tr".to_string(),
        implementation_digest: fern_artifact::stage_implementation_digest(
            StageRunnerV1::PythonModule,
            "fr_tr",
            &[
                "serie.materialize.cell",
                "fern.train.current",
                "fern.postprocess",
                "oracle.controlled_linear.generate",
            ],
            &artifact,
        ),
    })
}

fn collect_python_source_files(
    source_root: &Path,
    directory: &Path,
    files: &mut Vec<(String, Sha256Digest)>,
) -> Fallible<()> {
    for entry in std::fs::read_dir(directory)? {
        let entry = entry?;
        let path = entry.path();
        let metadata = std::fs::symlink_metadata(&path)?;
        if metadata.file_type().is_symlink() {
            return Err(invalid_input(format!("{} must not be a symlink", path.display())));
        }
        if metadata.is_dir() {
            if path
                .file_name()
                .is_some_and(|name| name == "__pycache__" || name == "tests")
            {
                continue;
            }
            collect_python_source_files(source_root, &path, files)?;
            continue;
        }
        if !metadata.is_file() {
            continue;
        }
        let keep = path
            .extension()
            .and_then(|ext| ext.to_str())
            .is_some_and(|ext| ext == "py" || ext == "pyi" || ext == "lock");
        if !keep {
            continue;
        }
        let relative = path
            .strip_prefix(source_root)
            .map_err(|_| invalid_input("python source path escaped the materialized root"))?
            .to_string_lossy()
            .replace('\\', "/");
        files.push((relative, Sha256Digest::from_bytes(&std::fs::read(&path)?)));
    }
    Ok(())
}

#[cfg(fern_role_operator)]
fn stage_pipeline_requires_cuda(template: &StagePipelineTemplateV1) -> bool {
    template
        .stages
        .iter()
        .any(|stage| stage.handler == "fern.train.current")
}

fn require_draw_protocol(
    field: &str,
    target_idx: u64,
    draw: fern_run::experiment_v2::ExperimentDrawV2,
) -> Fallible<()> {
    let ordinal = usize::try_from(draw.ordinal).map_err(|_| {
        invalid_input(format!(
            "V2 target {target_idx} {field}.ordinal does not fit this platform"
        ))
    })?;
    DrawRef::try_from_parts(draw.seed_base, ordinal, draw.seed).map_err(|error| {
        invalid_input(format!(
            "V2 target {target_idx} {field} violates the Serie draw protocol: {error}"
        ))
    })?;
    Ok(())
}

pub(crate) fn resolve_model_link_profile(profile_name: &str) -> Fallible<ResolvedExecutionProfile> {
    let relative_profile_path = ExecutionProfile::default_path(profile_name).map_err(|issue| {
        invalid_input(format!(
            "invalid execution profile name {profile_name:?}: {}",
            issue.explanation
        ))
    })?;
    let workspace_root = crate::run_sweep::reviewed_source_root().map_err(|error| {
        invalid_input(format!(
            "cannot identify the reviewed Fern workspace for execution profile {profile_name:?}: {error}"
        ))
    })?;
    let profile_path = workspace_root.join(relative_profile_path);
    let profile = ExecutionProfile::read(&profile_path).map_err(invalid_input)?;
    let profile = profile
        .resolve()
        .map_err(|error| invalid_input(format!("execution profile {profile_name:?} is invalid: {error:?}")))?;
    Ok(profile)
}

fn run_bounded_remote_probe(mut command: Command) -> Fallible<Vec<u8>> {
    let mut child = command
        .spawn()
        .map_err(|error| invalid_input(format!("cannot start maintained remote Python probe: {error}")))?;
    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| invalid_input("remote Python probe stdout pipe is unavailable"))?;
    let stderr = child
        .stderr
        .take()
        .ok_or_else(|| invalid_input("remote Python probe stderr pipe is unavailable"))?;
    let stdout_reader = thread::spawn(move || read_bounded_probe_stream(stdout));
    let stderr_reader = thread::spawn(move || read_bounded_probe_stream(stderr));
    let started = Instant::now();
    let status = loop {
        if let Some(status) = child
            .try_wait()
            .map_err(|error| invalid_input(format!("cannot poll maintained remote Python probe: {error}")))?
        {
            break status;
        }
        if started.elapsed() >= REMOTE_PROBE_TIMEOUT {
            let _ = child.kill();
            let _ = child.wait();
            return Err(invalid_input(format!(
                "maintained remote Python probe exceeded {} seconds",
                REMOTE_PROBE_TIMEOUT.as_secs()
            )));
        }
        thread::sleep(Duration::from_millis(20));
    };
    let stdout = join_bounded_probe_reader(stdout_reader, "stdout")?;
    let stderr = join_bounded_probe_reader(stderr_reader, "stderr")?;
    if !status.success() {
        return Err(invalid_input(format!(
            "maintained remote Python probe returned {status}: {}",
            String::from_utf8_lossy(&stderr).trim()
        )));
    }
    Ok(stdout)
}

fn read_bounded_probe_stream(mut stream: impl Read) -> std::io::Result<Vec<u8>> {
    let mut bytes = Vec::new();
    stream
        .by_ref()
        .take((REMOTE_PROBE_MAX_OUTPUT_BYTES + 1) as u64)
        .read_to_end(&mut bytes)?;
    Ok(bytes)
}

fn join_bounded_probe_reader(reader: thread::JoinHandle<std::io::Result<Vec<u8>>>, name: &str) -> Fallible<Vec<u8>> {
    let bytes = reader
        .join()
        .map_err(|_| invalid_input(format!("remote Python probe {name} reader panicked")))?
        .map_err(|error| invalid_input(format!("cannot read remote Python probe {name}: {error}")))?;
    if bytes.len() > REMOTE_PROBE_MAX_OUTPUT_BYTES {
        return Err(invalid_input(format!(
            "remote Python probe {name} exceeds the {} byte limit",
            REMOTE_PROBE_MAX_OUTPUT_BYTES
        )));
    }
    Ok(bytes)
}

// CHG 2026-08-28: plan-ablation and run-ablation came BACK. The seventh cut retired
// them with the experiment pair because the C# host lists both verbs, but Grok's
// correction the same night: the host has no public compile or register command
// for an Ablation, so Rust still owns them. The items below are the retired copies
// restored verbatim; their original positions are named in each header.

// restored from experiment_v2.rs:34-34 (constant REMOTE_HELPER_TIMEOUT_SECONDS)
const REMOTE_HELPER_TIMEOUT_SECONDS: &str = "180";

// restored from experiment_v2.rs:1481-1511 (function bound_stage_python_source)
/// Resolve the Binding a compiled object names to the one materialized source.
///
/// Both V2 run routes need the identical four steps, and writing them twice is
/// how one route ends up checking something the other does not.
///
/// Neither the Binding nor the Registry Home is a flag here. The Binding is
/// read from the compiled bytes, so a run cannot execute source its own plan
/// never approved; the Registry Home is the profile's, so the Binding cannot be
/// read from one database and registered against another. The authoring
/// commands do take `--registry`, because they only read.
#[cfg(fern_role_operator)]
fn bound_stage_python_source(
    profile: &ResolvedExecutionProfile,
    compiled_implementation_id: Option<&str>,
    implementation_binding_id: Option<&str>,
    implementation_bundle: &ImplementationBundleResolutionV2,
    owner_storage_override_reason: Option<&str>,
) -> Fallible<(BoundImplementationSource, Sha256Digest)> {
    let registry_home = crate::run_sweep::resolve_registry_home_profile(profile)?;
    let registry = registry_home.declaration.campaign_root.join("lifecycle.db");
    let (compiled_implementation_id, binding_id) =
        compiled_source_identities(compiled_implementation_id, implementation_binding_id)?;
    let bound = materialize_bound_implementation_source(binding_id, &registry, owner_storage_override_reason)?;
    require_compiled_implementation_matches_binding(
        compiled_implementation_id,
        binding_id,
        &bound.implementation.implementation_id,
    )?;
    let (_, digest) = stage_python_source_identity(&bound, implementation_bundle)?;
    Ok((bound, digest))
}

// restored from experiment_v2.rs:1237-1267 (function checked_stage_input)
#[cfg(fern_role_operator)]
fn checked_stage_input(
    input_key: &str,
    path: &Path,
    declared: &fern_run::csharp_experiment::DeclaredStageInputArtifact,
) -> Fallible<fern_run::stage_attempt::StagePlanInstanceInput> {
    let metadata = std::fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink() || !metadata.file_type().is_file() {
        return Err(invalid_input(format!(
            "Stage input {input_key} must be a regular non-symlink file"
        )));
    }
    let bytes = std::fs::read(path)?;
    let observed = fern_experiment_types::Sha256Digest::from_bytes(&bytes);
    if observed != declared.content_digest {
        return Err(invalid_input(format!(
            "Stage input {input_key} bytes disagree with the declared digest"
        )));
    }
    Ok(fern_run::stage_attempt::StagePlanInstanceInput {
        input_key: input_key.to_string(),
        path: path
            .to_str()
            .ok_or_else(|| invalid_input(format!("Stage input {input_key} path must be utf-8")))?
            .to_string(),
        sha256: observed.as_str().to_string(),
        byte_count: i64::try_from(bytes.len()).unwrap_or(i64::MAX),
        role: declared.role.clone(),
        schema_id: declared.schema_id.clone(),
    })
}

// restored from experiment_v2.rs:1289-1330 (function compile_experiment_resolution)
/// Compile against one resolved Binding.
///
/// Taking the whole `BoundImplementationSource` rather than a bare path is the
/// point: the bytes the probe reads and the identity the compiled object
/// records now come from one object, so they cannot describe different sources.
fn compile_experiment_resolution(
    profile: &ResolvedExecutionProfile,
    bound: &BoundImplementationSource,
    resolution: ExperimentResolutionV2,
    print_lowered_config: bool,
) -> Fallible<CompiledExperimentV2> {
    let source_root = bound.materialized_source_root.as_path();
    let lowered = lower_experiment_v2(
        resolution.clone(),
        profile.declaration.profile_id.clone(),
        profile.revision.revision_hash.clone(),
        profile.declaration.device,
    )
    .map_err(invalid_input)?;
    if print_lowered_config {
        write_lowered_worker_configs(lowered.cells(), &mut std::io::stdout().lock())?;
    }
    let channels = lowered.channels();
    let action_dim = lowered.action_dim();
    let model_link_receipts = probe_model_link_receipts(
        profile,
        source_root,
        &resolution.implementation_bundle,
        lowered.model_link_configs().map_err(invalid_input)?,
        channels,
        action_dim,
    )?;
    let compiled = lowered
        .finish(
            model_link_receipts,
            bound.implementation.implementation_id.clone(),
            bound.binding.binding_id.clone(),
        )
        .map_err(invalid_input)?;
    require_compiled_draw_protocol(&compiled)?;
    Ok(compiled)
}

// restored from experiment_v2.rs:1392-1479 (function materialize_bound_implementation_source)
/// Walk Binding -> Implementation -> SourceContent -> CAS bytes and publish a
/// verified source root.
///
/// Every hop refuses rather than defaults. A binding that names an
/// implementation the Registry does not hold is a broken chain, not a reason to
/// fall back to the workspace.
pub(crate) fn materialize_bound_implementation_source(
    binding_id: &str,
    registry: &Path,
    owner_storage_override_reason: Option<&str>,
) -> Fallible<BoundImplementationSource> {
    // Check the shape before opening a database, so a typo reads as a typo.
    let hex = binding_id
        .strip_prefix("implementation-binding:")
        .ok_or_else(|| invalid_input(format!("{binding_id:?} is not an implementation-binding identity")))?;
    if hex.len() != 64
        || !hex
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return Err(invalid_input(format!(
            "implementation binding {binding_id:?} is not 64 lowercase hex characters"
        )));
    }
    let workspace_root = crate::run_sweep::reviewed_source_root()?;
    let registry = if registry.is_absolute() {
        registry.to_path_buf()
    } else {
        workspace_root.join(registry)
    };
    let binding: fern_run::source_content::ImplementationBindingV2 =
        crate::implementation_snapshot::load_json_object("load-implementation-binding", &registry, binding_id)
            .map_err(invalid_input)?
            .ok_or_else(|| {
                invalid_input(format!(
                    "Registry Home {} has no implementation binding {binding_id}",
                    registry.display()
                ))
            })?;
    let implementation: fern_run::source_content::SourceImplementationV2 =
        crate::implementation_snapshot::load_json_object(
            "load-source-implementation",
            &registry,
            &binding.implementation_id,
        )
        .map_err(invalid_input)?
        .ok_or_else(|| {
            invalid_input(format!(
                "binding {binding_id} names implementation {}, which this Registry Home does not hold",
                binding.implementation_id
            ))
        })?;
    let content_id = implementation
        .sole_component_content_id()
        .map_err(|error| invalid_input(error.to_string()))?
        .to_string();
    let content: fern_run::source_content::SourceContentV2 =
        crate::implementation_snapshot::load_json_object("load-source-content", &registry, &content_id)
            .map_err(invalid_input)?
            .ok_or_else(|| {
                invalid_input(format!(
                    "implementation {} names source content {content_id}, which this Registry Home does not hold",
                    implementation.implementation_id
                ))
            })?;

    let deployment_root = workspace_root.join(crate::paths::DEFAULT_DEPLOYMENT_ROOT);
    let roots_directory = deployment_root.join("source-content-roots");
    let content_hex = content
        .source_content_id
        .strip_prefix("source-content:")
        .ok_or_else(|| invalid_input("source_content_id is missing its prefix"))?;
    // Reopening an existing content-addressed root writes no source bytes. Let
    // the publisher below verify that entry (including symlink refusal) without
    // requiring free space reserved for a publication that will not happen.
    if std::fs::symlink_metadata(roots_directory.join(content_hex)).is_err() {
        crate::binary_deploy::inspect_deploy_storage(&workspace_root, owner_storage_override_reason)?;
    }
    let materialized_source_root =
        fern_run::source_content::publish_materialized_source_root(&deployment_root, &content, &roots_directory)
            .map_err(|error| invalid_input(format!("cannot materialize source content {content_id}: {error}")))?;
    Ok(BoundImplementationSource {
        binding,
        implementation,
        content,
        materialized_source_root,
    })
}

// restored from experiment_v2.rs:1652-1706 (function probe_model_link_receipts)
fn probe_model_link_receipts(
    profile: &ResolvedExecutionProfile,
    source_root: &Path,
    implementation: &ImplementationBundleResolutionV2,
    configs: BTreeMap<String, ResolvedConfigSpec>,
    channels: u64,
    action_dim: Option<u64>,
) -> Fallible<BTreeMap<String, ModelLinkReceiptV1>> {
    let mut receipts = BTreeMap::new();
    for (config_hash, config) in configs {
        let config_value = serde_json::to_value(config)?;
        let receipt = match profile.declaration.executor {
            ExecutionProfileExecutor::Local => fern_experiment_authoring::probe_model_link_v2(
                source_root,
                &profile.declaration.python,
                implementation,
                &config_hash,
                &config_value,
                channels,
                action_dim,
            ),
            ExecutionProfileExecutor::Ssh => {
                let request = fern_experiment_authoring::python_model_link_request(
                    implementation,
                    &config_value,
                    channels,
                    action_dim,
                )?;
                let request = std::str::from_utf8(&request)
                    .map_err(|error| invalid_input(format!("model-link request is not UTF-8: {error}")))?;
                let stdout = run_remote_python_probe(
                    profile,
                    fern_experiment_authoring::python_model_link_probe_source(),
                    &[request],
                )?;
                fern_experiment_authoring::model_link_receipt_from_python_output(
                    implementation,
                    &config_hash,
                    &config_value,
                    channels,
                    action_dim,
                    &stdout,
                    &[],
                )
            }
        }
        .map_err(|error| {
            invalid_input(format!(
                "production model-link failed for config {config_hash}: {error}"
            ))
        })?;
        receipts.insert(config_hash, receipt);
    }
    Ok(receipts)
}

// restored from experiment_v2.rs:466-480 (function read_compiled_ablation)
pub fn read_compiled_ablation(path: &std::path::Path) -> Fallible<CompiledAblationV2> {
    let bytes = std::fs::read(path)
        .map_err(|error| invalid_input(format!("cannot read compiled V2 ablation {}: {error}", path.display())))?;
    let compiled = checked_from_slice::<CompiledAblationV2>(&bytes)
        .map_err(|error| invalid_input(format!("compiled V2 ablation is invalid JSON: {error}")))?;
    compiled.validate().map_err(invalid_input)?;
    require_compiled_draw_protocol(compiled.control())?;
    require_compiled_draw_protocol(compiled.treatment())?;
    fern_experiment_authoring::verify_ablation_resolution_v2(compiled.resolution()).map_err(|error| {
        invalid_input(format!(
            "compiled V2 ablation contains an unauthorized saved pair: {error}"
        ))
    })?;
    Ok(compiled)
}

// restored from experiment_v2.rs:914-1186 (function register_compiled_ablation_stage_plans)
#[cfg(fern_role_operator)]
fn register_compiled_ablation_stage_plans(
    plan_name: String,
    on: String,
    keep_forecast_origins: bool,
    after_matrices: Vec<String>,
    compiled: CompiledAblationV2,
) -> Fallible<()> {
    let template = compiled
        .implementation_bundle()
        .capabilities
        .stage_pipeline_template
        .as_ref()
        .ok_or_else(|| {
            invalid_input(
                "implementation bundle does not report a Stage pipeline template; re-probe the implementation and recompile",
            )
        })?;
    let profile = resolve_model_link_profile(&on)?;
    if profile.declaration.python.as_os_str().is_empty()
        || profile.declaration.work_root.as_os_str().is_empty()
        || profile.declaration.artifact_root.as_os_str().is_empty()
        || profile.declaration.worker_binary.as_os_str().is_empty()
    {
        return Err(invalid_input(
            "execution profile is missing worker launch paths; refusing to leave a half-registered Plan",
        ));
    }
    if profile.declaration.device != fern_run::resolved_cfg::DeviceKind::Cuda
        || compiled.control().runtime_device() != fern_run::resolved_cfg::DeviceKind::Cuda
        || compiled.treatment().runtime_device() != fern_run::resolved_cfg::DeviceKind::Cuda
    {
        return Err(invalid_input(
            "this Stage Plan slice trains on CUDA only; CPU fallback is refused",
        ));
    }
    // Control's Binding stands for both arms: `read_compiled_ablation` validated
    // the pair, and that validation refuses two arms whose Bindings differ.
    let (bound, python_digest) = bound_stage_python_source(
        &profile,
        compiled.control().implementation_id(),
        compiled.control().implementation_binding_id(),
        compiled.implementation_bundle(),
        None,
    )?;
    let python = StageImplementationRefV1 {
        runner: StageRunnerV1::PythonModule,
        entrypoint: "fr_tr".to_string(),
        implementation_digest: python_digest,
    };
    let template_digest = template.digest().map_err(|error| invalid_input(error.to_string()))?;
    let compiled_projection = fern_run::csharp_experiment::compiled_ablation_projection(&compiled, &template_digest)
        .map_err(invalid_input)?;
    let pair_receipt = fern_run::csharp_experiment::pair_ablation(&fern_run::csharp_experiment::PairAblationRequest {
        schema: "fern.pair_compiled_ablation.request.v1".to_string(),
        compiled: compiled_projection.clone(),
    })
    .map_err(invalid_input)?;
    if pair_receipt.schema != "fern.pair_compiled_ablation.receipt.v1" || pair_receipt.units.is_empty() {
        return Err(invalid_input("C# pairing returned no valid compiled Ablation units"));
    }
    Sha256Digest::parse(pair_receipt.resolution_digest.clone())
        .map_err(|error| invalid_input(format!("C# pairing returned an invalid resolution_digest: {error}")))?;
    let control_count = compiled.control().cells().len();
    let treatment_count = compiled.treatment().cells().len();
    if pair_receipt.units.len() != control_count || pair_receipt.units.len() != treatment_count {
        return Err(invalid_input(format!(
            "C# pairing returned {} unit(s) for {control_count} control and {treatment_count} treatment cell(s)",
            pair_receipt.units.len()
        )));
    }
    let mut seen_control_ordinals = BTreeSet::new();
    let mut seen_treatment_ordinals = BTreeSet::new();
    let mut seen_pair_ids = BTreeSet::new();
    for unit in &pair_receipt.units {
        let control_ordinal = usize::try_from(unit.control_cell_ordinal)
            .map_err(|_| invalid_input("C# pairing returned an invalid control cell ordinal"))?;
        let treatment_ordinal = usize::try_from(unit.treatment_cell_ordinal)
            .map_err(|_| invalid_input("C# pairing returned an invalid treatment cell ordinal"))?;
        let control_cell = compiled
            .control()
            .cells()
            .get(control_ordinal)
            .ok_or_else(|| invalid_input("C# pairing named a foreign control cell"))?;
        let treatment_cell = compiled
            .treatment()
            .cells()
            .get(treatment_ordinal)
            .ok_or_else(|| invalid_input("C# pairing named a foreign treatment cell"))?;
        if !seen_control_ordinals.insert(control_ordinal)
            || !seen_treatment_ordinals.insert(treatment_ordinal)
            || !seen_pair_ids.insert(unit.paired_unit_id.as_str())
        {
            return Err(invalid_input(
                "C# pairing returned duplicate cell membership or paired_unit_id",
            ));
        }
        let control_coordinates = serde_json::to_value(&control_cell.coordinates)?;
        let treatment_coordinates = serde_json::to_value(&treatment_cell.coordinates)?;
        if unit.coordinates != control_coordinates || unit.coordinates != treatment_coordinates {
            return Err(invalid_input(
                "C# pairing coordinates do not match both submitted compiled cells",
            ));
        }
        Sha256Digest::parse(unit.paired_unit_id.clone())
            .map_err(|error| invalid_input(format!("C# pairing returned an invalid paired_unit_id: {error}")))?;
        Sha256Digest::parse(unit.target_hash.clone())
            .map_err(|error| invalid_input(format!("C# pairing returned an invalid target_hash: {error}")))?;
        Sha256Digest::parse(unit.config_hash.clone())
            .map_err(|error| invalid_input(format!("C# pairing returned an invalid config_hash: {error}")))?;
    }
    let mut prepared = Vec::with_capacity(pair_receipt.units.len());
    for (ordinal, unit) in pair_receipt.units.into_iter().enumerate() {
        let cell_ordinal = i64::try_from(ordinal).map_err(|_| invalid_input("pair ordinal does not fit"))?;
        let control_ordinal = usize::try_from(unit.control_cell_ordinal)
            .map_err(|_| invalid_input("control cell ordinal does not fit"))?;
        let treatment_ordinal = usize::try_from(unit.treatment_cell_ordinal)
            .map_err(|_| invalid_input("treatment cell ordinal does not fit"))?;
        let control_cell = compiled
            .control()
            .cells()
            .get(control_ordinal)
            .ok_or_else(|| invalid_input("C# pairing named a foreign control cell"))?;
        let treatment_cell = compiled
            .treatment()
            .cells()
            .get(treatment_ordinal)
            .ok_or_else(|| invalid_input("C# pairing named a foreign treatment cell"))?;
        let control_summary = crate::plan_recipe_run::plan_compiled_experiment_cell(
            compiled.control(),
            control_cell,
            0,
            &profile.declaration.artifact_root,
            keep_forecast_origins,
            None,
        )?;
        let treatment_summary = crate::plan_recipe_run::plan_compiled_experiment_cell(
            compiled.treatment(),
            treatment_cell,
            0,
            &profile.declaration.artifact_root,
            keep_forecast_origins,
            None,
        )?;
        let control_path = write_checked_training_worker_request(
            &profile,
            &plan_name,
            &format!("pair-{ordinal}/control"),
            control_summary.request,
        )?;
        let treatment_path = write_checked_training_worker_request(
            &profile,
            &plan_name,
            &format!("pair-{ordinal}/treatment"),
            treatment_summary.request,
        )?;
        let control_bytes = std::fs::read(&control_path.0)?;
        let treatment_bytes = std::fs::read(&treatment_path.0)?;
        prepared.push((
            cell_ordinal,
            unit,
            control_path,
            treatment_path,
            String::from_utf8(control_bytes).map_err(|error| invalid_input(error.to_string()))?,
            String::from_utf8(treatment_bytes).map_err(|error| invalid_input(error.to_string()))?,
        ));
    }
    let lower_receipt = fern_run::csharp_experiment::lower_pairs(&fern_run::csharp_experiment::LowerPairBatchRequest {
        schema: "fern.lower_pair_batch.request.v1".to_string(),
        compiled: compiled_projection,
        template_json: fern_run::csharp_experiment::template_json(template).map_err(invalid_input)?,
        python: python_projection(&python),
        requests: prepared
            .iter()
            .map(|item| fern_run::csharp_experiment::PairRequestProjection {
                paired_unit_id: item.1.paired_unit_id.clone(),
                control_training_worker_request_json: item.4.clone(),
                treatment_training_worker_request_json: item.5.clone(),
            })
            .collect(),
    })
    .map_err(invalid_input)?;
    if lower_receipt.schema != "fern.lower_pair_batch.receipt.v1" || lower_receipt.lowered.len() != prepared.len() {
        return Err(invalid_input("C# lowering returned the wrong paired-unit cardinality"));
    }

    let control_key = "control.training_worker_request";
    let treatment_key = "treatment.training_worker_request";
    let control_bank_key = "control.dgp_bank";
    let treatment_bank_key = "treatment.dgp_bank";
    let mut instances = Vec::with_capacity(prepared.len());
    let mut records = Vec::with_capacity(prepared.len());
    for ((cell_ordinal, unit, control_path, treatment_path, _, _), lowered) in
        prepared.into_iter().zip(lower_receipt.lowered)
    {
        if lowered.cell_ordinal != cell_ordinal
            || lowered.paired_unit_id != unit.paired_unit_id
            || lowered.target_hash != unit.target_hash
            || lowered.config_hash != unit.config_hash
            || lowered.subject.kind != "fern.compiled_ablation_paired_unit"
            || lowered.subject.identity != unit.paired_unit_id
        {
            return Err(invalid_input("C# lowering changed paired-unit order or identity"));
        }
        let control_digest = lowered
            .inputs
            .get(control_key)
            .ok_or_else(|| invalid_input("paired unit is missing control.training_worker_request"))?;
        let treatment_digest = lowered
            .inputs
            .get(treatment_key)
            .ok_or_else(|| invalid_input("paired unit is missing treatment.training_worker_request"))?;
        let control_bank = lowered
            .inputs
            .get(control_bank_key)
            .ok_or_else(|| invalid_input("paired unit is missing control.dgp_bank"))?;
        let treatment_bank = lowered
            .inputs
            .get(treatment_bank_key)
            .ok_or_else(|| invalid_input("paired unit is missing treatment.dgp_bank"))?;
        if control_path.1 != control_digest.content_digest {
            return Err(invalid_input(
                "control training_worker_request digest does not match the Stage Plan declared input",
            ));
        }
        if treatment_path.1 != treatment_digest.content_digest {
            return Err(invalid_input(
                "treatment training_worker_request digest does not match the Stage Plan declared input",
            ));
        }
        let declaration = fern_run::stage_attempt::StagePlanInstanceDeclaration {
            cell_ordinal,
            compiled_cell_subject_digest: lowered.subject.content_digest.as_str().to_string(),
            target_hash: lowered.target_hash,
            config_hash: lowered.config_hash,
            stage_plan: lowered.stage_plan,
            storage_site_id: on.clone(),
            paired_unit_id: unit.paired_unit_id,
            inputs: vec![
                checked_stage_input(control_key, &control_path.0, control_digest)?,
                checked_stage_input(control_bank_key, &control_path.2, control_bank)?,
                checked_stage_input(treatment_key, &treatment_path.0, treatment_digest)?,
                checked_stage_input(treatment_bank_key, &treatment_path.2, treatment_bank)?,
            ],
        };
        records.push(
            fern_run::stage_attempt::declared_instance_record(&declaration)
                .map_err(|error| invalid_input(error.to_string()))?,
        );
        instances.push(declaration);
    }
    let set_digest =
        fern_run::stage_attempt::stage_plan_set_digest(&records).map_err(|error| invalid_input(error.to_string()))?;
    let definition = fern_run::run_plan::RunPlanDefinitionV1 {
        schema: fern_run::run_plan::RUN_PLAN_SCHEMA_V2.to_string(),
        plan_name,
        implementation_source_snapshot_id: None,
        compiled_ablation: Some(compiled),
        compiled_experiment: None,
        stage_plan_set_digest: Some(set_digest),
        stage_plan_digest: None,
        training_worker_request_digest: None,
        implementation_id: Some(bound.implementation.implementation_id.clone()),
        implementation_binding_id: Some(bound.binding.binding_id.clone()),
        owner_storage_override_reason: None,
        dependencies: after_matrices
            .into_iter()
            .map(|matrix_id| fern_run::run_plan::RunPlanMatrixDependencyV1 { matrix_id })
            .collect(),
        steps: Vec::new(),
    };
    crate::run_sweep::register_stage_driven_run_plan_definition(definition, on, instances)
}

// restored from experiment_v2.rs:1522-1556 (function reprobe_implementation_for_profile)
fn reprobe_implementation_for_profile(
    profile: &ResolvedExecutionProfile,
    local_source_root: &Path,
    implementation: &ImplementationBundleResolutionV2,
) -> Fallible<()> {
    match profile.declaration.executor {
        ExecutionProfileExecutor::Local => fern_experiment_authoring::reprobe_implementation_bundle_v2(
            local_source_root,
            &profile.declaration.python,
            implementation,
        )
        .map_err(|error| invalid_input(format!("installed V2 implementation does not match: {error}"))),
        ExecutionProfileExecutor::Ssh => {
            let ImplementationRuntimeV2::PythonModule { module } = &implementation.runtime;
            let discovery = run_remote_python_probe(
                profile,
                fern_experiment_authoring::python_capability_probe_source(),
                &[module.as_str()],
            )?;
            let observation = run_remote_python_probe(
                profile,
                fern_experiment_authoring::python_capability_probe_source(),
                &[module.as_str()],
            )?;
            fern_experiment_authoring::reprobe_implementation_bundle_v2_from_python_outputs(
                local_source_root,
                &profile.declaration.source_root,
                implementation,
                &discovery,
                &observation,
            )
            .map_err(|error| invalid_input(format!("installed remote V2 implementation does not match: {error}")))
        }
    }
}

// restored from experiment_v2.rs:1332-1338 (function require_compiled_draw_protocol)
fn require_compiled_draw_protocol(compiled: &CompiledExperimentV2) -> Fallible<()> {
    for cell in compiled.cells() {
        require_draw_protocol("trajectory_draw", cell.target_idx, cell.trajectory_draw)?;
        require_draw_protocol("noise_draw", cell.target_idx, cell.noise_draw)?;
    }
    Ok(())
}

// restored from experiment_v2.rs:1558-1584 (function run_remote_python_probe)
fn run_remote_python_probe(profile: &ResolvedExecutionProfile, source: &str, arguments: &[&str]) -> Fallible<Vec<u8>> {
    let destination = profile
        .declaration
        .ssh_destination
        .as_deref()
        .ok_or_else(|| invalid_input("SSH execution profile has no ssh_destination"))?;
    let helper = crate::run_sweep::remote_ssh_helper_path()?;
    let source_root = profile.declaration.source_root.to_string_lossy().into_owned();
    let mut command = Command::new(helper);
    command
        .env("RUNPOD_SSH_TARGET", destination)
        .env("RUNPOD_COMMAND_TIMEOUT_SECONDS", REMOTE_HELPER_TIMEOUT_SECONDS)
        .arg("exec")
        .arg("--")
        .arg("env")
        .arg("PYTHONDONTWRITEBYTECODE=1")
        .arg(format!("FERN_PROBE_SOURCE_ROOT={source_root}"))
        .arg(format!("PYTHONPATH={source_root}:{source_root}/python"))
        .arg(&profile.declaration.python)
        .arg("-c")
        .arg(source)
        .args(arguments)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    run_bounded_remote_probe(command)
}

// restored from experiment_v2.rs:1513-1520 (function stage_python_source_identity)
pub(crate) fn stage_python_source_identity(
    bound: &BoundImplementationSource,
    implementation_bundle: &ImplementationBundleResolutionV2,
) -> Fallible<(Vec<String>, Sha256Digest)> {
    let files = crate::stage_plan_bundle::declared_python_stage_files(&bound.content, implementation_bundle)?;
    let digest = crate::stage_plan_bundle::declared_python_implementation_digest(&bound.content, &files)?;
    Ok((files, digest))
}

// restored from experiment_v2.rs:401-464 (function try_plan_ablation)
pub fn try_plan_ablation(args: PlanAblation) -> Fallible<()> {
    let bytes = std::fs::read(&args.source).map_err(|error| {
        invalid_input(format!(
            "cannot read V2 ablation resolution {}: {error}",
            args.source.display()
        ))
    })?;
    let resolution = checked_from_slice::<AblationResolutionV2>(&bytes)
        .map_err(|error| invalid_input(format!("V2 ablation resolution is invalid: {error}")))?;
    fern_experiment_authoring::verify_ablation_resolution_v2(&resolution)
        .map_err(|error| invalid_input(format!("V2 ablation resolution failed semantic verification: {error}")))?;
    let profile = resolve_model_link_profile(&args.on)?;
    let bound = materialize_bound_implementation_source(&args.implementation_binding, &args.registry, None)?;
    require_design_ref_matches_binding(resolution.design_ref.as_deref(), &bound.binding)?;
    bound
        .content
        .require_implementation_coverage(
            resolution
                .implementation_bundle
                .source_files
                .iter()
                .chain(&resolution.implementation_bundle.dependency_lock_files),
        )
        .map_err(|error| {
            invalid_input(format!(
                "binding source content does not cover the implementation: {error}"
            ))
        })?;
    reprobe_implementation_for_profile(
        &profile,
        &bound.materialized_source_root,
        &resolution.implementation_bundle,
    )
    .map_err(|error| invalid_input(format!("installed V2 implementation does not match: {error}")))?;
    // Both arms compile against ONE bound source, so sharing an implementation
    // is structural rather than a rule the arms are separately checked against.
    let control = compile_experiment_resolution(
        &profile,
        &bound,
        experiment_resolution_for_ablation_arm_v2(&resolution, AblationArmV2::Control).map_err(invalid_input)?,
        args.print_lowered_config,
    )?;
    let treatment = compile_experiment_resolution(
        &profile,
        &bound,
        experiment_resolution_for_ablation_arm_v2(&resolution, AblationArmV2::Treatment).map_err(invalid_input)?,
        args.print_lowered_config,
    )?;
    let compiled = CompiledAblationV2::new(resolution, control, treatment).map_err(invalid_input)?;
    match args.out {
        Some(path) => {
            write_json_file(&path, &compiled)?;
            println!(
                "compiled control={} and treatment={} cells on {} -> {}",
                compiled.control().cells().len(),
                compiled.treatment().cells().len(),
                compiled.execution_profile_id(),
                path.display()
            );
        }
        None => println!("{}", compiled.checked_pretty_json()?),
    }
    Ok(())
}

// restored from experiment_v2.rs:891-912 (function try_run_ablation)
#[cfg(fern_role_operator)]
pub fn try_run_ablation(args: RunAblation) -> Fallible<()> {
    let compiled = read_compiled_ablation(&args.source)?;
    if compiled.execution_profile_id() != args.on {
        return Err(invalid_input(format!(
            "compiled Ablation targets profile {:?}, but run-ablation was given {:?}; compile again for the intended profile",
            compiled.execution_profile_id(),
            args.on,
        )));
    }
    if args.plan.trim().is_empty() {
        return Err(invalid_input("--plan must be non-empty"));
    }
    let _ = (args.control_lane, args.treatment_lane);
    register_compiled_ablation_stage_plans(
        args.plan,
        args.on,
        args.keep_forecast_origins,
        args.after_matrices,
        compiled,
    )
}

// restored from experiment_v2.rs:1188-1235 (function write_checked_training_worker_request)
#[cfg(fern_role_operator)]
fn write_checked_training_worker_request(
    profile: &ResolvedExecutionProfile,
    plan_name: &str,
    leaf: &str,
    mut request: serde_json::Value,
) -> Fallible<(PathBuf, fern_experiment_types::Sha256Digest, PathBuf)> {
    let python = profile
        .declaration
        .python
        .to_str()
        .ok_or_else(|| invalid_input("execution profile Python path is not valid UTF-8"))?;
    let bank_path = crate::cli_pass2_parent::ensure_request_dgp_bank_with_python(
        &mut request,
        &profile.declaration.work_root,
        Some(python),
    )?
    .ok_or_else(|| invalid_input("materialized-cell Stage v1 requires a Serie bank artifact"))?;
    let request_bytes =
        fern_json_contract::canonical_json_bytes(&request).map_err(|error| invalid_input(error.to_string()))?;
    let observed = fern_experiment_types::Sha256Digest::from_bytes(&request_bytes);
    let digest_hex = observed.as_str().strip_prefix("sha256:").unwrap_or(observed.as_str());
    let request_path = profile
        .declaration
        .artifact_root
        .join("stage-inputs")
        .join(plan_name)
        .join(leaf)
        .join(digest_hex)
        .join("training_worker_request.json");
    if let Some(parent) = request_path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(&request_path, &request_bytes)?;
    let reopened = std::fs::read(&request_path)?;
    if reopened != request_bytes {
        return Err(invalid_input(format!(
            "reopened training_worker_request bytes for {leaf} do not match what was written"
        )));
    }
    let reopened_digest = fern_experiment_types::Sha256Digest::from_bytes(&reopened);
    if reopened_digest != observed {
        return Err(invalid_input(format!(
            "reopened training_worker_request digest for {leaf} does not match"
        )));
    }
    Ok((request_path, observed, bank_path))
}

// restored from experiment_v2.rs:1269-1287 (function write_lowered_worker_configs)
/// The lowered worker configs, as the probe is about to receive them.
///
/// Printed BEFORE `probe_model_link_receipts`, because the configuration a
/// refused probe was reading is exactly what a refusal does not tell you. The
/// dump changes no source root and decides nothing: the probe still runs.
fn write_lowered_worker_configs(
    cells: &[fern_run::experiment_v2::CompiledExperimentCellV2],
    out: &mut impl std::io::Write,
) -> Fallible<()> {
    let text = serde_json::to_string_pretty(&serde_json::json!({
        "schema": "fern.lowered-worker-configs.v2",
        "cell_count": cells.len(),
        "cells": cells,
    }))
    .map_err(|error| invalid_input(format!("cannot render the lowered worker configs: {error}")))?;
    writeln!(out, "{text}")
        .map_err(|error| invalid_input(format!("cannot print the lowered worker configs: {error}")))?;
    Ok(())
}

// restored from experiment_v2.rs:1378-1390 (struct BoundImplementationSource)
/// One Binding resolved all the way to bytes on disk.
///
/// This is now the only way a V2 Plan reaches source. The V1 route resolved a
/// root keyed by an archive digest; this one is keyed by the content address of
/// the files themselves, so nothing outside the Registry decides what it holds.
/// `run_sweep::model_link_source_root_for_snapshot` still serves already-
/// registered V1 Matrix plans and is not a second answer for V2.
pub(crate) struct BoundImplementationSource {
    pub(crate) binding: fern_run::source_content::ImplementationBindingV2,
    pub(crate) implementation: fern_run::source_content::SourceImplementationV2,
    pub(crate) content: fern_run::source_content::SourceContentV2,
    pub(crate) materialized_source_root: PathBuf,
}

#[cfg(test)]
mod tests {
    use std::ffi::OsString;
    use std::sync::Mutex;

    use super::*;

    static FERN_EXPERIMENT_ENV_LOCK: Mutex<()> = Mutex::new(());

    struct PinnedExperimentEnvironment(Option<OsString>);

    impl PinnedExperimentEnvironment {
        fn install(binary: &Path) -> Self {
            let previous = std::env::var_os("FERN_EXPERIMENT");
            // SAFETY: this test holds the process-wide environment lock and
            // restores the prior value in Drop before releasing it.
            unsafe { std::env::set_var("FERN_EXPERIMENT", binary) };
            Self(previous)
        }
    }

    impl Drop for PinnedExperimentEnvironment {
        fn drop(&mut self) {
            match self.0.take() {
                Some(previous) => unsafe { std::env::set_var("FERN_EXPERIMENT", previous) },
                None => unsafe { std::env::remove_var("FERN_EXPERIMENT") },
            }
        }
    }

    fn lower_stage_plan_fixture(
        directory: &Path,
    ) -> (
        CompiledExperimentV2,
        StagePipelineTemplateV1,
        StageImplementationRefV1,
        String,
    ) {
        let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .unwrap()
            .to_path_buf();
        let golden: serde_json::Value = serde_json::from_slice(
            &std::fs::read(
                root.join("crates/fern-experiment-types/fixtures/stage_plan_go_v3_baseline_lowering_golden.json"),
            )
            .unwrap(),
        )
        .unwrap();
        let captured: CompiledExperimentV2 = serde_json::from_value(golden["compiled"].clone()).unwrap();
        let current = lower_experiment_v2(
            captured.resolution().clone(),
            captured.execution_profile_id().to_string(),
            captured.execution_profile_revision().to_string(),
            captured.runtime_device(),
        )
        .unwrap()
        .finish(
            captured.model_link_receipts().clone(),
            captured.implementation_id().unwrap().to_string(),
            captured.implementation_binding_id().unwrap().to_string(),
        )
        .unwrap();
        let python_source_root = directory.join("python");
        std::fs::create_dir_all(&python_source_root).unwrap();
        std::fs::write(python_source_root.join("fr_tr.py"), b"# caller-bound source\n").unwrap();
        let template = StagePipelineTemplateV1::parse_json(
            &std::fs::read(root.join("crates/fern-experiment-types/fixtures/stage_pipeline_forecast_origin_v1.json"))
                .unwrap(),
        )
        .unwrap();
        let python = hashed_python_source(&python_source_root).unwrap();
        let mut worker_request: serde_json::Value =
            serde_json::from_str(golden["training_worker_request_utf8"].as_str().unwrap()).unwrap();
        worker_request["config"] = serde_json::to_value(&current.cells()[0].config).unwrap();
        worker_request["run_packet"]["config_hash"] = serde_json::Value::String(current.cells()[0].config_hash.clone());
        let worker_request =
            String::from_utf8(fern_json_contract::canonical_json_bytes(&worker_request).unwrap()).unwrap();
        (current, template, python, worker_request)
    }

    #[test]
    fn lower_stage_plan_production_caller_executes_the_real_csharp_child() {
        let _lock = FERN_EXPERIMENT_ENV_LOCK.lock().unwrap();
        let directory = tempfile::tempdir().unwrap();
        let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .unwrap()
            .to_path_buf();
        let binary = root.join("csharp/Fern.Experiment.Cli/bin/Debug/net9.0/fern-experiment");
        assert!(binary.is_file(), "build Fern.Experiment.Cli before this boundary test");
        let _environment = PinnedExperimentEnvironment::install(&binary);
        let (compiled, template, python, worker_request) = lower_stage_plan_fixture(directory.path());
        let lowered = lower_first_compiled_cell_with_csharp(&compiled, &template, &python, &worker_request).unwrap();
        lowered.stage_plan.validate().unwrap();
    }

    #[test]
    fn lower_stage_plan_production_caller_fails_when_the_csharp_child_fails() {
        let _lock = FERN_EXPERIMENT_ENV_LOCK.lock().unwrap();
        let directory = tempfile::tempdir().unwrap();
        // CHG 2026-08-28: the fixture itself now asks the C# child for the
        // dataset-source capabilities, so it is built BEFORE the child is
        // pinned to /bin/false; only the call under test sees the refusal.
        let (compiled, template, python, worker_request) = lower_stage_plan_fixture(directory.path());
        let _environment = PinnedExperimentEnvironment::install(Path::new("/bin/false"));
        let error = lower_first_compiled_cell_with_csharp(&compiled, &template, &python, &worker_request)
            .unwrap_err()
            .to_string();
        // CHG 2026-08-28: the production caller now asks the C# child for the
        // dataset-source capabilities before it lowers, so with every child
        // refusing, the first refusal is that step's; the promise — the error
        // names the C# step that failed — is unchanged.
        assert!(
            error.contains("resolve-dataset-source-capabilities:") || error.contains("lower-cell-batch:"),
            "{error}"
        );
    }

    #[test]
    fn v2_draw_protocol_rejects_a_seed_base_repeated_as_the_child_seed() {
        let error = require_draw_protocol(
            "trajectory_draw",
            0,
            fern_run::experiment_v2::ExperimentDrawV2 {
                ordinal: 0,
                seed_base: 42,
                seed: 42,
            },
        )
        .unwrap_err()
        .to_string();
        assert!(error.contains("got 42, expected 803958421"), "got: {error}");
    }

    #[test]
    fn v2_draw_protocol_accepts_serie_spawned_child_seeds() {
        require_draw_protocol(
            "trajectory_draw",
            0,
            fern_run::experiment_v2::ExperimentDrawV2 {
                ordinal: 0,
                seed_base: 42,
                seed: 803_958_421,
            },
        )
        .unwrap();
        require_draw_protocol(
            "noise_draw",
            0,
            fern_run::experiment_v2::ExperimentDrawV2 {
                ordinal: 0,
                seed_base: 1000,
                seed: 1_305_264_456,
            },
        )
        .unwrap();
    }

    fn binding_for(design: &str) -> fern_run::source_content::ImplementationBindingV2 {
        let content = fern_run::source_content::SourceContentV2::from_files(
            vec![fern_run::source_content::SourceFileV2 {
                path: "fr_core.py".to_string(),
                kind: fern_run::implementation_source_snapshot::SourceEntryKind::RegularFile,
                byte_digest: Sha256Digest::from_bytes(b"# fern core").as_str().to_string(),
                bytes: 11,
            }],
            Vec::new(),
            Sha256Digest::from_bytes(b"cargo-lock").as_str().to_string(),
            Sha256Digest::from_bytes(b"catalog").as_str().to_string(),
        )
        .unwrap();
        let implementation = fern_run::source_content::SourceImplementationV2::from_content(&content).unwrap();
        fern_run::source_content::ImplementationBindingV2::new(
            implementation.implementation_id,
            design.to_string(),
            "witness".to_string(),
            String::new(),
        )
        .unwrap()
    }

    /// Gap 1a: half a source identity is a planner that did not finish.
    #[test]
    fn a_compiled_object_missing_either_source_identity_says_recompile() {
        let implementation = Some("implementation:aa");
        let binding = Some("implementation-binding:bb");
        assert!(compiled_source_identities(implementation, binding).is_ok());
        for (left, right) in [(implementation, None), (None, binding), (None, None)] {
            let error = compiled_source_identities(left, right).unwrap_err().to_string();
            assert!(error.contains("recompile"), "{error}");
        }
    }

    /// Gap 1b: the mismatch must refuse, not be silently discarded.
    #[test]
    fn a_compiled_implementation_that_disagrees_with_its_binding_is_refused() {
        require_compiled_implementation_matches_binding(
            "implementation:aa",
            "implementation-binding:bb",
            "implementation:aa",
        )
        .unwrap();
        let error = require_compiled_implementation_matches_binding(
            "implementation:aa",
            "implementation-binding:bb",
            "implementation:cc",
        )
        .unwrap_err()
        .to_string();
        assert!(error.contains("implementation:aa"), "{error}");
        assert!(error.contains("implementation:cc"), "{error}");
        assert!(error.contains("disagree"), "{error}");
    }

    /// Gap 4: one Design, or none, but never two answers.
    #[test]
    fn a_design_ref_that_disagrees_with_the_binding_is_refused() {
        let binding = binding_for("research-design:2026-08-20-controlled-dynamics");
        require_design_ref_matches_binding(None, &binding).unwrap();
        require_design_ref_matches_binding(Some("research-design:2026-08-20-controlled-dynamics"), &binding).unwrap();
        let error = require_design_ref_matches_binding(Some("research-design:2026-08-20-something-else"), &binding)
            .unwrap_err()
            .to_string();
        assert!(error.contains("2026-08-20-something-else"), "{error}");
        assert!(error.contains("2026-08-20-controlled-dynamics"), "{error}");
    }

    #[test]
    fn only_a_pipeline_with_the_train_stage_requires_cuda() {
        let training = StagePipelineTemplateV1::forecast_origin_pipeline();
        assert!(stage_pipeline_requires_cuda(&training));

        let mut oracle = training;
        oracle
            .stages
            .iter_mut()
            .find(|stage| stage.handler == "fern.train.current")
            .unwrap()
            .handler = "outside-rust-oracle".to_string();
        assert!(!stage_pipeline_requires_cuda(&oracle));
    }

    #[test]
    fn remote_probe_uses_the_maintained_helper_with_bounded_output() {
        let helper = crate::run_sweep::remote_ssh_helper_path().unwrap();
        assert_eq!(
            helper.file_name().and_then(|name| name.to_str()),
            Some("connect_runpod_ssh.sh")
        );
        let mut command = Command::new(helper);
        command
            .arg("help")
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());
        let output = run_bounded_remote_probe(command).unwrap();
        assert!(String::from_utf8_lossy(&output).contains("scripts/connect_runpod_ssh.sh exec"));

        let oversized = thread::spawn(|| Ok(vec![0; REMOTE_PROBE_MAX_OUTPUT_BYTES + 1]));
        let error = join_bounded_probe_reader(oversized, "fixture").unwrap_err().to_string();
        assert!(error.contains("exceeds"), "{error}");
    }
}
