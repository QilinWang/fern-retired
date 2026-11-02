//! `fern-run` - Rust command surface for Fern run lifecycle work.
//!
//! Subcrate root reading sequence:
//!   CLI Pass 1  Catalog/config/path discovery.
//!   CLI Pass 2  Parent runners.
//!   CLI Pass 3  Artifact/postprocess one-shot wrappers.
//!   CLI Pass 4  Run registry views.
//!   CLI Pass 5  Analyses and lab citations.
//!   CLI Pass 6  Lifecycle and storage maintenance.
//!   CLI Pass 7  Human run-monitor front doors.
//!
//! CHG 2026-08-28: CLI Pass 8 (typed LLM clients over the public Fern
//! operations) is retired. Its eight files are preserved verbatim at
//! `retired/fern-cli-rs/llm_2026_08_28/`; the pass number is not reused.
//!
//! Workflow pins:
//!   - docs/workflow.md#fern-run-cli-command-waterfall
//!   - docs/meta_workflow.md
//!
//! Keep the module map, Command enum, and main dispatch in the same waterfall
//! order. If that order becomes hard to maintain, treat it as a refactor signal
//! rather than adding another loose command route.

// CLI Pass 1 - catalog, config, and path discovery.
mod binary_deploy;
mod build_config;
mod build_identity;
mod command_runtime;
mod experiment_front_door;
mod experiment_v2;
mod implementation_snapshot;
mod paths;
mod scheduler_service;
pub(crate) use fern_recipes as presets;

// CLI Pass 2 - parent runners.
mod cli_pass2_parent;
mod deferred_lane_projection;
mod plan_recipe_run;
mod plan_run;
// CHG 2026-08-28: python_training_worker retired whole with the matrix-worker role
// (retired/fern-cli-rs/python_training_worker_2026_08_28.rs); nothing in the operator
// crate referenced it.
mod run_sweep;
mod stage_plan_artifact;
#[cfg_attr(fern_role_operator, allow(dead_code))]
mod stage_plan_bundle;
// Campaign analysis commands live in fern-statistics. End-of-run writers live
// in fern_post so the worker can score artifacts without that crate. The CLI
// injects catalog lookup and prepared-bank operation at dispatch. The shared
// Pass 2 handoff stage itself remains in cli_pass2_parent.rs.
// CLI Pass 3 - artifact representation shared by the worker. Scientific
// postprocess execution is the Python `fern.postprocess` Stage.
mod cli_pass3_postprocess;
#[cfg(test)]
mod cli_defaults;

// CLI Pass 4 and Pass 6 - registry views plus lifecycle/storage maintenance.
// CLI Pass 5 keeps scientific proof reads; Knowledge outbox survey and
// acknowledgement moved to the private Go Registry Home boundary.
mod run_views;

// CLI Pass 6 support - worker-input writer. `run-experiment` mints the
// production training_worker_request bytes at registration; execute-matrix
// planning uses the same function. This does not spawn training.
mod write_python_training_worker_inputs;

use std::path::PathBuf;

#[cfg(test)]
use std::fs;

#[cfg(test)]
use clap::CommandFactory;
use clap::{Parser, Subcommand};
use fern_json_contract::JsonContract;
use schemars::JsonSchema;
use serde::Serialize;

pub(crate) use command_runtime::*;

/// Print `<command> failed: <error>` to stderr and exit nonzero on error.
/// Generic over the error: export-demo's errors are fern_post's non-Send
/// `Box<dyn Error>`, the rest use the crate-wide `Fallible`.
fn run_or_exit(command_name: &str, result: Result<(), impl std::fmt::Display>) {
    if let Err(e) = result {
        eprintln!("{command_name} failed: {e}");
        std::process::exit(1);
    }
}

/// Let statistics resolve a public recipe name without importing or copying
/// the CLI-owned catalog. This is the thin boundary Pass 3 leaves behind.
#[derive(Debug, Parser)]
#[command(name = "fern-run")]
#[command(about = "Rust command surface for Fern run utilities.")]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Debug, Subcommand)]
enum Command {
    // CHG 2026-08-28: the ablation pair is Rust's until the C# host has a public
    // compile and register command for an Ablation (Grok's correction; the
    // seventh cut had retired them with the experiment pair).
    /// Plan one paired ablation from a resolved Ablation: both arms, one shared Implementation.
    PlanAblation(experiment_v2::PlanAblation),
    /// Register one paired ablation Plan; the only V2 Ablation-registration entrypoint.
    RunAblation(experiment_v2::RunAblation),
    // CLI reading sequence (docs/workflow.md#fern-run-cli-command-waterfall):
    //   CLI Pass 1  Catalog/config/path discovery.
    //   CLI Pass 2  Parent runners.
    //   CLI Pass 3  Artifact/postprocess one-shot wrappers.
    //   CLI Pass 4  Run registry views.
    //   CLI Pass 5  Analyses/lab/proposals/memory.
    //   CLI Pass 6  Lifecycle and storage maintenance.
    //   CLI Pass 7  Human TUI front doors.
    //
    // Keep this enum in the same order as the dispatch below and docs/workflow.md.

