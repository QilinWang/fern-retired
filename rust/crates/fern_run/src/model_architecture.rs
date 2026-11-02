//! Fern model-architecture identity.
//!
//! This is the Rust-owned mirror of `fr_tr.model_codepath_identity`.
//! It records architecture choices that are not fully expressed by plain
//! hyperparameter fields: active blocks, postprocess clamps, rotation path, and
//! known paper drift. The result is hashed as part of the run identity.

use serde_json::{Value, json};

use fern_experiment_types::{
    ArchitectureCapabilitiesV1, ArchitectureCardError, ArchitectureNativeOutputV1, ArchitecturePluginSchemaHash,
    ArchitectureTrainingCapabilityV1, CanonicalExternalArchitectureV1, DependencyLockHash, FernArchitectureVariantV1,
    FernDensityVariantV1, ImplementationCardError, ImplementationRuntimeV1, ModelArchitectureCardV1,
    ModelArchitecturePlugin, ModelImplementationCardV1, NumericalBackendV1, RawArchitectureChannelContractV1,
    RawArchitectureShapeV1, RawDLinearArchitectureV1, RawExternalArchitectureV1, RawFernArchitectureV1,
    RawFernFactorySchemasV1, RawFernOperatorV1, RawFernPatchV1, RawModelArchitectureCardV1,
    RawModelImplementationCardV1, RawNumericalPolicyV1, RawReversibleInstanceNormV1, SourceBundleHash,
};

use crate::resolved_cfg::{ExecutionKind, ResolvedConfigSpec};

pub const MODEL_ARCHITECTURE_SCHEMA: &str = "fern.model_architecture.current_code.v3";

const RESOLVED_MODEL_PLUGIN_SCHEMA: &str = "fern.resolved_model_architecture.v1";
const RESOLVED_MODEL_PLUGIN_SCHEMA_BYTES: &[u8] = br#"{
  "schema":"fern.resolved_model_architecture.v1",
  "model_type":"non-empty string",
  "execution_kind":"train|evaluation_only|pretrained_inference",
  "context_length":"positive integer",
  "horizon_length":"positive integer",
  "channel_independent":"boolean",
  "knobs":"canonical JSON object"
}"#;

/// Canonicalize one resolved Python/Torch model adapter without teaching the
/// lifecycle a Rust enum variant for every architecture.
///
/// The plugin ID includes the model type, while the versioned manifest keeps
/// every resolved architecture knob in canonical JSON. Adding a model through
/// the existing typed CLI catalogue therefore does not require editing the
/// lifecycle, artifact, evidence, or TUI crates. A future independently
/// packaged plugin can implement the same `ModelArchitecturePlugin` boundary.
struct ResolvedModelArchitecturePluginV1 {
    plugin_id: String,
    execution_kind: ExecutionKind,
}

impl ResolvedModelArchitecturePluginV1 {
    fn new(model_type: &str, execution_kind: ExecutionKind) -> Result<Self, ArchitectureCardError> {
        if model_type.is_empty()
            || !model_type
                .bytes()
                .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'_')
        {
            return Err(ArchitectureCardError {
                field: "model.type".to_string(),
                explanation: format!(
                    "resolved model type must use lowercase ASCII letters, digits, or underscores, found {model_type:?}"
                ),
            });
        }
        Ok(Self {
            plugin_id: format!("fern.resolved_model.{model_type}"),
            execution_kind,
        })
    }

    fn execution_kind_wire(&self) -> &'static str {
        match self.execution_kind {
            ExecutionKind::Train => "train",
            ExecutionKind::EvaluationOnly => "evaluation_only",
            ExecutionKind::PretrainedInference => "pretrained_inference",
        }
    }
}

impl ModelArchitecturePlugin for ResolvedModelArchitecturePluginV1 {
    fn plugin_id(&self) -> &str {
        &self.plugin_id
    }

    fn schema_hash(&self, schema_version: u32) -> Result<ArchitecturePluginSchemaHash, ArchitectureCardError> {
        if schema_version != 1 {
            return Err(ArchitectureCardError {
                field: "external.plugin_schema_version".to_string(),
                explanation: format!("resolved-model plugin supports schema version 1, found {schema_version}"),
            });
        }
        Ok(ArchitecturePluginSchemaHash::from_schema_bytes(
            RESOLVED_MODEL_PLUGIN_SCHEMA_BYTES,
        ))
    }

    fn canonicalize(
        &self,
        schema_version: u32,
        raw_config: &Value,
    ) -> Result<CanonicalExternalArchitectureV1, ArchitectureCardError> {
        let object = raw_config.as_object().ok_or_else(|| ArchitectureCardError {
            field: "external.config".to_string(),
            explanation: "resolved-model architecture config must be a JSON object".to_string(),
        })?;
        let expected_keys = [
            "channel_independent",
            "context_length",
            "execution_kind",
            "horizon_length",
            "knobs",
            "model_type",
            "schema",
        ];
        let observed_keys = object.keys().map(String::as_str).collect::<Vec<_>>();
        if observed_keys.len() != expected_keys.len()
            || expected_keys.iter().any(|expected| !observed_keys.contains(expected))
        {
            return Err(ArchitectureCardError {
                field: "external.config".to_string(),
                explanation: format!(
                    "resolved-model architecture fields must be exactly {expected_keys:?}, found {observed_keys:?}"
                ),
            });
        }
        let required_string = |field: &str| {
            object
                .get(field)
                .and_then(Value::as_str)
                .ok_or_else(|| ArchitectureCardError {
                    field: format!("external.config.{field}"),
                    explanation: "must be a string".to_string(),
                })
        };
        if required_string("schema")? != RESOLVED_MODEL_PLUGIN_SCHEMA {
            return Err(ArchitectureCardError {
                field: "external.config.schema".to_string(),
                explanation: format!("must be {RESOLVED_MODEL_PLUGIN_SCHEMA:?}"),
            });
        }
        let expected_model_type = self
            .plugin_id
            .strip_prefix("fern.resolved_model.")
            .expect("constructor owns plugin prefix");
        if required_string("model_type")? != expected_model_type {
            return Err(ArchitectureCardError {
                field: "external.config.model_type".to_string(),
                explanation: format!("must match plugin model type {expected_model_type:?}"),
            });
        }
        if required_string("execution_kind")? != self.execution_kind_wire() {
            return Err(ArchitectureCardError {
                field: "external.config.execution_kind".to_string(),
                explanation: format!("must match {:?}", self.execution_kind_wire()),
            });
        }
        for field in ["context_length", "horizon_length"] {
            if object.get(field).and_then(Value::as_u64).unwrap_or(0) == 0 {
                return Err(ArchitectureCardError {
                    field: format!("external.config.{field}"),
                    explanation: "must be a positive integer".to_string(),
                });
            }
        }
        let channel_independent = object
            .get("channel_independent")
            .and_then(Value::as_bool)
            .ok_or_else(|| ArchitectureCardError {
                field: "external.config.channel_independent".to_string(),
                explanation: "must be a boolean".to_string(),
            })?;
        if !object.get("knobs").is_some_and(Value::is_object) {
            return Err(ArchitectureCardError {
                field: "external.config.knobs".to_string(),
                explanation: "must be a JSON object".to_string(),
            });
        }
        let capabilities = match self.execution_kind {
            ExecutionKind::Train => ArchitectureCapabilitiesV1 {
                training: ArchitectureTrainingCapabilityV1::Trainable,
                native_output: ArchitectureNativeOutputV1::Point,
                has_native_point_head: true,
                supports_channel_independence: channel_independent,
            },
            ExecutionKind::EvaluationOnly => ArchitectureCapabilitiesV1 {
                training: ArchitectureTrainingCapabilityV1::FixedWeights,
                native_output: ArchitectureNativeOutputV1::Point,
                has_native_point_head: true,
                supports_channel_independence: channel_independent,
            },
            ExecutionKind::PretrainedInference => ArchitectureCapabilitiesV1 {
                training: ArchitectureTrainingCapabilityV1::FixedWeights,
                native_output: ArchitectureNativeOutputV1::Quantiles,
                has_native_point_head: false,
                supports_channel_independence: channel_independent,
            },
        };
        CanonicalExternalArchitectureV1::new(
            self.plugin_id(),
            schema_version,
            self.schema_hash(schema_version)?,
            raw_config,
            capabilities,
        )
    }
}

