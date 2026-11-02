//! Immutable, content-addressed Fern scheduler and worker versions.
//!
//! A Plan parent starts a new process at every Matrix boundary. Executing a
//! mutable Cargo output there means a rebuild can replace or temporarily remove
//! the path between stages. This module publishes the exact executable bytes
//! once under its typed executable name so the Plan can record and reuse one
//! stable path.
//!
//! This module does not decide retention. A waiting Plan may have no process at
//! this instant and still need its version later, so only Registry Home can say
//! which digests remain live. In particular, `/proc` is never a deletion input.

use std::collections::BTreeSet;
use std::fs::{self, File, OpenOptions};
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

use sha2::{Digest, Sha256};
use thiserror::Error;
use uuid::Uuid;

const PUBLISH_LOCK_TIMEOUT: Duration = Duration::from_secs(120);
const PUBLISH_LOCK_POLL: Duration = Duration::from_millis(50);
const LEGACY_RUNNER_PREFIX: &str = "fern-run.";

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BinaryExecutableKind {
    Scheduler,
    Worker,
    RegistryHome,
    WorkerSite,
    StageRunner,
}

impl BinaryExecutableKind {
    fn prefix(self) -> &'static str {
        match self {
            Self::Scheduler => "fern-scheduler.",
            Self::Worker => "fern-worker.",
            Self::RegistryHome => "fern-home.",
            Self::WorkerSite => "fern-site.",
            Self::StageRunner => "fern-stage-runner.",
        }
    }
}

/// A validated full SHA-256 digest used as an immutable deployment version.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct BinaryDigest(String);

impl BinaryDigest {
    pub fn parse(value: impl Into<String>) -> Result<Self, InvalidBinaryDigest> {
        let value = value.into();
        if value.len() == 64
            && value
                .bytes()
                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        {
            Ok(Self(value))
        } else {
            Err(InvalidBinaryDigest { value })
        }
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl std::fmt::Display for BinaryDigest {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.0)
    }
}

#[derive(Debug, Error, PartialEq, Eq)]
#[error("binary digest must be 64 lowercase hexadecimal characters, got {value:?}")]
pub struct InvalidBinaryDigest {
    value: String,
}

/// The immutable executable identity a Plan Attempt stores in Registry Home.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PublishedBinaryVersion {
    pub digest: BinaryDigest,
    pub executable_path: PathBuf,
    pub byte_count: u64,
    pub reused_existing: bool,
}

impl PublishedBinaryVersion {
    pub fn identity(&self) -> BinaryVersionIdentity {
        BinaryVersionIdentity {
            digest: self.digest.clone(),
            executable_path: self.executable_path.clone(),
        }
    }
}

/// Holds the per-digest publication lock across the Registry Home hand-off.
///
/// The caller reads `identity()`, creates the Plan Attempt with that identity,
/// and only then calls `finish_registry_handoff`. A collector needs the same
/// lock, so it cannot delete the file in the gap between publication and the
/// Attempt transaction.
#[derive(Debug)]
pub struct BinaryPublication {
    published: PublishedBinaryVersion,
    _publication_lock: File,
}

impl BinaryPublication {
    pub fn identity(&self) -> BinaryVersionIdentity {
        self.published.identity()
    }

    pub fn finish_registry_handoff(self) -> PublishedBinaryVersion {
        self.published
    }
}

/// The two fields Registry Home binds atomically to one Plan Attempt.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct BinaryVersionIdentity {
    digest: BinaryDigest,
    executable_path: PathBuf,
}

