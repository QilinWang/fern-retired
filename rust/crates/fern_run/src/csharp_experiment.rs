//! Typed local-process boundary to the C# Experiment engine.
//!
//! Rust callers project already-compiled cells into this closed wire; C# owns
//! cell membership, execution lowering, subject and pair identity, and Stage
//! Plan construction. This adapter performs no scientific interpretation.

use std::collections::{BTreeMap, BTreeSet};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

use fern_experiment_types::{ContentAddressedSubjectV1, Sha256Digest, StagePlanV1};
use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::experiment_v2::{CompiledAblationV2, CompiledExperimentV2};

const CSHARP_EXPERIMENT_PROCESS_DEADLINE: Duration = Duration::from_secs(30);
const CSHARP_EXPERIMENT_PARENT_BOUNDARY_DEADLINE: Duration = Duration::from_secs(31);
const CSHARP_EXPERIMENT_PROCESS_POLL_INTERVAL: Duration = Duration::from_millis(25);

#[derive(Debug, Clone, Serialize)]
pub struct CompiledCellProjection {
    pub cell_ordinal: i64,
    pub target_hash: String,
    pub config_hash: String,
    pub coordinates: Value,
    pub config: Value,
}

#[derive(Debug, Clone, Serialize)]
pub struct CompiledExperimentProjection {
    pub manifest_sha256: String,
    pub execution_profile_id: String,
    pub execution_profile_revision: String,
    pub cells: Vec<CompiledCellProjection>,
}

#[derive(Debug, Clone, Serialize)]
pub struct PythonImplementationProjection {
    pub runner: String,
    pub entrypoint: String,
    pub implementation_digest: String,
}

#[derive(Debug, Clone, Serialize)]
pub struct CellRequestProjection {
    pub cell_ordinal: i64,
    pub training_worker_request_json: String,
}

#[derive(Debug, Clone, Serialize)]
pub struct LowerCellBatchRequest {
    pub schema: String,
    pub compiled: CompiledExperimentProjection,
    pub template_json: String,
    pub python: PythonImplementationProjection,
    pub requests: Vec<CellRequestProjection>,
}

#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct DeclaredStageInputArtifact {
    pub port: String,
    pub role: String,
    pub schema_id: String,
    pub content_digest: Sha256Digest,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct LoweredCell {
    pub cell_ordinal: i64,
    pub subject: ContentAddressedSubjectV1,
    pub stage_plan_instance_id: Sha256Digest,
    pub stage_plan: StagePlanV1,
    pub inputs: BTreeMap<String, DeclaredStageInputArtifact>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct LowerCellBatchReceipt {
    pub schema: String,
    pub lowered: Vec<LoweredCell>,
}

#[derive(Debug, Clone, Serialize)]
pub struct CompiledAblationProjection {
    pub resolution: Value,
    pub reported_template_digest: String,
    pub control: CompiledExperimentProjection,
    pub treatment: CompiledExperimentProjection,
}

#[derive(Debug, Clone, Serialize)]
pub struct PairAblationRequest {
    pub schema: String,
    pub compiled: CompiledAblationProjection,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct PairedUnitProjection {
    pub paired_unit_id: String,
    pub coordinates: Value,
    pub control_cell_ordinal: i64,
    pub treatment_cell_ordinal: i64,
    pub target_hash: String,
    pub config_hash: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct PairAblationReceipt {
    pub schema: String,
    pub resolution_digest: String,
    pub units: Vec<PairedUnitProjection>,
}

#[derive(Debug, Clone, Serialize)]
pub struct PairRequestProjection {
    pub paired_unit_id: String,
    pub control_training_worker_request_json: String,
    pub treatment_training_worker_request_json: String,
}

#[derive(Debug, Clone, Serialize)]
pub struct LowerPairBatchRequest {
    pub schema: String,
    pub compiled: CompiledAblationProjection,
    pub template_json: String,
    pub python: PythonImplementationProjection,
    pub requests: Vec<PairRequestProjection>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct LoweredPair {
    pub cell_ordinal: i64,
    pub paired_unit_id: String,
    pub target_hash: String,
    pub config_hash: String,
    pub subject: ContentAddressedSubjectV1,
    pub stage_plan: StagePlanV1,
    pub inputs: BTreeMap<String, DeclaredStageInputArtifact>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct LowerPairBatchReceipt {
    pub schema: String,
    pub lowered: Vec<LoweredPair>,
}

pub fn lower_cells(request: &LowerCellBatchRequest) -> Result<LowerCellBatchReceipt, String> {
    run_json("lower-cell-batch", request)
}

pub fn pair_ablation(request: &PairAblationRequest) -> Result<PairAblationReceipt, String> {
    run_json("pair-ablation", request)
}

pub fn lower_pairs(request: &LowerPairBatchRequest) -> Result<LowerPairBatchReceipt, String> {
    run_json("lower-pair-batch", request)
}

/// The receipt for one C#-owned V1 recipe selection.  The child returns the
/// complete expanded coordinate set, its C#-minted identities, and the exact
/// resolved configurations.  Rust may build a worker packet from this data,
/// but must not re-expand or re-hash the selection locally.
#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RecipeTargetsReceipt {
    pub schema: String,
    pub recipe: String,
    pub recipe_hash: String,
    pub recipe_identity: fern_recipes::LegacyRecipeIdentityV1,
    pub target_count: usize,
    pub draw_seed_protocol: String,
    pub trajectory_seed_base: u64,
    pub noise_seed_base: u64,
    pub targets: Vec<RecipeTargetReceipt>,
    pub configs: BTreeMap<String, crate::resolved_cfg::ResolvedConfigSpec>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RecipeTargetReceipt {
    pub target_hash: String,
    pub config_hash: String,
    pub target_preimage: Value,
    #[serde(flatten)]
    pub target: RecipeTarget,
}

/// One selected designed cell.  These fields deliberately mirror the public
/// C# report, not the retired Rust planner's internal type.
#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RecipeTarget {
    pub target_idx: u64,
    pub recipe: String,
    pub family: String,
    pub source_name: String,
    pub source_is_stochastic: bool,
    pub draw_design: String,
    pub data_engine: fern_recipes::DataEngine,
    pub seq_len: i64,
    pub pred_len: i64,
    pub noise_sigma: f32,
    pub model_seed: i64,
    pub trajectory_draw_ordinal: u64,
    pub noise_draw_ordinal: u64,
    pub trajectory_seed_base: u64,
    pub noise_seed_base: u64,
    pub trajectory_draw_seed: u64,
    pub noise_draw_seed: u64,
    pub model: String,
    pub eval: String,
    pub loader: String,
    pub train: String,
    pub run_whiten: bool,
    pub run_rewind: bool,
    pub scale_noise_by_std: bool,
    pub split_treatment_scheme: Option<String>,
    pub checkpoint_reuse_policy: Option<String>,
    pub cell_tag: String,
    pub bootstrap: Option<RecipeTargetBootstrapMember>,
}

#[derive(Debug, Clone, Copy, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RecipeTargetBootstrapMember {
    pub bootstrap_idx: u64,
    pub bootstrap_seed: u64,
    pub block_size: u64,
    pub ensemble_size: u64,
}

#[derive(Serialize)]
struct RecipeTargetsRequest<'a> {
    #[serde(skip_serializing_if = "Option::is_none")]
    recipe: Option<&'a str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    identity: Option<&'a fern_recipes::LegacyRecipeIdentityV1>,
    #[serde(skip_serializing_if = "Option::is_none")]
    blueprint: Option<&'a Value>,
    device: &'static str,
    trajectory_seed_base: u64,
    noise_seed_base: u64,
    format: &'static str,
}

fn recipe_targets_request<'a>(
    recipe: Option<&'a str>,
    identity: Option<&'a fern_recipes::LegacyRecipeIdentityV1>,
    blueprint: Option<&'a Value>,
    device: crate::resolved_cfg::DeviceKind,
    trajectory_seed_base: u64,
    noise_seed_base: u64,
) -> RecipeTargetsRequest<'a> {
    RecipeTargetsRequest {
        recipe,
        identity,
        blueprint,
        device: match device {
            crate::resolved_cfg::DeviceKind::Cpu => "cpu",
            crate::resolved_cfg::DeviceKind::Cuda => "cuda",
        },
        trajectory_seed_base,
        noise_seed_base,
        format: "json",
    }
}

/// Resolve one installed catalog recipe through C#.  This is the public CLI's
/// narrow selection boundary: it sends a name and execution inputs, then
/// prints the resulting bytes unchanged.
pub fn list_recipe_run_targets_bytes(
    recipe: &str,
    device: crate::resolved_cfg::DeviceKind,
    trajectory_seed_base: u64,
    noise_seed_base: u64,
    format: &'static str,
) -> Result<Vec<u8>, String> {
    let mut request = recipe_targets_request(Some(recipe), None, None, device, trajectory_seed_base, noise_seed_base);
    request.format = format;
    run_bytes("list-recipe-run-targets", &request)
}

