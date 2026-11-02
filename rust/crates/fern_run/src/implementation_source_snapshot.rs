//! Recoverable source snapshots and their immutable Registry projection.
//!
//! The filesystem store owns the large archive and readable patch. Registry
//! Home keeps the complete checked manifest so a Matrix or Attempt can name
//! the source object without treating a mutable path as identity. This is
//! provenance only: it never enters recipe, target, run, or Matrix hashes.

use std::collections::BTreeSet;
use std::fs;
use std::os::unix::ffi::OsStrExt;
use std::path::{Component, Path, PathBuf};

use fern_json_contract::JsonContract;
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

pub const IMPLEMENTATION_SOURCE_SNAPSHOT_SCHEMA: &str = "fern.implementation-source-snapshot.v1";
pub const SOURCE_ARCHIVE_FILE: &str = "source.tgz";
pub const GIT_PATCH_FILE: &str = "changes.patch";

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum SourceEntryKind {
    RegularFile,
    SymbolicLink,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct UntrackedSourceV1 {
    pub path: String,
    pub kind: SourceEntryKind,
    pub byte_digest: String,
    pub bytes: u64,
}

/// One exact path placed in `source.tgz`. New snapshots enumerate every
/// tracked and explicitly included untracked input; older snapshots keep this
/// list empty and remain readable, but cannot authorize V2 execution.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ArchivedSourceV1 {
    pub path: String,
    pub kind: SourceEntryKind,
    pub byte_digest: String,
    pub bytes: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct StoredSnapshotFileV1 {
    pub file_name: String,
    pub byte_digest: String,
    pub bytes: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ImplementationSourceSnapshotV1 {
    pub schema: String,
    pub snapshot_id: String,
    pub title: String,
    pub research_design_id: String,
    pub base_commit: String,
    pub tracked_tree_dirty: bool,
    pub source_archive: StoredSnapshotFileV1,
    pub git_patch: StoredSnapshotFileV1,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub archived_sources: Vec<ArchivedSourceV1>,
    pub untracked_sources: Vec<UntrackedSourceV1>,
    pub cargo_lock_byte_digest: String,
    pub recipe_catalog_source_digest: String,
}

impl JsonContract for ImplementationSourceSnapshotV1 {
    const SCHEMA_ID: &'static str = IMPLEMENTATION_SOURCE_SNAPSHOT_SCHEMA;
}

#[derive(Serialize)]
struct SnapshotIdentityPreimage<'a> {
    schema: &'static str,
    title: &'a str,
    research_design_id: &'a str,
    base_commit: &'a str,
    tracked_tree_dirty: bool,
    source_archive: &'a StoredSnapshotFileV1,
    git_patch: &'a StoredSnapshotFileV1,
    #[serde(skip_serializing_if = "<[ArchivedSourceV1]>::is_empty")]
    archived_sources: &'a [ArchivedSourceV1],
    untracked_sources: &'a [UntrackedSourceV1],
    cargo_lock_byte_digest: &'a str,
    recipe_catalog_source_digest: &'a str,
}

impl ImplementationSourceSnapshotV1 {
    fn identity_preimage(&self) -> SnapshotIdentityPreimage<'_> {
        SnapshotIdentityPreimage {
            schema: IMPLEMENTATION_SOURCE_SNAPSHOT_SCHEMA,
            title: &self.title,
            research_design_id: &self.research_design_id,
            base_commit: &self.base_commit,
            tracked_tree_dirty: self.tracked_tree_dirty,
            source_archive: &self.source_archive,
            git_patch: &self.git_patch,
            archived_sources: &self.archived_sources,
            untracked_sources: &self.untracked_sources,
            cargo_lock_byte_digest: &self.cargo_lock_byte_digest,
            recipe_catalog_source_digest: &self.recipe_catalog_source_digest,
        }
    }

    pub fn expected_snapshot_id(&self) -> Result<String, SourceSnapshotContractError> {
        let bytes = serde_json::to_vec(&self.identity_preimage())?;
        Ok(format!("implementation-source-snapshot:{:x}", Sha256::digest(bytes)))
    }

    pub fn source_archive_sha256(&self) -> Result<&str, SourceSnapshotContractError> {
        byte_digest_hex(&self.source_archive.byte_digest, "source_archive.byte_digest")
    }

    pub fn validate(&self) -> Result<(), SourceSnapshotContractError> {
        require(
            self.schema == IMPLEMENTATION_SOURCE_SNAPSHOT_SCHEMA,
            format!(
                "snapshot schema {:?} is not {IMPLEMENTATION_SOURCE_SNAPSHOT_SCHEMA:?}",
                self.schema
            ),
        )?;
        require_trimmed(&self.title, "title")?;
        require(
            self.research_design_id.starts_with("research-design:")
                && self.research_design_id.len() > "research-design:".len()
                && !self.research_design_id.chars().any(char::is_whitespace),
            "research_design_id must be a full non-empty research-design: identity".to_string(),
        )?;
        require_lower_hex(&self.base_commit, 40, "base_commit")?;
        validate_stored_file(&self.source_archive, SOURCE_ARCHIVE_FILE, "source_archive")?;
        require(
            self.source_archive.bytes > 0,
            "source_archive.bytes must be positive".to_string(),
        )?;
        validate_stored_file(&self.git_patch, GIT_PATCH_FILE, "git_patch")?;
        require(
            self.tracked_tree_dirty == (self.git_patch.bytes > 0),
            "tracked_tree_dirty must say exactly whether changes.patch is non-empty".to_string(),
        )?;
        byte_digest_hex(&self.cargo_lock_byte_digest, "cargo_lock_byte_digest")?;
        byte_digest_hex(&self.recipe_catalog_source_digest, "recipe_catalog_source_digest")?;

        validate_archived_sources(&self.archived_sources)?;
        let mut paths = BTreeSet::new();
        let mut previous: Option<&str> = None;
        for source in &self.untracked_sources {
            let path = Path::new(&source.path);
            require(
                !source.path.is_empty()
                    && path
                        .components()
                        .all(|component| matches!(component, Component::Normal(_))),
                format!("untracked source path {:?} is not a safe relative path", source.path),
            )?;
            require(
                paths.insert(source.path.as_str()),
                format!("untracked source path {:?} is duplicated", source.path),
            )?;
            if let Some(previous) = previous {
                require(
                    previous < source.path.as_str(),
                    "untracked_sources must be in stable path order".to_string(),
                )?;
            }
            previous = Some(source.path.as_str());
            byte_digest_hex(&source.byte_digest, "untracked_sources[].byte_digest")?;
        }
        require(
            self.snapshot_id == self.expected_snapshot_id()?,
            "snapshot_id does not match the complete manifest content".to_string(),
        )?;
        Ok(())
    }

    pub fn require_implementation_coverage(
        &self,
        implementation: &fern_recipes::ImplementationBundleResolutionV2,
    ) -> Result<(), SourceSnapshotContractError> {
        require(
            !self.archived_sources.is_empty(),
            "source snapshot predates archived_sources and cannot authorize a V2 implementation; freeze the current source again"
                .to_string(),
        )?;
        let archived = self
            .archived_sources
            .iter()
            .map(|source| (source.path.as_str(), source))
            .collect::<std::collections::BTreeMap<_, _>>();
        for source in implementation
            .source_files
            .iter()
            .chain(&implementation.dependency_lock_files)
        {
            let observed = archived.get(source.path.as_str()).ok_or_else(|| {
                SourceSnapshotContractError::Invalid(format!(
                    "implementation source {:?} is absent from the frozen source archive",
                    source.path
                ))
            })?;
            require(
                observed.kind == SourceEntryKind::RegularFile
                    && observed.byte_digest == source.byte_sha256
                    && observed.bytes == source.bytes,
                format!(
                    "implementation source {:?} does not match the bytes frozen in the source archive",
                    source.path
                ),
            )?;
        }
        Ok(())
    }
}