impl BinaryVersionIdentity {
    pub fn parse(
        digest: impl Into<String>,
        executable_path: impl Into<PathBuf>,
    ) -> Result<Self, InvalidBinaryVersionIdentity> {
        let digest = BinaryDigest::parse(digest).map_err(InvalidBinaryVersionIdentity::Digest)?;
        let executable_path = executable_path.into();
        if !executable_path.is_absolute() {
            return Err(InvalidBinaryVersionIdentity::PathNotAbsolute { executable_path });
        }
        let names = [
            format!("{}{digest}", BinaryExecutableKind::Scheduler.prefix()),
            format!("{}{digest}", BinaryExecutableKind::Worker.prefix()),
            format!("{}{digest}", BinaryExecutableKind::RegistryHome.prefix()),
            format!("{}{digest}", BinaryExecutableKind::WorkerSite.prefix()),
            format!("{}{digest}", BinaryExecutableKind::StageRunner.prefix()),
            format!("{LEGACY_RUNNER_PREFIX}{digest}"),
        ];
        if !executable_path
            .file_name()
            .and_then(|name| name.to_str())
            .is_some_and(|name| names.iter().any(|expected| expected == name))
        {
            return Err(InvalidBinaryVersionIdentity::PathDoesNotNameDigest {
                executable_path,
                expected_names: names,
            });
        }
        Ok(Self {
            digest,
            executable_path,
        })
    }

    pub fn digest(&self) -> &BinaryDigest {
        &self.digest
    }

    pub fn executable_path(&self) -> &Path {
        &self.executable_path
    }
}

#[derive(Debug, Error, PartialEq, Eq)]
pub enum InvalidBinaryVersionIdentity {
    #[error(transparent)]
    Digest(InvalidBinaryDigest),
    #[error("content-addressed binary path must be absolute, got {executable_path}")]
    PathNotAbsolute { executable_path: PathBuf },
    #[error("content-addressed binary path {executable_path} must end with one of {expected_names:?}")]
    PathDoesNotNameDigest {
        executable_path: PathBuf,
        expected_names: [String; 6],
    },
}

#[derive(Debug, Error)]
pub enum BinaryDeploymentError {
    #[error("binary source {path} is not a regular file")]
    SourceNotRegular { path: PathBuf },
    #[error("published binary version {path} is not a regular file")]
    PublishedVersionNotRegular { path: PathBuf },
    #[error("cannot {action} {path}: {source}")]
    Io {
        action: &'static str,
        path: PathBuf,
        #[source]
        source: io::Error,
    },
    #[error(
        "binary source {path} changed while its immutable deployment version was being published: expected {expected_digest}, copied {copied_digest}; retry before claiming Plan authority"
    )]
    SourceChanged {
        path: PathBuf,
        expected_digest: BinaryDigest,
        copied_digest: BinaryDigest,
    },
    #[error(
        "content-addressed binary {path} is corrupt: its name requires {expected_digest}, but its bytes hash to {actual_digest}"
    )]
    ExistingVersionCorrupt {
        path: PathBuf,
        expected_digest: BinaryDigest,
        actual_digest: BinaryDigest,
    },
    #[error(
        "timed out after {waited_seconds} seconds waiting to publish binary version {digest}; no Plan authority was claimed"
    )]
    PublishLockTimeout { digest: BinaryDigest, waited_seconds: u64 },
    #[error("invalid entry in the binary version directory at {path}: {detail}")]
    InvalidVersionEntry { path: PathBuf, detail: String },
}

/// Re-hash one recorded version before a supervisor relies on it.
pub fn verify_binary_version(identity: &BinaryVersionIdentity) -> Result<u64, BinaryDeploymentError> {
    verify_existing_version(identity.executable_path(), identity.digest())?;
    fs::metadata(identity.executable_path())
        .map(|metadata| metadata.len())
        .map_err(|source_error| {
            io_error(
                "inspect verified binary version",
                identity.executable_path(),
                source_error,
            )
        })
}

/// Hash one executable file without publishing or mutating it.
pub fn binary_file_digest(path: &Path) -> Result<BinaryDigest, BinaryDeploymentError> {
    let metadata =
        fs::symlink_metadata(path).map_err(|source_error| io_error("inspect binary file", path, source_error))?;
    if !metadata.file_type().is_file() {
        return Err(BinaryDeploymentError::SourceNotRegular {
            path: path.to_path_buf(),
        });
    }
    hash_file(path).map(|(digest, _)| digest)
}

