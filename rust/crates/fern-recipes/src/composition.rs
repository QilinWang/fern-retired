//! Compositional experiment authoring and resolved worker manifests.
//!
//! V1 Recipe Extensions preserve the historical compiled-catalog identity.
//! V2 is deliberately a new identity family: an author supplies small typed
//! component cards inline or by content address, and `fern-knowledge` resolves
//! them into one complete manifest before any Plan may register.  The worker
//! reads only that resolved manifest; it never reopens mutable authoring files.

use std::collections::{BTreeMap, BTreeSet};

use fern_experiment_types::{ImplementationBundleHashV2, StagePipelineTemplateV1};
use fern_json_contract::JsonContract;
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};
use serde_json::Value;

#[cfg(any())]
use crate::published_dataset_source_capabilities;

pub const RECIPE_COMPONENT_CARD_SCHEMA_V2: &str = "fern.recipe-component-card.v2";
pub const ARCHITECTURE_COMPONENT_CARD_SCHEMA_V2: &str = "fern.architecture-component-card.v2";
pub const ACTION_COMPONENT_CARD_SCHEMA_V2: &str = "fern.action-component-card.v2";
pub const DENSITY_COMPONENT_CARD_SCHEMA_V2: &str = "fern.density-component-card.v2";
pub const EXPERIMENT_DRAFT_SCHEMA_V2: &str = "fern.experiment-draft.v2";
pub const ABLATION_DRAFT_SCHEMA_V2: &str = "fern.ablation-draft.v2";
pub const IMPLEMENTATION_CAPABILITIES_SCHEMA_V2: &str = "fern.implementation-capabilities.v2";
pub const PYTHON_CAPABILITY_PROBE_SCHEMA_V2: &str = "fern.python-capability-probe.v2";
pub const MODEL_LINK_PROBE_RESULT_SCHEMA_V1: &str = "fern.model-link-probe-result.v1";
pub const MODEL_LINK_RECEIPT_SCHEMA_V1: &str = "fern.model-link-receipt.v1";
pub const IMPLEMENTATION_BUNDLE_DRAFT_SCHEMA_V2: &str = "fern.implementation-bundle-draft.v2";
pub const IMPLEMENTATION_BUNDLE_RESOLUTION_SCHEMA_V2: &str = "fern.implementation-bundle-resolution.v2";
pub const EXPERIMENT_MANIFEST_SCHEMA_V2: &str = "fern.run-manifest.v2";
pub const EXPERIMENT_RESOLUTION_SCHEMA_V2: &str = "fern.experiment-resolution.v2";
pub const ABLATION_RESOLUTION_SCHEMA_V2: &str = "fern.ablation-resolution.v2";
pub const EXPERIMENT_FORK_RESOLUTION_SCHEMA_V2: &str = "fern.experiment-fork-resolution.v2";

/// One executable role in a composed experiment.
///
/// The role is structural rather than a global list of concrete handlers.  An
/// implementation capability manifest supplies the handler names and schemas
/// it actually supports.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ExperimentComponentRoleV2 {
    World,
    Evaluator,
    Loader,
    Trainer,
    Architecture,
    Action,
    Density,
}

impl ExperimentComponentRoleV2 {
    pub const fn value(self) -> &'static str {
        match self {
            Self::World => "world",
            Self::Evaluator => "evaluator",
            Self::Loader => "loader",
            Self::Trainer => "trainer",
            Self::Architecture => "architecture",
            Self::Action => "action",
            Self::Density => "density",
        }
    }
}

/// One handler request.  Every scientifically meaningful parameter is explicit
/// in `parameters`; V2 runtimes are not allowed to fill another layer of
/// defaults after resolution.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct HandlerRequestV2 {
    pub handler: String,
    pub parameters: Value,
}

/// Data generation, fitting, evaluation, and the finite experiment grid.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RecipeComponentCardV2 {
    pub schema: String,
    pub world: HandlerRequestV2,
    pub evaluator: HandlerRequestV2,
    pub loader: HandlerRequestV2,
    pub trainer: HandlerRequestV2,
    pub context_length: u64,
    /// Complete finite execution grid.  Axis names belong to the selected
    /// scheduler/world implementation rather than to a Rust combination enum.
    /// The envelope only requires non-empty, duplicate-free values and can
    /// therefore count work before a GPU is touched.
    pub grid: BTreeMap<String, Vec<Value>>,
    /// Optional complete binary-factorial meaning for selected grid axes. The
    /// scheduler still receives ordinary cells; this block only freezes how
    /// those arm labels map to factors and signed contrasts.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub factorial: Option<BinaryFactorialDesignV2>,
}

impl JsonContract for RecipeComponentCardV2 {
    const SCHEMA_ID: &'static str = RECIPE_COMPONENT_CARD_SCHEMA_V2;
}

/// One named binary factor. Its grid axis is also an existing model-knob name;
/// level order fixes the contrast sign (-1, +1).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct BinaryFactorV2 {
    pub name: String,
    pub model_knob: String,
    pub levels: [Value; 2],
}

/// One predeclared main effect or interaction. The signed weight is the
/// product of the named factors' -1/+1 codes.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct FactorialContrastV2 {
    pub name: String,
    pub factors: Vec<String>,
}

/// Complete binary-factorial meaning attached to ordinary recipe grid axes.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct BinaryFactorialDesignV2 {
    pub factors: Vec<BinaryFactorV2>,
    pub contrasts: Vec<FactorialContrastV2>,
}

impl BinaryFactorialDesignV2 {
    pub fn normalize(&mut self, grid: &BTreeMap<String, Vec<Value>>) -> Result<(), String> {
        self.factors.sort_by(|left, right| left.name.cmp(&right.name));
        for contrast in &mut self.contrasts {
            contrast.factors.sort();
        }
        self.contrasts.sort_by(|left, right| left.name.cmp(&right.name));
        self.validate(grid)
    }

    /// Prove that each factor owns one two-level grid axis and that every
    /// non-empty main/interaction contrast is named exactly once. The grid's
    /// existing Cartesian expansion supplies the complete 2^k cube.
    pub fn validate(&self, grid: &BTreeMap<String, Vec<Value>>) -> Result<(), String> {
        if self.factors.is_empty() || self.factors.len() > 10 {
            return Err("factorial requires between 1 and 10 binary factors".to_string());
        }
        let mut factor_index = BTreeMap::new();
        let mut knobs = BTreeSet::new();
        for (index, factor) in self.factors.iter().enumerate() {
            validate_factorial_token(&factor.name, "factor name")?;
            validate_factorial_token(&factor.model_knob, "factor model_knob")?;
            if factor.levels[0] == factor.levels[1] {
                return Err(format!("factor {:?} repeats one level", factor.name));
            }
            if factor_index.insert(factor.name.as_str(), index).is_some() || !knobs.insert(&factor.model_knob) {
                return Err("factor names and model_knob axes must be unique".to_string());
            }
            let observed = grid
                .get(&factor.model_knob)
                .ok_or_else(|| format!("factor model_knob {:?} is absent from recipe grid", factor.model_knob))?;
            if observed.len() != 2 || !factor.levels.iter().all(|level| observed.contains(level)) {
                return Err(format!(
                    "factor {:?} grid axis must contain exactly its two declared levels",
                    factor.name
                ));
            }
        }

        let expected = (1usize << self.factors.len()) - 1;
        if self.contrasts.len() != expected {
            return Err(format!("factorial requires all {expected} non-empty factor contrasts"));
        }
        let mut names = BTreeSet::new();
        let mut masks = BTreeSet::new();
        for contrast in &self.contrasts {
            validate_factorial_token(&contrast.name, "contrast name")?;
            if !names.insert(&contrast.name) || contrast.factors.is_empty() {
                return Err("contrast names must be unique and name at least one factor".to_string());
            }
            let mut mask = 0usize;
            for factor in &contrast.factors {
                let index = *factor_index
                    .get(factor.as_str())
                    .ok_or_else(|| format!("contrast {:?} names unknown factor {factor:?}", contrast.name))?;
                let bit = 1usize << index;
                if mask & bit != 0 {
                    return Err(format!("contrast {:?} repeats factor {factor:?}", contrast.name));
                }
                mask |= bit;
            }
            if !masks.insert(mask) {
                return Err("two contrasts name the same factor subset".to_string());
            }
        }
        Ok(())
    }

