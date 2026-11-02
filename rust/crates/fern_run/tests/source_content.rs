//! Source content, implementation composition, and Research Design binding.

use std::collections::BTreeMap;
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::OnceLock;

use fern_run::implementation_source_snapshot::{
    ArchivedSourceV1, GIT_PATCH_FILE, IMPLEMENTATION_SOURCE_SNAPSHOT_SCHEMA, ImplementationSourceSnapshotV1,
    SOURCE_ARCHIVE_FILE, SourceEntryKind, StoredSnapshotFileV1,
};
use fern_run::source_content::{DeclarationSource, LegacyArchiveMembers, verify_materialized_tree};
use fern_run::source_content::{
    ImplementationBindingV2, LegacyMappingOutcome, LegacySnapshotAuditV1, LegacySnapshotClass, SOURCE_BLOBS_DIR,
    SourceContentV2, SourceFileV2, SourceImplementationV2, SourceObservationKindV2, SourceObservationV2,
    SourceRegistrationOutcome, audit_legacy_source_snapshots, map_all_legacy_source_snapshots,
    map_legacy_source_snapshot, materialize_source_content, publish_materialized_source_root, register_source_content,
    register_source_implementation, register_source_observation, write_source_blob, write_source_content_document,
};
use sha2::{Digest, Sha256};
use tempfile::TempDir;

/// Archive members as the resolver now hands them over: path, kind, bytes.
fn members<'a>(entries: impl IntoIterator<Item = (&'a str, &'a [u8])>) -> LegacyArchiveMembers {
    entries
        .into_iter()
        .map(|(path, bytes)| (path.to_string(), (SourceEntryKind::RegularFile, bytes.to_vec())))
        .collect()
}

fn digest(bytes: &[u8]) -> String {
    format!("sha256:{:x}", Sha256::digest(bytes))
}

fn go_home_binary() -> &'static Path {
    static BINARY: OnceLock<PathBuf> = OnceLock::new();
    BINARY
        .get_or_init(|| {
            let repository_root = Path::new(env!("CARGO_MANIFEST_DIR")).parent().unwrap();
            let output = std::env::temp_dir().join(format!("fern-home-source-snapshot-tests-{}", std::process::id()));
            let go = std::env::var_os("GO")
                .map(PathBuf::from)
                .or_else(|| std::env::var_os("HOME").map(|home| PathBuf::from(home).join(".local/bin/go")))
                .filter(|path| path.is_file())
                .expect("GO or $HOME/.local/bin/go must name the maintained Go toolchain");
            let built = Command::new(go)
                .args(["build", "-o"])
                .arg(&output)
                .arg("./cmd/fern-home")
                .current_dir(repository_root.join("tools/fern-control"))
                .status()
                .expect("build fern-home source-snapshot test reader");
            assert!(built.success(), "go build ./cmd/fern-home failed");
            output
        })
        .as_path()
}

