//! The campaign's refresh chain, declared once.
//!
//! Order is dependency order, not preference. The injection audit runs FIRST
//! because if a lane's injected energy has stopped tracking the dose, every
//! number computed from that lane afterwards should be read with that in mind.
//! The checks run last because they judge what the layers just produced.

use std::path::PathBuf;

use crate::{NativeResult, OnFailure, Step, StepAction};

/// Where the layers write. Every artifact path below is relative to this.
pub const CAMPAIGN_OUTPUT: &str = "results/paper_campaign";

/// One statistics layer through Rust declaration and Go-owned admission.
fn layer(
    fern_stat: &str,
    python: &str,
    fern_home: &str,
    analysis: &str,
    artifact: &str,
    description: &'static str,
) -> Step {
    Step {
        action: StepAction::Subprocess {
            program: PathBuf::from(fern_stat),
            arguments: vec![
                "research-compute-statistic".to_string(),
                analysis.to_string(),
                "--python".to_string(),
                python.to_string(),
                "--fern-home".to_string(),
                fern_home.to_string(),
            ],
        },
        artifact: Some(PathBuf::from(CAMPAIGN_OUTPUT).join(artifact)),
        description,
        on_failure: OnFailure::Stop,
    }
}

/// One check: a program that judges, and writes nothing.
///
/// `arguments` matters. Two of these are subcommands of a larger tool and do
/// something else entirely without them: `check_compute_drift.py` runs `scan`
/// AND `coverage` as separate steps, and `register_model_variants.py` without
/// `check` REGISTERS rather than verifying.
fn check(python: &str, program: &str, arguments: &[&str], description: &'static str, on_failure: OnFailure) -> Step {
    let mut argv = vec![program.to_string()];
    argv.extend(arguments.iter().map(|a| a.to_string()));
    Step {
        action: StepAction::Subprocess {
            program: PathBuf::from(python),
            arguments: argv,
        },
        artifact: None,
        description,
        on_failure,
    }
}