    pub fn contrast_weight(&self, contrast_name: &str, coordinate: &BTreeMap<String, Value>) -> Result<i8, String> {
        let contrast = self
            .contrasts
            .iter()
            .find(|contrast| contrast.name == contrast_name)
            .ok_or_else(|| format!("unknown factorial contrast {contrast_name:?}"))?;
        contrast.factors.iter().try_fold(1i8, |weight, name| {
            let factor = self
                .factors
                .iter()
                .find(|factor| factor.name == *name)
                .ok_or_else(|| format!("contrast names unknown factor {name:?}"))?;
            match coordinate.get(&factor.model_knob) {
                Some(level) if level == &factor.levels[0] => Ok(-weight),
                Some(level) if level == &factor.levels[1] => Ok(weight),
                _ => Err(format!("coordinate has no declared level for factor {name:?}")),
            }
        })
    }
}

fn validate_factorial_token(value: &str, field: &str) -> Result<(), String> {
    if value.trim().is_empty()
        || value.trim() != value
        || value.chars().any(char::is_control)
        || value.chars().any(char::is_whitespace)
    {
        return Err(format!(
            "{field} must be trimmed, non-empty text without whitespace or control characters"
        ));
    }
    Ok(())
}

/// Model geometry and its explicit shape/operator parameters.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ArchitectureComponentCardV2 {
    pub schema: String,
    pub handler: String,
    pub parameters: Value,
}

impl JsonContract for ArchitectureComponentCardV2 {
    const SCHEMA_ID: &'static str = ARCHITECTURE_COMPONENT_CARD_SCHEMA_V2;
}

/// Past/future action representation and fusion parameters.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ActionComponentCardV2 {
    pub schema: String,
    pub handler: String,
    pub parameters: Value,
}

impl JsonContract for ActionComponentCardV2 {
    const SCHEMA_ID: &'static str = ACTION_COMPONENT_CARD_SCHEMA_V2;
}

/// Predictive probability law, separate from model geometry.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct DensityComponentCardV2 {
    pub schema: String,
    pub handler: String,
    pub parameters: Value,
}

impl JsonContract for DensityComponentCardV2 {
    const SCHEMA_ID: &'static str = DENSITY_COMPONENT_CARD_SCHEMA_V2;
}

/// Authoring form for one component: immediate inline use or an immutable
/// content address.  Resolution erases this distinction, so promoting an
/// inline card to the component store cannot change the run manifest.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ComponentSourceV2<T> {
    Inline(T),
    Ref(String),
}

/// State-only is explicit.  Missing `action` is not interpreted as a default.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "mode", rename_all = "snake_case", deny_unknown_fields)]
pub enum ActionSourceV2 {
    StateOnly,
    Conditioned {
        component: ComponentSourceV2<ActionComponentCardV2>,
    },
}

/// The four independently owned scientific components of one experiment.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ExperimentComponentsSourceV2 {
    pub recipe: ComponentSourceV2<RecipeComponentCardV2>,
    pub architecture: ComponentSourceV2<ArchitectureComponentCardV2>,
    pub action: ActionSourceV2,
    pub density: ComponentSourceV2<DensityComponentCardV2>,
}

/// File paths and source overrides are deliberately absent.  They must first
/// resolve into one complete, checked implementation identity.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ImplementationReferenceV2 {
    #[serde(rename = "ref")]
    pub implementation: ImplementationBundleHashV2,
}

/// Author-declared hard stop for a scratch experiment.  It is deliberately
/// outside the worker manifest and its scientific identity.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ExperimentBudgetV2 {
    pub max_cells: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_gpu_seconds: Option<u64>,
}

/// Explicit, inspectable planning estimate.  It never enters run identity;
/// the basis names the receipt or bounded observation that supplied the rate.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ExperimentCostEstimateV2 {
    pub gpu_seconds_per_cell: u64,
    pub basis: String,
}

/// A declared inferential gate that must be achievable before work is priced
/// or registered.  V2 currently supports only the exact paired sign test,
/// whose discrete p-value floor is known without data.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "test", rename_all = "snake_case", deny_unknown_fields)]
pub enum InferentialRequirementV2 {
    ExactTwoSidedSign { paired_units: u64, alpha: f64 },
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct InferentialFeasibilityV2 {
    pub requirement: InferentialRequirementV2,
    pub minimum_attainable_p: f64,
}

/// A scratch-friendly one-file experiment declaration.
///
/// `question` and `design_ref` are provenance and remain outside run identity.
/// The implementation is deliberately absent: `resolve-experiment` receives
/// one checked implementation bundle as its separate, unique selection input
/// and freezes that identity into the resolved manifest.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ExperimentDraftV2 {
    pub schema: String,
    pub question: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub design_ref: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub budget: Option<ExperimentBudgetV2>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cost_estimate: Option<ExperimentCostEstimateV2>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub inference: Option<InferentialRequirementV2>,
    pub components: ExperimentComponentsSourceV2,
}

impl JsonContract for ExperimentDraftV2 {
    const SCHEMA_ID: &'static str = EXPERIMENT_DRAFT_SCHEMA_V2;
}

/// A control/treatment pair with one implementation by construction.
///
/// Neither the pair nor either arm has an implementation field.  The checked
/// bundle passed to `resolve-ablation` is the one selection point, so a code
/// revision cannot silently move with treatment; comparing code revisions is
/// a different experiment object, not an ablation.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct AblationDraftV2 {
    pub schema: String,
    pub question: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub design_ref: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub budget: Option<ExperimentBudgetV2>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cost_estimate: Option<ExperimentCostEstimateV2>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub inference: Option<InferentialRequirementV2>,
    pub declared_changes: Vec<String>,
    /// Implementation-reported semantic facts expected to differ between the
    /// arms.  The resolver requires the exact observed fact-key difference.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub declared_fact_changes: Vec<String>,
    pub control: ExperimentComponentsSourceV2,
    pub treatment: ExperimentComponentsSourceV2,
}