fn checked_recipe_targets_receipt(receipt: RecipeTargetsReceipt) -> Result<RecipeTargetsReceipt, String> {
    if receipt.schema != "fern.recipe_run_targets.v1" {
        return Err(format!(
            "list-recipe-run-targets returned schema {:?}, expected fern.recipe_run_targets.v1",
            receipt.schema
        ));
    }
    if receipt.target_count != receipt.targets.len() {
        return Err(format!(
            "list-recipe-run-targets reported target_count {} but returned {} targets",
            receipt.target_count,
            receipt.targets.len()
        ));
    }
    Ok(receipt)
}

/// Resolve an exact frozen or externally-declared recipe through C#.  The
/// identity is a typed immutable selection, so the child, rather than Rust,
/// owns expansion, configuration selection, and the two hashes.
pub fn resolve_recipe_run_targets(
    identity: &fern_recipes::LegacyRecipeIdentityV1,
    device: crate::resolved_cfg::DeviceKind,
    trajectory_seed_base: u64,
    noise_seed_base: u64,
) -> Result<RecipeTargetsReceipt, String> {
    let request = recipe_targets_request(
        None,
        Some(identity),
        None,
        device,
        trajectory_seed_base,
        noise_seed_base,
    );
    let receipt: RecipeTargetsReceipt = checked_recipe_targets_receipt(run_json("list-recipe-run-targets", &request)?)?;
    if receipt.recipe_identity != *identity {
        return Err("list-recipe-run-targets changed the requested frozen recipe identity".to_string());
    }
    Ok(receipt)
}

/// Resolve one existing Rust worker-packet blueprint through the C# identity
/// owner. Rust transports current fields but never derives its legacy name,
/// optional-field wire, or recipe hash locally.
pub fn resolve_recipe_run_targets_from_blueprint(
    recipe: &fern_recipes::RecipeBlueprint,
    device: crate::resolved_cfg::DeviceKind,
    trajectory_seed_base: u64,
    noise_seed_base: u64,
) -> Result<RecipeTargetsReceipt, String> {
    let blueprint = recipe_blueprint_transport_value(recipe);
    let request = recipe_targets_request(
        None,
        None,
        Some(&blueprint),
        device,
        trajectory_seed_base,
        noise_seed_base,
    );
    let receipt: RecipeTargetsReceipt = checked_recipe_targets_receipt(run_json("list-recipe-run-targets", &request)?)?;
    if receipt.recipe != recipe.name() {
        return Err(format!(
            "list-recipe-run-targets changed the transported recipe name {:?} into {:?}",
            recipe.name(),
            receipt.recipe
        ));
    }
    Ok(receipt)
}

/// The C# pre-dispatch compatibility receipt. The only successful projection
/// is the ordered handler-name list a separately installed scheduler lacks;
/// Rust does not reconstruct the comparison from capability rows.
#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RecipeHandlerCompatibilityReceipt {
    pub schema: String,
    pub recipe_name: String,
    pub recipe_hash: String,
    pub missing_handler_names: Vec<String>,
}

#[derive(Serialize)]
struct RecipeHandlerCompatibilityRequest<'a> {
    schema: &'static str,
    identity: &'a fern_recipes::LegacyRecipeIdentityV1,
    capabilities: &'a fern_recipes::RecipeHandlerCapabilitiesV1,
}

/// Ask the C# Experiment authority whether an admitted frozen identity is
/// executable by the installed scheduler receipt. It is a pure admission
/// preflight: a malformed receipt or an unknown handler creates no plan and
/// causes no Registry mutation.
pub fn missing_recipe_handler_names_via_csharp(
    identity: &fern_recipes::LegacyRecipeIdentityV1,
    capabilities: &fern_recipes::RecipeHandlerCapabilitiesV1,
) -> Result<Vec<String>, String> {
    let receipt: RecipeHandlerCompatibilityReceipt = run_json(
        "check-recipe-handler-compatibility",
        &RecipeHandlerCompatibilityRequest {
            schema: "fern.recipe_handler_compatibility.request.v1",
            identity,
            capabilities,
        },
    )?;
    if receipt.schema != "fern.recipe_handler_compatibility.v1" {
        return Err(format!(
            "check-recipe-handler-compatibility returned schema {:?}, expected fern.recipe_handler_compatibility.v1",
            receipt.schema
        ));
    }
    if receipt.recipe_name != identity.recipe.name {
        return Err(format!(
            "check-recipe-handler-compatibility changed admitted recipe name {:?} into {:?}",
            identity.recipe.name, receipt.recipe_name
        ));
    }
    if receipt.recipe_hash.len() != 16
        || !receipt
            .recipe_hash
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (byte.is_ascii_lowercase() && byte.is_ascii_hexdigit()))
    {
        return Err(format!(
            "check-recipe-handler-compatibility returned invalid recipe_hash {:?}",
            receipt.recipe_hash
        ));
    }
    Ok(receipt.missing_handler_names)
}

/// One immutable arm projection for the C# reader-warning authority. Warnings
/// depend only on these two already-admitted parameter records and the arm's
/// public label; Rust transports the values without interpreting terms,
/// weights, or training duration.
#[derive(Serialize)]
struct ResolutionWarningArmRequest<'a> {
    name: &'a str,
    evaluator_parameters: &'a Value,
    trainer_parameters: &'a Value,
}

#[derive(Serialize)]
struct ResolutionWarningsRequest<'a> {
    schema: &'static str,
    arms: Vec<ResolutionWarningArmRequest<'a>>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
struct ResolutionWarningsReceipt {
    schema: String,
    arm_names: Vec<String>,
    warnings: Vec<String>,
}

/// Resolve reader-facing warnings through C# from complete already-admitted
/// arm parameter records. This pure route creates no Plan, Registry, Stage,
/// Scheduler, Site, artifact, worker, or durable refusal state.
pub fn resolution_warnings_via_csharp(
    arms: &[(&str, &fern_recipes::ResolvedExperimentArmV2)],
) -> Result<Vec<String>, String> {
    let expected_arm_names = arms.iter().map(|(name, _)| (*name).to_string()).collect::<Vec<_>>();
    let receipt: ResolutionWarningsReceipt = run_json(
        "resolve-experiment-warnings",
        &ResolutionWarningsRequest {
            schema: "fern.experiment_resolution_warnings.request.v1",
            arms: arms
                .iter()
                .map(|(name, arm)| ResolutionWarningArmRequest {
                    name,
                    evaluator_parameters: &arm.manifest.components.recipe.evaluator.parameters,
                    trainer_parameters: &arm.manifest.components.recipe.trainer.parameters,
                })
                .collect(),
        },
    )?;
    if receipt.schema != "fern.experiment_resolution_warnings.v1" {
        return Err(format!(
            "resolve-experiment-warnings returned schema {:?}, expected fern.experiment_resolution_warnings.v1",
            receipt.schema
        ));
    }
    if receipt.arm_names != expected_arm_names {
        return Err(format!(
            "resolve-experiment-warnings changed requested arm names {:?} into {:?}",
            expected_arm_names, receipt.arm_names
        ));
    }
    Ok(receipt.warnings)
}

fn recipe_blueprint_transport_value(recipe: &fern_recipes::RecipeBlueprint) -> Value {
    serde_json::json!({
        "family": recipe.family.value(),
        "cells": recipe.family.cells(),
        "model": recipe.model.value(),
        "eval": recipe.eval.value(),
        "loader": recipe.loader.value(),
        "train": recipe.train.value(),
        "plan": recipe.plan.value(),
        "seq_len": recipe.plan.seq_len(),
        "pred_lens": recipe.plan.pred_lens(),
        "noise_levels": recipe.plan.noise_levels(),
        "seeds": recipe.seeds,
        "n_trajectory_draws": recipe.n_trajectory_draws,
        "n_noise_draws": recipe.n_noise_draws,
        "run_whiten": recipe.run_whiten,
        "run_rewind": recipe.run_rewind,
        "scale_noise_by_std": recipe.scale_noise_by_std,
        "single_clean_anchor": recipe.single_clean_anchor,
        "split_treatment_scheme": recipe.split_treatment_scheme.map(|value| value.value()),
        "noise_kind": recipe.noise_kind.value(),
        "noise_ar1_phi": recipe.noise_ar1_phi,
        "checkpoint_reuse_policy": recipe.checkpoint_reuse_policy.map(|value| value.value()),
        "bootstrap": recipe.bootstrap.as_ref().map(|value| serde_json::json!({
            "ensemble_size": value.ensemble_size,
            "block_size": value.block_size,
            "resample_seed_base": value.resample_seed_base,
        })),
        "fern_architecture": recipe.fern_architecture.map(|value| value.value()),
        "source_std": recipe.source_std,
        "fern_density": recipe.fern_density.as_ref().map(|value| serde_json::json!({
            "variant": value.variant().value(),
            "component_count": value.component_count(),
            "sparse_active_component_count": value.sparse_active_component_count(),
        })),
        "fern_radial_head": recipe.fern_radial_head,
    })
}

