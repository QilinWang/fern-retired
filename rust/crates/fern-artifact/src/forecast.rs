//! Semantic contract for dense forecast tensors.
//!
//! Shape alone cannot tell whether a point tensor is a native model head, a
//! sample mean, or a median.  These types bind that meaning to the artifact
//! report and therefore to the worker result and Attempt commit.

use std::collections::HashSet;

use serde::{Deserialize, Serialize};

use super::{
    ArtifactReference, ArtifactRole, ContractError, ExactRatio, ModelImplementationHash, ScientificCaseReference,
    TensorLayout,
};

pub const REPORTED_FORECAST_MEANING_SCHEMA: &str = "fern.reported_forecast_meaning.v1";
pub const DECLARED_FORECAST_OUTPUT_SCHEMA: &str = "fern.declared_forecast_output.v1";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ForecastValueSpace {
    Physical,
    TrainingStandardized,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum PointKind {
    ConditionalMean,
    ConditionalMedian,
    NativePointHead,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum SampleStatisticV1 {
    Mean,
    /// Lower middle order statistic, matching `torch.median(dim=...)` when
    /// the sample count is even.  The wire name is explicit so a future
    /// interpolated median cannot share this identity.
    LowerMedian,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(tag = "operation", rename_all = "snake_case", deny_unknown_fields)]
pub enum ReportedPointDerivationV1 {
    NativePointHead {
        producer_implementation: ModelImplementationHash,
    },
    PredictiveSampleReduction {
        source: ArtifactReference,
        statistic: SampleStatisticV1,
        derivation_version: u32,
    },
    ExactQuantileSelection {
        source: ArtifactReference,
        quantile: ExactRatio,
        derivation_version: u32,
    },
}

/// The point derivation known before a worker starts.
///
/// A request can name the logical source artifact, but it cannot know that
/// artifact's content digest yet.  The result-side [`ReportedPointDerivationV1`]
/// supplies the checked digest after the source file exists.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(tag = "operation", rename_all = "snake_case", deny_unknown_fields)]
pub enum DeclaredPointDerivationV1 {
    NativePointHead {
        producer_implementation: ModelImplementationHash,
    },
    PredictiveSampleReduction {
        source_artifact_key: super::ArtifactKey,
        statistic: SampleStatisticV1,
        derivation_version: u32,
    },
    ExactQuantileSelection {
        source_artifact_key: super::ArtifactKey,
        quantile: ExactRatio,
        derivation_version: u32,
    },
}

impl DeclaredPointDerivationV1 {
    fn validate(&self, point_kind: PointKind) -> Result<(), ContractError> {
        match self {
            Self::NativePointHead { .. } if point_kind == PointKind::NativePointHead => Ok(()),
            Self::PredictiveSampleReduction {
                statistic,
                derivation_version,
                ..
            } if matches!(
                (point_kind, statistic),
                (PointKind::ConditionalMean, SampleStatisticV1::Mean)
                    | (PointKind::ConditionalMedian, SampleStatisticV1::LowerMedian)
            ) =>
            {
                require_derivation_version(*derivation_version)
            }
            Self::ExactQuantileSelection {
                quantile,
                derivation_version,
                ..
            } if point_kind == PointKind::ConditionalMedian => {
                require_derivation_version(*derivation_version)?;
                if *quantile != ExactRatio::new(1, 2)? {
                    return Err(ContractError::new(
                        "declared_forecast_output.point_derivation.quantile",
                        "V1 conditional_median requires the exact reduced quantile 1/2",
                    ));
                }
                Ok(())
            }
            _ => Err(ContractError::new(
                "declared_forecast_output.point_derivation",
                "point kind and declared derivation operation disagree",
            )),
        }
    }

    pub const fn source_role(&self) -> Option<ArtifactRole> {
        match self {
            Self::NativePointHead { .. } => None,
            Self::PredictiveSampleReduction { .. } => Some(ArtifactRole::PredictiveSamples),
            Self::ExactQuantileSelection { .. } => Some(ArtifactRole::QuantileForecast),
        }
    }

    pub const fn source_artifact_key(&self) -> Option<&super::ArtifactKey> {
        match self {
            Self::NativePointHead { .. } => None,
            Self::PredictiveSampleReduction {
                source_artifact_key, ..
            }
            | Self::ExactQuantileSelection {
                source_artifact_key, ..
            } => Some(source_artifact_key),
        }
    }
}

impl ReportedPointDerivationV1 {
    fn validate(&self, point_kind: PointKind) -> Result<(), ContractError> {
        match self {
            Self::NativePointHead { .. } if point_kind == PointKind::NativePointHead => Ok(()),
            Self::PredictiveSampleReduction {
                source,
                statistic,
                derivation_version,
            } if matches!(
                (point_kind, statistic),
                (PointKind::ConditionalMean, SampleStatisticV1::Mean)
                    | (PointKind::ConditionalMedian, SampleStatisticV1::LowerMedian)
            ) =>
            {
                require_derivation_version(*derivation_version)?;
                if source.role != ArtifactRole::PredictiveSamples {
                    return Err(ContractError::new(
                        "reported_forecast_meaning.point_derivation.source",
                        "predictive_sample_reduction must reference predictive_samples",
                    ));
                }
                Ok(())
            }
            Self::ExactQuantileSelection {
                source,
                quantile,
                derivation_version,
            } if point_kind == PointKind::ConditionalMedian => {
                require_derivation_version(*derivation_version)?;
                if source.role != ArtifactRole::QuantileForecast {
                    return Err(ContractError::new(
                        "reported_forecast_meaning.point_derivation.source",
                        "exact_quantile_selection must reference quantile_forecast",
                    ));
                }
                if *quantile != ExactRatio::new(1, 2)? {
                    return Err(ContractError::new(
                        "reported_forecast_meaning.point_derivation.quantile",
                        "V1 conditional_median requires the exact reduced quantile 1/2",
                    ));
                }
                Ok(())
            }
            _ => Err(ContractError::new(
                "reported_forecast_meaning.point_derivation",
                "point kind and derivation operation disagree",
            )),
        }
    }
}

fn require_derivation_version(version: u32) -> Result<(), ContractError> {
    if version != 1 {
        return Err(ContractError::new(
            "reported_forecast_meaning.derivation_version",
            format!("V1 requires derivation_version=1, found {version}"),
        ));
    }
    Ok(())
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum ForecastMeaningV1 {
    DensePoint {
        point_kind: PointKind,
        derivation: ReportedPointDerivationV1,
    },
    Quantiles {
        levels: Vec<ExactRatio>,
    },
    PredictiveSamples {
        sample_count: u64,
        sampling_contract_version: u32,
    },
}

/// Result-independent meaning declared by the parent before worker launch.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum DeclaredForecastMeaningV1 {
    DensePoint {
        point_kind: PointKind,
        derivation: DeclaredPointDerivationV1,
    },
    Quantiles {
        levels: Vec<ExactRatio>,
    },
    PredictiveSamples {
        sample_count: u64,
        sampling_contract_version: u32,
    },
}

/// Forecast facts that are knowable before output bytes exist.
///
/// This mirrors [`ReportedForecastMeaningV1`] except that a derived point
/// names its source by predeclared logical key rather than pretending to know
/// the future source content digest.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct DeclaredForecastOutputV1 {
    pub schema: String,
    pub scientific_cases: Vec<ScientificCaseReference>,
    pub forecast_origin_plan: ArtifactReference,
    pub channel_order: Vec<String>,
    pub value_space: ForecastValueSpace,
    pub scale_reference: Option<ArtifactReference>,
    pub meaning: DeclaredForecastMeaningV1,
}

impl DeclaredForecastOutputV1 {
    pub fn validate_for(&self, role: ArtifactRole, layout: &TensorLayout) -> Result<(), ContractError> {
        if self.schema != DECLARED_FORECAST_OUTPUT_SCHEMA {
            return Err(ContractError::new(
                "declared_forecast_output.schema",
                format!("expected {DECLARED_FORECAST_OUTPUT_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        validate_shared_forecast_fields(
            &self.scientific_cases,
            &self.forecast_origin_plan,
            &self.channel_order,
            self.value_space,
            self.scale_reference.as_ref(),
            layout,
            "declared_forecast_output",
        )?;
        match (&self.meaning, role) {
            (DeclaredForecastMeaningV1::DensePoint { point_kind, derivation }, ArtifactRole::DensePointForecast) => {
                derivation.validate(*point_kind)
            }
            (DeclaredForecastMeaningV1::Quantiles { levels }, ArtifactRole::QuantileForecast) => {
                validate_quantile_levels(levels, layout, "declared_forecast_output.quantiles.levels")
            }
            (
                DeclaredForecastMeaningV1::PredictiveSamples {
                    sample_count,
                    sampling_contract_version,
                },
                ArtifactRole::PredictiveSamples,
            ) => validate_sample_contract(
                *sample_count,
                *sampling_contract_version,
                layout,
                "declared_forecast_output.predictive_samples",
            ),
            _ => Err(ContractError::new(
                "declared_forecast_output.meaning",
                format!("meaning does not match artifact role {}", role.as_str()),
            )),
        }
    }

    /// Prove that the result-side report implements this exact request and, for
    /// a derived point, binds the checked source artifact rather than merely
    /// repeating its name.
    pub fn matches_reported(
        &self,
        role: ArtifactRole,
        layout: &TensorLayout,
        reported: &ReportedForecastMeaningV1,
        source: Option<&super::ReportedArtifactV1>,
    ) -> Result<(), ContractError> {
        self.validate_for(role, layout)?;
        reported.validate_for(role, layout)?;
        if self.scientific_cases != reported.scientific_cases
            || self.forecast_origin_plan != reported.forecast_origin_plan
            || self.channel_order != reported.channel_order
            || self.value_space != reported.value_space
            || self.scale_reference != reported.scale_reference
        {
            return Err(ContractError::new(
                "declared_forecast_output.shared_fields",
                "reported forecast case/origin/channel/value-space fields differ from the request",
            ));
        }
        match (&self.meaning, &reported.meaning) {
            (
                DeclaredForecastMeaningV1::DensePoint {
                    point_kind: expected_kind,
                    derivation: expected,
                },
                ForecastMeaningV1::DensePoint {
                    point_kind: reported_kind,
                    derivation: reported,
                },
            ) if expected_kind == reported_kind => match_reported_point_derivation(expected, reported, source),
            (
                DeclaredForecastMeaningV1::Quantiles { levels: expected },
                ForecastMeaningV1::Quantiles { levels: observed },
            ) if expected == observed => Ok(()),
            (
                DeclaredForecastMeaningV1::PredictiveSamples {
                    sample_count: expected_count,
                    sampling_contract_version: expected_version,
                },
                ForecastMeaningV1::PredictiveSamples {
                    sample_count: observed_count,
                    sampling_contract_version: observed_version,
                },
            ) if expected_count == observed_count && expected_version == observed_version => Ok(()),
            _ => Err(ContractError::new(
                "declared_forecast_output.meaning",
                "observed forecast meaning differs from the predeclared contract",
            )),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ReportedForecastMeaningV1 {
    pub schema: String,
    pub scientific_cases: Vec<ScientificCaseReference>,
    pub forecast_origin_plan: ArtifactReference,
    pub channel_order: Vec<String>,
    pub value_space: ForecastValueSpace,
    pub scale_reference: Option<ArtifactReference>,
    pub meaning: ForecastMeaningV1,
}

impl ReportedForecastMeaningV1 {
    pub fn validate_for(&self, role: ArtifactRole, layout: &TensorLayout) -> Result<(), ContractError> {
        if self.schema != REPORTED_FORECAST_MEANING_SCHEMA {
            return Err(ContractError::new(
                "reported_forecast_meaning.schema",
                format!("expected {REPORTED_FORECAST_MEANING_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        validate_shared_forecast_fields(
            &self.scientific_cases,
            &self.forecast_origin_plan,
            &self.channel_order,
            self.value_space,
            self.scale_reference.as_ref(),
            layout,
            "reported_forecast_meaning",
        )?;
        match (&self.meaning, role) {
            (ForecastMeaningV1::DensePoint { point_kind, derivation }, ArtifactRole::DensePointForecast) => {
                derivation.validate(*point_kind)?
            }
            (ForecastMeaningV1::Quantiles { levels }, ArtifactRole::QuantileForecast) => {
                validate_quantile_levels(levels, layout, "reported_forecast_meaning.quantiles.levels")?;
            }
            (
                ForecastMeaningV1::PredictiveSamples {
                    sample_count,
                    sampling_contract_version,
                },
                ArtifactRole::PredictiveSamples,
            ) => {
                validate_sample_contract(
                    *sample_count,
                    *sampling_contract_version,
                    layout,
                    "reported_forecast_meaning.predictive_samples",
                )?;
            }
            _ => {
                return Err(ContractError::new(
                    "reported_forecast_meaning.meaning",
                    format!("meaning does not match artifact role {}", role.as_str()),
                ));
            }
        }
        Ok(())
    }
}

#[allow(clippy::too_many_arguments)]
fn validate_shared_forecast_fields(
    scientific_cases: &[ScientificCaseReference],
    forecast_origin_plan: &ArtifactReference,
    channel_order: &[String],
    value_space: ForecastValueSpace,
    scale_reference: Option<&ArtifactReference>,
    layout: &TensorLayout,
    field: &str,
) -> Result<(), ContractError> {
    if scientific_cases.is_empty() {
        return Err(ContractError::new(
            format!("{field}.scientific_cases"),
            "must name at least one ScientificCase",
        ));
    }
    let mut case_ids = HashSet::new();
    for case in scientific_cases {
        if !case_ids.insert(case.case_id.as_str()) {
            return Err(ContractError::new(
                format!("{field}.scientific_cases"),
                "contains a duplicate case_id",
            ));
        }
    }
    require_axis_length(
        layout,
        "case",
        scientific_cases.len() as u64,
        &format!("{field}.scientific_cases"),
    )?;
    if forecast_origin_plan.role != ArtifactRole::ForecastOriginPlan {
        return Err(ContractError::new(
            format!("{field}.forecast_origin_plan"),
            "must reference the canonical forecast_origin_plan artifact",
        ));
    }
    if channel_order.is_empty() {
        return Err(ContractError::new(
            format!("{field}.channel_order"),
            "must name at least one channel",
        ));
    }
    let mut channels = HashSet::new();
    if channel_order
        .iter()
        .any(|name| name.is_empty() || !channels.insert(name))
    {
        return Err(ContractError::new(
            format!("{field}.channel_order"),
            "must contain non-empty unique channel names",
        ));
    }
    require_axis_length(
        layout,
        "channel",
        channel_order.len() as u64,
        &format!("{field}.channel_order"),
    )?;
    match (value_space, scale_reference) {
        (ForecastValueSpace::Physical, None) => Ok(()),
        (ForecastValueSpace::TrainingStandardized, Some(scale)) if scale.role == ArtifactRole::TrainingScale => Ok(()),
        (ForecastValueSpace::Physical, Some(_)) => Err(ContractError::new(
            format!("{field}.scale_reference"),
            "physical values must not declare a scale artifact",
        )),
        (ForecastValueSpace::TrainingStandardized, _) => Err(ContractError::new(
            format!("{field}.scale_reference"),
            "training_standardized values require training_scale",
        )),
    }
}

fn validate_quantile_levels(levels: &[ExactRatio], layout: &TensorLayout, field: &str) -> Result<(), ContractError> {
    if levels.is_empty()
        || levels
            .iter()
            .any(|level| level.numerator == 0 || level.numerator >= level.denominator)
        || levels.windows(2).any(|pair| !ratio_is_strictly_less(pair[0], pair[1]))
    {
        return Err(ContractError::new(
            field,
            "must be a non-empty strictly increasing list of exact probabilities in (0,1)",
        ));
    }
    require_axis_length(layout, "quantile", levels.len() as u64, field)
}

fn validate_sample_contract(
    sample_count: u64,
    sampling_contract_version: u32,
    layout: &TensorLayout,
    field: &str,
) -> Result<(), ContractError> {
    if sample_count == 0 || sampling_contract_version != 1 {
        return Err(ContractError::new(
            field,
            "requires a positive sample_count and sampling_contract_version=1",
        ));
    }
    require_axis_length(layout, "sample", sample_count, &format!("{field}.sample_count"))
}

fn match_reported_point_derivation(
    expected: &DeclaredPointDerivationV1,
    reported: &ReportedPointDerivationV1,
    source: Option<&super::ReportedArtifactV1>,
) -> Result<(), ContractError> {
    match (expected, reported) {
        (
            DeclaredPointDerivationV1::NativePointHead {
                producer_implementation: expected,
            },
            ReportedPointDerivationV1::NativePointHead {
                producer_implementation: observed,
            },
        ) if expected == observed => {
            if source.is_some() {
                return Err(ContractError::new(
                    "declared_forecast_output.point_derivation.source",
                    "native point head must not claim a source forecast artifact",
                ));
            }
            Ok(())
        }
        (
            DeclaredPointDerivationV1::PredictiveSampleReduction {
                source_artifact_key,
                statistic: expected_statistic,
                derivation_version: expected_version,
            },
            ReportedPointDerivationV1::PredictiveSampleReduction {
                source: observed_source,
                statistic: observed_statistic,
                derivation_version: observed_version,
            },
        ) if expected_statistic == observed_statistic && expected_version == observed_version => {
            validate_reported_source_artifact(
                source_artifact_key,
                ArtifactRole::PredictiveSamples,
                observed_source,
                source,
            )
        }
        (
            DeclaredPointDerivationV1::ExactQuantileSelection {
                source_artifact_key,
                quantile: expected_quantile,
                derivation_version: expected_version,
            },
            ReportedPointDerivationV1::ExactQuantileSelection {
                source: observed_source,
                quantile: observed_quantile,
                derivation_version: observed_version,
            },
        ) if expected_quantile == observed_quantile && expected_version == observed_version => {
            validate_reported_source_artifact(
                source_artifact_key,
                ArtifactRole::QuantileForecast,
                observed_source,
                source,
            )
        }
        _ => Err(ContractError::new(
            "declared_forecast_output.point_derivation",
            "observed point derivation differs from the predeclared operation",
        )),
    }
}

fn validate_reported_source_artifact(
    expected_key: &super::ArtifactKey,
    expected_role: ArtifactRole,
    observed_source: &ArtifactReference,
    source: Option<&super::ReportedArtifactV1>,
) -> Result<(), ContractError> {
    let source = source.ok_or_else(|| {
        ContractError::new(
            "declared_forecast_output.point_derivation.source",
            "derived point requires the checked source artifact report",
        )
    })?;
    if &source.artifact_key != expected_key
        || source.role != expected_role
        || observed_source.artifact_key != source.artifact_key
        || observed_source.role != source.role
        || observed_source.content_digest != source.content_digest
    {
        return Err(ContractError::new(
            "declared_forecast_output.point_derivation.source",
            "reported source reference does not match the declared key and checked source artifact",
        ));
    }
    Ok(())
}

fn ratio_is_strictly_less(left: ExactRatio, right: ExactRatio) -> bool {
    u128::from(left.numerator) * u128::from(right.denominator)
        < u128::from(right.numerator) * u128::from(left.denominator)
}

fn require_axis_length(
    layout: &TensorLayout,
    axis_name: &str,
    expected: u64,
    field: &str,
) -> Result<(), ContractError> {
    let observed = layout
        .axes
        .iter()
        .find(|axis| axis.name == axis_name)
        .map(|axis| axis.length)
        .ok_or_else(|| ContractError::new(field, format!("layout has no {axis_name:?} axis")))?;
    if observed != expected {
        return Err(ContractError::new(
            field,
            format!("declares {expected} values but the tensor axis has length {observed}"),
        ));
    }
    Ok(())
}