impl JsonContract for AblationDraftV2 {
    const SCHEMA_ID: &'static str = ABLATION_DRAFT_SCHEMA_V2;
}

/// How an implementation produced its handler schema projection.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum CapabilityGeneratorKindV2 {
    /// The static component-handler publication is emitted by the typed C#
    /// Experiment process and bound to the observed C# source records.
    #[serde(rename = "csharp_experiment")]
    CSharpExperiment,
    RustTypes,
    PythonConfigSchema,
}

/// Provenance carried by an implementation-generated capability manifest.
///
/// This record alone is not proof that a worker emitted it.  Production Plan
/// registration must compare it with the selected live worker's report.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct CapabilityGeneratorV2 {
    pub kind: CapabilityGeneratorKindV2,
    pub provider: String,
    /// Exact bundle-relative source files from which this projection was
    /// generated.  The bundle verifier recomputes `source_sha256` from their
    /// recorded bytes; a provider name alone is never provenance.
    pub source_paths: Vec<String>,
    #[schemars(regex(pattern = "^sha256:[0-9a-f]{64}$"))]
    pub source_sha256: String,
}

/// Small implementation-neutral relation language for constraints spanning
/// component owners.  Rust evaluates pointers and arithmetic; the selected
/// implementation supplies every domain name and path.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum CrossComponentRelationV2 {
    Equal,
    /// Both pointed values must be finite numbers and left must be smaller.
    LessThan,
    /// The left value, or every value in a left array, must be an integer
    /// multiple of the positive integer at the right pointer.
    EveryMultipleOf,
}

#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct CrossComponentConstraintV2 {
    pub left_pointer: String,
    pub relation: CrossComponentRelationV2,
    pub right_pointer: String,
}

/// One executable handler and the exact parameter contract it accepts.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct HandlerCapabilityV2 {
    pub role: ExperimentComponentRoleV2,
    pub handler: String,
    pub parameter_schema: Value,
    /// Optional JSON Schema over the complete resolved `components` object.
    /// This is generated beside the handler and can relate fields owned by
    /// different cards without teaching Rust any Python handler names.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub composition_schema: Option<Value>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub cross_component_constraints: Vec<CrossComponentConstraintV2>,
    /// Static semantic facts emitted whenever this handler is selected, such
    /// as the predictive law family.  Parameter-dependent facts belong in the
    /// later concrete-configuration probe, not in a guessed Rust mapping.
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub emits: BTreeMap<String, Value>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub provides: Vec<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub requires: Vec<String>,
}

/// Retired 2026-08-28: C# `publish-implementation-handler-capabilities` now
/// owns the static V2 handler publication. Kept intact for source recovery;
/// active authoring transports the C# typed receipt instead.
#[cfg(any())]
pub fn rust_handler_capabilities_v2() -> Vec<HandlerCapabilityV2> {
    let capability =
        |role, handler: String, parameter_schema: Value, provides: &[&str], requires: &[&str]| HandlerCapabilityV2 {
            role,
            handler,
            parameter_schema,
            composition_schema: None,
            cross_component_constraints: Vec::new(),
            emits: BTreeMap::new(),
            provides: provides.iter().map(|value| (*value).to_string()).collect(),
            requires: requires.iter().map(|value| (*value).to_string()).collect(),
        };
    let mut handlers = published_dataset_source_capabilities()
        .into_iter()
        .map(|source| {
            capability(
                ExperimentComponentRoleV2::World,
                source.source_name,
                world_parameter_schema_v2(),
                &["world.samples.v2"],
                &[],
            )
        })
        .collect::<Vec<_>>();
    // These three V2 handlers name implementation boundaries, not presets.
    // Split fractions, batching, optimizer, and loss are complete Card
    // parameters. Adding a new value therefore does not add an enum variant or
    // rebuild the scheduler.
    handlers.push(capability(
        ExperimentComponentRoleV2::Loader,
        "explicit_loader".to_string(),
        loader_parameter_schema_v2(),
        &["training.windows.v2"],
        &["world.samples.v2"],
    ));
    handlers.push(capability(
        ExperimentComponentRoleV2::Trainer,
        "python_training_worker".to_string(),
        trainer_parameter_schema_v2(),
        &[],
        &["training.windows.v2", "forecast.density.v2"],
    ));
    handlers.push(capability(
        ExperimentComponentRoleV2::Evaluator,
        "resolved_loss".to_string(),
        evaluator_parameter_schema_v2(),
        &[],
        &["world.samples.v2", "forecast.density.v2"],
    ));
    handlers
}

#[cfg(any())]
fn closed_object_schema(properties: Value, required: &[&str]) -> Value {
    serde_json::json!({
        "type": "object",
        "properties": properties,
        "required": required,
        "additionalProperties": false
    })
}

#[cfg(any())]
fn world_parameter_schema_v2() -> Value {
    closed_object_schema(
        serde_json::json!({
            "channels": {"type": "integer", "minimum": 1},
            "noise_kind": {"type": "string", "enum": ["gaussian", "laplace", "student_t3", "contaminated"]},
            "noise_ar1_phi": {"type": "number", "exclusiveMinimum": -1.0, "exclusiveMaximum": 1.0},
            "relative_dose": {"type": "boolean"},
            "run_whiten": {"type": "boolean"},
            "run_rewind": {"type": "boolean"}
        }),
        &[
            "channels",
            "noise_kind",
            "noise_ar1_phi",
            "relative_dose",
            "run_whiten",
            "run_rewind",
        ],
    )
}

#[cfg(any())]
fn loader_parameter_schema_v2() -> Value {
    closed_object_schema(
        serde_json::json!({
            "train_start": {"type": "number", "minimum": 0.0, "maximum": 1.0},
            "train_end": {"type": "number", "minimum": 0.0, "maximum": 1.0},
            "val_start": {"type": "number", "minimum": 0.0, "maximum": 1.0},
            "val_end": {"type": "number", "minimum": 0.0, "maximum": 1.0},
            "test_start": {"type": "number", "minimum": 0.0, "maximum": 1.0},
            "test_end": {"type": "number", "minimum": 0.0, "maximum": 1.0},
            "normalization": {"type": "string", "enum": ["standard", "robust_iqr", "robust_mad", "center_mean", "center_median", "none"]},
            "batch_size": {"type": "integer", "minimum": 1},
            "num_workers": {"type": "integer", "minimum": 0}
        }),
        &[
            "train_start",
            "train_end",
            "val_start",
            "val_end",
            "test_start",
            "test_end",
            "normalization",
            "batch_size",
            "num_workers",
        ],
    )
}