/// Materialize the existing Rust worker-packet type from an identity a C#
/// receipt has already admitted. This is a bounded local transport adapter:
/// its enum parsing protects the packet boundary but it does not validate,
/// normalize, name, or hash a recipe.
pub fn recipe_blueprint_from_csharp_identity(
    identity: &fern_recipes::LegacyRecipeIdentityV1,
) -> Result<fern_recipes::RecipeBlueprint, String> {
    if identity.schema != fern_recipes::LEGACY_RECIPE_IDENTITY_SCHEMA {
        return Err(format!(
            "C# recipe identity schema {:?} is not {}",
            identity.schema,
            fern_recipes::LEGACY_RECIPE_IDENTITY_SCHEMA
        ));
    }
    let values = &identity.recipe;
    let model = fern_recipes::RecipeModelName::parse(&values.model)?;
    let eval = fern_recipes::RecipeEvalName::parse(&values.eval)?;
    let loader = fern_recipes::RecipeLoaderName::parse(&values.loader)?;
    let train = fern_recipes::RecipeTrainName::parse(&values.train)?;
    let family = fern_recipes::RecipeSourceSet::new(values.family.clone(), values.cells.clone())?;
    let plan = fern_recipes::RecipeGridSpec::new(
        values.plan.clone(),
        values.seq_len,
        values.pred_lens.clone(),
        values.noise_levels.clone(),
    )?;
    let blueprint = fern_recipes::RecipeBlueprint {
        family,
        model,
        eval,
        loader,
        train,
        plan,
        seeds: values.seeds.clone(),
        n_trajectory_draws: values.n_trajectory_draws,
        n_noise_draws: values.n_noise_draws,
        run_whiten: values.run_whiten,
        run_rewind: values.run_rewind,
        scale_noise_by_std: values.scale_noise_by_std,
        single_clean_anchor: values.single_clean_anchor.unwrap_or(false),
        noise_kind: values.noise_kind.unwrap_or(fern_experiment_types::NoiseKind::Gaussian),
        noise_ar1_phi: values.noise_ar1_phi.unwrap_or(0.0),
        split_treatment_scheme: values.split_treatment_scheme,
        checkpoint_reuse_policy: values.checkpoint_reuse_policy,
        fern_architecture: values
            .fern_architecture
            .as_deref()
            .map(fern_recipes::FernArchitectureVariant::parse)
            .transpose()?,
        bootstrap: values.bootstrap.map(|value| fern_recipes::RecipeBootstrapSpec {
            ensemble_size: value.ensemble_size,
            block_size: value.block_size,
            resample_seed_base: value.resample_seed_base,
        }),
        source_std: values.source_std,
        fern_density: values
            .fern_density
            .as_ref()
            .map(|density| {
                let variant = fern_experiment_types::FernDensityVariantV1::parse(&density.variant)
                    .map_err(|error| error.to_string())?;
                fern_recipes::RecipeFernDensitySpec::new(
                    variant,
                    density.component_count,
                    density.sparse_active_component_count,
                )
            })
            .transpose()?,
        fern_radial_head: values.fern_radial_head.unwrap_or(false),
    };
    if blueprint.name() != values.name {
        return Err(format!(
            "C# admitted identity name {:?} disagrees with the local worker-packet name {:?}",
            values.name,
            blueprint.name()
        ));
    }
    Ok(blueprint)
}

#[derive(Serialize)]
struct DatasetSourceCapabilitiesRequest<'a> {
    schema: &'static str,
    source_names: &'a [&'a str],
}

/// One C#-catalogued source capability. Rust uses the direct projections in
/// this receipt for V2 cells; it does not derive draw behaviour from a local
/// source registration table.
#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct DatasetSourceCapabilityReceipt {
    pub source_name: String,
    pub draw_kind: fern_recipes::SourceDrawKind,
    pub data_engine: fern_recipes::DataEngine,
    pub noise_seed_offset: Option<u64>,
    pub source_is_stochastic: bool,
    pub draw_design: String,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct DatasetSourceCapabilitiesReceipt {
    schema: String,
    requested_source_names: Vec<String>,
    sources: Vec<DatasetSourceCapabilityReceipt>,
}

/// Resolve the C# installed source catalog for one ordered selection. Passing
/// no names is the explicit complete sorted capability publication used while
/// building an installation identity; a nonempty selection is receipt-bound
/// to its exact caller order.
pub fn resolve_dataset_source_capabilities(
    source_names: &[&str],
) -> Result<Vec<DatasetSourceCapabilityReceipt>, String> {
    let receipt: DatasetSourceCapabilitiesReceipt = run_json(
        "resolve-dataset-source-capabilities",
        &DatasetSourceCapabilitiesRequest {
            schema: "fern.dataset_source_capability_resolution.request.v1",
            source_names,
        },
    )?;
    if receipt.schema != "fern.dataset_source_capability_resolution.v1" {
        return Err(format!(
            "resolve-dataset-source-capabilities returned schema {:?}, expected fern.dataset_source_capability_resolution.v1",
            receipt.schema
        ));
    }
    let expected_names = source_names.iter().map(|name| (*name).to_string()).collect::<Vec<_>>();
    if receipt.requested_source_names != expected_names {
        return Err("resolve-dataset-source-capabilities changed the requested source-name selection".to_string());
    }
    if source_names.is_empty() {
        if receipt.sources.is_empty() {
            return Err("resolve-dataset-source-capabilities returned an empty installed catalog".to_string());
        }
        let names = receipt
            .sources
            .iter()
            .map(|source| source.source_name.as_str())
            .collect::<Vec<_>>();
        if names.windows(2).any(|pair| pair[0] >= pair[1]) {
            return Err(
                "resolve-dataset-source-capabilities did not return a strictly sorted unique complete catalog"
                    .to_string(),
            );
        }
    } else {
        let received_names = receipt
            .sources
            .iter()
            .map(|source| source.source_name.as_str())
            .collect::<Vec<_>>();
        if received_names != source_names {
            return Err("resolve-dataset-source-capabilities changed the selected source-name order".to_string());
        }
    }
    for source in &receipt.sources {
        if !matches!(source.draw_design.as_str(), "joint" | "noise_only") {
            return Err(format!(
                "resolve-dataset-source-capabilities returned invalid draw_design {:?} for {:?}",
                source.draw_design, source.source_name
            ));
        }
    }
    Ok(receipt.sources)
}

/// Resolve exactly one source row and refuse any child that changes the
/// caller's selection cardinality.
pub fn resolve_dataset_source_capability(source_name: &str) -> Result<DatasetSourceCapabilityReceipt, String> {
    let mut sources = resolve_dataset_source_capabilities(&[source_name])?;
    match sources.pop() {
        Some(source) if sources.is_empty() => Ok(source),
        _ => Err("resolve-dataset-source-capabilities returned the wrong number of selected sources".to_string()),
    }
}

#[derive(Serialize)]
struct ConfigIdentityRequest<'a> {
    stored_config: &'a str,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ConfigIdentityReceipt {
    schema: String,
    stored_identity_scheme: u32,
    stored_bytes_sha256_16hex: String,
    current_identity_scheme: u32,
    current_config_hash: String,
    current_preimage: String,
}

/// Send one stored UTF-8 configuration to the C# identity owner.  This
/// adapter intentionally returns its response bytes unchanged: C# owns both
/// the stored-byte digest and the current typed preimage/hash reading.
pub fn show_config_identity_bytes(stored: &[u8]) -> Result<Vec<u8>, String> {
    let stored_config = std::str::from_utf8(stored)
        .map_err(|error| format!("stored config must be UTF-8 for the C# Experiment boundary: {error}"))?;
    let response = run_bytes("show-config-identity", &ConfigIdentityRequest { stored_config })?;
    let receipt: ConfigIdentityReceipt = serde_json::from_slice(&response)
        .map_err(|error| format!("show-config-identity receipt is not typed JSON: {error}"))?;
    if receipt.schema != "fern.config_identity_reading.v1" {
        return Err(format!(
            "show-config-identity returned schema {:?}, expected fern.config_identity_reading.v1",
            receipt.schema
        ));
    }
    let _ = receipt.stored_identity_scheme;
    if receipt.current_identity_scheme != 2
        || receipt.stored_bytes_sha256_16hex.is_empty()
        || receipt.current_config_hash.is_empty()
        || receipt.current_preimage.is_empty()
    {
        return Err("show-config-identity returned an incomplete identity reading".to_string());
    }
    Ok(response)
}