/// Every step, in the order it must run.
pub fn campaign_refresh_chain(python: &str, fern_stat: &str, fern_home: &str, fern_registry: &str) -> Vec<Step> {
    vec![
        // A broken import is not a statistics problem: it stops every cell from
        // being produced at all, one line into scoring, after the forecast has
        // already been computed. Two of these killed a local lane twice on
        // 2026-08-12, so this is cheap and first.
        check(
            python,
            "scripts/check_worker_imports_resolve.py",
            &[],
            "every imported name exists where it is imported from",
            OnFailure::Stop,
        ),
        layer(
            fern_stat,
            python,
            fern_home,
            "injection-audit",
            "injection_audit.json",
            "integrity of every lane, run first",
        ),
        layer(
            fern_stat,
            python,
            fern_home,
            "main-table",
            "main_table.json",
            "clean error, fold change, d50, EPT, reversal dose",
        ),
        layer(
            fern_stat,
            python,
            fern_home,
            "metric-disagreement",
            "metric_disagreement.json",
            "MSE against WD",
        ),
        layer(
            fern_stat,
            python,
            fern_home,
            "real-sensor-statistics",
            "real_sensor_statistics.json",
            "recorded gait, unit-free only",
        ),
        layer(
            fern_stat,
            python,
            fern_home,
            "probabilistic-statistics",
            "probabilistic_statistics.json",
            "coverage, width, WIS, quantile-PIT proxy",
        ),
        layer(
            fern_stat,
            python,
            fern_home,
            "holdout-confirmation",
            "holdout_confirmation.json",
            "exact sign test on the holdout",
        ),
        layer(
            fern_stat,
            python,
            fern_home,
            "random-initial-condition-confirmation",
            "random_initial_condition_confirmation.json",
            "reversal rate over sixteen drawn initial conditions",
        ),
        layer(
            fern_stat,
            python,
            fern_home,
            "clean-target-response",
            "clean_target_response.json",
            "scoring against the retained clean future",
        ),
        layer(
            fern_stat,
            python,
            fern_home,
            "degradation-decomposition",
            "degradation_decomposition.json",
            "injected energy versus ledger residual",
        ),
        layer(
            fern_stat,
            python,
            fern_home,
            "two-dial-interaction",
            "two_dial_interaction.json",
            "system dial crossed with measurement dial",
        ),
        // Added to the chain on 2026-08-04. It ships in the supplement and
        // nothing recomputed it, which is exactly the shape where an artifact
        // and the data it claims to summarize drift apart unnoticed.
        layer(
            fern_stat,
            python,
            fern_home,
            "hard-switch-adaptation",
            "hard_switch_adaptation.json",
            "adaptation contrast at eight paired units",
        ),
        // Ported 2026-08-12. Verified against the script it replaced while that
        // script still ran, on the real campaign, where it FAILS: same one
        // collision, same lane and world, same row counts per variant.
        // CHG 2026-08-26: back to Python, restored from the retired original
        // and verified against the Rust on the real repository before the Rust
        // was retired -- 462 groups, the same 2 collisions, byte-identical
        // output and exit code. The Rust's one real gain came back with it:
        // the check asks `fern-run list-runs`, the command that OWNS run rows,
        // instead of opening each lane's fern.db with a second SQL driver.
        Step {
            action: StepAction::Subprocess {
                program: PathBuf::from(python),
                arguments: vec!["scripts/check_model_name_collisions.py".to_string()],
            },
            artifact: None,
            description: "no model_type value covers two different models",
            on_failure: OnFailure::Report,
        },
        // Ported 2026-08-12 — the READING half only. Registering a variant
        // writes `.ai/model_variants.json` and is a deliberate human act; it
        // stays in the script. Dropping the `check` argument during the Pass 1
        // port would have turned this verification into a registration, which
        // is why that argument is written out in the chain's test.
        Step {
            action: StepAction::Subprocess {
                program: PathBuf::from(python),
                arguments: vec!["scripts/register_model_variants.py".to_string(), "check".to_string()],
            },
            artifact: None,
            description: "every model name maps to exactly one configuration",
            on_failure: OnFailure::Report,
        },
        // Ported into this crate 2026-08-12; it reads source files and looks at
        // directories, and needed no Python at all. Verified against the script
        // it replaced on the real repository before that script was retired:
        // the same four lanes, none missing.
        // CHG 2026-08-26: back to Python, restored from the retired original
        // and verified against the Rust on the real repository before the Rust
        // was retired -- the same three lanes, none missing, byte-identical.
        // The Rust's two structural gains came back with it: the root is a
        // --root argument, and the audit is a pure function separate from the
        // printing. (An earlier note here said the comparison was permanently
        // unavailable because the script was gone; it was in retired/ the
        // whole time, which is the argument for retiring rather than deleting.)
        Step {
            action: StepAction::Subprocess {
                program: PathBuf::from(python),
                arguments: vec!["scripts/check_lane_references.py".to_string()],
            },
            artifact: None,
            description: "every lane directory named in code still exists",
            on_failure: OnFailure::Report,
        },
        // Ported 2026-08-12. Advisory on purpose: a concentrated mean is a
        // fact about the world as often as it is a defect — a chaotic attractor
        // really does produce rare huge errors — so this reports and never
        // blocks.
        // CHG 2026-08-26: back to Python, where this check started. The
        // statistic itself lives in python/fern_analysis/metric_scale.py and is
        // pinned by crates/fern-statistics/fixtures/metric_scale_golden.json,
        // captured from the Rust before it was retired.
        Step {
            action: StepAction::Subprocess {
                program: PathBuf::from(python),
                arguments: vec!["scripts/check_metric_scale_sanity.py".to_string()],
            },
            artifact: None,
            description: "no reported mean is really a handful of windows",
            on_failure: OnFailure::Report,
        },
        check(
            python,
            "scripts/check_compute_drift.py",
            &["scan"],
            "the code did not change underneath a matrix being filled",
            OnFailure::Report,
        ),
        // These two are the reason the chain exists: they decide whether the
        // paper's stated numbers and shipped figures still match their sources.
        check(
            python,
            "scripts/check_paper_numbers.py",
            &[],
            "the numbers the paper states still match the artifacts",
            OnFailure::Stop,
        ),
        // Ported 2026-08-12. Fatal: a figure that contradicts the numbers under
        // it is the defect this whole chain was built after.
        Step {
            action: StepAction::Subprocess {
                program: PathBuf::from(python),
                arguments: vec!["scripts/check_artifact_freshness.py".to_string()],
            },
            artifact: None,
            description: "every figure and shipped copy is newer than its data",
            on_failure: OnFailure::Stop,
        },
        // Ported 2026-08-12; it reads one Markdown table and then the
        // filesystem, and needed no Python. Verified against the script it
        // replaced on the real repository: the same counts, same exit code.
        // CHG 2026-08-26: back to Python, where this check started. The
        // retired original was restored as scripts/check_lab_ledger.py (with
        // the root as a --root flag) and verified against the Rust it replaces
        // on the real repository before the Rust was retired: the same five
        // misses, byte-identical, same exit code. The Rust's matcher was also
        // measurably weaker than the Python's globbing, so this is a repair.
        Step {
            action: StepAction::Subprocess {
                program: PathBuf::from(python),
                arguments: vec!["scripts/check_lab_ledger.py".to_string()],
            },
            artifact: None,
            description: "the lab ledger's scripts, artifacts and notes all exist",
            on_failure: OnFailure::Report,
        },
        Step {
            action: StepAction::Subprocess {
                // CHG 2026-08-27: check-lab-citations moved from the Go
                // `fern-home` to the C# `fern-registry` (commit b181eff0); same
                // arguments, refusal exits 2 instead of 1, and this runner
                // already treats any non-zero exit as failure.
                program: PathBuf::from(fern_registry),
                arguments: vec![
                    "check-lab-citations".to_string(),
                    "--db".to_string(),
                    "results/paper_campaign/lifecycle.db".to_string(),
                    "--lab-dir".to_string(),
                    "docs/lab".to_string(),
                    "--root".to_string(),
                    ".".to_string(),
                ],
            },
            artifact: None,
            description: "every lab citation resolves in Registry Home",
            on_failure: OnFailure::Report,
        },
        // Programs are human-owned prose with a small machine frame. fern-card
        // owns that frame; refresh reads its receipt instead of preserving a
        // second Rust or Python interpretation.
        Step {
            action: StepAction::Native {
                name: "knowledge-programs",
                run: knowledge_programs_check,
            },
            artifact: None,
            description: "every research program carries a valid machine-checkable frame",
            on_failure: OnFailure::Report,
        },
        Step {
            action: StepAction::Native {
                name: "knowledge-corrections",
                run: knowledge_corrections_check,
            },
            artifact: None,
            description: "the active-corrections digest is current with its source",
            on_failure: OnFailure::Report,
        },
        // Ported 2026-08-12.
        Step {
            action: StepAction::Subprocess {
                program: PathBuf::from(python),
                arguments: vec!["scripts/check_pending_patches.py".to_string()],
            },
            artifact: None,
            description: "every pending-patch note says where it stands",
            on_failure: OnFailure::Report,
        },
        // Added 2026-08-14. `docs/workflow.md` is 154 KB of design contract that
        // CLAUDE.md requires an agent to read AND update whenever a stage order
        // changes, and nothing verified a word of it. Its first run found
        // `run_sweep.rs::try_start`, renamed to `try_run_plan` some time before.
        Step {
            action: StepAction::Subprocess {
                program: PathBuf::from(python),
                arguments: vec!["scripts/check_doc_citations.py".to_string()],
            },
            artifact: None,
            description: "every file::name a pinned document cites still exists",
            on_failure: OnFailure::Report,
        },
        // The second half of the drift tool: `scan` asks whether any lane drifted,
        // `coverage` asks whether any path is undecided about being scanned at all.
        check(
            python,
            "scripts/check_compute_drift.py",
            &["coverage"],
            "no root file or crate is undecided about drift scanning",
            OnFailure::Report,
        ),
        // Restored 2026-08-12 by the port audit. The first port dropped this
        // step; nothing ran the inventory check for the day it was missing, and
        // it had gone stale by the time the audit put it back. Without `--check`
        // the same program REGENERATES the inventory instead of verifying it.
        check(
            python,
            "scripts/build_script_inventory.py",
            &["--check"],
            "the script inventory matches the Status headers",
            OnFailure::Report,
        ),
        // Added 2026-08-12, and the only step here the shell chain never ran.
        // It lived in a headless eight-line file that nothing executed, so the
        // wall was refreshed by hand while every commit read as though the
        // chain did it. Advisory: a gallery is a projection that votes on
        // nothing, and a broken wall must not stop the paper's numbers from
        // being recomputed. It names its artifact, so a run that leaves the
        // wall unwritten is reported as a failure whatever the exit code says.
        Step {
            action: StepAction::Subprocess {
                program: PathBuf::from(python),
                arguments: vec!["scripts/build_figure_gallery.py".to_string()],
            },
            artifact: Some(PathBuf::from("figures_by_question/gallery.html")),
            description: "the figure wall matches the figures on disk",
            on_failure: OnFailure::Report,
        },
    ]
}