#[cfg(any())]
fn trainer_parameter_schema_v2() -> Value {
    let state_forecast = closed_object_schema(serde_json::json!({"kind": {"const": "state_forecast"}}), &["kind"]);
    let state_and_action_forecast = closed_object_schema(
        serde_json::json!({
            "kind": {"const": "state_and_action_forecast"},
            "action_loss_weight": {"type": "number", "exclusiveMinimum": 0.0},
            "action_stitch_reducer": {"type": "string", "enum": ["mean", "median"]}
        }),
        &["kind", "action_loss_weight", "action_stitch_reducer"],
    );
    let optim = closed_object_schema(
        serde_json::json!({
            "type": {"type": "string", "enum": ["adamw"]},
            "learning_rate": {"type": "number", "exclusiveMinimum": 0.0},
            "weight_decay": {"type": "number", "minimum": 0.0},
            "max_grad_norm": {"type": "number", "minimum": 0.0},
            "muon_momentum": {"type": "number", "exclusiveMinimum": 0.0},
            "muon_beta2": {"type": "number", "exclusiveMinimum": 0.0},
            "muon_ns_steps": {"type": "integer", "minimum": 1}
        }),
        &[
            "type",
            "learning_rate",
            "weight_decay",
            "max_grad_norm",
            "muon_momentum",
            "muon_beta2",
            "muon_ns_steps",
        ],
    );
    let scheduler = closed_object_schema(
        serde_json::json!({
            "type": {"type": "string", "enum": ["plateau", "cosine", "none"]},
            "plateau_patience": {"type": "integer", "minimum": 0},
            "factor": {"type": "number", "exclusiveMinimum": 0.0},
            "warmup_epochs": {"type": "integer", "minimum": 0},
            "eta_min": {"type": "number", "minimum": 0.0}
        }),
        &["type", "plateau_patience", "factor", "warmup_epochs", "eta_min"],
    );
    let compile = closed_object_schema(
        serde_json::json!({
            "enabled": {"type": "boolean"},
            "mode": {"type": "string", "enum": ["default", "reduce-overhead", "max-autotune"]},
            "fullgraph": {"type": "boolean"},
            "dynamic": {"type": "boolean"}
        }),
        &["enabled", "mode", "fullgraph", "dynamic"],
    );
    let checkpoint_source = serde_json::json!({
        "oneOf": [
            {"type": "null"},
            closed_object_schema(
                serde_json::json!({
                    "kind": {"const": "trained_attempt"},
                    "producer_stage_attempt_id": {"type": "string", "minLength": 1},
                    "training_config_digest": {"type": "string", "pattern": "^sha256:[0-9a-f]{64}$"},
                    "implementation_bundle_digest": {"type": "string", "pattern": "^sha256:[0-9a-f]{64}$"},
                    "artifact_digest": {"type": "string", "pattern": "^sha256:[0-9a-f]{64}$"}
                }),
                &[
                    "kind",
                    "producer_stage_attempt_id",
                    "training_config_digest",
                    "implementation_bundle_digest",
                    "artifact_digest",
                ],
            ),
            closed_object_schema(
                serde_json::json!({
                    "kind": {"const": "pretrained_asset"},
                    "model_id": {"type": "string", "minLength": 1},
                    "revision": {"type": "string", "minLength": 1}
                }),
                &["kind", "model_id", "revision"],
            )
        ]
    });
    closed_object_schema(
        serde_json::json!({
            "execution_kind": {"type": "string", "enum": ["train", "evaluation_only", "pretrained_inference"]},
            "checkpoint_source": checkpoint_source,
            "task_name": {"type": "string", "minLength": 1},
            "channel_independent": {"type": "boolean"},
            // CHG 2026-08-21 (review): zero epochs ran no training loop and still
            // saved a model as if it had trained; a training task needs at least one.
            // Zero is legal HERE and refused for training executions only, in
            // refuse_zero_epochs_for_a_training_execution below. Commit
            // 4c67c89b relaxed this from 1 to 0 in the same change that added
            // `evaluation_only`, because an evaluation-only or
            // pretrained-inference execution declares no training and every
            // zero-shot model in the campaign is one. A blanket minimum of 1
            // would make those unauthorable.
            "epochs": {"type": "integer", "minimum": 0},
            "patience": {"type": "integer", "minimum": 0},
            "grace_period": {"type": "integer", "minimum": 0},
            "optim": optim,
            "scheduler": scheduler,
            "dtype": {"type": "string", "enum": ["float32", "float16", "bfloat16"]},
            "amp_dtype": {"type": "string", "enum": ["none", "float16", "bfloat16"]},
            "compile": compile,
            "artifact_policy": {"type": "string", "enum": ["slim", "full"]},
            "training_target": {"oneOf": [state_forecast, state_and_action_forecast]}
        }),
        &[
            "execution_kind",
            "checkpoint_source",
            "task_name",
            "channel_independent",
            "epochs",
            "patience",
            "grace_period",
            "optim",
            "scheduler",
            "dtype",
            "amp_dtype",
            "compile",
            "artifact_policy",
            "training_target",
        ],
    )
}

#[cfg(any())]
fn evaluator_parameter_schema_v2() -> Value {
    let metric = closed_object_schema(
        serde_json::json!({
            "name": {"type": "string", "enum": ["mse", "mae", "huber", "swd", "ept", "crps", "nll", "fern_nll"]},
            "role": {"type": "string", "enum": ["monitor", "active"]},
            // CHG 2026-08-21 (review): the deck sums weight * value, so a negative
            // weight rewards a worse loss; weights are non-negative, and the
            // resolver requires at least one strictly positive evaluation weight.
            "weight": {"type": "number", "minimum": 0.0}
        }),
        &["name", "role", "weight"],
    );
    let huber_delta = closed_object_schema(
        serde_json::json!({
            "train": {"type": "number", "exclusiveMinimum": 0.0},
            "eval": {"type": "number", "exclusiveMinimum": 0.0}
        }),
        &["train", "eval"],
    );
    let probability = closed_object_schema(
        serde_json::json!({
            "num_train_samples": {"type": "integer", "minimum": 1},
            "num_eval_samples": {"type": "integer", "minimum": 1},
            "quantile_levels": {
                "type": "array",
                "minItems": 1,
                "items": {"type": "number", "exclusiveMinimum": 0.0, "exclusiveMaximum": 1.0}
            }
        }),
        &["num_train_samples", "num_eval_samples", "quantile_levels"],
    );
    let smoothing_spec = || {
        closed_object_schema(
            serde_json::json!({
                "kind": {"type": "string", "enum": ["none", "sma", "ema", "median"]},
                "window": {"type": "integer", "minimum": 1},
                "alpha": {"type": "number", "exclusiveMinimum": 0.0, "maximum": 1.0}
            }),
            &["kind", "window", "alpha"],
        )
    };
    let smoothing = closed_object_schema(
        serde_json::json!({
            "train": smoothing_spec(),
            "val": smoothing_spec(),
            "test": smoothing_spec()
        }),
        &["train", "val", "test"],
    );
    closed_object_schema(
        serde_json::json!({
            "train": {"type": "array", "items": metric.clone()},
            "eval": {"type": "array", "items": metric},
            "huber_delta": huber_delta,
            "num_proj_swd": {"type": "integer", "minimum": 0},
            "probability": probability,
            "smoothing": smoothing
        }),
        &[
            "train",
            "eval",
            "huber_delta",
            "num_proj_swd",
            "probability",
            "smoothing",
        ],
    )
}