fn migrate_registry_via_go(registry: &Path) {
    let mut child = Command::new(go_home_binary())
        .args(["registry-apply-migrations", "--registry"])
        .arg(registry)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("start fern-home Registry migration owner");
    serde_json::to_writer(child.stdin.as_mut().unwrap(), &serde_json::json!({})).unwrap();
    child.stdin.take().unwrap().flush().unwrap();
    let output = child.wait_with_output().unwrap();
    assert!(
        output.status.success(),
        "fern-home registry-apply-migrations refused: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    let receipt: serde_json::Value = serde_json::from_slice(&output.stdout).unwrap();
    assert_eq!(receipt["schema"], "fern.registry_migrations.v1");
    assert_eq!(receipt["registry"], registry.display().to_string());
}

fn register_source_snapshot_via_go(registry: &Path, snapshot: &ImplementationSourceSnapshotV1, registered_at_ms: i64) {
    fn digest_hex(value: &str) -> &str {
        value.strip_prefix("sha256:").unwrap()
    }
    let request = serde_json::json!({
        "snapshot_id": snapshot.snapshot_id,
        "schema": snapshot.schema,
        "research_design_card_id": snapshot.research_design_id,
        "title": snapshot.title,
        "canonical_manifest_json": serde_json::to_string(snapshot).unwrap(),
        "source_archive_sha256": digest_hex(&snapshot.source_archive.byte_digest),
        "git_patch_sha256": digest_hex(&snapshot.git_patch.byte_digest),
        "base_commit": snapshot.base_commit,
        "cargo_lock_sha256": digest_hex(&snapshot.cargo_lock_byte_digest),
        "recipe_catalog_source_sha256": digest_hex(&snapshot.recipe_catalog_source_digest),
        "registered_by": "rust-source-content-caller-test",
        "registered_at_ms": registered_at_ms,
    });
    let mut child = Command::new(go_home_binary())
        .args(["register-source-snapshot", "--registry"])
        .arg(registry)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("start fern-home source-snapshot writer");
    serde_json::to_writer(child.stdin.as_mut().unwrap(), &request).unwrap();
    child.stdin.take().unwrap().flush().unwrap();
    let output = child.wait_with_output().unwrap();
    assert!(
        output.status.success(),
        "fern-home register-source-snapshot refused: {}",
        String::from_utf8_lossy(&output.stderr)
    );
}

fn file(path: &str, bytes: &[u8]) -> SourceFileV2 {
    SourceFileV2 {
        path: path.to_string(),
        kind: SourceEntryKind::RegularFile,
        byte_digest: digest(bytes),
        bytes: bytes.len() as u64,
    }
}

fn content(files: Vec<SourceFileV2>, untracked: Vec<String>) -> SourceContentV2 {
    SourceContentV2::from_files(files, untracked, digest(b"lock"), digest(b"catalog")).unwrap()
}

fn v1_snapshot(title: &str, archive_bytes: &[u8], files: Vec<ArchivedSourceV1>) -> ImplementationSourceSnapshotV1 {
    let mut snapshot = ImplementationSourceSnapshotV1 {
        schema: IMPLEMENTATION_SOURCE_SNAPSHOT_SCHEMA.to_string(),
        snapshot_id: String::new(),
        title: title.to_string(),
        research_design_id: "research-design:source-content-fixture".to_string(),
        base_commit: "c".repeat(40),
        tracked_tree_dirty: false,
        source_archive: StoredSnapshotFileV1 {
            file_name: SOURCE_ARCHIVE_FILE.to_string(),
            byte_digest: digest(archive_bytes),
            bytes: archive_bytes.len() as u64,
        },
        git_patch: StoredSnapshotFileV1 {
            file_name: GIT_PATCH_FILE.to_string(),
            byte_digest: digest(&[]),
            bytes: 0,
        },
        archived_sources: files,
        untracked_sources: Vec::new(),
        cargo_lock_byte_digest: digest(b"lock"),
        recipe_catalog_source_digest: digest(b"catalog"),
    };
    snapshot.snapshot_id = snapshot.expected_snapshot_id().unwrap();
    snapshot
}

#[test]
fn title_and_design_are_not_source_content_identity() {
    let files = vec![file("src/lib.rs", b"pub fn f() {}\n"), file("Cargo.lock", b"lock\n")];
    let left = content(files.clone(), Vec::new());
    let right = content(files, Vec::new());
    assert_eq!(left.source_content_id, right.source_content_id);
    let implementation = SourceImplementationV2::from_content(&left).unwrap();
    let first = ImplementationBindingV2::new(
        implementation.implementation_id.clone(),
        "research-design:one".to_string(),
        "First title".to_string(),
        String::new(),
    )
    .unwrap();
    let second = ImplementationBindingV2::new(
        implementation.implementation_id.clone(),
        "research-design:two".to_string(),
        "Second title".to_string(),
        String::new(),
    )
    .unwrap();
    assert_eq!(first.implementation_id, second.implementation_id);
    assert_ne!(first.binding_id, second.binding_id);
}

#[test]
fn machine_path_is_not_source_content_identity() {
    let files = vec![file("src/lib.rs", b"code\n")];
    let left = content(files.clone(), Vec::new());
    let right = content(files, Vec::new());
    assert_eq!(left.source_content_id, right.source_content_id);
    assert!(!left.source_content_id.contains("/home/"));
    assert!(!left.source_content_id.contains("results/"));
}

#[test]
fn one_blob_change_does_not_copy_the_tree() {
    let directory = TempDir::new().unwrap();
    let cas = directory.path().join("deploy");
    let a = b"first file\n";
    let b = b"second file\n";
    let c = b"changed first\n";
    write_source_blob(&cas, a).unwrap();
    write_source_blob(&cas, b).unwrap();
    let before = fs::read_dir(cas.join(SOURCE_BLOBS_DIR)).unwrap().count();
    write_source_blob(&cas, c).unwrap();
    let after = fs::read_dir(cas.join(SOURCE_BLOBS_DIR)).unwrap().count();
    assert_eq!(before, 2);
    assert_eq!(after, 3);
}

#[test]
fn materialized_root_matches_file_manifest() {
    let directory = TempDir::new().unwrap();
    let cas = directory.path().join("deploy");
    let bytes = b"pub fn catalog() {}\n";
    write_source_blob(&cas, bytes).unwrap();
    write_source_blob(&cas, b"lock\n").unwrap();
    let source = content(
        vec![
            file("crates/fern-recipes/src/lib.rs", bytes),
            file("Cargo.lock", b"lock\n"),
        ],
        Vec::new(),
    );
    write_source_content_document(&cas, &source, &[]).unwrap();
    let dest = directory.path().join("root");
    materialize_source_content(&cas, &source, &dest).unwrap();
    assert_eq!(fs::read(dest.join("crates/fern-recipes/src/lib.rs")).unwrap(), bytes);
}

#[test]
fn legacy_snapshots_with_the_same_tree_converge() {
    let directory = TempDir::new().unwrap();
    let registry = directory.path().join("registry.db");
    migrate_registry_via_go(&registry);
    let files = vec![ArchivedSourceV1 {
        path: "src/lib.rs".to_string(),
        kind: SourceEntryKind::RegularFile,
        byte_digest: digest(b"lib\n"),
        bytes: 4,
    }];
    let first = v1_snapshot("First title", b"archive-same", files.clone());
    let second = v1_snapshot("Second title", b"archive-same", files);
    register_source_snapshot_via_go(&registry, &first, 10);
    register_source_snapshot_via_go(&registry, &second, 11);
    let cas = TempDir::new().unwrap();
    let resolve = |_snapshot: &ImplementationSourceSnapshotV1| Ok(members([("src/lib.rs", b"lib\n".as_slice())]));
    let store_directories = BTreeMap::from([(first.snapshot_id.clone(), None), (second.snapshot_id.clone(), None)]);
    let outcomes = map_all_legacy_source_snapshots(&registry, &resolve, &store_directories, cas.path(), 12).unwrap();
    assert_eq!(outcomes.len(), 2);
    let maps: Vec<_> = outcomes
        .iter()
        .map(|outcome| match outcome {
            LegacyMappingOutcome::Mapped(map) => map.clone(),
            LegacyMappingOutcome::Unresolved { snapshot_id, reason } => {
                panic!("{snapshot_id} should map: {reason}")
            }
        })
        .collect();
    assert_eq!(maps[0].source_content_id, maps[1].source_content_id);
    assert_eq!(maps[0].implementation_id, maps[1].implementation_id);
    assert_ne!(maps[0].binding_id, maps[1].binding_id);
    assert_ne!(
        maps[0].observation_id, maps[1].observation_id,
        "identical bytes converge to one content, but each snapshot is its own observation of them"
    );
}

/// Six real snapshots predate `archived_sources` and declare nothing, while
/// their archives are intact. The declaration is recovered from the members,
/// and the row says so, so a derived list is never mistaken for one the
/// author wrote.
#[test]
fn an_empty_declaration_is_backfilled_from_the_archive_and_says_so() {
    let directory = TempDir::new().unwrap();
    let registry = directory.path().join("registry.db");
    migrate_registry_via_go(&registry);
    let snapshot = v1_snapshot("Old", b"archive-old", Vec::new());
    register_source_snapshot_via_go(&registry, &snapshot, 10);
    let cas = TempDir::new().unwrap();
    let archive = members([("src/lib.rs", b"lib\n".as_slice()), ("notes.md", b"notes\n".as_slice())]);
    let outcome = map_legacy_source_snapshot(&registry, &snapshot, &archive, cas.path(), 11).unwrap();
    let LegacyMappingOutcome::Mapped(map) = outcome else {
        panic!("an intact archive supplies the declaration");
    };
    assert_eq!(map.declaration_source, DeclarationSource::BackfilledFromArchive);

    // The recovered declaration is the archive's members, and the bytes are
    // in the CAS, so the tree rebuilds like any other.
    let content = fern_run::source_content::load_source_content(&registry, &map.source_content_id)
        .unwrap()
        .expect("the backfilled content is readable");
    let paths: Vec<&str> = content.files.iter().map(|file| file.path.as_str()).collect();
    assert_eq!(paths, vec!["notes.md", "src/lib.rs"]);
    let rebuilt = directory.path().join("rebuilt");
    materialize_source_content(cas.path(), &content, &rebuilt).unwrap();
    verify_materialized_tree(&rebuilt, &content).expect("a backfilled tree rebuilds from the CAS");
}

/// Backfill needs an archive. With nothing declared AND nothing in the
/// archive there is no identity to recover, and two such snapshots would
/// otherwise collapse into one shared empty-tree identity.
#[test]
fn an_empty_declaration_with_an_empty_archive_still_cannot_map() {
    let directory = TempDir::new().unwrap();
    let registry = directory.path().join("registry.db");
    migrate_registry_via_go(&registry);
    let snapshot = v1_snapshot("Old", b"archive-old", Vec::new());
    register_source_snapshot_via_go(&registry, &snapshot, 10);
    let cas = TempDir::new().unwrap();
    let outcome = map_legacy_source_snapshot(&registry, &snapshot, &BTreeMap::new(), cas.path(), 11).unwrap();
    match outcome {
        LegacyMappingOutcome::Unresolved { reason, .. } => assert!(reason.contains("archive holds none")),
        LegacyMappingOutcome::Mapped(_) => panic!("nothing declared and nothing archived proves nothing"),
    }
    assert!(
        fern_run::source_content::load_legacy_map(&registry, &snapshot.snapshot_id)
            .unwrap()
            .is_none(),
        "an unresolved snapshot writes no immutable row"
    );
}

/// A snapshot that DID declare its files is taken at its word, and the row
/// records that, so the two provenances stay distinguishable forever.
#[test]
fn a_declared_snapshot_records_that_it_declared_itself() {
    let directory = TempDir::new().unwrap();
    let registry = directory.path().join("registry.db");
    migrate_registry_via_go(&registry);
    let snapshot = v1_snapshot("Declared", b"archive-declared", archived_lib());
    register_source_snapshot_via_go(&registry, &snapshot, 10);
    let cas = TempDir::new().unwrap();
    // The archive also carries an undeclared file; it must not enter the
    // declaration, because this snapshot declared its own.
    let archive = members([
        ("src/lib.rs", b"lib\n".as_slice()),
        ("stray.pyc", b"bytecode".as_slice()),
    ]);
    let outcome = map_legacy_source_snapshot(&registry, &snapshot, &archive, cas.path(), 11).unwrap();
    let LegacyMappingOutcome::Mapped(map) = outcome else {
        panic!("a declared snapshot maps");
    };
    assert_eq!(map.declaration_source, DeclarationSource::DeclaredBySnapshot);
    let content = fern_run::source_content::load_source_content(&registry, &map.source_content_id)
        .unwrap()
        .unwrap();
    let paths: Vec<&str> = content.files.iter().map(|file| file.path.as_str()).collect();
    assert_eq!(paths, vec!["src/lib.rs"], "the undeclared member stays out");
}

#[test]
fn paired_bindings_share_one_implementation() {
    let files = vec![file("src/lib.rs", b"shared\n")];
    let source = content(files, Vec::new());
    let implementation = SourceImplementationV2::from_content(&source).unwrap();
    let control = ImplementationBindingV2::new(
        implementation.implementation_id.clone(),
        "research-design:ablation".to_string(),
        "control".to_string(),
        "state_only".to_string(),
    )
    .unwrap();
    let treatment = ImplementationBindingV2::new(
        implementation.implementation_id.clone(),
        "research-design:ablation".to_string(),
        "treatment".to_string(),
        "separate_tde".to_string(),
    )
    .unwrap();
    assert_eq!(control.implementation_id, treatment.implementation_id);
    assert_ne!(control.binding_id, treatment.binding_id);
}

/// Two writers of the SAME digest must not share one staging file.
///
/// The failure this creates is silent: with a staging name derived only from
/// the digest, both writers open one file, and a short write can be published
/// under a digest that no longer describes it. Content addressing cannot
/// detect that on its own, because nothing rereads the blob afterwards.
#[test]
fn concurrent_writers_of_one_digest_publish_intact_bytes() {
    let cas = TempDir::new().expect("temp cas");
    let root = cas.path().to_path_buf();
    // Large enough that a truncated write is a different length, not a race
    // that happens to land on identical bytes.
    let bytes: Vec<u8> = (0..64_000u32).map(|index| (index % 251) as u8).collect();
    let expected = digest(&bytes);

    let handles: Vec<_> = (0..8)
        .map(|_| {
            let root = root.clone();
            let bytes = bytes.clone();
            std::thread::spawn(move || write_source_blob(&root, &bytes))
        })
        .collect();
    for handle in handles {
        let observed = handle.join().expect("writer thread").expect("blob write");
        assert_eq!(observed, expected, "every writer reports the same content address");
    }

    let published = fs::read(
        root.join(SOURCE_BLOBS_DIR)
            .join(expected.strip_prefix("sha256:").unwrap()),
    )
    .expect("published blob");
    assert_eq!(published, bytes, "the published bytes are the whole file");

    let leftovers: Vec<_> = fs::read_dir(root.join(SOURCE_BLOBS_DIR))
        .expect("blob dir")
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.file_name().to_string_lossy().into_owned())
        .filter(|name| name.starts_with(".staging-"))
        .collect();
    assert!(
        leftovers.is_empty(),
        "no staging file survives a successful publish: {leftovers:?}"
    );
}

