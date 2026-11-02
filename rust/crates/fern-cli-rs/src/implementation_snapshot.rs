//! Freeze and reopen the exact source bytes behind an implementation change.
//!
//! A Research Design says what should be tested, and an Experiment Contrast
//! says which recipe field differs. This boundary answers a different
//! question: which source tree existed when that implementation was prepared?
//! The content address is provenance only. It never enters recipe, target,
//! run, Matrix, or accepted-result identity.

use std::collections::{BTreeMap, BTreeSet};
use std::ffi::OsStr;
use std::fs::{self, File};
use std::os::unix::ffi::OsStrExt;
use std::path::{Component, Path, PathBuf};
use std::process::{Command, Output, Stdio};

use anyhow::{Context, Result, bail, ensure};
use fern_artifact::ByteDigest;
use fern_json_contract::{ContractRegistration, JsonContract};
use fern_run::implementation_source_snapshot::{
    ArchivedSourceV1, GIT_PATCH_FILE, ImplementationSourceSnapshotV1, SOURCE_ARCHIVE_FILE, SourceEntryKind,
    StoredSnapshotFileV1,
};
use fern_run::source_content::{ImplementationBindingV2, SourceContentV2, SourceImplementationV2};
use serde::Deserialize;

use crate::{read_json_contract, write_file_durably};

const MANIFEST_FILE: &str = "manifest.json";
const MAX_MANIFEST_BYTES: u64 = 1024 * 1024;

/// Read and check one saved implementation bundle resolution.
///
/// The resolution is validated against its own schema before it can reach an
/// identity: a typo in a source-file path would otherwise be frozen into a
/// capability digest that looks exactly as authoritative as a correct one.
///
/// A draft passed here is refused by name rather than accepted. The two files
/// sit side by side in `docs/implementation-bundles/` and differ in the only
/// way that matters: the draft lists what a person declared, the resolution
/// lists the import closure a probe found. A capability minted from the draft
/// can never equal what a running worker reports, so the mistake has to be
/// caught where it is made instead of hours later at the execution gate.
fn read_implementation_bundle_resolution(path: &Path) -> Result<fern_recipes::ImplementationBundleResolutionV2> {
    let bytes = std::fs::read(path).with_context(|| format!("cannot read implementation bundle {}", path.display()))?;
    let schema = serde_json::from_slice::<serde_json::Value>(&bytes)
        .with_context(|| format!("{} is not JSON", path.display()))?
        .get("schema")
        .and_then(serde_json::Value::as_str)
        .unwrap_or_default()
        .to_string();
    ensure!(
        schema != fern_recipes::ImplementationBundleDraftV2::SCHEMA_ID,
        "{} is an authored bundle DRAFT, which lists the files a person declared rather than the import closure a \
         worker loads. A capability minted from it can never match what a running worker reports. Probe it first \
         with `fern-run probe-implementation` and pass the resolution it writes.",
        path.display(),
    );
    let resolution: fern_recipes::ImplementationBundleResolutionV2 = serde_json::from_slice(&bytes)
        .with_context(|| format!("{} is not a valid implementation bundle resolution", path.display()))?;
    ensure!(
        resolution.schema == fern_recipes::ImplementationBundleResolutionV2::SCHEMA_ID,
        "{} declares schema {:?}, but this runtime accepts only {:?}",
        path.display(),
        resolution.schema,
        fern_recipes::ImplementationBundleResolutionV2::SCHEMA_ID,
    );
    ensure!(
        !resolution.source_files.is_empty(),
        "{} declares no source files, so it cannot describe what an implementation is made of",
        path.display(),
    );
    Ok(resolution)
}

fn verify_snapshot_directory(directory: &Path) -> Result<ImplementationSourceSnapshotV1> {
    let manifest_path = directory.join(MANIFEST_FILE);
    let metadata = fs::symlink_metadata(&manifest_path)
        .with_context(|| format!("cannot inspect source snapshot manifest {}", manifest_path.display()))?;
    ensure!(
        metadata.file_type().is_file(),
        "source snapshot manifest must be a regular file"
    );
    ensure!(
        metadata.len() <= MAX_MANIFEST_BYTES,
        "source snapshot manifest exceeds {MAX_MANIFEST_BYTES} bytes"
    );
    let snapshot: ImplementationSourceSnapshotV1 =
        read_json_contract(&manifest_path).map_err(|error| anyhow::anyhow!(error.to_string()))?;
    snapshot.validate()?;
    ensure!(
        snapshot.snapshot_id == snapshot.expected_snapshot_id()?,
        "source snapshot manifest identity does not match its content"
    );
    let directory_name = directory
        .file_name()
        .and_then(OsStr::to_str)
        .context("source snapshot directory has no UTF-8 name")?;
    ensure!(
        snapshot.snapshot_id.strip_prefix("implementation-source-snapshot:") == Some(directory_name),
        "source snapshot directory name does not match manifest identity"
    );
    verify_stored_file(directory, &snapshot.source_archive, SOURCE_ARCHIVE_FILE)?;
    verify_stored_file(directory, &snapshot.git_patch, GIT_PATCH_FILE)?;
    Ok(snapshot)
}

