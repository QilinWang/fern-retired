//! Source bytes, implementation composition, and Research Design binding.
//!
//! Title, Research Design, machine path, and Plan name never enter
//! `SourceContent` or `Implementation` identity. The same tree with a new
//! title is a new Binding, not another copy of the tree. V1 snapshot rows
//! stay; new writes use these objects only.

use std::collections::BTreeMap;
use std::fs;
use std::io::Write;
use std::os::unix::ffi::OsStrExt;
use std::path::{Component, Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use fern_json_contract::{JsonContract, canonical_json_bytes};
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::implementation_source_snapshot::{
    ArchivedSourceV1, ImplementationSourceSnapshotV1, SourceEntryKind, UntrackedSourceV1,
};

pub const SOURCE_CONTENT_SCHEMA: &str = "fern.source-content.v2";
pub const SOURCE_IMPLEMENTATION_SCHEMA: &str = "fern.source-implementation.v2";
pub const IMPLEMENTATION_BINDING_SCHEMA: &str = "fern.implementation-binding.v2";
pub const SOURCE_OBSERVATION_SCHEMA: &str = "fern.source-observation.v2";
pub const SOURCE_BLOBS_DIR: &str = "source-blobs";
pub const SOURCE_CONTENTS_DIR: &str = "source-contents";
pub const IMPLEMENTATIONS_DIR: &str = "implementations";
pub const PYTHON_MODULE_ENTRYPOINT: &str = "fr_tr";
pub const RUST_BINARY_ENTRYPOINT: &str = "fern-stage-runner";

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct SourceFileV2 {
    pub path: String,
    pub kind: SourceEntryKind,
    pub byte_digest: String,
    pub bytes: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct SourceContentV2 {
    pub schema: String,
    pub source_content_id: String,
    pub files: Vec<SourceFileV2>,
    pub untracked_paths: Vec<String>,
    pub cargo_lock_byte_digest: String,
    pub recipe_catalog_source_digest: String,
    pub source_tree_digest: String,
}

impl JsonContract for SourceContentV2 {
    const SCHEMA_ID: &'static str = SOURCE_CONTENT_SCHEMA;
}

#[derive(Serialize)]
struct SourceContentIdentity<'a> {
    schema: &'static str,
    files: &'a [SourceFileV2],
    untracked_paths: &'a [String],
    cargo_lock_byte_digest: &'a str,
    recipe_catalog_source_digest: &'a str,
}

impl SourceContentV2 {
    pub fn from_files(
        files: Vec<SourceFileV2>,
        untracked_paths: Vec<String>,
        cargo_lock_byte_digest: String,
        recipe_catalog_source_digest: String,
    ) -> Result<Self, SourceContentError> {
        let mut files = files;
        files.sort_by(|left, right| left.path.cmp(&right.path));
        let mut untracked_paths = untracked_paths;
        untracked_paths.sort();
        let mut content = Self {
            schema: SOURCE_CONTENT_SCHEMA.to_string(),
            source_content_id: String::new(),
            files,
            untracked_paths,
            cargo_lock_byte_digest,
            recipe_catalog_source_digest,
            source_tree_digest: String::new(),
        };
        content.source_tree_digest = content.tree_digest()?;
        content.source_content_id = content.expected_id()?;
        content.validate()?;
        Ok(content)
    }

    fn identity_preimage(&self) -> SourceContentIdentity<'_> {
        SourceContentIdentity {
            schema: SOURCE_CONTENT_SCHEMA,
            files: &self.files,
            untracked_paths: &self.untracked_paths,
            cargo_lock_byte_digest: &self.cargo_lock_byte_digest,
            recipe_catalog_source_digest: &self.recipe_catalog_source_digest,
        }
    }

    pub fn expected_id(&self) -> Result<String, SourceContentError> {
        Ok(format!(
            "source-content:{}",
            digest_preimage(&self.identity_preimage())?
        ))
    }

    pub fn tree_digest(&self) -> Result<String, SourceContentError> {
        let material = serde_json::json!({
            "schema": "fern.source-tree.v2",
            "files": self.files,
        });
        Ok(format!("sha256:{}", digest_preimage(&material)?))
    }

    pub fn validate(&self) -> Result<(), SourceContentError> {
        require(
            self.schema == SOURCE_CONTENT_SCHEMA,
            format!(
                "source content schema {:?} is not {SOURCE_CONTENT_SCHEMA:?}",
                self.schema
            ),
        )?;
        require(!self.files.is_empty(), "source content has no files".to_string())?;
        let mut previous: Option<&str> = None;
        let mut paths = std::collections::BTreeSet::new();
        for file in &self.files {
            validate_relative_path(&file.path, "source file path")?;
            require(
                paths.insert(file.path.as_str()),
                format!("source file path {:?} is duplicated", file.path),
            )?;
            if let Some(previous) = previous {
                require(
                    previous < file.path.as_str(),
                    "source files must be in stable path order".to_string(),
                )?;
            }
            previous = Some(file.path.as_str());
            byte_digest_hex(&file.byte_digest, "files[].byte_digest")?;
        }
        let file_paths = paths;
        previous = None;
        for path in &self.untracked_paths {
            validate_relative_path(path, "untracked path")?;
            require(
                file_paths.contains(path.as_str()),
                format!("untracked path {path:?} is not a file in this source content"),
            )?;
            if let Some(previous) = previous {
                require(
                    previous < path.as_str(),
                    "untracked_paths must be in stable path order".to_string(),
                )?;
            }
            previous = Some(path.as_str());
        }
        byte_digest_hex(&self.cargo_lock_byte_digest, "cargo_lock_byte_digest")?;
        byte_digest_hex(&self.recipe_catalog_source_digest, "recipe_catalog_source_digest")?;
        require(
            self.source_tree_digest == self.tree_digest()?,
            "source_tree_digest does not match the file list".to_string(),
        )?;
        require(
            self.source_content_id == self.expected_id()?,
            "source_content_id does not match source-byte identity".to_string(),
        )?;
        Ok(())
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct SourceImplementationV2 {
    pub schema: String,
    pub implementation_id: String,
    pub component_content_ids: Vec<String>,
    pub entrypoints: BTreeMap<String, String>,
    /// What this Implementation can DO, as declared by its bundle.
    ///
    /// This used to be the digest of `default_entrypoints()` -- a compile-time
    /// constant, identical for every Implementation ever frozen. It therefore
    /// separated nothing: two implementations whose Python modules, source file
    /// list, lock files and feature flags all differed carried the same
    /// "capability", and an identity that cannot tell two things apart is not
    /// carrying its own name. When a bundle is supplied it is now derived from
    /// that bundle; without one, the old constant is kept so the rows already in
    /// a Registry Home still reproduce their `implementation_id`.
    pub capability_digest: String,
    /// The entrypoint table digest, when the capability digest no longer is it.
    ///
    /// `None` for every Implementation frozen before the capability digest meant
    /// anything, which is what keeps their identity preimage byte-identical:
    /// the field is skipped when absent, so an old record hashes exactly the
    /// bytes it hashed the day it was written.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub entrypoint_contract_digest: Option<String>,
    pub build_contract_digest: String,
}

impl JsonContract for SourceImplementationV2 {
    const SCHEMA_ID: &'static str = SOURCE_IMPLEMENTATION_SCHEMA;
}

#[derive(Serialize)]
struct ImplementationIdentity<'a> {
    schema: &'static str,
    component_content_ids: &'a [String],
    entrypoints: &'a BTreeMap<String, String>,
    capability_digest: &'a str,
    // Skipped when absent, which is what makes every Implementation frozen
    // before this field existed still hash the bytes it hashed originally.
    #[serde(skip_serializing_if = "Option::is_none")]
    entrypoint_contract_digest: Option<&'a str>,
    build_contract_digest: &'a str,
}

/// The digest of the fixed entrypoint table every Implementation shares.
fn entrypoint_contract_digest(entrypoints: &BTreeMap<String, String>) -> Result<String, SourceContentError> {
    digest_prefixed(&serde_json::json!({
        "schema": "fern.implementation.entrypoint-contract.v1",
        "entrypoints": entrypoints,
    }))
}

/// What the frozen bundle says this Implementation can do.
///
/// Every field the bundle declares is here, because every one of them changes
/// what the implementation IS: the runtime that will be started, the wire
/// protocol it speaks, the exact source files it is made of, the dependency
/// locks that resolve its libraries, and the feature flags that select which
/// code inside it is live. Deriving from a subset would put two genuinely
/// different implementations under one capability again, which is the defect
/// this replaces.
pub fn bundle_capability_digest(
    bundle: &fern_recipes::ImplementationBundleDraftV2,
) -> Result<String, SourceContentError> {
    let (runtime_kind, runtime_entrypoint) = match &bundle.runtime {
        fern_recipes::ImplementationRuntimeV2::PythonModule { module } => ("python_module", module),
    };
    digest_prefixed(&serde_json::json!({
        "schema": "fern.implementation.capability-contract.v1",
        "runtime_kind": runtime_kind,
        "runtime_entrypoint": runtime_entrypoint,
        "protocol_version": bundle.protocol_version,
        "source_files": bundle.source_files,
        "dependency_lock_files": bundle.dependency_lock_files,
        "active_feature_flags": bundle.active_feature_flags,
    }))
}

impl SourceImplementationV2 {
    /// The pre-capability shape: one constant standing in for every capability.
    ///
    /// Kept, and kept producing the same identity, because the Implementations
    /// already bound to Coordinates in a Registry Home were minted this way and
    /// their `implementation_id` must not move under them. New freezes that
    /// name a bundle should use `from_content_and_bundle`.
    pub fn from_content(content: &SourceContentV2) -> Result<Self, SourceContentError> {
        let entrypoints = default_entrypoints();
        let capability_digest = entrypoint_contract_digest(&entrypoints)?;
        Self::assemble(content, entrypoints, capability_digest, None)
    }

    /// Derive the capability from the bundle this Implementation was frozen for.
    ///
    /// The entrypoint table does not disappear -- it moves to its own field, so
    /// the two facts the old single digest was conflating (which entrypoints
    /// exist, and what this implementation is made of) are separately readable.
    pub fn from_content_and_bundle(
        content: &SourceContentV2,
        bundle: &fern_recipes::ImplementationBundleDraftV2,
    ) -> Result<Self, SourceContentError> {
        let entrypoints = default_entrypoints();
        let contract = entrypoint_contract_digest(&entrypoints)?;
        let capability_digest = bundle_capability_digest(bundle)?;
        Self::assemble(content, entrypoints, capability_digest, Some(contract))
    }

    /// Derive the capability from the bundle a probe RESOLVED, which is the
    /// convention a running worker can be checked against.
    ///
    /// An authored draft names the files a person listed -- 17 of them for the
    /// Fern provider. Probing that draft against a real interpreter resolves the
    /// import closure it actually loads, which is 93. Both are honest lists and
    /// they are not the same list, so a digest over one never equals a digest
    /// over the other. Only the resolved list describes the code that will run,
    /// so it is the one the self-report gate compares against, and this is how a
    /// record is minted to be checkable. `from_content_and_bundle` remains for
    /// the records already minted from a draft, whose ids must not move.
    pub fn from_content_and_resolution(
        content: &SourceContentV2,
        resolution: &fern_recipes::ImplementationBundleResolutionV2,
    ) -> Result<Self, SourceContentError> {
        let entrypoints = default_entrypoints();
        let contract = entrypoint_contract_digest(&entrypoints)?;
        let capability_digest = observed_capability_digest(resolution)?;
        Self::assemble(content, entrypoints, capability_digest, Some(contract))
    }

    fn assemble(
        content: &SourceContentV2,
        entrypoints: BTreeMap<String, String>,
        capability_digest: String,
        entrypoint_contract_digest: Option<String>,
    ) -> Result<Self, SourceContentError> {
        let build_contract_digest = digest_prefixed(&serde_json::json!({
            "schema": "fern.implementation.build-contract.v1",
            "cargo_lock_byte_digest": content.cargo_lock_byte_digest,
            "recipe_catalog_source_digest": content.recipe_catalog_source_digest,
        }))?;
        let mut implementation = Self {
            schema: SOURCE_IMPLEMENTATION_SCHEMA.to_string(),
            implementation_id: String::new(),
            component_content_ids: vec![content.source_content_id.clone()],
            entrypoints,
            capability_digest,
            entrypoint_contract_digest,
            build_contract_digest,
        };
        implementation.implementation_id = implementation.expected_id()?;
        implementation.validate()?;
        Ok(implementation)
    }

    pub fn expected_id(&self) -> Result<String, SourceContentError> {
        Ok(format!(
            "implementation:{}",
            digest_preimage(&ImplementationIdentity {
                schema: SOURCE_IMPLEMENTATION_SCHEMA,
                component_content_ids: &self.component_content_ids,
                entrypoints: &self.entrypoints,
                capability_digest: &self.capability_digest,
                entrypoint_contract_digest: self.entrypoint_contract_digest.as_deref(),
                build_contract_digest: &self.build_contract_digest,
            })?
        ))
    }

    pub fn validate(&self) -> Result<(), SourceContentError> {
        require(
            self.schema == SOURCE_IMPLEMENTATION_SCHEMA,
            format!(
                "implementation schema {:?} is not {SOURCE_IMPLEMENTATION_SCHEMA:?}",
                self.schema
            ),
        )?;
        require(
            !self.component_content_ids.is_empty(),
            "implementation has no source-content components".to_string(),
        )?;
        for content_id in &self.component_content_ids {
            require(
                content_id.starts_with("source-content:") && content_id.len() == "source-content:".len() + 64,
                format!("component content id {content_id:?} is not a source-content identity"),
            )?;
        }
        require(
            !self.entrypoints.is_empty(),
            "implementation has no entrypoints".to_string(),
        )?;
        byte_digest_hex(&self.capability_digest, "capability_digest")?;
        if let Some(contract) = &self.entrypoint_contract_digest {
            byte_digest_hex(contract, "entrypoint_contract_digest")?;
        }
        byte_digest_hex(&self.build_contract_digest, "build_contract_digest")?;
        require(
            self.implementation_id == self.expected_id()?,
            "implementation_id does not match composition identity".to_string(),
        )?;
        Ok(())
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ImplementationBindingV2 {
    pub schema: String,
    pub binding_id: String,
    pub implementation_id: String,
    pub research_design_id: String,
    pub title: String,
    pub reason: String,
}

impl SourceContentV2 {
    /// Require these bytes to cover every file the authored implementation names.
    ///
    /// This is what ties the authored bundle, whose digests the model-link probe
    /// enforces, to the content-addressed bytes a Stage will actually execute.
    /// Without it a Binding can name content that does not contain the program
    /// the probe approved. Mirrors the V1 check in
    /// `implementation_source_snapshot.rs` so both source authorities answer the
    /// same question the same way.
    /// Takes the files it checks rather than the whole resolution: the caller
    /// chains `source_files` with `dependency_lock_files`, and a test needs a
    /// Vec instead of a six-field fixture it would not otherwise read.
    pub fn require_implementation_coverage<'a>(
        &self,
        declared: impl IntoIterator<Item = &'a fern_recipes::ImplementationBundleFileV2>,
    ) -> Result<(), SourceContentError> {
        let present = self
            .files
            .iter()
            .map(|file| (file.path.as_str(), file))
            .collect::<BTreeMap<_, _>>();
        for source in declared {
            let observed = present.get(source.path.as_str()).ok_or_else(|| {
                SourceContentError::Invalid(format!(
                    "implementation source {:?} is absent from source content {}",
                    source.path, self.source_content_id
                ))
            })?;
            require(
                observed.kind == SourceEntryKind::RegularFile
                    && observed.byte_digest == source.byte_sha256
                    && observed.bytes == source.bytes,
                format!(
                    "implementation source {:?} does not match the bytes in source content {}",
                    source.path, self.source_content_id
                ),
            )?;
        }
        Ok(())
    }
}

impl SourceImplementationV2 {
    /// The one content this implementation composes.
    ///
    /// `component_content_ids` is a Vec because a Fern core plus an extension
    /// will one day be two components. Nothing composes more than one yet, so
    /// this refuses a multi-component implementation by name rather than
    /// indexing `[0]` and silently running half of it.
    pub fn sole_component_content_id(&self) -> Result<&str, SourceContentError> {
        match self.component_content_ids.as_slice() {
            [only] => Ok(only.as_str()),
            [] => Err(SourceContentError::Invalid(format!(
                "implementation {} composes no source content",
                self.implementation_id
            ))),
            many => Err(SourceContentError::Invalid(format!(
                "implementation {} composes {} source contents ({}); multi-component \
                 authoring is not wired, so this cannot be planned yet",
                self.implementation_id,
                many.len(),
                many.join(", ")
            ))),
        }
    }
}

impl JsonContract for ImplementationBindingV2 {
    const SCHEMA_ID: &'static str = IMPLEMENTATION_BINDING_SCHEMA;
}

#[derive(Serialize)]
struct BindingIdentity<'a> {
    schema: &'static str,
    implementation_id: &'a str,
    research_design_id: &'a str,
    title: &'a str,
    reason: &'a str,
}

impl ImplementationBindingV2 {
    pub fn new(
        implementation_id: String,
        research_design_id: String,
        title: String,
        reason: String,
    ) -> Result<Self, SourceContentError> {
        let mut binding = Self {
            schema: IMPLEMENTATION_BINDING_SCHEMA.to_string(),
            binding_id: String::new(),
            implementation_id,
            research_design_id,
            title,
            reason,
        };
        binding.binding_id = binding.expected_id()?;
        binding.validate()?;
        Ok(binding)
    }

    pub fn expected_id(&self) -> Result<String, SourceContentError> {
        Ok(format!(
            "implementation-binding:{}",
            digest_preimage(&BindingIdentity {
                schema: IMPLEMENTATION_BINDING_SCHEMA,
                implementation_id: &self.implementation_id,
                research_design_id: &self.research_design_id,
                title: &self.title,
                reason: &self.reason,
            })?
        ))
    }

    pub fn validate(&self) -> Result<(), SourceContentError> {
        require(
            self.schema == IMPLEMENTATION_BINDING_SCHEMA,
            format!(
                "binding schema {:?} is not {IMPLEMENTATION_BINDING_SCHEMA:?}",
                self.schema
            ),
        )?;
        require(
            self.implementation_id.starts_with("implementation:")
                && self.implementation_id.len() == "implementation:".len() + 64,
            "implementation_id must be a full implementation:<sha256> identity".to_string(),
        )?;
        require(
            self.research_design_id.starts_with("research-design:")
                && self.research_design_id.len() > "research-design:".len()
                && !self.research_design_id.chars().any(char::is_whitespace),
            "research_design_id must be a full non-empty research-design: identity".to_string(),
        )?;
        require(
            !self.title.trim().is_empty() && self.title.trim() == self.title,
            "title must be trimmed, non-empty text".to_string(),
        )?;
        require(
            self.reason.trim() == self.reason,
            "reason must not have surrounding whitespace".to_string(),
        )?;
        require(
            self.binding_id == self.expected_id()?,
            "binding_id does not match implementation plus Research Design plus title".to_string(),
        )?;
        Ok(())
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FrozenSourceV2 {
    pub source_content: SourceContentV2,
    pub source_observation: SourceObservationV2,
    pub implementation: SourceImplementationV2,
    pub binding: ImplementationBindingV2,
    pub tracked_patch_digest: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SourceRegistrationOutcome {
    Registered,
    AlreadyRegistered,
}

/// Where one `SourceContent` was seen.
///
/// A commit is not a property of the bytes: the same tree can appear under
/// many commits, and `register_source_content` deliberately compares only
/// files and paths, so a second freeze of identical content from a different
/// commit was silently discarding its own provenance. An Observation is the
/// object that can hold it honestly.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields, tag = "observation_kind", rename_all = "snake_case")]
pub enum SourceObservationKindV2 {
    /// Resolved from a live Git object database.
    ExactGit { git_commit: String, git_tree: String },
    /// Recovered from a V1 snapshot. `declared_commit` is what the snapshot
    /// claimed; there is no tree, because an unresolvable commit must read as
    /// absent rather than be filled with the commit id.
    LegacySnapshot {
        snapshot_id: String,
        declared_commit: String,
    },
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct SourceObservationV2 {
    pub schema: String,
    pub observation_id: String,
    pub source_content_id: String,
    pub observed: SourceObservationKindV2,
}

impl JsonContract for SourceObservationV2 {
    const SCHEMA_ID: &'static str = SOURCE_OBSERVATION_SCHEMA;
}

#[derive(Serialize)]
struct SourceObservationIdentity<'a> {
    schema: &'static str,
    source_content_id: &'a str,
    observed: &'a SourceObservationKindV2,
}

impl SourceObservationV2 {
    pub fn new(source_content_id: String, observed: SourceObservationKindV2) -> Result<Self, SourceContentError> {
        let mut observation = Self {
            schema: SOURCE_OBSERVATION_SCHEMA.to_string(),
            observation_id: String::new(),
            source_content_id,
            observed,
        };
        observation.observation_id = observation.expected_id()?;
        observation.validate()?;
        Ok(observation)
    }

    /// The address these facts must have. Hashing the whole struct would let a
    /// rewritten `observation_id` describe itself, so the preimage excludes it.
    pub fn expected_id(&self) -> Result<String, SourceContentError> {
        let bytes = canonical_json_bytes(&SourceObservationIdentity {
            schema: SOURCE_OBSERVATION_SCHEMA,
            source_content_id: &self.source_content_id,
            observed: &self.observed,
        })
        .map_err(|error| SourceContentError::Invalid(format!("cannot canonicalize observation: {error}")))?;
        Ok(format!("source-observation:{:x}", Sha256::digest(&bytes)))
    }

    pub fn validate(&self) -> Result<(), SourceContentError> {
        require(
            self.schema == SOURCE_OBSERVATION_SCHEMA,
            format!("observation schema must be {SOURCE_OBSERVATION_SCHEMA}"),
        )?;
        require(
            self.source_content_id.starts_with("source-content:"),
            "observation must name a source content".to_string(),
        )?;
        match &self.observed {
            SourceObservationKindV2::ExactGit { git_commit, git_tree } => {
                require_lower_hex(git_commit, 40, "git_commit")?;
                require_lower_hex(git_tree, 40, "git_tree")?;
            }
            SourceObservationKindV2::LegacySnapshot {
                snapshot_id,
                declared_commit,
            } => {
                require(
                    !snapshot_id.is_empty(),
                    "legacy observation needs a snapshot_id".to_string(),
                )?;
                require_lower_hex(declared_commit, 40, "declared_commit")?;
            }
        }
        require(
            self.observation_id == self.expected_id()?,
            "observation_id does not address these observation facts".to_string(),
        )?;
        Ok(())
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct LegacySnapshotMapV2 {
    pub snapshot_id: String,
    pub source_content_id: String,
    pub observation_id: String,
    pub implementation_id: String,
    pub binding_id: String,
    /// Whether the snapshot declared its own files, or the declaration was
    /// reconstructed from the archive's members when it declared none.
    pub declaration_source: DeclarationSource,
}

/// What one mapping attempt established.
///
/// `Unresolved` is not stored. A damaged or absent archive is a fact about
/// today, and an immutable row saying the snapshot can never be mapped is a
/// permanent answer to a question an unmounted disk can make temporary. The
/// command reports it; the Registry stays silent until something is proven.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub enum LegacyMappingOutcome {
    Mapped(LegacySnapshotMapV2),
    Unresolved { snapshot_id: String, reason: String },
}

impl LegacyMappingOutcome {
    pub fn snapshot_id(&self) -> &str {
        match self {
            Self::Mapped(map) => &map.snapshot_id,
            Self::Unresolved { snapshot_id, .. } => snapshot_id,
        }
    }
}

/// Supplies the archive bytes for one snapshot, keyed by archived path.
///
/// `fern_run` does not understand tar. Whoever can reopen the archive proves
/// the bytes and hands them over; the Registry verifies digests and records
/// only what it verified.
/// Every member the archive holds, by path, with the kind each one extracted
/// as. All of them, not only the declared ones: a snapshot that predates
/// `archived_sources` declares nothing, and its declaration can only be
/// recovered from what the archive actually contains.
pub type LegacyArchiveMembers = BTreeMap<String, (SourceEntryKind, Vec<u8>)>;

pub type LegacyArchiveResolver<'a> =
    &'a dyn Fn(&ImplementationSourceSnapshotV1) -> Result<LegacyArchiveMembers, String>;

/// Where a mapped snapshot's file declaration came from.
///
/// The two are not the same fact. A snapshot that declared its own files is
/// being taken at its word; a snapshot whose declaration was reconstructed
/// from its archive is being given one it never wrote. Both are honest ways
/// to hold a source tree, and a reader must always be able to tell them
/// apart, so the distinction is stored rather than inferred.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum DeclarationSource {
    /// manifest.json listed the files; they were verified against the archive.
    DeclaredBySnapshot,
    /// manifest.json listed nothing; the declaration is the archive's members.
    BackfilledFromArchive,
}

impl DeclarationSource {
    pub fn as_str(self) -> &'static str {
        match self {
            DeclarationSource::DeclaredBySnapshot => "declared_by_snapshot",
            DeclarationSource::BackfilledFromArchive => "backfilled_from_archive",
        }
    }

    pub fn parse(value: &str) -> Result<Self, SourceContentError> {
        match value {
            "declared_by_snapshot" => Ok(DeclarationSource::DeclaredBySnapshot),
            "backfilled_from_archive" => Ok(DeclarationSource::BackfilledFromArchive),
            other => Err(SourceContentError::Invalid(format!(
                "unknown declaration source {other:?}"
            ))),
        }
    }
}

#[derive(Debug, thiserror::Error)]
pub enum SourceContentError {
    #[error("invalid source content: {0}")]
    Invalid(String),
    #[error("source content serialization error: {0}")]
    Serialization(#[from] serde_json::Error),
    #[error("source content filesystem error: {0}")]
    Filesystem(String),
    #[error("source content Registry error: {0}")]
    Registry(String),
    #[error("source content conflict for {object_id}: {explanation}")]
    Conflict { object_id: String, explanation: String },
}

impl From<fern_json_contract::JsonContractError> for SourceContentError {
    fn from(error: fern_json_contract::JsonContractError) -> Self {
        Self::Invalid(error.to_string())
    }
}

pub fn default_entrypoints() -> BTreeMap<String, String> {
    BTreeMap::from([
        ("python_module".to_string(), PYTHON_MODULE_ENTRYPOINT.to_string()),
        ("rust_binary".to_string(), RUST_BINARY_ENTRYPOINT.to_string()),
    ])
}

pub fn blob_path(cas_root: &Path, byte_digest: &str) -> Result<PathBuf, SourceContentError> {
    let hex = byte_digest_hex(byte_digest, "blob digest")?;
    Ok(cas_root.join(SOURCE_BLOBS_DIR).join(hex))
}

/// Distinguishes concurrent stagings of the SAME digest. A staging name
/// derived only from the digest is one shared file, so two writers of
/// identical bytes truncate each other's partial write and one of them
/// publishes a short file under a digest that no longer describes it.
static BLOB_STAGING_SEQUENCE: AtomicU64 = AtomicU64::new(0);

/// Compare a published blob against the bytes a caller is trying to store.
///
/// Content addressing makes a matching blob a success, not a conflict: the
/// bytes are already durable and the digest already describes them.
fn accept_published_blob(path: &Path, bytes: &[u8], digest: String) -> Result<String, SourceContentError> {
    let existing = fs::read(path).map_err(|error| {
        SourceContentError::Filesystem(format!("cannot reread source blob {}: {error}", path.display()))
    })?;
    if existing.as_slice() != bytes {
        return Err(SourceContentError::Conflict {
            object_id: digest,
            explanation: "existing source blob bytes do not match this digest".to_string(),
        });
    }
    Ok(digest)
}

/// Make a rename durable. `fsync` on a file persists its contents; only
/// `fsync` on the containing directory persists the name that reaches them.
/// Without this a crash can leave a registered content-address whose blob
/// has no directory entry.
fn sync_directory(directory: &Path) -> Result<(), SourceContentError> {
    fs::File::open(directory)
        .and_then(|handle| handle.sync_all())
        .map_err(|error| {
            SourceContentError::Filesystem(format!("cannot fsync directory {}: {error}", directory.display()))
        })
}

pub fn write_source_blob(cas_root: &Path, bytes: &[u8]) -> Result<String, SourceContentError> {
    let digest = format!("sha256:{:x}", Sha256::digest(bytes));
    let path = blob_path(cas_root, &digest)?;
    if path.exists() {
        return accept_published_blob(&path, bytes, digest);
    }
    let directory = path.parent().expect("blob path has a parent").to_path_buf();
    fs::create_dir_all(&directory)
        .map_err(|error| SourceContentError::Filesystem(format!("cannot create source-blob directory: {error}")))?;

    // Stage inside the destination directory so the publish is a rename
    // within one filesystem, which is the only form that is atomic.
    let staging = directory.join(format!(
        ".staging-{}-{}",
        std::process::id(),
        BLOB_STAGING_SEQUENCE.fetch_add(1, Ordering::Relaxed)
    ));
    let write_result = (|| -> std::io::Result<()> {
        let mut handle = fs::File::create(&staging)?;
        handle.write_all(bytes)?;
        handle.sync_all()
    })();
    if let Err(error) = write_result {
        let _ = fs::remove_file(&staging);
        return Err(SourceContentError::Filesystem(format!(
            "cannot write source blob {}: {error}",
            staging.display()
        )));
    }
    if let Err(error) = fs::rename(&staging, &path) {
        let _ = fs::remove_file(&staging);
        return Err(SourceContentError::Filesystem(format!(
            "cannot publish source blob {}: {error}",
            path.display()
        )));
    }
    sync_directory(&directory)?;

    // A concurrent writer may have published between the existence check and
    // the rename. Reread rather than assume: the bytes on disk are the fact,
    // and identical content makes the race a non-event.
    accept_published_blob(&path, bytes, digest)
}

pub fn write_source_content_document(
    cas_root: &Path,
    content: &SourceContentV2,
    tracked_patch: &[u8],
) -> Result<PathBuf, SourceContentError> {
    content.validate()?;
    let hex = content
        .source_content_id
        .strip_prefix("source-content:")
        .ok_or_else(|| SourceContentError::Invalid("source_content_id is missing its prefix".to_string()))?;
    let directory = cas_root.join(SOURCE_CONTENTS_DIR).join(hex);
    fs::create_dir_all(&directory).map_err(|error| {
        SourceContentError::Filesystem(format!(
            "cannot create source-content directory {}: {error}",
            directory.display()
        ))
    })?;
    let document_path = directory.join("manifest.json");
    let bytes = canonical_json_bytes(content)?;
    if document_path.exists() {
        let existing = fs::read(&document_path).map_err(|error| {
            SourceContentError::Filesystem(format!(
                "cannot reread source-content document {}: {error}",
                document_path.display()
            ))
        })?;
        let stored: SourceContentV2 = serde_json::from_slice(&existing)?;
        stored.validate()?;
        if stored.source_content_id != content.source_content_id
            || stored.files != content.files
            || stored.untracked_paths != content.untracked_paths
            || stored.cargo_lock_byte_digest != content.cargo_lock_byte_digest
            || stored.recipe_catalog_source_digest != content.recipe_catalog_source_digest
        {
            return Err(SourceContentError::Conflict {
                object_id: content.source_content_id.clone(),
                explanation: "existing source-content document disagrees with these bytes".to_string(),
            });
        }
        return Ok(directory);
    }
    fs::write(&document_path, bytes).map_err(|error| {
        SourceContentError::Filesystem(format!(
            "cannot write source-content document {}: {error}",
            document_path.display()
        ))
    })?;
    let patch_digest = write_source_blob(cas_root, tracked_patch)?;
    fs::write(directory.join("tracked_patch_digest"), format!("{patch_digest}\n"))
        .map_err(|error| SourceContentError::Filesystem(format!("cannot record tracked patch digest: {error}")))?;
    Ok(directory)
}

pub fn write_implementation_document(
    cas_root: &Path,
    implementation: &SourceImplementationV2,
) -> Result<PathBuf, SourceContentError> {
    implementation.validate()?;
    let hex = implementation
        .implementation_id
        .strip_prefix("implementation:")
        .ok_or_else(|| SourceContentError::Invalid("implementation_id is missing its prefix".to_string()))?;
    let directory = cas_root.join(IMPLEMENTATIONS_DIR).join(hex);
    fs::create_dir_all(&directory).map_err(|error| {
        SourceContentError::Filesystem(format!(
            "cannot create implementation directory {}: {error}",
            directory.display()
        ))
    })?;
    let document_path = directory.join("manifest.json");
    let bytes = canonical_json_bytes(implementation)?;
    if document_path.exists() {
        let stored: SourceImplementationV2 = serde_json::from_slice(&fs::read(&document_path).map_err(|error| {
            SourceContentError::Filesystem(format!(
                "cannot reread implementation document {}: {error}",
                document_path.display()
            ))
        })?)?;
        if stored != *implementation {
            return Err(SourceContentError::Conflict {
                object_id: implementation.implementation_id.clone(),
                explanation: "existing implementation document disagrees with these bytes".to_string(),
            });
        }
        return Ok(directory);
    }
    fs::write(&document_path, bytes).map_err(|error| {
        SourceContentError::Filesystem(format!(
            "cannot write implementation document {}: {error}",
            document_path.display()
        ))
    })?;
    Ok(directory)
}

static ROOT_STAGING_SEQUENCE: AtomicU64 = AtomicU64::new(0);

/// Materialize one SourceContent to a stable content-addressed root.
///
/// `materialize_source_content` writes straight into its destination, so an
/// interrupted call leaves a partial tree that `verify_materialized_tree` will
/// reject forever afterwards — the failure outlives the interruption. This
/// stages into a sibling directory and publishes with one rename, so a root
/// either does not exist or is complete.
///
/// An existing root is verified and reused rather than rebuilt: the bytes are
/// addressed by their content, so rebuilding could only produce the same tree.
/// Require an existing content-addressed destination to be a real directory.
///
/// A symlink here is the whole defence defeated: the path names a content
/// address, but the bytes behind it would be whatever mutable tree the link
/// points at, and every later verification would read that tree instead. Both
/// the reuse path and the concurrent-winner path go through this, because a
/// racing publisher is exactly when an attacker-or-accident-placed link is
/// least likely to be noticed.
fn require_real_directory(path: &Path) -> Result<(), SourceContentError> {
    let metadata = fs::symlink_metadata(path).map_err(|error| {
        SourceContentError::Filesystem(format!("cannot inspect published root {}: {error}", path.display()))
    })?;
    if metadata.file_type().is_symlink() {
        return Err(SourceContentError::Invalid(format!(
            "published root {} is a symbolic link; a content-addressed directory must be the bytes it names",
            path.display()
        )));
    }
    if !metadata.is_dir() {
        return Err(SourceContentError::Invalid(format!(
            "published root {} is not a directory",
            path.display()
        )));
    }
    Ok(())
}

pub fn publish_materialized_source_root(
    cas_root: &Path,
    content: &SourceContentV2,
    roots_directory: &Path,
) -> Result<PathBuf, SourceContentError> {
    content.validate()?;
    let hex = content
        .source_content_id
        .strip_prefix("source-content:")
        .ok_or_else(|| SourceContentError::Invalid("source_content_id is missing its prefix".to_string()))?;
    let destination = roots_directory.join(hex);
    if destination.exists() {
        require_real_directory(&destination)?;
        verify_materialized_tree(&destination, content)?;
        return Ok(destination);
    }
    fs::create_dir_all(roots_directory).map_err(|error| {
        SourceContentError::Filesystem(format!(
            "cannot create source-root directory {}: {error}",
            roots_directory.display()
        ))
    })?;
    let staging = roots_directory.join(format!(
        ".staging-{}-{}",
        std::process::id(),
        ROOT_STAGING_SEQUENCE.fetch_add(1, Ordering::Relaxed)
    ));
    let _ = fs::remove_dir_all(&staging);
    let staged = (|| -> Result<(), SourceContentError> {
        materialize_source_content(cas_root, content, &staging)?;
        verify_materialized_tree(&staging, content)
    })();
    if let Err(error) = staged {
        let _ = fs::remove_dir_all(&staging);
        return Err(error);
    }
    match fs::rename(&staging, &destination) {
        Ok(()) => {}
        Err(_) if destination.exists() => {
            // A concurrent publisher won. Content addressing makes that a
            // non-event, but verify rather than assume.
            let _ = fs::remove_dir_all(&staging);
            require_real_directory(&destination)?;
            verify_materialized_tree(&destination, content)?;
            return Ok(destination);
        }
        Err(error) => {
            let _ = fs::remove_dir_all(&staging);
            return Err(SourceContentError::Filesystem(format!(
                "cannot publish source root {}: {error}",
                destination.display()
            )));
        }
    }
    sync_directory(roots_directory)?;
    verify_materialized_tree(&destination, content)?;
    Ok(destination)
}

pub fn materialize_source_content(
    cas_root: &Path,
    content: &SourceContentV2,
    destination: &Path,
) -> Result<(), SourceContentError> {
    content.validate()?;
    fs::create_dir_all(destination).map_err(|error| {
        SourceContentError::Filesystem(format!(
            "cannot create materialized source root {}: {error}",
            destination.display()
        ))
    })?;
    for file in &content.files {
        let blob = blob_path(cas_root, &file.byte_digest)?;
        let bytes = fs::read(&blob).map_err(|error| {
            SourceContentError::Filesystem(format!(
                "cannot read source blob {} for {}: {error}",
                blob.display(),
                file.path
            ))
        })?;
        let observed = format!("sha256:{:x}", Sha256::digest(&bytes));
        require(
            observed == file.byte_digest && u64::try_from(bytes.len()).unwrap_or(u64::MAX) == file.bytes,
            format!("source blob for {} no longer matches the content document", file.path),
        )?;
        let target = destination.join(&file.path);
        if let Some(parent) = target.parent() {
            fs::create_dir_all(parent).map_err(|error| {
                SourceContentError::Filesystem(format!(
                    "cannot create materialized directory {}: {error}",
                    parent.display()
                ))
            })?;
        }
        match file.kind {
            SourceEntryKind::RegularFile => {
                fs::write(&target, &bytes).map_err(|error| {
                    SourceContentError::Filesystem(format!(
                        "cannot write materialized file {}: {error}",
                        target.display()
                    ))
                })?;
            }
            SourceEntryKind::SymbolicLink => {
                let target_bytes = String::from_utf8(bytes).map_err(|error| {
                    SourceContentError::Invalid(format!("symlink {} is not utf-8: {error}", file.path))
                })?;
                std::os::unix::fs::symlink(&target_bytes, &target).map_err(|error| {
                    SourceContentError::Filesystem(format!(
                        "cannot create materialized symlink {}: {error}",
                        target.display()
                    ))
                })?;
            }
        }
    }
    verify_materialized_tree(destination, content)
}

pub fn verify_materialized_tree(root: &Path, content: &SourceContentV2) -> Result<(), SourceContentError> {
    content.validate()?;
    verify_tree_files(root, &content.files, &content.source_content_id)
}

/// Byte-verify a tree against an expected file list, naming it in failures.
pub fn verify_tree_files(root: &Path, expected_files: &[SourceFileV2], label: &str) -> Result<(), SourceContentError> {
    let expected: BTreeMap<_, _> = expected_files.iter().map(|file| (file.path.as_str(), file)).collect();
    let mut observed = BTreeMap::new();
    collect_materialized_paths(root, root, &mut observed)?;
    // Undeclared files are not consulted. The declaration is a list of files
    // and their bytes; a file it never mentions cannot make it wrong.
    for (path, file) in expected {
        let Some(bytes) = observed.get(path) else {
            return Err(SourceContentError::Invalid(format!(
                "materialized source tree does not match {label}: declared file {path} is missing"
            )));
        };
        let digest = format!("sha256:{:x}", Sha256::digest(bytes));
        require(
            digest == file.byte_digest && u64::try_from(bytes.len()).unwrap_or(u64::MAX) == file.bytes,
            format!("materialized file {path} does not match {label}"),
        )?;
    }
    Ok(())
}

fn collect_materialized_paths(
    root: &Path,
    directory: &Path,
    observed: &mut BTreeMap<String, Vec<u8>>,
) -> Result<(), SourceContentError> {
    let mut entries = fs::read_dir(directory)
        .map_err(|error| {
            SourceContentError::Filesystem(format!(
                "cannot read materialized directory {}: {error}",
                directory.display()
            ))
        })?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|error| SourceContentError::Filesystem(format!("cannot enumerate materialized directory: {error}")))?;
    entries.sort_by_key(fs::DirEntry::file_name);
    for entry in entries {
        let path = entry.path();
        let metadata = fs::symlink_metadata(&path).map_err(|error| {
            SourceContentError::Filesystem(format!("cannot inspect materialized path {}: {error}", path.display()))
        })?;
        if metadata.file_type().is_dir() {
            collect_materialized_paths(root, &path, observed)?;
            continue;
        }
        let relative = path.strip_prefix(root).map_err(|error| {
            SourceContentError::Invalid(format!("materialized path escaped the source root: {error}"))
        })?;
        let relative_text = relative
            .to_str()
            .ok_or_else(|| SourceContentError::Invalid(format!("materialized path {} is not utf-8", path.display())))?;
        let bytes = if metadata.file_type().is_symlink() {
            fs::read_link(&path)
                .map_err(|error| {
                    SourceContentError::Filesystem(format!(
                        "cannot read materialized symlink {}: {error}",
                        path.display()
                    ))
                })?
                .as_os_str()
                .as_bytes()
                .to_vec()
        } else if metadata.is_file() {
            fs::read(&path).map_err(|error| {
                SourceContentError::Filesystem(format!("cannot read materialized file {}: {error}", path.display()))
            })?
        } else {
            return Err(SourceContentError::Invalid(format!(
                "materialized path {} is neither a file nor a symlink",
                path.display()
            )));
        };
        observed.insert(relative_text.replace('\\', "/"), bytes);
    }
    Ok(())
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Pass ⑤  Declared files are the identity
//
// A source content document, and a V1 snapshot manifest, name a LIST OF
// FILES. That list is what the identity means, and checking it is the whole
// of verification: every declared file present, carrying the declared bytes.
//
// What a tree holds BESIDES those files is not part of the claim. A
// `__pycache__`, a `.pyc`, an `.ai/agent.md`, someone's scratch note - none
// of them changes a declared byte, so none of them can make the declared
// source wrong. Refusing a tree for carrying them invents a problem the file
// list does not have, and that invention cost real work: six published roots
// read as polluted, and six V1 snapshots were refused as unmappable, purely
// for holding files nobody had declared.
//
// An earlier version of this pass went the other way. It sealed a published
// root read-only at publish time so that nothing could ever appear inside
// it. That approach is WITHDRAWN. The owner's principle is simpler and needs
// no cooperation from the filesystem: check what was declared, ignore the
// rest, and leave people free to write Python wherever they like.
//
// A MISSING or CHANGED declared file is still refused, and the refusal names
// the file, because that is the claim actually being broken.

#[derive(Deserialize)]
struct SourceRegistrationReceipt {
    status: String,
}

#[derive(Deserialize)]
struct LoadedJsonReceipt {
    status: String,
    canonical_json: Option<String>,
}

fn csharp_conflict(object_id: impl Into<String>, error: String) -> SourceContentError {
    SourceContentError::Conflict {
        object_id: object_id.into(),
        explanation: error,
    }
}

fn registration_outcome(status: &str) -> Result<SourceRegistrationOutcome, SourceContentError> {
    match status {
        "registered" => Ok(SourceRegistrationOutcome::Registered),
        "already_registered" => Ok(SourceRegistrationOutcome::AlreadyRegistered),
        other => Err(SourceContentError::Invalid(format!(
            "C# source-content registration returned unknown status {other:?}"
        ))),
    }
}

fn load_canonical_json(operation: &str, registry: &Path, id: &str) -> Result<Option<String>, SourceContentError> {
    let loaded: LoadedJsonReceipt =
        crate::csharp_registry::run_json(operation, registry, &serde_json::json!({ "id": id }))
            .map_err(|error| csharp_conflict(id, error))?;
    match loaded.status.as_str() {
        "missing" => Ok(None),
        "found" => Ok(loaded.canonical_json),
        other => Err(SourceContentError::Invalid(format!(
            "{operation} returned unknown status {other:?}"
        ))),
    }
}

pub fn register_source_content(
    registry: &Path,
    content: &SourceContentV2,
    registered_by: &str,
    registered_at_ms: i64,
) -> Result<SourceRegistrationOutcome, SourceContentError> {
    content.validate()?;
    require_registrar(registered_by, registered_at_ms, &content.source_content_id)?;
    let canonical_json = serde_json::to_string(content)?;
    let cargo_lock_sha256 = byte_digest_hex(&content.cargo_lock_byte_digest, "cargo_lock_byte_digest")?;
    let recipe_catalog_source_sha256 =
        byte_digest_hex(&content.recipe_catalog_source_digest, "recipe_catalog_source_digest")?;
    let receipt: SourceRegistrationReceipt = crate::csharp_registry::run_json(
        "register-source-content",
        registry,
        &serde_json::json!({
            "source_content_id": content.source_content_id,
            "schema": content.schema,
            "canonical_json": canonical_json,
            "source_tree_digest": content.source_tree_digest,
            "cargo_lock_sha256": cargo_lock_sha256,
            "recipe_catalog_source_sha256": recipe_catalog_source_sha256,
            "registered_by": registered_by,
            "registered_at_ms": registered_at_ms,
        }),
    )
    .map_err(|error| csharp_conflict(&content.source_content_id, error))?;
    registration_outcome(&receipt.status)
}

pub fn register_source_observation(
    registry: &Path,
    observation: &SourceObservationV2,
    observed_by: &str,
    observed_at_ms: i64,
) -> Result<SourceRegistrationOutcome, SourceContentError> {
    observation.validate()?;
    let canonical_json = String::from_utf8(
        canonical_json_bytes(observation)
            .map_err(|error| SourceContentError::Invalid(format!("cannot canonicalize observation: {error}")))?,
    )
    .map_err(|error| SourceContentError::Invalid(format!("observation json is not utf-8: {error}")))?;
    let (kind, git_commit, git_tree, snapshot_id, declared_commit) = match &observation.observed {
        SourceObservationKindV2::ExactGit { git_commit, git_tree } => (
            "exact_git",
            Some(git_commit.as_str()),
            Some(git_tree.as_str()),
            None,
            None,
        ),
        SourceObservationKindV2::LegacySnapshot {
            snapshot_id,
            declared_commit,
        } => (
            "legacy_snapshot",
            None,
            None,
            Some(snapshot_id.as_str()),
            Some(declared_commit.as_str()),
        ),
    };
    let receipt: SourceRegistrationReceipt = crate::csharp_registry::run_json(
        "register-source-observation",
        registry,
        &serde_json::json!({
            "observation_id": observation.observation_id,
            "schema": observation.schema,
            "canonical_json": canonical_json,
            "source_content_id": observation.source_content_id,
            "observation_kind": kind,
            "git_commit": git_commit,
            "git_tree": git_tree,
            "snapshot_id": snapshot_id,
            "declared_commit": declared_commit,
            "observed_by": observed_by,
            "observed_at_ms": observed_at_ms,
        }),
    )
    .map_err(|error| csharp_conflict(&observation.observation_id, error))?;
    registration_outcome(&receipt.status)
}

pub fn register_source_implementation(
    registry: &Path,
    implementation: &SourceImplementationV2,
    registered_by: &str,
    registered_at_ms: i64,
) -> Result<SourceRegistrationOutcome, SourceContentError> {
    implementation.validate()?;
    require_registrar(registered_by, registered_at_ms, &implementation.implementation_id)?;
    let canonical_json = serde_json::to_string(implementation)?;
    let receipt: SourceRegistrationReceipt = crate::csharp_registry::run_json(
        "register-source-implementation",
        registry,
        &serde_json::json!({
            "implementation_id": implementation.implementation_id,
            "schema": implementation.schema,
            "canonical_json": canonical_json,
            "component_content_ids": implementation.component_content_ids,
            "registered_by": registered_by,
            "registered_at_ms": registered_at_ms,
        }),
    )
    .map_err(|error| csharp_conflict(&implementation.implementation_id, error))?;
    registration_outcome(&receipt.status)
}

pub fn register_implementation_binding(
    registry: &Path,
    binding: &ImplementationBindingV2,
    registered_by: &str,
    registered_at_ms: i64,
) -> Result<SourceRegistrationOutcome, SourceContentError> {
    binding.validate()?;
    require_registrar(registered_by, registered_at_ms, &binding.binding_id)?;
    let canonical_json = serde_json::to_string(binding)?;
    let receipt: SourceRegistrationReceipt = crate::csharp_registry::run_json(
        "register-implementation-binding",
        registry,
        &serde_json::json!({
            "binding_id": binding.binding_id,
            "schema": binding.schema,
            "canonical_json": canonical_json,
            "implementation_id": binding.implementation_id,
            "research_design_id": binding.research_design_id,
            "title": binding.title,
            "reason": binding.reason,
            "registered_by": registered_by,
            "registered_at_ms": registered_at_ms,
        }),
    )
    .map_err(|error| csharp_conflict(&binding.binding_id, error))?;
    registration_outcome(&receipt.status)
}

/// Read one registered SourceContent back. The Registry row is the authority;
/// the document under `source-contents/` is a copy for humans to read.
pub fn load_source_content(
    registry: &Path,
    source_content_id: &str,
) -> Result<Option<SourceContentV2>, SourceContentError> {
    load_canonical_json("load-source-content", registry, source_content_id)?
        .map(|json| {
            let content: SourceContentV2 = serde_json::from_str(&json)?;
            content.validate()?;
            Ok(content)
        })
        .transpose()
}

pub fn load_implementation_binding(
    registry: &Path,
    binding_id: &str,
) -> Result<Option<ImplementationBindingV2>, SourceContentError> {
    load_canonical_json("load-implementation-binding", registry, binding_id)?
        .map(|json| {
            let binding: ImplementationBindingV2 = serde_json::from_str(&json)?;
            binding.validate()?;
            Ok(binding)
        })
        .transpose()
}

pub fn load_source_implementation(
    registry: &Path,
    implementation_id: &str,
) -> Result<Option<SourceImplementationV2>, SourceContentError> {
    load_canonical_json("load-source-implementation", registry, implementation_id)?
        .map(|json| {
            let implementation: SourceImplementationV2 = serde_json::from_str(&json)?;
            implementation.validate()?;
            Ok(implementation)
        })
        .transpose()
}

/// Everything an apply would write for one snapshot, built from bytes that
/// were re-hashed against the manifest.
///
/// Constructing this IS the proof. There is no way to hold one without having
/// verified every declared file, which is what lets the preview say "ready to
/// map" and mean "apply will succeed".
struct ProvenLegacySnapshot {
    content: SourceContentV2,
    observation: SourceObservationV2,
    implementation: SourceImplementationV2,
    binding: ImplementationBindingV2,
    /// Declared path and its digest-checked bytes, ready to publish.
    verified_files: Vec<(String, Vec<u8>)>,
    /// Whether the snapshot declared these files or the archive supplied them.
    declaration_source: DeclarationSource,
}

/// The one verification preview and apply share.
///
/// `Err` carries the sentence a person reads. It deliberately does not
/// separate "not proven yet" from "cannot be proven": that is a fact about
/// today's disk, and the Registry stays silent about it either way.
fn prove_legacy_snapshot(
    snapshot: &ImplementationSourceSnapshotV1,
    archive_members: &LegacyArchiveMembers,
) -> Result<ProvenLegacySnapshot, String> {
    snapshot.validate().map_err(|error| error.to_string())?;

    // Verify before writing anything: a half-written CAS plus a failure is
    // worse than no attempt, because the blobs look like evidence.
    let mut files = Vec::new();
    let mut verified_files = Vec::new();
    let declaration_source = if snapshot.archived_sources.is_empty() {
        // Six snapshots predate `archived_sources` and declare nothing, so
        // there is no author-written list to verify. Their archives are
        // intact, and an archive IS a list of files with bytes, so the
        // declaration is recovered from the members themselves. The bytes
        // still decide the content address, exactly as for any other
        // snapshot; what differs is only who wrote the list down, and that
        // is recorded rather than quietly forgotten.
        if archive_members.is_empty() {
            return Err("snapshot declares no files and its archive holds none either".to_string());
        }
        for (path, (kind, bytes)) in archive_members {
            files.push(SourceFileV2 {
                path: path.clone(),
                kind: kind.clone(),
                byte_digest: format!("sha256:{:x}", Sha256::digest(bytes)),
                bytes: bytes.len() as u64,
            });
            verified_files.push((path.clone(), bytes.clone()));
        }
        DeclarationSource::BackfilledFromArchive
    } else {
        for declared in &snapshot.archived_sources {
            let Some((kind, bytes)) = archive_members.get(&declared.path) else {
                return Err(format!("archive does not contain {}", declared.path));
            };
            if kind != &declared.kind {
                return Err(format!(
                    "{} extracted as {kind:?}, but the manifest declares {:?}",
                    declared.path, declared.kind
                ));
            }
            let observed = format!("sha256:{:x}", Sha256::digest(bytes));
            if observed != declared.byte_digest {
                return Err(format!(
                    "{} hashes to {observed}, but the manifest declares {}",
                    declared.path, declared.byte_digest
                ));
            }
            if bytes.len() as u64 != declared.bytes {
                return Err(format!(
                    "{} is {} bytes, but the manifest declares {}",
                    declared.path,
                    bytes.len(),
                    declared.bytes
                ));
            }
            files.push(file_from_archived(declared));
            verified_files.push((declared.path.clone(), bytes.clone()));
        }
        DeclarationSource::DeclaredBySnapshot
    };

    let untracked_paths = snapshot
        .untracked_sources
        .iter()
        .map(|source| source.path.clone())
        .collect();
    let content = SourceContentV2::from_files(
        files,
        untracked_paths,
        snapshot.cargo_lock_byte_digest.clone(),
        snapshot.recipe_catalog_source_digest.clone(),
    )
    .map_err(|error| error.to_string())?;
    // The snapshot declared a commit; nothing here resolved a tree, and the
    // commit id is not one. Recording the declaration is honest; substituting
    // it for a tree would not be.
    let observation = SourceObservationV2::new(
        content.source_content_id.clone(),
        SourceObservationKindV2::LegacySnapshot {
            snapshot_id: snapshot.snapshot_id.clone(),
            declared_commit: snapshot.base_commit.clone(),
        },
    )
    .map_err(|error| error.to_string())?;
    let implementation = SourceImplementationV2::from_content(&content).map_err(|error| error.to_string())?;
    let binding = ImplementationBindingV2::new(
        implementation.implementation_id.clone(),
        snapshot.research_design_id.clone(),
        snapshot.title.clone(),
        String::new(),
    )
    .map_err(|error| error.to_string())?;
    Ok(ProvenLegacySnapshot {
        content,
        observation,
        implementation,
        binding,
        verified_files,
        declaration_source,
    })
}

/// The four object ids one proven snapshot would register, and what publishing
/// its blobs would cost.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct ReadyLegacySnapshotV1 {
    pub source_content_id: String,
    pub observation_id: String,
    pub implementation_id: String,
    pub binding_id: String,
    /// Content addresses this snapshot would publish that the CAS does not
    /// hold yet, and the bytes each costs. Two snapshots sharing a blob both
    /// list it; the audit charges for it once.
    pub absent_blobs: BTreeMap<String, u64>,
}

/// What one V1 snapshot row — or one store directory no row claims — is now.
///
/// Preview and apply decide through this one type, so the preview can never
/// promise a write that apply would refuse.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub enum LegacySnapshotClass {
    /// Mapped by an earlier run. Apply reports it and rewrites nothing.
    AlreadyMapped(LegacySnapshotMapV2),
    /// Registered, the archive reopened, every declared file re-hashed.
    RegisteredArchiveValid(ReadyLegacySnapshotV1),
    /// Registered, but the store holds no directory of that identity. A row
    /// whose archive is absent is unresolved, not orphaned.
    RegisteredDirectoryMissing,
    /// The directory is present and disagrees with what it should hold.
    ManifestOrArchiveMismatch { reason: String },
    /// A store directory no Registry row claims. Reported, never registered:
    /// inferring ownership is the owner's decision, not the mapper's.
    DirectoryWithoutRegistryRow { manifest_title: Option<String> },
}

/// One audited thing: a snapshot identity, or a directory name for an orphan.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct LegacySnapshotAuditEntryV1 {
    pub identity: String,
    pub class: LegacySnapshotClass,
}

/// Rows an apply would insert, per table.
///
/// Not five per snapshot. SourceContent and Implementation ids are content
/// addresses, so two snapshots of one tree share both rows, and every writer
/// here is `on_conflict`, so an id already present costs nothing. Only the
/// LegacyMap is reliably one row per snapshot. Counting five per snapshot
/// would overstate the writes to whoever authorizes the apply.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
pub struct LegacyWritePlanV1 {
    pub source_contents: usize,
    pub source_observations: usize,
    pub source_implementations: usize,
    pub implementation_bindings: usize,
    pub legacy_source_snapshot_maps: usize,
}

impl LegacyWritePlanV1 {
    pub fn total(&self) -> usize {
        self.source_contents
            + self.source_observations
            + self.source_implementations
            + self.implementation_bindings
            + self.legacy_source_snapshot_maps
    }
}

/// The read-only answer to "what would an apply do".
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct LegacySnapshotAuditV1 {
    pub entries: Vec<LegacySnapshotAuditEntryV1>,
    pub already_mapped: usize,
    pub registered_archive_valid: usize,
    pub registered_directory_missing: usize,
    pub manifest_or_archive_mismatch: usize,
    pub directory_without_registry_row: usize,
    pub planned_writes: LegacyWritePlanV1,
    pub cas_byte_increase: u64,
    /// Registered rows that cannot be mapped today. The orphan directory is
    /// NOT counted: it is not a V1 snapshot, so it cannot be an unmapped one.
    pub unresolved: usize,
}

/// The per-snapshot decision, for one snapshot.
///
/// `store_directories` maps a directory name to its manifest title when that
/// manifest parses. `fern_run` does not know the store's layout, so whoever
/// can read it supplies both — the same division as `LegacyArchiveResolver`.
pub fn classify_legacy_source_snapshot(
    registry: &Path,
    snapshot: &ImplementationSourceSnapshotV1,
    store_directories: &BTreeMap<String, Option<String>>,
    resolve: LegacyArchiveResolver<'_>,
    cas_root: &Path,
) -> Result<LegacySnapshotClass, SourceContentError> {
    // A proven mapping stays proven. Re-running after the archive has gone
    // offline must return what was verified, not demote it to unresolved.
    if let Some(existing) = load_legacy_map(registry, &snapshot.snapshot_id)? {
        return Ok(LegacySnapshotClass::AlreadyMapped(existing));
    }
    // Asked before the resolver, which collapses "no directory" and "bad
    // directory" into one error string. Those are different facts: one is a
    // disk that is not mounted, the other is evidence that disagrees with the
    // Registry, and only the second says anything about the snapshot.
    if !store_directories.contains_key(&snapshot.snapshot_id) {
        return Ok(LegacySnapshotClass::RegisteredDirectoryMissing);
    }
    let archive_files = match resolve(snapshot) {
        Ok(files) => files,
        Err(reason) => return Ok(LegacySnapshotClass::ManifestOrArchiveMismatch { reason }),
    };
    let proven = match prove_legacy_snapshot(snapshot, &archive_files) {
        Ok(proven) => proven,
        Err(reason) => return Ok(LegacySnapshotClass::ManifestOrArchiveMismatch { reason }),
    };
    // Costed from the PROVEN content, not from the snapshot's own list: a
    // backfilled snapshot declares nothing, so keying on `archived_sources`
    // would price its whole tree at zero bytes.
    let mut absent_blobs = BTreeMap::new();
    for file in &proven.content.files {
        if !blob_path(cas_root, &file.byte_digest)?.exists() {
            absent_blobs.insert(file.byte_digest.clone(), file.bytes);
        }
    }
    Ok(LegacySnapshotClass::RegisteredArchiveValid(ReadyLegacySnapshotV1 {
        source_content_id: proven.content.source_content_id,
        observation_id: proven.observation.observation_id,
        implementation_id: proven.implementation.implementation_id,
        binding_id: proven.binding.binding_id,
        absent_blobs,
    }))
}

fn source_observation_exists(registry: &Path, observation_id: &str) -> Result<bool, SourceContentError> {
    Ok(load_canonical_json("load-source-observation", registry, observation_id)?.is_some())
}

/// Classify every registered V1 snapshot and every store directory, and total
/// what an apply would write. Writes nothing.
pub fn audit_legacy_source_snapshots(
    registry: &Path,
    resolve: LegacyArchiveResolver<'_>,
    store_directories: &BTreeMap<String, Option<String>>,
    cas_root: &Path,
) -> Result<LegacySnapshotAuditV1, SourceContentError> {
    let stored: Vec<String> =
        crate::csharp_registry::run_json("list-source-snapshot-manifests", registry, &serde_json::json!({}))
            .map_err(|error| csharp_conflict("legacy-audit", error))?;

    let mut audit = LegacySnapshotAuditV1 {
        entries: Vec::with_capacity(stored.len()),
        already_mapped: 0,
        registered_archive_valid: 0,
        registered_directory_missing: 0,
        manifest_or_archive_mismatch: 0,
        directory_without_registry_row: 0,
        planned_writes: LegacyWritePlanV1::default(),
        cas_byte_increase: 0,
        unresolved: 0,
    };
    // Sets, not counters: two snapshots of one tree share a SourceContent and
    // an Implementation, so a per-snapshot tally would promise writes that
    // never happen.
    let mut new_contents = std::collections::BTreeSet::new();
    let mut new_observations = std::collections::BTreeSet::new();
    let mut new_implementations = std::collections::BTreeSet::new();
    let mut new_bindings = std::collections::BTreeSet::new();
    let mut absent_blobs: BTreeMap<String, u64> = BTreeMap::new();
    let mut registered = std::collections::BTreeSet::new();

    for json in stored {
        let snapshot: ImplementationSourceSnapshotV1 = serde_json::from_str(&json)?;
        registered.insert(snapshot.snapshot_id.clone());
        let class = classify_legacy_source_snapshot(registry, &snapshot, store_directories, resolve, cas_root)?;
        match &class {
            LegacySnapshotClass::AlreadyMapped(_) => audit.already_mapped += 1,
            LegacySnapshotClass::RegisteredArchiveValid(ready) => {
                audit.registered_archive_valid += 1;
                audit.planned_writes.legacy_source_snapshot_maps += 1;
                if load_source_content(registry, &ready.source_content_id)?.is_none() {
                    new_contents.insert(ready.source_content_id.clone());
                }
                if !source_observation_exists(registry, &ready.observation_id)? {
                    new_observations.insert(ready.observation_id.clone());
                }
                if load_source_implementation(registry, &ready.implementation_id)?.is_none() {
                    new_implementations.insert(ready.implementation_id.clone());
                }
                if load_implementation_binding(registry, &ready.binding_id)?.is_none() {
                    new_bindings.insert(ready.binding_id.clone());
                }
                for (digest, bytes) in &ready.absent_blobs {
                    absent_blobs.insert(digest.clone(), *bytes);
                }
            }
            LegacySnapshotClass::RegisteredDirectoryMissing => {
                audit.registered_directory_missing += 1;
                audit.unresolved += 1;
            }
            LegacySnapshotClass::ManifestOrArchiveMismatch { .. } => {
                audit.manifest_or_archive_mismatch += 1;
                audit.unresolved += 1;
            }
            // Unreachable for a registered row; a directory cannot be orphaned
            // and claimed at once.
            LegacySnapshotClass::DirectoryWithoutRegistryRow { .. } => {}
        }
        audit.entries.push(LegacySnapshotAuditEntryV1 {
            identity: snapshot.snapshot_id.clone(),
            class,
        });
    }

    for (directory, manifest_title) in store_directories {
        if registered.contains(directory) {
            continue;
        }
        audit.directory_without_registry_row += 1;
        audit.entries.push(LegacySnapshotAuditEntryV1 {
            identity: directory.clone(),
            class: LegacySnapshotClass::DirectoryWithoutRegistryRow {
                manifest_title: manifest_title.clone(),
            },
        });
    }

    audit.planned_writes.source_contents = new_contents.len();
    audit.planned_writes.source_observations = new_observations.len();
    audit.planned_writes.source_implementations = new_implementations.len();
    audit.planned_writes.implementation_bindings = new_bindings.len();
    audit.cas_byte_increase = absent_blobs.values().sum();
    Ok(audit)
}

/// Register one V1 snapshot as V2 objects, from bytes that were proven.
///
/// The caller supplies the archive's members; this function re-hashes every
/// declared file against the manifest before anything is written, so a
/// mapping means the source can actually be rebuilt. A manifest that merely
/// parses is not evidence: the archive is what a later reader has to reopen.
/// When the manifest declares nothing at all, the members become the
/// declaration, and the stored row records that they did.
pub fn map_legacy_source_snapshot(
    registry: &Path,
    snapshot: &ImplementationSourceSnapshotV1,
    archive_files: &LegacyArchiveMembers,
    cas_root: &Path,
    mapped_at_ms: i64,
) -> Result<LegacyMappingOutcome, SourceContentError> {
    // A proven mapping stays proven. Re-running after the archive has gone
    // offline must return what was verified, not demote it to unresolved.
    if let Some(existing) = load_legacy_map(registry, &snapshot.snapshot_id)? {
        return Ok(LegacyMappingOutcome::Mapped(existing));
    }
    let proven = match prove_legacy_snapshot(snapshot, archive_files) {
        Ok(proven) => proven,
        Err(reason) => {
            return Ok(LegacyMappingOutcome::Unresolved {
                snapshot_id: snapshot.snapshot_id.clone(),
                reason,
            });
        }
    };
    for (_, bytes) in &proven.verified_files {
        write_source_blob(cas_root, bytes)?;
    }
    register_source_content(registry, &proven.content, "legacy-map", mapped_at_ms)?;
    register_source_observation(registry, &proven.observation, "legacy-map", mapped_at_ms)?;
    register_source_implementation(registry, &proven.implementation, "legacy-map", mapped_at_ms)?;
    register_implementation_binding(registry, &proven.binding, "legacy-map", mapped_at_ms)?;
    let map = store_legacy_map(
        registry,
        &snapshot.snapshot_id,
        &proven.content.source_content_id,
        &proven.observation.observation_id,
        &proven.implementation.implementation_id,
        &proven.binding.binding_id,
        mapped_at_ms,
        proven.declaration_source,
    )?;
    Ok(LegacyMappingOutcome::Mapped(map))
}

/// Map every registered V1 snapshot, reporting the ones that could not be
/// proven.
///
/// Apply runs the audit first and maps only what the audit called ready, so
/// the sentence a person authorized and the writes that follow come from one
/// classification rather than two that happen to agree.
pub fn map_all_legacy_source_snapshots(
    registry: &Path,
    resolve: LegacyArchiveResolver<'_>,
    store_directories: &BTreeMap<String, Option<String>>,
    cas_root: &Path,
    mapped_at_ms: i64,
) -> Result<Vec<LegacyMappingOutcome>, SourceContentError> {
    let audit = audit_legacy_source_snapshots(registry, resolve, store_directories, cas_root)?;
    let stored: Vec<String> =
        crate::csharp_registry::run_json("list-source-snapshot-manifests", registry, &serde_json::json!({}))
            .map_err(|error| csharp_conflict("legacy-map", error))?;
    let by_id: BTreeMap<String, ImplementationSourceSnapshotV1> = stored
        .into_iter()
        .map(|json| {
            let snapshot: ImplementationSourceSnapshotV1 = serde_json::from_str(&json)?;
            Ok((snapshot.snapshot_id.clone(), snapshot))
        })
        .collect::<Result<_, SourceContentError>>()?;

    let mut outcomes = Vec::with_capacity(audit.entries.len());
    for entry in &audit.entries {
        match &entry.class {
            LegacySnapshotClass::AlreadyMapped(map) => outcomes.push(LegacyMappingOutcome::Mapped(map.clone())),
            LegacySnapshotClass::RegisteredArchiveValid(_) => {
                let snapshot = by_id
                    .get(&entry.identity)
                    .expect("the audit classified a row this query returned");
                let archive_files = resolve(snapshot).map_err(SourceContentError::Invalid)?;
                outcomes.push(map_legacy_source_snapshot(
                    registry,
                    snapshot,
                    &archive_files,
                    cas_root,
                    mapped_at_ms,
                )?);
            }
            LegacySnapshotClass::RegisteredDirectoryMissing => outcomes.push(LegacyMappingOutcome::Unresolved {
                snapshot_id: entry.identity.clone(),
                reason: "no snapshot directory of that identity is in the store".to_string(),
            }),
            LegacySnapshotClass::ManifestOrArchiveMismatch { reason } => {
                outcomes.push(LegacyMappingOutcome::Unresolved {
                    snapshot_id: entry.identity.clone(),
                    reason: reason.clone(),
                })
            }
            // Not a V1 snapshot row, so not something a mapping can resolve.
            LegacySnapshotClass::DirectoryWithoutRegistryRow { .. } => {}
        }
    }
    Ok(outcomes)
}

pub fn load_legacy_map(registry: &Path, snapshot_id: &str) -> Result<Option<LegacySnapshotMapV2>, SourceContentError> {
    #[derive(Deserialize)]
    struct LegacyMapReceipt {
        snapshot_id: String,
        source_content_id: String,
        observation_id: String,
        implementation_id: String,
        binding_id: String,
        declaration_source: String,
    }
    let row: Option<LegacyMapReceipt> = crate::csharp_registry::run_optional_json(
        "load-legacy-source-map",
        registry,
        &serde_json::json!({ "id": snapshot_id }),
    )
    .map_err(|error| csharp_conflict(snapshot_id, error))?;
    row.map(|row| {
        Ok(LegacySnapshotMapV2 {
            snapshot_id: row.snapshot_id,
            source_content_id: row.source_content_id,
            observation_id: row.observation_id,
            implementation_id: row.implementation_id,
            binding_id: row.binding_id,
            declaration_source: DeclarationSource::parse(&row.declaration_source)?,
        })
    })
    .transpose()
}

#[allow(clippy::too_many_arguments)]
fn store_legacy_map(
    registry: &Path,
    snapshot_id: &str,
    source_content_id: &str,
    observation_id: &str,
    implementation_id: &str,
    binding_id: &str,
    mapped_at_ms: i64,
    declaration_source: DeclarationSource,
) -> Result<LegacySnapshotMapV2, SourceContentError> {
    crate::csharp_registry::run_json::<_, serde_json::Value>(
        "store-legacy-source-map",
        registry,
        &serde_json::json!({
            "snapshot_id": snapshot_id,
            "source_content_id": source_content_id,
            "observation_id": observation_id,
            "implementation_id": implementation_id,
            "binding_id": binding_id,
            "mapped_at_ms": mapped_at_ms,
            "declaration_source": declaration_source.as_str(),
        }),
    )
    .map_err(|error| csharp_conflict(snapshot_id, error))?;
    let stored = load_legacy_map(registry, snapshot_id)?
        .ok_or_else(|| SourceContentError::Invalid(format!("legacy map for {snapshot_id} was not stored")))?;
    // One snapshot has one mapping. A second run that proves different objects
    // is a contradiction about the same bytes, not an update.
    require(
        stored.source_content_id == source_content_id
            && stored.observation_id == observation_id
            && stored.implementation_id == implementation_id
            && stored.binding_id == binding_id
            && stored.declaration_source == declaration_source,
        format!("snapshot {snapshot_id} is already mapped to different objects"),
    )?;
    Ok(stored)
}

fn file_from_archived(source: &ArchivedSourceV1) -> SourceFileV2 {
    SourceFileV2 {
        path: source.path.clone(),
        kind: source.kind.clone(),
        byte_digest: source.byte_digest.clone(),
        bytes: source.bytes,
    }
}

pub fn files_from_untracked(sources: &[UntrackedSourceV1]) -> Vec<SourceFileV2> {
    sources
        .iter()
        .map(|source| SourceFileV2 {
            path: source.path.clone(),
            kind: source.kind.clone(),
            byte_digest: source.byte_digest.clone(),
            bytes: source.bytes,
        })
        .collect()
}

fn require_registrar(registered_by: &str, registered_at_ms: i64, object_id: &str) -> Result<(), SourceContentError> {
    if registered_by.trim().is_empty() || registered_by.trim() != registered_by {
        return Err(SourceContentError::Conflict {
            object_id: object_id.to_string(),
            explanation: "registered_by must be trimmed, non-empty text".to_string(),
        });
    }
    if registered_at_ms < 0 {
        return Err(SourceContentError::Conflict {
            object_id: object_id.to_string(),
            explanation: "registered_at_ms must be non-negative".to_string(),
        });
    }
    Ok(())
}

fn digest_preimage<T: Serialize>(value: &T) -> Result<String, SourceContentError> {
    let bytes = canonical_json_bytes(value)?;
    Ok(format!("{:x}", Sha256::digest(&bytes)))
}

fn digest_prefixed<T: Serialize>(value: &T) -> Result<String, SourceContentError> {
    Ok(format!("sha256:{}", digest_preimage(value)?))
}

fn byte_digest_hex<'a>(value: &'a str, field: &str) -> Result<&'a str, SourceContentError> {
    let hex = value
        .strip_prefix("sha256:")
        .ok_or_else(|| SourceContentError::Invalid(format!("{field} must start with sha256:")))?;
    require_lower_hex(hex, 64, field)?;
    Ok(hex)
}

