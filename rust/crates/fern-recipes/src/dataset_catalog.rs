//! Executable dataset-source vocabulary shared by Recipe authoring and runs.
//!
//! A Recipe family is data: it gives a scientific name to an ordered set of
//! source names. This module answers the different, compiled question: which
//! source handlers this Fern build actually knows, and what draw/engine
//! semantics each handler owns. Keeping that answer here prevents the CLI,
//! scheduler, and Recipe validator from maintaining parallel source tables.

#[cfg(any())]
use std::error::Error;
#[cfg(any())]
use std::fmt;

use schemars::JsonSchema;
use serde::{Deserialize, Serialize};
use serde_json::Value;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum SourceDrawKind {
    DeterministicTrajectory,
    StochasticTrajectory,
    KnownData,
}

#[cfg(any())]
impl SourceDrawKind {
    pub fn source_is_stochastic(self) -> bool {
        matches!(self, Self::StochasticTrajectory)
    }

    pub fn draw_design(self) -> &'static str {
        match self {
            Self::StochasticTrajectory => "joint",
            Self::DeterministicTrajectory | Self::KnownData => "noise_only",
        }
    }

    pub fn trajectory_seed_base(self, requested_seed_base: u64) -> u64 {
        match self {
            Self::KnownData => 0,
            Self::DeterministicTrajectory | Self::StochasticTrajectory => requested_seed_base,
        }
    }
}

/// Which installed implementation materializes a source.
///
/// This is part of run identity because the Rust and Python RNG stacks are not
/// byte-equivalent even when they start from the same child seed.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum DataEngine {
    SerieRs,
    SeriePy,
}

/// One source handler advertised by an installed worker.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct DatasetSourceCapabilityV1 {
    pub source_name: String,
    pub draw_kind: SourceDrawKind,
    pub data_engine: DataEngine,
    /// Offset from the requested observation-noise seed for a named initial
    /// condition. Ordinary sources use the requested seed unchanged.
    pub noise_seed_offset: Option<u64>,
}

// Retired 2026-08-28: C# RecipeCatalog owns this complete source table. The
// former Rust registrations remain below as an inert recovery record; active
// Rust callers consume either a typed C# receipt or the build-bound C# catalog
// projection used before a sibling executable can be probed.
#[cfg(any())]
const DATASET_SOURCE_REGISTRATIONS: &[(&str, SourceDrawKind, DataEngine)] = &[
    ("lorenz", SourceDrawKind::DeterministicTrajectory, DataEngine::SeriePy),
    (
        "lorenz_param_shock",
        SourceDrawKind::DeterministicTrajectory,
        DataEngine::SeriePy,
    ),
    (
        "lorenz_state_shock",
        SourceDrawKind::DeterministicTrajectory,
        DataEngine::SeriePy,
    ),
    (
        "lorenz_switch",
        SourceDrawKind::DeterministicTrajectory,
        DataEngine::SeriePy,
    ),
    ("rossler", SourceDrawKind::DeterministicTrajectory, DataEngine::SeriePy),
    (
        "rossler_param_shock",
        SourceDrawKind::DeterministicTrajectory,
        DataEngine::SeriePy,
    ),
    ("lorenz96", SourceDrawKind::DeterministicTrajectory, DataEngine::SeriePy),
    (
        "lorenz96_switch",
        SourceDrawKind::DeterministicTrajectory,
        DataEngine::SeriePy,
    ),
    ("chua", SourceDrawKind::DeterministicTrajectory, DataEngine::SeriePy),
    (
        "chua_param_shock",
        SourceDrawKind::DeterministicTrajectory,
        DataEngine::SeriePy,
    ),
    (
        "chua_switch",
        SourceDrawKind::DeterministicTrajectory,
        DataEngine::SeriePy,
    ),
    ("ks", SourceDrawKind::DeterministicTrajectory, DataEngine::SeriePy),
    (
        "ks_param_shock",
        SourceDrawKind::DeterministicTrajectory,
        DataEngine::SeriePy,
    ),
    ("ou", SourceDrawKind::StochasticTrajectory, DataEngine::SeriePy),
    (
        "ou_param_shock",
        SourceDrawKind::StochasticTrajectory,
        DataEngine::SeriePy,
    ),
    ("slds", SourceDrawKind::StochasticTrajectory, DataEngine::SeriePy),
    (
        "slds_param_shock",
        SourceDrawKind::StochasticTrajectory,
        DataEngine::SeriePy,
    ),
    ("slds_switch", SourceDrawKind::StochasticTrajectory, DataEngine::SeriePy),
    ("doublewell", SourceDrawKind::StochasticTrajectory, DataEngine::SeriePy),
    (
        "doublewell_barrier_collapse",
        SourceDrawKind::StochasticTrajectory,
        DataEngine::SeriePy,
    ),
    (
        "doublewell_param_shock",
        SourceDrawKind::StochasticTrajectory,
        DataEngine::SeriePy,
    ),
    (
        "doublewell_switch",
        SourceDrawKind::StochasticTrajectory,
        DataEngine::SeriePy,
    ),
    ("seasonal_ar", SourceDrawKind::StochasticTrajectory, DataEngine::SeriePy),
    (
        "seasonal_ar_param_shock",
        SourceDrawKind::StochasticTrajectory,
        DataEngine::SeriePy,
    ),
    ("garch", SourceDrawKind::StochasticTrajectory, DataEngine::SeriePy),
    (
        "garch_param_shock",
        SourceDrawKind::StochasticTrajectory,
        DataEngine::SeriePy,
    ),
    ("lg_ssm", SourceDrawKind::StochasticTrajectory, DataEngine::SeriePy),
    (
        "lg_ssm_switch",
        SourceDrawKind::StochasticTrajectory,
        DataEngine::SeriePy,
    ),
    (
        "lg_ssm_regime_switch",
        SourceDrawKind::StochasticTrajectory,
        DataEngine::SeriePy,
    ),
    ("etth1", SourceDrawKind::KnownData, DataEngine::SeriePy),
    ("etth2", SourceDrawKind::KnownData, DataEngine::SeriePy),
    ("ettm1", SourceDrawKind::KnownData, DataEngine::SeriePy),
    ("ettm2", SourceDrawKind::KnownData, DataEngine::SeriePy),
    ("solar_energy", SourceDrawKind::KnownData, DataEngine::SeriePy),
    ("solar_energy_subset", SourceDrawKind::KnownData, DataEngine::SeriePy),
    ("hugadb_accel_emg", SourceDrawKind::KnownData, DataEngine::SeriePy),
    (
        "hugadb_walk_bicycling_switch",
        SourceDrawKind::KnownData,
        DataEngine::SeriePy,
    ),
    (
        "hugadb_walk_run_natural",
        SourceDrawKind::KnownData,
        DataEngine::SeriePy,
    ),
    (
        "hugadb_walk_only_natural",
        SourceDrawKind::KnownData,
        DataEngine::SeriePy,
    ),
];

