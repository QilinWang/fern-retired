//! Integration tests for the run registry maintenance commands:
//! `list-pending-runs`, `prepare-resume-runs`, and `inspect-run-artifacts`.
//! All assertions target stable JSON fields
//! (schema/status/counts/flags), not display text.
//!
//! The sample_registration/write_json helpers are copied from
//! db_lifecycle_cli.rs — duplication between test files is deliberate so each
//! file stays self-contained.
//!
//! CHG 2026-08-28: `list-pending-runs`, `prepare-resume-runs` and
//! `inspect-run-artifacts` are now served by the C# host
//! (`csharp/Fern.Run.Cli`), so their two tests and every fixture helper they
//! needed were deleted with the Rust handlers. What stays is the absence
//! guard below, which is about retired spellings rather than about any verb.

use std::process::Command;

fn run_fern_capture(args: &[&str]) -> std::process::Output {
    Command::new(env!("CARGO_BIN_EXE_fern-run"))
        .args(args)
        .output()
        .unwrap()
}

#[test]
fn public_legacy_status_mutators_are_gone() {
    let retired = run_fern_capture(&["repair-legacy-run", "--help"]);
    assert!(!retired.status.success());
    assert!(String::from_utf8_lossy(&retired.stderr).contains("unrecognized subcommand"));

    for retired in ["mark-run-running", "complete-run", "mark-run-failed", "mark-run-stale"] {
        let output = run_fern_capture(&[retired, "--help"]);
        assert!(!output.status.success(), "retired command {retired} still exists");
        assert!(String::from_utf8_lossy(&output.stderr).contains("unrecognized subcommand"));
    }
}