/// Convert the resolved run configuration into the first typed canonical
/// architecture card.
///
/// This is deliberately an adapter beside the existing current-code identity:
/// it proves that the new card describes real resolved knobs before any live
/// run hash or worker handoff is changed. Unsupported families return `None`;
/// they must gain a typed built-in card or a registered plugin rather than be
/// guessed from a model name.
pub fn model_architecture_card(
    config: &ResolvedConfigSpec,
) -> Result<Option<ModelArchitectureCardV1>, ArchitectureCardError> {
    let shape = || RawArchitectureShapeV1 {
        context_length: config.task.seq_len,
        horizon_length: config.task.pred_len,
        channel_contract: RawArchitectureChannelContractV1::RuntimePositive,
    };

    let raw = match config.model.model_type.as_str() {
        "fern" => {
            require_shape_layout(config.model.knobs.get("shape_contract"), "b s h d")?;
            require_empty_object(config.model.knobs.get("factory_schemas"), "model.knobs.factory_schemas")?;
            let (density_variant, mixture_component_count) = fern_density_from_knobs(&config.model.knobs)?;
            RawModelArchitectureCardV1::Fern(RawFernArchitectureV1 {
                schema_version: 1,
                architecture_variant: FernArchitectureVariantV1::parse(fern_architecture_variant_wire(
                    &config.model.knobs,
                )?)?,
                shape: shape(),
                hidden_width: required_knob_u64(&config.model.knobs, "dim_hidden")?,
                augmented_width: required_knob_u64(&config.model.knobs, "dim_augment")?,
                block_size: required_knob_u64(&config.model.knobs, "block_size")?,
                density_variant,
                mixture_component_count,
                sparse_active_component_count: optional_knob_u64(&config.model.knobs, "sparse_active_component_count")?,
                mixture_relative_std_floor_ppm: optional_relative_floor_ppm(
                    &config.model.knobs,
                    "mixture_relative_std_floor",
                )?,
                operator: RawFernOperatorV1::Householder,
                householder_reflection_count: required_knob_u64(&config.model.knobs, "num_reflects")?,
                coupling_patch: required_patch(&config.model.knobs, "Coupling_patch_spec")?,
                brenier_patch: required_patch(&config.model.knobs, "Brenier_patch_spec")?,
                factory_schemas: RawFernFactorySchemasV1::Default,
                reversible_instance_norm: RawReversibleInstanceNormV1 {
                    enabled: required_knob_bool(&config.model.knobs, "revin")?,
                    affine: required_knob_bool(&config.model.knobs, "revin_affine")?,
                    subtract_last: required_knob_bool(&config.model.knobs, "revin_subtract_last")?,
                },
            })
        }
        "dlinear" => {
            require_shape_layout(config.model.knobs.get("shape_contract"), "b h d")?;
            let individual = required_knob_bool(&config.model.knobs, "individual")?;
            let channel_contract = if individual {
                RawArchitectureChannelContractV1::Exact {
                    count: config.task.channels.ok_or_else(|| {
                        architecture_error(
                            "task.channels",
                            "individual DLinear requires an exact channel count before architecture registration",
                        )
                    })?,
                }
            } else {
                RawArchitectureChannelContractV1::RuntimePositive
            };
            RawModelArchitectureCardV1::DLinear(RawDLinearArchitectureV1 {
                schema_version: 1,
                shape: RawArchitectureShapeV1 {
                    channel_contract,
                    ..shape()
                },
                moving_average_window: required_knob_u64(&config.model.knobs, "moving_avg")?,
                individual_per_channel: individual,
                stride: required_knob_u64(&config.model.knobs, "stride")?,
            })
        }
        _ => {
            let plugin = ResolvedModelArchitecturePluginV1::new(&config.model.model_type, config.execution.kind)?;
            let raw_config = json!({
                "schema": RESOLVED_MODEL_PLUGIN_SCHEMA,
                "model_type": config.model.model_type,
                "execution_kind": plugin.execution_kind_wire(),
                "context_length": config.task.seq_len,
                "horizon_length": config.task.pred_len,
                "channel_independent": config.task.channel_independent,
                "knobs": config.model.knobs,
            });
            return ModelArchitectureCardV1::canonicalize_external(
                RawExternalArchitectureV1 {
                    plugin_id: plugin.plugin_id().to_string(),
                    plugin_schema_version: 1,
                    plugin_schema_hash: plugin.schema_hash(1)?,
                    config: raw_config,
                },
                &plugin,
            )
            .map(Some);
        }
    };

    ModelArchitectureCardV1::canonicalize_builtin(raw).map(Some)
}

/// Reviewed non-config inputs needed to identify one executable model build.
///
/// Source and lock digests come from canonical manifests, while the numerical
/// policy comes from the declared worker policy. Observed host/GPU/Torch facts
/// are deliberately absent because they belong to the Attempt receipt.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModelImplementationInputsV1 {
    pub source_bundle_hash: SourceBundleHash,
    pub dependency_lock_hash: DependencyLockHash,
    pub worker_protocol_version: u32,
    pub numerical_policy: RawNumericalPolicyV1,
    pub active_feature_flags: Vec<String>,
}