#[derive(Serialize)]
struct ConfigOverlayRequest<'a> {
    stored_config: &'a str,
    #[serde(skip_serializing_if = "Option::is_none")]
    loss_json: Option<&'a str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    fern_patch_json: Option<&'a str>,
}

/// Apply loss and Fern-patch overlays in the C# resolved-config owner. Rust
/// reads each chosen document once and forwards its original UTF-8 spelling;
/// it must not parse, restamp, synchronize model knobs, or validate a patch.
pub fn apply_config_overlays_bytes(
    stored: &[u8],
    loss_json: Option<&str>,
    fern_patch_json: Option<&str>,
) -> Result<Vec<u8>, String> {
    let stored_config = std::str::from_utf8(stored)
        .map_err(|error| format!("stored config must be UTF-8 for the C# Experiment boundary: {error}"))?;
    let response = run_bytes(
        "apply-config-overlays",
        &ConfigOverlayRequest {
            stored_config,
            loss_json,
            fern_patch_json,
        },
    )?;
    let config: crate::resolved_cfg::ResolvedConfigSpec = serde_json::from_slice(&response)
        .map_err(|error| format!("apply-config-overlays receipt is not a resolved config: {error}"))?;
    if config.identity_scheme != crate::resolved_cfg::CURRENT_IDENTITY_SCHEME {
        return Err(format!(
            "apply-config-overlays returned identity scheme {}, expected {}",
            config.identity_scheme,
            crate::resolved_cfg::CURRENT_IDENTITY_SCHEME
        ));
    }
    Ok(response)
}

#[derive(Serialize)]
struct RecipeCatalogCommandRequest<'a> {
    format: &'a str,
}

/// Read one complete public recipe or preset catalog from the C# Experiment
/// owner. Rust forwards only the caller's closed output selection and checks
/// the receipt envelope; recipe identity, hashing, catalog membership, and
/// rendering stay on the other side of the process boundary.
pub fn recipe_catalog_command_bytes(operation: &str, format: &str) -> Result<Vec<u8>, String> {
    let (schema, collection) = match operation {
        "list-recipes" => ("fern.recipe_catalog.v1", "recipes"),
        "list-presets" => ("fern.preset_catalog.v1", "presets"),
        "catalog" => ("fern.experiment_catalog.v1", "presets"),
        _ => return Err(format!("unsupported C# recipe catalog operation {operation:?}")),
    };
    let response = run_bytes(operation, &RecipeCatalogCommandRequest { format })?;
    if format == "json" {
        let receipt: Value =
            serde_json::from_slice(&response).map_err(|error| format!("{operation} receipt is not JSON: {error}"))?;
        if receipt["schema"] != schema || !receipt[collection].is_array() {
            return Err(format!("{operation} returned an incomplete {schema} receipt"));
        }
    } else if std::str::from_utf8(&response)
        .map_err(|error| format!("{operation} response is not UTF-8: {error}"))?
        .is_empty()
    {
        return Err(format!("{operation} returned an empty text catalog"));
    }
    Ok(response)
}

#[derive(Serialize)]
struct RecipeHashLookupRequest<'a> {
    schema: &'static str,
    recipe_name: &'a str,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct RecipeHashLookupReceipt {
    schema: String,
    recipe_name: String,
    recipe_hash: String,
}

/// Resolve one catalog name through the C# Experiment owner. The full catalog
/// record is C#-validated before this receipt is minted, so Rust must neither
/// rebuild the catalog nor re-hash an identity preimage locally.
pub fn resolve_catalog_recipe_hash(recipe_name: &str) -> Result<String, String> {
    let receipt: RecipeHashLookupReceipt = run_json(
        "resolve-recipe-hash",
        &RecipeHashLookupRequest {
            schema: "fern.recipe_hash_lookup.request.v1",
            recipe_name,
        },
    )?;
    if receipt.schema != "fern.recipe_hash_lookup.receipt.v1" {
        return Err(format!(
            "resolve-recipe-hash returned schema {:?}, expected fern.recipe_hash_lookup.receipt.v1",
            receipt.schema
        ));
    }
    if receipt.recipe_name != recipe_name {
        return Err("resolve-recipe-hash changed the requested recipe name".to_string());
    }
    if receipt.recipe_hash.len() != 16
        || !receipt
            .recipe_hash
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (byte as char).is_ascii_lowercase())
    {
        return Err("resolve-recipe-hash returned an invalid 16-hex recipe hash".to_string());
    }
    Ok(receipt.recipe_hash)
}

#[derive(Serialize)]
struct RecipeSelectionRequest<'a> {
    schema: &'static str,
    recipe: Option<&'a str>,
    preset: Option<&'a str>,
    models: &'a [&'a str],
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct RecipeSelectionReceipt {
    schema: String,
    source_kind: String,
    source_name: String,
    recipes: Vec<fern_recipes::LegacyRecipeIdentityV1>,
}

/// Select one recipe or a preset/model slice through the C# catalog owner.
/// The returned identities are immutable compatibility values for Rust
/// callers that have not yet moved their worker-packet representation.
pub fn select_catalog_recipes(
    recipe: Option<&str>,
    preset: Option<&str>,
    models: &[&str],
) -> Result<Vec<fern_recipes::RecipeBlueprint>, String> {
    let receipt: RecipeSelectionReceipt = run_json(
        "select-recipes",
        &RecipeSelectionRequest {
            schema: "fern.recipe_selection.request.v1",
            recipe,
            preset,
            models,
        },
    )?;
    if receipt.schema != "fern.recipe_selection.v1" {
        return Err(format!(
            "select-recipes returned schema {:?}, expected fern.recipe_selection.v1",
            receipt.schema
        ));
    }
    let (expected_kind, expected_name) = match (recipe, preset) {
        (Some(name), None) => ("recipe", name),
        (None, Some(name)) => ("preset", name),
        _ => return Err("select-recipes requires exactly one of recipe or preset".to_string()),
    };
    if receipt.source_kind != expected_kind || receipt.source_name != expected_name {
        return Err(format!(
            "select-recipes changed {expected_kind} {expected_name:?} into {} {:?}",
            receipt.source_kind, receipt.source_name
        ));
    }
    if receipt.recipes.is_empty() {
        return Err(format!(
            "select-recipes returned no recipes for {expected_kind} {expected_name:?}"
        ));
    }
    receipt
        .recipes
        .into_iter()
        .map(|identity| {
            recipe_blueprint_from_csharp_identity(&identity)
                .map_err(|error| format!("cannot materialize C# selected recipe transport: {error}"))
        })
        .collect()
}

/// One raw UTF-8 external declaration transported by the Rust file reader.
/// C# owns parsing, admission, historical hashing, and any compiled-catalog
/// shadow check; Rust supplies no interpreted recipe fields at this boundary.
#[derive(Debug, Clone, Serialize)]
pub struct RecipeExtensionAdmissionSource<'a> {
    pub source_name: &'a str,
    pub extension_json: &'a str,
}

#[derive(Serialize)]
struct RecipeExtensionAdmissionRequest<'a> {
    schema: &'static str,
    documents: &'a [RecipeExtensionAdmissionSource<'a>],
}

/// One C#-checked immutable identity returned from external file admission.
#[derive(Debug, Clone, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct AdmittedRecipeExtension {
    pub source_name: String,
    pub recipe_name: String,
    pub recipe_hash: String,
    pub recipe_identity_sha256: String,
    pub identity: fern_recipes::LegacyRecipeIdentityV1,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct RecipeExtensionAdmissionReceipt {
    schema: String,
    declarations: Vec<AdmittedRecipeExtension>,
}