fn require_lower_hex(value: &str, length: usize, field: &str) -> Result<(), SourceContentError> {
    require(
        value.len() == length
            && value
                .bytes()
                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte)),
        format!("{field} must be exactly {length} lowercase hexadecimal characters"),
    )
}

fn validate_relative_path(path: &str, field: &str) -> Result<(), SourceContentError> {
    let parsed = Path::new(path);
    require(
        !path.is_empty()
            && parsed
                .components()
                .all(|component| matches!(component, Component::Normal(_))),
        format!("{field} {path:?} is not a safe relative path"),
    )
}

fn require(condition: bool, explanation: String) -> Result<(), SourceContentError> {
    if condition {
        Ok(())
    } else {
        Err(SourceContentError::Invalid(explanation))
    }
}

// ── The runtime self-report gate ────────────────────────────────────────────
//
// An Implementation's registered capability is a promise about what the code on
// a Worker Site can do. Nothing re-checks that promise unless somebody asks the
// code itself, which is what these three functions are for: derive the digest
// from what the Site actually reports, find which Implementation the Site's
// published source resolves to, and refuse when the two disagree.

/// The capability digest of a bundle the Site just reported about itself.
///
/// It builds the same draft `reprobe_implementation_bundle_v2` builds and hands
/// it to the SAME digest function that minted the digest stored on the
/// Implementation record. Two digests over one concept is how a check starts
/// passing while meaning nothing, so there is one function and this projects
/// into it.
pub fn observed_capability_digest(
    resolution: &fern_recipes::ImplementationBundleResolutionV2,
) -> Result<String, SourceContentError> {
    let draft = fern_recipes::ImplementationBundleDraftV2 {
        schema: fern_recipes::IMPLEMENTATION_BUNDLE_DRAFT_SCHEMA_V2.to_string(),
        runtime: resolution.runtime.clone(),
        protocol_version: resolution.protocol_version,
        source_files: resolution.source_files.iter().map(|file| file.path.clone()).collect(),
        dependency_lock_files: resolution
            .dependency_lock_files
            .iter()
            .map(|file| file.path.clone())
            .collect(),
        active_feature_flags: resolution.active_feature_flags.clone(),
    };
    bundle_capability_digest(&draft)
}

