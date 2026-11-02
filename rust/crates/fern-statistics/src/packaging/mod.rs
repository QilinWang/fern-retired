//! Pack a frozen manifest into the archive a reviewer receives.
//!
//! > **What exactly is in the bundle, and is every promised file really there?**
//!
//! A MODULE, not a crate. This was briefly `crates/fern-release`, created for
//! the only reason that never justifies one: the noun is different. It has one
//! consumer, no dependency to isolate, no authority boundary, and an API that
//! is still changing as four shell scripts come in. Crates are houses and cost
//! a wall; modules are drawers. It moves out when several callers need it and
//! its shape has stopped moving.
//!
//! It sits under `fern-statistics` because that crate's product is the
//! post-experiment research surface — turn a finished experiment into numbers,
//! figures and deliverables you can check — and a submission bundle is one of
//! the deliverables. The crate name is a responsibility boundary, not a
//! university department.
//!
//! WHY IT LEFT SHELL. Four scripts did one job with different file lists, and
//! every one of them opened with `rm -rf "$OUT"` before rebuilding. Under
//! `set -e` a missing source then aborts halfway and leaves the bundle DELETED
//! — the failure arrives as an empty directory where a submission used to be.
//! Two more shell-specific defects are in the correction log from these files:
//! `mktemp` created an empty file that `zip` read as a corrupt archive (#121),
//! and `cp ... || true` shipped an empty code directory against a promise the
//! reproducibility checklist makes to reviewers, which is why `copy_and_require`
//! had to be invented.
//!
//! So a bundle is built in a temporary directory and moved into place only
//! once it is complete and its archive has been read back. A failed build
//! leaves the previous bundle untouched.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use sha2::{Digest, Sha256};

pub mod bundles;

/// One file the bundle promises, and where it comes from.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Copy {
    /// Path inside the bundle.
    pub target: &'static str,
    /// Path in the repository.
    pub source: &'static str,
}

/// A whole source tree copied in, filtered by extension.
#[derive(Debug, Clone)]
pub struct SourceTree {
    pub source: &'static str,
    /// Where it lands under the bundle root.
    pub target: &'static str,
    pub extensions: &'static [&'static str],
    /// Names removed after copying. Packaging metadata names the author and
    /// the repository, so it never ships.
    pub remove_named: &'static [&'static str],
    /// Generated/cache directories that are not part of the promised source.
    pub skip_directories: &'static [&'static str],
}

/// Files selected from one directory and copied flat into the bundle.
///
/// This is the typed replacement for shell globs such as `paper_*.py`: the
/// selector is visible in the manifest, and `required` makes a promise fail
/// before the previous archive is touched when the selector matches nothing.
#[derive(Debug, Clone)]
pub struct FlatFiles {
    pub source: &'static str,
    pub target: &'static str,
    pub prefixes: &'static [&'static str],
    pub suffixes: &'static [&'static str],
    pub exact_names: &'static [&'static str],
    pub exclude_names: &'static [&'static str],
    pub exclude_prefixes: &'static [&'static str],
    pub exclude_contains: &'static [&'static str],
    pub required: bool,
}

/// A text substitution applied to one file INSIDE the bundle.
///
/// A `\input{../results/...}` resolves in the repository and not in the
/// archive, where everything sits alongside. Rewriting the copy is what makes
/// the bundle compile for whoever receives it.
#[derive(Debug, Clone)]
pub struct Rewrite {
    pub file: &'static str,
    pub from: &'static str,
    pub to: &'static str,
}

/// Apply one anonymity/path rewrite throughout a selected subtree.
#[derive(Debug, Clone)]
pub struct TreeRewrite {
    pub target: &'static str,
    pub extensions: &'static [&'static str],
    pub from: &'static str,
    pub to: &'static str,
}

/// The submission page rule checked against the PDF that will actually ship.
#[derive(Debug, Clone)]
pub struct PdfPolicy {
    pub file: &'static str,
    pub max_pages: usize,
    pub max_references_page: usize,
}

/// Everything one bundle is.
#[derive(Debug, Clone)]
pub struct Bundle {
    pub name: &'static str,
    /// One line saying what this upload is for and who reads it.
    pub purpose: &'static str,
    pub out: &'static str,
    /// Where the archive lands, relative to the repository root.
    pub archive: &'static str,
    pub copies: &'static [Copy],
    pub trees: &'static [SourceTree],
    pub flat_files: &'static [FlatFiles],
    pub rewrites: &'static [Rewrite],
    pub tree_rewrites: &'static [TreeRewrite],
    pub pdf_policy: Option<PdfPolicy>,
    pub max_archive_bytes: Option<u64>,
}

