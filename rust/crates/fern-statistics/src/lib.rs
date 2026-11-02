//! The ordered chain that recomputes every number the paper cites.
//!
//! WHY THIS IS NOT A SHELL SCRIPT ANY MORE. It was 197 lines of bash whose
//! thirteen checks handled failure with `|| { echo "..."; }` -- print a
//! sentence and keep going -- so eleven of them could not stop anything. The
//! failure modes that cost this project time were all bash's own: `2>/dev/null`
//! on a correctness check hid a real exception and a stale JSON was compared
//! instead (correction 168), and documentation backticks inside a
//! double-quoted pattern executed as a command three separate times
//! (corrections 197, 218, 230).
//!
//! WHAT DID NOT MOVE. The eleven statistics layers are still Python and are
//! still invoked as subprocesses. They are where the science is, they need
//! numpy and scipy, and rewriting a bootstrap in Rust would buy nothing. This
//! crate owns the ORDER, the outcome of each step, and the decision about which
//! failures stop the run.
//!
//! The `registry` feature (on by default) keeps the legacy Registry readers and
//! Registry readers. `declare_analysis` hashes a freeze;
//! `admit_statistic_via_home` sends exact paths to Go, which alone admits the
//! live declared finding. An analysis box builds this library with
//! `--no-default-features`.

use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::Instant;

use serde::Serialize;
use serde::de::DeserializeOwned;
use sha2::{Digest, Sha256};

pub mod analysis_contract;
#[cfg(feature = "registry")]
pub mod run_set_interval_calibration;
// Card meaning is read through the installed fern-knowledge binary, never
// linked. A half-edited Card provider must not stop this crate compiling.
pub mod knowledge_cli;
pub mod packaging;
pub mod postprocess_io;
pub mod refresh;
pub mod repo_health;
#[cfg(feature = "registry")]
pub mod titration_statistics;

/// Shared error type for public statistics operations.
pub type Fallible<T> = Result<T, Box<dyn std::error::Error + Send + Sync>>;

/// Hex SHA-256 of exact bytes. Local so declare/hash paths do not open the
/// run registry crate.
pub(crate) fn sha256_hex(bytes: &[u8]) -> String {
    Sha256::digest(bytes).iter().map(|b| format!("{b:02x}")).collect()
}

/// Report an input that cannot name a valid statistical operation.
pub fn invalid_input(message: impl Into<String>) -> Box<dyn std::error::Error + Send + Sync> {
    Box::new(std::io::Error::new(std::io::ErrorKind::InvalidInput, message.into()))
}

/// Crash-safe sibling write used by statistics artifacts and projections.
pub fn write_file_durably(path: &Path, bytes: &[u8]) -> Fallible<()> {
    let directory = path
        .parent()
        .ok_or_else(|| invalid_input(format!("{path:?} has no parent directory")))?;
    let mut temp_name = path.file_name().unwrap_or_default().to_os_string();
    temp_name.push(".partial");
    let temp_path = directory.join(temp_name);
    let mut file = fs::File::create(&temp_path)?;
    file.write_all(bytes)?;
    file.sync_all()?;
    drop(file);
    fs::rename(&temp_path, path)?;
    if let Ok(directory_handle) = fs::File::open(directory) {
        let _ = directory_handle.sync_all();
    }
    Ok(())
}

/// Write pretty JSON with a trailing newline through the durable writer.
pub fn write_json_file<T: Serialize>(path: &Path, value: &T) -> Fallible<()> {
    write_file_durably(path, (serde_json::to_string_pretty(value)? + "\n").as_bytes())
}

/// Read one JSON artifact through its declared Rust type.
pub fn read_json_file<T: DeserializeOwned>(path: &Path) -> Fallible<T> {
    Ok(serde_json::from_str(&fs::read_to_string(path)?)?)
}

