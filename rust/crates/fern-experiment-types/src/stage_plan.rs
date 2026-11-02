//! Content-addressed Stage Plan V1: stages and artifact dependencies, not execution.
//!
//! A plan names an ordered topology of stages. Dependencies are only the
//! `stage_output` input bindings. There is no parallel `next` / `needs` list.
//! The `stages` array is topological order: a binding may name only an earlier
//! stage and an output that stage actually declares.
//!
//! `StageSpecV1::digest` covers that stage's own declaration: handler,
//! implementation, parameters, and ports. Input and output ports are
//! declarations only. Resolved artifact identity belongs in `fern-artifact`
//! invocation, result, and receipt contracts. Changing a downstream
//! implementation therefore changes that stage and the plan, and leaves an
//! upstream train digest untouched so those results can be reused.

use std::collections::{BTreeMap, BTreeSet};

use fern_json_contract::JsonContract;
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};

/// Wire schema for one content-addressed stage plan.
pub const STAGE_PLAN_SCHEMA: &str = "fern.stage_plan.v1";
/// Wire schema for an implementation-reported Stage pipeline template.
pub const STAGE_PIPELINE_TEMPLATE_SCHEMA: &str = "fern.stage_pipeline_template.v1";

const SHA256_PREFIX: &str = "sha256:";
const SHA256_HEX_LEN: usize = 64;

/// One invalid stage-plan field and the reason it was refused.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StagePlanError {
    pub field: String,
    pub explanation: String,
}

impl StagePlanError {
    fn new(field: impl Into<String>, explanation: impl Into<String>) -> Self {
        Self {
            field: field.into(),
            explanation: explanation.into(),
        }
    }
}

impl std::fmt::Display for StagePlanError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "{}: {}", self.field, self.explanation)
    }
}

impl std::error::Error for StagePlanError {}

impl From<fern_json_contract::JsonContractError> for StagePlanError {
    fn from(error: fern_json_contract::JsonContractError) -> Self {
        Self::new("stage_plan", error.to_string())
    }
}

/// SHA-256 digest in the Fern wire form `sha256:` plus 64 lowercase hex digits.
///
/// Used for plan, stage, subject, and implementation hashes. Artifact byte and
/// content identities live in `fern-artifact`. The type lives here so this crate
/// does not depend on `fern-artifact` (that crate already depends on this one).
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize, JsonSchema)]
#[serde(try_from = "String", into = "String")]
pub struct Sha256Digest(#[schemars(regex(pattern = "^sha256:[0-9a-f]{64}$"))] String);

impl Sha256Digest {
    pub fn from_bytes(bytes: &[u8]) -> Self {
        Self(format!("{SHA256_PREFIX}{:x}", Sha256::digest(bytes)))
    }

    pub fn parse(value: impl Into<String>) -> Result<Self, StagePlanError> {
        let value = value.into();
        validate_sha256_text("sha256", &value)?;
        Ok(Self(value))
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl TryFrom<String> for Sha256Digest {
    type Error = StagePlanError;

    fn try_from(value: String) -> Result<Self, Self::Error> {
        Self::parse(value)
    }
}

impl From<Sha256Digest> for String {
    fn from(value: Sha256Digest) -> Self {
        value.0
    }
}

fn validate_sha256_text(field: &str, value: &str) -> Result<(), StagePlanError> {
    let Some(hex) = value.strip_prefix(SHA256_PREFIX) else {
        return Err(StagePlanError::new(field, "must start with sha256:"));
    };
    if hex.len() != SHA256_HEX_LEN
        || !hex
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return Err(StagePlanError::new(
            field,
            "must contain exactly 64 lowercase hexadecimal digits",
        ));
    }
    Ok(())
}

/// The content-addressed subject a plan is about, typically a compiled experiment.
///
/// `kind` is a validated open name, not a Rust enum of experiments.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ContentAddressedSubjectV1 {
    pub kind: String,
    pub identity: String,
    pub content_digest: Sha256Digest,
}

/// How a stage is invoked. Infrastructure, not the scientific handler name.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum StageRunnerV1 {
    RustBinary,
    PythonModule,
}

/// Exact binary or source-plus-lock bundle that implements a stage.
///
/// `entrypoint` is a binary or Python module name, never a filesystem path.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StageImplementationRefV1 {
    pub runner: StageRunnerV1,
    pub entrypoint: String,
    pub implementation_digest: Sha256Digest,
}

/// Where a stage input is bound.
///
/// An external input is a declared port, not a resolved artifact identity.
/// Byte and content digests are bound later by a checked invocation.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum StageInputSourceV1 {
    ExternalArtifact,
    StageOutput { stage_id: String, output: String },
}

/// Whether the consumer needs the artifact bytes or only its identity.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum MaterializationRequirementV1 {
    BytesRequired,
    IdentityOnly,
}

/// One named input port on a stage.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StageInputSpecV1 {
    pub source: StageInputSourceV1,
    pub materialization_requirement: MaterializationRequirementV1,
    pub role: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub schema_id: Option<String>,
}

/// One named output slot. Its digest does not exist until the stage runs.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StageOutputSpecV1 {
    pub role: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub schema_id: Option<String>,
}

/// A Plan-level reason to retain a stage output that no later stage consumes.
///
/// Every stage output must be a downstream input or an export. `identity_only`
/// keeps the digest after bytes may go; `bytes_required` keeps the file.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StagePlanExportV1 {
    pub stage_id: String,
    pub output: String,
    pub materialization_requirement: MaterializationRequirementV1,
}

/// How many times one stage may be attempted before its node blocks.
///
/// This is execution policy, so it lives in the plan-level `retry` map and
/// never inside `StageSpecV1`: a different budget changes the plan digest but
/// not a stage digest or execution key, so a checked stage result stays
/// reusable across plans with different budgets. A stage absent from the map
/// has one total attempt, which is what every plan registered before the map
/// existed declared by silence.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StageRetrySpecV1 {
    pub max_total_attempts: u32,
}

/// One total attempt: the budget of every stage that declares nothing.
pub const DEFAULT_STAGE_RETRY: StageRetrySpecV1 = StageRetrySpecV1 { max_total_attempts: 1 };

