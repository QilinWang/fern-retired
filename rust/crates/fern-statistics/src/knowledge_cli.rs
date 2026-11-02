//! Read TOML Cards through `fern-card` and remaining historical checks through
//! the installed `fern-knowledge` binary.
//!
//! This crate used to link `fern-knowledge` as a library, which meant that one
//! agent's half-edited Card provider stopped `fern-statistics` — and therefore
//! `fern-run` — from compiling at all. Card meaning is a separate authority
//! with a separate release cycle; a repo-health check that reads it is a
//! reader of that authority's output, not a member of its build.
//!
//! So the checks here run the same commands a human runs and parse the JSON
//! receipts they already print:
//!
//! - `fern-knowledge check` prints the repository inventory, one entry per
//!   discovered Markdown source with its classification and diagnostics. It
//!   exits nonzero when the repository has invalid records, and it prints the
//!   inventory first either way, so the exit code is not the answer here — the
//!   entries are, and each caller judges only the entries it owns.
//! - `fern-card check` prints every TOML Card with its kind, canonical path,
//!   and typed links. It is the Card reader used by Program and lab checks.
//!
//! Only the fields these checks read are declared. The receipts carry more,
//! and a new field in them must not break a reader that does not want it.

use std::path::{Path, PathBuf};
use std::process::Command;

use serde::Deserialize;

/// One discovered Markdown source in the repository inventory.
#[derive(Debug, Clone, Deserialize)]
pub struct InventoryEntry {
    pub canonical_path: String,
    /// `typed`, `legacy`, `invalid`, or `broken-link`.
    pub classification: String,
    #[serde(default)]
    pub diagnostics: Vec<String>,
}

impl InventoryEntry {
    pub fn is_refused(&self) -> bool {
        self.classification == "invalid" || self.classification == "broken-link"
    }
}

#[derive(Debug, Clone, Deserialize)]
pub struct RepositoryInventory {
    #[serde(default)]
    pub entries: Vec<InventoryEntry>,
}

/// One typed link on a Card, with the outcome of checking its target.
#[derive(Debug, Clone, Deserialize)]
pub struct IndexedLink {
    /// Kebab-case relation, for example `supported-by` or `uses-evidence`.
    pub relation: String,
    pub target: String,
    /// `valid`, `invalid`, `unresolved`, or `not_consulted`.
    pub outcome: String,
    #[serde(default)]
    pub detail: String,
}

#[derive(Debug, Clone, Deserialize)]
pub struct IndexedCard {
    /// Kebab-case Card kind, for example `program` or `research-design`.
    pub kind: String,
    pub canonical_path: String,
    #[serde(default)]
    pub links: Vec<IndexedLink>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct CardIndex {
    #[serde(default)]
    pub cards: Vec<IndexedCard>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct CardRepositoryCheck {
    #[serde(default)]
    pub entries: Vec<InventoryEntry>,
    #[serde(default)]
    pub card_index: Vec<IndexedCard>,
}

impl CardIndex {
    pub fn card_at(&self, canonical_path: &str) -> Option<&IndexedCard> {
        self.cards.iter().find(|card| card.canonical_path == canonical_path)
    }
}

impl IndexedCard {
    /// Targets of every link with this relation, in declaration order.
    pub fn targets(&self, relation: &str) -> Vec<&str> {
        self.links
            .iter()
            .filter(|link| link.relation == relation)
            .map(|link| link.target.as_str())
            .collect()
    }
}

/// Where the Card authority's command lives, and when it was built.
#[derive(Debug, Clone)]
pub struct ChosenBinary {
    pub path: PathBuf,
    /// Modification time, formatted for a human. `unknown` when the platform
    /// or the filesystem will not say.
    pub built_at: String,
}

impl std::fmt::Display for ChosenBinary {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "{} (built {})", self.path.display(), self.built_at)
    }
}

