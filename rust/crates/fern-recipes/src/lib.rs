use strum::IntoEnumIterator;

use fern_experiment_types::FernDensityVariantV1;

mod composition;
mod dataset_catalog;
mod draw_protocol;
mod extension;

pub use composition::{
    ABLATION_DRAFT_SCHEMA_V2, ABLATION_RESOLUTION_SCHEMA_V2, ACTION_COMPONENT_CARD_SCHEMA_V2,
    ARCHITECTURE_COMPONENT_CARD_SCHEMA_V2, AblationDraftV2, AblationResolutionV2, ActionComponentCardV2,
    ActionSourceV2, ArchitectureComponentCardV2, BinaryFactorV2, BinaryFactorialDesignV2, CapabilityGeneratorKindV2,
    CapabilityGeneratorV2, ComponentSourceV2, CrossComponentConstraintV2, CrossComponentRelationV2,
    DENSITY_COMPONENT_CARD_SCHEMA_V2, DensityComponentCardV2, EXPERIMENT_DRAFT_SCHEMA_V2,
    EXPERIMENT_FORK_RESOLUTION_SCHEMA_V2, EXPERIMENT_MANIFEST_SCHEMA_V2, EXPERIMENT_RESOLUTION_SCHEMA_V2,
    ExperimentBudgetV2, ExperimentComponentReferencesV2, ExperimentComponentRoleV2, ExperimentComponentsSourceV2,
    ExperimentCostEstimateV2, ExperimentDraftV2, ExperimentForkResolutionV2, ExperimentResolutionV2,
    FactorialContrastV2, HandlerCapabilityV2, HandlerRequestV2, IMPLEMENTATION_BUNDLE_DRAFT_SCHEMA_V2,
    IMPLEMENTATION_BUNDLE_RESOLUTION_SCHEMA_V2, IMPLEMENTATION_CAPABILITIES_SCHEMA_V2, ImplementationBundleDraftV2,
    ImplementationBundleFileV2, ImplementationBundleResolutionV2, ImplementationCapabilitiesV2,
    ImplementationReferenceV2, ImplementationRuntimeV2, InferentialFeasibilityV2, InferentialRequirementV2,
    MODEL_LINK_PROBE_RESULT_SCHEMA_V1, MODEL_LINK_RECEIPT_SCHEMA_V1, ModelLinkCheckpointRoundTripV1,
    ModelLinkConstructionV1, ModelLinkLossBackwardV1, ModelLinkOptimizerMembershipV1, ModelLinkOutputContractV1,
    ModelLinkParameterContractV1, ModelLinkReceiptV1, ModelLinkShapeModeV1, ObservedArchitectureManifestV1,
    ObservedParameterGroupV1, ObservedSlotV1, PYTHON_CAPABILITY_PROBE_SCHEMA_V2, PythonCapabilityProbeV2,
    PythonModelLinkProbeV2, PythonModelLinkResultV1, RECIPE_COMPONENT_CARD_SCHEMA_V2, RecipeComponentCardV2,
    ResolvedActionV2, ResolvedExperimentArmV2, ResolvedExperimentComponentsV2, ResolvedExperimentManifestV2,
    WorkloadArmPreviewV2, WorkloadPreviewV2,
};
pub use dataset_catalog::{
    DataEngine, DatasetSourceCapabilityV1, SourceDrawKind, published_dataset_source_capabilities,
};
pub use draw_protocol::{DgpDrawBank, DrawDesign, DrawRef, TitrationCell, TitrationPlan, spawn_draw_seeds};
pub use extension::RecipeFernDensityIdentityV1;
pub use extension::{
    LEGACY_RECIPE_IDENTITY_SCHEMA, LegacyRecipeIdentityV1, LegacyRecipeIdentityValuesV1,
    RECIPE_HANDLER_CAPABILITIES_SCHEMA, RecipeFamilyCapabilityV1, RecipeHandlerCapabilitiesV1,
    RecipeModelCapabilityV1, RecipePlanCapabilityV1, recipe_handler_capabilities,
};

// EnumIter replaces the retired hand-written ALL list: iteration is now
// exhaustive BY CONSTRUCTION, so a new variant cannot miss the registry
// closure checks (corrections 86/295).
#[derive(Debug, Clone, Copy, PartialEq, Eq, strum::EnumIter)]
pub enum RecipeFamilyName {
    Lorenz,
    Rossler,
    Lorenz96,
    Chua,
    Ou,
    Slds,
    Doublewell,
    SeasonalAr,
    OuBase,
    DoublewellBase,
    DoublewellBarrierCollapse,
    LorenzBase,
    LorenzParamShock,
    RosslerBase,
    RosslerInitialConditionHoldout,
    RosslerInitialConditionRandom,
    LorenzInitialConditionRandom,
    RosslerParamShock,
    LgSsmBase,
    LgSsmSwitch,
    LgSsmRegimeSwitch,
    HugadbAccelEmg,
    HugadbSwitch,
    HugadbWalkRunNatural,
    HugadbWalkOnlyNatural,
    SolarEnergy,
    SolarEnergySubset,
    IcmlMain,
    IcmlMainEtth1S336H96,
    IcmlMainEtth1S336H336,
    IcmlMainLorenzS336H336,
    IcmlMainHugadbGeneric,
    IcmlMainHugadbSplice,
    IcmlShock8,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, strum::EnumIter)]
pub enum RecipeModelName {
    Fern,
    FernIcmlPinned,
    /// `fern` with the ceiling on emitted uncertainty raised and nothing else
    /// changed. Selects `FernArchitectureVariant::CurrentWideScale`.
    FernWideScale,
    Dlinear,
    Duet,
    Patchtst,
    Timemixer,
    ChronosBoltBase,
    ChronosBoltSmall,
    Zeus,
    Chronos2,
    Tirex,
    Timesfm25_200m,
}

/// Three-factor Fern architecture identity used by the Rössler ablation.
///
/// The endpoint labels remain `current` and `icml_pinned` so existing
/// recipes and run hashes stay stable. The other six variants change one or
/// two factors while holding every other Fern knob fixed.
#[derive(Debug, Clone, Copy, PartialEq, Eq, strum::EnumIter)]
pub enum FernArchitectureVariant {
    Current,
    IcmlPinned,
    InterleavedUnclampedWide,
    AllComplexClampedWide,
    AllComplexUnclampedPaperBrenier,
    InterleavedClampedWide,
    InterleavedUnclampedPaperBrenier,
    AllComplexClampedPaperBrenier,
    /// `current` with the ceiling on emitted uncertainty raised.
    ///
    /// Fern states a standard deviation of `source_std * (1 + c)` with `c`
    /// soft-clipped at 8.5, so the widest interval it can ever report is 0.95.
    /// Measured 2026-08-17 on the linear state-space world, the scale head sits
    /// at exactly that ceiling in every cell at every noise dose while the error
    /// it must cover runs from 1.14 to 2.43 — the model could not report an
    /// honest width anywhere in the grid. This variant raises the ceiling and
    /// changes nothing else, so the difference measures the cap alone.
    ///
    /// It is a separate name rather than a knob on `current` because the ceiling
    /// enters the architecture card and therefore the architecture hash: two
    /// ceilings are two models and must not share one identity.
    CurrentWideScale,
}

impl FernArchitectureVariant {
    /// Exact members of the historical Rössler 2×2×2 architecture diagnosis.
    ///
    /// This is deliberately not `Self::iter()`: the enum is Fern's complete
    /// architecture vocabulary, while this slice is one experiment's frozen
    /// factorial design. Adding an unrelated architecture must extend the
    /// capability registry without silently changing this eight-arm study.
    pub const fn rossler_three_factor_cube() -> [Self; 8] {
        [
            Self::Current,
            Self::IcmlPinned,
            Self::InterleavedUnclampedWide,
            Self::AllComplexClampedWide,
            Self::AllComplexUnclampedPaperBrenier,
            Self::InterleavedClampedWide,
            Self::InterleavedUnclampedPaperBrenier,
            Self::AllComplexClampedPaperBrenier,
        ]
    }

    pub fn value(self) -> &'static str {
        match self {
            Self::Current => "current",
            Self::IcmlPinned => "icml_pinned",
            Self::InterleavedUnclampedWide => "interleaved_unclamped_wide",
            Self::AllComplexClampedWide => "all_complex_clamped_wide",
            Self::AllComplexUnclampedPaperBrenier => "all_complex_unclamped_paper_brenier",
            Self::InterleavedClampedWide => "interleaved_clamped_wide",
            Self::InterleavedUnclampedPaperBrenier => "interleaved_unclamped_paper_brenier",
            Self::AllComplexClampedPaperBrenier => "all_complex_clamped_paper_brenier",
            Self::CurrentWideScale => "current_wide_scale",
        }
    }

    /// Ceiling on the positive Brenier coefficient, or `None` to leave the
    /// architecture's own default in place. Only the wide-scale variant raises
    /// it; every other variant returns `None` and keeps the identity it has.
    ///
    /// 60.0 is chosen from the measurement rather than tuned: with the fixed
    /// `source_std` of 0.1 the emitted standard deviation is `0.1 * (1 + c)`, so
    /// this states widths up to 6.1 against a largest observed need of 2.43.
    pub fn brenier_scale_ceiling(self) -> Option<f64> {
        match self {
            Self::CurrentWideScale => Some(60.0),
            _ => None,
        }
    }

    pub fn parse(raw: &str) -> Result<Self, String> {
        Self::iter().find(|variant| variant.value() == raw).ok_or_else(|| {
            let valid = Self::iter()
                .map(|variant| variant.value())
                .collect::<Vec<_>>()
                .join(", ");
            format!("unknown Fern architecture {raw:?}; valid: {valid}")
        })
    }

    pub fn from_recipe_model(model: RecipeModelName) -> Self {
        match model {
            RecipeModelName::FernIcmlPinned => Self::IcmlPinned,
            RecipeModelName::FernWideScale => Self::CurrentWideScale,
            _ => Self::Current,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, strum::EnumIter)]
pub enum RecipeEvalName {
    Huber,
    HuberProj500,
    NllS16E32,
    FernNllS4E32,
    HuberEvalCrps,
    HuberCrps,
    CrpsS4E48,
    HuberEvalCrpsE32NoSwd,
    FernNllS16E32NoSwd,
    /// Fern's own exact likelihood with ONE predictive sample. The
    /// seam-attached densities build one component law per case from one
    /// Core hidden and refuse more than one sample, so every other Fern-NLL
    /// preset (s4, s16) is unusable with them.
    FernNllS1E1,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, strum::EnumIter)]
pub enum RecipeLoaderName {
    P70_20_10NoneBs95,
    P70_20_10StandardBs95,
    P70_20_10StandardBs24,
    P70_10_20NoneBs128,
    P70_10_20RobustMadBs128,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, strum::EnumIter)]
pub enum RecipePlanName {
    CleanH64,
    CleanH96,
    CleanH192,
    CleanH336,
    CleanH64H192,
    CleanH96H192H336H720,
    TitrationH64Sigmas,
    TitrationH192Sigmas,
    TitrationH64H192Sigmas,
    TitrationH64RelativeDoses,
    TitrationH64PrimaryDoses,
    RosslerInitialConditionHoldoutH64,
}

/// A named set of already-compiled world sources.
///
/// The name is scientific identity and the cells are recipe data. Legacy Rust
/// catalog builders convert [`RecipeFamilyName`] into this shape, while an
/// external Recipe Extension may declare a new grouping without adding an enum
/// arm or rebuilding the scheduler.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RecipeSourceSet {
    name: String,
    cells: Vec<String>,
}

impl RecipeSourceSet {
    pub fn new(name: impl Into<String>, cells: Vec<String>) -> Result<Self, String> {
        let name = name.into();
        validate_recipe_identity_name("recipe family", &name)?;
        validate_recipe_string_set("recipe family cells", &cells)?;
        Ok(Self { name, cells })
    }

    pub fn value(&self) -> &str {
        &self.name
    }

    pub fn cells(&self) -> &[String] {
        &self.cells
    }
}

impl From<RecipeFamilyName> for RecipeSourceSet {
    fn from(family: RecipeFamilyName) -> Self {
        Self {
            name: family.value().to_string(),
            cells: family.cells().iter().map(|cell| (*cell).to_string()).collect(),
        }
    }
}

impl PartialEq<RecipeFamilyName> for RecipeSourceSet {
    fn eq(&self, other: &RecipeFamilyName) -> bool {
        self.name == other.value() && self.cells.iter().map(String::as_str).eq(other.cells().iter().copied())
    }
}

/// A named forecast grid whose lengths and doses are recipe data.
///
/// `RecipePlanName` remains the legacy catalog vocabulary. New external
/// recipes construct this value directly, so trying another horizon or dose
/// does not require recompiling Fern.
#[derive(Debug, Clone, PartialEq)]
pub struct RecipeGridSpec {
    name: String,
    seq_len: i64,
    pred_lens: Vec<i64>,
    noise_levels: Vec<f32>,
}

impl RecipeGridSpec {
    pub fn new(
        name: impl Into<String>,
        seq_len: i64,
        pred_lens: Vec<i64>,
        noise_levels: Vec<f32>,
    ) -> Result<Self, String> {
        let name = name.into();
        validate_recipe_identity_name("recipe plan", &name)?;
        if seq_len <= 0 {
            return Err("recipe plan seq_len must be positive".to_string());
        }
        if pred_lens.is_empty() || pred_lens.iter().any(|value| *value <= 0) {
            return Err("recipe plan pred_lens must contain positive horizons".to_string());
        }
        if pred_lens
            .iter()
            .copied()
            .collect::<std::collections::BTreeSet<_>>()
            .len()
            != pred_lens.len()
        {
            return Err("recipe plan pred_lens must not repeat a horizon".to_string());
        }
        if noise_levels.is_empty()
            || noise_levels
                .iter()
                .any(|value| !value.is_finite() || *value < 0.0 || value.is_sign_negative())
        {
            return Err("recipe plan noise_levels must contain finite nonnegative doses".to_string());
        }
        let unique_doses = noise_levels
            .iter()
            .map(|value| value.to_bits())
            .collect::<std::collections::BTreeSet<_>>();
        if unique_doses.len() != noise_levels.len() {
            return Err("recipe plan noise_levels must not repeat a dose".to_string());
        }
        Ok(Self {
            name,
            seq_len,
            pred_lens,
            noise_levels,
        })
    }

    pub fn value(&self) -> &str {
        &self.name
    }

    pub fn seq_len(&self) -> i64 {
        self.seq_len
    }

    pub fn pred_lens(&self) -> &[i64] {
        &self.pred_lens
    }

    pub fn noise_levels(&self) -> &[f32] {
        &self.noise_levels
    }
}

impl From<RecipePlanName> for RecipeGridSpec {
    fn from(plan: RecipePlanName) -> Self {
        Self {
            name: plan.value().to_string(),
            seq_len: plan.seq_len(),
            pred_lens: plan.pred_lens().to_vec(),
            noise_levels: plan.noise_levels().to_vec(),
        }
    }
}

impl PartialEq<RecipePlanName> for RecipeGridSpec {
    fn eq(&self, other: &RecipePlanName) -> bool {
        self.name == other.value()
            && self.seq_len == other.seq_len()
            && self.pred_lens == other.pred_lens()
            && self.noise_levels == other.noise_levels()
    }
}

fn validate_recipe_identity_name(field: &str, value: &str) -> Result<(), String> {
    if value.is_empty()
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

fn validate_recipe_string_set(field: &str, values: &[String]) -> Result<(), String> {
    if values.is_empty() {
        return Err(format!("{field} must not be empty"));
    }
    let mut seen = std::collections::BTreeSet::new();
    for value in values {
        validate_recipe_identity_name(field, value)?;
        if !seen.insert(value.as_str()) {
            return Err(format!("{field} repeats {value:?}"));
        }
    }
    Ok(())
}

/// Which chronological splits receive a titration dose.
///
/// `NoiseMatchedRetraining` is the historical Fern experiment: train,
/// validation, and test all receive the same dose.  The CSR scheme keeps
/// train and validation clean and injects only into test, allowing a model
/// selected once on clean validation data to be frozen across test doses.
#[derive(
    Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize, schemars::JsonSchema, strum::EnumIter,
)]
#[serde(rename_all = "snake_case")]
pub enum SplitTreatmentScheme {
    NoiseMatchedRetraining,
    CleanTrainCleanSelectTestTitration,
    NoiseTrainNoiseSelectCleanTest,
}

/// Whether trained recipes fit independently or reuse the clean
/// validation-best checkpoint for every positive test dose.
#[derive(
    Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize, schemars::JsonSchema, strum::EnumIter,
)]
#[serde(rename_all = "snake_case")]
pub enum CheckpointReusePolicy {
    /// Legacy/self-contained route: this recipe owns its dose-zero fit.
    CleanValidationBest,
    /// Positive test-only cells reuse the dose-zero run from the matching
    /// Gaussian noise-matched recipe instead of fitting another clean model.
    NoiseMatchedCleanValidationBest,
    /// Positive return-to-clean cells reuse the validation-best checkpoint
    /// from the matching noise-matched run at the same dose and bank.
    NoiseMatchedDoseValidationBest,
}

impl CheckpointReusePolicy {
    pub fn value(self) -> &'static str {
        match self {
            Self::CleanValidationBest => "clean_validation_best",
            Self::NoiseMatchedCleanValidationBest => "noise_matched_clean_validation_best",
            Self::NoiseMatchedDoseValidationBest => "noise_matched_dose_validation_best",
        }
    }
}

impl SplitTreatmentScheme {
    pub fn value(self) -> &'static str {
        match self {
            Self::NoiseMatchedRetraining => "noise_matched_retraining",
            Self::CleanTrainCleanSelectTestTitration => "clean_train_clean_select_test_titration",
            Self::NoiseTrainNoiseSelectCleanTest => "noise_train_noise_select_clean_test",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, strum::EnumIter)]
pub enum RecipeTrainName {
    Default,
    IcmlMain,
    IcmlShock,
}

const CLEAN_SIGMAS: &[f32] = &[0.0];
const TITRATION_SIGMAS: &[f32] = &[0.0, 0.25, 1.0, 2.0];
const CSR_RELATIVE_DOSES: &[f32] = &[0.0, 0.0625, 0.125, 0.25, 0.5, 1.0, 2.0];
const PRIMARY_RELATIVE_DOSES: &[f32] = &[0.0, 0.125, 0.5, 2.0];
const ROSSLER_INITIAL_CONDITION_HOLDOUT_DOSES: &[f32] = &[0.0, 0.125];

const H64: &[i64] = &[64];
const H96: &[i64] = &[96];
const H192: &[i64] = &[192];
const H336: &[i64] = &[336];
const H64_H192: &[i64] = &[64, 192];
const H96_H192_H336_H720: &[i64] = &[96, 192, 336, 720];

const LORENZ_CELLS: &[&str] = &["lorenz", "lorenz_param_shock", "lorenz_state_shock", "lorenz_switch"];
const ROSSLER_CELLS: &[&str] = &["rossler", "rossler_param_shock"];
const LORENZ96_CELLS: &[&str] = &["lorenz96", "lorenz96_switch"];
const CHUA_CELLS: &[&str] = &["chua", "chua_param_shock", "chua_switch"];
const OU_CELLS: &[&str] = &["ou", "ou_param_shock"];
const SLDS_CELLS: &[&str] = &["slds", "slds_param_shock", "slds_switch"];
const DOUBLEWELL_CELLS: &[&str] = &["doublewell", "doublewell_param_shock", "doublewell_switch"];
const SEASONAL_AR_CELLS: &[&str] = &["seasonal_ar", "seasonal_ar_param_shock"];
const OU_BASE_CELLS: &[&str] = &["ou"];
const DOUBLEWELL_BASE_CELLS: &[&str] = &["doublewell"];
const DOUBLEWELL_BARRIER_COLLAPSE_CELLS: &[&str] = &["doublewell", "doublewell_barrier_collapse"];
/// Lorenz-63 as a SECOND chaotic attractor beside Rossler: same protocol, a
/// different attractor geometry, so a finding that holds on both is not an
/// artifact of one system. Split base and parameter shift into their own
/// families (mirroring the Rossler pair) so a lane can select the stationary
/// world and the changed world independently.
const LORENZ_BASE_CELLS: &[&str] = &["lorenz"];
const LORENZ_PARAM_SHOCK_CELLS: &[&str] = &["lorenz_param_shock"];
const ROSSLER_BASE_CELLS: &[&str] = &["rossler"];
/// Eight untouched Rössler trajectories around historical x0=(1,1,1).
///
/// The complete ±0.01 cube is fixed before any result is inspected. There is
/// NO added burn-in: every trajectory retains the original 36_000-row
/// protocol from its perturbed initial condition. Each source is a separate
/// paired unit so DLinear and TiREx see the same clean trajectory and
/// observation noise, while different units change BOTH the initial condition
/// and noise draw. This confirms over local initial-condition perturbations;
/// it must not be described as eight noise seeds on one path or as the whole
/// Rössler process.
const ROSSLER_INITIAL_CONDITION_HOLDOUT_CELLS: &[&str] = &[
    "rossler_ic_holdout_0",
    "rossler_ic_holdout_1",
    "rossler_ic_holdout_2",
    "rossler_ic_holdout_3",
    "rossler_ic_holdout_4",
    "rossler_ic_holdout_5",
    "rossler_ic_holdout_6",
    "rossler_ic_holdout_7",
];
/// Sixteen Rössler trajectories at initial conditions drawn uniformly from the
/// same ±0.01 cube whose corners the eight-unit holdout uses.
///
/// The corners are the extreme points of that cube and all sit at one distance
/// from the historical x0, so they are a designed set. These sixteen sample its
/// interior. The count is the reason they exist: an exact two-sided sign test
/// on eight paired units must be UNANIMOUS to reject at five per cent, and the
/// corner lane returned six of eight. Sixteen units make the reversal rate an
/// estimable proportion rather than a pass/fail on unanimity.
///
/// The draws are declared here; the historical numerical implementation is
/// retained under `retired/serie-rs`. Same protocol as the corner lane: no
/// added burn-in, the original 36_000-row trajectory, one noise draw per unit.
/// Sixteen independent Lorenz-63 starts, drawn from a cube of half-width 1.0
/// around the registered x0; the historical numerical implementation is
/// retained under `retired/serie-rs`.
///
/// WHY INDEPENDENT TRAJECTORIES AND NOT MORE NOISE DRAWS. Every chaotic result
/// in this campaign rests on ONE orbit per world with four frozen measurement
/// draws on top of it. That design asks what the instrument does while holding
/// the realisation fixed, so no finding there can be separated from the
/// particular trajectory it was measured on -- and the Rossler reversal did
/// move when the trajectory changed. These sixteen make "does this transport
/// across realisations" a question with an estimable answer.
///
/// The half-width is 1.0, not Rossler's 0.01: Lorenz-63 lives on an attractor
/// roughly twenty units across, so the same absolute offset would leave every
/// draw on effectively the same orbit and the sixteen units would not be
/// independent in any sense that matters.
const LORENZ_INITIAL_CONDITION_RANDOM_CELLS: &[&str] = &[
    "lorenz_ic_0",
    "lorenz_ic_1",
    "lorenz_ic_2",
    "lorenz_ic_3",
    "lorenz_ic_4",
    "lorenz_ic_5",
    "lorenz_ic_6",
    "lorenz_ic_7",
    "lorenz_ic_8",
    "lorenz_ic_9",
    "lorenz_ic_10",
    "lorenz_ic_11",
    "lorenz_ic_12",
    "lorenz_ic_13",
    "lorenz_ic_14",
    "lorenz_ic_15",
];