#[cfg(any())]
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UnknownDatasetSource {
    source_name: String,
}

#[cfg(any())]
impl fmt::Display for UnknownDatasetSource {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            formatter,
            "unknown dataset source {:?}; add one compiled handler to fern-recipes::dataset_catalog before a Recipe may name it",
            self.source_name
        )
    }
}

#[cfg(any())]
impl Error for UnknownDatasetSource {}

/// Resolve one source name to the executable handler semantics it selects.
///
/// The match is intentionally exhaustive over handler families, not Recipe
/// families. New groupings of these names stay data-only; a genuinely new
/// generator or recorded-data loader must still add code here.
#[cfg(any())]
pub fn dataset_source_capability(source_name: &str) -> Result<DatasetSourceCapabilityV1, UnknownDatasetSource> {
    let registered = DATASET_SOURCE_REGISTRATIONS
        .iter()
        .find(|(registered_name, _, _)| *registered_name == source_name);
    let (draw_kind, data_engine, noise_seed_offset) = match registered {
        Some((_, draw_kind, data_engine)) => (*draw_kind, *data_engine, None),
        None => match initial_condition_noise_seed_offset(source_name) {
            Some(offset) => (
                SourceDrawKind::DeterministicTrajectory,
                DataEngine::SeriePy,
                Some(offset),
            ),
            None => {
                return Err(UnknownDatasetSource {
                    source_name: source_name.to_string(),
                });
            }
        },
    };
    Ok(DatasetSourceCapabilityV1 {
        source_name: source_name.to_string(),
        draw_kind,
        data_engine,
        noise_seed_offset,
    })
}

/// Complete source vocabulary advertised by the installed numeric workers.
///
/// `DataEngine` records which installed worker currently materializes each
/// source. Every advertised source routes to Python. Special historical bank
/// artifacts that are absent from the active Recipe catalog are deliberately
/// not advertised: an unknown name refuses before Plan registration rather
/// than pretending that an unrelated Python oracle is a materializer. A new
/// external Recipe family may regroup these sources without a build; a source
/// absent here has no deployed handler and is refused before Plan registration.
const CSHARP_SOURCE_CAPABILITY_CATALOG: &str =
    include_str!("../../../csharp/Fern.Experiment/Catalog/recipe_source_capabilities_v1.json");

/// The C# catalog bytes compiled beside this Rust binary, projected only for
/// worker installation identity and V2 handler publication. Runtime source
/// lookup uses the typed C# process receipt instead. Keeping this projection
/// build-bound prevents an installation identity from needing to launch a
/// sibling executable before the binary set has been accepted.
pub fn published_dataset_source_capabilities() -> Vec<DatasetSourceCapabilityV1> {
    let catalog: Value = serde_json::from_str(CSHARP_SOURCE_CAPABILITY_CATALOG)
        .expect("C# source capability catalog is checked-in valid JSON");
    let cases = catalog["handler_capability_cases"]
        .as_object()
        .expect("C# source capability catalog preserves handler capability cases");
    let sources = cases
        .values()
        .filter_map(|case| case.get("capabilities")?.get("sources")?.as_array())
        .max_by_key(|sources| sources.len())
        .expect("C# source capability catalog has one complete source publication");
    let mut capabilities = sources
        .iter()
        .cloned()
        .map(serde_json::from_value::<DatasetSourceCapabilityV1>)
        .collect::<Result<Vec<_>, _>>()
        .expect("C# source capability rows match the typed Rust compatibility projection");
    capabilities.sort_by(|left, right| left.source_name.cmp(&right.source_name));
    assert!(
        capabilities
            .windows(2)
            .all(|pair| pair[0].source_name < pair[1].source_name),
        "C# source capability catalog must publish sorted unique source names"
    );
    capabilities
}

