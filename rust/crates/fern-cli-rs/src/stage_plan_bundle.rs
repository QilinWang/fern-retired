//! What a Stage Plan executes: a materialized SourceContent root and the
//! implementation digests observed on the declared files that will run.
//!
//! This packaging half survived the retirement of `stage_plan_inline`: lowering
//! a plan needs to know which
//! bytes a Stage will run and what their digest is; it never spawns a runner,
//! waits on a child, or classifies a runner failure. The functions, doc
//! comments and inline comments below were moved verbatim from the archived
//! `retired/fern_cli_rs/stage_plan_inline.rs`.

use std::fs;
use std::path::Path;

use fern_artifact::{stage_implementation_digest, stage_python_bundle_digest};
use fern_experiment_types::{Sha256Digest, StageRunnerV1};
use fern_run::implementation_source_snapshot::SourceEntryKind;
use fern_run::source_content::SourceContentV2;

use crate::{Fallible, invalid_input};

const PYTHON_ENTRYPOINT: &str = "fr_tr";

const PYTHON_HANDLERS: &[&str] = &[
    "serie.materialize.cell",
    "fern.train.current",
    "fern.postprocess",
    "oracle.controlled_linear.generate",
];

const RUST_ENTRYPOINT: &str = "fern-stage-runner";

// The installed Rust binary now checks returned bytes only. Scientific Stage
// execution is owned by the frozen Python implementation bundle.
const RUST_HANDLERS: &[&str] = &["fern.stage.return.check"];

/// Every Stage handler this build packages a runner for.
///
/// A Worker Site registration defaults its allowed handlers to exactly this
/// list: what the Site may claim should be what its build can actually execute,
/// and a handler nobody packages is a refusal waiting to happen at the worker.
pub(crate) fn packaged_stage_handlers() -> Vec<&'static str> {
    PYTHON_HANDLERS.to_vec()
}

pub(crate) const STAGE_PYTHON_RUNTIME_FILES: &[&str] = &[
    "crates/fern-experiment-types/fixtures/stage_pipeline_forecast_origin_v1.json",
    "fern_stage_runtime/__init__.py",
    "fern_stage_runtime/__main__.py",
    "fern_stage_runtime/cli.py",
    "fern_stage_runtime/postprocess.py",
    "python/fern_analysis/__init__.py",
    "python/fern_analysis/sequence_stitch.py",
    "serie/__init__.py",
];

/// The declared files a Python Stage may import from one materialized source.
///
/// The authored implementation resolution supplies its probed source and lock
/// closure. The Stage runner adds its small fixed wrapper/trainer surface. No
/// directory is copied or recursively reinterpreted here: an implementation
/// file absent from the probed closure is not part of this Stage.
pub(crate) fn declared_python_stage_files(
    content: &SourceContentV2,
    implementation: &fern_recipes::ImplementationBundleResolutionV2,
) -> Fallible<Vec<String>> {
    content
        .require_implementation_coverage(
            implementation
                .source_files
                .iter()
                .chain(&implementation.dependency_lock_files),
        )
        .map_err(|error| invalid_input(error.to_string()))?;

    let mut required = crate::run_sweep::reviewed_worker_source();
    required.extend(STAGE_PYTHON_RUNTIME_FILES.iter().map(|path| (*path).to_string()));
    required.extend(implementation.source_files.iter().map(|file| file.path.clone()));
    required.extend(
        implementation
            .dependency_lock_files
            .iter()
            .map(|file| file.path.clone()),
    );
    required.sort();
    required.dedup();

    let files = content
        .files
        .iter()
        .map(|file| (file.path.as_str(), file))
        .collect::<std::collections::BTreeMap<_, _>>();
    for relative in &required {
        let file = files.get(relative.as_str()).ok_or_else(|| {
            invalid_input(format!(
                "source content {} is missing Python Stage file {relative:?}",
                content.source_content_id
            ))
        })?;
        if file.kind != SourceEntryKind::RegularFile {
            return Err(invalid_input(format!(
                "Python Stage file {relative:?} in {} is not a regular file",
                content.source_content_id
            )));
        }
    }
    Ok(required)
}

/// Derive the Stage implementation digest from SourceContent's declared list.
///
/// Materialization verifies these bytes once. This function does not reopen or
/// walk the tree merely to rediscover the manifest the Registry already owns.
pub(crate) fn declared_python_implementation_digest(
    content: &SourceContentV2,
    relative_paths: &[String],
) -> Fallible<Sha256Digest> {
    let files = content
        .files
        .iter()
        .map(|file| (file.path.as_str(), file))
        .collect::<std::collections::BTreeMap<_, _>>();
    let declared = relative_paths
        .iter()
        .map(|relative| {
            let file = files
                .get(relative.as_str())
                .ok_or_else(|| invalid_input(format!("source content has no declared file {relative:?}")))?;
            let digest =
                Sha256Digest::parse(file.byte_digest.clone()).map_err(|error| invalid_input(error.to_string()))?;
            Ok((relative.clone(), digest))
        })
        .collect::<Fallible<Vec<_>>>()?;
    python_implementation_digest(&declared)
}