/// Prove that one installed source root contains exactly the inputs named by
/// the snapshot and that every regular-file or symlink payload still matches.
/// This lives beside the snapshot contract so the human CLI, scheduler, and
/// worker all enforce one filesystem interpretation.
pub fn verify_source_root(
    source_root: &Path,
    snapshot: &ImplementationSourceSnapshotV1,
) -> Result<(), SourceSnapshotContractError> {
    snapshot.validate()?;
    require(
        source_root.is_dir(),
        format!("installed source root {} is missing", source_root.display()),
    )?;
    // Only the DECLARED files are checked. A file the manifest never mentions
    // is not part of the snapshot's claim, so an installed tree carrying a
    // `__pycache__`, an `.ai/agent.md`, or any other undeclared file is
    // still the source this snapshot names.
    let observed_paths = source_tree_paths(source_root, source_root)?;
    let expected_paths = snapshot
        .archived_sources
        .iter()
        .map(|source| PathBuf::from(&source.path))
        .collect::<BTreeSet<_>>();
    let absent = expected_paths.difference(&observed_paths).collect::<Vec<_>>();
    require(
        absent.is_empty(),
        format!(
            "installed source tree does not match snapshot {}: declared file(s) missing {absent:?}",
            snapshot.snapshot_id
        ),
    )?;
    for expected in &snapshot.archived_sources {
        let observed = source_entry(source_root, Path::new(&expected.path))?;
        require(
            &observed == expected,
            format!(
                "installed source input {:?} differs from snapshot {}",
                expected.path, snapshot.snapshot_id
            ),
        )?;
    }
    Ok(())
}