/// Locks and validates every current version before Registry Home is queried.
///
/// Holding all candidate locks is what makes the later retention snapshot
/// meaningful: a publisher cannot expose a file and then race its Attempt
/// insert against collection. Candidates are locked in digest order so two
/// collectors cannot deadlock each other.
pub fn prepare_binary_version_collection(
    deployment_root: &Path,
) -> Result<BinaryVersionCollection, BinaryDeploymentError> {
    let deployment_root = absolute_path(deployment_root)?;
    let mut candidates = Vec::new();
    for entry in fs::read_dir(&deployment_root)
        .map_err(|source_error| io_error("read binary deployment directory", &deployment_root, source_error))?
    {
        let entry = entry.map_err(|source_error| {
            io_error(
                "read entry in binary deployment directory",
                &deployment_root,
                source_error,
            )
        })?;
        let name = entry.file_name();
        let Some(name) = name.to_str() else {
            return Err(BinaryDeploymentError::InvalidVersionEntry {
                path: entry.path(),
                detail: "filename is not valid UTF-8".to_string(),
            });
        };
        let Some(digest) = [
            BinaryExecutableKind::Scheduler.prefix(),
            BinaryExecutableKind::Worker.prefix(),
            BinaryExecutableKind::RegistryHome.prefix(),
            BinaryExecutableKind::WorkerSite.prefix(),
            LEGACY_RUNNER_PREFIX,
        ]
        .into_iter()
        .find_map(|prefix| name.strip_prefix(prefix)) else {
            continue;
        };
        let identity = BinaryVersionIdentity::parse(digest, entry.path()).map_err(|error| {
            BinaryDeploymentError::InvalidVersionEntry {
                path: entry.path(),
                detail: error.to_string(),
            }
        })?;
        candidates.push(identity);
    }
    candidates.sort();

    let mut locks = Vec::with_capacity(candidates.len());
    for candidate in &candidates {
        locks.push(lock_version_publication(&deployment_root, candidate.digest())?);
    }

    // Another collector may have removed a candidate while this collector was
    // waiting for its lock. That is already the desired terminal state; omit
    // it. Every surviving candidate is verified before any deletion begins.
    candidates.retain(|candidate| candidate.executable_path().exists());
    for candidate in &candidates {
        verify_binary_version(candidate)?;
    }

    Ok(BinaryVersionCollection {
        deployment_root,
        candidates,
        _locks: locks,
    })
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BinaryCollectionReport {
    pub scanned_versions: usize,
    pub retained_versions: usize,
    pub removed_versions: usize,
    /// Bytes whose final hard link was removed, counted once per inode.
    pub reclaimed_unique_bytes: u64,
}

/// A collection candidate set whose publication locks are all held.
#[derive(Debug)]
pub struct BinaryVersionCollection {
    deployment_root: PathBuf,
    candidates: Vec<BinaryVersionIdentity>,
    _locks: Vec<File>,
}

impl BinaryVersionCollection {
    /// Remove only versions absent from a fresh Registry Home root set.
    pub fn remove_unreferenced(
        self,
        retained_versions: &[BinaryVersionIdentity],
    ) -> Result<BinaryCollectionReport, BinaryDeploymentError> {
        let retained = retained_versions.iter().cloned().collect::<BTreeSet<_>>();
        let scanned_versions = self.candidates.len();
        let mut retained_count = 0_usize;
        let mut removed_versions = 0_usize;
        let mut reclaimed_unique_bytes = 0_u64;
        #[cfg(unix)]
        let mut reclaimed_inodes = BTreeSet::new();

        for candidate in self.candidates {
            if retained.contains(&candidate) {
                retained_count += 1;
                continue;
            }
            let metadata = fs::metadata(candidate.executable_path()).map_err(|source_error| {
                io_error(
                    "inspect binary version before collection",
                    candidate.executable_path(),
                    source_error,
                )
            })?;
            fs::remove_file(candidate.executable_path()).map_err(|source_error| {
                io_error(
                    "remove unreferenced binary version",
                    candidate.executable_path(),
                    source_error,
                )
            })?;
            removed_versions += 1;
            #[cfg(unix)]
            {
                use std::os::unix::fs::MetadataExt;
                if metadata.nlink() == 1 && reclaimed_inodes.insert((metadata.dev(), metadata.ino())) {
                    reclaimed_unique_bytes = reclaimed_unique_bytes.saturating_add(metadata.len());
                }
            }
            #[cfg(not(unix))]
            {
                reclaimed_unique_bytes = reclaimed_unique_bytes.saturating_add(metadata.len());
            }
        }
        if removed_versions > 0 {
            File::open(&self.deployment_root)
                .and_then(|directory| directory.sync_all())
                .map_err(|source_error| {
                    io_error(
                        "fsync binary deployment directory after collection",
                        &self.deployment_root,
                        source_error,
                    )
                })?;
        }
        Ok(BinaryCollectionReport {
            scanned_versions,
            retained_versions: retained_count,
            removed_versions,
            reclaimed_unique_bytes,
        })
    }
}

/// Publish `source` once under its full content digest.
///
/// The source is hashed before any destination is selected. An existing exact
/// version is verified and reused without another copy. A new version is copied
/// to a unique staging file, hashed while copying, fsynced, and linked into its
/// final no-replacement name. Staging and final names share one inode until the
/// staging name is removed, so publication never occupies two copies' worth of
/// space. Copying instead of linking from the Cargo output keeps a later
/// in-place rebuild from mutating an allegedly content-addressed version.
pub fn publish_binary_version(
    source: &Path,
    deployment_root: &Path,
    kind: BinaryExecutableKind,
) -> Result<BinaryPublication, BinaryDeploymentError> {
    let source_metadata =
        fs::metadata(source).map_err(|source_error| io_error("inspect binary source", source, source_error))?;
    if !source_metadata.is_file() {
        return Err(BinaryDeploymentError::SourceNotRegular {
            path: source.to_path_buf(),
        });
    }

    fs::create_dir_all(deployment_root)
        .map_err(|source_error| io_error("create binary deployment directory", deployment_root, source_error))?;
    let (expected_digest, source_byte_count) = hash_file(source)?;
    let final_path = deployment_root.join(format!("{}{expected_digest}", kind.prefix()));

    // The no-replacement final link prevents corruption, but without this lock
    // several first launchers could still each write a full staging copy before
    // one wins that race. The caller keeps the lock through the registry insert,
    // which also closes the publish-before-Attempt collection race.
    let publication_lock = lock_version_publication(deployment_root, &expected_digest)?;
    if final_path.exists() {
        verify_existing_version(&final_path, &expected_digest)?;
        let final_path = absolute_path(&final_path)?;
        return Ok(BinaryPublication {
            published: PublishedBinaryVersion {
                digest: expected_digest,
                executable_path: final_path,
                byte_count: source_byte_count,
                reused_existing: true,
            },
            _publication_lock: publication_lock,
        });
    }

    let staging_path = deployment_root.join(format!(
        ".{}publish.{}.{}",
        kind.prefix(),
        std::process::id(),
        Uuid::new_v4().simple()
    ));
    let mut staging = StagingFile::create(&staging_path)?;
    let (copied_digest, copied_byte_count) = copy_and_hash(source, &staging_path, staging.file_mut())?;
    fs::set_permissions(&staging_path, immutable_executable_permissions(&source_metadata))
        .map_err(|source_error| io_error("preserve binary permissions on", &staging_path, source_error))?;
    staging
        .file_mut()
        .sync_all()
        .map_err(|source_error| io_error("fsync staged binary", &staging_path, source_error))?;

    if copied_digest != expected_digest || copied_byte_count != source_byte_count {
        return Err(BinaryDeploymentError::SourceChanged {
            path: source.to_path_buf(),
            expected_digest,
            copied_digest,
        });
    }

    match fs::hard_link(&staging_path, &final_path) {
        Ok(()) => {}
        Err(error) if error.kind() == io::ErrorKind::AlreadyExists => {
            verify_existing_version(&final_path, &expected_digest)?;
        }
        Err(source_error) => {
            return Err(io_error(
                "publish no-replacement binary version at",
                &final_path,
                source_error,
            ));
        }
    }
    staging.remove()?;
    File::open(deployment_root)
        .and_then(|directory| directory.sync_all())
        .map_err(|source_error| io_error("fsync binary deployment directory", deployment_root, source_error))?;

    let final_path = absolute_path(&final_path)?;
    Ok(BinaryPublication {
        published: PublishedBinaryVersion {
            digest: expected_digest,
            executable_path: final_path,
            byte_count: source_byte_count,
            reused_existing: false,
        },
        _publication_lock: publication_lock,
    })
}

fn absolute_path(path: &Path) -> Result<PathBuf, BinaryDeploymentError> {
    path.canonicalize()
        .map_err(|source_error| io_error("resolve published binary version", path, source_error))
}

fn immutable_executable_permissions(source_metadata: &fs::Metadata) -> fs::Permissions {
    let mut permissions = source_metadata.permissions();
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        permissions.set_mode(permissions.mode() & 0o555);
    }
    #[cfg(not(unix))]
    permissions.set_readonly(true);
    permissions
}