/// Verify Program/Design frames through the shared TOML Card authority.
fn knowledge_programs_check(root: &std::path::Path) -> NativeResult {
    let check = match crate::knowledge_cli::card_repository(root) {
        Ok(check) => check,
        Err(error) => {
            return NativeResult {
                passed: false,
                output: error,
            };
        }
    };
    let programs = check
        .entries
        .iter()
        .filter(|entry| {
            entry.canonical_path.starts_with("docs/programs/") && entry.canonical_path != "docs/programs/README.md"
        })
        .collect::<Vec<_>>();
    let invalid = programs.iter().filter(|entry| entry.is_refused()).collect::<Vec<_>>();
    let invalid_links = check
        .card_index
        .iter()
        .filter(|card| card.kind == "program" || card.kind == "research-design")
        .flat_map(|card| {
            card.links
                .iter()
                .filter(|link| link.outcome == "invalid" || link.outcome == "unresolved")
                .map(move |link| (card, link))
        })
        .collect::<Vec<_>>();
    let typed = programs.iter().filter(|entry| entry.classification == "typed").count();
    let legacy = programs.iter().filter(|entry| entry.classification == "legacy").count();
    let mut output = format!(
        "{} Program/Design Card(s): {typed} typed, {legacy} explicit legacy adapter(s)",
        programs.len()
    );
    for entry in &invalid {
        output.push_str(&format!("\n  INVALID {}", entry.canonical_path));
        for diagnostic in &entry.diagnostics {
            output.push_str(&format!("\n    {diagnostic}"));
        }
    }
    for (card, link) in &invalid_links {
        output.push_str(&format!(
            "\n  INVALID {}: {} {} is {}: {}",
            card.canonical_path, link.relation, link.target, link.outcome, link.detail
        ));
    }
    NativeResult {
        passed: invalid.is_empty() && invalid_links.is_empty(),
        output,
    }
}

