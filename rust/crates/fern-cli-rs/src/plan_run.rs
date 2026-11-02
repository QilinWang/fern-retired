//! Internal typed RunPacket transport for resolved config JSON plus a run context.
//!
//! The public route is `just plan-run <request.json>`, which sends the complete
//! typed request to C# `PlanRunCli`. This module retains only the typed packet
//! consumption used by Plan recipe authoring; it does not mint identities.

use std::path::PathBuf;

use fern_experiment_types::DeclaredReplicaV1;
use fern_json_contract::JsonContract;
#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
use fern_run::resolved_cfg::ExecutionKind;
use fern_run::resolved_cfg::{NoiseKind, ResolvedConfigSpec, hash_canonical_json_16hex};
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};
use serde_json::Value;
#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
use serde_json::json;

use crate::build_config::QuantileCrossingPolicy;
use crate::presets::SplitTreatmentScheme;
use crate::{Fallible, invalid_input};
#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
use fern_recipes::{DataEngine, SourceDrawKind};

const RUN_CONTEXT_SCHEMA: &str = "fern.run_context.v2";
const RUN_IDENTITY_SCHEMA: &str = "fern.run_identity.v4";
const RUN_PACKET_SCHEMA: &str = "fern.run_packet.v1";

// Retired 2026-08-28: the public stable-file parser and pretty renderer moved
// to `just plan-run <request.json>`. The full source remains below for
// archaeology; C# PlanRunCliTests maps every meaningful wrapper test by name.
#[cfg(any())]
#[derive(Debug, clap::Args)]
pub struct PlanRun {
    /// Resolved `fern.cfg.v1` JSON exported from the current Python config object.
    #[arg(long)]
    pub config: PathBuf,
    /// Resolved `fern.run_context.v2` JSON carrying the typed experiment protocol identity.
    #[arg(long)]
    pub context: PathBuf,
    /// Root directory for run artifacts. This is provenance, not part of the run hash.
    #[arg(long, default_value = "results/runs")]
    pub artifact_root: PathBuf,
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema)]
struct RunPlanFlags {
    run_whiten: bool,
    run_rewind: bool,
    scale_noise_by_std: bool,
    max_epochs_override: Option<u64>,
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema)]
struct RunPlanRunContext {
    seed: i64,
    run_name: String,
    dataset_base: Option<String>,
    dataset_id: Option<String>,
    cell: serde_json::Value,
    flags: RunPlanFlags,
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema)]
struct RunPlanCatalogContext {
    config_name: Option<String>,
    recipe_name: Option<String>,
    recipe_hash: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema)]
/// Frozen null-shaped fragment retained only so existing run identities do not
/// change when free-text ablation labels leave the active command/DB surface.
struct LegacyNullAblationContext {
    study: Option<String>,
    name: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema)]
