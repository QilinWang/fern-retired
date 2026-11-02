//! Typed compact-metric contracts and reconstruction math.
//!
//! This module keeps two layers distinct:
//! - [`RiskPrimitiveV1`] stores additive measurement-ledger quantities;
//! - [`MetricValueV1`] stores mature derived metrics under typed, versioned specs.
//!
//! Values here are pure data and math. Arrow IPC is a transport chosen by the
//! parent artifact writer; V3 authority and evidence acceptance remain outside
//! this module.

use std::collections::HashSet;

use serde::{Deserialize, Serialize};

use super::{
    ArtifactReference, ArtifactRole, COMPACT_METRICS_SCHEMA, ContractError, ExactRatio, ScientificCaseId,
    ScientificContentDigest, domain_digest,
};

pub const METRIC_CONTRACT_SCHEMA: &str = "fern.metric_contract.v1";
pub const METRIC_VALUE_SCHEMA: &str = "fern.metric_value.v1";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum MetricTargetKind {
    ObservedCorrupted,
    RecordedReference,
    LatentClean,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum MetricSourceRepresentation {
    Point,
    PredictiveSamples,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum MetricValueSpace {
    Physical,
    TrainingStandardized,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SampleAggregation {
    NotPresent,
    ArithmeticMean,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ChannelAggregation {
    EqualArithmeticMean,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ForecastOriginAggregation {
    ArithmeticMean,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct MetricAggregationV1 {
    pub sample: SampleAggregation,
    pub forecast_origin: ForecastOriginAggregation,
    pub channel: ChannelAggregation,
}

impl MetricAggregationV1 {
    pub const fn point_default() -> Self {
        Self {
            sample: SampleAggregation::NotPresent,
            forecast_origin: ForecastOriginAggregation::ArithmeticMean,
            channel: ChannelAggregation::EqualArithmeticMean,
        }
    }

    pub const fn sample_default() -> Self {
        Self {
            sample: SampleAggregation::ArithmeticMean,
            forecast_origin: ForecastOriginAggregation::ArithmeticMean,
            channel: ChannelAggregation::EqualArithmeticMean,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "metric_id", rename_all = "snake_case", deny_unknown_fields)]
pub enum MetricSpecV1 {
    EffectivePredictionTime {
        metric_version: u32,
        target_kind: MetricTargetKind,
        target: ArtifactReference,
        threshold_source: ArtifactReference,
        threshold_multiplier: ExactRatio,
        horizon_length: u64,
        source_representation: MetricSourceRepresentation,
        source: ArtifactReference,
        aggregation: MetricAggregationV1,
    },
    PerChannelSquaredWasserstein {
        metric_version: u32,
        target_kind: MetricTargetKind,
        target: ArtifactReference,
        horizon_length: u64,
        value_space: MetricValueSpace,
        scale_reference: Option<ArtifactReference>,
        source_representation: MetricSourceRepresentation,
        source: ArtifactReference,
        aggregation: MetricAggregationV1,
    },
}

impl MetricSpecV1 {
    pub fn validate(&self) -> Result<(), ContractError> {
        match self {
            Self::EffectivePredictionTime {
                metric_version,
                target_kind,
                target,
                threshold_source,
                horizon_length,
                source_representation,
                source,
                aggregation,
                ..
            } => {
                require_metric_version(*metric_version)?;
                require_target_role(*target_kind, target)?;
                if threshold_source.role != ArtifactRole::TrainingScale {
                    return Err(ContractError::new(
                        "metric.ept.threshold_source",
                        "must reference the canonical training_scale artifact",
                    ));
                }
                require_positive_horizon(*horizon_length)?;
                require_source_role(*source_representation, source)?;
                require_aggregation(*source_representation, *aggregation)?;
            }
            Self::PerChannelSquaredWasserstein {
                metric_version,
                target_kind,
                target,
                horizon_length,
                value_space,
                scale_reference,
                source_representation,
                source,
                aggregation,
            } => {
                require_metric_version(*metric_version)?;
                require_target_role(*target_kind, target)?;
                require_positive_horizon(*horizon_length)?;
                require_source_role(*source_representation, source)?;
                require_aggregation(*source_representation, *aggregation)?;
                match (value_space, scale_reference) {
                    (MetricValueSpace::Physical, None) => {}
                    (MetricValueSpace::TrainingStandardized, Some(scale))
                        if scale.role == ArtifactRole::TrainingScale => {}
                    (MetricValueSpace::Physical, Some(_)) => {
                        return Err(ContractError::new(
                            "metric.wd.scale_reference",
                            "physical value space must not declare a scale artifact",
                        ));
                    }
                    (MetricValueSpace::TrainingStandardized, _) => {
                        return Err(ContractError::new(
                            "metric.wd.scale_reference",
                            "training_standardized value space requires training_scale",
                        ));
                    }
                }
            }
        }
        Ok(())
    }

    pub fn identity_digest(&self) -> Result<ScientificContentDigest, ContractError> {
        self.validate()?;
        let bytes = serde_json::to_vec(self)
            .map_err(|error| ContractError::new("metric.spec", format!("cannot canonicalize spec: {error}")))?;
        Ok(domain_digest(b"fern.metric_spec.v1\0", &bytes))
    }

    pub const fn target_kind(&self) -> MetricTargetKind {
        match self {
            Self::EffectivePredictionTime { target_kind, .. }
            | Self::PerChannelSquaredWasserstein { target_kind, .. } => *target_kind,
        }
    }

    pub const fn source_representation(&self) -> MetricSourceRepresentation {
        match self {
            Self::EffectivePredictionTime {
                source_representation, ..
            }
            | Self::PerChannelSquaredWasserstein {
                source_representation, ..
            } => *source_representation,
        }
    }

    fn artifact_references(&self) -> Vec<&ArtifactReference> {
        match self {
            Self::EffectivePredictionTime {
                target,
                threshold_source,
                source,
                ..
            } => vec![target, threshold_source, source],
            Self::PerChannelSquaredWasserstein {
                target,
                scale_reference,
                source,
                ..
            } => {
                let mut references = vec![target, source];
                if let Some(scale) = scale_reference {
                    references.push(scale);
                }
                references
            }
        }
    }
}

fn require_metric_version(version: u32) -> Result<(), ContractError> {
    if version != 1 {
        return Err(ContractError::new(
            "metric.metric_version",
            format!("V1 accepts metric_version=1, found {version}"),
        ));
    }
    Ok(())
}

fn require_positive_horizon(horizon: u64) -> Result<(), ContractError> {
    if horizon == 0 {
        return Err(ContractError::new("metric.horizon_length", "must be positive"));
    }
    Ok(())
}

fn require_target_role(kind: MetricTargetKind, target: &ArtifactReference) -> Result<(), ContractError> {
    let accepted = match kind {
        MetricTargetKind::ObservedCorrupted => target.role == ArtifactRole::FinalNoisyInput,
        MetricTargetKind::RecordedReference => target.role == ArtifactRole::RecordedReferenceTrajectory,
        MetricTargetKind::LatentClean => target.role == ArtifactRole::LatentCleanTrajectory,
    };
    if !accepted {
        return Err(ContractError::new(
            "metric.target",
            format!(
                "target kind {kind:?} is incompatible with role {}",
                target.role.as_str()
            ),
        ));
    }
    Ok(())
}

fn require_source_role(
    representation: MetricSourceRepresentation,
    source: &ArtifactReference,
) -> Result<(), ContractError> {
    let accepted = match representation {
        MetricSourceRepresentation::Point => source.role == ArtifactRole::DensePointForecast,
        MetricSourceRepresentation::PredictiveSamples => source.role == ArtifactRole::PredictiveSamples,
    };
    if !accepted {
        return Err(ContractError::new(
            "metric.source",
            format!(
                "source representation {representation:?} is incompatible with role {}",
                source.role.as_str()
            ),
        ));
    }
    Ok(())
}

fn require_aggregation(
    representation: MetricSourceRepresentation,
    aggregation: MetricAggregationV1,
) -> Result<(), ContractError> {
    let expected = match representation {
        MetricSourceRepresentation::Point => MetricAggregationV1::point_default(),
        MetricSourceRepresentation::PredictiveSamples => MetricAggregationV1::sample_default(),
    };
    if aggregation != expected {
        return Err(ContractError::new(
            "metric.aggregation",
            format!("expected {expected:?} for {representation:?}, found {aggregation:?}"),
        ));
    }
    Ok(())
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct MetricValueV1 {
    pub schema: String,
    pub spec: MetricSpecV1,
    pub value: f64,
}

impl MetricValueV1 {
    pub fn validate(&self) -> Result<(), ContractError> {
        if self.schema != METRIC_VALUE_SCHEMA {
            return Err(ContractError::new(
                "metric_value.schema",
                format!("expected {METRIC_VALUE_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        self.spec.validate()?;
        if !self.value.is_finite() {
            return Err(ContractError::new("metric_value.value", "must be finite"));
        }
        Ok(())
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RiskPrimitiveV1 {
    pub element_count: u64,
    pub observed_squared_error_sum: f64,
    pub observed_absolute_error_sum: f64,
    pub reference_target_kind: Option<MetricTargetKind>,
    pub reference_squared_error_sum: Option<f64>,
    pub injected_noise_squared_sum: Option<f64>,
    pub reference_residual_noise_inner_product_sum: Option<f64>,
}

impl RiskPrimitiveV1 {
    pub fn from_aligned(
        forecast: &[f64],
        observed_target: &[f64],
        reference_target: Option<(MetricTargetKind, &[f64])>,
    ) -> Result<(Self, LedgerDiagnosticV1), ContractError> {
        require_same_nonempty_length("risk_primitive.forecast", forecast, observed_target)?;
        if let Some((kind, reference)) = reference_target {
            if kind == MetricTargetKind::ObservedCorrupted {
                return Err(ContractError::new(
                    "risk_primitive.reference_target_kind",
                    "must be latent_clean or recorded_reference",
                ));
            }
            require_same_nonempty_length("risk_primitive.reference_target", reference, observed_target)?;
        }

        let mut observed_sse = 0.0;
        let mut observed_sae = 0.0;
        let mut reference_sse = 0.0;
        let mut noise_sse = 0.0;
        let mut inner = 0.0;
        for (index, (&prediction, &observed)) in forecast.iter().zip(observed_target).enumerate() {
            require_finite("risk_primitive.forecast", index, prediction)?;
            require_finite("risk_primitive.observed_target", index, observed)?;
            let observed_error = observed - prediction;
            observed_sse += observed_error * observed_error;
            observed_sae += observed_error.abs();
            if let Some((_, reference)) = reference_target {
                let reference_value = reference[index];
                require_finite("risk_primitive.reference_target", index, reference_value)?;
                let reference_residual = reference_value - prediction;
                let injection = observed - reference_value;
                reference_sse += reference_residual * reference_residual;
                noise_sse += injection * injection;
                inner += reference_residual * injection;
            }
        }

        let primitive = Self {
            element_count: forecast.len() as u64,
            observed_squared_error_sum: observed_sse,
            observed_absolute_error_sum: observed_sae,
            reference_target_kind: reference_target.map(|(kind, _)| kind),
            reference_squared_error_sum: reference_target.map(|_| reference_sse),
            injected_noise_squared_sum: reference_target.map(|_| noise_sse),
            reference_residual_noise_inner_product_sum: reference_target.map(|_| inner),
        };
        let diagnostic = LedgerDiagnosticV1 {
            ledger_residual_sum: reference_target.map(|_| observed_sse - reference_sse - noise_sse - 2.0 * inner),
        };
        primitive.validate()?;
        diagnostic.validate_against(&primitive, NumericalToleranceV1::f64_default())?;
        Ok((primitive, diagnostic))
    }

    pub fn validate(&self) -> Result<(), ContractError> {
        if self.element_count == 0 {
            return Err(ContractError::new("risk_primitive.element_count", "must be positive"));
        }
        require_nonnegative_finite(
            "risk_primitive.observed_squared_error_sum",
            self.observed_squared_error_sum,
        )?;
        require_nonnegative_finite(
            "risk_primitive.observed_absolute_error_sum",
            self.observed_absolute_error_sum,
        )?;
        let option_count = [
            self.reference_squared_error_sum,
            self.injected_noise_squared_sum,
            self.reference_residual_noise_inner_product_sum,
        ]
        .into_iter()
        .filter(Option::is_some)
        .count();
        if option_count != 0 && option_count != 3 {
            return Err(ContractError::new(
                "risk_primitive.reference_ledger",
                "reference SSE, injected-noise SSE, and residual-noise inner product must be all present or all absent",
            ));
        }
        if self.reference_target_kind.is_some() != (option_count == 3) {
            return Err(ContractError::new(
                "risk_primitive.reference_target_kind",
                "must be present exactly when the reference ledger is present",
            ));
        }
        if self.reference_target_kind == Some(MetricTargetKind::ObservedCorrupted) {
            return Err(ContractError::new(
                "risk_primitive.reference_target_kind",
                "must be latent_clean or recorded_reference",
            ));
        }
        if let Some(value) = self.reference_squared_error_sum {
            require_nonnegative_finite("risk_primitive.reference_squared_error_sum", value)?;
        }
        if let Some(value) = self.injected_noise_squared_sum {
            require_nonnegative_finite("risk_primitive.injected_noise_squared_sum", value)?;
        }
        if let Some(value) = self.reference_residual_noise_inner_product_sum
            && !value.is_finite()
        {
            return Err(ContractError::new(
                "risk_primitive.reference_residual_noise_inner_product_sum",
                "must be finite",
            ));
        }
        Ok(())
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct LedgerDiagnosticV1 {
    pub ledger_residual_sum: Option<f64>,
}

impl LedgerDiagnosticV1 {
    pub fn validate_against(
        &self,
        primitive: &RiskPrimitiveV1,
        tolerance: NumericalToleranceV1,
    ) -> Result<(), ContractError> {
        primitive.validate()?;
        tolerance.validate()?;
        let expected = match (
            primitive.reference_squared_error_sum,
            primitive.injected_noise_squared_sum,
            primitive.reference_residual_noise_inner_product_sum,
        ) {
            (Some(clean), Some(noise), Some(inner)) => {
                Some(primitive.observed_squared_error_sum - clean - noise - 2.0 * inner)
            }
            (None, None, None) => None,
            _ => unreachable!("RiskPrimitiveV1::validate rejects partial reference ledgers"),
        };
        match (self.ledger_residual_sum, expected) {
            (None, None) => Ok(()),
            (Some(observed), Some(expected)) if tolerance.contains(observed, expected) => Ok(()),
            (Some(observed), Some(expected)) => Err(ContractError::new(
                "ledger_diagnostic.ledger_residual_sum",
                format!("stored {observed} does not match recomputed {expected}"),
            )),
            _ => Err(ContractError::new(
                "ledger_diagnostic.ledger_residual_sum",
                "must be present exactly when the clean ledger is present",
            )),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct NumericalToleranceV1 {
    pub absolute: f64,
    pub relative: f64,
}

impl NumericalToleranceV1 {
    pub const fn f64_default() -> Self {
        Self {
            absolute: 1.0e-10,
            relative: 1.0e-10,
        }
    }

    pub fn validate(self) -> Result<(), ContractError> {
        if !self.absolute.is_finite() || !self.relative.is_finite() || self.absolute < 0.0 || self.relative < 0.0 {
            return Err(ContractError::new(
                "numerical_tolerance",
                "absolute and relative tolerances must be finite and non-negative",
            ));
        }
        Ok(())
    }

    pub fn contains(self, observed: f64, expected: f64) -> bool {
        observed.is_finite()
            && expected.is_finite()
            && (observed - expected).abs() <= self.absolute + self.relative * expected.abs().max(1.0)
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct MetricContractV1 {
    pub schema: String,
    pub required_targets: Vec<MetricTargetKind>,
    pub ept_threshold_multipliers: Vec<ExactRatio>,
    pub require_point_wd: bool,
}

impl MetricContractV1 {
    pub fn time_series_v1(required_targets: Vec<MetricTargetKind>) -> Result<Self, ContractError> {
        let contract = Self {
            schema: METRIC_CONTRACT_SCHEMA.to_string(),
            required_targets,
            ept_threshold_multipliers: vec![ExactRatio::new(1, 1)?, ExactRatio::new(3, 2)?, ExactRatio::new(2, 1)?],
            require_point_wd: true,
        };
        contract.validate()?;
        Ok(contract)
    }

    pub fn validate(&self) -> Result<(), ContractError> {
        if self.schema != METRIC_CONTRACT_SCHEMA {
            return Err(ContractError::new(
                "metric_contract.schema",
                format!("expected {METRIC_CONTRACT_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        let mut targets = self.required_targets.clone();
        targets.sort();
        targets.dedup();
        if targets != self.required_targets || !targets.contains(&MetricTargetKind::ObservedCorrupted) {
            return Err(ContractError::new(
                "metric_contract.required_targets",
                "must be sorted, unique, and include observed_corrupted",
            ));
        }
        let reference_targets = targets
            .iter()
            .filter(|target| **target != MetricTargetKind::ObservedCorrupted)
            .count();
        if reference_targets > 1 {
            return Err(ContractError::new(
                "metric_contract.required_targets",
                "may declare latent_clean or recorded_reference, but not both for one case",
            ));
        }
        let expected = [ExactRatio::new(1, 1)?, ExactRatio::new(3, 2)?, ExactRatio::new(2, 1)?];
        if self.ept_threshold_multipliers.as_slice() != expected {
            return Err(ContractError::new(
                "metric_contract.ept_threshold_multipliers",
                "V1 requires the predeclared ordered family [1/1, 3/2, 2/1]",
            ));
        }
        if !self.require_point_wd {
            return Err(ContractError::new(
                "metric_contract.require_point_wd",
                "V1 cross-model evaluation requires point-scored WD",
            ));
        }
        Ok(())
    }

    pub fn digest(&self) -> Result<ScientificContentDigest, ContractError> {
        self.validate()?;
        let bytes = serde_json::to_vec(self)
            .map_err(|error| ContractError::new("metric_contract", format!("cannot canonicalize contract: {error}")))?;
        Ok(domain_digest(b"fern.metric_contract.v1\0", &bytes))
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CompactMetricsCaseV1 {
    pub case_id: ScientificCaseId,
    pub case_realization_digest: ScientificContentDigest,
    pub risk_primitive: RiskPrimitiveV1,
    pub derived_diagnostic: LedgerDiagnosticV1,
    pub metric_values: Vec<MetricValueV1>,
}

/// One small, permanent numeric ledger for one Attempt.
///
/// Attempt-wide facts live here once; each case row carries only facts that
/// can vary within this artifact.  The same object is encoded as Arrow IPC by
/// the transport adapter, so JSON never becomes a second numeric truth.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CompactMetricsV1 {
    pub schema: String,
    pub attempt_id: String,
    pub request_digest: ScientificContentDigest,
    pub metric_contract: MetricContractV1,
    pub numerical_tolerance: NumericalToleranceV1,
    pub checked_source_artifacts: Vec<ArtifactReference>,
    pub cases: Vec<CompactMetricsCaseV1>,
}

impl CompactMetricsV1 {
    pub fn validate(&self) -> Result<(), ContractError> {
        if self.schema != COMPACT_METRICS_SCHEMA {
            return Err(ContractError::new(
                "compact_metrics.schema",
                format!("expected {COMPACT_METRICS_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        super::validate_logical_id("compact_metrics.attempt_id", &self.attempt_id)?;
        self.metric_contract.validate()?;
        self.numerical_tolerance.validate()?;
        if self.cases.is_empty() {
            return Err(ContractError::new(
                "compact_metrics.cases",
                "must contain at least one evaluated case",
            ));
        }
        let mut source_keys = HashSet::new();
        for source in &self.checked_source_artifacts {
            if !source_keys.insert(&source.artifact_key) {
                return Err(ContractError::new(
                    "compact_metrics.checked_source_artifacts",
                    "contains a duplicate artifact key",
                ));
            }
        }
        let mut case_ids = HashSet::new();
        for case in &self.cases {
            if !case_ids.insert(&case.case_id) {
                return Err(ContractError::new(
                    "compact_metrics.cases",
                    "contains a duplicate case_id",
                ));
            }
            case.validate(&self.metric_contract, self.numerical_tolerance)?;
            for metric in &case.metric_values {
                for reference in metric.spec.artifact_references() {
                    let Some(checked) = self
                        .checked_source_artifacts
                        .iter()
                        .find(|candidate| candidate.artifact_key == reference.artifact_key)
                    else {
                        return Err(ContractError::new(
                            "compact_metrics.checked_source_artifacts",
                            format!(
                                "metric source {:?} is not in the checked artifact map",
                                reference.artifact_key.as_str()
                            ),
                        ));
                    };
                    if checked != reference {
                        return Err(ContractError::new(
                            "compact_metrics.checked_source_artifacts",
                            format!(
                                "metric source {:?} does not match its checked role/content digest",
                                reference.artifact_key.as_str()
                            ),
                        ));
                    }
                }
            }
        }
        Ok(())
    }

    /// Prove that this cached ledger belongs to the exact Attempt request.
    ///
    /// Artifact-reference membership is checked by [`Self::validate`]. This
    /// method additionally prevents a valid ledger from another Attempt,
    /// ScientificCase, realization, or metric policy from being committed
    /// under the current worker result merely because its Arrow schema fits.
    pub fn validate_against_request(&self, request: &super::WorkerRequestV1) -> Result<(), ContractError> {
        self.validate()?;
        request.validate()?;
        if self.attempt_id != request.attempt_id {
            return Err(ContractError::new(
                "compact_metrics.attempt_id",
                "does not match the worker request",
            ));
        }
        if self.request_digest != request.digest()? {
            return Err(ContractError::new(
                "compact_metrics.request_digest",
                "does not match the worker request",
            ));
        }
        if self.metric_contract != request.metric_contract {
            return Err(ContractError::new(
                "compact_metrics.metric_contract",
                "does not match the predeclared worker metric contract",
            ));
        }
        if self.cases.len() != 1 || self.cases[0].case_id != request.scientific_case.case_id {
            return Err(ContractError::new(
                "compact_metrics.cases",
                "must contain exactly the worker request's ScientificCase",
            ));
        }
        if self.cases[0].case_realization_digest != request.realization.digest()? {
            return Err(ContractError::new(
                "compact_metrics.cases.case_realization_digest",
                "does not match the worker request realization",
            ));
        }
        Ok(())
    }

    pub fn digest(&self) -> Result<ScientificContentDigest, ContractError> {
        Ok(domain_digest(b"fern.compact_metrics.v1\0", &self.canonical_bytes()?))
    }

    pub fn canonical_bytes(&self) -> Result<Vec<u8>, ContractError> {
        self.validate()?;
        let mut canonical = self.clone();
        canonical
            .checked_source_artifacts
            .sort_by(|left, right| left.artifact_key.cmp(&right.artifact_key));
        canonical.cases.sort_by(|left, right| left.case_id.cmp(&right.case_id));
        for case in &mut canonical.cases {
            case.metric_values.sort_by(|left, right| {
                left.spec
                    .identity_digest()
                    .expect("validated metric spec")
                    .cmp(&right.spec.identity_digest().expect("validated metric spec"))
            });
        }
        serde_json::to_vec(&canonical).map_err(|error| {
            ContractError::new(
                "compact_metrics",
                format!("cannot canonicalize compact metrics: {error}"),
            )
        })
    }
}

impl CompactMetricsCaseV1 {
    pub fn validate(&self, contract: &MetricContractV1, tolerance: NumericalToleranceV1) -> Result<(), ContractError> {
        contract.validate()?;
        self.risk_primitive.validate()?;
        self.derived_diagnostic
            .validate_against(&self.risk_primitive, tolerance)?;
        let mut metric_ids = HashSet::new();
        for metric in &self.metric_values {
            metric.validate()?;
            let digest = metric.spec.identity_digest()?;
            if !metric_ids.insert(digest) {
                return Err(ContractError::new(
                    "compact_metrics.metric_values",
                    "contains duplicate metric identity",
                ));
            }
        }
        for target in &contract.required_targets {
            validate_required_ept_family(&self.metric_values, *target, tolerance)?;
            if contract.require_point_wd {
                let matches = self
                    .metric_values
                    .iter()
                    .filter(|metric| {
                        matches!(
                            &metric.spec,
                            MetricSpecV1::PerChannelSquaredWasserstein {
                                target_kind,
                                source_representation: MetricSourceRepresentation::Point,
                                ..
                            } if target_kind == target
                        )
                    })
                    .count();
                if matches != 1 {
                    return Err(ContractError::new(
                        "compact_metrics.metric_values",
                        format!("target {target:?} requires exactly one point-scored WD, found {matches}"),
                    ));
                }
            }
        }
        Ok(())
    }
}

fn validate_required_ept_family(
    metrics: &[MetricValueV1],
    target: MetricTargetKind,
    tolerance: NumericalToleranceV1,
) -> Result<(), ContractError> {
    let required = [ExactRatio::new(1, 1)?, ExactRatio::new(3, 2)?, ExactRatio::new(2, 1)?];
    let mut family = Vec::new();
    for threshold in required {
        let matches = metrics
            .iter()
            .filter(|metric| {
                matches!(
                    &metric.spec,
                    MetricSpecV1::EffectivePredictionTime {
                        target_kind,
                        threshold_multiplier,
                        source_representation: MetricSourceRepresentation::Point,
                        ..
                    } if *target_kind == target && *threshold_multiplier == threshold
                )
            })
            .collect::<Vec<_>>();
        if matches.len() != 1 {
            return Err(ContractError::new(
                "compact_metrics.metric_values",
                format!(
                    "target {target:?} requires one point EPT at {}/{}, found {}",
                    threshold.numerator,
                    threshold.denominator,
                    matches.len()
                ),
            ));
        }
        family.push(matches[0]);
    }
    for pair in family.windows(2) {
        require_same_ept_family(&pair[0].spec, &pair[1].spec)?;
        if pair[0].value > pair[1].value && !tolerance.contains(pair[0].value, pair[1].value) {
            return Err(ContractError::new(
                "compact_metrics.metric_values",
                "EPT threshold family must be non-decreasing",
            ));
        }
    }
    Ok(())
}

fn require_same_ept_family(left: &MetricSpecV1, right: &MetricSpecV1) -> Result<(), ContractError> {
    let (
        MetricSpecV1::EffectivePredictionTime {
            metric_version: left_version,
            target_kind: left_target_kind,
            target: left_target,
            threshold_source: left_threshold_source,
            horizon_length: left_horizon,
            source_representation: left_representation,
            source: left_source,
            aggregation: left_aggregation,
            ..
        },
        MetricSpecV1::EffectivePredictionTime {
            metric_version: right_version,
            target_kind: right_target_kind,
            target: right_target,
            threshold_source: right_threshold_source,
            horizon_length: right_horizon,
            source_representation: right_representation,
            source: right_source,
            aggregation: right_aggregation,
            ..
        },
    ) = (left, right)
    else {
        return Err(ContractError::new("metric.ept", "expected two EPT specs"));
    };
    if (
        left_version,
        left_target_kind,
        left_target,
        left_threshold_source,
        left_horizon,
        left_representation,
        left_source,
        left_aggregation,
    ) != (
        right_version,
        right_target_kind,
        right_target,
        right_threshold_source,
        right_horizon,
        right_representation,
        right_source,
        right_aggregation,
    ) {
        return Err(ContractError::new(
            "metric.ept",
            "threshold sensitivity values must share target, source, scale, horizon, aggregation, and version",
        ));
    }
    Ok(())
}

/// Reconstruct EPT from `[forecast_origin, sample, horizon, channel]` values.
/// For a point forecast, pass `samples=1`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ForecastMetricShapeV1 {
    pub samples: usize,
    pub forecast_origins: usize,
    pub horizon: usize,
    pub channels: usize,
}

pub fn reconstruct_ept(
    forecast: &[f64],
    target: &[f64],
    training_scale: &[f64],
    shape: ForecastMetricShapeV1,
    threshold_multiplier: ExactRatio,
) -> Result<f64, ContractError> {
    let ForecastMetricShapeV1 {
        samples,
        forecast_origins,
        horizon,
        channels,
    } = shape;
    require_metric_shape(forecast, target, training_scale, shape)?;
    let multiplier = threshold_multiplier.numerator as f64 / threshold_multiplier.denominator as f64;
    let mut total = 0.0;
    for sample in 0..samples {
        for origin in 0..forecast_origins {
            for channel in 0..channels {
                let threshold = training_scale[channel] * multiplier;
                if !threshold.is_finite() || threshold < 0.0 {
                    return Err(ContractError::new(
                        "metric.ept.training_scale",
                        format!("channel {channel} scale must be finite and non-negative"),
                    ));
                }
                let mut first = horizon;
                for step in 0..horizon {
                    let prediction =
                        forecast[forecast_index(origin, sample, step, channel, samples, horizon, channels)];
                    let truth = target[target_index(origin, step, channel, horizon, channels)];
                    if (prediction - truth).abs() > threshold {
                        first = step;
                        break;
                    }
                }
                total += first as f64;
            }
        }
    }
    Ok(total / (samples * forecast_origins * channels) as f64)
}

/// Reconstruct per-channel squared WD from the same declared source shape.
pub fn reconstruct_per_channel_squared_wd(
    forecast: &[f64],
    target: &[f64],
    training_scale: Option<&[f64]>,
    shape: ForecastMetricShapeV1,
) -> Result<f64, ContractError> {
    let ForecastMetricShapeV1 {
        samples,
        forecast_origins,
        horizon,
        channels,
    } = shape;
    let scale = training_scale.unwrap_or(&[]);
    require_metric_shape(forecast, target, scale, shape)?;
    let mut total = 0.0;
    for sample in 0..samples {
        for origin in 0..forecast_origins {
            for channel in 0..channels {
                let denominator = training_scale.map_or(1.0, |values| values[channel]);
                if !denominator.is_finite() || denominator <= 0.0 {
                    return Err(ContractError::new(
                        "metric.wd.training_scale",
                        format!("channel {channel} scale must be finite and positive"),
                    ));
                }
                let mut predicted = (0..horizon)
                    .map(|step| {
                        forecast[forecast_index(origin, sample, step, channel, samples, horizon, channels)]
                            / denominator
                    })
                    .collect::<Vec<_>>();
                let mut truth = (0..horizon)
                    .map(|step| target[target_index(origin, step, channel, horizon, channels)] / denominator)
                    .collect::<Vec<_>>();
                predicted.sort_by(f64::total_cmp);
                truth.sort_by(f64::total_cmp);
                total += predicted
                    .iter()
                    .zip(truth)
                    .map(|(prediction, truth)| (prediction - truth).powi(2))
                    .sum::<f64>()
                    / horizon as f64;
            }
        }
    }
    Ok(total / (samples * forecast_origins * channels) as f64)
}

pub struct PointMetricTargetInputV1<'a> {
    pub target_kind: MetricTargetKind,
    pub artifact: ArtifactReference,
    pub values: &'a [f64],
}

/// Inputs needed to reconstruct the mandatory point-scored case ledger.
/// `forecast` and every target use `[forecast_origin,horizon,channel]`.
pub struct PointMetricReconstructionInputV1<'a> {
    pub case_id: ScientificCaseId,
    pub case_realization_digest: ScientificContentDigest,
    pub point_source: ArtifactReference,
    pub forecast: &'a [f64],
    pub targets: Vec<PointMetricTargetInputV1<'a>>,
    pub training_scale_artifact: ArtifactReference,
    pub training_scale: &'a [f64],
    pub forecast_origins: usize,
    pub horizon: usize,
    pub channels: usize,
}

/// Recompute risk primitives, the measurement ledger, EPT sensitivity, and WD
/// from exact declared artifacts.  The worker may cache these values, but the
/// parent reader can independently reproduce them through this operation.
pub fn reconstruct_point_compact_metrics_case(
    contract: &MetricContractV1,
    input: PointMetricReconstructionInputV1<'_>,
) -> Result<CompactMetricsCaseV1, ContractError> {
    contract.validate()?;
    if input.point_source.role != ArtifactRole::DensePointForecast {
        return Err(ContractError::new(
            "compact_metrics.point_source",
            "must reference dense_point_forecast",
        ));
    }
    if input.training_scale_artifact.role != ArtifactRole::TrainingScale {
        return Err(ContractError::new(
            "compact_metrics.training_scale_artifact",
            "must reference training_scale",
        ));
    }
    let expected = input
        .forecast_origins
        .checked_mul(input.horizon)
        .and_then(|count| count.checked_mul(input.channels))
        .ok_or_else(|| ContractError::new("compact_metrics.shape", "target shape overflows usize"))?;
    if input.forecast.len() != expected {
        return Err(ContractError::new(
            "compact_metrics.forecast",
            format!("expected {expected} values, found {}", input.forecast.len()),
        ));
    }
    let mut target_kinds = HashSet::new();
    for target in &input.targets {
        require_target_role(target.target_kind, &target.artifact)?;
        if !target_kinds.insert(target.target_kind) {
            return Err(ContractError::new(
                "compact_metrics.targets",
                format!("duplicate target kind {:?}", target.target_kind),
            ));
        }
        if target.values.len() != expected {
            return Err(ContractError::new(
                "compact_metrics.targets",
                format!(
                    "target {:?} expected {expected} values, found {}",
                    target.target_kind,
                    target.values.len()
                ),
            ));
        }
    }
    for required in &contract.required_targets {
        if !target_kinds.contains(required) {
            return Err(ContractError::new(
                "compact_metrics.targets",
                format!("missing required target {required:?}"),
            ));
        }
    }
    let observed = input
        .targets
        .iter()
        .find(|target| target.target_kind == MetricTargetKind::ObservedCorrupted)
        .expect("MetricContractV1 always requires observed_corrupted");
    let reference_target = input
        .targets
        .iter()
        .find(|target| target.target_kind != MetricTargetKind::ObservedCorrupted)
        .map(|target| (target.target_kind, target.values));
    let (risk_primitive, derived_diagnostic) =
        RiskPrimitiveV1::from_aligned(input.forecast, observed.values, reference_target)?;

    let mut metric_values = Vec::new();
    for target in input
        .targets
        .iter()
        .filter(|target| contract.required_targets.contains(&target.target_kind))
    {
        for threshold_multiplier in &contract.ept_threshold_multipliers {
            metric_values.push(MetricValueV1 {
                schema: METRIC_VALUE_SCHEMA.to_string(),
                spec: MetricSpecV1::EffectivePredictionTime {
                    metric_version: 1,
                    target_kind: target.target_kind,
                    target: target.artifact.clone(),
                    threshold_source: input.training_scale_artifact.clone(),
                    threshold_multiplier: *threshold_multiplier,
                    horizon_length: input.horizon as u64,
                    source_representation: MetricSourceRepresentation::Point,
                    source: input.point_source.clone(),
                    aggregation: MetricAggregationV1::point_default(),
                },
                value: reconstruct_ept(
                    input.forecast,
                    target.values,
                    input.training_scale,
                    ForecastMetricShapeV1 {
                        samples: 1,
                        forecast_origins: input.forecast_origins,
                        horizon: input.horizon,
                        channels: input.channels,
                    },
                    *threshold_multiplier,
                )?,
            });
        }
        if contract.require_point_wd {
            metric_values.push(MetricValueV1 {
                schema: METRIC_VALUE_SCHEMA.to_string(),
                spec: MetricSpecV1::PerChannelSquaredWasserstein {
                    metric_version: 1,
                    target_kind: target.target_kind,
                    target: target.artifact.clone(),
                    horizon_length: input.horizon as u64,
                    value_space: MetricValueSpace::Physical,
                    scale_reference: None,
                    source_representation: MetricSourceRepresentation::Point,
                    source: input.point_source.clone(),
                    aggregation: MetricAggregationV1::point_default(),
                },
                value: reconstruct_per_channel_squared_wd(
                    input.forecast,
                    target.values,
                    None,
                    ForecastMetricShapeV1 {
                        samples: 1,
                        forecast_origins: input.forecast_origins,
                        horizon: input.horizon,
                        channels: input.channels,
                    },
                )?,
            });
        }
    }
    let case = CompactMetricsCaseV1 {
        case_id: input.case_id,
        case_realization_digest: input.case_realization_digest,
        risk_primitive,
        derived_diagnostic,
        metric_values,
    };
    case.validate(contract, NumericalToleranceV1::f64_default())?;
    Ok(case)
}

fn require_metric_shape(
    forecast: &[f64],
    target: &[f64],
    training_scale: &[f64],
    shape: ForecastMetricShapeV1,
) -> Result<(), ContractError> {
    let ForecastMetricShapeV1 {
        samples,
        forecast_origins: origins,
        horizon,
        channels,
    } = shape;
    if [samples, origins, horizon, channels].contains(&0) {
        return Err(ContractError::new("metric.shape", "all dimensions must be positive"));
    }
    let expected_forecast = samples
        .checked_mul(origins)
        .and_then(|value| value.checked_mul(horizon))
        .and_then(|value| value.checked_mul(channels))
        .ok_or_else(|| ContractError::new("metric.shape", "forecast shape overflows usize"))?;
    let expected_target = origins
        .checked_mul(horizon)
        .and_then(|value| value.checked_mul(channels))
        .ok_or_else(|| ContractError::new("metric.shape", "target shape overflows usize"))?;
    if forecast.len() != expected_forecast || target.len() != expected_target {
        return Err(ContractError::new(
            "metric.shape",
            format!(
                "expected forecast {expected_forecast} and target {expected_target} values, found {} and {}",
                forecast.len(),
                target.len()
            ),
        ));
    }
    if !training_scale.is_empty() && training_scale.len() != channels {
        return Err(ContractError::new(
            "metric.training_scale",
            format!("expected {channels} channel scales, found {}", training_scale.len()),
        ));
    }
    for (index, value) in forecast.iter().chain(target).enumerate() {
        require_finite("metric.values", index, *value)?;
    }
    Ok(())
}

fn forecast_index(
    origin: usize,
    sample: usize,
    step: usize,
    channel: usize,
    samples: usize,
    horizon: usize,
    channels: usize,
) -> usize {
    ((origin * samples + sample) * horizon + step) * channels + channel
}

fn target_index(origin: usize, step: usize, channel: usize, horizon: usize, channels: usize) -> usize {
    (origin * horizon + step) * channels + channel
}

fn require_same_nonempty_length(field: &str, left: &[f64], right: &[f64]) -> Result<(), ContractError> {
    if left.is_empty() || left.len() != right.len() {
        return Err(ContractError::new(
            field,
            format!(
                "requires equal non-empty lengths, found {} and {}",
                left.len(),
                right.len()
            ),
        ));
    }
    Ok(())
}

fn require_finite(field: &str, index: usize, value: f64) -> Result<(), ContractError> {
    if !value.is_finite() {
        return Err(ContractError::new(field, format!("value {index} must be finite")));
    }
    Ok(())
}

fn require_nonnegative_finite(field: &str, value: f64) -> Result<(), ContractError> {
    if !value.is_finite() || value < 0.0 {
        return Err(ContractError::new(field, "must be finite and non-negative"));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{ArtifactKey, ScientificContentDigest};

    fn reference(role: ArtifactRole, name: &str) -> ArtifactReference {
        ArtifactReference {
            artifact_key: ArtifactKey::parse(format!("artifact:{name}")).unwrap(),
            role,
            content_digest: ScientificContentDigest::from_bytes(name.as_bytes()),
        }
    }

    fn ept_spec(target: MetricTargetKind, multiplier: ExactRatio) -> MetricSpecV1 {
        MetricSpecV1::EffectivePredictionTime {
            metric_version: 1,
            target_kind: target,
            target: reference(ArtifactRole::FinalNoisyInput, "observed"),
            threshold_source: reference(ArtifactRole::TrainingScale, "scale"),
            threshold_multiplier: multiplier,
            horizon_length: 4,
            source_representation: MetricSourceRepresentation::Point,
            source: reference(ArtifactRole::DensePointForecast, "point"),
            aggregation: MetricAggregationV1::point_default(),
        }
    }

    fn wd_spec(target: MetricTargetKind) -> MetricSpecV1 {
        MetricSpecV1::PerChannelSquaredWasserstein {
            metric_version: 1,
            target_kind: target,
            target: reference(ArtifactRole::FinalNoisyInput, "observed"),
            horizon_length: 4,
            value_space: MetricValueSpace::Physical,
            scale_reference: None,
            source_representation: MetricSourceRepresentation::Point,
            source: reference(ArtifactRole::DensePointForecast, "point"),
            aggregation: MetricAggregationV1::point_default(),
        }
    }

    fn value(spec: MetricSpecV1, value: f64) -> MetricValueV1 {
        MetricValueV1 {
            schema: METRIC_VALUE_SCHEMA.to_string(),
            spec,
            value,
        }
    }

    fn shape(samples: usize) -> ForecastMetricShapeV1 {
        ForecastMetricShapeV1 {
            samples,
            forecast_origins: 1,
            horizon: 4,
            channels: 1,
        }
    }

    #[test]
    fn risk_ledger_closes_and_distinguishes_unavailable_from_zero() {
        let forecast = [1.0, 3.0];
        let reference = [2.0, 4.0];
        let observed = [3.0, 2.0];
        let (primitive, diagnostic) =
            RiskPrimitiveV1::from_aligned(&forecast, &observed, Some((MetricTargetKind::LatentClean, &reference)))
                .unwrap();
        assert_eq!(primitive.element_count, 2);
        assert_eq!(primitive.reference_target_kind, Some(MetricTargetKind::LatentClean));
        assert_eq!(primitive.reference_squared_error_sum, Some(2.0));
        assert_eq!(primitive.injected_noise_squared_sum, Some(5.0));
        assert_eq!(primitive.reference_residual_noise_inner_product_sum, Some(-1.0));
        assert!(NumericalToleranceV1::f64_default().contains(diagnostic.ledger_residual_sum.unwrap(), 0.0));

        let (without_reference, diagnostic) = RiskPrimitiveV1::from_aligned(&forecast, &observed, None).unwrap();
        assert_eq!(without_reference.reference_target_kind, None);
        assert_eq!(without_reference.reference_squared_error_sum, None);
        assert_eq!(diagnostic.ledger_residual_sum, None);

        let zero_reference = [1.0, 3.0];
        let (measured_zero, _) = RiskPrimitiveV1::from_aligned(
            &forecast,
            &forecast,
            Some((MetricTargetKind::RecordedReference, &zero_reference)),
        )
        .unwrap();
        assert_eq!(
            measured_zero.reference_target_kind,
            Some(MetricTargetKind::RecordedReference)
        );
        assert_eq!(measured_zero.reference_squared_error_sum, Some(0.0));
    }

    #[test]
    fn ept_reconstruction_uses_strict_threshold_zero_origin_and_h_ceiling() {
        // One sample, one origin, H=4, one channel.
        let target = [0.0, 0.0, 0.0, 0.0];
        let scale = [1.0];
        assert_eq!(
            reconstruct_ept(
                &[1.0, 1.01, 9.0, 9.0],
                &target,
                &scale,
                shape(1),
                ExactRatio::new(1, 1).unwrap()
            )
            .unwrap(),
            1.0
        );
        assert_eq!(
            reconstruct_ept(
                &[0.0, 0.0, 0.0, 0.0],
                &target,
                &scale,
                shape(1),
                ExactRatio::new(1, 1).unwrap()
            )
            .unwrap(),
            4.0
        );
        assert_eq!(
            reconstruct_ept(
                &[2.0, 0.0, 0.0, 0.0],
                &target,
                &scale,
                shape(1),
                ExactRatio::new(1, 1).unwrap()
            )
            .unwrap(),
            0.0
        );
    }

    #[test]
    fn ept_family_is_monotone_and_point_differs_from_sample_average() {
        let target = [0.0, 0.0, 0.0, 0.0];
        let scale = [1.0];
        let samples = [0.0, 0.0, 0.0, 0.0, 2.0, 2.0, 2.0, 2.0];
        let point = [1.0, 1.0, 1.0, 1.0];
        let sample_ept = reconstruct_ept(&samples, &target, &scale, shape(2), ExactRatio::new(1, 1).unwrap()).unwrap();
        let point_ept = reconstruct_ept(&point, &target, &scale, shape(1), ExactRatio::new(1, 1).unwrap()).unwrap();
        assert_eq!(sample_ept, 2.0);
        assert_eq!(point_ept, 4.0);

        let curve = [
            ExactRatio::new(1, 1).unwrap(),
            ExactRatio::new(3, 2).unwrap(),
            ExactRatio::new(2, 1).unwrap(),
        ]
        .map(|ratio| reconstruct_ept(&[1.1, 1.6, 2.1, 3.0], &target, &scale, shape(1), ratio).unwrap());
        assert_eq!(curve, [0.0, 1.0, 2.0]);
    }

    #[test]
    fn wd_sorts_horizon_and_ignores_timing_but_not_values() {
        let target = [0.0, 1.0, 2.0, 3.0];
        let reordered = [3.0, 2.0, 1.0, 0.0];
        let collapsed = [1.5, 1.5, 1.5, 1.5];
        assert_eq!(
            reconstruct_per_channel_squared_wd(&reordered, &target, None, shape(1)).unwrap(),
            0.0
        );
        assert!(reconstruct_per_channel_squared_wd(&collapsed, &target, None, shape(1)).unwrap() > 0.0);
    }

    #[test]
    fn compact_case_requires_the_predeclared_ept_family_and_point_wd() {
        let contract = MetricContractV1::time_series_v1(vec![MetricTargetKind::ObservedCorrupted]).unwrap();
        let metrics = vec![
            value(
                ept_spec(MetricTargetKind::ObservedCorrupted, ExactRatio::new(1, 1).unwrap()),
                1.0,
            ),
            value(
                ept_spec(MetricTargetKind::ObservedCorrupted, ExactRatio::new(3, 2).unwrap()),
                2.0,
            ),
            value(
                ept_spec(MetricTargetKind::ObservedCorrupted, ExactRatio::new(2, 1).unwrap()),
                3.0,
            ),
            value(wd_spec(MetricTargetKind::ObservedCorrupted), 0.5),
        ];
        let (risk, diagnostic) = RiskPrimitiveV1::from_aligned(&[0.0], &[1.0], None).unwrap();
        let case = CompactMetricsCaseV1 {
            case_id: ScientificCaseId::parse("case-1").unwrap(),
            case_realization_digest: ScientificContentDigest::from_bytes(b"case-1"),
            risk_primitive: risk,
            derived_diagnostic: diagnostic,
            metric_values: metrics,
        };
        case.validate(&contract, NumericalToleranceV1::f64_default()).unwrap();

        let mut nonmonotone = case;
        nonmonotone.metric_values[1].value = 0.5;
        assert!(
            nonmonotone
                .validate(&contract, NumericalToleranceV1::f64_default())
                .unwrap_err()
                .to_string()
                .contains("non-decreasing")
        );
    }

    #[test]
    fn compact_metrics_binds_attempt_policy_and_every_metric_source() {
        let contract = MetricContractV1::time_series_v1(vec![MetricTargetKind::ObservedCorrupted]).unwrap();
        let metrics = vec![
            value(
                ept_spec(MetricTargetKind::ObservedCorrupted, ExactRatio::new(1, 1).unwrap()),
                1.0,
            ),
            value(
                ept_spec(MetricTargetKind::ObservedCorrupted, ExactRatio::new(3, 2).unwrap()),
                2.0,
            ),
            value(
                ept_spec(MetricTargetKind::ObservedCorrupted, ExactRatio::new(2, 1).unwrap()),
                3.0,
            ),
            value(wd_spec(MetricTargetKind::ObservedCorrupted), 0.5),
        ];
        let (risk, diagnostic) = RiskPrimitiveV1::from_aligned(&[0.0], &[1.0], None).unwrap();
        let compact = CompactMetricsV1 {
            schema: COMPACT_METRICS_SCHEMA.to_string(),
            attempt_id: "attempt-1".to_string(),
            request_digest: ScientificContentDigest::from_bytes(b"request"),
            metric_contract: contract,
            numerical_tolerance: NumericalToleranceV1::f64_default(),
            checked_source_artifacts: vec![
                reference(ArtifactRole::FinalNoisyInput, "observed"),
                reference(ArtifactRole::TrainingScale, "scale"),
                reference(ArtifactRole::DensePointForecast, "point"),
            ],
            cases: vec![CompactMetricsCaseV1 {
                case_id: ScientificCaseId::parse("case-1").unwrap(),
                case_realization_digest: ScientificContentDigest::from_bytes(b"case-1"),
                risk_primitive: risk,
                derived_diagnostic: diagnostic,
                metric_values: metrics,
            }],
        };
        compact.validate().unwrap();
        let first_digest = compact.digest().unwrap();

        let mut reordered = compact.clone();
        reordered.checked_source_artifacts.reverse();
        reordered.cases[0].metric_values.reverse();
        assert_eq!(first_digest, reordered.digest().unwrap());

        let mut missing_source = compact;
        missing_source
            .checked_source_artifacts
            .retain(|source| source.role != ArtifactRole::TrainingScale);
        assert!(
            missing_source
                .validate()
                .unwrap_err()
                .to_string()
                .contains("checked artifact map")
        );
    }

    #[test]
    fn parent_reconstructs_complete_point_metrics_from_declared_artifacts() {
        let contract =
            MetricContractV1::time_series_v1(vec![MetricTargetKind::ObservedCorrupted, MetricTargetKind::LatentClean])
                .unwrap();
        let observed_values = [0.0, 0.0, 0.0, 0.0];
        let clean_values = [0.5, 0.5, 0.5, 0.5];
        let forecast = [0.0, 1.1, 1.6, 2.1];
        let case = reconstruct_point_compact_metrics_case(
            &contract,
            PointMetricReconstructionInputV1 {
                case_id: ScientificCaseId::parse("case-1").unwrap(),
                case_realization_digest: ScientificContentDigest::from_bytes(b"case"),
                point_source: reference(ArtifactRole::DensePointForecast, "point"),
                forecast: &forecast,
                targets: vec![
                    PointMetricTargetInputV1 {
                        target_kind: MetricTargetKind::ObservedCorrupted,
                        artifact: reference(ArtifactRole::FinalNoisyInput, "observed"),
                        values: &observed_values,
                    },
                    PointMetricTargetInputV1 {
                        target_kind: MetricTargetKind::LatentClean,
                        artifact: reference(ArtifactRole::LatentCleanTrajectory, "clean"),
                        values: &clean_values,
                    },
                ],
                training_scale_artifact: reference(ArtifactRole::TrainingScale, "scale"),
                training_scale: &[1.0],
                forecast_origins: 1,
                horizon: 4,
                channels: 1,
            },
        )
        .unwrap();
        assert_eq!(case.metric_values.len(), 8);
        assert!(case.risk_primitive.reference_squared_error_sum.is_some());
        assert!(
            NumericalToleranceV1::f64_default().contains(case.derived_diagnostic.ledger_residual_sum.unwrap(), 0.0)
        );

        let missing_clean_contract =
            MetricContractV1::time_series_v1(vec![MetricTargetKind::ObservedCorrupted, MetricTargetKind::LatentClean])
                .unwrap();
        let error = reconstruct_point_compact_metrics_case(
            &missing_clean_contract,
            PointMetricReconstructionInputV1 {
                case_id: ScientificCaseId::parse("case-1").unwrap(),
                case_realization_digest: ScientificContentDigest::from_bytes(b"case"),
                point_source: reference(ArtifactRole::DensePointForecast, "point"),
                forecast: &forecast,
                targets: vec![PointMetricTargetInputV1 {
                    target_kind: MetricTargetKind::ObservedCorrupted,
                    artifact: reference(ArtifactRole::FinalNoisyInput, "observed"),
                    values: &observed_values,
                }],
                training_scale_artifact: reference(ArtifactRole::TrainingScale, "scale"),
                training_scale: &[1.0],
                forecast_origins: 1,
                horizon: 4,
                channels: 1,
            },
        )
        .unwrap_err()
        .to_string();
        assert!(error.contains("missing required target"), "got: {error}");
    }

    #[test]
    fn recorded_reference_supports_additional_corruption_ledger_without_claiming_latent_truth() {
        let contract = MetricContractV1::time_series_v1(vec![
            MetricTargetKind::ObservedCorrupted,
            MetricTargetKind::RecordedReference,
        ])
        .unwrap();
        let observed = [2.0, 1.0];
        let recorded = [1.0, 1.0];
        let forecast = [0.0, 1.0];
        let case = reconstruct_point_compact_metrics_case(
            &contract,
            PointMetricReconstructionInputV1 {
                case_id: ScientificCaseId::parse("case-recorded").unwrap(),
                case_realization_digest: ScientificContentDigest::from_bytes(b"recorded-case"),
                point_source: reference(ArtifactRole::DensePointForecast, "point-recorded"),
                forecast: &forecast,
                targets: vec![
                    PointMetricTargetInputV1 {
                        target_kind: MetricTargetKind::ObservedCorrupted,
                        artifact: reference(ArtifactRole::FinalNoisyInput, "observed-recorded"),
                        values: &observed,
                    },
                    PointMetricTargetInputV1 {
                        target_kind: MetricTargetKind::RecordedReference,
                        artifact: reference(ArtifactRole::RecordedReferenceTrajectory, "recorded-reference"),
                        values: &recorded,
                    },
                ],
                training_scale_artifact: reference(ArtifactRole::TrainingScale, "scale-recorded"),
                training_scale: &[1.0],
                forecast_origins: 1,
                horizon: 2,
                channels: 1,
            },
        )
        .unwrap();
        assert_eq!(
            case.risk_primitive.reference_target_kind,
            Some(MetricTargetKind::RecordedReference)
        );
        assert_eq!(case.risk_primitive.reference_squared_error_sum, Some(1.0));
        assert!(
            NumericalToleranceV1::f64_default().contains(case.derived_diagnostic.ledger_residual_sum.unwrap(), 0.0)
        );

        let error = MetricContractV1::time_series_v1(vec![
            MetricTargetKind::ObservedCorrupted,
            MetricTargetKind::RecordedReference,
            MetricTargetKind::LatentClean,
        ])
        .unwrap_err()
        .to_string();
        assert!(error.contains("but not both"), "got: {error}");
    }
}
