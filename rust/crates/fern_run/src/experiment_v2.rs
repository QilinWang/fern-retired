//! Lower one resolved compositional Experiment into complete worker configs.
//!
//! The authoring resolver proves that each component is supported by the
//! frozen implementation.  This module owns the next boundary: every grid
//! coordinate becomes one complete `fern.cfg.v2`, with no preset lookup or
//! worker-side default filling.  A `LoweredExperimentV2` is deliberately not
//! executable; only attaching successful production model-link results can
//! construct `CompiledExperimentV2`.

use std::collections::{BTreeMap, BTreeSet};

use fern_json_contract::{JsonContract, canonical_json_bytes};
use fern_recipes::{
    ABLATION_RESOLUTION_SCHEMA_V2, AblationResolutionV2, DataEngine, EXPERIMENT_RESOLUTION_SCHEMA_V2,
    ExperimentResolutionV2, MODEL_LINK_RECEIPT_SCHEMA_V1, ModelLinkReceiptV1, ResolvedActionV2,
    ResolvedExperimentArmV2, ResolvedExperimentManifestV2, WorkloadArmPreviewV2, WorkloadPreviewV2,
};
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};

use crate::resolved_cfg::{
    ActionConfig, ActionHandlerKind, ActionVariantKind, AmpDTypeKind, ArtifactConfig, ArtifactPolicy, CheckpointSource,
    CompileConfig, DTypeKind, DataConfig, DeviceKind, ExecutionConfig, ExecutionKind, LossConfig, ModelConfig,
    NoiseKind, NormalizationKind, OptimConfig, RESOLVED_CONFIG_SCHEMA, ResolvedConfigSpec, RuntimeConfig,
    SchedulerConfig, TaskConfig, TrainConfig, TrainingTargetConfig,
};

pub const COMPILED_EXPERIMENT_SCHEMA_V2: &str = "fern.compiled-experiment.v2";
pub const COMPILED_ABLATION_SCHEMA_V2: &str = "fern.compiled-ablation.v2";

#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(deny_unknown_fields)]
struct WorldParametersV2 {
    channels: u64,
    noise_kind: NoiseKind,
    noise_ar1_phi: f64,
    relative_dose: bool,
    run_whiten: bool,
    run_rewind: bool,
}

#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(deny_unknown_fields)]
struct LoaderParametersV2 {
    train_start: f64,
    train_end: f64,
    val_start: f64,
    val_end: f64,
    test_start: f64,
    test_end: f64,
    normalization: NormalizationKind,
    batch_size: u64,
    num_workers: u64,
}

#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(deny_unknown_fields)]
struct TrainerParametersV2 {
    execution_kind: ExecutionKind,
    #[serde(default)]
    checkpoint_source: Option<CheckpointSource>,
    task_name: String,
    channel_independent: bool,
    epochs: u64,
    patience: u64,
    grace_period: u64,
    optim: OptimConfig,
    scheduler: SchedulerConfig,
    dtype: DTypeKind,
    amp_dtype: AmpDTypeKind,
    compile: CompileConfig,
    artifact_policy: ArtifactPolicy,
    #[serde(default)]
    training_target: TrainingTargetConfig,
}

#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields)]
struct SeparateTdeActionParametersV2 {
    action_dim: u64,
    #[serde(default)]
    future_only: bool,
    #[serde(default)]
    variant: crate::resolved_cfg::ActionVariantKind,
}

#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields)]
struct ExperimentDrawPairV2 {
    trajectory_draw: ExperimentDrawV2,
    noise_draw: ExperimentDrawV2,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ExperimentDrawV2 {
    pub ordinal: u64,
    /// The declared bank base is preserved separately from the derived draw
    /// seed because the production worker records both provenance facts.
    pub seed_base: u64,
    pub seed: u64,
}

/// One completely explicit scientific Coordinate and its complete worker cfg.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct CompiledExperimentCellV2 {
    pub target_idx: u64,
    pub target_hash: String,
    pub config_hash: String,
    pub coordinates: BTreeMap<String, Value>,
    pub config: ResolvedConfigSpec,
    pub source_name: String,
    pub source_is_stochastic: bool,
    pub draw_design: String,
    pub data_engine: DataEngine,
    pub model_seed: i64,
    pub noise_dose: f64,
    pub trajectory_draw: ExperimentDrawV2,
    pub noise_draw: ExperimentDrawV2,
    pub relative_dose: bool,
    pub run_whiten: bool,
    pub run_rewind: bool,
}

/// Checked lowering output.  It cannot enter Plan registration because it has
/// not yet shown that the selected production model, loss, optimizer, action
/// path, and checkpoint format are connected.
#[derive(Debug, Clone)]
pub struct LoweredExperimentV2 {
    resolution: ExperimentResolutionV2,
    execution_profile_id: String,
    execution_profile_revision: String,
    runtime_device: DeviceKind,
    cells: Vec<CompiledExperimentCellV2>,
}

impl LoweredExperimentV2 {
    pub fn cells(&self) -> &[CompiledExperimentCellV2] {
        &self.cells
    }

    pub fn model_link_configs(&self) -> Result<BTreeMap<String, ResolvedConfigSpec>, String> {
        let mut configs = BTreeMap::new();
        for cell in &self.cells {
            let fixture = model_link_fixture(cell)?;
            configs.entry(cell.config_hash.clone()).or_insert(fixture);
        }
        Ok(configs)
    }

    pub fn channels(&self) -> u64 {
        self.cells[0]
            .config
            .task
            .channels
            .expect("V2 lowering requires an explicit channel count")
    }

    pub fn action_dim(&self) -> Option<u64> {
        self.cells[0].config.action.action_dim()
    }