const ROSSLER_INITIAL_CONDITION_RANDOM_CELLS: &[&str] = &[
    "rossler_ic_random_0",
    "rossler_ic_random_1",
    "rossler_ic_random_2",
    "rossler_ic_random_3",
    "rossler_ic_random_4",
    "rossler_ic_random_5",
    "rossler_ic_random_6",
    "rossler_ic_random_7",
    "rossler_ic_random_8",
    "rossler_ic_random_9",
    "rossler_ic_random_10",
    "rossler_ic_random_11",
    "rossler_ic_random_12",
    "rossler_ic_random_13",
    "rossler_ic_random_14",
    "rossler_ic_random_15",
];
const ROSSLER_PARAM_SHOCK_CELLS: &[&str] = &["rossler_param_shock"];
const LG_SSM_BASE_CELLS: &[&str] = &["lg_ssm"];
const LG_SSM_SWITCH_CELLS: &[&str] = &["lg_ssm_switch"];
const LG_SSM_REGIME_SWITCH_CELLS: &[&str] = &["lg_ssm_regime_switch"];
const HUGADB_ACCEL_EMG_CELLS: &[&str] = &["hugadb_accel_emg"];
const HUGADB_SWITCH_CELLS: &[&str] = &["hugadb_walk_bicycling_switch"];
/// One continuous single-subject recording with a performed
/// walk -> run -> walk change at 36.4% of the record (not a splice).
const HUGADB_WALK_RUN_NATURAL_CELLS: &[&str] = &["hugadb_walk_run_natural"];
/// The stationary control for the world above: same subject, same sensors,
/// same session, walking throughout, so the pair differs only in whether the
/// activity changed.
const HUGADB_WALK_ONLY_NATURAL_CELLS: &[&str] = &["hugadb_walk_only_natural"];
/// Solar power output, Alabama 2006, all 137 plants at ten-minute resolution.
/// Every recorded world in the campaign so far is one body wearing sensors, so
/// the recorded axis cannot separate "recorded rather than simulated" from
/// "human gait". Solar is a second recorded domain with none of gait's
/// properties: it is driven by a daily cycle rather than a limb cadence, its
/// channels are geographically spread copies of one forcing rather than joints
/// on one body, and it is hard-bounded below at zero every night.
///
/// The registered sixteen-plant subset is deliberately NOT used. A subset is a
/// choice that has to be defended — which sixteen, and does the answer depend
/// on that — and it makes the width incomparable with the published Solar-Energy
/// results everyone else reports. The full 137 channels cost more per cell and
/// the campaign has the machine for it.
const SOLAR_ENERGY_CELLS: &[&str] = &["solar_energy"];
const SOLAR_ENERGY_SUBSET_CELLS: &[&str] = &["solar_energy_subset"];
const ICML_MAIN_CELLS: &[&str] = &["chua", "rossler", "lorenz", "etth1", "ettm1"];
const ICML_MAIN_ETTH1_CELLS: &[&str] = &["etth1"];
const ICML_MAIN_LORENZ_CELLS: &[&str] = &["lorenz"];
const ICML_MAIN_HUGADB_GENERIC_CELLS: &[&str] = &["hugadb_accel_emg"];
const ICML_MAIN_HUGADB_SPLICE_CELLS: &[&str] = &["hugadb_walk_bicycling_switch"];
const ICML_SHOCK_8_CELLS: &[&str] = &[
    "lorenz",
    "lorenz_param_shock",
    "lorenz_state_shock",
    "lorenz_switch",
    "rossler",
    "rossler_param_shock",
    "lorenz96",
    "lorenz96_switch",
    "chua",
    "chua_param_shock",
    "chua_switch",
    "ou",
    "ou_param_shock",
    "slds",
    "slds_param_shock",
    "slds_switch",
    "doublewell",
    "doublewell_param_shock",
    "doublewell_switch",
    "seasonal_ar",
    "seasonal_ar_param_shock",
];

impl RecipeFamilyName {
    pub fn value(self) -> &'static str {
        match self {
            Self::Lorenz => "lorenz",
            Self::Rossler => "rossler",
            Self::Lorenz96 => "lorenz96",
            Self::Chua => "chua",
            Self::Ou => "ou",
            Self::Slds => "slds",
            Self::Doublewell => "doublewell",
            Self::SeasonalAr => "seasonal_ar",
            Self::OuBase => "ou_base",
            Self::DoublewellBase => "doublewell_base",
            Self::DoublewellBarrierCollapse => "doublewell_barrier_collapse",
            Self::LorenzBase => "lorenz_base",
            Self::LorenzParamShock => "lorenz_param_shock",
            Self::RosslerBase => "rossler_base",
            Self::RosslerInitialConditionHoldout => "rossler_initial_condition_holdout",
            Self::RosslerInitialConditionRandom => "rossler_initial_condition_random",
            Self::LorenzInitialConditionRandom => "lorenz_initial_condition_random",
            Self::RosslerParamShock => "rossler_param_shock",
            Self::LgSsmBase => "lg_ssm_base",
            Self::LgSsmSwitch => "lg_ssm_switch",
            Self::LgSsmRegimeSwitch => "lg_ssm_regime_switch",
            Self::HugadbAccelEmg => "hugadb_accel_emg",
            Self::HugadbSwitch => "hugadb_switch",
            Self::HugadbWalkRunNatural => "hugadb_walk_run_natural",
            Self::HugadbWalkOnlyNatural => "hugadb_walk_only_natural",
            Self::SolarEnergy => "solar_energy",
            Self::SolarEnergySubset => "solar_energy_subset",
            Self::IcmlMain => "icml_main",
            Self::IcmlMainEtth1S336H96 => "icml_main_etth1_s336_h96",
            Self::IcmlMainEtth1S336H336 => "icml_main_etth1_s336_h336",
            Self::IcmlMainLorenzS336H336 => "icml_main_lorenz_s336_h336",
            Self::IcmlMainHugadbGeneric => "icml_main_hugadb_generic",
            Self::IcmlMainHugadbSplice => "icml_main_hugadb_splice",
            Self::IcmlShock8 => "icml_shock_8",
        }
    }

    pub fn cells(self) -> &'static [&'static str] {
        match self {
            Self::Lorenz => LORENZ_CELLS,
            Self::Rossler => ROSSLER_CELLS,
            Self::Lorenz96 => LORENZ96_CELLS,
            Self::Chua => CHUA_CELLS,
            Self::Ou => OU_CELLS,
            Self::Slds => SLDS_CELLS,
            Self::Doublewell => DOUBLEWELL_CELLS,
            Self::SeasonalAr => SEASONAL_AR_CELLS,
            Self::OuBase => OU_BASE_CELLS,
            Self::DoublewellBase => DOUBLEWELL_BASE_CELLS,
            Self::DoublewellBarrierCollapse => DOUBLEWELL_BARRIER_COLLAPSE_CELLS,
            Self::LorenzBase => LORENZ_BASE_CELLS,
            Self::LorenzParamShock => LORENZ_PARAM_SHOCK_CELLS,
            Self::RosslerBase => ROSSLER_BASE_CELLS,
            Self::RosslerInitialConditionHoldout => ROSSLER_INITIAL_CONDITION_HOLDOUT_CELLS,
            Self::RosslerInitialConditionRandom => ROSSLER_INITIAL_CONDITION_RANDOM_CELLS,
            Self::LorenzInitialConditionRandom => LORENZ_INITIAL_CONDITION_RANDOM_CELLS,
            Self::RosslerParamShock => ROSSLER_PARAM_SHOCK_CELLS,
            Self::LgSsmBase => LG_SSM_BASE_CELLS,
            Self::LgSsmSwitch => LG_SSM_SWITCH_CELLS,
            Self::LgSsmRegimeSwitch => LG_SSM_REGIME_SWITCH_CELLS,
            Self::HugadbAccelEmg => HUGADB_ACCEL_EMG_CELLS,
            Self::HugadbSwitch => HUGADB_SWITCH_CELLS,
            Self::HugadbWalkRunNatural => HUGADB_WALK_RUN_NATURAL_CELLS,
            Self::HugadbWalkOnlyNatural => HUGADB_WALK_ONLY_NATURAL_CELLS,
            Self::SolarEnergy => SOLAR_ENERGY_CELLS,
            Self::SolarEnergySubset => SOLAR_ENERGY_SUBSET_CELLS,
            Self::IcmlMain => ICML_MAIN_CELLS,
            Self::IcmlMainEtth1S336H96 => ICML_MAIN_ETTH1_CELLS,
            Self::IcmlMainEtth1S336H336 => ICML_MAIN_ETTH1_CELLS,
            Self::IcmlMainLorenzS336H336 => ICML_MAIN_LORENZ_CELLS,
            Self::IcmlMainHugadbGeneric => ICML_MAIN_HUGADB_GENERIC_CELLS,
            Self::IcmlMainHugadbSplice => ICML_MAIN_HUGADB_SPLICE_CELLS,
            Self::IcmlShock8 => ICML_SHOCK_8_CELLS,
        }
    }
}

impl RecipeModelName {
    /// Whether a cell of this model FITS WEIGHTS, as opposed to running a
    /// pretrained checkpoint forward.
    ///
    /// This is the campaign's standing machine split -- trainable models on the
    /// rented GPU, foundation models on the local card -- and it also decides
    /// something less obvious. A trained cell writes its per-origin
    /// `raw_tensors.safetensors` and the Rust parent then DELETES them unless
    /// retention was asked for, while a pretrained cell returns through a
    /// different path that keeps them. So a trained cell whose typed point
    /// forecast is derived from those tensors cannot be committed without
    /// retention, and a pretrained one always can -- which is exactly why a
    /// local TiREx lane ran clean while a Solar TimeMixer cell trained for
    /// twenty minutes and then failed at commit.
    ///
    /// Listed by hand rather than derived: adding a model should make this fail
    /// to compile and force the author to say which kind it is.
    pub const fn fits_weights(self) -> bool {
        match self {
            Self::Fern
            | Self::FernIcmlPinned
            | Self::FernWideScale
            | Self::Dlinear
            | Self::Duet
            | Self::Patchtst
            | Self::Timemixer => true,
            Self::ChronosBoltBase
            | Self::ChronosBoltSmall
            | Self::Zeus
            | Self::Chronos2
            | Self::Tirex
            | Self::Timesfm25_200m => false,
        }
    }

    pub fn value(self) -> &'static str {
        match self {
            Self::Fern => "fern",
            Self::FernIcmlPinned => "fern_icml_pinned",
            Self::FernWideScale => "fern_wide_scale",
            Self::Dlinear => "dlinear",
            Self::Duet => "duet",
            Self::Patchtst => "patchtst",
            Self::Timemixer => "timemixer",
            Self::ChronosBoltBase => "chronos_bolt_base",
            Self::ChronosBoltSmall => "chronos_bolt_small",
            Self::Zeus => "zeus",
            Self::Chronos2 => "chronos2",
            Self::Tirex => "tirex",
            Self::Timesfm25_200m => "timesfm_2p5_200m",
        }
    }

    pub fn parse(raw: &str) -> Result<Self, String> {
        Self::iter().find(|model| model.value() == raw).ok_or_else(|| {
            let valid = Self::iter().map(|model| model.value()).collect::<Vec<_>>().join(", ");
            format!("unknown model {raw:?}; valid: {valid}")
        })
    }
}

impl RecipeEvalName {
    pub fn value(self) -> &'static str {
        match self {
            Self::Huber => "huber",
            Self::HuberProj500 => "huber_proj500",
            Self::NllS16E32 => "nll_s16e32",
            Self::FernNllS4E32 => "eig_nll_s4e32",
            Self::HuberEvalCrps => "huber_eval_crps",
            Self::HuberCrps => "huber_crps",
            Self::CrpsS4E48 => "crps_s4e48",
            Self::HuberEvalCrpsE32NoSwd => "huber_eval_crps_e32_no_swd",
            Self::FernNllS16E32NoSwd => "eig_nll_s16e32_no_swd",
            Self::FernNllS1E1 => "eig_nll_s1e1",
        }
    }

    pub fn parse(raw: &str) -> Result<Self, String> {
        Self::iter().find(|eval| eval.value() == raw).ok_or_else(|| {
            let valid = Self::iter().map(|eval| eval.value()).collect::<Vec<_>>().join(", ");
            format!("unknown eval {raw:?}; valid: {valid}")
        })
    }
}

impl RecipeLoaderName {
    pub fn value(self) -> &'static str {
        match self {
            Self::P70_20_10NoneBs95 => "p70_20_10_none_bs95",
            Self::P70_20_10StandardBs95 => "p70_20_10_standard_bs95",
            Self::P70_20_10StandardBs24 => "p70_20_10_standard_bs24",
            Self::P70_10_20NoneBs128 => "p70_10_20_none_bs128",
            Self::P70_10_20RobustMadBs128 => "p70_10_20_robust_mad_bs128",
        }
    }

    pub fn parse(raw: &str) -> Result<Self, String> {
        Self::iter().find(|loader| loader.value() == raw).ok_or_else(|| {
            let valid = Self::iter().map(|loader| loader.value()).collect::<Vec<_>>().join(", ");
            format!("unknown loader {raw:?}; valid: {valid}")
        })
    }
}

impl RecipePlanName {
    pub fn value(self) -> &'static str {
        match self {
            Self::CleanH64 => "clean_h64",
            Self::CleanH96 => "clean_h96",
            Self::CleanH192 => "clean_h192",
            Self::CleanH336 => "clean_h336",
            Self::CleanH64H192 => "clean_h64_h192",
            Self::CleanH96H192H336H720 => "clean_h96_h192_h336_h720",
            Self::TitrationH64Sigmas => "titration_h64_sigmas",
            Self::TitrationH192Sigmas => "titration_h192_sigmas",
            Self::TitrationH64H192Sigmas => "titration_h64_h192_sigmas",
            Self::TitrationH64RelativeDoses => "titration_h64_relative_doses",
            Self::TitrationH64PrimaryDoses => "titration_h64_primary_doses",
            Self::RosslerInitialConditionHoldoutH64 => "rossler_ic_holdout_h64",
        }
    }

    pub fn seq_len(self) -> i64 {
        336
    }

    pub fn pred_lens(self) -> &'static [i64] {
        match self {
            Self::CleanH64 => H64,
            Self::CleanH96 => H96,
            Self::CleanH192 => H192,
            Self::CleanH336 => H336,
            Self::CleanH64H192 => H64_H192,
            Self::CleanH96H192H336H720 => H96_H192_H336_H720,
            Self::TitrationH64Sigmas => H64,
            Self::TitrationH192Sigmas => H192,
            Self::TitrationH64H192Sigmas => H64_H192,
            Self::TitrationH64RelativeDoses | Self::TitrationH64PrimaryDoses => H64,
            Self::RosslerInitialConditionHoldoutH64 => H64,
        }
    }

    pub fn noise_levels(self) -> &'static [f32] {
        match self {
            Self::CleanH64
            | Self::CleanH96
            | Self::CleanH192
            | Self::CleanH336
            | Self::CleanH64H192
            | Self::CleanH96H192H336H720 => CLEAN_SIGMAS,
            Self::TitrationH64Sigmas | Self::TitrationH192Sigmas | Self::TitrationH64H192Sigmas => TITRATION_SIGMAS,
            Self::TitrationH64RelativeDoses => CSR_RELATIVE_DOSES,
            Self::TitrationH64PrimaryDoses => PRIMARY_RELATIVE_DOSES,
            Self::RosslerInitialConditionHoldoutH64 => ROSSLER_INITIAL_CONDITION_HOLDOUT_DOSES,
        }
    }
}

impl RecipeTrainName {
    pub fn value(self) -> &'static str {
        match self {
            Self::Default => "default_train",
            Self::IcmlMain => "lr9e4_ep50_pat5_grace0",
            Self::IcmlShock => "lr3e4_ep50_pat5_grace3",
        }
    }

    pub fn parse(raw: &str) -> Result<Self, String> {
        Self::iter().find(|train| train.value() == raw).ok_or_else(|| {
            let valid = Self::iter().map(|train| train.value()).collect::<Vec<_>>().join(", ");
            format!("unknown train {raw:?}; valid: {valid}")
        })
    }
}

/// EnbPI ensemble declaration (one per recipe, not per member): B members,
/// the moving-block length over train-window indices, and the seed base
/// each member's mask seed derives from (member i uses base + i). Entering
/// this in a recipe multiplies its designed cells by B at expansion time.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RecipeBootstrapSpec {
    pub ensemble_size: i64,
    pub block_size: i64,
    pub resample_seed_base: u64,
}

/// A non-default finite-density arm carried as recipe data.
///
/// `None` on [`RecipeBlueprint`] is the historical single Gaussian. This value
/// therefore represents only the four genuine mixture routes and makes invalid
/// combinations such as "bounded MDN with sparse active experts" impossible to
/// enter a recipe after construction.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RecipeFernDensitySpec {
    variant: FernDensityVariantV1,
    component_count: u64,
    sparse_active_component_count: Option<u64>,
}

impl RecipeFernDensitySpec {
    pub fn new(
        variant: FernDensityVariantV1,
        component_count: u64,
        sparse_active_component_count: Option<u64>,
    ) -> Result<Self, String> {
        match variant {
            FernDensityVariantV1::SingleGaussian => {
                return Err("single_gaussian is the omitted recipe default; do not declare a mixture spec".to_string());
            }
            FernDensityVariantV1::SparseMoe => {
                let Some(active) = sparse_active_component_count else {
                    return Err("sparse_moe requires sparse_active_component_count".to_string());
                };
                if active < 2 || active >= component_count {
                    return Err(
                        "sparse_moe active components must be at least two and smaller than component_count"
                            .to_string(),
                    );
                }
            }
            FernDensityVariantV1::MixtureSourceSharedHidden
            | FernDensityVariantV1::MixtureFernTransportsSharedHidden => {
                // One component is the ladder's base rung, not an error.
                if sparse_active_component_count.is_some() {
                    return Err("sparse_active_component_count is valid only for sparse_moe".to_string());
                }
            }
            FernDensityVariantV1::BoundedMdn
            | FernDensityVariantV1::SharedTransportMixtureSource
            | FernDensityVariantV1::MixtureOfFernTransports => {
                if component_count < 2 {
                    return Err("a finite mixture requires at least two components".to_string());
                }
                if sparse_active_component_count.is_some() {
                    return Err("sparse_active_component_count is valid only for sparse_moe".to_string());
                }
            }
        }
        Ok(Self {
            variant,
            component_count,
            sparse_active_component_count,
        })
    }

    pub const fn variant(self) -> FernDensityVariantV1 {
        self.variant
    }

    pub const fn component_count(self) -> u64 {
        self.component_count
    }

    pub const fn sparse_active_component_count(self) -> Option<u64> {
        self.sparse_active_component_count
    }
}