/// Complete handler contract generated by one frozen implementation.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ImplementationCapabilitiesV2 {
    pub schema: String,
    pub implementation: ImplementationBundleHashV2,
    pub generators: Vec<CapabilityGeneratorV2>,
    /// Production compile probe exported by the selected Python worker.
    /// Its callable name and protocol are bound to the same implementation
    /// digest as the handler schemas.
    pub model_link_probe: PythonModelLinkProbeV2,
    pub handlers: Vec<HandlerCapabilityV2>,
    /// Implementation-reported Stage graph. Absent on bundles probed before this field.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub stage_pipeline_template: Option<StagePipelineTemplateV1>,
}

impl JsonContract for ImplementationCapabilitiesV2 {
    const SCHEMA_ID: &'static str = IMPLEMENTATION_CAPABILITIES_SCHEMA_V2;
}

/// Python-owned half of the handler registry emitted by the imported worker
/// package.  Rust-owned scheduler handlers are appended by the bundle builder
/// from their executable enums, never copied into this response.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct PythonModelLinkProbeV2 {
    /// Callable on the imported provider module. It must construct the real
    /// production model path; a separate test model is not a valid probe.
    pub entrypoint: String,
    pub protocol_version: u32,
}

/// Which production shape path the real model-link probe exercised.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ModelLinkShapeModeV1 {
    FakeTensor,
    TinyCpu,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ModelLinkConstructionV1 {
    ProductionFactory,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ModelLinkParameterContractV1 {
    CpuFloat32,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ModelLinkOutputContractV1 {
    Checked,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ModelLinkLossBackwardV1 {
    FiniteConnected,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ModelLinkOptimizerMembershipV1 {
    ExactTrainableParameters,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ModelLinkCheckpointRoundTripV1 {
    StrictSafetensors,
}

/// Strict result emitted by the implementation's production model-link probe.
/// Every field corresponds to a check the Python entrypoint actually performs;
/// an abbreviated or extended object is rejected at the process boundary.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct PythonModelLinkResultV1 {
    pub schema: String,
    pub model: String,
    pub density: String,
    pub predictive_law: String,
    pub action_dim: Option<u64>,
    pub shape_mode: ModelLinkShapeModeV1,
    pub model_construction: ModelLinkConstructionV1,
    pub parameter_contract: ModelLinkParameterContractV1,
    pub output_contract: ModelLinkOutputContractV1,
    pub loss_backward: ModelLinkLossBackwardV1,
    pub optimizer_membership: ModelLinkOptimizerMembershipV1,
    pub linked_parameter_groups: Vec<String>,
    pub checkpoint_round_trip: ModelLinkCheckpointRoundTripV1,
    /// What the implementation actually CONSTRUCTED, reported by the model
    /// itself rather than inferred from the request it was given. Optional so
    /// receipts written before this field existed still deserialize; a probe
    /// that emits it lets a reader see a model that quietly built something
    /// else than what was asked for.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub architecture_manifest: Option<ObservedArchitectureManifestV1>,
}

/// One slot's occupant and the parameters it owns, as observed on the built
/// model. An EMPTY slot is absent from the map rather than present and null:
/// the manifest states what exists, and Rust never enumerates what could.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ObservedSlotV1 {
    /// The extension kind the slot's own Python file declared, such as
    /// `fern.action.separate_tde`. Rust stores the string and never keeps a
    /// list of which strings are legal.
    pub kind: String,
    /// The named family within that kind when the extension has one, such as
    /// which finite density a mixture head built.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub variant: Option<String>,
    pub parameter_groups: Vec<ObservedParameterGroupV1>,
}

/// One group of parameters counted off the constructed model, named by the
/// `state_dict` prefix that owns them.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ObservedParameterGroupV1 {
    pub name: String,
    pub parameter_count: u64,
}

/// The observed architecture of one constructed model.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ObservedArchitectureManifestV1 {
    /// The Core seam the implementation was built against.
    pub seam_version: u64,
    /// Occupied slots only, keyed by slot name.
    pub slots: BTreeMap<String, ObservedSlotV1>,
    /// Every top-level parameter group of the whole model, Core groups
    /// included, so the manifest can be checked against the real parameters.
    pub parameter_groups: Vec<ObservedParameterGroupV1>,
    pub parameter_count: u64,
}

impl JsonContract for PythonModelLinkResultV1 {
    const SCHEMA_ID: &'static str = MODEL_LINK_PROBE_RESULT_SCHEMA_V1;
}

/// Rust-authored binding between one typed Python result and the exact frozen
/// implementation/config fixture that produced it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ModelLinkReceiptV1 {
    pub schema: String,
    pub implementation: ImplementationBundleHashV2,
    pub config_hash: String,
    #[schemars(regex(pattern = "^sha256:[0-9a-f]{64}$"))]
    pub fixture_sha256: String,
    pub channels: u64,
    pub action_dim: Option<u64>,
    pub result: PythonModelLinkResultV1,
}

impl JsonContract for ModelLinkReceiptV1 {
    const SCHEMA_ID: &'static str = MODEL_LINK_RECEIPT_SCHEMA_V1;
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct PythonCapabilityProbeV2 {
    pub schema: String,
    /// Files actually read as authority by the provider.  This must include
    /// the imported module's own `__file__`.
    pub generator_source_files: Vec<String>,
    pub model_link_probe: PythonModelLinkProbeV2,
    pub handlers: Vec<HandlerCapabilityV2>,
    /// Optional Stage graph reported by this exact Python implementation.
    /// Older training implementations omitted it and retain the frozen
    /// forecast-origin graph as read-only compatibility.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub stage_pipeline_template: Option<StagePipelineTemplateV1>,
}

impl JsonContract for PythonCapabilityProbeV2 {
    const SCHEMA_ID: &'static str = PYTHON_CAPABILITY_PROBE_SCHEMA_V2;
}

/// Runtime entrypoint frozen into one executable implementation bundle.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum ImplementationRuntimeV2 {
    /// The module must export `fern_experiment_capabilities_v2()`.
    PythonModule { module: String },
}

/// Authoring-only file declaration.  Paths disappear when an Experiment is
/// resolved; a Plan sees only the resulting implementation-bundle digest.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ImplementationBundleDraftV2 {
    pub schema: String,
    pub runtime: ImplementationRuntimeV2,
    pub protocol_version: u32,
    pub source_files: Vec<String>,
    pub dependency_lock_files: Vec<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub active_feature_flags: Vec<String>,
}

impl JsonContract for ImplementationBundleDraftV2 {
    const SCHEMA_ID: &'static str = IMPLEMENTATION_BUNDLE_DRAFT_SCHEMA_V2;
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ImplementationBundleFileV2 {
    pub path: String,
    #[schemars(regex(pattern = "^sha256:[0-9a-f]{64}$"))]
    pub byte_sha256: String,
    pub bytes: u64,
}

/// Checked output of one stable-source observation and live implementation
/// probe.  This object may be stored by content; experiments copy only its
/// implementation and capability identities into their run manifests.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ImplementationBundleResolutionV2 {
    pub schema: String,
    pub implementation: ImplementationBundleHashV2,
    pub runtime: ImplementationRuntimeV2,
    pub protocol_version: u32,
    pub source_files: Vec<ImplementationBundleFileV2>,
    pub dependency_lock_files: Vec<ImplementationBundleFileV2>,
    pub active_feature_flags: Vec<String>,
    pub capabilities: ImplementationCapabilitiesV2,
}