/// The Implementation a published source snapshot resolves to, through the
/// Binding that maps them.
pub fn implementation_for_source_snapshot(
    registry: &Path,
    snapshot_id: &str,
) -> Result<Option<String>, SourceContentError> {
    let loaded: LoadedJsonReceipt = crate::csharp_registry::run_json(
        "implementation-for-source-snapshot",
        registry,
        &serde_json::json!({ "id": snapshot_id }),
    )
    .map_err(|error| csharp_conflict(snapshot_id, error))?;
    match loaded.status.as_str() {
        "missing" => Ok(None),
        "found" => Ok(loaded.canonical_json),
        other => Err(SourceContentError::Invalid(format!(
            "implementation-for-source-snapshot returned unknown status {other:?}"
        ))),
    }
}

/// Which convention minted a registered capability digest, when one matches.
///
/// Three digests have been in use. Keeping them named rather than folded into
/// a boolean is the difference between "the code agrees" and "the code agrees,
/// and here is the shape of the claim it agreed with" -- which is what a person
/// reading a receipt six months from now needs.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CapabilityConvention {
    /// The resolved import closure: the list that describes the running code.
    ResolvedBundle,
    /// The authored bundle draft's declared file list.
    AuthoredBundleDraft,
    /// The entrypoint table alone, from before a capability digest meant
    /// anything. Every Implementation frozen that way carries it.
    EntrypointContract,
}