fn verify_existing_version(path: &Path, expected_digest: &BinaryDigest) -> Result<(), BinaryDeploymentError> {
    let metadata = fs::symlink_metadata(path)
        .map_err(|source_error| io_error("inspect published binary version", path, source_error))?;
    if !metadata.file_type().is_file() {
        return Err(BinaryDeploymentError::PublishedVersionNotRegular {
            path: path.to_path_buf(),
        });
    }
    let (actual_digest, _) = hash_file(path)?;
    if actual_digest != *expected_digest {
        return Err(BinaryDeploymentError::ExistingVersionCorrupt {
            path: path.to_path_buf(),
            expected_digest: expected_digest.clone(),
            actual_digest,
        });
    }
    Ok(())
}

fn hash_file(path: &Path) -> Result<(BinaryDigest, u64), BinaryDeploymentError> {
    let mut file = File::open(path).map_err(|source_error| io_error("open binary for hashing", path, source_error))?;
    let mut digest = Sha256::new();
    let mut buffer = [0_u8; 1024 * 1024];
    let mut byte_count = 0_u64;
    loop {
        let read = file
            .read(&mut buffer)
            .map_err(|source_error| io_error("read binary while hashing", path, source_error))?;
        if read == 0 {
            break;
        }
        byte_count = byte_count
            .checked_add(read as u64)
            .expect("usize read count always fits into u64 on supported Fern platforms");
        digest.update(&buffer[..read]);
    }
    Ok((BinaryDigest(format!("{:x}", digest.finalize())), byte_count))
}