#[cfg(any())]
fn initial_condition_capability(prefix: &str, ordinal: u64, offset_base: u64) -> DatasetSourceCapabilityV1 {
    DatasetSourceCapabilityV1 {
        source_name: format!("{prefix}{ordinal}"),
        draw_kind: SourceDrawKind::DeterministicTrajectory,
        data_engine: DataEngine::SeriePy,
        noise_seed_offset: Some(offset_base + ordinal),
    }
}

#[cfg(any())]
fn initial_condition_noise_seed_offset(source_name: &str) -> Option<u64> {
    bounded_ordinal(source_name, "rossler_ic_holdout_", 8)
        .map(|ordinal| 10_000 + ordinal)
        .or_else(|| bounded_ordinal(source_name, "rossler_ic_random_", 16).map(|ordinal| 20_000 + ordinal))
        .or_else(|| bounded_ordinal(source_name, "lorenz_ic_", 16).map(|ordinal| 30_000 + ordinal))
}

#[cfg(any())]
fn bounded_ordinal(source_name: &str, prefix: &str, upper_bound: u64) -> Option<u64> {
    let ordinal = source_name.strip_prefix(prefix)?.parse::<u64>().ok()?;
    (ordinal < upper_bound).then_some(ordinal)
}

// Retired source-test inventory. C# DatasetSourceCapabilitiesCliTests keeps
// the five exact semantic names against the C# catalog authority.
#[cfg(any())]
mod tests {
    use super::*;
    use crate::RecipeFamilyName;
    use strum::IntoEnumIterator;

    #[test]
    fn catalog_distinguishes_draw_law_from_execution_engine() {
        let stochastic = dataset_source_capability("lg_ssm").unwrap();
        assert_eq!(stochastic.draw_kind, SourceDrawKind::StochasticTrajectory);
        assert_eq!(stochastic.data_engine, DataEngine::SeriePy);

        let recorded = dataset_source_capability("etth1").unwrap();
        assert_eq!(recorded.draw_kind, SourceDrawKind::KnownData);
        assert_eq!(recorded.data_engine, DataEngine::SeriePy);

        let python = dataset_source_capability("ks").unwrap();
        assert_eq!(python.draw_kind, SourceDrawKind::DeterministicTrajectory);
        assert_eq!(python.data_engine, DataEngine::SeriePy);

        let held_out = dataset_source_capability("rossler_ic_holdout_3").unwrap();
        assert_eq!(held_out.noise_seed_offset, Some(10_003));
        assert_eq!(held_out.data_engine, DataEngine::SeriePy);
    }

    #[test]
    fn python_owns_every_standard_trajectory_and_known_data_route() {
        for (source_name, _, data_engine) in DATASET_SOURCE_REGISTRATIONS {
            assert_eq!(*data_engine, DataEngine::SeriePy, "{source_name}");
        }
    }

    #[test]
    fn named_initial_condition_routes_are_python_owned() {
        let named_initial_conditions = published_dataset_source_capabilities()
            .into_iter()
            .filter(|capability| capability.noise_seed_offset.is_some())
            .collect::<Vec<_>>();
        assert_eq!(named_initial_conditions.len(), 40);
        assert!(
            named_initial_conditions
                .iter()
                .all(|capability| capability.data_engine == DataEngine::SeriePy)
        );
    }

    #[test]
    fn every_legacy_family_member_resolves_once_in_the_worker_catalog() {
        let catalog = published_dataset_source_capabilities();
        let names = catalog
            .iter()
            .map(|source| source.source_name.as_str())
            .collect::<std::collections::BTreeSet<_>>();
        assert_eq!(names.len(), catalog.len());
        for family in RecipeFamilyName::iter() {
            for source_name in family.cells() {
                assert!(names.contains(source_name), "{} misses {source_name}", family.value());
            }
        }
    }

    #[test]
    fn unknown_and_out_of_range_sources_are_refused() {
        assert!(dataset_source_capability("new_sde").is_err());
        assert!(dataset_source_capability("rossler_ic_holdout_8").is_err());
        assert!(dataset_source_capability("lorenz_ic_16").is_err());
        for inactive_artifact in [
            "controlled_pendulum",
            "level0a_controlled_lgssm",
            "radial_attenuation_gamma4",
            "radial_attenuation_gaussian",
        ] {
            assert!(
                dataset_source_capability(inactive_artifact).is_err(),
                "{inactive_artifact}"
            );
        }
    }
}