    // CLI Pass 1 - catalog, config, and path discovery.

    // CLI Pass 2 - parent runners.

    // Transitional presentation commands with active callers. They leave
    // fern-run only when those callers move to their Python/tool owner.

    // CLI Pass 4 - run registry views.

    // CLI Pass 5 - analyses and lab citations.
    // CLI Pass 6 - lifecycle and storage maintenance.
}

#[derive(Debug, Serialize, JsonSchema)]
struct LanePathsReport {
    schema: &'static str,
    lane: String,
    db: PathBuf,
    artifact_root: PathBuf,
    work_root: PathBuf,
    note: &'static str,
}

impl JsonContract for LanePathsReport {
    const SCHEMA_ID: &'static str = "fern.lane_paths.v1";
}

fn main() {
    let cli = Cli::parse();
    match cli.command {
        // CLI Pass 1 - catalog, config, and path discovery.

        // CLI Pass 2 - parent runners.

        // CLI Pass 4 - run registry views.
        Command::PlanAblation(args) => run_or_exit("plan-ablation", experiment_v2::try_plan_ablation(args)),
        Command::RunAblation(args) => run_or_exit("run-ablation", experiment_v2::try_run_ablation(args)),

        // CLI Pass 5 - analyses and lab citations.
        // CLI Pass 6 - lifecycle and storage maintenance.
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pass_eight_intent_commands_are_public_and_legacy_spellings_are_absent() {
        let command = Cli::command();
        let visible = command
            .get_subcommands()
            .filter(|subcommand| !subcommand.is_hide_set())
            .map(|subcommand| subcommand.get_name())
            .collect::<std::collections::BTreeSet<_>>();
        // CHG 2026-08-26: "run" moved to the retired list below. dc8bb9d9
        // finished V2 materialization and retired V1 authoring, taking the
        // Run variant with it, and this list was not followed. The test has
        // been asserting that a command which no longer exists must be
        // public ever since.
        // CHG 2026-08-28: "resume", "monitor", "show-source" and
        // "list-experiment-contrasts" moved to the moved_to_the_csharp_host
        // list below. The C# host (csharp/Fern.Run.Cli) serves them now, so
        // asserting that Rust still publishes them would forbid the cut.
        // CHG 2026-08-28 (seventh pass): "plan-experiment" and
        // "run-experiment" followed; the C# host is the V2 launch route.
        // Nothing in this enum is a public intent command any more; the
        // loop stays as the place the next one would be asserted.
        // (The `for public in [...]` loop that held them is gone with its last
        // entry; `visible` is still checked by the assertions around it.)
        // Retired 2026-08-28 with NO successor anywhere: each had zero callers
        // in Python, Go, C#, shell, systemd, the Justfile, docs and agent
        // definitions. Unlike the two lists that follow, nothing else answers
        // their question, so each one's implementation is preserved verbatim
        // under `retired/fern-cli-rs/` rather than deleted -- "no caller" is
        // reachability evidence, not value evidence (correction 364).
        for retired_with_no_successor in [
            "llm",
            "worker-source",
            "show-fingerprint",
            "publish-implementation-source",
            "prepare-remote-attempt",
            "prepare-remote-return",
            // Owner decision 2026-08-28 evening: retire both; no caller reads the
            // contract index, and the demo export was reached only from a Justfile
            // recipe the owner ruled out of scope.
            "schema",
            "export-demo",
        ] {
            assert!(
                command.find_subcommand(retired_with_no_successor).is_none(),
                "{retired_with_no_successor} was retired to retired/fern-cli-rs/; restore it deliberately or not at all"
            );
        }
        // Served by the C# host since 2026-08-28. Unlike the retired list
        // below, these verbs are alive -- they are just not this binary's any
        // more, and a second Rust implementation would be the drift this
        // migration exists to remove.
        for moved_to_the_csharp_host in [
            "version",
            "show-source",
            "stop-scheduler",
            "resume",
            "cancel-matrix",
            "cancel-plan",
            "pause-scheduling",
            "release-scheduling-pause",
            "abandon-attempt",
            "declare-matrix",
            "explain",
            "explain-attempt",
            "monitor",
            "task",
            "enroll-run",
            "list-pending-runs",
            "prepare-resume-runs",
            "list-usable-runs",
            "audit-run-registry",
            "show-plan-definition",
            "show-run",
            "list-runs",
            "compare-runs",
            "open-run-campaign",
            "summarize-run-storage",
            "list-runs-to-slim",
            "inspect-run-artifacts",
            "protect-run",
            "unprotect-run",
            "slim-run-artifacts",
            "slim-run-artifacts-by-group",
            "delete-run-by-id",
            "discard-run",
            "show-lane-ownership",
            "show-cell-provenance",
            "audit-artifact-presence",
            "audit-artifact-retention",
            "verify-matrix-artifacts",
            "sign-matrix-artifacts",
            "relocate-scratch-evidence",
            "backfill-protocol-columns",
            "backfill-run-lifecycle",
            "list-experiment-contrasts",
            // Landed on the host 2026-08-28, after the first pass of this list.
            "estimate-stage",
            "show-attempt-metadata",
            "register-experiment-contrast",
            "accept-sweep",
            "freeze-source",
            // Landed on the host 2026-08-28, fourth pass. Goldens were recorded
            // from the Rust binary BEFORE each cut, at
            // csharp/Fern.Scheduler.Tests/Fixtures/rust-goldens/.
            "lower-stage-plan",
            "map-legacy-source-snapshots",
            "reclaim-storage",
            // Fifth pass: proved equivalent on the host, not byte-identical.
            "install-scheduler-service",
            "audit-run-artifact-storage",
            "site",
            "reconcile",
            // Seventh pass, 2026-08-28: the V2 launch route is the C# host's; Grok's
            // one-cell DLinear Plan completed on local-4070 with the worker request
            // minted in C# (csharp-dlinear-local-4070-20260828d).
            "plan-experiment",
            "run-experiment",
            // Sixth pass: the local preflight front door. `plan` was proved on
            // a real preset (paper-expansion-doublewell-h64/dlinear), `doctor`
            // and `profile validate` on local-4070; the report machinery they
            // rendered stays here because `run` still reaches it.
            "profile",
            "doctor",
            "plan",
            "probe-unsealed-returns",
            // `evidence verify` is the host's; the rest of the verb retired
            // with no caller (retired/fern-cli-rs/evidence_commands_2026_08_28.rs).
            "evidence",
            // Sixth pass. The module `binary_deploy` stays for the three
            // functions Rust still calls; only the verb moved. Its source is at
            // retired/fern-cli-rs/binary_deploy_2026_08_28.rs.
            "deploy",
        ] {
            assert!(
                command.find_subcommand(moved_to_the_csharp_host).is_none(),
                "{moved_to_the_csharp_host} is served by csharp/Fern.Run.Cli; Rust must not carry a second copy"
            );
        }
        for retired in [
            "run",
            "run-target",
            "run-sweep",
            "start-sweep",
            "resume-sweep",
            "show-sweep-status",
            "watch-sweep",
            "wait-sweep",
            "propose-recipe",
            "list-proposals",
            "approve-proposal",
            "reject-proposal",
            "record-agent-memory",
            "search-agent-memory",
            "list-llm-tools",
            "describe-llm-tool",
            "call-llm-tool",
            "propose-llm-tool",
            "execute-llm-proposal",
            "ask-openai-llm-tool",
            "run-agent-task",
            "list-agent-sessions",
            "show-agent-session",
            "list-recipes",
            "list-presets",
            "catalog",
            "list-recipe-run-targets",
            "build-config",
            "show-config-identity",
            "apply-config-overlays",
            "show-lane-paths",
            "build-artifact-dir",
            "plan-run",
        ] {
            assert!(
                command.find_subcommand(retired).is_none(),
                "{retired} was retired and must not remain as a hidden compatibility route"
            );
        }
        assert!(
            command.find_subcommand("execute-matrix").is_none(),
            "Matrix execution belongs only to fern-worker"
        );
        assert!(
            command.find_subcommand("execute-remote-attempt").is_none(),
            "Worker-Site execution belongs only to fern-worker"
        );
        assert!(
            command.find_subcommand("knowledge").is_none(),
            "Knowledge authoring and provenance belong only to fern-knowledge"
        );
        assert!(
            command.find_subcommand("dash").is_none(),
            "result dashboards belong only to fern_dash/server.ts"
        );
    }

    // RETIRED 2026-08-26: public_run_accepts_intent_and_profile_not_private_execution_fields.
    //
    // It inspected the `run` subcommand's arguments and asserted the split that
    // mattered when `run` was the public launcher: it must expose plan, stages,
    // after_matrices and on, and must NOT expose lane, recipe, preset, model,
    // limit, db, artifact_root, work_root, python, module, device,
    // max_restarts, restart_delay_seconds, max_target_seconds,
    // max_wall_seconds or retrain -- profile-owned plumbing that leaking onto a
    // public command would let a caller override the execution profile by hand.
    //
    // dc8bb9d9 retired V1 authoring and took the Run variant with it, so the
    // test has been panicking on `.expect("run command")` ever since. There is
    // no successor with the same contract: `run-experiment` registers a frozen
    // compiled object and legitimately takes --lane, which this test called
    // private.
    //
    // The RULE it protected is still worth having -- a public registration
    // command should not expose profile-owned execution fields -- and it now
    // has nowhere to live. Recorded here rather than deleted silently, because
    // the next person to add a public command should know the rule existed.

    // CHG 2026-08-28 (fourth cut pass): `automation_reads_have_structured_output_
    // and_maintenance_is_explicit` is gone with `reclaim-storage`, the last
    // automation read it checked. Its two rules — an automation read publishes
    // --format, storage removal is an explicit second call — now hold on the C#
    // host, where FernRunHostContractTests asserts them against
    // StorageReclaimVerbs. Nothing left in this enum is an automation read.

    /// The replace must be all-or-nothing and must not litter. A reader that
    /// globs the artifact directory has to see the finished receipt or the
    /// previous one, never a `.partial` scratch file.
    #[test]
    fn durable_write_replaces_whole_file_and_leaves_no_scratch() {
        let dir = std::env::temp_dir().join(format!("fern_durable_write_{}", std::process::id()));
        fs::create_dir_all(&dir).unwrap();
        let path = dir.join("receipt.json");

        write_file_durably(&path, b"{\"first\":1}\n").unwrap();
        assert_eq!(fs::read_to_string(&path).unwrap(), "{\"first\":1}\n");

        write_file_durably(&path, b"{\"second\":2}\n").unwrap();
        assert_eq!(fs::read_to_string(&path).unwrap(), "{\"second\":2}\n");

        let leftovers: Vec<_> = fs::read_dir(&dir)
            .unwrap()
            .map(|entry| entry.unwrap().file_name())
            .filter(|name| name.to_string_lossy().ends_with(".partial"))
            .collect();
        assert!(leftovers.is_empty(), "scratch files left behind: {leftovers:?}");
        fs::remove_dir_all(&dir).unwrap();
    }
}

#[cfg(test)]
mod cli_hygiene {
    use clap::CommandFactory;

    /// An argument that selects the data, the machine, or the identity may not
    /// carry a default. `crate::cli_defaults` (moved verbatim from the retired
    /// fern-statistics crate's `repo_health::cli_defaults` on 2026-08-28) holds the
    /// rule, the list, and the three failures on 2026-08-12 that produced it.
    #[test]
    fn no_scope_bearing_argument_carries_a_default() {
        let command = super::Cli::command();
        let found = crate::cli_defaults::defaulted_scope_arguments(&command);
        assert!(
            found.is_empty(),
            "{}",
            crate::cli_defaults::describe(&found)
        );
    }

    /// `--help` must not still advertise the retired registration as a launcher.
    ///
    /// A reader who is choosing a command reads this one line and nothing else.
    /// While it said "Register one declared Fern plan" beside `run-experiment`,
    /// the command surface offered two apparently equal ways to start work, and
    /// only one of them can say which program produced its numbers.
    #[test]
    fn the_retired_run_registration_does_not_read_as_a_launcher() {
        let command = super::Cli::command();
        assert!(
            command.get_subcommands().all(|sub| sub.get_name() != "run"),
            "the retired `run` registration must not return as a second launcher"
        );

        // CHG 2026-08-28 (seventh pass): the replacement the old refusal named,
        // `run-experiment`, is the C# host's now and no longer in this enum.
    }

    /// The retired V1 authoring spelling is absent, while the V2 replacement
    /// remains public. Existing V1 plans are still resumable and observable;
    /// only registration of a new string-authored Plan has disappeared.
    #[test]
    fn matrix_step_plan_registration_is_not_a_command() {
        use clap::Parser;
        let error = super::Cli::try_parse_from([
            "fern-run",
            "run",
            "--plan",
            "a-new-plan",
            "--stage",
            "step-0:some-lane:some-recipe:fern:28",
            "--on",
            "local-4070",
        ])
        .expect_err("the retired V1 registration command must not parse");
        assert_eq!(error.kind(), clap::error::ErrorKind::InvalidSubcommand);

    }
}