/// What a build produced, or the first promise it could not keep.
#[derive(Debug)]
pub struct BuildReport {
    pub lines: Vec<String>,
    pub files: usize,
    pub archive_bytes: u64,
}

fn sha256_of(path: &Path) -> std::io::Result<String> {
    let bytes = std::fs::read(path)?;
    let mut hasher = Sha256::new();
    hasher.update(&bytes);
    Ok(format!("{:x}", hasher.finalize()))
}

fn copy_into(root: &Path, staging: &Path, copy: &Copy) -> Result<String, String> {
    let source = root.join(copy.source);
    if !source.is_file() {
        // The shell printed `MISSING <source>` and exited; the difference is
        // that nothing has been destroyed by the time this returns.
        return Err(format!("MISSING {}", copy.source));
    }
    let target = staging.join(copy.target);
    if let Some(parent) = target.parent() {
        std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    std::fs::copy(&source, &target).map_err(|error| format!("{}: {error}", copy.target))?;
    Ok(format!("  {:<46} <- {}", copy.target, copy.source))
}

fn copy_tree(root: &Path, staging: &Path, tree: &SourceTree) -> Result<usize, String> {
    let source_root = root.join(tree.source);
    if !source_root.is_dir() {
        return Err(format!("MISSING code tree {}", tree.source));
    }
    let destination = staging.join(tree.target);
    let mut copied = 0;
    let mut queue = vec![source_root.clone()];
    while let Some(directory) = queue.pop() {
        let entries = std::fs::read_dir(&directory).map_err(|error| error.to_string())?;
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_dir() {
                let name = path.file_name().and_then(|name| name.to_str()).unwrap_or_default();
                if tree.skip_directories.contains(&name) {
                    continue;
                }
                queue.push(path);
                continue;
            }
            let extension = path.extension().and_then(|e| e.to_str()).unwrap_or_default();
            if !tree.extensions.is_empty() && !tree.extensions.contains(&extension) {
                continue;
            }
            let name = path.file_name().and_then(|n| n.to_str()).unwrap_or_default();
            if tree.remove_named.contains(&name) {
                continue;
            }
            let relative = path.strip_prefix(&source_root).map_err(|error| error.to_string())?;
            let target = destination.join(relative);
            if let Some(parent) = target.parent() {
                std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
            }
            std::fs::copy(&path, &target).map_err(|error| error.to_string())?;
            copied += 1;
        }
    }
    if copied == 0 {
        // `cp ... || true` shipped an empty directory against a promise the
        // reproducibility checklist makes. A promise is the one thing that
        // must not fail quietly.
        return Err(format!(
            "nothing matched for {}; the checklist promises this code is in the bundle",
            tree.target
        ));
    }
    Ok(copied)
}

fn copy_flat_files(root: &Path, staging: &Path, group: &FlatFiles) -> Result<usize, String> {
    let source = root.join(group.source);
    if !source.is_dir() {
        return Err(format!("MISSING source directory {}", group.source));
    }
    let target = staging.join(group.target);
    std::fs::create_dir_all(&target).map_err(|error| error.to_string())?;
    let mut copied = 0;
    for entry in std::fs::read_dir(&source).map_err(|error| error.to_string())? {
        let entry = entry.map_err(|error| error.to_string())?;
        let path = entry.path();
        if !path.is_file() {
            continue;
        }
        let name = entry.file_name();
        let name = name.to_string_lossy();
        let prefix_match = group.prefixes.iter().any(|prefix| name.starts_with(prefix));
        let suffix_match = group.suffixes.iter().any(|suffix| name.ends_with(suffix));
        let exact_match = group.exact_names.contains(&name.as_ref());
        if group.exclude_names.contains(&name.as_ref())
            || group.exclude_prefixes.iter().any(|prefix| name.starts_with(prefix))
            || group.exclude_contains.iter().any(|part| name.contains(part))
        {
            continue;
        }
        let selected =
            exact_match || ((group.prefixes.is_empty() || prefix_match) && (group.suffixes.is_empty() || suffix_match));
        if !selected {
            continue;
        }
        std::fs::copy(&path, target.join(name.as_ref())).map_err(|error| error.to_string())?;
        copied += 1;
    }
    if group.required && copied == 0 {
        return Err(format!(
            "nothing matched for {}; this bundle promises those files",
            group.target
        ));
    }
    Ok(copied)
}

fn apply_rewrite(staging: &Path, rewrite: &Rewrite) -> Result<(), String> {
    let path = staging.join(rewrite.file);
    let text = std::fs::read_to_string(&path).map_err(|error| format!("{}: {error}", rewrite.file))?;
    std::fs::write(&path, text.replace(rewrite.from, rewrite.to)).map_err(|error| format!("{}: {error}", rewrite.file))
}