/// Verify the generated digest through the same authority that writes it.
///
/// `fern-knowledge check` already validates it: a stale or missing
/// `.ai/corrections_active.md` makes the `.ai/corrections.md` inventory entry
/// invalid, carrying the diagnostic. Reading that one entry keeps this check
/// on the authority's own answer instead of writing a second one here.
fn knowledge_corrections_check(root: &std::path::Path) -> NativeResult {
    let inventory = match crate::knowledge_cli::repository_inventory(root) {
        Ok(inventory) => inventory,
        Err(error) => {
            return NativeResult {
                passed: false,
                output: error,
            };
        }
    };
    match inventory
        .entries
        .iter()
        .find(|entry| entry.canonical_path == ".ai/corrections.md")
    {
        // No source file is not a stale digest; the authority reports the same.
        None => NativeResult {
            passed: true,
            output: "no .ai/corrections.md source in this repository".to_string(),
        },
        Some(entry) if entry.is_refused() => NativeResult {
            passed: false,
            output: format!(
                "{}; run fern-knowledge refresh-corrections",
                entry.diagnostics.join("; ")
            ),
        },
        Some(_) => NativeResult {
            passed: true,
            output: "active Corrections digest is current".to_string(),
        },
    }
}

/// The argument the drift check needs when a lane must be sealed by hand.
pub const COMPUTE_DRIFT_HELP: &str = "\
a lane spans revisions that touch the compute path and carries no seal.
Its rows are UNKNOWN, not clean. Read the diff and seal it:
  scripts/check_compute_drift.py show <lane>
  scripts/check_compute_drift.py seal <lane> --verdict no_drift --reason '...'";

