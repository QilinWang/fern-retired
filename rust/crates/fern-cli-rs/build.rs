use std::env;
use std::path::{Path, PathBuf};
use std::process::Command;

const BUILD_COMMIT_ENV: &str = "FERN_BUILD_COMMIT";
const BUILD_DIRTY_ENV: &str = "FERN_BUILD_DIRTY";
const SOURCE_ARCHIVE_SHA256_ENV: &str = "FERN_SOURCE_ARCHIVE_SHA256";

fn main() {
    for variable in [BUILD_COMMIT_ENV, BUILD_DIRTY_ENV, SOURCE_ARCHIVE_SHA256_ENV] {
        println!("cargo:rerun-if-env-changed={variable}");
    }

    let manifest_dir = PathBuf::from(env::var_os("CARGO_MANIFEST_DIR").expect("Cargo sets CARGO_MANIFEST_DIR"));
    let repository_root = manifest_dir
        .parent()
        .expect("fern-cli-rs and fern-worker live directly under the Fern repository root");
    let commit_override = env::var(BUILD_COMMIT_ENV).ok();
    let dirty_override = env::var(BUILD_DIRTY_ENV).ok();
    if commit_override.is_none() || dirty_override.is_none() {
        watch_git_identity(repository_root);
    }

    let commit = commit_override.unwrap_or_else(|| git_text(repository_root, &["rev-parse", "HEAD"]));
    require_lower_hex(BUILD_COMMIT_ENV, &commit, 40);

    let dirty = match dirty_override {
        Some(value) => parse_bool(BUILD_DIRTY_ENV, &value),
        None => git_dirty(repository_root),
    };
    let source_archive_sha256 = env::var(SOURCE_ARCHIVE_SHA256_ENV).ok();
    if let Some(digest) = &source_archive_sha256 {
        require_lower_hex(SOURCE_ARCHIVE_SHA256_ENV, digest, 64);
    }

    println!("cargo:rustc-env=FERN_BUILT_FROM_COMMIT={commit}");
    println!("cargo:rustc-env=FERN_BUILT_FROM_DIRTY_TREE={dirty}");
    if let Some(digest) = source_archive_sha256 {
        println!("cargo:rustc-env=FERN_BUILT_FROM_SOURCE_ARCHIVE_SHA256={digest}");
    }

    // The same launch source is compiled by two packages. Role cfg is the
    // wall: operator commands do not type-check into the worker, and the
    // execute-matrix training path does not type-check into fern-run.
    println!("cargo:rustc-check-cfg=cfg(fern_role_operator)");
    println!("cargo:rustc-check-cfg=cfg(fern_role_matrix_worker)");
    println!("cargo:rustc-check-cfg=cfg(fern_role_scheduler)");
    let package = env::var("CARGO_PKG_NAME").expect("Cargo sets CARGO_PKG_NAME");
    match package.as_str() {
        "fern-cli-rs" => println!("cargo:rustc-cfg=fern_role_operator"),
        "fern-worker" => {
            println!("cargo:rustc-cfg=fern_role_matrix_worker");
            // The immutable worker also hosts the private one-Plan
            // supervisor.  It still exposes no scheduler election or human
            // command; the extra role compiles the already-proven Plan
            // interpreter so the resident loop can hand authority to a child
            // instead of interpreting the Plan itself.
            println!("cargo:rustc-cfg=fern_role_scheduler");
        }
        other => panic!("fern-cli-rs/build.rs is only for fern-cli-rs and fern-worker; got {other}"),
    }
}

/// Rebuild the embedded identity when a commit or staged tree changes.
///
/// Ordinary Rust source changes are already Cargo inputs. Watching Git's HEAD,
/// current branch ref, and index covers a commit or staging change without
/// recursively statting the repository's large result and paper trees on every
/// fast incremental build.
fn watch_git_identity(repository_root: &Path) {
    let git_directory = PathBuf::from(git_text(repository_root, &["rev-parse", "--absolute-git-dir"]));
    let head = git_directory.join("HEAD");
    println!("cargo:rerun-if-changed={}", head.display());
    println!("cargo:rerun-if-changed={}", git_directory.join("index").display());
    if let Ok(contents) = std::fs::read_to_string(&head)
        && let Some(reference) = contents.trim().strip_prefix("ref: ")
    {
        println!("cargo:rerun-if-changed={}", git_directory.join(reference).display());
    }
}

fn git_text(repository_root: &Path, arguments: &[&str]) -> String {
    let output = Command::new("git")
        .arg("-C")
        .arg(repository_root)
        .args(arguments)
        .output()
        .unwrap_or_else(|error| panic!("cannot execute git for fern-run build identity: {error}"));
    if !output.status.success() {
        panic!(
            "git {} failed while building fern-run identity: {}",
            arguments.join(" "),
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }
    String::from_utf8(output.stdout)
        .unwrap_or_else(|error| panic!("git build identity output is not UTF-8: {error}"))
        .trim()
        .to_string()
}

fn git_dirty(repository_root: &Path) -> bool {
    let output = Command::new("git")
        .arg("-C")
        .arg(repository_root)
        .args(["status", "--porcelain=v1", "--untracked-files=normal"])
        .output()
        .unwrap_or_else(|error| panic!("cannot inspect the fern-run build tree: {error}"));
    if !output.status.success() {
        panic!(
            "git status failed while building fern-run identity: {}",
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }
    !output.stdout.is_empty()
}

fn parse_bool(variable: &str, value: &str) -> bool {
    match value {
        "true" | "1" => true,
        "false" | "0" => false,
        _ => panic!("{variable} must be true, false, 1, or 0; got {value:?}"),
    }
}

fn require_lower_hex(variable: &str, value: &str, length: usize) {
    assert!(
        value.len() == length
            && value
                .bytes()
                .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte)),
        "{variable} must be exactly {length} lowercase hexadecimal characters; got {value:?}"
    );
}