    /// Every compiled Experiment is built here, so taking the source identity
    /// as an argument is what makes it impossible to compile one without it.
    pub fn finish(
        self,
        model_link_receipts: BTreeMap<String, ModelLinkReceiptV1>,
        implementation_id: String,
        implementation_binding_id: String,
    ) -> Result<CompiledExperimentV2, String> {
        validate_model_link_receipts(&self.resolution.resolved.manifest, &self.cells, &model_link_receipts)?;
        let compiled = CompiledExperimentV2 {
            schema: COMPILED_EXPERIMENT_SCHEMA_V2.to_string(),
            resolution: self.resolution,
            execution_profile_id: self.execution_profile_id,
            execution_profile_revision: self.execution_profile_revision,
            runtime_device: self.runtime_device,
            cells: self.cells,
            model_link_receipts,
            implementation_id: Some(implementation_id),
            implementation_binding_id: Some(implementation_binding_id),
        };
        compiled.validate()?;
        Ok(compiled)
    }
}

/// The sole V2 object Plan registration may accept.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct CompiledExperimentV2 {
    schema: String,
    resolution: ExperimentResolutionV2,
    execution_profile_id: String,
    execution_profile_revision: String,
    runtime_device: DeviceKind,
    cells: Vec<CompiledExperimentCellV2>,
    model_link_receipts: BTreeMap<String, ModelLinkReceiptV1>,
    /// The source authority this object was compiled against.
    ///
    /// Option, not String, and deliberately so: this type denies unknown
    /// fields, and the stage-driven plans already registered embed a compiled
    /// object without these. A required field would make every historical
    /// definition fail to load and would move definition_digest. `validate`
    /// therefore does not demand Some — `finish` supplies it by construction,
    /// and run time refuses a plan that lacks it.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    implementation_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    implementation_binding_id: Option<String>,
}

impl CompiledExperimentV2 {
    pub fn implementation_id(&self) -> Option<&str> {
        self.implementation_id.as_deref()
    }

    pub fn implementation_binding_id(&self) -> Option<&str> {
        self.implementation_binding_id.as_deref()
    }
}

impl JsonContract for CompiledExperimentV2 {
    const SCHEMA_ID: &'static str = COMPILED_EXPERIMENT_SCHEMA_V2;
}

impl CompiledExperimentV2 {
    pub fn validate(&self) -> Result<(), String> {
        if self.schema != COMPILED_EXPERIMENT_SCHEMA_V2 {
            return Err(format!(
                "compiled Experiment schema must be {COMPILED_EXPERIMENT_SCHEMA_V2:?}, got {:?}",
                self.schema
            ));
        }
        let lowered = lower_experiment_v2(
            self.resolution.clone(),
            self.execution_profile_id.clone(),
            self.execution_profile_revision.clone(),
            self.runtime_device,
        )?;
        if lowered.cells != self.cells {
            return Err("compiled Experiment cells differ from deterministic V2 lowering".to_string());
        }
        validate_model_link_receipts(
            &self.resolution.resolved.manifest,
            &self.cells,
            &self.model_link_receipts,
        )
    }

    pub fn resolution(&self) -> &ExperimentResolutionV2 {
        &self.resolution
    }

    pub fn execution_profile_id(&self) -> &str {
        &self.execution_profile_id
    }

    pub fn execution_profile_revision(&self) -> &str {
        &self.execution_profile_revision
    }

    pub fn cells(&self) -> &[CompiledExperimentCellV2] {
        &self.cells
    }

    pub fn runtime_device(&self) -> DeviceKind {
        self.runtime_device
    }

    pub fn implementation_bundle(&self) -> &fern_recipes::ImplementationBundleResolutionV2 {
        &self.resolution.implementation_bundle
    }

    pub fn model_link_configs(&self) -> Result<BTreeMap<String, ResolvedConfigSpec>, String> {
        let mut configs = BTreeMap::new();
        for cell in &self.cells {
            configs.insert(cell.config_hash.clone(), model_link_fixture(cell)?);
        }
        Ok(configs)
    }

    pub fn channels(&self) -> u64 {
        self.cells[0]
            .config
            .task
            .channels
            .expect("V2 lowering requires an explicit channel count")
    }

    pub fn action_dim(&self) -> Option<u64> {
        self.cells[0].config.action.action_dim()
    }

    pub fn model_link_receipts(&self) -> &BTreeMap<String, ModelLinkReceiptV1> {
        &self.model_link_receipts
    }

    pub fn manifest_sha256(&self) -> &str {
        &self.resolution.resolved.manifest_sha256
    }

    pub fn recipe_hash(&self) -> String {
        self.resolution
            .resolved
            .manifest_sha256
            .strip_prefix("experiment:sha256:")
            .expect("validate resolution before reading its short recipe hash")[..16]
            .to_string()
    }

    pub fn model_type(&self) -> &str {
        &self.cells[0].config.model.model_type
    }
}

/// The complete executable proof for one same-implementation control/treatment
/// pair. The pair resolution remains present after its two arms are lowered, so
/// Plan registration can prove that the cells came from one declared ablation
/// instead of two unrelated Experiments that happen to share code.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct CompiledAblationV2 {
    schema: String,
    resolution: AblationResolutionV2,
    control: CompiledExperimentV2,
    treatment: CompiledExperimentV2,
}

impl JsonContract for CompiledAblationV2 {
    const SCHEMA_ID: &'static str = COMPILED_ABLATION_SCHEMA_V2;
}

impl CompiledAblationV2 {
    pub fn new(
        resolution: AblationResolutionV2,
        control: CompiledExperimentV2,
        treatment: CompiledExperimentV2,
    ) -> Result<Self, String> {
        let compiled = Self {
            schema: COMPILED_ABLATION_SCHEMA_V2.to_string(),
            resolution,
            control,
            treatment,
        };
        compiled.validate()?;
        Ok(compiled)
    }