/// What to tell a reader when the freshness gate refuses.
pub const STALE_ARTIFACT_HELP: &str = "\
re-render the stale units, then re-copy into paper/aaai_upload:
  paper_plot.py --unit titration-ledger --unit adaptation-gap \\
                --unit metric-disagreement --unit clean-target-attribution";

#[cfg(test)]
mod tests {
    use super::*;

    /// The chain must not silently lose a step in the move out of shell.
    ///
    /// The first draft of this port dropped three checks and both subcommand
    /// arguments, and the test written beside it passed -- because it asserted
    /// the count from the same list the port was built from, which tests
    /// nothing. The expected argv is therefore written out in full, taken from
    /// `git show` of the shell script it replaced. A step that vanishes in a
    /// later edit fails here; a step that vanishes because someone edited both
    /// this list and the chain together is a deliberate change, which is the
    /// most a test can ask for.
    ///
    /// `register_model_variants.py` without `check` REGISTERS instead of
    /// verifying, and `check_compute_drift.py` runs twice with different
    /// subcommands, so the arguments are part of the identity of a step.
    ///
    /// Hand-written lists still failed once: the audit on 2026-08-12 found
    /// `build_script_inventory.py --check` missing from this list AND from the
    /// chain, so the two agreed with each other and neither agreed with the
    /// shell script. `nothing_the_shell_chain_ran_is_missing` below reads the
    /// retired script itself, which is the only party to the comparison that
    /// cannot be edited into agreement by whoever is doing the porting.
    #[test]
    fn the_chain_is_exactly_this_list_of_steps_in_this_order() {
        let expected: Vec<Vec<&str>> = vec![
            vec!["scripts/check_worker_imports_resolve.py"],
            vec![
                "research-compute-statistic",
                "injection-audit",
                "--python",
                "python",
                "--fern-home",
                "/receipted/fern-home",
            ],
            vec![
                "research-compute-statistic",
                "main-table",
                "--python",
                "python",
                "--fern-home",
                "/receipted/fern-home",
            ],
            vec![
                "research-compute-statistic",
                "metric-disagreement",
                "--python",
                "python",
                "--fern-home",
                "/receipted/fern-home",
            ],
            vec![
                "research-compute-statistic",
                "real-sensor-statistics",
                "--python",
                "python",
                "--fern-home",
                "/receipted/fern-home",
            ],
            vec![
                "research-compute-statistic",
                "probabilistic-statistics",
                "--python",
                "python",
                "--fern-home",
                "/receipted/fern-home",
            ],
            vec![
                "research-compute-statistic",
                "holdout-confirmation",
                "--python",
                "python",
                "--fern-home",
                "/receipted/fern-home",
            ],
            vec![
                "research-compute-statistic",
                "random-initial-condition-confirmation",
                "--python",
                "python",
                "--fern-home",
                "/receipted/fern-home",
            ],
            vec![
                "research-compute-statistic",
                "clean-target-response",
                "--python",
                "python",
                "--fern-home",
                "/receipted/fern-home",
            ],
            vec![
                "research-compute-statistic",
                "degradation-decomposition",
                "--python",
                "python",
                "--fern-home",
                "/receipted/fern-home",
            ],
            vec![
                "research-compute-statistic",
                "two-dial-interaction",
                "--python",
                "python",
                "--fern-home",
                "/receipted/fern-home",
            ],
            vec![
                "research-compute-statistic",
                "hard-switch-adaptation",
                "--python",
                "python",
                "--fern-home",
                "/receipted/fern-home",
            ],
            vec!["scripts/check_model_name_collisions.py"],
            vec!["scripts/register_model_variants.py", "check"],
            vec!["scripts/check_lane_references.py"],
            // CHG 2026-08-26: metric-scale, lab-ledger and the six repository
            // lints returned to Python scripts, so their identities are script
            // paths again.
            vec!["scripts/check_metric_scale_sanity.py"],
            vec!["scripts/check_compute_drift.py", "scan"],
            vec!["scripts/check_paper_numbers.py"],
            vec!["scripts/check_artifact_freshness.py"],
            vec!["scripts/check_lab_ledger.py"],
            vec![
                "check-lab-citations",
                "--db",
                "results/paper_campaign/lifecycle.db",
                "--lab-dir",
                "docs/lab",
                "--root",
                ".",
            ],
            vec!["knowledge-programs"],
            vec!["knowledge-corrections"],
            vec!["scripts/check_pending_patches.py"],
            // Not from the shell chain either: `docs/workflow.md` had never
            // been verified by anything.
            vec!["scripts/check_doc_citations.py"],
            vec!["scripts/check_compute_drift.py", "coverage"],
            vec!["scripts/build_script_inventory.py", "--check"],
            // Not from the shell chain; see the step's comment above.
            vec!["scripts/build_figure_gallery.py"],
        ];
        let steps = campaign_refresh_chain(
            "python",
            "fern-stat",
            "/receipted/fern-home",
            "/receipted/fern-registry",
        );
        let identities: Vec<Vec<String>> = steps.iter().map(Step::identity).collect();
        let actual: Vec<Vec<&str>> = identities
            .iter()
            .map(|argv| argv.iter().map(String::as_str).collect())
            .collect();
        assert_eq!(actual, expected);
        let lab_citations = steps
            .iter()
            .find(|step| step.identity().first().map(String::as_str) == Some("check-lab-citations"))
            .expect("the declared chain carries the Lab-citations check");
        assert_eq!(
            lab_citations.executable_argv(),
            vec![
                "/receipted/fern-registry",
                "check-lab-citations",
                "--db",
                "results/paper_campaign/lifecycle.db",
                "--lab-dir",
                "docs/lab",
                "--root",
                ".",
            ]
        );
        // Eleven statistics layers plus the figure wall, which is not a
        // statistic but is written the same way and reported the same way.
        assert_eq!(
            steps.iter().filter(|s| s.artifact.is_some()).count(),
            12,
            "writes a file"
        );
        assert_eq!(steps.iter().filter(|s| s.artifact.is_none()).count(), 16, "checks");
    }