/// Whether something earns a CRATE, or is a module in one.
///
/// > **Commands are not crates. Nouns are not crates. A crate exists for a
/// > boundary that has to be enforced by a wall.**
///
/// This comment reached for size twice — a 400-line guest limit, then "enough
/// mass to be a build unit", which is the same idea in a different word. Both
/// are gone. Lines are easy to count, which is exactly why they keep getting
/// grabbed when the real question is hard.
///
/// The real question, and a new crate needs two or three of these:
///
/// 1. **A hard invariant to protect.** `fern-evidence` separates checked from
///    accepted; that is worth a wall.
/// 2. **A dependency to isolate.** Keeping Polars or a TUI stack out of the core.
/// 3. **More than one real consumer.** A CLI, an agent, CI, another crate all
///    calling the same API.
/// 4. **An API that has stopped moving.** Mid-migration shapes do not qualify.
///
/// And the reverse alarm: **if the only reason is that the name is different,
/// no new crate.** `fern-release` was created on exactly that reason and
/// withdrawn the same hour; packaging is now `packaging/` in this crate.
///
/// WHAT THIS CRATE IS. Not mathematics — the post-experiment research surface:
/// *the experiment is finished; turn it into numbers, figures and deliverables
/// you can check.* Statistics, checks over their outputs, and the packaging of
/// what ships are all that. What may never come in is anything with an
/// authority boundary: GPU scheduling, leases, worker launch, artifact
/// authority, evidence acceptance.
///
/// WHERE A CHECK BELONGS, in one question:
///
/// > **If this check fails, am I saying the research numbers are wrong, or that
/// > the repository's upkeep is wrong?**
///
/// The first is this crate's main surface. The second stays inside the
/// `repo_health` module: repository files, git state, document relationships,
/// and no scientific metric, lifecycle operation, or evidence decision.
///
/// `fern-stat` may ORCHESTRATE a check without this crate owning its domain.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ModuleRole {
    pub module: &'static str,
    /// One line: what question this module answers.
    pub answers: &'static str,
}

/// Every module here, and the question it answers. Adding one means saying so.
pub const AT_HOME: &[ModuleRole] = &[
    ModuleRole {
        module: "analysis_contract",
        answers: "which exact evidence and implementation produced one checked analysis",
    },
    ModuleRole {
        module: "knowledge_cli",
        answers: "which Knowledge Cards the statistics read, through the installed fern-knowledge binary rather than a crate link",
    },
    ModuleRole {
        module: "refresh",
        answers: "are the paper's numbers current, and in what order is that decided",
    },
    // CHG 2026-08-26: `metric_scale` and `artifact_freshness` left the roster
    // because they left the crate. Both are Python again --
    // scripts/check_metric_scale_sanity.py and
    // scripts/check_artifact_freshness.py -- and the Rust is at
    // retired/crates/fern-statistics/src/. Five repository lints went the same
    // way on the same day; they lived under `repo_health`, which is still one
    // roster entry, so they leave no gap here.
    ModuleRole {
        module: "packaging",
        answers: "what is in the bundle a reviewer receives, and is it complete",
    },
    // Pass 3, moved out of fern-cli-rs: the analysis that never launched anything.
    ModuleRole {
        module: "run_set_interval_calibration",
        answers: "how closely prediction intervals match their stated coverage across doses",
    },
    ModuleRole {
        module: "diagnose",
        answers: "are the residuals normal",
    },
    ModuleRole {
        module: "diagnose_quantile_tensors",
        answers: "do the model's reported quantiles behave",
    },
    ModuleRole {
        module: "export_latex_table",
        answers: "what does the paper table say",
    },
    ModuleRole {
        module: "postprocess_io",
        answers: "how are analysis-cohort Arrow tables read and written",
    },
    ModuleRole {
        module: "score_sequence_forecast",
        answers: "what score do these predictive samples earn",
    },
    ModuleRole {
        module: "titration_statistics",
        answers: "how forecast error and runtime change across one recipe or campaign's noise doses",
    },
    // A different question from the rest — is the repository's bookkeeping
    // honest, not are the research numbers right — kept here because
    // `fern-stat refresh` runs it and the difference earns a module, not a wall.
    ModuleRole {
        module: "repo_health",
        answers: "does every written record point at one real thing",
    },
];

