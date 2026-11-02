//! `fern_experiment_types` — stable types shared by Fern packages.
//!
//! Scope:
//! - device vocabulary shared by planning and resolved configurations;
//! - pure, serializable error receipts shared by every Fern entry point.
//! - versioned model-architecture and model-implementation declarations.
//! - content-addressed stage-plan contracts that name stages and artifact
//!   dependencies without executing them.
//!
//! Non-scope:
//! - declared execution profiles (`fern_execution_profile`);
//! - database ownership and lifecycle (`fern_run`);
//! - preset expansion, worker spawning, and CLI presentation (`fern-cli-rs`);
//! - data transforms (the historical Rust implementation is retained under
//!   `retired/serie-rs`).
//! - running a stage, holding or deleting artifacts, or creating leases.
//!
mod stage_plan;
pub use stage_plan::{
    ContentAddressedSubjectV1, MaterializationRequirementV1, STAGE_PIPELINE_TEMPLATE_SCHEMA, STAGE_PLAN_SCHEMA,
    Sha256Digest, StageImplementationRefV1, StageInputSourceV1, StageInputSpecV1, StageOutputSpecV1,
    StagePipelineStageV1, StagePipelineTemplateV1, StagePlanError, StagePlanExportV1, StagePlanV1, StageRunnerV1,
    StageSpecV1,
};

use schemars::JsonSchema;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};

// ---------------------------------------------------------------------------
// Declared scientific analyses
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema, PartialEq)]
#[serde(deny_unknown_fields)]
pub struct AnalysisOutputSpecV1 {
    /// Repository-relative path promised before computation starts.
    pub path: String,
    /// Required top-level `schema` for JSON outputs; absent for PDF/PNG/etc.
    pub schema_id: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema, PartialEq, Eq, Default)]
#[serde(deny_unknown_fields)]
pub struct AcceptedMatrixQueryV1 {
    /// Scientific grid/preset names. A lane is deliberately not expressible.
    #[serde(default)]
    pub grids: Vec<String>,
    /// Anchored scientific grid patterns. `*` is the only wildcard; paths and
    /// lane names are deliberately not expressible.
    #[serde(default)]
    pub grid_patterns: Vec<String>,
    #[serde(default)]
    pub datasets: Vec<String>,
    #[serde(default)]
    pub models: Vec<String>,
    pub noise_law: Option<String>,
    pub split_treatment_scheme: Option<String>,
    pub runtime_device: Option<String>,
    /// Primary evidence is replica zero unless a declaration says otherwise.
    #[serde(default)]
    pub include_replicas: bool,
    /// Normally two rows for one scientific coordinate are an error. Audits
    /// that explicitly compare separately registered grids may instead keep
    /// grid identity as a declared stratum. This is never inferred from lane.
    #[serde(default)]
    pub grid_multiplicity: GridMultiplicityV1,
    /// Required when the selected cohort contains unsigned historical rows.
    pub accept_unsigned_reason: Option<String>,
    /// Required when intentionally analysing an older immutable export.
    pub accept_stale_export_reason: Option<String>,
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, JsonSchema, PartialEq, Eq, Default)]
#[serde(rename_all = "snake_case")]
pub enum GridMultiplicityV1 {
    /// One row per scientific coordinate; any duplicate is refused.
    #[default]
    OneScientificCell,
    /// The registered grid/preset is part of the analysis grouping key.
    DistinctGridStrata,
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema, PartialEq)]
#[serde(tag = "source", rename_all = "snake_case", deny_unknown_fields)]
pub enum AnalysisEvidenceDeclarationV1 {
    /// Exact input artifacts for an analysis derived from upstream findings.
    Artifacts { paths: Vec<String> },
    /// A scientific query resolved by Rust against the accepted evidence table.
    AcceptedMatrix {
        table: String,
        manifest: String,
        query: AcceptedMatrixQueryV1,
    },
    /// One Rust-resolved cohort plus exact upstream files used beside it.
    /// This is one declaration so neither side can drift independently.
    AcceptedMatrixWithArtifacts {
        table: String,
        manifest: String,
        query: AcceptedMatrixQueryV1,
        paths: Vec<String>,
    },
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema, PartialEq)]
#[serde(deny_unknown_fields)]
pub struct AnalysisDeclarationV1 {
    pub schema: String,
    /// Scientific result schema, e.g. `fern.main_table.v1`.
    pub analysis_schema: String,
    pub kind: String,
    /// Which implementation of `kind` this declaration is, when more than one
    /// exists: `rust`, `python`, and so on.
    ///
    /// One statistic may be computed by more than one program. Those are
    /// implementations of one analysis, not different analyses, so they share a
    /// `kind` and differ here. The name is carried as DATA rather than inferred
    /// from the filename, because a renamed file must be refused rather than
    /// quietly answering as something else: the resolver requires the filename
    /// suffix to equal this field.
    ///
    /// Absent means the single-implementation case, which needs no suffix and no
    /// `--implementation` flag.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub implementation: Option<String>,
    pub generator: String,
    /// Every source file whose bytes can change the numerical answer.
    pub implementation_sources: Vec<String>,
    #[serde(default)]
    pub parameters: Value,
    pub evidence: AnalysisEvidenceDeclarationV1,
    pub promised_outputs: Vec<AnalysisOutputSpecV1>,
}

impl AnalysisDeclarationV1 {
    /// Refuse an implementation name that cannot be a filename suffix.
    ///
    /// The resolver builds `analysis_declarations/<kind>.<implementation>.json`
    /// and requires the suffix to equal this field, so a name carrying a dot
    /// would make the two halves ambiguous and a name carrying anything outside
    /// this set would not survive the round trip. Checking it here means a bad
    /// name is refused when the declaration is read, not when someone tries to
    /// run it.
    pub fn validate_implementation_name(name: &str) -> Result<(), String> {
        if name.is_empty() {
            return Err("implementation name must not be empty".to_string());
        }
        if let Some(bad) = name
            .chars()
            .find(|c| !(c.is_ascii_lowercase() || c.is_ascii_digit() || *c == '-'))
        {
            return Err(format!(
                "implementation name {name:?} may contain only lowercase letters, digits and hyphens; found {bad:?}"
            ));
        }
        Ok(())
    }

    /// Check every rule this declaration owns about its own fields.
    pub fn validate(&self) -> Result<(), String> {
        match self.implementation.as_deref() {
            Some(name) => Self::validate_implementation_name(name),
            None => Ok(()),
        }
    }
}

impl fern_json_contract::JsonContract for AnalysisDeclarationV1 {
    const SCHEMA_ID: &'static str = "fern.declared_analysis.v1";
}

/// Wire-schema version for a structured, user-actionable Fern error.
pub const ERROR_RECEIPT_SCHEMA: &str = "fern.error_receipt.v1";

/// The declared device class for a planned or resolved Fern run.
///
/// This sits here rather than in a specific executor because an execution
/// profile and the resolved run configuration must use one vocabulary. A CPU
/// and CUDA run remain distinct in the resolved configuration's identity.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum DeviceKind {
    Cpu,
    Cuda,
}

// ---------------------------------------------------------------------------
// Declared execution replicas
// ---------------------------------------------------------------------------

/// Wire schema for one intentionally repeated execution of a scientific cell.
pub const DECLARED_REPLICA_SCHEMA: &str = "fern.declared_replica.v1";

/// Why Fern is allowed to execute an already-declared scientific cell again.
///
/// This is deliberately bounded. A free-form note cannot distinguish a planned
/// robustness check from an accidental duplicate in a query or acceptance
/// policy. The human explanation remains available through [`Self::reason`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum DeclaredReplicaPurpose {
    /// Recompute the same cell while retaining per-forecast-origin tensors that
    /// an older slim artifact policy discarded.
    ForecastOriginEvidence,
    /// Repeat the fit with a separately declared model-training seed.
    ModelSeedRobustness,
    /// Repeat the same scientific cell under a separately declared execution
    /// platform or numerical implementation.
    ExecutionPlatformRobustness,
}

impl DeclaredReplicaPurpose {
    pub const fn value(self) -> &'static str {
        match self {
            Self::ForecastOriginEvidence => "forecast_origin_evidence",
            Self::ModelSeedRobustness => "model_seed_robustness",
            Self::ExecutionPlatformRobustness => "execution_platform_robustness",
        }
    }

    pub fn parse(value: &str) -> Result<Self, String> {
        for purpose in [
            Self::ForecastOriginEvidence,
            Self::ModelSeedRobustness,
            Self::ExecutionPlatformRobustness,
        ] {
            if value == purpose.reason() {
                return Ok(purpose);
            }
        }
        match value {
            "forecast-origin-evidence" | "forecast_origin_evidence" => Ok(Self::ForecastOriginEvidence),
            "model-seed-robustness" | "model_seed_robustness" => Ok(Self::ModelSeedRobustness),
            "execution-platform-robustness" | "execution_platform_robustness" => Ok(Self::ExecutionPlatformRobustness),
            other => Err(format!(
                "unknown replica purpose {other:?}. Valid: forecast-origin-evidence, model-seed-robustness, execution-platform-robustness"
            )),
        }
    }

    pub const fn reason(self) -> &'static str {
        match self {
            Self::ForecastOriginEvidence => {
                "repeat the same scientific cell to retain per-forecast-origin evidence discarded by the earlier slim artifact policy"
            }
            Self::ModelSeedRobustness => "repeat the scientific cell with a predeclared model-training seed",
            Self::ExecutionPlatformRobustness => {
                "repeat the scientific cell under a predeclared execution platform or numerical implementation"
            }
        }
    }
}

/// Identity carried by a repeated execution without changing the scientific
/// coordinate it answers.
///
/// The ordinal and purpose enter `run_hash`, so the repeat has its own registry
/// row and artifact directory. They do not enter `target_hash`: both executions
/// still answer the same scientific cell.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
pub struct DeclaredReplicaV1 {
    pub schema: String,
    pub ordinal: i64,
    pub purpose: DeclaredReplicaPurpose,
}

impl DeclaredReplicaV1 {
    pub fn new(ordinal: i64, purpose: DeclaredReplicaPurpose) -> Result<Self, String> {
        if ordinal <= 0 {
            return Err(format!(
                "replica ordinal must be positive; 0 is the primary execution, found {ordinal}"
            ));
        }
        Ok(Self {
            schema: DECLARED_REPLICA_SCHEMA.to_string(),
            ordinal,
            purpose,
        })
    }

    pub fn validate(&self) -> Result<(), String> {
        if self.schema != DECLARED_REPLICA_SCHEMA {
            return Err(format!(
                "declared replica schema must be {DECLARED_REPLICA_SCHEMA:?}, found {:?}",
                self.schema
            ));
        }
        if self.ordinal <= 0 {
            return Err(format!(
                "replica ordinal must be positive; 0 is the primary execution, found {}",
                self.ordinal
            ));
        }
        Ok(())
    }
}

/// The seven user-visible families of a Fern failure.
///
/// The code answers *what kind of fix is needed*. `ErrorStage` answers
/// *where the request stopped*. Keeping them separate lets an agent distinguish
/// “the GPU is full before work began” from “the worker failed after training”.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ErrorCode {
    /// The request is malformed or names an impossible scientific design.
    InvalidRequest,
    /// A declared model, data asset, profile, or runtime is unavailable.
    MissingPrerequisite,
    /// A current owner already has authority for the requested work.
    OwnershipConflict,
    /// The request is valid but cannot safely start with current resources.
    CapacityBlocked,
    /// A worker started, then returned an error, timed out, or was lost.
    WorkerFailed,
    /// A run finished but its expected artifact is absent, changed, or malformed.
    ArtifactInvalid,
    /// A candidate result cannot enter the requested evidence release.
    EvidenceRefused,
}

/// The authority boundary at which Fern stopped a request.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ErrorStage {
    Request,
    Preflight,
    Ownership,
    Capacity,
    Worker,
    Artifact,
    Evidence,
}

/// Whether the attempted operation had begun real computation before failing.
///
/// `Unknown` is intentional: an SSH connection or a crashed external worker
/// can prevent Fern from proving either answer. It is more honest than saying
/// “no GPU work started” based on an unavailable process table.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum WorkStartState {
    NotStarted,
    Started,
    Unknown,
}

impl WorkStartState {
    /// Plain-language form for a terminal or TUI receipt.
    pub const fn human_text(self) -> &'static str {
        match self {
            Self::NotStarted => "No. Fern blocked the request before work began.",
            Self::Started => "Yes. The worker had already begun work.",
            Self::Unknown => "Unknown. Fern could not verify the external worker state.",
        }
    }
}