// No `Eq`: noise_ar1_phi is an f64. Blueprints are compared and sorted by
// `name()`, which is a string, so total equality was never used here.
#[derive(Debug, Clone, PartialEq)]
pub struct RecipeBlueprint {
    pub family: RecipeSourceSet,
    pub model: RecipeModelName,
    pub eval: RecipeEvalName,
    pub loader: RecipeLoaderName,
    pub train: RecipeTrainName,
    pub plan: RecipeGridSpec,
    pub seeds: Vec<i64>,
    pub n_trajectory_draws: i64,
    pub n_noise_draws: i64,
    pub run_whiten: bool,
    pub run_rewind: bool,
    pub scale_noise_by_std: bool,
    /// For deterministic/known-data noise-only designs, materialize dose zero
    /// once instead of once per observation-noise draw.
    pub single_clean_anchor: bool,
    /// Observation-noise law for the frozen bank draws. `Gaussian` is the
    /// legacy default and stays out of the recipe preimage so pre-existing
    /// recipe hashes remain stable; non-Gaussian laws are scientific protocol
    /// and appear in the recipe name, preimage, and run identity.
    pub noise_kind: fern_experiment_types::NoiseKind,
    /// Lag-one autocorrelation of the observation noise; a separate axis from
    /// `noise_kind`. Zero for every lane run so far, and zero is left out of
    /// the recipe name below, so existing recipe hashes do not move.
    pub noise_ar1_phi: f64,
    /// `None` preserves the legacy all-split protocol and its existing hashes.
    /// New non-default schemes are explicit in recipe JSON and run identity.
    pub split_treatment_scheme: Option<SplitTreatmentScheme>,
    /// A policy means positive test-only doses load one clean validation-best
    /// checkpoint without training. The variant pins whether this recipe owns
    /// the clean fit or shares the matching noise-matched dose-zero run.
    pub checkpoint_reuse_policy: Option<CheckpointReusePolicy>,
    /// Optional per-recipe Fern architecture override. `None` preserves the
    /// historical model-derived current/ICML-pinned behavior and stays out of
    /// existing recipe names and hashes.
    pub fern_architecture: Option<FernArchitectureVariant>,
    /// EnbPI ensemble declaration. `None` preserves every existing recipe's
    /// name and hash; `Some` multiplies expansion by B members whose
    /// bootstrap identity enters config, target, and run hashes.
    pub bootstrap: Option<RecipeBootstrapSpec>,
    /// Width of the base cloud Fern's predictive samples are drawn from, σ₀.
    ///
    /// Fern states an uncertainty of `source_std * (1 + c)` and learns only
    /// `c`, so this is the anchor the learned stretch is measured against.
    /// `None` keeps the historical 0.1 written in `fr.py` and `fr_cfg.py`, and
    /// therefore every existing recipe's name and hash.
    ///
    /// A NUMBER, not a named variant. It was briefly one compiled architecture
    /// name per value, which cost a rebuild, a scheduler reinstall and four
    /// separate registrations to try a second value. Nothing executes
    /// differently at a different σ₀ — code that already reads it just reads a
    /// different float — so it belongs in recipe data, where a JSON Recipe
    /// Extension can carry a new value with no rebuild at all. Contrast a new
    /// evaluator or world, which has a function behind it and stays an enum.
    pub source_std: Option<f64>,
    /// Optional finite-density ablation. Absence is the historical single
    /// Gaussian and preserves every existing recipe name and hash.
    pub fern_density: Option<RecipeFernDensitySpec>,
    /// Emit one magnitude per (channel, patch-group) block, on top of the
    /// per-step Brenier stretch.
    ///
    /// `false` is every recipe run before 2026-08-17 and stays out of the name
    /// and the preimage, so their hashes hold. The head answers whether Fern can
    /// state a block's SIZE separately from its shape; the shape head cannot,
    /// because its coefficient is clipped around a fixed base width.
    pub fern_radial_head: bool,
}

#[derive(Debug, Clone, PartialEq)]
pub struct RecipePreset {
    pub name: &'static str,
    pub label: &'static str,
    pub recipes: Vec<RecipeBlueprint>,
}

impl RecipeBlueprint {
    pub fn new(family: RecipeFamilyName) -> Self {
        Self {
            family: family.into(),
            model: RecipeModelName::Fern,
            eval: RecipeEvalName::Huber,
            loader: RecipeLoaderName::P70_20_10NoneBs95,
            train: RecipeTrainName::Default,
            plan: RecipePlanName::CleanH64.into(),
            seeds: vec![42],
            n_trajectory_draws: 1,
            n_noise_draws: 1,
            run_whiten: false,
            run_rewind: false,
            scale_noise_by_std: false,
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
        }
    }

    pub fn model(mut self, value: RecipeModelName) -> Self {
        self.model = value;
        self
    }

    pub fn eval(mut self, value: RecipeEvalName) -> Self {
        self.eval = value;
        self
    }

    pub fn loader(mut self, value: RecipeLoaderName) -> Self {
        self.loader = value;
        self
    }

    pub fn train(mut self, value: RecipeTrainName) -> Self {
        self.train = value;
        self
    }

    pub fn plan(mut self, value: RecipePlanName) -> Self {
        self.plan = value.into();
        self
    }

    pub fn seeds(mut self, value: &[i64]) -> Self {
        self.seeds = value.to_vec();
        self
    }

    pub fn trajectory_draws(mut self, value: i64) -> Self {
        self.n_trajectory_draws = value;
        self
    }

    pub fn noise_draws(mut self, value: i64) -> Self {
        self.n_noise_draws = value;
        self
    }

    pub fn run_whiten(mut self) -> Self {
        self.run_whiten = true;
        self
    }

    pub fn scale_noise_by_std(mut self) -> Self {
        self.scale_noise_by_std = true;
        self
    }

    pub fn single_clean_anchor(mut self) -> Self {
        self.single_clean_anchor = true;
        self
    }

    /// AR(1) temporal dependence with Gaussian innovations. Gaussian is an
    /// experimental control here, not a synonym for AR(1): it keeps the
    /// stationary one-time marginal exactly Gaussian while phi changes only
    /// temporal dependence. Other innovation laws define a different
    /// factorial experiment because their stationary marginal is generally
    /// not the named innovation law.
    pub fn noise_ar1_phi(mut self, value: f64) -> Self {
        self.noise_ar1_phi = value;
        self
    }

    pub fn noise_kind(mut self, value: fern_experiment_types::NoiseKind) -> Self {
        self.noise_kind = value;
        self
    }

    pub fn bootstrap(mut self, value: RecipeBootstrapSpec) -> Self {
        self.bootstrap = Some(value);
        self
    }

    pub fn split_treatment_scheme(mut self, value: SplitTreatmentScheme) -> Self {
        self.split_treatment_scheme = Some(value);
        self
    }

    pub fn clean_train_clean_select_test_titration(self) -> Self {
        self.split_treatment_scheme(SplitTreatmentScheme::CleanTrainCleanSelectTestTitration)
    }

    pub fn reuse_clean_validation_best(mut self) -> Self {
        self.checkpoint_reuse_policy = Some(CheckpointReusePolicy::CleanValidationBest);
        self
    }

    pub fn reuse_noise_matched_clean_validation_best(mut self) -> Self {
        self.checkpoint_reuse_policy = Some(CheckpointReusePolicy::NoiseMatchedCleanValidationBest);
        self
    }

    pub fn reuse_noise_matched_dose_validation_best(mut self) -> Self {
        self.checkpoint_reuse_policy = Some(CheckpointReusePolicy::NoiseMatchedDoseValidationBest);
        self
    }

    pub fn fern_architecture(mut self, value: FernArchitectureVariant) -> Self {
        self.fern_architecture = Some(value);
        self
    }

    pub fn source_std(mut self, value: f64) -> Self {
        self.source_std = Some(value);
        self
    }

    pub fn fern_radial_head(mut self) -> Self {
        self.fern_radial_head = true;
        self
    }

    pub fn fern_density(mut self, value: RecipeFernDensitySpec) -> Self {
        self.fern_density = Some(value);
        self
    }

    pub fn name(&self) -> String {
        let mut parts = vec![
            self.family.value(),
            self.model.value(),
            self.eval.value(),
            self.loader.value(),
        ];
        if self.train != RecipeTrainName::Default {
            parts.push(self.train.value());
        }
        parts.push(self.plan.value());
        if self.noise_kind != fern_experiment_types::NoiseKind::Gaussian {
            parts.push(self.noise_kind.value());
        }
        let mut name = parts.join("_");
        // Dynamic segments append to the joined string. They cannot be pushed
        // into `parts`, which holds &'static str — doing so needs Box::leak,
        // which leaks once per call and name() is called for every catalog
        // listing.
        if self.noise_ar1_phi != 0.0 {
            // Only a correlated lane carries this segment, so every existing
            // recipe name — and therefore every recipe hash — is untouched.
            name.push_str("_ar1_");
            name.push_str(&format!("{}", self.noise_ar1_phi).replace('.', "p").replace('-', "neg"));
        }
        if self.n_noise_draws > 4 {
            // Bank count belongs in identity, and it IS in the recipe hash
            // preimage — but the historical catalog left it out of the NAME,
            // so two recipes differing only in bank count were the same name
            // with different identities. Encoding it retroactively is not an
            // option: the catalog holds counts {4, 1, 3} unencoded across 244
            // recipes, and naming them would move 244 hashes and orphan every
            // completed run. Four is the historical maximum, so a segment
            // above four collides with nothing that already exists.
            name.push_str("_banks");
            name.push_str(&self.n_noise_draws.to_string());
        }
        if self.seeds != [42] {
            let seed_text = self.seeds.iter().map(i64::to_string).collect::<Vec<_>>().join("_");
            name.push_str("_seeds");
            name.push_str(&seed_text);
        }
        if let Some(split_treatment_scheme) = self.split_treatment_scheme {
            name.push('_');
            name.push_str(split_treatment_scheme.value());
        }
        if let Some(bootstrap) = self.bootstrap {
            // The declaration is identity, so it is in the NAME too (the
            // bank-count lesson: an identity knob absent from the name lets
            // two different experiments collide on one name). No existing
            // recipe carries a segment of this shape, so nothing moves.
            name.push_str(&format!(
                "_enbpi{}_blk{}_rs{}",
                bootstrap.ensemble_size, bootstrap.block_size, bootstrap.resample_seed_base
            ));
        }
        if let Some(checkpoint_reuse_policy) = self.checkpoint_reuse_policy {
            name.push_str("_reuse_");
            name.push_str(checkpoint_reuse_policy.value());
        }
        if self.single_clean_anchor {
            name.push_str("_single_clean_anchor");
        }
        if let Some(fern_architecture) = self.fern_architecture {
            name.push_str("_arch_");
            name.push_str(fern_architecture.value());
        }
        if self.fern_radial_head {
            name.push_str("_radial_head");
        }
        if let Some(source_std) = self.source_std {
            // An identity knob absent from the name lets two different
            // experiments collide on one name (correction 241). Nothing
            // existing carries this segment, so nothing moves.
            name.push_str("_src_");
            name.push_str(&format!("{source_std}").replace('.', "p").replace('-', "neg"));
        }
        if let Some(density) = self.fern_density {
            name.push_str("_density_");
            name.push_str(density.variant().value());
            name.push_str("_k");
            name.push_str(&density.component_count().to_string());
            if let Some(active) = density.sparse_active_component_count() {
                name.push_str("_r");
                name.push_str(&active.to_string());
            }
        }
        name
    }
}

impl RecipePreset {
    pub fn model_names(&self) -> Vec<&'static str> {
        self.recipes
            .iter()
            .map(|recipe| recipe.model.value())
            .collect::<std::collections::BTreeSet<_>>()
            .into_iter()
            .collect()
    }

    pub fn describe(&self) -> String {
        let models = self.model_names().join(",");
        let recipes = self
            .recipes
            .iter()
            .map(RecipeBlueprint::name)
            .collect::<Vec<_>>()
            .join(",");
        format!(
            "{} label={} models={} recipes={}",
            self.name, self.label, models, recipes
        )
    }
}

/// Historical H64 test-only-treatment smoke recipes.
///
/// OU is stochastic, so four paired trajectory/noise draws estimate
/// between-draw variation without a full cross product.  Every recipe uses
/// clean train/validation data, train-standard-deviation-relative doses, and
/// test-only titration. These older recipe names intentionally retain
/// independent per-dose fitting for reproducibility; they are not in the paper
/// preset and must not be reported as exact clean-selection regret.
fn paper_csr_ou_retraining_smoke_recipe_builds() -> Vec<RecipeBlueprint> {
    [
        RecipeModelName::Fern,
        RecipeModelName::Dlinear,
        RecipeModelName::Timemixer,
    ]
    .into_iter()
    .map(|model| {
        RecipeBlueprint::new(RecipeFamilyName::OuBase)
            .model(model)
            .plan(RecipePlanName::TitrationH64RelativeDoses)
            .trajectory_draws(4)
            .noise_draws(4)
            .scale_noise_by_std()
            .clean_train_clean_select_test_titration()
    })
    .collect()
}

/// Exact H64 CSR candidate set for the paper expansion gate.
///
/// Dose zero fits and saves the clean-validation-best checkpoint. Every
/// positive dose names that source run in its identity and only evaluates it
/// on the corresponding noisy test split. The later analysis selects one
/// candidate model using only aggregated dose-zero validation performance.
fn paper_csr_ou_recipe_builds() -> Vec<RecipeBlueprint> {
    paper_csr_ou_retraining_smoke_recipe_builds()
        .into_iter()
        .map(RecipeBlueprint::reuse_clean_validation_best)
        .collect()
}

/// Primary H64 launch matrix: three worlds × four model tracks. TimesFM was
/// deferred from the required report scope (2026-07-24, no time on the local
/// GPU); its recipes stay launchable here for a later cloud pass.
///
/// Each recipe expands to 7 relative doses × 4 paired banks = 28 cells.
/// At every dose, the model observes that dose's noisy train, validation, and
/// test splits. Trained models therefore fit independently inside each
/// dose-matched observed world; no clean checkpoint is assumed available.
///
/// CHG (2026-07-26): added the two Chronos-Bolt sizes. With Chronos-2 as the
/// only pretrained track, "pretrained models track noise-matched retraining"
/// could not be separated from "large pretrained models do". Bolt small and
/// base are a different pretrained family at two smaller sizes, so the lane
/// now carries a capacity axis. Existing recipe hashes are untouched; these
/// are new recipes in the same preset.
///
/// CHG (2026-07-26): TimesFM is removed from the paper presets. Its recipes
/// never completed and it is out of this paper by decision; the model
/// implementation and `RecipeModelName::Timesfm25_200m` stay, so the lane is
/// re-addable, but no paper preset expands it.
fn paper_core_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for family in [
        RecipeFamilyName::LgSsmBase,
        RecipeFamilyName::LgSsmSwitch,
        RecipeFamilyName::RosslerBase,
    ] {
        for model in [
            RecipeModelName::Dlinear,
            RecipeModelName::Chronos2,
            RecipeModelName::Tirex,
            RecipeModelName::ChronosBoltSmall,
            RecipeModelName::ChronosBoltBase,
            // Zeus emits the campaign's own nine quantile levels natively, so
            // it is the one pretrained track that enters coverage/WIS/PIT with
            // nothing reshaped. Runs on its own interpreter; see
            // docs/upstream_defects.md.
            RecipeModelName::Zeus,
        ] {
            let trajectory_draws = if family == RecipeFamilyName::RosslerBase { 1 } else { 4 };
            let recipe = RecipeBlueprint::new(family)
                .model(model)
                .plan(RecipePlanName::TitrationH64RelativeDoses)
                .trajectory_draws(trajectory_draws)
                .noise_draws(4)
                .scale_noise_by_std()
                .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining);
            recipes.push(recipe);
        }
    }
    recipes
}

/// Extended H64 lane: variance-matched sparse contamination — Huber's
/// gross-error model, with 2% of timestamps carrying a reading ten times the
/// ordinary one and the whole draw rescaled to unit variance.
///
/// This is the COMPLEMENT of the tail-weight ladder, not another rung of it.
/// Laplace and Student-t3 make every reading heavier-tailed and ask whether a
/// model can integrate a diffuse tail away. Contamination leaves 98% of the
/// context clean and makes a few readings enormous, asking instead whether one
/// outlier can capture a context window — a different failure channel, and the
/// one closest to a real sensor glitch or dropout.
///
/// Two properties make it a better heavy-tail probe than Student-t3 despite
/// the smaller kurtosis. Its fourth moment is FINITE (kurtosis about 68), so
/// squared-error confidence intervals are stable where Student-t3's are not.
/// And Huber loss is minimax optimal precisely over an epsilon-contamination
/// neighbourhood, so this lane carries a nameable prediction that can be
/// registered before it runs: a Huber-trained local model should degrade more
/// slowly here than an MSE-trained one.
///
/// The rate and scale are pinned constants, not tuning knobs.
/// Whether a world has ONE trajectory, so that repeated draws would all be the
/// same path.
///
/// A stochastic source redraws its system path per case, so the campaign pairs
/// trajectory ordinal with noise ordinal and asks for four of each. A
/// deterministic attractor has a single path from its registered initial
/// condition, so only the measurement varies and asking for four trajectories
/// would silently request four copies of one. Rössler was the only such family
/// in these lanes until Lorenz-63 joined them, and a hardcoded
/// `family == RosslerBase` check would have given Lorenz four identical
/// trajectories rather than failing.
fn is_deterministic_family(family: RecipeFamilyName) -> bool {
    matches!(family, RecipeFamilyName::RosslerBase | RecipeFamilyName::LorenzBase)
}

fn paper_extended_contaminated_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for family in [
        RecipeFamilyName::LgSsmBase,
        RecipeFamilyName::LgSsmSwitch,
        RecipeFamilyName::RosslerBase,
        // CHG (2026-08-11): see the Laplace lane's note — two of the three
        // worlds here are linear Gaussian, so Rössler was carrying every
        // heavy-tail claim about chaotic dynamics alone.
        RecipeFamilyName::LorenzBase,
    ] {
        for model in [
            RecipeModelName::Dlinear,
            RecipeModelName::Chronos2,
            RecipeModelName::Tirex,
            RecipeModelName::ChronosBoltSmall,
            RecipeModelName::ChronosBoltBase,
            RecipeModelName::Zeus,
            // CHG (2026-07-28): the trained deep panel joins every noise
            // law. Until now these four existed ONLY on the Gaussian core,
            // so every tail-law and correlation conclusion rested on the
            // fixed-weight panel plus one linear trained model.
            RecipeModelName::Fern,
            RecipeModelName::Patchtst,
            RecipeModelName::Timemixer,
            RecipeModelName::Duet,
        ] {
            let trajectory_draws = if is_deterministic_family(family) { 1 } else { 4 };
            recipes.push(
                RecipeBlueprint::new(family)
                    .model(model)
                    .plan(RecipePlanName::TitrationH64RelativeDoses)
                    .trajectory_draws(trajectory_draws)
                    .noise_draws(4)
                    .scale_noise_by_std()
                    .noise_kind(fern_experiment_types::NoiseKind::Contaminated)
                    .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining),
            );
        }
    }
    recipes
}

/// Extended H64 lane: variance-matched Student-t3 observation noise
/// (long-term plan item 1.4). Same three worlds, seven relative doses, four
/// paired banks, and noise-matched treatment as the core matrix, restricted
/// to the three required core models. Bank seed bases match the Gaussian
/// lane; each t3 draw is scaled by sqrt((nu-2)/nu) = sqrt(1/3) so it is
/// variance-matched and relative doses stay train-std-scale interventions.
/// This lane never blocks the Gaussian core report.
fn paper_extended_t3_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for family in [
        RecipeFamilyName::LgSsmBase,
        RecipeFamilyName::LgSsmSwitch,
        RecipeFamilyName::RosslerBase,
        // CHG (2026-08-11): see the Laplace lane's note — two of the three
        // worlds here are linear Gaussian, so Rössler was carrying every
        // heavy-tail claim about chaotic dynamics alone.
        RecipeFamilyName::LorenzBase,
    ] {
        for model in [
            RecipeModelName::Dlinear,
            RecipeModelName::Chronos2,
            RecipeModelName::Tirex,
            // CHG (2026-07-27): the two Bolt sizes join so the zero-shot panel
            // is the SAME four models on every noise axis. Without them the
            // tail-law axis compares a different set of forecasters than the
            // correlation axis does, and no cross-axis statement is possible.
            RecipeModelName::ChronosBoltSmall,
            RecipeModelName::ChronosBoltBase,
            RecipeModelName::Zeus,
            // CHG (2026-07-28): the trained deep panel joins every noise
            // law. Until now these four existed ONLY on the Gaussian core,
            // so every tail-law and correlation conclusion rested on the
            // fixed-weight panel plus one linear trained model.
            RecipeModelName::Fern,
            RecipeModelName::Patchtst,
            RecipeModelName::Timemixer,
            RecipeModelName::Duet,
        ] {
            let trajectory_draws = if is_deterministic_family(family) { 1 } else { 4 };
            recipes.push(
                RecipeBlueprint::new(family)
                    .model(model)
                    .plan(RecipePlanName::TitrationH64RelativeDoses)
                    .trajectory_draws(trajectory_draws)
                    .noise_draws(4)
                    .scale_noise_by_std()
                    .noise_kind(fern_experiment_types::NoiseKind::StudentT3)
                    .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining),
            );
        }
    }
    recipes
}