impl CapabilityConvention {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::ResolvedBundle => "resolved-bundle",
            Self::AuthoredBundleDraft => "authored-bundle-draft",
            Self::EntrypointContract => "entrypoint-contract",
        }
    }

    /// What a match under this convention is worth as evidence.
    ///
    /// Only the resolved closure describes the code that runs, so only a match
    /// against it verifies anything. The entrypoint table names the module a
    /// worker will import and nothing about the source behind it: two workers
    /// built from completely different trees produce the same entrypoint
    /// digest, so a match there says the record is OLD, not that the code is
    /// right. Calling that "verified" is the lie this distinction exists to
    /// stop.
    pub fn verification_class(self) -> &'static str {
        match self {
            Self::ResolvedBundle => "verified",
            Self::AuthoredBundleDraft | Self::EntrypointContract => "legacy-unverified",
        }
    }

    pub fn verifies_running_code(self) -> bool {
        matches!(self, Self::ResolvedBundle)
    }
}

/// Whether an Implementation's registered capability can describe running code
/// at all, decided from the record alone.
///
/// A record whose capability digest is exactly the digest of its own entrypoint
/// table carries no statement about source: nothing about the files, the locks
/// or the feature flags entered it. No probe is needed to know that, which is
/// why this can run at Plan registration, before any worker exists.
pub fn capability_is_legacy_entrypoint_only(
    implementation: &SourceImplementationV2,
) -> Result<bool, SourceContentError> {
    Ok(implementation.capability_digest == entrypoint_contract_digest(&implementation.entrypoints)?)
}