/// Choose the `fern-knowledge` this check will ask.
///
/// Named, never guessed: a path compiled in at build time is a path that is
/// wrong on every other machine (correction 281). The order is deliberate. An
/// explicit `$FERN_KNOWLEDGE_BIN` wins, then an installed `fern-knowledge` on
/// `PATH`, then whichever of this workspace's two build profiles is NEWER.
///
/// Newer, not release-first. A binary verified earlier is not verified now: a
/// stale `target/release` build refused authoring that the current
/// `target/debug` build accepts, and preferring release by rank made the older
/// answer the authoritative one. Build time is the only thing that separates
/// them, so build time decides, and the caller is told which it got.
pub fn knowledge_binary(root: &Path) -> Result<ChosenBinary, String> {
    if let Ok(declared) = std::env::var("FERN_KNOWLEDGE_BIN") {
        let path = PathBuf::from(&declared);
        if path.is_file() {
            return Ok(describe(path));
        }
        return Err(format!("FERN_KNOWLEDGE_BIN is {declared:?}, which is not a file"));
    }
    for candidate in installed_on_path("fern-knowledge") {
        if candidate.is_file() {
            return Ok(describe(candidate));
        }
    }
    let built = [
        root.join("target/release/fern-knowledge"),
        root.join("target/debug/fern-knowledge"),
    ];
    if let Some(newest) = newest_existing(&built, modified_at) {
        return Ok(describe(newest));
    }
    Err(format!(
        "cannot find fern-knowledge. Looked at $FERN_KNOWLEDGE_BIN, every PATH entry, {}. Build it, or set FERN_KNOWLEDGE_BIN.",
        built
            .iter()
            .map(|path| path.display().to_string())
            .collect::<Vec<_>>()
            .join(", ")
    ))
}

/// Choose the installed `fern-card` authority used for TOML Card reads.
pub fn card_binary() -> Result<PathBuf, String> {
    if let Ok(declared) = std::env::var("FERN_CARD_BIN") {
        let path = PathBuf::from(&declared);
        if path.is_file() {
            return Ok(path);
        }
        return Err(format!("FERN_CARD_BIN is {declared:?}, which is not a file"));
    }
    for candidate in installed_on_path("fern-card") {
        if candidate.is_file() {
            return Ok(candidate);
        }
    }
    Err("cannot find fern-card. Looked at $FERN_CARD_BIN and every PATH entry. Build and install tools/cardbox-go/cmd/fern-card, or set FERN_CARD_BIN.".to_string())
}

/// The most recently built of the candidates that exist.
///
/// A candidate whose modification time cannot be read loses to one whose can:
/// an unreadable time is not evidence of being newer.
///
/// The clock reader is a parameter so a test can state two build times
/// directly. Writing real files a second apart would test the filesystem's
/// timestamp granularity, not this rule.
fn newest_existing(
    candidates: &[PathBuf],
    modified: impl Fn(&Path) -> Option<std::time::SystemTime>,
) -> Option<PathBuf> {
    candidates
        .iter()
        .filter(|path| path.is_file())
        .filter_map(|path| modified(path).map(|time| (time, path.clone())))
        .max_by_key(|(time, _)| *time)
        .map(|(_, path)| path)
}

fn modified_at(path: &Path) -> Option<std::time::SystemTime> {
    std::fs::metadata(path).ok()?.modified().ok()
}

fn describe(path: PathBuf) -> ChosenBinary {
    let built_at = modified_at(&path)
        .and_then(|time| time.duration_since(std::time::UNIX_EPOCH).ok())
        .map(|since_epoch| format!("epoch+{}s", since_epoch.as_secs()))
        .unwrap_or_else(|| "unknown".to_string());
    ChosenBinary { path, built_at }
}

/// Every `PATH` entry that could hold this command, in `PATH` order.
fn installed_on_path(command: &str) -> Vec<PathBuf> {
    let Some(path) = std::env::var_os("PATH") else {
        return Vec::new();
    };
    std::env::split_paths(&path)
        .map(|directory| directory.join(command))
        .collect()
}

/// Run one read-only `fern-knowledge` subcommand and parse its JSON receipt.
///
/// The exit code is deliberately not consulted. `check` refuses the repository
/// when anything in it is invalid, and still prints the complete inventory; a
/// caller that owns one folder must be able to read its own entries without
/// inheriting a verdict about somebody else's. A command that printed no
/// parseable receipt is the real failure, and its stderr goes in the message.
fn read_receipt<T: for<'a> Deserialize<'a>>(root: &Path, subcommand: &str) -> Result<T, String> {
    let binary = knowledge_binary(root)?;
    let output = Command::new(&binary.path)
        .arg("--root")
        .arg(root)
        .arg(subcommand)
        .output()
        .map_err(|error| format!("cannot run {binary} {subcommand}: {error}"))?;
    // Name the binary AND when it was built. Several `fern-knowledge` builds
    // live on one machine, and the interesting failure is nearly always an old
    // one answering: without the build time the reader cannot tell.
    serde_json::from_slice(&output.stdout).map_err(|error| {
        format!(
            "{binary} {subcommand} printed no readable receipt ({error}): {}",
            String::from_utf8_lossy(&output.stderr).trim()
        )
    })
}

