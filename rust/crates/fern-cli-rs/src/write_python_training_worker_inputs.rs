//! Run packet -> Python training-worker registration/request derivation.
//!
//! This is the narrow bridge between the Rust identity planner and the current
//! Python training rectangle. `plan-run` mints the stable packet; this module
//! derives the registration and request JSON the run commands hand to the
//! Python training worker.

use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};

use fern_run::db::RunRegistration;
use fern_run::resolved_cfg::{ResolvedConfigSpec, hash_bytes_16hex};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};

use crate::{Fallible, invalid_input};

const RUN_PACKET_SCHEMA: &str = "fern.run_packet.v1";
const TRAINING_WORKER_REQUEST_SCHEMA: &str = "fern.training_worker_request.v1";
const CODE_FINGERPRINT_SCHEMA: &str = "fern.code_fingerprint.v1";

/// The load-bearing root Python files that define what the training worker
/// executes. Their content hashes are recorded as run provenance (decision
/// 2026-07-01-02) — outside run identity — because `git_sha` alone cannot see
/// dirty working trees, which is how research runs usually launch.
const CODE_FINGERPRINT_FILES: &[&str] = &[
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
];

#[derive(Debug, Clone, Serialize, Deserialize)]
struct WorkerRunPacket {
    schema: String,
    run_hash: String,
    config_hash: String,
    artifact_root: String,
    artifact_dir: PathBuf,
    /// Keep the per-window forecast/target tensors this run is scored on.
    /// Provenance, never identity -- see `RunPacket::retain_forecast_origins`.
    /// Defaulted so every packet written before the field existed still parses.
    #[serde(default)]
    retain_forecast_origins: bool,
    config: ResolvedConfigSpec,
    context: WorkerRunContext,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct WorkerRunContext {
    experiment: Value,
    protocol: Value,
    run: WorkerRunContextRun,
    catalog: WorkerCatalogContext,
    #[serde(default)]
    run_note: Option<String>,
    artifact: WorkerArtifactContext,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct WorkerRunFlags {
    run_whiten: bool,
    run_rewind: bool,
    scale_noise_by_std: bool,
    max_epochs_override: Option<u64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct WorkerRunContextRun {
    seed: i64,
    dataset_base: Option<String>,
    dataset_id: Option<String>,
    cell: Value,
    flags: WorkerRunFlags,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct WorkerCatalogContext {
    config_name: Option<String>,
    recipe_name: Option<String>,
    recipe_hash: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct WorkerArtifactContext {
    noise_sigma: f64,
    trajectory_draw_ordinal: i64,
    noise_draw_ordinal: i64,
}

fn build_worker_inputs(
    packet: &WorkerRunPacket,
    packet_value: &Value,
    campaign_id: i64,
    target_hash: Option<String>,
) -> Fallible<(RunRegistration, Value)> {
    if packet.schema != RUN_PACKET_SCHEMA {
        return Err(invalid_input(format!(
            "run packet schema must be {RUN_PACKET_SCHEMA:?}, got {:?}",
            packet.schema
        )));
    }
    if !packet.context.artifact.noise_sigma.is_finite() {
        return Err(invalid_input("context.artifact.noise_sigma must be finite"));
    }

    let cell = &packet.context.run.cell;
    let dataset = packet.context.run.dataset_base.clone().unwrap_or_default();
    let dataset_id = packet
        .context
        .run
        .dataset_id
        .clone()
        .or_else(|| packet.context.run.dataset_base.clone())
        .unwrap_or_default();
    let experiment_name = experiment_name(&packet.context.experiment)?;
    let trajectory_draw_ordinal = cell_i64_or(
        cell,
        "trajectory_draw_ordinal",
        packet.context.artifact.trajectory_draw_ordinal,
    )?;
    let noise_draw_ordinal = cell_i64_or(cell, "noise_draw_ordinal", packet.context.artifact.noise_draw_ordinal)?;
    let trajectory_seed_base = required_cell_i64(cell, "trajectory_seed_base")?;
    let noise_seed_base = required_cell_i64(cell, "noise_seed_base")?;
    let trajectory_draw_seed = required_cell_i64(cell, "trajectory_draw_seed")?;
    let noise_draw_seed = required_cell_i64(cell, "noise_draw_seed")?;

    let registration = RunRegistration {
        schema: RunRegistration::SCHEMA_ID.to_string(),
        dataset,
        dataset_id,
        model_type: packet.config.model.model_type.clone(),
        seed: packet.context.run.seed,
        pred_len: i64::try_from(packet.config.task.pred_len)
            .map_err(|_| invalid_input("config.task.pred_len is too large for RunRegistration"))?,
        seq_len: i64::try_from(packet.config.task.seq_len)
            .map_err(|_| invalid_input("config.task.seq_len is too large for RunRegistration"))?,
        noise_sigma: packet.context.artifact.noise_sigma,
        trajectory_draw_ordinal,
        noise_draw_ordinal,
        trajectory_seed_base,
        noise_seed_base,
        trajectory_draw_seed,
        noise_draw_seed,
        config_json: Some(serde_json::to_string(&packet.config)?),
        config_hash: Some(packet.config_hash.clone()),
        git_sha: String::new(),
        code_fingerprint_json: Some(code_fingerprint_json()?),
        template_id: Some(campaign_id),
        source: if packet.context.catalog.recipe_name.is_some() {
            "template".to_string()
        } else {
            "user_defined".to_string()
        },
        config_name: packet.context.catalog.config_name.clone(),
        recipe_name: packet.context.catalog.recipe_name.clone(),
        recipe_hash: packet.context.catalog.recipe_hash.clone(),
        // Which DESIGNED recipe cell this run executes (recipe_targets row).
        // Passed alongside the packet, never read out of it: target_hash is
        // derived from content already inside run identity, so it must not
        // enter the hashed run packet. None for standalone runs.
        target_hash,
        run_note: packet.context.run_note.clone(),
        invocation_json: None,
        run_hash: packet.run_hash.clone(),
        n_features: packet
            .config
            .task
            .channels
            .map(i64::try_from)
            .transpose()
            .map_err(|_| invalid_input("config.task.channels is too large for RunRegistration"))?,
        artifact_dir: packet.artifact_dir.clone(),
    };

    let mut request = json!({
        "schema": TRAINING_WORKER_REQUEST_SCHEMA,
        "experiment_name": experiment_name,
        "protocol": packet.context.protocol,
        "config": packet.config,
        "cell": packet.context.run.cell,
        "seed": packet.context.run.seed,
        "sweep_id": campaign_id,
        "model_name": packet.config.model.model_type,
        "results_root": packet.artifact_root,
        "flags": packet.context.run.flags,
        "catalog": packet.context.catalog,
        "run_note": packet.context.run_note,
        "dataset_base": packet.context.run.dataset_base,
        "dataset_id": packet.context.run.dataset_id,
        "retain_forecast_origins": packet.retain_forecast_origins,
        "run_packet": packet_value,
    });
    // The worker reads `bootstrap` at the request's top level (fr_tr.py's
    // EnbPI child path), while identity lives in config.bootstrap — one
    // source, lifted here, never duplicated by hand. train_window_count is
    // deliberately absent: Rust must not re-derive serie-rs windowing math
    // (#41); each member REPORTS its actual count in the bootstrap output,
    // and the aggregation stage demands cross-member unanimity instead.
    if let Some(bootstrap) = &packet.config.bootstrap {
        request["bootstrap"] = json!({
            "bootstrap_idx": bootstrap.bootstrap_idx,
            "bootstrap_seed": bootstrap.bootstrap_seed,
            "block_size": bootstrap.block_size,
        });
    }

    Ok((registration, request))
}

pub(crate) fn build_worker_inputs_from_packet_value(
    packet: Value,
    campaign_id: i64,
    target_hash: Option<String>,
) -> Fallible<(RunRegistration, Value)> {
    let typed_packet: WorkerRunPacket = serde_json::from_value(packet.clone())?;
    build_worker_inputs(&typed_packet, &packet, campaign_id, target_hash)
}

/// Fingerprint the worker's executable Python surface at spawn time. Files are
/// resolved relative to the parent's working directory — the same directory
/// `python -m fr_tr` imports from. Missing files are listed, not fatal, so the
/// fingerprint stays honest when commands run outside the workspace root.
/// CHG: when the cwd lookup finds NO fingerprint file at all (the wrong-cwd
/// signature), resolution falls back to the compile-time workspace root, so
/// the fingerprint is cwd-independent and truthful; "root" records which root
/// was hashed ("cwd" | "workspace"). All-missing under BOTH roots warns loudly
/// on stderr but still records as-is: provenance never aborts a run.
fn code_fingerprint_json() -> Fallible<String> {
    let workspace_root = compile_time_workspace_root();
    let fingerprint = resolve_code_fingerprint(Path::new("."), workspace_root);
    if fingerprint.files.is_empty() {
        let cwd = std::env::current_dir()
            .map(|dir| dir.display().to_string())
            .unwrap_or_else(|_| ".".to_string());
        eprintln!(
            "fern-run: WARNING: none of the code-fingerprint files ({}) exist under the current \
             directory ({cwd}) or the workspace root ({}); recording an all-missing fingerprint. \
             Run commands from the workspace root where fr.py/fr_tr.py live — `python -m fr_tr` \
             cannot import the training worker otherwise.",
            CODE_FINGERPRINT_FILES.join(", "),
            workspace_root.display(),
        );
    }
    let value = json!({
        "schema": CODE_FINGERPRINT_SCHEMA,
        "files": fingerprint.files,
        "missing": fingerprint.missing,
        "root": fingerprint.root,
        "git_dirty": git_dirty(),
    });
    Ok(serde_json::to_string(&value)?)
}

/// Workspace root baked in at COMPILE time: the parent of this crate's
/// manifest directory (fern-cli-rs/ sits directly under the workspace root,
/// beside fr.py/fr_tr.py). Fallback fingerprint root only — a fern-run binary
/// invoked from elsewhere still hashes the real worker files instead of
/// recording an all-missing fingerprint.
fn compile_time_workspace_root() -> &'static Path {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap_or_else(|| Path::new(env!("CARGO_MANIFEST_DIR")))
}

/// One resolved code fingerprint: hashed files, files not found under the
/// chosen root, and which root was hashed ("cwd" | "workspace").
struct ResolvedCodeFingerprint {
    files: BTreeMap<&'static str, String>,
    missing: Vec<&'static str>,
    root: &'static str,
}

/// cwd first; the workspace root only when the cwd yields NOTHING. A partial
/// cwd hit stays a cwd fingerprint with a recorded "missing" list — that is
/// the legitimate mid-refactor shape, not the wrong-cwd signature.
fn resolve_code_fingerprint(cwd_root: &Path, workspace_root: &Path) -> ResolvedCodeFingerprint {
    let (files, missing) = hash_fingerprint_files_under(cwd_root);
    if !files.is_empty() {
        return ResolvedCodeFingerprint {
            files,
            missing,
            root: "cwd",
        };
    }
    let (files, missing) = hash_fingerprint_files_under(workspace_root);
    ResolvedCodeFingerprint {
        files,
        missing,
        root: "workspace",
    }
}

fn hash_fingerprint_files_under(root: &Path) -> (BTreeMap<&'static str, String>, Vec<&'static str>) {
    let mut files = BTreeMap::new();
    let mut missing = Vec::new();
    for name in CODE_FINGERPRINT_FILES {
        match fs::read(root.join(name)) {
            Ok(bytes) => {
                files.insert(*name, hash_bytes_16hex(&bytes));
            }
            Err(_) => missing.push(*name),
        }
    }
    (files, missing)
}

/// Some(true) = tracked files have uncommitted changes; None = git unavailable
/// or not a repository.
fn git_dirty() -> Option<bool> {
    let output = std::process::Command::new("git")
        .args(["status", "--porcelain", "-uno"])
        .output()
        .ok()?;
    if !output.status.success() {
        return None;
    }
    Some(!output.stdout.is_empty())
}

fn experiment_name(experiment: &Value) -> Fallible<String> {
    if let Some(name) = experiment.get("name").and_then(Value::as_str) {
        if !name.trim().is_empty() {
            return Ok(name.to_string());
        }
    }
    if let Some(name) = experiment.as_str() {
        if !name.trim().is_empty() {
            return Ok(name.to_string());
        }
    }
    Err(invalid_input(
        "run packet context.experiment must contain a non-empty name",
    ))
}

fn cell_i64_or(cell: &Value, field: &str, fallback: i64) -> Fallible<i64> {
    match cell.get(field) {
        Some(value) => value
            .as_i64()
            .ok_or_else(|| invalid_input(format!("context.run.cell.{field} must be an integer"))),
        None => Ok(fallback),
    }
}

fn required_cell_i64(cell: &Value, field: &str) -> Fallible<i64> {
    cell.get(field)
        .and_then(Value::as_i64)
        .ok_or_else(|| invalid_input(format!("context.run.cell.{field} is required for worker registration")))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample_packet_value() -> Value {
        json!({
            "schema": RUN_PACKET_SCHEMA,
            "run_hash": "abc123def4567890",
            "config_hash": "cfg123def4567890",
            "artifact_root": "results/runs",
            "artifact_dir": "results/runs/lorenz/lorenz_traj0/fern_h64_sig0p2_s42_abc1",
            "config": {
                "schema": "fern.cfg.v2",
                "execution": {"kind": "train"},
                "model": {"type": "fern", "knobs": {"dim_hidden": 128}},
                "task": {"seq_len": 96, "pred_len": 64, "channels": 3, "channel_independent": false},
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
                "train": {
                    "task_name": "long_term_forecast",
                    "epochs": 50,
                    "patience": 5,
                    "grace_period": 1,
                    "batch_size": 95,
                    "num_workers": 0
                },
                "optim": {
                    "type": "adamw",
                    "learning_rate": 0.0003,
                    "weight_decay": 0.0,
                    "max_grad_norm": 0.0,
                    "muon_momentum": 0.95,
                    "muon_beta2": 0.999,
                    "muon_ns_steps": 5
                },
                "scheduler": {
                    "type": "none",
                    "plateau_patience": 10,
                    "factor": 0.1,
                    "warmup_epochs": 5,
                    "eta_min": 0.00001
                },
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
            },
            "context": {
                "schema": "fern.run_context.v2",
                "experiment": {"name": "lorenz"},
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
                    "run_name": "fern_lorenz_sig0p2",
                    "dataset_base": "lorenz",
                    "dataset_id": "lorenz_traj0",
                    "cell": {
                        "source_name": "lorenz",
                        "noise_level": 0.2,
                        "trajectory_draw_ordinal": 0,
                        "noise_draw_ordinal": 0,
                        "trajectory_seed_base": 42,
                        "noise_seed_base": 1000,
                        "trajectory_draw_seed": 803958421u64,
                        "noise_draw_seed": 1305264456u64
                    },
                    "flags": {
                        "run_whiten": false,
                        "run_rewind": false,
                        "scale_noise_by_std": false,
                        "max_epochs_override": null
                    }
                },
                "catalog": {"config_name": "lorenz", "recipe_name": "lorenz_fern_huber", "recipe_hash": "recipe123"},
                "ablation": {"study": null, "name": null},
                "artifact": {
                    "run_collection": "lorenz_fern_huber",
                    "dataset": "lorenz",
                    "pred_len": 64,
                    "noise_sigma": 0.2,
                    "trajectory_draw_ordinal": 0,
                    "noise_draw_ordinal": 0,
                    "short_id_format": "recipe-draw"
                }
            },
            "identity": {"schema": "fern.run_identity.test.v1"}
        })
    }

    fn sample_packet() -> WorkerRunPacket {
        serde_json::from_value(sample_packet_value()).unwrap()
    }

    #[test]
    fn derives_worker_registration_and_request_from_run_packet() {
        let packet_value = sample_packet_value();
        let packet = sample_packet();
        let (registration, request) =
            build_worker_inputs(&packet, &packet_value, 17, Some("tgt123def4567890".to_string())).unwrap();

        assert_eq!(registration.run_hash, "abc123def4567890");
        assert_eq!(registration.target_hash.as_deref(), Some("tgt123def4567890"));
        assert_eq!(registration.template_id, Some(17));
        assert_eq!(registration.dataset, "lorenz");
        assert_eq!(registration.dataset_id, "lorenz_traj0");
        assert_eq!(registration.model_type, "fern");
        assert_eq!(registration.pred_len, 64);
        assert_eq!(registration.seq_len, 96);
        assert_eq!(registration.noise_sigma, 0.2);
        assert_eq!(registration.trajectory_seed_base, 42);
        assert_eq!(registration.noise_seed_base, 1000);
        assert_eq!(registration.n_features, Some(3));
        assert_eq!(registration.source, "template");
        let registration_json = serde_json::to_value(&registration).unwrap();
        assert!(registration_json.get("ablation_study").is_none());
        assert!(registration_json.get("ablation_name").is_none());

        assert_eq!(request["schema"], TRAINING_WORKER_REQUEST_SCHEMA);
        assert_eq!(request["experiment_name"], "lorenz");
        assert_eq!(request["protocol"]["noise_bank_seed"], 1000);
        assert_eq!(request["sweep_id"], 17);
        assert_eq!(request["model_name"], "fern");
        assert_eq!(request["config"]["schema"], "fern.cfg.v2");
        assert_eq!(request["cell"]["noise_seed_base"], 1000);
        assert_eq!(request["run_packet"]["run_hash"], "abc123def4567890");
        assert_eq!(request["run_packet"]["identity"]["schema"], "fern.run_identity.test.v1");
        assert!(request.get("ablation").is_none());
    }

    /// EnbPI piece 1's wire law: a non-ensemble packet emits NO top-level
    /// bootstrap key (fr_tr.py's `request.get("bootstrap")` stays None and
    /// today's behavior is untouched); a member packet lifts exactly the
    /// three fields the worker consumes from config.bootstrap — one source,
    /// never a hand-duplicated block. train_window_count is deliberately
    /// absent: the worker reports its actual count and aggregation demands
    /// cross-member unanimity, instead of Rust re-deriving windowing (#41).
    #[test]
    fn a_member_packet_lifts_its_bootstrap_block_and_a_plain_packet_emits_none() {
        let packet_value = sample_packet_value();
        let packet = sample_packet();
        let (_registration, request) = build_worker_inputs(&packet, &packet_value, 17, None).unwrap();
        assert!(
            request.get("bootstrap").is_none(),
            "plain packets carry no bootstrap key"
        );

        let mut member_value = sample_packet_value();
        member_value["config"]["bootstrap"] = serde_json::json!({
            "bootstrap_idx": 3,
            "bootstrap_seed": 7003,
            "block_size": 24,
            "ensemble_size": 15,
        });
        let member: WorkerRunPacket = serde_json::from_value(member_value.clone()).unwrap();
        let (_registration, request) = build_worker_inputs(&member, &member_value, 17, None).unwrap();
        assert_eq!(request["bootstrap"]["bootstrap_idx"], 3);
        assert_eq!(request["bootstrap"]["bootstrap_seed"], 7003);
        assert_eq!(request["bootstrap"]["block_size"], 24);
        assert!(
            request["bootstrap"].get("ensemble_size").is_none(),
            "the worker consumes exactly idx/seed/block; B stays config identity"
        );
        assert!(
            request["bootstrap"].get("train_window_count").is_none(),
            "N is worker-reported, never Rust-derived"
        );
        assert_eq!(
            request["config"]["bootstrap"]["ensemble_size"], 15,
            "identity remains in config"
        );
    }

    #[test]
    fn rejects_packet_without_cell_seed_provenance() {
        let mut packet = sample_packet_value();
        packet["context"]["run"]["cell"]
            .as_object_mut()
            .unwrap()
            .remove("noise_seed_base");

        let err =
            build_worker_inputs_from_packet_value(packet, 17, None).expect_err("missing seed provenance should fail");

        assert!(err.to_string().contains("noise_seed_base"));
    }

    /// Fresh scratch dir per test (same pattern as the integration tests):
    /// resolve_code_fingerprint takes explicit roots, so no cwd mutation.
    fn scratch_fingerprint_root(name: &str) -> PathBuf {
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let dir = std::env::temp_dir().join(format!("fern_cli_rs_fingerprint_{name}_{}_{nanos}", std::process::id()));
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn partial_cwd_fingerprint_stays_on_cwd_root_with_missing_list() {
        let cwd_root = scratch_fingerprint_root("partial_cwd");
        let workspace_root = scratch_fingerprint_root("full_workspace");
        fs::write(cwd_root.join("fr.py"), b"cwd fr").unwrap();
        for name in CODE_FINGERPRINT_FILES {
            fs::write(workspace_root.join(name), b"workspace copy").unwrap();
        }

        let fingerprint = resolve_code_fingerprint(&cwd_root, &workspace_root);

        // A partial cwd hit never falls back — missing files are the honest
        // mid-refactor shape, recorded against the directory Python imports from.
        assert_eq!(fingerprint.root, "cwd");
        assert_eq!(fingerprint.files.get("fr.py"), Some(&hash_bytes_16hex(b"cwd fr")));
        assert!(fingerprint.missing.contains(&"fr_tr.py"));

        fs::remove_dir_all(&cwd_root).unwrap();
        fs::remove_dir_all(&workspace_root).unwrap();
    }

    #[test]
    fn empty_cwd_fingerprint_falls_back_to_workspace_root() {
        let cwd_root = scratch_fingerprint_root("empty_cwd");
        let workspace_root = scratch_fingerprint_root("fallback_workspace");
        fs::write(workspace_root.join("fr_tr.py"), b"workspace fr_tr").unwrap();

        let fingerprint = resolve_code_fingerprint(&cwd_root, &workspace_root);

        assert_eq!(fingerprint.root, "workspace");
        assert_eq!(
            fingerprint.files.get("fr_tr.py"),
            Some(&hash_bytes_16hex(b"workspace fr_tr"))
        );
        assert!(fingerprint.missing.contains(&"fr.py"));

        fs::remove_dir_all(&cwd_root).unwrap();
        fs::remove_dir_all(&workspace_root).unwrap();
    }

    #[test]
    fn all_missing_under_both_roots_records_as_is() {
        let cwd_root = scratch_fingerprint_root("bare_cwd");
        let workspace_root = scratch_fingerprint_root("bare_workspace");

        let fingerprint = resolve_code_fingerprint(&cwd_root, &workspace_root);

        // Recorded as-is (never aborts a run); code_fingerprint_json warns
        // loudly on stderr for this shape.
        assert_eq!(fingerprint.root, "workspace");
        assert!(fingerprint.files.is_empty());
        assert_eq!(fingerprint.missing.len(), CODE_FINGERPRINT_FILES.len());

        fs::remove_dir_all(&cwd_root).unwrap();
        fs::remove_dir_all(&workspace_root).unwrap();
    }
}