    pub fn validate(&self) -> Result<(), String> {
        if self.schema != COMPILED_ABLATION_SCHEMA_V2 {
            return Err(format!(
                "compiled Ablation schema must be {COMPILED_ABLATION_SCHEMA_V2:?}, got {:?}",
                self.schema
            ));
        }
        if self.resolution.schema != ABLATION_RESOLUTION_SCHEMA_V2 {
            return Err(format!(
                "compiled Ablation resolution schema must be {ABLATION_RESOLUTION_SCHEMA_V2:?}, got {:?}",
                self.resolution.schema
            ));
        }
        self.control.validate()?;
        self.treatment.validate()?;
        if self.control.execution_profile_id != self.treatment.execution_profile_id
            || self.control.execution_profile_revision != self.treatment.execution_profile_revision
            || self.control.runtime_device != self.treatment.runtime_device
        {
            return Err(
                "compiled Ablation arms must use one execution profile revision and runtime device".to_string(),
            );
        }
        if self.control.implementation_bundle().implementation != self.resolution.implementation
            || self.treatment.implementation_bundle().implementation != self.resolution.implementation
        {
            return Err("compiled Ablation arms do not use the pair's one shared implementation".to_string());
        }
        // The bundle check above compares authored implementations. This one
        // compares the source authority they were compiled against, which is a
        // different fact: two arms can name one bundle and still have been
        // built from different bytes.
        match (
            self.control.implementation_binding_id(),
            self.treatment.implementation_binding_id(),
        ) {
            (Some(control), Some(treatment)) if control == treatment => {}
            (None, None) => {}
            _ => return Err("compiled Ablation arms do not share one implementation binding".to_string()),
        }
        match (self.control.implementation_id(), self.treatment.implementation_id()) {
            (Some(control), Some(treatment)) if control == treatment => {}
            (None, None) => {}
            _ => return Err("compiled Ablation arms do not share one implementation".to_string()),
        }
        let expected_control = experiment_resolution_for_ablation_arm_v2(&self.resolution, AblationArmV2::Control)?;
        let expected_treatment = experiment_resolution_for_ablation_arm_v2(&self.resolution, AblationArmV2::Treatment)?;
        if self.control.resolution != expected_control {
            return Err("compiled Ablation control differs from deterministic pair lowering".to_string());
        }
        if self.treatment.resolution != expected_treatment {
            return Err("compiled Ablation treatment differs from deterministic pair lowering".to_string());
        }
        Ok(())
    }

    pub fn resolution(&self) -> &AblationResolutionV2 {
        &self.resolution
    }

    pub fn control(&self) -> &CompiledExperimentV2 {
        &self.control
    }

    pub fn treatment(&self) -> &CompiledExperimentV2 {
        &self.treatment
    }

    pub fn execution_profile_id(&self) -> &str {
        self.control.execution_profile_id()
    }

