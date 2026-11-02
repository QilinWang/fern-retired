//! `fern_artifact` owns Fern's pure artifact contracts.
//!
//! Pass 6A is intentionally side-effect free:
//! 1. validate logical artifact identities and tensor layouts;
//! 2. describe one shared [`ScientificCaseManifest`];
//! 3. bind an exact-dose [`CaseRealization`] to a worker request;
//! 4. validate a worker result against its declared forecast output.
//!
//! This crate does not open a database, spawn a worker, seal a live directory,
//! accept evidence, or delete files. The maintained workflow pin is
//! `docs/decisions/2026-08-05-migration-plan-priority-1.md`, Pass 6.
//!
//! Stage invocation, result, and receipt contracts bind a stage-plan port
//! declaration to independently checked artifacts. They do not schedule, spawn,
//! or delete.

use std::collections::HashSet;

pub use fern_experiment_types::{ModelArchitectureHash, ModelImplementationHash};
use serde::{Deserialize, Deserializer, Serialize};
use sha2::{Digest, Sha256};

mod compact_metrics_arrow;
mod run_metrics_ipc;
pub use run_metrics_ipc::{RunMetricsCellV1, run_metrics_ipc_bytes};
mod attempt_metadata;
mod dense_point;
mod forecast;
mod forecast_policy;
mod metrics;
mod native_forecast;
mod receipts;
mod stage_invocation;
mod stage_runner;

pub use attempt_metadata::*;
pub use compact_metrics_arrow::*;
pub use dense_point::*;
pub use forecast::*;
pub use forecast_policy::*;
pub use metrics::*;
pub use native_forecast::*;
pub use receipts::*;
pub use stage_invocation::*;
pub use stage_runner::*;

pub const REPORTED_ARTIFACT_SCHEMA: &str = "fern.reported_artifact.v1";
pub const TENSOR_LAYOUT_SCHEMA: &str = "fern.tensor_layout.v1";
pub const SCIENTIFIC_CASE_SCHEMA: &str = "fern.scientific_case.v1";
pub const CASE_REALIZATION_SCHEMA: &str = "fern.case_realization.v1";
pub const WORKER_REQUEST_SCHEMA: &str = "fern.worker_request.v1";
pub const WORKER_RESULT_REPORT_SCHEMA: &str = "fern.worker_result_report.v1";
pub const ATTEMPT_ARTIFACT_COMMIT_SCHEMA: &str = "fern.attempt_artifact_commit.v1";
pub const DENSE_POINT_FORECAST_SCHEMA: &str = "fern.dense_point_forecast.v1";
pub const QUANTILE_FORECAST_SCHEMA: &str = "fern.quantile_forecast.v1";
pub const SAMPLE_FORECAST_SCHEMA: &str = "fern.sample_forecast.v1";
pub const COMPACT_METRICS_SCHEMA: &str = "fern.compact_metrics.v1";
pub const COMPACT_METRICS_MEDIA_TYPE: &str = "application/vnd.apache.arrow.file";
pub const FORECAST_ORIGIN_PLAN_SCHEMA: &str = "fern.forecast_origin_plan.v1";
pub const RETENTION_DECISION_SCHEMA: &str = "fern.retention_decision.v1";

/// One invalid artifact-contract field and the concrete reason it was refused.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ContractError {
    pub field: String,
    pub explanation: String,
}

impl ContractError {
    fn new(field: impl Into<String>, explanation: impl Into<String>) -> Self {
        Self {
            field: field.into(),
            explanation: explanation.into(),
        }
    }
}

impl std::fmt::Display for ContractError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "{}: {}", self.field, self.explanation)
    }
}

impl std::error::Error for ContractError {}

fn sha256_text(bytes: &[u8]) -> String {
    format!("sha256:{:x}", Sha256::digest(bytes))
}

fn validate_sha256_text(field: &str, value: &str) -> Result<(), ContractError> {
    let Some(hex) = value.strip_prefix("sha256:") else {
        return Err(ContractError::new(field, "must start with sha256:"));
    };
    if hex.len() != 64
        || !hex
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return Err(ContractError::new(
            field,
            "must contain exactly 64 lowercase hexadecimal digits",
        ));
    }
    Ok(())
}

macro_rules! digest_type {
    ($name:ident, $field:literal) => {
        #[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize, schemars::JsonSchema)]
        #[serde(try_from = "String", into = "String")]
        pub struct $name(String);

        impl $name {
            pub fn from_bytes(bytes: &[u8]) -> Self {
                Self(sha256_text(bytes))
            }

            pub fn parse(value: impl Into<String>) -> Result<Self, ContractError> {
                let value = value.into();
                validate_sha256_text($field, &value)?;
                Ok(Self(value))
            }

            pub fn as_str(&self) -> &str {
                &self.0
            }
        }

        impl TryFrom<String> for $name {
            type Error = ContractError;

            fn try_from(value: String) -> Result<Self, Self::Error> {
                Self::parse(value)
            }
        }

        impl From<$name> for String {
            fn from(value: $name) -> Self {
                value.0
            }
        }
    };
}

digest_type!(ByteDigest, "byte_digest");
digest_type!(ScientificContentDigest, "scientific_content_digest");
digest_type!(ImplementationDigest, "implementation_digest");
digest_type!(StageExecutionKey, "execution_key");

fn validate_logical_id(field: &str, value: &str) -> Result<(), ContractError> {
    if value.is_empty() {
        return Err(ContractError::new(field, "must be non-empty"));
    }
    if value.starts_with('/') || value.starts_with('\\') || value.contains("..") {
        return Err(ContractError::new(
            field,
            "is a logical identity, not an absolute or parent-relative path",
        ));
    }
    if !value
        .bytes()
        .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b':' | b'.' | b'_' | b'-'))
    {
        return Err(ContractError::new(
            field,
            "may contain only ASCII letters, digits, colon, dot, underscore, and hyphen",
        ));
    }
    Ok(())
}

macro_rules! logical_id_type {
    ($name:ident, $field:literal) => {
        #[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize, schemars::JsonSchema)]
        #[serde(try_from = "String", into = "String")]
        pub struct $name(String);

        impl $name {
            pub fn parse(value: impl Into<String>) -> Result<Self, ContractError> {
                let value = value.into();
                validate_logical_id($field, &value)?;
                Ok(Self(value))
            }

            pub fn as_str(&self) -> &str {
                &self.0
            }
        }

        impl TryFrom<String> for $name {
            type Error = ContractError;

            fn try_from(value: String) -> Result<Self, Self::Error> {
                Self::parse(value)
            }
        }

        impl From<$name> for String {
            fn from(value: $name) -> Self {
                value.0
            }
        }
    };
}

logical_id_type!(ArtifactKey, "artifact_key");
logical_id_type!(ScientificCaseId, "case_id");

/// Semantic purpose of an artifact, independent of its filename or directory.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ArtifactRole {
    WorldParameters,
    LatentCleanTrajectory,
    RecordedReferenceTrajectory,
    UnitNoise,
    MissingnessMask,
    ContaminationMask,
    TrainingScale,
    SplitDefinition,
    ForecastOriginPlan,
    FinalNoisyInput,
    DensePointForecast,
    LegacyPointForecast,
    QuantileForecast,
    PredictiveSamples,
    RawEvaluationTensors,
    ModelCheckpoint,
    ReconstructionManifest,
    CompactMetrics,
    ReleaseMetricsTable,
    CampaignMetricsTable,
    InvocationReceipt,
    ExecutionEnvironment,
    Diagnostics,
    WorkerLog,
    CompiledExperiment,
    RawForecast,
    PostprocessSpec,
    SequenceForecast,
    Metrics,
    ScoreReceipt,
    AnalysisReport,
    TrainingWorkerRequest,
    DgpBank,
    MaterializedCell,
    HorizonEndResponse,
}

impl ArtifactRole {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::WorldParameters => "world_parameters",
            Self::LatentCleanTrajectory => "latent_clean_trajectory",
            Self::RecordedReferenceTrajectory => "recorded_reference_trajectory",
            Self::UnitNoise => "unit_noise",
            Self::MissingnessMask => "missingness_mask",
            Self::ContaminationMask => "contamination_mask",
            Self::TrainingScale => "training_scale",
            Self::SplitDefinition => "split_definition",
            Self::ForecastOriginPlan => "forecast_origin_plan",
            Self::FinalNoisyInput => "final_noisy_input",
            Self::DensePointForecast => "dense_point_forecast",
            Self::LegacyPointForecast => "legacy_point_forecast",
            Self::QuantileForecast => "quantile_forecast",
            Self::PredictiveSamples => "predictive_samples",
            Self::RawEvaluationTensors => "raw_evaluation_tensors",
            Self::ModelCheckpoint => "model_checkpoint",
            Self::ReconstructionManifest => "reconstruction_manifest",
            Self::CompactMetrics => "compact_metrics",
            Self::ReleaseMetricsTable => "release_metrics_table",
            Self::CampaignMetricsTable => "campaign_metrics_table",
            Self::InvocationReceipt => "invocation_receipt",
            Self::ExecutionEnvironment => "execution_environment",
            Self::Diagnostics => "diagnostics",
            Self::WorkerLog => "worker_log",
            Self::CompiledExperiment => "compiled_experiment",
            Self::RawForecast => "raw_forecast",
            Self::PostprocessSpec => "postprocess_spec",
            Self::SequenceForecast => "sequence_forecast",
            Self::Metrics => "metrics",
            Self::ScoreReceipt => "score_receipt",
            Self::AnalysisReport => "analysis_report",
            Self::TrainingWorkerRequest => "training_worker_request",
            Self::DgpBank => "dgp_bank",
            Self::MaterializedCell => "materialized_cell",
            Self::HorizonEndResponse => "horizon_end_response",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum RetentionTier {
    PermanentCompact,
    ReleasePinned,
    BestCheckpoint,
    ColdReconstructable,
    DisposableOperational,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RetentionFactsV1 {
    pub artifact: ArtifactReference,
    pub tier: RetentionTier,
    pub committed_and_checked: bool,
    pub referenced_by_artifacts: Vec<ArtifactKey>,
    pub pinned_by_releases: Vec<String>,
    pub verified_reconstruction: Option<VerifiedReconstructionProofV1>,
}

pub const VERIFIED_RECONSTRUCTION_PROOF_SCHEMA: &str = "fern.verified_reconstruction_proof.v2";

/// The declared comparison for one reconstructed tensor.
///
/// Every reconstruction uses the fixed per-value test
/// `abs(rebuilt - original) <= absolute + relative * abs(original)`. Both
/// byte digests remain recorded for identity and audit, but their equality is
/// not a separate retention rule.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ReconstructionEquivalenceV1 {
    pub absolute_tolerance: f64,
    pub relative_tolerance: f64,
    pub maximum_normalized_error: f64,
    pub compared_value_count: u64,
}

impl ReconstructionEquivalenceV1 {
    fn validate(&self) -> Result<(), ContractError> {
        if !self.absolute_tolerance.is_finite()
            || !self.relative_tolerance.is_finite()
            || !self.maximum_normalized_error.is_finite()
            || self.absolute_tolerance < 0.0
            || self.relative_tolerance < 0.0
            || self.maximum_normalized_error < 0.0
        {
            return Err(ContractError::new(
                "verified_reconstruction.equivalence",
                "tolerance values and maximum_normalized_error must be finite and non-negative",
            ));
        }
        if self.absolute_tolerance == 0.0 && self.relative_tolerance == 0.0 {
            return Err(ContractError::new(
                "verified_reconstruction.equivalence",
                "requires a positive absolute or relative tolerance",
            ));
        }
        if self.compared_value_count == 0 {
            return Err(ContractError::new(
                "verified_reconstruction.equivalence.compared_value_count",
                "must be positive",
            ));
        }
        if self.maximum_normalized_error > 1.0 {
            return Err(ContractError::new(
                "verified_reconstruction.equivalence.maximum_normalized_error",
                "exceeds the declared tolerance",
            ));
        }
        Ok(())
    }
}

/// Proof that an artifact was actually reconstructed and compared with the
/// original scientific content. Merely naming a possible recipe is not proof.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct VerifiedReconstructionProofV1 {
    pub schema: String,
    pub manifest: ArtifactReference,
    pub reconstructor_implementation: ImplementationDigest,
    pub original_content_digest: ScientificContentDigest,
    pub regenerated_content_digest: ScientificContentDigest,
    pub equivalence: ReconstructionEquivalenceV1,
    pub verified_at_unix_ms: u64,
}

impl VerifiedReconstructionProofV1 {
    pub fn validate_against(&self, artifact: &ArtifactReference) -> Result<(), ContractError> {
        if self.schema != VERIFIED_RECONSTRUCTION_PROOF_SCHEMA {
            return Err(ContractError::new(
                "verified_reconstruction.schema",
                format!("must equal {VERIFIED_RECONSTRUCTION_PROOF_SCHEMA}"),
            ));
        }
        if self.manifest.role != ArtifactRole::ReconstructionManifest {
            return Err(ContractError::new(
                "verified_reconstruction.manifest.role",
                "must be reconstruction_manifest",
            ));
        }
        if self.verified_at_unix_ms == 0 {
            return Err(ContractError::new(
                "verified_reconstruction.verified_at_unix_ms",
                "must be positive",
            ));
        }
        if self.original_content_digest != artifact.content_digest {
            return Err(ContractError::new(
                "verified_reconstruction.original_content_digest",
                "must match the artifact selected for retention",
            ));
        }
        self.equivalence.validate()?;
        Ok(())
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "decision", rename_all = "snake_case", deny_unknown_fields)]
pub enum TombstoneDecisionV1 {
    RefusedPermanent,
    RefusedBestCheckpoint,
    RefusedUnverified,
    RefusedReferenced { artifact_keys: Vec<ArtifactKey> },
    RefusedReleasePinned { release_ids: Vec<String> },
    RefusedNotReconstructable,
    Eligible { reason: String },
}

/// Decide whether a destructive retention transition is even eligible.
/// Filesystem deletion is deliberately outside this pure operation and must
/// consume an `Eligible` decision plus write a durable tombstone receipt.
pub fn decide_tombstone(
    facts: &RetentionFactsV1,
    reason: impl Into<String>,
) -> Result<TombstoneDecisionV1, ContractError> {
    let reason = reason.into();
    if reason.trim().is_empty() {
        return Err(ContractError::new("retention.reason", "must be non-empty"));
    }
    let role_minimum = minimum_retention_tier(facts.artifact.role);
    if matches!(facts.tier, RetentionTier::PermanentCompact) || matches!(role_minimum, RetentionTier::PermanentCompact)
    {
        return Ok(TombstoneDecisionV1::RefusedPermanent);
    }
    if matches!(facts.tier, RetentionTier::BestCheckpoint) || matches!(role_minimum, RetentionTier::BestCheckpoint) {
        return Ok(TombstoneDecisionV1::RefusedBestCheckpoint);
    }
    if !facts.committed_and_checked {
        return Ok(TombstoneDecisionV1::RefusedUnverified);
    }
    if !facts.pinned_by_releases.is_empty() {
        return Ok(TombstoneDecisionV1::RefusedReleasePinned {
            release_ids: facts.pinned_by_releases.clone(),
        });
    }
    if !facts.referenced_by_artifacts.is_empty() {
        return Ok(TombstoneDecisionV1::RefusedReferenced {
            artifact_keys: facts.referenced_by_artifacts.clone(),
        });
    }
    if matches!(facts.tier, RetentionTier::ColdReconstructable)
        || matches!(role_minimum, RetentionTier::ColdReconstructable)
    {
        let Some(proof) = facts.verified_reconstruction.as_ref() else {
            return Ok(TombstoneDecisionV1::RefusedNotReconstructable);
        };
        if proof.validate_against(&facts.artifact).is_err() {
            return Ok(TombstoneDecisionV1::RefusedNotReconstructable);
        }
    }
    Ok(TombstoneDecisionV1::Eligible { reason })
}