fn verify_stored_file(directory: &Path, stored: &StoredSnapshotFileV1, expected_name: &str) -> Result<()> {
    ensure!(
        stored.file_name == expected_name,
        "source snapshot file name {:?} is not {expected_name:?}",
        stored.file_name
    );
    let path = directory.join(expected_name);
    let observed = stored_file(&path, expected_name)?;
    ensure!(
        &observed == stored,
        "source snapshot file {} no longer matches its manifest",
        path.display()
    );
    Ok(())
}

/// Reopen one V1 archive and prove its contents against the manifest.
///
/// Measured on GNU tar 1.34 (2026-08-20): absolute member names have their
/// leading `/` stripped, a `..` component is refused outright, and a symlink
/// extracted from the archive is NOT traversed when a later member descends
/// through it. All three protections are disabled by exactly one flag,
/// `-P`/`--absolute-names`, which is why this never passes it. `-k` is added
/// because a duplicate member otherwise overwrites silently; with it tar fails.
///
/// The manifest comparison is the real defence rather than the flags. The
/// manifest declares regular files with a type, a length and a digest, so a
/// symlink, a FIFO, a device node, a duplicate or any extra entry is already
/// something it does not describe.
/// Reopen one V1 archive and hand back every member it holds.
///
/// All members, not only the declared ones. A snapshot that predates
/// `archived_sources` declares nothing, and the only place its declaration
/// can be recovered from is the archive itself, so the caller needs to see
/// what is actually in there. Declared paths are still fully checked here.
fn read_v1_archive(
    store_root: &Path,
    snapshot: &ImplementationSourceSnapshotV1,
) -> std::result::Result<fern_run::source_content::LegacyArchiveMembers, String> {
    let refuse =
        |reason: String| -> std::result::Result<fern_run::source_content::LegacyArchiveMembers, String> { Err(reason) };
    let directory = snapshot_directory(store_root, &snapshot.snapshot_id).map_err(|error| error.to_string())?;

    // A symlinked snapshot directory would let the store point anywhere.
    match fs::symlink_metadata(&directory) {
        Ok(metadata) if metadata.file_type().is_symlink() => {
            return refuse(format!("snapshot directory {} is a symlink", directory.display()));
        }
        Ok(metadata) if !metadata.is_dir() => {
            return refuse(format!("snapshot path {} is not a directory", directory.display()));
        }
        Ok(_) => {}
        Err(error) => {
            return refuse(format!(
                "snapshot directory {} is unreadable: {error}",
                directory.display()
            ));
        }
    }

    // The on-disk manifest must be the Registry's manifest. verify_snapshot_directory
    // also re-derives the snapshot identity and re-digests both stored files.
    let on_disk = verify_snapshot_directory(&directory).map_err(|error| error.to_string())?;
    if &on_disk != snapshot {
        return refuse(format!(
            "manifest.json in {} disagrees with the Registry copy of {}",
            directory.display(),
            snapshot.snapshot_id
        ));
    }

    let extracted = tempfile::tempdir().map_err(|error| format!("cannot create extraction directory: {error}"))?;
    let archive = directory.join(SOURCE_ARCHIVE_FILE);
    let status = Command::new("tar")
        // Never -P: that single flag disables every name-safety guard tar
        // has. A duplicate member is no longer refused; whatever ends up at a
        // DECLARED path still has to carry the declared digest below, and a
        // duplicate at an undeclared path is not part of the claim.
        .args(["-x", "-z", "-f"])
        .arg(&archive)
        .arg("-C")
        .arg(extracted.path())
        .status()
        .map_err(|error| format!("cannot run tar on {}: {error}", archive.display()))?;
    if !status.success() {
        return refuse(format!(
            "tar refused {} with status {status}; the archive is damaged or carries a hostile member",
            archive.display()
        ));
    }

    let mut observed = BTreeMap::new();
    collect_extracted_files(extracted.path(), extracted.path(), &mut observed)
        .map_err(|error| format!("cannot walk the extracted tree: {error}"))?;

    let declared: BTreeMap<&str, &ArchivedSourceV1> = snapshot
        .archived_sources
        .iter()
        .map(|source| (source.path.as_str(), source))
        .collect();
    // Undeclared members are ignored. The manifest declares a list of files
    // and their bytes; an archive that also carries something it never
    // mentioned still contains exactly the source it declared. This check
    // used to refuse them, which made six otherwise provable snapshots
    // unmappable over a stray `.ai/agent.md`.
    for (path, source) in &declared {
        let Some((kind, bytes)) = observed.get(*path) else {
            return refuse(format!("archive is missing {path}, which the manifest declares"));
        };
        if kind != &source.kind {
            return refuse(format!(
                "{path} extracted as {kind:?}, but the manifest declares {:?}",
                source.kind
            ));
        }
        if bytes.len() as u64 != source.bytes {
            return refuse(format!(
                "{path} is {} bytes on extraction, but the manifest declares {}",
                bytes.len(),
                source.bytes
            ));
        }
        // The same digest helper the freeze path uses, so one algorithm
        // decides identity on both sides of the translation.
        let observed_digest = ByteDigest::from_bytes(bytes).as_str().to_string();
        if observed_digest != source.byte_digest {
            return refuse(format!(
                "{path} hashes to {observed_digest}, but the manifest declares {}",
                source.byte_digest
            ));
        }
    }
    // Every member, declared or not. What is declared has just been proven;
    // what is not is the caller's business, and for a snapshot that declared
    // nothing it is the only thing there is.
    Ok(observed)
}