pub(crate) fn observed_rust_implementation_digest(binary: &Path) -> Fallible<Sha256Digest> {
    refuse_symlink(binary)?;
    let bytes = fs::read(binary)
        .map_err(|error| invalid_input(format!("cannot read Stage runner binary {}: {error}", binary.display())))?;
    let artifact = Sha256Digest::from_bytes(&bytes);
    Ok(stage_implementation_digest(
        StageRunnerV1::RustBinary,
        RUST_ENTRYPOINT,
        RUST_HANDLERS,
        &artifact,
    ))
}

/// Observe a legacy bundle that predates SourceContent-backed Stage execution.
/// New V2 plans use [`observed_python_implementation_digest_for_files`].
pub(crate) fn observed_python_implementation_digest(bundle_root: &Path) -> Fallible<Sha256Digest> {
    let mut files = Vec::new();
    collect_bundle_files(bundle_root, bundle_root, &mut files)?;
    python_implementation_digest(&files)
}

/// Reopen exactly the declared files at worker start and derive their digest.
pub(crate) fn observed_python_implementation_digest_for_files(
    source_root: &Path,
    relative_paths: &[String],
) -> Fallible<Sha256Digest> {
    let files = relative_paths
        .iter()
        .map(|relative| {
            let path = source_root.join(relative);
            refuse_symlink(&path)?;
            let digest = Sha256Digest::from_bytes(&fs::read(&path).map_err(|error| {
                invalid_input(format!(
                    "cannot read declared Python Stage file {}: {error}",
                    path.display()
                ))
            })?);
            Ok((relative.clone(), digest))
        })
        .collect::<Fallible<Vec<_>>>()?;
    python_implementation_digest(&files)
}

fn python_implementation_digest(files: &[(String, Sha256Digest)]) -> Fallible<Sha256Digest> {
    let artifact = stage_python_bundle_digest(files).map_err(|error| invalid_input(error.to_string()))?;
    Ok(stage_implementation_digest(
        StageRunnerV1::PythonModule,
        PYTHON_ENTRYPOINT,
        PYTHON_HANDLERS,
        &artifact,
    ))
}

pub(crate) fn collect_bundle_files(
    bundle_root: &Path,
    directory: &Path,
    files: &mut Vec<(String, Sha256Digest)>,
) -> Fallible<()> {
    let entries = fs::read_dir(directory)
        .map_err(|error| invalid_input(format!("cannot read bundle directory {}: {error}", directory.display())))?;
    for entry in entries {
        let entry = entry?;
        let path = entry.path();
        let metadata = fs::symlink_metadata(&path)?;
        if metadata.file_type().is_symlink() {
            return Err(invalid_input(format!("{} must not be a symlink", path.display())));
        }
        if metadata.is_dir() {
            collect_bundle_files(bundle_root, &path, files)?;
            continue;
        }
        if !metadata.is_file() {
            continue;
        }
        let relative = path
            .strip_prefix(bundle_root)
            .map_err(|_| invalid_input("bundle file escaped its root"))?
            .to_str()
            .ok_or_else(|| invalid_input("bundle path is not utf-8"))?
            .replace('\\', "/");
        let digest = Sha256Digest::from_bytes(&fs::read(&path)?);
        files.push((relative, digest));
    }
    Ok(())
}

pub(crate) fn refuse_symlink(path: &Path) -> Fallible<()> {
    let metadata = fs::symlink_metadata(path)
        .map_err(|error| invalid_input(format!("cannot inspect {}: {error}", path.display())))?;
    if metadata.file_type().is_symlink() {
        return Err(invalid_input(format!("{} must not be a symlink", path.display())));
    }
    Ok(())
}

pub(crate) fn file_name(path: &Path) -> Fallible<String> {
    path.file_name()
        .and_then(|name| name.to_str())
        .map(str::to_string)
        .ok_or_else(|| invalid_input("path has no utf-8 file name"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use fern_run::source_content::SourceFileV2;

    #[test]
    fn declared_digest_matches_worker_start_reopen_and_detects_a_change() {
        let root = tempfile::tempdir().unwrap();
        fs::write(root.path().join("fr_tr.py"), b"trainer\n").unwrap();
        fs::write(root.path().join("uv.lock"), b"lock\n").unwrap();
        let content = SourceContentV2::from_files(
            vec![
                SourceFileV2 {
                    path: "fr_tr.py".to_string(),
                    kind: SourceEntryKind::RegularFile,
                    byte_digest: Sha256Digest::from_bytes(b"trainer\n").as_str().to_string(),
                    bytes: 8,
                },
                SourceFileV2 {
                    path: "uv.lock".to_string(),
                    kind: SourceEntryKind::RegularFile,
                    byte_digest: Sha256Digest::from_bytes(b"lock\n").as_str().to_string(),
                    bytes: 5,
                },
            ],
            Vec::new(),
            Sha256Digest::from_bytes(b"cargo-lock").as_str().to_string(),
            Sha256Digest::from_bytes(b"catalog").as_str().to_string(),
        )
        .unwrap();
        let files = vec!["fr_tr.py".to_string(), "uv.lock".to_string()];
        let declared = declared_python_implementation_digest(&content, &files).unwrap();
        let reopened = observed_python_implementation_digest_for_files(root.path(), &files).unwrap();
        assert_eq!(declared, reopened);

        fs::write(root.path().join("fr_tr.py"), b"changed\n").unwrap();
        let changed = observed_python_implementation_digest_for_files(root.path(), &files).unwrap();
        assert_ne!(declared, changed);
    }
}