/// Modules here whose question belongs elsewhere, with where.
///
/// Empty. The list exists so admitting one costs a deliberate edit naming its
/// destination, which is the difference between a guest room and a drawer.
pub const LODGERS: [(&str, &str); 0] = [];

/// What one step of the chain did.
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize)]
#[serde(rename_all = "snake_case")]
pub enum StepOutcome {
    /// The artifact did not exist before this run.
    New,
    /// The artifact exists and its bytes are different: the numbers moved.
    Changed,
    /// The artifact exists and its bytes are identical.
    Same,
    /// The program exited non-zero, or produced no artifact when one was named.
    Failed,
}

impl StepOutcome {
    pub fn label(self) -> &'static str {
        match self {
            Self::New => "NEW",
            Self::Changed => "CHANGED",
            Self::Same => "same",
            Self::Failed => "FAILED",
        }
    }
}

/// Whether a step's failure stops the chain.
///
/// The shell version decided this by whether someone had written `exit 1`
/// inside the `||` block, which meant the answer was invisible at the call
/// site and wrong for eleven of thirteen steps. It is now declared beside the
/// step, and the reason belongs in the declaration.
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize)]
#[serde(rename_all = "snake_case")]
pub enum OnFailure {
    /// The chain cannot produce trustworthy output past this point.
    Stop,
    /// Report it and continue. Reserved for checks whose finding is a fact
    /// about the world as often as it is a defect -- a concentrated mean on a
    /// chaotic attractor is real, and blocking on it is the permanently-firing
    /// alarm that teaches a reader to skip the output.
    Report,
}

/// How one step does its work.
///
/// Both kinds exist because the migration is partial by design. The eleven
/// statistics layers are Python and are staying Python; the checks move into
/// this crate one at a time, and a check that has moved should not pay for a
/// second process and a second interpreter start to be asked its question.
#[derive(Clone)]
pub enum StepAction {
    /// Run a program and read its exit status.
    Subprocess { program: PathBuf, arguments: Vec<String> },
    /// Call a function in this crate. It returns the lines a human should see,
    /// and whether the check passed.
    Native {
        /// What to print where a subprocess step prints its program path.
        name: &'static str,
        run: fn(&Path) -> NativeResult,
    },
}

/// What a native check found.
pub struct NativeResult {
    pub passed: bool,
    /// Everything the check wants a human to read, pass or fail.
    pub output: String,
}

/// Where to find `fern-run`, which owns every question this crate asks about
/// runs, matrices and lanes.
///
/// Resolved at runtime and never baked in: this crate is built once and run
/// from a repository checkout, a release profile, and a Worker Site, and a path
/// compiled in is a path that is wrong on every machine but the build host
/// (correction 281). `FERN_RUN_BIN` wins so a caller can point at a candidate
/// binary without moving the one a running plan still has to exec.
///
/// The failure names every place it looked, because "cannot run fern-run" with
/// no path is the error that reads like a missing data file.
pub fn fern_run_binary(root: &Path) -> Result<PathBuf, String> {
    if let Ok(declared) = std::env::var("FERN_RUN_BIN") {
        let path = PathBuf::from(&declared);
        if path.is_file() {
            return Ok(path);
        }
        return Err(format!("FERN_RUN_BIN is {declared:?}, which is not a file"));
    }
    let candidates = [root.join("target/debug/fern-run"), root.join("target/release/fern-run")];
    for candidate in &candidates {
        if candidate.is_file() {
            return Ok(candidate.clone());
        }
    }
    Err(format!(
        "cannot find fern-run. Looked at $FERN_RUN_BIN, {}. Build it, or set FERN_RUN_BIN.",
        candidates
            .iter()
            .map(|path| path.display().to_string())
            .collect::<Vec<_>>()
            .join(", ")
    ))
}