/// Extended H64 lane: variance-matched Laplace observation noise — the middle
/// rung of the tail-weight ladder (gaussian excess kurtosis 0, laplace 3,
/// student_t3 infinite). Same three worlds, seven relative doses, four paired
/// banks, and noise-matched treatment as the core matrix; draws use scale
/// b = 1/sqrt(2) so variance is one and relative doses stay train-std-scale
/// interventions. Never blocks the Gaussian core report.
///
/// CHG (2026-08-11): brought to the same ten-model panel as the Student-t3
/// lane. It had been left at three models while t3 and contamination grew to
/// ten, so the tail-weight ladder compared a different panel at its middle rung
/// than at its ends — the one comparison the ladder exists to support.
///
/// CHG (2026-08-11): Lorenz-63 joins all three tail-law lanes. Of the three
/// worlds they carried, two are linear Gaussian state-space models, so every
/// heavy-tail conclusion rested on a single chaotic world (Rössler). Lorenz is
/// a second chaotic attractor under the identical protocol.
fn paper_extended_laplace_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for family in [
        RecipeFamilyName::LgSsmBase,
        RecipeFamilyName::LgSsmSwitch,
        RecipeFamilyName::RosslerBase,
        RecipeFamilyName::LorenzBase,
    ] {
        for model in [
            RecipeModelName::Dlinear,
            RecipeModelName::Chronos2,
            RecipeModelName::Tirex,
            RecipeModelName::ChronosBoltSmall,
            RecipeModelName::ChronosBoltBase,
            RecipeModelName::Zeus,
            RecipeModelName::Fern,
            RecipeModelName::Patchtst,
            RecipeModelName::Timemixer,
            RecipeModelName::Duet,
        ] {
            let trajectory_draws = if is_deterministic_family(family) { 1 } else { 4 };
            recipes.push(
                RecipeBlueprint::new(family)
                    .model(model)
                    .plan(RecipePlanName::TitrationH64RelativeDoses)
                    .trajectory_draws(trajectory_draws)
                    .noise_draws(4)
                    .scale_noise_by_std()
                    .noise_kind(fern_experiment_types::NoiseKind::Laplace)
                    .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining),
            );
        }
    }
    recipes
}

/// Eight-bank core matrix: the same three worlds, seven doses, and treatment
/// as `paper-core-h64`, resampled over EIGHT frozen banks instead of four.
///
/// This is for the confidence intervals, not for new science. Every interval
/// in the campaign is a paired bootstrap over four banks, and a percentile
/// bootstrap at n=4 does not deliver its nominal coverage — simulated
/// 2026-07-27, a nominal 95% interval covers 79.9% at n=4, 88.5% at n=8, and
/// 92.0% at n=16. Too-narrow intervals overstate significance, the wrong
/// direction for a paper whose headline is that rankings are unstable. BCa was
/// implemented and measured and does NOT fix it: at n=4 the four-point
/// jackknife cannot estimate acceleration, and it came out worse than
/// percentile at n=8 and n=16. More banks is the only remedy.
///
/// Banks 0..3 are bitwise identical to the four-bank lane, because `draw_seed`
/// is a pure function of `(seed_base, draw_ordinal)` — asking for eight draws
/// does not disturb the first four. The recipe hash still differs, since
/// `n_noise_draws` is in the recipe preimage, so execute-matrix cannot skip the
/// cells it already holds and this lane recomputes all 168. That is
/// bookkeeping cost, not scientific cost: the recomputed cells reproduce their
/// existing values, which is itself a reproducibility check.
///
/// Restricted to the four models that finish a lane in under an hour. Pairing
/// requires every compared model to share the same banks, so an eight-bank
/// claim spans only the models that all have eight; TiRex at 179 s per cell
/// would be 8.4 hours alone and stays at four.
fn paper_core_h64_banks8_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for family in [
        RecipeFamilyName::LgSsmBase,
        RecipeFamilyName::LgSsmSwitch,
        RecipeFamilyName::RosslerBase,
    ] {
        for model in [
            RecipeModelName::ChronosBoltSmall,
            RecipeModelName::ChronosBoltBase,
            // CHG (2026-07-27): the headline claim is the DLinear--TiRex
            // reversal on Rossler. Confirming it on eight banks requires
            // TiRex here; four banks cannot reach a two-sided sign-flip
            // p below 0.125 no matter how large the margin is.
            RecipeModelName::Tirex,
            RecipeModelName::Dlinear,
            RecipeModelName::Chronos2,
        ] {
            // Rossler is deterministic: one trajectory, many noise draws.
            let trajectory_draws = if family == RecipeFamilyName::RosslerBase { 1 } else { 8 };
            recipes.push(
                RecipeBlueprint::new(family)
                    .model(model)
                    .plan(RecipePlanName::TitrationH64RelativeDoses)
                    .trajectory_draws(trajectory_draws)
                    .noise_draws(8)
                    .scale_noise_by_std()
                    .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining),
            );
        }
    }
    recipes
}

/// Confirm the discovery-grid DLinear--TiREx Rössler reversal on trajectories
/// that were not part of model or analysis selection.
///
/// The eight family cells are the complete ±0.01 cube around the historical
/// x0=(1,1,1), not eight observation-noise redraws on one fixed trajectory.
/// Only the two predeclared comparison doses are run: clean establishes the
/// starting rank, and d=1/8 tests the discovered reversal. One noise draw per
/// initial condition gives eight paired units without pretending that 64
/// horizon coordinates are independent observations.
fn paper_confirm_rossler_initial_conditions_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    [RecipeModelName::Dlinear, RecipeModelName::Tirex]
        .into_iter()
        .map(|model| {
            RecipeBlueprint::new(RecipeFamilyName::RosslerInitialConditionHoldout)
                .model(model)
                .plan(RecipePlanName::RosslerInitialConditionHoldoutH64)
                .trajectory_draws(1)
                .noise_draws(1)
                .scale_noise_by_std()
                .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining)
        })
        .collect()
}

/// The sixteen randomly drawn initial conditions, same two doses and same two
/// models as the corner lane.
///
/// Reusing `RosslerInitialConditionHoldoutH64` as the PLAN is deliberate: the
/// horizon and the two comparison doses must be identical to the corner lane
/// or the two cannot be read on one scale. Only the family (and therefore the
/// set of trajectories) differs.
fn paper_confirm_rossler_random_initial_conditions_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    [RecipeModelName::Dlinear, RecipeModelName::Tirex]
        .into_iter()
        .map(|model| {
            RecipeBlueprint::new(RecipeFamilyName::RosslerInitialConditionRandom)
                .model(model)
                .plan(RecipePlanName::RosslerInitialConditionHoldoutH64)
                .trajectory_draws(1)
                .noise_draws(1)
                .scale_noise_by_std()
                .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining)
        })
        .collect()
}

/// Correlated-noise H64 lane: the marginal law is held at gaussian and only
/// the observation noise's lag-one autocorrelation moves.
///
/// The tail axis is closed. Gaussian, Laplace, and variance-matched
/// Student-t3 all produced the same dose response, with paired intervals
/// covering one, so a heavier tail is not what the protocol was missing.
/// Every one of those lanes drew each timestep INDEPENDENTLY, which is an
/// assumption about the measurement process rather than a fact about it —
/// drift, thermal effects, and calibration offsets are correlated in time.
///
/// phi is the knob from "trivially filtered" to "indistinguishable from
/// signal": at phi = 0 the noise is white and a model can average it out
/// inside its context window; as phi approaches one the same dose looks like a
/// slow trend the model will instead try to fit. Variance is held at one
/// throughout, so the dose keeps meaning the same multiple of the clean
/// training standard deviation and the correlation is the only thing varying.
///
/// phi = 0 is the existing iid Gaussian core lane and is NOT duplicated in
/// this preset. The bitwise-equivalence test proves that it is a valid shared
/// reference; this lane adds only phi = 0.5 and 0.9.
///
/// Fast models first: Chronos-Bolt and DLinear return a full lane in tens of
/// minutes, so the axis can be read before committing TiRex-scale time to it.
fn paper_correlated_noise_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for family in [
        RecipeFamilyName::LgSsmBase,
        RecipeFamilyName::LgSsmSwitch,
        RecipeFamilyName::RosslerBase,
    ] {
        for model in [
            RecipeModelName::ChronosBoltSmall,
            RecipeModelName::ChronosBoltBase,
            RecipeModelName::Dlinear,
            RecipeModelName::Chronos2,
            RecipeModelName::Zeus,
            // CHG (2026-07-27): TiRex joins so the model set matches the
            // Gaussian and t3 lanes. Without it the cross-law comparison of
            // REVERSAL dose is confounded by which models are present; only
            // the within-model fold change was comparable.
            RecipeModelName::Tirex,
            // CHG (2026-07-28): the trained deep panel joins. The AR(1)
            // result so far rests on ONE trained model, DLinear, which is
            // also the only LINEAR model -- so 'a linear model absorbs a
            // linear filter' and 'a trained model absorbs it' are
            // confounded. A trained non-linear forecaster separates them.
            RecipeModelName::Timemixer,
            RecipeModelName::Patchtst,
            // CHG (2026-07-28): the trained deep panel joins every noise
            // law. Until now these four existed ONLY on the Gaussian core,
            // so every tail-law and correlation conclusion rested on the
            // fixed-weight panel plus one linear trained model.
            RecipeModelName::Fern,
            RecipeModelName::Duet,
        ] {
            for phi in [0.5_f64, 0.9] {
                let trajectory_draws = if family == RecipeFamilyName::RosslerBase { 1 } else { 4 };
                recipes.push(
                    RecipeBlueprint::new(family)
                        .model(model)
                        .plan(RecipePlanName::TitrationH64RelativeDoses)
                        .trajectory_draws(trajectory_draws)
                        .noise_draws(4)
                        .scale_noise_by_std()
                        .noise_ar1_phi(phi)
                        .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining),
                );
            }
        }
    }
    recipes
}

/// Real-sensor H64 noise-dial lane: the same seven-dose, four-bank,
/// noise-matched protocol as the synthetic core, applied to recorded HuGaDB
/// walking. `hugadb_accel_emg` is one continuous activity (the stationary
/// real-data reference); `hugadb_walk_run_natural` is one continuous
/// recording in which the subject changed activity (walk -> run -> walk at
/// 36.4%), so the real-data lane carries the same two axes as the synthetic
/// worlds: a stationary world and a world where the system changed.
/// Tier-3 claims only: the recorded series is a reference trajectory, never
/// an exact latent physical truth.
///
/// CHG (2026-07-26): added the two Chronos-Bolt sizes here first. This is the
/// lane where the pretrained tracks separated from DLinear, so it is where a
/// capacity axis is most informative.
///
/// CHG (2026-07-26): added `hugadb_walk_only_natural`. `hugadb_accel_emg` is
/// subject 06 while the changed world is subject 14, so the two of them
/// together cannot say whether a difference comes from the activity change or
/// from the different person, session, and recording length. The added world
/// is subject 14 walking throughout — same subject, same sensors, same
/// session as the changed world — so that pair isolates the activity change.
/// `hugadb_accel_emg` stays: it is a second recorded stationary reference and
/// its cells are already run.
fn paper_real_sensor_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for family in [
        RecipeFamilyName::HugadbAccelEmg,
        RecipeFamilyName::HugadbWalkRunNatural,
        RecipeFamilyName::HugadbWalkOnlyNatural,
    ] {
        for model in [
            RecipeModelName::Dlinear,
            RecipeModelName::Chronos2,
            RecipeModelName::Tirex,
            RecipeModelName::ChronosBoltSmall,
            RecipeModelName::ChronosBoltBase,
            // Zeus emits the campaign's own nine quantile levels natively, so
            // it is the one pretrained track that enters coverage/WIS/PIT with
            // nothing reshaped. Runs on its own interpreter; see
            // docs/upstream_defects.md.
            RecipeModelName::Zeus,
        ] {
            recipes.push(
                RecipeBlueprint::new(family)
                    .model(model)
                    // Recorded series: one fixed trajectory, four noise draws.
                    .trajectory_draws(1)
                    .noise_draws(4)
                    .plan(RecipePlanName::TitrationH64RelativeDoses)
                    .scale_noise_by_std()
                    .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining),
            );
        }
    }
    recipes
}

/// Solar power: a recorded world that is not a human body.
///
/// The recorded axis so far is entirely HuGaDB — three worlds, all of them
/// inertial and muscle sensors on one walking person. Any statement of the form
/// "recorded series behave differently from simulated ones" is therefore also a
/// statement about gait, and the two cannot be told apart. Solar output shares
/// none of gait's structure: its driving cycle is daily rather than a limb
/// cadence, its 137 channels are geographically separated plants responding
/// to one sky rather than joints on one skeleton, and it is pinned at zero
/// every night, which is a hard floor no simulated world in the campaign has.
///
/// All 137 plants, not the registered sixteen-plant subset: a subset is a
/// choice that has to be defended, and it makes the series width incomparable
/// with the published Solar-Energy numbers other work reports.
///
/// One fixed recording, four frozen noise banks, the same seven relative doses
/// and noise-matched treatment as every other lane, so it drops straight into
/// the existing per-world curves. Tier-3 claims only: like HuGaDB, the recorded
/// series is a reference trajectory and never an exact latent physical truth,
/// so there is no oracle panel for it.
fn paper_solar_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for model in [
        RecipeModelName::Dlinear,
        RecipeModelName::Chronos2,
        RecipeModelName::Tirex,
        RecipeModelName::ChronosBoltSmall,
        RecipeModelName::ChronosBoltBase,
        RecipeModelName::Zeus,
        RecipeModelName::Fern,
        RecipeModelName::Patchtst,
        RecipeModelName::Timemixer,
        RecipeModelName::Duet,
    ] {
        recipes.push(
            RecipeBlueprint::new(RecipeFamilyName::SolarEnergy)
                .model(model)
                // Recorded series: one fixed trajectory, four noise draws.
                .trajectory_draws(1)
                .noise_draws(4)
                .plan(RecipePlanName::TitrationH64RelativeDoses)
                .scale_noise_by_std()
                .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining),
        );
    }
    recipes
}

/// The 16-channel solar subset: the full 137-plant grid priced at 20+ minutes
/// per cell and never completed one (correction 270); the subset keeps the
/// many-channel column at tractable cost. Channel selection is DETERMINISTIC,
/// not random: plants at evenly spaced positions 0, 9, 18, …, 136 of the full
/// roster (endpoints included), verified from the asset bytes 2026-08-13.
/// Zero-shot models only, per the world-roster direction note: the zoo runs
/// locally in hours; trained models are a separate decision.
fn paper_solar_subset_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for model in [
        RecipeModelName::Chronos2,
        RecipeModelName::Tirex,
        RecipeModelName::ChronosBoltSmall,
        RecipeModelName::ChronosBoltBase,
        RecipeModelName::Zeus,
    ] {
        recipes.push(
            RecipeBlueprint::new(RecipeFamilyName::SolarEnergySubset)
                .model(model)
                // Recorded series: one fixed trajectory, four noise draws.
                .trajectory_draws(1)
                .noise_draws(4)
                .plan(RecipePlanName::TitrationH64RelativeDoses)
                .scale_noise_by_std()
                .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining),
        );
    }
    recipes
}

/// The two simple synthetic worlds the campaign registered and then never ran.
///
/// `ou` is mean-reverting and linear with a Gaussian stationary law; it is the
/// simplest possible world in the bank, and it holds two completed cells out of
/// a designed lane — a single DLinear recipe from an early pilot. `seasonal_ar`
/// is autoregressive with a periodic component and has never run at all. Both
/// matter for the same reason: every world currently carrying the paper is
/// either chaotic, switching, or recorded, so a reader cannot tell whether the
/// reported ranking instability needs difficult dynamics or appears in the
/// easiest well-behaved series too. These two answer that at low cost.
///
/// `seasonal_ar` contributes its own parameter-shock variant, so this lane also
/// gets a system-change world for free under the same observation dial.
fn paper_expansion_ou_seasonal_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for family in [RecipeFamilyName::OuBase, RecipeFamilyName::SeasonalAr] {
        for model in [
            RecipeModelName::Dlinear,
            RecipeModelName::Chronos2,
            RecipeModelName::Tirex,
            RecipeModelName::ChronosBoltSmall,
            RecipeModelName::ChronosBoltBase,
            RecipeModelName::Zeus,
            RecipeModelName::Fern,
            RecipeModelName::Patchtst,
            RecipeModelName::Timemixer,
            RecipeModelName::Duet,
        ] {
            recipes.push(
                RecipeBlueprint::new(family)
                    .model(model)
                    .plan(RecipePlanName::TitrationH64RelativeDoses)
                    // Both are stochastic: the system path is redrawn per case,
                    // so trajectory and noise ordinals pair four with four.
                    .trajectory_draws(4)
                    .noise_draws(4)
                    .scale_noise_by_std()
                    .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining),
            );
        }
    }
    recipes
}

/// Deadline-bounded trained-model completion for the matched HuGaDB pair.
///
/// The stationary control and natural walk-to-run recording come from the
/// same subject, sensor layout, and session. Keeping only this pair answers
/// the system-change question without spending another 28 cells per model on
/// the already-covered accel/EMG reference. Fern uses the explicit ICML-pinned
/// architecture identity; `fern` (the current research implementation) is not
/// a compatibility alias for it.
fn paper_real_sensor_trained_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for family in [
        RecipeFamilyName::HugadbWalkOnlyNatural,
        RecipeFamilyName::HugadbWalkRunNatural,
    ] {
        for model in [
            RecipeModelName::FernIcmlPinned,
            RecipeModelName::Timemixer,
            RecipeModelName::Patchtst,
            RecipeModelName::Duet,
        ] {
            recipes.push(
                RecipeBlueprint::new(family)
                    .model(model)
                    .trajectory_draws(1)
                    .noise_draws(4)
                    .plan(RecipePlanName::TitrationH64RelativeDoses)
                    .scale_noise_by_std()
                    .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining),
            );
        }
    }
    recipes
}

/// The trained models the chaotic Lorenz lane never had. Lorenz carries six
/// models where the Rossler and LG-SSM worlds carry ten, and every
/// cross-world claim is limited by its thinnest world -- a dose response that
/// "agrees on both attractors" is a weaker statement when one of them is
/// missing four of the trained baselines.
///
/// Separate from `paper-chaotic-lorenz-h64` rather than added to it, following
/// `paper-real-sensor-trained-h64`: the existing preset is a published object
/// and widening it in place would silently change what a completed run of that
/// name means. Fern is deliberately absent here because the ICML-pinned build
/// is already registered in that preset, and one recipe may have exactly one
/// catalog owner.
/// Tier 1: the same seven-dose titration on sixteen INDEPENDENT Lorenz-63
/// trajectories, every model. This is the blind validation world -- the
/// analysis is frozen before it runs, so what comes back is a test of the
/// existing phenomenon rather than a search for a new one.
fn paper_lorenz_initial_conditions_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for model in [
        RecipeModelName::Dlinear,
        RecipeModelName::Tirex,
        // Zeus emits the campaign's own nine quantile levels natively, so it is
        // the one pretrained track that enters coverage/WIS/PIT with nothing
        // reshaped. Leaving it out would make the probabilistic half of this
        // panel weaker than the panel it is meant to validate.
        RecipeModelName::Zeus,
        RecipeModelName::Chronos2,
        RecipeModelName::ChronosBoltSmall,
        RecipeModelName::ChronosBoltBase,
        RecipeModelName::Timemixer,
        RecipeModelName::Patchtst,
        RecipeModelName::Duet,
    ] {
        recipes.push(
            RecipeBlueprint::new(RecipeFamilyName::LorenzInitialConditionRandom)
                .model(model)
                // One trajectory per source name; the sixteen sources ARE the
                // sixteen realisations, so the draw count stays one and the
                // independence lives in the source list.
                .trajectory_draws(1)
                .noise_draws(1)
                .plan(RecipePlanName::TitrationH64RelativeDoses)
                .scale_noise_by_std()
                .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining),
        );
    }
    recipes
}

fn paper_chaotic_lorenz_trained_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for family in [RecipeFamilyName::LorenzBase, RecipeFamilyName::LorenzParamShock] {
        for model in [
            RecipeModelName::Timemixer,
            RecipeModelName::Duet,
            RecipeModelName::Patchtst,
            // Zeus runs on its own interpreter (flash-attn); its execution
            // profile routes it there. It is the one pretrained track that
            // emits the campaign's nine quantile levels natively.
            RecipeModelName::Zeus,
        ] {
            recipes.push(
                RecipeBlueprint::new(family)
                    .model(model)
                    // Same protocol as the lane it completes: deterministic
                    // attractor, one trajectory, four frozen noise draws.
                    .trajectory_draws(1)
                    .noise_draws(4)
                    .plan(RecipePlanName::TitrationH64RelativeDoses)
                    .scale_noise_by_std()
                    .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining),
            );
        }
    }
    recipes
}