/// Refuse a NEW Plan on an Implementation whose capability cannot be checked.
///
/// An already-registered Plan is untouched: its pin is its pin, and stopping
/// work that is already under way to enforce a rule about future work throws
/// away compute for nothing. But a Plan being registered TODAY can name a
/// re-frozen Implementation instead, so it must.
pub fn refuse_new_plan_on_legacy_capability(
    implementation: &SourceImplementationV2,
) -> Result<Option<String>, SourceContentError> {
    if !capability_is_legacy_entrypoint_only(implementation)? {
        return Ok(None);
    }
    Ok(Some(format!(
        "implementation {} carries a legacy-unverified capability: its digest is the digest of its own entrypoint table, which names the module a worker imports and nothing about the source behind it, so no running worker can ever be checked against it. Re-freeze from the resolved bundle: fern-run freeze-source --implementation-bundle <resolution>",
        implementation.implementation_id
    )))
}

/// Refuse when the running code does not report the capability its
/// Implementation was registered with.
///
/// The registered digest is a claim made when the Implementation was frozen.
/// Between then and now the Worker Site may have been rebuilt, redeployed from
/// a different tree, or had its lock files moved, and every one of those makes
/// the record a statement about code that is no longer there. Checking it here
/// costs one row read and refuses before any data is read or any GPU is
/// touched.
///
/// It accepts under any convention it can REPRODUCE from what it holds, and
/// says which one matched. That matters because the digest convention changed
/// twice: the 23 Implementations a Registry Home reaches through its legacy
/// snapshot maps were all minted from the entrypoint table alone, and a gate
/// that only knew the current convention would refuse every one of them --
/// a check that refuses correct work teaches its reader to skip it.
///
/// The authored-draft convention is deliberately NOT in the accept set. It
/// cannot be reproduced here: the resolved closure does not mark which of its
/// files a person originally listed, so there is no way to recompute that
/// digest from what the worker reports. A record minted that way is refused
/// with the instruction that fixes it, rather than passed on a guess.
///
/// An Implementation Registry Home does not know is NOT refused: this gate
/// exists to catch disagreement, and silence is not disagreement.
pub fn require_registered_capability(
    registry: &Path,
    implementation_id: &str,
    observed_capability_digest: &str,
) -> Result<Option<CapabilityConvention>, SourceContentError> {
    let Some(implementation) = load_source_implementation(registry, implementation_id)? else {
        return Ok(None);
    };
    require_registered_capability_from_record(&implementation, observed_capability_digest)
}

