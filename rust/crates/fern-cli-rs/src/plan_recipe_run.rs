//! One recipe target -> run packet + worker request.
//!
//! This composes the C#-owned recipe selection receipt with Rust's existing
//! worker-packet and Python-worker request shapes. It never re-expands a
//! recipe or re-mints its recipe/target identities locally.

#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
use std::path::Path;

#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
use fern_run::model_architecture::model_architecture_identity;
use fern_run::resolved_cfg::DeviceKind;
#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
use serde::Serialize;
#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
use serde_json::Value;
#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
use serde_json::json;

#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
use crate::plan_run::{build_run_packet_from_context_value, experiment_protocol_identity};
use crate::presets::RecipeBlueprint;
#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
use crate::write_python_training_worker_inputs::build_worker_inputs_from_packet_value;
use crate::{Fallible, invalid_input};

#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
const PLAN_SCHEMA: &str = "fern.recipe_worker_request_plan.v1";

#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
const DRAW_SEED_PROTOCOL: &str = "serie_rs_splitmix64";

/// Compose the stable database dataset id from the C#-selected draw ordinals.
#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
fn dataset_id_for_draw_ordinals(source_name: &str, trajectory_draw_ordinal: u64, noise_draw_ordinal: u64) -> String {
    let mut dataset_id = format!("{source_name}_traj{trajectory_draw_ordinal}");
    if noise_draw_ordinal > 0 {
        dataset_id = format!("{dataset_id}_noise{noise_draw_ordinal}");
    }
    dataset_id
}

#[derive(Debug, Serialize)]
#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
pub(crate) struct PlanRecipeRunSummary {
    pub(crate) schema: &'static str,
    pub(crate) recipe: String,
    pub(crate) target_idx: usize,
    pub(crate) run_hash: String,
    pub(crate) target_hash: String,
    pub(crate) config_hash: String,
    pub(crate) artifact_dir: String,
    pub(crate) registration: Value,
    pub(crate) run_packet: Value,
    pub(crate) request: Value,
    /// Internal typed input for the authority/artifact cutover. The public plan
    /// already carries this inside `run_packet`; skipping it here avoids a
    /// second JSON copy while letting the runner register implementation intent
    /// before the first Attempt exists.
    #[serde(skip)]
    #[allow(dead_code)]
    pub(crate) resolved_config: fern_run::resolved_cfg::ResolvedConfigSpec,
}

#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
fn csharp_recipe_targets(
    recipe: &RecipeBlueprint,
    trajectory_seed_base: u64,
    noise_seed_base: u64,
    device: DeviceKind,
) -> Fallible<fern_run::csharp_experiment::RecipeTargetsReceipt> {
    let receipt = fern_run::csharp_experiment::resolve_recipe_run_targets_from_blueprint(
        recipe,
        device,
        trajectory_seed_base,
        noise_seed_base,
    )
    .map_err(invalid_input)?;
    if receipt.recipe != recipe.name() {
        return Err(invalid_input(format!(
            "C# recipe selection returned {:?} for requested frozen recipe {:?}",
            receipt.recipe,
            recipe.name()
        )));
    }
    if receipt.draw_seed_protocol != DRAW_SEED_PROTOCOL
        || receipt.trajectory_seed_base != trajectory_seed_base
        || receipt.noise_seed_base != noise_seed_base
    {
        return Err(invalid_input(
            "C# recipe selection receipt does not name the requested draw protocol and seed bases",
        ));
    }
    Ok(receipt)
}