/// Admit every external Recipe Extension V1 document as one C# transaction.
/// The returned records remain immutable compatibility identities for the
/// existing frozen-Plan shape; this adapter only verifies receipt binding and
/// never parses declaration JSON or derives a legacy hash itself.
pub fn admit_recipe_extensions(
    documents: &[RecipeExtensionAdmissionSource<'_>],
) -> Result<Vec<AdmittedRecipeExtension>, String> {
    let receipt: RecipeExtensionAdmissionReceipt = run_json(
        "admit-recipe-extensions",
        &RecipeExtensionAdmissionRequest {
            schema: "fern.recipe_extension_admission.request.v1",
            documents,
        },
    )?;
    if receipt.schema != "fern.recipe_extension_admission.receipt.v1" {
        return Err(format!(
            "admit-recipe-extensions returned schema {:?}, expected fern.recipe_extension_admission.receipt.v1",
            receipt.schema
        ));
    }
    if receipt.declarations.len() != documents.len() {
        return Err(format!(
            "admit-recipe-extensions returned {} declarations for {} inputs",
            receipt.declarations.len(),
            documents.len()
        ));
    }
    let mut recipe_names = BTreeSet::new();
    for (input, declared) in documents.iter().zip(&receipt.declarations) {
        if declared.source_name != input.source_name {
            return Err(format!(
                "admit-recipe-extensions changed source label {:?} into {:?}",
                input.source_name, declared.source_name
            ));
        }
        if declared.recipe_name != declared.identity.recipe.name {
            return Err(format!(
                "admit-recipe-extensions receipt recipe name {:?} disagrees with its immutable identity {:?}",
                declared.recipe_name, declared.identity.recipe.name
            ));
        }
        if declared.recipe_hash.len() != 16
            || !declared
                .recipe_hash
                .bytes()
                .all(|byte| byte.is_ascii_digit() || matches!(byte, b'a'..=b'f'))
        {
            return Err("admit-recipe-extensions returned an invalid 16-hex recipe hash".to_string());
        }
        if declared.recipe_identity_sha256.len() != 71
            || !declared.recipe_identity_sha256.starts_with("sha256:")
            || !declared.recipe_identity_sha256[7..]
                .bytes()
                .all(|byte| byte.is_ascii_digit() || matches!(byte, b'a'..=b'f'))
        {
            return Err("admit-recipe-extensions returned an invalid recipe identity SHA-256".to_string());
        }
        if !recipe_names.insert(&declared.recipe_name) {
            return Err(format!(
                "admit-recipe-extensions returned duplicate recipe name {:?}",
                declared.recipe_name
            ));
        }
    }
    Ok(receipt.declarations)
}

pub fn compiled_experiment_projection(compiled: &CompiledExperimentV2) -> Result<CompiledExperimentProjection, String> {
    let cells = compiled
        .cells()
        .iter()
        .enumerate()
        .map(|(ordinal, cell)| {
            Ok(CompiledCellProjection {
                cell_ordinal: i64::try_from(ordinal).map_err(|_| "cell ordinal does not fit i64".to_string())?,
                target_hash: cell.target_hash.clone(),
                config_hash: cell.config_hash.clone(),
                coordinates: serde_json::to_value(&cell.coordinates).map_err(|error| error.to_string())?,
                config: serde_json::to_value(&cell.config).map_err(|error| error.to_string())?,
            })
        })
        .collect::<Result<Vec<_>, String>>()?;
    Ok(CompiledExperimentProjection {
        manifest_sha256: compiled.manifest_sha256().to_string(),
        execution_profile_id: compiled.execution_profile_id().to_string(),
        execution_profile_revision: compiled.execution_profile_revision().to_string(),
        cells,
    })
}

pub fn compiled_ablation_projection(
    compiled: &CompiledAblationV2,
    reported_template_digest: &Sha256Digest,
) -> Result<CompiledAblationProjection, String> {
    Ok(CompiledAblationProjection {
        resolution: serde_json::to_value(compiled.resolution()).map_err(|error| error.to_string())?,
        reported_template_digest: reported_template_digest.as_str().to_string(),
        control: compiled_experiment_projection(compiled.control())?,
        treatment: compiled_experiment_projection(compiled.treatment())?,
    })
}

pub fn template_json(template: &impl Serialize) -> Result<String, String> {
    let bytes = fern_json_contract::canonical_json_bytes(template).map_err(|error| error.to_string())?;
    String::from_utf8(bytes).map_err(|error| error.to_string())
}

/// One C# Experiment handoff whose reply is printed unchanged rather than
/// parsed: the operator-facing `list-recipe-run-targets` report (CHG 2026-08-28).
pub fn run_bytes<T: Serialize>(operation: &str, input: &T) -> Result<Vec<u8>, String> {
    let boundary_started = Instant::now();
    let parent_deadline = boundary_started + CSHARP_EXPERIMENT_PARENT_BOUNDARY_DEADLINE;
    let input_json = serde_json::to_vec(input).map_err(|error| format!("{operation} request is not JSON: {error}"))?;
    let binary = binary()?;
    if Instant::now() >= parent_deadline {
        return Err(format!(
            "C# Experiment parent boundary deadline {:?} exhausted before {operation} child launch",
            CSHARP_EXPERIMENT_PARENT_BOUNDARY_DEADLINE
        ));
    }
    let output = invoke_process_before_deadlines(
        &binary,
        operation,
        input_json,
        CSHARP_EXPERIMENT_PROCESS_DEADLINE,
        Some(parent_deadline),
    )?;
    if !output.status.success() {
        return Err(format!(
            "{operation}: {}",
            String::from_utf8_lossy(&output.stderr).trim()
        ));
    }
    if boundary_started.elapsed() >= CSHARP_EXPERIMENT_PARENT_BOUNDARY_DEADLINE {
        return Err(format!(
            "C# Experiment parent boundary deadline {:?} exhausted while completing {operation}",
            CSHARP_EXPERIMENT_PARENT_BOUNDARY_DEADLINE
        ));
    }
    Ok(output.stdout)
}

fn run_json<T: Serialize, R: for<'de> Deserialize<'de>>(operation: &str, input: &T) -> Result<R, String> {
    let stdout = run_bytes(operation, input)?;
    serde_json::from_slice(&stdout).map_err(|error| format!("{operation} receipt is not JSON: {error}"))
}

#[cfg(test)]
fn invoke_process_before_deadline(
    binary: &Path,
    operation: &str,
    input_json: Vec<u8>,
    deadline: Duration,
) -> Result<std::process::Output, String> {
    invoke_process_before_deadlines(binary, operation, input_json, deadline, None)
}

fn invoke_process_before_deadlines(
    binary: &Path,
    operation: &str,
    input_json: Vec<u8>,
    process_deadline: Duration,
    parent_deadline: Option<Instant>,
) -> Result<std::process::Output, String> {
    let started = Instant::now();
    let mut child = Command::new(binary)
        .arg(operation)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| format!("cannot start {}: {error}", binary.display()))?;
    let mut stdin = child
        .stdin
        .take()
        .ok_or_else(|| format!("{operation} did not expose stdin"))?;
    let mut stdout = child
        .stdout
        .take()
        .ok_or_else(|| format!("{operation} did not expose stdout"))?;
    let mut stderr = child
        .stderr
        .take()
        .ok_or_else(|| format!("{operation} did not expose stderr"))?;
    let input_writer = std::thread::spawn(move || stdin.write_all(&input_json));
    let stdout_reader = std::thread::spawn(move || {
        let mut bytes = Vec::new();
        stdout.read_to_end(&mut bytes).map(|_| bytes)
    });
    let stderr_reader = std::thread::spawn(move || {
        let mut bytes = Vec::new();
        stderr.read_to_end(&mut bytes).map(|_| bytes)
    });
    let status =
        collect_process_exit_before_deadline(&mut child, operation, started, process_deadline, parent_deadline);
    let input_result = input_writer
        .join()
        .map_err(|_| format!("{operation} request writer panicked"))?;
    let stdout = stdout_reader
        .join()
        .map_err(|_| format!("{operation} stdout reader panicked"))?
        .map_err(|error| format!("cannot read {operation} stdout: {error}"))?;
    let stderr = stderr_reader
        .join()
        .map_err(|_| format!("{operation} stderr reader panicked"))?
        .map_err(|error| format!("cannot read {operation} stderr: {error}"))?;
    let status = status?;
    if status.success() {
        input_result.map_err(|error| format!("cannot write {operation} request: {error}"))?;
    }
    Ok(std::process::Output { status, stdout, stderr })
}

fn collect_process_exit_before_deadline(
    child: &mut std::process::Child,
    operation: &str,
    started: Instant,
    process_deadline: Duration,
    parent_deadline: Option<Instant>,
) -> Result<std::process::ExitStatus, String> {
    let child_id = child.id();
    loop {
        match child
            .try_wait()
            .map_err(|error| format!("wait for {child_id}: {error}"))?
        {
            Some(status) => return Ok(status),
            None if parent_deadline.is_some_and(|deadline| Instant::now() >= deadline) => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(format!(
                    "C# Experiment parent boundary deadline {:?} exhausted while running {operation}",
                    CSHARP_EXPERIMENT_PARENT_BOUNDARY_DEADLINE
                ));
            }
            None if started.elapsed() >= process_deadline => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(format!(
                    "C# Experiment deadline {process_deadline:?} exhausted while running {operation}"
                ));
            }
            None => std::thread::sleep(CSHARP_EXPERIMENT_PROCESS_POLL_INTERVAL),
        }
    }
}