/// Collect every extracted entry with its real type.
///
/// `symlink_metadata` is deliberate and is the one safety property left in
/// this walk: it never FOLLOWS a link, so a member pointing outside the
/// extraction directory is read as a link value rather than as whatever it
/// aims at. Everything else here is descriptive. Entries no manifest kind
/// can describe are skipped, because only declared paths are ever consulted.
fn collect_extracted_files(
    root: &Path,
    directory: &Path,
    into: &mut BTreeMap<String, (SourceEntryKind, Vec<u8>)>,
) -> Result<()> {
    for entry in fs::read_dir(directory)? {
        let entry = entry?;
        let path = entry.path();
        let relative = path
            .strip_prefix(root)
            .map_err(|error| anyhow::anyhow!("extracted path escaped its root: {error}"))?
            .to_str()
            .context("extracted path is not UTF-8")?
            .to_string();
        let metadata = fs::symlink_metadata(&path)?;
        let file_type = metadata.file_type();
        if file_type.is_dir() {
            collect_extracted_files(root, &path, into)?;
        } else if file_type.is_symlink() {
            let target = fs::read_link(&path)?;
            into.insert(
                relative,
                (SourceEntryKind::SymbolicLink, target.as_os_str().as_bytes().to_vec()),
            );
        } else if file_type.is_file() {
            into.insert(relative, (SourceEntryKind::RegularFile, fs::read(&path)?));
        } else {
            // A FIFO, socket or device node. No manifest kind can describe
            // one, so it can never be a declared file; it is skipped rather
            // than refused, exactly like any other undeclared member. If the
            // manifest DOES declare this path, the declared-file loop below
            // reports it as missing and names it.
            continue;
        }
    }
    Ok(())
}

fn snapshot_directory(store_root: &Path, identity: &str) -> Result<PathBuf> {
    let digest = identity
        .strip_prefix("implementation-source-snapshot:")
        .unwrap_or(identity);
    require_lower_hex(digest, 64, "source snapshot digest")?;
    Ok(store_root.join(digest))
}

#[allow(dead_code)]
fn create_source_archive(workspace_root: &Path, path_list: &Path, archive: &Path, commit_epoch: &str) -> Result<()> {
    let archive_file = File::create(archive)
        .with_context(|| format!("cannot create source snapshot archive {}", archive.display()))?;
    let mut tar = Command::new("tar")
        .current_dir(workspace_root)
        .arg("--format=gnu")
        .arg("--sort=name")
        .arg(format!("--mtime=@{commit_epoch}"))
        .args(["--owner=0", "--group=0", "--numeric-owner", "--null", "-cf", "-"])
        .arg("--files-from")
        .arg(path_list)
        .stdout(Stdio::piped())
        .spawn()
        .context("cannot start tar for the source snapshot")?;
    let tar_stdout = tar.stdout.take().context("tar source snapshot stdout was not piped")?;
    let mut gzip = Command::new("gzip")
        .arg("-n")
        .stdin(Stdio::from(tar_stdout))
        .stdout(Stdio::from(archive_file))
        .spawn()
        .context("cannot start deterministic gzip for the source snapshot")?;
    let gzip_status = gzip.wait().context("cannot wait for source snapshot gzip")?;
    let tar_status = tar.wait().context("cannot wait for source snapshot tar")?;
    ensure!(
        tar_status.success() && gzip_status.success(),
        "source snapshot archive failed: tar {tar_status}, gzip {gzip_status}"
    );
    Ok(())
}