/// Plan one cell from the frozen V2 object without consulting a preset or the
/// legacy recipe catalog. The output is deliberately the existing production
/// worker plan: V2 changes how configuration is authored, not the worker
/// protocol that has already produced Fern evidence.
#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
pub(crate) fn plan_compiled_experiment_cell(
    compiled: &fern_run::experiment_v2::CompiledExperimentV2,
    cell: &fern_run::experiment_v2::CompiledExperimentCellV2,
    campaign_id: i64,
    artifact_root: &Path,
    keep_forecast_origins: bool,
    run_note: Option<&str>,
) -> Fallible<PlanRecipeRunSummary> {
    compiled.validate().map_err(invalid_input)?;
    let source = fern_run::csharp_experiment::resolve_dataset_source_capability(&cell.source_name).map_err(invalid_input)?;
    let model_architecture = model_architecture_identity(&cell.config);
    let protocol = experiment_protocol_identity(
        &cell.config,
        source.draw_kind,
        cell.data_engine,
        &cell.source_name,
        cell.trajectory_draw.seed,
        cell.noise_draw.seed_base,
        cell.relative_dose,
        None,
        None,
    )?;
    let trajectory_ordinal = usize::try_from(cell.trajectory_draw.ordinal)
        .map_err(|_| invalid_input("trajectory draw ordinal does not fit this platform"))?;
    let noise_ordinal = usize::try_from(cell.noise_draw.ordinal)
        .map_err(|_| invalid_input("noise draw ordinal does not fit this platform"))?;
    let trajectory_dataset_ordinal =
        u64::try_from(trajectory_ordinal).map_err(|_| invalid_input("trajectory draw ordinal does not fit u64"))?;
    let noise_dataset_ordinal =
        u64::try_from(noise_ordinal).map_err(|_| invalid_input("noise draw ordinal does not fit u64"))?;
    let collection = format!("experiment_{}", compiled.recipe_hash());
    let context = json!({
        "schema": "fern.run_context.v2",
        "experiment": {
            "schema": "fern.compositional_experiment_ref.v2",
            "name": compiled.manifest_sha256(),
            "source_name": cell.source_name,
            "source_is_stochastic": cell.source_is_stochastic,
            "draw_design": cell.draw_design,
            "data_engine": cell.data_engine,
        },
        "model_architecture": model_architecture,
        "protocol": protocol,
        "run": {
            "seed": cell.model_seed,
            "run_name": format!("{}_v2_{}", cell.config.model.model_type, cell.target_idx),
            "dataset_base": cell.source_name,
            "dataset_id": dataset_id_for_draw_ordinals(&cell.source_name, trajectory_dataset_ordinal, noise_dataset_ordinal),
            "cell": {
                "source_name": cell.source_name,
                "noise_level": cell.noise_dose,
                "trajectory_draw_ordinal": cell.trajectory_draw.ordinal,
                "noise_draw_ordinal": cell.noise_draw.ordinal,
                "trajectory_seed_base": cell.trajectory_draw.seed_base,
                "noise_seed_base": cell.noise_draw.seed_base,
                "trajectory_draw_seed": cell.trajectory_draw.seed,
                "noise_draw_seed": cell.noise_draw.seed,
                "draw_seed_protocol": DRAW_SEED_PROTOCOL,
            },
            "flags": {
                "run_whiten": cell.run_whiten,
                "run_rewind": cell.run_rewind,
                "scale_noise_by_std": cell.relative_dose,
                "max_epochs_override": null,
            },
        },
        "catalog": {
            "config_name": null,
            "recipe_name": collection,
            "recipe_hash": compiled.recipe_hash(),
        },
        "ablation": {"study": null, "name": null},
        "run_note": run_note,
        "artifact": {
            "run_collection": collection,
            "dataset": cell.source_name,
            "pred_len": cell.config.task.pred_len,
            "noise_sigma": cell.noise_dose,
            "trajectory_draw_ordinal": cell.trajectory_draw.ordinal,
            "noise_draw_ordinal": cell.noise_draw.ordinal,
            "short_id_format": "recipe-draw",
        },
    });
    let mut packet =
        build_run_packet_from_context_value(cell.config.clone(), context, artifact_root.to_path_buf(), None)?;
    if packet.config_hash != cell.config_hash {
        return Err(invalid_input(format!(
            "compiled cell {} config hash changed while building its worker packet: expected {}, got {}",
            cell.target_idx, cell.config_hash, packet.config_hash
        )));
    }
    packet.retain_forecast_origins = keep_forecast_origins;
    let packet_value = serde_json::to_value(&packet)?;
    let (registration, request) =
        build_worker_inputs_from_packet_value(packet_value.clone(), campaign_id, Some(cell.target_hash.clone()))?;
    Ok(PlanRecipeRunSummary {
        schema: PLAN_SCHEMA,
        recipe: collection,
        target_idx: usize::try_from(cell.target_idx)
            .map_err(|_| invalid_input("compiled target index does not fit this platform"))?,
        run_hash: packet.run_hash,
        target_hash: cell.target_hash.clone(),
        config_hash: packet.config_hash,
        artifact_dir: packet.artifact_dir,
        registration: serde_json::to_value(registration)?,
        run_packet: packet_value,
        request,
        resolved_config: packet.config,
    })
}

pub(crate) fn compiled_experiment_target_registrations(
    compiled: &fern_run::experiment_v2::CompiledExperimentV2,
) -> Fallible<Vec<fern_run::db::RecipeTargetRegistration>> {
    compiled.validate().map_err(invalid_input)?;
    let recipe_hash = compiled.recipe_hash();
    compiled
        .cells()
        .iter()
        .map(|cell| {
            Ok(fern_run::db::RecipeTargetRegistration {
                recipe_hash: recipe_hash.clone(),
                target_hash: cell.target_hash.clone(),
                target_idx: i64::try_from(cell.target_idx)
                    .map_err(|_| invalid_input("compiled target index is too large for recipe_targets"))?,
                target_json: serde_json::to_string(cell)?,
                dataset: cell.source_name.clone(),
                seq_len: i64::try_from(cell.config.task.seq_len)
                    .map_err(|_| invalid_input("compiled seq_len is too large for recipe_targets"))?,
                pred_len: i64::try_from(cell.config.task.pred_len)
                    .map_err(|_| invalid_input("compiled pred_len is too large for recipe_targets"))?,
                noise_sigma: cell.noise_dose,
                trajectory_draw_ordinal: i64::try_from(cell.trajectory_draw.ordinal)
                    .map_err(|_| invalid_input("compiled trajectory ordinal is too large for recipe_targets"))?,
                noise_draw_ordinal: i64::try_from(cell.noise_draw.ordinal)
                    .map_err(|_| invalid_input("compiled noise ordinal is too large for recipe_targets"))?,
                seed: cell.model_seed,
                config_hash: cell.config_hash.clone(),
            })
        })
        .collect()
}