fn source_entry(source_root: &Path, relative: &Path) -> Result<ArchivedSourceV1, SourceSnapshotContractError> {
    let path = source_root.join(relative);
    let metadata = fs::symlink_metadata(&path).map_err(|error| {
        SourceSnapshotContractError::Invalid(format!(
            "cannot inspect installed source input {}: {error}",
            path.display()
        ))
    })?;
    let (kind, bytes) = if metadata.file_type().is_symlink() {
        let target = fs::read_link(&path).map_err(|error| {
            SourceSnapshotContractError::Invalid(format!(
                "cannot read installed source symlink {}: {error}",
                path.display()
            ))
        })?;
        (SourceEntryKind::SymbolicLink, target.as_os_str().as_bytes().to_vec())
    } else if metadata.is_file() {
        let bytes = fs::read(&path).map_err(|error| {
            SourceSnapshotContractError::Invalid(format!(
                "cannot read installed source file {}: {error}",
                path.display()
            ))
        })?;
        (SourceEntryKind::RegularFile, bytes)
    } else {
        return Err(SourceSnapshotContractError::Invalid(format!(
            "installed source input {} is neither a regular file nor a symbolic link",
            path.display()
        )));
    };
    let bytes_len = u64::try_from(bytes.len()).map_err(|error| {
        SourceSnapshotContractError::Invalid(format!(
            "installed source input {} size does not fit u64: {error}",
            path.display()
        ))
    })?;
    Ok(ArchivedSourceV1 {
        path: relative.to_string_lossy().into_owned(),
        kind,
        byte_digest: format!("sha256:{:x}", Sha256::digest(bytes)),
        bytes: bytes_len,
    })
}