fn select_untracked_paths(
    workspace_root: &Path,
    all_untracked: &BTreeSet<PathBuf>,
    requested: &[PathBuf],
) -> Result<BTreeSet<PathBuf>> {
    let mut selected = BTreeSet::new();
    for requested_path in requested {
        let relative = normalize_relative_input(workspace_root, requested_path)?;
        let matches = all_untracked
            .iter()
            .filter(|candidate| **candidate == relative || candidate.starts_with(&relative))
            .cloned()
            .collect::<Vec<_>>();
        ensure!(
            !matches.is_empty(),
            "--include-untracked {} does not name an untracked, non-ignored source file or directory",
            requested_path.display()
        );
        selected.extend(matches);
    }
    Ok(selected)
}

fn normalize_relative_input(workspace_root: &Path, input: &Path) -> Result<PathBuf> {
    let relative = if input.is_absolute() {
        input
            .strip_prefix(workspace_root)
            .with_context(|| format!("{} is outside workspace {}", input.display(), workspace_root.display()))?
    } else {
        input
    };
    ensure!(
        !relative.as_os_str().is_empty(),
        "an untracked include cannot name the workspace root"
    );
    ensure!(
        relative
            .components()
            .all(|component| matches!(component, Component::Normal(_))),
        "untracked include {} must be a normalized path below the workspace root",
        input.display()
    );
    Ok(relative.to_path_buf())
}

fn stored_file(path: &Path, file_name: &str) -> Result<StoredSnapshotFileV1> {
    let metadata = fs::symlink_metadata(path).with_context(|| format!("cannot inspect {}", path.display()))?;
    ensure!(
        metadata.file_type().is_file(),
        "{} must be a regular, non-symlink file",
        path.display()
    );
    let bytes = fs::read(path).with_context(|| format!("cannot read {}", path.display()))?;
    Ok(StoredSnapshotFileV1 {
        file_name: file_name.to_string(),
        byte_digest: ByteDigest::from_bytes(&bytes).as_str().to_string(),
        bytes: u64::try_from(bytes.len()).context("snapshot file size does not fit u64")?,
    })
}

#[allow(dead_code)]
fn write_path_list(path: &Path, paths: &BTreeSet<PathBuf>) -> Result<()> {
    let mut bytes = Vec::new();
    for source_path in paths {
        bytes.extend_from_slice(source_path.as_os_str().as_bytes());
        bytes.push(0);
    }
    write_file_durably(path, &bytes).map_err(|error| anyhow::anyhow!(error.to_string()))
}

fn git_paths(workspace_root: &Path, arguments: &[&str], action: &str) -> Result<BTreeSet<PathBuf>> {
    let bytes = git_bytes(workspace_root, arguments, action)?;
    let mut paths = BTreeSet::new();
    for raw in bytes.split(|byte| *byte == 0).filter(|entry| !entry.is_empty()) {
        let text = std::str::from_utf8(raw)
            .with_context(|| format!("git returned a non-UTF-8 path while trying to {action}"))?;
        let path = PathBuf::from(text);
        ensure!(
            path.components()
                .all(|component| matches!(component, Component::Normal(_))),
            "git returned unsafe source path {text:?}"
        );
        paths.insert(path);
    }
    Ok(paths)
}

fn git_bytes(workspace_root: &Path, arguments: &[&str], action: &str) -> Result<Vec<u8>> {
    let Output { status, stdout, stderr } = Command::new("git")
        .arg("-C")
        .arg(workspace_root)
        .args(arguments)
        .output()
        .with_context(|| format!("cannot {action}"))?;
    ensure!(
        status.success(),
        "cannot {action}: {}",
        String::from_utf8_lossy(&stderr).trim()
    );
    Ok(stdout)
}

fn require_lower_hex(value: &str, length: usize, label: &str) -> Result<()> {
    ensure!(
        value.len() == length
            && value
                .bytes()
                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte)),
        "{label} must be exactly {length} lowercase hexadecimal characters"
    );
    Ok(())
}