/// A published blob whose bytes no longer match its name is a Conflict, not a
/// silent overwrite. This is the corruption case the reread exists to catch.
#[test]
fn a_corrupted_published_blob_is_refused_rather_than_replaced() {
    let cas = TempDir::new().expect("temp cas");
    let bytes = b"canonical source line\n";
    let address = write_source_blob(cas.path(), bytes).expect("first write");

    let path = cas
        .path()
        .join(SOURCE_BLOBS_DIR)
        .join(address.strip_prefix("sha256:").unwrap());
    fs::write(&path, b"tampered\n").expect("corrupt the published blob");

    let error = write_source_blob(cas.path(), bytes).expect_err("a mismatching blob must refuse");
    assert!(
        format!("{error}").contains("do not match this digest"),
        "the refusal names the mismatch: {error}"
    );
}

/// A manifest that parses is not evidence. If the archive bytes disagree with
/// the digest the manifest declares, the mapping is unresolved and the
/// Registry records nothing — the previous version registered "mapped" from
/// manifest fields alone, so the mapped content could fail to materialize.
#[test]
fn archive_bytes_that_disagree_with_the_manifest_map_nothing() {
    let directory = TempDir::new().unwrap();
    let cas = TempDir::new().unwrap();
    let registry = directory.path().join("registry.db");
    migrate_registry_via_go(&registry);
    let declared = vec![ArchivedSourceV1 {
        path: "src/lib.rs".to_string(),
        kind: SourceEntryKind::RegularFile,
        byte_digest: digest(b"the bytes the manifest promises\n"),
        bytes: 32,
    }];
    let snapshot = v1_snapshot("Tampered", b"archive", declared);
    register_source_snapshot_via_go(&registry, &snapshot, 10);

    let archive = members([("src/lib.rs", b"different bytes entirely\n".as_slice())]);
    let outcome = map_legacy_source_snapshot(&registry, &snapshot, &archive, cas.path(), 11).unwrap();
    match outcome {
        LegacyMappingOutcome::Unresolved { reason, .. } => {
            assert!(reason.contains("src/lib.rs"), "the refusal names the file: {reason}");
            assert!(reason.contains("hashes to"), "and says what it hashed to: {reason}");
        }
        LegacyMappingOutcome::Mapped(_) => panic!("mismatching archive bytes must not map"),
    }
    assert!(
        fern_run::source_content::load_legacy_map(&registry, &snapshot.snapshot_id)
            .unwrap()
            .is_none(),
        "nothing is written for a mapping that was not proven"
    );
    let blobs = cas.path().join(SOURCE_BLOBS_DIR);
    assert!(!blobs.exists(), "and no blob is written before every file verifies");
}

/// A rewritten `observation_id` must not describe itself. The preimage
/// excludes the id, so validate recomputes the address these facts must have.
#[test]
fn a_forged_observation_id_is_refused() {
    let source = content(vec![file("src/lib.rs", b"one\n")], Vec::new());
    let mut observation = SourceObservationV2::new(
        source.source_content_id.clone(),
        SourceObservationKindV2::LegacySnapshot {
            snapshot_id: "snapshot:abc".to_string(),
            declared_commit: "c".repeat(40),
        },
    )
    .unwrap();
    observation.validate().unwrap();
    observation.observation_id = format!("source-observation:{}", "f".repeat(64));
    let error = observation.validate().expect_err("a forged id must be refused");
    assert!(format!("{error}").contains("does not address"), "{error}");
}

/// Two different observations that collide on one id must Conflict. A present
/// primary key is not agreement about what it names.
#[test]
fn a_colliding_observation_id_with_other_facts_conflicts() {
    let directory = TempDir::new().unwrap();
    let registry = directory.path().join("registry.db");
    migrate_registry_via_go(&registry);
    let source = content(vec![file("src/lib.rs", b"one\n")], Vec::new());
    register_source_content(&registry, &source, "test", 10).unwrap();
    let honest = SourceObservationV2::new(
        source.source_content_id.clone(),
        SourceObservationKindV2::ExactGit {
            git_commit: "a".repeat(40),
            git_tree: "b".repeat(40),
        },
    )
    .unwrap();
    register_source_observation(&registry, &honest, "test", 10).unwrap();
    assert_eq!(
        register_source_observation(&registry, &honest, "test", 11).unwrap(),
        SourceRegistrationOutcome::AlreadyRegistered,
        "the same observation re-registers cleanly"
    );

    let mut impostor = SourceObservationV2::new(
        source.source_content_id.clone(),
        SourceObservationKindV2::ExactGit {
            git_commit: "d".repeat(40),
            git_tree: "e".repeat(40),
        },
    )
    .unwrap();
    impostor.observation_id = honest.observation_id.clone();
    let error = register_source_observation(&registry, &impostor, "test", 12)
        .expect_err("different facts under one id must be refused");
    // The identity gate fires first and is the stronger refusal: the id does
    // not address these facts, so the row is never consulted. The stored
    // comparison behind it still matters for a row tampered with directly.
    let message = format!("{error}");
    assert!(
        message.contains("does not address") || message.contains("disagrees"),
        "{message}"
    );
}