/// Which Python runs the analyses and the statistics layers.
///
/// Resolved, never defaulted. `/home/user/.venv/bin/python` was compiled into
/// three commands: correct on one laptop and wrong on every other machine,
/// including the Worker Site (correction 281). And a default string makes
/// "not supplied" indistinguishable from "supplied that exact value", which is
/// how an omitted `--python` came to be refused for disagreeing with a profile
/// (correction 284).
///
/// Order: what the caller asked for, then `$FERN_PYTHON`, then the interpreter
/// on PATH. The failure names every place it looked.
pub fn python_interpreter(asked_for: Option<&Path>) -> Result<PathBuf, String> {
    if let Some(path) = asked_for {
        if path.is_file() {
            return Ok(path.to_path_buf());
        }
        return Err(format!("--python is {}, which is not a file", path.display()));
    }
    if let Ok(declared) = std::env::var("FERN_PYTHON") {
        let path = PathBuf::from(&declared);
        if path.is_file() {
            return Ok(path);
        }
        return Err(format!("FERN_PYTHON is {declared:?}, which is not a file"));
    }
    let on_path = Command::new("sh")
        .arg("-c")
        .arg("command -v python3 || command -v python")
        .output()
        .ok()
        .filter(|found| found.status.success())
        .map(|found| String::from_utf8_lossy(&found.stdout).trim().to_string())
        .filter(|found| !found.is_empty());
    match on_path {
        Some(found) => Ok(PathBuf::from(found)),
        None => Err("no Python: pass --python, set FERN_PYTHON, or put python3 on PATH".to_string()),
    }
}

/// One step of the chain, and what to make of its outcome.
#[derive(Clone)]
pub struct Step {
    pub action: StepAction,
    /// The file this step rewrites, if it has one. Its bytes decide
    /// new/changed/same; a step with no artifact is a check, not a layer.
    pub artifact: Option<PathBuf>,
    /// One line a human reads to know what this step is for.
    pub description: &'static str,
    pub on_failure: OnFailure,
}

impl Step {
    /// Stable operation identity used by the old-shell migration census.
    ///
    /// This deliberately excludes the executable path because the Rust
    /// front end is itself `fern-stat` and its installed path varies. Human
    /// reports and caller-boundary tests use `executable_argv` instead.
    pub fn identity(&self) -> Vec<String> {
        match &self.action {
            StepAction::Subprocess { arguments, .. } => arguments.clone(),
            StepAction::Native { name, .. } => vec![(*name).to_string()],
        }
    }

    /// The complete command that will execute, including its selected owner.
    pub fn executable_argv(&self) -> Vec<String> {
        match &self.action {
            StepAction::Subprocess { program, arguments } => {
                let mut argv = Vec::with_capacity(arguments.len() + 1);
                argv.push(program.display().to_string());
                argv.extend(arguments.iter().cloned());
                argv
            }
            StepAction::Native { name, .. } => vec![(*name).to_string()],
        }
    }
}

/// What one step did, for the report and for a caller that wants the data.
#[derive(Debug, Clone, serde::Serialize)]
pub struct StepReport {
    pub program: String,
    pub artifact: Option<String>,
    pub description: &'static str,
    pub outcome: StepOutcome,
    pub seconds: u64,
    /// The tail of the program's combined output, kept only when it failed.
    pub failure_output: Option<String>,
}

fn digest_of(path: &Path) -> Option<String> {
    let bytes = std::fs::read(path).ok()?;
    let mut hasher = Sha256::new();
    hasher.update(&bytes);
    Some(format!("{:x}", hasher.finalize()))
}

/// Keep the last four lines, which is what a reader needs to see a traceback's
/// final frame without the chain printing a whole Python log.
fn failure_tail(output: &[u8], stderr: &[u8]) -> String {
    let combined = format!("{}{}", String::from_utf8_lossy(output), String::from_utf8_lossy(stderr));
    let lines: Vec<&str> = combined.lines().collect();
    lines[lines.len().saturating_sub(4)..].join("\n")
}