/// The safe retry route for a failed request.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum RetryPolicy {
    /// Repeating this exact request cannot make it valid.
    Never,
    /// The same declared execution profile can retry after the stated fix.
    SameProfile,
    /// The request needs a different declared execution profile.
    OtherProfile,
    /// A human must make a policy or evidence decision before retrying.
    RequiresHuman,
}

impl RetryPolicy {
    /// Whether an automatic retry is ever appropriate after the stated fix.
    pub const fn is_retryable(self) -> bool {
        !matches!(self, Self::Never | Self::RequiresHuman)
    }
}

/// One machine-readable and human-readable explanation of why Fern stopped.
///
/// This is an operation result, not a transition receipt. A rejected request
/// must explain itself without pretending that it changed a Matrix, Coordinate,
/// Attempt, Artifact, or EvidenceRelease state.
///
/// `fix_command` and `retry_command` are argv tokens rather than a shell string.
/// The CLI can render them for a person without ever executing them implicitly.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ErrorReceipt {
    pub schema: String,
    pub code: ErrorCode,
    pub stage: ErrorStage,
    pub summary: String,
    pub what_happened: String,
    pub work_started: WorkStartState,
    pub retry_policy: RetryPolicy,
    pub expected: Option<String>,
    pub observed: Option<String>,
    pub fix_command: Option<Vec<String>>,
    pub retry_command: Option<Vec<String>>,
    pub operation_id: Option<String>,
}

impl fern_json_contract::JsonContract for ErrorReceipt {
    const SCHEMA_ID: &'static str = ERROR_RECEIPT_SCHEMA;
}

impl ErrorReceipt {
    /// Create an error receipt with the stable Fern schema marker.
    pub fn new(
        code: ErrorCode,
        stage: ErrorStage,
        summary: impl Into<String>,
        what_happened: impl Into<String>,
        work_started: WorkStartState,
        retry_policy: RetryPolicy,
    ) -> Self {
        Self {
            schema: ERROR_RECEIPT_SCHEMA.to_string(),
            code,
            stage,
            summary: summary.into(),
            what_happened: what_happened.into(),
            work_started,
            retry_policy,
            expected: None,
            observed: None,
            fix_command: None,
            retry_command: None,
            operation_id: None,
        }
    }

    /// Render the same structured receipt as short, actionable terminal text.
    pub fn render_text(&self) -> String {
        let mut sections = vec![
            self.summary.clone(),
            format!("What happened:\n  {}", self.what_happened),
        ];
        sections.push(format!("Work started:\n  {}", self.work_started.human_text()));
        if self.expected.is_some() || self.observed.is_some() {
            let mut why = String::from("Why:");
            if let Some(expected) = &self.expected {
                why.push_str(&format!("\n  Expected: {expected}"));
            }
            if let Some(observed) = &self.observed {
                why.push_str(&format!("\n  Observed: {observed}"));
            }
            sections.push(why);
        }
        if let Some(command) = &self.fix_command {
            sections.push(format!("Fix:\n  {}", render_command(command)));
        }
        if let Some(command) = &self.retry_command {
            sections.push(format!("Then retry:\n  {}", render_command(command)));
        }
        sections.join("\n\n")
    }
}

fn render_command(command: &[String]) -> String {
    command.join(" ")
}

// ---------------------------------------------------------------------------
// Model Architecture Card V1
// ---------------------------------------------------------------------------

/// Wire schema for the first typed model-architecture card.
pub const MODEL_ARCHITECTURE_CARD_SCHEMA: &str = "fern.model_architecture.v1";

/// Content identity of the schema that an external architecture plugin parses.
///
/// A version number alone is not sufficient: a plugin could accidentally
/// change the schema while leaving `plugin_schema_version = 1`.  Carrying the
/// reviewed schema digest makes that drift visible before planning.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize, JsonSchema)]
#[serde(try_from = "String", into = "String")]
pub struct ArchitecturePluginSchemaHash(
    #[schemars(regex(pattern = "^architecture-schema:sha256:[0-9a-f]{64}$"))] String,
);

impl ArchitecturePluginSchemaHash {
    pub fn parse(value: impl Into<String>) -> Result<Self, ArchitectureCardError> {
        let value = value.into();
        let Some(hex) = value.strip_prefix("architecture-schema:sha256:") else {
            return Err(ArchitectureCardError::new(
                "plugin_schema_hash",
                "must start with architecture-schema:sha256:",
            ));
        };
        if hex.len() != 64
            || !hex
                .bytes()
                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        {
            return Err(ArchitectureCardError::new(
                "plugin_schema_hash",
                "must end with exactly 64 lowercase hexadecimal digits",
            ));
        }
        Ok(Self(value))
    }

    pub fn from_schema_bytes(bytes: &[u8]) -> Self {
        Self(format!("architecture-schema:sha256:{:x}", Sha256::digest(bytes)))
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl TryFrom<String> for ArchitecturePluginSchemaHash {
    type Error = ArchitectureCardError;

    fn try_from(value: String) -> Result<Self, Self::Error> {
        Self::parse(value)
    }
}

impl From<ArchitecturePluginSchemaHash> for String {
    fn from(value: ArchitecturePluginSchemaHash) -> Self {
        value.0
    }
}

/// Stable identity of one canonical model architecture.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize, JsonSchema)]
#[serde(try_from = "String", into = "String")]
pub struct ModelArchitectureHash(#[schemars(regex(pattern = "^architecture:sha256:[0-9a-f]{64}$"))] String);

impl ModelArchitectureHash {
    fn from_preimage(bytes: &[u8]) -> Self {
        Self(format!("architecture:sha256:{:x}", Sha256::digest(bytes)))
    }

    pub fn parse(value: impl Into<String>) -> Result<Self, ArchitectureCardError> {
        let value = value.into();
        let Some(hex) = value.strip_prefix("architecture:sha256:") else {
            return Err(ArchitectureCardError::new(
                "architecture_hash",
                "must start with architecture:sha256:",
            ));
        };
        if hex.len() != 64
            || !hex
                .bytes()
                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        {
            return Err(ArchitectureCardError::new(
                "architecture_hash",
                "must end with exactly 64 lowercase hexadecimal digits",
            ));
        }
        Ok(Self(value))
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl TryFrom<String> for ModelArchitectureHash {
    type Error = ArchitectureCardError;

    fn try_from(value: String) -> Result<Self, Self::Error> {
        Self::parse(value)
    }
}

impl From<ModelArchitectureHash> for String {
    fn from(value: ModelArchitectureHash) -> Self {
        value.0
    }
}

/// One invalid card field plus the reason it was refused.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ArchitectureCardError {
    pub field: String,
    pub explanation: String,
}

impl ArchitectureCardError {
    fn new(field: impl Into<String>, explanation: impl Into<String>) -> Self {
        Self {
            field: field.into(),
            explanation: explanation.into(),
        }
    }
}

impl std::fmt::Display for ArchitectureCardError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "{}: {}", self.field, self.explanation)
    }
}

impl std::error::Error for ArchitectureCardError {}

/// The raw JSON-facing architecture vocabulary.
///
/// Built-in families deserialize directly into typed Rust fields.  An
/// external payload remains opaque only until its named plugin canonicalizer
/// validates it; core code never accepts the raw `Value` as a canonical card.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(
    tag = "family",
    content = "architecture",
    rename_all = "snake_case",
    deny_unknown_fields
)]
pub enum RawModelArchitectureCardV1 {
    Fern(RawFernArchitectureV1),
    DLinear(RawDLinearArchitectureV1),
    External(RawExternalArchitectureV1),
}