impl JsonContract for ImplementationBundleResolutionV2 {
    const SCHEMA_ID: &'static str = IMPLEMENTATION_BUNDLE_RESOLUTION_SCHEMA_V2;
}

/// Resolved action component consumed by the worker.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "mode", rename_all = "snake_case", deny_unknown_fields)]
pub enum ResolvedActionV2 {
    StateOnly,
    Conditioned { card: ActionComponentCardV2 },
}

/// Complete card bodies after every inline/reference source has been resolved.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ResolvedExperimentComponentsV2 {
    pub recipe: RecipeComponentCardV2,
    pub architecture: ArchitectureComponentCardV2,
    pub action: ResolvedActionV2,
    pub density: DensityComponentCardV2,
}

/// Content addresses of the independently owned component cards.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ExperimentComponentReferencesV2 {
    #[schemars(regex(pattern = "^recipe:sha256:[0-9a-f]{64}$"))]
    pub recipe: String,
    #[schemars(regex(pattern = "^architecture:sha256:[0-9a-f]{64}$"))]
    pub architecture: String,
    #[schemars(regex(pattern = "^action:sha256:[0-9a-f]{64}$"))]
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub action: Option<String>,
    #[schemars(regex(pattern = "^density:sha256:[0-9a-f]{64}$"))]
    pub density: String,
}

/// The only V2 JSON object a worker may execute.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ResolvedExperimentManifestV2 {
    pub schema: String,
    pub implementation: ImplementationBundleHashV2,
    #[schemars(regex(pattern = "^capabilities:sha256:[0-9a-f]{64}$"))]
    pub capability_manifest_sha256: String,
    pub component_refs: ExperimentComponentReferencesV2,
    pub components: ResolvedExperimentComponentsV2,
    pub configuration_facts: BTreeMap<String, Value>,
}

impl JsonContract for ResolvedExperimentManifestV2 {
    const SCHEMA_ID: &'static str = EXPERIMENT_MANIFEST_SCHEMA_V2;
}

/// One resolved arm and its manifest identity.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ResolvedExperimentArmV2 {
    #[schemars(regex(pattern = "^experiment:sha256:[0-9a-f]{64}$"))]
    pub manifest_sha256: String,
    pub manifest: ResolvedExperimentManifestV2,
}

/// Exact expansion size shown before registration or launch.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct WorkloadArmPreviewV2 {
    pub arm: String,
    pub cells: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct WorkloadPreviewV2 {
    pub arms: Vec<WorkloadArmPreviewV2>,
    pub total_cells: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_cells: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub estimated_gpu_seconds: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub gpu_seconds_per_cell: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub estimate_basis: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_gpu_seconds: Option<u64>,
}

/// One declared loss term, as the loss deck actually reads it.
///
/// `role` is documentation: the deck sums `weight * value` and never looks at
/// the role string, which is why every judgement below is made on the weight.
#[cfg(any())]
struct DeclaredTerm {
    name: String,
    weight: f64,
}

#[cfg(any())]
fn declared_terms(recipe: &RecipeComponentCardV2, deck: &str) -> Vec<DeclaredTerm> {
    recipe
        .evaluator
        .parameters
        .get(deck)
        .and_then(Value::as_array)
        .map(|terms| {
            terms
                .iter()
                .map(|term| DeclaredTerm {
                    name: term
                        .get("name")
                        .and_then(Value::as_str)
                        .unwrap_or("<unnamed>")
                        .to_string(),
                    weight: term.get("weight").and_then(Value::as_f64).unwrap_or(0.0),
                })
                .collect()
        })
        .unwrap_or_default()
}

#[cfg(any())]
fn active_names(terms: &[DeclaredTerm]) -> Vec<&str> {
    terms
        .iter()
        .filter(|term| term.weight != 0.0)
        .map(|term| term.name.as_str())
        .collect()
}

#[cfg(any())]
fn listed(names: &[&str]) -> String {
    if names.is_empty() {
        "nothing".to_string()
    } else {
        names.join(", ")
    }
}

/// Odd-but-legal shapes a resolved experiment can carry, stated rather than
/// refused.
///
/// The zero-weight refusal above catches the one shape that cannot possibly
/// work. These are the shapes that CAN work and are usually a mistake — a
/// mismatched objective, a monitor list, a term that trains nothing. Refusing
/// them would block deliberate designs (a held-out monitor is a real thing to
/// want), and staying silent is how the Level 0A canary spent two registered
/// units measuring an untrained model. So they are warnings: the receipt
/// carries them and every reader repeats them verbatim.
///
/// Recomputed on verification like every other derived field, so a warning
/// cannot be edited out of a saved receipt.
#[cfg(any())]
pub fn resolution_warnings(arms: &[(&str, &ResolvedExperimentArmV2)]) -> Vec<String> {
    let mut warnings = Vec::new();
    for (arm, resolved) in arms {
        let recipe = &resolved.manifest.components.recipe;
        let train = declared_terms(recipe, "train");
        let eval = declared_terms(recipe, "eval");
        let train_active = active_names(&train);
        let eval_active = active_names(&eval);

        // (a) Training one objective and selecting on another is legal and
        // occasionally meant. It is far more often a term edited on one deck
        // and not the other, and the run then optimises something it is not
        // judged by.
        if train_active != eval_active {
            warnings.push(format!(
                "arm {arm:?} trains on {} but selects checkpoints on {}; \
                 the run optimises one objective and is judged by another",
                listed(&train_active),
                listed(&eval_active)
            ));
        }

        // (b) A single active term beside weight-0 monitors reads like a
        // multi-term objective and is not one. Name the term that actually
        // decides, because that is the question a reader has.
        if let [only] = eval_active.as_slice() {
            let monitors: Vec<&str> = eval
                .iter()
                .filter(|term| term.weight == 0.0)
                .map(|term| term.name.as_str())
                .collect();
            if !monitors.is_empty() {
                warnings.push(format!(
                    "arm {arm:?} selects checkpoints on {only} alone; {} \
                     {} weight 0 and only monitored",
                    listed(&monitors),
                    if monitors.len() == 1 { "carries" } else { "carry" }
                ));
            }
        }

        // (c) A weight-0 training term contributes nothing to the gradient.
        // Legal — it keeps the term declared while it is switched off — and
        // indistinguishable from a term someone believes is training.
        for term in train.iter().filter(|term| term.weight == 0.0) {
            warnings.push(format!(
                "arm {arm:?} declares training term {} at weight 0, which trains nothing",
                term.name
            ));
        }

        // (d) Selection happens once per epoch against the validation
        // objective, so a run with almost no optimizer steps has almost no
        // distinct models to choose between.
        //
        // Computed only from what the DRAFT declares. Steps per epoch depends
        // on the windowed training-split length, which lives in the world
        // implementation and not in the draft; no trainer schema admits a
        // `steps_per_epoch` today, so in practice the epoch branch is the one
        // that fires. Deriving steps here would put a second copy of
        // serie-rs's windowing math in the authoring path, where it would
        // silently drift from the real one.
        let epochs = recipe.trainer.parameters.get("epochs").and_then(Value::as_u64);
        let steps = recipe.trainer.parameters.get("steps_per_epoch").and_then(Value::as_u64);
        match (epochs, steps) {
            (Some(epochs), Some(steps)) => {
                let total = epochs.saturating_mul(steps);
                if total < MINIMUM_INFORMATIVE_STEPS {
                    warnings.push(format!(
                        "arm {arm:?} declares {epochs} epochs of {steps} optimizer steps, {total} in all; \
                         selection has almost nothing to choose from"
                    ));
                }
            }
            // Steps per epoch is unknown, but selection happens ONCE per
            // epoch, so the epoch count alone bounds how many distinct models
            // validation-best ever gets to compare.
            (Some(epochs), None) if epochs < MINIMUM_INFORMATIVE_STEPS => {
                warnings.push(format!(
                    "arm {arm:?} declares {epochs} epochs, and selection compares one checkpoint per epoch; \
                     selection has almost nothing to choose from"
                ));
            }
            _ => {}
        }
    }
    warnings
}