fn apply_tree_rewrite(staging: &Path, rewrite: &TreeRewrite) -> Result<usize, String> {
    let root = staging.join(rewrite.target);
    let mut rewritten = 0;
    let mut queue = vec![root];
    while let Some(directory) = queue.pop() {
        for entry in std::fs::read_dir(&directory).map_err(|error| error.to_string())? {
            let path = entry.map_err(|error| error.to_string())?.path();
            if path.is_dir() {
                queue.push(path);
                continue;
            }
            let extension = path.extension().and_then(|e| e.to_str()).unwrap_or_default();
            if !rewrite.extensions.is_empty() && !rewrite.extensions.contains(&extension) {
                continue;
            }
            let text = std::fs::read_to_string(&path).map_err(|error| format!("{}: {error}", path.display()))?;
            let updated = text.replace(rewrite.from, rewrite.to);
            if updated != text {
                std::fs::write(&path, updated).map_err(|error| format!("{}: {error}", path.display()))?;
                rewritten += 1;
            }
        }
    }
    Ok(rewritten)
}

fn command_text(program: &str, args: &[&str], cwd: &Path) -> Result<String, String> {
    let output = std::process::Command::new(program)
        .args(args)
        .current_dir(cwd)
        .output()
        .map_err(|error| format!("cannot run {program}: {error}"))?;
    if !output.status.success() {
        return Err(format!(
            "{program} failed: {}",
            String::from_utf8_lossy(&output.stderr).trim()
        ));
    }
    Ok(String::from_utf8_lossy(&output.stdout).into_owned())
}

fn check_pdf_policy(staging: &Path, policy: &PdfPolicy) -> Result<String, String> {
    let path = staging.join(policy.file);
    let path = path
        .canonicalize()
        .map_err(|error| format!("{}: {error}", policy.file))?;
    let path_text = path.to_string_lossy();
    let info = command_text("pdfinfo", &[&path_text], staging)?;
    let pages = info
        .lines()
        .find_map(|line| {
            line.strip_prefix("Pages:")
                .and_then(|value| value.trim().parse::<usize>().ok())
        })
        .ok_or_else(|| format!("could not read page count from {}", policy.file))?;
    if pages > policy.max_pages {
        return Err(format!(
            "{} has {pages} pages, over the {}-page maximum",
            policy.file, policy.max_pages
        ));
    }
    let mut references_page = None;
    for page in 1..=pages {
        let page_text = page.to_string();
        let text = command_text(
            "pdftotext",
            &["-f", &page_text, "-l", &page_text, &path_text, "-"],
            staging,
        )?;
        if text.lines().any(|line| line.trim() == "References") {
            references_page = Some(page);
            if page == policy.max_references_page {
                let first = text.lines().find(|line| !line.trim().is_empty()).unwrap_or_default();
                if first.trim() != "References" {
                    return Err(format!(
                        "page {page} of {} carries body text above References: {first}",
                        policy.file
                    ));
                }
            }
            break;
        }
    }
    let references_page =
        references_page.ok_or_else(|| format!("{} has no readable References heading", policy.file))?;
    if references_page > policy.max_references_page {
        return Err(format!(
            "body text in {} runs too late; References starts on page {references_page}",
            policy.file
        ));
    }
    Ok(format!(
        "  {:<28} {pages} pages; References from page {references_page}",
        policy.file
    ))
}

/// Every file under `directory`, relative and sorted, excluding the checksum
/// file itself.
pub fn bundle_files(directory: &Path) -> Vec<PathBuf> {
    let mut found = Vec::new();
    let mut queue = vec![directory.to_path_buf()];
    while let Some(current) = queue.pop() {
        let Ok(entries) = std::fs::read_dir(&current) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_dir() {
                queue.push(path);
            } else if path.file_name().and_then(|n| n.to_str()) != Some("SHA256SUMS.txt") {
                if let Ok(relative) = path.strip_prefix(directory) {
                    found.push(relative.to_path_buf());
                }
            }
        }
    }
    found.sort();
    found
}

/// `sha256sum` output for the whole bundle, in the same format and order.
pub fn checksum_manifest(directory: &Path) -> Result<String, String> {
    let mut text = String::new();
    for relative in bundle_files(directory) {
        let digest = sha256_of(&directory.join(&relative)).map_err(|error| error.to_string())?;
        text.push_str(&format!("{digest}  {}\n", relative.display()));
    }
    Ok(text)
}