fn binary() -> Result<PathBuf, String> {
    if let Ok(path) = std::env::var("FERN_EXPERIMENT") {
        let path = PathBuf::from(path);
        if path.is_file() {
            return Ok(path);
        }
        return Err(format!("FERN_EXPERIMENT is not a file: {}", path.display()));
    }
    if let Some(path) = look_path("fern-experiment") {
        return Ok(path);
    }
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .ok_or_else(|| "cannot locate the repository root for fern-experiment".to_string())?
        .to_path_buf();
    let debug = root.join("csharp/Fern.Experiment.Cli/bin/Debug/net9.0/fern-experiment");
    let release = root.join("csharp/Fern.Experiment.Cli/bin/Release/net9.0/fern-experiment");
    match (debug.is_file(), release.is_file()) {
        (true, true) => {
            let debug_time = std::fs::metadata(&debug)
                .and_then(|meta| meta.modified())
                .map_err(|error| error.to_string())?;
            let release_time = std::fs::metadata(&release)
                .and_then(|meta| meta.modified())
                .map_err(|error| error.to_string())?;
            Ok(if release_time >= debug_time { release } else { debug })
        }
        (true, false) => Ok(debug),
        (false, true) => Ok(release),
        (false, false) => Err(format!(
            "fern-experiment is not on PATH and was not built under {}",
            root.join("csharp/Fern.Experiment.Cli/bin").display()
        )),
    }
}

fn look_path(name: &str) -> Option<PathBuf> {
    let path = std::env::var_os("PATH")?;
    std::env::split_paths(&path).find_map(|directory| {
        let candidate = directory.join(name);
        candidate.is_file().then_some(candidate)
    })
}

#[cfg(test)]
mod tests {
    use std::ffi::OsString;
    use std::path::{Path, PathBuf};
    use std::sync::Mutex;

    use super::*;

    static FERN_EXPERIMENT_ENV_LOCK: Mutex<()> = Mutex::new(());

    struct PinnedExperimentEnvironment {
        previous: Option<OsString>,
    }

    impl PinnedExperimentEnvironment {
        fn install(binary: &Path) -> Self {
            let previous = std::env::var_os("FERN_EXPERIMENT");
            // SAFETY: this test holds the process-wide environment lock and
            // restores the prior value in Drop before releasing it.
            unsafe { std::env::set_var("FERN_EXPERIMENT", binary) };
            Self { previous }
        }
    }

    impl Drop for PinnedExperimentEnvironment {
        fn drop(&mut self) {
            match self.previous.take() {
                Some(previous) => unsafe { std::env::set_var("FERN_EXPERIMENT", previous) },
                None => unsafe { std::env::remove_var("FERN_EXPERIMENT") },
            }
        }
    }

    fn fixture_request() -> (LowerCellBatchRequest, Value) {
        let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .unwrap()
            .to_path_buf();
        let golden: Value = serde_json::from_slice(
            &std::fs::read(
                root.join("crates/fern-experiment-types/fixtures/stage_plan_go_v3_baseline_lowering_golden.json"),
            )
            .unwrap(),
        )
        .unwrap();
        let compiled = &golden["compiled"];
        let cell = &compiled["cells"][0];
        let accepted = &golden["accepted_implementation"];
        let request = LowerCellBatchRequest {
            schema: "fern.lower_cell_batch.request.v1".to_string(),
            compiled: CompiledExperimentProjection {
                manifest_sha256: compiled["resolution"]["resolved"]["manifest_sha256"]
                    .as_str()
                    .unwrap()
                    .to_string(),
                execution_profile_id: compiled["execution_profile_id"].as_str().unwrap().to_string(),
                execution_profile_revision: compiled["execution_profile_revision"].as_str().unwrap().to_string(),
                cells: vec![CompiledCellProjection {
                    cell_ordinal: 0,
                    target_hash: cell["target_hash"].as_str().unwrap().to_string(),
                    config_hash: cell["config_hash"].as_str().unwrap().to_string(),
                    coordinates: cell["coordinates"].clone(),
                    config: cell["config"].clone(),
                }],
            },
            template_json: std::fs::read_to_string(
                root.join("crates/fern-experiment-types/fixtures/stage_pipeline_forecast_origin_v1.json"),
            )
            .unwrap(),
            python: PythonImplementationProjection {
                runner: accepted["runner"].as_str().unwrap().to_string(),
                entrypoint: accepted["entrypoint"].as_str().unwrap().to_string(),
                implementation_digest: accepted["implementation_digest"].as_str().unwrap().to_string(),
            },
            requests: vec![CellRequestProjection {
                cell_ordinal: 0,
                training_worker_request_json: golden["training_worker_request_utf8"].as_str().unwrap().to_string(),
            }],
        };
        (request, golden["lowered"].clone())
    }

    fn experiment_projection_from_golden(compiled: &Value) -> CompiledExperimentProjection {
        CompiledExperimentProjection {
            manifest_sha256: compiled["resolution"]["resolved"]["manifest_sha256"]
                .as_str()
                .unwrap()
                .to_string(),
            execution_profile_id: compiled["execution_profile_id"].as_str().unwrap().to_string(),
            execution_profile_revision: compiled["execution_profile_revision"].as_str().unwrap().to_string(),
            cells: compiled["cells"]
                .as_array()
                .unwrap()
                .iter()
                .enumerate()
                .map(|(ordinal, cell)| CompiledCellProjection {
                    cell_ordinal: i64::try_from(ordinal).unwrap(),
                    target_hash: cell["target_hash"].as_str().unwrap().to_string(),
                    config_hash: cell["config_hash"].as_str().unwrap().to_string(),
                    coordinates: cell["coordinates"].clone(),
                    config: cell["config"].clone(),
                })
                .collect(),
        }
    }

    fn pair_fixture_request() -> (PairAblationRequest, LowerPairBatchRequest, Value) {
        let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .unwrap()
            .to_path_buf();
        let golden: Value = serde_json::from_slice(
            &std::fs::read(
                root.join("crates/fern-experiment-types/fixtures/stage_plan_paired_unit_lowering_golden.json"),
            )
            .unwrap(),
        )
        .unwrap();
        let compiled = &golden["compiled"];
        let compiled_projection = CompiledAblationProjection {
            resolution: compiled["resolution"].clone(),
            reported_template_digest: golden["reported_template_digest"].as_str().unwrap().to_string(),
            control: experiment_projection_from_golden(&compiled["control"]),
            treatment: experiment_projection_from_golden(&compiled["treatment"]),
        };
        let requests = golden["paired_units"]
            .as_array()
            .unwrap()
            .iter()
            .map(|unit| PairRequestProjection {
                paired_unit_id: unit["paired_unit_id"].as_str().unwrap().to_string(),
                control_training_worker_request_json: unit["control_training_worker_request_utf8"]
                    .as_str()
                    .unwrap()
                    .to_string(),
                treatment_training_worker_request_json: unit["treatment_training_worker_request_utf8"]
                    .as_str()
                    .unwrap()
                    .to_string(),
            })
            .collect();
        let python = &golden["accepted_implementation"];
        let pair_request = PairAblationRequest {
            schema: "fern.pair_compiled_ablation.request.v1".to_string(),
            compiled: compiled_projection.clone(),
        };
        let lower_request = LowerPairBatchRequest {
            schema: "fern.lower_pair_batch.request.v1".to_string(),
            compiled: compiled_projection,
            template_json: std::fs::read_to_string(
                root.join("crates/fern-experiment-types/fixtures/stage_pipeline_forecast_origin_v1.json"),
            )
            .unwrap(),
            python: PythonImplementationProjection {
                runner: python["runner"].as_str().unwrap().to_string(),
                entrypoint: python["entrypoint"].as_str().unwrap().to_string(),
                implementation_digest: python["implementation_digest"].as_str().unwrap().to_string(),
            },
            requests,
        };
        (pair_request, lower_request, golden)
    }

    #[test]
    fn explicit_failing_child_proves_the_process_boundary() {
        let _lock = FERN_EXPERIMENT_ENV_LOCK.lock().unwrap();
        let _environment = PinnedExperimentEnvironment::install(Path::new("/bin/false"));
        let error = lower_cells(&fixture_request().0).unwrap_err();
        assert!(error.contains("lower-cell-batch:"), "{error}");
    }

    #[test]
    fn parent_deadline_caps_a_child_below_its_own_process_deadline() {
        let started = Instant::now();
        let error = invoke_process_before_deadlines(
            Path::new("/bin/sleep"),
            "5",
            Vec::new(),
            CSHARP_EXPERIMENT_PROCESS_DEADLINE,
            Some(started + Duration::from_millis(75)),
        )
        .unwrap_err();

        assert!(error.contains("parent boundary deadline"), "{error}");
        assert!(started.elapsed() < Duration::from_secs(1));
    }

