//! Typed authoring record for data-only Fern recipe declarations.
//!
//! A Recipe Extension says which already-compiled world, model, evaluator,
//! loader, and training handlers to combine, then supplies its source grouping
//! and forecast grid as data. It does not contain executable code. The legacy
//! scientific identity remains the exact
//! `fern.recipe_blueprint.rust.v1` JSON preimage so moving a declaration out of
//! Rust cannot rename historical results.

#[cfg(any())]
use std::collections::BTreeSet;

use schemars::JsonSchema;
use serde::{Deserialize, Serialize};
use strum::IntoEnumIterator;

use crate::{
    CheckpointReusePolicy, DatasetSourceCapabilityV1, FernArchitectureVariant, RecipeEvalName, RecipeFamilyName,
    RecipeLoaderName, RecipeModelName, RecipePlanName, RecipeTrainName, SplitTreatmentScheme,
    published_dataset_source_capabilities,
};

pub const RECIPE_EXTENSION_SCHEMA: &str = "fern.recipe-extension.v1";
pub const LEGACY_RECIPE_IDENTITY_SCHEMA: &str = "fern.recipe_blueprint.rust.v1";
pub const RECIPE_HANDLER_CAPABILITIES_SCHEMA: &str = "fern.recipe-handler-capabilities.v1";