/// Below this many optimizer steps in the whole run — or, when steps per epoch
/// are not declared, this many epochs — validation-best selection is choosing
/// between models that have barely moved from initialization. Named rather
/// than inlined so the threshold is one value a reader can find.
#[cfg(any())]
const MINIMUM_INFORMATIVE_STEPS: u64 = 10;

/// Read-only result of resolving one scratch experiment.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ExperimentResolutionV2 {
    pub schema: String,
    pub question: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub design_ref: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub inference: Option<InferentialFeasibilityV2>,
    pub workload: WorkloadPreviewV2,
    /// Odd-but-legal shapes this resolution carries, stated so a reader meets
    /// them. Never a refusal: each one is a design someone may have meant.
    /// Recomputed on verification, so a warning cannot be edited out of a
    /// saved receipt.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub warnings: Vec<String>,
    /// The exact probed implementation travels with the resolved authoring
    /// object so `plan-experiment --from ...` can re-observe its source and
    /// live capability provider without accepting a second mutable path.
    /// The scientific manifest still carries only the implementation and
    /// capability digests; embedding the checked bundle here is execution
    /// provenance, not a second run-identity preimage.
    pub implementation_bundle: ImplementationBundleResolutionV2,
    pub resolved: ResolvedExperimentArmV2,
}

impl JsonContract for ExperimentResolutionV2 {
    const SCHEMA_ID: &'static str = EXPERIMENT_RESOLUTION_SCHEMA_V2;
}

/// Read-only result of resolving an implementation-controlled ablation pair.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct AblationResolutionV2 {
    pub schema: String,
    pub question: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub design_ref: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub inference: Option<InferentialFeasibilityV2>,
    pub implementation: ImplementationBundleHashV2,
    pub declared_changes: Vec<String>,
    pub declared_fact_changes: Vec<String>,
    pub workload: WorkloadPreviewV2,
    /// Odd-but-legal shapes this resolution carries, stated so a reader meets
    /// them. Never a refusal: each one is a design someone may have meant.
    /// Recomputed on verification, so a warning cannot be edited out of a
    /// saved receipt.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub warnings: Vec<String>,
    /// One shared executable implementation for both arms.  Keeping it once
    /// at the pair root makes the same-code ablation law structural.
    pub implementation_bundle: ImplementationBundleResolutionV2,
    pub control: ResolvedExperimentArmV2,
    pub treatment: ResolvedExperimentArmV2,
}

impl JsonContract for AblationResolutionV2 {
    const SCHEMA_ID: &'static str = ABLATION_RESOLUTION_SCHEMA_V2;
}

/// Result of forking one already-resolved experiment and changing one JSON
/// pointer below `/components`.  The implementation is inherited, printed,
/// and cannot move through this operation.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ExperimentForkResolutionV2 {
    pub schema: String,
    pub parent_manifest_sha256: String,
    pub inherited_implementation: ImplementationBundleHashV2,
    pub changed_paths: Vec<String>,
    pub workload: WorkloadPreviewV2,
    pub resolved: ExperimentResolutionV2,
}

impl JsonContract for ExperimentForkResolutionV2 {
    const SCHEMA_ID: &'static str = EXPERIMENT_FORK_RESOLUTION_SCHEMA_V2;
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn rossler_factorial() -> (BTreeMap<String, Vec<Value>>, BinaryFactorialDesignV2) {
        let grid = BTreeMap::from([
            (
                "encoder_layout".to_string(),
                vec![
                    Value::String("all_complex".to_string()),
                    Value::String("interleaved".to_string()),
                ],
            ),
            (
                "free_scale_processing".to_string(),
                vec![
                    Value::String("identity".to_string()),
                    Value::String("soft_clamped".to_string()),
                ],
            ),
            ("brenier_scale_ceiling".to_string(), vec![json!(8.5), json!(4.5)]),
        ]);
        let factors = vec![
            BinaryFactorV2 {
                name: "encoder".to_string(),
                model_knob: "encoder_layout".to_string(),
                levels: [json!("all_complex"), json!("interleaved")],
            },
            BinaryFactorV2 {
                name: "free_scale".to_string(),
                model_knob: "free_scale_processing".to_string(),
                levels: [json!("identity"), json!("soft_clamped")],
            },
            BinaryFactorV2 {
                name: "brenier_bound".to_string(),
                model_knob: "brenier_scale_ceiling".to_string(),
                levels: [json!(8.5), json!(4.5)],
            },
        ];
        let contrast = |name: &str, factors: &[&str]| FactorialContrastV2 {
            name: name.to_string(),
            factors: factors.iter().map(|factor| (*factor).to_string()).collect(),
        };
        let contrasts = vec![
            contrast("encoder", &["encoder"]),
            contrast("free_scale", &["free_scale"]),
            contrast("brenier_bound", &["brenier_bound"]),
            contrast("encoder_x_free_scale", &["encoder", "free_scale"]),
            contrast("encoder_x_brenier_bound", &["encoder", "brenier_bound"]),
            contrast("free_scale_x_brenier_bound", &["free_scale", "brenier_bound"]),
            contrast(
                "encoder_x_free_scale_x_brenier_bound",
                &["encoder", "free_scale", "brenier_bound"],
            ),
        ];
        (grid, BinaryFactorialDesignV2 { factors, contrasts })
    }