/// A proven mapping stays proven. Re-running after the archive goes offline
/// returns the verified map rather than demoting it to unresolved.
#[test]
fn a_verified_map_survives_an_offline_archive() {
    let directory = TempDir::new().unwrap();
    let cas = TempDir::new().unwrap();
    let registry = directory.path().join("registry.db");
    migrate_registry_via_go(&registry);
    let declared = vec![ArchivedSourceV1 {
        path: "src/lib.rs".to_string(),
        kind: SourceEntryKind::RegularFile,
        byte_digest: digest(b"lib\n"),
        bytes: 4,
    }];
    let snapshot = v1_snapshot("Once mapped", b"archive", declared);
    register_source_snapshot_via_go(&registry, &snapshot, 10);
    let archive = members([("src/lib.rs", b"lib\n".as_slice())]);
    let first = map_legacy_source_snapshot(&registry, &snapshot, &archive, cas.path(), 11).unwrap();
    let LegacyMappingOutcome::Mapped(proven) = first else {
        panic!("a matching archive must map");
    };

    // The archive is gone now. The proof is not.
    let second = map_legacy_source_snapshot(&registry, &snapshot, &BTreeMap::new(), cas.path(), 12).unwrap();
    match second {
        LegacyMappingOutcome::Mapped(again) => assert_eq!(again, proven),
        LegacyMappingOutcome::Unresolved { reason, .. } => {
            panic!("a proven map must not be demoted: {reason}")
        }
    }
}

fn declared_bundle_file(path: &str, body: &[u8]) -> fern_recipes::ImplementationBundleFileV2 {
    fern_recipes::ImplementationBundleFileV2 {
        path: path.to_string(),
        byte_sha256: digest(body),
        bytes: body.len() as u64,
    }
}

/// Coverage is what ties the authored bundle — whose digests the model-link
/// probe enforces — to the bytes a Stage will execute. Without it a Binding can
/// name content that does not contain the program the probe approved.
#[test]
fn coverage_requires_every_declared_implementation_file() {
    let source = content(
        vec![file("fr.py", b"def train(): ...\n"), file("uv.lock", b"locked\n")],
        Vec::new(),
    );
    source
        .require_implementation_coverage(&[
            declared_bundle_file("fr.py", b"def train(): ...\n"),
            declared_bundle_file("uv.lock", b"locked\n"),
        ])
        .expect("content covering every declared file passes");

    let absent = source
        .require_implementation_coverage(&[declared_bundle_file("fr_core.py", b"missing\n")])
        .expect_err("a file the content does not hold must refuse");
    assert!(format!("{absent}").contains("absent from source content"), "{absent}");

    // The path is present but the bytes are someone else's.
    let swapped = source
        .require_implementation_coverage(&[declared_bundle_file("fr.py", b"def train(): pass\n")])
        .expect_err("different bytes under a declared path must refuse");
    assert!(format!("{swapped}").contains("does not match the bytes"), "{swapped}");
}

/// Multi-component authoring is a later slice. Until then an implementation
/// that composes more than one content must say so, never run the first.
#[test]
fn a_multi_component_implementation_refuses_as_not_wired() {
    let source = content(vec![file("fr.py", b"core\n")], Vec::new());
    let mut implementation = SourceImplementationV2::from_content(&source).unwrap();
    assert_eq!(
        implementation.sole_component_content_id().unwrap(),
        source.source_content_id
    );

    implementation
        .component_content_ids
        .push("source-content:".to_string() + &"a".repeat(64));
    let error = implementation
        .sole_component_content_id()
        .expect_err("two components cannot be planned yet");
    let message = format!("{error}");
    assert!(message.contains("not wired"), "it says why, not just no: {message}");
    assert!(message.contains('2'), "and how many it found: {message}");
}

/// A published root is complete or absent. Materializing straight into the
/// destination leaves a partial tree that verification rejects forever, so the
/// failure outlives the interruption that caused it.
/// Gap 3: a content-addressed root that is a symbolic link would put whatever
/// mutable tree it points at behind an identity that promises fixed bytes, and
/// every later verification would read that tree instead.
#[test]
fn a_symlinked_source_root_destination_is_refused() {
    let cas = TempDir::new().unwrap();
    let roots = TempDir::new().unwrap();
    let elsewhere = TempDir::new().unwrap();
    let source = content(vec![file("src/lib.rs", b"one\n")], Vec::new());
    write_source_blob(cas.path(), b"one\n").unwrap();
    let hex = source
        .source_content_id
        .strip_prefix("source-content:")
        .expect("validated source-content id");
    fs::create_dir_all(roots.path()).unwrap();
    std::os::unix::fs::symlink(elsewhere.path(), roots.path().join(hex)).unwrap();
    let error = publish_materialized_source_root(cas.path(), &source, roots.path())
        .expect_err("a symlinked destination must be refused");
    assert!(format!("{error}").contains("symbolic link"), "{error}");
}

#[test]
fn a_published_source_root_is_reusable_and_verified() {
    let cas = TempDir::new().unwrap();
    let roots = TempDir::new().unwrap();
    let source = content(
        vec![file("src/lib.rs", b"one\n"), file("uv.lock", b"lock\n")],
        Vec::new(),
    );
    for file_entry in &source.files {
        let bytes: &[u8] = match file_entry.path.as_str() {
            "src/lib.rs" => b"one\n",
            _ => b"lock\n",
        };
        write_source_blob(cas.path(), bytes).unwrap();
    }
    let first = publish_materialized_source_root(cas.path(), &source, roots.path()).unwrap();
    assert_eq!(fs::read(first.join("src/lib.rs")).unwrap(), b"one\n");

    // Publishing again reuses the verified root rather than rebuilding it.
    let second = publish_materialized_source_root(cas.path(), &source, roots.path()).unwrap();
    assert_eq!(first, second);

    let staging: Vec<_> = fs::read_dir(roots.path())
        .unwrap()
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.file_name().to_string_lossy().into_owned())
        .filter(|name| name.starts_with(".staging-"))
        .collect();
    assert!(
        staging.is_empty(),
        "no staging directory survives a publish: {staging:?}"
    );

    // A root whose DECLARED bytes have drifted is refused, not silently reused.
    fs::write(first.join("src/lib.rs"), b"tampered\n").unwrap();
    let error = publish_materialized_source_root(cas.path(), &source, roots.path())
        .expect_err("a drifted root must be refused");
    assert!(!format!("{error}").is_empty());
}

/// One Registry and one store holding every case the audit has to separate.
///
/// `valid` and `already_mapped` deliberately declare the SAME tree from
/// different archives. That is what makes the write plan interesting: they are
/// two snapshots and two bindings, but one SourceContent and one
/// Implementation, so a plan that counted five rows per snapshot would be
/// wrong here in a way a same-tree pair is the only thing that shows.
struct LegacyAuditFixture {
    _home: TempDir,
    cas: TempDir,
    registry: std::path::PathBuf,
    valid: ImplementationSourceSnapshotV1,
    already_mapped: ImplementationSourceSnapshotV1,
    mismatch: ImplementationSourceSnapshotV1,
    missing_directory: ImplementationSourceSnapshotV1,
    orphan_identity: String,
    store_directories: BTreeMap<String, Option<String>>,
}

fn archived_lib() -> Vec<ArchivedSourceV1> {
    vec![ArchivedSourceV1 {
        path: "src/lib.rs".to_string(),
        kind: SourceEntryKind::RegularFile,
        byte_digest: digest(b"lib\n"),
        bytes: 4,
    }]
}

/// Ok for anything whose archive can be reopened; Err for the one snapshot
/// whose directory is there and disagrees. The missing-directory snapshot
/// never reaches here, which is the point of checking the store first.
fn audit_resolver(snapshot: &ImplementationSourceSnapshotV1) -> Result<LegacyArchiveMembers, String> {
    if snapshot.title == "Mismatch" {
        return Err("src/lib.rs hashes to sha256:dead, but the manifest declares something else".to_string());
    }
    Ok(members([("src/lib.rs", b"lib\n".as_slice())]))
}