/// Lorenz-96 under the same seven-dose titration protocol as every other world.
///
/// The control this world supplies is CHANNEL COUNT. Rossler has three channels
/// and one of them, z, carries 86.8% of the pooled error, so every tail result
/// there is open to the reading that one variable dominates the benchmark.
/// Lorenz-96 has forty channels of essentially symmetric form, so if the error
/// spreads across variables and the tail concentration still appears, that
/// phenomenon is not an artefact of one dominant observable -- and if it
/// disappears, that is equally informative and says the effect depends on
/// attractor geometry.
///
/// Existing lorenz96 recipes are the old clean Fern-only shape with no dose
/// ladder; this is the titration protocol, so the recipes are new rather than
/// reused, and the catalog owns each exactly once.
fn paper_lorenz96_titration_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for family in [RecipeFamilyName::Lorenz96] {
        for model in [
            RecipeModelName::Dlinear,
            RecipeModelName::Chronos2,
            RecipeModelName::Tirex,
            RecipeModelName::ChronosBoltSmall,
            RecipeModelName::ChronosBoltBase,
            RecipeModelName::Timemixer,
            RecipeModelName::Patchtst,
            RecipeModelName::Duet,
        ] {
            recipes.push(
                RecipeBlueprint::new(family)
                    .model(model)
                    .trajectory_draws(1)
                    .noise_draws(4)
                    .plan(RecipePlanName::TitrationH64RelativeDoses)
                    .scale_noise_by_std()
                    .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining),
            );
        }
    }
    recipes
}

/// Chaotic replication lane: Lorenz-63 stationary and Lorenz-63 under a
/// parameter shift, run through the same seven-dose, four-bank, noise-matched
/// protocol as the Rossler worlds. The point is replication across attractor
/// geometry — Lorenz has two lobes and a different Lyapunov spectrum than
/// Rossler's folded band, so a dose response that agrees on both is a
/// property of the protocol rather than of one system.
fn paper_chaotic_lorenz_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for family in [RecipeFamilyName::LorenzBase, RecipeFamilyName::LorenzParamShock] {
        for model in [
            // The Pod Fern lane uses the architecture explicitly pinned to
            // the submitted ICML implementation. Keep that exact model
            // identity in the source preset; a one-off binary patch is not a
            // reproducible experiment definition.
            RecipeModelName::FernIcmlPinned,
            RecipeModelName::Dlinear,
            RecipeModelName::Chronos2,
            RecipeModelName::ChronosBoltSmall,
            RecipeModelName::ChronosBoltBase,
            RecipeModelName::Tirex,
        ] {
            recipes.push(
                RecipeBlueprint::new(family)
                    .model(model)
                    // Lorenz is a deterministic attractor: one trajectory,
                    // four frozen observation-noise draws.
                    .trajectory_draws(1)
                    .noise_draws(4)
                    .plan(RecipePlanName::TitrationH64RelativeDoses)
                    .scale_noise_by_std()
                    .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining),
            );
        }
    }
    recipes
}

/// Optional H64 TimeMixer expansion: the same three worlds and paired-dose
/// protocol as the core matrix, without making this trained point baseline a
/// fail-closed dependency of the core report.
fn paper_expansion_timemixer_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    [
        RecipeFamilyName::LgSsmBase,
        RecipeFamilyName::LgSsmSwitch,
        RecipeFamilyName::RosslerBase,
    ]
    .into_iter()
    .map(|family| {
        let trajectory_draws = if family == RecipeFamilyName::RosslerBase { 1 } else { 4 };
        RecipeBlueprint::new(family)
            .model(RecipeModelName::Timemixer)
            .plan(RecipePlanName::TitrationH64RelativeDoses)
            .trajectory_draws(trajectory_draws)
            .noise_draws(4)
            .scale_noise_by_std()
            .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining)
    })
    .collect()
}

/// H64 trained-model expansion for Fern and PatchTST.
///
/// This stays separate from the completed core DB and the independently
/// running TimeMixer lane. Each model receives the same three worlds, seven
/// train-standard-deviation-relative doses, and four paired banks under
/// noise-matched train/validation/test treatment.
fn paper_expansion_fern_patchtst_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for family in [
        RecipeFamilyName::LgSsmBase,
        RecipeFamilyName::LgSsmSwitch,
        RecipeFamilyName::RosslerBase,
    ] {
        for model in [RecipeModelName::Fern, RecipeModelName::Patchtst] {
            let trajectory_draws = if family == RecipeFamilyName::RosslerBase { 1 } else { 4 };
            recipes.push(
                RecipeBlueprint::new(family)
                    .model(model)
                    .plan(RecipePlanName::TitrationH64RelativeDoses)
                    .trajectory_draws(trajectory_draws)
                    .noise_draws(4)
                    .scale_noise_by_std()
                    .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining),
            );
        }
    }
    recipes
}

/// The same two worlds as the Fern expansion, scored by a proper score.
///
/// Every campaign lane that retained per-window tensors trained under Huber, so
/// Fern's covariance was never in the objective it was fitted against. Measured
/// on those stored runs, the block distance divided by its degrees of freedom
/// comes out near 60 on LGSSM where a calibrated covariance would give 1 — the
/// residuals are about eight times the predicted spread. Nothing downstream of
/// that standardization can be read: whether the residual's shape carries
/// information, and whether its direction is uniform, are both measured through
/// a covariance that was never asked to be one.
///
/// This lane changes exactly ONE property against
/// `paper_expansion_fern_patchtst_h64_recipe_builds` — the eval — so the pair
/// is a controlled comparison rather than two separate experiments. Rössler and
/// LGSSM are the two worlds because they disagreed on the question this is
/// built to settle: in the stored Rössler runs the two halves of a 64-step
/// forecast share a residual magnitude (correlated at 0.41), while in LGSSM
/// they do not (0.00 to 0.08). Fern's patch structure asserts one magnitude per
/// 32 steps, so those two worlds sit on opposite sides of its assumption.
///
/// One preset per world, deliberately. A run-plan stage must resolve to exactly
/// ONE Matrix, and the scheduler that dispatches it resolves the stage's name
/// against the PRESET catalog only -- so a preset spanning two worlds can be
/// rehearsed by `plan` and then never dispatched, which is how this lane failed
/// on 2026-08-15. Keeping one preset per Matrix makes the two agree.
/// The model is a parameter so the scale-ceiling arm is the SAME builder with
/// one field changed, rather than a near-duplicate that can drift from it.
/// Passing `Fern` reproduces the recipe the calibration lane already ran, so no
/// existing recipe name, config hash or run hash moves.
/// Base cloud widths this sweep runs, bracketing the 0.1 built into the model.
///
/// A LIST OF NUMBERS, not a list of compiled names: adding a fourth point is an
/// edit here or a JSON Recipe Extension, never a rebuild and redeploy.
const RADIAL_NLL_SOURCE_STD_SWEEP: [f64; 2] = [1.0, 10.0];

fn radial_nll_calibration_h64_recipe_build(family: RecipeFamilyName, model: RecipeModelName) -> RecipeBlueprint {
    let trajectory_draws = if family == RecipeFamilyName::RosslerBase { 1 } else { 4 };
    RecipeBlueprint::new(family)
        .model(model)
        .eval(RecipeEvalName::FernNllS16E32NoSwd)
        .plan(RecipePlanName::TitrationH64RelativeDoses)
        .trajectory_draws(trajectory_draws)
        .noise_draws(4)
        .scale_noise_by_std()
        .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining)
}

/// Full H64 rerun of the paper-bounds Fern reconstruction.
///
/// The separate preset keeps `fern` and `fern_icml_pinned` as exact model
/// identities: callers never need to patch a preset or rely on a runtime alias.
fn paper_expansion_fern_icml_pinned_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    [
        RecipeFamilyName::LgSsmBase,
        RecipeFamilyName::LgSsmSwitch,
        RecipeFamilyName::RosslerBase,
    ]
    .into_iter()
    .map(|family| {
        let trajectory_draws = if family == RecipeFamilyName::RosslerBase { 1 } else { 4 };
        RecipeBlueprint::new(family)
            .model(RecipeModelName::FernIcmlPinned)
            .plan(RecipePlanName::TitrationH64RelativeDoses)
            .trajectory_draws(trajectory_draws)
            .noise_draws(4)
            .scale_noise_by_std()
            .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining)
    })
    .collect()
}

/// Optional DUET H64 expansion after official-source adapter parity.
fn paper_expansion_duet_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    [
        RecipeFamilyName::LgSsmBase,
        RecipeFamilyName::LgSsmSwitch,
        RecipeFamilyName::RosslerBase,
    ]
    .into_iter()
    .map(|family| {
        let trajectory_draws = if family == RecipeFamilyName::RosslerBase { 1 } else { 4 };
        RecipeBlueprint::new(family)
            .model(RecipeModelName::Duet)
            .plan(RecipePlanName::TitrationH64RelativeDoses)
            .trajectory_draws(trajectory_draws)
            .noise_draws(4)
            .scale_noise_by_std()
            .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining)
    })
    .collect()
}

/// One-cell paper-bounds reconstruction canary for Fern.
///
/// This holds the current Rössler parameter-shift data, seed, loader, training
/// loss, and validation selection fixed. Only the hashed Fern architecture
/// variant changes, so the result is an interpretable current-vs-ICML A/B.
fn paper_canary_rossler_param_shift_fern_icml_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    vec![
        RecipeBlueprint::new(RecipeFamilyName::RosslerParamShock)
            .model(RecipeModelName::FernIcmlPinned)
            .loader(RecipeLoaderName::P70_20_10NoneBs95)
            .plan(RecipePlanName::CleanH64)
            .trajectory_draws(1)
            .noise_draws(1),
    ]
}

/// Clean H64 2×2×2 Fern architecture ablation on the original Rössler shift.
///
/// The eight recipes hold the generated trajectory, split, model seed,
/// optimizer, validation selection, patching, and all non-ablated model knobs
/// fixed. Only encoder layout, free-scale clamp, and the positive Brenier
/// upper bound vary. This is a causal implementation diagnosis, not a noise
/// titration matrix.
fn paper_canary_rossler_shift_fern_factorial_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    FernArchitectureVariant::rossler_three_factor_cube()
        .into_iter()
        .map(|fern_architecture| {
            RecipeBlueprint::new(RecipeFamilyName::RosslerParamShock)
                .model(RecipeModelName::Fern)
                .loader(RecipeLoaderName::P70_20_10NoneBs95)
                .plan(RecipePlanName::CleanH64)
                .trajectory_draws(1)
                .noise_draws(1)
                .fern_architecture(fern_architecture)
        })
        .collect()
}

/// Original Rössler parameter-shift paper expansion.
///
/// The deterministic source keeps the standard Rössler equations and initial
/// state, then changes `(a, b, c): (0.2, 0.2, 5.7) -> (0.25, 0.25, 5.75)`
/// at 35% of the sequence. One trajectory plus four frozen observation-noise
/// banks is enough; the base Rössler world is not duplicated in this preset.
fn paper_expansion_rossler_param_shift_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    [
        RecipeModelName::Dlinear,
        RecipeModelName::Chronos2,
        RecipeModelName::Tirex,
        // The paper model is the ICML-bounds reconstruction. Historical
        // current-`fern` rows remain in their old DB but are not a source for
        // the dual-dial return-to-clean comparison.
        RecipeModelName::FernIcmlPinned,
        RecipeModelName::Patchtst,
        RecipeModelName::Timemixer,
        RecipeModelName::Duet,
        // CHG (2026-07-26): the capacity axis reaches the expansion worlds.
        RecipeModelName::ChronosBoltSmall,
        RecipeModelName::ChronosBoltBase,
        RecipeModelName::Zeus,
    ]
    .into_iter()
    .map(|model| {
        RecipeBlueprint::new(RecipeFamilyName::RosslerParamShock)
            .model(model)
            .loader(RecipeLoaderName::P70_20_10NoneBs95)
            .plan(RecipePlanName::TitrationH64RelativeDoses)
            .trajectory_draws(1)
            .noise_draws(4)
            .scale_noise_by_std()
            .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining)
    })
    .collect()
}

/// Rössler dual-dial evaluation-only companion.
///
/// The two worlds hold the dynamical-system dial at Base versus the registered
/// parameter shift. Within each world, the observation dial uses the same
/// seven relative doses and four frozen banks as the noise-matched source
/// matrix. Dose zero is shared with that source; every positive cell loads the
/// validation-best checkpoint fitted at the same dose and bank, keeps
/// train/validation noisy, and evaluates on the retained clean test interval.
///
/// Together with the noise-matched source matrix, the two evaluation recipes
/// per model complete all three deployment views without another fit:
/// clean-selected -> noisy test, noise-matched -> noisy test, and
/// noise-matched -> clean test. Foundation models are absent because they have
/// no fitted adaptation policy to return to clean.
fn paper_rossler_dual_dial_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for family in [RecipeFamilyName::RosslerBase, RecipeFamilyName::RosslerParamShock] {
        for model in [
            RecipeModelName::Dlinear,
            RecipeModelName::FernIcmlPinned,
            RecipeModelName::Patchtst,
            RecipeModelName::Timemixer,
            RecipeModelName::Duet,
        ] {
            recipes.push(
                RecipeBlueprint::new(family)
                    .model(model)
                    .loader(RecipeLoaderName::P70_20_10NoneBs95)
                    .plan(RecipePlanName::TitrationH64RelativeDoses)
                    .trajectory_draws(1)
                    .noise_draws(4)
                    .scale_noise_by_std()
                    .clean_train_clean_select_test_titration()
                    .reuse_noise_matched_clean_validation_best(),
            );
            recipes.push(
                RecipeBlueprint::new(family)
                    .model(model)
                    .loader(RecipeLoaderName::P70_20_10NoneBs95)
                    .plan(RecipePlanName::TitrationH64RelativeDoses)
                    .trajectory_draws(1)
                    .noise_draws(4)
                    .scale_noise_by_std()
                    .split_treatment_scheme(SplitTreatmentScheme::NoiseTrainNoiseSelectCleanTest)
                    .reuse_noise_matched_dose_validation_best(),
            );
        }
    }
    recipes
}

/// Stronger LG-SSM change-world expansion.
///
/// Unlike `lg_ssm_switch` (frequency only), this world changes frequency,
/// damping/memory, non-normal channel geometry, and process-noise scale at the
/// known halfway boundary. It remains linear-Gaussian, so exact time-varying
/// Kalman truth stays admissible. Keeping it in a separate preset avoids
/// changing the completed core matrix or its expected row count.
fn paper_expansion_lgssm_regime_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    [
        RecipeModelName::Dlinear,
        RecipeModelName::Chronos2,
        RecipeModelName::Tirex,
        RecipeModelName::FernIcmlPinned,
        RecipeModelName::Patchtst,
        RecipeModelName::Timemixer,
        RecipeModelName::Duet,
    ]
    .into_iter()
    .map(|model| {
        RecipeBlueprint::new(RecipeFamilyName::LgSsmRegimeSwitch)
            .model(model)
            .loader(RecipeLoaderName::P70_20_10NoneBs95)
            .plan(RecipePlanName::TitrationH64RelativeDoses)
            .trajectory_draws(4)
            .noise_draws(4)
            .scale_noise_by_std()
            .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining)
    })
    .collect()
}

/// Submission-window Hard-Switch core.
///
/// Eight independent stochastic trajectory/noise pairs matter more than
/// interpolating all seven dose levels. The four predeclared doses isolate
/// the clean endpoint, moderate onset, middle regime, and stress endpoint.
/// DLinear is the primary fitted model and retains eight pairs. TimeMixer,
/// PatchTST, and DUET are trained breadth checks with four pairs each; unlike
/// fixed-weight foundation models, their noise-matched source and
/// clean-selected deployment policies are both identifiable. The clean anchor
/// is shared between policies, so DLinear has 32 source plus 24 positive-dose
/// reuse cells, while each breadth model has 16 plus 12.
fn paper_lgssm_regime_primary_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    [
        (RecipeModelName::Dlinear, 8),
        (RecipeModelName::Timemixer, 4),
        (RecipeModelName::Patchtst, 4),
        (RecipeModelName::Duet, 4),
    ]
    .into_iter()
    .flat_map(|(model, paired_draws)| {
        let source = RecipeBlueprint::new(RecipeFamilyName::LgSsmRegimeSwitch)
            .model(model)
            .loader(RecipeLoaderName::P70_20_10NoneBs95)
            .plan(RecipePlanName::TitrationH64PrimaryDoses)
            .trajectory_draws(paired_draws)
            .noise_draws(paired_draws)
            .scale_noise_by_std()
            .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining);
        let clean_selected = RecipeBlueprint::new(RecipeFamilyName::LgSsmRegimeSwitch)
            .model(model)
            .loader(RecipeLoaderName::P70_20_10NoneBs95)
            .plan(RecipePlanName::TitrationH64PrimaryDoses)
            .trajectory_draws(paired_draws)
            .noise_draws(paired_draws)
            .scale_noise_by_std()
            .clean_train_clean_select_test_titration()
            .reuse_noise_matched_clean_validation_best();
        [source, clean_selected]
    })
    .collect()
}

/// SLDS paper expansion under the frozen H64 noise-matched protocol.
///
/// The three worlds are the stationary SLDS, its in-place parameter shift,
/// and the existing trajectory-splice switch. SLDS trajectories are
/// stochastic, so each recipe uses four paired trajectory/observation-noise
/// banks. This runner does not imply that an exact SLDS mixture forecast law
/// has been implemented; metric admissibility still follows each model's
/// submitted forecast representation.
fn paper_expansion_slds_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    [
        RecipeModelName::Dlinear,
        RecipeModelName::Chronos2,
        RecipeModelName::Tirex,
        RecipeModelName::Fern,
        RecipeModelName::Patchtst,
        RecipeModelName::Timemixer,
        RecipeModelName::Duet,
        // CHG (2026-07-26): the capacity axis reaches the expansion worlds.
        RecipeModelName::ChronosBoltSmall,
        RecipeModelName::ChronosBoltBase,
        RecipeModelName::Zeus,
    ]
    .into_iter()
    .map(|model| {
        RecipeBlueprint::new(RecipeFamilyName::Slds)
            .model(model)
            .loader(RecipeLoaderName::P70_20_10NoneBs95)
            .plan(RecipePlanName::TitrationH64RelativeDoses)
            .trajectory_draws(4)
            .noise_draws(4)
            .scale_noise_by_std()
            .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining)
    })
    .collect()
}

/// Double Well base-versus-hard-shift paper expansion.
///
/// `doublewell_barrier_collapse` changes only the latent potential parameter
/// `a: 1.5 -> 0.5` at 35%, while keeping the process-noise scale at 0.25.
/// Four paired stochastic trajectory/noise banks are therefore needed. The
/// seven downstream observation-noise doses remain a separate controlled
/// measurement axis.
fn paper_expansion_doublewell_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    [
        RecipeModelName::Dlinear,
        RecipeModelName::Chronos2,
        RecipeModelName::Tirex,
        // Keep the paper Fern identity explicit. The current research
        // implementation and the submitted ICML reconstruction produced
        // materially different Rössler results, so `fern` is not an
        // acceptable shorthand in a paper comparison matrix.
        RecipeModelName::FernIcmlPinned,
        RecipeModelName::Patchtst,
        RecipeModelName::Timemixer,
        RecipeModelName::Duet,
        // CHG (2026-07-26): the capacity axis reaches the expansion worlds.
        RecipeModelName::ChronosBoltSmall,
        RecipeModelName::ChronosBoltBase,
        RecipeModelName::Zeus,
    ]
    .into_iter()
    .map(|model| {
        RecipeBlueprint::new(RecipeFamilyName::DoublewellBarrierCollapse)
            .model(model)
            .loader(RecipeLoaderName::P70_20_10NoneBs95)
            .plan(RecipePlanName::TitrationH64RelativeDoses)
            .trajectory_draws(4)
            .noise_draws(4)
            .scale_noise_by_std()
            .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining)
    })
    .collect()
}

/// Raw-scale HuGaDB accel/EMG expansion under the paper H64 protocol.
///
/// HuGaDB contributes one fixed observed reference path, not four stochastic
/// trajectory seeds. Four frozen observation-noise draws quantify degradation
/// uncertainty at each positive relative dose.
fn paper_hugadb_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    [
        RecipeModelName::Dlinear,
        RecipeModelName::Chronos2,
        RecipeModelName::Tirex,
        RecipeModelName::Fern,
        RecipeModelName::Patchtst,
        RecipeModelName::Timemixer,
        RecipeModelName::Duet,
    ]
    .into_iter()
    .map(|model| {
        RecipeBlueprint::new(RecipeFamilyName::HugadbAccelEmg)
            .model(model)
            .loader(RecipeLoaderName::P70_20_10NoneBs95)
            .plan(RecipePlanName::TitrationH64RelativeDoses)
            .trajectory_draws(1)
            .noise_draws(4)
            .scale_noise_by_std()
            .single_clean_anchor()
            .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining)
    })
    .collect()
}