fn source_tree_paths(root: &Path, directory: &Path) -> Result<BTreeSet<PathBuf>, SourceSnapshotContractError> {
    let mut paths = BTreeSet::new();
    let mut entries = fs::read_dir(directory)
        .map_err(|error| {
            SourceSnapshotContractError::Invalid(format!(
                "cannot read installed source directory {}: {error}",
                directory.display()
            ))
        })?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|error| {
            SourceSnapshotContractError::Invalid(format!(
                "cannot enumerate installed source directory {}: {error}",
                directory.display()
            ))
        })?;
    entries.sort_by_key(fs::DirEntry::file_name);
    for entry in entries {
        let path = entry.path();
        let metadata = fs::symlink_metadata(&path).map_err(|error| {
            SourceSnapshotContractError::Invalid(format!(
                "cannot inspect installed source input {}: {error}",
                path.display()
            ))
        })?;
        if metadata.file_type().is_dir() {
            paths.extend(source_tree_paths(root, &path)?);
            continue;
        }
        require(
            metadata.file_type().is_file() || metadata.file_type().is_symlink(),
            format!(
                "installed source input {} is neither a regular file, directory, nor symbolic link",
                path.display()
            ),
        )?;
        let relative = path.strip_prefix(root).map_err(|error| {
            SourceSnapshotContractError::Invalid(format!(
                "installed source input {} escaped {}: {error}",
                path.display(),
                root.display()
            ))
        })?;
        paths.insert(relative.to_path_buf());
    }
    Ok(paths)
}

fn validate_archived_sources(sources: &[ArchivedSourceV1]) -> Result<(), SourceSnapshotContractError> {
    let mut previous: Option<&str> = None;
    for source in sources {
        let path = Path::new(&source.path);
        require(
            !source.path.is_empty()
                && path
                    .components()
                    .all(|component| matches!(component, Component::Normal(_))),
            format!("archived source path {:?} is not a safe relative path", source.path),
        )?;
        if let Some(previous) = previous {
            require(
                previous < source.path.as_str(),
                "archived_sources must be duplicate-free and in stable path order".to_string(),
            )?;
        }
        previous = Some(source.path.as_str());
        byte_digest_hex(&source.byte_digest, "archived_sources[].byte_digest")?;
    }
    Ok(())
}

#[derive(Debug, thiserror::Error)]
pub enum SourceSnapshotContractError {
    #[error("invalid implementation source snapshot: {0}")]
    Invalid(String),
    #[error("cannot serialize implementation source snapshot identity: {0}")]
    Serialization(#[from] serde_json::Error),
}

fn validate_stored_file(
    file: &StoredSnapshotFileV1,
    expected_name: &str,
    field: &str,
) -> Result<(), SourceSnapshotContractError> {
    require(
        file.file_name == expected_name,
        format!("{field}.file_name must be {expected_name:?}"),
    )?;
    byte_digest_hex(&file.byte_digest, &format!("{field}.byte_digest"))?;
    Ok(())
}

fn byte_digest_hex<'a>(value: &'a str, field: &str) -> Result<&'a str, SourceSnapshotContractError> {
    let digest = value
        .strip_prefix("sha256:")
        .ok_or_else(|| SourceSnapshotContractError::Invalid(format!("{field} must use the sha256:<hex> form")))?;
    require_lower_hex(digest, 64, field)?;
    Ok(digest)
}

fn require_trimmed(value: &str, field: &str) -> Result<(), SourceSnapshotContractError> {
    require(
        !value.is_empty() && value.trim() == value,
        format!("{field} must be trimmed, non-empty text"),
    )
}

fn require_lower_hex(value: &str, length: usize, field: &str) -> Result<(), SourceSnapshotContractError> {
    require(
        value.len() == length
            && value
                .bytes()
                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte)),
        format!("{field} must be exactly {length} lowercase hexadecimal characters"),
    )
}