    #[test]
    fn rossler_factorial_is_one_complete_cube_with_seven_signed_partitions() {
        let (grid, mut factorial) = rossler_factorial();
        factorial.normalize(&grid).unwrap();
        // Bound before the closures rather than indexed inside them. Indexing
        // `grid` in the inner closures made them capture the map itself, which
        // cannot escape a FnMut while the outer iterator still borrows it --
        // five borrow-checker errors, and the crate has not compiled since
        // d718e80b. References are Copy, so binding the three axes first says
        // the same thing and borrows nothing extra.
        let encoder_layouts = &grid["encoder_layout"];
        let free_scale_processings = &grid["free_scale_processing"];
        let brenier_scale_ceilings = &grid["brenier_scale_ceiling"];
        let coordinates = encoder_layouts
            .iter()
            .flat_map(|encoder| {
                free_scale_processings.iter().flat_map(move |free_scale| {
                    brenier_scale_ceilings.iter().map(move |bound| {
                        BTreeMap::from([
                            ("encoder_layout".to_string(), encoder.clone()),
                            ("free_scale_processing".to_string(), free_scale.clone()),
                            ("brenier_scale_ceiling".to_string(), bound.clone()),
                        ])
                    })
                })
            })
            .collect::<Vec<_>>();

        assert_eq!(coordinates.len(), 8);
        assert_eq!(factorial.contrasts.len(), 7);
        for contrast in &factorial.contrasts {
            let weights = coordinates
                .iter()
                .map(|coordinate| factorial.contrast_weight(&contrast.name, coordinate).unwrap())
                .collect::<Vec<_>>();
            assert_eq!(weights.iter().map(|weight| i16::from(*weight)).sum::<i16>(), 0);
            assert!(weights.iter().all(|weight| matches!(weight, -1 | 1)));
        }
    }

    #[test]
    fn factorial_refuses_a_missing_level_before_workload_counting() {
        let (mut grid, factorial) = rossler_factorial();
        grid.get_mut("encoder_layout").unwrap().pop();

        assert!(
            factorial
                .validate(&grid)
                .unwrap_err()
                .contains("exactly its two declared levels")
        );
    }

    #[cfg(any())]
    #[test]
    fn v2_parameterized_handlers_do_not_republish_legacy_presets() {
        let capabilities = rust_handler_capabilities_v2();
        let names_for = |role| {
            capabilities
                .iter()
                .filter(|capability| capability.role == role)
                .map(|capability| capability.handler.as_str())
                .collect::<Vec<_>>()
        };
        assert_eq!(names_for(ExperimentComponentRoleV2::Loader), ["explicit_loader"]);
        assert_eq!(
            names_for(ExperimentComponentRoleV2::Trainer),
            ["python_training_worker"]
        );
        assert_eq!(names_for(ExperimentComponentRoleV2::Evaluator), ["resolved_loss"]);
        assert!(
            capabilities
                .iter()
                .all(|capability| !capability.handler.contains("p70_20_10"))
        );
    }

    fn model_link_result_json(extra: &str) -> String {
        format!(
            r#"{{
                "schema": "fern.model-link-probe-result.v1",
                "model": "fern",
                "density": "single_gaussian",
                "predictive_law": "single_gaussian",
                "action_dim": null,
                "shape_mode": "tiny_cpu",
                "model_construction": "production_factory",
                "parameter_contract": "cpu_float32",
                "output_contract": "checked",
                "loss_backward": "finite_connected",
                "optimizer_membership": "exact_trainable_parameters",
                "linked_parameter_groups": ["encoder"],
                "checkpoint_round_trip": "strict_safetensors"{extra}
            }}"#
        )
    }

    #[test]
    fn a_model_link_receipt_written_before_the_manifest_existed_still_parses() {
        let parsed: PythonModelLinkResultV1 =
            serde_json::from_str(&model_link_result_json("")).expect("legacy receipt must parse");
        assert!(parsed.architecture_manifest.is_none());
        // An absent manifest must not appear in the re-serialized receipt, or a
        // reader could not tell "did not report" from "reported nothing".
        let round_tripped = serde_json::to_string(&parsed).expect("serialize");
        assert!(!round_tripped.contains("architecture_manifest"));
    }

    #[test]
    fn an_observed_architecture_manifest_round_trips_with_its_occupied_slots() {
        let json = model_link_result_json(
            r#",
                "architecture_manifest": {
                    "seam_version": 1,
                    "slots": {
                        "action": {
                            "kind": "fern.action.separate_tde",
                            "parameter_groups": [
                                {"name": "past_action_conditioner.", "parameter_count": 12}
                            ]
                        },
                        "density": {
                            "kind": "fern.density.finite_mixture",
                            "variant": "bounded_mdn",
                            "parameter_groups": [{"name": "gate_head.", "parameter_count": 4}]
                        }
                    },
                    "parameter_groups": [
                        {"name": "model.enc_layers.", "parameter_count": 100},
                        {"name": "model.action_extension.", "parameter_count": 12}
                    ],
                    "parameter_count": 112
                }"#,
        );
        let parsed: PythonModelLinkResultV1 = serde_json::from_str(&json).expect("parse manifest");
        let manifest = parsed
            .architecture_manifest
            .clone()
            .expect("the manifest must survive parsing");
        assert_eq!(manifest.seam_version, 1);
        assert_eq!(manifest.parameter_count, 112);
        // An empty slot is ABSENT, never present and null.
        assert_eq!(manifest.slots.len(), 2);
        assert_eq!(manifest.slots["action"].kind, "fern.action.separate_tde");
        assert_eq!(manifest.slots["action"].variant, None);
        assert_eq!(manifest.slots["density"].variant.as_deref(), Some("bounded_mdn"));

        let again: PythonModelLinkResultV1 =
            serde_json::from_str(&serde_json::to_string(&parsed).expect("serialize")).expect("reparse");
        assert_eq!(again, parsed);
    }

    /// The manifest below was produced by the real Python model-link probe on a
    /// real constructed Fern, not written by hand here. It is committed so this
    /// crate's type is checked against bytes the other language actually emits:
    /// a Rust struct that only ever parses Rust-authored JSON has been agreed
    /// with itself.
    #[test]
    fn the_manifest_python_actually_emits_parses_into_this_type() {
        const FROM_PYTHON: &str = include_str!("../tests/fixtures/observed_architecture_manifest_from_python.json");
        let manifest: ObservedArchitectureManifestV1 =
            serde_json::from_str(FROM_PYTHON).expect("Python's manifest must parse");
        assert_eq!(manifest.seam_version, 1);
        let action = manifest
            .slots
            .get("action")
            .expect("this fixture was built with the action slot filled");
        assert_eq!(action.kind, "fern.action.separate_tde");
        assert_eq!(action.variant.as_deref(), Some("current_two_site"));
        // The slot's own groups must be a subset of the whole model's total.
        let slot_total: u64 = action.parameter_groups.iter().map(|group| group.parameter_count).sum();
        assert!(slot_total > 0 && slot_total < manifest.parameter_count);
        let group_total: u64 = manifest
            .parameter_groups
            .iter()
            .map(|group| group.parameter_count)
            .sum();
        assert_eq!(
            group_total, manifest.parameter_count,
            "the reported groups must account for every parameter"
        );
    }

    #[test]
    fn an_unknown_manifest_field_is_refused_rather_than_ignored() {
        let json = model_link_result_json(
            r#",
                "architecture_manifest": {
                    "seam_version": 1,
                    "slots": {},
                    "parameter_groups": [],
                    "parameter_count": 0,
                    "inferred_from_the_request": true
                }"#,
        );
        let error = serde_json::from_str::<PythonModelLinkResultV1>(&json)
            .expect_err("an undeclared manifest field must fail, not be dropped");
        assert!(
            error.to_string().contains("inferred_from_the_request"),
            "the refusal must name the field it did not expect, got {error}"
        );
    }
}