/// Compare an already loaded Implementation record to the capability a worker reports.
pub fn require_registered_capability_from_record(
    implementation: &SourceImplementationV2,
    observed_capability_digest: &str,
) -> Result<Option<CapabilityConvention>, SourceContentError> {
    if implementation.capability_digest == observed_capability_digest {
        return Ok(Some(CapabilityConvention::ResolvedBundle));
    }
    // The pre-capability convention, recomputed from the record's OWN entrypoint
    // table rather than from a constant written here, so a record with different
    // entrypoints cannot be waved through by a stale copy of the old value.
    let entrypoint_contract = entrypoint_contract_digest(&implementation.entrypoints)?;
    if implementation.capability_digest == entrypoint_contract {
        return Ok(Some(CapabilityConvention::EntrypointContract));
    }
    Err(SourceContentError::Conflict {
        object_id: implementation.implementation_id.clone(),
        explanation: format!(
            "implementation {} registered capability {}, worker reports {observed_capability_digest}; rebuild/redeploy the Worker Site. If this Implementation was frozen from an authored bundle DRAFT, its digest covers the files a person listed rather than the import closure the worker loads, and no rebuild can reconcile the two: re-freeze it from the resolved bundle instead.",
            implementation.implementation_id, implementation.capability_digest
        ),
    })
}