    /// Every program the shell chain executed is still executed here.
    ///
    /// The list above is written by hand and can therefore be wrong in the same
    /// direction as the code. This one extracts the shell script's own step list
    /// mechanically and compares, which is the check that would have caught the
    /// dropped inventory step on the day it was dropped.
    ///
    /// Seven steps became native Rust and no longer name a Python file; they are
    /// listed with what replaced them, so a rename is a deliberate edit here
    /// rather than a silent disappearance. `CARGO_MANIFEST_DIR` locates the
    /// retired script: acceptable because only the test reads it and a test runs
    /// where it was built, never in a shipped binary (correction 281).
    #[test]
    fn nothing_the_shell_chain_ran_is_missing() {
        let retired = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../retired/scripts/refresh_paper_statistics_2026_08_12.sh");
        let Ok(shell) = std::fs::read_to_string(&retired) else {
            // Not a skip. If the replaced implementation is gone there is nothing
            // left to prove equivalence against, and that is worth failing over.
            panic!(
                "{} is gone; the only evidence for this port went with it",
                retired.display()
            );
        };

        // A layer is an array entry `"<script>:<artifact>:<description>"`; a
        // check is a top-level `"$PY" <script> [args]` that is executed rather
        // than echoed inside a failure hint.
        let mut ran: Vec<String> = Vec::new();
        let mut inside_layers = false;
        for line in shell.lines() {
            if line.starts_with("LAYERS=(") {
                inside_layers = true;
            } else if inside_layers && line.starts_with(')') {
                inside_layers = false;
            } else if inside_layers {
                if let Some(entry) = line.trim().strip_prefix('"') {
                    ran.push(entry.split(':').next().unwrap_or_default().to_string());
                }
            } else if let Some(rest) = line.strip_prefix("\"$PY\" ") {
                ran.push(rest.trim().trim_end_matches(" || {").to_string());
            }
        }

        // Shell step -> what now runs it, for the steps that stopped being Python.
        // CHG 2026-08-26: this list is nearly empty now. check_metric_scale_sanity.py
        // and check_lab_ledger.py went back to Python earlier today, and the six
        // repository lints followed -- model-name-collisions, model-variants,
        // lane-references, artifact-freshness, pending-patches and doc-citations
        // all match by their own script name again. What remains are the two
        // steps whose Rust name genuinely differs from the shell script's.
        const NATIVE: [(&str, &str); 2] = [
            ("scripts/check_program_files.py", "knowledge-programs"),
            ("scripts/corrections_digest.py --check", "knowledge-corrections"),
        ];
        const PACKAGED_ANALYSES: [(&str, &str); 11] = [
            (
                "paper_injection_audit.py",
                "research-compute-statistic injection-audit --python python",
            ),
            (
                "paper_main_table.py",
                "research-compute-statistic main-table --python python",
            ),
            (
                "paper_metric_disagreement.py",
                "research-compute-statistic metric-disagreement --python python",
            ),
            (
                "paper_real_sensor_statistics.py",
                "research-compute-statistic real-sensor-statistics --python python",
            ),
            (
                "paper_probabilistic_statistics.py",
                "research-compute-statistic probabilistic-statistics --python python",
            ),
            (
                "paper_holdout_confirmation.py",
                "research-compute-statistic holdout-confirmation --python python",
            ),
            (
                "paper_random_initial_condition_confirmation.py",
                "research-compute-statistic random-initial-condition-confirmation --python python",
            ),
            (
                "paper_clean_target_response.py",
                "research-compute-statistic clean-target-response --python python",
            ),
            (
                "paper_degradation_decomposition.py",
                "research-compute-statistic degradation-decomposition --python python",
            ),
            (
                "paper_two_dial_interaction.py",
                "research-compute-statistic two-dial-interaction --python python",
            ),
            (
                "paper_hard_switch_adaptation.py",
                "research-compute-statistic hard-switch-adaptation --python python",
            ),
        ];

        let steps = campaign_refresh_chain(
            "python",
            "fern-stat",
            "/receipted/fern-home",
            "/receipted/fern-registry",
        );
        let now: Vec<String> = steps.iter().map(|step| step.identity().join(" ")).collect();
        let mut missing: Vec<String> = Vec::new();
        for step in &ran {
            let expected = NATIVE
                .iter()
                .find(|(shell_step, _)| shell_step == step)
                .map(|(_, native)| (*native).to_string())
                .or_else(|| {
                    PACKAGED_ANALYSES
                        .iter()
                        .find(|(shell_step, _)| shell_step == step)
                        .map(|(_, packaged)| (*packaged).to_string())
                })
                .unwrap_or_else(|| step.clone());
            let expected = if expected.starts_with("research-compute-statistic ") {
                format!("{expected} --fern-home /receipted/fern-home")
            } else {
                expected
            };
            if !now.contains(&expected) {
                missing.push(format!("{step} (looked for {expected})"));
            }
        }
        assert!(
            ran.len() >= 24,
            "extracted only {} steps from the shell script",
            ran.len()
        );
        assert!(missing.is_empty(), "dropped by the port: {missing:#?}");
    }