fn copy_and_hash(
    source: &Path,
    destination_path: &Path,
    destination: &mut File,
) -> Result<(BinaryDigest, u64), BinaryDeploymentError> {
    let mut source_file =
        File::open(source).map_err(|source_error| io_error("open binary for publication", source, source_error))?;
    let mut digest = Sha256::new();
    let mut buffer = [0_u8; 1024 * 1024];
    let mut byte_count = 0_u64;
    loop {
        let read = source_file
            .read(&mut buffer)
            .map_err(|source_error| io_error("read binary for publication", source, source_error))?;
        if read == 0 {
            break;
        }
        byte_count = byte_count
            .checked_add(read as u64)
            .expect("usize read count always fits into u64 on supported Fern platforms");
        digest.update(&buffer[..read]);
        destination
            .write_all(&buffer[..read])
            .map_err(|source_error| io_error("write staged binary", destination_path, source_error))?;
    }
    Ok((BinaryDigest(format!("{:x}", digest.finalize())), byte_count))
}

fn lock_version_publication(deployment_root: &Path, digest: &BinaryDigest) -> Result<File, BinaryDeploymentError> {
    let lock_path = deployment_root.join(format!(".fern-binary.{digest}.publish.lock"));
    let lock_file = OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .open(&lock_path)
        .map_err(|source_error| io_error("open binary publication lock", &lock_path, source_error))?;
    let started = Instant::now();
    loop {
        match lock_file.try_lock() {
            Ok(()) => return Ok(lock_file),
            Err(fs::TryLockError::WouldBlock) if started.elapsed() < PUBLISH_LOCK_TIMEOUT => {
                std::thread::sleep(PUBLISH_LOCK_POLL);
            }
            Err(fs::TryLockError::WouldBlock) => {
                return Err(BinaryDeploymentError::PublishLockTimeout {
                    digest: digest.clone(),
                    waited_seconds: PUBLISH_LOCK_TIMEOUT.as_secs(),
                });
            }
            Err(fs::TryLockError::Error(source_error)) => {
                return Err(io_error("lock binary publication", &lock_path, source_error));
            }
        }
    }
}