/// Test-noise-only companion for the stronger LG-SSM regime switch.
fn paper_lgssm_regime_test_corruption_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    [
        RecipeModelName::Dlinear,
        RecipeModelName::Chronos2,
        RecipeModelName::Tirex,
        RecipeModelName::FernIcmlPinned,
        RecipeModelName::Patchtst,
        RecipeModelName::Timemixer,
        RecipeModelName::Duet,
    ]
    .into_iter()
    .map(|model| {
        let recipe = RecipeBlueprint::new(RecipeFamilyName::LgSsmRegimeSwitch)
            .model(model)
            .loader(RecipeLoaderName::P70_20_10NoneBs95)
            .plan(RecipePlanName::TitrationH64RelativeDoses)
            .trajectory_draws(4)
            .noise_draws(4)
            .scale_noise_by_std()
            .clean_train_clean_select_test_titration();
        if matches!(model, RecipeModelName::Chronos2 | RecipeModelName::Tirex) {
            recipe
        } else {
            recipe.reuse_noise_matched_clean_validation_best()
        }
    })
    .collect()
}

/// Raw-scale HuGaDB test-noise-only companion.
///
/// Trained models source their clean checkpoint from `paper-hugadb-h64`.
/// Foundation models have no fitted checkpoint and run direct inference.
fn paper_hugadb_test_corruption_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    [
        RecipeModelName::Dlinear,
        RecipeModelName::Chronos2,
        RecipeModelName::Tirex,
        RecipeModelName::Fern,
        RecipeModelName::Patchtst,
        RecipeModelName::Timemixer,
        RecipeModelName::Duet,
    ]
    .into_iter()
    .map(|model| {
        let recipe = RecipeBlueprint::new(RecipeFamilyName::HugadbAccelEmg)
            .model(model)
            .loader(RecipeLoaderName::P70_20_10NoneBs95)
            .plan(RecipePlanName::TitrationH64RelativeDoses)
            .trajectory_draws(1)
            .noise_draws(4)
            .scale_noise_by_std()
            .single_clean_anchor()
            .clean_train_clean_select_test_titration();
        if matches!(model, RecipeModelName::Chronos2 | RecipeModelName::Tirex) {
            recipe
        } else {
            recipe.reuse_noise_matched_clean_validation_best()
        }
    })
    .collect()
}

/// Clean-checkpoint H64 test-corruption matrix for the main experiment.
///
/// These are the original clean-train/clean-select/test-titration recipes.
/// DLinear reuses the matching `paper-core-h64` dose-zero
/// validation-best checkpoint, while foundation models have no fitting stage.
/// This is a low-cost main evaluation track alongside, but never a substitute
/// for, noise-matched retraining.
fn paper_test_corruption_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for family in [
        RecipeFamilyName::LgSsmBase,
        RecipeFamilyName::LgSsmSwitch,
        RecipeFamilyName::RosslerBase,
        RecipeFamilyName::RosslerParamShock,
    ] {
        for model in [
            RecipeModelName::Dlinear,
            RecipeModelName::Chronos2,
            RecipeModelName::Tirex,
        ] {
            let trajectory_draws = if matches!(
                family,
                RecipeFamilyName::RosslerBase | RecipeFamilyName::RosslerParamShock
            ) {
                1
            } else {
                4
            };
            let recipe = RecipeBlueprint::new(family)
                .model(model)
                .plan(RecipePlanName::TitrationH64RelativeDoses)
                .trajectory_draws(trajectory_draws)
                .noise_draws(4)
                .scale_noise_by_std()
                .clean_train_clean_select_test_titration();
            recipes.push(if model == RecipeModelName::Dlinear {
                recipe.reuse_noise_matched_clean_validation_best()
            } else {
                recipe
            });
        }
    }
    recipes
}

/// Student-t3 clean-checkpoint test-corruption lane.
///
/// Foundation models have no fitting stage, so their test-corruption track is
/// pure per-dose inference. DLinear reuses the law-neutral clean checkpoint
/// from the Gaussian noise-matched core: dose zero injects nothing, so
/// retraining it once per noise law or split-treatment label would break the
/// shared-anchor comparison. Run this lane in the same DB as
/// `paper-core-h64` so the source resolves.
fn paper_test_corruption_t3_recipe_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = Vec::new();
    for family in [
        RecipeFamilyName::LgSsmBase,
        RecipeFamilyName::LgSsmSwitch,
        RecipeFamilyName::RosslerBase,
    ] {
        for model in [
            RecipeModelName::Dlinear,
            RecipeModelName::Chronos2,
            RecipeModelName::Tirex,
        ] {
            let trajectory_draws = if family == RecipeFamilyName::RosslerBase { 1 } else { 4 };
            let recipe = RecipeBlueprint::new(family)
                .model(model)
                .plan(RecipePlanName::TitrationH64RelativeDoses)
                .trajectory_draws(trajectory_draws)
                .noise_draws(4)
                .scale_noise_by_std()
                .noise_kind(fern_experiment_types::NoiseKind::StudentT3)
                .clean_train_clean_select_test_titration();
            recipes.push(if model == RecipeModelName::Dlinear {
                recipe.reuse_noise_matched_clean_validation_best()
            } else {
                recipe
            });
        }
    }
    recipes
}

/// EnbPI v1 canary (mini-program decision, author-approved 2026-08-14):
/// ONE world (LGSSM base), ONE trained member model (DLinear), clean plan,
/// B=15 members, block length 48 (the frozen Python StageBlockBootstrap
/// default — formula-exact with the retired implementation), mask seeds
/// resample_seed_base + member index. The model seed stays the recipe's
/// [42] for every member: the retired route coupled model seed and mask
/// seed as 42+b, and this DELIBERATE decoupling (members differ by
/// resample only) is flagged in the patch for the author. No online
/// updates, no multi-model ensembles — extensions are separate recipes.
fn enbpi_canary_lgssm_dlinear_h64_recipe_builds() -> Vec<RecipeBlueprint> {
    vec![
        RecipeBlueprint::new(RecipeFamilyName::LgSsmBase)
            .model(RecipeModelName::Dlinear)
            .bootstrap(RecipeBootstrapSpec {
                ensemble_size: 15,
                block_size: 48,
                resample_seed_base: 7000,
            }),
    ]
}

pub fn recipe_catalog_builds() -> Vec<RecipeBlueprint> {
    let mut recipes = enbpi_canary_lgssm_dlinear_h64_recipe_builds();
    recipes.extend(vec![
        RecipeBlueprint::new(RecipeFamilyName::OuBase)
            .plan(RecipePlanName::TitrationH64H192Sigmas)
            .trajectory_draws(3)
            .noise_draws(3),
        RecipeBlueprint::new(RecipeFamilyName::DoublewellBase)
            .plan(RecipePlanName::TitrationH64H192Sigmas)
            .trajectory_draws(3)
            .noise_draws(3),
        RecipeBlueprint::new(RecipeFamilyName::RosslerBase)
            .plan(RecipePlanName::TitrationH64H192Sigmas)
            .noise_draws(3),
        RecipeBlueprint::new(RecipeFamilyName::OuBase)
            .model(RecipeModelName::Tirex)
            .plan(RecipePlanName::TitrationH64H192Sigmas)
            .trajectory_draws(3)
            .noise_draws(3),
        RecipeBlueprint::new(RecipeFamilyName::DoublewellBase)
            .model(RecipeModelName::Tirex)
            .plan(RecipePlanName::TitrationH64H192Sigmas)
            .trajectory_draws(3)
            .noise_draws(3),
        RecipeBlueprint::new(RecipeFamilyName::RosslerBase)
            .model(RecipeModelName::Tirex)
            .plan(RecipePlanName::TitrationH64H192Sigmas)
            .noise_draws(3),
        RecipeBlueprint::new(RecipeFamilyName::HugadbAccelEmg)
            .loader(RecipeLoaderName::P70_20_10StandardBs95)
            .plan(RecipePlanName::TitrationH64Sigmas)
            .run_whiten()
            .scale_noise_by_std(),
        RecipeBlueprint::new(RecipeFamilyName::HugadbSwitch)
            .eval(RecipeEvalName::HuberEvalCrpsE32NoSwd)
            .loader(RecipeLoaderName::P70_20_10StandardBs95)
            .plan(RecipePlanName::TitrationH64Sigmas)
            .run_whiten()
            .scale_noise_by_std(),
        RecipeBlueprint::new(RecipeFamilyName::HugadbSwitch)
            .eval(RecipeEvalName::FernNllS16E32NoSwd)
            .loader(RecipeLoaderName::P70_20_10StandardBs24)
            .plan(RecipePlanName::TitrationH64Sigmas)
            .run_whiten()
            .scale_noise_by_std(),
        RecipeBlueprint::new(RecipeFamilyName::Ou).plan(RecipePlanName::CleanH64H192),
        RecipeBlueprint::new(RecipeFamilyName::OuBase)
            .model(RecipeModelName::ChronosBoltSmall)
            .plan(RecipePlanName::CleanH64),
        RecipeBlueprint::new(RecipeFamilyName::Ou).eval(RecipeEvalName::HuberEvalCrps),
        RecipeBlueprint::new(RecipeFamilyName::Ou).eval(RecipeEvalName::HuberCrps),
        RecipeBlueprint::new(RecipeFamilyName::Lorenz96)
            .eval(RecipeEvalName::CrpsS4E48)
            .plan(RecipePlanName::CleanH192)
            .seeds(&[1955, 7, 20]),
        RecipeBlueprint::new(RecipeFamilyName::IcmlMain)
            .eval(RecipeEvalName::HuberProj500)
            .loader(RecipeLoaderName::P70_10_20NoneBs128)
            .train(RecipeTrainName::IcmlMain)
            .plan(RecipePlanName::CleanH96H192H336H720)
            .seeds(&[7, 1955, 2023, 4]),
        RecipeBlueprint::new(RecipeFamilyName::IcmlMainEtth1S336H96)
            .eval(RecipeEvalName::HuberProj500)
            .loader(RecipeLoaderName::P70_10_20NoneBs128)
            .train(RecipeTrainName::IcmlMain)
            .plan(RecipePlanName::CleanH96)
            .seeds(&[7]),
        RecipeBlueprint::new(RecipeFamilyName::IcmlMainEtth1S336H336)
            .eval(RecipeEvalName::HuberProj500)
            .loader(RecipeLoaderName::P70_10_20NoneBs128)
            .train(RecipeTrainName::IcmlMain)
            .plan(RecipePlanName::CleanH336)
            .seeds(&[7]),
        RecipeBlueprint::new(RecipeFamilyName::IcmlMainLorenzS336H336)
            .eval(RecipeEvalName::HuberProj500)
            .loader(RecipeLoaderName::P70_10_20NoneBs128)
            .train(RecipeTrainName::IcmlMain)
            .plan(RecipePlanName::CleanH336)
            .seeds(&[7]),
        RecipeBlueprint::new(RecipeFamilyName::IcmlMainHugadbGeneric)
            .eval(RecipeEvalName::HuberProj500)
            .loader(RecipeLoaderName::P70_10_20RobustMadBs128)
            .train(RecipeTrainName::IcmlMain)
            .plan(RecipePlanName::CleanH96H192H336H720)
            .seeds(&[7, 1955, 2023, 4]),
        RecipeBlueprint::new(RecipeFamilyName::IcmlMainHugadbGeneric)
            .eval(RecipeEvalName::HuberProj500)
            .loader(RecipeLoaderName::P70_10_20RobustMadBs128)
            .train(RecipeTrainName::IcmlMain)
            .plan(RecipePlanName::CleanH336)
            .seeds(&[7]),
        RecipeBlueprint::new(RecipeFamilyName::IcmlMainHugadbGeneric)
            .model(RecipeModelName::Timemixer)
            .eval(RecipeEvalName::HuberProj500)
            .loader(RecipeLoaderName::P70_10_20RobustMadBs128)
            .train(RecipeTrainName::IcmlMain)
            .plan(RecipePlanName::CleanH96H192H336H720)
            .seeds(&[7, 1955, 2023, 4]),
        RecipeBlueprint::new(RecipeFamilyName::IcmlMainHugadbGeneric)
            .model(RecipeModelName::Timemixer)
            .eval(RecipeEvalName::HuberProj500)
            .loader(RecipeLoaderName::P70_10_20RobustMadBs128)
            .train(RecipeTrainName::IcmlMain)
            .plan(RecipePlanName::CleanH336)
            .seeds(&[7]),
        RecipeBlueprint::new(RecipeFamilyName::IcmlMainHugadbSplice)
            .eval(RecipeEvalName::HuberProj500)
            .loader(RecipeLoaderName::P70_10_20RobustMadBs128)
            .train(RecipeTrainName::IcmlMain)
            .plan(RecipePlanName::CleanH96H192H336H720)
            .seeds(&[7, 1955, 2023, 4]),
        RecipeBlueprint::new(RecipeFamilyName::IcmlShock8)
            .loader(RecipeLoaderName::P70_20_10NoneBs95)
            .train(RecipeTrainName::IcmlShock)
            .plan(RecipePlanName::CleanH336)
            .seeds(&[7, 1955]),
    ]);

    recipes.extend(paper_csr_ou_retraining_smoke_recipe_builds());
    recipes.extend(paper_csr_ou_recipe_builds());
    recipes.extend(paper_core_h64_recipe_builds());
    recipes.extend(paper_canary_rossler_param_shift_fern_icml_h64_recipe_builds());
    recipes.extend(paper_canary_rossler_shift_fern_factorial_h64_recipe_builds());
    recipes.extend(paper_extended_t3_h64_recipe_builds());
    recipes.extend(paper_extended_contaminated_h64_recipe_builds());
    recipes.extend(paper_extended_laplace_h64_recipe_builds());
    recipes.extend(paper_real_sensor_h64_recipe_builds());
    recipes.extend(paper_real_sensor_trained_h64_recipe_builds());
    recipes.extend(paper_correlated_noise_h64_recipe_builds());
    recipes.extend(paper_core_h64_banks8_recipe_builds());
    recipes.extend(paper_confirm_rossler_initial_conditions_h64_recipe_builds());
    recipes.extend(paper_confirm_rossler_random_initial_conditions_h64_recipe_builds());
    recipes.extend(paper_chaotic_lorenz_h64_recipe_builds());
    recipes.extend(paper_chaotic_lorenz_trained_h64_recipe_builds());
    recipes.extend(paper_lorenz_initial_conditions_h64_recipe_builds());
    recipes.extend(paper_lorenz96_titration_h64_recipe_builds());
    recipes.extend(paper_expansion_duet_h64_recipe_builds());
    recipes.extend(paper_expansion_doublewell_h64_recipe_builds());
    recipes.extend(paper_expansion_fern_icml_pinned_h64_recipe_builds());
    recipes.extend(paper_expansion_fern_patchtst_h64_recipe_builds());
    recipes.push(radial_nll_calibration_h64_recipe_build(
        RecipeFamilyName::LgSsmBase,
        RecipeModelName::Fern,
    ));
    recipes.push(radial_nll_calibration_h64_recipe_build(
        RecipeFamilyName::RosslerBase,
        RecipeModelName::Fern,
    ));
    recipes.push(radial_nll_calibration_h64_recipe_build(
        RecipeFamilyName::LgSsmBase,
        RecipeModelName::FernWideScale,
    ));
    recipes.push(
        radial_nll_calibration_h64_recipe_build(RecipeFamilyName::LgSsmBase, RecipeModelName::Fern).fern_radial_head(),
    );
    recipes.push(
        radial_nll_calibration_h64_recipe_build(RecipeFamilyName::RosslerBase, RecipeModelName::Fern)
            .fern_radial_head(),
    );
    for source_std in RADIAL_NLL_SOURCE_STD_SWEEP {
        recipes.push(
            radial_nll_calibration_h64_recipe_build(RecipeFamilyName::LgSsmBase, RecipeModelName::Fern)
                .source_std(source_std),
        );
    }
    recipes.extend(paper_expansion_lgssm_regime_h64_recipe_builds());
    recipes.extend(paper_expansion_rossler_param_shift_h64_recipe_builds());
    recipes.extend(
        paper_rossler_dual_dial_h64_recipe_builds()
            .into_iter()
            .filter(|recipe| {
                // `paper-test-corruption-h64` is the catalog owner for these
                // two DLinear clean-selected recipes. The dual-dial preset
                // references the exact same scientific recipes so it stays
                // self-contained, but registering them twice would make
                // name-based `--recipe` selection ambiguous.
                !(recipe.model == RecipeModelName::Dlinear
                    && recipe.split_treatment_scheme == Some(SplitTreatmentScheme::CleanTrainCleanSelectTestTitration)
                    && recipe.checkpoint_reuse_policy == Some(CheckpointReusePolicy::NoiseMatchedCleanValidationBest))
            }),
    );
    recipes.extend(paper_solar_h64_recipe_builds());
    recipes.extend(paper_solar_subset_h64_recipe_builds());
    recipes.extend(paper_expansion_ou_seasonal_h64_recipe_builds());
    recipes.extend(paper_expansion_slds_h64_recipe_builds());
    recipes.extend(paper_expansion_timemixer_h64_recipe_builds());
    recipes.extend(paper_hugadb_h64_recipe_builds());
    recipes.extend(paper_hugadb_test_corruption_h64_recipe_builds());
    recipes.extend(paper_lgssm_regime_primary_h64_recipe_builds());
    recipes.extend(paper_lgssm_regime_test_corruption_h64_recipe_builds());
    recipes.extend(paper_test_corruption_h64_recipe_builds());
    recipes.extend(paper_test_corruption_t3_recipe_builds());

    for model in [
        RecipeModelName::Dlinear,
        RecipeModelName::Timemixer,
        RecipeModelName::Patchtst,
    ] {
        for plan in [RecipePlanName::TitrationH64Sigmas, RecipePlanName::TitrationH192Sigmas] {
            recipes.push(
                RecipeBlueprint::new(RecipeFamilyName::HugadbAccelEmg)
                    .model(model)
                    .loader(RecipeLoaderName::P70_20_10StandardBs95)
                    .plan(plan)
                    .scale_noise_by_std(),
            );
        }
    }

    for model in [RecipeModelName::Dlinear, RecipeModelName::Timemixer] {
        recipes.push(
            RecipeBlueprint::new(RecipeFamilyName::HugadbSwitch)
                .model(model)
                .loader(RecipeLoaderName::P70_20_10StandardBs95)
                .plan(RecipePlanName::TitrationH64Sigmas)
                .scale_noise_by_std(),
        );
    }

    for family in [
        RecipeFamilyName::Lorenz,
        RecipeFamilyName::Rossler,
        RecipeFamilyName::Lorenz96,
        RecipeFamilyName::Chua,
        RecipeFamilyName::Ou,
        RecipeFamilyName::Slds,
        RecipeFamilyName::Doublewell,
        RecipeFamilyName::SeasonalAr,
    ] {
        recipes.push(RecipeBlueprint::new(family).eval(RecipeEvalName::NllS16E32));
        recipes.push(
            RecipeBlueprint::new(family)
                .eval(RecipeEvalName::FernNllS4E32)
                .plan(RecipePlanName::CleanH64H192)
                .seeds(&[1955, 7]),
        );
    }

    recipes.sort_by_key(RecipeBlueprint::name);
    recipes
}