    #[test]
    fn process_protocol_refuses_unknown_request_and_response_fields() {
        let _lock = FERN_EXPERIMENT_ENV_LOCK.lock().unwrap();
        let (request, _) = fixture_request();
        let mut request_value = serde_json::to_value(&request).unwrap();
        request_value["unknown_field"] = Value::Bool(true);
        let refused = invoke_process_before_deadline(
            &binary().unwrap(),
            "lower-cell-batch",
            serde_json::to_vec(&request_value).unwrap(),
            CSHARP_EXPERIMENT_PROCESS_DEADLINE,
        )
        .unwrap();
        assert!(!refused.status.success());
        assert!(
            String::from_utf8_lossy(&refused.stderr).contains("unknown property unknown_field"),
            "{}",
            String::from_utf8_lossy(&refused.stderr)
        );

        let accepted = invoke_process_before_deadline(
            &binary().unwrap(),
            "lower-cell-batch",
            serde_json::to_vec(&request).unwrap(),
            CSHARP_EXPERIMENT_PROCESS_DEADLINE,
        )
        .unwrap();
        assert!(accepted.status.success());
        let mut receipt: Value = serde_json::from_slice(&accepted.stdout).unwrap();
        receipt["unknown_field"] = Value::Bool(true);
        let error = serde_json::from_value::<LowerCellBatchReceipt>(receipt).unwrap_err();
        assert!(error.to_string().contains("unknown field"), "{error}");
    }

    #[test]
    fn real_csharp_child_lowers_the_complete_old_rust_cell_fixture() {
        let _lock = FERN_EXPERIMENT_ENV_LOCK.lock().unwrap();
        let (request, expected) = fixture_request();
        let receipt = lower_cells(&request).unwrap();
        let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .unwrap()
            .to_path_buf();
        let identity_golden: Value = serde_json::from_slice(
            &std::fs::read(root.join("crates/fern-experiment-types/fixtures/stage_plan_repeated_lowering_golden.json"))
                .unwrap(),
        )
        .unwrap();
        assert_eq!(receipt.schema, "fern.lower_cell_batch.receipt.v1");
        assert_eq!(receipt.lowered.len(), 1);
        assert_eq!(receipt.lowered[0].cell_ordinal, 0);
        assert_eq!(
            serde_json::to_value(&receipt.lowered[0].subject).unwrap(),
            identity_golden["cell"]["subject"],
        );
        assert_eq!(
            receipt.lowered[0].stage_plan_instance_id.as_str(),
            identity_golden["cell"]["stage_plan_instance_id"],
        );
        assert_eq!(
            fern_json_contract::canonical_json_bytes(&receipt.lowered[0].stage_plan).unwrap(),
            fern_json_contract::canonical_json_bytes(&expected["stage_plan"]).unwrap(),
        );
        let expected_inputs = expected["inputs"].as_object().unwrap();
        assert_eq!(receipt.lowered[0].inputs.len(), expected_inputs.len());
        for (input_key, actual) in &receipt.lowered[0].inputs {
            let expected = &expected_inputs[input_key];
            assert_eq!(actual.port, expected["port"]);
            assert_eq!(actual.role, expected["role"]);
            assert_eq!(actual.schema_id, expected["schema_id"]);
            assert_eq!(actual.content_digest.as_str(), expected["content_digest"]);
        }
    }

    #[test]
    #[ignore = "the historical research fixture is not included in the public archive"]
    fn real_csharp_child_reads_stored_config_identity_without_rust_rehashing() {
        let _lock = FERN_EXPERIMENT_ENV_LOCK.lock().unwrap();
        let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .unwrap()
            .to_path_buf();
        let golden: Value = serde_json::from_slice(
            &std::fs::read(
                root.join("crates/fern-experiment-types/fixtures/config_identity_registered_scheme_1_to_2_golden.json"),
            )
            .unwrap(),
        )
        .unwrap();
        let entry = &golden["configs"][0];
        let response = show_config_identity_bytes(entry["config_json"].as_str().unwrap().as_bytes()).unwrap();
        let report: Value = serde_json::from_slice(&response).unwrap();
        assert_eq!(report["schema"], "fern.config_identity_reading.v1");
        assert_eq!(report["stored_identity_scheme"], 1);
        assert_eq!(report["stored_bytes_sha256_16hex"], entry["scheme1_config_hash"]);
        assert_eq!(report["current_identity_scheme"], 2);
        assert_eq!(report["current_config_hash"], entry["scheme2_config_hash"]);
        assert_eq!(report["current_preimage"], entry["scheme2_preimage"]);
    }

    #[test]
    #[ignore = "the historical research fixtures are not included in the public archive"]
    fn real_csharp_child_reads_the_complete_recipe_and_preset_catalogs() {
        let _lock = FERN_EXPERIMENT_ENV_LOCK.lock().unwrap();
        let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .unwrap()
            .to_path_buf();
        let fixture_root = root.join("crates/fern-recipes/fixtures");
        let recipes: Value =
            serde_json::from_slice(&std::fs::read(fixture_root.join("recipe_catalog_identity_golden.json")).unwrap())
                .unwrap();
        let recipe_response = recipe_catalog_command_bytes("list-recipes", "json").unwrap();
        let recipe_report: Value = serde_json::from_slice(&recipe_response).unwrap();
        assert_eq!(
            recipe_report, recipes["catalog"],
            "list-recipes must preserve complete recipe output"
        );

        let presets: Value =
            serde_json::from_slice(&std::fs::read(fixture_root.join("recipe_preset_catalog_golden.json")).unwrap())
                .unwrap();
        let preset_response = recipe_catalog_command_bytes("list-presets", "json").unwrap();
        let preset_report: Value = serde_json::from_slice(&preset_response).unwrap();
        assert_eq!(preset_report["schema"], "fern.preset_catalog.v1");
        let expected_presets = presets["catalog"]["presets"].as_array().unwrap();
        let actual_presets = preset_report["presets"].as_array().unwrap();
        assert_eq!(actual_presets.len(), expected_presets.len());
        for (expected, actual) in expected_presets.iter().zip(actual_presets) {
            assert_eq!(actual["name"], expected["name"]);
            assert_eq!(actual["label"], expected["label"]);
            assert_eq!(actual["models"], expected["models"]);
            let expected_names = expected["recipes"].as_array().unwrap();
            let actual_rows = actual["recipes"].as_array().unwrap();
            assert_eq!(actual_rows.len(), expected_names.len());
            for (expected_name, actual_row) in expected_names.iter().zip(actual_rows) {
                assert_eq!(actual_row["name"], *expected_name);
            }
        }

        let catalog_response = recipe_catalog_command_bytes("catalog", "json").unwrap();
        let catalog_report: Value = serde_json::from_slice(&catalog_response).unwrap();
        assert_eq!(catalog_report["schema"], "fern.experiment_catalog.v1");
        assert_eq!(
            catalog_report["presets"].as_array().unwrap().len(),
            expected_presets.len()
        );
        assert_eq!(catalog_report["presets"][0]["preset"], expected_presets[0]["name"]);
        assert_eq!(
            catalog_report["presets"][0]["recipe_count"],
            expected_presets[0]["recipes"].as_array().unwrap().len(),
        );
    }

    #[test]
    fn real_csharp_child_admits_a_raw_legacy_recipe_transport_before_target_expansion() {
        let _lock = FERN_EXPERIMENT_ENV_LOCK.lock().unwrap();
        let recipe = select_catalog_recipes(
            Some("rossler_base_fern_huber_p70_20_10_none_bs95_titration_h64_h192_sigmas"),
            None,
            &[],
        )
        .unwrap()
        .into_iter()
        .next()
        .unwrap();
        let receipt =
            resolve_recipe_run_targets_from_blueprint(&recipe, crate::resolved_cfg::DeviceKind::Cpu, 42, 1000).unwrap();
        assert_eq!(receipt.recipe, recipe.name());
        assert_eq!(receipt.recipe_hash.len(), 16);
        let materialized = recipe_blueprint_from_csharp_identity(&receipt.recipe_identity).unwrap();
        assert_eq!(materialized, recipe);
    }

