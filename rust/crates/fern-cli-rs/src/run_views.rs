//! CLI Pass 4 and Pass 6 run-registry command implementations.
//!
//! Pass 4 is the read-only registry view surface: show one run, list filtered
//! runs, and compare completed metrics. Pass 6, later in this file, owns
//! lifecycle and artifact-storage maintenance.
//!
//! Workflow pins:
//! - docs/workflow.md#cli-pass-4---run-registry-views
//! - docs/workflow.md#cli-pass-6---lifecycle-and-storage-maintenance

use clap::ValueEnum;
use fern_json_contract::{ContractRegistration, JsonContract};

use schemars::JsonSchema;
use serde::Serialize;

use fern_run::db::RunRegistration;

macro_rules! json_report {
    ($name:ident, $schema_id:literal, { $($field:ident : $field_type:ty),* $(,)? }) => {
        #[derive(Debug, Serialize, JsonSchema)]
        struct $name {
            schema: &'static str,
            $($field: $field_type),*
        }

        impl JsonContract for $name {
            const SCHEMA_ID: &'static str = $schema_id;
        }
    };
}

json_report!(ArtifactStorageAuditReport, "fern.artifact_storage_audit.v1", {
    checked: usize,
    missing: usize,
    marked_db_metrics_only: usize,
    marked_stale: usize,
    staged_db_row_deletion: usize,
    purged_db_rows: usize,
    applied: bool,
});

#[derive(Debug, Clone, Copy, ValueEnum)]
pub enum OutputFormat {
    Text,
    Json,
}

// CLI Pass 4 argument structs - read-only registry views.

// CLI Pass 6 argument structs - lifecycle and storage maintenance.

// CLI Pass 4 - read-only registry views.
// Keep these first: a reader should meet the non-mutating inspection surface
// before the Pass 6 lifecycle and storage maintenance commands below.

// CLI Pass 6a/6b - explicit legacy import/repair plus storage maintenance.
// Normal worker state transitions have no public command here: the controlled
// Rust parent owns them. Historical fixtures use one named repair bridge that
// records actor and reason and never creates V3 authority or evidence.

// CLI Pass 6c - resume/reuse views over lifecycle rows.
// These read mutable lifecycle state, but do not mutate it.

// CLI Pass 6d - registry and artifact health inspection.
// These commands explain what exists and what is missing before any deletion or
// slimming command is allowed to act.

// CLI Pass 6e - protected storage and row maintenance.
// Deletions/slimming require explicit flags or DB policy choices; keep those
// gates visible rather than hiding them behind a generic cleanup command.

// Shared printers and artifact inspectors for Pass 4/6 commands.
// These stay below command owners so the file reads command -> display support.