/// Run one step and say what it did.
///
/// The artifact's modification time is RESTORED when the bytes are identical.
/// A layer rewrites its file whether or not the numbers moved, and the
/// freshness gate later in the chain reads modification times, so without this
/// every correct figure reads as stale after a refresh -- a false-alarm
/// generator that trains the reader to skip the one real one. Restoring the
/// timestamp makes "modified" mean "the numbers moved".
pub fn run_step(step: &Step, working_directory: &Path) -> std::io::Result<StepReport> {
    let artifact_path = step.artifact.as_ref().map(|name| working_directory.join(name));
    let before = artifact_path.as_deref().and_then(digest_of);
    let before_modified = artifact_path
        .as_deref()
        .and_then(|path| std::fs::metadata(path).ok())
        .and_then(|metadata| metadata.modified().ok());

    let started = Instant::now();
    let (succeeded, standard_output, error_output) = match &step.action {
        StepAction::Subprocess { program, arguments } => {
            let output = Command::new(program)
                .args(arguments)
                .current_dir(working_directory)
                .output()?;
            (output.status.success(), output.stdout, output.stderr)
        }
        StepAction::Native { run, .. } => {
            let result = run(working_directory);
            (result.passed, result.output.into_bytes(), Vec::new())
        }
    };
    let seconds = started.elapsed().as_secs();

    let (outcome, failure_output) = if succeeded {
        let after = artifact_path.as_deref().and_then(digest_of);
        let outcome = match (&before, &after) {
            // A step that names an artifact and then does not write one has not
            // succeeded, whatever its exit code says. `paper_plot.py` exits zero
            // for an unknown flag and changes nothing (correction 151). An
            // artifact that existed and is now GONE is the same failure seen
            // from the other side.
            (_, None) if step.artifact.is_some() => StepOutcome::Failed,
            (None, Some(_)) => StepOutcome::New,
            (Some(old), Some(new)) if old == new => StepOutcome::Same,
            (Some(_), Some(_)) => StepOutcome::Changed,
            // No artifact named: a check, which succeeds by exiting zero.
            (None, None) | (Some(_), None) => StepOutcome::Same,
        };
        if outcome == StepOutcome::Same
            && let (Some(path), Some(modified)) = (artifact_path.as_deref(), before_modified)
        {
            let _ = filetime_set(path, modified);
        }
        let failure =
            (outcome == StepOutcome::Failed).then(|| "the step exited zero but wrote no artifact".to_string());
        (outcome, failure)
    } else {
        (StepOutcome::Failed, Some(failure_tail(&standard_output, &error_output)))
    };

    Ok(StepReport {
        program: step.executable_argv().join(" "),
        artifact: step.artifact.as_ref().map(|p| p.display().to_string()),
        description: step.description,
        outcome,
        seconds,
        failure_output,
    })
}

/// Restore one file's modification time.
///
/// `std::fs` cannot set mtime, and the crate that can is not worth a dependency
/// for one call, so this uses `utimensat` through the same `touch` the shell
/// version used -- but with the timestamp taken from the file's own metadata
/// rather than reformatted through `date -r`, which is where a locale could
/// have silently produced the wrong stamp.
fn filetime_set(path: &Path, modified: std::time::SystemTime) -> std::io::Result<()> {
    let seconds = modified
        .duration_since(std::time::UNIX_EPOCH)
        .map_err(|error| std::io::Error::other(error.to_string()))?
        .as_secs();
    let status = Command::new("touch")
        .arg("-d")
        .arg(format!("@{seconds}"))
        .arg(path)
        .status()?;
    if status.success() {
        Ok(())
    } else {
        Err(std::io::Error::other(format!(
            "cannot restore the modification time of {}",
            path.display()
        )))
    }
}