fn legacy_audit_fixture() -> LegacyAuditFixture {
    let home = TempDir::new().unwrap();
    let registry = home.path().join("registry.db");
    migrate_registry_via_go(&registry);
    let valid = v1_snapshot("Valid", b"archive-valid", archived_lib());
    let already_mapped = v1_snapshot("Already mapped", b"archive-already", archived_lib());
    let mismatch = v1_snapshot("Mismatch", b"archive-mismatch", archived_lib());
    let missing_directory = v1_snapshot("Missing directory", b"archive-missing", archived_lib());
    for (index, snapshot) in [&valid, &already_mapped, &mismatch, &missing_directory]
        .iter()
        .enumerate()
    {
        register_source_snapshot_via_go(&registry, snapshot, 10 + index as i64);
    }
    let orphan_identity = format!("implementation-source-snapshot:{}", "f".repeat(64));
    // Everything except `missing_directory` has a directory in the store, and
    // one directory has no row at all.
    let store_directories = BTreeMap::from([
        (valid.snapshot_id.clone(), Some("Valid".to_string())),
        (already_mapped.snapshot_id.clone(), Some("Already mapped".to_string())),
        (mismatch.snapshot_id.clone(), Some("Mismatch".to_string())),
        (orphan_identity.clone(), Some("Orphan title".to_string())),
    ]);
    LegacyAuditFixture {
        _home: home,
        cas: TempDir::new().unwrap(),
        registry,
        valid,
        already_mapped,
        mismatch,
        missing_directory,
        orphan_identity,
        store_directories,
    }
}

fn published_blob_count(cas: &TempDir) -> usize {
    let blobs = cas.path().join(SOURCE_BLOBS_DIR);
    match fs::read_dir(&blobs) {
        Ok(entries) => entries.filter_map(Result::ok).filter(|e| e.path().is_file()).count(),
        Err(_) => 0,
    }
}

fn class_of<'a>(audit: &'a LegacySnapshotAuditV1, identity: &str) -> &'a LegacySnapshotClass {
    &audit
        .entries
        .iter()
        .find(|entry| entry.identity == identity)
        .unwrap_or_else(|| panic!("the audit says nothing about {identity}"))
        .class
}

#[test]
fn the_preview_separates_all_five_cases_and_writes_nothing() {
    let fixture = legacy_audit_fixture();
    map_legacy_source_snapshot(
        &fixture.registry,
        &fixture.already_mapped,
        &audit_resolver(&fixture.already_mapped).unwrap(),
        fixture.cas.path(),
        11,
    )
    .unwrap();
    let blobs_before = published_blob_count(&fixture.cas);

    let audit = audit_legacy_source_snapshots(
        &fixture.registry,
        &audit_resolver,
        &fixture.store_directories,
        fixture.cas.path(),
    )
    .unwrap();

    assert!(matches!(
        class_of(&audit, &fixture.already_mapped.snapshot_id),
        LegacySnapshotClass::AlreadyMapped(_)
    ));
    assert!(matches!(
        class_of(&audit, &fixture.valid.snapshot_id),
        LegacySnapshotClass::RegisteredArchiveValid(_)
    ));
    assert!(matches!(
        class_of(&audit, &fixture.mismatch.snapshot_id),
        LegacySnapshotClass::ManifestOrArchiveMismatch { .. }
    ));
    assert!(matches!(
        class_of(&audit, &fixture.missing_directory.snapshot_id),
        LegacySnapshotClass::RegisteredDirectoryMissing
    ));
    match class_of(&audit, &fixture.orphan_identity) {
        LegacySnapshotClass::DirectoryWithoutRegistryRow { manifest_title } => {
            assert_eq!(manifest_title.as_deref(), Some("Orphan title"))
        }
        other => panic!("the unclaimed directory is not {other:?}"),
    }

    assert_eq!(audit.unresolved, 2, "the orphan is not an unmapped snapshot");
    // Three rows, not the five an apply of one snapshot might suggest: the
    // already-mapped twin registered this content and implementation already.
    assert_eq!(audit.planned_writes.source_contents, 0);
    assert_eq!(audit.planned_writes.source_implementations, 0);
    assert_eq!(audit.planned_writes.source_observations, 1);
    assert_eq!(audit.planned_writes.implementation_bindings, 1);
    assert_eq!(audit.planned_writes.legacy_source_snapshot_maps, 1);
    assert_eq!(audit.planned_writes.total(), 3);
    assert_eq!(
        audit.cas_byte_increase, 0,
        "the twin already published this blob, so an apply costs no new bytes"
    );

    assert!(
        fern_run::source_content::load_legacy_map(&fixture.registry, &fixture.valid.snapshot_id)
            .unwrap()
            .is_none(),
        "a report must not map the snapshot it calls ready"
    );
    assert_eq!(
        published_blob_count(&fixture.cas),
        blobs_before,
        "a report must publish no blob"
    );
}

#[test]
fn the_preview_charges_for_blobs_the_cas_does_not_hold() {
    let fixture = legacy_audit_fixture();
    let audit = audit_legacy_source_snapshots(
        &fixture.registry,
        &audit_resolver,
        &fixture.store_directories,
        fixture.cas.path(),
    )
    .unwrap();
    // Nothing is published yet, and both provable snapshots declare the one
    // four-byte file, so the shared blob is charged once rather than twice.
    assert_eq!(audit.registered_archive_valid, 2);
    assert_eq!(audit.cas_byte_increase, 4);
    assert_eq!(audit.planned_writes.source_contents, 1);
    assert_eq!(audit.planned_writes.source_implementations, 1);
    assert_eq!(audit.planned_writes.legacy_source_snapshot_maps, 2);
}

#[test]
fn apply_maps_only_the_proven_snapshot_and_a_second_apply_changes_nothing() {
    let fixture = legacy_audit_fixture();
    map_legacy_source_snapshot(
        &fixture.registry,
        &fixture.already_mapped,
        &audit_resolver(&fixture.already_mapped).unwrap(),
        fixture.cas.path(),
        11,
    )
    .unwrap();

    let outcomes = map_all_legacy_source_snapshots(
        &fixture.registry,
        &audit_resolver,
        &fixture.store_directories,
        fixture.cas.path(),
        12,
    )
    .unwrap();
    let mapped: Vec<_> = outcomes
        .iter()
        .filter_map(|outcome| match outcome {
            LegacyMappingOutcome::Mapped(map) => Some(map.clone()),
            LegacyMappingOutcome::Unresolved { .. } => None,
        })
        .collect();
    assert_eq!(mapped.len(), 2, "the ready snapshot and the already-mapped one");
    assert_eq!(
        outcomes.len(),
        4,
        "the orphan directory is not a snapshot and produces no outcome"
    );
    assert!(
        fern_run::source_content::load_legacy_map(&fixture.registry, &fixture.mismatch.snapshot_id)
            .unwrap()
            .is_none(),
        "a snapshot whose archive disagrees writes no row"
    );

    // A written row claims the source can be rebuilt. Rebuild it.
    let valid_map = fern_run::source_content::load_legacy_map(&fixture.registry, &fixture.valid.snapshot_id)
        .unwrap()
        .expect("the proven snapshot is mapped");
    let content = fern_run::source_content::load_source_content(&fixture.registry, &valid_map.source_content_id)
        .unwrap()
        .expect("the mapping registered its content");
    let rebuilt = TempDir::new().unwrap();
    let destination = rebuilt.path().join("tree");
    materialize_source_content(fixture.cas.path(), &content, &destination).unwrap();
    fern_run::source_content::verify_materialized_tree(&destination, &content).unwrap();

    let blobs_after_first = published_blob_count(&fixture.cas);
    let second = map_all_legacy_source_snapshots(
        &fixture.registry,
        &audit_resolver,
        &fixture.store_directories,
        fixture.cas.path(),
        13,
    )
    .unwrap();
    let remapped = fern_run::source_content::load_legacy_map(&fixture.registry, &fixture.valid.snapshot_id)
        .unwrap()
        .unwrap();
    assert_eq!(remapped, valid_map, "a rerun returns the row it proved, not a new one");
    assert_eq!(second.len(), outcomes.len());
    assert_eq!(published_blob_count(&fixture.cas), blobs_after_first);

    let after = audit_legacy_source_snapshots(
        &fixture.registry,
        &audit_resolver,
        &fixture.store_directories,
        fixture.cas.path(),
    )
    .unwrap();
    assert_eq!(after.registered_archive_valid, 0, "nothing is left ready to map");
    assert_eq!(after.already_mapped, 2);
    assert_eq!(after.planned_writes.total(), 0);
    assert_eq!(after.cas_byte_increase, 0, "a rerun after apply costs no new bytes");
    assert_eq!(
        after.unresolved, 2,
        "the two that could not be proven are still unresolved"
    );
}

