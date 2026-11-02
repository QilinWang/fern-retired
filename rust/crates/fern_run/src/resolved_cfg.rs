//! Resolved Fern training config wire schema.
//!
//! `fern.cfg.v2` is the narrow bridge between the current Python attrs config
//! objects and the future Rust run planner. This module validates an already
//! resolved config; it does not choose presets, allocate models, or touch the DB.

use std::collections::{BTreeMap, BTreeSet};

use schemars::JsonSchema;
use serde::{Deserialize, Deserializer, Serialize};
use sha2::{Digest, Sha256};

// `DeviceKind` is shared with the declared execution profile. Re-export it
// here because this is the existing resolved-config public path; the concrete
// type has one owner in `fern_experiment_types`.
pub use fern_experiment_types::{DeviceKind, Sha256Digest};

pub const RESOLVED_CONFIG_SCHEMA: &str = "fern.cfg.v2";

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
pub struct ResolvedConfigSpec {
    pub schema: String,
    /// Which rule turns this config into an identity. See the "Identity
    /// schemes" section below; `schema` says what the config MEANS, this says
    /// what its hash is OVER.
    ///
    /// Absent on every config written before 2026-08-26, which is a date
    /// rather than a guess, so absence reads as scheme 1.
    #[serde(default = "identity_scheme_of_configs_written_before_schemes_existed")]
    pub identity_scheme: u32,
    pub execution: ExecutionConfig,
    pub model: ModelConfig,
    pub task: TaskConfig,
    /// Explicit in the TYPE and invisible in the BYTES, which is what the two
    /// requirements together actually mean.
    ///
    /// The type is not `Option`: every config has an action, and state-only is
    /// a value rather than an absence, so no reader can forget the case
    /// (correction 353, and the owner's ruling of 2026-08-26 that an explicit
    /// `state_only` beats a missing field).
    ///
    /// The bytes still omit it, because `config_hash` is a sha256 over this
    /// struct's serialization and `target_hash` embeds that. Serialising
    /// state-only where nothing serialised before moves the identity of every
    /// historical run at once -- proved on 2026-08-26 by
    /// `historical_single_gaussian_fern_target_keeps_its_campaign_identity`,
    /// which pins a real campaign target at dc85f49833ca324a and read
    /// 53c5cdb930bda4a1 while this field serialised unconditionally.
    ///
    /// So: an action-conditioned run writes its action and always did; a
    /// state-only run writes nothing and always did; and Rust code can no
    /// longer see an absence.
    ///
    /// CHG 2026-08-26: the omission is gone from the WIRE and moved into
    /// scheme 1, where it is a rule with a number instead of an attribute
    /// nobody could touch. The paragraph above is kept because it is still the
    /// reason scheme 1 looks the way it does -- but "the bytes still omit it"
    /// now describes scheme 1 only, and scheme 2 states it like everything
    /// else. See `project_onto_scheme_one`.
    #[serde(default)]
    pub action: ActionConfig,
    pub data: DataConfig,
    pub train: TrainConfig,
    pub optim: OptimConfig,
    pub scheduler: SchedulerConfig,
    pub runtime: RuntimeConfig,
    pub loss: LossConfig,
    pub artifact: ArtifactConfig,
    /// EnbPI member identity: this run trains on a block-bootstrap resample
    /// of the training windows. Absent for every non-ensemble run, and serde
    /// skips it when None, so every existing config's canonical JSON — and
    /// therefore every existing run identity — is byte-unchanged.
    #[serde(default)]
    pub bootstrap: Option<BootstrapMemberConfig>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ActionVariantKind {
    CurrentTwoSite,
    ActionUsedFutureMean,
    DelayEmbedding,
    SeparateEncoderHidden,
    JointEncoderConcat,
    ActionForecastModifiesState,
    StateAffineFullCausal,
}

impl Default for ActionVariantKind {
    fn default() -> Self {
        Self::CurrentTwoSite
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
pub enum ActionHandlerKind {
    #[serde(rename = "fern.separate_tde")]
    FernSeparateTde,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum ActionConfig {
    StateOnly,
    GlobalActionVector {
        handler: ActionHandlerKind,
        action_dim: u64,
        future_only: bool,
        variant: ActionVariantKind,
    },
}

impl Default for ActionConfig {
    fn default() -> Self {
        // Absent `action` on fern.cfg.v2 was state-only; the field was optional
        // so historical identities did not change (be27a341).
        Self::StateOnly
    }
}

impl<'de> Deserialize<'de> for ActionConfig {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        // Frozen action records predate the internally tagged `kind` field and
        // the later `variant` field. They were a struct { handler, action_dim,
        // future_only } with future_only omitted when false, and handler stored
        // as "separate_tde_v1". Missing kind + those fields is GlobalActionVector
        // with future_only=false and variant=CurrentTwoSite.
        #[derive(Deserialize)]
        #[serde(deny_unknown_fields)]
        struct ActionConfigWire {
            kind: Option<String>,
            handler: Option<String>,
            action_dim: Option<u64>,
            future_only: Option<bool>,
            variant: Option<ActionVariantKind>,
        }

        let wire = ActionConfigWire::deserialize(deserializer)?;
        let handler = match wire.handler.as_deref() {
            None => None,
            Some("fern.separate_tde") | Some("separate_tde_v1") => Some(ActionHandlerKind::FernSeparateTde),
            Some(other) => {
                return Err(serde::de::Error::unknown_variant(
                    other,
                    &["fern.separate_tde", "separate_tde_v1"],
                ));
            }
        };
        match (wire.kind.as_deref(), handler, wire.action_dim) {
            (Some("state_only"), None, None) => Ok(Self::StateOnly),
            (Some("state_only"), _, _) => Err(serde::de::Error::custom(
                "action kind state_only cannot carry handler or action_dim",
            )),
            (Some("global_action_vector"), Some(handler), Some(action_dim))
            | (None, Some(handler), Some(action_dim)) => Ok(Self::GlobalActionVector {
                handler,
                action_dim,
                future_only: wire.future_only.unwrap_or(false),
                variant: wire.variant.unwrap_or(ActionVariantKind::CurrentTwoSite),
            }),
            (Some("global_action_vector"), _, _) => Err(serde::de::Error::missing_field("handler")),
            (None, None, None) => Ok(Self::StateOnly),
            (None, _, _) => Err(serde::de::Error::custom(
                "action without kind must be state-only or carry handler and action_dim",
            )),
            (Some(other), _, _) => Err(serde::de::Error::unknown_variant(
                other,
                &["state_only", "global_action_vector"],
            )),
        }
    }
}

impl ActionConfig {
    pub fn action_dim(&self) -> Option<u64> {
        match self {
            Self::StateOnly => None,
            Self::GlobalActionVector { action_dim, .. } => Some(*action_dim),
        }
    }

    /// True when this config declares no action axis.
    ///
    /// Used by `skip_serializing_if` so state-only stays byte-invisible; see the
    /// note on `ResolvedConfigSpec::action`. It is a method rather than an
    /// inline closure so the reason has somewhere to live.
    pub fn is_state_only(&self) -> bool {
        matches!(self, Self::StateOnly)
    }

    pub fn future_only(&self) -> bool {
        match self {
            Self::StateOnly => false,
            Self::GlobalActionVector { future_only, .. } => *future_only,
        }
    }

    pub fn variant(&self) -> Option<ActionVariantKind> {
        match self {
            Self::StateOnly => None,
            Self::GlobalActionVector { variant, .. } => Some(*variant),
        }
    }
}

/// One ensemble member's bootstrap declaration (EnbPI rebuild, piece 1).
/// All four fields are identity: two members differ by seed and index, and
/// a 15-member design is a different experiment from a 25-member one even
/// for the member they share.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
pub struct BootstrapMemberConfig {
    /// Which member of the declared ensemble this run is (0-based).
    pub bootstrap_idx: u64,
    /// This member's mask seed, derived at planning time as
    /// resample_seed_base + bootstrap_idx.
    pub bootstrap_seed: u64,
    /// Moving-block length over train-window indices (blocks preserve
    /// temporal dependence; the Python StageBlockBootstrap consumes it).
    pub block_size: u64,
    /// Declared ensemble size B.
    pub ensemble_size: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ExecutionKind {
    Train,
    EvaluationOnly,
    PretrainedInference,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum CheckpointSource {
    /// Validation-best weights produced by one already closed Fern Stage
    /// Attempt. Every digest is scientific identity; the current file path is
    /// deliberately absent and is resolved by the outer runtime boundary.
    TrainedAttempt {
        producer_stage_attempt_id: String,
        training_config_digest: Sha256Digest,
        implementation_bundle_digest: Sha256Digest,
        artifact_digest: Sha256Digest,
    },
    /// Fixed external weights identified by the provider's immutable revision.
    /// This does not pretend a multi-file provider snapshot is one local blob.
    PretrainedAsset { model_id: String, revision: String },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ExecutionContract<'a> {
    Train,
    EvaluateTrainedCheckpoint {
        producer_stage_attempt_id: &'a str,
        training_config_digest: &'a Sha256Digest,
        implementation_bundle_digest: &'a Sha256Digest,
        artifact_digest: &'a Sha256Digest,
    },
    PretrainedAsset {
        model_id: &'a str,
        revision: &'a str,
    },
    /// Historical provider-backed inference already pins model id and
    /// revision inside model.knobs. It remains readable while new V2 drafts
    /// may additionally carry the same provider identity in a typed source.
    PretrainedInference,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ExecutionConfig {
    pub kind: ExecutionKind,
    #[serde(default)]
    pub checkpoint_source: Option<CheckpointSource>,
}

impl ExecutionConfig {
    /// Resolve the execution meaning without filesystem, Registry, or process
    /// access. I/O layers may locate the named bytes only after this pure
    /// contract refuses an impossible kind/source combination.
    pub fn contract(&self) -> Result<ExecutionContract<'_>, String> {
        match (&self.kind, &self.checkpoint_source) {
            (ExecutionKind::Train, None) => Ok(ExecutionContract::Train),
            (ExecutionKind::PretrainedInference, None) => Ok(ExecutionContract::PretrainedInference),
            (
                ExecutionKind::EvaluationOnly,
                Some(CheckpointSource::TrainedAttempt {
                    producer_stage_attempt_id,
                    training_config_digest,
                    implementation_bundle_digest,
                    artifact_digest,
                }),
            ) if !producer_stage_attempt_id.trim().is_empty() => Ok(ExecutionContract::EvaluateTrainedCheckpoint {
                producer_stage_attempt_id,
                training_config_digest,
                implementation_bundle_digest,
                artifact_digest,
            }),
            (ExecutionKind::PretrainedInference, Some(CheckpointSource::PretrainedAsset { model_id, revision }))
                if !model_id.trim().is_empty() && !revision.trim().is_empty() =>
            {
                Ok(ExecutionContract::PretrainedAsset { model_id, revision })
            }
            (ExecutionKind::Train, Some(_)) => Err("execution.kind=train must not name checkpoint_source".to_string()),
            (ExecutionKind::EvaluationOnly, None) => {
                Err("execution.kind=evaluation_only requires a trained_attempt checkpoint_source".to_string())
            }
            (ExecutionKind::EvaluationOnly, Some(_)) => {
                Err("execution.kind=evaluation_only requires a non-empty trained_attempt checkpoint_source".to_string())
            }
            (ExecutionKind::PretrainedInference, Some(_)) => Err(
                "execution.kind=pretrained_inference requires a non-empty pretrained_asset checkpoint_source"
                    .to_string(),
            ),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
pub struct ModelConfig {
    #[serde(rename = "type")]
    pub model_type: String,
    #[serde(default)]
    pub knobs: BTreeMap<String, serde_json::Value>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
pub struct TaskConfig {
    pub seq_len: u64,
    pub pred_len: u64,
    pub channels: Option<u64>,
    pub channel_independent: bool,
}

/// Mirrors Python `fr_specs.NormalizationMethod`; serde strings must match its
/// StrEnum values exactly.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum NormalizationKind {
    Standard,
    RobustIqr,
    RobustMad,
    CenterMean,
    CenterMedian,
    None,
}

/// One definition, re-exported (boundary_surgery_batch step 1): `NoiseKind`
/// lives in the leaf identity crate so a future presets crate does not eat
/// fern_run whole for one small pure enum. Every existing
/// `fern_run::resolved_cfg::NoiseKind` path still compiles through this
/// re-export — not a synonym, the same type.
pub use fern_experiment_types::NoiseKind;

/// Data seam owned by Rust: partition fractions, normalization, and noise
/// kind. Fraction field names mirror serie `PartitionSpec`; each pair is a
/// half-open fraction interval over the full series.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize, JsonSchema)]
pub struct DataConfig {
    pub train_start: f64,
    pub train_end: f64,
    pub val_start: f64,
    pub val_end: f64,
    pub test_start: f64,
    pub test_end: f64,
    pub normalization: NormalizationKind,
    pub noise_kind: NoiseKind,
    /// Lag-one autocorrelation of the observation noise.
    ///
    /// A SEPARATE axis from `noise_kind`: that says what one reading's error
    /// looks like, this says how much it remembers the previous reading.
    /// `eps_t = phi*eps_{t-1} + sqrt(1-phi^2)*eta_t`, so variance stays one
    /// and a relative dose keeps meaning the same multiple of the clean
    /// training standard deviation at every phi.
    ///
    /// Zero is the campaign's entire history — every lane so far draws each
    /// timestep independently — so zero is omitted from the serialized config
    /// and therefore from the hashed preimage. Existing run hashes are
    /// unchanged; only a lane that asks for correlation moves.
    #[serde(default)]
    pub noise_ar1_phi: f64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ActionStitchReducer {
    Mean,
    Median,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum TrainingTargetConfig {
    StateForecast,
    StateAndActionForecast {
        action_loss_weight: f64,
        action_stitch_reducer: ActionStitchReducer,
    },
}

impl TrainingTargetConfig {
    /// True when this config trains the ordinary state forecast and no action
    /// head. Used by `skip_serializing_if` to keep the historical value
    /// byte-invisible; see the note on `TrainConfig::training_target`.
    pub fn is_state_forecast(&self) -> bool {
        matches!(self, Self::StateForecast)
    }
}

impl Default for TrainingTargetConfig {
    fn default() -> Self {
        // Implicit before this field existed. Named by `is_state_forecast` on
        // this type, and by the 2026-08-26 census of 49 frozen Plans
        // (reference:88981116). CHG 2026-08-27: this cited "resolved_cfg.rs:753",
        // which a 182-line insertion above moved into the middle of an unrelated
        // comment block. A line number is a citation that rots on the next edit;
        // name the item instead.
        Self::StateForecast
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
pub struct TrainConfig {
    pub task_name: String,
    pub epochs: u64,
    pub patience: u64,
    pub grace_period: u64,
    pub batch_size: u64,
    pub num_workers: u64,
    /// Explicit in the type, invisible in the bytes when it is the historical
    /// value -- the same rule as `ResolvedConfigSpec::action` above, for the
    /// same reason.
    ///
    /// The owner's ruling authorised the default as a READ rule, for the 49
    /// frozen Plans that fail with "missing field training_target". `#[serde(default)]`
    /// alone only governs deserialisation, so without the skip every config
    /// this code WRITES gains `"training_target":{"kind":"state_forecast"}` and
    /// the identity of every existing run moves with it. The Migration Plan
    /// states the invariant directly: integrity of a stored Plan is the hash of
    /// the frozen bytes, not the hash of a projection into today's type.
    #[serde(default)]
    pub training_target: TrainingTargetConfig,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum OptimizerKind {
    Adamw,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
pub struct OptimConfig {
    #[serde(rename = "type")]
    pub optimizer_type: OptimizerKind,
    pub learning_rate: f64,
    pub weight_decay: f64,
    pub max_grad_norm: f64,
    pub muon_momentum: f64,
    pub muon_beta2: f64,
    pub muon_ns_steps: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum SchedulerKind {
    Plateau,
    Cosine,
    None,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
pub struct SchedulerConfig {
    #[serde(rename = "type")]
    pub scheduler_type: SchedulerKind,
    pub plateau_patience: u64,
    pub factor: f64,
    pub warmup_epochs: u64,
    pub eta_min: f64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum DTypeKind {
    Float32,
    Float16,
    Bfloat16,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum AmpDTypeKind {
    None,
    Float16,
    Bfloat16,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
pub struct RuntimeConfig {
    pub device: DeviceKind,
    pub dtype: DTypeKind,
    pub amp_dtype: AmpDTypeKind,
    pub compile: CompileConfig,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
pub struct CompileConfig {
    pub enabled: bool,
    pub mode: String,
    pub fullgraph: bool,
    pub dynamic: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum MetricName {
    Mse,
    Mae,
    Huber,
    Swd,
    Ept,
    Crps,
    Nll,
    FernNll,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum MetricRole {
    Monitor,
    Active,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
pub struct MetricConfig {
    pub name: MetricName,
    pub role: MetricRole,
    pub weight: f64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
pub struct HuberDeltaConfig {
    pub train: f64,
    pub eval: f64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
pub struct ProbabilityConfig {
    pub num_train_samples: u64,
    pub num_eval_samples: u64,
    pub quantile_levels: Vec<f64>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
pub struct LossConfig {
    pub train: Vec<MetricConfig>,
    pub eval: Vec<MetricConfig>,
    pub huber_delta: HuberDeltaConfig,
    pub num_proj_swd: u64,
    pub probability: ProbabilityConfig,
    pub smoothing: SmoothPolicyConfig,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum SmoothingKind {
    None,
    Sma,
    Ema,
    Median,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
pub struct SmoothingSpecConfig {
    pub kind: SmoothingKind,
    pub window: u64,
    pub alpha: f64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
pub struct SmoothPolicyConfig {
    pub train: SmoothingSpecConfig,
    pub val: SmoothingSpecConfig,
    pub test: SmoothingSpecConfig,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ArtifactPolicy {
    Slim,
    Full,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
pub struct ArtifactConfig {
    pub policy: ArtifactPolicy,
}

impl ResolvedConfigSpec {
    pub fn validate(&self) -> Result<(), Vec<String>> {
        let mut errors = Vec::new();

        if self.schema != RESOLVED_CONFIG_SCHEMA {
            errors.push(format!(
                "schema must be {RESOLVED_CONFIG_SCHEMA:?}, got {:?}",
                self.schema
            ));
        }
        if self.model.model_type.trim().is_empty() {
            errors.push("model.type must be non-empty".to_string());
        }
        if self.task.seq_len == 0 {
            errors.push("task.seq_len must be > 0".to_string());
        }
        if self.task.pred_len == 0 {
            errors.push("task.pred_len must be > 0".to_string());
        }
        if let ActionConfig::GlobalActionVector { action_dim, .. } = &self.action {
            if *action_dim == 0 {
                errors.push("action.action_dim must be > 0".to_string());
            }
            if self.task.channel_independent {
                errors.push("action-conditioned configs cannot set task.channel_independent=true".to_string());
            }
        }
        validate_data_config(&self.data, &mut errors);
        match self.execution.contract() {
            Ok(ExecutionContract::PretrainedAsset { model_id, revision }) => {
                if self.model.knobs.get("hf_model_id").and_then(serde_json::Value::as_str) != Some(model_id) {
                    errors.push("pretrained_asset.model_id must match model.knobs.hf_model_id".to_string());
                }
                if self.model.knobs.get("hf_revision").and_then(serde_json::Value::as_str) != Some(revision) {
                    errors.push("pretrained_asset.revision must match model.knobs.hf_revision".to_string());
                }
            }
            Ok(_) => {}
            Err(error) => errors.push(error),
        }
        if self.execution.kind == ExecutionKind::Train && self.train.epochs == 0 {
            errors.push("train.epochs must be > 0".to_string());
        }
        if self.execution.kind == ExecutionKind::EvaluationOnly && self.train.epochs != 0 {
            errors.push("evaluation_only requires train.epochs=0".to_string());
        }
        if self.execution.kind == ExecutionKind::PretrainedInference && self.train.epochs != 0 {
            errors.push("pretrained_inference requires train.epochs=0".to_string());
        }
        if self.train.task_name.trim().is_empty() {
            errors.push("train.task_name must be non-empty".to_string());
        }
        if self.train.batch_size == 0 {
            errors.push("train.batch_size must be > 0".to_string());
        }
        if let TrainingTargetConfig::StateAndActionForecast { action_loss_weight, .. } = &self.train.training_target {
            if !action_loss_weight.is_finite() || *action_loss_weight <= 0.0 {
                errors.push("train.training_target.action_loss_weight must be finite and > 0".to_string());
            }
            match self.action.variant() {
                Some(ActionVariantKind::ActionForecastModifiesState) => {}
                _ => errors.push(
                    "state_and_action_forecast requires action.variant=action_forecast_modifies_state".to_string(),
                ),
            }
        }
        if !self.optim.learning_rate.is_finite() || self.optim.learning_rate <= 0.0 {
            errors.push("optim.learning_rate must be finite and > 0".to_string());
        }
        if !self.optim.weight_decay.is_finite() || self.optim.weight_decay < 0.0 {
            errors.push("optim.weight_decay must be finite and >= 0".to_string());
        }
        if !self.optim.max_grad_norm.is_finite() || self.optim.max_grad_norm < 0.0 {
            errors.push("optim.max_grad_norm must be finite and >= 0".to_string());
        }
        if !self.optim.muon_momentum.is_finite() || self.optim.muon_momentum <= 0.0 {
            errors.push("optim.muon_momentum must be finite and > 0".to_string());
        }
        if !self.optim.muon_beta2.is_finite() || self.optim.muon_beta2 <= 0.0 {
            errors.push("optim.muon_beta2 must be finite and > 0".to_string());
        }
        if self.optim.muon_ns_steps == 0 {
            errors.push("optim.muon_ns_steps must be > 0".to_string());
        }
        if !self.scheduler.factor.is_finite() || self.scheduler.factor <= 0.0 {
            errors.push("scheduler.factor must be finite and > 0".to_string());
        }
        if !self.scheduler.eta_min.is_finite() || self.scheduler.eta_min < 0.0 {
            errors.push("scheduler.eta_min must be finite and >= 0".to_string());
        }
        if !matches!(
            self.runtime.compile.mode.as_str(),
            "default" | "reduce-overhead" | "max-autotune"
        ) {
            errors.push("runtime.compile.mode must be one of default, reduce-overhead, max-autotune".to_string());
        }

        if let Some(bootstrap) = &self.bootstrap {
            if bootstrap.block_size == 0 {
                errors.push("bootstrap.block_size must be > 0".to_string());
            }
            if bootstrap.ensemble_size == 0 {
                errors.push("bootstrap.ensemble_size must be > 0".to_string());
            }
            if bootstrap.bootstrap_idx >= bootstrap.ensemble_size {
                errors.push("bootstrap.bootstrap_idx must be < bootstrap.ensemble_size".to_string());
            }
        }

        validate_metric_phase("loss.train", &self.loss.train, &mut errors);
        validate_metric_phase("loss.eval", &self.loss.eval, &mut errors);

        if !self.loss.huber_delta.train.is_finite() || self.loss.huber_delta.train <= 0.0 {
            errors.push("loss.huber_delta.train must be finite and > 0".to_string());
        }
        if !self.loss.huber_delta.eval.is_finite() || self.loss.huber_delta.eval <= 0.0 {
            errors.push("loss.huber_delta.eval must be finite and > 0".to_string());
        }
        validate_probability_config(&self.loss.probability, &mut errors);
        validate_smooth_policy(&self.loss.smoothing, &mut errors);

        if errors.is_empty() { Ok(()) } else { Err(errors) }
    }

    pub fn canonical_json(&self) -> Result<String, serde_json::Error> {
        serde_json::to_string(self)
    }

    /// This config's identity, under the scheme the config itself declares.
    ///
    /// The scheme is read from `self` rather than passed in, so there is one
    /// source of truth: a stored config cannot be re-hashed under a rule it
    /// was not written by.
    pub fn config_hash(&self) -> Result<String, IdentitySchemeError> {
        Ok(hash_bytes_16hex(&identity_preimage_bytes(self)?))
    }
}

// ---------------------------------------------------------------------------
// Identity schemes
//
// A SCHEME is an integer naming one rule for what a config's identity covers
// and how it is written down before hashing. It is not a file, not a format,
// and not the same thing as `schema` above: `schema` says what the config
// MEANS ("fern.cfg.v2"), a scheme says what its HASH is over.
//
// Why this exists. `config_hash` used to be sha256 over whatever serde
// happened to emit for `ResolvedConfigSpec`, so the preimage was invisible.
// Nobody could change the config type without silently renaming published
// work, and the defence was five `skip_serializing_if` attributes and a rule
// never to touch it. That is a freeze, not a design: it left `-0.0` phi
// reading back as `+0.0`, and it made every new field an identity crisis
// answered with another attribute.
//
// Stating the scheme INSIDE the preimage makes each hash self-describing. Two
// schemes can never collide, because the number is part of the material being
// hashed, so changing what identity covers stops being an event and becomes a
// migration: add a scheme, capture the before/after map with
// scripts/capture_target_identity_map.py, record the step under
// identity-migrations/.
// ---------------------------------------------------------------------------

/// The scheme every identity is minted under, and the only one this build can
/// compute.
///
/// The preimage is `{"identity_scheme":2,"config":<the whole config>}` with
/// every key sorted and every field stated. Two properties follow, and both
/// are the point:
///
/// - No field is omitted for equalling its default, so adding a field or
///   changing a default is a visible migration rather than an accident.
/// - Keys are sorted rather than written in declaration order, so MOVING a
///   field within the struct no longer changes any identity. Scheme 1 had
///   that landmine too, and nobody had noticed it.
pub const CURRENT_IDENTITY_SCHEME: u32 = 2;

/// What every identity accepted before 2026-08-26 was minted under.
///
/// Scheme 1 hashed the bare config in DECLARATION order with five fields
/// omitted when they held their ordinary value, and with no scheme marker,
/// because schemes did not exist yet.
///
/// This build cannot compute it, and deliberately does not try. Reproducing it
/// would mean carrying a byte-exact replica of the retired serialization
/// forward inside the live struct -- which is the freeze this whole change
/// exists to end, and which would rot the first time a sub-config gained a
/// field.
///
/// Scheme-1 identities are resolved by lookup, in `identity-migrations/`.
/// Two files, because they answer two questions:
///
/// - `2026-08-27-registered-config-identities-scheme-1-to-2.tsv` -- every
///   identity that ACTUALLY EXISTS. 64 distinct configs over 11,909 registered
///   runs, BOTH DEVICES. Its old column is not reconstructed but READ: the
///   registry stores each run's `config_json`, and those bytes are the
///   scheme-1 preimage, verified for all 11,909.
/// - `2026-08-26-config-identity-scheme-1-to-2.tsv` -- 15,304 designed
///   coordinates, CPU ONLY. A designed cell nobody ran has no old name to
///   strand, so this is the weaker of the two; it is kept because its old
///   column was minted by an executable that no longer exists.
///
/// CHG 2026-08-27: the first of those did not exist and this comment claimed
/// the second mapped "all" scheme-1 identities. It does not. `runtime.device`
/// is inside the hashed config, so cpu and cuda are different names for the
/// same experiment, and the campaign is overwhelmingly cuda -- including the
/// one worked example this file's own anchor test uses. Found by review.
///
/// So the constant earns its place by making a scheme-1 config REFUSE rather
/// than silently re-hash as scheme 2 and look current.
pub const IDENTITY_SCHEME_BEFORE_SCHEMES_EXISTED: u32 = 1;

/// What a stored config with no `identity_scheme` field was written under.
///
/// Absence here is a DATE, not a default in the sense correction 353 forbids:
/// every config JSON on disk before 2026-08-26 lacks the field because the
/// field did not exist, and no writer can go back and add it. New configs
/// state it; only historical bytes rely on this.
fn identity_scheme_of_configs_written_before_schemes_existed() -> u32 {
    IDENTITY_SCHEME_BEFORE_SCHEMES_EXISTED
}

/// A scheme this build cannot compute an identity under.
#[derive(Debug)]
pub enum IdentitySchemeError {
    /// The config was minted before schemes existed. Its identity is not
    /// recomputable and does not need to be; look it up in the migration.
    BeforeSchemesExisted,
    Unknown(u32),
    Serialization(serde_json::Error),
}

impl std::fmt::Display for IdentitySchemeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::BeforeSchemesExisted => f.write_str(
                "identity_scheme 1 predates identity schemes and cannot be recomputed. \
                 Look it up: \
                 identity-migrations/2026-08-27-registered-config-identities-scheme-1-to-2.tsv \
                 holds every identity that was actually registered (11,909 runs, both \
                 devices), and \
                 identity-migrations/2026-08-26-config-identity-scheme-1-to-2.tsv \
                 holds the designed coordinates that never ran (cpu only)",
            ),
            Self::Unknown(scheme) => write!(
                f,
                "identity_scheme {scheme} is not one this build knows (it mints {CURRENT_IDENTITY_SCHEME})"
            ),
            Self::Serialization(error) => write!(f, "{error}"),
        }
    }
}

impl std::error::Error for IdentitySchemeError {}

impl From<serde_json::Error> for IdentitySchemeError {
    fn from(error: serde_json::Error) -> Self {
        Self::Serialization(error)
    }
}

/// The exact bytes hashed to produce this config's identity.
///
/// This is the one place that answers "what is hashed". Changing identity is a
/// change here plus a new scheme number, and the two cannot drift apart
/// because the number is written into the bytes.
///
/// CHG 2026-08-27: the scheme was a PARAMETER, which meant
/// `identity_preimage_bytes(&old_config, CURRENT_IDENTITY_SCHEME)` would mint a
/// scheme-2 name for scheme-1 work — the exact bypass the refusal exists to
/// prevent, standing open in a public function. It is read from the config now,
/// so there is one source of truth and no way for a caller to disagree with it.
pub fn identity_preimage_bytes(config: &ResolvedConfigSpec) -> Result<Vec<u8>, IdentitySchemeError> {
    match config.identity_scheme {
        IDENTITY_SCHEME_BEFORE_SCHEMES_EXISTED => Err(IdentitySchemeError::BeforeSchemesExisted),
        CURRENT_IDENTITY_SCHEME => {
            // serde_json's Map is a BTreeMap in this workspace (no
            // preserve_order feature), so this sorts every key at every depth.
            // That is scheme 2's rule, not an accident of the round trip.
            let mut value = serde_json::to_value(config)?;
            // The wrapper states the scheme; stating it twice would let an
            // edit to one copy move the hash for a reason that is not a rule.
            if let Some(object) = value.as_object_mut() {
                object.remove("identity_scheme");
            }
            #[derive(Serialize)]
            struct ConfigIdentityPreimage<'a> {
                identity_scheme: u32,
                config: &'a serde_json::Value,
            }
            Ok(serde_json::to_vec(&ConfigIdentityPreimage {
                identity_scheme: CURRENT_IDENTITY_SCHEME,
                config: &value,
            })?)
        }
        other => Err(IdentitySchemeError::Unknown(other)),
    }
}

pub fn hash_canonical_json_16hex<T: Serialize>(value: &T) -> Result<String, serde_json::Error> {
    let canonical_json = serde_json::to_string(value)?;
    let digest = Sha256::digest(canonical_json.as_bytes());
    let full_hex = format!("{digest:x}");
    Ok(full_hex[..16].to_string())
}

/// SHA-256 over raw bytes, truncated to the same 16-hex prefix the identity
/// hashes use. Used for code-fingerprint file hashing (provenance, never part
/// of run identity).
pub fn hash_bytes_16hex(bytes: &[u8]) -> String {
    let digest = Sha256::digest(bytes);
    let full_hex = format!("{digest:x}");
    full_hex[..16].to_string()
}

/// Mirrors what serie `PartitionSpec` itself enforces: every fraction in
/// [0, 1] and strictly ordered, contiguous-or-gapped bounds
/// train_start < train_end <= val_start < val_end <= test_start < test_end.
fn validate_data_config(data: &DataConfig, errors: &mut Vec<String>) {
    if !data.noise_ar1_phi.is_finite() || !(-1.0..1.0).contains(&data.noise_ar1_phi) {
        errors.push(format!(
            "data.noise_ar1_phi must be finite and lie in (-1, 1), got {}",
            data.noise_ar1_phi
        ));
    }
    if data.noise_ar1_phi != 0.0 && data.noise_kind != NoiseKind::Gaussian {
        errors.push(format!(
            "data.noise_ar1_phi is nonzero but data.noise_kind is {}; this protocol pairs AR(1) temporal dependence with Gaussian innovations so the stationary marginal remains Gaussian while only temporal dependence changes",
            data.noise_kind.value()
        ));
    }
    for (name, value) in [
        ("train_start", data.train_start),
        ("train_end", data.train_end),
        ("val_start", data.val_start),
        ("val_end", data.val_end),
        ("test_start", data.test_start),
        ("test_end", data.test_end),
    ] {
        if !value.is_finite() || !(0.0..=1.0).contains(&value) {
            errors.push(format!("data.{name} must be finite in [0, 1]"));
        }
    }
    if !(data.train_start < data.train_end) {
        errors.push(format!(
            "data train bounds invalid: {}-{}",
            data.train_start, data.train_end
        ));
    }
    if !(data.train_end <= data.val_start && data.val_start < data.val_end) {
        errors.push(format!("data val bounds invalid: {}-{}", data.val_start, data.val_end));
    }
    if !(data.val_end <= data.test_start && data.test_start < data.test_end) {
        errors.push(format!(
            "data test bounds invalid: {}-{}",
            data.test_start, data.test_end
        ));
    }
}

fn validate_metric_phase(phase_name: &str, metrics: &[MetricConfig], errors: &mut Vec<String>) {
    let mut seen = BTreeSet::new();
    for metric in metrics {
        if !seen.insert(metric.name) {
            errors.push(format!("{phase_name} contains duplicate metric {:?}", metric.name));
        }
        match metric.role {
            MetricRole::Active => {
                if !metric.weight.is_finite() || metric.weight <= 0.0 {
                    errors.push(format!(
                        "{phase_name}.{:?} active weight must be finite and > 0",
                        metric.name
                    ));
                }
            }
            MetricRole::Monitor => {
                if metric.weight != 0.0 {
                    errors.push(format!("{phase_name}.{:?} monitor weight must be 0", metric.name));
                }
            }
        }
    }
}

fn validate_probability_config(prob: &ProbabilityConfig, errors: &mut Vec<String>) {
    if prob.num_train_samples == 0 {
        errors.push("loss.probability.num_train_samples must be > 0".to_string());
    }
    if prob.num_eval_samples == 0 {
        errors.push("loss.probability.num_eval_samples must be > 0".to_string());
    }
    if prob.quantile_levels.is_empty() {
        errors.push("loss.probability.quantile_levels cannot be empty".to_string());
        return;
    }

    for quantile in &prob.quantile_levels {
        if !quantile.is_finite() || *quantile <= 0.0 || *quantile >= 1.0 {
            errors.push("loss.probability.quantile_levels must be finite values in (0, 1)".to_string());
            break;
        }
    }
    if prob.quantile_levels.windows(2).any(|pair| pair[0] >= pair[1]) {
        errors.push("loss.probability.quantile_levels must be sorted ascending without duplicates".to_string());
    }
}

fn validate_smooth_policy(policy: &SmoothPolicyConfig, errors: &mut Vec<String>) {
    validate_smoothing_spec("loss.smoothing.train", &policy.train, errors);
    validate_smoothing_spec("loss.smoothing.val", &policy.val, errors);
    validate_smoothing_spec("loss.smoothing.test", &policy.test, errors);
}

fn validate_smoothing_spec(name: &str, spec: &SmoothingSpecConfig, errors: &mut Vec<String>) {
    if spec.window == 0 {
        errors.push(format!("{name}.window must be > 0"));
    }
    if !spec.alpha.is_finite() || spec.alpha <= 0.0 || spec.alpha > 1.0 {
        errors.push(format!("{name}.alpha must be finite in (0, 1]"));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample_config() -> ResolvedConfigSpec {
        ResolvedConfigSpec {
            identity_scheme: CURRENT_IDENTITY_SCHEME,
            schema: RESOLVED_CONFIG_SCHEMA.to_string(),
            execution: ExecutionConfig {
                kind: ExecutionKind::Train,
                checkpoint_source: None,
            },
            model: ModelConfig {
                model_type: "fern".to_string(),
                knobs: BTreeMap::from([("dim_hidden".to_string(), serde_json::json!(128))]),
            },
            task: TaskConfig {
                seq_len: 96,
                pred_len: 48,
                channels: Some(7),
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
                train: vec![
                    MetricConfig {
                        name: MetricName::Huber,
                        role: MetricRole::Active,
                        weight: 1.0,
                    },
                    MetricConfig {
                        name: MetricName::Mse,
                        role: MetricRole::Monitor,
                        weight: 0.0,
                    },
                ],
                eval: vec![MetricConfig {
                    name: MetricName::Mae,
                    role: MetricRole::Active,
                    weight: 1.0,
                }],
                huber_delta: HuberDeltaConfig { train: 1.0, eval: 1.0 },
                num_proj_swd: 0,
                probability: ProbabilityConfig {
                    num_train_samples: 1,
                    num_eval_samples: 1,
                    quantile_levels: vec![0.05, 0.5, 0.95],
                },
                smoothing: SmoothPolicyConfig {
                    train: SmoothingSpecConfig {
                        kind: SmoothingKind::None,
                        window: 3,
                        alpha: 0.1,
                    },
                    val: SmoothingSpecConfig {
                        kind: SmoothingKind::None,
                        window: 3,
                        alpha: 0.1,
                    },
                    test: SmoothingSpecConfig {
                        kind: SmoothingKind::None,
                        window: 3,
                        alpha: 0.1,
                    },
                },
            },
            artifact: ArtifactConfig {
                policy: ArtifactPolicy::Slim,
            },
            bootstrap: None,
        }
    }

    #[test]
    fn validates_sample_config_and_hashes_stably() {
        let cfg = sample_config();
        cfg.validate().expect("sample config should validate");
        assert_eq!(cfg.config_hash().unwrap().len(), 16);
        assert_eq!(cfg.config_hash().unwrap(), cfg.config_hash().unwrap());
    }

    fn digest(label: &[u8]) -> Sha256Digest {
        Sha256Digest::from_bytes(label)
    }

    #[test]
    fn execution_contract_refuses_training_or_evaluation_drift() {
        let mut cfg = sample_config();
        cfg.execution.checkpoint_source = Some(CheckpointSource::TrainedAttempt {
            producer_stage_attempt_id: "stage-attempt-clean".to_string(),
            training_config_digest: digest(b"training config"),
            implementation_bundle_digest: digest(b"implementation bundle"),
            artifact_digest: digest(b"validation-best weights"),
        });
        let errors = cfg.validate().expect_err("train must not consume old weights");
        assert!(
            errors.iter().any(|error| error.contains("train must not name")),
            "{errors:?}"
        );

        cfg.execution.kind = ExecutionKind::EvaluationOnly;
        cfg.train.epochs = 0;
        let contract = cfg.execution.contract().expect("typed evaluation source validates");
        assert!(matches!(
            contract,
            ExecutionContract::EvaluateTrainedCheckpoint {
                producer_stage_attempt_id: "stage-attempt-clean",
                ..
            }
        ));
        cfg.validate()
            .expect("evaluation-only config validates without a fit budget");

        cfg.execution.checkpoint_source = None;
        let errors = cfg.validate().expect_err("evaluation without weights must fail");
        assert!(errors.iter().any(|error| error.contains("requires a trained_attempt")));
    }

    #[test]
    fn checkpoint_source_is_config_identity_but_runtime_path_is_not_a_field() {
        let mut cfg = sample_config();
        cfg.execution = ExecutionConfig {
            kind: ExecutionKind::EvaluationOnly,
            checkpoint_source: Some(CheckpointSource::TrainedAttempt {
                producer_stage_attempt_id: "stage-attempt-clean".to_string(),
                training_config_digest: digest(b"training config"),
                implementation_bundle_digest: digest(b"implementation bundle"),
                artifact_digest: digest(b"weights-a"),
            }),
        };
        cfg.train.epochs = 0;
        let first_hash = cfg.config_hash().unwrap();
        let json = cfg.canonical_json().unwrap();
        assert!(!json.contains("artifact_dir"));
        assert!(!json.contains("path"));

        let Some(CheckpointSource::TrainedAttempt { artifact_digest, .. }) = cfg.execution.checkpoint_source.as_mut()
        else {
            panic!("test source is trained_attempt")
        };
        *artifact_digest = digest(b"weights-b");
        assert_ne!(cfg.config_hash().unwrap(), first_hash);
    }

    #[test]
    fn pretrained_asset_uses_provider_identity_without_a_fake_local_blob() {
        let mut cfg = sample_config();
        cfg.execution = ExecutionConfig {
            kind: ExecutionKind::PretrainedInference,
            checkpoint_source: Some(CheckpointSource::PretrainedAsset {
                model_id: "amazon/chronos-2".to_string(),
                revision: "immutable-provider-revision".to_string(),
            }),
        };
        cfg.model
            .knobs
            .insert("hf_model_id".to_string(), serde_json::json!("amazon/chronos-2"));
        cfg.model.knobs.insert(
            "hf_revision".to_string(),
            serde_json::json!("immutable-provider-revision"),
        );
        cfg.train.epochs = 0;

        assert!(matches!(
            cfg.execution.contract().unwrap(),
            ExecutionContract::PretrainedAsset {
                model_id: "amazon/chronos-2",
                revision: "immutable-provider-revision",
            }
        ));
        cfg.validate().expect("provider identity matches model knobs");
        assert!(!cfg.canonical_json().unwrap().contains("artifact_digest"));
    }

    /// EnbPI piece 1's identity law: a present bootstrap block enters the hash,
    /// and two members differ by seed/index alone.
    ///
    /// CHG 2026-08-26: the first half of this law was "an absent block leaves
    /// every existing identity byte-unchanged", which was true only while the
    /// wire omitted it, and only because nobody was allowed to change the wire.
    /// An absent block is now WRITTEN as null, like everything else, and the
    /// identities it moved are recorded in
    /// identity-migrations/2026-08-26-config-identity-scheme-1-to-2.tsv. The
    /// half that was actually about EnbPI is unchanged and is what is tested.
    #[test]
    fn bootstrap_absent_is_written_and_present_is_identity() {
        let cfg = sample_config();
        assert!(cfg.bootstrap.is_none(), "sample stays a non-ensemble run");
        let json = cfg.canonical_json().unwrap();
        assert!(
            json.contains("\"bootstrap\":null"),
            "an absent block is stated, not omitted"
        );
        let base_hash = cfg.config_hash().unwrap();

        let mut member_zero = sample_config();
        member_zero.bootstrap = Some(BootstrapMemberConfig {
            bootstrap_idx: 0,
            bootstrap_seed: 7_000,
            block_size: 24,
            ensemble_size: 15,
        });
        member_zero.validate().expect("a valid member validates");
        assert_ne!(member_zero.config_hash().unwrap(), base_hash, "the block is identity");

        let mut member_one = member_zero.clone();
        member_one.bootstrap = Some(BootstrapMemberConfig {
            bootstrap_idx: 1,
            bootstrap_seed: 7_001,
            block_size: 24,
            ensemble_size: 15,
        });
        assert_ne!(
            member_one.config_hash().unwrap(),
            member_zero.config_hash().unwrap(),
            "two members are two experiments"
        );

        // Round-trip: an old serialized config (no bootstrap key) still
        // deserializes, with None.
        let reparsed: ResolvedConfigSpec = serde_json::from_str(&json).unwrap();
        assert!(reparsed.bootstrap.is_none());

        let mut invalid = member_zero.clone();
        invalid.bootstrap = Some(BootstrapMemberConfig {
            bootstrap_idx: 15,
            bootstrap_seed: 7_015,
            block_size: 24,
            ensemble_size: 15,
        });
        let errors = invalid.validate().expect_err("idx >= B refuses");
        assert!(errors.iter().any(|e| e.contains("bootstrap_idx")), "{errors:?}");
    }

    #[test]
    fn rejects_overlapping_partition_fractions() {
        let mut cfg = sample_config();
        cfg.data.val_start = 0.6; // overlaps train_end = 0.7
        let errors = cfg.validate().expect_err("overlapping partition should fail");
        assert!(errors.iter().any(|e| e.contains("data val bounds invalid")));
    }

    #[test]
    fn serializes_data_section_with_python_enum_strings() {
        let json = serde_json::to_string(&sample_config()).unwrap();
        assert!(json.contains("\"normalization\":\"none\""));
        assert!(json.contains("\"noise_kind\":\"gaussian\""));
        // CHG 2026-08-26: was `!json.contains("noise_ar1_phi")`. Zero phi used
        // to be omitted so that adding correlated noise would not move any
        // identity -- which also meant -0.0 was written as nothing and read
        // back as +0.0. It is stated now, at whatever value it holds.
        assert!(json.contains("\"noise_ar1_phi\":0.0"));
    }

    #[test]
    fn validates_correlated_noise_as_gaussian_stationary_protocol() {
        let mut cfg = sample_config();
        cfg.data.noise_ar1_phi = 0.9;
        cfg.validate().expect("stationary Gaussian AR(1) should validate");
        assert!(serde_json::to_string(&cfg).unwrap().contains("\"noise_ar1_phi\":0.9"));

        cfg.data.noise_kind = NoiseKind::StudentT3;
        let errors = cfg
            .validate()
            .expect_err("AR(1) over Student-t3 would change two scientific axes");
        assert!(
            errors
                .iter()
                .any(|error| error.contains("pairs AR(1) temporal dependence with Gaussian innovations"))
        );

        cfg.data.noise_kind = NoiseKind::Gaussian;
        cfg.data.noise_ar1_phi = 1.0;
        let errors = cfg.validate().expect_err("non-stationary phi must fail");
        assert!(errors.iter().any(|error| error.contains("lie in (-1, 1)")));
    }

    #[test]
    fn rejects_unsorted_quantiles() {
        let mut cfg = sample_config();
        cfg.loss.probability.quantile_levels = vec![0.5, 0.05, 0.95];
        let errors = cfg.validate().expect_err("unsorted quantiles should fail");
        assert!(errors.iter().any(|e| e.contains("sorted ascending")));
    }

    #[test]
    fn parses_schema_json() {
        let json = serde_json::to_string(&sample_config()).unwrap();
        let cfg: ResolvedConfigSpec = serde_json::from_str(&json).unwrap();
        assert_eq!(cfg.schema, RESOLVED_CONFIG_SCHEMA);
        assert_eq!(cfg.model.model_type, "fern");
    }

    #[test]
    fn action_shape_and_variant_are_always_explicit_identity() {
        let mut pendulum = sample_config();
        pendulum.action = ActionConfig::GlobalActionVector {
            handler: ActionHandlerKind::FernSeparateTde,
            action_dim: 1,
            future_only: false,
            variant: ActionVariantKind::CurrentTwoSite,
        };
        pendulum.validate().expect("an action-conditioned config validates");

        let json = pendulum.canonical_json().unwrap();
        assert!(json.contains("\"kind\":\"global_action_vector\""));
        assert!(json.contains("\"future_only\":false"));
        assert!(json.contains("\"variant\":\"current_two_site\""));
        let reparsed: ResolvedConfigSpec = serde_json::from_str(&json).unwrap();
        assert!(!reparsed.action.future_only());

        let mut level0a = pendulum.clone();
        let ActionConfig::GlobalActionVector { future_only, .. } = &mut level0a.action else {
            panic!("fixture must remain action-conditioned");
        };
        *future_only = true;
        level0a.validate().expect("a future-only config validates");
        assert_ne!(level0a.config_hash().unwrap(), pendulum.config_hash().unwrap());
    }

    #[test]
    fn training_target_is_explicit_and_requires_the_action_forecast_head() {
        let mut cfg = sample_config();
        cfg.train.training_target = TrainingTargetConfig::StateAndActionForecast {
            action_loss_weight: 0.5,
            action_stitch_reducer: ActionStitchReducer::Median,
        };
        let errors = cfg.validate().expect_err("state-only config cannot train actions");
        assert!(
            errors
                .iter()
                .any(|error| error.contains("action_forecast_modifies_state"))
        );

        cfg.action = ActionConfig::GlobalActionVector {
            handler: ActionHandlerKind::FernSeparateTde,
            action_dim: 1,
            future_only: false,
            variant: ActionVariantKind::ActionForecastModifiesState,
        };
        cfg.validate().expect("the declared action head and trainer agree");
        let json = cfg.canonical_json().unwrap();
        assert!(json.contains("\"kind\":\"state_and_action_forecast\""));
        assert!(json.contains("\"action_stitch_reducer\":\"median\""));
        assert!(json.contains("\"action_loss_weight\":0.5"));
    }

    #[test]
    fn stored_train_config_without_training_target_is_state_forecast() {
        let json = r#"{
            "task_name":"forecast","epochs":1,"patience":1,"grace_period":1,
            "batch_size":1,"num_workers":0
        }"#;
        let train: TrainConfig = serde_json::from_str(json).expect("absent training_target is StateForecast");
        assert_eq!(train.training_target, TrainingTargetConfig::StateForecast);
    }

    #[test]
    fn stored_action_without_kind_and_with_global_fields_is_global_action_vector() {
        let json = r#"{
            "handler":"fern.separate_tde","action_dim":1,"future_only":true,
            "variant":"current_two_site"
        }"#;
        let action: ActionConfig = serde_json::from_str(json).expect("legacy action object");
        assert_eq!(
            action,
            ActionConfig::GlobalActionVector {
                handler: ActionHandlerKind::FernSeparateTde,
                action_dim: 1,
                future_only: true,
                variant: ActionVariantKind::CurrentTwoSite,
            }
        );
    }

    #[test]
    fn stored_action_struct_before_kind_and_variant_is_global_action_vector() {
        let json = r#"{"handler":"separate_tde_v1","action_dim":1}"#;
        let action: ActionConfig = serde_json::from_str(json).expect("pre-kind action struct");
        assert_eq!(
            action,
            ActionConfig::GlobalActionVector {
                handler: ActionHandlerKind::FernSeparateTde,
                action_dim: 1,
                future_only: false,
                variant: ActionVariantKind::CurrentTwoSite,
            }
        );
    }
}