/// Run every step in order and return what each one did.
///
/// It does not stop at the first `Stop` failure: a chain that reports one
/// failure and hides the next five costs another run to discover them. It runs
/// everything and lets the caller refuse at the end.
pub fn run_chain(steps: &[Step], working_directory: &Path) -> std::io::Result<Vec<StepReport>> {
    steps.iter().map(|step| run_step(step, working_directory)).collect()
}

/// Whether the chain's outputs may be trusted.
pub fn chain_is_trustworthy(steps: &[Step], reports: &[StepReport]) -> bool {
    steps
        .iter()
        .zip(reports)
        .all(|(step, report)| report.outcome != StepOutcome::Failed || step.on_failure == OnFailure::Report)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_directory(name: &str) -> PathBuf {
        let base = std::env::temp_dir().join(format!("fern-statistics-{name}-{}", std::process::id()));
        std::fs::create_dir_all(&base).unwrap();
        base
    }

    fn writing_step(artifact: &str, shell: &str) -> Step {
        Step {
            action: StepAction::Subprocess {
                program: PathBuf::from("/bin/sh"),
                arguments: vec!["-c".to_string(), shell.to_string()],
            },
            artifact: Some(PathBuf::from(artifact)),
            description: "test step",
            on_failure: OnFailure::Stop,
        }
    }

    /// A native check runs in-process and its output survives to the report.
    #[test]
    fn a_native_step_reports_what_the_function_found() {
        let directory = temp_directory("native-step");
        fn always_fails(_: &Path) -> NativeResult {
            NativeResult {
                passed: false,
                output: "two lanes are missing".to_string(),
            }
        }
        let step = Step {
            action: StepAction::Native {
                name: "test-check",
                run: always_fails,
            },
            artifact: None,
            description: "a check with no subprocess",
            on_failure: OnFailure::Report,
        };
        let report = run_step(&step, &directory).unwrap();
        assert_eq!(report.outcome, StepOutcome::Failed);
        assert_eq!(report.program, "test-check");
        assert!(report.failure_output.unwrap().contains("two lanes are missing"));
        std::fs::remove_dir_all(&directory).unwrap();
    }

    /// The whole reason the timestamp is restored.
    ///
    /// A layer rewrites its artifact every run. The freshness gate later in the
    /// chain reads modification times, so an unchanged recomputation would mark
    /// every correct figure stale and train the reader to ignore the gate.
    #[test]
    fn an_unchanged_artifact_keeps_its_modification_time() {
        let directory = temp_directory("unchanged-keeps-mtime");
        let artifact = directory.join("numbers.json");
        std::fs::write(&artifact, b"{\"value\": 1}").unwrap();
        // Backdate it, or "unchanged" and "just written" are indistinguishable.
        Command::new("touch")
            .arg("-d")
            .arg("@1600000000")
            .arg(&artifact)
            .status()
            .unwrap();
        let before = std::fs::metadata(&artifact).unwrap().modified().unwrap();

        let step = writing_step("numbers.json", "printf '{\"value\": 1}' > numbers.json");
        let report = run_step(&step, &directory).unwrap();
        assert_eq!(report.outcome, StepOutcome::Same);
        assert_eq!(
            std::fs::metadata(&artifact).unwrap().modified().unwrap(),
            before,
            "identical bytes must not look newer than the data they summarize"
        );

        // And a real change must move it, or the gate can never fire.
        let step = writing_step("numbers.json", "printf '{\"value\": 2}' > numbers.json");
        let report = run_step(&step, &directory).unwrap();
        assert_eq!(report.outcome, StepOutcome::Changed);
        assert!(std::fs::metadata(&artifact).unwrap().modified().unwrap() > before);
        std::fs::remove_dir_all(&directory).unwrap();
    }

    /// Exit zero is not evidence that a step did its job.
    ///
    /// `paper_plot.py` exposes functions and has no command-line entry point,
    /// so invoking it with an unknown flag exits zero and changes nothing
    /// (correction 151). A step that names an artifact must produce one.
    #[test]
    fn a_step_that_writes_no_artifact_has_not_succeeded() {
        let directory = temp_directory("zero-exit-no-artifact");
        let step = writing_step("never_written.json", "true");
        let report = run_step(&step, &directory).unwrap();
        assert_eq!(report.outcome, StepOutcome::Failed);
        assert!(report.failure_output.unwrap().contains("wrote no artifact"));
        std::fs::remove_dir_all(&directory).unwrap();
    }

    /// A failing step reports its own last words.
    ///
    /// The shell version sent every step's output to a temp file and printed
    /// its tail only on failure; when someone silenced stderr instead, a real
    /// exception vanished and a stale artifact was compared in its place
    /// (correction 168). The output is captured, never discarded.
    #[test]
    fn a_failing_step_keeps_the_end_of_its_output() {
        let directory = temp_directory("failure-tail");
        let step = writing_step(
            "unwritten.json",
            "echo first; echo second; echo 'ValueError: the real reason' >&2; exit 3",
        );
        let report = run_step(&step, &directory).unwrap();
        assert_eq!(report.outcome, StepOutcome::Failed);
        let tail = report.failure_output.unwrap();
        assert!(tail.contains("ValueError: the real reason"), "got: {tail}");
        std::fs::remove_dir_all(&directory).unwrap();
    }

    /// Every module here is on the roster, saying what it answers.
    ///
    /// The admission rule as a test rather than a paragraph. No size threshold:
    /// two earlier versions of this test had one and both were invented. What
    /// decides is the question a module answers and whether it has a home.
    #[test]
    fn every_module_is_on_the_roster_or_a_named_lodger() {
        let source_directory = Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
        let mut modules: Vec<String> = std::fs::read_dir(&source_directory)
            .unwrap()
            .flatten()
            .filter_map(|entry| {
                let path = entry.path();
                let stem = path.file_stem()?.to_str()?.to_string();
                if path.is_dir() {
                    return path.join("mod.rs").is_file().then_some(stem);
                }
                (path.extension()?.to_str()? == "rs" && stem != "lib" && stem != "main").then_some(stem)
            })
            .collect();
        modules.sort();
        let mut declared: Vec<String> = AT_HOME
            .iter()
            .map(|role| role.module.to_string())
            .chain(LODGERS.iter().map(|(name, _)| name.to_string()))
            .collect();
        declared.sort();
        // The rule is that nothing is UNDECLARED, not that everything declared
        // is present: Pass 3 lands its modules over several commits from another
        // worktree, and a roster entry that arrives before its file is a
        // statement of intent rather than a failure.
        let undeclared: Vec<&String> = modules.iter().filter(|m| !declared.contains(m)).collect();
        assert!(
            undeclared.is_empty(),
            "{undeclared:?} must be on AT_HOME with the question each answers, or in LODGERS \
             with where it belongs. `fern-stat` may CALL a check without this crate owning it"
        );
        for role in AT_HOME {
            assert!(
                !role.answers.is_empty(),
                "{} is on the roster without saying what it answers",
                role.module
            );
        }
        for (name, destination) in LODGERS {
            assert!(
                !destination.is_empty(),
                "{name} lodges here without saying where it should live, which is how a drawer starts"
            );
        }
    }

    /// An advisory failure must not condemn the run, and a fatal one must.
    #[test]
    fn only_a_declared_stop_failure_makes_the_chain_untrustworthy() {
        let advisory = Step {
            on_failure: OnFailure::Report,
            ..writing_step("a.json", "true")
        };
        let fatal = writing_step("b.json", "true");
        let failed = StepReport {
            program: "x".to_string(),
            artifact: None,
            description: "t",
            outcome: StepOutcome::Failed,
            seconds: 0,
            failure_output: None,
        };
        let passed = StepReport {
            outcome: StepOutcome::Same,
            ..failed.clone()
        };
        assert!(chain_is_trustworthy(
            &[advisory.clone(), fatal.clone()],
            &[failed.clone(), passed.clone()]
        ));
        assert!(!chain_is_trustworthy(&[advisory, fatal], &[passed, failed]));
    }
}