/// One stage in a plan: handler, implementation, parameters, and ports.
///
/// `handler` is a validated open name such as `fern.train`. Legal scientific
/// handlers are not a Rust enum.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StageSpecV1 {
    pub id: String,
    pub handler: String,
    pub implementation: StageImplementationRefV1,
    pub parameters: Value,
    pub inputs: BTreeMap<String, StageInputSpecV1>,
    pub outputs: BTreeMap<String, StageOutputSpecV1>,
}

/// Ordered, content-addressed description of stages and artifact dependencies.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StagePlanV1 {
    pub schema: String,
    pub subject: ContentAddressedSubjectV1,
    pub stages: Vec<StageSpecV1>,
    pub exports: BTreeMap<String, StagePlanExportV1>,
    /// Per-stage attempt budget keyed by stage id. A stage absent here has one attempt.
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub retry: BTreeMap<String, StageRetrySpecV1>,
}

impl JsonContract for StagePlanV1 {
    const SCHEMA_ID: &'static str = STAGE_PLAN_SCHEMA;
}

/// One stage in an implementation-reported pipeline, before identities are bound.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StagePipelineStageV1 {
    pub id: String,
    pub handler: String,
    pub runner: StageRunnerV1,
    pub entrypoint: String,
    pub parameters: Value,
    pub inputs: BTreeMap<String, StageInputSpecV1>,
    pub outputs: BTreeMap<String, StageOutputSpecV1>,
}

/// Content-addressed pipeline graph reported by Stage implementations.
///
/// Topology, handlers, and ports live here. Rust instantiates this document; it
/// does not select a graph by model or preset name.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StagePipelineTemplateV1 {
    pub schema: String,
    pub stages: Vec<StagePipelineStageV1>,
    pub exports: BTreeMap<String, StagePlanExportV1>,
    /// Per-stage attempt budget keyed by stage id. A stage absent here has one attempt.
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub retry: BTreeMap<String, StageRetrySpecV1>,
}

impl JsonContract for StagePipelineTemplateV1 {
    const SCHEMA_ID: &'static str = STAGE_PIPELINE_TEMPLATE_SCHEMA;
}

impl StagePipelineTemplateV1 {
    pub fn parse_json(bytes: &[u8]) -> Result<Self, StagePlanError> {
        let template = fern_json_contract::checked_from_slice::<Self>(bytes)?;
        template.validate()?;
        Ok(template)
    }

    /// The forecast-origin materialize → train → postprocess graph this implementation executes.
    ///
    /// Architecture, action, and density change train configuration, not this graph.
    pub fn forecast_origin_pipeline() -> Self {
        Self::parse_json(include_str!("../fixtures/stage_pipeline_forecast_origin_v1.json").as_bytes())
            .expect("implementation-reported forecast-origin pipeline must parse")
    }

    pub fn digest(&self) -> Result<Sha256Digest, StagePlanError> {
        self.validate()?;
        hash_canonical(self)
    }