/// The bounded executable recipe vocabulary this worker currently implements.
///
/// Installation compatibility hashes this typed value, not a selected source
/// file. The family and plan rows preserve the legacy catalog projection; an
/// external declaration is checked against their union of compiled world cells
/// and does not need to reuse one of their group or grid names.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RecipeHandlerCapabilitiesV1 {
    pub schema: String,
    pub recipe_extension_schema: String,
    pub legacy_identity_schema: String,
    pub families: Vec<RecipeFamilyCapabilityV1>,
    pub sources: Vec<DatasetSourceCapabilityV1>,
    pub models: Vec<RecipeModelCapabilityV1>,
    pub evals: Vec<String>,
    pub loaders: Vec<String>,
    pub trains: Vec<String>,
    pub plans: Vec<RecipePlanCapabilityV1>,
    pub fern_architectures: Vec<String>,
    #[serde(default)]
    pub fern_densities: Vec<String>,
    pub split_treatment_schemes: Vec<String>,
    pub checkpoint_reuse_policies: Vec<String>,
    pub noise_kinds: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RecipeFamilyCapabilityV1 {
    pub name: String,
    pub cells: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RecipeModelCapabilityV1 {
    pub name: String,
    pub fits_weights: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RecipePlanCapabilityV1 {
    pub name: String,
    pub seq_len: i64,
    pub pred_lens: Vec<i64>,
    pub noise_levels: Vec<f32>,
}

/// Derive the executable recipe capability record from exhaustive enums and
/// their actual handler mappings. There is no second hand-maintained list.
pub fn recipe_handler_capabilities() -> RecipeHandlerCapabilitiesV1 {
    RecipeHandlerCapabilitiesV1 {
        schema: RECIPE_HANDLER_CAPABILITIES_SCHEMA.to_string(),
        recipe_extension_schema: RECIPE_EXTENSION_SCHEMA.to_string(),
        legacy_identity_schema: LEGACY_RECIPE_IDENTITY_SCHEMA.to_string(),
        families: RecipeFamilyName::iter()
            .map(|family| RecipeFamilyCapabilityV1 {
                name: family.value().to_string(),
                cells: family.cells().iter().map(|cell| (*cell).to_string()).collect(),
            })
            .collect(),
        sources: published_dataset_source_capabilities(),
        models: RecipeModelName::iter()
            .map(|model| RecipeModelCapabilityV1 {
                name: model.value().to_string(),
                fits_weights: model.fits_weights(),
            })
            .collect(),
        evals: RecipeEvalName::iter().map(|value| value.value().to_string()).collect(),
        loaders: RecipeLoaderName::iter()
            .map(|value| value.value().to_string())
            .collect(),
        trains: RecipeTrainName::iter().map(|value| value.value().to_string()).collect(),
        plans: RecipePlanName::iter()
            .map(|plan| RecipePlanCapabilityV1 {
                name: plan.value().to_string(),
                seq_len: plan.seq_len(),
                pred_lens: plan.pred_lens().to_vec(),
                noise_levels: plan.noise_levels().to_vec(),
            })
            .collect(),
        fern_architectures: FernArchitectureVariant::iter()
            .map(|value| value.value().to_string())
            .collect(),
        fern_densities: fern_experiment_types::FernDensityVariantV1::iter()
            .map(|value| value.value().to_string())
            .collect(),
        split_treatment_schemes: SplitTreatmentScheme::iter()
            .map(|value| value.value().to_string())
            .collect(),
        checkpoint_reuse_policies: CheckpointReusePolicy::iter()
            .map(|value| value.value().to_string())
            .collect(),
        noise_kinds: fern_experiment_types::NoiseKind::iter()
            .map(|value| value.value().to_string())
            .collect(),
    }
}

/// Handler names in `identity` that `capabilities` cannot name, as
/// `field: value` pairs in the order a reader would read them.
///
/// A plan freezes a recipe by IDENTITY, and dispatch turns that identity back
/// into handler enums. So a binary that dispatches a plan must know every
/// handler name the plan froze, and one that does not fails only at dispatch,
/// after the plan is registered and queued. This answers the same question
/// ahead of time and against another binary's capability record, so the
/// registering process can refuse instead of leaving a live unstartable plan.
///
/// Compares the exact names this recipe uses, never whole capability records:
/// a dispatcher that has learned an unrelated new evaluator is still able to
/// run every plan it could run before, and refusing that would be an alarm
/// that fires on every unrelated change.
#[cfg(any())]
pub fn handler_names_absent_from_capabilities(
    identity: &LegacyRecipeIdentityV1,
    capabilities: &RecipeHandlerCapabilitiesV1,
) -> Vec<String> {
    let values = &identity.recipe;
    let mut missing = Vec::new();
    let mut require = |field: &str, value: Option<&str>, known: bool| {
        if let Some(value) = value {
            if !known {
                missing.push(format!("{field}: {value:?}"));
            }
        }
    };

    let compiled_sources = capabilities
        .sources
        .iter()
        .map(|source| source.source_name.as_str())
        .collect::<BTreeSet<_>>();
    for cell in &values.cells {
        require("cell", Some(cell), compiled_sources.contains(cell.as_str()));
    }
    require(
        "model",
        Some(values.model.as_str()),
        capabilities.models.iter().any(|item| item.name == values.model),
    );
    require(
        "eval",
        Some(values.eval.as_str()),
        capabilities.evals.contains(&values.eval),
    );
    require(
        "loader",
        Some(values.loader.as_str()),
        capabilities.loaders.contains(&values.loader),
    );
    require(
        "train",
        Some(values.train.as_str()),
        capabilities.trains.contains(&values.train),
    );
    // The remaining four are omitted from an identity whose legacy default
    // applies, so an absent field is nothing to check rather than a miss.
    let architecture = values.fern_architecture.as_deref();
    require(
        "fern_architecture",
        architecture,
        architecture.is_none_or(|value| capabilities.fern_architectures.iter().any(|known| known == value)),
    );
    let density = values.fern_density.as_ref().map(|value| value.variant.as_str());
    require(
        "fern_density",
        density,
        density.is_none_or(|value| capabilities.fern_densities.iter().any(|known| known == value)),
    );
    let split = values.split_treatment_scheme.map(|value| value.value());
    require(
        "split_treatment_scheme",
        split,
        split.is_none_or(|value| capabilities.split_treatment_schemes.iter().any(|known| known == value)),
    );
    let checkpoint = values.checkpoint_reuse_policy.map(|value| value.value());
    require(
        "checkpoint_reuse_policy",
        checkpoint,
        checkpoint.is_none_or(|value| {
            capabilities
                .checkpoint_reuse_policies
                .iter()
                .any(|known| known == value)
        }),
    );
    let noise = values.noise_kind.map(|value| value.value());
    require(
        "noise_kind",
        noise,
        noise.is_none_or(|value| capabilities.noise_kinds.iter().any(|known| known == value)),
    );

    missing
}

/// One author-facing declaration linked to the Research Design that owns it.
///
/// `recipe_hash` is not a second identity algorithm. Validation recomputes it
/// from `identity` using the historical 16-hex rule and refuses disagreement.
///
/// Retired 2026-08-28: C# `RecipeExtensionCli` now owns external declaration
/// admission and historical hash binding. The former Rust body stays below as
/// an inert recovery record until the migration record's old-executable
/// fixture is superseded; it is not compiled or reachable by a caller.
#[cfg(any())]
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RecipeExtensionV1 {
    pub schema: String,
    pub extension_id: String,
    pub title: String,
    pub belongs_to: String,
    pub recipe_hash: String,
    pub identity: LegacyRecipeIdentityV1,
}

#[cfg(any())]
impl fern_json_contract::JsonContract for RecipeExtensionV1 {
    const SCHEMA_ID: &'static str = RECIPE_EXTENSION_SCHEMA;
}

/// Exact historical recipe-hash preimage envelope.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct LegacyRecipeIdentityV1 {
    pub schema: String,
    pub recipe: LegacyRecipeIdentityValuesV1,
}

/// Exact historical recipe fields, including the old omit-default rules.
///
/// Optional fields are absent from canonical JSON when their legacy default
/// applies. Explicitly writing a default is refused instead of producing a
/// second byte representation for the same scientific declaration.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct LegacyRecipeIdentityValuesV1 {
    pub name: String,
    pub family: String,
    pub cells: Vec<String>,
    pub model: String,
    pub eval: String,
    pub loader: String,
    pub train: String,
    pub plan: String,
    pub seq_len: i64,
    pub pred_lens: Vec<i64>,
    pub noise_levels: Vec<f32>,
    pub seeds: Vec<i64>,
    pub n_trajectory_draws: i64,
    pub n_noise_draws: i64,
    pub run_whiten: bool,
    pub run_rewind: bool,
    pub scale_noise_by_std: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub single_clean_anchor: Option<bool>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub split_treatment_scheme: Option<SplitTreatmentScheme>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub noise_kind: Option<fern_experiment_types::NoiseKind>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub noise_ar1_phi: Option<f64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub checkpoint_reuse_policy: Option<CheckpointReusePolicy>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub bootstrap: Option<RecipeBootstrapIdentityV1>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fern_architecture: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub source_std: Option<f64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fern_density: Option<RecipeFernDensityIdentityV1>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub fern_radial_head: Option<bool>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RecipeBootstrapIdentityV1 {
    pub ensemble_size: i64,
    pub block_size: i64,
    pub resample_seed_base: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RecipeFernDensityIdentityV1 {
    pub variant: String,
    pub component_count: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sparse_active_component_count: Option<u64>,
}

/// Parsed extension whose bounded handler names and legacy hash have passed.
///
/// Construction is private to [`RecipeExtensionV1::validate`], so runtime code
/// cannot accidentally treat raw file strings as worker capabilities.
#[cfg(any())]
#[derive(Debug, Clone, PartialEq)]
pub struct ValidatedRecipeExtensionV1 {
    extension: RecipeExtensionV1,
    blueprint: RecipeBlueprint,
}

#[cfg(any())]
impl RecipeExtensionV1 {
    /// Validate one extension at the file boundary.
    pub fn validate(self) -> Result<ValidatedRecipeExtensionV1, String> {
        require_exact("schema", &self.schema, RECIPE_EXTENSION_SCHEMA)?;
        require_prefixed_identity("extension_id", &self.extension_id, "recipe-extension:")?;
        require_canonical_text("title", &self.title)?;
        require_prefixed_identity("belongs_to", &self.belongs_to, "research-design:")?;
        require_lower_hex_16("recipe_hash", &self.recipe_hash)?;
        require_exact("identity.schema", &self.identity.schema, LEGACY_RECIPE_IDENTITY_SCHEMA)?;
        validate_identity_values(&self.identity.recipe)?;

        let blueprint = recipe_blueprint_from_legacy_identity(&self.identity)?;
        let observed_hash = legacy_recipe_hash(&self.identity)?;
        if self.recipe_hash != observed_hash {
            return Err(format!(
                "recipe_hash {:?} disagrees with the legacy identity preimage; expected {observed_hash}",
                self.recipe_hash
            ));
        }

        Ok(ValidatedRecipeExtensionV1 {
            extension: self,
            blueprint,
        })
    }
}

/// Reconstruct one executable recipe from a frozen, validated identity.
///
/// The identity contains data only. Every named handler remains bounded by an
/// exhaustive Fern enum. Source cells are checked against installed world
/// capabilities, while their grouping and the forecast grid come entirely
/// from the declaration rather than from a compiled catalog entry.
#[cfg(any())]
pub fn recipe_blueprint_from_legacy_identity(identity: &LegacyRecipeIdentityV1) -> Result<RecipeBlueprint, String> {
    require_exact("identity.schema", &identity.schema, LEGACY_RECIPE_IDENTITY_SCHEMA)?;
    validate_identity_values(&identity.recipe)?;
    let values = &identity.recipe;
    let model = RecipeModelName::parse(&values.model)?;
    let eval = RecipeEvalName::parse(&values.eval)?;
    let loader = RecipeLoaderName::parse(&values.loader)?;
    let train = RecipeTrainName::parse(&values.train)?;
    let family = RecipeSourceSet::new(values.family.clone(), values.cells.clone())?;
    let plan = RecipeGridSpec::new(
        values.plan.clone(),
        values.seq_len,
        values.pred_lens.clone(),
        values.noise_levels.clone(),
    )?;
    let blueprint = RecipeBlueprint {
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
            .map(FernArchitectureVariant::parse)
            .transpose()?,
        bootstrap: values.bootstrap.map(|value| crate::RecipeBootstrapSpec {
            ensemble_size: value.ensemble_size,
            block_size: value.block_size,
            resample_seed_base: value.resample_seed_base,
        }),
        // A scalar needs no handler lookup, which is the whole point of it
        // being data: a JSON declaration may name a value this binary has
        // never seen, and the run is still exactly reproducible.
        source_std: values.source_std,
        fern_density: values
            .fern_density
            .as_ref()
            .map(|density| {
                let variant = fern_experiment_types::FernDensityVariantV1::parse(&density.variant)
                    .map_err(|error| error.to_string())?;
                RecipeFernDensitySpec::new(variant, density.component_count, density.sparse_active_component_count)
            })
            .transpose()?,
        fern_radial_head: values.fern_radial_head.unwrap_or(false),
    };
    if blueprint.name() != values.name {
        return Err(format!(
            "identity.recipe.name {:?} disagrees with the name derived from its fields {:?}",
            values.name,
            blueprint.name()
        ));
    }
    Ok(blueprint)
}

#[cfg(any())]
impl ValidatedRecipeExtensionV1 {
    pub fn extension(&self) -> &RecipeExtensionV1 {
        &self.extension
    }

    pub fn family(&self) -> &RecipeSourceSet {
        &self.blueprint.family
    }

    pub fn model(&self) -> RecipeModelName {
        self.blueprint.model
    }

    pub fn eval(&self) -> RecipeEvalName {
        self.blueprint.eval
    }

    pub fn loader(&self) -> RecipeLoaderName {
        self.blueprint.loader
    }

    pub fn train(&self) -> RecipeTrainName {
        self.blueprint.train
    }

    pub fn plan(&self) -> &RecipeGridSpec {
        &self.blueprint.plan
    }

    pub fn blueprint(&self) -> &RecipeBlueprint {
        &self.blueprint
    }
}

/// Reproduce the historical 16-hex recipe hash exactly.
#[cfg(any())]
pub fn legacy_recipe_hash(identity: &LegacyRecipeIdentityV1) -> Result<String, String> {
    // The historical path hashed a `serde_json::Value`. Its map uses sorted
    // keys, while direct struct serialization preserves declaration order.
    // Equal JSON meaning is therefore not enough: reproduce the old byte
    // preimage before hashing so migration cannot rename a recipe.
    let canonical_value =
        serde_json::to_value(identity).map_err(|error| format!("cannot project legacy recipe identity: {error}"))?;
    let canonical_json = serde_json::to_string(&canonical_value)
        .map_err(|error| format!("cannot serialize legacy recipe identity: {error}"))?;
    let full = format!("{:x}", Sha256::digest(canonical_json.as_bytes()));
    Ok(full[..16].to_string())
}

/// Project one current Rust blueprint into the external extension shape.
///
/// This is the migration oracle: shadow fixtures compare its hash and bytes
/// against the pre-existing CLI identity path before any runtime cutover.
#[cfg(any())]
pub fn recipe_extension_from_blueprint(
    extension_id: impl Into<String>,
    title: impl Into<String>,
    belongs_to: impl Into<String>,
    recipe: RecipeBlueprint,
) -> Result<RecipeExtensionV1, String> {
    let identity = legacy_identity_from_blueprint(recipe);
    let recipe_hash = legacy_recipe_hash(&identity)?;
    Ok(RecipeExtensionV1 {
        schema: RECIPE_EXTENSION_SCHEMA.to_string(),
        extension_id: extension_id.into(),
        title: title.into(),
        belongs_to: belongs_to.into(),
        recipe_hash,
        identity,
    })
}

// CHG 2026-08-28: pub, so `fern-run list-recipes --format json` can print every
// recipe's identity record and hash — the data the C# port of recipe_hash is
// proved against (migration-plan run-identity-moves-to-csharp).
#[cfg(any())]
pub fn legacy_identity_from_blueprint(recipe: RecipeBlueprint) -> LegacyRecipeIdentityV1 {
    LegacyRecipeIdentityV1 {
        schema: LEGACY_RECIPE_IDENTITY_SCHEMA.to_string(),
        recipe: LegacyRecipeIdentityValuesV1 {
            name: recipe.name(),
            family: recipe.family.value().to_string(),
            cells: recipe.family.cells().to_vec(),
            model: recipe.model.value().to_string(),
            eval: recipe.eval.value().to_string(),
            loader: recipe.loader.value().to_string(),
            train: recipe.train.value().to_string(),
            plan: recipe.plan.value().to_string(),
            seq_len: recipe.plan.seq_len(),
            pred_lens: recipe.plan.pred_lens().to_vec(),
            noise_levels: recipe.plan.noise_levels().to_vec(),
            seeds: recipe.seeds.clone(),
            n_trajectory_draws: recipe.n_trajectory_draws,
            n_noise_draws: recipe.n_noise_draws,
            run_whiten: recipe.run_whiten,
            run_rewind: recipe.run_rewind,
            scale_noise_by_std: recipe.scale_noise_by_std,
            single_clean_anchor: recipe.single_clean_anchor.then_some(true),
            split_treatment_scheme: recipe.split_treatment_scheme,
            noise_kind: (recipe.noise_kind != fern_experiment_types::NoiseKind::Gaussian).then_some(recipe.noise_kind),
            noise_ar1_phi: (recipe.noise_ar1_phi != 0.0).then_some(recipe.noise_ar1_phi),
            checkpoint_reuse_policy: recipe.checkpoint_reuse_policy,
            bootstrap: recipe.bootstrap.map(|bootstrap| RecipeBootstrapIdentityV1 {
                ensemble_size: bootstrap.ensemble_size,
                block_size: bootstrap.block_size,
                resample_seed_base: bootstrap.resample_seed_base,
            }),
            fern_architecture: recipe.fern_architecture.map(|value| value.value().to_string()),
            source_std: recipe.source_std,
            fern_density: recipe.fern_density.map(|density| RecipeFernDensityIdentityV1 {
                variant: density.variant().value().to_string(),
                component_count: density.component_count(),
                sparse_active_component_count: density.sparse_active_component_count(),
            }),
            fern_radial_head: recipe.fern_radial_head.then_some(true),
        },
    }
}

#[cfg(any())]
fn validate_identity_values(values: &LegacyRecipeIdentityValuesV1) -> Result<(), String> {
    for (field, value) in [
        ("identity.recipe.name", values.name.as_str()),
        ("identity.recipe.family", values.family.as_str()),
        ("identity.recipe.model", values.model.as_str()),
        ("identity.recipe.eval", values.eval.as_str()),
        ("identity.recipe.loader", values.loader.as_str()),
        ("identity.recipe.train", values.train.as_str()),
        ("identity.recipe.plan", values.plan.as_str()),
    ] {
        require_canonical_text(field, value)?;
    }
    require_unique_nonempty("identity.recipe.cells", &values.cells)?;
    if values.seeds.is_empty() {
        return Err("identity.recipe.seeds must not be empty".to_string());
    }
    let unique_seeds = values.seeds.iter().copied().collect::<BTreeSet<_>>();
    if unique_seeds.len() != values.seeds.len() {
        return Err("identity.recipe.seeds must not repeat a model seed".to_string());
    }
    if values.pred_lens.is_empty() || values.pred_lens.iter().any(|value| *value <= 0) {
        return Err("identity.recipe.pred_lens must contain positive horizons".to_string());
    }
    if values.noise_levels.is_empty()
        || values
            .noise_levels
            .iter()
            .any(|value| !value.is_finite() || *value < 0.0)
    {
        return Err("identity.recipe.noise_levels must contain finite nonnegative values".to_string());
    }
    if values.seq_len <= 0 || values.n_trajectory_draws <= 0 || values.n_noise_draws <= 0 {
        return Err("identity recipe lengths and draw counts must be positive".to_string());
    }
    if values.single_clean_anchor == Some(false) {
        return Err("identity.recipe.single_clean_anchor=false is noncanonical; omit the legacy default".to_string());
    }
    if values.noise_kind == Some(fern_experiment_types::NoiseKind::Gaussian) {
        return Err("identity.recipe.noise_kind=gaussian is noncanonical; omit the legacy default".to_string());
    }
    if values.noise_ar1_phi == Some(0.0) {
        return Err("identity.recipe.noise_ar1_phi=0 is noncanonical; omit the legacy default".to_string());
    }
    if let Some(phi) = values.noise_ar1_phi {
        if !phi.is_finite() || !(-1.0..1.0).contains(&phi) {
            return Err("identity.recipe.noise_ar1_phi must be finite and strictly between -1 and 1".to_string());
        }
    }
    if let Some(bootstrap) = values.bootstrap {
        if bootstrap.ensemble_size <= 0 || bootstrap.block_size <= 0 {
            return Err("identity.recipe.bootstrap counts must be positive".to_string());
        }
    }
    if let Some(architecture) = &values.fern_architecture {
        FernArchitectureVariant::parse(architecture)?;
    }
    if let Some(density) = &values.fern_density {
        let variant =
            fern_experiment_types::FernDensityVariantV1::parse(&density.variant).map_err(|error| error.to_string())?;
        RecipeFernDensitySpec::new(variant, density.component_count, density.sparse_active_component_count)?;
    }
    Ok(())
}

#[cfg(any())]
fn require_canonical_text(field: &str, value: &str) -> Result<(), String> {
    if value.is_empty() || value.trim() != value || value.chars().any(char::is_control) {
        return Err(format!(
            "{field} must be trimmed, non-empty text without control characters"
        ));
    }
    Ok(())
}

#[cfg(any())]
fn require_prefixed_identity(field: &str, value: &str, prefix: &str) -> Result<(), String> {
    require_canonical_text(field, value)?;
    let tail = value
        .strip_prefix(prefix)
        .ok_or_else(|| format!("{field} must start with {prefix:?}"))?;
    if tail.is_empty() || tail.chars().any(char::is_whitespace) {
        return Err(format!("{field} must contain a non-empty identity after {prefix:?}"));
    }
    Ok(())
}

#[cfg(any())]
fn require_exact(field: &str, observed: &str, expected: &str) -> Result<(), String> {
    if observed == expected {
        Ok(())
    } else {
        Err(format!("{field} must be {expected:?}, found {observed:?}"))
    }
}

#[cfg(any())]
fn require_lower_hex_16(field: &str, value: &str) -> Result<(), String> {
    if value.len() == 16
        && value
            .chars()
            .all(|character| character.is_ascii_digit() || ('a'..='f').contains(&character))
    {
        Ok(())
    } else {
        Err(format!("{field} must be exactly 16 lowercase hexadecimal characters"))
    }
}

#[cfg(any())]
fn require_unique_nonempty(field: &str, values: &[String]) -> Result<(), String> {
    if values.is_empty() {
        return Err(format!("{field} must not be empty"));
    }
    let mut seen = BTreeSet::new();
    for value in values {
        require_canonical_text(field, value)?;
        if !seen.insert(value) {
            return Err(format!("{field} repeats {value:?}"));
        }
    }
    Ok(())
}

// Retired source-test inventory. C# keeps the exact semantic test names and
// reads the checked-in old-executable golden; compiling this old validator
// would recreate a second active admission authority.
#[cfg(any())]
mod tests {
    use super::*;

    fn canary() -> RecipeExtensionV1 {
        recipe_extension_from_blueprint(
            "recipe-extension:lgssm-fern-clean-h64",
            "LGSSM Fern clean H64",
            "research-design:fixture",
            RecipeBlueprint::new(RecipeFamilyName::LgSsmBase),
        )
        .unwrap()
    }

    /// Write the executable Rust oracle the C# reader must consume.
    ///
    /// Recipe extensions carry the old Rust recipe preimage, whose hash uses
    /// sorted `serde_json::Value` keys rather than declaration-order struct
    /// JSON. The fixture therefore retains both the complete extension and
    /// those exact preimage bytes: a C# reader checked only against a hash it
    /// computed itself could silently choose the wrong serialization family.
    ///
    /// The checked-in declarations cover the public author-facing records;
    /// the final entry proves the deliberately unbounded `source_std` scalar
    /// travels as identity data without adding a compiled vocabulary member.
    #[test]
    fn write_recipe_extension_v1_golden_for_csharp() {
        fn entry(label: String, extension: RecipeExtensionV1) -> serde_json::Value {
            extension.clone().validate().unwrap();
            let declaration_identity_json = serde_json::to_string(&extension.identity).unwrap();
            let canonical_identity_json =
                serde_json::to_string(&serde_json::to_value(&extension.identity).unwrap()).unwrap();
            assert_eq!(extension.recipe_hash, legacy_recipe_hash(&extension.identity).unwrap());
            serde_json::json!({
                "label": label,
                "extension": extension,
                "canonical_identity_json": canonical_identity_json,
                "recipe_identity_sha256": format!("sha256:{:x}", Sha256::digest(declaration_identity_json.as_bytes())),
            })
        }

        let recipe_directory = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../docs/recipes");
        let mut paths = std::fs::read_dir(&recipe_directory)
            .unwrap()
            .map(|item| item.unwrap().path())
            .filter(|path| path.extension().is_some_and(|extension| extension == "json"))
            .collect::<Vec<_>>();
        paths.sort();

        let mut entries = paths
            .into_iter()
            .map(|path| {
                let label = format!("docs/recipes/{}", path.file_name().unwrap().to_string_lossy());
                let extension = serde_json::from_slice::<RecipeExtensionV1>(&std::fs::read(path).unwrap()).unwrap();
                entry(label, extension)
            })
            .collect::<Vec<_>>();

        let unseen_source_std = RecipeBlueprint::new(RecipeFamilyName::LgSsmBase).source_std(3.75);
        entries.push(entry(
            "generated/unseen-source-std-3.75".to_string(),
            recipe_extension_from_blueprint(
                "recipe-extension:unseen-source-std",
                unseen_source_std.name(),
                "research-design:fixture",
                unseen_source_std,
            )
            .unwrap(),
        ));

        for (variant, component_count, active_component_count) in [
            (fern_experiment_types::FernDensityVariantV1::BoundedMdn, 3, None),
            (fern_experiment_types::FernDensityVariantV1::SparseMoe, 4, Some(2)),
            (
                fern_experiment_types::FernDensityVariantV1::SharedTransportMixtureSource,
                3,
                None,
            ),
            (
                fern_experiment_types::FernDensityVariantV1::MixtureOfFernTransports,
                3,
                None,
            ),
        ] {
            let density = RecipeFernDensitySpec::new(variant, component_count, active_component_count).unwrap();
            let declared = RecipeBlueprint::new(RecipeFamilyName::LgSsmBase).fern_density(density);
            entries.push(entry(
                format!("generated/density/{}", variant.value()),
                recipe_extension_from_blueprint(
                    format!("recipe-extension:{}", variant.value()),
                    declared.name(),
                    "research-design:fixture",
                    declared,
                )
                .unwrap(),
            ));
        }

        let mut wrong_hash = canary();
        wrong_hash.recipe_hash = "0".repeat(16);
        let wrong_hash_error = wrong_hash.clone().validate().unwrap_err();

        let mut changed_identity = canary();
        changed_identity.identity.recipe.n_noise_draws += 1;
        let changed_identity_error = changed_identity.clone().validate().unwrap_err();

        let mut explicit_default = canary();
        explicit_default.identity.recipe.noise_kind = Some(fern_experiment_types::NoiseKind::Gaussian);
        let explicit_default_error = explicit_default.clone().validate().unwrap_err();

        let mut unknown_field = serde_json::to_value(canary()).unwrap();
        unknown_field["invented"] = serde_json::json!(true);
        let unknown_field_error = serde_json::from_value::<RecipeExtensionV1>(unknown_field.clone())
            .unwrap_err()
            .to_string();

        let mut repeated_seed = canary();
        repeated_seed.identity.recipe.seeds = vec![42, 42];
        repeated_seed.recipe_hash = legacy_recipe_hash(&repeated_seed.identity).unwrap();
        let repeated_seed_error = repeated_seed.clone().validate().unwrap_err();

        let mut unknown_eval = canary();
        unknown_eval.identity.recipe.eval = "future_eval_not_compiled".to_string();
        unknown_eval.recipe_hash = legacy_recipe_hash(&unknown_eval.identity).unwrap();
        let unknown_eval_error = unknown_eval.clone().validate().unwrap_err();

        let current_handler_capabilities = recipe_handler_capabilities();

        let missing_model_identity = canary().identity;
        let mut older_model_capabilities = recipe_handler_capabilities();
        older_model_capabilities.models.retain(|model| model.name != "fern");
        let missing_model = handler_names_absent_from_capabilities(
            &missing_model_identity,
            &older_model_capabilities,
        );

        let density = RecipeFernDensitySpec::new(
            fern_experiment_types::FernDensityVariantV1::BoundedMdn,
            3,
            None,
        )
        .unwrap();
        let missing_density_identity = recipe_extension_from_blueprint(
            "recipe-extension:bounded-mdn",
            "bounded MDN",
            "research-design:fixture",
            RecipeBlueprint::new(RecipeFamilyName::LgSsmBase).fern_density(density),
        )
        .unwrap()
        .identity;
        let mut older_density_capabilities = recipe_handler_capabilities();
        older_density_capabilities
            .fern_densities
            .retain(|value| value != "bounded_mdn");
        let missing_density = handler_names_absent_from_capabilities(
            &missing_density_identity,
            &older_density_capabilities,
        );

        let unused_extra_identity = canary().identity;
        let mut wider_capabilities = recipe_handler_capabilities();
        wider_capabilities
            .evals
            .push("an_evaluator_this_recipe_never_names".to_string());
        let wider_missing = handler_names_absent_from_capabilities(
            &unused_extra_identity,
            &wider_capabilities,
        );
        let current_missing = handler_names_absent_from_capabilities(
            &unused_extra_identity,
            &current_handler_capabilities,
        );

        let mut unknown_cell_identity = canary().identity;
        unknown_cell_identity.recipe.family = "future_world_group".to_string();
        unknown_cell_identity.recipe.cells = vec!["world_handler_not_installed".to_string()];
        let missing_cell = handler_names_absent_from_capabilities(
            &unknown_cell_identity,
            &current_handler_capabilities,
        );

        let catalog_capabilities = recipe_handler_capabilities();
        let catalog_projection = crate::recipe_catalog_builds()
            .into_iter()
            .enumerate()
            .map(|(index, recipe)| {
                let name = recipe.name();
                let extension = recipe_extension_from_blueprint(
                    format!("recipe-extension:catalog-{index}"),
                    name.clone(),
                    "research-design:fixture",
                    recipe,
                )
                .unwrap();
                extension.clone().validate().unwrap();
                let identity_json = serde_json::to_value(&extension.identity).unwrap();
                let source_std_is_none = extension.identity.recipe.source_std.is_none();
                let source_std_member_present = identity_json["recipe"].get("source_std").is_some();
                let missing = handler_names_absent_from_capabilities(
                    &extension.identity,
                    &catalog_capabilities,
                );
                serde_json::json!({
                    "index": index,
                    "name": name,
                    "extension": extension,
                    "validated": true,
                    "source_std_is_none": source_std_is_none,
                    "source_std_member_present": source_std_member_present,
                    "missing": missing,
                })
            })
            .collect::<Vec<_>>();
        assert_eq!(catalog_projection.len(), 506);

        let mut external_extension = canary();
        {
            let values = &mut external_extension.identity.recipe;
            values.family = "counterfactual_pair".to_string();
            values.cells = vec!["lg_ssm".to_string(), "lg_ssm_switch".to_string()];
            values.plan = "action_probe_h48_h96".to_string();
            values.seq_len = 192;
            values.pred_lens = vec![48, 96];
            values.noise_levels = vec![0.0, 0.2];
        }
        let stale_external_identity = external_extension.identity.clone();
        let stale_name_error = recipe_blueprint_from_legacy_identity(&external_extension.identity).unwrap_err();
        let values = &external_extension.identity.recipe;
        let reconstructed = RecipeBlueprint {
            family: RecipeSourceSet::new(values.family.clone(), values.cells.clone()).unwrap(),
            model: RecipeModelName::Fern,
            eval: RecipeEvalName::Huber,
            loader: RecipeLoaderName::P70_20_10NoneBs95,
            train: RecipeTrainName::Default,
            plan: RecipeGridSpec::new(
                values.plan.clone(),
                values.seq_len,
                values.pred_lens.clone(),
                values.noise_levels.clone(),
            )
            .unwrap(),
            seeds: values.seeds.clone(),
            n_trajectory_draws: values.n_trajectory_draws,
            n_noise_draws: values.n_noise_draws,
            run_whiten: values.run_whiten,
            run_rewind: values.run_rewind,
            scale_noise_by_std: values.scale_noise_by_std,
            single_clean_anchor: false,
            noise_kind: fern_experiment_types::NoiseKind::Gaussian,
            noise_ar1_phi: 0.0,
            split_treatment_scheme: None,
            checkpoint_reuse_policy: None,
            fern_architecture: None,
            bootstrap: None,
            source_std: None,
            fern_density: None,
            fern_radial_head: false,
        };
        let reconstructed_identity = legacy_identity_from_blueprint(reconstructed.clone());
        external_extension.identity.recipe.name = reconstructed.name();
        external_extension.recipe_hash = legacy_recipe_hash(&external_extension.identity).unwrap();
        let checked_external = external_extension.clone().validate().unwrap();
        assert_eq!(checked_external.blueprint(), &reconstructed);
        let external_missing = handler_names_absent_from_capabilities(
            &external_extension.identity,
            &catalog_capabilities,
        );

        let refusals = serde_json::json!({
            "identity_or_claimed_hash_drift_is_refused": {
                "wrong_hash": {
                    "extension": wrong_hash,
                    "error": wrong_hash_error,
                },
                "changed_identity": {
                    "extension": changed_identity,
                    "error": changed_identity_error,
                },
            },
            "explicit_legacy_defaults_and_unknown_fields_are_refused": {
                "explicit_default": {
                    "extension": explicit_default,
                    "error": explicit_default_error,
                },
                "unknown_field": {
                    "extension": unknown_field,
                    "error": unknown_field_error,
                },
                "repeated_seed": {
                    "extension": repeated_seed,
                    "error": repeated_seed_error,
                },
            },
            "required_handler_names_are_validated_at_the_file_boundary": {
                "unknown_eval": {
                    "extension": unknown_eval,
                    "error": unknown_eval_error,
                },
            },
        });

        let handler_capability_cases = serde_json::json!({
            "handler_capabilities_are_derived_from_executable_semantics": {
                "capabilities": current_handler_capabilities,
            },
            "a_handler_the_other_binary_lacks_is_named_before_dispatch": {
                "identity": missing_model_identity,
                "capabilities": older_model_capabilities,
                "missing": missing_model,
            },
            "dispatcher_capabilities_name_the_density_a_recipe_requires": {
                "identity": missing_density_identity,
                "capabilities": older_density_capabilities,
                "missing": missing_density,
            },
            "unused_extra_capability_is_not_a_mismatch": {
                "identity": unused_extra_identity,
                "wider_capabilities": wider_capabilities,
                "wider_missing": wider_missing,
                "current_missing": current_missing,
            },
            "an_unknown_world_cell_is_refused_before_dispatch": {
                "identity": unknown_cell_identity,
                "missing": missing_cell,
            },
        });

        let catalog_cases = serde_json::json!({
            "every_current_recipe_projects_to_a_valid_external_extension": {
                "validated_count": catalog_projection.iter().filter(|row| row["validated"] == true).count(),
            },
            "omitting_the_base_cloud_width_moves_no_existing_recipe": {
                "eligible_indices": catalog_projection
                    .iter()
                    .filter(|row| row["source_std_is_none"] == true)
                    .map(|row| row["index"].clone())
                    .collect::<Vec<_>>(),
                "serialized_member_present_indices": catalog_projection
                    .iter()
                    .filter(|row| row["source_std_is_none"] == true && row["source_std_member_present"] == true)
                    .map(|row| row["index"].clone())
                    .collect::<Vec<_>>(),
            },
            "external_source_group_and_grid_need_no_compiled_enum_variant": {
                "stale_identity": stale_external_identity,
                "stale_name_error": stale_name_error,
                "reconstructed_identity": reconstructed_identity,
                "extension": external_extension,
                "missing": external_missing,
            },
            "every_catalog_recipe_is_nameable_by_its_own_capabilities": {
                "checked_count": catalog_projection.len(),
                "rows_with_missing_handlers": catalog_projection
                    .iter()
                    .filter(|row| row["missing"].as_array().is_some_and(|missing| !missing.is_empty()))
                    .map(|row| row["index"].clone())
                    .collect::<Vec<_>>(),
            },
        });

        let source_directory = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
        let mut catalog_source_paths = std::fs::read_dir(&source_directory)
            .unwrap()
            .map(|item| item.unwrap().path())
            .filter(|path| path.extension().is_some_and(|extension| extension == "rs"))
            .collect::<Vec<_>>();
        catalog_source_paths.sort();
        let catalog_sources = catalog_source_paths
            .iter()
            .map(|path| {
                serde_json::json!({
                    "source_path": format!(
                        "crates/fern-recipes/src/{}",
                        path.file_name().unwrap().to_string_lossy(),
                    ),
                    "sha256": format!("{:x}", Sha256::digest(std::fs::read(path).unwrap())),
                })
            })
            .collect::<Vec<_>>();
        let oracle_source_commit = option_env!("FERN_RECIPE_ORACLE_SOURCE_COMMIT")
            .expect("set FERN_RECIPE_ORACLE_SOURCE_COMMIT before writing the C# proof fixture");
        let catalog_entries = catalog_projection
            .iter()
            .map(|row| {
                serde_json::json!({
                    "index": row["index"].clone(),
                    "name": row["name"].clone(),
                    "extension": row["extension"].clone(),
                })
            })
            .collect::<Vec<_>>();
        let catalog_artifact = serde_json::json!({
            "schema": "fern.recipe-catalog.v1",
            "oracle": {
                "crate": env!("CARGO_PKG_NAME"),
                "test": "write_recipe_extension_v1_golden_for_csharp",
                "source_commit": oracle_source_commit,
                "sources": catalog_sources,
            },
            "entries": catalog_entries,
        });
        let catalog_bytes = serde_json::to_string_pretty(&catalog_artifact).unwrap() + "\n";
        let catalog_sha256 = format!("{:x}", Sha256::digest(catalog_bytes.as_bytes()));
        let catalog_directory = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../csharp/Fern.Experiment/Catalog");
        std::fs::create_dir_all(&catalog_directory).unwrap();
        let catalog_path = catalog_directory.join("recipe_catalog_v1.json");
        std::fs::write(&catalog_path, catalog_bytes).unwrap();

        let source_path = source_directory.join("extension.rs");
        let source_sha256 = format!("{:x}", Sha256::digest(std::fs::read(source_path).unwrap()));
        let golden = serde_json::json!({
            "schema": "fern.recipe_extension_v1_rust_golden.v1",
            "note": "RecipeExtensionV1 validated by the Rust executable. recipe_hash = first 16 lowercase hex of sha256 over canonical_identity_json, where serde_json::Value sorts object keys.",
            "oracle": {
                "crate": env!("CARGO_PKG_NAME"),
                "test": "write_recipe_extension_v1_golden_for_csharp",
                "source_commit": oracle_source_commit,
                "extension_source_sha256": source_sha256,
            },
            "source_tests": [
                {
                    "source_path": "crates/fern-recipes/src/extension.rs",
                    "test_name": "every_current_recipe_projects_to_a_valid_external_extension",
                    "proof_kind": "rust_executable_full_output",
                },
                {
                    "source_path": "crates/fern-recipes/src/extension.rs",
                    "test_name": "identity_or_claimed_hash_drift_is_refused",
                    "proof_kind": "rust_executable_full_output",
                },
                {
                    "source_path": "crates/fern-recipes/src/extension.rs",
                    "test_name": "explicit_legacy_defaults_and_unknown_fields_are_refused",
                    "proof_kind": "rust_executable_full_output",
                },
                {
                    "source_path": "crates/fern-recipes/src/extension.rs",
                    "test_name": "required_handler_names_are_validated_at_the_file_boundary",
                    "proof_kind": "rust_executable_full_output",
                },
                {
                    "source_path": "crates/fern-recipes/src/extension.rs",
                    "test_name": "handler_capabilities_are_derived_from_executable_semantics",
                    "proof_kind": "rust_executable_full_output",
                },
                {
                    "source_path": "crates/fern-recipes/src/extension.rs",
                    "test_name": "a_handler_the_other_binary_lacks_is_named_before_dispatch",
                    "proof_kind": "rust_executable_full_output",
                },
                {
                    "source_path": "crates/fern-recipes/src/extension.rs",
                    "test_name": "an_unseen_base_cloud_width_arrives_as_data_and_round_trips",
                    "proof_kind": "rust_executable_full_output",
                },
                {
                    "source_path": "crates/fern-recipes/src/extension.rs",
                    "test_name": "omitting_the_base_cloud_width_moves_no_existing_recipe",
                    "proof_kind": "rust_executable_full_output",
                },
                {
                    "source_path": "crates/fern-recipes/src/extension.rs",
                    "test_name": "all_four_finite_density_routes_round_trip_as_recipe_data",
                    "proof_kind": "rust_executable_full_output",
                },
                {
                    "source_path": "crates/fern-recipes/src/extension.rs",
                    "test_name": "dispatcher_capabilities_name_the_density_a_recipe_requires",
                    "proof_kind": "rust_executable_full_output",
                },
                {
                    "source_path": "crates/fern-recipes/src/extension.rs",
                    "test_name": "unused_extra_capability_is_not_a_mismatch",
                    "proof_kind": "rust_executable_full_output",
                },
                {
                    "source_path": "crates/fern-recipes/src/extension.rs",
                    "test_name": "external_source_group_and_grid_need_no_compiled_enum_variant",
                    "proof_kind": "rust_executable_full_output",
                },
                {
                    "source_path": "crates/fern-recipes/src/extension.rs",
                    "test_name": "an_unknown_world_cell_is_refused_before_dispatch",
                    "proof_kind": "rust_executable_full_output",
                },
                {
                    "source_path": "crates/fern-recipes/src/extension.rs",
                    "test_name": "every_catalog_recipe_is_nameable_by_its_own_capabilities",
                    "proof_kind": "rust_executable_full_output",
                },
            ],
            "entries": entries,
            "refusals": refusals,
            "handler_capability_cases": handler_capability_cases,
            "catalog_artifact": {
                "schema": "fern.recipe-catalog.v1",
                "source_path": "csharp/Fern.Experiment/Catalog/recipe_catalog_v1.json",
                "catalog_count": catalog_projection.len(),
                "sha256": catalog_sha256,
            },
            "catalog_cases": catalog_cases,
        });

        let fixture_directory = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("fixtures");
        std::fs::create_dir_all(&fixture_directory).unwrap();
        let path = fixture_directory.join("recipe_extension_v1_rust_golden.json");
        std::fs::write(&path, serde_json::to_string_pretty(&golden).unwrap() + "\n").unwrap();

        let written = std::fs::read_to_string(&path).unwrap();
        assert!(written.contains("fern.recipe_extension_v1_rust_golden.v1"));
        assert!(written.contains("generated/unseen-source-std-3.75"));
        assert!(written.contains("generated/density/bounded_mdn"));
    }

    #[test]
    fn every_current_recipe_projects_to_a_valid_external_extension() {
        for (index, recipe) in crate::recipe_catalog_builds().into_iter().enumerate() {
            recipe_extension_from_blueprint(
                format!("recipe-extension:catalog-{index}"),
                recipe.name(),
                "research-design:fixture",
                recipe,
            )
            .unwrap()
            .validate()
            .unwrap();
        }
    }

    #[test]
    fn identity_or_claimed_hash_drift_is_refused() {
        let mut wrong_hash = canary();
        wrong_hash.recipe_hash = "0".repeat(16);
        assert!(wrong_hash.validate().unwrap_err().contains("disagrees"));

        let mut changed_identity = canary();
        changed_identity.identity.recipe.n_noise_draws += 1;
        assert!(changed_identity.validate().unwrap_err().contains("disagrees"));
    }

    #[test]
    fn explicit_legacy_defaults_and_unknown_fields_are_refused() {
        let mut explicit_default = canary();
        explicit_default.identity.recipe.noise_kind = Some(fern_experiment_types::NoiseKind::Gaussian);
        assert!(explicit_default.validate().unwrap_err().contains("noncanonical"));

        let mut value = serde_json::to_value(canary()).unwrap();
        value["invented"] = serde_json::json!(true);
        assert!(serde_json::from_value::<RecipeExtensionV1>(value).is_err());

        let mut repeated_seed = canary();
        repeated_seed.identity.recipe.seeds = vec![42, 42];
        repeated_seed.recipe_hash = legacy_recipe_hash(&repeated_seed.identity).unwrap();
        assert!(repeated_seed.validate().unwrap_err().contains("must not repeat"));
    }

    #[test]
    fn required_handler_names_are_validated_at_the_file_boundary() {
        let mut unknown = canary();
        unknown.identity.recipe.eval = "future_eval_not_compiled".to_string();
        unknown.recipe_hash = legacy_recipe_hash(&unknown.identity).unwrap();
        assert!(unknown.validate().unwrap_err().contains("unknown eval"));
    }

    #[test]
    fn handler_capabilities_are_derived_from_executable_semantics() {
        let capabilities = recipe_handler_capabilities();
        assert_eq!(capabilities.schema, RECIPE_HANDLER_CAPABILITIES_SCHEMA);
        assert_eq!(capabilities.recipe_extension_schema, RECIPE_EXTENSION_SCHEMA);
        assert_eq!(capabilities.families.len(), RecipeFamilyName::iter().count());
        assert!(!capabilities.sources.is_empty());
        assert_eq!(capabilities.models.len(), RecipeModelName::iter().count());
        assert_eq!(capabilities.plans.len(), RecipePlanName::iter().count());

        let lgssm = capabilities
            .families
            .iter()
            .find(|family| family.name == "lg_ssm_base")
            .expect("LGSSM family capability");
        assert_eq!(lgssm.cells, ["lg_ssm"]);
        let lgssm_source = capabilities
            .sources
            .iter()
            .find(|source| source.source_name == "lg_ssm")
            .expect("LGSSM source capability");
        assert_eq!(lgssm_source.draw_kind, crate::SourceDrawKind::StochasticTrajectory);
        assert_eq!(lgssm_source.data_engine, crate::DataEngine::SeriePy);

        let relative_h64 = capabilities
            .plans
            .iter()
            .find(|plan| plan.name == "titration_h64_relative_doses")
            .expect("relative-dose H64 plan capability");
        assert_eq!(relative_h64.pred_lens, [64]);
        assert_eq!(relative_h64.noise_levels, [0.0, 0.0625, 0.125, 0.25, 0.5, 1.0, 2.0]);
    }

    /// The exact 2026-08-17 failure: a plan froze `fern_wide_scale` while the
    /// installed dispatcher's vocabulary predated that model, so the plan
    /// registered, queued, and only then reported `unknown model`.
    #[test]
    fn a_handler_the_other_binary_lacks_is_named_before_dispatch() {
        let identity = canary().identity;
        let mut older = recipe_handler_capabilities();
        older.models.retain(|model| model.name != "fern");

        let missing = handler_names_absent_from_capabilities(&identity, &older);
        assert_eq!(missing, [r#"model: "fern""#]);
    }

    /// The reason `source_std` is a field and not a compiled variant name: a
    /// value this binary has never seen arrives as data, round-trips through
    /// the frozen identity, and reproduces exactly — no enum arm, no rebuild,
    /// no scheduler reinstall. Adding it as one architecture name per value
    /// cost all three before it was moved here.
    #[test]
    fn an_unseen_base_cloud_width_arrives_as_data_and_round_trips() {
        let declared = RecipeBlueprint::new(RecipeFamilyName::LgSsmBase).source_std(3.75);
        let extension = recipe_extension_from_blueprint(
            "recipe-extension:unseen-source-std",
            declared.name(),
            "research-design:fixture",
            declared.clone(),
        )
        .unwrap();

        // It is in the identity, so it is in the hash...
        assert_eq!(extension.identity.recipe.source_std, Some(3.75));
        assert_ne!(
            extension.recipe_hash,
            recipe_extension_from_blueprint(
                "recipe-extension:default-source-std",
                RecipeBlueprint::new(RecipeFamilyName::LgSsmBase).name(),
                "research-design:fixture",
                RecipeBlueprint::new(RecipeFamilyName::LgSsmBase),
            )
            .unwrap()
            .recipe_hash,
            "a declared base cloud width must move the recipe hash"
        );
        // ...and in the NAME, or two experiments collide on one name.
        assert!(declared.name().ends_with("_src_3p75"), "{}", declared.name());

        // A checked declaration reconstructs the identical recipe.
        let reopened = extension.validate().unwrap();
        assert_eq!(reopened.blueprint(), &declared);
    }

    /// The other half: omitting it must leave every recipe already in the
    /// catalog byte-identical, or moving this knob into data would orphan the
    /// completed campaign.
    #[test]
    fn omitting_the_base_cloud_width_moves_no_existing_recipe() {
        for recipe in crate::recipe_catalog_builds() {
            if recipe.source_std.is_some() {
                continue;
            }
            let identity = legacy_identity_from_blueprint(recipe.clone());
            assert_eq!(identity.recipe.source_std, None);
            let json = serde_json::to_value(&identity).unwrap();
            assert!(
                json["recipe"].get("source_std").is_none(),
                "recipe {} must not carry the field in its preimage",
                recipe.name()
            );
        }
    }

    #[test]
    fn all_four_finite_density_routes_round_trip_as_recipe_data() {
        for (variant, component_count, active_component_count) in [
            (fern_experiment_types::FernDensityVariantV1::BoundedMdn, 3, None),
            (fern_experiment_types::FernDensityVariantV1::SparseMoe, 4, Some(2)),
            (
                fern_experiment_types::FernDensityVariantV1::SharedTransportMixtureSource,
                3,
                None,
            ),
            (
                fern_experiment_types::FernDensityVariantV1::MixtureOfFernTransports,
                3,
                None,
            ),
        ] {
            let density = RecipeFernDensitySpec::new(variant, component_count, active_component_count).unwrap();
            let declared = RecipeBlueprint::new(RecipeFamilyName::LgSsmBase).fern_density(density);
            let extension = recipe_extension_from_blueprint(
                format!("recipe-extension:{}", variant.value()),
                declared.name(),
                "research-design:fixture",
                declared.clone(),
            )
            .unwrap();

            let identity_density = extension.identity.recipe.fern_density.as_ref().unwrap();
            assert_eq!(identity_density.variant, variant.value());
            assert_eq!(identity_density.component_count, component_count);
            assert_eq!(identity_density.sparse_active_component_count, active_component_count,);
            assert_eq!(extension.validate().unwrap().blueprint(), &declared);
        }
    }

    #[test]
    fn dispatcher_capabilities_name_the_density_a_recipe_requires() {
        let density =
            RecipeFernDensitySpec::new(fern_experiment_types::FernDensityVariantV1::BoundedMdn, 3, None).unwrap();
        let identity = recipe_extension_from_blueprint(
            "recipe-extension:bounded-mdn",
            "bounded MDN",
            "research-design:fixture",
            RecipeBlueprint::new(RecipeFamilyName::LgSsmBase).fern_density(density),
        )
        .unwrap()
        .identity;
        let mut older = recipe_handler_capabilities();
        older.fern_densities.retain(|value| value != "bounded_mdn");

        assert_eq!(
            handler_names_absent_from_capabilities(&identity, &older),
            [r#"fern_density: "bounded_mdn""#],
        );
    }

    /// A dispatcher that has learned something this recipe does not use can
    /// still run it; refusing there would fire on every unrelated change.
    #[test]
    fn unused_extra_capability_is_not_a_mismatch() {
        let identity = canary().identity;
        let mut wider = recipe_handler_capabilities();
        wider.evals.push("an_evaluator_this_recipe_never_names".to_string());

        assert!(handler_names_absent_from_capabilities(&identity, &wider).is_empty());
        assert!(handler_names_absent_from_capabilities(&identity, &recipe_handler_capabilities()).is_empty());
    }

    #[test]
    fn external_source_group_and_grid_need_no_compiled_enum_variant() {
        let mut extension = canary();
        {
            let values = &mut extension.identity.recipe;
            values.family = "counterfactual_pair".to_string();
            values.cells = vec!["lg_ssm".to_string(), "lg_ssm_switch".to_string()];
            values.plan = "action_probe_h48_h96".to_string();
            values.seq_len = 192;
            values.pred_lens = vec![48, 96];
            values.noise_levels = vec![0.0, 0.2];
        }
        let stale_name_error = recipe_blueprint_from_legacy_identity(&extension.identity).unwrap_err();
        assert!(stale_name_error.contains("identity.recipe.name"), "{stale_name_error}");

        let values = &extension.identity.recipe;
        let reconstructed = RecipeBlueprint {
            family: RecipeSourceSet::new(values.family.clone(), values.cells.clone()).unwrap(),
            model: RecipeModelName::Fern,
            eval: RecipeEvalName::Huber,
            loader: RecipeLoaderName::P70_20_10NoneBs95,
            train: RecipeTrainName::Default,
            plan: RecipeGridSpec::new(
                values.plan.clone(),
                values.seq_len,
                values.pred_lens.clone(),
                values.noise_levels.clone(),
            )
            .unwrap(),
            seeds: values.seeds.clone(),
            n_trajectory_draws: values.n_trajectory_draws,
            n_noise_draws: values.n_noise_draws,
            run_whiten: values.run_whiten,
            run_rewind: values.run_rewind,
            scale_noise_by_std: values.scale_noise_by_std,
            single_clean_anchor: false,
            noise_kind: fern_experiment_types::NoiseKind::Gaussian,
            noise_ar1_phi: 0.0,
            split_treatment_scheme: None,
            checkpoint_reuse_policy: None,
            fern_architecture: None,
            bootstrap: None,
            source_std: None,
            fern_density: None,
            fern_radial_head: false,
        };
        extension.identity.recipe.name = reconstructed.name();
        extension.recipe_hash = legacy_recipe_hash(&extension.identity).unwrap();

        let checked = extension.validate().unwrap();
        assert_eq!(checked.blueprint(), &reconstructed);
        assert!(
            handler_names_absent_from_capabilities(&checked.extension().identity, &recipe_handler_capabilities())
                .is_empty()
        );
    }

    #[test]
    fn an_unknown_world_cell_is_refused_before_dispatch() {
        let mut identity = canary().identity;
        identity.recipe.family = "future_world_group".to_string();
        identity.recipe.cells = vec!["world_handler_not_installed".to_string()];
        let missing = handler_names_absent_from_capabilities(&identity, &recipe_handler_capabilities());
        assert_eq!(missing, ["cell: \"world_handler_not_installed\""]);
    }

    /// Every recipe the catalog can build must be nameable by the vocabulary
    /// that built it, or registration would refuse a plan nothing is wrong with.
    #[test]
    fn every_catalog_recipe_is_nameable_by_its_own_capabilities() {
        let capabilities = recipe_handler_capabilities();
        for recipe in crate::recipe_catalog_builds() {
            let name = recipe.name();
            let extension = recipe_extension_from_blueprint(
                "recipe-extension:capability-closure",
                name.clone(),
                "research-design:fixture",
                recipe,
            )
            .unwrap();
            assert!(
                handler_names_absent_from_capabilities(&extension.identity, &capabilities).is_empty(),
                "catalog recipe {name} names a handler its own capability record does not"
            );
        }
    }
}