/// Build one bundle into a staging directory and move it into place.
///
/// Nothing under `out` is touched until the whole bundle exists and its archive
/// has been read back. That is the difference from the shell version, where
/// `rm -rf "$OUT"` ran first and a missing source left a deleted submission.
pub fn build(bundle: &Bundle, root: &Path) -> Result<BuildReport, String> {
    let staging = root.join(format!("{}.staging", bundle.out));
    let _ = std::fs::remove_dir_all(&staging);
    std::fs::create_dir_all(&staging).map_err(|error| error.to_string())?;

    let mut lines = Vec::new();
    // Broad trees go first; explicit copies then replace any tracked file with
    // its declared source of truth (the aaai-upload handoff uses this).
    for tree in bundle.trees {
        let copied = copy_tree(root, &staging, tree)?;
        lines.push(format!("  {:<28} {copied} files", tree.target));
    }
    for group in bundle.flat_files {
        let copied = copy_flat_files(root, &staging, group)?;
        lines.push(format!("  {:<28} {copied} files", group.target));
    }
    for copy in bundle.copies {
        lines.push(copy_into(root, &staging, copy)?);
    }
    for rewrite in bundle.rewrites {
        apply_rewrite(&staging, rewrite)?;
    }
    for rewrite in bundle.tree_rewrites {
        let rewritten = apply_tree_rewrite(&staging, rewrite)?;
        lines.push(format!("  {:<28} {rewritten} rewritten", rewrite.target));
    }
    if let Some(policy) = &bundle.pdf_policy {
        lines.push(check_pdf_policy(&staging, policy)?);
    }

    let manifest = checksum_manifest(&staging)?;
    std::fs::write(staging.join("SHA256SUMS.txt"), &manifest).map_err(|error| error.to_string())?;
    let files = bundle_files(&staging).len() + 1;

    // The archive is written to a path that did not exist, read back, and only
    // then moved: `zip` treats an existing empty file as a corrupt archive.
    // ABSOLUTE, because zip runs with the staging directory as its cwd and a
    // relative archive path would resolve inside the bundle being packed.
    let archive_staging = root
        .canonicalize()
        .map_err(|error| error.to_string())?
        .join(format!(".fern-pack-{}.staging.zip", bundle.name));
    if let Some(parent) = archive_staging.parent() {
        std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    let _ = std::fs::remove_file(&archive_staging);
    let zip = std::process::Command::new("zip")
        .arg("-q")
        .arg("-r")
        .arg(&archive_staging)
        .arg(".")
        .current_dir(&staging)
        .status()
        .map_err(|error| format!("cannot run zip: {error}"))?;
    if !zip.success() {
        return Err(format!("zip failed for {}", bundle.name));
    }
    let verify = std::process::Command::new("unzip")
        .arg("-tq")
        .arg(&archive_staging)
        .status()
        .map_err(|error| format!("cannot run unzip: {error}"))?;
    if !verify.success() {
        return Err(format!("the archive for {} did not read back", bundle.name));
    }
    let archive_bytes = std::fs::metadata(&archive_staging)
        .map(|metadata| metadata.len())
        .unwrap_or(0);
    if let Some(maximum) = bundle.max_archive_bytes
        && archive_bytes >= maximum
    {
        return Err(format!(
            "{} archive is {:.1} MB, over the {:.1} MB limit",
            bundle.name,
            archive_bytes as f64 / 1_000_000.0,
            maximum as f64 / 1_000_000.0
        ));
    }

    let final_out = root.join(bundle.out);
    let backup = root.join(format!("{}.previous", bundle.out));
    let _ = std::fs::remove_dir_all(&backup);
    if final_out.exists() {
        std::fs::rename(&final_out, &backup).map_err(|error| error.to_string())?;
    }
    if let Err(error) = std::fs::rename(&staging, &final_out) {
        if backup.exists() {
            let _ = std::fs::rename(&backup, &final_out);
        }
        return Err(error.to_string());
    }
    let _ = std::fs::remove_dir_all(&backup);
    let archive_final = root
        .canonicalize()
        .map_err(|error| error.to_string())?
        .join(bundle.archive);
    if let Some(parent) = archive_final.parent() {
        std::fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    std::fs::rename(&archive_staging, archive_final).map_err(|error| error.to_string())?;

    Ok(BuildReport {
        lines,
        files,
        archive_bytes,
    })
}

/// What a built bundle contains, for comparing one build against another.
pub fn manifest_of(directory: &Path) -> Result<BTreeMap<String, String>, String> {
    bundle_files(directory)
        .into_iter()
        .map(|relative| {
            let digest = sha256_of(&directory.join(&relative)).map_err(|error| error.to_string())?;
            Ok((relative.display().to_string(), digest))
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture(name: &str) -> PathBuf {
        let root = std::env::temp_dir().join(format!("fern-release-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(&root).unwrap();
        root
    }

    fn write(root: &Path, name: &str, text: &str) {
        let path = root.join(name);
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(path, text).unwrap();
    }

    const TINY: Bundle = Bundle {
        name: "tiny",
        purpose: "a test bundle",
        out: "out/tiny",
        archive: "out/tiny.zip",
        copies: &[
            Copy {
                target: "doc.pdf",
                source: "paper/doc.pdf",
            },
            Copy {
                target: "source/a.tex",
                source: "paper/a.tex",
            },
        ],
        trees: &[],
        flat_files: &[],
        rewrites: &[Rewrite {
            file: "source/a.tex",
            from: "{../results/table}",
            to: "{table}",
        }],
        tree_rewrites: &[],
        pdf_policy: None,
        max_archive_bytes: None,
    };

    #[test]
    fn a_bundle_carries_every_promised_file_and_a_checksum_of_each() {
        let root = fixture("happy");
        write(&root, "paper/doc.pdf", "pdf bytes");
        write(&root, "paper/a.tex", "\\input{../results/table}\n");
        let report = build(&TINY, &root).unwrap();
        assert_eq!(report.files, 3, "two copies plus SHA256SUMS.txt");
        assert!(root.join("out/tiny/doc.pdf").is_file());
        // The rewrite makes the bundle compile where everything sits alongside.
        assert_eq!(
            std::fs::read_to_string(root.join("out/tiny/source/a.tex")).unwrap(),
            "\\input{table}\n"
        );
        let sums = std::fs::read_to_string(root.join("out/tiny/SHA256SUMS.txt")).unwrap();
        assert_eq!(sums.lines().count(), 2);
        assert!(root.join("out/tiny.zip").is_file());
        std::fs::remove_dir_all(&root).unwrap();
    }

    /// The reason this left shell. `rm -rf "$OUT"` ran BEFORE the copies, so a
    /// missing source under `set -e` left the previous submission deleted.
    #[test]
    fn a_missing_source_leaves_the_previous_bundle_untouched() {
        let root = fixture("missing-source");
        write(&root, "paper/doc.pdf", "pdf bytes");
        write(&root, "paper/a.tex", "x");
        build(&TINY, &root).unwrap();
        let before = manifest_of(&root.join("out/tiny")).unwrap();

        // Now a source disappears and the build is attempted again.
        std::fs::remove_file(root.join("paper/a.tex")).unwrap();
        let error = build(&TINY, &root).unwrap_err();
        assert_eq!(error, "MISSING paper/a.tex");
        assert_eq!(
            manifest_of(&root.join("out/tiny")).unwrap(),
            before,
            "a failed build must not touch the bundle that was already there"
        );
        std::fs::remove_dir_all(&root).unwrap();
    }

    /// A promise that fails quietly is the one that reaches a reviewer.
    #[test]
    fn a_source_tree_that_matches_nothing_fails_rather_than_shipping_empty() {
        let root = fixture("empty-tree");
        write(&root, "paper/doc.pdf", "x");
        write(&root, "paper/a.tex", "x");
        write(&root, "code/readme.md", "not a python file");
        const WITH_TREE: Bundle = Bundle {
            trees: &[SourceTree {
                source: "code",
                target: "code",
                extensions: &["py"],
                remove_named: &[],
                skip_directories: &[],
            }],
            ..TINY
        };
        let error = build(&WITH_TREE, &root).unwrap_err();
        assert!(error.contains("nothing matched for code"), "{error}");
        std::fs::remove_dir_all(&root).unwrap();
    }

    /// Packaging metadata names the author, so it never ships.
    #[test]
    fn named_files_are_excluded_from_a_copied_tree() {
        let root = fixture("exclusions");
        write(&root, "paper/doc.pdf", "x");
        write(&root, "paper/a.tex", "x");
        write(&root, "code/keep.py", "code");
        write(&root, "code/pyproject.toml", "[project]\nauthors = ...");
        const WITH_TREE: Bundle = Bundle {
            trees: &[SourceTree {
                source: "code",
                target: "code",
                extensions: &["py", "toml"],
                remove_named: &["pyproject.toml"],
                skip_directories: &[],
            }],
            ..TINY
        };
        build(&WITH_TREE, &root).unwrap();
        assert!(root.join("out/tiny/code/keep.py").is_file());
        assert!(!root.join("out/tiny/code/pyproject.toml").exists());
        std::fs::remove_dir_all(&root).unwrap();
    }
}