pub const fn minimum_retention_tier(role: ArtifactRole) -> RetentionTier {
    match role {
        ArtifactRole::WorldParameters
        | ArtifactRole::RecordedReferenceTrajectory
        | ArtifactRole::UnitNoise
        | ArtifactRole::MissingnessMask
        | ArtifactRole::ContaminationMask
        | ArtifactRole::TrainingScale
        | ArtifactRole::SplitDefinition
        | ArtifactRole::ForecastOriginPlan
        | ArtifactRole::ReconstructionManifest
        | ArtifactRole::DensePointForecast
        | ArtifactRole::LegacyPointForecast
        | ArtifactRole::CompactMetrics
        | ArtifactRole::InvocationReceipt
        | ArtifactRole::ExecutionEnvironment
        | ArtifactRole::CompiledExperiment
        | ArtifactRole::ScoreReceipt
        | ArtifactRole::AnalysisReport
        | ArtifactRole::HorizonEndResponse
        | ArtifactRole::PostprocessSpec
        | ArtifactRole::Metrics => RetentionTier::PermanentCompact,
        ArtifactRole::QuantileForecast
        | ArtifactRole::PredictiveSamples
        | ArtifactRole::RawEvaluationTensors
        | ArtifactRole::RawForecast
        | ArtifactRole::SequenceForecast
        | ArtifactRole::ReleaseMetricsTable => RetentionTier::ReleasePinned,
        ArtifactRole::CampaignMetricsTable => RetentionTier::DisposableOperational,
        ArtifactRole::ModelCheckpoint => RetentionTier::BestCheckpoint,
        ArtifactRole::LatentCleanTrajectory
        | ArtifactRole::FinalNoisyInput
        | ArtifactRole::DgpBank
        | ArtifactRole::MaterializedCell => RetentionTier::ColdReconstructable,
        ArtifactRole::Diagnostics | ArtifactRole::WorkerLog | ArtifactRole::TrainingWorkerRequest => {
            RetentionTier::DisposableOperational
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct LegacyArtifactFactsV1 {
    pub checked_dense_point: Option<ArtifactReference>,
    pub checked_raw_forecast: Option<ArtifactReference>,
    pub reconstruction_adapter: Option<ImplementationDigest>,
    pub checked_summary: Option<ArtifactReference>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "class", rename_all = "snake_case", deny_unknown_fields)]
pub enum LegacyArtifactClassV1 {
    CanonicalPointAvailable {
        point: ArtifactReference,
    },
    RawPointReconstructable {
        raw_forecast: ArtifactReference,
        adapter: ImplementationDigest,
    },
    SummaryOnlyLegacy {
        summary: ArtifactReference,
    },
    Unusable {
        reason: String,
    },
}

/// Classify historical output without rewriting it or pretending a scalar
/// summary can reconstruct a dense forecast.
pub fn classify_legacy_artifacts(facts: LegacyArtifactFactsV1) -> LegacyArtifactClassV1 {
    if let Some(point) = facts.checked_dense_point {
        if point.role == ArtifactRole::DensePointForecast {
            return LegacyArtifactClassV1::CanonicalPointAvailable { point };
        }
        return LegacyArtifactClassV1::Unusable {
            reason: "checked_dense_point has the wrong artifact role".to_string(),
        };
    }
    match (facts.checked_raw_forecast, facts.reconstruction_adapter) {
        (Some(raw_forecast), Some(adapter))
            if matches!(
                raw_forecast.role,
                ArtifactRole::QuantileForecast | ArtifactRole::PredictiveSamples
            ) =>
        {
            return LegacyArtifactClassV1::RawPointReconstructable { raw_forecast, adapter };
        }
        (Some(_), Some(_)) => {
            return LegacyArtifactClassV1::Unusable {
                reason: "raw reconstruction source has no typed forecast role".to_string(),
            };
        }
        (Some(_), None) | (None, Some(_)) => {
            return LegacyArtifactClassV1::Unusable {
                reason: "raw reconstruction requires both a checked source and versioned adapter".to_string(),
            };
        }
        (None, None) => {}
    }
    if let Some(summary) = facts.checked_summary {
        if summary.role == ArtifactRole::CompactMetrics {
            return LegacyArtifactClassV1::SummaryOnlyLegacy { summary };
        }
        return LegacyArtifactClassV1::Unusable {
            reason: "checked_summary has the wrong artifact role".to_string(),
        };
    }
    LegacyArtifactClassV1::Unusable {
        reason: "no checked dense point, reconstructable raw forecast, or checked summary exists".to_string(),
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum TensorDtype {
    Float32,
    Float64,
    Int64,
    Uint8,
    Bool,
}

impl TensorDtype {
    const fn byte_width(self) -> u64 {
        match self {
            Self::Float32 => 4,
            Self::Float64 | Self::Int64 => 8,
            Self::Uint8 | Self::Bool => 1,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum TensorByteOrder {
    LittleEndian,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum TensorStorageOrder {
    RowMajor,
}

/// One named tensor dimension. Names are explicit so `[W,H,D]` cannot be read
/// as `[H,W,D]` merely because both shapes happen to fit.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct TensorAxis {
    pub name: String,
    pub length: u64,
}

impl TensorAxis {
    pub fn try_new(name: impl Into<String>, length: u64) -> Result<Self, ContractError> {
        let axis = Self {
            name: name.into(),
            length,
        };
        axis.validate()?;
        Ok(axis)
    }

    fn validate(&self) -> Result<(), ContractError> {
        let mut chars = self.name.chars();
        let valid_name = chars.next().is_some_and(|first| first.is_ascii_lowercase())
            && chars.all(|character| character.is_ascii_lowercase() || character.is_ascii_digit() || character == '_');
        if !valid_name {
            return Err(ContractError::new(
                "tensor_layout.axes.name",
                format!("axis {:?} must be lower_snake_case", self.name),
            ));
        }
        if self.length == 0 {
            return Err(ContractError::new(
                "tensor_layout.axes.length",
                format!("axis {:?} must have a positive length", self.name),
            ));
        }
        Ok(())
    }
}

/// Exact semantic layout used when hashing canonical tensor values.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct TensorLayout {
    pub schema: String,
    pub dtype: TensorDtype,
    pub byte_order: TensorByteOrder,
    pub storage_order: TensorStorageOrder,
    pub axes: Vec<TensorAxis>,
}

impl TensorLayout {
    pub fn validate(&self) -> Result<(), ContractError> {
        if self.schema != TENSOR_LAYOUT_SCHEMA {
            return Err(ContractError::new(
                "tensor_layout.schema",
                format!("expected {TENSOR_LAYOUT_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        if self.axes.is_empty() {
            return Err(ContractError::new(
                "tensor_layout.axes",
                "must contain at least one axis",
            ));
        }
        let mut names = HashSet::new();
        for axis in &self.axes {
            axis.validate()?;
            if !names.insert(axis.name.as_str()) {
                return Err(ContractError::new(
                    "tensor_layout.axes",
                    format!("duplicate axis {:?}", axis.name),
                ));
            }
        }
        self.expected_payload_bytes()?;
        Ok(())
    }

    pub fn expected_payload_bytes(&self) -> Result<u64, ContractError> {
        let elements = self.axes.iter().try_fold(1_u64, |count, axis| {
            count
                .checked_mul(axis.length)
                .ok_or_else(|| ContractError::new("tensor_layout.axes", "shape product overflows u64"))
        })?;
        elements
            .checked_mul(self.dtype.byte_width())
            .ok_or_else(|| ContractError::new("tensor_layout.dtype", "payload byte count overflows u64"))
    }

    pub fn scientific_digest(&self, canonical_payload: &[u8]) -> Result<ScientificContentDigest, ContractError> {
        self.validate()?;
        let expected = self.expected_payload_bytes()?;
        let observed = u64::try_from(canonical_payload.len())
            .map_err(|_| ContractError::new("canonical_payload", "payload length does not fit u64"))?;
        if observed != expected {
            return Err(ContractError::new(
                "canonical_payload",
                format!("expected {expected} bytes from the declared tensor layout, found {observed}"),
            ));
        }
        let metadata = serde_json::to_vec(self)
            .map_err(|error| ContractError::new("tensor_layout", format!("cannot canonicalize layout: {error}")))?;
        let mut digest = Sha256::new();
        digest.update(b"fern.tensor.content.v1\0");
        digest.update(metadata);
        digest.update(b"\0");
        digest.update(canonical_payload);
        Ok(ScientificContentDigest(format!("sha256:{:x}", digest.finalize())))
    }
}

/// A committed artifact reference uses logical identity and scientific content,
/// never an absolute filesystem path.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ArtifactReference {
    pub artifact_key: ArtifactKey,
    pub role: ArtifactRole,
    pub content_digest: ScientificContentDigest,
}

/// Both transport identity and scientific identity for one produced file.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ReportedArtifactV1 {
    pub schema: String,
    pub artifact_key: ArtifactKey,
    pub role: ArtifactRole,
    pub media_type: String,
    pub byte_count: u64,
    pub byte_digest: ByteDigest,
    pub content_digest: ScientificContentDigest,
    pub tensor_layout: Option<TensorLayout>,
    pub reported_forecast_meaning: Option<ReportedForecastMeaningV1>,
}

/// One reported artifact whose stored bytes and typed canonical content were
/// independently reopened and checked by Fern.
///
/// Fields are private so parsing or constructing a [`ReportedArtifactV1`]
/// cannot manufacture this stronger trust stage.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CheckedArtifactV1 {
    reported_artifact: ReportedArtifactV1,
}

impl CheckedArtifactV1 {
    pub fn reported_artifact(&self) -> &ReportedArtifactV1 {
        &self.reported_artifact
    }
}

impl ReportedArtifactV1 {
    /// Build both identities from the bytes the parent actually observed.
    ///
    /// `stored_bytes` are the complete file bytes used to detect transport or
    /// storage changes. `canonical_content_bytes` are the typed reader's
    /// canonical values: for a tensor they exclude container metadata and are
    /// interpreted through `tensor_layout`; for JSON/IPC the caller first
    /// canonicalizes the parsed scientific content. This keeps the worker from
    /// choosing either digest by assertion alone.
    pub fn from_payloads(
        artifact_key: ArtifactKey,
        role: ArtifactRole,
        media_type: impl Into<String>,
        stored_bytes: &[u8],
        canonical_content_bytes: &[u8],
        tensor_layout: Option<TensorLayout>,
        reported_forecast_meaning: Option<ReportedForecastMeaningV1>,
    ) -> Result<Self, ContractError> {
        let media_type = media_type.into();
        let content_digest = scientific_artifact_content_digest(
            role,
            &media_type,
            tensor_layout.as_ref(),
            reported_forecast_meaning.as_ref(),
            canonical_content_bytes,
        )?;
        let reported_artifact = Self {
            schema: REPORTED_ARTIFACT_SCHEMA.to_string(),
            artifact_key,
            role,
            media_type,
            byte_count: u64::try_from(stored_bytes.len())
                .map_err(|_| ContractError::new("artifact.byte_count", "file length does not fit u64"))?,
            byte_digest: ByteDigest::from_bytes(stored_bytes),
            content_digest,
            tensor_layout,
            reported_forecast_meaning,
        };
        reported_artifact.validate()?;
        Ok(reported_artifact)
    }

    /// Logical identity used by receipts that name an input without repeating
    /// the full reported envelope.
    pub fn artifact_reference(&self) -> ArtifactReference {
        ArtifactReference {
            artifact_key: self.artifact_key.clone(),
            role: self.role,
            content_digest: self.content_digest.clone(),
        }
    }

    /// Recompute both identities instead of trusting the worker report.
    pub fn check_reopened_payloads(
        &self,
        stored_bytes: &[u8],
        canonical_content_bytes: &[u8],
    ) -> Result<CheckedArtifactV1, ContractError> {
        self.validate()?;
        let observed_count = u64::try_from(stored_bytes.len())
            .map_err(|_| ContractError::new("artifact.byte_count", "file length does not fit u64"))?;
        if self.byte_count != observed_count {
            return Err(ContractError::new(
                "artifact.byte_count",
                format!("report says {}, observed {observed_count}", self.byte_count),
            ));
        }
        let observed_byte_digest = ByteDigest::from_bytes(stored_bytes);
        if self.byte_digest != observed_byte_digest {
            return Err(ContractError::new(
                "artifact.byte_digest",
                "stored file bytes do not match the reported artifact",
            ));
        }
        let observed_content_digest = scientific_artifact_content_digest(
            self.role,
            &self.media_type,
            self.tensor_layout.as_ref(),
            self.reported_forecast_meaning.as_ref(),
            canonical_content_bytes,
        )?;
        if self.content_digest != observed_content_digest {
            return Err(ContractError::new(
                "artifact.content_digest",
                "canonical scientific content does not match the reported artifact",
            ));
        }
        Ok(CheckedArtifactV1 {
            reported_artifact: self.clone(),
        })
    }

    /// Recompute the scientific-content identity from typed, canonical values.
    ///
    /// This is deliberately narrower than [`Self::check_reopened_payloads`]:
    /// the checked-commit boundary has already reopened the stored file and
    /// verified its byte identity. A later derived-artifact builder uses this
    /// method to prove that the typed value it embeds still comes from that
    /// exact checked artifact rather than from a second, unrelated input.
    pub fn verify_canonical_content(&self, canonical_content_bytes: &[u8]) -> Result<(), ContractError> {
        self.validate()?;
        let observed_content_digest = scientific_artifact_content_digest(
            self.role,
            &self.media_type,
            self.tensor_layout.as_ref(),
            self.reported_forecast_meaning.as_ref(),
            canonical_content_bytes,
        )?;
        if self.content_digest != observed_content_digest {
            return Err(ContractError::new(
                "artifact.content_digest",
                "canonical scientific content does not match the checked artifact",
            ));
        }
        Ok(())
    }

    pub fn validate(&self) -> Result<(), ContractError> {
        if self.schema != REPORTED_ARTIFACT_SCHEMA {
            return Err(ContractError::new(
                "artifact.schema",
                format!("expected {REPORTED_ARTIFACT_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        if self.media_type.trim().is_empty() {
            return Err(ContractError::new("artifact.media_type", "must be non-empty"));
        }
        if self.byte_count == 0 {
            return Err(ContractError::new("artifact.byte_count", "must be positive"));
        }
        if let Some(layout) = &self.tensor_layout {
            layout.validate()?;
        }
        if matches!(
            self.role,
            ArtifactRole::DensePointForecast | ArtifactRole::QuantileForecast | ArtifactRole::PredictiveSamples
        ) && self.tensor_layout.is_none()
        {
            return Err(ContractError::new(
                "artifact.tensor_layout",
                format!("{} requires an explicit tensor layout", self.role.as_str()),
            ));
        }
        validate_canonical_forecast_layout(self.role, self.tensor_layout.as_ref(), "artifact.tensor_layout")?;
        validate_reported_forecast_meaning(
            self.role,
            self.tensor_layout.as_ref(),
            self.reported_forecast_meaning.as_ref(),
            "artifact.reported_forecast_meaning",
        )?;
        Ok(())
    }
}

/// One verified worker output inside an Attempt-owned committed directory.
///
/// `relative_path` is transport metadata, not artifact identity. The identity
/// remains [`ArtifactKey`] plus the two digests on `artifact`. Keeping the path
/// relative lets a complete Attempt directory move between machines without
/// changing its scientific meaning.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct CommittedArtifactV1 {
    artifact: ReportedArtifactV1,
    relative_path: String,
}

impl CommittedArtifactV1 {
    pub fn from_checked(checked: CheckedArtifactV1, relative_path: impl Into<String>) -> Result<Self, ContractError> {
        let committed = Self {
            artifact: checked.reported_artifact,
            relative_path: relative_path.into(),
        };
        committed.validate()?;
        Ok(committed)
    }

    pub fn artifact(&self) -> &ReportedArtifactV1 {
        &self.artifact
    }

    pub fn relative_path(&self) -> &str {
        &self.relative_path
    }

    pub fn validate(&self) -> Result<(), ContractError> {
        self.artifact.validate()?;
        validate_relative_artifact_path(&self.relative_path)
    }
}

fn validate_relative_artifact_path(path: &str) -> Result<(), ContractError> {
    if path.is_empty() || path.starts_with('/') || path.starts_with('\\') || path.contains('\\') {
        return Err(ContractError::new(
            "attempt_artifact_commit.outputs.relative_path",
            "must be a non-empty portable relative path using forward slashes",
        ));
    }
    if path
        .split('/')
        .any(|component| component.is_empty() || component == "." || component == ".." || component.contains(':'))
    {
        return Err(ContractError::new(
            "attempt_artifact_commit.outputs.relative_path",
            "must not contain empty, dot, parent, or drive-qualified components",
        ));
    }
    Ok(())
}

/// Immutable record that one exact Attempt produced and verified one exact
/// set of files. It is still only a candidate artifact commit: Pass 7 decides
/// whether the candidate belongs to an evidence release.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct AttemptArtifactCommitV1 {
    schema: String,
    attempt_id: String,
    run_hash: String,
    request_digest: ScientificContentDigest,
    result_digest: ScientificContentDigest,
    outputs: Vec<CommittedArtifactV1>,
}

impl fern_json_contract::JsonContract for AttemptArtifactCommitV1 {
    const SCHEMA_ID: &'static str = ATTEMPT_ARTIFACT_COMMIT_SCHEMA;
}

impl AttemptArtifactCommitV1 {
    pub fn from_checked_outputs(
        request: &WorkerRequestV1,
        result: &WorkerResultReportV1,
        outputs: Vec<CommittedArtifactV1>,
    ) -> Result<Self, ContractError> {
        let commit = Self {
            schema: ATTEMPT_ARTIFACT_COMMIT_SCHEMA.to_string(),
            attempt_id: request.attempt_id.clone(),
            run_hash: request.run_hash.clone(),
            request_digest: request.digest()?,
            result_digest: result.digest_against(request)?,
            outputs,
        };
        commit.validate_against(request, result)?;
        Ok(commit)
    }

    pub fn outputs(&self) -> &[CommittedArtifactV1] {
        &self.outputs
    }

    pub fn validate_against(
        &self,
        request: &WorkerRequestV1,
        result: &WorkerResultReportV1,
    ) -> Result<(), ContractError> {
        result.validate_against(request)?;
        if self.schema != ATTEMPT_ARTIFACT_COMMIT_SCHEMA {
            return Err(ContractError::new(
                "attempt_artifact_commit.schema",
                format!("expected {ATTEMPT_ARTIFACT_COMMIT_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        if self.attempt_id != request.attempt_id || self.run_hash != request.run_hash {
            return Err(ContractError::new(
                "attempt_artifact_commit.identity",
                "Attempt and run must match the worker request",
            ));
        }
        if self.request_digest != request.digest()? {
            return Err(ContractError::new(
                "attempt_artifact_commit.request_digest",
                "does not match the worker request",
            ));
        }
        if self.result_digest != result.digest_against(request)? {
            return Err(ContractError::new(
                "attempt_artifact_commit.result_digest",
                "does not match the worker result's reported artifact",
            ));
        }

        let result_outputs = result
            .outputs
            .iter()
            .map(|artifact| (&artifact.artifact_key, artifact))
            .collect::<std::collections::HashMap<_, _>>();
        let mut committed_keys = HashSet::new();
        let mut relative_paths = HashSet::new();
        for output in &self.outputs {
            output.validate()?;
            if !committed_keys.insert(&output.artifact.artifact_key) {
                return Err(ContractError::new(
                    "attempt_artifact_commit.outputs.artifact_key",
                    "contains a duplicate artifact key",
                ));
            }
            if !relative_paths.insert(output.relative_path.as_str()) {
                return Err(ContractError::new(
                    "attempt_artifact_commit.outputs.relative_path",
                    "contains a duplicate relative path",
                ));
            }
            if result_outputs.get(&output.artifact.artifact_key) != Some(&&output.artifact) {
                return Err(ContractError::new(
                    "attempt_artifact_commit.outputs",
                    format!(
                        "artifact {:?} does not exactly match the worker result",
                        output.artifact.artifact_key.as_str()
                    ),
                ));
            }
        }
        if committed_keys.len() != result_outputs.len() {
            return Err(ContractError::new(
                "attempt_artifact_commit.outputs",
                "must commit every output in the worker result exactly once",
            ));
        }
        Ok(())
    }

    pub fn digest_against(
        &self,
        request: &WorkerRequestV1,
        result: &WorkerResultReportV1,
    ) -> Result<ScientificContentDigest, ContractError> {
        self.validate_against(request, result)?;
        let mut canonical = self.clone();
        canonical
            .outputs
            .sort_by(|left, right| left.artifact.artifact_key.cmp(&right.artifact.artifact_key));
        let bytes = serde_json::to_vec(&canonical).map_err(|error| {
            ContractError::new(
                "attempt_artifact_commit",
                format!("cannot canonicalize commit: {error}"),
            )
        })?;
        Ok(domain_digest(b"fern.attempt_artifact_commit.v1\0", &bytes))
    }
}

fn scientific_artifact_content_digest(
    role: ArtifactRole,
    media_type: &str,
    tensor_layout: Option<&TensorLayout>,
    reported_forecast_meaning: Option<&ReportedForecastMeaningV1>,
    canonical_content_bytes: &[u8],
) -> Result<ScientificContentDigest, ContractError> {
    match tensor_layout {
        Some(layout) => {
            let tensor_digest = layout.scientific_digest(canonical_content_bytes)?;
            let mut digest = Sha256::new();
            digest.update(b"fern.tensor.artifact.content.v1\0");
            digest.update(role.as_str().as_bytes());
            digest.update(b"\0");
            digest.update(media_type.as_bytes());
            digest.update(b"\0");
            digest.update(tensor_digest.as_str().as_bytes());
            if let Some(reported_meaning) = reported_forecast_meaning {
                let bytes = serde_json::to_vec(reported_meaning).map_err(|error| {
                    ContractError::new(
                        "artifact.reported_forecast_meaning",
                        format!("cannot canonicalize reported forecast meaning: {error}"),
                    )
                })?;
                digest.update(b"\0");
                digest.update(bytes);
            }
            Ok(ScientificContentDigest(format!("sha256:{:x}", digest.finalize())))
        }
        None => {
            if canonical_content_bytes.is_empty() {
                return Err(ContractError::new(
                    "canonical_content_bytes",
                    "non-tensor scientific content must be non-empty",
                ));
            }
            let mut digest = Sha256::new();
            digest.update(b"fern.artifact.content.v1\0");
            digest.update(role.as_str().as_bytes());
            digest.update(b"\0");
            digest.update(media_type.as_bytes());
            digest.update(b"\0");
            digest.update(canonical_content_bytes);
            Ok(ScientificContentDigest(format!("sha256:{:x}", digest.finalize())))
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum CaseOperation {
    ScaleFromTrain,
    InjectNoise,
    ApplyMissingnessMask,
    ApplyContaminationMask,
    Clip,
    Interpolate,
    Window,
}

impl CaseOperation {
    const fn as_str(self) -> &'static str {
        match self {
            Self::ScaleFromTrain => "scale_from_train",
            Self::InjectNoise => "inject_noise",
            Self::ApplyMissingnessMask => "apply_missingness_mask",
            Self::ApplyContaminationMask => "apply_contamination_mask",
            Self::Clip => "clip",
            Self::Interpolate => "interpolate",
            Self::Window => "window",
        }
    }
}

/// Shared case material. Dose is deliberately absent: all doses can reference
/// the same clean trajectory, unit-noise stream, masks, split, and forecast-origin plan.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ScientificCaseManifest {
    pub schema: String,
    pub case_id: ScientificCaseId,
    pub scientific_target_hash: ScientificContentDigest,
    pub world_parameters: ArtifactReference,
    /// The undosed reference is either constructed latent truth or the
    /// recorded sensor series to which Fern adds controlled corruption.
    pub reference_trajectory: ArtifactReference,
    pub unit_noise: ArtifactReference,
    pub training_scale: ArtifactReference,
    pub split_definition: ArtifactReference,
    pub forecast_origin_plan: ArtifactReference,
    pub masks: Vec<ArtifactReference>,
    pub pipeline_implementation: ImplementationDigest,
    pub channel_order: Vec<String>,
    pub operation_order: Vec<CaseOperation>,
}

impl ScientificCaseManifest {
    pub fn validate(&self) -> Result<(), ContractError> {
        if self.schema != SCIENTIFIC_CASE_SCHEMA {
            return Err(ContractError::new(
                "scientific_case.schema",
                format!("expected {SCIENTIFIC_CASE_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        let mut artifact_keys = HashSet::new();
        for (field, artifact, role) in [
            (
                "world_parameters",
                &self.world_parameters,
                ArtifactRole::WorldParameters,
            ),
            ("unit_noise", &self.unit_noise, ArtifactRole::UnitNoise),
            ("training_scale", &self.training_scale, ArtifactRole::TrainingScale),
            (
                "split_definition",
                &self.split_definition,
                ArtifactRole::SplitDefinition,
            ),
            (
                "forecast_origin_plan",
                &self.forecast_origin_plan,
                ArtifactRole::ForecastOriginPlan,
            ),
        ] {
            if artifact.role != role {
                return Err(ContractError::new(
                    format!("scientific_case.{field}"),
                    format!(
                        "must reference role {}, found {}",
                        role.as_str(),
                        artifact.role.as_str()
                    ),
                ));
            }
            if !artifact_keys.insert(&artifact.artifact_key) {
                return Err(ContractError::new(
                    format!("scientific_case.{field}.artifact_key"),
                    format!(
                        "artifact key {:?} is reused by more than one case role",
                        artifact.artifact_key.as_str()
                    ),
                ));
            }
        }
        if !matches!(
            self.reference_trajectory.role,
            ArtifactRole::LatentCleanTrajectory | ArtifactRole::RecordedReferenceTrajectory
        ) {
            return Err(ContractError::new(
                "scientific_case.reference_trajectory",
                "must reference latent_clean_trajectory or recorded_reference_trajectory",
            ));
        }
        if !artifact_keys.insert(&self.reference_trajectory.artifact_key) {
            return Err(ContractError::new(
                "scientific_case.reference_trajectory.artifact_key",
                format!(
                    "artifact key {:?} is reused by more than one case role",
                    self.reference_trajectory.artifact_key.as_str()
                ),
            ));
        }
        let mut mask_roles = HashSet::new();
        for mask in &self.masks {
            if !matches!(
                mask.role,
                ArtifactRole::MissingnessMask | ArtifactRole::ContaminationMask
            ) {
                return Err(ContractError::new(
                    "scientific_case.masks",
                    format!("role {} is not a mask", mask.role.as_str()),
                ));
            }
            if !mask_roles.insert(mask.role) {
                return Err(ContractError::new(
                    "scientific_case.masks",
                    format!("duplicate {} role", mask.role.as_str()),
                ));
            }
            if !artifact_keys.insert(&mask.artifact_key) {
                return Err(ContractError::new(
                    "scientific_case.masks.artifact_key",
                    format!(
                        "artifact key {:?} is reused by more than one case role",
                        mask.artifact_key.as_str()
                    ),
                ));
            }
        }
        if self.channel_order.is_empty() {
            return Err(ContractError::new(
                "scientific_case.channel_order",
                "must name at least one channel",
            ));
        }
        let mut channels = HashSet::new();
        for channel in &self.channel_order {
            if channel.trim().is_empty() || channel != channel.trim() {
                return Err(ContractError::new(
                    "scientific_case.channel_order",
                    "channel names must be non-empty and contain no surrounding whitespace",
                ));
            }
            if !channels.insert(channel.as_str()) {
                return Err(ContractError::new(
                    "scientific_case.channel_order",
                    format!("duplicate channel {channel:?}"),
                ));
            }
        }
        let mut operations = HashSet::new();
        for operation in &self.operation_order {
            if !operations.insert(*operation) {
                return Err(ContractError::new(
                    "scientific_case.operation_order",
                    format!("duplicate operation {}", operation.as_str()),
                ));
            }
        }
        let scale_position = self
            .operation_order
            .iter()
            .position(|operation| *operation == CaseOperation::ScaleFromTrain)
            .ok_or_else(|| ContractError::new("scientific_case.operation_order", "must contain scale_from_train"))?;
        let noise_position = self
            .operation_order
            .iter()
            .position(|operation| *operation == CaseOperation::InjectNoise)
            .ok_or_else(|| ContractError::new("scientific_case.operation_order", "must contain inject_noise"))?;
        if scale_position > noise_position {
            return Err(ContractError::new(
                "scientific_case.operation_order",
                "scale_from_train must occur before inject_noise",
            ));
        }
        if self.operation_order.last() != Some(&CaseOperation::Window) {
            return Err(ContractError::new(
                "scientific_case.operation_order",
                "window must be the final declared operation",
            ));
        }
        if mask_roles.contains(&ArtifactRole::MissingnessMask)
            && !operations.contains(&CaseOperation::ApplyMissingnessMask)
        {
            return Err(ContractError::new(
                "scientific_case.operation_order",
                "a missingness_mask artifact requires apply_missingness_mask",
            ));
        }
        if operations.contains(&CaseOperation::ApplyMissingnessMask)
            && !mask_roles.contains(&ArtifactRole::MissingnessMask)
        {
            return Err(ContractError::new(
                "scientific_case.operation_order",
                "apply_missingness_mask is declared but no missingness_mask artifact exists",
            ));
        }
        if mask_roles.contains(&ArtifactRole::ContaminationMask)
            && !operations.contains(&CaseOperation::ApplyContaminationMask)
        {
            return Err(ContractError::new(
                "scientific_case.operation_order",
                "a contamination_mask artifact requires apply_contamination_mask",
            ));
        }
        if operations.contains(&CaseOperation::ApplyContaminationMask)
            && !mask_roles.contains(&ArtifactRole::ContaminationMask)
        {
            return Err(ContractError::new(
                "scientific_case.operation_order",
                "apply_contamination_mask is declared but no contamination_mask artifact exists",
            ));
        }
        Ok(())
    }

    pub fn manifest_digest(&self) -> Result<ScientificContentDigest, ContractError> {
        self.validate()?;
        let mut canonical = self.clone();
        canonical
            .masks
            .sort_by(|left, right| (left.role, &left.artifact_key).cmp(&(right.role, &right.artifact_key)));
        let bytes = serde_json::to_vec(&canonical)
            .map_err(|error| ContractError::new("scientific_case", format!("cannot canonicalize manifest: {error}")))?;
        Ok(domain_digest(b"fern.scientific_case.v1\0", &bytes))
    }
}

/// Exact, canonical non-negative ratio used for a dose such as 1/8.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, schemars::JsonSchema)]
pub struct ExactRatio {
    pub numerator: u64,
    pub denominator: u64,
}

impl ExactRatio {
    pub fn new(numerator: u64, denominator: u64) -> Result<Self, ContractError> {
        if denominator == 0 {
            return Err(ContractError::new("dose.denominator", "must be positive"));
        }
        let divisor = gcd(numerator, denominator);
        Ok(Self {
            numerator: numerator / divisor,
            denominator: denominator / divisor,
        })
    }
}

impl<'de> Deserialize<'de> for ExactRatio {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        #[derive(Deserialize)]
        #[serde(deny_unknown_fields)]
        struct Wire {
            numerator: u64,
            denominator: u64,
        }

        let wire = Wire::deserialize(deserializer)?;
        let canonical = Self::new(wire.numerator, wire.denominator).map_err(serde::de::Error::custom)?;
        if canonical.numerator != wire.numerator || canonical.denominator != wire.denominator {
            return Err(serde::de::Error::custom(format!(
                "dose ratio must be reduced; use {}/{}",
                canonical.numerator, canonical.denominator
            )));
        }
        Ok(canonical)
    }
}

fn gcd(mut left: u64, mut right: u64) -> u64 {
    while right != 0 {
        let remainder = left % right;
        left = right;
        right = remainder;
    }
    left.max(1)
}

/// One dose-specific realization of shared case material.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct CaseRealization {
    pub schema: String,
    pub dose: ExactRatio,
    pub final_input_content_digest: ScientificContentDigest,
    pub retained_final_input: Option<ArtifactReference>,
}

impl CaseRealization {
    pub fn validate(&self) -> Result<(), ContractError> {
        if self.schema != CASE_REALIZATION_SCHEMA {
            return Err(ContractError::new(
                "case_realization.schema",
                format!("expected {CASE_REALIZATION_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        if let Some(retained) = &self.retained_final_input {
            if retained.role != ArtifactRole::FinalNoisyInput {
                return Err(ContractError::new(
                    "case_realization.retained_final_input",
                    "must reference role final_noisy_input",
                ));
            }
            if retained.content_digest != self.final_input_content_digest {
                return Err(ContractError::new(
                    "case_realization.retained_final_input",
                    "content digest must match final_input_content_digest",
                ));
            }
        }
        Ok(())
    }

    pub fn digest(&self) -> Result<ScientificContentDigest, ContractError> {
        self.validate()?;
        #[derive(Serialize)]
        struct CaseRealizationIdentity<'a> {
            schema: &'a str,
            dose: ExactRatio,
            final_input_content_digest: &'a ScientificContentDigest,
        }
        let identity = CaseRealizationIdentity {
            schema: &self.schema,
            dose: self.dose,
            final_input_content_digest: &self.final_input_content_digest,
        };
        let bytes = serde_json::to_vec(&identity).map_err(|error| {
            ContractError::new("case_realization", format!("cannot canonicalize realization: {error}"))
        })?;
        Ok(domain_digest(b"fern.case_realization.v1\0", &bytes))
    }

    /// Verify a replayed final model input against the digest captured when
    /// the case realization was created.  Serie owns the actual operation
    /// sequence; this boundary proves that replay produced the same typed
    /// tensor instead of trusting a successful generator exit code.
    pub fn verify_reconstructed_final_input(
        &self,
        layout: &TensorLayout,
        canonical_payload: &[u8],
    ) -> Result<(), ContractError> {
        self.validate()?;
        let observed = scientific_artifact_content_digest(
            ArtifactRole::FinalNoisyInput,
            "application/x-safetensors",
            Some(layout),
            None,
            canonical_payload,
        )?;
        if observed != self.final_input_content_digest {
            return Err(ContractError::new(
                "case_realization.final_input_content_digest",
                "reconstructed final input does not match the generation-time scientific digest",
            ));
        }
        Ok(())
    }
}

fn domain_digest(domain: &[u8], bytes: &[u8]) -> ScientificContentDigest {
    let mut digest = Sha256::new();
    digest.update(domain);
    digest.update(bytes);
    ScientificContentDigest(format!("sha256:{:x}", digest.finalize()))
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ScientificCaseReference {
    pub case_id: ScientificCaseId,
    pub manifest_digest: ScientificContentDigest,
}

/// One explicit forecast origin. Intervals are zero-based and half-open.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ForecastOriginV1 {
    pub origin_id: String,
    pub origin_index: u64,
    pub history_start: u64,
    pub history_end: u64,
    pub forecast_start: u64,
    pub forecast_end: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ForecastOriginPlanV1 {
    pub schema: String,
    /// Stable owner of these origins.  This is deliberately only the logical
    /// case ID: the ScientificCase manifest contains this plan's artifact
    /// digest, so embedding the manifest digest here would create an
    /// impossible circular hash dependency.
    pub case_id: ScientificCaseId,
    pub context_length: u64,
    pub horizon_length: u64,
    pub stride: u64,
    pub origins: Vec<ForecastOriginV1>,
}

impl ForecastOriginPlanV1 {
    pub fn validate(&self) -> Result<(), ContractError> {
        if self.schema != FORECAST_ORIGIN_PLAN_SCHEMA {
            return Err(ContractError::new(
                "forecast_origin_plan.schema",
                format!("expected {FORECAST_ORIGIN_PLAN_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        if self.context_length == 0 || self.horizon_length == 0 || self.stride == 0 || self.origins.is_empty() {
            return Err(ContractError::new(
                "forecast_origin_plan",
                "requires positive context/horizon lengths, a positive stride, and at least one origin",
            ));
        }
        let mut ids = HashSet::new();
        for (expected_index, origin) in self.origins.iter().enumerate() {
            validate_logical_id("forecast_origin_plan.origin_id", &origin.origin_id)?;
            if !ids.insert(origin.origin_id.as_str()) {
                return Err(ContractError::new(
                    "forecast_origin_plan.origin_id",
                    "contains a duplicate origin_id",
                ));
            }
            if origin.origin_index != expected_index as u64 {
                return Err(ContractError::new(
                    "forecast_origin_plan.origin_index",
                    "must be contiguous, ordered, and zero-based",
                ));
            }
            if origin.history_start >= origin.history_end
                || origin.forecast_start >= origin.forecast_end
                || origin.history_end > origin.forecast_start
            {
                return Err(ContractError::new(
                    "forecast_origin_plan.intervals",
                    "requires non-empty half-open history/forecast intervals with history ending before forecast",
                ));
            }
            if origin.history_end - origin.history_start != self.context_length
                || origin.forecast_end - origin.forecast_start != self.horizon_length
            {
                return Err(ContractError::new(
                    "forecast_origin_plan.intervals",
                    "every origin must match the plan's one declared history length and forecast horizon",
                ));
            }
            if let Some(previous) = expected_index.checked_sub(1).map(|index| &self.origins[index])
                && (origin.history_start.checked_sub(previous.history_start) != Some(self.stride)
                    || origin.forecast_start.checked_sub(previous.forecast_start) != Some(self.stride))
            {
                return Err(ContractError::new(
                    "forecast_origin_plan.stride",
                    "adjacent history and forecast starts must advance by the declared stride",
                ));
            }
        }
        Ok(())
    }

    pub fn digest(&self) -> Result<ScientificContentDigest, ContractError> {
        self.validate()?;
        let bytes = serde_json::to_vec(self).map_err(|error| {
            ContractError::new("forecast_origin_plan", format!("cannot canonicalize plan: {error}"))
        })?;
        Ok(domain_digest(b"fern.forecast_origin_plan.v1\0", &bytes))
    }

    /// Check that a forecast tensor's named axes implement this exact map.
    ///
    /// V1 maps one ScientificCase at a time. Merely carrying the map's artifact
    /// reference in forecast metadata is not enough: the reopened map must agree
    /// with the declared case, number of forecast origins, and horizon length.
    pub fn check_forecast_layout(
        &self,
        scientific_cases: &[ScientificCaseReference],
        layout: &TensorLayout,
    ) -> Result<(), ContractError> {
        self.validate()?;
        layout.validate()?;
        if scientific_cases.len() != 1 || scientific_cases[0].case_id != self.case_id {
            return Err(ContractError::new(
                "forecast_origin_plan.case_id",
                "reopened origin plan does not name the forecast tensor's one declared ScientificCase",
            ));
        }
        for (name, expected) in [
            ("case", 1_u64),
            ("forecast_origin", self.origins.len() as u64),
            ("horizon", self.horizon_length),
        ] {
            let observed = layout
                .axes
                .iter()
                .find(|axis| axis.name == name)
                .map(|axis| axis.length)
                .ok_or_else(|| {
                    ContractError::new(
                        format!("forecast_origin_plan.tensor_layout.{name}"),
                        format!("forecast tensor is missing the {name:?} axis required by its origin plan"),
                    )
                })?;
            if observed != expected {
                return Err(ContractError::new(
                    format!("forecast_origin_plan.tensor_layout.{name}"),
                    format!("origin plan requires axis length {expected}, found {observed}"),
                ));
            }
        }
        Ok(())
    }
}

/// Durable authority identity only. Capability secrets are deliberately absent
/// from the serializable worker contract.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct WorkerAuthorityReference {
    pub coordinator_lease_id: String,
    pub coordinator_epoch: i64,
    pub coordinate_lease_id: String,
    pub coordinate_epoch: i64,
    pub resource_lease_id: String,
    pub resource_epoch: i64,
}

impl WorkerAuthorityReference {
    fn validate(&self) -> Result<(), ContractError> {
        for (field, value) in [
            ("coordinator_lease_id", self.coordinator_lease_id.as_str()),
            ("coordinate_lease_id", self.coordinate_lease_id.as_str()),
            ("resource_lease_id", self.resource_lease_id.as_str()),
        ] {
            validate_logical_id(&format!("authority.{field}"), value)?;
        }
        for (field, epoch) in [
            ("coordinator_epoch", self.coordinator_epoch),
            ("coordinate_epoch", self.coordinate_epoch),
            ("resource_epoch", self.resource_epoch),
        ] {
            if epoch <= 0 {
                return Err(ContractError::new(format!("authority.{field}"), "must be positive"));
            }
        }
        Ok(())
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum OutputDisposition {
    Required,
    Optional,
    Omitted { reason: String },
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct OutputContract {
    pub role: ArtifactRole,
    /// Logical identity fixed before launch. Omitted outputs have no key
    /// because no artifact is allowed to appear for them.
    pub artifact_key: Option<ArtifactKey>,
    pub disposition: OutputDisposition,
    pub tensor_layout: Option<TensorLayout>,
    pub forecast_contract: Option<DeclaredForecastOutputV1>,
}

impl OutputContract {
    pub fn required(artifact_key: ArtifactKey, role: ArtifactRole, tensor_layout: Option<TensorLayout>) -> Self {
        Self {
            role,
            artifact_key: Some(artifact_key),
            disposition: OutputDisposition::Required,
            tensor_layout,
            forecast_contract: None,
        }
    }

    pub fn required_forecast(
        artifact_key: ArtifactKey,
        role: ArtifactRole,
        tensor_layout: TensorLayout,
        forecast_contract: DeclaredForecastOutputV1,
    ) -> Self {
        Self {
            role,
            artifact_key: Some(artifact_key),
            disposition: OutputDisposition::Required,
            tensor_layout: Some(tensor_layout),
            forecast_contract: Some(forecast_contract),
        }
    }

    pub fn omitted(role: ArtifactRole, reason: impl Into<String>) -> Self {
        Self {
            role,
            artifact_key: None,
            disposition: OutputDisposition::Omitted { reason: reason.into() },
            tensor_layout: None,
            forecast_contract: None,
        }
    }

    fn validate(&self) -> Result<(), ContractError> {
        if let Some(layout) = &self.tensor_layout {
            layout.validate()?;
        }
        if let OutputDisposition::Omitted { reason } = &self.disposition {
            if reason.trim().is_empty() {
                return Err(ContractError::new(
                    "worker_request.outputs.disposition.reason",
                    format!("{} omission requires a reason", self.role.as_str()),
                ));
            }
            if self.tensor_layout.is_some() {
                return Err(ContractError::new(
                    "worker_request.outputs.tensor_layout",
                    format!("{} is omitted and cannot declare a produced layout", self.role.as_str()),
                ));
            }
            if self.artifact_key.is_some() {
                return Err(ContractError::new(
                    "worker_request.outputs.artifact_key",
                    format!("{} is omitted and cannot reserve an artifact key", self.role.as_str()),
                ));
            }
            if self.forecast_contract.is_some() {
                return Err(ContractError::new(
                    "worker_request.outputs.forecast_contract",
                    format!(
                        "{} is omitted and cannot declare a forecast contract",
                        self.role.as_str()
                    ),
                ));
            }
        } else {
            if self.artifact_key.is_none() {
                return Err(ContractError::new(
                    "worker_request.outputs.artifact_key",
                    format!("{} must reserve one artifact key before launch", self.role.as_str()),
                ));
            }
            validate_canonical_forecast_layout(
                self.role,
                self.tensor_layout.as_ref(),
                "worker_request.outputs.tensor_layout",
            )?;
            validate_forecast_contract(
                self.role,
                self.tensor_layout.as_ref(),
                self.forecast_contract.as_ref(),
                "worker_request.outputs.forecast_contract",
            )?;
        }
        Ok(())
    }
}

fn validate_forecast_contract(
    role: ArtifactRole,
    layout: Option<&TensorLayout>,
    contract: Option<&DeclaredForecastOutputV1>,
    field: &str,
) -> Result<(), ContractError> {
    if canonical_tensor_axes(role).is_some() {
        let layout = layout.ok_or_else(|| ContractError::new(field, "forecast contract requires a tensor layout"))?;
        let contract = contract.ok_or_else(|| {
            ContractError::new(field, format!("{} requires a typed forecast contract", role.as_str()))
        })?;
        contract.validate_for(role, layout)
    } else if contract.is_some() {
        Err(ContractError::new(
            field,
            format!(
                "{} is not a forecast tensor and cannot carry a forecast contract",
                role.as_str()
            ),
        ))
    } else {
        Ok(())
    }
}

fn validate_reported_forecast_meaning(
    role: ArtifactRole,
    layout: Option<&TensorLayout>,
    reported_meaning: Option<&ReportedForecastMeaningV1>,
    field: &str,
) -> Result<(), ContractError> {
    if canonical_tensor_axes(role).is_some() {
        let layout =
            layout.ok_or_else(|| ContractError::new(field, "reported forecast meaning requires a tensor layout"))?;
        let reported_meaning = reported_meaning.ok_or_else(|| {
            ContractError::new(
                field,
                format!("{} requires typed reported forecast meaning", role.as_str()),
            )
        })?;
        reported_meaning.validate_for(role, layout)
    } else if reported_meaning.is_some() {
        Err(ContractError::new(
            field,
            format!(
                "{} is not a forecast tensor and cannot carry reported forecast meaning",
                role.as_str()
            ),
        ))
    } else {
        Ok(())
    }
}

fn canonical_tensor_axes(role: ArtifactRole) -> Option<&'static [&'static str]> {
    match role {
        ArtifactRole::DensePointForecast => Some(&["case", "forecast_origin", "horizon", "channel"]),
        ArtifactRole::QuantileForecast => Some(&["case", "forecast_origin", "horizon", "channel", "quantile"]),
        ArtifactRole::PredictiveSamples => Some(&["case", "forecast_origin", "sample", "horizon", "channel"]),
        _ => None,
    }
}

fn validate_canonical_forecast_layout(
    role: ArtifactRole,
    layout: Option<&TensorLayout>,
    field: &str,
) -> Result<(), ContractError> {
    let Some(expected_axes) = canonical_tensor_axes(role) else {
        return Ok(());
    };
    let layout =
        layout.ok_or_else(|| ContractError::new(field, format!("{} requires a tensor layout", role.as_str())))?;
    let observed_axes = layout.axes.iter().map(|axis| axis.name.as_str()).collect::<Vec<_>>();
    if observed_axes.as_slice() != expected_axes {
        return Err(ContractError::new(
            field,
            format!(
                "{} requires canonical axes {:?}, found {:?}",
                role.as_str(),
                expected_axes,
                observed_axes
            ),
        ));
    }
    Ok(())
}

/// Versioned, immutable worker intent. It contains no filesystem paths and no
/// capability secret; live adapters add transport details outside this digest.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct WorkerRequestV1 {
    pub schema: String,
    pub matrix_id: String,
    pub coordinate_id: String,
    pub attempt_id: String,
    pub run_hash: String,
    pub scientific_case: ScientificCaseReference,
    pub realization: CaseRealization,
    /// What model is being built. This is distinct from `implementation`,
    /// which identifies the code/numerical realization of that architecture.
    pub model_architecture: ModelArchitectureHash,
    /// Registered code/dependency/numerical realization of `model_architecture`.
    pub implementation: ModelImplementationHash,
    pub execution_profile_revision: String,
    pub authority: WorkerAuthorityReference,
    /// Predeclared metrics and sensitivity checks.  This belongs to the
    /// request so a worker cannot choose targets or thresholds after seeing
    /// its predictions.
    pub metric_contract: MetricContractV1,
    pub outputs: Vec<OutputContract>,
}

impl fern_json_contract::JsonContract for WorkerRequestV1 {
    const SCHEMA_ID: &'static str = WORKER_REQUEST_SCHEMA;
}

impl WorkerRequestV1 {
    pub fn validate(&self) -> Result<(), ContractError> {
        if self.schema != WORKER_REQUEST_SCHEMA {
            return Err(ContractError::new(
                "worker_request.schema",
                format!("expected {WORKER_REQUEST_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        for (field, value) in [
            ("matrix_id", self.matrix_id.as_str()),
            ("coordinate_id", self.coordinate_id.as_str()),
            ("attempt_id", self.attempt_id.as_str()),
            ("run_hash", self.run_hash.as_str()),
        ] {
            validate_logical_id(&format!("worker_request.{field}"), value)?;
        }
        self.realization.validate()?;
        self.authority.validate()?;
        self.metric_contract.validate()?;
        validate_sha256_text(
            "worker_request.execution_profile_revision",
            &self.execution_profile_revision,
        )?;
        if self.outputs.is_empty() {
            return Err(ContractError::new(
                "worker_request.outputs",
                "must declare every required, optional, or intentionally omitted output",
            ));
        }
        let mut roles = HashSet::new();
        let mut artifact_keys = HashSet::new();
        for output in &self.outputs {
            output.validate()?;
            if let Some(forecast_contract) = &output.forecast_contract
                && forecast_contract.scientific_cases.as_slice() != std::slice::from_ref(&self.scientific_case)
            {
                return Err(ContractError::new(
                    "worker_request.outputs.forecast_contract.scientific_cases",
                    format!(
                        "{} must describe the request's one ScientificCase",
                        output.role.as_str()
                    ),
                ));
            }
            if let Some(DeclaredForecastOutputV1 {
                meaning:
                    DeclaredForecastMeaningV1::DensePoint {
                        derivation:
                            DeclaredPointDerivationV1::NativePointHead {
                                producer_implementation,
                            },
                        ..
                    },
                ..
            }) = &output.forecast_contract
                && producer_implementation != &self.implementation
            {
                return Err(ContractError::new(
                    "worker_request.outputs.forecast_contract.producer_implementation",
                    "native point output must name the request's registered model implementation",
                ));
            }
            if !roles.insert(output.role) {
                return Err(ContractError::new(
                    "worker_request.outputs",
                    format!("duplicate {} contract", output.role.as_str()),
                ));
            }
            if let Some(artifact_key) = &output.artifact_key
                && !artifact_keys.insert(artifact_key)
            {
                return Err(ContractError::new(
                    "worker_request.outputs.artifact_key",
                    format!("duplicate predeclared artifact key {:?}", artifact_key.as_str()),
                ));
            }
        }
        for required in [
            ArtifactRole::CompactMetrics,
            ArtifactRole::InvocationReceipt,
            ArtifactRole::ExecutionEnvironment,
            ArtifactRole::DensePointForecast,
        ] {
            let contract = self
                .outputs
                .iter()
                .find(|output| output.role == required)
                .ok_or_else(|| {
                    ContractError::new("worker_request.outputs", format!("must declare {}", required.as_str()))
                })?;
            if !matches!(contract.disposition, OutputDisposition::Required) {
                return Err(ContractError::new(
                    "worker_request.outputs",
                    format!("{} must be retained as a required output", required.as_str()),
                ));
            }
        }
        for output in &self.outputs {
            let Some(DeclaredForecastOutputV1 {
                meaning: DeclaredForecastMeaningV1::DensePoint { derivation, .. },
                ..
            }) = &output.forecast_contract
            else {
                continue;
            };
            let Some(source_key) = derivation.source_artifact_key() else {
                continue;
            };
            let source_role = derivation
                .source_role()
                .expect("a planned source key always has a source role");
            let source = self
                .outputs
                .iter()
                .find(|candidate| candidate.artifact_key.as_ref() == Some(source_key))
                .ok_or_else(|| {
                    ContractError::new(
                        "worker_request.outputs.forecast_contract",
                        format!(
                            "derived point source artifact {:?} is not declared as an output",
                            source_key.as_str()
                        ),
                    )
                })?;
            if source.role != source_role || !matches!(source.disposition, OutputDisposition::Required) {
                return Err(ContractError::new(
                    "worker_request.outputs.forecast_contract",
                    format!(
                        "derived point source {:?} must be required as {}",
                        source_key.as_str(),
                        source_role.as_str()
                    ),
                ));
            }
        }
        Ok(())
    }

    pub fn digest(&self) -> Result<ScientificContentDigest, ContractError> {
        self.validate()?;
        let mut canonical = self.clone();
        canonical.outputs.sort_by_key(|output| output.role);
        let bytes = serde_json::to_vec(&canonical)
            .map_err(|error| ContractError::new("worker_request", format!("cannot canonicalize request: {error}")))?;
        Ok(domain_digest(b"fern.worker_request.v1\0", &bytes))
    }
}

/// Successful worker handoff. This is a candidate report, not an artifact
/// seal, acceptance decision, or evidence release.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct WorkerResultReportV1 {
    pub schema: String,
    pub attempt_id: String,
    pub run_hash: String,
    pub request_digest: ScientificContentDigest,
    pub outputs: Vec<ReportedArtifactV1>,
}

impl fern_json_contract::JsonContract for WorkerResultReportV1 {
    const SCHEMA_ID: &'static str = WORKER_RESULT_REPORT_SCHEMA;
}

fn find_reported_point_source<'a>(
    reported_meaning: &ReportedForecastMeaningV1,
    outputs: &'a [ReportedArtifactV1],
) -> Option<&'a ReportedArtifactV1> {
    let ForecastMeaningV1::DensePoint { derivation, .. } = &reported_meaning.meaning else {
        return None;
    };
    let source_key = match derivation {
        ReportedPointDerivationV1::NativePointHead { .. } => return None,
        ReportedPointDerivationV1::PredictiveSampleReduction { source, .. }
        | ReportedPointDerivationV1::ExactQuantileSelection { source, .. } => &source.artifact_key,
    };
    outputs.iter().find(|candidate| &candidate.artifact_key == source_key)
}

impl WorkerResultReportV1 {
    pub fn validate_against(&self, request: &WorkerRequestV1) -> Result<(), ContractError> {
        request.validate()?;
        if self.schema != WORKER_RESULT_REPORT_SCHEMA {
            return Err(ContractError::new(
                "worker_result.schema",
                format!("expected {WORKER_RESULT_REPORT_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        if self.attempt_id != request.attempt_id {
            return Err(ContractError::new(
                "worker_result.attempt_id",
                format!("expected {:?}, found {:?}", request.attempt_id, self.attempt_id),
            ));
        }
        if self.run_hash != request.run_hash {
            return Err(ContractError::new(
                "worker_result.run_hash",
                format!("expected {:?}, found {:?}", request.run_hash, self.run_hash),
            ));
        }
        let expected_request_digest = request.digest()?;
        if self.request_digest != expected_request_digest {
            return Err(ContractError::new(
                "worker_result.request_digest",
                "does not match the declared worker request",
            ));
        }
        let contracts = request
            .outputs
            .iter()
            .map(|contract| (contract.role, contract))
            .collect::<std::collections::HashMap<_, _>>();
        let mut observed_roles = HashSet::new();
        let mut observed_keys = HashSet::new();
        for output in &self.outputs {
            let contract = contracts.get(&output.role).ok_or_else(|| {
                ContractError::new(
                    "worker_result.outputs",
                    format!("{} was not declared by the request", output.role.as_str()),
                )
            })?;
            if matches!(contract.disposition, OutputDisposition::Omitted { .. }) {
                return Err(ContractError::new(
                    "worker_result.outputs",
                    format!(
                        "{} was declared omitted but the result contains it",
                        output.role.as_str()
                    ),
                ));
            }
            if !observed_roles.insert(output.role) {
                return Err(ContractError::new(
                    "worker_result.outputs",
                    format!("duplicate reported {} artifact", output.role.as_str()),
                ));
            }
            if !observed_keys.insert(&output.artifact_key) {
                return Err(ContractError::new(
                    "worker_result.outputs.artifact_key",
                    format!(
                        "artifact key {:?} is reused for more than one output role",
                        output.artifact_key.as_str()
                    ),
                ));
            }
            output.validate()?;
            if contract.artifact_key.as_ref() != Some(&output.artifact_key) {
                return Err(ContractError::new(
                    "worker_result.outputs.artifact_key",
                    format!("{} must use the predeclared artifact key", output.role.as_str()),
                ));
            }
            if contract.tensor_layout != output.tensor_layout {
                return Err(ContractError::new(
                    "worker_result.outputs.tensor_layout",
                    format!("{} does not match the requested layout", output.role.as_str()),
                ));
            }
            if let Some(forecast_contract) = &contract.forecast_contract {
                let layout = output.tensor_layout.as_ref().ok_or_else(|| {
                    ContractError::new(
                        "worker_result.outputs.tensor_layout",
                        format!("{} is missing its requested layout", output.role.as_str()),
                    )
                })?;
                let reported_meaning = output.reported_forecast_meaning.as_ref().ok_or_else(|| {
                    ContractError::new(
                        "worker_result.outputs.reported_forecast_meaning",
                        format!("{} is missing reported forecast meaning", output.role.as_str()),
                    )
                })?;
                let source = find_reported_point_source(reported_meaning, &self.outputs);
                forecast_contract.matches_reported(output.role, layout, reported_meaning, source)?;
            }
        }
        for contract in &request.outputs {
            if matches!(contract.disposition, OutputDisposition::Required) && !observed_roles.contains(&contract.role) {
                return Err(ContractError::new(
                    "worker_result.outputs",
                    format!("missing required {}", contract.role.as_str()),
                ));
            }
        }
        Ok(())
    }

    /// Canonical identity of the worker's declared result, after proving that
    /// it answers this exact request. Output order is not scientific, so two
    /// workers listing the same artifacts in a different JSON order retain
    /// one result identity.
    pub fn digest_against(&self, request: &WorkerRequestV1) -> Result<ScientificContentDigest, ContractError> {
        self.validate_against(request)?;
        let mut canonical = self.clone();
        canonical
            .outputs
            .sort_by(|left, right| left.artifact_key.cmp(&right.artifact_key));
        let bytes = serde_json::to_vec(&canonical).map_err(|error| {
            ContractError::new("worker_result", format!("cannot canonicalize worker result: {error}"))
        })?;
        Ok(domain_digest(b"fern.worker_result_report.v1\0", &bytes))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn controlled_linear_horizon_response_is_a_closed_artifact_role() {
        let encoded = serde_json::to_string(&ArtifactRole::HorizonEndResponse).unwrap();
        assert_eq!(encoded, "\"horizon_end_response\"");
        assert_eq!(
            serde_json::from_str::<ArtifactRole>(&encoded).unwrap(),
            ArtifactRole::HorizonEndResponse
        );
        assert_eq!(ArtifactRole::HorizonEndResponse.as_str(), "horizon_end_response");
    }

    fn digest(byte: u8) -> ScientificContentDigest {
        ScientificContentDigest::from_bytes(&[byte])
    }

    fn model_implementation() -> ModelImplementationHash {
        ModelImplementationHash::parse(format!("implementation:sha256:{}", "44".repeat(32))).unwrap()
    }

    fn artifact_ref(role: ArtifactRole, byte: u8) -> ArtifactReference {
        ArtifactReference {
            artifact_key: ArtifactKey::parse(format!("artifact:fixture:{byte}")).unwrap(),
            role,
            content_digest: digest(byte),
        }
    }

    fn point_layout() -> TensorLayout {
        TensorLayout {
            schema: TENSOR_LAYOUT_SCHEMA.to_string(),
            dtype: TensorDtype::Float32,
            byte_order: TensorByteOrder::LittleEndian,
            storage_order: TensorStorageOrder::RowMajor,
            axes: vec![
                TensorAxis::try_new("case", 1).unwrap(),
                TensorAxis::try_new("forecast_origin", 3).unwrap(),
                TensorAxis::try_new("horizon", 4).unwrap(),
                TensorAxis::try_new("channel", 1).unwrap(),
            ],
        }
    }

    fn point_reported_meaning() -> ReportedForecastMeaningV1 {
        ReportedForecastMeaningV1 {
            schema: REPORTED_FORECAST_MEANING_SCHEMA.to_string(),
            scientific_cases: vec![ScientificCaseReference {
                case_id: ScientificCaseId::parse("case:rossler:draw-0").unwrap(),
                manifest_digest: case_manifest().manifest_digest().unwrap(),
            }],
            forecast_origin_plan: artifact_ref(ArtifactRole::ForecastOriginPlan, 7),
            channel_order: vec!["x".to_string()],
            value_space: ForecastValueSpace::Physical,
            scale_reference: None,
            meaning: ForecastMeaningV1::DensePoint {
                point_kind: PointKind::NativePointHead,
                derivation: ReportedPointDerivationV1::NativePointHead {
                    producer_implementation: model_implementation(),
                },
            },
        }
    }

    fn point_contract() -> DeclaredForecastOutputV1 {
        let reported_meaning = point_reported_meaning();
        DeclaredForecastOutputV1 {
            schema: DECLARED_FORECAST_OUTPUT_SCHEMA.to_string(),
            scientific_cases: reported_meaning.scientific_cases,
            forecast_origin_plan: reported_meaning.forecast_origin_plan,
            channel_order: reported_meaning.channel_order,
            value_space: reported_meaning.value_space,
            scale_reference: reported_meaning.scale_reference,
            meaning: DeclaredForecastMeaningV1::DensePoint {
                point_kind: PointKind::NativePointHead,
                derivation: DeclaredPointDerivationV1::NativePointHead {
                    producer_implementation: model_implementation(),
                },
            },
        }
    }

    fn sample_layout() -> TensorLayout {
        TensorLayout {
            schema: TENSOR_LAYOUT_SCHEMA.to_string(),
            dtype: TensorDtype::Float32,
            byte_order: TensorByteOrder::LittleEndian,
            storage_order: TensorStorageOrder::RowMajor,
            axes: vec![
                TensorAxis::try_new("case", 1).unwrap(),
                TensorAxis::try_new("forecast_origin", 3).unwrap(),
                TensorAxis::try_new("sample", 2).unwrap(),
                TensorAxis::try_new("horizon", 4).unwrap(),
                TensorAxis::try_new("channel", 1).unwrap(),
            ],
        }
    }

    fn sample_contract() -> DeclaredForecastOutputV1 {
        let point = point_reported_meaning();
        DeclaredForecastOutputV1 {
            schema: DECLARED_FORECAST_OUTPUT_SCHEMA.to_string(),
            scientific_cases: point.scientific_cases,
            forecast_origin_plan: point.forecast_origin_plan,
            channel_order: point.channel_order,
            value_space: point.value_space,
            scale_reference: point.scale_reference,
            meaning: DeclaredForecastMeaningV1::PredictiveSamples {
                sample_count: 2,
                sampling_contract_version: 1,
            },
        }
    }

    fn sample_reported_meaning() -> ReportedForecastMeaningV1 {
        let point = point_reported_meaning();
        ReportedForecastMeaningV1 {
            schema: REPORTED_FORECAST_MEANING_SCHEMA.to_string(),
            scientific_cases: point.scientific_cases,
            forecast_origin_plan: point.forecast_origin_plan,
            channel_order: point.channel_order,
            value_space: point.value_space,
            scale_reference: point.scale_reference,
            meaning: ForecastMeaningV1::PredictiveSamples {
                sample_count: 2,
                sampling_contract_version: 1,
            },
        }
    }

    fn output_key(byte: u8) -> ArtifactKey {
        ArtifactKey::parse(format!("artifact:output:{byte}")).unwrap()
    }

    fn case_manifest() -> ScientificCaseManifest {
        ScientificCaseManifest {
            schema: SCIENTIFIC_CASE_SCHEMA.to_string(),
            case_id: ScientificCaseId::parse("case:rossler:draw-0").unwrap(),
            scientific_target_hash: digest(1),
            world_parameters: artifact_ref(ArtifactRole::WorldParameters, 2),
            reference_trajectory: artifact_ref(ArtifactRole::LatentCleanTrajectory, 3),
            unit_noise: artifact_ref(ArtifactRole::UnitNoise, 4),
            training_scale: artifact_ref(ArtifactRole::TrainingScale, 5),
            split_definition: artifact_ref(ArtifactRole::SplitDefinition, 6),
            forecast_origin_plan: artifact_ref(ArtifactRole::ForecastOriginPlan, 7),
            masks: vec![artifact_ref(ArtifactRole::ContaminationMask, 8)],
            pipeline_implementation: ImplementationDigest::from_bytes(b"serie-rs-v1"),
            channel_order: vec!["x".to_string(), "y".to_string(), "z".to_string()],
            operation_order: vec![
                CaseOperation::ScaleFromTrain,
                CaseOperation::InjectNoise,
                CaseOperation::ApplyContaminationMask,
                CaseOperation::Window,
            ],
        }
    }

    fn request() -> WorkerRequestV1 {
        let case = case_manifest();
        WorkerRequestV1 {
            schema: WORKER_REQUEST_SCHEMA.to_string(),
            matrix_id: "matrix-1".to_string(),
            coordinate_id: "coordinate-1".to_string(),
            attempt_id: "attempt-1".to_string(),
            run_hash: "run-1".to_string(),
            scientific_case: ScientificCaseReference {
                case_id: case.case_id.clone(),
                manifest_digest: case.manifest_digest().unwrap(),
            },
            realization: CaseRealization {
                schema: CASE_REALIZATION_SCHEMA.to_string(),
                dose: ExactRatio::new(1, 8).unwrap(),
                final_input_content_digest: digest(9),
                retained_final_input: None,
            },
            model_architecture: ModelArchitectureHash::parse(format!("architecture:sha256:{}", "11".repeat(32)))
                .unwrap(),
            implementation: model_implementation(),
            execution_profile_revision: ByteDigest::from_bytes(b"profile").as_str().to_string(),
            authority: WorkerAuthorityReference {
                coordinator_lease_id: "coordinator-lease".to_string(),
                coordinator_epoch: 4,
                coordinate_lease_id: "coordinate-lease".to_string(),
                coordinate_epoch: 7,
                resource_lease_id: "resource-lease".to_string(),
                resource_epoch: 2,
            },
            metric_contract: MetricContractV1::time_series_v1(vec![MetricTargetKind::ObservedCorrupted]).unwrap(),
            outputs: vec![
                OutputContract::required(output_key(20), ArtifactRole::CompactMetrics, None),
                OutputContract::required(output_key(21), ArtifactRole::InvocationReceipt, None),
                OutputContract::required(output_key(22), ArtifactRole::ExecutionEnvironment, None),
                OutputContract::required_forecast(
                    output_key(23),
                    ArtifactRole::DensePointForecast,
                    point_layout(),
                    point_contract(),
                ),
                OutputContract::omitted(
                    ArtifactRole::QuantileForecast,
                    "this point-only model does not emit forecast quantiles",
                ),
            ],
        }
    }

    fn reported_artifact(role: ArtifactRole, byte: u8, layout: Option<TensorLayout>) -> ReportedArtifactV1 {
        ReportedArtifactV1 {
            schema: REPORTED_ARTIFACT_SCHEMA.to_string(),
            artifact_key: output_key(byte),
            role,
            media_type: if layout.is_some() {
                "application/x-safetensors".to_string()
            } else {
                "application/json".to_string()
            },
            byte_count: 1,
            byte_digest: ByteDigest::from_bytes(&[byte]),
            content_digest: digest(byte),
            tensor_layout: layout,
            reported_forecast_meaning: (role == ArtifactRole::DensePointForecast).then(point_reported_meaning),
        }
    }

    #[test]
    fn artifact_keys_and_digests_reject_paths_and_malformed_values() {
        assert!(ArtifactKey::parse("artifact:sha256:abc").is_ok());
        assert!(ArtifactKey::parse("/tmp/result").is_err());
        assert!(ArtifactKey::parse("artifact:../result").is_err());
        assert!(ByteDigest::parse("not-a-digest").is_err());
        assert!(ScientificContentDigest::parse("sha256:1234").is_err());
    }

    #[test]
    fn tensor_layout_refuses_duplicate_axes_zero_dimensions_and_wrong_payload_size() {
        let mut duplicate = point_layout();
        duplicate.axes[1] = TensorAxis::try_new("case", 3).unwrap();
        assert!(duplicate.validate().unwrap_err().to_string().contains("duplicate axis"));

        let mut zero = point_layout();
        zero.axes[0].length = 0;
        assert!(zero.validate().unwrap_err().to_string().contains("positive"));

        assert!(point_layout().scientific_digest(&[0; 47]).is_err());
        assert!(point_layout().scientific_digest(&[0; 48]).is_ok());
    }

    #[test]
    fn scientific_tensor_digest_includes_layout_while_byte_digest_does_not() {
        let payload = vec![0_u8; 48];
        let layout = point_layout();
        let mut transposed = layout.clone();
        transposed.axes.swap(1, 2);

        assert_eq!(ByteDigest::from_bytes(&payload), ByteDigest::from_bytes(&payload));
        assert_ne!(
            layout.scientific_digest(&payload).unwrap(),
            transposed.scientific_digest(&payload).unwrap()
        );
    }

    #[test]
    fn reported_artifact_requires_canonical_forecast_axes() {
        let mut wrong_axes = point_layout();
        wrong_axes.axes.swap(1, 2);
        let forecast = reported_artifact(ArtifactRole::DensePointForecast, 42, Some(wrong_axes));

        assert!(forecast.validate().unwrap_err().to_string().contains("canonical axes"));
    }

    #[test]
    fn case_manifest_separates_shared_case_from_exact_dose_realization() {
        let case = case_manifest();
        assert!(case.validate().is_ok());

        let clean = CaseRealization {
            schema: CASE_REALIZATION_SCHEMA.to_string(),
            dose: ExactRatio::new(0, 1).unwrap(),
            final_input_content_digest: digest(10),
            retained_final_input: None,
        };
        let noisy = CaseRealization {
            dose: ExactRatio::new(1, 8).unwrap(),
            final_input_content_digest: digest(11),
            ..clean.clone()
        };

        assert_eq!(case.case_id, case_manifest().case_id);
        assert_ne!(clean.digest().unwrap(), noisy.digest().unwrap());

        let retained = CaseRealization {
            retained_final_input: Some(ArtifactReference {
                artifact_key: ArtifactKey::parse("case-1-final-input").unwrap(),
                role: ArtifactRole::FinalNoisyInput,
                content_digest: clean.final_input_content_digest.clone(),
            }),
            ..clean.clone()
        };
        assert_eq!(
            clean.digest().unwrap(),
            retained.digest().unwrap(),
            "retaining an audit copy must not change the scientific realization identity"
        );
    }

    #[test]
    fn forecast_origin_plan_names_history_and_forecast_intervals_without_window_guessing() {
        let case = case_manifest();
        let case_reference = ScientificCaseReference {
            case_id: case.case_id.clone(),
            manifest_digest: case.manifest_digest().unwrap(),
        };
        let plan = ForecastOriginPlanV1 {
            schema: FORECAST_ORIGIN_PLAN_SCHEMA.to_string(),
            case_id: case.case_id.clone(),
            context_length: 4,
            horizon_length: 2,
            stride: 2,
            origins: vec![
                ForecastOriginV1 {
                    origin_id: "origin-0".to_string(),
                    origin_index: 0,
                    history_start: 0,
                    history_end: 4,
                    forecast_start: 4,
                    forecast_end: 6,
                },
                ForecastOriginV1 {
                    origin_id: "origin-1".to_string(),
                    origin_index: 1,
                    history_start: 2,
                    history_end: 6,
                    forecast_start: 6,
                    forecast_end: 8,
                },
            ],
        };
        plan.validate().unwrap();
        let layout = TensorLayout {
            schema: TENSOR_LAYOUT_SCHEMA.to_string(),
            dtype: TensorDtype::Float32,
            byte_order: TensorByteOrder::LittleEndian,
            storage_order: TensorStorageOrder::RowMajor,
            axes: [("case", 1), ("forecast_origin", 2), ("horizon", 2), ("channel", 3)]
                .into_iter()
                .map(|(name, length)| TensorAxis::try_new(name, length).unwrap())
                .collect(),
        };
        plan.check_forecast_layout(std::slice::from_ref(&case_reference), &layout)
            .unwrap();
        let mut wrong_origin_count = layout;
        wrong_origin_count
            .axes
            .iter_mut()
            .find(|axis| axis.name == "forecast_origin")
            .unwrap()
            .length = 3;
        assert!(
            plan.check_forecast_layout(std::slice::from_ref(&case_reference), &wrong_origin_count)
                .unwrap_err()
                .to_string()
                .contains("requires axis length 2")
        );

        let mut bad_stride = plan;
        bad_stride.origins[1].forecast_start = 7;
        bad_stride.origins[1].forecast_end = 9;
        assert!(bad_stride.validate().unwrap_err().to_string().contains("stride"));

        let mut ragged_horizon = bad_stride;
        ragged_horizon.origins[1].forecast_start = 6;
        ragged_horizon.origins[1].forecast_end = 9;
        assert!(
            ragged_horizon
                .validate()
                .unwrap_err()
                .to_string()
                .contains("one declared history length and forecast horizon")
        );
    }

    #[test]
    fn forecast_origin_plan_wire_and_active_source_use_the_plain_name_end_to_end() {
        let case = case_manifest();
        let plan = ForecastOriginPlanV1 {
            schema: FORECAST_ORIGIN_PLAN_SCHEMA.to_string(),
            case_id: case.case_id.clone(),
            context_length: 4,
            horizon_length: 2,
            stride: 2,
            origins: vec![ForecastOriginV1 {
                origin_id: "origin-0".to_string(),
                origin_index: 0,
                history_start: 0,
                history_end: 4,
                forecast_start: 4,
                forecast_end: 6,
            }],
        };
        let json = serde_json::to_value(&plan).unwrap();
        assert_eq!(json["schema"], "fern.forecast_origin_plan.v1");
        assert_eq!(json["case_id"], case.case_id.as_str());
        assert!(
            json.get("scientific_case").is_none(),
            "the origin plan must not reintroduce the ScientificCase-manifest digest cycle"
        );
        assert_eq!(json["origins"][0]["history_start"], 0);
        assert_eq!(json["origins"][0]["forecast_end"], 6);

        let active_source = [
            include_str!("lib.rs"),
            include_str!("forecast.rs"),
            include_str!("dense_point.rs"),
            include_str!("native_forecast.rs"),
            include_str!("forecast_policy.rs"),
            include_str!("receipts.rs"),
        ]
        .concat();
        for retired_name in [
            ["ForecastOrigin", "MapV1"].concat(),
            ["FORECAST_ORIGIN_", "MAP_SCHEMA"].concat(),
            ["forecast_origin_", "map"].concat(),
            ["context_", "start"].concat(),
            ["context_", "end"].concat(),
            ["target_", "start"].concat(),
            ["target_", "end"].concat(),
            ["forecast_capability_", "card"].concat(),
            ["forecast_", "requirements"].concat(),
            ["resolved_forecast_", "contract"].concat(),
        ] {
            assert!(
                !active_source.contains(&retired_name),
                "active fern-artifact source still contains retired wire name {retired_name:?}"
            );
        }
        assert_eq!(FORECAST_MENU_SCHEMA, "fern.forecast_menu.v1");
        assert_eq!(FORECAST_ORDER_SCHEMA, "fern.forecast_order.v1");
        assert_eq!(CONFIRMED_FORECAST_ORDER_SCHEMA, "fern.confirmed_forecast_order.v1");
        for retired_identifier in [
            ["ForecastOutput", "ContractV1"].concat(),
            ["ForecastContract", "MeaningV1"].concat(),
            ["PlannedPoint", "DerivationV1"].concat(),
            ["ForecastTensor", "SemanticsV1"].concat(),
            ["Point", "DerivationV1"].concat(),
            ["Quantile", "Projection"].concat(),
            ["Artifact", "Descriptor"].concat(),
            ["WorkerResult", "DescriptorV1"].concat(),
        ] {
            let remains = active_source
                .split(|character: char| !(character.is_ascii_alphanumeric() || character == '_'))
                .any(|token| token == retired_identifier);
            assert!(
                !remains,
                "active fern-artifact source still contains retired Rust name {retired_identifier:?}"
            );
        }
    }

    #[test]
    fn case_reconstruction_checks_typed_final_input_content() {
        let layout = point_layout();
        let payload = vec![0_u8; layout.expected_payload_bytes().unwrap() as usize];
        let expected = scientific_artifact_content_digest(
            ArtifactRole::FinalNoisyInput,
            "application/x-safetensors",
            Some(&layout),
            None,
            &payload,
        )
        .unwrap();
        let realization = CaseRealization {
            schema: CASE_REALIZATION_SCHEMA.to_string(),
            dose: ExactRatio::new(1, 8).unwrap(),
            final_input_content_digest: expected,
            retained_final_input: None,
        };
        realization.verify_reconstructed_final_input(&layout, &payload).unwrap();
        let mut changed = payload;
        changed[0] = 1;
        assert!(realization.verify_reconstructed_final_input(&layout, &changed).is_err());
    }

    #[test]
    fn retention_refuses_permanent_pinned_referenced_and_unreconstructable_artifacts() {
        let artifact = artifact_ref(ArtifactRole::DensePointForecast, 44);
        let permanent = RetentionFactsV1 {
            artifact: artifact.clone(),
            tier: minimum_retention_tier(ArtifactRole::DensePointForecast),
            committed_and_checked: true,
            referenced_by_artifacts: vec![],
            pinned_by_releases: vec![],
            verified_reconstruction: None,
        };
        assert_eq!(
            decide_tombstone(&permanent, "save space").unwrap(),
            TombstoneDecisionV1::RefusedPermanent
        );
        let falsely_downgraded_point = RetentionFactsV1 {
            tier: RetentionTier::DisposableOperational,
            ..permanent.clone()
        };
        assert_eq!(
            decide_tombstone(&falsely_downgraded_point, "caller mislabeled it").unwrap(),
            TombstoneDecisionV1::RefusedPermanent
        );

        let cold = RetentionFactsV1 {
            artifact: ArtifactReference {
                role: ArtifactRole::FinalNoisyInput,
                ..artifact
            },
            tier: RetentionTier::ColdReconstructable,
            committed_and_checked: true,
            referenced_by_artifacts: vec![],
            pinned_by_releases: vec![],
            verified_reconstruction: None,
        };
        assert_eq!(
            decide_tombstone(&cold, "rebuildable").unwrap(),
            TombstoneDecisionV1::RefusedNotReconstructable
        );
        let falsely_downgraded_input = RetentionFactsV1 {
            tier: RetentionTier::DisposableOperational,
            ..cold.clone()
        };
        assert_eq!(
            decide_tombstone(&falsely_downgraded_input, "caller mislabeled it").unwrap(),
            TombstoneDecisionV1::RefusedNotReconstructable
        );
        let unverified_recipe = RetentionFactsV1 {
            verified_reconstruction: Some(VerifiedReconstructionProofV1 {
                schema: VERIFIED_RECONSTRUCTION_PROOF_SCHEMA.to_string(),
                manifest: artifact_ref(ArtifactRole::InvocationReceipt, 45),
                reconstructor_implementation: ImplementationDigest::from_bytes(b"reconstructor-v1"),
                original_content_digest: cold.artifact.content_digest.clone(),
                regenerated_content_digest: cold.artifact.content_digest.clone(),
                equivalence: ReconstructionEquivalenceV1 {
                    absolute_tolerance: 1.0e-6,
                    relative_tolerance: 2.0e-5,
                    maximum_normalized_error: 0.0,
                    compared_value_count: 64,
                },
                verified_at_unix_ms: 1,
            }),
            ..cold.clone()
        };
        assert_eq!(
            decide_tombstone(&unverified_recipe, "rebuildable").unwrap(),
            TombstoneDecisionV1::RefusedNotReconstructable
        );
        let eligible = RetentionFactsV1 {
            verified_reconstruction: Some(VerifiedReconstructionProofV1 {
                schema: VERIFIED_RECONSTRUCTION_PROOF_SCHEMA.to_string(),
                manifest: artifact_ref(ArtifactRole::ReconstructionManifest, 46),
                reconstructor_implementation: ImplementationDigest::from_bytes(b"reconstructor-v1"),
                original_content_digest: cold.artifact.content_digest.clone(),
                regenerated_content_digest: cold.artifact.content_digest.clone(),
                equivalence: ReconstructionEquivalenceV1 {
                    absolute_tolerance: 1.0e-6,
                    relative_tolerance: 2.0e-5,
                    maximum_normalized_error: 0.0,
                    compared_value_count: 64,
                },
                verified_at_unix_ms: 1,
            }),
            ..cold.clone()
        };
        assert!(matches!(
            decide_tombstone(&eligible, "rebuildable").unwrap(),
            TombstoneDecisionV1::Eligible { .. }
        ));

        let tolerance_exact = RetentionFactsV1 {
            artifact: ArtifactReference {
                role: ArtifactRole::LatentCleanTrajectory,
                ..cold.artifact.clone()
            },
            tier: RetentionTier::ColdReconstructable,
            committed_and_checked: true,
            referenced_by_artifacts: vec![],
            pinned_by_releases: vec![],
            verified_reconstruction: Some(VerifiedReconstructionProofV1 {
                schema: VERIFIED_RECONSTRUCTION_PROOF_SCHEMA.to_string(),
                manifest: artifact_ref(ArtifactRole::ReconstructionManifest, 48),
                reconstructor_implementation: ImplementationDigest::from_bytes(b"reconstructor-v1"),
                original_content_digest: cold.artifact.content_digest.clone(),
                regenerated_content_digest: digest(49),
                equivalence: ReconstructionEquivalenceV1 {
                    absolute_tolerance: 1.0e-6,
                    relative_tolerance: 2.0e-5,
                    maximum_normalized_error: 0.75,
                    compared_value_count: 64,
                },
                verified_at_unix_ms: 1,
            }),
        };
        assert_eq!(
            minimum_retention_tier(ArtifactRole::LatentCleanTrajectory),
            RetentionTier::ColdReconstructable
        );
        assert!(matches!(
            decide_tombstone(&tolerance_exact, "rebuilt within the declared tolerance").unwrap(),
            TombstoneDecisionV1::Eligible { .. }
        ));

        let tolerance_miss = RetentionFactsV1 {
            verified_reconstruction: Some(VerifiedReconstructionProofV1 {
                equivalence: ReconstructionEquivalenceV1 {
                    absolute_tolerance: 1.0e-6,
                    relative_tolerance: 2.0e-5,
                    maximum_normalized_error: 1.01,
                    compared_value_count: 64,
                },
                ..tolerance_exact.verified_reconstruction.clone().unwrap()
            }),
            ..tolerance_exact
        };
        assert_eq!(
            decide_tombstone(&tolerance_miss, "outside the declared tolerance").unwrap(),
            TombstoneDecisionV1::RefusedNotReconstructable
        );

        let best_checkpoint = RetentionFactsV1 {
            artifact: artifact_ref(ArtifactRole::ModelCheckpoint, 47),
            tier: RetentionTier::BestCheckpoint,
            committed_and_checked: true,
            referenced_by_artifacts: vec![],
            pinned_by_releases: vec![],
            verified_reconstruction: None,
        };
        assert_eq!(
            decide_tombstone(&best_checkpoint, "save space").unwrap(),
            TombstoneDecisionV1::RefusedBestCheckpoint
        );
    }

    #[test]
    fn legacy_classifier_never_invents_a_point_tensor_from_summary_metrics() {
        let summary = artifact_ref(ArtifactRole::CompactMetrics, 50);
        assert!(matches!(
            classify_legacy_artifacts(LegacyArtifactFactsV1 {
                checked_dense_point: None,
                checked_raw_forecast: None,
                reconstruction_adapter: None,
                checked_summary: Some(summary.clone()),
            }),
            LegacyArtifactClassV1::SummaryOnlyLegacy { .. }
        ));
        assert!(matches!(
            classify_legacy_artifacts(LegacyArtifactFactsV1 {
                checked_dense_point: None,
                checked_raw_forecast: Some(artifact_ref(ArtifactRole::PredictiveSamples, 51)),
                reconstruction_adapter: Some(ImplementationDigest::from_bytes(b"legacy-sample-mean-v1")),
                checked_summary: Some(summary),
            }),
            LegacyArtifactClassV1::RawPointReconstructable { .. }
        ));
    }

    #[test]
    fn case_manifest_rejects_wrong_roles_duplicate_channels_and_bad_operation_order() {
        let mut recorded_case = case_manifest();
        recorded_case.reference_trajectory.role = ArtifactRole::RecordedReferenceTrajectory;
        assert!(recorded_case.validate().is_ok());

        let mut case = case_manifest();
        case.reference_trajectory.role = ArtifactRole::UnitNoise;
        assert!(
            case.validate()
                .unwrap_err()
                .to_string()
                .contains("reference_trajectory")
        );

        let mut case = case_manifest();
        case.channel_order.push("x".to_string());
        assert!(case.validate().unwrap_err().to_string().contains("duplicate channel"));

        let mut case = case_manifest();
        case.operation_order.swap(0, 1);
        assert!(case.validate().unwrap_err().to_string().contains("scale_from_train"));
    }

    #[test]
    fn case_manifest_rejects_reused_artifact_keys_and_unbacked_mask_operations() {
        let mut reused = case_manifest();
        reused.unit_noise.artifact_key = reused.reference_trajectory.artifact_key.clone();
        assert!(reused.validate().unwrap_err().to_string().contains("artifact key"));

        let mut unbacked = case_manifest();
        unbacked
            .operation_order
            .insert(unbacked.operation_order.len() - 1, CaseOperation::ApplyMissingnessMask);
        assert!(
            unbacked
                .validate()
                .unwrap_err()
                .to_string()
                .contains("no missingness_mask")
        );
    }

    #[test]
    fn manifest_digest_changes_when_pipeline_or_operation_order_changes() {
        let case = case_manifest();
        let mut changed_pipeline = case.clone();
        changed_pipeline.pipeline_implementation = ImplementationDigest::from_bytes(b"serie-rs-v2");
        let mut changed_order = case.clone();
        changed_order
            .operation_order
            .insert(changed_order.operation_order.len() - 1, CaseOperation::Clip);

        assert_ne!(
            case.manifest_digest().unwrap(),
            changed_pipeline.manifest_digest().unwrap()
        );
        assert_ne!(
            case.manifest_digest().unwrap(),
            changed_order.manifest_digest().unwrap()
        );
    }

    #[test]
    fn unordered_reference_sets_have_one_manifest_and_request_identity() {
        let case = case_manifest();
        let mut reordered_masks = case.clone();
        reordered_masks.masks = vec![
            artifact_ref(ArtifactRole::MissingnessMask, 12),
            artifact_ref(ArtifactRole::ContaminationMask, 8),
        ];
        reordered_masks
            .operation_order
            .insert(3, CaseOperation::ApplyMissingnessMask);
        let mut reverse_masks = reordered_masks.clone();
        reverse_masks.masks.reverse();
        assert_eq!(
            reordered_masks.manifest_digest().unwrap(),
            reverse_masks.manifest_digest().unwrap()
        );

        let request = request();
        let mut reversed_outputs = request.clone();
        reversed_outputs.outputs.reverse();
        assert_eq!(request.digest().unwrap(), reversed_outputs.digest().unwrap());
    }

    #[test]
    fn exact_ratio_has_one_canonical_representation() {
        assert_eq!(ExactRatio::new(2, 16).unwrap(), ExactRatio::new(1, 8).unwrap());
        assert!(ExactRatio::new(1, 0).is_err());

        let noncanonical = r#"{"numerator":2,"denominator":16}"#;
        assert!(serde_json::from_str::<ExactRatio>(noncanonical).is_err());
    }

    #[test]
    fn worker_request_requires_three_authorities_and_declares_point_forecast_retention() {
        let request = request();
        assert!(request.validate().is_ok());

        let mut no_resource_authority = request.clone();
        no_resource_authority.authority.resource_lease_id.clear();
        assert!(no_resource_authority.validate().is_err());

        let mut no_point_forecast_decision = request.clone();
        no_point_forecast_decision
            .outputs
            .retain(|output| output.role != ArtifactRole::DensePointForecast);
        assert!(
            no_point_forecast_decision
                .validate()
                .unwrap_err()
                .to_string()
                .contains("point_forecast")
        );

        let mut wrong_case = request;
        wrong_case
            .outputs
            .iter_mut()
            .find(|output| output.role == ArtifactRole::DensePointForecast)
            .unwrap()
            .forecast_contract
            .as_mut()
            .unwrap()
            .scientific_cases[0] = ScientificCaseReference {
            case_id: ScientificCaseId::parse("case:other").unwrap(),
            manifest_digest: digest(99),
        };
        assert!(
            wrong_case
                .validate()
                .unwrap_err()
                .to_string()
                .contains("request's one ScientificCase")
        );
    }

    #[test]
    fn native_point_contract_must_reference_the_requests_implementation_card() {
        let mut request = request();
        let point = request
            .outputs
            .iter_mut()
            .find(|output| output.role == ArtifactRole::DensePointForecast)
            .unwrap()
            .forecast_contract
            .as_mut()
            .unwrap();
        let DeclaredForecastMeaningV1::DensePoint {
            derivation:
                DeclaredPointDerivationV1::NativePointHead {
                    producer_implementation,
                },
            ..
        } = &mut point.meaning
        else {
            panic!("fixture must use a native point head")
        };
        *producer_implementation =
            ModelImplementationHash::parse(format!("implementation:sha256:{}", "55".repeat(32))).unwrap();

        assert!(
            request
                .validate()
                .unwrap_err()
                .to_string()
                .contains("request's registered model implementation")
        );
    }

    #[test]
    fn tensor_output_contract_requires_its_canonical_axes() {
        let mut no_layout = request();
        no_layout
            .outputs
            .iter_mut()
            .find(|output| output.role == ArtifactRole::DensePointForecast)
            .unwrap()
            .tensor_layout = None;
        assert!(no_layout.validate().unwrap_err().to_string().contains("tensor layout"));

        let mut wrong_axes = request();
        wrong_axes
            .outputs
            .iter_mut()
            .find(|output| output.role == ArtifactRole::DensePointForecast)
            .unwrap()
            .tensor_layout
            .as_mut()
            .unwrap()
            .axes
            .swap(1, 2);
        assert!(
            wrong_axes
                .validate()
                .unwrap_err()
                .to_string()
                .contains("canonical axes")
        );
    }

    #[test]
    fn worker_request_rejects_a_non_digest_profile_revision() {
        let mut request = request();
        request.execution_profile_revision = "local-4070".to_string();
        assert!(
            request
                .validate()
                .unwrap_err()
                .to_string()
                .contains("execution_profile_revision")
        );
    }

    #[test]
    fn worker_request_rejects_path_shaped_authority_and_entity_ids() {
        let mut bad_matrix = request();
        bad_matrix.matrix_id = "../matrix".to_string();
        assert!(bad_matrix.validate().unwrap_err().to_string().contains("matrix_id"));

        let mut bad_lease = request();
        bad_lease.authority.resource_lease_id = "/tmp/lease".to_string();
        assert!(
            bad_lease
                .validate()
                .unwrap_err()
                .to_string()
                .contains("resource_lease_id")
        );
    }

    #[test]
    fn worker_request_json_rejects_capability_secrets_and_unknown_fields() {
        let mut value = serde_json::to_value(request()).unwrap();
        value["authority"]["coordinate_capability"] = serde_json::Value::String("secret".to_string());
        assert!(serde_json::from_value::<WorkerRequestV1>(value).is_err());
    }

    #[test]
    fn observed_payloads_separate_file_bytes_from_scientific_content() {
        let compact_json = br#"{"mse":1.25}"#;
        let pretty_json = b"{\n  \"mse\": 1.25\n}\n";
        let first = ReportedArtifactV1::from_payloads(
            ArtifactKey::parse("artifact:metrics:first").unwrap(),
            ArtifactRole::CompactMetrics,
            "application/json",
            compact_json,
            compact_json,
            None,
            None,
        )
        .unwrap();
        let reformatted = ReportedArtifactV1::from_payloads(
            ArtifactKey::parse("artifact:metrics:pretty").unwrap(),
            ArtifactRole::CompactMetrics,
            "application/json",
            pretty_json,
            compact_json,
            None,
            None,
        )
        .unwrap();

        assert_ne!(first.byte_digest, reformatted.byte_digest);
        assert_eq!(first.content_digest, reformatted.content_digest);
        let checked = reformatted.check_reopened_payloads(pretty_json, compact_json).unwrap();
        assert_eq!(checked.reported_artifact(), &reformatted);
        let committed = CommittedArtifactV1::from_checked(checked, "outputs/metrics.arrow").unwrap();
        assert_eq!(committed.artifact(), &reformatted);
        assert_eq!(committed.relative_path(), "outputs/metrics.arrow");
        assert!(
            reformatted
                .check_reopened_payloads(b"changed", compact_json)
                .unwrap_err()
                .to_string()
                .contains("byte")
        );
        assert!(
            reformatted
                .check_reopened_payloads(pretty_json, br#"{"mse":9.0}"#)
                .unwrap_err()
                .to_string()
                .contains("scientific content")
        );
    }

    #[test]
    fn reported_tensor_hashes_container_and_canonical_values_separately() {
        let layout = point_layout();
        let canonical_values = vec![0_u8; 48];
        let container = b"fake-safetensors-container";
        let reported_artifact = ReportedArtifactV1::from_payloads(
            ArtifactKey::parse("artifact:point-forecast").unwrap(),
            ArtifactRole::DensePointForecast,
            "application/x-safetensors",
            container,
            &canonical_values,
            Some(layout),
            Some(point_reported_meaning()),
        )
        .unwrap();

        reported_artifact
            .check_reopened_payloads(container, &canonical_values)
            .unwrap();
        let mut changed_values = canonical_values;
        changed_values[0] = 1;
        assert!(
            reported_artifact
                .check_reopened_payloads(container, &changed_values)
                .is_err()
        );
    }

    #[test]
    fn worker_result_must_match_attempt_request_and_required_output_contract() {
        let request = request();
        let result = WorkerResultReportV1 {
            schema: WORKER_RESULT_REPORT_SCHEMA.to_string(),
            attempt_id: request.attempt_id.clone(),
            run_hash: request.run_hash.clone(),
            request_digest: request.digest().unwrap(),
            outputs: vec![
                reported_artifact(ArtifactRole::CompactMetrics, 20, None),
                reported_artifact(ArtifactRole::InvocationReceipt, 21, None),
                reported_artifact(ArtifactRole::ExecutionEnvironment, 22, None),
                reported_artifact(ArtifactRole::DensePointForecast, 23, Some(point_layout())),
            ],
        };
        assert!(result.validate_against(&request).is_ok());

        let mut wrong_attempt = result.clone();
        wrong_attempt.attempt_id = "attempt-other".to_string();
        assert!(wrong_attempt.validate_against(&request).is_err());

        let mut missing_forecast = result;
        missing_forecast
            .outputs
            .retain(|artifact| artifact.role != ArtifactRole::DensePointForecast);
        assert!(missing_forecast.validate_against(&request).is_err());
    }

    #[test]
    fn derived_point_request_names_source_before_launch_and_result_binds_its_checked_digest() {
        let sample_key = output_key(24);
        let mut request = request();
        let point_output = request
            .outputs
            .iter_mut()
            .find(|output| output.role == ArtifactRole::DensePointForecast)
            .unwrap();
        let common = point_reported_meaning();
        point_output.forecast_contract = Some(DeclaredForecastOutputV1 {
            schema: DECLARED_FORECAST_OUTPUT_SCHEMA.to_string(),
            scientific_cases: common.scientific_cases,
            forecast_origin_plan: common.forecast_origin_plan,
            channel_order: common.channel_order,
            value_space: common.value_space,
            scale_reference: common.scale_reference,
            meaning: DeclaredForecastMeaningV1::DensePoint {
                point_kind: PointKind::ConditionalMean,
                derivation: DeclaredPointDerivationV1::PredictiveSampleReduction {
                    source_artifact_key: sample_key.clone(),
                    statistic: SampleStatisticV1::Mean,
                    derivation_version: 1,
                },
            },
        });
        assert!(
            request
                .validate()
                .unwrap_err()
                .to_string()
                .contains("not declared as an output")
        );
        request.outputs.push(OutputContract::required_forecast(
            sample_key.clone(),
            ArtifactRole::PredictiveSamples,
            sample_layout(),
            sample_contract(),
        ));
        request.validate().unwrap();

        let sample_values = vec![0_u8; sample_layout().expected_payload_bytes().unwrap() as usize];
        let sample = ReportedArtifactV1::from_payloads(
            sample_key,
            ArtifactRole::PredictiveSamples,
            "application/x-safetensors",
            b"sample-container",
            &sample_values,
            Some(sample_layout()),
            Some(sample_reported_meaning()),
        )
        .unwrap();
        let point_source = ArtifactReference {
            artifact_key: sample.artifact_key.clone(),
            role: sample.role,
            content_digest: sample.content_digest.clone(),
        };
        let point_common = point_reported_meaning();
        let point_observed = ReportedForecastMeaningV1 {
            schema: REPORTED_FORECAST_MEANING_SCHEMA.to_string(),
            scientific_cases: point_common.scientific_cases,
            forecast_origin_plan: point_common.forecast_origin_plan,
            channel_order: point_common.channel_order,
            value_space: point_common.value_space,
            scale_reference: point_common.scale_reference,
            meaning: ForecastMeaningV1::DensePoint {
                point_kind: PointKind::ConditionalMean,
                derivation: ReportedPointDerivationV1::PredictiveSampleReduction {
                    source: point_source,
                    statistic: SampleStatisticV1::Mean,
                    derivation_version: 1,
                },
            },
        };
        let point_values = vec![0_u8; point_layout().expected_payload_bytes().unwrap() as usize];
        let point = ReportedArtifactV1::from_payloads(
            output_key(23),
            ArtifactRole::DensePointForecast,
            "application/x-safetensors",
            b"point-container",
            &point_values,
            Some(point_layout()),
            Some(point_observed),
        )
        .unwrap();
        let result = WorkerResultReportV1 {
            schema: WORKER_RESULT_REPORT_SCHEMA.to_string(),
            attempt_id: request.attempt_id.clone(),
            run_hash: request.run_hash.clone(),
            request_digest: request.digest().unwrap(),
            outputs: vec![
                reported_artifact(ArtifactRole::CompactMetrics, 20, None),
                reported_artifact(ArtifactRole::InvocationReceipt, 21, None),
                reported_artifact(ArtifactRole::ExecutionEnvironment, 22, None),
                point,
                sample,
            ],
        };
        result.validate_against(&request).unwrap();

        let mut wrong_source = result.clone();
        let point = wrong_source
            .outputs
            .iter_mut()
            .find(|output| output.role == ArtifactRole::DensePointForecast)
            .unwrap();
        let Some(ReportedForecastMeaningV1 {
            meaning: ForecastMeaningV1::DensePoint { derivation, .. },
            ..
        }) = point.reported_forecast_meaning.as_mut()
        else {
            panic!("point forecast must carry reported forecast meaning")
        };
        let ReportedPointDerivationV1::PredictiveSampleReduction { source, .. } = derivation else {
            panic!("fixture must use a sample reduction")
        };
        source.content_digest = digest(99);
        assert!(
            wrong_source
                .validate_against(&request)
                .unwrap_err()
                .to_string()
                .contains("checked source artifact")
        );
    }

    #[test]
    fn worker_result_refuses_an_output_declared_as_omitted() {
        let request = request();
        let result = WorkerResultReportV1 {
            schema: WORKER_RESULT_REPORT_SCHEMA.to_string(),
            attempt_id: request.attempt_id.clone(),
            run_hash: request.run_hash.clone(),
            request_digest: request.digest().unwrap(),
            outputs: vec![
                reported_artifact(ArtifactRole::CompactMetrics, 20, None),
                reported_artifact(ArtifactRole::InvocationReceipt, 21, None),
                reported_artifact(ArtifactRole::ExecutionEnvironment, 22, None),
                reported_artifact(ArtifactRole::DensePointForecast, 23, Some(point_layout())),
                reported_artifact(ArtifactRole::QuantileForecast, 24, Some(point_layout())),
            ],
        };

        assert!(
            result
                .validate_against(&request)
                .unwrap_err()
                .to_string()
                .contains("declared omitted")
        );
    }

    #[test]
    fn worker_result_cannot_relabel_one_artifact_key_as_two_outputs() {
        let request = request();
        let metrics = reported_artifact(ArtifactRole::CompactMetrics, 20, None);
        let mut invocation = reported_artifact(ArtifactRole::InvocationReceipt, 21, None);
        invocation.artifact_key = metrics.artifact_key.clone();
        let result = WorkerResultReportV1 {
            schema: WORKER_RESULT_REPORT_SCHEMA.to_string(),
            attempt_id: request.attempt_id.clone(),
            run_hash: request.run_hash.clone(),
            request_digest: request.digest().unwrap(),
            outputs: vec![
                metrics,
                invocation,
                reported_artifact(ArtifactRole::ExecutionEnvironment, 22, None),
                reported_artifact(ArtifactRole::DensePointForecast, 23, Some(point_layout())),
            ],
        };

        assert!(
            result
                .validate_against(&request)
                .unwrap_err()
                .to_string()
                .contains("artifact key")
        );
    }

    #[test]
    fn attempt_commit_is_order_independent_and_rejects_missing_or_unsafe_outputs() {
        let request = request();
        let result = WorkerResultReportV1 {
            schema: WORKER_RESULT_REPORT_SCHEMA.to_string(),
            attempt_id: request.attempt_id.clone(),
            run_hash: request.run_hash.clone(),
            request_digest: request.digest().unwrap(),
            outputs: vec![
                reported_artifact(ArtifactRole::CompactMetrics, 20, None),
                reported_artifact(ArtifactRole::InvocationReceipt, 21, None),
                reported_artifact(ArtifactRole::ExecutionEnvironment, 22, None),
                reported_artifact(ArtifactRole::DensePointForecast, 23, Some(point_layout())),
            ],
        };
        let outputs = result
            .outputs
            .iter()
            .enumerate()
            .map(|(index, artifact)| CommittedArtifactV1 {
                artifact: artifact.clone(),
                relative_path: format!("outputs/{index:03}.artifact"),
            })
            .collect::<Vec<_>>();
        let commit = AttemptArtifactCommitV1 {
            schema: ATTEMPT_ARTIFACT_COMMIT_SCHEMA.to_string(),
            attempt_id: request.attempt_id.clone(),
            run_hash: request.run_hash.clone(),
            request_digest: request.digest().unwrap(),
            result_digest: result.digest_against(&request).unwrap(),
            outputs,
        };
        let first_digest = commit.digest_against(&request, &result).unwrap();
        let mut reordered = commit.clone();
        reordered.outputs.reverse();
        assert_eq!(first_digest, reordered.digest_against(&request, &result).unwrap());

        let mut missing = commit.clone();
        missing.outputs.pop();
        assert!(missing.validate_against(&request, &result).is_err());

        let mut shared_container = commit.clone();
        shared_container.outputs[1].relative_path = shared_container.outputs[0].relative_path.clone();
        assert!(
            shared_container
                .validate_against(&request, &result)
                .unwrap_err()
                .to_string()
                .contains("duplicate relative path")
        );

        let mut escaping = commit;
        escaping.outputs[0].relative_path = "../outside".to_string();
        assert!(
            escaping
                .validate_against(&request, &result)
                .unwrap_err()
                .to_string()
                .contains("relative_path")
        );
    }
}