    #[test]
    fn real_csharp_child_checks_installed_handler_compatibility_before_dispatch() {
        let _lock = FERN_EXPERIMENT_ENV_LOCK.lock().unwrap();
        let recipe = select_catalog_recipes(
            Some("rossler_base_fern_huber_p70_20_10_none_bs95_titration_h64_h192_sigmas"),
            None,
            &[],
        )
        .unwrap()
        .into_iter()
        .next()
        .unwrap();
        let identity =
            resolve_recipe_run_targets_from_blueprint(&recipe, crate::resolved_cfg::DeviceKind::Cpu, 42, 1000)
                .unwrap()
                .recipe_identity;

        let mut missing_model = fern_recipes::recipe_handler_capabilities();
        missing_model.models.retain(|model| model.name != "fern");
        assert_eq!(
            missing_recipe_handler_names_via_csharp(&identity, &missing_model).unwrap(),
            [r#"model: "fern""#]
        );
        assert!(
            missing_recipe_handler_names_via_csharp(&identity, &fern_recipes::recipe_handler_capabilities(),)
                .unwrap()
                .is_empty()
        );
    }

    #[test]
    fn real_csharp_child_resolves_complete_dataset_source_capabilities() {
        let _lock = FERN_EXPERIMENT_ENV_LOCK.lock().unwrap();
        let complete = resolve_dataset_source_capabilities(&[]).unwrap();
        assert_eq!(complete.len(), 79);
        assert!(
            complete
                .windows(2)
                .all(|pair| pair[0].source_name < pair[1].source_name)
        );
        let selected = resolve_dataset_source_capabilities(&["etth1", "lg_ssm"]).unwrap();
        assert_eq!(selected.len(), 2);
        assert_eq!(selected[0].source_name, "etth1");
        assert_eq!(selected[0].draw_kind, fern_recipes::SourceDrawKind::KnownData);
        assert!(!selected[0].source_is_stochastic);
        assert_eq!(selected[0].draw_design, "noise_only");
        assert_eq!(selected[1].source_name, "lg_ssm");
        assert_eq!(
            selected[1].draw_kind,
            fern_recipes::SourceDrawKind::StochasticTrajectory
        );
        assert!(selected[1].source_is_stochastic);
        assert_eq!(selected[1].draw_design, "joint");
        assert!(resolve_dataset_source_capability("lorenz_ic_16").is_err());
    }

    #[test]
    #[ignore = "the historical research fixture is not included in the public archive"]
    fn real_csharp_child_applies_loss_and_fern_patch_overlays() {
        let _lock = FERN_EXPERIMENT_ENV_LOCK.lock().unwrap();
        let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .unwrap()
            .to_path_buf();
        let golden: Value = serde_json::from_slice(
            &std::fs::read(
                root.join("crates/fern-experiment-types/fixtures/config_identity_registered_scheme_1_to_2_golden.json"),
            )
            .unwrap(),
        )
        .unwrap();
        let entry = golden["configs"]
            .as_array()
            .unwrap()
            .iter()
            .find(|entry| {
                serde_json::from_str::<Value>(entry["config_json"].as_str().unwrap()).unwrap()["model"]["type"]
                    == "fern"
            })
            .unwrap();
        let stored = entry["config_json"].as_str().unwrap();
        let mut parsed: Value = serde_json::from_str(stored).unwrap();
        parsed["loss"]["probability"]["num_train_samples"] = 5.into();
        parsed["loss"]["probability"]["num_eval_samples"] = 9.into();
        let loss = serde_json::to_string(&parsed["loss"]).unwrap();
        let response =
            apply_config_overlays_bytes(stored.as_bytes(), Some(&loss), Some(r#"{"L":96,"P":24,"G":4}"#)).unwrap();
        let report: Value = serde_json::from_slice(&response).unwrap();
        assert_eq!(report["identity_scheme"], 2);
        assert_eq!(report["loss"]["probability"]["num_train_samples"], 5);
        assert_eq!(report["loss"]["probability"]["num_eval_samples"], 9);
        assert_eq!(report["model"]["knobs"]["num_train_samples"], 5);
        assert_eq!(report["model"]["knobs"]["num_eval_samples"], 9);
        assert_eq!(report["model"]["knobs"]["Brenier_patch_spec"]["P"], 24);
    }

    #[test]
    fn real_csharp_child_drains_a_complete_receipt_larger_than_an_os_pipe() {
        let _lock = FERN_EXPERIMENT_ENV_LOCK.lock().unwrap();
        let (base, _) = fixture_request();
        let base_cell = base.compiled.cells[0].clone();
        let base_request: Value = serde_json::from_str(&base.requests[0].training_worker_request_json).unwrap();
        let mut cells = Vec::new();
        let mut requests = Vec::new();
        for ordinal in 0..64_i64 {
            let config_hash = format!("large-batch-config-{ordinal}");
            cells.push(CompiledCellProjection {
                cell_ordinal: ordinal,
                target_hash: format!("large-batch-target-{ordinal}"),
                config_hash: config_hash.clone(),
                coordinates: base_cell.coordinates.clone(),
                config: base_cell.config.clone(),
            });
            let mut request = base_request.clone();
            request["run_packet"]["config_hash"] = Value::String(config_hash);
            requests.push(CellRequestProjection {
                cell_ordinal: ordinal,
                training_worker_request_json: String::from_utf8(
                    fern_json_contract::canonical_json_bytes(&request).unwrap(),
                )
                .unwrap(),
            });
        }
        let receipt = lower_cells(&LowerCellBatchRequest {
            schema: base.schema,
            compiled: CompiledExperimentProjection {
                manifest_sha256: base.compiled.manifest_sha256,
                execution_profile_id: base.compiled.execution_profile_id,
                execution_profile_revision: base.compiled.execution_profile_revision,
                cells,
            },
            template_json: base.template_json,
            python: base.python,
            requests,
        })
        .unwrap();
        assert_eq!(receipt.lowered.len(), 64);
        let complete_plan_bytes = receipt
            .lowered
            .iter()
            .map(|item| {
                fern_json_contract::canonical_json_bytes(&item.stage_plan)
                    .unwrap()
                    .len()
            })
            .sum::<usize>();
        assert!(complete_plan_bytes > 128 * 1024, "{complete_plan_bytes}");
    }

    #[test]
    fn real_csharp_child_pairs_and_lowers_the_complete_old_rust_ablation_fixture() {
        let _lock = FERN_EXPERIMENT_ENV_LOCK.lock().unwrap();
        let (pair_request, lower_request, golden) = pair_fixture_request();
        let paired = pair_ablation(&pair_request).unwrap();
        let expected_units = golden["paired_units"].as_array().unwrap();
        assert_eq!(paired.schema, "fern.pair_compiled_ablation.receipt.v1");
        assert_eq!(paired.resolution_digest, golden["ablation_resolution_digest"]);
        assert_eq!(paired.units.len(), expected_units.len());
        for (ordinal, (actual, expected)) in paired.units.iter().zip(expected_units).enumerate() {
            assert_eq!(actual.paired_unit_id, expected["paired_unit_id"]);
            assert_eq!(actual.coordinates, expected["coordinates"]);
            assert_eq!(actual.control_cell_ordinal, i64::try_from(ordinal).unwrap());
            assert_eq!(actual.treatment_cell_ordinal, i64::try_from(ordinal).unwrap());
            assert!(!actual.target_hash.is_empty());
            assert!(!actual.config_hash.is_empty());
        }

        let lowered = lower_pairs(&lower_request).unwrap();
        let selected = usize::try_from(golden["selected_unit_ordinal"].as_u64().unwrap()).unwrap();
        assert_eq!(lowered.schema, "fern.lower_pair_batch.receipt.v1");
        assert_eq!(lowered.lowered.len(), expected_units.len());
        assert_eq!(
            lowered.lowered[selected].paired_unit_id,
            golden["lowered"]["paired_unit_id"]
        );
        assert_eq!(
            lowered.lowered[selected].target_hash,
            paired.units[selected].target_hash
        );
        assert_eq!(
            lowered.lowered[selected].config_hash,
            paired.units[selected].config_hash
        );
        assert_eq!(
            serde_json::to_value(&lowered.lowered[selected].subject).unwrap(),
            golden["lowered"]["stage_plan"]["subject"],
        );
        assert_eq!(
            fern_json_contract::canonical_json_bytes(&lowered.lowered[selected].stage_plan).unwrap(),
            fern_json_contract::canonical_json_bytes(&golden["lowered"]["stage_plan"]).unwrap(),
        );
        let expected_inputs = golden["lowered"]["inputs"].as_object().unwrap();
        assert_eq!(lowered.lowered[selected].inputs.len(), expected_inputs.len());
        for (input_key, actual) in &lowered.lowered[selected].inputs {
            let expected = &expected_inputs[input_key];
            assert_eq!(actual.port, expected["port"]);
            assert_eq!(actual.role, expected["role"]);
            assert_eq!(actual.schema_id, expected["schema_id"]);
            assert_eq!(actual.content_digest.as_str(), expected["content_digest"]);
        }
    }
}