struct RunPlanArtifactContext {
    run_collection: String,
    dataset: Option<String>,
    pred_len: u64,
    noise_sigma: f64,
    trajectory_draw_ordinal: i64,
    noise_draw_ordinal: i64,
    short_id_format: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub(crate) enum TruthKind {
    SimulatorTrajectory,
    ObservedDataset,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub(crate) enum NoisePlacement {
    AdditivePostGenerationObservation,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub(crate) enum TrainingScaleHashKind {
    DerivationPreimage,
}

/// Whether this run owns training or evaluates a checkpoint fitted by the
/// paired clean target. This is protocol identity, not merely scheduling:
/// loading a frozen model answers a different scientific question from
/// retraining a nominally identical model at every dose.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub(crate) enum CheckpointExecutionMode {
    FitCleanValidationBest,
    EvaluateCleanValidationBest,
    EvaluateNoiseMatchedValidationBest,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
pub(crate) struct CheckpointSourceIdentity {
    pub(crate) run_hash: String,
    pub(crate) target_hash: String,
    pub(crate) model_file: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
pub(crate) struct CheckpointExecutionIdentity {
    pub(crate) mode: CheckpointExecutionMode,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub(crate) source: Option<CheckpointSourceIdentity>,
}

/// Scientific protocol fields that must change `run_hash` when their meaning
/// changes. The training-scale value is the hash of an inspectable derivation
/// preimage because the actual train-split tensor does not exist at plan time.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
pub(crate) struct ExperimentProtocolIdentity {
    schema: String,
    truth_kind: TruthKind,
    noise_placement: NoisePlacement,
    noise_law: NoiseKind,
    /// Lag-one correlation of the measurement process. Omitted for the iid
    /// historical protocol so existing run hashes remain unchanged.
    #[serde(default, skip_serializing_if = "is_zero_noise_ar1_phi")]
    noise_ar1_phi: f64,
    relative_dose: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    split_treatment_scheme: Option<SplitTreatmentScheme>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    checkpoint_execution: Option<CheckpointExecutionIdentity>,
    noise_bank_seed: u64,
    training_scale_hash: String,
    training_scale_hash_kind: TrainingScaleHashKind,
    training_scale_preimage: Value,
    checkpoint_revision: Option<String>,
    quantile_crossing_policy: QuantileCrossingPolicy,
}

fn is_zero_noise_ar1_phi(phi: &f64) -> bool {
    *phi == 0.0
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema)]
struct RunPlanContext {
    schema: String,
    experiment: serde_json::Value,
    model_architecture: serde_json::Value,
    protocol: ExperimentProtocolIdentity,
    run: RunPlanRunContext,
    catalog: RunPlanCatalogContext,
    ablation: LegacyNullAblationContext,
    artifact: RunPlanArtifactContext,
}

#[derive(Debug, Clone, Serialize, JsonSchema)]
struct RunIdentity {
    schema: &'static str,
    config_hash: String,
    config: ResolvedConfigSpec,
    context: RunPlanContext,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    declared_replica: Option<DeclaredReplicaV1>,
}

#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
pub(crate) fn experiment_protocol_identity(
    config: &ResolvedConfigSpec,
    draw_kind: SourceDrawKind,
    data_engine: DataEngine,
    source_name: &str,
    trajectory_draw_seed: u64,
    noise_bank_seed: u64,
    relative_dose: bool,
    split_treatment_scheme: Option<SplitTreatmentScheme>,
    checkpoint_execution: Option<CheckpointExecutionIdentity>,
) -> Fallible<ExperimentProtocolIdentity> {
    let truth_kind = match draw_kind {
        SourceDrawKind::DeterministicTrajectory | SourceDrawKind::StochasticTrajectory => {
            TruthKind::SimulatorTrajectory
        }
        SourceDrawKind::KnownData => TruthKind::ObservedDataset,
    };
    let training_scale_kind = if relative_dose {
        "clean_train_channel_std_ddof1_floor_1e-8"
    } else {
        "unit_scale"
    };
    let training_scale_preimage = json!({
        "schema": "fern.training_scale_derivation.v1",
        "kind": training_scale_kind,
        "source": {
            "name": source_name,
            "data_engine": data_engine,
            "trajectory_draw_seed": trajectory_draw_seed,
        },
        "train_partition": {
            "start": config.data.train_start,
            "end": config.data.train_end,
        },
    });
    let training_scale_hash = hash_canonical_json_16hex(&training_scale_preimage)?;

    let checkpoint_revision = config
        .model
        .knobs
        .get("hf_revision")
        .and_then(Value::as_str)
        .map(str::to_string);
    if config.execution.kind == ExecutionKind::PretrainedInference && checkpoint_revision.is_none() {
        return Err(invalid_input(
            "pretrained inference config must pin model.knobs.hf_revision",
        ));
    }
    let crossing_policy_raw = config
        .model
        .knobs
        .get("quantile_crossing_policy")
        .and_then(Value::as_str)
        .unwrap_or(QuantileCrossingPolicy::NotApplicable.value());
    let quantile_crossing_policy = QuantileCrossingPolicy::parse(crossing_policy_raw).map_err(invalid_input)?;

    Ok(ExperimentProtocolIdentity {
        schema: "fern.experiment_protocol.v1".to_string(),
        truth_kind,
        noise_placement: NoisePlacement::AdditivePostGenerationObservation,
        noise_law: config.data.noise_kind,
        noise_ar1_phi: config.data.noise_ar1_phi,
        relative_dose,
        split_treatment_scheme,
        checkpoint_execution,
        noise_bank_seed,
        training_scale_hash,
        training_scale_hash_kind: TrainingScaleHashKind::DerivationPreimage,
        training_scale_preimage,
        checkpoint_revision,
        quantile_crossing_policy,
    })
}

#[derive(Debug, Serialize, JsonSchema)]
pub(crate) struct RunPacket {
    pub(crate) schema: &'static str,
    pub(crate) run_hash: String,
    pub(crate) config_hash: String,
    pub(crate) artifact_root: String,
    pub(crate) artifact_dir: String,
    /// Keep `raw_tensors.safetensors` -- the complete horizon-by-channel
    /// forecast and target for each forecast origin -- instead of deleting it once the stitched
    /// sequence has been written.
    ///
    /// WHY THIS IS NOT `artifact.policy`. There is already a slim/full switch,
    /// and it sits inside the hashed config, so asking for origin tensors changes
    /// the run hash and the answer arrives at a different identity from the
    /// campaign cell it was supposed to reproduce. Retention decides which
    /// bytes survive on disk and nothing else: the origin tensors are computed and
    /// written on every run either way, and the parent deletes them afterwards.
    /// So it belongs here, beside `artifact_dir`, outside `identity` -- the
    /// same cell, with its evidence kept. `plan_run_hash_ignores_window_retention`
    /// holds that line.
    #[serde(default)]
    pub(crate) retain_forecast_origins: bool,
    identity: RunIdentity,
    pub(crate) config: ResolvedConfigSpec,
    context: RunPlanContext,
}

/// C# must name the same fixed packet/identity schemas, but the private wire
/// decoder owns its received strings so a malicious or stale child cannot
/// manufacture an arbitrary Rust `&'static str` tag.
#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct CSharpRunIdentityReceipt {
    schema: String,
    config_hash: String,
    config: ResolvedConfigSpec,
    context: RunPlanContext,
    declared_replica: Option<DeclaredReplicaV1>,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct CSharpRunPacketReceipt {
    schema: String,
    run_hash: String,
    config_hash: String,
    artifact_root: String,
    artifact_dir: String,
    retain_forecast_origins: bool,
    identity: CSharpRunIdentityReceipt,
    config: ResolvedConfigSpec,
    context: RunPlanContext,
}

impl JsonContract for RunPacket {
    const SCHEMA_ID: &'static str = RUN_PACKET_SCHEMA;
}

#[cfg(any())]
pub fn try_run(args: PlanRun) -> Fallible<()> {
    let packet = build_run_packet(args)?;
    println!("{}", packet.checked_pretty_json()?);
    Ok(())
}

#[cfg(any())]
fn build_run_packet(args: PlanRun) -> Fallible<RunPacket> {
    let config_file = File::open(&args.config)?;
    let config: ResolvedConfigSpec = serde_json::from_reader(config_file)?;
    let context_file = File::open(&args.context)?;
    let context: serde_json::Value = serde_json::from_reader(context_file)?;
    build_run_packet_from_context_value(config, context, args.artifact_root, None)
}

#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
pub(crate) fn build_run_packet_from_context_value(
    config: ResolvedConfigSpec,
    context: serde_json::Value,
    artifact_root: PathBuf,
    declared_replica: Option<&DeclaredReplicaV1>,
) -> Fallible<RunPacket> {
    #[derive(Serialize)]
    struct PlanRunRequest<'a> {
        config: &'a str,
        context: &'a serde_json::Value,
        artifact_root: &'a str,
        declared_replica: Option<&'a DeclaredReplicaV1>,
    }

    let config_json = serde_json::to_string(&config)?;
    let artifact_root = artifact_root
        .to_str()
        .ok_or_else(|| invalid_input("artifact root must be valid UTF-8 for the C# Experiment boundary"))?;
    let request = PlanRunRequest {
        config: &config_json,
        context: &context,
        artifact_root,
        declared_replica,
    };
    let response = fern_run::csharp_experiment::run_bytes("plan-run", &request).map_err(invalid_input)?;
    let receipt: CSharpRunPacketReceipt = serde_json::from_slice(&response)
        .map_err(|error| invalid_input(format!("plan-run returned invalid run packet: {error}")))?;
    if receipt.schema != RUN_PACKET_SCHEMA {
        return Err(invalid_input(format!(
            "plan-run returned schema {:?}, expected {RUN_PACKET_SCHEMA:?}",
            receipt.schema
        )));
    }
    if receipt.identity.schema != RUN_IDENTITY_SCHEMA {
        return Err(invalid_input(format!(
            "plan-run returned identity schema {:?}, expected {RUN_IDENTITY_SCHEMA:?}",
            receipt.identity.schema
        )));
    }
    if receipt.identity.config_hash != receipt.config_hash {
        return Err(invalid_input(
            "plan-run returned an identity whose config_hash differs from its packet",
        ));
    }
    Ok(RunPacket {
        schema: RUN_PACKET_SCHEMA,
        run_hash: receipt.run_hash,
        config_hash: receipt.config_hash,
        artifact_root: receipt.artifact_root,
        artifact_dir: receipt.artifact_dir,
        retain_forecast_origins: receipt.retain_forecast_origins,
        identity: RunIdentity {
            schema: RUN_IDENTITY_SCHEMA,
            config_hash: receipt.identity.config_hash,
            config: receipt.identity.config,
            context: receipt.identity.context,
            declared_replica: receipt.identity.declared_replica,
        },
        config: receipt.config,
        context: receipt.context,
    })
}

// Retired 2026-08-28: PlanRunCli owns complete config/context admission,
// identity minting and artifact projection.  The former Rust body remains
// intact below for source archaeology only; no active caller reaches it.
#[cfg(any())]
fn build_run_packet_from_config_and_context(
    config: ResolvedConfigSpec,
    context: RunPlanContext,
    artifact_root: PathBuf,
    declared_replica: Option<&DeclaredReplicaV1>,
) -> Fallible<RunPacket> {
    validate_context(&context)?;
    if let Some(replica) = declared_replica {
        replica.validate().map_err(invalid_input)?;
    }

    let pred_len_i64 = i64::try_from(context.artifact.pred_len).map_err(|_| {
        invalid_input(format!(
            "context.artifact.pred_len too large for artifact path: {}",
            context.artifact.pred_len
        ))
    })?;
    let config_hash = config.config_hash()?;
    let identity = RunIdentity {
        schema: RUN_IDENTITY_SCHEMA,
        config_hash: config_hash.clone(),
        config: config.clone(),
        context: context.clone(),
        declared_replica: declared_replica.cloned(),
    };
    // A repeated execution remains the same scientific target, but it cannot
    // share one run row or artifact path with the primary execution. Putting
    // the declared ordinal and bounded purpose in RUN identity gives it a
    // separate run_hash while target_hash remains unchanged.
    let run_hash = hash_canonical_json_16hex(&serde_json::to_value(&identity)?)?;

    let artifact_dir = fern_run::csharp_experiment::build_artifact_dir(
        &artifact_root,
        &context.artifact.run_collection,
        context.artifact.dataset.as_deref(),
        &config.model.model_type,
        pred_len_i64,
        context.run.seed,
        context.artifact.noise_sigma,
        context.artifact.trajectory_draw_ordinal,
        context.artifact.noise_draw_ordinal,
        Some(&run_hash),
        &context.artifact.short_id_format,
    )
    .map_err(invalid_input)?;

    Ok(RunPacket {
        schema: RUN_PACKET_SCHEMA,
        run_hash,
        config_hash,
        artifact_root: artifact_root.display().to_string(),
        artifact_dir: artifact_dir.display().to_string(),
        // Off unless a caller asks: keeping every run's origin tensors is what filled
        // 183 GB. Callers set it after planning rather than through this
        // signature, so a launcher that wants the origin tensors adds one line and
        // every other launcher is untouched.
        retain_forecast_origins: false,
        identity,
        config,
        context,
    })
}

#[cfg(any())]
fn validate_context(context: &RunPlanContext) -> Fallible<()> {
    if context.schema != RUN_CONTEXT_SCHEMA {
        return Err(invalid_input(format!(
            "context.schema must be {RUN_CONTEXT_SCHEMA:?}, got {:?}",
            context.schema
        )));
    }
    if context.run.run_name.trim().is_empty() {
        return Err(invalid_input("context.run.run_name must be non-empty"));
    }
    if context.ablation.study.is_some() || context.ablation.name.is_some() {
        return Err(invalid_input(
            "context.ablation is a frozen legacy identity fragment and both study/name must be null; register a typed Experiment Contrast instead",
        ));
    }
    if !context.artifact.noise_sigma.is_finite() {
        return Err(invalid_input("context.artifact.noise_sigma must be finite"));
    }
    if context.artifact.pred_len == 0 {
        return Err(invalid_input("context.artifact.pred_len must be > 0"));
    }
    if context.artifact.short_id_format == "recipe-draw" && context.artifact.dataset.is_none() {
        return Err(invalid_input(
            "context.artifact.short_id_format recipe-draw requires context.artifact.dataset",
        ));
    }
    Ok(())
}

#[cfg(all(test, any()))]
mod tests {
    use super::*;
    use std::fs;
    use std::sync::atomic::{AtomicUsize, Ordering};

    static TEMP_FILE_COUNTER: AtomicUsize = AtomicUsize::new(0);

    fn temp_json_path(prefix: &str) -> PathBuf {
        let ordinal = TEMP_FILE_COUNTER.fetch_add(1, Ordering::SeqCst);
        std::env::temp_dir().join(format!("{prefix}_{}_{}.json", std::process::id(), ordinal))
    }

    fn write_config_file() -> PathBuf {
        let config_path = temp_json_path("fern_plan_run_cfg");
        let config = serde_json::json!({
            "schema": "fern.cfg.v2",
            // Stated, as every config written by `build-config` states it since
            // 2026-08-26. A fixture without it reads as scheme 1 and is refused,
            // which is the point: an old config on disk carries a scheme-1
            // identity that this build cannot recompute.
            "identity_scheme": 2,
            "execution": {"kind": "train"},
            "model": {"type": "fern", "knobs": {"dim_hidden": 128}},
            "task": {"seq_len": 96, "pred_len": 48, "channels": 3, "channel_independent": false},
            "data": {
                "train_start": 0.0,
                "train_end": 0.7,
                "val_start": 0.7,
                "val_end": 0.9,
                "test_start": 0.9,
                "test_end": 1.0,
                "normalization": "none",
                "noise_kind": "gaussian"
            },
            "train": {"task_name": "long_term_forecast", "epochs": 50, "patience": 5, "grace_period": 1, "batch_size": 95, "num_workers": 0},
            "optim": {
                "type": "adamw",
                "learning_rate": 0.0003,
                "weight_decay": 0.0,
                "max_grad_norm": 0.0,
                "muon_momentum": 0.95,
                "muon_beta2": 0.999,
                "muon_ns_steps": 5
            },
            "scheduler": {"type": "none", "plateau_patience": 10, "factor": 0.1, "warmup_epochs": 5, "eta_min": 0.00001},
            "runtime": {
                "device": "cuda",
                "dtype": "float32",
                "amp_dtype": "none",
                "compile": {"enabled": false, "mode": "default", "fullgraph": false, "dynamic": false}
            },
            "loss": {
                "train": [{"name": "huber", "role": "active", "weight": 1.0}],
                "eval": [{"name": "mae", "role": "active", "weight": 1.0}],
                "huber_delta": {"train": 1.0, "eval": 1.0},
                "num_proj_swd": 0,
                "probability": {"num_train_samples": 1, "num_eval_samples": 1, "quantile_levels": [0.05, 0.5, 0.95]},
                "smoothing": {
                    "train": {"kind": "none", "window": 3, "alpha": 0.1},
                    "val": {"kind": "none", "window": 3, "alpha": 0.1},
                    "test": {"kind": "none", "window": 3, "alpha": 0.1}
                }
            },
            "artifact": {"policy": "slim"}
        });
        fs::write(&config_path, serde_json::to_string(&config).unwrap()).unwrap();
        config_path
    }

    fn write_context_file(short_id_format: &str, dataset: Option<&str>) -> PathBuf {
        let context_path = temp_json_path("fern_plan_run_context");
        let context = serde_json::json!({
            "schema": RUN_CONTEXT_SCHEMA,
            "experiment": {"name": "lorenz", "version": "v1"},
            "model_architecture": {"schema": "fern.model_architecture.test.v1"},
            "protocol": {
                "schema": "fern.experiment_protocol.v1",
                "truth_kind": "simulator_trajectory",
                "noise_placement": "additive_post_generation_observation",
                "noise_law": "gaussian",
                "relative_dose": false,
                "noise_bank_seed": 1000,
                "training_scale_hash": "1234567890abcdef",
                "training_scale_hash_kind": "derivation_preimage",
                "training_scale_preimage": {"schema": "fern.training_scale_derivation.v1"},
                "checkpoint_revision": null,
                "quantile_crossing_policy": "not_applicable"
            },
            "run": {
                "seed": 42,
                "run_name": "fern_lorenz",
                "dataset_base": dataset,
                "dataset_id": dataset.map(|name| format!("{name}_traj0")),
                "cell": {"source_name": dataset, "noise_level": 0.2},
                "flags": {
                    "run_whiten": false,
                    "run_rewind": false,
                    "scale_noise_by_std": false,
                    "max_epochs_override": null
                }
            },
            "catalog": {"config_name": "lorenz", "recipe_name": null, "recipe_hash": null},
            "ablation": {"study": null, "name": null},
            "artifact": {
                "run_collection": "standalone",
                "dataset": dataset,
                "pred_len": 48,
                "noise_sigma": 0.2,
                "trajectory_draw_ordinal": 0,
                "noise_draw_ordinal": 0,
                "short_id_format": short_id_format
            }
        });
        fs::write(&context_path, serde_json::to_string(&context).unwrap()).unwrap();
        context_path
    }

    #[test]
    fn plans_single_run_packet_without_db_write() {
        let packet = build_run_packet(PlanRun {
            config: write_config_file(),
            context: write_context_file("single-run", None),
            artifact_root: PathBuf::from("results/runs"),
        })
        .unwrap();

        assert_eq!(packet.schema, RUN_PACKET_SCHEMA);
        assert_eq!(packet.identity.schema, RUN_IDENTITY_SCHEMA);
        assert_eq!(packet.run_hash.len(), 16);
        assert_eq!(packet.config_hash.len(), 16);
        assert!(
            packet
                .artifact_dir
                .ends_with(&format!("fern_h48_sig0p2_s42_{}", &packet.run_hash[..4]))
        );
    }

    #[test]
    fn recipe_draw_requires_dataset() {
        let err = build_run_packet(PlanRun {
            config: write_config_file(),
            context: write_context_file("recipe-draw", None),
            artifact_root: PathBuf::from("results/runs"),
        })
        .expect_err("recipe-draw without dataset should fail");

        assert!(err.to_string().contains("requires context.artifact.dataset"));
    }

    #[test]
    fn free_text_legacy_ablation_is_refused() {
        let config_path = write_config_file();
        let context_path = write_context_file("single-run", None);
        let mut context: Value = serde_json::from_str(&fs::read_to_string(&context_path).unwrap()).unwrap();
        context["ablation"]["study"] = json!("untyped-label");
        fs::write(&context_path, serde_json::to_string(&context).unwrap()).unwrap();

        let err = build_run_packet(PlanRun {
            config: config_path,
            context: context_path,
            artifact_root: PathBuf::from("results/runs"),
        })
        .expect_err("free-text ablation labels must not mint new run identities");

        assert!(err.to_string().contains("register a typed Experiment Contrast"));
    }

    #[test]
    fn plan_run_hash_ignores_forecast_origin_retention() {
        // The whole point of the field. Asking a finished cell for its
        // per-origin evidence must return the SAME cell, so the rerun can be
        // compared against the campaign's recorded score rather than becoming
        // a different experiment that happens to look similar. The existing
        // slim/full switch cannot do this: it lives in the hashed config.
        let mut packet = build_run_packet(PlanRun {
            config: write_config_file(),
            context: write_context_file("single-run", None),
            artifact_root: PathBuf::from("results/runs"),
        })
        .unwrap();
        let (planned_hash, planned_dir) = (packet.run_hash.clone(), packet.artifact_dir.clone());

        assert!(!packet.retain_forecast_origins, "retention is off unless asked");
        packet.retain_forecast_origins = true;

        assert_eq!(packet.run_hash, planned_hash);
        assert_eq!(packet.artifact_dir, planned_dir);
        // And it is absent from the hashed preimage itself, not merely equal by
        // luck: `identity` is what gets hashed, and it never sees the field.
        let identity_json = serde_json::to_value(&packet.identity).unwrap();
        assert!(identity_json.get("retain_forecast_origins").is_none());
        let serialized = serde_json::to_value(&packet).unwrap();
        assert_eq!(serialized["retain_forecast_origins"], json!(true));
        assert_eq!(serialized["run_hash"], json!(planned_hash));
    }

    #[test]
    fn protocol_change_changes_run_hash() {
        let config_path = write_config_file();
        let context_path = write_context_file("single-run", None);
        let first = build_run_packet(PlanRun {
            config: config_path.clone(),
            context: context_path.clone(),
            artifact_root: PathBuf::from("results/runs"),
        })
        .unwrap();

        let mut context: Value = serde_json::from_str(&fs::read_to_string(&context_path).unwrap()).unwrap();
        context["protocol"]["relative_dose"] = json!(true);
        fs::write(&context_path, serde_json::to_string(&context).unwrap()).unwrap();
        let second = build_run_packet(PlanRun {
            config: config_path,
            context: context_path,
            artifact_root: PathBuf::from("results/runs"),
        })
        .unwrap();

        assert_ne!(first.run_hash, second.run_hash);
        assert_eq!(first.identity.schema, "fern.run_identity.v4");
    }

    #[test]
    fn split_treatment_scheme_changes_run_hash() {
        let config_path = write_config_file();
        let context_path = write_context_file("single-run", None);
        let legacy = build_run_packet(PlanRun {
            config: config_path.clone(),
            context: context_path.clone(),
            artifact_root: PathBuf::from("results/runs"),
        })
        .unwrap();

        let mut context: Value = serde_json::from_str(&fs::read_to_string(&context_path).unwrap()).unwrap();
        context["protocol"]["split_treatment_scheme"] = json!("clean_train_clean_select_test_titration");
        fs::write(&context_path, serde_json::to_string(&context).unwrap()).unwrap();
        let csr = build_run_packet(PlanRun {
            config: config_path,
            context: context_path,
            artifact_root: PathBuf::from("results/runs"),
        })
        .unwrap();

        assert_ne!(legacy.run_hash, csr.run_hash);
        assert!(legacy.identity.context.protocol.split_treatment_scheme.is_none());
        assert_eq!(
            csr.identity.context.protocol.split_treatment_scheme,
            Some(SplitTreatmentScheme::CleanTrainCleanSelectTestTitration),
        );
    }
}

#[cfg(test)]
mod csharp_child_tests {
    use super::*;
    use fern_json_contract::JsonContract;
    use fern_run::resolved_cfg::Sha256Digest;
    use serde_json::json;

    #[test]
    fn real_csharp_child_plans_single_run_packet_without_db_write() {
        // Full-output golden captured from the retired public Rust executable:
        // the SHA-256 below covers its complete pretty packet plus newline,
        // rather than only the two visible identity strings.
        let config: ResolvedConfigSpec = serde_json::from_slice(
            &fern_run::csharp_experiment::run_bytes(
                "build-config",
                &json!({
                    "model": "fern",
                    "fern_architecture": "current",
                    "source_std": null,
                    "fern_radial_head": false,
                    "fern_density": "single_gaussian",
                    "fern_components": 1,
                    "fern_active_components": 1,
                    "seq_len": 336,
                    "pred_len": 64,
                    "channels": null,
                    "channel_independent": false,
                    "eval": "huber",
                    "loader": "p70_20_10_none_bs95",
                    "train": "default_train",
                    "noise_kind": "gaussian",
                    "noise_ar1_phi": 0.0,
                    "device": "cuda",
                    "loss_json": null,
                    "fern_patch_json": null,
                    "bootstrap": null,
                }),
            )
            .unwrap(),
        )
        .unwrap();
        let context = json!({
            "schema": RUN_CONTEXT_SCHEMA,
            "experiment": {"name": "lorenz", "version": "v1"},
            "model_architecture": {"schema": "fern.model_architecture.test.v1"},
            "protocol": {
                "schema": "fern.experiment_protocol.v1",
                "truth_kind": "simulator_trajectory",
                "noise_placement": "additive_post_generation_observation",
                "noise_law": "gaussian",
                "relative_dose": false,
                "noise_bank_seed": 1000,
                "training_scale_hash": "1234567890abcdef",
                "training_scale_hash_kind": "derivation_preimage",
                "training_scale_preimage": {"schema": "fern.training_scale_derivation.v1"},
                "checkpoint_revision": null,
                "quantile_crossing_policy": "not_applicable",
            },
            "run": {
                "seed": 42,
                "run_name": "fern_lorenz",
                "dataset_base": null,
                "dataset_id": null,
                "cell": {"source_name": null, "noise_level": 0.2},
                "flags": {
                    "run_whiten": false,
                    "run_rewind": false,
                    "scale_noise_by_std": false,
                    "max_epochs_override": null,
                },
            },
            "catalog": {"config_name": "lorenz", "recipe_name": null, "recipe_hash": null},
            "ablation": {"study": null, "name": null},
            "artifact": {
                "run_collection": "standalone",
                "dataset": null,
                "pred_len": 96,
                "noise_sigma": 0.2,
                "trajectory_draw_ordinal": 0,
                "noise_draw_ordinal": 0,
                "short_id_format": "single-run",
            },
        });

        let packet = build_run_packet_from_context_value(
            config,
            context,
            PathBuf::from("results/runs"),
            None,
        )
        .unwrap();

        let rendered = format!("{}\n", packet.checked_pretty_json().unwrap());
        assert_eq!(
            Sha256Digest::from_bytes(rendered.as_bytes()).as_str(),
            "sha256:467696c0bd7585bad52ab305b45052b6df7f01cc89698499e17c26617742f27e"
        );
    }
}