#[derive(Deserialize)]
struct LoadedJsonReceipt {
    status: String,
    canonical_json: Option<String>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct LoadedSourceSnapshotReceipt {
    id: String,
    status: String,
    canonical_json: Option<String>,
}

pub(crate) fn load_registered_source_snapshot_via_home(
    home_binary: &Path,
    registry: &Path,
    snapshot_id: &str,
) -> Result<Option<ImplementationSourceSnapshotV1>> {
    let bytes = crate::run_go_home(
        home_binary,
        "load-source-snapshot",
        registry,
        &serde_json::json!({ "id": snapshot_id }),
    )
    .map_err(|error| anyhow::anyhow!(error.to_string()))?;
    let loaded: LoadedSourceSnapshotReceipt =
        serde_json::from_slice(&bytes).context("Go Home source-snapshot load receipt is not the expected JSON")?;
    ensure!(
        loaded.id == snapshot_id,
        "Go Home source-snapshot receipt names {}, expected {snapshot_id}",
        loaded.id
    );
    match loaded.status.as_str() {
        "missing" => {
            ensure!(
                loaded.canonical_json.is_none(),
                "Go Home reported missing source snapshot {snapshot_id} with manifest JSON"
            );
            Ok(None)
        }
        "found" => {
            let canonical = loaded
                .canonical_json
                .with_context(|| format!("Go Home found source snapshot {snapshot_id} without manifest JSON"))?;
            let snapshot: ImplementationSourceSnapshotV1 = serde_json::from_str(&canonical)
                .with_context(|| format!("Go Home source snapshot {snapshot_id} is not the Rust contract"))?;
            snapshot.validate()?;
            ensure!(
                snapshot.snapshot_id == snapshot_id,
                "Go Home returned a different source snapshot identity"
            );
            ensure!(
                serde_json::to_string(&snapshot)? == canonical,
                "Go Home source snapshot {snapshot_id} is not canonical compact JSON"
            );
            Ok(Some(snapshot))
        }
        other => bail!("load-source-snapshot returned unknown status {other:?}"),
    }
}

pub(crate) fn load_json_object<T: serde::de::DeserializeOwned>(
    operation: &str,
    registry: &Path,
    id: &str,
) -> Result<Option<T>, String> {
    let bytes = crate::run_csharp_registry(
        operation,
        registry,
        &serde_json::json!({ "id": id }),
        crate::CSHARP_REGISTRY_PROCESS_DEADLINE,
    )
    .map_err(|error| error.to_string())?;
    let loaded: LoadedJsonReceipt = serde_json::from_slice(&bytes)
        .map_err(|error| format!("C# source-content load receipt is not JSON: {error}"))?;
    match loaded.status.as_str() {
        "missing" => Ok(None),
        "found" => {
            let json = loaded
                .canonical_json
                .ok_or_else(|| format!("{operation} found {id} with no JSON"))?;
            serde_json::from_str(&json).map(Some).map_err(|error| error.to_string())
        }
        other => Err(format!("{operation} returned unknown status {other:?}")),
    }
}

#[allow(dead_code)]
fn sync_file(path: &Path) -> Result<()> {
    File::open(path)
        .with_context(|| format!("cannot reopen {} for fsync", path.display()))?
        .sync_all()
        .with_context(|| format!("cannot fsync {}", path.display()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use fern_run::implementation_source_snapshot::IMPLEMENTATION_SOURCE_SNAPSHOT_SCHEMA;

    // ---- V1 archive reader ----
    //
    // GNU tar 1.34 was measured on 2026-08-20: it refuses absolute names,
    // `..` components, and writes through an extracted symlink by default,
    // and `-k` makes a duplicate member fail. Those cases assert that the
    // reader keeps that refusal rather than re-deriving tar's behaviour. The
    // manifest-comparison cases assert code written here.

    /// Forge an archive whose entries GNU tar itself will not create.
    fn forge_archive(at: &Path, python_entries: &str) {
        let script = format!(
            "import tarfile, io, os\nos.chdir({:?})\nwith tarfile.open('source.tgz','w:gz') as tf:\n{}\n",
            at.to_str().unwrap(),
            python_entries
        );
        let output = Command::new("/home/user/.venv/bin/python")
            .arg("-c")
            .arg(&script)
            .output()
            .expect("forge archive");
        assert!(
            output.status.success(),
            "forge failed: {}",
            String::from_utf8_lossy(&output.stderr)
        );
    }

    fn add_entry(path: &str, body: &str) -> String {
        format!(
            "    d = {body:?}.encode()\n    ti = tarfile.TarInfo({path:?}); ti.size = len(d); ti.mode = 0o644\n    tf.addfile(ti, io.BytesIO(d))"
        )
    }

    /// A snapshot directory whose manifest declares exactly `declared`.
    fn snapshot_fixture(
        store: &Path,
        declared: Vec<ArchivedSourceV1>,
        entries: &str,
    ) -> ImplementationSourceSnapshotV1 {
        let staging = store.join("staging");
        fs::create_dir_all(&staging).unwrap();
        forge_archive(&staging, entries);
        fs::write(staging.join(GIT_PATCH_FILE), b"").unwrap();
        let mut snapshot = ImplementationSourceSnapshotV1 {
            schema: IMPLEMENTATION_SOURCE_SNAPSHOT_SCHEMA.to_string(),
            snapshot_id: String::new(),
            title: "fixture".to_string(),
            research_design_id: "research-design:fixture".to_string(),
            base_commit: "a".repeat(40),
            tracked_tree_dirty: false,
            source_archive: stored_file(&staging.join(SOURCE_ARCHIVE_FILE), SOURCE_ARCHIVE_FILE).unwrap(),
            git_patch: stored_file(&staging.join(GIT_PATCH_FILE), GIT_PATCH_FILE).unwrap(),
            archived_sources: declared,
            untracked_sources: Vec::new(),
            cargo_lock_byte_digest: ByteDigest::from_bytes(b"lock").as_str().to_string(),
            recipe_catalog_source_digest: ByteDigest::from_bytes(b"catalog").as_str().to_string(),
        };
        snapshot.snapshot_id = snapshot.expected_snapshot_id().unwrap();
        let hex = snapshot
            .snapshot_id
            .strip_prefix("implementation-source-snapshot:")
            .unwrap();
        let directory = store.join(hex);
        fs::create_dir_all(&directory).unwrap();
        for name in [SOURCE_ARCHIVE_FILE, GIT_PATCH_FILE] {
            fs::rename(staging.join(name), directory.join(name)).unwrap();
        }
        let json = serde_json::to_vec_pretty(&snapshot).unwrap();
        fs::write(directory.join(MANIFEST_FILE), json).unwrap();
        snapshot
    }

    fn declared_file(path: &str, body: &str) -> ArchivedSourceV1 {
        ArchivedSourceV1 {
            path: path.to_string(),
            kind: SourceEntryKind::RegularFile,
            byte_digest: ByteDigest::from_bytes(body.as_bytes()).as_str().to_string(),
            bytes: body.len() as u64,
        }
    }

    fn go_home_test_binary() -> &'static Path {
        static BINARY: std::sync::OnceLock<PathBuf> = std::sync::OnceLock::new();
        BINARY
            .get_or_init(|| {
                let repository_root = Path::new(env!("CARGO_MANIFEST_DIR")).parent().unwrap();
                let output = std::env::temp_dir().join(format!(
                    "fern-home-implementation-snapshot-tests-{}",
                    std::process::id()
                ));
                let go = std::env::var_os("GO")
                    .map(PathBuf::from)
                    .or_else(|| std::env::var_os("HOME").map(|home| PathBuf::from(home).join(".local/bin/go")))
                    .filter(|path| path.is_file())
                    .expect("GO or $HOME/.local/bin/go must name the maintained Go toolchain");
                let status = Command::new(go)
                    .args(["build", "-o"])
                    .arg(&output)
                    .arg("./cmd/fern-home")
                    .current_dir(repository_root.join("tools/fern-control"))
                    .status()
                    .expect("build fern-home implementation-snapshot reader");
                assert!(status.success(), "go build ./cmd/fern-home failed");
                output
            })
            .as_path()
    }

    #[test]
    fn rust_source_snapshot_reader_crosses_go_home_process() {
        let store = TempDir::new().unwrap();
        let snapshot = snapshot_fixture(
            store.path(),
            vec![declared_file("src/lib.rs", "fn main() {}\n")],
            &add_entry("src/lib.rs", "fn main() {}\n"),
        );
        let registry = store.path().join("registry.db");
        crate::apply_registry_migrations_via_home(Some(go_home_test_binary()), &registry).unwrap();
        fn digest_hex(value: &str) -> &str {
            value.strip_prefix("sha256:").unwrap()
        }
        let request = serde_json::json!({
            "snapshot_id": snapshot.snapshot_id,
            "schema": snapshot.schema,
            "research_design_card_id": snapshot.research_design_id,
            "title": snapshot.title,
            "canonical_manifest_json": serde_json::to_string(&snapshot).unwrap(),
            "source_archive_sha256": digest_hex(&snapshot.source_archive.byte_digest),
            "git_patch_sha256": digest_hex(&snapshot.git_patch.byte_digest),
            "base_commit": snapshot.base_commit,
            "cargo_lock_sha256": digest_hex(&snapshot.cargo_lock_byte_digest),
            "recipe_catalog_source_sha256": digest_hex(&snapshot.recipe_catalog_source_digest),
            "registered_by": "rust-go-reader-boundary-test",
            "registered_at_ms": 1,
        });
        let registered =
            crate::run_go_home(go_home_test_binary(), "register-source-snapshot", &registry, &request).unwrap();
        assert!(
            String::from_utf8(registered)
                .unwrap()
                .contains("\"status\":\"registered\"")
        );

        let reopened =
            load_registered_source_snapshot_via_home(go_home_test_binary(), &registry, &snapshot.snapshot_id)
                .unwrap()
                .expect("Go Home must return the registered manifest");
        assert_eq!(reopened, snapshot);
    }

    #[test]
    fn an_honest_archive_reopens_to_its_declared_bytes() {
        let store = TempDir::new().unwrap();
        let snapshot = snapshot_fixture(
            store.path(),
            vec![declared_file("src/lib.rs", "fn main() {}\n")],
            &add_entry("src/lib.rs", "fn main() {}\n"),
        );
        let files = read_v1_archive(store.path(), &snapshot).expect("an honest archive maps");
        assert_eq!(files.get("src/lib.rs").unwrap().1, b"fn main() {}\n");
    }

    #[test]
    fn an_entry_the_manifest_does_not_declare_is_ignored() {
        let store = TempDir::new().unwrap();
        let entries = format!(
            "{}\n{}",
            add_entry("src/lib.rs", "ok\n"),
            add_entry("src/extra.rs", "smuggled\n")
        );
        let snapshot = snapshot_fixture(store.path(), vec![declared_file("src/lib.rs", "ok\n")], &entries);
        // The manifest declares a list of files and their bytes. An archive
        // that also carries something it never mentioned still contains
        // exactly the declared source, so it reads. Every member comes back,
        // because a caller with nothing declared has only these to work from;
        // what matters is that the declared file was proven.
        let members = read_v1_archive(store.path(), &snapshot).expect("an undeclared entry is not a defect");
        assert_eq!(members["src/lib.rs"].1, b"ok\n");
        assert!(members.contains_key("src/extra.rs"));
    }

    #[test]
    fn a_declared_file_missing_from_the_archive_is_refused() {
        let store = TempDir::new().unwrap();
        let snapshot = snapshot_fixture(
            store.path(),
            // The manifest requires stable path order: gone sorts before lib.
            vec![
                declared_file("src/gone.rs", "gone\n"),
                declared_file("src/lib.rs", "ok\n"),
            ],
            &add_entry("src/lib.rs", "ok\n"),
        );
        let error = read_v1_archive(store.path(), &snapshot).expect_err("a missing entry must be refused");
        assert!(error.contains("src/gone.rs") && error.contains("missing"), "{error}");
    }

    #[test]
    fn bytes_that_disagree_with_the_declared_digest_are_refused() {
        let store = TempDir::new().unwrap();
        let snapshot = snapshot_fixture(
            store.path(),
            vec![declared_file("src/lib.rs", "promised\n")],
            &add_entry("src/lib.rs", "delivered\n"),
        );
        let error = read_v1_archive(store.path(), &snapshot).expect_err("tampered bytes must be refused");
        assert!(error.contains("src/lib.rs"), "{error}");
    }

    #[test]
    fn an_absolute_member_never_reaches_the_declared_set() {
        let store = TempDir::new().unwrap();
        let entries = format!(
            "{}\n{}",
            add_entry("src/lib.rs", "ok\n"),
            add_entry("/etc/fern-probe", "pwned\n")
        );
        let snapshot = snapshot_fixture(store.path(), vec![declared_file("src/lib.rs", "ok\n")], &entries);
        // tar strips the leading slash, so it lands as etc/fern-probe inside
        // the extraction root, which is a temporary directory. The property
        // that matters is in this test's name and survives extras being
        // ignored: an absolute member never enters the declared set, so it
        // can never be mistaken for source, and it wrote nothing outside the
        // extraction root.
        let members = read_v1_archive(store.path(), &snapshot).expect("an undeclared member is not a defect");
        assert_eq!(members["src/lib.rs"].1, b"ok\n");
        // It landed inside the temporary extraction root under a relative
        // name, and nothing outside was written.
        assert!(!Path::new("/etc/fern-probe").exists());
    }

    #[test]
    fn a_dotdot_member_is_refused() {
        let store = TempDir::new().unwrap();
        let entries = format!(
            "{}\n{}",
            add_entry("src/lib.rs", "ok\n"),
            add_entry("../escape.rs", "pwned\n")
        );
        let snapshot = snapshot_fixture(store.path(), vec![declared_file("src/lib.rs", "ok\n")], &entries);
        let error = read_v1_archive(store.path(), &snapshot).expect_err("a traversal member must be refused");
        assert!(error.contains("tar refused"), "tar itself fails closed here: {error}");
    }

    #[test]
    fn a_duplicate_member_cannot_smuggle_bytes_into_a_declared_path() {
        let store = TempDir::new().unwrap();
        let entries = format!(
            "{}\n{}",
            add_entry("src/lib.rs", "first\n"),
            add_entry("src/lib.rs", "second\n")
        );
        let snapshot = snapshot_fixture(store.path(), vec![declared_file("src/lib.rs", "first\n")], &entries);
        // tar now overwrites rather than refusing, so the second copy is what
        // lands. It does not matter: the path is DECLARED, so whatever ends
        // up there still has to carry the declared bytes, and this does not.
        let error = read_v1_archive(store.path(), &snapshot).expect_err("a declared path must carry declared bytes");
        assert!(error.contains("src/lib.rs"), "{error}");
    }

    #[test]
    fn a_symlink_where_a_regular_file_is_declared_is_refused() {
        let store = TempDir::new().unwrap();
        let entries = "    ti = tarfile.TarInfo('src/lib.rs'); ti.type = tarfile.SYMTYPE; ti.linkname = '/etc/passwd'\n    tf.addfile(ti)";
        let snapshot = snapshot_fixture(store.path(), vec![declared_file("src/lib.rs", "ok\n")], entries);
        let error = read_v1_archive(store.path(), &snapshot).expect_err("a type swap must be refused");
        assert!(error.contains("extracted as"), "{error}");
    }

    #[test]
    fn a_symlinked_snapshot_directory_is_refused() {
        let store = TempDir::new().unwrap();
        let snapshot = snapshot_fixture(
            store.path(),
            vec![declared_file("src/lib.rs", "ok\n")],
            &add_entry("src/lib.rs", "ok\n"),
        );
        let hex = snapshot
            .snapshot_id
            .strip_prefix("implementation-source-snapshot:")
            .unwrap();
        let real = store.path().join(hex);
        let moved = store.path().join("moved");
        fs::rename(&real, &moved).unwrap();
        std::os::unix::fs::symlink(&moved, &real).unwrap();
        let error = read_v1_archive(store.path(), &snapshot).expect_err("a symlinked store entry must be refused");
        assert!(error.contains("is a symlink"), "{error}");
    }

    use tempfile::TempDir;

    fn run_git(root: &Path, args: &[&str]) {
        let output = Command::new("git").arg("-C").arg(root).args(args).output().unwrap();
        assert!(
            output.status.success(),
            "git {} failed: {}",
            args.join(" "),
            String::from_utf8_lossy(&output.stderr)
        );
    }

    fn fixture() -> TempDir {
        let directory = TempDir::new().unwrap();
        let root = directory.path();
        fs::create_dir_all(root.join("crates/fern-recipes/src")).unwrap();
        fs::write(root.join("Cargo.lock"), "version = 4\n").unwrap();
        fs::write(root.join("crates/fern-recipes/src/lib.rs"), "pub fn catalog() {}\n").unwrap();
        fs::write(root.join("tracked.txt"), "before\n").unwrap();
        // These paths have opposite `PathBuf` component order and UTF-8 string
        // order. A source manifest must use the latter.
        fs::create_dir_all(root.join("order")).unwrap();
        fs::write(root.join("order/file.rs"), "pub fn nested() {}\n").unwrap();
        fs::write(root.join("order-file.rs"), "pub fn flat() {}\n").unwrap();
        run_git(root, &["init", "-q"]);
        run_git(root, &["config", "user.name", "Fern Test"]);
        run_git(root, &["config", "user.email", "fern@example.invalid"]);
        run_git(
            root,
            &[
                "add",
                "Cargo.lock",
                "crates/fern-recipes/src/lib.rs",
                "order/file.rs",
                "order-file.rs",
                "tracked.txt",
            ],
        );
        run_git(root, &["commit", "-qm", "fixture"]);
        directory
    }

    const AUTHORED_DRAFT: &str = include_str!("../../docs/implementation-bundles/fern-python-v2.draft.json");

    #[test]
    fn passing_the_authored_draft_is_refused_where_the_mistake_is_made() {
        let directory = TempDir::new().unwrap();
        let path = directory.path().join("fern-python-v2.draft.json");
        fs::write(&path, AUTHORED_DRAFT).unwrap();
        let error = read_implementation_bundle_resolution(&path)
            .expect_err("a draft cannot mint a capability a worker could ever match");
        let message = format!("{error:#}");
        assert!(message.contains("authored bundle DRAFT"), "{message}");
        assert!(message.contains("probe-implementation"), "{message}");
    }

    #[test]
    fn undeclared_untracked_path_is_not_silently_selected() {
        let fixture = fixture();
        fs::write(fixture.path().join("new.rs"), "fn new() {}\n").unwrap();
        let all = git_paths(
            fixture.path(),
            &["ls-files", "-z", "--others", "--exclude-standard"],
            "list fixture files",
        )
        .unwrap();
        assert!(select_untracked_paths(fixture.path(), &all, &[]).unwrap().is_empty());
        assert!(
            select_untracked_paths(fixture.path(), &all, &[PathBuf::from("missing.rs")])
                .unwrap_err()
                .to_string()
                .contains("does not name")
        );
    }

}