/// Generate the versioned JSON Schema consumed by Python, agents, and future
/// plugin tooling from the same Rust types that parse the request.
pub fn raw_model_architecture_json_schema() -> Result<Value, ArchitectureCardError> {
    serde_json::to_value(schemars::schema_for!(RawModelArchitectureCardV1)).map_err(|error| {
        ArchitectureCardError::new(
            "architecture.schema",
            format!("cannot encode generated JSON Schema: {error}"),
        )
    })
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RawArchitectureShapeV1 {
    pub context_length: u64,
    pub horizon_length: u64,
    pub channel_contract: RawArchitectureChannelContractV1,
}

/// Whether channel count is part of the instantiated weight structure.
///
/// Fern and shared-weight DLinear accept any positive channel count supplied
/// by the ScientificCase.  Per-channel DLinear creates separate parameters
/// and therefore requires an exact count in its architecture identity.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum RawArchitectureChannelContractV1 {
    RuntimePositive,
    Exact { count: u64 },
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RawFernPatchV1 {
    pub length: u64,
    pub patch_length: u64,
    pub group_count: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum RawFernOperatorV1 {
    Householder,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema, strum::EnumIter)]
#[serde(rename_all = "snake_case")]
pub enum FernDensityVariantV1 {
    SingleGaussian,
    BoundedMdn,
    SparseMoe,
    SharedTransportMixtureSource,
    MixtureOfFernTransports,
    /// The first density that ATTACHES to the Core seam instead of replacing
    /// the Core: K component laws built from the Core's own hidden, sharing the
    /// Core's transport. One component is legal here and is the ladder's base
    /// rung, because a single component with a zero source offset is the Core's
    /// own single-Gaussian law.
    MixtureSourceSharedHidden,
    /// The rung above: one Fern transport per component, all built from that
    /// same Core hidden, so components may differ in shape and orientation and
    /// not only in position.
    MixtureFernTransportsSharedHidden,
}

impl FernDensityVariantV1 {
    pub fn parse(value: &str) -> Result<Self, ArchitectureCardError> {
        match value {
            "single_gaussian" => Ok(Self::SingleGaussian),
            "bounded_mdn" => Ok(Self::BoundedMdn),
            "sparse_moe" => Ok(Self::SparseMoe),
            "shared_transport_mixture_source" => Ok(Self::SharedTransportMixtureSource),
            "mixture_of_fern_transports" => Ok(Self::MixtureOfFernTransports),
            "mixture_source_shared_hidden" => Ok(Self::MixtureSourceSharedHidden),
            "mixture_fern_transports_shared_hidden" => Ok(Self::MixtureFernTransportsSharedHidden),
            other => Err(ArchitectureCardError::new(
                "fern.density_variant",
                format!("unknown registered Fern density {other:?}"),
            )),
        }
    }

    pub const fn value(self) -> &'static str {
        match self {
            Self::SingleGaussian => "single_gaussian",
            Self::BoundedMdn => "bounded_mdn",
            Self::SparseMoe => "sparse_moe",
            Self::SharedTransportMixtureSource => "shared_transport_mixture_source",
            Self::MixtureOfFernTransports => "mixture_of_fern_transports",
            Self::MixtureSourceSharedHidden => "mixture_source_shared_hidden",
            Self::MixtureFernTransportsSharedHidden => "mixture_fern_transports_shared_hidden",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum FernArchitectureVariantV1 {
    /// User-facing moving alias. Canonical cards resolve this before hashing.
    Current,
    IcmlPinned,
    AllComplexUnclampedWide,
    InterleavedUnclampedWide,
    AllComplexClampedWide,
    AllComplexUnclampedPaperBrenier,
    InterleavedClampedWide,
    InterleavedUnclampedPaperBrenier,
    AllComplexClampedPaperBrenier,
    /// The three factors of `all_complex_unclamped_wide` with the ceiling on
    /// emitted uncertainty raised.
    ///
    /// Unlike `Current` this is NOT resolved to its factor spelling, because
    /// the factors do not distinguish it: Fern states a standard deviation of
    /// `source_std * (1 + c)` with `c` soft-clipped, and only the clip differs.
    /// Resolving it would give two models one architecture hash, which is the
    /// thing this enum exists to prevent.
    CurrentWideScale,
}

impl FernArchitectureVariantV1 {
    pub fn parse(value: &str) -> Result<Self, ArchitectureCardError> {
        match value {
            "current" => Ok(Self::Current),
            "icml_pinned" => Ok(Self::IcmlPinned),
            "all_complex_unclamped_wide" => Ok(Self::AllComplexUnclampedWide),
            "interleaved_unclamped_wide" => Ok(Self::InterleavedUnclampedWide),
            "all_complex_clamped_wide" => Ok(Self::AllComplexClampedWide),
            "all_complex_unclamped_paper_brenier" => Ok(Self::AllComplexUnclampedPaperBrenier),
            "interleaved_clamped_wide" => Ok(Self::InterleavedClampedWide),
            "interleaved_unclamped_paper_brenier" => Ok(Self::InterleavedUnclampedPaperBrenier),
            "all_complex_clamped_paper_brenier" => Ok(Self::AllComplexClampedPaperBrenier),
            "current_wide_scale" => Ok(Self::CurrentWideScale),
            other => Err(ArchitectureCardError::new(
                "fern.architecture_variant",
                format!("unknown registered Fern architecture {other:?}"),
            )),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum RawFernFactorySchemasV1 {
    Default,
}

/// Explicit schema bundle stored in an immutable canonical Fern card.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
enum FernFactorySchemasV1 {
    V1,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RawReversibleInstanceNormV1 {
    pub enabled: bool,
    pub affine: bool,
    pub subtract_last: bool,
}

impl Default for RawReversibleInstanceNormV1 {
    fn default() -> Self {
        Self {
            enabled: false,
            affine: true,
            subtract_last: false,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RawFernArchitectureV1 {
    pub schema_version: u32,
    pub architecture_variant: FernArchitectureVariantV1,
    pub shape: RawArchitectureShapeV1,
    pub hidden_width: u64,
    pub augmented_width: u64,
    pub block_size: u64,
    pub density_variant: FernDensityVariantV1,
    pub mixture_component_count: u64,
    pub sparse_active_component_count: Option<u64>,
    pub mixture_relative_std_floor_ppm: Option<u64>,
    pub operator: RawFernOperatorV1,
    pub householder_reflection_count: u64,
    pub coupling_patch: RawFernPatchV1,
    pub brenier_patch: RawFernPatchV1,
    pub factory_schemas: RawFernFactorySchemasV1,
    #[serde(default)]
    pub reversible_instance_norm: RawReversibleInstanceNormV1,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RawDLinearArchitectureV1 {
    pub schema_version: u32,
    pub shape: RawArchitectureShapeV1,
    pub moving_average_window: u64,
    pub individual_per_channel: bool,
    pub stride: u64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RawExternalArchitectureV1 {
    pub plugin_id: String,
    pub plugin_schema_version: u32,
    pub plugin_schema_hash: ArchitecturePluginSchemaHash,
    pub config: Value,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ArchitectureTrainingCapabilityV1 {
    Trainable,
    FixedWeights,
    TrainableOrFixed,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ArchitectureNativeOutputV1 {
    Point,
    PredictiveSamples,
    Quantiles,
    DistributionParameters,
}

/// Capabilities are consequences of the canonical architecture/plugin result;
/// users do not submit a second independent capability card.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ArchitectureCapabilitiesV1 {
    pub training: ArchitectureTrainingCapabilityV1,
    pub native_output: ArchitectureNativeOutputV1,
    pub has_native_point_head: bool,
    pub supports_channel_independence: bool,
}

impl ArchitectureCapabilitiesV1 {
    fn validate(&self) -> Result<(), ArchitectureCardError> {
        if self.native_output == ArchitectureNativeOutputV1::Point && !self.has_native_point_head {
            return Err(ArchitectureCardError::new(
                "architecture.capabilities.has_native_point_head",
                "must be true when native_output is point",
            ));
        }
        Ok(())
    }
}

/// Trusted plugin result. The core recomputes canonical JSON and the final
/// architecture hash; a plugin cannot inject a claimed hash.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CanonicalExternalArchitectureV1 {
    plugin_id: String,
    plugin_schema_version: u32,
    plugin_schema_hash: ArchitecturePluginSchemaHash,
    canonical_config_json: String,
    capabilities: ArchitectureCapabilitiesV1,
}

impl CanonicalExternalArchitectureV1 {
    pub fn new(
        plugin_id: impl Into<String>,
        plugin_schema_version: u32,
        plugin_schema_hash: ArchitecturePluginSchemaHash,
        canonical_config: &Value,
        capabilities: ArchitectureCapabilitiesV1,
    ) -> Result<Self, ArchitectureCardError> {
        let plugin_id = plugin_id.into();
        validate_plugin_id(&plugin_id)?;
        if plugin_schema_version == 0 {
            return Err(ArchitectureCardError::new(
                "external.plugin_schema_version",
                "must be positive",
            ));
        }
        capabilities.validate()?;
        Ok(Self {
            plugin_id,
            plugin_schema_version,
            plugin_schema_hash,
            canonical_config_json: canonical_json_string(canonical_config)?,
            capabilities,
        })
    }

    pub fn plugin_id(&self) -> &str {
        &self.plugin_id
    }
}

/// Plugin-owned parser/canonicalizer boundary.
///
/// The external implementation may deserialize into its own Rust or Python
/// typed schema, but it must return one validated canonical value and derived
/// capabilities. Unknown plugin IDs or schema versions fail before planning.
pub trait ModelArchitecturePlugin {
    fn plugin_id(&self) -> &str;

    /// Digest of the exact schema understood at this version.
    fn schema_hash(&self, schema_version: u32) -> Result<ArchitecturePluginSchemaHash, ArchitectureCardError>;

    fn canonicalize(
        &self,
        schema_version: u32,
        raw_config: &Value,
    ) -> Result<CanonicalExternalArchitectureV1, ArchitectureCardError>;
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(
    tag = "family",
    content = "architecture",
    rename_all = "snake_case",
    deny_unknown_fields
)]
enum CanonicalArchitectureBodyV1 {
    Fern(FernArchitectureV1),
    DLinear(DLinearArchitectureV1),
    External(CanonicalExternalArchitectureV1),
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ArchitectureShapeV1 {
    context_length: u64,
    horizon_length: u64,
    channel_contract: RawArchitectureChannelContractV1,
}

impl TryFrom<RawArchitectureShapeV1> for ArchitectureShapeV1 {
    type Error = ArchitectureCardError;

    fn try_from(raw: RawArchitectureShapeV1) -> Result<Self, Self::Error> {
        for (field, value) in [
            ("shape.context_length", raw.context_length),
            ("shape.horizon_length", raw.horizon_length),
        ] {
            if value == 0 {
                return Err(ArchitectureCardError::new(field, "must be positive"));
            }
        }
        if let RawArchitectureChannelContractV1::Exact { count: 0 } = raw.channel_contract {
            return Err(ArchitectureCardError::new(
                "shape.channel_contract.count",
                "must be positive",
            ));
        }
        Ok(Self {
            context_length: raw.context_length,
            horizon_length: raw.horizon_length,
            channel_contract: raw.channel_contract,
        })
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct FernPatchV1 {
    length: u64,
    patch_length: u64,
    group_count: u64,
}

impl TryFrom<RawFernPatchV1> for FernPatchV1 {
    type Error = ArchitectureCardError;

    fn try_from(raw: RawFernPatchV1) -> Result<Self, Self::Error> {
        if raw.length == 0 || raw.patch_length == 0 || raw.group_count == 0 {
            return Err(ArchitectureCardError::new(
                "fern.patch",
                "all dimensions must be positive",
            ));
        }
        if raw.patch_length.checked_mul(raw.group_count) != Some(raw.length) {
            return Err(ArchitectureCardError::new(
                "fern.patch",
                "patch_length * group_count must equal length",
            ));
        }
        Ok(Self {
            length: raw.length,
            patch_length: raw.patch_length,
            group_count: raw.group_count,
        })
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct FernArchitectureV1 {
    schema_version: u32,
    architecture_variant: FernArchitectureVariantV1,
    shape: ArchitectureShapeV1,
    hidden_width: u64,
    augmented_width: u64,
    block_size: u64,
    density_variant: FernDensityVariantV1,
    mixture_component_count: u64,
    sparse_active_component_count: Option<u64>,
    mixture_relative_std_floor_ppm: Option<u64>,
    operator: RawFernOperatorV1,
    householder_reflection_count: u64,
    coupling_patch: FernPatchV1,
    brenier_patch: FernPatchV1,
    factory_schemas: FernFactorySchemasV1,
    reversible_instance_norm: RawReversibleInstanceNormV1,
}

fn require_mixture_std_floor(value: Option<u64>) -> Result<(), ArchitectureCardError> {
    match value {
        Some(1..=999_999) => Ok(()),
        Some(_) => Err(ArchitectureCardError::new(
            "fern.mixture_relative_std_floor_ppm",
            "must encode a relative floor strictly between zero and one",
        )),
        None => Err(ArchitectureCardError::new(
            "fern.mixture_relative_std_floor_ppm",
            "mixture densities require a variance floor",
        )),
    }
}

impl TryFrom<RawFernArchitectureV1> for FernArchitectureV1 {
    type Error = ArchitectureCardError;

    fn try_from(raw: RawFernArchitectureV1) -> Result<Self, Self::Error> {
        if raw.schema_version != 1 {
            return Err(ArchitectureCardError::new(
                "fern.schema_version",
                format!("expected 1, found {}", raw.schema_version),
            ));
        }
        for (field, value) in [
            ("fern.hidden_width", raw.hidden_width),
            ("fern.augmented_width", raw.augmented_width),
            ("fern.block_size", raw.block_size),
            ("fern.mixture_component_count", raw.mixture_component_count),
            ("fern.householder_reflection_count", raw.householder_reflection_count),
        ] {
            if value == 0 {
                return Err(ArchitectureCardError::new(field, "must be positive"));
            }
        }
        match raw.density_variant {
            FernDensityVariantV1::SingleGaussian => {
                if raw.mixture_component_count != 1
                    || raw.sparse_active_component_count.is_some()
                    || raw.mixture_relative_std_floor_ppm.is_some()
                {
                    return Err(ArchitectureCardError::new(
                        "fern.density_variant",
                        "single_gaussian requires one component and no mixture-only fields",
                    ));
                }
            }
            FernDensityVariantV1::SparseMoe => {
                let Some(active) = raw.sparse_active_component_count else {
                    return Err(ArchitectureCardError::new(
                        "fern.sparse_active_component_count",
                        "sparse_moe requires an active component count",
                    ));
                };
                if active < 2 || active >= raw.mixture_component_count {
                    return Err(ArchitectureCardError::new(
                        "fern.sparse_active_component_count",
                        "must be at least two and smaller than mixture_component_count",
                    ));
                }
                require_mixture_std_floor(raw.mixture_relative_std_floor_ppm)?;
            }
            FernDensityVariantV1::MixtureSourceSharedHidden
            | FernDensityVariantV1::MixtureFernTransportsSharedHidden => {
                // ONE component is legal, unlike every Core-replacing mixture
                // above: one component with a zero source offset is the Core's
                // own law, which is what makes this family a ladder rather than
                // a separate model.
                if raw.sparse_active_component_count.is_some() {
                    return Err(ArchitectureCardError::new(
                        "fern.sparse_active_component_count",
                        "is valid only for sparse_moe",
                    ));
                }
                require_mixture_std_floor(raw.mixture_relative_std_floor_ppm)?;
            }
            FernDensityVariantV1::BoundedMdn
            | FernDensityVariantV1::SharedTransportMixtureSource
            | FernDensityVariantV1::MixtureOfFernTransports => {
                if raw.mixture_component_count < 2 {
                    return Err(ArchitectureCardError::new(
                        "fern.mixture_component_count",
                        "mixture densities require at least two components",
                    ));
                }
                if raw.sparse_active_component_count.is_some() {
                    return Err(ArchitectureCardError::new(
                        "fern.sparse_active_component_count",
                        "is valid only for sparse_moe",
                    ));
                }
                require_mixture_std_floor(raw.mixture_relative_std_floor_ppm)?;
            }
        }
        if raw.householder_reflection_count > raw.hidden_width {
            return Err(ArchitectureCardError::new(
                "fern.householder_reflection_count",
                "cannot exceed hidden_width",
            ));
        }
        if raw.shape.channel_contract != RawArchitectureChannelContractV1::RuntimePositive {
            return Err(ArchitectureCardError::new(
                "fern.shape.channel_contract",
                "Fern shares weights across channels and requires runtime_positive",
            ));
        }
        let shape = ArchitectureShapeV1::try_from(raw.shape)?;
        let coupling_patch = FernPatchV1::try_from(raw.coupling_patch)?;
        let brenier_patch = FernPatchV1::try_from(raw.brenier_patch)?;
        if coupling_patch.length != shape.horizon_length || brenier_patch.length != shape.horizon_length {
            return Err(ArchitectureCardError::new(
                "fern.patch.length",
                "coupling and Brenier patch lengths must equal the forecast horizon",
            ));
        }
        Ok(Self {
            schema_version: 1,
            architecture_variant: match raw.architecture_variant {
                FernArchitectureVariantV1::Current => FernArchitectureVariantV1::AllComplexUnclampedWide,
                explicit => explicit,
            },
            shape,
            hidden_width: raw.hidden_width,
            augmented_width: raw.augmented_width,
            block_size: raw.block_size,
            density_variant: raw.density_variant,
            mixture_component_count: raw.mixture_component_count,
            sparse_active_component_count: raw.sparse_active_component_count,
            mixture_relative_std_floor_ppm: raw.mixture_relative_std_floor_ppm,
            operator: raw.operator,
            householder_reflection_count: raw.householder_reflection_count,
            coupling_patch,
            brenier_patch,
            factory_schemas: match raw.factory_schemas {
                RawFernFactorySchemasV1::Default => FernFactorySchemasV1::V1,
            },
            reversible_instance_norm: raw.reversible_instance_norm,
        })
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct DLinearArchitectureV1 {
    schema_version: u32,
    shape: ArchitectureShapeV1,
    moving_average_window: u64,
    individual_per_channel: bool,
    stride: u64,
}

impl TryFrom<RawDLinearArchitectureV1> for DLinearArchitectureV1 {
    type Error = ArchitectureCardError;

    fn try_from(raw: RawDLinearArchitectureV1) -> Result<Self, Self::Error> {
        if raw.schema_version != 1 {
            return Err(ArchitectureCardError::new(
                "dlinear.schema_version",
                format!("expected 1, found {}", raw.schema_version),
            ));
        }
        if raw.moving_average_window == 0 || raw.moving_average_window.is_multiple_of(2) {
            return Err(ArchitectureCardError::new(
                "dlinear.moving_average_window",
                "must be a positive odd integer",
            ));
        }
        if raw.stride == 0 {
            return Err(ArchitectureCardError::new("dlinear.stride", "must be positive"));
        }
        if raw.individual_per_channel
            && matches!(
                raw.shape.channel_contract,
                RawArchitectureChannelContractV1::RuntimePositive
            )
        {
            return Err(ArchitectureCardError::new(
                "dlinear.shape.channel_contract",
                "individual_per_channel requires an exact channel count",
            ));
        }
        if !raw.individual_per_channel
            && matches!(
                raw.shape.channel_contract,
                RawArchitectureChannelContractV1::Exact { .. }
            )
        {
            return Err(ArchitectureCardError::new(
                "dlinear.shape.channel_contract",
                "shared-weight DLinear requires runtime_positive; exact channels belong only to individual_per_channel",
            ));
        }
        Ok(Self {
            schema_version: 1,
            shape: ArchitectureShapeV1::try_from(raw.shape)?,
            moving_average_window: raw.moving_average_window,
            individual_per_channel: raw.individual_per_channel,
            stride: raw.stride,
        })
    }
}

/// Validated canonical architecture. All fields are private; construction is
/// possible only through typed built-in validation or a matching plugin.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct ModelArchitectureCardV1 {
    schema: String,
    body: CanonicalArchitectureBodyV1,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct ModelArchitectureCardWireV1 {
    schema: String,
    body: CanonicalArchitectureBodyV1,
}

impl<'de> Deserialize<'de> for ModelArchitectureCardV1 {
    fn deserialize<Deserializer>(deserializer: Deserializer) -> Result<Self, Deserializer::Error>
    where
        Deserializer: serde::Deserializer<'de>,
    {
        let wire = ModelArchitectureCardWireV1::deserialize(deserializer)?;
        let card = Self {
            schema: wire.schema,
            body: wire.body,
        };
        card.validate().map_err(serde::de::Error::custom)?;
        Ok(card)
    }
}

impl ModelArchitectureCardV1 {
    pub fn canonicalize_builtin(raw: RawModelArchitectureCardV1) -> Result<Self, ArchitectureCardError> {
        let body = match raw {
            RawModelArchitectureCardV1::Fern(raw) => CanonicalArchitectureBodyV1::Fern(raw.try_into()?),
            RawModelArchitectureCardV1::DLinear(raw) => CanonicalArchitectureBodyV1::DLinear(raw.try_into()?),
            RawModelArchitectureCardV1::External(raw) => {
                return Err(ArchitectureCardError::new(
                    "external.plugin_id",
                    format!(
                        "plugin {:?} requires its registered canonicalizer; raw external JSON is never canonical",
                        raw.plugin_id
                    ),
                ));
            }
        };
        Ok(Self {
            schema: MODEL_ARCHITECTURE_CARD_SCHEMA.to_string(),
            body,
        })
    }

    pub fn canonicalize_external(
        raw: RawExternalArchitectureV1,
        plugin: &dyn ModelArchitecturePlugin,
    ) -> Result<Self, ArchitectureCardError> {
        validate_plugin_id(&raw.plugin_id)?;
        if raw.plugin_id != plugin.plugin_id() {
            return Err(ArchitectureCardError::new(
                "external.plugin_id",
                format!(
                    "card requests {:?}, but canonicalizer is {:?}",
                    raw.plugin_id,
                    plugin.plugin_id()
                ),
            ));
        }
        let expected_schema_hash = plugin.schema_hash(raw.plugin_schema_version)?;
        if raw.plugin_schema_hash != expected_schema_hash {
            return Err(ArchitectureCardError::new(
                "external.plugin_schema_hash",
                format!(
                    "card declares {:?}, but registered plugin schema is {:?}",
                    raw.plugin_schema_hash.as_str(),
                    expected_schema_hash.as_str()
                ),
            ));
        }
        let external = plugin.canonicalize(raw.plugin_schema_version, &raw.config)?;
        if external.plugin_id() != raw.plugin_id {
            return Err(ArchitectureCardError::new(
                "external.plugin_id",
                "plugin returned a canonical card for a different plugin ID",
            ));
        }
        if external.plugin_schema_hash != raw.plugin_schema_hash {
            return Err(ArchitectureCardError::new(
                "external.plugin_schema_hash",
                "plugin returned canonical content for a different schema digest",
            ));
        }
        Ok(Self {
            schema: MODEL_ARCHITECTURE_CARD_SCHEMA.to_string(),
            body: CanonicalArchitectureBodyV1::External(external),
        })
    }

    pub fn family(&self) -> &'static str {
        match self.body {
            CanonicalArchitectureBodyV1::Fern(_) => "fern",
            CanonicalArchitectureBodyV1::DLinear(_) => "dlinear",
            CanonicalArchitectureBodyV1::External(_) => "external",
        }
    }

    /// Re-run the registered plugin gate before an external card is used to
    /// plan work.
    ///
    /// Storage verification can prove that a card is internally consistent;
    /// it cannot prove that the plugin capable of executing that meaning is
    /// still installed. Planning therefore calls this method and compares the
    /// plugin's current schema digest, canonical config, and derived
    /// capabilities with the stored declaration.
    pub fn revalidate_external_with_plugin(
        &self,
        plugin: &dyn ModelArchitecturePlugin,
    ) -> Result<(), ArchitectureCardError> {
        let CanonicalArchitectureBodyV1::External(stored) = &self.body else {
            return Err(ArchitectureCardError::new(
                "architecture.family",
                "external plugin validation was requested for a built-in architecture",
            ));
        };
        if plugin.plugin_id() != stored.plugin_id {
            return Err(ArchitectureCardError::new(
                "external.plugin_id",
                format!(
                    "stored card requires {:?}, but planner supplied {:?}",
                    stored.plugin_id,
                    plugin.plugin_id()
                ),
            ));
        }
        let schema_hash = plugin.schema_hash(stored.plugin_schema_version)?;
        if schema_hash != stored.plugin_schema_hash {
            return Err(ArchitectureCardError::new(
                "external.plugin_schema_hash",
                "installed plugin schema digest does not match the stored card",
            ));
        }
        let config: Value = serde_json::from_str(&stored.canonical_config_json).map_err(|error| {
            ArchitectureCardError::new(
                "external.canonical_config_json",
                format!("cannot decode canonical plugin config: {error}"),
            )
        })?;
        let observed = plugin.canonicalize(stored.plugin_schema_version, &config)?;
        if &observed != stored {
            return Err(ArchitectureCardError::new(
                "external",
                "installed plugin canonical meaning or capabilities differ from the stored card",
            ));
        }
        Ok(())
    }

    /// Re-check a canonical card after deserialization.
    ///
    /// Private fields prevent ordinary callers from constructing an invalid
    /// card in Rust, but Serde is also a constructor.  A stored JSON object
    /// therefore has to pass the same cross-field rules as a newly submitted
    /// raw card before its hash or query projections are trusted.
    fn validate(&self) -> Result<(), ArchitectureCardError> {
        if self.schema != MODEL_ARCHITECTURE_CARD_SCHEMA {
            return Err(ArchitectureCardError::new(
                "architecture.schema",
                format!("expected {MODEL_ARCHITECTURE_CARD_SCHEMA:?}, found {:?}", self.schema),
            ));
        }

        match &self.body {
            CanonicalArchitectureBodyV1::Fern(fern) => {
                let normalized = FernArchitectureV1::try_from(RawFernArchitectureV1 {
                    schema_version: fern.schema_version,
                    architecture_variant: fern.architecture_variant,
                    shape: RawArchitectureShapeV1 {
                        context_length: fern.shape.context_length,
                        horizon_length: fern.shape.horizon_length,
                        channel_contract: fern.shape.channel_contract,
                    },
                    hidden_width: fern.hidden_width,
                    augmented_width: fern.augmented_width,
                    block_size: fern.block_size,
                    density_variant: fern.density_variant,
                    mixture_component_count: fern.mixture_component_count,
                    sparse_active_component_count: fern.sparse_active_component_count,
                    mixture_relative_std_floor_ppm: fern.mixture_relative_std_floor_ppm,
                    operator: fern.operator,
                    householder_reflection_count: fern.householder_reflection_count,
                    coupling_patch: RawFernPatchV1 {
                        length: fern.coupling_patch.length,
                        patch_length: fern.coupling_patch.patch_length,
                        group_count: fern.coupling_patch.group_count,
                    },
                    brenier_patch: RawFernPatchV1 {
                        length: fern.brenier_patch.length,
                        patch_length: fern.brenier_patch.patch_length,
                        group_count: fern.brenier_patch.group_count,
                    },
                    factory_schemas: match fern.factory_schemas {
                        FernFactorySchemasV1::V1 => RawFernFactorySchemasV1::Default,
                    },
                    reversible_instance_norm: fern.reversible_instance_norm.clone(),
                })?;
                if &normalized != fern {
                    return Err(ArchitectureCardError::new(
                        "architecture.fern",
                        "stored canonical Fern card is not normalized",
                    ));
                }
            }
            CanonicalArchitectureBodyV1::DLinear(dlinear) => {
                let normalized = DLinearArchitectureV1::try_from(RawDLinearArchitectureV1 {
                    schema_version: dlinear.schema_version,
                    shape: RawArchitectureShapeV1 {
                        context_length: dlinear.shape.context_length,
                        horizon_length: dlinear.shape.horizon_length,
                        channel_contract: dlinear.shape.channel_contract,
                    },
                    moving_average_window: dlinear.moving_average_window,
                    individual_per_channel: dlinear.individual_per_channel,
                    stride: dlinear.stride,
                })?;
                if &normalized != dlinear {
                    return Err(ArchitectureCardError::new(
                        "architecture.dlinear",
                        "stored canonical DLinear card is not normalized",
                    ));
                }
            }
            CanonicalArchitectureBodyV1::External(external) => {
                validate_plugin_id(&external.plugin_id)?;
                if external.plugin_schema_version == 0 {
                    return Err(ArchitectureCardError::new(
                        "external.plugin_schema_version",
                        "must be positive",
                    ));
                }
                ArchitecturePluginSchemaHash::parse(external.plugin_schema_hash.0.clone())?;
                external.capabilities.validate()?;
                let config: Value = serde_json::from_str(&external.canonical_config_json).map_err(|error| {
                    ArchitectureCardError::new(
                        "external.canonical_config_json",
                        format!("cannot decode canonical plugin config: {error}"),
                    )
                })?;
                if canonical_json_string(&config)? != external.canonical_config_json {
                    return Err(ArchitectureCardError::new(
                        "external.canonical_config_json",
                        "stored plugin config is not canonical JSON",
                    ));
                }
            }
        }

        self.capabilities().validate()
    }

    pub fn capabilities(&self) -> ArchitectureCapabilitiesV1 {
        match &self.body {
            CanonicalArchitectureBodyV1::Fern(_) => ArchitectureCapabilitiesV1 {
                training: ArchitectureTrainingCapabilityV1::Trainable,
                native_output: ArchitectureNativeOutputV1::PredictiveSamples,
                has_native_point_head: false,
                supports_channel_independence: true,
            },
            CanonicalArchitectureBodyV1::DLinear(_) => ArchitectureCapabilitiesV1 {
                training: ArchitectureTrainingCapabilityV1::Trainable,
                native_output: ArchitectureNativeOutputV1::Point,
                has_native_point_head: true,
                supports_channel_independence: true,
            },
            CanonicalArchitectureBodyV1::External(external) => external.capabilities.clone(),
        }
    }

    pub fn canonical_json(&self) -> Result<String, ArchitectureCardError> {
        self.validate()?;
        serde_json::to_string(self).map_err(|error| {
            ArchitectureCardError::new("architecture", format!("cannot serialize canonical card: {error}"))
        })
    }

    pub fn architecture_hash(&self) -> Result<ModelArchitectureHash, ArchitectureCardError> {
        let json = self.canonical_json()?;
        let mut preimage = Vec::with_capacity(MODEL_ARCHITECTURE_CARD_SCHEMA.len() + 1 + json.len());
        preimage.extend_from_slice(MODEL_ARCHITECTURE_CARD_SCHEMA.as_bytes());
        preimage.push(0);
        preimage.extend_from_slice(json.as_bytes());
        Ok(ModelArchitectureHash::from_preimage(&preimage))
    }

    pub fn to_record(&self) -> Result<ModelArchitectureRecordV1, ArchitectureCardError> {
        Ok(ModelArchitectureRecordV1 {
            architecture_hash: self.architecture_hash()?,
            model_family: self.family().to_string(),
            card_schema_version: 1,
            canonical_card_json: self.canonical_json()?,
            capabilities: self.capabilities(),
        })
    }
}

/// SQL/storage projection: the full canonical card always accompanies its
/// hash. Family and capabilities are rebuildable query columns, not a second
/// hand-maintained truth.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ModelArchitectureRecordV1 {
    pub architecture_hash: ModelArchitectureHash,
    pub model_family: String,
    pub card_schema_version: u32,
    pub canonical_card_json: String,
    pub capabilities: ArchitectureCapabilitiesV1,
}

impl ModelArchitectureRecordV1 {
    pub fn verify(&self) -> Result<ModelArchitectureCardV1, ArchitectureCardError> {
        let card: ModelArchitectureCardV1 = serde_json::from_str(&self.canonical_card_json).map_err(|error| {
            ArchitectureCardError::new("canonical_card_json", format!("cannot decode canonical card: {error}"))
        })?;
        if self.card_schema_version != 1
            || self.model_family != card.family()
            || self.capabilities != card.capabilities()
            || self.architecture_hash != card.architecture_hash()?
        {
            return Err(ArchitectureCardError::new(
                "architecture_record",
                "query columns or hash do not match the canonical card",
            ));
        }
        Ok(card)
    }
}

fn validate_plugin_id(plugin_id: &str) -> Result<(), ArchitectureCardError> {
    if plugin_id.is_empty()
        || !plugin_id
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
    {
        return Err(ArchitectureCardError::new(
            "external.plugin_id",
            "must use ASCII letters, digits, dot, underscore, or hyphen",
        ));
    }
    Ok(())
}

fn canonical_json_string(value: &Value) -> Result<String, ArchitectureCardError> {
    fn write(value: &Value, output: &mut String) -> Result<(), ArchitectureCardError> {
        match value {
            Value::Null => output.push_str("null"),
            Value::Bool(value) => output.push_str(if *value { "true" } else { "false" }),
            Value::Number(value) => output.push_str(&value.to_string()),
            Value::String(value) => output.push_str(&serde_json::to_string(value).map_err(|error| {
                ArchitectureCardError::new("external.config", format!("cannot encode string: {error}"))
            })?),
            Value::Array(values) => {
                output.push('[');
                for (index, value) in values.iter().enumerate() {
                    if index > 0 {
                        output.push(',');
                    }
                    write(value, output)?;
                }
                output.push(']');
            }
            Value::Object(values) => {
                output.push('{');
                let mut entries = values.iter().collect::<Vec<_>>();
                entries.sort_by(|left, right| left.0.cmp(right.0));
                for (index, (key, value)) in entries.into_iter().enumerate() {
                    if index > 0 {
                        output.push(',');
                    }
                    output.push_str(&serde_json::to_string(key).map_err(|error| {
                        ArchitectureCardError::new("external.config", format!("cannot encode key: {error}"))
                    })?);
                    output.push(':');
                    write(value, output)?;
                }
                output.push('}');
            }
        }
        Ok(())
    }

    let mut output = String::new();
    write(value, &mut output)?;
    Ok(output)
}

// ---------------------------------------------------------------------------
// Model Implementation Card V1
// ---------------------------------------------------------------------------

/// Wire schema for the first typed model-implementation declaration.
///
/// Architecture answers “what model structure was requested?”  Implementation
/// answers “which source, dependency lock, numerical policy and backend turn
/// that structure into executable arithmetic?”.  An observed GPU or Torch
/// version remains an Attempt receipt, not part of this declaration.
pub const MODEL_IMPLEMENTATION_CARD_SCHEMA: &str = "fern.model_implementation.v1";

macro_rules! implementation_hash_type {
    ($name:ident, $prefix:literal, $pattern:literal, $field:literal) => {
        #[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize, JsonSchema)]
        #[serde(try_from = "String", into = "String")]
        pub struct $name(#[schemars(regex(pattern = $pattern))] String);

        impl $name {
            pub fn parse(value: impl Into<String>) -> Result<Self, ImplementationCardError> {
                let value = value.into();
                let Some(hex) = value.strip_prefix($prefix) else {
                    return Err(ImplementationCardError::new(
                        $field,
                        concat!("must start with ", $prefix),
                    ));
                };
                if hex.len() != 64
                    || !hex
                        .bytes()
                        .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
                {
                    return Err(ImplementationCardError::new(
                        $field,
                        "must end with exactly 64 lowercase hexadecimal digits",
                    ));
                }
                Ok(Self(value))
            }

            pub fn as_str(&self) -> &str {
                &self.0
            }
        }

        impl TryFrom<String> for $name {
            type Error = ImplementationCardError;

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

implementation_hash_type!(
    ModelImplementationHash,
    "implementation:sha256:",
    "^implementation:sha256:[0-9a-f]{64}$",
    "implementation_hash"
);
// V2 identity of executable code, dependencies, protocol, and generated
// capabilities only. Experiment configuration is deliberately outside this
// hash so a control/treatment pair can hold code fixed.
implementation_hash_type!(
    ImplementationBundleHashV2,
    "implementation-bundle:sha256:",
    "^implementation-bundle:sha256:[0-9a-f]{64}$",
    "implementation_bundle_hash"
);
implementation_hash_type!(
    SourceBundleHash,
    "source-bundle:sha256:",
    "^source-bundle:sha256:[0-9a-f]{64}$",
    "source_bundle_hash"
);
implementation_hash_type!(
    DependencyLockHash,
    "dependency-lock:sha256:",
    "^dependency-lock:sha256:[0-9a-f]{64}$",
    "dependency_lock_hash"
);

impl ModelImplementationHash {
    fn from_preimage(bytes: &[u8]) -> Self {
        Self(format!("implementation:sha256:{:x}", Sha256::digest(bytes)))
    }
}

impl ImplementationBundleHashV2 {
    /// Hash a canonical V2 executable-bundle preimage.  The preimage must not
    /// contain Architecture, Action, Density, or Recipe configuration.
    pub fn from_content_bytes(bytes: &[u8]) -> Self {
        Self(format!("implementation-bundle:sha256:{:x}", Sha256::digest(bytes)))
    }
}

impl SourceBundleHash {
    /// Hash the canonical source-bundle manifest bytes.
    ///
    /// The caller is responsible for constructing that reviewed manifest; the
    /// live implementation registry does not yet accept ad-hoc filesystem
    /// walks as a source bundle.
    pub fn from_content_bytes(bytes: &[u8]) -> Self {
        Self(format!("source-bundle:sha256:{:x}", Sha256::digest(bytes)))
    }
}

impl DependencyLockHash {
    /// Hash the canonical dependency-lock manifest bytes.
    pub fn from_content_bytes(bytes: &[u8]) -> Self {
        Self(format!("dependency-lock:sha256:{:x}", Sha256::digest(bytes)))
    }
}

/// One invalid implementation field plus the reason it was refused.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ImplementationCardError {
    pub field: String,
    pub explanation: String,
}

impl ImplementationCardError {
    fn new(field: impl Into<String>, explanation: impl Into<String>) -> Self {
        Self {
            field: field.into(),
            explanation: explanation.into(),
        }
    }
}

impl std::fmt::Display for ImplementationCardError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "{}: {}", self.field, self.explanation)
    }
}

impl std::error::Error for ImplementationCardError {}

/// Current worker runtime family.
///
/// V1 intentionally models the inspected production boundary only.  Adding a
/// Rust-native or external runtime requires a new reviewed variant rather than
/// hiding it in a free-form string.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum ImplementationRuntimeV1 {
    PythonTorch,
}

/// Arithmetic path selected inside the current Python/Torch implementation.
///
/// `TritonHouseholder` is separate because the fused reduction can differ
/// numerically from eager Torch even for the same Fern architecture.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum NumericalBackendV1 {
    TorchEager,
    TritonHouseholder,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum NumericalDtypeV1 {
    Float16,
    Bfloat16,
    Float32,
    Float64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum Float32MatmulPrecisionV1 {
    Highest,
    High,
    Medium,
}

/// Numerical choices declared before execution.
///
/// The corresponding Torch values observed inside the worker remain in
/// `WorkerExecutionEnvironmentV1`; the two sides can therefore detect drift
/// instead of rewriting the declaration after launch.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RawNumericalPolicyV1 {
    pub parameter_dtype: NumericalDtypeV1,
    pub autocast_dtype: Option<NumericalDtypeV1>,
    pub float32_matmul_precision: Float32MatmulPrecisionV1,
    pub cuda_matmul_allow_tf32: bool,
    pub cudnn_allow_tf32: bool,
    pub deterministic_algorithms: bool,
    pub cudnn_deterministic: bool,
    pub cudnn_benchmark: bool,
}

/// JSON-facing implementation declaration.
///
/// Feature flags are names, not arbitrary JSON values. Canonicalization sorts
/// and deduplicates them so CLI order cannot change implementation identity.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RawModelImplementationCardV1 {
    pub schema_version: u32,
    pub architecture_hash: ModelArchitectureHash,
    pub source_bundle_hash: SourceBundleHash,
    pub dependency_lock_hash: DependencyLockHash,
    pub runtime: ImplementationRuntimeV1,
    pub numerical_backend: NumericalBackendV1,
    pub worker_protocol_version: u32,
    pub numerical_policy: RawNumericalPolicyV1,
    #[serde(default)]
    pub active_feature_flags: Vec<String>,
}

/// Validated immutable model implementation.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ModelImplementationCardV1 {
    schema: String,
    architecture_hash: ModelArchitectureHash,
    source_bundle_hash: SourceBundleHash,
    dependency_lock_hash: DependencyLockHash,
    runtime: ImplementationRuntimeV1,
    numerical_backend: NumericalBackendV1,
    worker_protocol_version: u32,
    numerical_policy: RawNumericalPolicyV1,
    active_feature_flags: Vec<String>,
}

impl ModelImplementationCardV1 {
    pub fn canonicalize(mut raw: RawModelImplementationCardV1) -> Result<Self, ImplementationCardError> {
        validate_implementation_raw(&raw)?;
        raw.active_feature_flags.sort();
        raw.active_feature_flags.dedup();
        Ok(Self {
            schema: MODEL_IMPLEMENTATION_CARD_SCHEMA.to_string(),
            architecture_hash: raw.architecture_hash,
            source_bundle_hash: raw.source_bundle_hash,
            dependency_lock_hash: raw.dependency_lock_hash,
            runtime: raw.runtime,
            numerical_backend: raw.numerical_backend,
            worker_protocol_version: raw.worker_protocol_version,
            numerical_policy: raw.numerical_policy,
            active_feature_flags: raw.active_feature_flags,
        })
    }

    fn validate(&self) -> Result<(), ImplementationCardError> {
        if self.schema != MODEL_IMPLEMENTATION_CARD_SCHEMA {
            return Err(ImplementationCardError::new(
                "implementation.schema",
                format!("expected {MODEL_IMPLEMENTATION_CARD_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        let normalized = Self::canonicalize(RawModelImplementationCardV1 {
            schema_version: 1,
            architecture_hash: self.architecture_hash.clone(),
            source_bundle_hash: self.source_bundle_hash.clone(),
            dependency_lock_hash: self.dependency_lock_hash.clone(),
            runtime: self.runtime,
            numerical_backend: self.numerical_backend,
            worker_protocol_version: self.worker_protocol_version,
            numerical_policy: self.numerical_policy.clone(),
            active_feature_flags: self.active_feature_flags.clone(),
        })?;
        if &normalized != self {
            return Err(ImplementationCardError::new(
                "implementation",
                "stored implementation card is not canonical",
            ));
        }
        Ok(())
    }

    pub fn architecture_hash(&self) -> &ModelArchitectureHash {
        &self.architecture_hash
    }

    pub const fn runtime(&self) -> ImplementationRuntimeV1 {
        self.runtime
    }

    pub const fn numerical_backend(&self) -> NumericalBackendV1 {
        self.numerical_backend
    }

    pub const fn worker_protocol_version(&self) -> u32 {
        self.worker_protocol_version
    }

    /// Numerical choices declared before worker launch.
    ///
    /// The artifact boundary compares the subset observable inside PyTorch
    /// against `WorkerExecutionEnvironmentV1`; returning the typed policy keeps
    /// that comparison out of JSON-string matching code.
    pub fn numerical_policy(&self) -> &RawNumericalPolicyV1 {
        &self.numerical_policy
    }

    pub fn canonical_json(&self) -> Result<String, ImplementationCardError> {
        self.validate()?;
        serde_json::to_string(self).map_err(|error| {
            ImplementationCardError::new(
                "implementation",
                format!("cannot serialize canonical implementation card: {error}"),
            )
        })
    }

    pub fn implementation_hash(&self) -> Result<ModelImplementationHash, ImplementationCardError> {
        let json = self.canonical_json()?;
        let mut preimage = Vec::with_capacity(MODEL_IMPLEMENTATION_CARD_SCHEMA.len() + 1 + json.len());
        preimage.extend_from_slice(MODEL_IMPLEMENTATION_CARD_SCHEMA.as_bytes());
        preimage.push(0);
        preimage.extend_from_slice(json.as_bytes());
        Ok(ModelImplementationHash::from_preimage(&preimage))
    }

    pub fn to_record(&self) -> Result<ModelImplementationRecordV1, ImplementationCardError> {
        Ok(ModelImplementationRecordV1 {
            implementation_hash: self.implementation_hash()?,
            architecture_hash: self.architecture_hash.clone(),
            card_schema_version: 1,
            canonical_card_json: self.canonical_json()?,
            source_bundle_hash: self.source_bundle_hash.clone(),
            dependency_lock_hash: self.dependency_lock_hash.clone(),
            runtime: self.runtime,
            numerical_backend: self.numerical_backend,
            worker_protocol_version: self.worker_protocol_version,
        })
    }
}

/// Storage/query projection. The full canonical card is always retained and
/// rechecked before any projection is trusted.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ModelImplementationRecordV1 {
    pub implementation_hash: ModelImplementationHash,
    pub architecture_hash: ModelArchitectureHash,
    pub card_schema_version: u32,
    pub canonical_card_json: String,
    pub source_bundle_hash: SourceBundleHash,
    pub dependency_lock_hash: DependencyLockHash,
    pub runtime: ImplementationRuntimeV1,
    pub numerical_backend: NumericalBackendV1,
    pub worker_protocol_version: u32,
}

impl ModelImplementationRecordV1 {
    pub fn verify(&self) -> Result<ModelImplementationCardV1, ImplementationCardError> {
        let card: ModelImplementationCardV1 = serde_json::from_str(&self.canonical_card_json).map_err(|error| {
            ImplementationCardError::new(
                "canonical_card_json",
                format!("cannot decode canonical implementation card: {error}"),
            )
        })?;
        let expected = card.to_record()?;
        if self != &expected {
            return Err(ImplementationCardError::new(
                "implementation_record",
                "query columns or hash do not match the canonical implementation card",
            ));
        }
        Ok(card)
    }
}

pub fn raw_model_implementation_json_schema() -> Result<Value, ImplementationCardError> {
    serde_json::to_value(schemars::schema_for!(RawModelImplementationCardV1)).map_err(|error| {
        ImplementationCardError::new(
            "implementation.schema",
            format!("cannot encode generated JSON Schema: {error}"),
        )
    })
}

fn validate_implementation_raw(raw: &RawModelImplementationCardV1) -> Result<(), ImplementationCardError> {
    if raw.schema_version != 1 {
        return Err(ImplementationCardError::new(
            "implementation.schema_version",
            format!("V1 requires schema_version=1, found {}", raw.schema_version),
        ));
    }
    if raw.worker_protocol_version == 0 {
        return Err(ImplementationCardError::new(
            "implementation.worker_protocol_version",
            "must be positive",
        ));
    }
    if matches!(
        raw.numerical_policy.autocast_dtype,
        Some(NumericalDtypeV1::Float32 | NumericalDtypeV1::Float64)
    ) {
        return Err(ImplementationCardError::new(
            "implementation.numerical_policy.autocast_dtype",
            "must be float16, bfloat16, or null",
        ));
    }
    for flag in &raw.active_feature_flags {
        if flag.is_empty()
            || flag.len() > 80
            || !flag
                .bytes()
                .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || matches!(byte, b'-' | b'_'))
        {
            return Err(ImplementationCardError::new(
                "implementation.active_feature_flags",
                format!("feature flag {flag:?} must use 1-80 lowercase ASCII letters, digits, hyphen, or underscore"),
            ));
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Owner ruling 2026-08-21: the implementation name is DATA, not a filename.
    #[test]
    fn analysis_declaration_carries_a_typed_implementation_name() {
        let with_name = serde_json::json!({
            "schema": "fern.declared_analysis.v1",
            "analysis_schema": "fern.quantile_forecast_summary.v1",
            "kind": "quantile_forecast_summary",
            "implementation": "python",
            "generator": "fern_analysis.quantile_forecast_summary_python",
            "implementation_sources": ["python/fern_analysis/quantile_forecast_summary_python.py"],
            "evidence": {"source": "artifacts", "paths": ["a.npz"]},
            "promised_outputs": [{"path": "out.json", "schema_id": null}],
        });
        let declared: AnalysisDeclarationV1 = serde_json::from_value(with_name).unwrap();
        assert_eq!(declared.implementation.as_deref(), Some("python"));
        declared.validate().unwrap();
        // It round-trips, so a re-serialized declaration keeps naming itself.
        let round_tripped: AnalysisDeclarationV1 =
            serde_json::from_value(serde_json::to_value(&declared).unwrap()).unwrap();
        assert_eq!(round_tripped, declared);

        // Absent is the single-implementation case and stays legal.
        let without = serde_json::json!({
            "schema": "fern.declared_analysis.v1",
            "analysis_schema": "fern.main_table.v1",
            "kind": "main_table",
            "generator": "fern_analysis.main_table",
            "implementation_sources": ["python/fern_analysis/main_table.py"],
            "evidence": {"source": "artifacts", "paths": ["a.npz"]},
            "promised_outputs": [{"path": "out.json", "schema_id": null}],
        });
        let plain: AnalysisDeclarationV1 = serde_json::from_value(without).unwrap();
        assert!(plain.implementation.is_none());
        plain.validate().unwrap();
        // An omitted name is not serialized back as null.
        assert!(!serde_json::to_string(&plain).unwrap().contains("implementation\":"));
    }

    /// A name that cannot be a filename suffix is refused when it is read.
    #[test]
    fn implementation_name_refuses_anything_that_is_not_a_filename_suffix() {
        AnalysisDeclarationV1::validate_implementation_name("rust").unwrap();
        AnalysisDeclarationV1::validate_implementation_name("python").unwrap();
        AnalysisDeclarationV1::validate_implementation_name("rust-f64").unwrap();
        AnalysisDeclarationV1::validate_implementation_name("v2").unwrap();

        for bad in ["", "Rust", "rust_f64", "rust.f64", "rust f64", "rüst", "rust/f64"] {
            let refusal = AnalysisDeclarationV1::validate_implementation_name(bad)
                .expect_err(&format!("{bad:?} must be refused"));
            assert!(!refusal.is_empty());
        }
        // A dot in particular, because it separates the two halves of the filename.
        assert!(
            AnalysisDeclarationV1::validate_implementation_name("a.b")
                .unwrap_err()
                .contains("lowercase letters, digits and hyphens")
        );
    }

    #[test]
    fn declared_replica_requires_a_positive_ordinal_and_has_a_bounded_reason() {
        let replica = DeclaredReplicaV1::new(1, DeclaredReplicaPurpose::ForecastOriginEvidence).unwrap();
        replica.validate().unwrap();
        assert_eq!(replica.purpose.value(), "forecast_origin_evidence");
        assert!(replica.purpose.reason().contains("per-forecast-origin"));
        assert_eq!(
            DeclaredReplicaPurpose::parse(replica.purpose.reason()).unwrap(),
            DeclaredReplicaPurpose::ForecastOriginEvidence
        );

        let error = DeclaredReplicaV1::new(0, DeclaredReplicaPurpose::ForecastOriginEvidence).unwrap_err();
        assert!(error.contains("0 is the primary execution"));
        assert!(
            DeclaredReplicaPurpose::parse("anything")
                .unwrap_err()
                .contains("Valid:")
        );
    }

    fn raw_fern() -> RawFernArchitectureV1 {
        RawFernArchitectureV1 {
            schema_version: 1,
            architecture_variant: FernArchitectureVariantV1::Current,
            shape: RawArchitectureShapeV1 {
                context_length: 336,
                horizon_length: 64,
                channel_contract: RawArchitectureChannelContractV1::RuntimePositive,
            },
            hidden_width: 128,
            augmented_width: 128,
            block_size: 24,
            density_variant: FernDensityVariantV1::SingleGaussian,
            mixture_component_count: 1,
            sparse_active_component_count: None,
            mixture_relative_std_floor_ppm: None,
            operator: RawFernOperatorV1::Householder,
            householder_reflection_count: 24,
            coupling_patch: RawFernPatchV1 {
                length: 64,
                patch_length: 64,
                group_count: 1,
            },
            brenier_patch: RawFernPatchV1 {
                length: 64,
                patch_length: 32,
                group_count: 2,
            },
            factory_schemas: RawFernFactorySchemasV1::Default,
            reversible_instance_norm: RawReversibleInstanceNormV1::default(),
        }
    }

    /// The architecture and implementation identities C# must reproduce.
    ///
    /// Written from the Rust while it still runs, because a port checked
    /// against values the porter chose has been checked against nothing.
    /// Requested by the C# translator, which reported that no committed
    /// artifact anywhere in docs/, crates/ or paper/ carries one concrete
    /// `architecture:sha256:` or `implementation:sha256:` value -- so every
    /// ported test was self-consistent and none could catch a C# writer whose
    /// bytes differ from serde's.
    ///
    /// What this pins is the convention, which is the one C# is least likely
    /// to get right by default, and which is NOT the stage-plan convention:
    ///
    ///   stage plan     canonical_json_bytes    keys SORTED
    ///   these two      serde_json::to_string   keys in DECLARATION order,
    ///                                          absent optionals written null
    ///
    /// Both are sha256 over `schema \0 json`. A port that picks one rule and
    /// applies it to both families -- the tidy thing to do -- renames one of
    /// them, and both sides stay valid sha256 over valid JSON, so nothing
    /// raises.
    #[test]
    fn write_the_card_identities_the_csharp_port_must_reproduce() {
        let fern = ModelArchitectureCardV1::canonicalize_builtin(RawModelArchitectureCardV1::Fern(raw_fern()))
            .unwrap();

        let dlinear = ModelArchitectureCardV1::canonicalize_builtin(RawModelArchitectureCardV1::DLinear(
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

        let external = ModelArchitectureCardV1::canonicalize_external(
            RawExternalArchitectureV1 {
                plugin_id: "example.forecaster".to_string(),
                plugin_schema_version: 3,
                plugin_schema_hash: test_plugin_schema_hash(),
                config: serde_json::json!({"width": 128, "depth": 4}),
            },
            &TestPlugin,
        )
        .unwrap();

        let implementation =
            ModelImplementationCardV1::canonicalize(raw_implementation()).unwrap();

        let golden = serde_json::json!({
            "schema": "fern.card_identity_golden.v1",
            "note": "hash = sha256 over `card_schema \\0 canonical_json`; canonical_json is \
                     serde_json::to_string -- keys in DECLARATION order, absent optionals \
                     written as null. This is NOT the sorted-key rule stage_plan uses.",
            "architecture_card_schema": MODEL_ARCHITECTURE_CARD_SCHEMA,
            "implementation_card_schema": MODEL_IMPLEMENTATION_CARD_SCHEMA,
            "architectures": {
                "fern_current_single_gaussian": {
                    "architecture_hash": fern.architecture_hash().unwrap().as_str().to_string(),
                    "canonical_json": fern.canonical_json().unwrap(),
                },
                "dlinear_shared_runtime_positive": {
                    "architecture_hash": dlinear.architecture_hash().unwrap().as_str().to_string(),
                    "canonical_json": dlinear.canonical_json().unwrap(),
                },
                "external_example_forecaster": {
                    "architecture_hash": external.architecture_hash().unwrap().as_str().to_string(),
                    "canonical_json": external.canonical_json().unwrap(),
                },
            },
            "implementations": {
                "python_torch_over_fern_current": {
                    "implementation_hash": implementation.implementation_hash().unwrap().as_str().to_string(),
                    "canonical_json": implementation.canonical_json().unwrap(),
                },
            },
        });

        let path =
            std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("fixtures/card_identity_golden.json");
        std::fs::write(&path, serde_json::to_string_pretty(&golden).unwrap() + "\n").unwrap();

        // The canonical JSON is written out beside the hash so a C# failure
        // says WHICH byte differs, not merely that two digests disagree.
        let written = std::fs::read_to_string(&path).unwrap();
        assert!(written.contains("architecture:sha256:"), "golden did not round-trip");
        assert!(written.contains("implementation:sha256:"), "golden did not round-trip");
        assert!(
            written.contains("\"autocast_dtype\": null") || written.contains("autocast_dtype\\\":null"),
            "the null-writing convention must be visible in the golden, since it is the trap"
        );
    }

    fn raw_implementation() -> RawModelImplementationCardV1 {
        let architecture =
            ModelArchitectureCardV1::canonicalize_builtin(RawModelArchitectureCardV1::Fern(raw_fern())).unwrap();
        RawModelImplementationCardV1 {
            schema_version: 1,
            architecture_hash: architecture.architecture_hash().unwrap(),
            source_bundle_hash: SourceBundleHash::from_content_bytes(b"reviewed Rust and Python source bundle"),
            dependency_lock_hash: DependencyLockHash::from_content_bytes(b"Cargo.lock plus Python lock"),
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
            active_feature_flags: vec!["paper-worker".to_string(), "fern-v1".to_string()],
        }
    }

    struct TestPlugin;

    impl ModelArchitecturePlugin for TestPlugin {
        fn plugin_id(&self) -> &str {
            "example.forecaster"
        }

        fn schema_hash(&self, schema_version: u32) -> Result<ArchitecturePluginSchemaHash, ArchitectureCardError> {
            if schema_version != 3 {
                return Err(ArchitectureCardError::new("plugin.schema_version", "expected 3"));
            }
            Ok(test_plugin_schema_hash())
        }

        fn canonicalize(
            &self,
            schema_version: u32,
            raw_config: &Value,
        ) -> Result<CanonicalExternalArchitectureV1, ArchitectureCardError> {
            if schema_version != 3 {
                return Err(ArchitectureCardError::new("plugin.schema_version", "expected 3"));
            }
            CanonicalExternalArchitectureV1::new(
                self.plugin_id(),
                schema_version,
                self.schema_hash(schema_version)?,
                raw_config,
                ArchitectureCapabilitiesV1 {
                    training: ArchitectureTrainingCapabilityV1::FixedWeights,
                    native_output: ArchitectureNativeOutputV1::Quantiles,
                    has_native_point_head: false,
                    supports_channel_independence: true,
                },
            )
        }
    }

    fn test_plugin_schema_hash() -> ArchitecturePluginSchemaHash {
        ArchitecturePluginSchemaHash::from_schema_bytes(br#"{"depth":"u64","width":"u64"}"#)
    }

    #[test]
    fn error_receipt_keeps_the_declared_code_in_json() {
        let receipt = ErrorReceipt::new(
            ErrorCode::MissingPrerequisite,
            ErrorStage::Preflight,
            "Cannot start this experiment.",
            "The recorded-data assets are missing on local-4070.",
            WorkStartState::NotStarted,
            RetryPolicy::SameProfile,
        );

        let json = serde_json::to_value(&receipt).unwrap();
        assert_eq!(json["schema"], ERROR_RECEIPT_SCHEMA);
        assert_eq!(json["code"], "missing_prerequisite");
        assert_eq!(json["stage"], "preflight");
        assert_eq!(json["work_started"], "not_started");
    }

    #[test]
    fn seven_error_codes_have_stable_wire_names() {
        let codes = [
            ErrorCode::InvalidRequest,
            ErrorCode::MissingPrerequisite,
            ErrorCode::OwnershipConflict,
            ErrorCode::CapacityBlocked,
            ErrorCode::WorkerFailed,
            ErrorCode::ArtifactInvalid,
            ErrorCode::EvidenceRefused,
        ];

        let wire_names = codes
            .into_iter()
            .map(|code| serde_json::to_value(code).unwrap().as_str().unwrap().to_string())
            .collect::<Vec<_>>();
        assert_eq!(
            wire_names,
            [
                "invalid_request",
                "missing_prerequisite",
                "ownership_conflict",
                "capacity_blocked",
                "worker_failed",
                "artifact_invalid",
                "evidence_refused",
            ]
        );
    }

    #[test]
    fn text_rendering_explains_the_fix_without_claiming_work_started() {
        let mut receipt = ErrorReceipt::new(
            ErrorCode::MissingPrerequisite,
            ErrorStage::Preflight,
            "Cannot start this experiment.",
            "The recorded-data assets are missing on local-4070.",
            WorkStartState::NotStarted,
            RetryPolicy::SameProfile,
        );
        receipt.expected = Some("/home/user/.cache/serie/known".to_string());
        receipt.observed = Some("directory is absent".to_string());
        receipt.fix_command = Some(vec![
            "fern-run".to_string(),
            "assets".to_string(),
            "stage".to_string(),
            "hugadb".to_string(),
            "--on".to_string(),
            "local-4070".to_string(),
        ]);
        receipt.retry_command = Some(vec![
            "fern-run".to_string(),
            "run".to_string(),
            "hugadb-natural".to_string(),
            "--model".to_string(),
            "timemixer".to_string(),
            "--canary".to_string(),
        ]);

        assert_eq!(
            receipt.render_text(),
            "Cannot start this experiment.\n\nWhat happened:\n  The recorded-data assets are missing on local-4070.\n\nWork started:\n  No. Fern blocked the request before work began.\n\nWhy:\n  Expected: /home/user/.cache/serie/known\n  Observed: directory is absent\n\nFix:\n  fern-run assets stage hugadb --on local-4070\n\nThen retry:\n  fern-run run hugadb-natural --model timemixer --canary"
        );
    }

    #[test]
    fn retry_policy_keeps_unknown_external_work_from_being_a_false_safe_retry() {
        assert!(RetryPolicy::SameProfile.is_retryable());
        assert!(!RetryPolicy::RequiresHuman.is_retryable());
        assert_eq!(
            WorkStartState::Unknown.human_text(),
            "Unknown. Fern could not verify the external worker state."
        );
    }

    #[test]
    fn architecture_json_rejects_a_misspelled_field_before_canonicalization() {
        let mut value = serde_json::to_value(RawModelArchitectureCardV1::Fern(raw_fern())).unwrap();
        let architecture = value["architecture"].as_object_mut().unwrap();
        let hidden = architecture.remove("hidden_width").unwrap();
        architecture.insert("hidden_wdith".to_string(), hidden);
        let error = serde_json::from_value::<RawModelArchitectureCardV1>(value)
            .unwrap_err()
            .to_string();
        assert!(error.contains("unknown field `hidden_wdith`"), "got: {error}");
    }

    #[test]
    fn fern_defaults_expand_before_hash_and_one_real_knob_changes_identity() {
        let explicit =
            ModelArchitectureCardV1::canonicalize_builtin(RawModelArchitectureCardV1::Fern(raw_fern())).unwrap();
        let mut json = serde_json::to_value(RawModelArchitectureCardV1::Fern(raw_fern())).unwrap();
        json["architecture"]
            .as_object_mut()
            .unwrap()
            .remove("reversible_instance_norm");
        let raw_without_default: RawModelArchitectureCardV1 = serde_json::from_value(json).unwrap();
        let defaulted = ModelArchitectureCardV1::canonicalize_builtin(raw_without_default).unwrap();
        assert_eq!(
            explicit.architecture_hash().unwrap(),
            defaulted.architecture_hash().unwrap()
        );

        let mut changed = raw_fern();
        changed.hidden_width = 256;
        let changed = ModelArchitectureCardV1::canonicalize_builtin(RawModelArchitectureCardV1::Fern(changed)).unwrap();
        assert_ne!(
            explicit.architecture_hash().unwrap(),
            changed.architecture_hash().unwrap()
        );

        let mut pinned = raw_fern();
        pinned.architecture_variant = FernArchitectureVariantV1::IcmlPinned;
        let pinned = ModelArchitectureCardV1::canonicalize_builtin(RawModelArchitectureCardV1::Fern(pinned)).unwrap();
        assert_ne!(
            explicit.architecture_hash().unwrap(),
            pinned.architecture_hash().unwrap(),
            "current and ICML-pinned Fern structures must never pool"
        );
        assert_eq!(
            explicit.capabilities().native_output,
            ArchitectureNativeOutputV1::PredictiveSamples
        );
        assert!(!explicit.capabilities().has_native_point_head);
        let canonical_json = explicit.canonical_json().unwrap();
        assert!(canonical_json.contains("all_complex_unclamped_wide"));
        assert!(!canonical_json.contains("\"current\""));
        assert!(canonical_json.contains("\"factory_schemas\":\"v1\""));
        assert!(!canonical_json.contains("\"factory_schemas\":\"default\""));
    }

    #[test]
    fn architecture_cross_field_rules_fail_before_a_run_can_be_planned() {
        let mut invalid = raw_fern();
        invalid.householder_reflection_count = invalid.hidden_width + 1;
        assert!(
            ModelArchitectureCardV1::canonicalize_builtin(RawModelArchitectureCardV1::Fern(invalid))
                .unwrap_err()
                .to_string()
                .contains("cannot exceed hidden_width")
        );

        let invalid_dlinear = RawDLinearArchitectureV1 {
            schema_version: 1,
            shape: RawArchitectureShapeV1 {
                context_length: 336,
                horizon_length: 64,
                channel_contract: RawArchitectureChannelContractV1::RuntimePositive,
            },
            moving_average_window: 24,
            individual_per_channel: false,
            stride: 1,
        };
        assert!(
            ModelArchitectureCardV1::canonicalize_builtin(RawModelArchitectureCardV1::DLinear(invalid_dlinear))
                .unwrap_err()
                .to_string()
                .contains("positive odd")
        );

        let channel_bound_dlinear = RawDLinearArchitectureV1 {
            schema_version: 1,
            shape: RawArchitectureShapeV1 {
                context_length: 336,
                horizon_length: 64,
                channel_contract: RawArchitectureChannelContractV1::RuntimePositive,
            },
            moving_average_window: 25,
            individual_per_channel: true,
            stride: 1,
        };
        assert!(
            ModelArchitectureCardV1::canonicalize_builtin(RawModelArchitectureCardV1::DLinear(channel_bound_dlinear))
                .unwrap_err()
                .to_string()
                .contains("requires an exact channel count")
        );

        let mut channel_bound_fern = raw_fern();
        channel_bound_fern.shape.channel_contract = RawArchitectureChannelContractV1::Exact { count: 3 };
        assert!(
            ModelArchitectureCardV1::canonicalize_builtin(RawModelArchitectureCardV1::Fern(channel_bound_fern))
                .unwrap_err()
                .to_string()
                .contains("requires runtime_positive")
        );

        let shared_but_channel_bound = RawDLinearArchitectureV1 {
            schema_version: 1,
            shape: RawArchitectureShapeV1 {
                context_length: 336,
                horizon_length: 64,
                channel_contract: RawArchitectureChannelContractV1::Exact { count: 3 },
            },
            moving_average_window: 25,
            individual_per_channel: false,
            stride: 1,
        };
        assert!(
            ModelArchitectureCardV1::canonicalize_builtin(RawModelArchitectureCardV1::DLinear(
                shared_but_channel_bound
            ))
            .unwrap_err()
            .to_string()
            .contains("shared-weight DLinear requires runtime_positive")
        );
    }

    #[test]
    fn external_json_order_does_not_change_the_plugin_architecture_hash() {
        let first = RawExternalArchitectureV1 {
            plugin_id: "example.forecaster".to_string(),
            plugin_schema_version: 3,
            plugin_schema_hash: test_plugin_schema_hash(),
            config: serde_json::json!({"width": 128, "depth": 4}),
        };
        let second = RawExternalArchitectureV1 {
            plugin_id: "example.forecaster".to_string(),
            plugin_schema_version: 3,
            plugin_schema_hash: test_plugin_schema_hash(),
            config: serde_json::json!({"depth": 4, "width": 128}),
        };
        let first = ModelArchitectureCardV1::canonicalize_external(first, &TestPlugin).unwrap();
        let second = ModelArchitectureCardV1::canonicalize_external(second, &TestPlugin).unwrap();
        assert_eq!(first.architecture_hash().unwrap(), second.architecture_hash().unwrap());
        assert_eq!(first.canonical_json().unwrap(), second.canonical_json().unwrap());
        first.revalidate_external_with_plugin(&TestPlugin).unwrap();

        let error = ModelArchitectureCardV1::canonicalize_external(
            RawExternalArchitectureV1 {
                plugin_id: "other.plugin".to_string(),
                plugin_schema_version: 3,
                plugin_schema_hash: test_plugin_schema_hash(),
                config: serde_json::json!({}),
            },
            &TestPlugin,
        )
        .unwrap_err()
        .to_string();
        assert!(error.contains("card requests"), "got: {error}");

        let error = ModelArchitectureCardV1::canonicalize_external(
            RawExternalArchitectureV1 {
                plugin_id: "example.forecaster".to_string(),
                plugin_schema_version: 3,
                plugin_schema_hash: ArchitecturePluginSchemaHash::from_schema_bytes(b"different schema"),
                config: serde_json::json!({"width": 128, "depth": 4}),
            },
            &TestPlugin,
        )
        .unwrap_err()
        .to_string();
        assert!(error.contains("registered plugin schema"), "got: {error}");
    }

    #[test]
    fn stored_external_card_requires_the_same_plugin_before_planning() {
        struct DriftedPlugin;

        impl ModelArchitecturePlugin for DriftedPlugin {
            fn plugin_id(&self) -> &str {
                "example.forecaster"
            }

            fn schema_hash(&self, schema_version: u32) -> Result<ArchitecturePluginSchemaHash, ArchitectureCardError> {
                if schema_version != 3 {
                    return Err(ArchitectureCardError::new("plugin.schema_version", "expected 3"));
                }
                Ok(ArchitecturePluginSchemaHash::from_schema_bytes(b"drifted schema"))
            }

            fn canonicalize(
                &self,
                schema_version: u32,
                raw_config: &Value,
            ) -> Result<CanonicalExternalArchitectureV1, ArchitectureCardError> {
                CanonicalExternalArchitectureV1::new(
                    self.plugin_id(),
                    schema_version,
                    self.schema_hash(schema_version)?,
                    raw_config,
                    ArchitectureCapabilitiesV1 {
                        training: ArchitectureTrainingCapabilityV1::FixedWeights,
                        native_output: ArchitectureNativeOutputV1::Quantiles,
                        has_native_point_head: false,
                        supports_channel_independence: true,
                    },
                )
            }
        }

        let card = ModelArchitectureCardV1::canonicalize_external(
            RawExternalArchitectureV1 {
                plugin_id: "example.forecaster".to_string(),
                plugin_schema_version: 3,
                plugin_schema_hash: test_plugin_schema_hash(),
                config: serde_json::json!({"width": 128, "depth": 4}),
            },
            &TestPlugin,
        )
        .unwrap();
        let error = card
            .revalidate_external_with_plugin(&DriftedPlugin)
            .unwrap_err()
            .to_string();
        assert!(error.contains("schema digest does not match"), "got: {error}");

        let built_in =
            ModelArchitectureCardV1::canonicalize_builtin(RawModelArchitectureCardV1::Fern(raw_fern())).unwrap();
        assert!(
            built_in
                .revalidate_external_with_plugin(&TestPlugin)
                .unwrap_err()
                .to_string()
                .contains("built-in architecture")
        );
    }

    #[test]
    fn external_canonical_json_preserves_array_order_and_rejects_impossible_capabilities() {
        assert_eq!(
            canonical_json_string(&serde_json::json!({
                "values": [1, 2, {"z": 3, "a": 4}]
            }))
            .unwrap(),
            r#"{"values":[1,2,{"a":4,"z":3}]}"#
        );

        let error = CanonicalExternalArchitectureV1::new(
            "example.forecaster",
            1,
            ArchitecturePluginSchemaHash::from_schema_bytes(b"{}"),
            &serde_json::json!({}),
            ArchitectureCapabilitiesV1 {
                training: ArchitectureTrainingCapabilityV1::FixedWeights,
                native_output: ArchitectureNativeOutputV1::Point,
                has_native_point_head: false,
                supports_channel_independence: true,
            },
        )
        .unwrap_err()
        .to_string();
        assert!(error.contains("must be true"), "got: {error}");
    }

    #[test]
    fn architecture_record_cannot_drift_from_its_full_canonical_card() {
        let card = ModelArchitectureCardV1::canonicalize_builtin(RawModelArchitectureCardV1::Fern(raw_fern())).unwrap();
        let record = card.to_record().unwrap();
        assert_eq!(record.verify().unwrap(), card);

        let mut drifted = record;
        drifted.model_family = "dlinear".to_string();
        assert!(drifted.verify().unwrap_err().to_string().contains("do not match"));
    }

    #[test]
    fn stored_canonical_json_cannot_bypass_architecture_validation() {
        let card = ModelArchitectureCardV1::canonicalize_builtin(RawModelArchitectureCardV1::Fern(raw_fern())).unwrap();
        let mut record = card.to_record().unwrap();
        let mut json: Value = serde_json::from_str(&record.canonical_card_json).unwrap();
        json["body"]["architecture"]["hidden_width"] = serde_json::json!(0);
        record.canonical_card_json = serde_json::to_string(&json).unwrap();

        let error = record.verify().unwrap_err().to_string();
        assert!(error.contains("must be positive"), "got: {error}");
    }

    #[test]
    fn generated_raw_architecture_schema_is_golden() {
        let schema = raw_model_architecture_json_schema().unwrap();
        let schema_text = serde_json::to_string(&schema).unwrap();
        assert!(schema_text.contains("plugin_schema_hash"));
        assert!(schema_text.contains("architecture-schema:sha256:"));
        let bytes = serde_json::to_vec(&schema).unwrap();
        let observed = format!("sha256:{:x}", Sha256::digest(bytes));
        assert_eq!(
            observed,
            // Changed 2026-08-07: raw `current` now resolves to the explicit
            // all-complex/unclamped/wide canonical architecture before hash.
            // Changed 2026-08-17: `current_wide_scale` joined the registered
            // architectures. It is a NEW name beside the existing ones and
            // resolves to nothing else, so no already-canonicalized card's own
            // hash moves; only this schema, which enumerates the vocabulary,
            // does. Verified by the neighbouring identity tests still passing.
            // Changed 2026-08-21: `mixture_source_shared_hidden` joined the
            // registered densities. Same shape as the 2026-08-17 entry above --
            // a NEW name beside the existing ones, resolving to nothing else --
            // so only this schema, which enumerates the vocabulary, moves. The
            // neighbouring identity tests pin already-canonicalized cards and
            // still pass, which is what says no recorded architecture hash moved.
            // Changed 2026-08-21, same commit: `mixture_fern_transports_shared_hidden`,
            // the rung above it, joined too. Same reasoning, one more name.
            "sha256:042f18f1b754efec7f9d4a5c8d08ee93570ee09e3962b551b28dee7b7440c864"
        );
    }

    #[test]
    fn implementation_card_canonicalizes_feature_order_and_tracks_numerical_changes() {
        let first = ModelImplementationCardV1::canonicalize(raw_implementation()).unwrap();
        let mut reordered = raw_implementation();
        reordered.active_feature_flags = vec!["fern-v1".to_string(), "paper-worker".to_string(), "fern-v1".to_string()];
        let reordered = ModelImplementationCardV1::canonicalize(reordered).unwrap();
        assert_eq!(
            first.implementation_hash().unwrap(),
            reordered.implementation_hash().unwrap()
        );

        let mut triton = raw_implementation();
        triton.numerical_backend = NumericalBackendV1::TritonHouseholder;
        let triton = ModelImplementationCardV1::canonicalize(triton).unwrap();
        assert_ne!(
            first.implementation_hash().unwrap(),
            triton.implementation_hash().unwrap()
        );

        let mut tf32_disabled = raw_implementation();
        tf32_disabled.numerical_policy.cuda_matmul_allow_tf32 = false;
        let tf32_disabled = ModelImplementationCardV1::canonicalize(tf32_disabled).unwrap();
        assert_ne!(
            first.implementation_hash().unwrap(),
            tf32_disabled.implementation_hash().unwrap()
        );
    }

    #[test]
    fn implementation_card_rejects_unknown_or_invalid_numerical_declarations() {
        let mut value = serde_json::to_value(raw_implementation()).unwrap();
        let object = value.as_object_mut().unwrap();
        let version = object.remove("worker_protocol_version").unwrap();
        object.insert("worker_protocol_vesion".to_string(), version);
        assert!(
            serde_json::from_value::<RawModelImplementationCardV1>(value)
                .unwrap_err()
                .to_string()
                .contains("worker_protocol_vesion")
        );

        let mut invalid = raw_implementation();
        invalid.worker_protocol_version = 0;
        assert!(
            ModelImplementationCardV1::canonicalize(invalid)
                .unwrap_err()
                .to_string()
                .contains("must be positive")
        );

        let mut invalid = raw_implementation();
        invalid.numerical_policy.autocast_dtype = Some(NumericalDtypeV1::Float32);
        assert!(
            ModelImplementationCardV1::canonicalize(invalid)
                .unwrap_err()
                .to_string()
                .contains("float16, bfloat16, or null")
        );
    }

    #[test]
    fn implementation_record_cannot_drift_from_its_full_card() {
        let card = ModelImplementationCardV1::canonicalize(raw_implementation()).unwrap();
        let record = card.to_record().unwrap();
        assert_eq!(record.verify().unwrap(), card);

        let mut drifted = record;
        drifted.worker_protocol_version += 1;
        assert!(drifted.verify().unwrap_err().to_string().contains("do not match"));
    }

    #[test]
    fn stored_implementation_json_cannot_bypass_normalization() {
        let card = ModelImplementationCardV1::canonicalize(raw_implementation()).unwrap();
        let mut record = card.to_record().unwrap();
        let mut json: Value = serde_json::from_str(&record.canonical_card_json).unwrap();
        json["active_feature_flags"] = serde_json::json!(["paper-worker", "fern-v1"]);
        record.canonical_card_json = serde_json::to_string(&json).unwrap();
        assert!(record.verify().unwrap_err().to_string().contains("not canonical"));
    }

    #[test]
    fn generated_raw_implementation_schema_is_golden() {
        let schema = raw_model_implementation_json_schema().unwrap();
        let schema_text = serde_json::to_string(&schema).unwrap();
        assert!(schema_text.contains("architecture:sha256"));
        assert!(schema_text.contains("triton_householder"));
        let bytes = serde_json::to_vec(&schema).unwrap();
        let observed = format!("sha256:{:x}", Sha256::digest(bytes));
        assert_eq!(
            observed,
            "sha256:9b313091b57ea5a0b61f05b13db2cf0b7b96f92db65223b04242d10cc57c4c42"
        );
    }
}

/// Mirrors Python `fr_tr_exp.Experiment.noise_kind` literals.
/// Every non-Gaussian law is variance-matched to one: Laplace uses scale
/// 1/sqrt(2), StudentT3 multiplies by sqrt((nu-2)/nu) = sqrt(1/3), so
/// relative doses stay train-std-scale interventions under every law.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema, strum::EnumIter)]
#[serde(rename_all = "snake_case")]
pub enum NoiseKind {
    Gaussian,
    Laplace,
    StudentT3,
    /// Huber's gross-error model: 98% of timestamps carry an ordinary
    /// Gaussian reading and 2% carry one ten times larger, the whole draw
    /// rescaled to unit variance. It is NOT another rung of the tail ladder.
    /// Laplace and Student-t3 make every reading heavier-tailed; contamination
    /// leaves almost every reading clean and makes a few isolated ones
    /// enormous, which is a different failure channel -- whether one outlier
    /// can capture a context window, rather than whether a diffuse tail can be
    /// integrated away. Its fourth moment is finite (kurtosis about 68), so
    /// unlike Student-t3 its squared-error confidence intervals are stable.
    Contaminated,
}

impl NoiseKind {
    /// The serde/wire string, identical to the serie `noise_kind` vocabulary.
    pub fn value(self) -> &'static str {
        match self {
            Self::Gaussian => "gaussian",
            Self::Laplace => "laplace",
            Self::StudentT3 => "student_t3",
            Self::Contaminated => "contaminated",
        }
    }

    pub fn parse(raw: &str) -> Result<Self, String> {
        use strum::IntoEnumIterator;
        Self::iter().find(|kind| kind.value() == raw).ok_or_else(|| {
            let valid = Self::iter().map(|kind| kind.value()).collect::<Vec<_>>().join(", ");
            format!("unknown noise kind {raw:?}; valid: {valid}")
        })
    }
}