pub fn preset_catalog_builds() -> Vec<RecipePreset> {
    let mut presets = vec![
        RecipePreset {
            name: "enbpi-canary-lgssm-dlinear-h64",
            label: "EnbPI v1 canary: LGSSM base, DLinear, 15 block-bootstrap members (block 48), clean H64",
            recipes: enbpi_canary_lgssm_dlinear_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-canary-rossler-param-shift-fern-icml-h64",
            label: "Paper H64 Fern ICML paper-bounds reconstruction canary on original Rössler parameter shift",
            recipes: paper_canary_rossler_param_shift_fern_icml_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-canary-rossler-shift-fern-factorial-h64",
            label: "Paper H64 Fern 2x2x2 architecture canary on the original Rössler parameter shift",
            recipes: paper_canary_rossler_shift_fern_factorial_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-core-h64",
            label: "Paper H64 noise-matched seven-dose core matrix",
            recipes: paper_core_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-chaotic-lorenz-h64",
            label: "Paper H64 Lorenz-63 chaotic replication lane (stationary + parameter shift)",
            recipes: paper_chaotic_lorenz_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-lorenz96-titration-h64",
            label: "Paper H64 Lorenz-96 forty-channel titration (channel-dominance control)",
            recipes: paper_lorenz96_titration_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-lorenz-initial-conditions-h64",
            label: "Paper H64 Lorenz-63 titration over sixteen independent initial conditions",
            recipes: paper_lorenz_initial_conditions_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-chaotic-lorenz-trained-h64",
            label: "Paper H64 Lorenz-63 trained-model completion (TimeMixer, DUET, PatchTST, Zeus)",
            recipes: paper_chaotic_lorenz_trained_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-core-h64-banks8",
            label: "Paper H64 core matrix over eight frozen banks (interval coverage, not new science)",
            recipes: paper_core_h64_banks8_recipe_builds(),
        },
        RecipePreset {
            name: "paper-correlated-noise-h64",
            label: "Paper H64 correlated-noise lane (gaussian marginal, AR(1) phi 0.5 and 0.9)",
            recipes: paper_correlated_noise_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-confirm-rossler-initial-conditions-h64",
            label: "Paper H64 Rössler DLinear--TiREx confirmation over eight untouched initial conditions",
            recipes: paper_confirm_rossler_initial_conditions_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-confirm-rossler-random-initial-conditions-h64",
            label: "Paper H64 Rössler DLinear--TiREx confirmation over sixteen drawn initial conditions",
            recipes: paper_confirm_rossler_random_initial_conditions_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-real-sensor-h64",
            label: "Paper H64 recorded-sensor noise-dial lane (HuGaDB stationary + natural walk/run change)",
            recipes: paper_real_sensor_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-real-sensor-trained-h64",
            label: "Paper H64 trained-model HuGaDB matched walking control and natural walk/run change",
            recipes: paper_real_sensor_trained_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-solar-h64",
            label: "Paper H64 recorded solar-power noise-dial lane (a recorded world that is not a body)",
            recipes: paper_solar_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-solar-subset-h64",
            label: "Paper H64 solar 16-channel subset, zero-shot models only (evenly spaced plants, full solar priced out)",
            recipes: paper_solar_subset_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-expansion-ou-seasonal-h64",
            label: "Paper H64 OU and seasonal-AR expansion (the two simple worlds never run)",
            recipes: paper_expansion_ou_seasonal_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-extended-laplace-h64",
            label: "Paper H64 variance-matched Laplace noise-matched extended lane (tail-ladder middle rung)",
            recipes: paper_extended_laplace_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-extended-t3-h64",
            label: "Paper H64 variance-matched Student-t3 noise-matched extended lane",
            recipes: paper_extended_t3_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-extended-contaminated-h64",
            label: "Paper H64 variance-matched sparse-contamination extended lane",
            recipes: paper_extended_contaminated_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-csr-ou",
            label: "Paper OU H64 clean-selection-regret test-only titration",
            recipes: paper_csr_ou_recipe_builds(),
        },
        RecipePreset {
            name: "paper-expansion-duet-h64",
            label: "Paper H64 DUET noise-matched expansion lane",
            recipes: paper_expansion_duet_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-expansion-doublewell-h64",
            label: "Paper H64 Double Well barrier-collapse noise-matched expansion",
            recipes: paper_expansion_doublewell_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-expansion-fern-patchtst-h64",
            label: "Paper H64 Fern and PatchTST noise-matched expansion lane",
            recipes: paper_expansion_fern_patchtst_h64_recipe_builds(),
        },
        RecipePreset {
            name: "radial-nll-calibration-lgssm-h64",
            label: "H64 Fern under a proper score on LGSSM, whose forecast halves do not share a residual magnitude",
            recipes: vec![radial_nll_calibration_h64_recipe_build(
                RecipeFamilyName::LgSsmBase,
                RecipeModelName::Fern,
            )],
        },
        RecipePreset {
            name: "radial-nll-calibration-rossler-h64",
            label: "H64 Fern under a proper score on Rossler, whose forecast halves do share a residual magnitude",
            recipes: vec![radial_nll_calibration_h64_recipe_build(
                RecipeFamilyName::RosslerBase,
                RecipeModelName::Fern,
            )],
        },
        RecipePreset {
            name: "radial-magnitude-head-lgssm-h64",
            label: "H64 Fern on LGSSM with one emitted magnitude per patch block, paired against radial-nll-calibration-lgssm-h64",
            recipes: vec![
                radial_nll_calibration_h64_recipe_build(RecipeFamilyName::LgSsmBase, RecipeModelName::Fern)
                    .fern_radial_head(),
            ],
        },
        RecipePreset {
            name: "radial-magnitude-head-rossler-h64",
            label: "H64 Fern on Rossler with one emitted magnitude per patch block, paired against radial-nll-calibration-rossler-h64",
            recipes: vec![
                radial_nll_calibration_h64_recipe_build(RecipeFamilyName::RosslerBase, RecipeModelName::Fern)
                    .fern_radial_head(),
            ],
        },
        RecipePreset {
            name: "radial-nll-source-std-lgssm-h64",
            label: "H64 Fern on LGSSM across base cloud widths 1 and 10, bracketing the built-in 0.1 that radial-nll-calibration-lgssm-h64 runs",
            recipes: RADIAL_NLL_SOURCE_STD_SWEEP
                .iter()
                .map(|source_std| {
                    radial_nll_calibration_h64_recipe_build(RecipeFamilyName::LgSsmBase, RecipeModelName::Fern)
                        .source_std(*source_std)
                })
                .collect(),
        },
        RecipePreset {
            name: "radial-nll-scale-ceiling-lgssm-h64",
            label: "H64 Fern on LGSSM with the ceiling on emitted uncertainty raised, paired against radial-nll-calibration-lgssm-h64",
            recipes: vec![radial_nll_calibration_h64_recipe_build(
                RecipeFamilyName::LgSsmBase,
                RecipeModelName::FernWideScale,
            )],
        },
        RecipePreset {
            name: "paper-expansion-fern-icml-pinned-h64",
            label: "Paper H64 Fern ICML paper-bounds reconstruction noise-matched expansion lane",
            recipes: paper_expansion_fern_icml_pinned_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-expansion-lgssm-regime-h64",
            label: "Paper H64 stronger LGSSM multi-axis regime-switch expansion",
            recipes: paper_expansion_lgssm_regime_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-expansion-rossler-param-shift-h64",
            label: "Paper H64 original Rössler parameter-shift noise-matched expansion",
            recipes: paper_expansion_rossler_param_shift_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-rossler-dual-dial-h64",
            label: "Paper H64 Rössler Base/Param-Shift three-view dual-dial evaluation",
            recipes: paper_rossler_dual_dial_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-expansion-slds-h64",
            label: "Paper H64 SLDS noise-matched expansion lane",
            recipes: paper_expansion_slds_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-expansion-timemixer-h64",
            label: "Paper H64 TimeMixer noise-matched expansion lane",
            recipes: paper_expansion_timemixer_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-hugadb-h64",
            label: "Paper H64 raw-scale HuGaDB noise-matched expansion",
            recipes: paper_hugadb_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-hugadb-test-corruption-h64",
            label: "Paper H64 raw-scale HuGaDB clean-checkpoint test-corruption expansion",
            recipes: paper_hugadb_test_corruption_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-lgssm-regime-test-corruption-h64",
            label: "Paper H64 stronger LGSSM clean-checkpoint test-corruption expansion",
            recipes: paper_lgssm_regime_test_corruption_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-lgssm-regime-primary-source-h64",
            label: "Paper H64 stronger LGSSM four-dose fitted-model source",
            recipes: paper_lgssm_regime_primary_h64_recipe_builds()
                .into_iter()
                .filter(|recipe| recipe.split_treatment_scheme == Some(SplitTreatmentScheme::NoiseMatchedRetraining))
                .collect(),
        },
        RecipePreset {
            name: "paper-lgssm-regime-primary-test-corruption-h64",
            label: "Paper H64 stronger LGSSM four-dose fitted-model clean-selected evaluation",
            recipes: paper_lgssm_regime_primary_h64_recipe_builds()
                .into_iter()
                .filter(|recipe| {
                    recipe.split_treatment_scheme == Some(SplitTreatmentScheme::CleanTrainCleanSelectTestTitration)
                })
                .collect(),
        },
        RecipePreset {
            name: "paper-test-corruption-h64",
            label: "Paper H64 clean-checkpoint test-corruption track",
            recipes: paper_test_corruption_h64_recipe_builds(),
        },
        RecipePreset {
            name: "paper-test-corruption-t3-h64",
            label: "Paper H64 Student-t3 test-corruption track (foundation models; inference only)",
            recipes: paper_test_corruption_t3_recipe_builds(),
        },
        RecipePreset {
            name: "icml-main",
            label: "ICML main rerun table",
            recipes: vec![
                RecipeBlueprint::new(RecipeFamilyName::IcmlMain)
                    .eval(RecipeEvalName::HuberProj500)
                    .loader(RecipeLoaderName::P70_10_20NoneBs128)
                    .train(RecipeTrainName::IcmlMain)
                    .plan(RecipePlanName::CleanH96H192H336H720)
                    .seeds(&[7, 1955, 2023, 4]),
            ],
        },
        RecipePreset {
            name: "icml-hugadb-generic",
            label: "ICML HuGaDB accel/emg generic table",
            recipes: vec![
                RecipeBlueprint::new(RecipeFamilyName::IcmlMainHugadbGeneric)
                    .eval(RecipeEvalName::HuberProj500)
                    .loader(RecipeLoaderName::P70_10_20RobustMadBs128)
                    .train(RecipeTrainName::IcmlMain)
                    .plan(RecipePlanName::CleanH96H192H336H720)
                    .seeds(&[7, 1955, 2023, 4]),
                RecipeBlueprint::new(RecipeFamilyName::IcmlMainHugadbGeneric)
                    .model(RecipeModelName::Timemixer)
                    .eval(RecipeEvalName::HuberProj500)
                    .loader(RecipeLoaderName::P70_10_20RobustMadBs128)
                    .train(RecipeTrainName::IcmlMain)
                    .plan(RecipePlanName::CleanH96H192H336H720)
                    .seeds(&[7, 1955, 2023, 4]),
            ],
        },
        RecipePreset {
            name: "icml-hugadb-splice",
            label: "ICML HuGaDB walking-to-bicycling splice table",
            recipes: vec![
                RecipeBlueprint::new(RecipeFamilyName::IcmlMainHugadbSplice)
                    .eval(RecipeEvalName::HuberProj500)
                    .loader(RecipeLoaderName::P70_10_20RobustMadBs128)
                    .train(RecipeTrainName::IcmlMain)
                    .plan(RecipePlanName::CleanH96H192H336H720)
                    .seeds(&[7, 1955, 2023, 4]),
            ],
        },
        RecipePreset {
            name: "icml-shock-8",
            label: "ICML shock table synthetic families",
            recipes: vec![
                RecipeBlueprint::new(RecipeFamilyName::IcmlShock8)
                    .loader(RecipeLoaderName::P70_20_10NoneBs95)
                    .train(RecipeTrainName::IcmlShock)
                    .plan(RecipePlanName::CleanH336)
                    .seeds(&[7, 1955]),
            ],
        },
        RecipePreset {
            name: "hugadb-accel",
            label: "HuGaDB accel/emg H64 titration",
            recipes: vec![
                RecipeBlueprint::new(RecipeFamilyName::HugadbAccelEmg)
                    .loader(RecipeLoaderName::P70_20_10StandardBs95)
                    .plan(RecipePlanName::TitrationH64Sigmas)
                    .run_whiten()
                    .scale_noise_by_std(),
                RecipeBlueprint::new(RecipeFamilyName::HugadbAccelEmg)
                    .model(RecipeModelName::Dlinear)
                    .loader(RecipeLoaderName::P70_20_10StandardBs95)
                    .plan(RecipePlanName::TitrationH64Sigmas)
                    .scale_noise_by_std(),
                RecipeBlueprint::new(RecipeFamilyName::HugadbAccelEmg)
                    .model(RecipeModelName::Patchtst)
                    .loader(RecipeLoaderName::P70_20_10StandardBs95)
                    .plan(RecipePlanName::TitrationH64Sigmas)
                    .scale_noise_by_std(),
                RecipeBlueprint::new(RecipeFamilyName::HugadbAccelEmg)
                    .model(RecipeModelName::Timemixer)
                    .loader(RecipeLoaderName::P70_20_10StandardBs95)
                    .plan(RecipePlanName::TitrationH64Sigmas)
                    .scale_noise_by_std(),
            ],
        },
        RecipePreset {
            name: "hugadb-switch",
            label: "HuGaDB walking-to-bicycling H64 huber baselines",
            recipes: vec![
                RecipeBlueprint::new(RecipeFamilyName::HugadbSwitch)
                    .model(RecipeModelName::Dlinear)
                    .loader(RecipeLoaderName::P70_20_10StandardBs95)
                    .plan(RecipePlanName::TitrationH64Sigmas)
                    .scale_noise_by_std(),
                RecipeBlueprint::new(RecipeFamilyName::HugadbSwitch)
                    .model(RecipeModelName::Timemixer)
                    .loader(RecipeLoaderName::P70_20_10StandardBs95)
                    .plan(RecipePlanName::TitrationH64Sigmas)
                    .scale_noise_by_std(),
            ],
        },
        RecipePreset {
            name: "hugadb-switch-crps",
            label: "HuGaDB walking-to-bicycling H64 Fern CRPS eval",
            recipes: vec![
                RecipeBlueprint::new(RecipeFamilyName::HugadbSwitch)
                    .eval(RecipeEvalName::HuberEvalCrpsE32NoSwd)
                    .loader(RecipeLoaderName::P70_20_10StandardBs95)
                    .plan(RecipePlanName::TitrationH64Sigmas)
                    .run_whiten()
                    .scale_noise_by_std(),
            ],
        },
        RecipePreset {
            name: "hugadb-switch-nll",
            label: "HuGaDB walking-to-bicycling H64 Fern NLL eval",
            recipes: vec![
                RecipeBlueprint::new(RecipeFamilyName::HugadbSwitch)
                    .eval(RecipeEvalName::FernNllS16E32NoSwd)
                    .loader(RecipeLoaderName::P70_20_10StandardBs24)
                    .plan(RecipePlanName::TitrationH64Sigmas)
                    .run_whiten()
                    .scale_noise_by_std(),
            ],
        },
    ];
    presets.sort_by_key(|preset| preset.name);
    presets
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn finite_density_recipe_spec_refuses_noncanonical_combinations() {
        assert!(RecipeFernDensitySpec::new(FernDensityVariantV1::SingleGaussian, 1, None,).is_err());
        assert!(RecipeFernDensitySpec::new(FernDensityVariantV1::BoundedMdn, 1, None).is_err());
        assert!(RecipeFernDensitySpec::new(FernDensityVariantV1::SparseMoe, 4, Some(4)).is_err());

        let sparse = RecipeFernDensitySpec::new(FernDensityVariantV1::SparseMoe, 4, Some(2)).unwrap();
        assert!(
            RecipeBlueprint::new(RecipeFamilyName::LgSsmBase)
                .fern_density(sparse)
                .name()
                .ends_with("_density_sparse_moe_k4_r2")
        );
    }

    #[test]
    fn recipe_name_matches_python_formula() {
        let recipe = RecipeBlueprint::new(RecipeFamilyName::IcmlShock8)
            .loader(RecipeLoaderName::P70_20_10NoneBs95)
            .train(RecipeTrainName::IcmlShock)
            .plan(RecipePlanName::CleanH336)
            .seeds(&[7, 1955]);
        assert_eq!(
            recipe.name(),
            "icml_shock_8_fern_huber_p70_20_10_none_bs95_lr3e4_ep50_pat5_grace3_clean_h336_seeds7_1955"
        );
    }

    #[test]
    fn known_presets_are_present() {
        let names = preset_catalog_builds()
            .into_iter()
            .map(|preset| preset.name)
            .collect::<Vec<_>>();
        assert_eq!(
            names,
            vec![
                "enbpi-canary-lgssm-dlinear-h64",
                "hugadb-accel",
                "hugadb-switch",
                "hugadb-switch-crps",
                "hugadb-switch-nll",
                "icml-hugadb-generic",
                "icml-hugadb-splice",
                "icml-main",
                "icml-shock-8",
                "paper-canary-rossler-param-shift-fern-icml-h64",
                "paper-canary-rossler-shift-fern-factorial-h64",
                "paper-chaotic-lorenz-h64",
                "paper-chaotic-lorenz-trained-h64",
                "paper-confirm-rossler-initial-conditions-h64",
                "paper-confirm-rossler-random-initial-conditions-h64",
                "paper-core-h64",
                "paper-core-h64-banks8",
                "paper-correlated-noise-h64",
                "paper-csr-ou",
                "paper-expansion-doublewell-h64",
                "paper-expansion-duet-h64",
                "paper-expansion-fern-icml-pinned-h64",
                "paper-expansion-fern-patchtst-h64",
                "paper-expansion-lgssm-regime-h64",
                "paper-expansion-ou-seasonal-h64",
                "paper-expansion-rossler-param-shift-h64",
                "paper-expansion-slds-h64",
                "paper-expansion-timemixer-h64",
                "paper-extended-contaminated-h64",
                "paper-extended-laplace-h64",
                "paper-extended-t3-h64",
                "paper-hugadb-h64",
                "paper-hugadb-test-corruption-h64",
                "paper-lgssm-regime-primary-source-h64",
                "paper-lgssm-regime-primary-test-corruption-h64",
                "paper-lgssm-regime-test-corruption-h64",
                "paper-lorenz-initial-conditions-h64",
                "paper-lorenz96-titration-h64",
                "paper-real-sensor-h64",
                "paper-real-sensor-trained-h64",
                "paper-rossler-dual-dial-h64",
                "paper-solar-h64",
                "paper-solar-subset-h64",
                "paper-test-corruption-h64",
                "paper-test-corruption-t3-h64",
                "radial-magnitude-head-lgssm-h64",
                "radial-magnitude-head-rossler-h64",
                "radial-nll-calibration-lgssm-h64",
                "radial-nll-calibration-rossler-h64",
                "radial-nll-scale-ceiling-lgssm-h64",
                "radial-nll-source-std-lgssm-h64",
            ]
        );
    }

    #[test]
    fn preset_model_names_are_unique_and_exact() {
        let presets = preset_catalog_builds();
        let fern_patchtst = presets
            .iter()
            .find(|preset| preset.name == "paper-expansion-fern-patchtst-h64")
            .unwrap();
        assert_eq!(fern_patchtst.model_names(), vec!["fern", "patchtst"]);

        let fern_icml = presets
            .iter()
            .find(|preset| preset.name == "paper-expansion-fern-icml-pinned-h64")
            .unwrap();
        assert_eq!(fern_icml.model_names(), vec!["fern_icml_pinned"]);

        let doublewell = presets
            .iter()
            .find(|preset| preset.name == "paper-expansion-doublewell-h64")
            .unwrap();
        assert!(doublewell.model_names().contains(&"fern_icml_pinned"));
        assert!(!doublewell.model_names().contains(&"fern"));

        let rossler_shift = presets
            .iter()
            .find(|preset| preset.name == "paper-expansion-rossler-param-shift-h64")
            .unwrap();
        assert!(rossler_shift.model_names().contains(&"fern_icml_pinned"));
        assert!(!rossler_shift.model_names().contains(&"fern"));

        let rossler_dual_dial = presets
            .iter()
            .find(|preset| preset.name == "paper-rossler-dual-dial-h64")
            .unwrap();
        assert!(rossler_dual_dial.model_names().contains(&"fern_icml_pinned"));
        assert!(!rossler_dual_dial.model_names().contains(&"fern"));

        let real_sensor_trained = presets
            .iter()
            .find(|preset| preset.name == "paper-real-sensor-trained-h64")
            .unwrap();
        assert_eq!(
            real_sensor_trained.model_names(),
            vec!["duet", "fern_icml_pinned", "patchtst", "timemixer"]
        );
        assert_eq!(real_sensor_trained.recipes.len(), 8);
        assert!(real_sensor_trained.recipes.iter().all(|recipe| {
            matches!(
                recipe.family.value(),
                "hugadb_walk_only_natural" | "hugadb_walk_run_natural"
            ) && recipe.plan == RecipePlanName::TitrationH64RelativeDoses
                && recipe.n_trajectory_draws == 1
                && recipe.n_noise_draws == 4
                && recipe.split_treatment_scheme == Some(SplitTreatmentScheme::NoiseMatchedRetraining)
        }));
    }

    #[test]
    fn rossler_param_shift_fern_icml_canary_is_one_clean_target() {
        let recipes = paper_canary_rossler_param_shift_fern_icml_h64_recipe_builds();
        assert_eq!(recipes.len(), 1);
        let recipe = recipes[0].clone();
        assert_eq!(recipe.family, RecipeFamilyName::RosslerParamShock);
        assert_eq!(recipe.model, RecipeModelName::FernIcmlPinned);
        assert_eq!(recipe.plan, RecipePlanName::CleanH64);
        assert_eq!(recipe.loader, RecipeLoaderName::P70_20_10NoneBs95);
        assert_eq!(recipe.n_trajectory_draws, 1);
        assert_eq!(recipe.n_noise_draws, 1);
        assert_eq!(
            recipe.name(),
            "rossler_param_shock_fern_icml_pinned_huber_p70_20_10_none_bs95_clean_h64"
        );
    }

    #[test]
    fn lorenz_lane_keeps_the_icml_pinned_fern_identity() {
        let recipes = paper_chaotic_lorenz_h64_recipe_builds();
        assert_eq!(recipes.len(), 12);
        for family in [RecipeFamilyName::LorenzBase, RecipeFamilyName::LorenzParamShock] {
            let family_models = recipes
                .iter()
                .filter(|recipe| recipe.family == family)
                .map(|recipe| recipe.model)
                .collect::<Vec<_>>();
            assert_eq!(
                family_models,
                vec![
                    RecipeModelName::FernIcmlPinned,
                    RecipeModelName::Dlinear,
                    RecipeModelName::Chronos2,
                    RecipeModelName::ChronosBoltSmall,
                    RecipeModelName::ChronosBoltBase,
                    RecipeModelName::Tirex,
                ]
            );
        }
    }

    /// Fern's architecture vocabulary is spelled out in four places that each
    /// refuse a name they do not hold, and each refusal arrives later than the
    /// last: this enum at plan time, `FernArchitectureVariantV1` when the
    /// worker declares its architecture card, and `fr_core.py`'s table when
    /// the model is built. Adding `current_wide_scale` on 2026-08-17 hit two
    /// of them one after the other, each costing a launch, a failed plan and a
    /// journal read. This closes the two Rust copies against each other; the
    /// Python table is checked by the test below.
    #[test]
    fn every_architecture_this_crate_names_is_one_the_card_registry_accepts() {
        for variant in FernArchitectureVariant::iter() {
            let name = variant.value();
            fern_experiment_types::FernArchitectureVariantV1::parse(name)
                .unwrap_or_else(|error| panic!("architecture {name:?} is not a registered card variant: {error:?}"));
        }
    }

    /// The Python worker keeps its own copy, and a name missing there fails
    /// only once a model is being built — after training has been scheduled.
    #[test]
    fn every_architecture_this_crate_names_is_one_the_python_worker_knows() {
        // A test runs from the source tree, so locating a reviewed source file
        // by manifest directory is sound here; a DEPLOYED binary must not do
        // this, because the path is baked in at compile time.
        let fr_core = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../fr_core.py")
            .canonicalize()
            .expect("fr_core.py is a reviewed worker source file");
        let source = std::fs::read_to_string(&fr_core).expect("fr_core.py is readable");
        let table = source
            .split_once("def fern_architecture_factors")
            .expect("fr_core.py declares the architecture factor table")
            .1;
        let table = table.split_once("\n\n\n").map_or(table, |(head, _)| head);
        for variant in FernArchitectureVariant::iter() {
            let quoted = format!("{:?}:", variant.value());
            assert!(
                table.contains(&quoted),
                "architecture {:?} has no entry in fr_core.py's fern_architecture_factors table",
                variant.value()
            );
        }
        // A slice that accidentally spanned the whole file would pass the loop
        // above no matter what, so prove the slice can still say "absent".
        assert!(
            !table.contains("\"an_architecture_nobody_registered\":"),
            "the extracted table is too wide to detect a missing entry"
        );
        assert!(!table.is_empty() && table.len() < source.len());
    }

    #[test]
    fn rossler_shift_fern_factorial_is_the_complete_three_factor_cube() {
        let recipes = paper_canary_rossler_shift_fern_factorial_h64_recipe_builds();
        assert_eq!(recipes.len(), 8);
        assert_eq!(
            recipes
                .iter()
                .map(|recipe| recipe.fern_architecture.unwrap().value())
                .collect::<std::collections::BTreeSet<_>>(),
            FernArchitectureVariant::rossler_three_factor_cube()
                .into_iter()
                .map(|variant| variant.value())
                .collect::<std::collections::BTreeSet<_>>()
        );
        assert!(recipes.iter().all(|recipe| {
            recipe.family == RecipeFamilyName::RosslerParamShock
                && recipe.model == RecipeModelName::Fern
                && recipe.plan == RecipePlanName::CleanH64
                && recipe.loader == RecipeLoaderName::P70_20_10NoneBs95
                && recipe.seeds == [42]
                && recipe.n_trajectory_draws == 1
                && recipe.n_noise_draws == 1
        }));
    }

    #[test]
    fn preset_recipes_are_catalog_recipes() {
        // `run --preset` runs preset.recipes directly. Every preset
        // blueprint must have a byte-identical catalog entry under the same
        // name, or the preset can run a recipe that `list-recipes`,
        // `run-recipe`, and `list-recipe-run-targets` cannot resolve.
        let catalog = recipe_catalog_builds();
        for preset in preset_catalog_builds() {
            for recipe in preset.recipes {
                let entry = catalog
                    .iter()
                    .find(|entry| entry.name() == recipe.name())
                    .unwrap_or_else(|| {
                        panic!(
                            "preset {:?} recipe {:?} is not in recipe_catalog_builds()",
                            preset.name,
                            recipe.name()
                        )
                    });
                assert_eq!(
                    *entry,
                    recipe,
                    "preset {:?} recipe {:?} drifted from the catalog entry with the same name",
                    preset.name,
                    recipe.name()
                );
            }
        }
    }

    #[test]
    fn recipe_catalog_names_are_unique_including_bank_expansions() {
        // Recipe selection is name-based. A duplicate name with a different
        // preimage is not cosmetic: `--recipe` could select the wrong bank
        // count while the displayed recipe still looked correct.
        let mut names = std::collections::BTreeSet::new();
        for recipe in recipe_catalog_builds() {
            let name = recipe.name();
            assert!(names.insert(name.clone()), "duplicate recipe name {name:?}");
        }
    }

    #[test]
    fn eight_bank_names_are_explicit_without_renaming_the_four_bank_lane() {
        let four = RecipeBlueprint::new(RecipeFamilyName::LgSsmBase)
            .model(RecipeModelName::Dlinear)
            .plan(RecipePlanName::TitrationH64RelativeDoses)
            .trajectory_draws(4)
            .noise_draws(4)
            .scale_noise_by_std()
            .split_treatment_scheme(SplitTreatmentScheme::NoiseMatchedRetraining);
        let eight = four.clone().noise_draws(8);

        assert!(!four.name().contains("_banks"));
        assert!(eight.name().contains("_banks8_"));
        assert_ne!(four.name(), eight.name());
    }

    #[test]
    fn recipe_catalog_keeps_runtime_flags_from_python_catalog() {
        let recipes = recipe_catalog_builds();

        let ou_base = recipes
            .iter()
            .find(|recipe| {
                recipe.family == RecipeFamilyName::OuBase && recipe.plan == RecipePlanName::TitrationH64H192Sigmas
            })
            .expect("OU base titration recipe exists");
        assert_eq!(ou_base.n_trajectory_draws, 3);
        assert_eq!(ou_base.n_noise_draws, 3);

        let hugadb_fern = recipes
            .iter()
            .find(|recipe| {
                recipe.family == RecipeFamilyName::HugadbAccelEmg
                    && recipe.model == RecipeModelName::Fern
                    && recipe.plan == RecipePlanName::TitrationH64Sigmas
            })
            .expect("HuGaDB Fern titration recipe exists");
        assert!(hugadb_fern.run_whiten);
        assert!(hugadb_fern.scale_noise_by_std);

        let hugadb_baseline = recipes
            .iter()
            .find(|recipe| {
                recipe.family == RecipeFamilyName::HugadbSwitch
                    && recipe.model == RecipeModelName::Dlinear
                    && recipe.plan == RecipePlanName::TitrationH64Sigmas
            })
            .expect("HuGaDB DLinear switch recipe exists");
        assert!(!hugadb_baseline.run_whiten);
        assert!(hugadb_baseline.scale_noise_by_std);

        let csr_recipes = paper_csr_ou_recipe_builds();
        assert_eq!(csr_recipes.len(), 3);
        assert!(csr_recipes.iter().all(|recipe| {
            recipe.plan == RecipePlanName::TitrationH64RelativeDoses
                && recipe.n_trajectory_draws == 4
                && recipe.n_noise_draws == 4
                && recipe.scale_noise_by_std
                && recipe.split_treatment_scheme == Some(SplitTreatmentScheme::CleanTrainCleanSelectTestTitration)
                && recipe.checkpoint_reuse_policy == Some(CheckpointReusePolicy::CleanValidationBest)
        }));
        assert_eq!(
            RecipePlanName::TitrationH64RelativeDoses.noise_levels(),
            [0.0, 0.0625, 0.125, 0.25, 0.5, 1.0, 2.0]
        );

        let smoke_recipes = paper_csr_ou_retraining_smoke_recipe_builds();
        assert_eq!(smoke_recipes.len(), 3);
        assert!(
            smoke_recipes
                .iter()
                .all(|recipe| recipe.checkpoint_reuse_policy.is_none())
        );
        assert!(
            smoke_recipes
                .iter()
                .all(|recipe| !recipe.name().contains("reuse_clean_validation_best"))
        );

        let core_recipes = paper_core_h64_recipe_builds();
        // 3 worlds x 6 model tracks: dlinear, chronos2, tirex, the two
        // Chronos-Bolt sizes, and Zeus. TimesFM is out of this paper.
        assert_eq!(core_recipes.len(), 18);
        assert!(core_recipes.iter().all(|recipe| {
            recipe.plan == RecipePlanName::TitrationH64RelativeDoses
                && recipe.n_noise_draws == 4
                && recipe.scale_noise_by_std
                && recipe.split_treatment_scheme == Some(SplitTreatmentScheme::NoiseMatchedRetraining)
                && recipe.checkpoint_reuse_policy.is_none()
        }));

        let extended_t3_recipes = paper_extended_t3_h64_recipe_builds();
        // 4 worlds x 10 model tracks: the six fixed-weight/core tracks plus
        // Fern, PatchTST, TimeMixer, and DUET. The exact count is a regression
        // pin for the registered matrix; `accept-sweep` derives the run-time
        // coordinate set from these recipes instead of copying this number.
        // CHG (2026-08-11): 3 worlds -> 4; Lorenz-63 joined every tail-law lane.
        assert_eq!(extended_t3_recipes.len(), 40);
        assert!(extended_t3_recipes.iter().all(|recipe| {
            recipe.plan == RecipePlanName::TitrationH64RelativeDoses
                && recipe.n_noise_draws == 4
                && recipe.scale_noise_by_std
                && recipe.noise_kind == fern_experiment_types::NoiseKind::StudentT3
                && recipe.name().contains("_student_t3_")
                && recipe.split_treatment_scheme == Some(SplitTreatmentScheme::NoiseMatchedRetraining)
                && recipe.checkpoint_reuse_policy.is_none()
        }));

        let extended_laplace_recipes = paper_extended_laplace_h64_recipe_builds();
        // CHG (2026-08-11): 3 models on 3 worlds -> the same 10-model panel as
        // t3 and contamination, on 4 worlds. The ladder's middle rung had been
        // measuring a different set of forecasters than its two ends.
        assert_eq!(extended_laplace_recipes.len(), 40);
        assert!(extended_laplace_recipes.iter().all(|recipe| {
            recipe.plan == RecipePlanName::TitrationH64RelativeDoses
                && recipe.n_noise_draws == 4
                && recipe.scale_noise_by_std
                && recipe.noise_kind == fern_experiment_types::NoiseKind::Laplace
                && recipe.name().contains("_laplace_")
                && recipe.split_treatment_scheme == Some(SplitTreatmentScheme::NoiseMatchedRetraining)
                && recipe.checkpoint_reuse_policy.is_none()
        }));

        let timemixer_expansion_recipes = paper_expansion_timemixer_h64_recipe_builds();
        assert_eq!(timemixer_expansion_recipes.len(), 3);
        assert!(timemixer_expansion_recipes.iter().all(|recipe| {
            recipe.model == RecipeModelName::Timemixer
                && recipe.plan == RecipePlanName::TitrationH64RelativeDoses
                && recipe.n_noise_draws == 4
                && recipe.scale_noise_by_std
                && recipe.split_treatment_scheme == Some(SplitTreatmentScheme::NoiseMatchedRetraining)
                && recipe.checkpoint_reuse_policy.is_none()
        }));

        let fern_patchtst_expansion_recipes = paper_expansion_fern_patchtst_h64_recipe_builds();
        assert_eq!(fern_patchtst_expansion_recipes.len(), 6);
        assert!(fern_patchtst_expansion_recipes.iter().all(|recipe| {
            matches!(recipe.model, RecipeModelName::Fern | RecipeModelName::Patchtst)
                && recipe.plan == RecipePlanName::TitrationH64RelativeDoses
                && recipe.n_noise_draws == 4
                && recipe.scale_noise_by_std
                && recipe.split_treatment_scheme == Some(SplitTreatmentScheme::NoiseMatchedRetraining)
                && recipe.checkpoint_reuse_policy.is_none()
        }));

        let fern_icml_expansion_recipes = paper_expansion_fern_icml_pinned_h64_recipe_builds();
        assert_eq!(fern_icml_expansion_recipes.len(), 3);
        assert!(fern_icml_expansion_recipes.iter().all(|recipe| {
            recipe.model == RecipeModelName::FernIcmlPinned
                && recipe.plan == RecipePlanName::TitrationH64RelativeDoses
                && recipe.n_noise_draws == 4
                && recipe.scale_noise_by_std
                && recipe.split_treatment_scheme == Some(SplitTreatmentScheme::NoiseMatchedRetraining)
                && recipe.checkpoint_reuse_policy.is_none()
        }));

        let duet_expansion_recipes = paper_expansion_duet_h64_recipe_builds();
        assert_eq!(duet_expansion_recipes.len(), 3);
        assert!(duet_expansion_recipes.iter().all(|recipe| {
            recipe.model == RecipeModelName::Duet
                && recipe.plan == RecipePlanName::TitrationH64RelativeDoses
                && recipe.n_noise_draws == 4
                && recipe.scale_noise_by_std
                && recipe.split_treatment_scheme == Some(SplitTreatmentScheme::NoiseMatchedRetraining)
                && recipe.checkpoint_reuse_policy.is_none()
        }));

        let regime_expansion_recipes = paper_expansion_lgssm_regime_h64_recipe_builds();
        assert_eq!(regime_expansion_recipes.len(), 7);
        assert!(regime_expansion_recipes.iter().all(|recipe| {
            recipe.family == RecipeFamilyName::LgSsmRegimeSwitch
                && recipe.loader == RecipeLoaderName::P70_20_10NoneBs95
                && recipe.train == RecipeTrainName::Default
                && recipe.n_trajectory_draws == 4
                && recipe.n_noise_draws == 4
                && recipe.scale_noise_by_std
                && !recipe.single_clean_anchor
                && recipe.split_treatment_scheme == Some(SplitTreatmentScheme::NoiseMatchedRetraining)
        }));

        let regime_primary_recipes = paper_lgssm_regime_primary_h64_recipe_builds();
        assert_eq!(regime_primary_recipes.len(), 8);
        assert!(regime_primary_recipes.iter().all(|recipe| {
            recipe.family == RecipeFamilyName::LgSsmRegimeSwitch
                && recipe.plan == RecipePlanName::TitrationH64PrimaryDoses
                && recipe.scale_noise_by_std
        }));
        for model in [
            RecipeModelName::Dlinear,
            RecipeModelName::Timemixer,
            RecipeModelName::Patchtst,
            RecipeModelName::Duet,
        ] {
            let recipes = regime_primary_recipes
                .iter()
                .filter(|recipe| recipe.model == model)
                .collect::<Vec<_>>();
            assert_eq!(recipes.len(), 2);
            let paired_draws = if model == RecipeModelName::Dlinear { 8 } else { 4 };
            assert!(
                recipes.iter().all(|recipe| {
                    recipe.n_trajectory_draws == paired_draws && recipe.n_noise_draws == paired_draws
                })
            );
        }
        assert_eq!(
            RecipePlanName::TitrationH64PrimaryDoses.noise_levels(),
            &[0.0, 0.125, 0.5, 2.0]
        );
        assert!(regime_primary_recipes.chunks_exact(2).all(|recipes| {
            recipes[0].split_treatment_scheme == Some(SplitTreatmentScheme::NoiseMatchedRetraining)
                && recipes[1].split_treatment_scheme == Some(SplitTreatmentScheme::CleanTrainCleanSelectTestTitration)
                && recipes[1].checkpoint_reuse_policy == Some(CheckpointReusePolicy::NoiseMatchedCleanValidationBest)
        }));

        let rossler_shift_recipes = paper_expansion_rossler_param_shift_h64_recipe_builds();
        assert_eq!(rossler_shift_recipes.len(), 10);
        assert!(rossler_shift_recipes.iter().all(|recipe| {
            recipe.family == RecipeFamilyName::RosslerParamShock
                && recipe.loader == RecipeLoaderName::P70_20_10NoneBs95
                && recipe.train == RecipeTrainName::Default
                && recipe.n_trajectory_draws == 1
                && recipe.n_noise_draws == 4
                && recipe.scale_noise_by_std
                && recipe.split_treatment_scheme == Some(SplitTreatmentScheme::NoiseMatchedRetraining)
        }));

        let rossler_dual_dial_recipes = paper_rossler_dual_dial_h64_recipe_builds();
        assert_eq!(rossler_dual_dial_recipes.len(), 20);
        assert!(rossler_dual_dial_recipes.iter().all(|recipe| {
            matches!(recipe.family.value(), "rossler_base" | "rossler_param_shock")
                && matches!(
                    recipe.model,
                    RecipeModelName::Dlinear
                        | RecipeModelName::FernIcmlPinned
                        | RecipeModelName::Patchtst
                        | RecipeModelName::Timemixer
                        | RecipeModelName::Duet
                )
                && recipe.n_trajectory_draws == 1
                && recipe.n_noise_draws == 4
                && recipe.scale_noise_by_std
        }));
        assert_eq!(
            rossler_dual_dial_recipes
                .iter()
                .filter(|recipe| {
                    recipe.split_treatment_scheme == Some(SplitTreatmentScheme::CleanTrainCleanSelectTestTitration)
                        && recipe.checkpoint_reuse_policy
                            == Some(CheckpointReusePolicy::NoiseMatchedCleanValidationBest)
                })
                .count(),
            10
        );
        assert_eq!(
            rossler_dual_dial_recipes
                .iter()
                .filter(|recipe| {
                    recipe.split_treatment_scheme == Some(SplitTreatmentScheme::NoiseTrainNoiseSelectCleanTest)
                        && recipe.checkpoint_reuse_policy == Some(CheckpointReusePolicy::NoiseMatchedDoseValidationBest)
                })
                .count(),
            10
        );

        let slds_expansion_recipes = paper_expansion_slds_h64_recipe_builds();
        assert_eq!(slds_expansion_recipes.len(), 10);
        assert!(slds_expansion_recipes.iter().all(|recipe| {
            recipe.family == RecipeFamilyName::Slds
                && recipe.loader == RecipeLoaderName::P70_20_10NoneBs95
                && recipe.train == RecipeTrainName::Default
                && recipe.n_trajectory_draws == 4
                && recipe.n_noise_draws == 4
                && recipe.scale_noise_by_std
                && recipe.split_treatment_scheme == Some(SplitTreatmentScheme::NoiseMatchedRetraining)
        }));

        let doublewell_expansion_recipes = paper_expansion_doublewell_h64_recipe_builds();
        assert_eq!(doublewell_expansion_recipes.len(), 10);
        assert!(doublewell_expansion_recipes.iter().all(|recipe| {
            recipe.family == RecipeFamilyName::DoublewellBarrierCollapse
                && recipe.loader == RecipeLoaderName::P70_20_10NoneBs95
                && recipe.train == RecipeTrainName::Default
                && recipe.n_trajectory_draws == 4
                && recipe.n_noise_draws == 4
                && recipe.scale_noise_by_std
                && recipe.split_treatment_scheme == Some(SplitTreatmentScheme::NoiseMatchedRetraining)
        }));

        let hugadb_recipes = paper_hugadb_h64_recipe_builds();
        assert_eq!(hugadb_recipes.len(), 7);
        assert!(hugadb_recipes.iter().all(|recipe| {
            recipe.family == RecipeFamilyName::HugadbAccelEmg
                && recipe.loader == RecipeLoaderName::P70_20_10NoneBs95
                && recipe.train == RecipeTrainName::Default
                && recipe.n_trajectory_draws == 1
                && recipe.n_noise_draws == 4
                && recipe.single_clean_anchor
                && recipe.split_treatment_scheme == Some(SplitTreatmentScheme::NoiseMatchedRetraining)
        }));

        let hugadb_test_corruption_recipes = paper_hugadb_test_corruption_h64_recipe_builds();
        assert_eq!(hugadb_test_corruption_recipes.len(), 7);
        assert!(hugadb_test_corruption_recipes.iter().all(|recipe| {
            recipe.single_clean_anchor
                && matches!(recipe.model, RecipeModelName::Chronos2 | RecipeModelName::Tirex)
                    == recipe.checkpoint_reuse_policy.is_none()
        }));

        let test_corruption_recipes = paper_test_corruption_h64_recipe_builds();
        // 4 worlds x 3 model tracks; the fourth world closes the Rössler
        // Base/Param-Shift dial without adding another fitting stage.
        assert_eq!(test_corruption_recipes.len(), 12);
        assert!(test_corruption_recipes.iter().all(|recipe| {
            recipe.plan == RecipePlanName::TitrationH64RelativeDoses
                && recipe.n_noise_draws == 4
                && recipe.scale_noise_by_std
                && recipe.split_treatment_scheme == Some(SplitTreatmentScheme::CleanTrainCleanSelectTestTitration)
        }));
        assert!(test_corruption_recipes.iter().all(|recipe| {
            (recipe.model == RecipeModelName::Dlinear)
                == (recipe.checkpoint_reuse_policy == Some(CheckpointReusePolicy::NoiseMatchedCleanValidationBest))
        }));

        let test_corruption_t3_recipes = paper_test_corruption_t3_recipe_builds();
        assert_eq!(test_corruption_t3_recipes.len(), 9);
        assert!(test_corruption_t3_recipes.iter().all(|recipe| {
            recipe.noise_kind == fern_experiment_types::NoiseKind::StudentT3
                && recipe.split_treatment_scheme == Some(SplitTreatmentScheme::CleanTrainCleanSelectTestTitration)
                && (recipe.model == RecipeModelName::Dlinear)
                    == (recipe.checkpoint_reuse_policy == Some(CheckpointReusePolicy::NoiseMatchedCleanValidationBest))
        }));
    }
}