fn io_error(action: &'static str, path: &Path, source: io::Error) -> BinaryDeploymentError {
    BinaryDeploymentError::Io {
        action,
        path: path.to_path_buf(),
        source,
    }
}

struct StagingFile {
    path: PathBuf,
    file: File,
}

impl StagingFile {
    fn create(path: &Path) -> Result<Self, BinaryDeploymentError> {
        let file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(path)
            .map_err(|source_error| io_error("create staged binary", path, source_error))?;
        Ok(Self {
            path: path.to_path_buf(),
            file,
        })
    }

    fn file_mut(&mut self) -> &mut File {
        &mut self.file
    }

    fn remove(self) -> Result<(), BinaryDeploymentError> {
        fs::remove_file(&self.path)
            .map_err(|source_error| io_error("remove staged binary name", &self.path, source_error))
    }
}

impl Drop for StagingFile {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.path);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn write_executable(path: &Path, bytes: &[u8]) {
        fs::write(path, bytes).unwrap();
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            fs::set_permissions(path, fs::Permissions::from_mode(0o755)).unwrap();
        }
    }

    #[test]
    fn repeated_publication_reuses_one_content_addressed_file() {
        let directory = tempfile::tempdir().unwrap();
        let source = directory.path().join("fern-run-source");
        let versions = directory.path().join("versions");
        write_executable(&source, b"same fern-run bytes");

        let first = publish_binary_version(&source, &versions, BinaryExecutableKind::Worker)
            .unwrap()
            .finish_registry_handoff();
        let second = publish_binary_version(&source, &versions, BinaryExecutableKind::Worker)
            .unwrap()
            .finish_registry_handoff();

        assert!(!first.reused_existing);
        assert!(second.reused_existing);
        assert_eq!(
            first,
            PublishedBinaryVersion {
                reused_existing: false,
                ..second.clone()
            }
        );
        assert_eq!(fs::read(&first.executable_path).unwrap(), b"same fern-run bytes");
        assert_eq!(published_version_count(&versions), 1);
    }

    #[test]
    fn a_rebuild_gets_a_different_version_without_changing_the_old_one() {
        let directory = tempfile::tempdir().unwrap();
        let source = directory.path().join("fern-run-source");
        let versions = directory.path().join("versions");
        write_executable(&source, b"first build");
        let first = publish_binary_version(&source, &versions, BinaryExecutableKind::Worker)
            .unwrap()
            .finish_registry_handoff();

        write_executable(&source, b"second build");
        let second = publish_binary_version(&source, &versions, BinaryExecutableKind::Worker)
            .unwrap()
            .finish_registry_handoff();

        assert_ne!(first.digest, second.digest);
        assert_ne!(first.executable_path, second.executable_path);
        assert_eq!(fs::read(&first.executable_path).unwrap(), b"first build");
        assert_eq!(fs::read(&second.executable_path).unwrap(), b"second build");
        assert_eq!(published_version_count(&versions), 2);
    }

    #[test]
    fn concurrent_first_launches_create_only_one_binary_copy() {
        let directory = tempfile::tempdir().unwrap();
        let source = directory.path().join("fern-run-source");
        let versions = directory.path().join("versions");
        write_executable(&source, b"one build launched by several plans");
        let barrier = std::sync::Arc::new(std::sync::Barrier::new(16));

        let outcomes = (0..16)
            .map(|_| {
                let source = source.clone();
                let versions = versions.clone();
                let barrier = barrier.clone();
                std::thread::spawn(move || {
                    barrier.wait();
                    publish_binary_version(&source, &versions, BinaryExecutableKind::Worker)
                        .unwrap()
                        .finish_registry_handoff()
                })
            })
            .collect::<Vec<_>>()
            .into_iter()
            .map(|thread| thread.join().unwrap())
            .collect::<Vec<_>>();

        assert_eq!(outcomes.iter().filter(|outcome| !outcome.reused_existing).count(), 1);
        assert!(
            outcomes
                .windows(2)
                .all(|pair| pair[0].executable_path == pair[1].executable_path)
        );
        assert_eq!(published_version_count(&versions), 1);
    }

    #[cfg(unix)]
    #[test]
    fn published_version_does_not_share_the_mutable_source_inode() {
        use std::os::unix::fs::{MetadataExt, PermissionsExt};

        let directory = tempfile::tempdir().unwrap();
        let source = directory.path().join("fern-run-source");
        let versions = directory.path().join("versions");
        write_executable(&source, b"immutable deployment bytes");

        let published = publish_binary_version(&source, &versions, BinaryExecutableKind::Worker)
            .unwrap()
            .finish_registry_handoff();

        assert_ne!(
            fs::metadata(&source).unwrap().ino(),
            fs::metadata(&published.executable_path).unwrap().ino()
        );
        assert_eq!(
            fs::metadata(&published.executable_path).unwrap().permissions().mode() & 0o222,
            0,
            "the content-addressed inode is not accidentally writable"
        );
    }

    #[test]
    fn a_corrupt_existing_version_is_refused_instead_of_reused() {
        let directory = tempfile::tempdir().unwrap();
        let source = directory.path().join("fern-run-source");
        let versions = directory.path().join("versions");
        write_executable(&source, b"checked deployment bytes");
        let published = publish_binary_version(&source, &versions, BinaryExecutableKind::Worker)
            .unwrap()
            .finish_registry_handoff();
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            fs::set_permissions(&published.executable_path, fs::Permissions::from_mode(0o755)).unwrap();
        }
        fs::write(&published.executable_path, b"tampered").unwrap();

        let error = publish_binary_version(&source, &versions, BinaryExecutableKind::Worker).unwrap_err();

        assert!(matches!(error, BinaryDeploymentError::ExistingVersionCorrupt { .. }));
    }

    #[test]
    fn digest_parser_refuses_short_or_uppercase_names() {
        assert!(BinaryDigest::parse("abc").is_err());
        assert!(BinaryDigest::parse("A".repeat(64)).is_err());
        assert!(BinaryDigest::parse("0".repeat(64)).is_ok());
    }

    #[test]
    fn registry_identity_requires_an_absolute_path_named_by_its_digest() {
        let digest = "0".repeat(64);
        let valid = BinaryVersionIdentity::parse(&digest, format!("/tmp/fern-worker.{digest}")).unwrap();
        assert_eq!(valid.digest().as_str(), digest);
        assert!(BinaryVersionIdentity::parse(&digest, format!("fern-worker.{digest}")).is_err());
        assert!(BinaryVersionIdentity::parse(&digest, "/tmp/fern-worker.wrong").is_err());
        assert!(BinaryVersionIdentity::parse(&digest, format!("/tmp/fern-scheduler.{digest}")).is_ok());
        assert!(BinaryVersionIdentity::parse(&digest, format!("/tmp/fern-home.{digest}")).is_ok());
        assert!(BinaryVersionIdentity::parse(&digest, format!("/tmp/fern-site.{digest}")).is_ok());
        assert!(BinaryVersionIdentity::parse(&digest, format!("/tmp/fern-stage-runner.{digest}")).is_ok());
        assert!(
            BinaryVersionIdentity::parse(&digest, format!("/tmp/fern-run.{digest}")).is_ok(),
            "immutable pre-split Registry history remains readable"
        );
    }

    #[test]
    fn collection_preserves_a_registry_root_using_the_legacy_runner_name() {
        let directory = tempfile::tempdir().unwrap();
        let source = directory.path().join("source");
        let versions = directory.path().join("versions");
        write_executable(&source, b"pre-split runner bytes");
        let published = publish_binary_version(&source, &versions, BinaryExecutableKind::Worker)
            .unwrap()
            .finish_registry_handoff();
        let legacy_path = versions.join(format!("{LEGACY_RUNNER_PREFIX}{}", published.digest));
        fs::rename(&published.executable_path, &legacy_path).unwrap();
        let legacy_identity = BinaryVersionIdentity::parse(published.digest.to_string(), &legacy_path).unwrap();

        let report = prepare_binary_version_collection(&versions)
            .unwrap()
            .remove_unreferenced(&[legacy_identity])
            .unwrap();

        assert_eq!(report.scanned_versions, 1);
        assert_eq!(report.retained_versions, 1);
        assert!(legacy_path.exists());
    }

    #[test]
    fn binary_file_digest_reads_the_exact_executable_bytes() {
        let directory = tempfile::tempdir().unwrap();
        let source = directory.path().join("fern-run-source");
        write_executable(&source, b"digest these executable bytes");

        let digest = binary_file_digest(&source).unwrap();

        assert_eq!(
            digest.as_str(),
            "ed8c3a50f52b2d6beec53eb09fe984d08aebdd76526a2999735980d4233fff78"
        );
    }

    #[test]
    fn collection_keeps_registry_roots_and_reports_unique_reclaimed_bytes() {
        let directory = tempfile::tempdir().unwrap();
        let source = directory.path().join("fern-run-source");
        let versions = directory.path().join("versions");
        write_executable(&source, b"retained build");
        let retained = publish_binary_version(&source, &versions, BinaryExecutableKind::Worker)
            .unwrap()
            .finish_registry_handoff();
        write_executable(&source, b"terminal build");
        let terminal = publish_binary_version(&source, &versions, BinaryExecutableKind::Worker)
            .unwrap()
            .finish_registry_handoff();

        let collection = prepare_binary_version_collection(&versions).unwrap();
        let report = collection.remove_unreferenced(&[retained.identity()]).unwrap();

        assert_eq!(report.scanned_versions, 2);
        assert_eq!(report.retained_versions, 1);
        assert_eq!(report.removed_versions, 1);
        assert_eq!(report.reclaimed_unique_bytes, terminal.byte_count);
        assert!(retained.executable_path.exists());
        assert!(!terminal.executable_path.exists());
    }

    #[test]
    fn collection_validates_every_candidate_before_deleting_any() {
        let directory = tempfile::tempdir().unwrap();
        let source = directory.path().join("fern-run-source");
        let versions = directory.path().join("versions");
        write_executable(&source, b"valid old build");
        let valid = publish_binary_version(&source, &versions, BinaryExecutableKind::Worker)
            .unwrap()
            .finish_registry_handoff();
        write_executable(&source, b"later corrupt build");
        let corrupt = publish_binary_version(&source, &versions, BinaryExecutableKind::Worker)
            .unwrap()
            .finish_registry_handoff();
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            fs::set_permissions(&corrupt.executable_path, fs::Permissions::from_mode(0o755)).unwrap();
        }
        fs::write(&corrupt.executable_path, b"tampered").unwrap();

        let error = prepare_binary_version_collection(&versions).unwrap_err();

        assert!(matches!(error, BinaryDeploymentError::ExistingVersionCorrupt { .. }));
        assert!(
            valid.executable_path.exists(),
            "validation failure performs no partial deletion"
        );
    }

    #[test]
    fn collection_waits_until_publication_has_crossed_the_registry_handoff() {
        let directory = tempfile::tempdir().unwrap();
        let source = directory.path().join("fern-run-source");
        let versions = directory.path().join("versions");
        write_executable(&source, b"newly published build");
        let publication = publish_binary_version(&source, &versions, BinaryExecutableKind::Worker).unwrap();
        let retained = publication.identity();
        let (send, receive) = std::sync::mpsc::channel();
        let collector_versions = versions.clone();
        let collector = std::thread::spawn(move || {
            let collection = prepare_binary_version_collection(&collector_versions).unwrap();
            send.send(collection).unwrap();
        });

        std::thread::sleep(Duration::from_millis(150));
        assert!(
            receive.try_recv().is_err(),
            "collector cannot pass a publisher before its Attempt is recorded"
        );
        let published = publication.finish_registry_handoff();
        let collection = receive.recv_timeout(Duration::from_secs(5)).unwrap();
        let report = collection.remove_unreferenced(&[retained]).unwrap();
        collector.join().unwrap();

        assert_eq!(report.retained_versions, 1);
        assert!(published.executable_path.exists());
    }

    fn published_version_count(directory: &Path) -> usize {
        fs::read_dir(directory)
            .unwrap()
            .filter_map(Result::ok)
            .filter(|entry| {
                entry.file_name().to_str().is_some_and(|name| {
                    [BinaryExecutableKind::Scheduler, BinaryExecutableKind::Worker]
                        .into_iter()
                        .any(|kind| name.starts_with(kind.prefix()))
                })
            })
            .count()
    }
}