fn require(condition: bool, explanation: String) -> Result<(), SourceSnapshotContractError> {
    if condition {
        Ok(())
    } else {
        Err(SourceSnapshotContractError::Invalid(explanation))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::run_plan::RunPlanDefinitionV1;
    use tempfile::TempDir;

    fn byte_digest(bytes: &[u8]) -> String {
        format!("sha256:{:x}", Sha256::digest(bytes))
    }

    fn snapshot(title: &str, archive_bytes: &[u8]) -> ImplementationSourceSnapshotV1 {
        let mut snapshot = ImplementationSourceSnapshotV1 {
            schema: IMPLEMENTATION_SOURCE_SNAPSHOT_SCHEMA.to_string(),
            snapshot_id: String::new(),
            title: title.to_string(),
            research_design_id: "research-design:source-provenance-fixture".to_string(),
            base_commit: "1".repeat(40),
            tracked_tree_dirty: false,
            source_archive: StoredSnapshotFileV1 {
                file_name: SOURCE_ARCHIVE_FILE.to_string(),
                byte_digest: byte_digest(archive_bytes),
                bytes: archive_bytes.len() as u64,
            },
            git_patch: StoredSnapshotFileV1 {
                file_name: GIT_PATCH_FILE.to_string(),
                byte_digest: byte_digest(&[]),
                bytes: 0,
            },
            archived_sources: Vec::new(),
            untracked_sources: Vec::new(),
            cargo_lock_byte_digest: byte_digest(b"lock"),
            recipe_catalog_source_digest: byte_digest(b"catalog"),
        };
        snapshot.snapshot_id = snapshot.expected_snapshot_id().unwrap();
        snapshot
    }

    #[test]
    fn installed_source_root_must_match_every_declared_path_and_byte() {
        let directory = TempDir::new().unwrap();
        fs::create_dir_all(directory.path().join("src")).unwrap();
        fs::write(directory.path().join("src/model.py"), b"model = 1\n").unwrap();
        let mut snapshot = snapshot("Installed source fixture", b"archive");
        snapshot.archived_sources = vec![ArchivedSourceV1 {
            path: "src/model.py".to_string(),
            kind: SourceEntryKind::RegularFile,
            byte_digest: byte_digest(b"model = 1\n"),
            bytes: 10,
        }];
        snapshot.snapshot_id = snapshot.expected_snapshot_id().unwrap();

        verify_source_root(directory.path(), &snapshot).unwrap();

        // A CHANGED declared file is refused, and the refusal names it.
        fs::write(directory.path().join("src/model.py"), b"model = 2\n").unwrap();
        let error = verify_source_root(directory.path(), &snapshot).unwrap_err();
        assert!(error.to_string().contains("differs from snapshot"));
        assert!(error.to_string().contains("src/model.py"), "{error}");

        // A MISSING declared file is refused, and named.
        fs::remove_file(directory.path().join("src/model.py")).unwrap();
        let error = verify_source_root(directory.path(), &snapshot).unwrap_err();
        assert!(error.to_string().contains("src/model.py"), "{error}");

        // CHG 2026-08-20: an extra undeclared file used to be refused here as
        // "unexpected". Identity is the DECLARED file list, so a file the
        // manifest never mentions changes no declared byte and cannot make the
        // declared source wrong. It now verifies. What this test is really
        // about -- every declared path present with its declared bytes -- is
        // unchanged and is asserted above.
        fs::write(directory.path().join("src/model.py"), b"model = 1\n").unwrap();
        fs::write(directory.path().join("src/extra.py"), b"extra = 1\n").unwrap();
        verify_source_root(directory.path(), &snapshot)
            .expect("an undeclared file is not part of the snapshot's claim");
    }

    #[test]
    fn source_and_recipe_snapshot_fields_do_not_rewrite_legacy_plan_bytes() {
        let legacy = concat!(
            r#"{"schema":"fern.run_plan.v1","plan_name":"legacy-plan","dependencies":[],"steps":[{"step_id":"one","lane":"legacy-lane","preset":"legacy-preset","model":"fern","limit":1,"keep_forecast_origins":false}]}"#,
        );
        let parsed: RunPlanDefinitionV1 = serde_json::from_str(legacy).unwrap();
        assert!(parsed.implementation_source_snapshot_id.is_none());
        assert!(parsed.steps[0].recipe_snapshot.is_none());
        assert_eq!(serde_json::to_string(&parsed).unwrap(), legacy);
    }

}