/// Project one C# recipe-selection receipt into the legacy registry row.
/// The SQL adapter persists the child's identity without recomputing it.
#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
fn recipe_registration(
    receipt: &fern_run::csharp_experiment::RecipeTargetsReceipt,
) -> Fallible<fern_run::db::RecipeRegistration> {
    let identity = &receipt.recipe_identity;
    let recipe = &identity.recipe;
    let preimage = serde_json::to_value(identity)?;
    Ok(fern_run::db::RecipeRegistration {
        recipe_hash: receipt.recipe_hash.clone(),
        schema: identity.schema.clone(),
        name: recipe.name.clone(),
        family: recipe.family.clone(),
        model: recipe.model.clone(),
        eval: recipe.eval.clone(),
        loader: recipe.loader.clone(),
        train: recipe.train.clone(),
        seq_len: recipe.seq_len,
        pred_lens_json: serde_json::to_string(&recipe.pred_lens)?,
        noise_levels_json: serde_json::to_string(&recipe.noise_levels)?,
        seeds_json: serde_json::to_string(&recipe.seeds)?,
        n_trajectory_draws: recipe.n_trajectory_draws,
        n_noise_draws: recipe.n_noise_draws,
        run_whiten: recipe.run_whiten,
        run_rewind: recipe.run_rewind,
        scale_noise_by_std: recipe.scale_noise_by_std,
        recipe_json: serde_json::to_string(&preimage)?,
    })
}

/// Materialize one C# receipt as legacy `recipes` and `recipe_targets` rows.
/// `runtime.device` is already part of the child's config and target hash, so
/// a CPU execution cannot accidentally inherit the CUDA identity.
pub(crate) fn recipe_provenance_for_device(
    recipe: &RecipeBlueprint,
    trajectory_seed_base: u64,
    noise_seed_base: u64,
    device: DeviceKind,
) -> Fallible<(
    fern_run::db::RecipeRegistration,
    Vec<fern_run::db::RecipeTargetRegistration>,
)> {
    let receipt = csharp_recipe_targets(recipe, trajectory_seed_base, noise_seed_base, device)?;
    let recipe_row = recipe_registration(&receipt)?;
    let mut rows = Vec::with_capacity(receipt.targets.len());
    for row in receipt.targets {
        if !receipt.configs.contains_key(&row.config_hash) {
            return Err(invalid_input(format!(
                "C# target {} names absent config {}",
                row.target.target_idx, row.config_hash
            )));
        }
        let target = row.target;
        rows.push(fern_run::db::RecipeTargetRegistration {
            recipe_hash: receipt.recipe_hash.clone(),
            target_hash: row.target_hash,
            target_idx: i64::try_from(target.target_idx)
                .map_err(|_| invalid_input("target_idx is too large for recipe_targets"))?,
            target_json: serde_json::to_string(&row.target_preimage)?,
            dataset: target.source_name.clone(),
            seq_len: target.seq_len,
            pred_len: target.pred_len,
            // SQLite REAL is physically a double; the experiment's authority
            // remains the f32 recipe/target value and widens only at this
            // legacy storage adapter.
            noise_sigma: f64::from(target.noise_sigma),
            trajectory_draw_ordinal: i64::try_from(target.trajectory_draw_ordinal)
                .map_err(|_| invalid_input("trajectory_draw_ordinal is too large for recipe_targets"))?,
            noise_draw_ordinal: i64::try_from(target.noise_draw_ordinal)
                .map_err(|_| invalid_input("noise_draw_ordinal is too large for recipe_targets"))?,
            seed: target.model_seed,
            config_hash: row.config_hash,
        });
    }
    Ok((recipe_row, rows))
}

#[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
pub(crate) fn recipe_target_registrations_for_device(
    recipe: &RecipeBlueprint,
    trajectory_seed_base: u64,
    noise_seed_base: u64,
    device: DeviceKind,
) -> Fallible<Vec<fern_run::db::RecipeTargetRegistration>> {
    recipe_provenance_for_device(recipe, trajectory_seed_base, noise_seed_base, device).map(|(_, rows)| rows)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    fn recipe_by_name(name: &str) -> Fallible<RecipeBlueprint> {
        fern_recipes::recipe_catalog_builds()
            .into_iter()
            .find(|recipe| recipe.name() == name)
            .ok_or_else(|| invalid_input(format!("missing catalog recipe {name}")))
    }

}
