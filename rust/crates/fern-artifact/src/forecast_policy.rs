//! Pre-spawn forecast capability and protocol resolution.
//!
//! A model menu says what one implementation can emit.  A protocol order says which
//! point functional and native probability evidence the experiment requires.
//! This module joins those declarations before a worker starts.  It does not
//! inspect tensor bytes, choose a scientifically convenient fallback, or infer
//! capability from a model name.

use std::collections::HashSet;

use fern_experiment_types::{
    ArchitectureNativeOutputV1, ModelArchitectureCardV1, ModelArchitectureHash, ModelImplementationCardV1,
    ModelImplementationHash,
};
use serde::{Deserialize, Serialize};

use super::{ArtifactRole, ContractError, ExactRatio, PointKind, SampleStatisticV1};

// Rust names tell the new story; the already-issued wire vocabulary remains
// stable so this readability pass does not become a data migration.
pub const FORECAST_MENU_SCHEMA: &str = "fern.forecast_menu.v1";
pub const FORECAST_ORDER_SCHEMA: &str = "fern.forecast_order.v1";
pub const CONFIRMED_FORECAST_ORDER_SCHEMA: &str = "fern.confirmed_forecast_order.v1";

/// One exact point-producing route offered by an architecture implementation.
///
/// Several routes may coexist.  For example, a probabilistic model may expose
/// both a native point head and predictive samples.  The protocol must select
/// one route explicitly; confirmation never chooses whichever looks nicest.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(tag = "route", rename_all = "snake_case", deny_unknown_fields)]
pub enum PointRouteCapabilityV1 {
    NativePointHead,
    ExactQuantileSelection {
        levels: Vec<ExactRatio>,
    },
    PredictiveSampleReduction {
        sample_count: u64,
        supported_statistics: Vec<SampleStatisticV1>,
    },
}

/// Native probability evidence a producer can retain.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum NativeProbabilityCapabilityV1 {
    Quantiles { levels: Vec<ExactRatio> },
    PredictiveSamples { sample_count: u64 },
}

/// Versioned implementation-side declaration.  It is content, not a hard-coded
/// match over Fern's built-in model names, so an external plugin can supply a
/// new card without changing lifecycle or artifact code.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ForecastMenuV1 {
    schema: String,
    model_architecture: ModelArchitectureHash,
    model_implementation: ModelImplementationHash,
    point_routes: Vec<PointRouteCapabilityV1>,
    native_probability: Vec<NativeProbabilityCapabilityV1>,
}

impl ForecastMenuV1 {
    /// Bind exact output details to capabilities derived from the canonical
    /// architecture. Callers may choose sample count or retained quantile
    /// levels, but cannot claim an output family the architecture lacks.
    pub fn from_implementation(
        architecture: &ModelArchitectureCardV1,
        implementation: &ModelImplementationCardV1,
        point_routes: Vec<PointRouteCapabilityV1>,
        native_probability: Vec<NativeProbabilityCapabilityV1>,
    ) -> Result<Self, ContractError> {
        let architecture_hash = architecture
            .architecture_hash()
            .map_err(|error| ContractError::new("forecast_menu.model_architecture", error.to_string()))?;
        if implementation.architecture_hash() != &architecture_hash {
            return Err(ContractError::new(
                "forecast_menu.model_implementation",
                "the implementation belongs to a different model architecture",
            ));
        }
        let architecture_capabilities = architecture.capabilities();
        for route in &point_routes {
            let supported = match route {
                PointRouteCapabilityV1::NativePointHead => architecture_capabilities.has_native_point_head,
                PointRouteCapabilityV1::ExactQuantileSelection { .. } => {
                    architecture_capabilities.native_output == ArchitectureNativeOutputV1::Quantiles
                }
                PointRouteCapabilityV1::PredictiveSampleReduction { .. } => {
                    architecture_capabilities.native_output == ArchitectureNativeOutputV1::PredictiveSamples
                }
            };
            if !supported {
                return Err(ContractError::new(
                    "forecast_menu.point_routes",
                    format!("route {route:?} is not supported by the canonical architecture"),
                ));
            }
        }
        for native in &native_probability {
            let supported = matches!(
                (native, architecture_capabilities.native_output),
                (
                    NativeProbabilityCapabilityV1::Quantiles { .. },
                    ArchitectureNativeOutputV1::Quantiles
                ) | (
                    NativeProbabilityCapabilityV1::PredictiveSamples { .. },
                    ArchitectureNativeOutputV1::PredictiveSamples
                )
            );
            if !supported {
                return Err(ContractError::new(
                    "forecast_menu.native_probability",
                    format!("native output {native:?} is not supported by the canonical architecture"),
                ));
            }
        }
        let menu = Self {
            schema: FORECAST_MENU_SCHEMA.to_string(),
            model_architecture: architecture_hash,
            model_implementation: implementation
                .implementation_hash()
                .map_err(|error| ContractError::new("forecast_menu.model_implementation", error.to_string()))?,
            point_routes,
            native_probability,
        };
        menu.validate()?;
        Ok(menu)
    }