// ── Declared files are the identity ─────────────────────────────────────────
// Verification checks the declared list and nothing else. Undeclared files
// are ignored; a missing or changed declared file is refused by name.

fn published_root_with_a_module(cas: &TempDir, roots: &TempDir) -> (std::path::PathBuf, SourceContentV2) {
    let module = b"VALUE = 1\n";
    let source = content(vec![file("importable_module.py", module)], Vec::new());
    write_source_blob(cas.path(), module).unwrap();
    let root = publish_materialized_source_root(cas.path(), &source, roots.path()).unwrap();
    (root, source)
}

/// The case that drove the change: six real roots held 521 `.pyc` files and
/// were read as broken, though every declared byte was exactly right.
#[test]
fn a_root_polluted_with_undeclared_files_still_verifies() {
    let cas = TempDir::new().unwrap();
    let roots = TempDir::new().unwrap();
    let (root, source) = published_root_with_a_module(&cas, &roots);

    fs::create_dir_all(root.join("__pycache__")).unwrap();
    fs::write(root.join("__pycache__/importable_module.pyc"), b"bytecode").unwrap();
    fs::create_dir_all(root.join(".ai")).unwrap();
    fs::write(root.join(".ai/agent.md"), b"notes\n").unwrap();
    fs::write(root.join("scratch.txt"), b"anything\n").unwrap();

    verify_materialized_tree(&root, &source).expect("undeclared files cannot make the declared source wrong");

    // Publishing again reuses the root rather than refusing it.
    publish_materialized_source_root(cas.path(), &source, roots.path()).expect("a reused root is still verifiable");
}

#[test]
fn a_changed_declared_file_is_refused_and_named() {
    let cas = TempDir::new().unwrap();
    let roots = TempDir::new().unwrap();
    let (root, source) = published_root_with_a_module(&cas, &roots);

    fs::write(root.join("importable_module.py"), b"VALUE = 999\n").unwrap();
    let error = verify_materialized_tree(&root, &source).expect_err("a changed declared file must be refused");
    assert!(
        format!("{error}").contains("importable_module.py"),
        "the refusal must name the file: {error}"
    );
}

#[test]
fn a_missing_declared_file_is_refused_and_named() {
    let cas = TempDir::new().unwrap();
    let roots = TempDir::new().unwrap();
    let (root, source) = published_root_with_a_module(&cas, &roots);

    fs::remove_file(root.join("importable_module.py")).unwrap();
    let error = verify_materialized_tree(&root, &source).expect_err("a missing declared file must be refused");
    let text = format!("{error}");
    assert!(
        text.contains("importable_module.py") && text.contains("missing"),
        "{text}"
    );
}

/// Publishing leaves the tree writable. The owner writes Python anywhere,
/// and an import that caches bytecode beside the source is not a defect.
#[test]
fn a_published_root_is_writable_and_an_import_inside_it_is_harmless() {
    let cas = TempDir::new().unwrap();
    let roots = TempDir::new().unwrap();
    let (root, source) = published_root_with_a_module(&cas, &roots);

    fs::write(root.join("new_note.txt"), b"written after publication\n").expect("a published root stays writable");

    let output = std::process::Command::new("python3")
        .arg("-c")
        .arg("import importable_module; print(importable_module.VALUE)")
        .current_dir(&root)
        .env_remove("PYTHONDONTWRITEBYTECODE")
        .output();
    if let Ok(output) = output {
        assert!(output.status.success(), "{}", String::from_utf8_lossy(&output.stderr));
    }
    verify_materialized_tree(&root, &source).expect("whatever the import left behind, the declared bytes stand");
}

/// The older archive-named roots every plan runs from obey the same rule.
#[test]
fn v1_verify_source_root_ignores_undeclared_files_and_names_a_changed_one() {
    use fern_run::implementation_source_snapshot::verify_source_root;

    let root = TempDir::new().unwrap();
    let snapshot = v1_snapshot("declared-files-fixture", b"archive", archived_lib());
    fs::create_dir_all(root.path().join("src")).unwrap();
    fs::write(root.path().join("src/lib.rs"), b"lib\n").unwrap();
    verify_source_root(root.path(), &snapshot).expect("the declared file is present with the declared bytes");

    fs::create_dir_all(root.path().join("src/__pycache__")).unwrap();
    fs::write(root.path().join("src/__pycache__/lib.pyc"), b"bytecode").unwrap();
    fs::write(root.path().join("untracked_note.md"), b"anything\n").unwrap();
    verify_source_root(root.path(), &snapshot).expect("undeclared files are not part of the claim");

    fs::write(root.path().join("src/lib.rs"), b"changed\n").unwrap();
    let error = verify_source_root(root.path(), &snapshot).expect_err("a changed declared file must be refused");
    assert!(format!("{error}").contains("src/lib.rs"), "{error}");
}

// ── The Implementation's capability digest ────────────────────────────────
//
// It used to be the digest of a compile-time entrypoint table: identical for
// every Implementation ever frozen, so it separated nothing. It is now derived
// from the bundle when one is supplied. These tests hold both ends of that
// change: the rows already bound to Coordinates must keep their identity, and
// two genuinely different bundles must stop sharing one.

fn bundle(module: &str, source_files: &[&str]) -> fern_recipes::ImplementationBundleDraftV2 {
    fern_recipes::ImplementationBundleDraftV2 {
        schema: "fern.implementation-bundle-draft.v2".to_string(),
        runtime: fern_recipes::ImplementationRuntimeV2::PythonModule {
            module: module.to_string(),
        },
        protocol_version: 1,
        source_files: source_files.iter().map(|path| path.to_string()).collect(),
        dependency_lock_files: vec!["uv.lock".to_string()],
        active_feature_flags: Vec::new(),
    }
}

/// An Implementation frozen before bundles keeps the identity it was minted
/// with, and its stored JSON gains no field.
///
/// This is the whole reason `entrypoint_contract_digest` is an `Option` that is
/// skipped when absent. If the identity preimage had simply gained a field,
/// every Implementation already bound to a Coordinate in a Registry Home would
/// compute a different `implementation_id` on reload and read as corrupt.
///
/// The fixture is not written here from what the code now does. These are the
/// exact bytes of `deploy/implementations/d582a966.../manifest.json`, written
/// by the binary that froze the gate-5 Pendulum canary's source on 2026-08-20 --
/// a head that could not have been edited into agreeing with this change.
#[test]
fn an_implementation_frozen_before_bundles_still_reproduces_its_stored_identity() {
    const FROZEN_BEFORE_THIS_CHANGE: &str = r#"{"build_contract_digest":"sha256:e3e3869e902fd679be01464c246343f3878ce0eaf097f032ac31b85febc9ef82","capability_digest":"sha256:df96b41f7969813f59490f10ea0e51f6c74a8132c6fc480e55204c638b357e98","component_content_ids":["source-content:3e2b9c479c43ab4decc242b297f273f59f7e5dd0eb93bc8566c963bcf3c7b879"],"entrypoints":{"python_module":"fr_tr","rust_binary":"fern-stage-runner"},"implementation_id":"implementation:d582a9663b1a72fc6d50db4dc0fb2cebb84ca39c97b6141fe8ef8c6f33b5c645","schema":"fern.source-implementation.v2"}"#;

    let stored: SourceImplementationV2 = serde_json::from_str(FROZEN_BEFORE_THIS_CHANGE)
        .expect("a record written before the new field must still deserialize");
    assert_eq!(
        stored.entrypoint_contract_digest, None,
        "the field did not exist when this was written"
    );
    stored.validate().expect("an old record still validates");
    assert_eq!(
        stored.expected_id().unwrap(),
        "implementation:d582a9663b1a72fc6d50db4dc0fb2cebb84ca39c97b6141fe8ef8c6f33b5c645",
        "the identity preimage must be byte-identical for a record nobody edited",
    );

    // Freezing the same shape today still produces no field, so nothing that
    // rewrites an old record can silently add one.
    let content = content(vec![file("fr.py", b"x")], Vec::new());
    let today = SourceImplementationV2::from_content(&content).unwrap();
    assert_eq!(today.entrypoint_contract_digest, None);
    assert!(
        !serde_json::to_string(&today)
            .unwrap()
            .contains("entrypoint_contract_digest"),
        "an absent field must not appear, or the stored bytes change under records nobody edited",
    );
}