/// Record that the running code reported the capability it was registered with.
///
/// A gate that only ever speaks when it refuses leaves no evidence that it ran,
/// which is how a check quietly stops running. This writes the agreement into
/// the same append-only receipt ledger every other decision here uses.
pub fn record_capability_self_report(
    registry: &Path,
    implementation_id: &str,
    capability_digest: &str,
    convention: CapabilityConvention,
    attempt_id: Option<&str>,
    actor: &crate::lifecycle::LifecycleActor,
) -> Result<String, SourceContentError> {
    let loaded: LoadedJsonReceipt = crate::csharp_registry::run_json(
        "record-capability-self-report",
        registry,
        &serde_json::json!({
            "implementation_id": implementation_id,
            "capability_digest": capability_digest,
            "convention": convention.as_str(),
            "verification_class": convention.verification_class(),
            "verifies_running_code": convention.verifies_running_code(),
            "actor_kind": actor.kind.as_str(),
            "actor_id": actor.actor_id,
            "attempt_id": attempt_id,
        }),
    )
    .map_err(|error| csharp_conflict(implementation_id, error))?;
    loaded
        .canonical_json
        .ok_or_else(|| SourceContentError::Invalid("capability self-report did not return a receipt id".to_string()))
}

#[cfg(test)]
mod golden_for_the_csharp_port {
    //! The source-content identities C# must reproduce.
    //!
    //! Written from the Rust while it still runs, because a port checked
    //! against values the porter chose has been checked against nothing. Asked
    //! for by name by the C# lane, which correctly refused to port this family
    //! on self-consistent tests.
    //!
    //! WHAT THIS PINS, and each of the three is a different way a port goes
    //! wrong silently:
    //!
    //! 1. `canonical_json_bytes` SORTS keys at every depth. This family is
    //!    sorted; the architecture-card family two crates away is DECLARATION
    //!    ORDER. A port that picks one rule and applies it to both renames one
    //!    of them, and both sides stay valid sha256 over valid JSON.
    //!
    //! 2. `entrypoint_contract_digest` is SKIPPED WHEN NONE -- inside the
    //!    sorted family. So this family has a nulls-written cousin and an
    //!    omitting member of its own, and only the bytes say which is which.
    //!    Both variants are here.
    //!
    //! 3. The binding carries FREE TEXT. `title` and `reason` are the only
    //!    unrestricted strings in the whole family, and the entry below puts an
    //!    embedded quote and a non-ASCII character in both. That single entry
    //!    settles a question that is currently open: C#'s
    //!    `IdentityCanonicalJson` uses `UnsafeRelaxedJsonEscaping`, whose
    //!    sibling encoder wrote `"` where serde writes `\"` and produced a
    //!    real defect one crate over on 2026-08-26. Against these bytes it is
    //!    proved or refuted rather than assumed.
    //!
    //! Every entry carries the exact preimage AS A STRING beside its id, so a
    //! C# mismatch says which byte differs rather than only that two digests
    //! disagree.