    pub fn model_architecture(&self) -> &ModelArchitectureHash {
        &self.model_architecture
    }

    pub fn model_implementation(&self) -> &ModelImplementationHash {
        &self.model_implementation
    }

    pub fn validate(&self) -> Result<(), ContractError> {
        if self.schema != FORECAST_MENU_SCHEMA {
            return Err(ContractError::new(
                "forecast_menu.schema",
                format!("expected {FORECAST_MENU_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        if self.point_routes.is_empty() {
            return Err(ContractError::new(
                "forecast_menu.point_routes",
                "must declare at least one exact point-producing route",
            ));
        }
        let mut routes = HashSet::new();
        for route in &self.point_routes {
            validate_point_route(route, "forecast_menu.point_routes")?;
            let canonical = serde_json::to_vec(route).map_err(|error| {
                ContractError::new(
                    "forecast_menu.point_routes",
                    format!("cannot canonicalize route: {error}"),
                )
            })?;
            if !routes.insert(canonical) {
                return Err(ContractError::new(
                    "forecast_menu.point_routes",
                    "contains a duplicate point-producing route",
                ));
            }
        }
        let mut native_capabilities = HashSet::new();
        for native in &self.native_probability {
            validate_native_capability(native)?;
            let canonical = serde_json::to_vec(native).map_err(|error| {
                ContractError::new(
                    "forecast_menu.native_probability",
                    format!("cannot canonicalize native capability: {error}"),
                )
            })?;
            if !native_capabilities.insert(canonical) {
                return Err(ContractError::new(
                    "forecast_menu.native_probability",
                    "contains a duplicate native probability capability",
                ));
            }
        }
        for route in &self.point_routes {
            match route {
                PointRouteCapabilityV1::NativePointHead => {}
                PointRouteCapabilityV1::ExactQuantileSelection { levels } => {
                    let retained = self.native_probability.iter().any(|native| {
                        matches!(native, NativeProbabilityCapabilityV1::Quantiles { levels: retained }
                            if retained == levels)
                    });
                    if !retained {
                        return Err(ContractError::new(
                            "forecast_menu.point_routes",
                            "a quantile point route requires retained native quantiles with the exact same source levels",
                        ));
                    }
                }
                PointRouteCapabilityV1::PredictiveSampleReduction { sample_count, .. } => {
                    let retained = self.native_probability.iter().any(|native| {
                        matches!(native, NativeProbabilityCapabilityV1::PredictiveSamples { sample_count: retained }
                            if retained == sample_count)
                    });
                    if !retained {
                        return Err(ContractError::new(
                            "forecast_menu.point_routes",
                            "a sample-reduction point route requires retained native samples with the exact same source count",
                        ));
                    }
                }
            }
        }
        Ok(())
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(tag = "route", rename_all = "snake_case", deny_unknown_fields)]
pub enum RequiredPointRouteV1 {
    NativePointHead,
    ExactQuantileSelection {
        quantile: ExactRatio,
        source_levels: Vec<ExactRatio>,
    },
    PredictiveSampleReduction {
        statistic: SampleStatisticV1,
        source_sample_count: u64,
    },
}

impl RequiredPointRouteV1 {
    pub const fn point_kind(&self) -> PointKind {
        match self {
            Self::NativePointHead => PointKind::NativePointHead,
            Self::ExactQuantileSelection { .. } => PointKind::ConditionalMedian,
            Self::PredictiveSampleReduction {
                statistic: SampleStatisticV1::Mean,
                ..
            } => PointKind::ConditionalMean,
            Self::PredictiveSampleReduction {
                statistic: SampleStatisticV1::LowerMedian,
                ..
            } => PointKind::ConditionalMedian,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum NativeProbabilityOrderV1 {
    None,
    Quantiles { retained_levels: Vec<ExactRatio> },
    PredictiveSamples { sample_count: u64 },
}

/// Current compact cross-model policy.  More policies get new enum variants;
/// they do not become unvalidated strings or booleans.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum CompactStatisticsRequirementV1 {
    PointEptWdAndRiskLedgerV1,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ForecastOrderV1 {
    pub schema: String,
    pub point: RequiredPointRouteV1,
    pub native_probability: NativeProbabilityOrderV1,
    pub compact_statistics: CompactStatisticsRequirementV1,
}

impl ForecastOrderV1 {
    pub fn validate(&self) -> Result<(), ContractError> {
        if self.schema != FORECAST_ORDER_SCHEMA {
            return Err(ContractError::new(
                "forecast_order.schema",
                format!("expected {FORECAST_ORDER_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        match &self.point {
            RequiredPointRouteV1::NativePointHead => {}
            RequiredPointRouteV1::ExactQuantileSelection {
                quantile,
                source_levels,
            } => {
                if *quantile != ExactRatio::new(1, 2)? {
                    return Err(ContractError::new(
                        "forecast_order.point.quantile",
                        "V1 quantile point selection requires the exact median 1/2",
                    ));
                }
                validate_levels(source_levels, "forecast_order.point.source_levels")?;
                if !source_levels.contains(quantile) {
                    return Err(ContractError::new(
                        "forecast_order.point.source_levels",
                        "must retain the exact selected quantile",
                    ));
                }
            }
            RequiredPointRouteV1::PredictiveSampleReduction {
                source_sample_count, ..
            } if *source_sample_count == 0 => {
                return Err(ContractError::new(
                    "forecast_order.point.source_sample_count",
                    "must be positive",
                ));
            }
            RequiredPointRouteV1::PredictiveSampleReduction { .. } => {}
        }
        match &self.native_probability {
            NativeProbabilityOrderV1::None => {}
            NativeProbabilityOrderV1::Quantiles { retained_levels } => {
                validate_levels(retained_levels, "forecast_order.native_probability.retained_levels")?;
            }
            NativeProbabilityOrderV1::PredictiveSamples { sample_count } => {
                if *sample_count == 0 {
                    return Err(ContractError::new(
                        "forecast_order.native_probability.sample_count",
                        "must be positive",
                    ));
                }
            }
        }
        Ok(())
    }
}

/// Concrete pre-spawn answer.  Layout dimensions and artifact keys are added
/// later by the ScientificCase-aware planner; this object only resolves which
/// semantic outputs must exist.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ConfirmedForecastOrderV1 {
    pub schema: String,
    pub model_architecture: ModelArchitectureHash,
    pub model_implementation: ModelImplementationHash,
    pub point: RequiredPointRouteV1,
    pub point_kind: PointKind,
    pub native_probability: NativeProbabilityOrderV1,
    pub required_artifact_roles: Vec<ArtifactRole>,
    pub compact_statistics: CompactStatisticsRequirementV1,
}

pub fn confirm_forecast_order(
    menu: &ForecastMenuV1,
    order: &ForecastOrderV1,
) -> Result<ConfirmedForecastOrderV1, ContractError> {
    menu.validate()?;
    order.validate()?;
    if !menu_supports_point(menu, &order.point) {
        return Err(ContractError::new(
            "forecast_confirmation.point",
            "the ordered point route, including its exact source shape, is absent from this implementation menu; Fern will not substitute another functional",
        ));
    }
    if !menu_supports_probability(&menu.native_probability, &order.native_probability) {
        return Err(ContractError::new(
            "forecast_confirmation.native_probability",
            "the implementation cannot supply the exact native probability evidence ordered by this protocol",
        ));
    }

    let mut required_artifact_roles = vec![ArtifactRole::DensePointForecast, ArtifactRole::CompactMetrics];
    match order.point {
        RequiredPointRouteV1::NativePointHead => {}
        RequiredPointRouteV1::ExactQuantileSelection { .. } => {
            required_artifact_roles.push(ArtifactRole::QuantileForecast);
        }
        RequiredPointRouteV1::PredictiveSampleReduction { .. } => {
            required_artifact_roles.push(ArtifactRole::PredictiveSamples);
        }
    }
    match order.native_probability {
        NativeProbabilityOrderV1::None => {}
        NativeProbabilityOrderV1::Quantiles { .. } => {
            required_artifact_roles.push(ArtifactRole::QuantileForecast);
        }
        NativeProbabilityOrderV1::PredictiveSamples { .. } => {
            required_artifact_roles.push(ArtifactRole::PredictiveSamples);
        }
    }
    required_artifact_roles.sort();
    required_artifact_roles.dedup();

    Ok(ConfirmedForecastOrderV1 {
        schema: CONFIRMED_FORECAST_ORDER_SCHEMA.to_string(),
        model_architecture: menu.model_architecture.clone(),
        model_implementation: menu.model_implementation.clone(),
        point: order.point.clone(),
        point_kind: order.point.point_kind(),
        native_probability: order.native_probability.clone(),
        required_artifact_roles,
        compact_statistics: order.compact_statistics,
    })
}

fn validate_point_route(route: &PointRouteCapabilityV1, field: &str) -> Result<(), ContractError> {
    match route {
        PointRouteCapabilityV1::NativePointHead => Ok(()),
        PointRouteCapabilityV1::ExactQuantileSelection { levels } => validate_levels(levels, field),
        PointRouteCapabilityV1::PredictiveSampleReduction {
            sample_count,
            supported_statistics,
        } => {
            if *sample_count == 0 {
                return Err(ContractError::new(field, "sample_count must be positive"));
            }
            if supported_statistics.is_empty() {
                return Err(ContractError::new(field, "must declare at least one sample reduction"));
            }
            let unique = supported_statistics.iter().copied().collect::<HashSet<_>>();
            if unique.len() != supported_statistics.len() {
                return Err(ContractError::new(field, "contains a duplicate sample reduction"));
            }
            Ok(())
        }
    }
}

fn validate_native_capability(capability: &NativeProbabilityCapabilityV1) -> Result<(), ContractError> {
    match capability {
        NativeProbabilityCapabilityV1::Quantiles { levels } => {
            validate_levels(levels, "forecast_capability.native_probability.levels")
        }
        NativeProbabilityCapabilityV1::PredictiveSamples { sample_count } => {
            if *sample_count == 0 {
                return Err(ContractError::new(
                    "forecast_capability.native_probability.sample_count",
                    "must be positive",
                ));
            }
            Ok(())
        }
    }
}

fn validate_levels(levels: &[ExactRatio], field: &str) -> Result<(), ContractError> {
    if levels.is_empty() {
        return Err(ContractError::new(field, "must not be empty"));
    }
    if levels
        .iter()
        .any(|level| level.numerator == 0 || level.numerator >= level.denominator)
    {
        return Err(ContractError::new(
            field,
            "all probability levels must be strictly between zero and one",
        ));
    }
    if levels.windows(2).any(|pair| !ratio_is_less(pair[0], pair[1])) {
        return Err(ContractError::new(
            field,
            "levels must be strictly increasing without duplicates",
        ));
    }
    Ok(())
}

fn ratio_is_less(left: ExactRatio, right: ExactRatio) -> bool {
    u128::from(left.numerator) * u128::from(right.denominator)
        < u128::from(right.numerator) * u128::from(left.denominator)
}

fn menu_supports_point(menu: &ForecastMenuV1, required: &RequiredPointRouteV1) -> bool {
    menu.point_routes.iter().any(|offered| match (offered, required) {
        (PointRouteCapabilityV1::NativePointHead, RequiredPointRouteV1::NativePointHead) => true,
        (
            PointRouteCapabilityV1::ExactQuantileSelection { levels },
            RequiredPointRouteV1::ExactQuantileSelection {
                quantile,
                source_levels,
            },
        ) => levels == source_levels && levels.contains(quantile),
        (
            PointRouteCapabilityV1::PredictiveSampleReduction {
                sample_count,
                supported_statistics,
            },
            RequiredPointRouteV1::PredictiveSampleReduction {
                statistic,
                source_sample_count,
            },
        ) => sample_count == source_sample_count && supported_statistics.contains(statistic),
        _ => false,
    })
}

fn menu_supports_probability(menu: &[NativeProbabilityCapabilityV1], order: &NativeProbabilityOrderV1) -> bool {
    if matches!(order, NativeProbabilityOrderV1::None) {
        return true;
    }
    menu.iter().any(|capability| match (capability, order) {
        (
            NativeProbabilityCapabilityV1::Quantiles { levels },
            NativeProbabilityOrderV1::Quantiles { retained_levels },
        ) => levels == retained_levels,
        (
            NativeProbabilityCapabilityV1::PredictiveSamples { sample_count },
            NativeProbabilityOrderV1::PredictiveSamples {
                sample_count: ordered_count,
            },
        ) => sample_count == ordered_count,
        _ => false,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn architecture_hash(tag: u8) -> ModelArchitectureHash {
        ModelArchitectureHash::parse(format!("architecture:sha256:{}", format!("{tag:02x}").repeat(32))).unwrap()
    }

    fn order(point: RequiredPointRouteV1, native_probability: NativeProbabilityOrderV1) -> ForecastOrderV1 {
        ForecastOrderV1 {
            schema: FORECAST_ORDER_SCHEMA.to_string(),
            point,
            native_probability,
            compact_statistics: CompactStatisticsRequirementV1::PointEptWdAndRiskLedgerV1,
        }
    }

    fn implementation_hash(tag: u8) -> ModelImplementationHash {
        ModelImplementationHash::parse(format!("implementation:sha256:{}", format!("{tag:02x}").repeat(32))).unwrap()
    }

    #[test]
    fn deterministic_point_only_model_does_not_fabricate_probability_output() {
        let menu = ForecastMenuV1 {
            schema: FORECAST_MENU_SCHEMA.to_string(),
            model_architecture: architecture_hash(1),
            model_implementation: implementation_hash(1),
            point_routes: vec![PointRouteCapabilityV1::NativePointHead],
            native_probability: vec![],
        };
        let confirmed = confirm_forecast_order(
            &menu,
            &order(RequiredPointRouteV1::NativePointHead, NativeProbabilityOrderV1::None),
        )
        .unwrap();
        assert_eq!(confirmed.point_kind, PointKind::NativePointHead);
        assert_eq!(
            confirmed.required_artifact_roles,
            [ArtifactRole::DensePointForecast, ArtifactRole::CompactMetrics]
        );
    }

    #[test]
    fn quantile_protocol_requires_native_quantiles_and_exact_median_route() {
        let levels = vec![
            ExactRatio::new(1, 10).unwrap(),
            ExactRatio::new(1, 2).unwrap(),
            ExactRatio::new(9, 10).unwrap(),
        ];
        let menu = ForecastMenuV1 {
            schema: FORECAST_MENU_SCHEMA.to_string(),
            model_architecture: architecture_hash(2),
            model_implementation: implementation_hash(2),
            point_routes: vec![PointRouteCapabilityV1::ExactQuantileSelection { levels: levels.clone() }],
            native_probability: vec![NativeProbabilityCapabilityV1::Quantiles { levels: levels.clone() }],
        };
        let confirmed = confirm_forecast_order(
            &menu,
            &order(
                RequiredPointRouteV1::ExactQuantileSelection {
                    quantile: ExactRatio::new(1, 2).unwrap(),
                    source_levels: levels.clone(),
                },
                NativeProbabilityOrderV1::Quantiles {
                    retained_levels: levels.clone(),
                },
            ),
        )
        .unwrap();
        assert_eq!(confirmed.point_kind, PointKind::ConditionalMedian);
        assert!(
            confirmed
                .required_artifact_roles
                .contains(&ArtifactRole::QuantileForecast)
        );

        let point_only_claims = confirm_forecast_order(
            &menu,
            &order(
                RequiredPointRouteV1::ExactQuantileSelection {
                    quantile: ExactRatio::new(1, 2).unwrap(),
                    source_levels: levels,
                },
                NativeProbabilityOrderV1::None,
            ),
        )
        .unwrap();
        assert!(
            point_only_claims
                .required_artifact_roles
                .contains(&ArtifactRole::QuantileForecast),
            "a derived point keeps its named source even when the protocol makes no probability claim"
        );
    }

    #[test]
    fn sample_protocol_makes_mean_and_median_different_explicit_routes() {
        let menu = ForecastMenuV1 {
            schema: FORECAST_MENU_SCHEMA.to_string(),
            model_architecture: architecture_hash(3),
            model_implementation: implementation_hash(3),
            point_routes: vec![PointRouteCapabilityV1::PredictiveSampleReduction {
                sample_count: 64,
                supported_statistics: vec![SampleStatisticV1::Mean, SampleStatisticV1::LowerMedian],
            }],
            native_probability: vec![NativeProbabilityCapabilityV1::PredictiveSamples { sample_count: 64 }],
        };
        let mean = confirm_forecast_order(
            &menu,
            &order(
                RequiredPointRouteV1::PredictiveSampleReduction {
                    statistic: SampleStatisticV1::Mean,
                    source_sample_count: 64,
                },
                NativeProbabilityOrderV1::PredictiveSamples { sample_count: 64 },
            ),
        )
        .unwrap();
        let median = confirm_forecast_order(
            &menu,
            &order(
                RequiredPointRouteV1::PredictiveSampleReduction {
                    statistic: SampleStatisticV1::LowerMedian,
                    source_sample_count: 64,
                },
                NativeProbabilityOrderV1::PredictiveSamples { sample_count: 64 },
            ),
        )
        .unwrap();
        assert_eq!(mean.point_kind, PointKind::ConditionalMean);
        assert_eq!(median.point_kind, PointKind::ConditionalMedian);
        assert_ne!(serde_json::to_vec(&mean).unwrap(), serde_json::to_vec(&median).unwrap());

        let wrong_source_count = confirm_forecast_order(
            &menu,
            &order(
                RequiredPointRouteV1::PredictiveSampleReduction {
                    statistic: SampleStatisticV1::Mean,
                    source_sample_count: 32,
                },
                NativeProbabilityOrderV1::PredictiveSamples { sample_count: 64 },
            ),
        )
        .unwrap_err();
        assert_eq!(wrong_source_count.field, "forecast_confirmation.point");

        let wrong_retained_count = confirm_forecast_order(
            &menu,
            &order(
                RequiredPointRouteV1::PredictiveSampleReduction {
                    statistic: SampleStatisticV1::Mean,
                    source_sample_count: 64,
                },
                NativeProbabilityOrderV1::PredictiveSamples { sample_count: 32 },
            ),
        )
        .unwrap_err();
        assert_eq!(wrong_retained_count.field, "forecast_confirmation.native_probability");
    }

    #[test]
    fn probabilistic_protocol_refuses_a_point_only_model_before_spawn() {
        let menu = ForecastMenuV1 {
            schema: FORECAST_MENU_SCHEMA.to_string(),
            model_architecture: architecture_hash(4),
            model_implementation: implementation_hash(4),
            point_routes: vec![PointRouteCapabilityV1::NativePointHead],
            native_probability: vec![],
        };
        let error = confirm_forecast_order(
            &menu,
            &order(
                RequiredPointRouteV1::NativePointHead,
                NativeProbabilityOrderV1::Quantiles {
                    retained_levels: vec![ExactRatio::new(1, 2).unwrap()],
                },
            ),
        )
        .unwrap_err();
        assert_eq!(error.field, "forecast_confirmation.native_probability");
    }

    #[test]
    fn confirmation_never_substitutes_a_sample_mean_for_requested_native_head() {
        let menu = ForecastMenuV1 {
            schema: FORECAST_MENU_SCHEMA.to_string(),
            model_architecture: architecture_hash(5),
            model_implementation: implementation_hash(5),
            point_routes: vec![PointRouteCapabilityV1::PredictiveSampleReduction {
                sample_count: 8,
                supported_statistics: vec![SampleStatisticV1::Mean],
            }],
            native_probability: vec![NativeProbabilityCapabilityV1::PredictiveSamples { sample_count: 8 }],
        };
        let error = confirm_forecast_order(
            &menu,
            &order(RequiredPointRouteV1::NativePointHead, NativeProbabilityOrderV1::None),
        )
        .unwrap_err();
        assert_eq!(error.field, "forecast_confirmation.point");
    }

    #[test]
    fn malformed_probability_levels_and_duplicate_routes_fail_closed() {
        let duplicate = PointRouteCapabilityV1::NativePointHead;
        let menu = ForecastMenuV1 {
            schema: FORECAST_MENU_SCHEMA.to_string(),
            model_architecture: architecture_hash(6),
            model_implementation: implementation_hash(6),
            point_routes: vec![duplicate.clone(), duplicate],
            native_probability: vec![],
        };
        assert_eq!(menu.validate().unwrap_err().field, "forecast_menu.point_routes");

        let unsorted = ForecastMenuV1 {
            schema: FORECAST_MENU_SCHEMA.to_string(),
            model_architecture: architecture_hash(7),
            model_implementation: implementation_hash(7),
            point_routes: vec![PointRouteCapabilityV1::ExactQuantileSelection {
                levels: vec![ExactRatio::new(1, 2).unwrap(), ExactRatio::new(1, 10).unwrap()],
            }],
            native_probability: vec![],
        };
        assert_eq!(unsorted.validate().unwrap_err().field, "forecast_menu.point_routes");

        let missing_source = ForecastMenuV1 {
            schema: FORECAST_MENU_SCHEMA.to_string(),
            model_architecture: architecture_hash(8),
            model_implementation: implementation_hash(8),
            point_routes: vec![PointRouteCapabilityV1::PredictiveSampleReduction {
                sample_count: 8,
                supported_statistics: vec![SampleStatisticV1::Mean],
            }],
            native_probability: vec![],
        };
        assert_eq!(
            missing_source.validate().unwrap_err().field,
            "forecast_menu.point_routes"
        );
    }

    #[test]
    fn output_capabilities_are_checked_against_the_canonical_architecture() {
        use fern_experiment_types::{
            DependencyLockHash, Float32MatmulPrecisionV1, ImplementationRuntimeV1, ModelArchitectureCardV1,
            ModelImplementationCardV1, NumericalBackendV1, NumericalDtypeV1, RawArchitectureChannelContractV1,
            RawArchitectureShapeV1, RawDLinearArchitectureV1, RawModelArchitectureCardV1, RawModelImplementationCardV1,
            RawNumericalPolicyV1, SourceBundleHash,
        };

        let architecture = ModelArchitectureCardV1::canonicalize_builtin(RawModelArchitectureCardV1::DLinear(
            RawDLinearArchitectureV1 {
                schema_version: 1,
                shape: RawArchitectureShapeV1 {
                    context_length: 336,
                    horizon_length: 64,
                    channel_contract: RawArchitectureChannelContractV1::RuntimePositive,
                },
                moving_average_window: 25,
                individual_per_channel: false,
                stride: 1,
            },
        ))
        .unwrap();
        let implementation = ModelImplementationCardV1::canonicalize(RawModelImplementationCardV1 {
            schema_version: 1,
            architecture_hash: architecture.architecture_hash().unwrap(),
            source_bundle_hash: SourceBundleHash::from_content_bytes(b"forecast-menu-source"),
            dependency_lock_hash: DependencyLockHash::from_content_bytes(b"forecast-menu-lock"),
            runtime: ImplementationRuntimeV1::PythonTorch,
            numerical_backend: NumericalBackendV1::TorchEager,
            worker_protocol_version: 4,
            numerical_policy: RawNumericalPolicyV1 {
                parameter_dtype: NumericalDtypeV1::Float32,
                autocast_dtype: None,
                float32_matmul_precision: Float32MatmulPrecisionV1::High,
                cuda_matmul_allow_tf32: true,
                cudnn_allow_tf32: true,
                deterministic_algorithms: false,
                cudnn_deterministic: false,
                cudnn_benchmark: false,
            },
            active_feature_flags: vec![],
        })
        .unwrap();
        let point = ForecastMenuV1::from_implementation(
            &architecture,
            &implementation,
            vec![PointRouteCapabilityV1::NativePointHead],
            vec![],
        )
        .unwrap();
        assert_eq!(point.model_architecture(), &architecture.architecture_hash().unwrap());
        assert_eq!(
            point.model_implementation(),
            &implementation.implementation_hash().unwrap()
        );

        let error = ForecastMenuV1::from_implementation(
            &architecture,
            &implementation,
            vec![PointRouteCapabilityV1::PredictiveSampleReduction {
                sample_count: 64,
                supported_statistics: vec![SampleStatisticV1::Mean],
            }],
            vec![NativeProbabilityCapabilityV1::PredictiveSamples { sample_count: 64 }],
        )
        .unwrap_err();
        assert_eq!(error.field, "forecast_menu.point_routes");
    }
}