    /// Only the two gates that guard published claims may stop the run.
    #[test]
    fn exactly_the_declared_gates_are_fatal() {
        let steps = campaign_refresh_chain(
            "python",
            "fern-stat",
            "/receipted/fern-home",
            "/receipted/fern-registry",
        );
        let fatal_checks: Vec<String> = steps
            .iter()
            .filter(|s| s.artifact.is_none() && s.on_failure == OnFailure::Stop)
            .map(|s| s.identity().remove(0))
            .collect();
        assert_eq!(
            fatal_checks,
            vec![
                "scripts/check_worker_imports_resolve.py",
                "scripts/check_paper_numbers.py",
                // CHG 2026-08-26: was "artifact-freshness" while the check was
                // a Rust subcommand. It is a Python script again; which gates
                // are fatal is unchanged, and that is what this test pins.
                "scripts/check_artifact_freshness.py",
            ]
        );
        // Every layer is fatal: a statistic that failed to recompute leaves its
        // old value on disk, which is worse than no value.
        //
        // One named exception, and it is an allowlist rather than a loosened
        // rule so a second one cannot arrive by accident. The figure wall is a
        // projection that votes on nothing; a broken gallery generator must not
        // stop the paper's numbers from being recomputed.
        let advisory_writers: Vec<String> = steps
            .iter()
            .filter(|s| s.artifact.is_some() && s.on_failure != OnFailure::Stop)
            .map(|s| s.identity().remove(0))
            .collect();
        assert_eq!(advisory_writers, vec!["scripts/build_figure_gallery.py"]);
    }
}
