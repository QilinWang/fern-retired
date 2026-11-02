//! `fern_run` — run lifecycle and experiment orchestration.
//!
//! Scope:
//! - `db`: run registration, metrics, artifact lifecycle (SQLite via Diesel)
//! - `resolved_cfg` / `model_architecture`: config schema and identity hashing
//! - `results`: run artifacts
//!
//! Non-scope:
//! - data-plane transforms (`serie-rs`)
//! - dashboard/UI (`fern_dash/server.ts`, Bun/Elysia)
//! - sweep/recipe expansion and Python worker spawning (`fern-cli-rs` commands)

pub mod db;
// A binary deployment version is immutable operational input. Publishing it
// here keeps content addressing and on-disk durability out of the CLI, while
// Plan authority and collection policy remain separate registry concerns.
pub mod binary_deployment;
// Who owns a unit of work, and what counts as its result (migration 017). The
// file lock plus JSON receipt it replaces could not answer either question
// reliably; see the module header for the four failures that produced it.
pub mod lifecycle;
// Reconciliation verdicts for the resident scheduler: classify every
// unresolved Attempt as alive, a return awaiting registration, or truly
// dead. Read-only by design — execution reuses the existing typed
// operations, so no second mutation route exists.
// CHG 2026-08-28: `reconcile` retired to retired/fern_run/reconcile_2026_08_28.rs —
// no caller in any crate once fern-run reconcile moved to the C# host.
// The scheduler lease elects one dispatch/reconcile loop per Registry Home.
// It authorizes no plan step and no worker; those keep their own permits.
pub mod scheduler;
// An ordered Matrix hand-off is its own durable object: keeping it outside the
// Coordinate lifecycle prevents one giant state enum and gives the shell queue
// a single Fern-owned replacement.
pub mod model_architecture;
pub mod run_plan;
pub mod stored_json;
// Deterministic Stage Plan lowering from compiled Experiment cells and
// ablation paired units.
// A Stage Attempt is work under a Run Plan lease. It reuses that parent
// fencing epoch; it does not elect a second scheduler or hold artifacts.
pub mod stage_attempt;
// An Implementation record is immutable and content-addressed, so it can never
// be edited to say "do not use me". Whether it may still be given new work is
// therefore a separate row that may change while the record cannot.
// A Worker Site is one registered machine Registry Home may hand a Stage to.
// site_id is the execution profile id, so this registers profiles rather than
// inventing a second machine vocabulary. It records and reads; the claim
// transaction that consumes its eligibility check is a later slice.
pub mod worker_site;
// CHG 2026-08-28: artifact_retention retired to retired/fern_run/artifact_retention_2026_08_28.rs —
// no caller in any crate after the V1 matrix route retired; the C# Registry's
// ArtifactRetentionAuthority owns retention.
// Task authority writes through csharp/Fern.Registry TaskAuthority, invoked as
// the fern-registry subprocess. The Rust writer was retired on 2026-08-26 to
// retired/fern_run/src/task_kernel_2026_08_26.rs.
// Immutable model Architecture and Implementation Card writes go through
// csharp/Fern.Registry ModelCardAuthority, invoked as the fern-registry
// subprocess. The Rust writers were retired on 2026-08-27 under retired/.
// A full source snapshot surrounds the narrower numerical Implementation Card.
// Its archive/patch live in the content store; Registry Home owns the checked
// manifest and the append-only Matrix/Attempt provenance edges.
pub mod implementation_source_snapshot;
// Source bytes, implementation composition, and Research Design binding are
// three objects. Title and design never enter source-content identity.
pub mod csharp_experiment;
pub mod csharp_registry;
pub mod source_content;
// Evidence writes go through csharp/Fern.Registry EvidenceAuthority, invoked as
// the fern-registry subprocess. Freeze and the frozen reader are included.
// The Rust writer was retired on 2026-08-26 to
// retired/fern_run/src/evidence_registry_2026_08_26.rs.
// Contrast writes go through csharp/Fern.Registry ContrastAuthority, invoked as
// the fern-registry subprocess. The Rust writer was retired on 2026-08-26 to
// retired/fern_run/src/experiment_contrast_registry_2026_08_26.rs.
// V2 component cards become executable only after deterministic cfg lowering
// and the production Python model-link gate. Keeping that typestate here lets
// Plan registration accept one frozen object rather than mutable authoring.
pub mod experiment_v2;
pub mod resolved_cfg;
pub mod results;

use std::path::PathBuf;

/// DB-issued identity for one training run.
/// Returned by the C# `enroll-run-data` authority. Carries the db row id, run
/// hash, and artifact path.
#[derive(Debug, Clone, PartialEq, Eq, serde::Serialize, serde::Deserialize, schemars::JsonSchema)]
pub struct RunHandle {
    pub db_id: i64,
    pub run_hash: String,
    pub artifact_dir: PathBuf,
}

// CHG: the unused `RunStatus` enum is deleted — nothing in the workspace ever
// referenced it. Run status is deliberately a string vocabulary owned by db.rs;
// the enum was a second, dead vocabulary for the same concept.

impl RunHandle {
    pub fn new(db_id: i64, run_hash: impl Into<String>, artifact_dir: impl Into<PathBuf>) -> Self {
        Self {
            db_id,
            run_hash: run_hash.into(),
            artifact_dir: artifact_dir.into(),
        }
    }

    /// Ensure the artifact directory exists on disk. Called before the training
    /// loop starts writing files. (Folded in from the old results.rs RunHandle.)
    pub fn ensure_dir(&self) -> std::io::Result<()> {
        std::fs::create_dir_all(&self.artifact_dir)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn run_handle_keeps_db_id_and_path() {
        let handle = RunHandle::new(7, "abcd1234efef5678", "results/lorenz/run_0007");
        assert_eq!(handle.db_id, 7);
        assert_eq!(handle.run_hash, "abcd1234efef5678");
        assert_eq!(handle.artifact_dir, PathBuf::from("results/lorenz/run_0007"));
    }
}