    use super::*;

    fn file(path: &str, digest: &str, bytes: u64) -> SourceFileV2 {
        SourceFileV2 {
            path: path.to_string(),
            kind: crate::implementation_source_snapshot::SourceEntryKind::RegularFile,
            // `sha256:` plus exactly 64 lowercase hex. The validator checks
            // both, so an abbreviated fixture would never reach the identity
            // this golden exists to pin.
            byte_digest: format!("sha256:{}", digest.repeat(32)),
            bytes,
        }
    }

    #[test]
    fn write_the_source_content_identities_the_csharp_port_must_reproduce() {
        // Given UNSORTED and with an untracked path, so the golden also pins
        // that `from_files` sorts both before hashing. A port that trusts its
        // caller's order reproduces nothing.
        let content = SourceContentV2::from_files(
            vec![
                file("python/zeta.py", "2b", 200),
                file("python/alpha.py", "1a", 100),
                // An untracked path must ALSO be a file in the content -- the
                // validator says so -- so these two are both.
                file("untracked/second.py", "3c", 300),
                file("untracked/first.py", "4d", 400),
            ],
            vec!["untracked/second.py".to_string(), "untracked/first.py".to_string()],
            format!("sha256:{}", "ca".repeat(32)),
            format!("sha256:{}", "1c".repeat(32)),
        )
        .unwrap();

        // Two implementations over the same content: one whose
        // entrypoint_contract_digest is absent -- the key does not appear at
        // all -- and one where it does.
        let legacy = SourceImplementationV2::from_content(&content).unwrap();
        let mut current = legacy.clone();
        current.entrypoint_contract_digest = Some("sha256:entrypointcontract".to_string());
        current.capability_digest = "sha256:bundlecapability".to_string();
        current.implementation_id = current.expected_id().unwrap();

        // The one entry with free text. An embedded quote and a non-ASCII
        // character in both fields, which is the whole reason it is here.
        let binding = ImplementationBindingV2::new(
            current.implementation_id.clone(),
            "research-design:0123456789abcdef0123456789abcdef".to_string(),
            r#"the "wide" scale placement — a dash and a quote"#.to_string(),
            r#"because "état" must survive the wire unescaped"#.to_string(),
        )
        .unwrap();

        // The second free-text entry closes the three remaining JSON string
        // escapes independently: a literal backslash, newline, and tab. These
        // are runtime characters in title/reason, not pre-escaped JSON text.
        let control_character_binding = ImplementationBindingV2::new(
            current.implementation_id.clone(),
            "research-design:0123456789abcdef0123456789abcdef".to_string(),
            "path C:\\fern\\stage\nsecond line\tindented".to_string(),
            "a literal \\ survives; newline\nand tab\tmust hash exactly".to_string(),
        )
        .unwrap();

        let observation = SourceObservationV2::new(
            content.source_content_id.clone(),
            SourceObservationKindV2::ExactGit {
                git_commit: "0".repeat(40),
                git_tree: "1".repeat(40),
            },
        )
        .unwrap();

        let text = |value: &dyn erased_preimage::Preimage| value.canonical_text();

        let golden = serde_json::json!({
            "schema": "fern.source_content_identity_golden.v1",
            "note": "every id is sha256 over canonical JSON -- keys SORTED at every depth, \
                     no whitespace, non-ASCII RAW. entrypoint_contract_digest is OMITTED when \
                     None, inside this sorted family. Preimages are given verbatim so a \
                     mismatch names its byte.",
            "source_content": {
                "source_content_id": content.source_content_id,
                "identity_preimage": text(&content.identity_preimage()),
                "source_tree_digest": content.source_tree_digest,
                "tree_digest_preimage": String::from_utf8(
                    canonical_json_bytes(&serde_json::json!({
                        "schema": "fern.source-tree.v2",
                        "files": content.files,
                    }))
                    .unwrap(),
                )
                .unwrap(),
                "files_after_sorting": content.files.iter().map(|f| f.path.clone()).collect::<Vec<_>>(),
                "untracked_after_sorting": content.untracked_paths.clone(),
            },
            "implementation_without_entrypoint_contract_digest": {
                "implementation_id": legacy.implementation_id,
                "identity_preimage": text(&ImplementationIdentity {
                    schema: SOURCE_IMPLEMENTATION_SCHEMA,
                    component_content_ids: &legacy.component_content_ids,
                    entrypoints: &legacy.entrypoints,
                    capability_digest: &legacy.capability_digest,
                    entrypoint_contract_digest: legacy.entrypoint_contract_digest.as_deref(),
                    build_contract_digest: &legacy.build_contract_digest,
                }),
                "entrypoint_contract_digest_is_absent": legacy.entrypoint_contract_digest.is_none(),
            },
            "implementation_with_entrypoint_contract_digest": {
                "implementation_id": current.implementation_id,
                "identity_preimage": text(&ImplementationIdentity {
                    schema: SOURCE_IMPLEMENTATION_SCHEMA,
                    component_content_ids: &current.component_content_ids,
                    entrypoints: &current.entrypoints,
                    capability_digest: &current.capability_digest,
                    entrypoint_contract_digest: current.entrypoint_contract_digest.as_deref(),
                    build_contract_digest: &current.build_contract_digest,
                }),
            },
            "binding_with_free_text": {
                "binding_id": binding.binding_id,
                "identity_preimage": text(&BindingIdentity {
                    schema: IMPLEMENTATION_BINDING_SCHEMA,
                    implementation_id: &binding.implementation_id,
                    research_design_id: &binding.research_design_id,
                    title: &binding.title,
                    reason: &binding.reason,
                }),
                "title": binding.title,
                "reason": binding.reason,
            },
            "binding_with_control_characters": {
                "binding_id": control_character_binding.binding_id,
                "identity_preimage": text(&BindingIdentity {
                    schema: IMPLEMENTATION_BINDING_SCHEMA,
                    implementation_id: &control_character_binding.implementation_id,
                    research_design_id: &control_character_binding.research_design_id,
                    title: &control_character_binding.title,
                    reason: &control_character_binding.reason,
                }),
                "title": control_character_binding.title,
                "reason": control_character_binding.reason,
            },
            "observation": {
                "observation_id": observation.observation_id,
                "identity_preimage": text(&SourceObservationIdentity {
                    schema: SOURCE_OBSERVATION_SCHEMA,
                    source_content_id: &observation.source_content_id,
                    observed: &observation.observed,
                }),
            },
        });

        let path =
            std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/source_content_identity_golden.json");
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(&path, serde_json::to_string_pretty(&golden).unwrap() + "\n").unwrap();

        let written = std::fs::read_to_string(&path).unwrap();
        // The two traps must be VISIBLE in the golden, or it does not pin them.
        assert!(
            !written.contains("\\u0022"),
            "an embedded quote must be written as \\\" the way serde writes it, never \\\\u0022"
        );
        assert!(
            written.contains("état"),
            "non-ASCII must survive raw into the preimage, never escaped"
        );
        let control_preimage = golden["binding_with_control_characters"]["identity_preimage"]
            .as_str()
            .unwrap();
        assert!(
            control_preimage.contains(r#"\\"#),
            "literal backslash must be JSON-escaped"
        );
        assert!(
            control_preimage.contains(r#"\n"#),
            "newline must use the JSON short escape"
        );
        assert!(control_preimage.contains(r#"\t"#), "tab must use the JSON short escape");
    }

    /// Serialising a private preimage struct without making it public.
    mod erased_preimage {
        use super::*;

        pub trait Preimage {
            fn canonical_text(&self) -> String;
        }

        impl<T: Serialize> Preimage for T {
            fn canonical_text(&self) -> String {
                String::from_utf8(canonical_json_bytes(self).unwrap()).unwrap()
            }
        }
    }
}