/// Two implementations that differ only in their bundle are two implementations.
///
/// Before this, the same capability digest covered a Python module called
/// `fr_cfg` and one called something else entirely, over different source
/// files. The identity could not tell them apart, which is the defect: a name
/// that cannot distinguish is not a name.
#[test]
fn different_bundles_give_different_capabilities_and_different_identities() {
    let content = content(vec![file("fr.py", b"x")], Vec::new());
    let first = SourceImplementationV2::from_content_and_bundle(&content, &bundle("fr_cfg", &["fr.py"])).unwrap();
    let other_module =
        SourceImplementationV2::from_content_and_bundle(&content, &bundle("fr_other", &["fr.py"])).unwrap();
    let other_files =
        SourceImplementationV2::from_content_and_bundle(&content, &bundle("fr_cfg", &["fr.py", "fr_core.py"])).unwrap();

    assert_ne!(
        first.capability_digest, other_module.capability_digest,
        "a different runtime module is a different capability",
    );
    assert_ne!(
        first.capability_digest, other_files.capability_digest,
        "a different source file list is a different capability",
    );
    assert_ne!(first.implementation_id, other_module.implementation_id);
    assert_ne!(first.implementation_id, other_files.implementation_id);

    // And the SAME bundle is the same capability, or nothing could be resumed.
    let again = SourceImplementationV2::from_content_and_bundle(&content, &bundle("fr_cfg", &["fr.py"])).unwrap();
    assert_eq!(first.implementation_id, again.implementation_id);
}

/// A bundle-derived Implementation still carries the entrypoint table, and
/// stops sharing the constant that used to stand in for its capability.
#[test]
fn a_bundle_moves_the_entrypoint_contract_out_of_the_capability_field() {
    let content = content(vec![file("fr.py", b"x")], Vec::new());
    let without = SourceImplementationV2::from_content(&content).unwrap();
    let with = SourceImplementationV2::from_content_and_bundle(&content, &bundle("fr_cfg", &["fr.py"])).unwrap();

    assert_eq!(
        with.entrypoint_contract_digest.as_deref(),
        Some(without.capability_digest.as_str()),
        "the old constant is not deleted, it moves to the field that describes it",
    );
    assert_ne!(
        with.capability_digest, without.capability_digest,
        "the capability field must stop holding the constant it was standing in for",
    );
    assert_eq!(
        with.entrypoints, without.entrypoints,
        "which entrypoints exist is unchanged; only where that fact is recorded moved",
    );
    with.validate().expect("a bundle-derived implementation validates");
    assert_eq!(with.expected_id().unwrap(), with.implementation_id);
}

// ── The runtime self-report gate ────────────────────────────────────────────

/// A real saved bundle resolution, so the digest is derived from the shape a
/// probe actually emits rather than from one written to make a test pass.
const SAVED_BUNDLE_RESOLUTION: &str = include_str!(
    "../../docs/implementation-bundles/96f8212c3205f59c748eda8983225b8e2a1612d40356bfeac0ab1a74893e6258.json"
);

fn saved_resolution() -> fern_recipes::ImplementationBundleResolutionV2 {
    serde_json::from_str(SAVED_BUNDLE_RESOLUTION).expect("the saved bundle resolution still parses")
}

#[test]
fn the_reported_capability_is_derived_by_the_same_function_that_minted_the_registered_one() {
    let resolution = saved_resolution();
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
    // Two digests over one concept is how a check starts passing while meaning
    // nothing. This asserts there is one.
    assert_eq!(
        fern_run::source_content::observed_capability_digest(&resolution).unwrap(),
        fern_run::source_content::bundle_capability_digest(&draft).unwrap()
    );
}

/// The authored draft the Fern provider is declared with: 17 files a person
/// listed, against the 93-file import closure a probe of that draft resolves.
const AUTHORED_BUNDLE_DRAFT: &str = include_str!("../../docs/implementation-bundles/fern-python-v2.draft.json");

fn source_content_fixture() -> SourceContentV2 {
    SourceContentV2::from_files(
        vec![SourceFileV2 {
            path: "fr_tr.py".to_string(),
            kind: SourceEntryKind::RegularFile,
            byte_digest: digest(b"trainer\n"),
            bytes: 8,
        }],
        Vec::new(),
        digest(b"cargo lock\n"),
        digest(b"catalog\n"),
    )
    .unwrap()
}

#[test]
fn an_implementation_minted_from_the_resolved_bundle_is_accepted_by_the_gate() {
    // The test the first version of this gate did not have, and the one that
    // fails without the fix: mint through the PUBLIC freeze path, then present
    // what a worker probing the same tree reports. Two different entry points,
    // one convention, and no test re-deriving the gate's own body.
    let directory = TempDir::new().unwrap();
    let registry = directory.path().join("registry.db");
    migrate_registry_via_go(&registry);
    let resolution = saved_resolution();
    let content = source_content_fixture();
    let implementation = SourceImplementationV2::from_content_and_resolution(&content, &resolution).unwrap();
    register_source_content(&registry, &content, "test", 1_755_000_000_000).unwrap();
    register_source_implementation(&registry, &implementation, "test", 1_755_000_000_000).unwrap();
    let implementation_id = implementation.implementation_id.clone();
    let observed = fern_run::source_content::observed_capability_digest(&resolution).unwrap();
    assert_eq!(
        fern_run::source_content::require_registered_capability(&registry, &implementation_id, &observed).unwrap(),
        Some(fern_run::source_content::CapabilityConvention::ResolvedBundle)
    );
}

#[test]
fn an_implementation_minted_from_the_authored_draft_is_refused_and_told_how_to_fix_it() {
    // The bug this pair pins: an authored draft lists 17 files and the closure
    // resolves 93, so the two digests can never agree and no rebuild reconciles
    // them. The refusal has to say that, or it reads as a broken Worker Site.
    let directory = TempDir::new().unwrap();
    let registry = directory.path().join("registry.db");
    migrate_registry_via_go(&registry);
    let draft: fern_recipes::ImplementationBundleDraftV2 = serde_json::from_str(AUTHORED_BUNDLE_DRAFT).unwrap();
    let resolution = saved_resolution();
    assert_ne!(
        draft.source_files.len(),
        resolution.source_files.len(),
        "the fixture must keep the two lists different, or this proves nothing"
    );
    let content = source_content_fixture();
    let implementation = SourceImplementationV2::from_content_and_bundle(&content, &draft).unwrap();
    register_source_content(&registry, &content, "test", 1_755_000_000_000).unwrap();
    register_source_implementation(&registry, &implementation, "test", 1_755_000_000_000).unwrap();
    let implementation_id = implementation.implementation_id.clone();
    let observed = fern_run::source_content::observed_capability_digest(&resolution).unwrap();
    let error = fern_run::source_content::require_registered_capability(&registry, &implementation_id, &observed)
        .expect_err("a draft-minted capability cannot describe the running closure");
    let message = error.to_string();
    assert!(message.contains(&implementation.capability_digest), "{message}");
    assert!(message.contains(&observed), "{message}");
    assert!(message.contains("re-freeze it from the resolved bundle"), "{message}");
}