/// Message text unchanged from the hand-written `Display`.
#[derive(Debug, thiserror::Error)]
pub enum ResolvedModelImplementationError {
    #[error("cannot resolve model architecture: {0}")]
    Architecture(#[from] ArchitectureCardError),
    #[error("cannot resolve model implementation: {0}")]
    Implementation(#[from] ImplementationCardError),
}

/// Resolve the implementation paired with the typed architecture adapter.
///
/// Unsupported model families return `None` instead of receiving a guessed
/// generic implementation. Fern's declared Householder backend moves here,
/// not into Architecture identity.
pub fn model_implementation_card(
    config: &ResolvedConfigSpec,
    inputs: ModelImplementationInputsV1,
) -> Result<Option<ModelImplementationCardV1>, ResolvedModelImplementationError> {
    let Some(architecture) = model_architecture_card(config)? else {
        return Ok(None);
    };
    let numerical_backend = match config.model.model_type.as_str() {
        "fern" => match required_knob_str(&config.model.knobs, "householder_backend")? {
            "torch" => NumericalBackendV1::TorchEager,
            "triton" => NumericalBackendV1::TritonHouseholder,
            other => {
                return Err(ArchitectureCardError {
                    field: "model.knobs.householder_backend".to_string(),
                    explanation: format!("must be \"torch\" or \"triton\", found {other:?}"),
                }
                .into());
            }
        },
        "dlinear" => NumericalBackendV1::TorchEager,
        // External resolved-model plugins in V1 all execute through the
        // reviewed Python/Torch worker. A plugin needing another arithmetic
        // backend must add a typed backend instead of hiding it in JSON.
        _ => NumericalBackendV1::TorchEager,
    };

    ModelImplementationCardV1::canonicalize(RawModelImplementationCardV1 {
        schema_version: 1,
        architecture_hash: architecture.architecture_hash()?,
        source_bundle_hash: inputs.source_bundle_hash,
        dependency_lock_hash: inputs.dependency_lock_hash,
        runtime: ImplementationRuntimeV1::PythonTorch,
        numerical_backend,
        worker_protocol_version: inputs.worker_protocol_version,
        numerical_policy: inputs.numerical_policy,
        active_feature_flags: inputs.active_feature_flags,
    })
    .map(Some)
    .map_err(Into::into)
}

pub fn model_architecture_identity(config: &ResolvedConfigSpec) -> Value {
    if config.model.model_type != "fern" {
        return Value::Null;
    }

    let knobs = &config.model.knobs;
    let num_reflects = knob_u64(knobs.get("num_reflects"));
    let block_size = knob_u64(knobs.get("block_size"));
    let householder_backend = knobs
        .get("householder_backend")
        .and_then(Value::as_str)
        .unwrap_or("torch");
    let triton_active = householder_backend == "triton";
    let fern_architecture = knobs.get("fern_architecture").and_then(Value::as_str);
    let declared_fern_density = knobs.get("fern_density").and_then(Value::as_str);
    let fern_density = declared_fern_density.unwrap_or("single_gaussian");
    let fern_radial_head = knobs.get("fern_radial_head").and_then(Value::as_bool);
    let architecture_factors = fern_architecture_factors_from_knobs(knobs);
    let encoder_scale_structure = if architecture_factors.interleaved_encoder {
        json!({
            "v0": "complex",
            "v1": "diagonal",
            "v2": "complex",
            "v3": "diagonal",
            "v4": "complex",
        })
    } else {
        json!({
            "v0": "complex",
            "v1": "complex",
            "v2": "complex",
            "v3": "complex",
            "v4": "complex",
        })
    };
    let free_scale_postprocess = if architecture_factors.clamped_free_scale {
        json!({
            "factory": "make_scale_pp",
            "active": "PPSoftclipLeaky",
            "active_clamped": true,
            "bounds": [-4.5, 4.5],
            "leak": 0.1,
            "beta": 0.8,
            "paper_bounds": [-4.5, 4.5],
        })
    } else {
        json!({
            "factory": "make_scale_pp",
            "active": "PPIdentity",
            "active_clamped": false,
            "declared_bounds": {
                "diagonal": [-4.5, 4.5],
                "complex": [-4.5, 4.5],
                "tri_sym": [-5.0, 5.0],
                "tri_anti": [-5.0, 5.0],
            },
            "paper_bounds": [-4.5, 4.5],
        })
    };
    // The ceiling on the positive Brenier coefficient is the ceiling on how
    // much uncertainty Fern can state: the emitted standard deviation is
    // `source_std * (1 + c)`, so `high` fixes the widest interval the model can
    // ever produce. Measured 2026-08-17 on the linear state-space world, the
    // scale head sits at exactly this ceiling in every cell at every noise
    // dose, so the value is load-bearing rather than a safety rail.
    //
    // `brenier_scale_ceiling` relaxes it per recipe. It stays a knob rather
    // than a new constant because the ceiling enters this architecture card and
    // therefore the architecture hash: two ceilings are two models, and must
    // not share one identity (the same rule that separates `current` from
    // `icml_pinned`). Absent, the architecture's own default applies and every
    // existing recipe keeps the identity it already has.
    let positive_brenier_high = knobs.get("brenier_scale_ceiling").and_then(Value::as_f64).unwrap_or(
        if architecture_factors.paper_brenier_bound {
            4.5
        } else {
            8.5
        },
    );
    let effective_brenier_high = 1.0 + positive_brenier_high;
    let paper_drift = if fern_architecture == Some("icml_pinned") {
        json!({
            "householder": "active real Householder matches the paper operator",
            "free_scale_clamp": "paper bounds restored with reconstructed leak=0.1 beta=0.8 constants",
            "shift_clamp": "matches paper bounds; leak=0.1 beta=0.8 constants remain reconstructed",
            "positive_brenier_scale": "paper effective upper bound 5.5 restored",
            "encoder_layout": "historical interleaved v0/v2/v4 complex and v1/v3 diagonal layout restored",
            "hidden_mlp": "current halved hidden schema remains; exact submitted depth is unrecovered",
        })
    } else if fern_architecture == Some("current") {
        json!({
            "householder": "active real Householder matches the paper operator",
            "free_scale_clamp": "current free scale PPIdentity differs from paper soft clamp",
            "shift_clamp": "matches paper soft clamp bounds in the active fr_core path",
            "positive_brenier_scale": "experimental widened upper bound; residual 1+c and leak=0.0 preserve positivity",
            "encoder_layout": "current all-complex encoder is a post-paper ablation setting",
            "hidden_mlp": "current halved hidden schema differs from submitted-paper prose",
        })
    } else {
        json!({
            "householder": "active real Householder matches the paper operator",
            "free_scale_clamp": if architecture_factors.clamped_free_scale {
                "paper soft-clamp factor enabled"
            } else {
                "unclamped current-code factor enabled"
            },
            "shift_clamp": "matches paper soft clamp bounds in the active fr_core path",
            "positive_brenier_scale": if architecture_factors.paper_brenier_bound {
                "paper effective upper-bound factor enabled"
            } else {
                "widened current-code upper-bound factor enabled"
            },
            "encoder_layout": if architecture_factors.interleaved_encoder {
                "historical interleaved encoder factor enabled"
            } else {
                "all-complex current-code encoder factor enabled"
            },
            "hidden_mlp": "current halved hidden schema held fixed across the factorial ablation",
        })
    };

    let mut identity = json!({
        "schema": MODEL_ARCHITECTURE_SCHEMA,
        "model_type": "fern",
        "config_knobs": {
            "fern_architecture": fern_architecture,
            "encoder_layout": knobs.get("encoder_layout").cloned().unwrap_or(Value::Null),
            "free_scale_processing": knobs.get("free_scale_processing").cloned().unwrap_or(Value::Null),
            "fern_density": fern_density,
            "seq_len": config.task.seq_len,
            "pred_len": config.task.pred_len,
            "shape_contract": knobs.get("shape_contract").cloned().unwrap_or(Value::Null),
            "dim_augment": knob_u64(knobs.get("dim_augment")),
            "dim_hidden": knob_u64(knobs.get("dim_hidden")),
            "num_reflects": num_reflects,
            "block_size": block_size,
            "householder_backend": householder_backend,
            "brenier_patch_spec": knobs.get("Brenier_patch_spec").cloned().unwrap_or(Value::Null),
            "coupling_patch_spec": knobs.get("Coupling_patch_spec").cloned().unwrap_or(Value::Null),
            "revin": knob_bool(knobs.get("revin")),
            "revin_affine": knob_bool(knobs.get("revin_affine")),
            "revin_subtract_last": knob_bool(knobs.get("revin_subtract_last")),
            "w_eig": knobs.get("w_eig").cloned().unwrap_or(Value::Null),
            "eig_mode": knobs.get("eig_mode").cloned().unwrap_or(Value::Null),
            "num_train_samples": knob_u64(knobs.get("num_train_samples")),
            "num_eval_samples": knob_u64(knobs.get("num_eval_samples")),
            "mixture_component_count": knob_u64(knobs.get("mixture_component_count")),
            "sparse_active_component_count": knobs
                .get("sparse_active_component_count")
                .cloned()
                .unwrap_or(Value::Null),
            "mixture_relative_std_floor": knobs
                .get("mixture_relative_std_floor")
                .cloned()
                .unwrap_or(Value::Null),
        },
        "active_modules": {
            "model": "fr.Fern",
            "core": "fr_core",
            "stale_duplicate": "fr_gen.py",
            "stale_duplicate_active": false,
        },
        "source_distribution": {
            "density_variant": fern_density,
            "z_kind": "gauss",
            "y_kind": "gauss",
            "source_std": knobs.get("source_std").and_then(Value::as_f64).unwrap_or(0.1),
        },
        "layout": {
            "input": "b s d",
            "internal": "b d 1 l",
            "sampled_prediction": "b s h d",
        },
        "encoder": {
            "constructed": true,
            "active_in_forward": true,
            "rounds": 5,
            "versions": ["v0", "v1", "v2", "v3", "v4"],
            "pattern": "x_to_z_then_z_to_x",
            "z_to_x_scale_structure_by_block_id": encoder_scale_structure,
        },
        "process": {
            "constructed": true,
            "active_in_forward": false,
            "versions": ["v11", "v12", "v13", "v14"],
            "pattern": "y_to_z_then_z_to_y",
        },
        "decoder": {
            "constructed": true,
            "active_in_forward": true,
            "src": "z",
            "dst": "y",
            "version": "v100",
            "block_type": "brenier",
            "scale_structure": "diagonal_pos",
            "eigen_sparse_spec": null,
        },
        "hidden_mlp": {
            "schema": "HiddenSchema._default_architecture",
            "blocks": [
                {"pre": "linear", "act": "identity", "norm": "none", "dropout": 0.0},
                {"pre": "linear", "act": "elu", "norm": "none", "dropout": 0.0},
                {"pre": "linear", "act": "elu", "norm": "none", "dropout": 0.0},
                {"pre": "linear", "act": "logsigmoid", "norm": "none", "dropout": 0.0},
                {"pre": "linear", "act": "elu", "norm": "none", "dropout": 0.0},
            ],
        },
        "coefficient_heads": {
            "scale_head": ["linear", "elu", "linear", "elu"],
            "shift_head": ["linear", "elu", "linear", "elu", "post_linear"],
            "rotation_head": ["linear_bias_false", "relu"],
        },
        "postprocess": {
            "free_scale": free_scale_postprocess,
            "positive_brenier_scale": {
                "factory": "make_scale_pos_pp",
                "active": "PPSoftclipLeaky",
                "coefficient_bounds": [-1.0, positive_brenier_high],
                "leak": 0.0,
                "beta": 0.8,
                "residual_offset": 1.0,
                "effective_scale_bounds": [0.0, effective_brenier_high],
                "effective_allows_negative": false,
                "load_bearing": "leak=0.0 keeps coefficient above -1 so 1+c stays positive",
            },
            "shift": {
                "factory": "make_shift_pp",
                "active": "PPSoftclipLeaky",
                "active_clamped": true,
                "bounds": [-15.0, 15.0],
                "leak": 0.1,
                "beta": 0.8,
                "paper_bounds": [-15.0, 15.0],
            },
        },
        "rotation": {
            "active_kind": "real_householder",
            "active_function": "Rotation.apply_reflect",
            "implementation_backend": householder_backend,
            "paper_kind": "real_householder",
            "real_reference_function": "Rotation.apply_reflect",
            "triton_kernel_present": true,
            "triton_import_gate": "_HAS_TRITON_HOUSEHOLDER",
            "triton_active_in_model": triton_active,
            "triton_called_by_rotation": triton_active,
            "triton_math": "real_householder",
            "num_reflects": num_reflects,
            "block_size": block_size,
            "complex_reference_function": "Rotation.apply_reflect_complex",
            "complex_reference_active": false,
        },
        "paper_drift": paper_drift,
    });
    if declared_fern_density.is_none() {
        let config_knobs = identity["config_knobs"]
            .as_object_mut()
            .expect("model architecture config_knobs is an object");
        config_knobs.remove("fern_density");
        config_knobs.remove("mixture_component_count");
        config_knobs.remove("sparse_active_component_count");
        config_knobs.remove("mixture_relative_std_floor");
        config_knobs.insert(
            "num_moe".to_string(),
            knobs.get("num_moe").cloned().unwrap_or(Value::Null),
        );
        identity["source_distribution"]
            .as_object_mut()
            .expect("model architecture source_distribution is an object")
            .remove("density_variant");
    } else {
        let emitted_law = if fern_density == "single_gaussian" {
            "single_gaussian"
        } else {
            "gaussian_mixture"
        };
        let distribution = identity["source_distribution"]
            .as_object_mut()
            .expect("model architecture source_distribution is an object");
        distribution.insert("emitted_law".to_string(), json!(emitted_law));
        distribution.insert("y_kind".to_string(), json!(emitted_law));
    }
    if let Some(active) = fern_radial_head {
        identity["config_knobs"]
            .as_object_mut()
            .expect("model architecture config_knobs is an object")
            .insert("fern_radial_head".to_string(), json!(active));
        identity["coefficient_heads"]
            .as_object_mut()
            .expect("model architecture coefficient_heads is an object")
            .insert(
                "radial_head".to_string(),
                json!({
                    "active": active,
                    "effect": "positive scalar rescaling of the existing elliptical law",
                    "changes_predictive_family": false,
                }),
            );
    }
    identity
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct FernArchitectureFactors {
    interleaved_encoder: bool,
    clamped_free_scale: bool,
    paper_brenier_bound: bool,
}

fn fern_architecture_factors(variant: &str) -> FernArchitectureFactors {
    let factors = match variant {
        "current" => (false, false, false),
        "icml_pinned" => (true, true, true),
        "interleaved_unclamped_wide" => (true, false, false),
        "all_complex_clamped_wide" => (false, true, false),
        "all_complex_unclamped_paper_brenier" => (false, false, true),
        "interleaved_clamped_wide" => (true, true, false),
        "interleaved_unclamped_paper_brenier" => (true, false, true),
        "all_complex_clamped_paper_brenier" => (false, true, true),
        // Same three factors as `current`; the variant differs only in the
        // Brenier scale ceiling, which arrives as its own knob.
        "current_wide_scale" => (false, false, false),
        _ => panic!("validated Fern architecture variant became unknown: {variant:?}"),
    };
    FernArchitectureFactors {
        interleaved_encoder: factors.0,
        clamped_free_scale: factors.1,
        paper_brenier_bound: factors.2,
    }
}

fn fern_architecture_factors_from_knobs(knobs: &std::collections::BTreeMap<String, Value>) -> FernArchitectureFactors {
    match (
        knobs.get("encoder_layout").and_then(Value::as_str),
        knobs.get("free_scale_processing").and_then(Value::as_str),
    ) {
        (Some(encoder_layout), Some(free_scale_processing)) => FernArchitectureFactors {
            interleaved_encoder: encoder_layout == "interleaved",
            clamped_free_scale: free_scale_processing == "soft_clamped",
            paper_brenier_bound: knobs
                .get("brenier_scale_ceiling")
                .and_then(Value::as_f64)
                .is_some_and(|ceiling| ceiling == 4.5),
        },
        _ => fern_architecture_factors(
            knobs
                .get("fern_architecture")
                .and_then(Value::as_str)
                .unwrap_or("current"),
        ),
    }
}

/// Project V2's three explicit factors into the historical typed card enum.
/// The config remains the source of truth; this mapping exists only so old and
/// new architecture cards share one checked projection during migration.
fn fern_architecture_variant_wire(
    knobs: &std::collections::BTreeMap<String, Value>,
) -> Result<&str, ArchitectureCardError> {
    if let Some(legacy) = knobs.get("fern_architecture") {
        return legacy
            .as_str()
            .ok_or_else(|| architecture_error("model.knobs.fern_architecture", "must be a string"));
    }
    let encoder = required_knob_str(knobs, "encoder_layout")?;
    let free_scale = required_knob_str(knobs, "free_scale_processing")?;
    let ceiling = knobs
        .get("brenier_scale_ceiling")
        .and_then(Value::as_f64)
        .ok_or_else(|| architecture_error("model.knobs.brenier_scale_ceiling", "must be a finite number"))?;
    let paper_bound = if ceiling == 4.5 {
        true
    } else if ceiling == 8.5 {
        false
    } else {
        return Err(architecture_error(
            "model.knobs.brenier_scale_ceiling",
            "explicit factorial cells require coefficient ceiling 4.5 or 8.5",
        ));
    };
    match (encoder, free_scale, paper_bound) {
        ("all_complex", "identity", false) => Ok("current"),
        ("interleaved", "soft_clamped", true) => Ok("icml_pinned"),
        ("interleaved", "identity", false) => Ok("interleaved_unclamped_wide"),
        ("all_complex", "soft_clamped", false) => Ok("all_complex_clamped_wide"),
        ("all_complex", "identity", true) => Ok("all_complex_unclamped_paper_brenier"),
        ("interleaved", "soft_clamped", false) => Ok("interleaved_clamped_wide"),
        ("interleaved", "identity", true) => Ok("interleaved_unclamped_paper_brenier"),
        ("all_complex", "soft_clamped", true) => Ok("all_complex_clamped_paper_brenier"),
        _ => Err(architecture_error(
            "model.knobs architecture factors",
            "explicit encoder_layout, free_scale_processing, and brenier_scale_ceiling do not name one registered factorial cell",
        )),
    }
}

fn architecture_error(field: impl Into<String>, explanation: impl Into<String>) -> ArchitectureCardError {
    ArchitectureCardError {
        field: field.into(),
        explanation: explanation.into(),
    }
}

fn required_knob_u64(
    knobs: &std::collections::BTreeMap<String, Value>,
    name: &str,
) -> Result<u64, ArchitectureCardError> {
    knobs
        .get(name)
        .and_then(Value::as_u64)
        .ok_or_else(|| architecture_error(format!("model.knobs.{name}"), "must be an unsigned integer"))
}

fn optional_knob_u64(
    knobs: &std::collections::BTreeMap<String, Value>,
    name: &str,
) -> Result<Option<u64>, ArchitectureCardError> {
    match knobs.get(name) {
        None => Ok(None),
        Some(value) => value
            .as_u64()
            .map(Some)
            .ok_or_else(|| architecture_error(format!("model.knobs.{name}"), "must be an unsigned integer")),
    }
}

fn fern_density_from_knobs(
    knobs: &std::collections::BTreeMap<String, Value>,
) -> Result<(FernDensityVariantV1, u64), ArchitectureCardError> {
    match knobs.get("fern_density") {
        Some(raw_density) => {
            if knobs.contains_key("num_moe") {
                return Err(architecture_error(
                    "model.knobs.num_moe",
                    "must be absent when fern_density is declared",
                ));
            }
            let density = raw_density
                .as_str()
                .ok_or_else(|| architecture_error("model.knobs.fern_density", "must be a string"))?;
            Ok((
                FernDensityVariantV1::parse(density)?,
                required_knob_u64(knobs, "mixture_component_count")?,
            ))
        }
        None => {
            if required_knob_u64(knobs, "num_moe")? != 1 {
                return Err(architecture_error(
                    "model.knobs.num_moe",
                    "single-Gaussian Fern requires the historical value 1",
                ));
            }
            if knobs.contains_key("mixture_component_count") {
                return Err(architecture_error(
                    "model.knobs.mixture_component_count",
                    "requires fern_density",
                ));
            }
            Ok((FernDensityVariantV1::SingleGaussian, 1))
        }
    }
}

fn optional_relative_floor_ppm(
    knobs: &std::collections::BTreeMap<String, Value>,
    name: &str,
) -> Result<Option<u64>, ArchitectureCardError> {
    let Some(value) = knobs.get(name) else {
        return Ok(None);
    };
    let floor = value
        .as_f64()
        .ok_or_else(|| architecture_error(format!("model.knobs.{name}"), "must be a number"))?;
    let ppm = floor * 1_000_000.0;
    if !ppm.is_finite() || (ppm - ppm.round()).abs() > 1e-6 {
        return Err(architecture_error(
            format!("model.knobs.{name}"),
            "must be exactly representable in parts per million",
        ));
    }
    Ok(Some(ppm.round() as u64))
}

fn required_knob_bool(
    knobs: &std::collections::BTreeMap<String, Value>,
    name: &str,
) -> Result<bool, ArchitectureCardError> {
    knobs
        .get(name)
        .and_then(Value::as_bool)
        .ok_or_else(|| architecture_error(format!("model.knobs.{name}"), "must be a boolean"))
}

fn required_knob_str<'a>(
    knobs: &'a std::collections::BTreeMap<String, Value>,
    name: &str,
) -> Result<&'a str, ArchitectureCardError> {
    knobs
        .get(name)
        .and_then(Value::as_str)
        .ok_or_else(|| architecture_error(format!("model.knobs.{name}"), "must be a string"))
}

fn required_patch(
    knobs: &std::collections::BTreeMap<String, Value>,
    name: &str,
) -> Result<RawFernPatchV1, ArchitectureCardError> {
    let object = knobs
        .get(name)
        .and_then(Value::as_object)
        .ok_or_else(|| architecture_error(format!("model.knobs.{name}"), "must be an object"))?;
    let field = |key: &str| {
        object
            .get(key)
            .and_then(Value::as_u64)
            .ok_or_else(|| architecture_error(format!("model.knobs.{name}.{key}"), "must be an unsigned integer"))
    };
    Ok(RawFernPatchV1 {
        length: field("L")?,
        patch_length: field("P")?,
        group_count: field("G")?,
    })
}

fn require_empty_object(value: Option<&Value>, field: &str) -> Result<(), ArchitectureCardError> {
    if value.and_then(Value::as_object).is_some_and(serde_json::Map::is_empty) {
        Ok(())
    } else {
        Err(architecture_error(
            field,
            "must be the registered empty/default factory schema",
        ))
    }
}

fn require_shape_layout(value: Option<&Value>, expected_prediction: &str) -> Result<(), ArchitectureCardError> {
    let prediction = value
        .and_then(|contract| contract.pointer("/pred_card/layout"))
        .and_then(Value::as_str);
    let target = value
        .and_then(|contract| contract.pointer("/target_card/layout"))
        .and_then(Value::as_str);
    if prediction == Some(expected_prediction) && target == Some("b h d") {
        Ok(())
    } else {
        Err(architecture_error(
            "model.knobs.shape_contract",
            format!("must declare pred={expected_prediction:?} and target=\"b h d\" before architecture registration"),
        ))
    }
}

fn knob_u64(value: Option<&Value>) -> Value {
    value.and_then(Value::as_u64).map_or(Value::Null, |value| json!(value))
}

fn knob_bool(value: Option<&Value>) -> Value {
    value.and_then(Value::as_bool).map_or(Value::Null, |value| json!(value))
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeMap;

    use serde_json::json;

    use super::*;
    use crate::resolved_cfg::{
        ActionConfig, AmpDTypeKind, ArtifactConfig, ArtifactPolicy, CompileConfig, DTypeKind, DataConfig, DeviceKind,
        ExecutionConfig, ExecutionKind, HuberDeltaConfig, LossConfig, ModelConfig, NoiseKind, NormalizationKind,
        OptimConfig, OptimizerKind, ProbabilityConfig, RESOLVED_CONFIG_SCHEMA, RuntimeConfig, SchedulerConfig,
        SchedulerKind, SmoothPolicyConfig, SmoothingKind, SmoothingSpecConfig, TaskConfig, TrainConfig,
        TrainingTargetConfig,
    };

    #[test]
    fn non_fern_model_has_null_architecture_identity() {
        let mut config = sample_config();
        config.model.model_type = "dlinear".to_string();

        assert_eq!(model_architecture_identity(&config), Value::Null);
    }

    #[test]
    fn fern_architecture_identity_carries_config_and_static_codepath() {
        let config = sample_config();
        let identity = model_architecture_identity(&config);

        assert_eq!(identity["schema"], MODEL_ARCHITECTURE_SCHEMA);
        assert_eq!(identity["model_type"], "fern");
        assert_eq!(identity["config_knobs"]["seq_len"], 336);
        assert_eq!(identity["config_knobs"]["pred_len"], 64);
        assert_eq!(identity["config_knobs"]["num_reflects"], 24);
        assert_eq!(identity["config_knobs"]["brenier_patch_spec"]["P"], 32);
        assert_eq!(
            identity["encoder"]["z_to_x_scale_structure_by_block_id"]["v4"],
            "complex"
        );
        assert_eq!(
            identity["postprocess"]["positive_brenier_scale"]["effective_scale_bounds"][1],
            9.5
        );
        assert_eq!(identity["rotation"]["active_kind"], "real_householder");
        assert_eq!(identity["rotation"]["implementation_backend"], "torch");
    }

    #[test]
    fn typed_fern_card_uses_resolved_knobs_and_keeps_backend_out_of_architecture() {
        let config = sample_config();
        let card = model_architecture_card(&config).unwrap().unwrap();
        let current_hash = card.architecture_hash().unwrap();

        let mut another_backend = config.clone();
        another_backend
            .model
            .knobs
            .insert("householder_backend".to_string(), json!("triton"));
        assert_eq!(
            model_architecture_card(&another_backend)
                .unwrap()
                .unwrap()
                .architecture_hash()
                .unwrap(),
            current_hash,
            "backend belongs to implementation identity, not architecture"
        );

        let mut another_evaluation_budget = config.clone();
        another_evaluation_budget
            .model
            .knobs
            .insert("num_eval_samples".to_string(), json!(512));
        assert_eq!(
            model_architecture_card(&another_evaluation_budget)
                .unwrap()
                .unwrap()
                .architecture_hash()
                .unwrap(),
            current_hash,
            "sample count belongs to the resolved evaluation/output contract, not architecture"
        );

        let mut pinned = config;
        pinned
            .model
            .knobs
            .insert("fern_architecture".to_string(), json!("icml_pinned"));
        assert_ne!(
            model_architecture_card(&pinned)
                .unwrap()
                .unwrap()
                .architecture_hash()
                .unwrap(),
            current_hash,
            "the registered Fern structural variant must change architecture identity"
        );
    }

    #[test]
    fn typed_implementation_card_moves_backend_out_of_architecture_and_tracks_it() {
        let config = sample_config();
        let torch = model_implementation_card(&config, implementation_inputs())
            .unwrap()
            .unwrap();
        assert_eq!(torch.numerical_backend(), NumericalBackendV1::TorchEager);

        let mut triton_config = config.clone();
        triton_config
            .model
            .knobs
            .insert("householder_backend".to_string(), json!("triton"));
        let triton = model_implementation_card(&triton_config, implementation_inputs())
            .unwrap()
            .unwrap();
        assert_eq!(triton.numerical_backend(), NumericalBackendV1::TritonHouseholder);
        assert_eq!(
            model_architecture_card(&config)
                .unwrap()
                .unwrap()
                .architecture_hash()
                .unwrap(),
            model_architecture_card(&triton_config)
                .unwrap()
                .unwrap()
                .architecture_hash()
                .unwrap()
        );
        assert_ne!(
            torch.implementation_hash().unwrap(),
            triton.implementation_hash().unwrap(),
            "the same architecture through two arithmetic paths must not pool"
        );

        let mut invalid = config;
        invalid
            .model
            .knobs
            .insert("householder_backend".to_string(), json!("automatic"));
        assert!(
            model_implementation_card(&invalid, implementation_inputs())
                .unwrap_err()
                .to_string()
                .contains("must be \"torch\" or \"triton\"")
        );
    }

    #[test]
    fn resolved_model_plugin_covers_trainable_architectures_without_a_lifecycle_variant() {
        let mut config = sample_config();
        config.model.model_type = "timemixer".to_string();
        config.model.knobs = BTreeMap::from([
            (
                "shape_contract".to_string(),
                json!({
                    "pred_card": {"layout": "b h d"},
                    "target_card": {"layout": "b h d"},
                }),
            ),
            ("d_model".to_string(), json!(16)),
            ("e_layers".to_string(), json!(2)),
        ]);

        let card = model_architecture_card(&config).unwrap().unwrap();
        assert_eq!(card.family(), "external");
        assert_eq!(
            card.capabilities().training,
            ArchitectureTrainingCapabilityV1::Trainable
        );
        assert_eq!(card.capabilities().native_output, ArchitectureNativeOutputV1::Point);
        let first_hash = card.architecture_hash().unwrap();

        config.model.knobs.insert("d_model".to_string(), json!(32));
        assert_ne!(
            model_architecture_card(&config)
                .unwrap()
                .unwrap()
                .architecture_hash()
                .unwrap(),
            first_hash,
            "a resolved architecture knob must change external model identity"
        );
    }

    #[test]
    fn resolved_model_plugin_distinguishes_fixed_quantile_models_and_implementation() {
        let mut config = sample_config();
        config.execution.kind = ExecutionKind::PretrainedInference;
        config.model.model_type = "chronos2".to_string();
        config.model.knobs = BTreeMap::from([
            (
                "shape_contract".to_string(),
                json!({
                    "pred_card": {"layout": "b h d"},
                    "target_card": {"layout": "b h d"},
                }),
            ),
            ("hf_model_id".to_string(), json!("amazon/chronos-2")),
            ("hf_revision".to_string(), json!("revision-a")),
        ]);

        let architecture = model_architecture_card(&config).unwrap().unwrap();
        assert_eq!(
            architecture.capabilities().training,
            ArchitectureTrainingCapabilityV1::FixedWeights
        );
        assert_eq!(
            architecture.capabilities().native_output,
            ArchitectureNativeOutputV1::Quantiles
        );
        assert!(!architecture.capabilities().has_native_point_head);
        let implementation = model_implementation_card(&config, implementation_inputs())
            .unwrap()
            .unwrap();
        assert_eq!(implementation.numerical_backend(), NumericalBackendV1::TorchEager);

        let chronos_hash = architecture.architecture_hash().unwrap();
        config.model.model_type = "tirex".to_string();
        assert_ne!(
            model_architecture_card(&config)
                .unwrap()
                .unwrap()
                .architecture_hash()
                .unwrap(),
            chronos_hash,
            "two external model plugins cannot pool merely because their knobs happen to match"
        );
    }

    #[test]
    fn typed_dlinear_card_maps_real_knob_names_and_channel_semantics() {
        let mut config = sample_config();
        config.model.model_type = "dlinear".to_string();
        config.model.knobs = BTreeMap::from([
            (
                "shape_contract".to_string(),
                json!({
                    "pred_card": {"layout": "b h d"},
                    "target_card": {"layout": "b h d"},
                }),
            ),
            ("individual".to_string(), json!(false)),
            ("moving_avg".to_string(), json!(25)),
            ("stride".to_string(), json!(1)),
        ]);

        let card = model_architecture_card(&config).unwrap().unwrap();
        assert_eq!(card.family(), "dlinear");
        assert_eq!(
            card.capabilities().native_output,
            fern_experiment_types::ArchitectureNativeOutputV1::Point
        );

        config.model.knobs.insert("individual".to_string(), json!(true));
        assert!(
            model_architecture_card(&config)
                .unwrap_err()
                .to_string()
                .contains("requires an exact channel count")
        );
        config.task.channels = Some(3);
        assert!(model_architecture_card(&config).unwrap().is_some());
    }

    #[test]
    fn icml_pinned_architecture_identity_records_reconstructed_paper_bounds() {
        let mut config = sample_config();
        config
            .model
            .knobs
            .insert("fern_architecture".to_string(), json!("icml_pinned"));

        let identity = model_architecture_identity(&config);

        assert_eq!(identity["config_knobs"]["fern_architecture"], "icml_pinned");
        assert_eq!(
            identity["encoder"]["z_to_x_scale_structure_by_block_id"]["v1"],
            "diagonal"
        );
        assert_eq!(identity["postprocess"]["free_scale"]["active"], "PPSoftclipLeaky");
        assert_eq!(
            identity["postprocess"]["positive_brenier_scale"]["effective_scale_bounds"][1],
            5.5
        );
    }

    #[test]
    fn fern_architecture_factor_decoder_covers_the_boolean_cube_once() {
        let variants = [
            "current",
            "icml_pinned",
            "interleaved_unclamped_wide",
            "all_complex_clamped_wide",
            "all_complex_unclamped_paper_brenier",
            "interleaved_clamped_wide",
            "interleaved_unclamped_paper_brenier",
            "all_complex_clamped_paper_brenier",
        ];
        let factors = variants
            .into_iter()
            .map(fern_architecture_factors)
            .map(|factors| {
                (
                    factors.interleaved_encoder,
                    factors.clamped_free_scale,
                    factors.paper_brenier_bound,
                )
            })
            .collect::<std::collections::BTreeSet<_>>();

        assert_eq!(factors.len(), 8);
    }

    #[test]
    fn v2_explicit_factors_project_to_the_same_typed_card_as_the_legacy_label() {
        let mut legacy = sample_config();
        legacy
            .model
            .knobs
            .insert("fern_architecture".to_string(), json!("icml_pinned"));
        let mut explicit = legacy.clone();
        explicit.model.knobs.remove("fern_architecture");
        explicit
            .model
            .knobs
            .insert("encoder_layout".to_string(), json!("interleaved"));
        explicit
            .model
            .knobs
            .insert("free_scale_processing".to_string(), json!("soft_clamped"));
        explicit
            .model
            .knobs
            .insert("brenier_scale_ceiling".to_string(), json!(4.5));

        assert_eq!(
            model_architecture_card(&legacy)
                .unwrap()
                .unwrap()
                .architecture_hash()
                .unwrap(),
            model_architecture_card(&explicit)
                .unwrap()
                .unwrap()
                .architecture_hash()
                .unwrap()
        );
        let identity = model_architecture_identity(&explicit);
        assert_eq!(identity["config_knobs"]["fern_architecture"], Value::Null);
        assert_eq!(identity["config_knobs"]["encoder_layout"], "interleaved");
    }

    #[test]
    fn mixed_factor_identity_reports_each_active_runtime_choice() {
        let mut config = sample_config();
        config.model.knobs.insert(
            "fern_architecture".to_string(),
            json!("interleaved_unclamped_paper_brenier"),
        );

        let identity = model_architecture_identity(&config);

        assert_eq!(
            identity["encoder"]["z_to_x_scale_structure_by_block_id"]["v1"],
            "diagonal"
        );
        assert_eq!(identity["postprocess"]["free_scale"]["active"], "PPIdentity");
        assert_eq!(
            identity["postprocess"]["positive_brenier_scale"]["effective_scale_bounds"][1],
            5.5
        );
    }

    #[test]
    fn radial_head_is_reported_without_claiming_a_new_predictive_family() {
        let mut config = sample_config();
        config.model.knobs.insert("fern_radial_head".to_string(), json!(true));

        let identity = model_architecture_identity(&config);

        assert_eq!(identity["config_knobs"]["fern_radial_head"], true);
        assert_eq!(identity["coefficient_heads"]["radial_head"]["active"], true);
        assert_eq!(
            identity["coefficient_heads"]["radial_head"]["changes_predictive_family"],
            false
        );
        assert_eq!(identity["source_distribution"]["y_kind"], "gauss");
    }

    #[test]
    fn declared_mixture_identity_reports_the_emitted_law() {
        let mut config = sample_config();
        config
            .model
            .knobs
            .insert("fern_density".to_string(), json!("bounded_mdn"));
        config.model.knobs.remove("num_moe");
        config
            .model
            .knobs
            .insert("mixture_component_count".to_string(), json!(4));
        config
            .model
            .knobs
            .insert("mixture_relative_std_floor".to_string(), json!(0.001));

        let identity = model_architecture_identity(&config);

        assert_eq!(identity["source_distribution"]["emitted_law"], "gaussian_mixture");
        assert_eq!(identity["source_distribution"]["y_kind"], "gaussian_mixture");
    }

    fn sample_config() -> ResolvedConfigSpec {
        let mut knobs = BTreeMap::new();
        knobs.insert("fern_architecture".to_string(), json!("current"));
        knobs.insert("Brenier_patch_spec".to_string(), json!({"G": 2, "L": 64, "P": 32}));
        knobs.insert("Coupling_patch_spec".to_string(), json!({"G": 1, "L": 64, "P": 64}));
        knobs.insert("block_size".to_string(), json!(24));
        knobs.insert("dim_augment".to_string(), json!(128));
        knobs.insert("dim_hidden".to_string(), json!(128));
        knobs.insert("eig_mode".to_string(), json!("w2_dirac"));
        knobs.insert("factory_schemas".to_string(), json!({}));
        knobs.insert("householder_backend".to_string(), json!("torch"));
        knobs.insert("num_eval_samples".to_string(), json!(1));
        knobs.insert("num_moe".to_string(), json!(1));
        knobs.insert("num_reflects".to_string(), json!(24));
        knobs.insert("num_train_samples".to_string(), json!(1));
        knobs.insert("revin".to_string(), json!(false));
        knobs.insert("revin_affine".to_string(), json!(true));
        knobs.insert("revin_subtract_last".to_string(), json!(false));
        knobs.insert(
            "shape_contract".to_string(),
            json!({
                "pred_card": {"layout": "b s h d"},
                "target_card": {"layout": "b h d"},
            }),
        );
        knobs.insert("w_eig".to_string(), json!(0.0));

        let smoothing = SmoothingSpecConfig {
            kind: SmoothingKind::None,
            window: 3,
            alpha: 0.1,
        };
        ResolvedConfigSpec {
            identity_scheme: crate::resolved_cfg::CURRENT_IDENTITY_SCHEME,
            schema: RESOLVED_CONFIG_SCHEMA.to_string(),
            execution: ExecutionConfig {
                kind: ExecutionKind::Train,
                checkpoint_source: None,
            },
            model: ModelConfig {
                model_type: "fern".to_string(),
                knobs,
            },
            task: TaskConfig {
                seq_len: 336,
                pred_len: 64,
                channels: None,
                channel_independent: false,
            },
            action: ActionConfig::StateOnly,
            data: DataConfig {
                train_start: 0.0,
                train_end: 0.7,
                val_start: 0.7,
                val_end: 0.9,
                test_start: 0.9,
                test_end: 1.0,
                normalization: NormalizationKind::None,
                noise_kind: NoiseKind::Gaussian,
                noise_ar1_phi: 0.0,
            },
            train: TrainConfig {
                task_name: "long_term_forecast".to_string(),
                epochs: 50,
                patience: 5,
                grace_period: 1,
                batch_size: 95,
                num_workers: 0,
                training_target: TrainingTargetConfig::StateForecast,
            },
            optim: OptimConfig {
                optimizer_type: OptimizerKind::Adamw,
                learning_rate: 3e-4,
                weight_decay: 0.0,
                max_grad_norm: 0.0,
                muon_momentum: 0.95,
                muon_beta2: 0.999,
                muon_ns_steps: 5,
            },
            scheduler: SchedulerConfig {
                scheduler_type: SchedulerKind::None,
                plateau_patience: 10,
                factor: 0.1,
                warmup_epochs: 5,
                eta_min: 1e-5,
            },
            runtime: RuntimeConfig {
                device: DeviceKind::Cuda,
                dtype: DTypeKind::Float32,
                amp_dtype: AmpDTypeKind::None,
                compile: CompileConfig {
                    enabled: false,
                    mode: "default".to_string(),
                    fullgraph: false,
                    dynamic: false,
                },
            },
            loss: LossConfig {
                train: Vec::new(),
                eval: Vec::new(),
                huber_delta: HuberDeltaConfig { train: 1.0, eval: 1.0 },
                num_proj_swd: 0,
                probability: ProbabilityConfig {
                    num_train_samples: 1,
                    num_eval_samples: 1,
                    quantile_levels: vec![0.05, 0.5, 0.95],
                },
                smoothing: SmoothPolicyConfig {
                    train: smoothing.clone(),
                    val: smoothing.clone(),
                    test: smoothing,
                },
            },
            artifact: ArtifactConfig {
                policy: ArtifactPolicy::Slim,
            },
            bootstrap: None,
        }
    }

    fn implementation_inputs() -> ModelImplementationInputsV1 {
        ModelImplementationInputsV1 {
            source_bundle_hash: SourceBundleHash::from_content_bytes(b"reviewed source manifest"),
            dependency_lock_hash: DependencyLockHash::from_content_bytes(b"reviewed dependency manifest"),
            worker_protocol_version: 4,
            numerical_policy: RawNumericalPolicyV1 {
                parameter_dtype: fern_experiment_types::NumericalDtypeV1::Float32,
                autocast_dtype: None,
                float32_matmul_precision: fern_experiment_types::Float32MatmulPrecisionV1::High,
                cuda_matmul_allow_tf32: true,
                cudnn_allow_tf32: true,
                deterministic_algorithms: false,
                cudnn_deterministic: false,
                cudnn_benchmark: false,
            },
            active_feature_flags: vec!["paper-worker".to_string()],
        }
    }
}