    pub fn implementation_bundle(&self) -> &fern_recipes::ImplementationBundleResolutionV2 {
        &self.resolution.implementation_bundle
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AblationArmV2 {
    Control,
    Treatment,
}

/// Project one arm of a verified pair into the existing single-Experiment
/// lowering boundary without inventing a second authoring route.
pub fn experiment_resolution_for_ablation_arm_v2(
    resolution: &AblationResolutionV2,
    arm: AblationArmV2,
) -> Result<ExperimentResolutionV2, String> {
    let (arm_name, resolved): (&str, &ResolvedExperimentArmV2) = match arm {
        AblationArmV2::Control => ("control", &resolution.control),
        AblationArmV2::Treatment => ("treatment", &resolution.treatment),
    };
    let cells = resolution
        .workload
        .arms
        .iter()
        .find(|preview| preview.arm == arm_name)
        .ok_or_else(|| format!("compiled Ablation workload has no {arm_name:?} arm"))?
        .cells;
    let estimated_gpu_seconds = resolution
        .workload
        .gpu_seconds_per_cell
        .map(|seconds| {
            cells
                .checked_mul(seconds)
                .ok_or_else(|| format!("compiled Ablation {arm_name} GPU estimate overflows u64"))
        })
        .transpose()?;
    Ok(ExperimentResolutionV2 {
        schema: EXPERIMENT_RESOLUTION_SCHEMA_V2.to_string(),
        question: resolution.question.clone(),
        design_ref: resolution.design_ref.clone(),
        inference: resolution.inference,
        workload: WorkloadPreviewV2 {
            arms: vec![WorkloadArmPreviewV2 {
                arm: "experiment".to_string(),
                cells,
            }],
            total_cells: cells,
            max_cells: resolution.workload.max_cells,
            estimated_gpu_seconds,
            gpu_seconds_per_cell: resolution.workload.gpu_seconds_per_cell,
            estimate_basis: resolution.workload.estimate_basis.clone(),
            max_gpu_seconds: resolution.workload.max_gpu_seconds,
        },
        // Recomputed, not copied from the pair. The pair's warnings name the
        // arm they are about ("control", "treatment"); this projection is a
        // one-arm experiment whose arm is called "experiment", so copying the
        // strings would leave a receipt whose own verification refuses it.
        warnings: crate::csharp_experiment::resolution_warnings_via_csharp(&[("experiment", resolved)])?,
        implementation_bundle: resolution.implementation_bundle.clone(),
        resolved: resolved.clone(),
    })
}

/// Deterministically expand one checked manifest into complete cfg objects.
/// The execution profile supplies only the target device and its immutable
/// revision; every scientific/training value comes from a single component
/// owner in the resolved manifest.
pub fn lower_experiment_v2(
    resolution: ExperimentResolutionV2,
    execution_profile_id: String,
    execution_profile_revision: String,
    runtime_device: DeviceKind,
) -> Result<LoweredExperimentV2, String> {
    if execution_profile_id.trim().is_empty() || execution_profile_revision.trim().is_empty() {
        return Err("V2 lowering requires an execution-profile id and revision".to_string());
    }
    let manifest = &resolution.resolved.manifest;
    validate_manifest_identity(manifest, &resolution.resolved.manifest_sha256)?;
    if resolution.implementation_bundle.implementation != manifest.implementation {
        return Err("implementation bundle and resolved manifest name different implementations".to_string());
    }
    if resolution.workload.total_cells == 0 {
        return Err("resolved V2 experiment declares zero cells".to_string());
    }
    let recipe = &manifest.components.recipe;
    let world: WorldParametersV2 = decode_parameters("world", &recipe.world.parameters)?;
    let loader: LoaderParametersV2 = decode_parameters("loader", &recipe.loader.parameters)?;
    let trainer: TrainerParametersV2 = decode_parameters("trainer", &recipe.trainer.parameters)?;
    let loss: LossConfig = decode_parameters("evaluator", &recipe.evaluator.parameters)?;
    if trainer.execution_kind == ExecutionKind::PretrainedInference && trainer.checkpoint_source.is_none() {
        return Err(
            "new V2 pretrained_inference requires checkpoint_source.kind=pretrained_asset with model_id and immutable revision"
                .to_string(),
        );
    }
    if world.channels == 0 {
        return Err("world.parameters.channels must be positive".to_string());
    }
    let action = match &manifest.components.action {
        ResolvedActionV2::StateOnly => ActionConfig::StateOnly,
        ResolvedActionV2::Conditioned { card } => {
            if card.handler != "fern.separate_tde" {
                return Err(format!(
                    "V2 execution has no production action path for handler {:?}",
                    card.handler
                ));
            }
            let parameters: SeparateTdeActionParametersV2 = decode_parameters("action", &card.parameters)?;
            if parameters.action_dim == 0 {
                return Err("action.parameters.action_dim must be positive".to_string());
            }
            admit_conditioned_v2_action_world(&recipe.world.handler, parameters.action_dim, parameters.future_only)?;
            if trainer.channel_independent {
                return Err(
                    "action-conditioned V2 execution does not fold state channels into the batch axis; trainer.channel_independent must be false"
                        .to_string(),
                );
            }
            if world.run_rewind {
                return Err(
                    "action-conditioned V2 execution has no declared intervention semantics for rewind; world.run_rewind must be false"
                        .to_string(),
                );
            }
            ActionConfig::GlobalActionVector {
                handler: ActionHandlerKind::FernSeparateTde,
                action_dim: parameters.action_dim,
                future_only: parameters.future_only,
                variant: parameters.variant,
            }
        }
    };
    if trainer.dtype != DTypeKind::Float32 || trainer.amp_dtype != AmpDTypeKind::None || trainer.compile.enabled {
        return Err(
            "the first V2 execution slice requires float32, amp_dtype=none, and compile.enabled=false so the production model-link fixture tests the executed numerical path"
                .to_string(),
        );
    }
    let source = crate::csharp_experiment::resolve_dataset_source_capability(&recipe.world.handler)?;
    let model_type = manifest
        .configuration_facts
        .get("model.type")
        .and_then(Value::as_str)
        .filter(|value| !value.trim().is_empty())
        .ok_or_else(|| "selected architecture capability must emit configuration fact model.type".to_string())?;
    let density_runtime_name = manifest
        .configuration_facts
        .get("density.runtime_name")
        .and_then(Value::as_str)
        .filter(|value| !value.trim().is_empty())
        .ok_or_else(|| "selected density capability must emit configuration fact density.runtime_name".to_string())?;
    let forecast_representation = manifest
        .configuration_facts
        .get("forecast.representation")
        .and_then(Value::as_str)
        .filter(|value| !value.trim().is_empty())
        .ok_or_else(|| {
            "selected architecture capability must emit configuration fact forecast.representation".to_string()
        })?;
    let model_knobs = model_knobs(
        &manifest.components.architecture.parameters,
        &manifest.components.density.parameters,
        forecast_representation,
        density_runtime_name,
        loss.probability.num_train_samples,
        loss.probability.num_eval_samples,
    )?;
    let coordinates = expand_grid(&recipe.grid, recipe.factorial.as_ref())?;
    if u64::try_from(coordinates.len()).map_err(|_| "V2 grid is too large".to_string())?
        != resolution.workload.total_cells
    {
        return Err(format!(
            "resolved workload says {} cells, but executable grid expands to {}",
            resolution.workload.total_cells,
            coordinates.len()
        ));
    }

    let mut cells = Vec::with_capacity(coordinates.len());
    for (target_idx, coordinate) in coordinates.into_iter().enumerate() {
        let pred_len = required_u64(&coordinate, "horizon")?;
        let model_seed = required_i64(&coordinate, "model_seed")?;
        let noise_dose = required_f64(&coordinate, "noise_dose")?;
        let (trajectory_draw, noise_draw) = required_draws(&coordinate)?;
        let cell_model_knobs = model_knobs_for_coordinate(&model_knobs, recipe, &coordinate)?;
        let config = ResolvedConfigSpec {
            schema: RESOLVED_CONFIG_SCHEMA.to_string(),
            // New identities are always minted under the current scheme; scheme
            // 1 is reproduced from stored configs and never written.
            identity_scheme: crate::resolved_cfg::CURRENT_IDENTITY_SCHEME,
            execution: ExecutionConfig {
                kind: trainer.execution_kind,
                checkpoint_source: trainer.checkpoint_source.clone(),
            },
            model: ModelConfig {
                model_type: model_type.to_string(),
                knobs: cell_model_knobs,
            },
            task: TaskConfig {
                seq_len: recipe.context_length,
                pred_len,
                channels: Some(world.channels),
                channel_independent: trainer.channel_independent,
            },
            action: action.clone(),
            data: DataConfig {
                train_start: loader.train_start,
                train_end: loader.train_end,
                val_start: loader.val_start,
                val_end: loader.val_end,
                test_start: loader.test_start,
                test_end: loader.test_end,
                normalization: loader.normalization,
                noise_kind: world.noise_kind,
                noise_ar1_phi: world.noise_ar1_phi,
            },
            train: TrainConfig {
                task_name: trainer.task_name.clone(),
                epochs: trainer.epochs,
                patience: trainer.patience,
                grace_period: trainer.grace_period,
                batch_size: loader.batch_size,
                num_workers: loader.num_workers,
                training_target: trainer.training_target.clone(),
            },
            optim: trainer.optim.clone(),
            scheduler: trainer.scheduler.clone(),
            runtime: RuntimeConfig {
                device: runtime_device,
                dtype: trainer.dtype,
                amp_dtype: trainer.amp_dtype,
                compile: trainer.compile.clone(),
            },
            loss: loss.clone(),
            artifact: ArtifactConfig {
                policy: trainer.artifact_policy,
            },
            bootstrap: None,
        };
        config.validate().map_err(|errors| {
            format!(
                "V2 cell {target_idx} lowers to an invalid fern.cfg.v2: {}",
                errors.join("; ")
            )
        })?;
        let config_hash = config.config_hash().map_err(|error| error.to_string())?;
        let target_hash = hash_16(&json!({
            "schema": "fern.experiment-target.v2",
            "manifest_sha256": resolution.resolved.manifest_sha256,
            "coordinates": coordinate,
            "config_hash": config_hash,
        }))?;
        cells.push(CompiledExperimentCellV2 {
            target_idx: u64::try_from(target_idx).map_err(|_| "V2 target index overflows u64".to_string())?,
            target_hash,
            config_hash,
            coordinates: coordinate,
            config,
            source_name: recipe.world.handler.clone(),
            source_is_stochastic: source.source_is_stochastic,
            draw_design: source.draw_design.clone(),
            data_engine: source.data_engine,
            model_seed,
            noise_dose,
            trajectory_draw,
            noise_draw,
            relative_dose: world.relative_dose,
            run_whiten: world.run_whiten,
            run_rewind: world.run_rewind,
        });
    }

    Ok(LoweredExperimentV2 {
        resolution,
        execution_profile_id,
        execution_profile_revision,
        runtime_device,
        cells,
    })
}

/// Apply optional factorial grid axes to already validated model knobs. This
/// pure cell projection neither knows model names nor decodes combined variant
/// labels; each factor states the exact existing knob it varies.
pub fn model_knobs_for_coordinate(
    base: &BTreeMap<String, Value>,
    recipe: &fern_recipes::RecipeComponentCardV2,
    coordinate: &BTreeMap<String, Value>,
) -> Result<BTreeMap<String, Value>, String> {
    let Some(factorial) = &recipe.factorial else {
        return Ok(base.clone());
    };
    let mut resolved = base.clone();
    for factor in &factorial.factors {
        if !base.contains_key(&factor.model_knob) {
            return Err(format!(
                "factorial axis {:?} is not an existing model knob",
                factor.model_knob
            ));
        }
        let level = coordinate
            .get(&factor.model_knob)
            .filter(|level| factor.levels.contains(level))
            .ok_or_else(|| {
                format!(
                    "factorial coordinate has no declared level for factor {:?}",
                    factor.name
                )
            })?;
        resolved.insert(factor.model_knob.clone(), level.clone());
    }
    Ok(resolved)
}

fn validate_manifest_identity(manifest: &ResolvedExperimentManifestV2, observed: &str) -> Result<(), String> {
    let expected = format!(
        "experiment:sha256:{:x}",
        Sha256::digest(canonical_json_bytes(manifest).map_err(|error| error.to_string())?)
    );
    if expected != observed {
        return Err(format!(
            "resolved manifest identity {observed} disagrees with canonical content {expected}"
        ));
    }
    Ok(())
}

fn model_knobs(
    architecture_parameters: &Value,
    density_parameters: &Value,
    forecast_representation: &str,
    density_runtime_name: &str,
    num_train_samples: u64,
    num_eval_samples: u64,
) -> Result<BTreeMap<String, Value>, String> {
    let architecture = architecture_parameters
        .as_object()
        .ok_or_else(|| "architecture parameters must be an object".to_string())?;
    let density = density_parameters
        .as_object()
        .ok_or_else(|| "density parameters must be an object".to_string())?;
    let mut knobs = architecture
        .iter()
        .map(|(key, value)| (key.clone(), value.clone()))
        .collect::<BTreeMap<_, _>>();
    match forecast_representation {
        "point" => {
            if density_runtime_name != "point_forecast" || !density.is_empty() {
                return Err(format!(
                    "a point forecast architecture requires the empty point.forecast output component, got runtime {density_runtime_name:?} with parameters {density:?}"
                ));
            }
            if knobs.contains_key("fern_density") {
                return Err("a point forecast architecture must not write model.knobs.fern_density".to_string());
            }
            return Ok(knobs);
        }
        "distribution" => {}
        other => {
            return Err(format!(
                "architecture capability emitted unsupported forecast.representation {other:?}; expected point or distribution"
            ));
        }
    }
    for (key, value) in density {
        if knobs.insert(key.clone(), value.clone()).is_some() {
            return Err(format!(
                "architecture and density both write model.knobs.{key}; one field must have one component owner"
            ));
        }
    }
    for (owner, parameters) in [("architecture", architecture), ("density", density)] {
        if parameters.contains_key("fern_density") {
            return Err(format!(
                "{owner} parameters must not write derived model.knobs.fern_density; the selected density capability owns density.runtime_name"
            ));
        }
    }
    knobs.insert(
        "fern_density".to_string(),
        Value::String(density_runtime_name.to_string()),
    );
    if density_runtime_name == "single_gaussian" {
        knobs.insert("mixture_component_count".to_string(), Value::from(1));
    }
    knobs.insert("num_train_samples".to_string(), Value::from(num_train_samples));
    knobs.insert("num_eval_samples".to_string(), Value::from(num_eval_samples));
    Ok(knobs)
}

fn decode_parameters<T: for<'de> Deserialize<'de>>(role: &str, value: &Value) -> Result<T, String> {
    serde_json::from_value(value.clone())
        .map_err(|error| format!("{role} handler parameters cannot lower to the worker contract: {error}"))
}

fn expand_grid(
    grid: &BTreeMap<String, Vec<Value>>,
    factorial: Option<&fern_recipes::BinaryFactorialDesignV2>,
) -> Result<Vec<BTreeMap<String, Value>>, String> {
    let mut cartesian = ["horizon", "model_seed", "noise_dose", "noise_draw", "trajectory_draw"]
        .into_iter()
        .map(str::to_string)
        .collect::<BTreeSet<_>>();
    let mut paired = ["draw_pair", "horizon", "model_seed", "noise_dose"]
        .into_iter()
        .map(str::to_string)
        .collect::<BTreeSet<_>>();
    if let Some(factorial) = factorial {
        factorial.validate(grid)?;
        for factor in &factorial.factors {
            cartesian.insert(factor.model_knob.clone());
            paired.insert(factor.model_knob.clone());
        }
    }
    let observed = grid.keys().cloned().collect::<BTreeSet<_>>();
    if observed != cartesian && observed != paired {
        return Err(format!(
            "executable V2 grid requires either Cartesian axes {:?} or explicit paired-draw axes {:?}; found {:?}",
            cartesian, paired, observed
        ));
    }
    let mut rows = vec![BTreeMap::new()];
    for (axis, values) in grid {
        if values.is_empty() {
            return Err(format!("V2 grid axis {axis:?} is empty"));
        }
        let mut expanded = Vec::with_capacity(rows.len().saturating_mul(values.len()));
        for row in &rows {
            for value in values {
                let mut next = row.clone();
                next.insert(axis.clone(), value.clone());
                expanded.push(next);
            }
        }
        rows = expanded;
    }
    Ok(rows)
}

fn required_u64(coordinate: &BTreeMap<String, Value>, field: &str) -> Result<u64, String> {
    coordinate
        .get(field)
        .and_then(Value::as_u64)
        .filter(|value| *value > 0)
        .ok_or_else(|| format!("V2 grid {field} must contain positive integers"))
}

fn required_i64(coordinate: &BTreeMap<String, Value>, field: &str) -> Result<i64, String> {
    coordinate
        .get(field)
        .and_then(Value::as_i64)
        .ok_or_else(|| format!("V2 grid {field} must contain signed integers"))
}

fn required_f64(coordinate: &BTreeMap<String, Value>, field: &str) -> Result<f64, String> {
    coordinate
        .get(field)
        .and_then(Value::as_f64)
        .filter(|value| value.is_finite() && *value >= 0.0)
        .ok_or_else(|| format!("V2 grid {field} must contain finite non-negative numbers"))
}

fn required_draw(coordinate: &BTreeMap<String, Value>, field: &str) -> Result<ExperimentDrawV2, String> {
    let draw: ExperimentDrawV2 = coordinate
        .get(field)
        .cloned()
        .ok_or_else(|| format!("V2 grid is missing {field}"))
        .and_then(|value| {
            serde_json::from_value(value).map_err(|error| format!("V2 grid {field} draw is invalid: {error}"))
        })?;
    Ok(draw)
}

fn required_draws(coordinate: &BTreeMap<String, Value>) -> Result<(ExperimentDrawV2, ExperimentDrawV2), String> {
    if let Some(value) = coordinate.get("draw_pair") {
        let pair: ExperimentDrawPairV2 =
            serde_json::from_value(value.clone()).map_err(|error| format!("V2 grid draw_pair is invalid: {error}"))?;
        return Ok((pair.trajectory_draw, pair.noise_draw));
    }
    Ok((
        required_draw(coordinate, "trajectory_draw")?,
        required_draw(coordinate, "noise_draw")?,
    ))
}

fn hash_16(value: &Value) -> Result<String, String> {
    let bytes = serde_json::to_vec(value).map_err(|error| error.to_string())?;
    let hex = format!("{:x}", Sha256::digest(bytes));
    Ok(hex[..16].to_string())
}

fn model_link_fixture(cell: &CompiledExperimentCellV2) -> Result<ResolvedConfigSpec, String> {
    let mut fixture = cell.config.clone();
    fixture.runtime = RuntimeConfig {
        device: DeviceKind::Cpu,
        dtype: DTypeKind::Float32,
        amp_dtype: AmpDTypeKind::None,
        compile: CompileConfig {
            enabled: false,
            mode: "default".to_string(),
            fullgraph: false,
            dynamic: false,
        },
    };
    fixture.validate().map_err(|errors| {
        format!(
            "model-link fixture for config {} is invalid: {}",
            cell.config_hash,
            errors.join("; ")
        )
    })?;
    Ok(fixture)
}

fn validate_model_link_receipts(
    manifest: &ResolvedExperimentManifestV2,
    cells: &[CompiledExperimentCellV2],
    receipts: &BTreeMap<String, ModelLinkReceiptV1>,
) -> Result<(), String> {
    let expected = cells
        .iter()
        .map(|cell| cell.config_hash.clone())
        .collect::<BTreeSet<_>>();
    let observed = receipts.keys().cloned().collect::<BTreeSet<_>>();
    if expected != observed {
        return Err(format!(
            "model-link receipt keys must equal concrete config hashes; expected {expected:?}, found {observed:?}"
        ));
    }
    let expected_model = manifest
        .configuration_facts
        .get("model.type")
        .and_then(Value::as_str)
        .ok_or_else(|| "architecture capability must emit model.type".to_string())?;
    let expected_density = manifest
        .configuration_facts
        .get("density.runtime_name")
        .and_then(Value::as_str)
        .ok_or_else(|| "density capability must emit density.runtime_name".to_string())?;
    let expected_law = manifest
        .configuration_facts
        .get("predictive_law.family")
        .and_then(Value::as_str)
        .ok_or_else(|| "density capability must emit predictive_law.family".to_string())?;
    for cell in cells {
        let receipt = &receipts[&cell.config_hash];
        if receipt.schema != MODEL_LINK_RECEIPT_SCHEMA_V1 {
            return Err(format!(
                "config {} model-link receipt has schema {:?}, expected {MODEL_LINK_RECEIPT_SCHEMA_V1}",
                cell.config_hash, receipt.schema
            ));
        }
        if receipt.implementation != manifest.implementation {
            return Err(format!(
                "config {} model-link receipt belongs to implementation {}, expected {}",
                cell.config_hash,
                receipt.implementation.as_str(),
                manifest.implementation.as_str()
            ));
        }
        if receipt.config_hash != cell.config_hash {
            return Err(format!(
                "config {} model-link receipt names config hash {:?}",
                cell.config_hash, receipt.config_hash
            ));
        }
        let fixture = model_link_fixture(cell)?;
        let fixture_value = serde_json::to_value(&fixture)
            .map_err(|error| format!("cannot project model-link fixture for {}: {error}", cell.config_hash))?;
        let fixture_sha256 = format!(
            "sha256:{:x}",
            Sha256::digest(canonical_json_bytes(&fixture_value).map_err(|error| error.to_string())?)
        );
        if receipt.fixture_sha256 != fixture_sha256 {
            return Err(format!(
                "config {} model-link receipt fixture {} does not match {}",
                cell.config_hash, receipt.fixture_sha256, fixture_sha256
            ));
        }
        let expected_channels = cell
            .config
            .task
            .channels
            .ok_or_else(|| format!("config {} has no channel count", cell.config_hash))?;
        if receipt.channels != expected_channels {
            return Err(format!(
                "config {} model-link receipt channels {} do not match {}",
                cell.config_hash, receipt.channels, expected_channels
            ));
        }
        let expected_action_dim = cell.config.action.action_dim();
        if receipt.action_dim != expected_action_dim || receipt.result.action_dim != receipt.action_dim {
            return Err(format!(
                "config {} model-link action dimensions outer={:?}, result={:?}, expected={:?}",
                cell.config_hash, receipt.action_dim, receipt.result.action_dim, expected_action_dim
            ));
        }
        let result = &receipt.result;
        if result.model != expected_model || result.model != cell.config.model.model_type {
            return Err(format!(
                "config {} model-link report names model {:?}, expected {:?}",
                cell.config_hash, result.model, expected_model
            ));
        }
        if result.density != expected_density {
            return Err(format!(
                "config {} model-link report names density {:?}, expected {:?}",
                cell.config_hash, result.density, expected_density
            ));
        }
        if result.predictive_law != expected_law {
            return Err(format!(
                "config {} model-link emitted law {:?}, but the frozen capability declares {expected_law:?}",
                cell.config_hash, result.predictive_law
            ));
        }
        if expected_density == "point_forecast" {
            if result.linked_parameter_groups.is_empty() {
                return Err(format!(
                    "config {} point-model probe reported no parameter group connected to its training objective",
                    cell.config_hash
                ));
            }
        } else {
            let expected_groups = expected_model_link_parameter_groups(
                expected_density,
                receipt.action_dim,
                cell.config.action.future_only(),
                cell.config.action.variant(),
            )?;
            if result.linked_parameter_groups != expected_groups {
                return Err(format!(
                    "config {} model-link linked groups {:?}, expected {:?} for density {:?}",
                    cell.config_hash, result.linked_parameter_groups, expected_groups, expected_density
                ));
            }
        }
    }
    Ok(())
}

/// The only action-conditioned V2 worlds that may enter Plan lowering.
///
/// `controlled_pendulum` is the existing one-torque production slice and keeps
/// both past and future action tensors. `level0a_controlled_lgssm` is the
/// Program II Level 0A canary: two action coordinates, future actions only.
/// Any other world, or the wrong action-dim / past-action pairing, is refused
/// rather than widened into an unverified handler.
pub fn admit_conditioned_v2_action_world(
    world_handler: &str,
    action_dim: u64,
    future_only: bool,
) -> Result<(), String> {
    match (world_handler, action_dim, future_only) {
        ("controlled_pendulum", 1, false) => Ok(()),
        ("controlled_pendulum", 1, true) => Err(
            "controlled_pendulum is partially observed; Level 0A future_only semantics belong on level0a_controlled_lgssm"
                .to_string(),
        ),
        ("controlled_pendulum", other, _) => Err(format!(
            "controlled_pendulum exposes exactly one action coordinate; action_dim must be 1, got {other}"
        )),
        ("level0a_controlled_lgssm", 2, true) => Ok(()),
        ("level0a_controlled_lgssm", 2, false) => Err(
            "level0a_controlled_lgssm is fully observed and Markov; action.parameters.future_only must be true so PastActionConditioner is not part of the architecture identity"
                .to_string(),
        ),
        ("level0a_controlled_lgssm", other, _) => Err(format!(
            "level0a_controlled_lgssm exposes exactly two action coordinates; action_dim must be 2, got {other}"
        )),
        (other, _, _) => Err(format!(
            "V2 execution has no admitted action-conditioned world {other:?}; the live slices are controlled_pendulum and level0a_controlled_lgssm"
        )),
    }
}

fn expected_model_link_parameter_groups(
    density: &str,
    action_dim: Option<u64>,
    future_only: bool,
    action_variant: Option<ActionVariantKind>,
) -> Result<Vec<String>, String> {
    let mut groups = match density {
        "single_gaussian" => vec!["fern_rotation", "fern_scale", "fern_shift"],
        "bounded_mdn" => vec!["component_parameters", "mixture_gate"],
        "sparse_moe" => vec!["mixture_gate", "selected_expert_parameters"],
        "shared_transport_mixture_source" => vec![
            "mixture_gate",
            "shared_transport_rotation",
            "shared_transport_scale_shift",
            "source_component_parameters",
        ],
        // The seam-attached kind keeps the Core, so the Core's own transport
        // groups must be linked as well as the density's.
        "mixture_source_shared_hidden" => vec![
            "fern_rotation",
            "fern_scale",
            "fern_shift",
            "mixture_gate",
            "source_component_parameters",
        ],
        "mixture_fern_transports_shared_hidden" => vec![
            "component_transport_rotation",
            "component_transport_scale_shift",
            "fern_rotation",
            "fern_scale",
            "fern_shift",
            "mixture_gate",
            "source_component_parameters",
        ],
        "mixture_of_fern_transports" => vec![
            "component_transport_rotation",
            "component_transport_scale_shift",
            "mixture_gate",
        ],
        other => {
            return Err(format!(
                "model-link has no gradient-group contract for density {other:?}"
            ));
        }
    };
    if action_dim.is_some() {
        groups.push("future_action_response");
        if action_variant == Some(ActionVariantKind::ActionForecastModifiesState) {
            groups.push("action_forecast_head");
        } else if !future_only {
            groups.push("past_action_conditioner");
        }
    }
    groups.sort_unstable();
    Ok(groups.into_iter().map(str::to_string).collect())
}

#[cfg(test)]
mod tests {
    use super::*;
    use fern_recipes::{
        ActionSourceV2, ArchitectureComponentCardV2, ComponentSourceV2, DensityComponentCardV2,
        ExperimentComponentsSourceV2, ExperimentDraftV2,
    };

    #[test]
    fn executable_grid_refuses_an_implicit_draw_seed() {
        let grid = BTreeMap::from([
            ("horizon".to_string(), vec![json!(64)]),
            ("model_seed".to_string(), vec![json!(1)]),
            ("noise_dose".to_string(), vec![json!(0.0)]),
            (
                "noise_draw".to_string(),
                vec![json!({"ordinal": 0, "seed_base": 1000, "seed": 1000})],
            ),
        ]);
        let error = expand_grid(&grid, None).unwrap_err();
        assert!(error.contains("trajectory_draw"), "{error}");
    }

    #[test]
    fn explicit_draw_pairs_do_not_expand_to_a_cross_product() {
        let pairs = (0_u64..4)
            .map(|ordinal| {
                json!({
                    "trajectory_draw": {"ordinal": ordinal, "seed_base": 42, "seed": 100 + ordinal},
                    "noise_draw": {"ordinal": ordinal, "seed_base": 1000, "seed": 200 + ordinal}
                })
            })
            .collect::<Vec<_>>();
        let grid = BTreeMap::from([
            ("draw_pair".to_string(), pairs),
            ("horizon".to_string(), vec![json!(64)]),
            ("model_seed".to_string(), vec![json!(1)]),
            (
                "noise_dose".to_string(),
                vec![
                    json!(0.0),
                    json!(0.1),
                    json!(0.2),
                    json!(0.4),
                    json!(0.6),
                    json!(0.8),
                    json!(1.0),
                ],
            ),
        ]);
        let rows = expand_grid(&grid, None).unwrap();
        assert_eq!(rows.len(), 28, "seven doses times four paired banks");
        for row in rows {
            let (trajectory, noise) = required_draws(&row).unwrap();
            assert_eq!(trajectory.ordinal, noise.ordinal);
        }
    }

    #[test]
    fn point_lowering_uses_the_capability_shape_not_a_model_name_registry() {
        let knobs = model_knobs(
            &json!({"adapter_owned_default": 17}),
            &json!({}),
            "point",
            "point_forecast",
            1,
            1,
        )
        .unwrap();
        assert_eq!(
            knobs,
            BTreeMap::from([("adapter_owned_default".to_string(), json!(17))])
        );

        let error = model_knobs(
            &json!({"adapter_owned_default": 17}),
            &json!({"invented_density_knob": true}),
            "point",
            "point_forecast",
            1,
            1,
        )
        .unwrap_err();
        assert!(error.contains("requires the empty point.forecast"), "{error}");
    }

    #[test]
    fn authoring_type_keeps_action_state_explicit() {
        let draft = ExperimentDraftV2 {
            schema: fern_recipes::EXPERIMENT_DRAFT_SCHEMA_V2.to_string(),
            question: "fixture".to_string(),
            design_ref: None,
            budget: None,
            cost_estimate: None,
            inference: None,
            components: ExperimentComponentsSourceV2 {
                recipe: ComponentSourceV2::Ref("recipe:sha256:fixture".to_string()),
                architecture: ComponentSourceV2::Inline(ArchitectureComponentCardV2 {
                    schema: fern_recipes::ARCHITECTURE_COMPONENT_CARD_SCHEMA_V2.to_string(),
                    handler: "fixture".to_string(),
                    parameters: json!({}),
                }),
                action: ActionSourceV2::StateOnly,
                density: ComponentSourceV2::Inline(DensityComponentCardV2 {
                    schema: fern_recipes::DENSITY_COMPONENT_CARD_SCHEMA_V2.to_string(),
                    handler: "fixture".to_string(),
                    parameters: json!({}),
                }),
            },
        };
        assert!(matches!(draft.components.action, ActionSourceV2::StateOnly));
    }

    #[test]
    fn model_link_gradient_groups_are_exact_for_each_density() {
        assert_eq!(
            expected_model_link_parameter_groups("single_gaussian", None, false, None).unwrap(),
            ["fern_rotation", "fern_scale", "fern_shift"]
        );
        assert_eq!(
            expected_model_link_parameter_groups("bounded_mdn", None, false, None).unwrap(),
            ["component_parameters", "mixture_gate"]
        );
        assert_eq!(
            expected_model_link_parameter_groups(
                "single_gaussian",
                Some(1),
                false,
                Some(ActionVariantKind::CurrentTwoSite),
            )
            .unwrap(),
            [
                "fern_rotation",
                "fern_scale",
                "fern_shift",
                "future_action_response",
                "past_action_conditioner",
            ]
        );
        assert_eq!(
            expected_model_link_parameter_groups(
                "single_gaussian",
                Some(2),
                true,
                Some(ActionVariantKind::CurrentTwoSite),
            )
            .unwrap(),
            ["fern_rotation", "fern_scale", "fern_shift", "future_action_response"]
        );
        assert_eq!(
            expected_model_link_parameter_groups("sparse_moe", None, false, None).unwrap(),
            ["mixture_gate", "selected_expert_parameters"]
        );
        assert!(expected_model_link_parameter_groups("unregistered", None, false, None).is_err());
        assert_eq!(
            expected_model_link_parameter_groups(
                "single_gaussian",
                Some(1),
                false,
                Some(ActionVariantKind::ActionForecastModifiesState),
            )
            .unwrap(),
            [
                "action_forecast_head",
                "fern_rotation",
                "fern_scale",
                "fern_shift",
                "future_action_response",
            ]
        );
    }

    #[test]
    fn conditioned_v2_action_worlds_are_the_two_admitted_slices() {
        assert!(admit_conditioned_v2_action_world("controlled_pendulum", 1, false).is_ok());
        assert!(admit_conditioned_v2_action_world("level0a_controlled_lgssm", 2, true).is_ok());
        assert!(
            admit_conditioned_v2_action_world("level0a_controlled_lgssm", 2, false)
                .unwrap_err()
                .contains("future_only")
        );
        assert!(
            admit_conditioned_v2_action_world("controlled_pendulum", 2, false)
                .unwrap_err()
                .contains("exactly one action coordinate")
        );
        assert!(
            admit_conditioned_v2_action_world("lorenz", 2, true)
                .unwrap_err()
                .contains("no admitted action-conditioned world")
        );
    }
}