#[test]
fn an_implementation_minted_before_capability_meant_anything_is_a_legacy_convention_match() {
    // Every Implementation a Registry Home reaches through its legacy snapshot
    // maps was minted this way. Refusing them would stop correct work, which is
    // why the gate reproduces the old convention from the record's own
    // entrypoint table instead of only knowing the current one.
    let directory = TempDir::new().unwrap();
    let registry = directory.path().join("registry.db");
    migrate_registry_via_go(&registry);
    let content = source_content_fixture();
    let implementation = SourceImplementationV2::from_content(&content).unwrap();
    register_source_content(&registry, &content, "test", 1_755_000_000_000).unwrap();
    register_source_implementation(&registry, &implementation, "test", 1_755_000_000_000).unwrap();
    let implementation_id = implementation.implementation_id.clone();
    let observed = fern_run::source_content::observed_capability_digest(&saved_resolution()).unwrap();
    assert_eq!(
        fern_run::source_content::require_registered_capability(&registry, &implementation_id, &observed).unwrap(),
        Some(fern_run::source_content::CapabilityConvention::EntrypointContract)
    );
}

#[test]
fn a_tampered_registered_digest_is_refused() {
    // Neither convention reproduces an invented digest, so it refuses -- which
    // is what stops the legacy branch from becoming a way through.
    let directory = TempDir::new().unwrap();
    let registry = directory.path().join("registry.db");
    migrate_registry_via_go(&registry);
    let content = source_content_fixture();
    let mut implementation =
        SourceImplementationV2::from_content_and_resolution(&content, &saved_resolution()).unwrap();
    implementation.capability_digest = format!("sha256:{}", "9".repeat(64));
    implementation.implementation_id = implementation.expected_id().unwrap();
    register_source_content(&registry, &content, "test", 1_755_000_000_000).unwrap();
    register_source_implementation(&registry, &implementation, "test", 1_755_000_000_000).unwrap();
    let implementation_id = implementation.implementation_id.clone();
    let observed = fern_run::source_content::observed_capability_digest(&saved_resolution()).unwrap();
    assert!(fern_run::source_content::require_registered_capability(&registry, &implementation_id, &observed).is_err());
}

#[test]
fn an_implementation_registry_home_does_not_know_is_not_a_disagreement() {
    let directory = TempDir::new().unwrap();
    let registry = directory.path().join("registry.db");
    migrate_registry_via_go(&registry);
    let observed = fern_run::source_content::observed_capability_digest(&saved_resolution()).unwrap();
    // This gate exists to catch disagreement. Silence is not disagreement, and
    // refusing here would break every Plan whose Implementation predates the
    // record without telling anyone anything true.
    assert_eq!(
        fern_run::source_content::require_registered_capability(
            &registry,
            &format!("implementation:{}", "3".repeat(64)),
            &observed,
        )
        .unwrap(),
        None
    );
}

#[test]
fn a_legacy_entrypoint_only_capability_is_classed_unverified_and_refuses_a_new_plan() {
    // The 23 Implementations a Registry Home reaches through its legacy
    // snapshot maps are all of this shape. Their capability digest is the
    // digest of their own entrypoint table: it names the module a worker will
    // import and nothing about the source behind it, so two workers built from
    // completely different trees produce the same digest. The gate accepting
    // them keeps existing Plans running; it must not let a NEW Plan be
    // registered behind a guarantee that does not exist.
    let directory = TempDir::new().unwrap();
    let registry = directory.path().join("registry.db");
    migrate_registry_via_go(&registry);
    let content = source_content_fixture();
    let implementation = SourceImplementationV2::from_content(&content).unwrap();
    register_source_content(&registry, &content, "test", 1_755_000_000_000).unwrap();
    register_source_implementation(&registry, &implementation, "test", 1_755_000_000_000).unwrap();
    let implementation_id = implementation.implementation_id.clone();

    assert!(fern_run::source_content::capability_is_legacy_entrypoint_only(&implementation).unwrap());
    let refusal = fern_run::source_content::refuse_new_plan_on_legacy_capability(&implementation)
        .unwrap()
        .expect("a capability nobody can check must refuse a new Plan");
    assert!(refusal.contains(&implementation_id), "{refusal}");
    assert!(refusal.contains("legacy-unverified"), "{refusal}");
    assert!(
        refusal.contains("fern-run freeze-source --implementation-bundle"),
        "the refusal must carry the command that fixes it: {refusal}"
    );

    // And the gate still ACCEPTS it, because an already-registered Plan pinned
    // to this Implementation keeps running -- but never as "verified".
    let observed = fern_run::source_content::observed_capability_digest(&saved_resolution()).unwrap();
    let convention = fern_run::source_content::require_registered_capability(&registry, &implementation_id, &observed)
        .unwrap()
        .expect("a legacy record is accepted, not refused, at execution");
    assert_eq!(
        convention,
        fern_run::source_content::CapabilityConvention::EntrypointContract
    );
    assert_eq!(convention.verification_class(), "legacy-unverified");
    assert!(!convention.verifies_running_code());
}

#[test]
fn a_resolution_minted_capability_verifies_running_code_and_takes_a_new_plan() {
    let directory = TempDir::new().unwrap();
    let registry = directory.path().join("registry.db");
    migrate_registry_via_go(&registry);
    let content = source_content_fixture();
    let implementation = SourceImplementationV2::from_content_and_resolution(&content, &saved_resolution()).unwrap();
    register_source_content(&registry, &content, "test", 1_755_000_000_000).unwrap();
    register_source_implementation(&registry, &implementation, "test", 1_755_000_000_000).unwrap();
    let implementation_id = implementation.implementation_id.clone();

    assert!(!fern_run::source_content::capability_is_legacy_entrypoint_only(&implementation).unwrap());
    assert_eq!(
        fern_run::source_content::refuse_new_plan_on_legacy_capability(&implementation).unwrap(),
        None
    );
    let observed = fern_run::source_content::observed_capability_digest(&saved_resolution()).unwrap();
    let convention = fern_run::source_content::require_registered_capability(&registry, &implementation_id, &observed)
        .unwrap()
        .unwrap();
    assert_eq!(convention.verification_class(), "verified");
    assert!(convention.verifies_running_code());
}

#[test]
fn published_source_and_implementation_identities_match_their_directories() {
    let deploy = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("fern_run crate sits under the repository root")
        .join("deploy");
    let mut source_ok = 0usize;
    for directory in std::fs::read_dir(deploy.join("source-contents")).unwrap() {
        let directory = directory.unwrap().path();
        if !directory.is_dir() {
            continue;
        }
        let json = std::fs::read_to_string(directory.join("manifest.json")).unwrap();
        let content: SourceContentV2 = serde_json::from_str(&json).unwrap();
        let expected = format!("source-content:{}", directory.file_name().unwrap().to_string_lossy());
        assert_eq!(content.expected_id().unwrap(), expected);
        assert_eq!(content.source_content_id, expected);
        source_ok += 1;
    }
    // CHG 2026-08-28: the exact count (32 that day) was a census of the deploy
    // shelf, not a promise about the code, and it failed the first time a new
    // source content was published. The promise is that EVERY directory
    // reproduces its id, and that the shelf is not empty.
    assert!(source_ok > 0, "no published source-content directory was read");

    let mut implementation_ok = 0usize;
    let mut absent_contract = 0usize;
    for directory in std::fs::read_dir(deploy.join("implementations")).unwrap() {
        let directory = directory.unwrap().path();
        if !directory.is_dir() {
            continue;
        }
        let json = std::fs::read_to_string(directory.join("manifest.json")).unwrap();
        let implementation: SourceImplementationV2 = serde_json::from_str(&json).unwrap();
        if implementation.entrypoint_contract_digest.is_none() {
            absent_contract += 1;
        }
        let expected = format!("implementation:{}", directory.file_name().unwrap().to_string_lossy());
        assert_eq!(implementation.expected_id().unwrap(), expected);
        assert_eq!(implementation.implementation_id, expected);
        implementation_ok += 1;
    }
    assert!(implementation_ok > 0, "no published implementation directory was read");
    // The two legacy implementations were frozen before the entrypoint
    // contract digest existed; a LARGER count means a new implementation was
    // frozen without one, which is the defect this guards. Pruning the shelf
    // may lower it.
    assert!(
        absent_contract <= 2,
        "an implementation was published without its entrypoint contract digest ({absent_contract} absent)"
    );
}