    pub fn validate(&self) -> Result<(), StagePlanError> {
        if self.schema != STAGE_PIPELINE_TEMPLATE_SCHEMA {
            return Err(StagePlanError::new(
                "schema",
                format!("must be {STAGE_PIPELINE_TEMPLATE_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        let plan = self.instantiate(
            ContentAddressedSubjectV1 {
                kind: "fern.pipeline.template".to_string(),
                identity: "validation".to_string(),
                content_digest: Sha256Digest::from_bytes(b"validation"),
            },
            &placeholder_implementations(self)?,
        )?;
        plan.validate()?;
        Ok(())
    }

    /// Bind published implementation identities onto this template.
    ///
    /// Host paths are refused by the Stage Plan laws. The caller hashes bundle
    /// or binary bytes and passes those identities; this function never fills a
    /// digest.
    pub fn instantiate(
        &self,
        subject: ContentAddressedSubjectV1,
        implementations: &BTreeMap<StageRunnerV1, StageImplementationRefV1>,
    ) -> Result<StagePlanV1, StagePlanError> {
        if self.schema != STAGE_PIPELINE_TEMPLATE_SCHEMA {
            return Err(StagePlanError::new(
                "schema",
                format!("must be {STAGE_PIPELINE_TEMPLATE_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        let mut stages = Vec::with_capacity(self.stages.len());
        for stage in &self.stages {
            let claimed = implementations.get(&stage.runner).ok_or_else(|| {
                StagePlanError::new(
                    format!("stages.{}.implementation", stage.id),
                    format!(
                        "template runner {:?} has no bound implementation identity",
                        stage.runner
                    ),
                )
            })?;
            if claimed.runner != stage.runner {
                return Err(StagePlanError::new(
                    format!("stages.{}.implementation.runner", stage.id),
                    "bound identity runner does not match the template",
                ));
            }
            if claimed.entrypoint != stage.entrypoint {
                return Err(StagePlanError::new(
                    format!("stages.{}.implementation.entrypoint", stage.id),
                    "bound identity entrypoint does not match the template",
                ));
            }
            stages.push(StageSpecV1 {
                id: stage.id.clone(),
                handler: stage.handler.clone(),
                implementation: claimed.clone(),
                parameters: stage.parameters.clone(),
                inputs: stage.inputs.clone(),
                outputs: stage.outputs.clone(),
            });
        }
        let plan = StagePlanV1 {
            schema: STAGE_PLAN_SCHEMA.to_string(),
            subject,
            stages,
            exports: self.exports.clone(),
            retry: self.retry.clone(),
        };
        plan.validate()?;
        Ok(plan)
    }
}

fn placeholder_implementations(
    template: &StagePipelineTemplateV1,
) -> Result<BTreeMap<StageRunnerV1, StageImplementationRefV1>, StagePlanError> {
    let mut implementations = BTreeMap::new();
    for stage in &template.stages {
        implementations
            .entry(stage.runner)
            .or_insert_with(|| StageImplementationRefV1 {
                runner: stage.runner,
                entrypoint: stage.entrypoint.clone(),
                implementation_digest: Sha256Digest::from_bytes(b"template-validation"),
            });
        let bound = &implementations[&stage.runner];
        if bound.entrypoint != stage.entrypoint {
            return Err(StagePlanError::new(
                format!("stages.{}.entrypoint", stage.id),
                "one runner kind must use one entrypoint in a pipeline template",
            ));
        }
    }
    Ok(implementations)
}

impl StageSpecV1 {
    /// Digest of this stage's own declaration.
    ///
    /// Covers id, handler, implementation, parameters, and ports. It does not
    /// include resolved artifact bytes or other stages' implementations, so a
    /// downstream analysis swap does not rewrite an upstream train digest.
    pub fn digest(&self) -> Result<Sha256Digest, StagePlanError> {
        self.validate_local()?;
        hash_canonical(self)
    }

    fn validate_local(&self) -> Result<(), StagePlanError> {
        let stage_field = |suffix: &str| format!("stages.{}.{suffix}", self.id);
        validate_open_name("stages.id", &self.id)?;
        validate_open_name(&stage_field("handler"), &self.handler)?;
        validate_entrypoint(
            &stage_field("implementation.entrypoint"),
            &self.implementation.entrypoint,
        )?;
        validate_parameters(&stage_field("parameters"), &self.parameters)?;
        if self.outputs.is_empty() {
            return Err(StagePlanError::new(
                stage_field("outputs"),
                "a stage must declare at least one output slot",
            ));
        }

        let mut output_roles = BTreeSet::new();
        for (port, output) in &self.outputs {
            validate_open_name(&stage_field("outputs"), port)?;
            validate_open_name(&format!("{}.role", stage_field("outputs")), &output.role)?;
            if let Some(schema_id) = &output.schema_id {
                validate_open_name(&format!("{}.schema_id", stage_field("outputs")), schema_id)?;
            }
            if !output_roles.insert(output.role.as_str()) {
                return Err(StagePlanError::new(
                    stage_field("outputs"),
                    format!("duplicate semantic port role {:?}", output.role),
                ));
            }
        }

        let mut input_roles = BTreeSet::new();
        for (port, input) in &self.inputs {
            validate_open_name(&stage_field("inputs"), port)?;
            validate_open_name(&format!("{}.role", stage_field("inputs")), &input.role)?;
            if let Some(schema_id) = &input.schema_id {
                validate_open_name(&format!("{}.schema_id", stage_field("inputs")), schema_id)?;
            }
            if self.outputs.contains_key(port) {
                return Err(StagePlanError::new(
                    stage_field("inputs"),
                    format!("duplicate semantic port {port:?} appears as both input and output"),
                ));
            }
            if !input_roles.insert(input.role.as_str()) {
                return Err(StagePlanError::new(
                    stage_field("inputs"),
                    format!("duplicate semantic port role {:?}", input.role),
                ));
            }
            match &input.source {
                StageInputSourceV1::ExternalArtifact => {}
                StageInputSourceV1::StageOutput { stage_id, output } => {
                    validate_open_name(&format!("{}.source.stage_id", stage_field("inputs")), stage_id)?;
                    validate_open_name(&format!("{}.source.output", stage_field("inputs")), output)?;
                    if stage_id == &self.id {
                        return Err(StagePlanError::new(
                            stage_field("inputs"),
                            "a stage may not bind a stage_output source to itself",
                        ));
                    }
                }
            }
        }
        Ok(())
    }
}

impl StagePlanV1 {
    /// Decode one JSON document through the generated contract and plan laws.
    pub fn parse_json(bytes: &[u8]) -> Result<Self, StagePlanError> {
        let plan = fern_json_contract::checked_from_slice::<Self>(bytes)?;
        plan.validate()?;
        Ok(plan)
    }

    /// Digest of the whole plan.
    ///
    /// Covers the subject, ordered topology, handlers, implementations,
    /// parameters, port declarations, and exports. Resolved artifact identities
    /// are not part of the plan.
    pub fn digest(&self) -> Result<Sha256Digest, StagePlanError> {
        self.validate()?;
        hash_canonical(self)
    }

    pub fn validate(&self) -> Result<(), StagePlanError> {
        if self.schema != STAGE_PLAN_SCHEMA {
            return Err(StagePlanError::new(
                "schema",
                format!("must be {STAGE_PLAN_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        validate_open_name("subject.kind", &self.subject.kind)?;
        validate_subject_identity("subject.identity", &self.subject.identity)?;
        if self.stages.is_empty() {
            return Err(StagePlanError::new("stages", "a plan must declare at least one stage"));
        }

        let mut seen_ids = BTreeMap::<&str, usize>::new();
        for (index, stage) in self.stages.iter().enumerate() {
            stage.validate_local()?;
            if seen_ids.insert(stage.id.as_str(), index).is_some() {
                return Err(StagePlanError::new(
                    "stages.id",
                    format!("duplicate stage id {:?}", stage.id),
                ));
            }
        }

        for (index, stage) in self.stages.iter().enumerate() {
            for (port, input) in &stage.inputs {
                let StageInputSourceV1::StageOutput { stage_id, output } = &input.source else {
                    continue;
                };
                match seen_ids.get(stage_id.as_str()) {
                    None => {
                        return Err(StagePlanError::new(
                            format!("stages.{}.inputs.{port}.source.stage_id", stage.id),
                            format!("stage_output refers to missing stage {stage_id:?}"),
                        ));
                    }
                    Some(&upstream_index) if upstream_index >= index => {
                        return Err(StagePlanError::new(
                            format!("stages.{}.inputs.{port}.source.stage_id", stage.id),
                            format!("stage_output may refer only to an earlier stage, not {stage_id:?}"),
                        ));
                    }
                    Some(&upstream_index) => {
                        let upstream = &self.stages[upstream_index];
                        let Some(declared) = upstream.outputs.get(output) else {
                            return Err(StagePlanError::new(
                                format!("stages.{}.inputs.{port}.source.output", stage.id),
                                format!("stage {stage_id:?} does not declare output {output:?}"),
                            ));
                        };
                        if declared.role != input.role || declared.schema_id != input.schema_id {
                            return Err(StagePlanError::new(
                                format!("stages.{}.inputs.{port}", stage.id),
                                format!(
                                    "input port {port:?} role/schema must match stage {stage_id:?} output {output:?}"
                                ),
                            ));
                        }
                    }
                }
            }
        }

        let mut consumed_outputs = BTreeSet::<(&str, &str)>::new();
        for stage in &self.stages {
            for input in stage.inputs.values() {
                if let StageInputSourceV1::StageOutput { stage_id, output } = &input.source {
                    consumed_outputs.insert((stage_id.as_str(), output.as_str()));
                }
            }
        }

        let mut exported_outputs = BTreeSet::<(&str, &str)>::new();
        for (export_name, export) in &self.exports {
            validate_open_name("exports", export_name)?;
            validate_open_name(&format!("exports.{export_name}.stage_id"), &export.stage_id)?;
            validate_open_name(&format!("exports.{export_name}.output"), &export.output)?;
            let Some(exported_stage) = self.stages.iter().find(|stage| stage.id == export.stage_id) else {
                return Err(StagePlanError::new(
                    format!("exports.{export_name}.stage_id"),
                    format!("export refers to missing stage {:?}", export.stage_id),
                ));
            };
            if !exported_stage.outputs.contains_key(&export.output) {
                return Err(StagePlanError::new(
                    format!("exports.{export_name}.output"),
                    format!(
                        "stage {:?} does not declare output {:?}",
                        export.stage_id, export.output
                    ),
                ));
            }
            if !exported_outputs.insert((export.stage_id.as_str(), export.output.as_str())) {
                return Err(StagePlanError::new(
                    format!("exports.{export_name}"),
                    format!(
                        "output {:?}.{:?} is exported more than once",
                        export.stage_id, export.output
                    ),
                ));
            }
        }

        for (stage_id, retry) in &self.retry {
            if !seen_ids.contains_key(stage_id.as_str()) {
                return Err(StagePlanError::new(
                    format!("retry.{stage_id}"),
                    format!("retry budget names missing stage {stage_id:?}"),
                ));
            }
            if retry.max_total_attempts == 0 {
                return Err(StagePlanError::new(
                    format!("retry.{stage_id}.max_total_attempts"),
                    "must be at least 1; a stage with no attempts can never be satisfied",
                ));
            }
        }

        for stage in &self.stages {
            for output_name in stage.outputs.keys() {
                let key = (stage.id.as_str(), output_name.as_str());
                if !consumed_outputs.contains(&key) && !exported_outputs.contains(&key) {
                    return Err(StagePlanError::new(
                        format!("stages.{}.outputs.{output_name}", stage.id),
                        "every stage output must be consumed by a later stage or named in exports",
                    ));
                }
            }
        }
        Ok(())
    }

    pub fn stage(&self, id: &str) -> Result<&StageSpecV1, StagePlanError> {
        self.stages
            .iter()
            .find(|stage| stage.id == id)
            .ok_or_else(|| StagePlanError::new("stages.id", format!("no stage named {id:?}")))
    }

    /// The attempt budget of one stage: its `retry` entry, or one total attempt.
    pub fn retry_spec(&self, stage_id: &str) -> StageRetrySpecV1 {
        self.retry.get(stage_id).copied().unwrap_or(DEFAULT_STAGE_RETRY)
    }
}

fn hash_canonical<T: Serialize>(value: &T) -> Result<Sha256Digest, StagePlanError> {
    let bytes = fern_json_contract::canonical_json_bytes(value).map_err(StagePlanError::from)?;
    Ok(Sha256Digest::from_bytes(&bytes))
}

fn validate_open_name(field: &str, value: &str) -> Result<(), StagePlanError> {
    if value.is_empty() {
        return Err(StagePlanError::new(field, "must be a non-empty name"));
    }
    if looks_like_filesystem_path(value) {
        return Err(StagePlanError::new(field, "must be a name, not a filesystem path"));
    }
    let mut saw_part = false;
    for part in value.split('.') {
        saw_part = true;
        if part.is_empty() {
            return Err(StagePlanError::new(field, "must not contain an empty dotted segment"));
        }
        let mut chars = part.chars();
        let Some(first) = chars.next() else {
            return Err(StagePlanError::new(field, "must be a non-empty name"));
        };
        if !first.is_ascii_alphabetic() {
            return Err(StagePlanError::new(
                field,
                "each segment must start with an ASCII letter",
            ));
        }
        if !part
            .chars()
            .all(|ch| ch.is_ascii_alphanumeric() || matches!(ch, '_' | '-'))
        {
            return Err(StagePlanError::new(
                field,
                "may contain only ASCII letters, digits, dot, underscore, and hyphen",
            ));
        }
    }
    if !saw_part {
        return Err(StagePlanError::new(field, "must be a non-empty name"));
    }
    Ok(())
}

fn validate_entrypoint(field: &str, value: &str) -> Result<(), StagePlanError> {
    if looks_like_filesystem_path(value) {
        return Err(StagePlanError::new(
            field,
            "must be a binary or Python module name, never a filesystem path",
        ));
    }
    validate_open_name(field, value)
}

fn validate_subject_identity(field: &str, value: &str) -> Result<(), StagePlanError> {
    if value.is_empty() || value.trim() != value {
        return Err(StagePlanError::new(field, "must be non-empty trimmed text"));
    }
    if looks_like_filesystem_path(value) {
        return Err(StagePlanError::new(field, "must not be a filesystem path"));
    }
    if value.chars().any(char::is_control) {
        return Err(StagePlanError::new(field, "must not contain control characters"));
    }
    Ok(())
}

fn validate_parameters(field: &str, value: &Value) -> Result<(), StagePlanError> {
    fn walk(field: &str, value: &Value) -> Result<(), StagePlanError> {
        match value {
            Value::Object(entries) => {
                for (key, child) in entries {
                    if looks_like_filesystem_path(key)
                        || FORBIDDEN_LOCATION_KEYS.contains(&key.to_ascii_lowercase().as_str())
                    {
                        return Err(StagePlanError::new(
                            field,
                            format!("parameters must not name location or control-plane field {key:?}"),
                        ));
                    }
                    walk(field, child)?;
                }
            }
            Value::Array(values) => {
                for child in values {
                    walk(field, child)?;
                }
            }
            Value::String(text) if looks_like_filesystem_path(text) => {
                return Err(StagePlanError::new(
                    field,
                    "parameters must not contain a filesystem path",
                ));
            }
            _ => {}
        }
        Ok(())
    }
    walk(field, value)
}

fn looks_like_filesystem_path(value: &str) -> bool {
    value.starts_with('/')
        || value.starts_with('\\')
        || value.starts_with("./")
        || value.starts_with(".\\")
        || value.starts_with("../")
        || value.starts_with("..\\")
        || value.contains('/')
        || value.contains('\\')
        || (value.len() >= 2 && value.as_bytes()[1] == b':' && value.as_bytes()[0].is_ascii_alphabetic())
}

const FORBIDDEN_LOCATION_KEYS: &[&str] = &["path", "host", "lane", "pid", "lease", "fencing"];

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    const CANONICAL_FIXTURE: &str = include_str!("../fixtures/stage_plan_v1_canonical.json");
    const DLINEAR_CANARY_FIXTURE: &str = include_str!("../fixtures/stage_plan_v1_dlinear_forecast_origin_canary.json");

    fn canonical_plan() -> StagePlanV1 {
        StagePlanV1::parse_json(CANONICAL_FIXTURE.as_bytes()).expect("canonical fixture must parse")
    }

    fn parse_value(value: Value) -> Result<StagePlanV1, StagePlanError> {
        StagePlanV1::parse_json(serde_json::to_string(&value).unwrap().as_bytes())
    }

    fn fixture_value() -> Value {
        serde_json::from_str(CANONICAL_FIXTURE).unwrap()
    }

    fn set_path(value: &mut Value, path: &[&str], next: Value) {
        let mut cursor = value;
        for key in &path[..path.len() - 1] {
            cursor = match key.parse::<usize>() {
                Ok(index) => &mut cursor[index],
                Err(_) => &mut cursor[*key],
            };
        }
        let last = path[path.len() - 1];
        match last.parse::<usize>() {
            Ok(index) => cursor[index] = next,
            Err(_) => cursor[last] = next,
        }
    }

    fn json_keys(value: &Value, keys: &mut BTreeSet<String>) {
        match value {
            Value::Object(entries) => {
                for (key, child) in entries {
                    keys.insert(key.clone());
                    json_keys(child, keys);
                }
            }
            Value::Array(values) => {
                for child in values {
                    json_keys(child, keys);
                }
            }
            _ => {}
        }
    }

    #[test]
    fn dlinear_forecast_origin_canary_fixture_parses() {
        let plan = StagePlanV1::parse_json(DLINEAR_CANARY_FIXTURE.as_bytes()).unwrap();
        assert_eq!(plan.stage("train").unwrap().handler, "fern.train.current");
        assert_eq!(plan.stage("postprocess").unwrap().handler, "fern.postprocess");
        assert!(plan.exports.contains_key("canary_metrics"));
    }

    #[test]
    fn strict_json_round_trip() {
        let plan = canonical_plan();
        let encoded = serde_json::to_vec(&plan).unwrap();
        let again = StagePlanV1::parse_json(&encoded).unwrap();
        assert_eq!(plan, again);
        assert_eq!(plan.digest().unwrap(), again.digest().unwrap());
        assert_eq!(plan.schema, STAGE_PLAN_SCHEMA);
        assert_eq!(
            plan.stage("train").unwrap().implementation.runner,
            StageRunnerV1::PythonModule
        );
        assert_eq!(
            plan.stage("acceptance_score").unwrap().implementation.runner,
            StageRunnerV1::RustBinary
        );
        assert_eq!(
            plan.stage("scientific_analysis").unwrap().implementation.runner,
            StageRunnerV1::PythonModule
        );
    }

    #[test]
    fn generated_schema_accepts_the_canonical_fixture() {
        let schema = StagePlanV1::schema_document().unwrap();
        assert_eq!(schema["$id"], STAGE_PLAN_SCHEMA);
        assert_eq!(schema["properties"]["schema"]["const"], STAGE_PLAN_SCHEMA);
        let validator = jsonschema::validator_for(&schema).expect("generated schema must be valid JSON Schema");
        let instance = fixture_value();
        let errors: Vec<String> = validator
            .iter_errors(&instance)
            .map(|error| format!("{error} at {}", error.instance_path()))
            .collect();
        assert!(errors.is_empty(), "generated schema rejected the fixture: {errors:?}");
    }

    #[test]
    fn unknown_field_is_refused() {
        let mut value = fixture_value();
        value["surprise"] = json!(true);
        let error = parse_value(value).unwrap_err().to_string();
        assert!(error.contains("unknown field"), "{error}");
    }

    #[test]
    fn forward_and_missing_output_references_are_refused() {
        let mut forward = fixture_value();
        set_path(
            &mut forward,
            &["stages", "0", "inputs", "compiled_experiment", "source"],
            json!({
                "kind": "stage_output",
                "stage_id": "acceptance_score",
                "output": "score_receipt"
            }),
        );
        let forward_error = parse_value(forward).unwrap_err().to_string();
        assert!(
            forward_error.contains("earlier stage") || forward_error.contains("forward"),
            "{forward_error}"
        );

        let mut missing_stage = fixture_value();
        set_path(
            &mut missing_stage,
            &["stages", "1", "inputs", "raw_forecast", "source", "stage_id"],
            json!("not_a_stage"),
        );
        let missing_error = parse_value(missing_stage).unwrap_err().to_string();
        assert!(missing_error.contains("missing stage"), "{missing_error}");

        let mut undeclared = fixture_value();
        set_path(
            &mut undeclared,
            &["stages", "1", "inputs", "raw_forecast", "source", "output"],
            json!("not_an_output"),
        );
        let undeclared_error = parse_value(undeclared).unwrap_err().to_string();
        assert!(
            undeclared_error.contains("does not declare output"),
            "{undeclared_error}"
        );
    }

    #[test]
    fn reordering_stages_rejects_a_broken_topology() {
        let mut value = fixture_value();
        let stages = value["stages"].as_array_mut().unwrap();
        stages.swap(0, 1);
        let error = parse_value(value).unwrap_err().to_string();
        assert!(error.contains("earlier stage") || error.contains("missing"), "{error}");
    }

    #[test]
    fn changing_train_implementation_changes_train_downstream_and_plan_identity() {
        let original = canonical_plan();
        let mut changed = fixture_value();
        set_path(
            &mut changed,
            &["stages", "0", "implementation", "implementation_digest"],
            json!("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
        );
        let changed = parse_value(changed).unwrap();
        assert_ne!(
            original.stage("train").unwrap().digest().unwrap(),
            changed.stage("train").unwrap().digest().unwrap()
        );
        assert_ne!(original.digest().unwrap(), changed.digest().unwrap());
        // Downstream stages bind train by id and port, not by train's implementation
        // digest. Their StageSpec digest stays put; the plan digest is the
        // identity of the composed downstream work, and a later execution key
        // will add the resolved train output digest.
        assert_eq!(
            original.stage("acceptance_score").unwrap().digest().unwrap(),
            changed.stage("acceptance_score").unwrap().digest().unwrap()
        );
        assert_eq!(
            original.stage("scientific_analysis").unwrap().digest().unwrap(),
            changed.stage("scientific_analysis").unwrap().digest().unwrap()
        );
        assert_ne!(original.digest().unwrap().as_str(), changed.digest().unwrap().as_str());
    }

    #[test]
    fn changing_only_analysis_implementation_leaves_train_digest_unchanged() {
        let original = canonical_plan();
        let mut changed = fixture_value();
        set_path(
            &mut changed,
            &["stages", "2", "implementation", "implementation_digest"],
            json!("sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
        );
        let changed = parse_value(changed).unwrap();
        assert_ne!(
            original.stage("scientific_analysis").unwrap().digest().unwrap(),
            changed.stage("scientific_analysis").unwrap().digest().unwrap()
        );
        assert_ne!(original.digest().unwrap(), changed.digest().unwrap());
        assert_eq!(
            original.stage("train").unwrap().digest().unwrap(),
            changed.stage("train").unwrap().digest().unwrap()
        );
        assert_eq!(
            original.stage("acceptance_score").unwrap().digest().unwrap(),
            changed.stage("acceptance_score").unwrap().digest().unwrap()
        );
    }

    #[test]
    fn input_and_output_object_ordering_does_not_change_the_digest() {
        let mut left = fixture_value();
        let mut right = fixture_value();
        left["stages"][2]["inputs"] = json!({
            "score_receipt": left["stages"][2]["inputs"]["score_receipt"],
            "unused_alias": {
                "source": {
                    "kind": "stage_output",
                    "stage_id": "train",
                    "output": "raw_forecast"
                },
                "materialization_requirement": "identity_only",
                "role": "raw_forecast",
                "schema_id": "fern.raw_forecast.v1"
            }
        });
        right["stages"][2]["inputs"] = json!({
            "unused_alias": left["stages"][2]["inputs"]["unused_alias"],
            "score_receipt": left["stages"][2]["inputs"]["score_receipt"]
        });
        left["stages"][0]["outputs"] = json!({
            "raw_forecast": left["stages"][0]["outputs"]["raw_forecast"],
            "unused_output": { "role": "unused_output" }
        });
        right["stages"][0]["outputs"] = json!({
            "unused_output": { "role": "unused_output" },
            "raw_forecast": right["stages"][0]["outputs"]["raw_forecast"]
        });
        left["exports"]["unused_train_output"] = json!({
            "stage_id": "train",
            "output": "unused_output",
            "materialization_requirement": "identity_only"
        });
        right["exports"]["unused_train_output"] = left["exports"]["unused_train_output"].clone();
        let left = parse_value(left).unwrap();
        let right = parse_value(right).unwrap();
        assert_eq!(left.digest().unwrap(), right.digest().unwrap());
        assert_eq!(
            left.stage("scientific_analysis").unwrap().digest().unwrap(),
            right.stage("scientific_analysis").unwrap().digest().unwrap()
        );
        assert_eq!(
            left.stage("train").unwrap().digest().unwrap(),
            right.stage("train").unwrap().digest().unwrap()
        );
    }

    #[test]
    fn resolved_artifact_identity_is_not_part_of_the_plan() {
        let plan = serde_json::to_value(&canonical_plan()).unwrap();
        let train_input = &plan["stages"][0]["inputs"]["compiled_experiment"];
        assert_eq!(train_input["source"]["kind"], "external_artifact");
        assert!(train_input["source"].get("artifact").is_none());
        assert!(train_input["source"].get("sha256").is_none());
        assert!(train_input.get("byte_count").is_none());
    }

    #[test]
    fn serialized_contract_has_no_location_or_control_plane_fields() {
        let plan = serde_json::to_value(&canonical_plan()).unwrap();
        let schema = StagePlanV1::schema_document().unwrap();
        for document in [&plan, &schema] {
            let mut keys = BTreeSet::new();
            json_keys(document, &mut keys);
            for key in &keys {
                let lowered = key.to_ascii_lowercase();
                for forbidden in FORBIDDEN_LOCATION_KEYS {
                    assert_ne!(
                        lowered.as_str(),
                        *forbidden,
                        "contract key {key:?} is a location or control-plane field"
                    );
                }
            }
            let rendered = serde_json::to_string(document).unwrap();
            for forbidden in [
                "\"path\"",
                "\"host\"",
                "\"lane\"",
                "\"pid\"",
                "\"lease\"",
                "\"fencing\"",
            ] {
                assert!(
                    !rendered.contains(forbidden),
                    "serialized contract contains {forbidden}"
                );
            }
        }
    }

    #[test]
    fn validation_rejects_empty_duplicate_invalid_and_self_referential_plans() {
        let empty_stages = json!({
            "schema": STAGE_PLAN_SCHEMA,
            "subject": fixture_value()["subject"],
            "stages": [],
            "exports": {}
        });
        assert!(
            parse_value(empty_stages)
                .unwrap_err()
                .to_string()
                .contains("at least one stage")
        );

        let mut duplicate_ids = fixture_value();
        duplicate_ids["stages"][2]["id"] = json!("train");
        assert!(
            parse_value(duplicate_ids)
                .unwrap_err()
                .to_string()
                .contains("duplicate stage id")
        );

        let mut no_outputs = fixture_value();
        no_outputs["stages"][0]["outputs"] = json!({});
        assert!(
            parse_value(no_outputs)
                .unwrap_err()
                .to_string()
                .contains("at least one output")
        );

        let mut path_entrypoint = fixture_value();
        path_entrypoint["stages"][0]["implementation"]["entrypoint"] = json!("./fr_tr.py");
        assert!(
            parse_value(path_entrypoint)
                .unwrap_err()
                .to_string()
                .contains("filesystem path")
        );

        let mut bad_digest = fixture_value();
        bad_digest["subject"]["content_digest"] = json!("not-a-digest");
        assert!(parse_value(bad_digest).unwrap_err().to_string().contains("sha256"));

        let mut uppercase_digest = fixture_value();
        uppercase_digest["subject"]["content_digest"] =
            json!("sha256:1592FAC4499AE692A137E5BB68389DBF64B0C4AAA83F30160E00F71C9282BE47");
        assert!(
            parse_value(uppercase_digest)
                .unwrap_err()
                .to_string()
                .contains("lowercase")
        );

        let mut self_ref = fixture_value();
        self_ref["stages"][1]["inputs"]["raw_forecast"]["source"]["stage_id"] = json!("acceptance_score");
        assert!(parse_value(self_ref).unwrap_err().to_string().contains("itself"));

        let mut invalid_name = fixture_value();
        invalid_name["stages"][0]["id"] = json!("train stage");
        assert!(
            parse_value(invalid_name)
                .unwrap_err()
                .to_string()
                .contains("may contain only")
        );

        let mut duplicate_role = fixture_value();
        duplicate_role["stages"][0]["outputs"] = json!({
            "raw_forecast": { "role": "raw_forecast" },
            "also_raw": { "role": "raw_forecast" }
        });
        assert!(
            parse_value(duplicate_role)
                .unwrap_err()
                .to_string()
                .contains("duplicate semantic port")
        );

        let mut unexported = fixture_value();
        unexported["exports"] = json!({});
        assert!(
            parse_value(unexported)
                .unwrap_err()
                .to_string()
                .contains("consumed by a later stage or named in exports")
        );
    }

    const FORECAST_ORIGIN_PIPELINE: &str = include_str!("../fixtures/stage_pipeline_forecast_origin_v1.json");

    fn bound_implementations(python: &str, rust: &str) -> BTreeMap<StageRunnerV1, StageImplementationRefV1> {
        BTreeMap::from([
            (
                StageRunnerV1::PythonModule,
                StageImplementationRefV1 {
                    runner: StageRunnerV1::PythonModule,
                    entrypoint: "fr_tr".to_string(),
                    implementation_digest: Sha256Digest::from_bytes(python.as_bytes()),
                },
            ),
            (
                StageRunnerV1::RustBinary,
                StageImplementationRefV1 {
                    runner: StageRunnerV1::RustBinary,
                    entrypoint: "fern-stage-runner".to_string(),
                    implementation_digest: Sha256Digest::from_bytes(rust.as_bytes()),
                },
            ),
        ])
    }

    #[test]
    fn forecast_origin_pipeline_instantiates_materialize_train_postprocess_without_model_names() {
        let template = StagePipelineTemplateV1::parse_json(FORECAST_ORIGIN_PIPELINE.as_bytes()).unwrap();
        let rendered = serde_json::to_string(&template).unwrap();
        assert!(!rendered.contains("dlinear"));
        assert!(!rendered.contains("DLinear"));
        assert!(!rendered.contains("\"model\""));
        let subject = ContentAddressedSubjectV1 {
            kind: "fern.compiled_experiment_cell".to_string(),
            identity: "cell-a".to_string(),
            content_digest: Sha256Digest::from_bytes(b"cell-a"),
        };
        let plan = template
            .instantiate(subject, &bound_implementations("python-a", "rust-a"))
            .unwrap();
        let ids = plan.stages.iter().map(|stage| stage.id.as_str()).collect::<Vec<_>>();
        assert_eq!(ids, ["materialize", "train", "postprocess"]);
        assert!(
            plan.stage("materialize")
                .unwrap()
                .inputs
                .contains_key("training_worker_request")
        );
        assert!(plan.stage("materialize").unwrap().inputs.contains_key("dgp_bank"));
        assert!(plan.stage("train").unwrap().inputs.contains_key("materialized_cell"));
        assert!(
            plan.stage("postprocess")
                .unwrap()
                .inputs
                .contains_key("postprocess_spec")
        );
        let again = template
            .instantiate(
                ContentAddressedSubjectV1 {
                    kind: "fern.compiled_experiment_cell".to_string(),
                    identity: "cell-a".to_string(),
                    content_digest: Sha256Digest::from_bytes(b"cell-a"),
                },
                &bound_implementations("python-a", "rust-a"),
            )
            .unwrap();
        assert_eq!(
            fern_json_contract::canonical_json_bytes(&plan).unwrap(),
            fern_json_contract::canonical_json_bytes(&again).unwrap()
        );
        assert_eq!(plan.digest().unwrap(), again.digest().unwrap());
    }

    #[test]
    fn subject_or_implementation_bytes_change_plan_identity_not_topology() {
        let template = StagePipelineTemplateV1::parse_json(FORECAST_ORIGIN_PIPELINE.as_bytes()).unwrap();
        let left = template
            .instantiate(
                ContentAddressedSubjectV1 {
                    kind: "fern.compiled_experiment_cell".to_string(),
                    identity: "cell-a".to_string(),
                    content_digest: Sha256Digest::from_bytes(b"cell-a"),
                },
                &bound_implementations("python-a", "rust-a"),
            )
            .unwrap();
        let forked_subject = template
            .instantiate(
                ContentAddressedSubjectV1 {
                    kind: "fern.compiled_experiment_cell".to_string(),
                    identity: "cell-b".to_string(),
                    content_digest: Sha256Digest::from_bytes(b"cell-b"),
                },
                &bound_implementations("python-a", "rust-a"),
            )
            .unwrap();
        assert_eq!(
            left.stages
                .iter()
                .map(|stage| (&stage.id, &stage.handler, &stage.inputs, &stage.outputs))
                .collect::<Vec<_>>(),
            forked_subject
                .stages
                .iter()
                .map(|stage| (&stage.id, &stage.handler, &stage.inputs, &stage.outputs))
                .collect::<Vec<_>>()
        );
        assert_ne!(left.digest().unwrap(), forked_subject.digest().unwrap());
        let rust_changed = template
            .instantiate(
                ContentAddressedSubjectV1 {
                    kind: "fern.compiled_experiment_cell".to_string(),
                    identity: "cell-a".to_string(),
                    content_digest: Sha256Digest::from_bytes(b"cell-a"),
                },
                &bound_implementations("python-a", "rust-b"),
            )
            .unwrap();
        // CHG 2026-08-26: this was assert_ne! while `canonicalize` and `score`
        // were Rust stages. The postprocess migration made every stage in this
        // pipeline a python_module, so no stage binds the Rust implementation
        // and its bytes cannot affect what the plan produces.
        //
        // Identity must cover exactly what runs. Including bytes that cannot
        // change the result would make two identical plans look different, and
        // the reverse -- omitting bytes that CAN change it -- is checked by the
        // Python assertion below, which still requires a different digest.
        //
        // If a Rust stage ever returns to this pipeline, this assertion must go
        // back to assert_ne! in the same change that adds it.
        assert_eq!(
            left.digest().unwrap(),
            rust_changed.digest().unwrap(),
            "no stage in the forecast-origin pipeline runs Rust, so Rust bytes are not part of its identity"
        );
        let python_changed = template
            .instantiate(
                ContentAddressedSubjectV1 {
                    kind: "fern.compiled_experiment_cell".to_string(),
                    identity: "cell-a".to_string(),
                    content_digest: Sha256Digest::from_bytes(b"cell-a"),
                },
                &bound_implementations("python-b", "rust-a"),
            )
            .unwrap();
        assert_ne!(left.digest().unwrap(), python_changed.digest().unwrap());
    }

    #[test]
    fn plan_without_retry_map_has_one_attempt_per_stage_and_serializes_no_retry_key() {
        let plan = canonical_plan();
        assert!(plan.retry.is_empty());
        for stage in &plan.stages {
            assert_eq!(plan.retry_spec(&stage.id), DEFAULT_STAGE_RETRY);
            assert_eq!(plan.retry_spec(&stage.id).max_total_attempts, 1);
        }
        let rendered: Value = serde_json::to_value(&plan).unwrap();
        assert!(rendered.get("retry").is_none(), "{rendered}");
        assert_eq!(plan.retry_spec("no-such-stage"), DEFAULT_STAGE_RETRY);
    }

    #[test]
    fn retry_map_refuses_missing_stage_zero_attempts_and_unknown_fields() {
        let mut missing = fixture_value();
        set_path(&mut missing, &["retry"], json!({"nowhere": {"max_total_attempts": 2}}));
        assert_eq!(parse_value(missing).unwrap_err().field, "retry.nowhere");
        let mut zero = fixture_value();
        set_path(&mut zero, &["retry"], json!({"train": {"max_total_attempts": 0}}));
        assert_eq!(parse_value(zero).unwrap_err().field, "retry.train.max_total_attempts");
        let mut unknown_field = fixture_value();
        set_path(
            &mut unknown_field,
            &["retry"],
            json!({"train": {"max_total_attempts": 2, "backoff_ms": 5}}),
        );
        assert!(parse_value(unknown_field).is_err());
    }

    #[test]
    fn retry_budget_changes_plan_digest_but_no_stage_digest_or_topology() {
        let without = canonical_plan();
        let mut with_value = fixture_value();
        set_path(&mut with_value, &["retry"], json!({"train": {"max_total_attempts": 2}}));
        let with = parse_value(with_value).unwrap();
        assert_eq!(with.retry_spec("train").max_total_attempts, 2);
        assert_eq!(with.retry_spec("acceptance_score").max_total_attempts, 1);
        assert_ne!(without.digest().unwrap(), with.digest().unwrap());
        assert_eq!(without.stages.len(), with.stages.len());
        for (left, right) in without.stages.iter().zip(with.stages.iter()) {
            assert_eq!(left.digest().unwrap(), right.digest().unwrap());
        }
    }

    #[test]
    fn forecast_origin_template_requests_two_attempts_per_stage_and_the_plan_carries_them() {
        let template = StagePipelineTemplateV1::parse_json(FORECAST_ORIGIN_PIPELINE.as_bytes()).unwrap();
        assert_eq!(template.retry.len(), template.stages.len());
        let plan = template
            .instantiate(
                ContentAddressedSubjectV1 {
                    kind: "fern.compiled_experiment_cell".to_string(),
                    identity: "cell-a".to_string(),
                    content_digest: Sha256Digest::from_bytes(b"cell-a"),
                },
                &bound_implementations("python-a", "rust-a"),
            )
            .unwrap();
        for stage in &plan.stages {
            assert_eq!(plan.retry_spec(&stage.id).max_total_attempts, 2, "{}", stage.id);
        }
        let mut orphan = template.clone();
        orphan
            .retry
            .insert("nowhere".to_string(), StageRetrySpecV1 { max_total_attempts: 2 });
        assert!(orphan.validate().is_err());
    }
}

#[cfg(test)]
mod golden_for_the_csharp_port {
    //! The stage-plan identities C# must reproduce.
    //!
    //! Written from the Rust while it still runs, because a port checked
    //! against values the porter chose has been checked against nothing. The
    //! inputs are the committed fixtures rather than constructed objects: a
    //! fixture cannot be quietly adjusted to make a digest come out right.
    //!
    //! What this pins is the canonical-JSON convention, which is the one C# is
    //! least likely to get right by default. `canonical_json_bytes` sorts keys
    //! and leaves non-ASCII raw; System.Text.Json does neither without being
    //! told, and neither difference raises anything.

    use super::*;

    const CANONICAL_FIXTURE: &str = include_str!("../fixtures/stage_plan_v1_canonical.json");
    const DLINEAR_CANARY_FIXTURE: &str =
        include_str!("../fixtures/stage_plan_v1_dlinear_forecast_origin_canary.json");
    const FORECAST_ORIGIN_PIPELINE: &str = include_str!("../fixtures/stage_pipeline_forecast_origin_v1.json");

    #[test]
    fn write_the_stage_plan_identities_the_csharp_port_must_reproduce() {
        let pipeline = StagePipelineTemplateV1::parse_json(FORECAST_ORIGIN_PIPELINE.as_bytes()).unwrap();
        let canonical = StagePlanV1::parse_json(CANONICAL_FIXTURE.as_bytes()).unwrap();
        let canary = StagePlanV1::parse_json(DLINEAR_CANARY_FIXTURE.as_bytes()).unwrap();

        let golden = serde_json::json!({
            "schema": "fern.stage_plan_identity_golden.v1",
            "note": "digest = sha256 over canonical JSON: keys SORTED, no whitespace, non-ASCII raw",
            "fixtures": {
                "stage_pipeline_forecast_origin_v1.json": pipeline.digest().unwrap().as_str().to_string(),
                "stage_plan_v1_canonical.json": canonical.digest().unwrap().as_str().to_string(),
                "stage_plan_v1_dlinear_forecast_origin_canary.json": canary.digest().unwrap().as_str().to_string(),
            },
            "canonical_bytes_len": {
                "stage_plan_v1_canonical.json":
                    fern_json_contract::canonical_json_bytes(&canonical).unwrap().len(),
            },
            "stage_ids": canonical.stages.iter().map(|stage| stage.id.clone()).collect::<Vec<_>>(),
        });

        let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("fixtures/stage_plan_identity_golden.json");
        std::fs::write(&path, serde_json::to_string_pretty(&golden).unwrap() + "\n").unwrap();
        let written = std::fs::read_to_string(&path).unwrap();
        assert!(
            written.contains("fern.stage_plan_identity_golden.v1"),
            "golden did not round-trip"
        );
    }
}