pub fn repository_inventory(root: &Path) -> Result<RepositoryInventory, String> {
    read_receipt(root, "check")
}

pub fn card_index(root: &Path) -> Result<CardIndex, String> {
    let binary = card_binary()?;
    let output = Command::new(&binary)
        .arg("check")
        .arg("--root")
        .arg(root)
        .output()
        .map_err(|error| format!("cannot run {} check: {error}", binary.display()))?;
    let receipt: CardRepositoryCheck = serde_json::from_slice(&output.stdout).map_err(|error| {
        format!(
            "{} check printed no readable receipt ({error}): {}",
            binary.display(),
            String::from_utf8_lossy(&output.stderr).trim()
        )
    })?;
    Ok(CardIndex {
        cards: receipt.card_index,
    })
}

pub fn card_repository(root: &Path) -> Result<CardRepositoryCheck, String> {
    let binary = card_binary()?;
    let output = Command::new(&binary)
        .arg("check")
        .arg("--root")
        .arg(root)
        .output()
        .map_err(|error| format!("cannot run {} check: {error}", binary.display()))?;
    serde_json::from_slice(&output.stdout).map_err(|error| {
        format!(
            "{} check printed no readable receipt ({error}): {}",
            binary.display(),
            String::from_utf8_lossy(&output.stderr).trim()
        )
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::BTreeMap;
    use std::time::{Duration, SystemTime, UNIX_EPOCH};

    fn scratch(name: &str) -> PathBuf {
        let unique = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
        let root = std::env::temp_dir().join(format!("fern-knowledge-bin-{name}-{}-{unique}", std::process::id()));
        std::fs::create_dir_all(root.join("target/release")).unwrap();
        std::fs::create_dir_all(root.join("target/debug")).unwrap();
        root
    }

    fn place(path: &Path) {
        std::fs::write(path, b"stand-in for a built binary").unwrap();
    }

    fn at(seconds_after_epoch: u64) -> SystemTime {
        UNIX_EPOCH + Duration::from_secs(seconds_after_epoch)
    }

    /// The failure this replaces: a release build from a previous week refused
    /// authoring that the current debug build accepts, because the resolver
    /// ranked the two profiles instead of reading their build times.
    #[test]
    fn the_newer_build_wins_whichever_profile_it_is() {
        let root = scratch("newer");
        let release = root.join("target/release/fern-knowledge");
        let debug = root.join("target/debug/fern-knowledge");
        place(&release);
        place(&debug);
        let candidates = [release.clone(), debug.clone()];

        let debug_is_newer = BTreeMap::from([(release.clone(), at(1_600_000_000)), (debug.clone(), at(1_700_000_000))]);
        assert_eq!(
            newest_existing(&candidates, |path| debug_is_newer.get(path).copied()).unwrap(),
            debug,
            "the newer debug build must win over an older release build"
        );

        // And the other way round, so this is not passing by candidate order.
        let release_is_newer =
            BTreeMap::from([(release.clone(), at(1_800_000_000)), (debug.clone(), at(1_700_000_000))]);
        assert_eq!(
            newest_existing(&candidates, |path| release_is_newer.get(path).copied()).unwrap(),
            release
        );
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn a_build_that_is_not_there_is_not_a_candidate() {
        let root = scratch("single");
        let release = root.join("target/release/fern-knowledge");
        let debug = root.join("target/debug/fern-knowledge");
        let candidates = [release.clone(), debug.clone()];
        let times = BTreeMap::from([(release.clone(), at(1_800_000_000)), (debug.clone(), at(1_700_000_000))]);

        assert!(
            newest_existing(&candidates, |path| times.get(path).copied()).is_none(),
            "no build on disk means no answer, however new its recorded time"
        );
        place(&debug);
        assert_eq!(
            newest_existing(&candidates, |path| times.get(path).copied()).unwrap(),
            debug,
            "the absent release build must not win on its timestamp alone"
        );
        std::fs::remove_dir_all(root).unwrap();
    }

    /// A caller reading a refusal has to be able to tell an old binary from a
    /// current one, so the chosen path carries its build time everywhere.
    #[test]
    fn the_chosen_binary_names_its_path_and_build_time() {
        let root = scratch("describe");
        let debug = root.join("target/debug/fern-knowledge");
        place(&debug);
        let chosen = describe(debug.clone());
        assert_eq!(chosen.path, debug);
        assert_ne!(chosen.built_at, "unknown", "a file on disk has a modification time");
        assert!(chosen.to_string().contains("built "), "{chosen}");
        std::fs::remove_dir_all(root).unwrap();
    }
}
