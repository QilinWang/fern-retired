//! RunData contracts and compatibility helpers.
//!
//! Registry creation/migration belongs to Go Registry Home; RunData admission,
//! mutation, and queries belong to C#. This Rust module retains typed transport
//! vocabulary and pure compatibility calculations while its callers evacuate.
//!
//! Python original → current owner mapping:
//!   get_db()         → fern-home registry-apply-migrations
//!   _init_schema()   → tools/fern-control/registry/migrations
//!   enroll()         → Fern.RunData C#    (transactional pending Run admission)
//!   mark_running()   → Fern.RunData C#    (durable running transition)
//!   complete()       → Fern.RunData C#    (terminal result + metrics)
//!   mark_failed()    → Fern.RunData C#    (terminal failure + detail)
//!   list_runs()      → Fern.RunData C#    (typed process read)
//!   compare()        → Fern.RunData C#    (GROUP BY model × sigma → avg metrics)
//!   slim_run()       → Fern.RunData C#    (DELETE heavy files + UPDATE artifacts)
//!   audit_run_artifact_storage()
//!                    → Fern.RunData C#       (audit missing artifact folders)

use std::path::{Path, PathBuf};


use chrono::Utc;
//     │
//     └─ Utc::now() gives us one canonical clock for started_at / finished_at.
//        We store timestamps as RFC3339 strings because that matches the old
//        Python DB and keeps SQLite dead simple: no custom datetime type, no
//        timezone ambiguity, and easy sorting/filtering from Rust or Python.

// The remaining compatibility row DTOs are typed JSON transport contracts.
// They do not open a database or own a query; C# RunData fills them across the
// process boundary.
#[cfg(test)]
use crate::RunHandle;

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Pass ②  DB lifecycle — Go Registry Home owns schema admission
//
// The retired Rust r2d2 pool, per-connection PRAGMAs, embedded migration
// runner, writer-wait constant, and their annotated teaching notes remain at
// `retired/fern_run/db_init_pool_migration_substrate.rs`. Every active test and
// production caller now invokes `fern-home registry-apply-migrations`; no Rust
// handle is exposed as a migration or transaction authority.

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Pass ③  Data shapes — the four structs
//
// This is where Rust's type system earns its keep. In Python, the DB
// layer has ONE row type for everything: sqlite3.Row. It behaves like
// a dict — you write row["mse"] and hope you didn't typo.
//
// Rust splits this into FOUR purpose-built structs:
//
//   READ side (what SELECT returns):
//     RunRow      — full row, used by list_runs, latest, best
//     CompareRow  — aggregated row, used by compare (GROUP BY)
//
//   WRITE side (what you give to INSERT/UPDATE):
//     RunRegistration — all fields for starting a new run
//     Metrics         — scalar results for finalizing a run (Pass ① §3)
//
//   FILTER (what you give to queries):
//     Run collection filters now belong to the Fern.RunData C# request.
//
// The key insight: column drift between schema and struct is now a
// COMPILE-TIME error. If you add a column to the SQL but forget the
// struct field (or vice versa), the code won't build.
//
// Python failure mode: you add a column to _SCHEMA_V1, forget to
// update some query, and the bug only shows up at RUNTIME when that
// query runs and the column name doesn't match. Could be weeks later.
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

// ──────────────────────────────────────────────────────────────────────
// §9  RunRow — the full row, read side
//
// Used by: list_runs(), latest(), best() — any query returning SELECT *
// Python twin: sqlite3.Row (the generic dict-like row object)
//
// This struct is the direct mirror of the SQL schema. Every column
// in the `runs` table has a matching field here. The derive macros
// do the heavy lifting:
//
//   FromRow   — sqlx maps SQL columns → struct fields by name
//   Serialize — serde converts the struct → JSON for the API
//   Debug     — println!("{:?}", row) works (for logging)
//   Clone     — row.clone() works (needed for some Axum response patterns)
// ──────────────────────────────────────────────────────────────────────

// ─────────────────────── ORIGINAL (sqlx) — commented during migration ────────
// #[derive(Debug, Clone, FromRow, serde::Serialize, serde::Deserialize)]
//   FromRow: sqlx fills the struct from a SQL row, matching columns to fields by
//   name (`id: row.get("id")`, ...); a missing name or wrong type is a compile error.
//   (full annotated original preserved in retired/fern_run/db.rs)
//
// ─────────────────────── CURRENT (typed JSON boundary) ────────────────────────
// C# RunData owns the SELECT and serializes this stable response shape. Rust
// keeps the explicit fields so CLI/report callers cannot fall back to a
// shapeless map. The retired Diesel mapping markers are preserved in
// `retired/fern_run/db_run_row_diesel_mapping.rs`.
#[rustfmt::skip]
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize, schemars::JsonSchema)]

pub struct RunRow {
    pub id: i64,
    //       │
    //       └─ i64: 64-bit signed integer. SQLite INTEGER PRIMARY KEY is always 64-bit.
    //          Python: int (unbounded). Rust: exactly 8 bytes, range ±9.2 × 10^18.
    //          You'll never hit the limit for a run counter.
    pub artifact_dir: Option<String>,
    pub dataset: String,
    //           │
    //           └─ String (not Option): TEXT NOT NULL in the schema.
    //              The compiler enforces this. If a query somehow returned NULL
    pub dataset_id: Option<String>,
    pub model_type: String,

    pub seed: i64,
    pub pred_len: i64,
    pub seq_len: i64,

    pub noise_sigma: f64,
    //               └─ f64 (not Option<f64>): REAL DEFAULT 0.0 in the schema.
    pub trajectory_draw_ordinal: i64,
    pub noise_draw_ordinal: i64,
    pub trajectory_seed_base: i64,
    pub noise_seed_base: i64,
    pub trajectory_draw_seed: i64,
    pub noise_draw_seed: i64,

    pub config_json: Option<String>,
    pub config_hash: Option<String>,
    pub invocation_json: Option<String>,
    pub run_hash: Option<String>,
    pub success_hash: Option<String>,
    pub config_name: Option<String>,
    pub recipe_name: Option<String>,
    pub recipe_hash: Option<String>,
    pub run_note: Option<String>,
    pub git_sha: Option<String>,
    pub status: String,
    //          │
    //          └─ The state machine value: "pending", "running", "done", or "failed".
    //             This is a String, not an enum, because:
    //               1. SQLite stores it as TEXT
    //               2. FromRow needs to deserialize it from TEXT
    //               3. Adding states doesn't require a schema migration
    //
    //             A more "Rust-native" approach would be an enum with
    //             serde + sqlx custom deserialization, but for 4 states,
    //             the String is simpler and matches the Python version exactly.
    pub started_at: Option<String>,
    //              │
    //              └─ Option<String> because a 'pending' run hasn't started yet.
    //                 These are RFC3339 timestamp strings, not chrono types.
    //                 Keeping them as String means:
    //                   • SELECT * → struct doesn't need chrono deserialization
    //                   • JSON API returns the string as-is (the dashboard parses it)
    //                   • Sorting in SQL still works (RFC3339 sorts lexicographically)
    pub finished_at: Option<String>,
    pub error_msg: Option<String>,
    //              │
    //              └─ Only populated when status = "failed".
    //                 "OOM at epoch 5", "NaN in loss at step 1200", etc.
    //                 Queryable: WHERE error_msg LIKE '%OOM%'
    pub source: String,    // "user_defined" | "template" | "backfill" | "import_csv"
    pub artifacts: String, // "full" | "slim" | "none"

    pub pinned: i64,
    //          └─ i64, not bool. SQLite has no boolean type.
    //             0 = not pinned, 1 = pinned.
    //             Could be a Rust bool with a custom sqlx decoder, but i64
    //             matches the schema literally and avoids a conversion layer.
    pub mse: Option<f64>,
    //       └─ Option<f64>: nullable REAL. NULL when:
    //            • Run is still 'pending' or 'running' (metrics not computed yet)
    //            • Run 'failed' (never got to evaluation)
    //            • This metric isn't applicable for this model type
    pub mae: Option<f64>,
    pub crps: Option<f64>,
    pub swd: Option<f64>,
    pub ept: Option<f64>,
    pub clean_target_mse: Option<f64>,
    //                     └─ migration 022, and the five below it: what `mse`
    //                        was made of, plus what the same windows score
    //                        with no model. NULL for rows written before the
    //                        column, and for any run whose window layout the
    //                        worker could not verify. Never defaulted to 0 —
    //                        the overlap term reaches 0 for real.
    pub injected_target_noise_mse: Option<f64>,
    pub noise_forecast_overlap: Option<f64>,
    pub baseline_constant_mse: Option<f64>,
    pub baseline_context_mean_mse: Option<f64>,
    pub baseline_last_value_mse: Option<f64>,
    pub template_id: Option<i64>,
    pub n_features: Option<i64>,
    // v6: dropped artifact_state/bytes + 13 has_*/can_* columns (no consumers).
    pub code_fingerprint_json: Option<String>,
    //                          └─ v7 provenance, OUTSIDE run identity: content
    //                             hashes of the load-bearing root Python files
    //                             plus git dirty state, recorded at enroll.
    //                             NULL for pre-v7 rows and manual enrollments.
    pub target_hash: Option<String>,
    //                └─ v8: which DESIGNED recipe cell this run executed
    //                   (recipe_targets identity). NULL for pre-v8 rows and
    //                   standalone runs — no recipe, no designed target.
    //                   Resume keys on it: "has this designed cell been
    //                   completed by any run", checked BEFORE enroll so the
    //                   crash-retry UPSERT never resets a done row.
}

/// The stable, read-only subset needed by the terminal dashboard.
///
/// A dashboard is an observer, so opening an older campaign database must not
/// run migrations merely to satisfy columns the screen never displays. This
/// projection names only fields present in the original runs table (with the
/// migration-005 `run_hash` rename handled explicitly by the reader below).
#[derive(Debug, Clone, serde::Deserialize)]
pub struct DashboardRunRow {
    pub id: i64,
    pub artifact_dir: Option<String>,
    pub dataset: String,
    pub model_type: String,
    pub seed: i64,
    pub pred_len: i64,
    pub seq_len: i64,
    pub noise_sigma: f64,
    pub run_hash: Option<String>,
    pub status: String,
    pub finished_at: Option<String>,
    pub source: String,
    pub mse: Option<f64>,
    pub mae: Option<f64>,
    pub crps: Option<f64>,
    pub swd: Option<f64>,
    pub ept: Option<f64>,
}

// ──────────────────────────────────────────────────────────────────────
// §10  CompareRow — the aggregated row
//
// Used by: the typed C# comparison response returned to Rust
// Python twin: same sqlite3.Row, just different columns
//
// The comparison projection doesn't return full runs. It returns one row per
// (model_type, noise_sigma) pair with averaged metrics across seeds.
// So it needs its OWN struct — you can't reuse RunRow because the
// columns are different (avg_mse instead of mse, n_seeds instead of seed).
//
// In Python, this wasn't a problem — sqlite3.Row is shapeless, it
// holds whatever columns the query returns. But that flexibility is
// exactly what causes bugs: nothing stops you from writing
// row["mse"] on a compare() result (which has "avg_mse", not "mse").
// You'd get a KeyError at runtime.
//
// In Rust, that's a compile error: CompareRow has no field named `mse`.
// ──────────────────────────────────────────────────────────────────────

// ─────────────────────── ORIGINAL (sqlx) — commented during migration ────────
// #[derive(Debug, Clone, FromRow, serde::Serialize)]
//
// ─────────────────────── NEW (diesel) ────────────────────────────────────────
// Queryable only — no Selectable/table_name. CompareRow is a GROUP BY aggregate
// (avg_mse, n_seeds, ...), not a runs row, so it maps a custom select result by
// position rather than from a table. Serde crosses the typed C# reader process
// boundary and preserves the existing Rust JSON response.
#[rustfmt::skip]
// CHG 2026-08-28: `compare-run-data` is the sole SQL projection owner. The
// historical Diesel teaching notes above stay readable, but this response
// record now participates only in the typed C# JSON boundary.
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize, schemars::JsonSchema)]
pub struct CompareRow {
    pub model_type: String,
    pub noise_sigma: f64,

    pub n_seeds: i64,
    //           │
    //           └─ count(*) aliased as n_seeds in the SQL.
    //              "How many seeds were averaged for this (model, sigma) cell?"
    //              e.g., if you ran fern with seeds 42, 43, 44 on lorenz at σ=0.1,
    //              n_seeds = 3.
    //
    //              The field name MUST match the SQL alias. If the SQL says
    //              `count(*) as n_seeds`, this field must be `n_seeds`.
    //              Rename either one without the other → compile error from FromRow.
    pub avg_mse: Option<f64>,
    //              Note the naming: avg_mse, not mse. This struct and RunRow
    //              are completely separate types. You can't accidentally pass
    //              a CompareRow to a function expecting a RunRow — the compiler
    //              rejects it. Python's sqlite3.Row would let you mix them up.
    pub avg_mae: Option<f64>,
    pub avg_crps: Option<f64>,
    pub avg_swd: Option<f64>,
    pub avg_ept: Option<f64>,
}

// ──────────────────────────────────────────────────────────────────────
// §11  RunRegistration — the write-side contract
//
// Used by: the typed C# RunData enrollment transaction through enroll().
// Python source idea: register_run()'s old kwargs scattered across
//              RunId, DoseLabel, cfg, and standalone-run parameters
//
// This is where the Python → Rust collapse is most dramatic.
//
// Python's former direct-write signature:
//   def register_run(db, run_id, *, dose=None, cfg=None,
//                    template_id=None,
//                    dataset_base=None, dataset_id=None,
//                    source="user_defined", artifact_dir=...)
//
// Inside, it unpacks run_id.dataset, run_id.model_type, run_id.seed,
// dose.trajectory_draw_ordinal, dose.noise_level, cfg → json, etc. Five objects get
// flattened into SQL columns.
//
// Rust: one struct. All fields explicit. No unpacking, no
// `if dose else default`, no scattered identity across 3 objects.
// Miss a field → compile error.
// ──────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, serde::Serialize, serde::Deserialize, schemars::JsonSchema)]
#[serde(deny_unknown_fields)]
//                   This is the "separate read and write shapes" principle:
//                     RunRegistration = what you give to start a run
//                     Metrics         = what you give to finish a run
//                     RunRow          = what you get back from queries

pub struct RunRegistration {
    /// Versioned wire contract. The default keeps pre-contract handoff files
    /// readable while every newly serialized registration carries the tag.
    #[serde(default = "run_registration_schema")]
    pub schema: String,
    pub dataset: String,
    //           │
    //           └─ String (owned), not &str (borrowed).
    //              The registration struct OWNS its data. It might outlive
    //              the scope that created it (e.g., stored in a queue, passed
    //              to another thread). Owned data = no lifetime worries.
    //
    //              Rule of thumb for struct fields:
    //                • String, PathBuf, Vec<T>  → owned, struct owns the data
    //                • &str, &Path, &[T]        → borrowed, struct depends on someone else
    //
    //              For long-lived structs (stored, queued, passed around), use owned.
    //              For short-lived function parameters, borrow with &.
    pub dataset_id: String,
    //              │
    //              └─ Note: String here, but Option<String> in RunRow.
    //                 The registration ALWAYS provides a dataset_id.
    //                 But old backfilled rows in the DB might have NULL.
    //                 The read shape (RunRow) reflects the DB reality (nullable).
    //                 The write shape (RunRegistration) reflects the API contract
    //                 (you must provide this when registering a new run).
    pub model_type: String,
    pub seed: i64,
    pub pred_len: i64,
    pub seq_len: i64,
    pub noise_sigma: f64,

    pub trajectory_draw_ordinal: i64,
    pub noise_draw_ordinal: i64,

    pub trajectory_seed_base: i64,
    pub noise_seed_base: i64,
    pub trajectory_draw_seed: i64,
    pub noise_draw_seed: i64,

    pub config_json: Option<String>,
    //               │
    //               └─ Option because quick standalone runs might not have a
    //                  full config snapshot. The training loop hasn't started
    //                  yet when we register, so sometimes config isn't ready.
    //
    //                  In the SQL, the UPSERT uses COALESCE:
    //                    config_json = COALESCE(excluded.config_json, runs.config_json)
    //                  This means "use the new value if provided (Some), otherwise
    //                  keep the existing value." So re-enrolling a crashed run
    //                  without config won't erase the config from the first attempt.
    pub config_hash: Option<String>,

    pub git_sha: String,
    //           │
    //           └─ String, not Option. The C# enrollment authority captures
    //              the live git SHA (or an empty string when unavailable), so
    //              a committed row always has SOME value, even if it's "".
    #[serde(default)]
    pub code_fingerprint_json: Option<String>,
    //  │
    //  └─ v7 provenance (decision 2026-07-01-02): what code actually executes
    //     this run — file-content hashes of the load-bearing root Python files
    //     plus git dirty state, minted by the fern-cli-rs parent. NEVER hashed
    //     into run_hash; enroll compares it across re-enrolls and warns loudly
    //     on "same identity, different executable contents".
    //     #[serde(default)] keeps pre-v7 registration.json files readable.
    pub template_id: Option<i64>, // which sweep spawned this? None for direct runs
    pub source: String,           // "user_defined" | "template" | "backfill"
    pub config_name: Option<String>,
    pub recipe_name: Option<String>,
    pub recipe_hash: Option<String>,
    #[serde(default)]
    pub target_hash: Option<String>,
    //  │
    //  └─ v8: identity of the DESIGNED recipe cell (recipe_targets row) this
    //     run executes. None for standalone runs — no recipe, no designed
    //     target. Derived from content already inside run identity, so it is
    //     NEVER hashed into run_hash. #[serde(default)] keeps pre-v8
    //     registration.json files readable.
    pub run_note: Option<String>,
    pub invocation_json: Option<String>,
    pub run_hash: String,
    pub n_features: Option<i64>,

    pub artifact_dir: PathBuf,
    //                │
    //                └─ PathBuf: owned path. The caller computes this before
    //                   DB registration. That keeps CLI/orchestrator path policy
    //                   outside the DB layer.
    //
    //                   The DB stores this exact path in runs.artifact_dir.
    //                   It does not derive the path from dataset/model/recipe.
    //
    //                   Python: str(artifact_dir) before binding to SQLite.
    //                   Rust: PathBuf in the write contract, converted to text
    //                   only at the SQL boundary.
}

fn run_registration_schema() -> String {
    RunRegistration::SCHEMA_ID.to_string()
}

impl RunRegistration {
    pub const SCHEMA_ID: &'static str = "fern.run_registration.v2";
}

impl fern_json_contract::JsonContract for RunRegistration {
    const SCHEMA_ID: &'static str = Self::SCHEMA_ID;
}

/// Scalar metrics written at finalize time.
#[derive(Debug, Clone, Default, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct Metrics {
    // Final scalar metrics are optional because not every trainer will emit all
    // of them. `Option<f64>` maps directly to nullable SQLite columns, so we
    // avoid sentinel values like -1.0 or NaN in the DB.
    pub mse: Option<f64>,
    pub mae: Option<f64>,
    pub crps: Option<f64>,
    pub swd: Option<f64>,
    pub ept: Option<f64>,
    // Migration 022. What `mse` was made of, and what it would have been with
    // no model. `mse` scores against the DOSED observation, so a worse model
    // and a noisier target give the same number; these separate them, as the
    // exact identity mse = clean_target + injected + overlap. None means the
    // worker could not verify its window layout against the tensors the run
    // was scored on -- never that the value was zero, which the overlap term
    // genuinely reaches.
    pub clean_target_mse: Option<f64>,
    pub injected_target_noise_mse: Option<f64>,
    pub noise_forecast_overlap: Option<f64>,
    pub baseline_constant_mse: Option<f64>,
    pub baseline_context_mean_mse: Option<f64>,
    pub baseline_last_value_mse: Option<f64>,
}

// The complete pending-Run INSERT/UPSERT, config preimage and enrolled-event
// transaction is now C# RunData authority. The exact former Diesel body lives
// intact at retired/fern_run/enrollment_diesel_2026_08_28.rs.

/// The scientific protocol, lifted out of the invocation packet into columns.
///
/// WHY THIS FUNCTION EXISTS AT ALL. The protocol has always been in
/// `invocation_json`, and it is part of what `run_hash` covers, so two protocols
/// at one cell can never be confused as runs. What was missing is the other
/// half: a hash cannot be used to ASK for something. Nothing could express
/// "every gaussian, noise-matched row", so fifteen analysis scripts each parsed
/// this JSON by hand and the ones that forgot silently reported another
/// estimand's numbers.
///
/// Derived HERE, at the moment of the write, and by the same function that
/// backfills old rows -- so the columns and the JSON they came from cannot drift
/// apart. Nothing is invented: if a field is absent, its column is NULL.
#[derive(Debug, Default, Clone, PartialEq)]
pub struct ProtocolColumns {
    pub noise_law: Option<String>,
    pub split_treatment_scheme: Option<String>,
    pub noise_ar1_phi: Option<f64>,
    pub relative_dose: Option<bool>,
    pub checkpoint_execution_mode: Option<String>,
    /// Migration 016. Where the run executed and whether it fitted anything.
    /// Both are hashed and both were unselectable; device separates the cpu
    /// canary from the cuda lane at identical Lorenz cells.
    pub runtime_device: Option<String>,
    pub execution_kind: Option<String>,
}

/// A name for this machine that stays useful when hostnames repeat.
///
/// Host alone is not enough -- two rented pods answer to the same hostname often
/// enough that "which machine produced this number" would still be unanswerable.
/// Host plus accelerator is, and both are already knowable here.
///
/// Computed once per process. The accelerator query is a subprocess, which is
/// nothing beside a training run but is not worth repeating per row; a machine
/// does not grow a different GPU while a sweep is running.
pub fn machine_alias() -> String {
    use std::sync::OnceLock;
    static ALIAS: OnceLock<String> = OnceLock::new();
    ALIAS
        .get_or_init(|| {
            let host = std::env::var("HOSTNAME")
                .ok()
                .filter(|name| !name.trim().is_empty())
                .or_else(|| {
                    std::fs::read_to_string("/proc/sys/kernel/hostname")
                        .ok()
                        .map(|name| name.trim().to_string())
                        .filter(|name| !name.is_empty())
                })
                .unwrap_or_else(|| "unknown-host".to_string());
            let accelerator = std::process::Command::new("nvidia-smi")
                .args(["--query-gpu=name", "--format=csv,noheader"])
                .output()
                .ok()
                .filter(|output| output.status.success())
                .and_then(|output| {
                    String::from_utf8(output.stdout)
                        .ok()
                        .and_then(|text| text.lines().next().map(str::to_string))
                })
                .unwrap_or_else(|| "cpu".to_string());
            format!("{}-{}", normalize_alias_part(&host), normalize_alias_part(&accelerator))
        })
        .clone()
}

/// Lowercase, hyphenated, with the vendor noise dropped so the model shows.
///
/// "NVIDIA GeForce RTX 4070 Laptop GPU" -> "geforce-rtx-4070-laptop". The vendor
/// and the word GPU carry no information here: every accelerator in this
/// campaign is an NVIDIA GPU, so keeping them would only lengthen every row.
fn normalize_alias_part(raw: &str) -> String {
    let trimmed = raw
        .trim()
        .trim_start_matches("NVIDIA ")
        .trim_end_matches(" GPU")
        .to_lowercase();
    let cleaned: String = trimmed
        .chars()
        .map(|c| if c.is_ascii_alphanumeric() { c } else { '-' })
        .collect();
    cleaned
        .split('-')
        .filter(|part| !part.is_empty())
        .collect::<Vec<_>>()
        .join("-")
}

pub fn protocol_columns_from_invocation(invocation_json: Option<&str>) -> ProtocolColumns {
    let Some(text) = invocation_json else {
        return ProtocolColumns::default();
    };
    let Ok(packet) = serde_json::from_str::<serde_json::Value>(text) else {
        return ProtocolColumns::default();
    };
    // The execution facts sit under identity.config, beside rather than inside
    // the protocol, and are read even when a packet carries no protocol block.
    let text_pointer = |pointer: &str| {
        packet
            .pointer(pointer)
            .and_then(serde_json::Value::as_str)
            .map(str::to_string)
    };
    let runtime_device = text_pointer("/identity/config/runtime/device");
    let execution_kind = text_pointer("/identity/config/execution/kind");

    let Some(protocol) = packet.pointer("/identity/context/protocol") else {
        return ProtocolColumns {
            runtime_device,
            execution_kind,
            ..ProtocolColumns::default()
        };
    };
    let text_at = |key: &str| {
        protocol
            .get(key)
            .and_then(serde_json::Value::as_str)
            .map(str::to_string)
    };
    ProtocolColumns {
        runtime_device,
        execution_kind,
        noise_law: text_at("noise_law"),
        split_treatment_scheme: text_at("split_treatment_scheme"),
        // The packet omits this field when it is zero, so an absent key inside a
        // protocol block means iid, not unknown. Writing 0.0 here is what makes
        // "iid" answerable by a query instead of by knowing that rule.
        noise_ar1_phi: Some(
            protocol
                .get("noise_ar1_phi")
                .and_then(serde_json::Value::as_f64)
                .unwrap_or(0.0),
        ),
        relative_dose: protocol.get("relative_dose").and_then(serde_json::Value::as_bool),
        checkpoint_execution_mode: protocol
            .pointer("/checkpoint_execution/mode")
            .and_then(serde_json::Value::as_str)
            .map(str::to_string),
    }
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Pass ④  Write path — terminal-state helpers
//
// A run follows the registry lifecycle:
//
//   enroll()       → status = 'pending'   (pass ⑤, the UPSERT)
//   mark_running() → status = 'running'
//         ├── complete()     → status = 'done'   + metrics
//         └── mark_failed()  → status = 'failed' + error_msg
//
// This pass covers helpers shared by the terminal updates. The interesting
// beginning (the INSERT ... ON CONFLICT) is pass ⑤.
//
// Active code keys state transitions by run_hash. The db row id still exists
// for display and artifact maintenance, but it is not the lifecycle identity.
//
//   let handle = enroll(&database_path, &reg)?;  // ← pending row
//   mark_running(&mut conn, &handle.run_hash)?;
//   // ... train for 100 epochs ...
//   complete(&mut conn, &handle.run_hash, &metrics)?;
//
// Or if training crashes:
//   mark_failed(&mut conn, &handle.run_hash, "OOM at epoch 50")?;
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

// ──────────────────────────────────────────────────────────────────────
// §13b  Sidebar: query() vs query!() — runtime vs compile-time SQL
//
// We used sqlx::query() above (runtime). sqlx also has sqlx::query!()
// (compile-time). The difference:
//
//   sqlx::query("UPDATE runs SET mse=? WHERE id=?")
//       .bind(metrics.mse)
//       .bind(db_id)
//       .execute(pool).await?;
//
//   sqlx::query!("UPDATE runs SET mse = $1 WHERE id = $2",
//       metrics.mse,       // $1
//       db_id              // $2
//   )
//       .execute(pool).await?;
//
// What query!() buys you:
//   • Named parameters ($1, $2) — no positional counting
//   • The compiler connects to your REAL database at build time,
//     parses the SQL, checks that column names exist, checks that
//     the Rust types match the SQL types. Wrong column name?
//     Won't compile. Bind a String to an INTEGER column? Won't compile.
//   • For SELECT queries, it auto-generates the return struct —
//     you don't even need RunRow or CompareRow.
//
// What it costs:
//   • You need DATABASE_URL set at compile time pointing to a real
//     (or template) .db file with the schema already applied.
//     `cargo build` literally opens SQLite and runs EXPLAIN on your SQL.
//   • Dynamic SQL (building WHERE clauses at runtime, like list_runs)
//     doesn't work — the SQL must be a string LITERAL, not a variable.
//   • The generated types are anonymous. If you want to pass them
//     across function boundaries or serialize to JSON, you still need
//     named structs and query_as!() instead.
//
// Verdict for Fern:
//   • complete, mark_failed, pin_run — fixed SQL, perfect for query!()
//   • enroll — owned by the typed C# RunData boundary
//   • list_runs — DYNAMIC SQL, must stay with query()
//   • best — dynamic column name, must stay with query()
//   • compare, latest — could go either way
//
// The migration path: start with query() everywhere (works, readable,
// matches the Python version). Switch individual functions to query!()
// once DATABASE_URL is set up in your build environment. No rush —
// the runtime version is not unsafe, just less checked.
//
// To enable query!(), add to .env or export:
//   DATABASE_URL=sqlite:results/fern.db
//
// Then complete() could become:
//
//   sqlx::query!(
//       "UPDATE runs SET status='done', finished_at=$1, \
//        mse=$2, mae=$3, crps=$4, swd=$5, ept=$6 WHERE id=$7",
//       now, metrics.mse, metrics.mae, metrics.crps,
//       metrics.swd, metrics.ept, db_id
//   )
//   .execute(pool).await?;
//
// Same SQL, but the compiler verifies it against the real schema.
// ──────────────────────────────────────────────────────────────────────

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// 6. list_runs  (was: list_runs)
//
// Python: dynamic WHERE clause built from optional kwargs.
// Rust: same pattern — push clauses + params conditionally.
//       sqlx doesn't support dynamic bind count easily, so we use
//       query_as with a fixed query per filter combination.
//       For simplicity: build SQL string, bind via sqlx::query_as.
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
/*
dataset    = "lorenz"                    ← stable base name, for queries
dataset_id = "lorenz_traj0_noise1"           ← full pipeline string, for provenance
dataset is the species name. Every run on the Lorenz system has dataset = "lorenz",
regardless of how the data was generated.
This is what you filter on:
SELECT * FROM runs WHERE dataset = 'lorenz'   -- gives you ALL lorenz runs
```

`dataset_id` is the **specimen tag**. It encodes the full generation pipeline — which ODE replication, which noise injection, etc. Two runs on the same attractor with different noise seeds have the same `dataset` but different `dataset_id`:
```
Run A:  dataset = "lorenz",  dataset_id = "lorenz_traj0_nr0"
Run B:  dataset = "lorenz",  dataset_id = "lorenz_traj0_noise1"
Run C:  dataset = "lorenz",  dataset_id = "lorenz_ode2_nr0"
*/
// ──────────────────────────────────────────────────────────────────────
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Pass ⑤  Enrollment boundary — C# RunData transaction
//
// C# owns the complete enrollment transaction. It derives the live machine,
// git, model/config and protocol fields, writes the config preimage and Run,
// then appends the enrolled event before it returns a RunHandle.
//
// Its INSERT ... ON CONFLICT(run_hash) DO UPDATE is one immediate transaction.
//
// Why upsert instead of plain INSERT?
//   Scenario: you start training lorenz_fern_s42_p336. It crashes at
//   epoch 50. You restart. Without upsert:
//     • Plain INSERT → UNIQUE constraint violation (run_hash exists)
//     • DELETE then INSERT → loses the original started_at, config, etc.
//     • Check-then-insert → race condition in concurrent scenarios
//
//   With upsert:
//     • Same artifact_dir → update the existing row
//     • Reset status to the caller's lifecycle state, clear error_msg,
//       refresh timestamps
//     • COALESCE preserves metadata from the first attempt if the
//       retry doesn't provide it (config_json, config_hash, etc.)
//     • One atomic SQL statement, no race conditions
//
// Python source idea: register_run() in fr_db.py — same SQL, but the input
// comes from 3 objects (RunId, DoseLabel, cfg) plus 6 kwargs.
// Rust: one &RunRegistration struct.
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

// ──────────────────────────────────────────────────────────────────────
// §15  enroll — "create or refresh the DB row for this run"
//
// Called by: the orchestrator before training starts
// Returns: RunHandle { db_id, artifact_dir }
//   • db_id → row identifier for display and artifact maintenance
//   • artifact_dir → where the trainer writes model weights, sequences, etc.
//
// The training loop holds the run_hash and artifact_dir for its lifetime:
//   let handle = enroll(&database_path, &reg)?;
//   // handle.artifact_dir → write model.safetensors, sequence_tensors.safetensors, etc. here
//   // ... train for 100 epochs ...
//   complete(&mut conn, &handle.run_hash, &metrics)?;
// ──────────────────────────────────────────────────────────────────────

// ════════════════════════════════════════════════════════════════════════════
// Pass ⑤ is the C# RunData enrollment transaction that makes crash-retry
// idempotent. Production callers cross `enroll-run-data` directly;
// `open_campaign` allocates the next template_id;
// the Python-training parent calls the C# invocation writer after the row exists.
// Shared explanation for the pass.
// ════════════════════════════════════════════════════════════════════════════

// ─────────────────────── ORIGINAL (sqlx) ─────────────────────────────────────
// pub async fn open_campaign(pool) -> Result<i64, sqlx::Error> {
//     sqlx::query_scalar("SELECT COALESCE(MAX(template_id), 0) + 1 FROM runs").fetch_one(pool).await
// }
// ─────────────────────── NEW (diesel) ────────────────────────────────────────
/// Which machine an EXISTING run was computed on, from its own receipt.
///
/// Not inferred from the lane directory. A directory is named after how a run
/// was scheduled, not where it executed, and reading `runpod` out of a folder
/// name is the same mistake as reading `canary` out of one -- it re-encodes a
/// guess as a fact. `execution_environment.json` is evidence: the worker wrote
/// it on the machine that ran.
///
/// It records the accelerator and the kernel, never a hostname, so the alias
/// recovered here is `unknown-host-<accelerator>`. That is less than a fresh run
/// records and it is what actually exists; claiming more would be inventing it.
pub fn machine_alias_from_receipt(artifact_dir: &std::path::Path) -> Option<String> {
    let text = std::fs::read_to_string(artifact_dir.join("execution_environment.json")).ok()?;
    let receipt: serde_json::Value = serde_json::from_str(&text).ok()?;
    let accelerator = receipt
        .pointer("/cuda_devices/0/name")
        .and_then(serde_json::Value::as_str)
        .map(str::to_string)
        .unwrap_or_else(|| "cpu".to_string());
    Some(format!("unknown-host-{}", normalize_alias_part(&accelerator)))
}

// ──────────────────────────────────────────────────────────────────────
// Provenance preimage stores — recipes and configs
// (docs/decisions/2026-07-01-02-sql-provenance-tables.md)
//
// A digest without its stored preimage is only an equality test: runs carry
// recipe_hash / config_hash, and these tables keep the exact JSON each hash
// was computed over, plus the queryable grid facts for recipes. Rows are
// content-addressed and immutable — writers use ON CONFLICT DO NOTHING, so
// re-running a recipe never rewrites its recorded meaning.
// ──────────────────────────────────────────────────────────────────────

/// Write shape for one recipe preimage row. The fern-cli-rs parent builds this
/// from the same blueprint payload it hashes into recipe_hash.
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct RecipeRegistration {
    pub recipe_hash: String,
    pub schema: String,
    pub name: String,
    pub family: String,
    pub model: String,
    pub eval: String,
    pub loader: String,
    pub train: String,
    pub seq_len: i64,
    pub pred_lens_json: String,
    pub noise_levels_json: String,
    pub seeds_json: String,
    pub n_trajectory_draws: i64,
    pub n_noise_draws: i64,
    pub run_whiten: bool,
    pub run_rewind: bool,
    pub scale_noise_by_std: bool,
    pub recipe_json: String,
}

/// Read shape for one recipe preimage row.
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct RecipeRow {
    pub recipe_hash: String,
    pub schema: String,
    pub name: String,
    pub family: String,
    pub model: String,
    pub eval: String,
    pub loader: String,
    pub train: String,
    pub seq_len: i64,
    pub pred_lens_json: String,
    pub noise_levels_json: String,
    pub seeds_json: String,
    pub n_trajectory_draws: i64,
    pub n_noise_draws: i64,
    pub run_whiten: bool,
    pub run_rewind: bool,
    pub scale_noise_by_std: bool,
    pub recipe_json: String,
    pub created_at: String,
}

/// Register one recipe preimage. Idempotent: an existing recipe_hash row wins.
pub fn upsert_recipe(database: &Path, recipe: &RecipeRegistration) -> Result<usize, String> {
    #[derive(serde::Deserialize)]
    struct Report {
        schema: String,
        recipe_hash: String,
        inserted: usize,
    }
    let report: Report = crate::csharp_registry::run_json("record-recipe-preimage-data", database, recipe)?;
    if report.schema != "fern.recipe_preimage_write.v1" || report.recipe_hash != recipe.recipe_hash {
        return Err("C# recipe-preimage writer returned the wrong schema or hash".to_string());
    }
    Ok(report.inserted)
}

/// Look up one recipe preimage by its hash. None = never registered (pre-v7
/// rows, or the recipe never ran through the Rust parent since v7).
pub fn find_recipe_by_hash(database: &Path, recipe_hash: &str) -> Result<Option<RecipeRow>, String> {
    #[derive(serde::Serialize)]
    struct Request<'a> {
        recipe_hash: &'a str,
    }
    #[derive(serde::Deserialize)]
    struct Report {
        schema: String,
        requested_recipe_hash: String,
        recipe: Option<RecipeRow>,
    }
    let report: Report =
        crate::csharp_registry::run_json("load-recipe-preimage-data", database, &Request { recipe_hash })?;
    if report.schema != "fern.recipe_preimage_read.v1" || report.requested_recipe_hash != recipe_hash {
        return Err("C# recipe-preimage reader returned the wrong schema or hash".to_string());
    }
    if report.recipe.as_ref().is_some_and(|row| row.recipe_hash != recipe_hash) {
        return Err("C# recipe-preimage reader returned a different recipe hash".to_string());
    }
    Ok(report.recipe)
}

// CHG 2026-08-28: the test-only Rust -> C# config-preimage adapter moved intact
// to retired/fern_run/analysis_config_dashboard_rust_csharp_test_adapters.rs.
// Source tests cross the real C# child directly; enrollment remains the writer.

// ──────────────────────────────────────────────────────────────────────
// Materialized recipe targets
// (docs/decisions/2026-07-01-02-sql-provenance-tables.md, recipe_targets)
//
// One row per expanded target/cell of a recipe, written when the recipe
// first executes — the same moment as the recipes preimage upsert. Rows are
// immutable and content-addressed by (recipe_hash, target_hash); target_idx
// is display/order only. Resume is a query, not planner state: skip a target
// when any run with its target_hash is 'done'. failed/pending targets run
// normally (crash-retry re-enroll contract intact), and the check happens
// BEFORE enroll because enroll's UPSERT resets status.
// ──────────────────────────────────────────────────────────────────────

/// Write shape for one materialized recipe target row. The fern-cli-rs parent
/// builds this from the same target preimage it hashes into target_hash.
/// Column vocabulary mirrors the runs table exactly (dataset, noise_sigma,
/// trajectory_draw_ordinal, noise_draw_ordinal, seed).
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct RecipeTargetRegistration {
    pub recipe_hash: String,
    pub target_hash: String,
    pub target_idx: i64,
    pub target_json: String,
    pub dataset: String,
    pub seq_len: i64,
    pub pred_len: i64,
    pub noise_sigma: f64,
    pub trajectory_draw_ordinal: i64,
    pub noise_draw_ordinal: i64,
    pub seed: i64,
    pub config_hash: String,
}

/// Read shape for one materialized recipe target row.
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct RecipeTargetRow {
    pub recipe_hash: String,
    pub target_hash: String,
    pub target_idx: i64,
    pub target_json: String,
    pub dataset: String,
    pub seq_len: i64,
    pub pred_len: i64,
    pub noise_sigma: f64,
    pub trajectory_draw_ordinal: i64,
    pub noise_draw_ordinal: i64,
    pub seed: i64,
    pub config_hash: String,
    pub created_at: String,
}

/// Materialize the expanded targets of one recipe. Idempotent: existing rows
/// win (ON CONFLICT DO NOTHING covers both the (recipe_hash, target_hash)
/// primary key and the (recipe_hash, target_idx) uniqueness). Returns the
/// number of newly inserted rows — 0 on a re-run.
#[derive(serde::Deserialize)]
#[serde(deny_unknown_fields)]
struct RecipeTargetWriteReport {
    schema: String,
    inserted: usize,
}

pub fn upsert_recipe_targets(database: &Path, targets: &[RecipeTargetRegistration]) -> Result<usize, String> {
    #[derive(serde::Serialize)]
    struct Request<'a> {
        targets: &'a [RecipeTargetRegistration],
    }
    let report: RecipeTargetWriteReport =
        crate::csharp_registry::run_json("materialize-recipe-target-data", database, &Request { targets })?;
    if report.schema != "fern.recipe_target_write.v1" {
        return Err(format!("unexpected recipe-target write schema {}", report.schema));
    }
    Ok(report.inserted)
}

#[derive(serde::Serialize)]
struct RecipeTargetByHashPrefixRequest<'a> {
    recipe_hash: &'a str,
    prefix: &'a str,
}

#[derive(serde::Deserialize)]
struct RecipeTargetLookupReport {
    schema: String,
    requested_recipe_hash: String,
    requested_prefix: String,
    target: RecipeTargetRow,
}

/// Resolve one materialized target by a target_hash prefix through the typed
/// C# RunData owner. Rust validates the request echo and returned identities
/// before the Matrix caller uses the stored target index.
pub fn find_recipe_target_by_hash_prefix(
    database: &Path,
    recipe_hash: &str,
    prefix: &str,
) -> Result<RecipeTargetRow, String> {
    let report: RecipeTargetLookupReport = crate::csharp_registry::run_json(
        "load-recipe-target-by-hash-prefix-data",
        database,
        &RecipeTargetByHashPrefixRequest { recipe_hash, prefix },
    )?;
    if report.schema != "fern.recipe_target_lookup.v1" {
        return Err(format!("C# recipe-target lookup returned schema {:?}", report.schema));
    }
    if report.requested_recipe_hash != recipe_hash || report.requested_prefix != prefix {
        return Err(format!(
            "C# recipe-target lookup echoed recipe/prefix {:?}/{:?}, expected {:?}/{:?}",
            report.requested_recipe_hash, report.requested_prefix, recipe_hash, prefix
        ));
    }
    if report.target.recipe_hash != recipe_hash || !report.target.target_hash.starts_with(&prefix.to_ascii_lowercase())
    {
        return Err(format!(
            "C# recipe-target lookup returned target {}/{} outside requested recipe/prefix {}/{}",
            report.target.recipe_hash, report.target.target_hash, recipe_hash, prefix
        ));
    }
    Ok(report.target)
}

// ──────────────────────────────────────────────────────────────────────
// Findings as data — analyses
// (fern-autoresearch-north-star.md Gap A, migration 010)
//
// A finding without its preimage is an opinion. An analysis is as
// reproducible as a run: (which runs) × (which scoring code) × (which
// parameters) → values, content-addressed by analysis_hash. The hash covers
// the INPUT preimage only — {schema, kind, query_json, run_hashes_json,
// scoring_fingerprint, params_json} — never result_json, so identical inputs
// through identical scoring code always collide to the same row, and a
// recompute whose result drifted is an ignored insert, not a silent second
// finding. Rows are immutable; writers use ON CONFLICT DO NOTHING.
// ──────────────────────────────────────────────────────────────────────

/// Write shape for one analysis finding row. The writer mints analysis_hash
/// from analysis_preimage below over the same six input fields it stores.
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct AnalysisRegistration {
    pub analysis_hash: String,
    pub schema: String,
    pub kind: String,
    pub query_json: String,
    pub run_hashes_json: String,
    pub scoring_fingerprint: String,
    pub params_json: String,
    pub result_json: String,
}

/// Read shape for one analysis finding row.
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct AnalysisRow {
    pub analysis_hash: String,
    pub schema: String,
    pub kind: String,
    pub query_json: String,
    pub run_hashes_json: String,
    pub scoring_fingerprint: String,
    pub params_json: String,
    pub result_json: String,
    pub created_at: String,
}

/// Machine-facing view of one immutable analysis row.
///
/// SQLite stores the four flexible analysis documents as canonical JSON text
/// so their exact bytes can participate in `analysis_hash`. The public JSON
/// boundary decodes those strings back into JSON values; consumers never need
/// to parse JSON nested inside JSON or guess which columns are documents.
#[derive(Debug, Clone, serde::Serialize, schemars::JsonSchema)]
pub struct AnalysisReport {
    pub schema: &'static str,
    pub analysis_hash: String,
    pub analysis_schema: String,
    pub kind: String,
    pub query: serde_json::Value,
    pub run_hashes: Vec<String>,
    pub scoring_fingerprint: String,
    pub params: serde_json::Value,
    pub result: serde_json::Value,
    pub created_at: String,
}

impl fern_json_contract::JsonContract for AnalysisReport {
    const SCHEMA_ID: &'static str = "fern.analysis_report.v1";
}

pub fn analysis_report(row: AnalysisRow) -> Result<AnalysisReport, serde_json::Error> {
    Ok(AnalysisReport {
        schema: <AnalysisReport as fern_json_contract::JsonContract>::SCHEMA_ID,
        analysis_hash: row.analysis_hash,
        analysis_schema: row.schema,
        kind: row.kind,
        query: serde_json::from_str(&row.query_json)?,
        run_hashes: serde_json::from_str(&row.run_hashes_json)?,
        scoring_fingerprint: row.scoring_fingerprint,
        params: serde_json::from_str(&row.params_json)?,
        result: serde_json::from_str(&row.result_json)?,
        created_at: row.created_at,
    })
}

pub fn json_contracts() -> Vec<fern_json_contract::ContractRegistration> {
    vec![fern_json_contract::ContractRegistration::of::<AnalysisReport>(
        "Decoded immutable scientific analysis record",
    )]
}

/// The exact payload analysis_hash is computed over: the finding's inputs.
/// query_json / run_hashes_json / params_json are embedded as the exact JSON
/// strings the row stores, so re-hashing a stored row's columns reproduces
/// its analysis_hash byte-for-byte. result_json is deliberately absent.
pub fn analysis_preimage(
    schema: &str,
    kind: &str,
    query_json: &str,
    run_hashes_json: &str,
    scoring_fingerprint: &str,
    params_json: &str,
) -> serde_json::Value {
    serde_json::json!({
        "schema": schema,
        "kind": kind,
        "query_json": query_json,
        "run_hashes_json": run_hashes_json,
        "scoring_fingerprint": scoring_fingerprint,
        "params_json": params_json,
    })
}

/// Mint the content address of one analysis from its input preimage.
#[rustfmt::skip]
pub fn analysis_hash_16hex(
    schema: &str,
    kind: &str,
    query_json: &str,
    run_hashes_json: &str,
    scoring_fingerprint: &str,
    params_json: &str,
) -> Result<String, serde_json::Error> {
    crate::resolved_cfg::hash_canonical_json_16hex(&analysis_preimage(
        schema, kind, query_json, run_hashes_json, scoring_fingerprint, params_json,
    ))
}

// CHG 2026-08-28: the zero-production-caller Rust -> C# analysis-store
// adapters moved intact to
// retired/fern_run/analysis_config_dashboard_rust_csharp_test_adapters.rs.
// Analysis identity/report types remain here; focused source and CLI tests now
// cross the real C# writer/read/list child directly.

// Analysis input selectors: C# Fern.RunData owns the read-only SQL projection.
// Rust statistics still freezes the returned rows and events into the exact
// scientific input preimage.

// CHG 2026-08-28: the final public exact-run Rust -> C# adapter and DTOs
// moved intact to retired/fern_run/find_by_run_hash_rust_csharp_adapter.rs.
// The three production callers and retained source tests now cross the typed
// C# child directly and validate schema, requested hash, and returned identity.

// ──────────────────────────────────────────────────────────────────────
// Run event journal — run_events
// (migrations/009_add_run_events)
//
// runs = current state, run_events = how it got there. One append-only row
// per lifecycle transition, keyed by run_hash: a hung or failed run can be
// reconstructed after the fact (when did it enroll, when did the worker
// process spawn, how long did it live, what killed it). This is monitoring
// provenance OUTSIDE run identity — event rows never enter run_hash.
//
// Fixed event vocabulary (documented at the table definition in
// migrations/009_add_run_events/up.sql):
//   enrolled / running / completed / failed / stale
//       — appended by the lifecycle functions below, inside themselves,
//         so each transition has a single write site;
//   worker_spawned / worker_exited
//       — appended by the fern-cli-rs Python training-worker spawn.
//   data_assets
//       — appended by fern-cli-rs after a successful complete() when the
//         worker result carries a non-null receipts.data_asset_fingerprint
//         (known-data runs); detail_json is the fern.data_asset_fingerprint.v1
//         JSON verbatim ({"schema", "files": {basename: sha256[:16]}}).
// ──────────────────────────────────────────────────────────────────────

/// Read shape for one run event row.
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct RunEventRow {
    pub id: i64,
    pub run_hash: String,
    pub ts: String,
    pub event: String,
    pub detail_json: Option<String>,
}

/// Seconds elapsed since a run_events.ts timestamp (SQLite datetime('now')
/// format, UTC). None when the string does not parse. Lives here because
/// db.rs owns the ts format; read-only consumers (the dash TUI) call this
/// instead of parsing SQLite datetimes themselves.
pub fn run_event_age_seconds(ts: &str) -> Option<i64> {
    let parsed = chrono::NaiveDateTime::parse_from_str(ts, "%Y-%m-%d %H:%M:%S").ok()?;
    Some((Utc::now().naive_utc() - parsed).num_seconds())
}

// CHG 2026-08-28: the test-only Rust -> C# lifecycle-fixture adapter moved
// intact to retired/fern_run/run_lifecycle_fixture_rust_csharp_test_adapter.rs.
// Every fixture caller now starts the existing typed C# child directly.

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Pass ⑥  Read path — querying the registry
//
// Run collection and derived projections moved to Fern.RunData C#. The
// remaining Rust readers here serve the separately retained historical
// dashboard projection and exact run-hash hub.
//
// They share a common structure:
//   1. Build SQL string (sometimes dynamic)
//   2. Bind parameters
//   3. Fetch results as typed structs
//
// The Python versions were identical in intent — the difference is
// HOW the results come back:
//   Python: list[sqlite3.Row]  ← dict-like, row["mse"], runtime lookup
//   Rust:   Vec<RunRow>        ← typed struct, row.mse, compile-time checked
//
// Two new sqlx concepts appear here:
//   query_as::<_, RunRow>  — "deserialize each row into a RunRow struct"
//   fetch_all              — "give me ALL matching rows as a Vec"
//
// These replace Python's:
//   db.execute("SELECT ...").fetchall()
//   → returns list[sqlite3.Row]
//
// Rust:
//   sqlx::query_as::<_, RunRow>("SELECT ...").fetch_all(pool).await
//   → returns Result<Vec<RunRow>, sqlx::Error>
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

// ──────────────────────────────────────────────────────────────────────
// CHG 2026-08-28: the zero-production-caller Rust -> C# historical-dashboard
// adapter moved intact to
// retired/fern_run/analysis_config_dashboard_rust_csharp_test_adapters.rs.
// The source invariant test invokes the read-only C# child directly.

/*
    // sqlx doesn't know what struct you want back.
    // This SQL could map to ANY struct with matching field names.
    let rows = sqlx::query_as("SELECT source, count(*) as n FROM runs GROUP BY source")
        .fetch_all(&pool).await;
    //  ↑ compile error: "query_as what? What struct am I mapping rows into?"
    The turbofish tells the compiler which struct:
    rustlet rows = sqlx::query_as::<_, SourceCount>("SELECT ...")
    //                          │   │
    //                          │   └─ "each row becomes a SourceCount"
    //                          └─ "infer the DB backend (SQLite)"
        .fetch_all(&pool).await;
*/

// CHG: §18 latest() and §19 best_by_* (with their teaching blocks and the §25
// test_latest) retired verbatim to
// retired/fern_run/db_latest_best_read_api_2026_07_01.rs. They served the
// Leptos/Bun-era GET /api/latest and GET /api/best handlers, which were never
// rebuilt; no CLI command or active caller remained.

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Pass ⑦  Artifact lifecycle — slim_run
//
// slim_run crosses the C# RunData process boundary, where one owner:
//   1. Read the DB to find the artifact directory and check guards
//   2. Delete heavy files from the filesystem
//   3. Update the DB to record "this run is now slim"
//
// The sibling storage audit and its closed missing-folder policies are also
// owned by Fern.RunData C#; no Rust database adapter remains in this module.
//
// The lifecycle of artifacts on disk:
//   enroll()        → records the directory path (but doesn't mkdir yet)
//   training loop   → writes model.safetensors, sequence_tensors.safetensors, hist.ipc, etc.
//   complete()      → updates DB status to 'done' (files stay)
//   slim_run()      → DELETES heavy files, keeps metrics + meta
//   Fern.RunData audit → audits artifact_dir pointers against filesystem truth
//
// After slimming, the run row is still fully queryable (all scalar
// metrics in the DB), but you lose the ability to:
//   • Reload model weights (model.safetensors gone)
//   • Replot training curves (hist.ipc gone)
//   • Resample predictions (samples.ipc gone)
//
// Recoverable: retrain with the same config (preserved in config_json).
// The caller supplies the same artifact_dir for a retry, so a retrained run
// lands in the same directory and the UPSERT reattaches to the same row.
//
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Pass ⑧  Tests — the in-memory safety net
//
// Each test creates a fresh in-memory SQLite database, exercises one
// lifecycle transition, and checks the result. Then the DB is dropped.
// No files on disk, no cleanup, no interference between tests.
//
// Why tests matter more than usual here:
//   This is a DB layer being actively rewritten from Python. The tests
//   are the contract: "if these pass, the Rust version behaves like
//   the Python version." Change SQL, break a test, fix it before the
//   training loop ever notices.
//
// Read the tests in order — they walk through the run lifecycle:
//   §21  test_pool / sample_reg       ← setup helpers
//   §22  enroll + complete            ← happy path: run succeeds
//   §23  mark_failed                  ← sad path: run crashes
//   §24  compare                      ← aggregation query
//   §25  latest                       ← recency query
//   §26  re-enroll resets status      ← crash-retry (the UPSERT story)
//   §27  pin prevents slim            ← artifact protection
//
// Python had no equivalent test suite — the DB was tested implicitly
// by running training and checking Jupyter outputs. Rust makes testing
// cheap enough to do properly.
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[cfg(test)]
//  │
//  └─ #[cfg(test)]: "only compile this module when running `cargo test`."
//     Everything inside mod tests { ... } is invisible to `cargo build`.
//     It doesn't bloat the binary, doesn't exist in production.
//
//     Python: no equivalent. Test files are always importable.
//     You might use `if __name__ == "__main__"` but that's convention, not enforcement.
//     Rust's #[cfg(test)] is compiler-enforced: test code literally doesn't
//     exist in the release binary.

mod tests {
    //  │
    //  └─ mod tests: a nested module. This is conventional — almost every
    //     Rust source file has a `mod tests` at the bottom.
    //     The module creates a namespace: test helpers are private to tests.

    use super::*;
    fn list_run_events(database: &Path, run_hash: &str) -> Result<Vec<RunEventRow>, String> {
        #[derive(serde::Deserialize)]
        struct Report {
            schema: String,
            events: Vec<RunEventRow>,
        }
        let report: Report = crate::csharp_registry::run_json(
            "list-run-event-data",
            database,
            &serde_json::json!({ "run_hash": run_hash }),
        )?;
        if report.schema != "fern.run_event_list.v1"
            || report.events.iter().any(|event| event.run_hash != run_hash)
        {
            return Err("C# Run event test reader returned a mismatched typed receipt".into());
        }
        Ok(report.events)
    }

    // These proof helpers deliberately live only in the source-test module.
    // They preserve the original behavioral test names while crossing the
    // real typed C# child; no Rust production adapter remains.
    fn find_config_json_by_hash(database: &Path, config_hash: &str) -> Result<Option<String>, String> {
        let report: serde_json::Value = crate::csharp_registry::run_json(
            "load-config-preimage-data",
            database,
            &serde_json::json!({ "config_hash": config_hash }),
        )?;
        if report["schema"] != "fern.run_config_preimage_read.v1"
            || report["requested_config_hash"].as_str() != Some(config_hash)
        {
            return Err("C# config-preimage test reader returned the wrong schema or hash".into());
        }
        report["config_json"]
            .as_str()
            .map(str::to_owned)
            .map(Some)
            .or_else(|| report["config_json"].is_null().then_some(None))
            .ok_or_else(|| "C# config-preimage test reader returned an invalid config_json".into())
    }

    fn upsert_analysis(database: &Path, analysis: &AnalysisRegistration) -> Result<usize, String> {
        let report: serde_json::Value =
            crate::csharp_registry::run_json("record-analysis-data", database, analysis)?;
        if report["schema"] != "fern.analysis_write.v1"
            || report["analysis_hash"].as_str() != Some(analysis.analysis_hash.as_str())
        {
            return Err("C# analysis test writer returned a mismatched schema or hash".into());
        }
        report["inserted"]
            .as_u64()
            .and_then(|inserted| usize::try_from(inserted).ok())
            .ok_or_else(|| "C# analysis test writer returned an invalid inserted count".into())
    }

    fn find_analysis_by_hash(database: &Path, analysis_hash: &str) -> Result<Option<AnalysisRow>, String> {
        let report: serde_json::Value = crate::csharp_registry::run_json(
            "load-analysis-by-hash-data",
            database,
            &serde_json::json!({ "analysis_hash": analysis_hash }),
        )?;
        if report["schema"] != "fern.analysis_read.v1"
            || report["requested_analysis_hash"].as_str() != Some(analysis_hash)
        {
            return Err("C# analysis test reader returned a mismatched schema or hash".into());
        }
        if report["analysis"].is_null() {
            return Ok(None);
        }
        serde_json::from_value(report["analysis"].clone())
            .map(Some)
            .map_err(|error| format!("C# analysis test reader returned an invalid row: {error}"))
    }

    fn list_analyses(
        database: &Path,
        kind_filter: Option<&str>,
        query_contains: Option<&str>,
    ) -> Result<Vec<AnalysisRow>, String> {
        let report: serde_json::Value = crate::csharp_registry::run_json(
            "list-analysis-data",
            database,
            &serde_json::json!({
                "kind_filter": kind_filter,
                "query_contains": query_contains,
            }),
        )?;
        if report["schema"] != "fern.analysis_list.v1" {
            return Err("C# analysis test list returned a mismatched schema".into());
        }
        serde_json::from_value(report["analyses"].clone())
            .map_err(|error| format!("C# analysis test list returned invalid rows: {error}"))
    }

    fn list_dashboard_runs_readonly(
        database: &Path,
        status: &str,
        dataset: Option<&str>,
    ) -> Result<Vec<DashboardRunRow>, String> {
        let report: serde_json::Value = crate::csharp_registry::run_json(
            "list-historical-dashboard-run-data",
            database,
            &serde_json::json!({ "status": status, "dataset": dataset }),
        )?;
        if report["schema"] != "fern.historical_dashboard_runs.v1" {
            return Err("C# historical dashboard test reader returned a mismatched schema".into());
        }
        serde_json::from_value(report["runs"].clone())
            .map_err(|error| format!("C# historical dashboard test reader returned invalid rows: {error}"))
    }

    fn find_by_run_hash(database: &Path, run_hash: &str) -> Result<Option<RunRow>, String> {
        let report: serde_json::Value = crate::csharp_registry::run_json(
            "load-run-by-hash-data",
            database,
            &serde_json::json!({ "run_hash": run_hash }),
        )?;
        if report["schema"] != "fern.run_list.v1"
            || report["requested_run_hash"].as_str() != Some(run_hash)
        {
            return Err("C# exact-Run source-test reader returned a mismatched schema or hash".into());
        }
        if report["run"].is_null() {
            return Ok(None);
        }
        let row: RunRow = serde_json::from_value(report["run"].clone())
            .map_err(|error| format!("C# exact-Run source-test reader returned an invalid complete row: {error}"))?;
        if row.run_hash.as_deref() != Some(run_hash) {
            return Err("C# exact-Run source-test reader returned a different Run identity".into());
        }
        Ok(Some(row))
    }

    fn set_run_lifecycle_fixture(
        database: &Path,
        run_hash: &str,
        state: &str,
        metrics: Option<&Metrics>,
        detail: Option<&str>,
    ) -> Result<usize, String> {
        let report: serde_json::Value = crate::csharp_registry::run_json(
            "set-run-lifecycle-fixture-data",
            database,
            &serde_json::json!({
                "run_hash": run_hash,
                "state": state,
                "metrics": metrics,
                "detail": detail,
            }),
        )?;
        if report["schema"] != "fern.run_lifecycle_fixture.v1"
            || report["run_hash"].as_str() != Some(run_hash)
            || report["state"].as_str() != Some(state)
            || report["rows_changed"].as_i64().is_none_or(|rows| rows < 0)
        {
            return Err("C# Run lifecycle test fixture returned a mismatched receipt".into());
        }
        usize::try_from(report["rows_changed"].as_i64().unwrap()).map_err(|error| error.to_string())
    }

    fn project_accepted_typed_result(
        database: &Path,
        run_hash: &str,
        metrics: &Metrics,
        coordinate_id: &str,
        attempt_id: &str,
    ) -> Result<usize, String> {
        let report: serde_json::Value = crate::csharp_registry::run_json(
            "project-accepted-run-result-data",
            database,
            &serde_json::json!({
                "run_hash": run_hash,
                "metrics": metrics,
                "coordinate_id": coordinate_id,
                "attempt_id": attempt_id,
            }),
        )?;
        if report["schema"] != "fern.accepted_typed_run_projection.v1"
            || report["run_hash"].as_str() != Some(run_hash)
            || report["rows_changed"].as_i64().is_none_or(|rows| rows < 0)
        {
            return Err("C# accepted typed Run projection test receipt changed".into());
        }
        usize::try_from(report["rows_changed"].as_i64().unwrap()).map_err(|error| error.to_string())
    }
    //  │
    //  └─ `use super::*`: import everything from the PARENT module.
    //     `super` = the module that contains this `mod tests` block = db.rs itself.
    //     This gives tests access to the typed RunData helpers and DTOs.
    //     without qualifying every name.
    //
    //     Python: tests in the same file can access everything already.
    //     Rust: the test module is a separate scope — it must explicitly
    //     import from the parent.

    use std::path::{Path, PathBuf};
    use std::time::{SystemTime, UNIX_EPOCH};

    // CHG 2026-08-28: the four callers of the test-only direct Diesel Run-row
    // reader all observe already committed C# enrollment effects. They now use
    // the exact-Run C# child above; the old helper is recoverable under
    // retired/fern_run/open_test_transaction_run_reader_diesel.rs.

    // ──────────────────────────────────────────────────────────────────
// §21  Test helpers — file Registry paths and sample_reg
    //
    // These two functions are the foundation every test builds on.
// Each test gives the real C# child a fresh, production-migrated database.
    // sample_reg() gives you a valid registration to insert.
    // ──────────────────────────────────────────────────────────────────

    /// Test-only bridge retaining source test names while requiring their
    /// enrollment to cross the same C# child as production callers.
    fn enroll(database: &Path, reg: &RunRegistration) -> Result<RunHandle, String> {
        crate::csharp_registry::run_json("enroll-run-data", database, reg)
    }

    fn hash_json_str(json: &str) -> String {
        let value: serde_json::Value = serde_json::from_str(json).unwrap();
        crate::resolved_cfg::hash_canonical_json_16hex(&value).unwrap()
    }

    fn temp_db_path(name: &str) -> PathBuf {
        let nanos = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
        let dir = std::env::temp_dir().join(format!("fern_run_{name}_{}_{}", std::process::id(), nanos));
        std::fs::create_dir_all(&dir).unwrap();
        dir.join("fern.db")
    }

    fn sample_reg() -> RunRegistration {
        //                  └─ Returns an owned RunRegistration.
        //                     Not async — just building a struct, no I/O.

        RunRegistration {
            schema: RunRegistration::SCHEMA_ID.to_string(),
            dataset: "lorenz".into(),
            //                │
            //                └─ .into(): converts &str → String.
            //                   "lorenz" is a &str (string literal).
            //                   The struct field is String (owned).
            //                   .into() does the conversion.
            //
            //                   This is the same as String::from("lorenz")
            //                   but shorter. You'll see .into() everywhere
            //                   in struct construction.
            //
            //                   Python: just "lorenz" — no conversion needed
            //                   because Python has one string type.
            //                   Rust has two (&str and String), so conversion
            //                   is explicit.
            dataset_id: "lorenz_traj0".into(),
            model_type: "fern".into(),
            seed: 42,
            pred_len: 336,
            seq_len: 336,
            noise_sigma: 0.1,
            trajectory_draw_ordinal: 0,
            noise_draw_ordinal: 0,
            trajectory_seed_base: 42,
            noise_seed_base: 1000,
            trajectory_draw_seed: 42,
            noise_draw_seed: 1000,

            config_json: Some(r#"{"lr": 1e-3}"#.into()),
            //           │    │
            //           │    └─ r#"..."#: raw string. The JSON content has quotes
            //           │       inside it. Raw strings avoid escaping.
            //           │       Without raw: Some("{\"lr\": 1e-3}".into())
            //           │       With raw:    Some(r#"{"lr": 1e-3}"#.into())
            //           │
            //           └─ Some(...): this field is Option<String>.
            //              Some means "yes, we have a config JSON."
            //              Tests could also use None to test the no-config case.
            config_hash: Some("abc12345".into()),
            git_sha: "deadbeef".into(),
            //        │
            //        └─ Fake SHA. Tests don't need a real git repo.
            //           "deadbeef" is the classic hex placeholder.
            code_fingerprint_json: None,
            template_id: None, // standalone run, not from a sweep
            source: "user_defined".into(),
            config_name: Some("lorenz".into()),
            recipe_name: None,
            recipe_hash: None,
            target_hash: None,
            run_note: None,
            invocation_json: None,
            run_hash: "abcd1234efef5678".into(),
            n_features: Some(3),
            artifact_dir: PathBuf::from("results/runs/lorenz/fern_h336_sig0p1_s42_abcd"),
        }
        // This struct is reused by every test. Tests that need variation
        // clone it and modify one field:
        //   let mut reg2 = sample_reg();
        //   reg2.model_type = "dlinear".into();
        //
        // This keeps tests focused on the transition they're testing,
        // not on constructing boilerplate.
    }

    // ──────────────────────────────────────────────────────────────────
    // §22  test_enroll_and_complete — the happy path
    //
    // The most important test: a run is born, trained, and completed.
    //   enroll       → status='pending'
    //   complete     → status='done' + metrics
    //
    // This tests the entire write path from passes ④ and ⑤.
    // ──────────────────────────────────────────────────────────────────

    //  │
    //  └─ CHG: in the sqlx era this was #[tokio::test] — async tests need the
    //     tokio runtime to drive .await calls, and it spins up a
    //     single-threaded runtime per test (Python: @pytest.mark.asyncio).
    //     Diesel is synchronous, so these are plain #[test] fns now — same as
    //     Python's synchronous sqlite3 tests, which never needed async either.
    #[test]
    fn accepted_typed_projection_is_atomic_idempotent_and_conflict_refusing() {
        let db_path = temp_db_path("accepted_typed_projection");
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let mut reg = sample_reg();
        reg.run_hash = "typed_projection_run".into();
        reg.artifact_dir = "results/runs/typed_projection_run".into();
        let handle = enroll(&db_path, &reg).unwrap();
        set_run_lifecycle_fixture(&db_path, &handle.run_hash, "running", None, None).unwrap();
        let metrics = Metrics {
            mse: Some(0.125),
            clean_target_mse: Some(0.1),
            injected_target_noise_mse: Some(0.025),
            noise_forecast_overlap: Some(0.0),
            ..Default::default()
        };

        assert_eq!(
            project_accepted_typed_result(&db_path, &handle.run_hash, &metrics, "coordinate-7", "attempt-11",).unwrap(),
            1
        );
        let first_events = list_run_events(&db_path, &handle.run_hash).unwrap();
        assert_eq!(
            first_events
                .iter()
                .filter(|event| event.event == "v3_accepted_projection")
                .count(),
            1
        );

        assert_eq!(
            project_accepted_typed_result(&db_path, &handle.run_hash, &metrics, "coordinate-7", "attempt-11",).unwrap(),
            0,
            "lost-response replay must not rewrite completion time or append another receipt"
        );
        let replay_events = list_run_events(&db_path, &handle.run_hash).unwrap();
        assert_eq!(replay_events.len(), first_events.len());
        assert_eq!(
            replay_events
                .iter()
                .filter(|event| event.event == "v3_accepted_projection")
                .count(),
            1
        );

        let conflicting = Metrics {
            mse: Some(0.5),
            ..metrics.clone()
        };
        assert!(
            project_accepted_typed_result(&db_path, &handle.run_hash, &conflicting, "coordinate-7", "attempt-12",)
                .is_err(),
            "the same run hash must never be overwritten with a second result"
        );
        let stored = find_by_run_hash(&db_path, &handle.run_hash).unwrap().unwrap();
        assert_eq!(stored.mse, Some(0.125));
        std::fs::remove_dir_all(db_path.parent().unwrap()).ok();
    }

    #[test]
    fn test_registry_lifecycle_names_enroll_mark_running_complete() {
        let db_path = temp_db_path("registry_lifecycle_names");
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let campaign_report: serde_json::Value = crate::csharp_registry::run_json(
            "load-next-campaign-id-run-data",
            &db_path,
            &serde_json::json!({ "public_report": false }),
        )
        .unwrap();
        assert_eq!(campaign_report["schema"], "fern.run_data.next_campaign_id.v1");
        let campaign_id = campaign_report["next_campaign_id"].as_i64().unwrap();
        assert!(campaign_id > 0);
        let mut reg = sample_reg();
        reg.template_id = Some(campaign_id);
        reg.run_hash = "feed1234feed5678".into();
        reg.artifact_dir = PathBuf::from("results/runs/lorenz/fern_h336_sig0p1_s42_feed");

        let handle = enroll(&db_path, &reg).unwrap();
        assert_eq!(handle.run_hash, "feed1234feed5678");

        let row = find_by_run_hash(&db_path, "feed1234feed5678").unwrap().unwrap();
        assert_eq!(row.status, "pending");

        set_run_lifecycle_fixture(&db_path, "feed1234feed5678", "running", None, None).unwrap();
        let row = find_by_run_hash(&db_path, "feed1234feed5678").unwrap().unwrap();
        assert_eq!(row.status, "running");

        set_run_lifecycle_fixture(
            &db_path,
            "feed1234feed5678",
            "done",
            Some(&Metrics {
                mse: Some(0.02),
                ..Default::default()
            }),
            None,
        )
        .unwrap();

        let row = find_by_run_hash(&db_path, "feed1234feed5678").unwrap().unwrap();
        assert_eq!(row.status, "done");
        assert_eq!(row.success_hash.as_deref(), Some("feed1234feed5678"));
        std::fs::remove_dir_all(db_path.parent().unwrap()).ok();
    }

    #[test]
    fn a_measured_zero_overlap_is_stored_and_an_unmeasured_one_stays_null() {
        // The whole reason these columns are nullable. The overlap term is
        // genuinely 0.0 whenever the forecast could not see the injected noise
        // -- every clean cell, and every zero-shot model -- so a defaulted zero
        // would be indistinguishable from "the worker refused to verify its
        // window layout". One run of each, side by side.
        let db_path = temp_db_path("measured_zero_overlap");
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let mut measured_reg = sample_reg();
        measured_reg.run_hash = "measured_hash".into();
        measured_reg.artifact_dir = "results/runs/measured".into();
        let mut unverified_reg = sample_reg();
        unverified_reg.run_hash = "unverified_hash".into();
        unverified_reg.artifact_dir = "results/runs/unverified".into();
        let measured = enroll(&db_path, &measured_reg).unwrap();
        let unverified = enroll(&db_path, &unverified_reg).unwrap();

        set_run_lifecycle_fixture(
            &db_path,
            &measured.run_hash,
            "done",
            Some(&Metrics {
                mse: Some(0.5),
                clean_target_mse: Some(0.3),
                injected_target_noise_mse: Some(0.2),
                noise_forecast_overlap: Some(0.0),
                baseline_constant_mse: Some(1.0),
                baseline_context_mean_mse: Some(0.9),
                baseline_last_value_mse: Some(0.8),
                ..Default::default()
            }),
            None,
        )
        .unwrap();
        set_run_lifecycle_fixture(
            &db_path,
            &unverified.run_hash,
            "done",
            Some(&Metrics {
                mse: Some(0.5),
                ..Default::default()
            }),
            None,
        )
        .unwrap();

        let stored = find_by_run_hash(&db_path, &measured.run_hash).unwrap().unwrap();
        assert_eq!(stored.noise_forecast_overlap, Some(0.0));
        assert_eq!(stored.clean_target_mse, Some(0.3));
        assert_eq!(stored.baseline_last_value_mse, Some(0.8));
        // The identity the columns exist to make queryable, straight out of SQL.
        let recomposed = stored.clean_target_mse.unwrap()
            + stored.injected_target_noise_mse.unwrap()
            + stored.noise_forecast_overlap.unwrap();
        assert!((recomposed - stored.mse.unwrap()).abs() < 1e-12);

        let blank = find_by_run_hash(&db_path, &unverified.run_hash).unwrap().unwrap();
        assert_eq!(blank.noise_forecast_overlap, None);
        assert_eq!(blank.clean_target_mse, None);
        assert_eq!(blank.baseline_constant_mse, None);
        std::fs::remove_dir_all(db_path.parent().unwrap()).ok();
    }

    #[test]
    fn test_update_run_invocation() {
        let db_path = temp_db_path("update_run_invocation");
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let handle = enroll(&db_path, &sample_reg()).unwrap();

        let report: serde_json::Value = crate::csharp_registry::run_json(
            "record-run-invocation-data",
            &db_path,
            &serde_json::json!({
                "run_hash": &handle.run_hash,
                "invocation_json": r#"{"schema":"fern.run_invocation.v1"}"#,
                "config_name": "lorenz",
                "recipe_name": null,
                "recipe_hash": null,
            }),
        )
        .unwrap();
        assert_eq!(report["rows_changed"], 1);

        let rows = vec![find_by_run_hash(&db_path, &handle.run_hash).unwrap().unwrap()];
        assert_eq!(rows.len(), 1);
        assert_eq!(
            rows[0].invocation_json.as_deref(),
            Some(r#"{"schema":"fern.run_invocation.v1"}"#)
        );
        assert_eq!(rows[0].config_name.as_deref(), Some("lorenz"));
        std::fs::remove_dir_all(db_path.parent().unwrap()).ok();
    }

    #[test]
    fn test_mark_failed() {
        let db_path = temp_db_path("mark_failed_fixture");
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let handle = enroll(&db_path, &sample_reg()).unwrap();

        set_run_lifecycle_fixture(&db_path, &handle.run_hash, "failed", None, Some("OOM at epoch 5")).unwrap();

        let rows = vec![find_by_run_hash(&db_path, &handle.run_hash).unwrap().unwrap()];

        assert_eq!(rows.len(), 1);

        assert_eq!(rows[0].error_msg.as_deref(), Some("OOM at epoch 5"));
        //                           │
        //                           └─ .as_deref(): Option<String> → Option<&str>.
        //                              Same trick from list_runs (§16).
        //                              We need it because the right side is
        //                              Some("OOM at epoch 5") which is Option<&str>,
        //                              but rows[0].error_msg is Option<String>.
        //                              .as_deref() makes them the same type for ==.
        //
        //                              Without it:
        //                                Option<String> == Option<&str>  ← type mismatch!
        //                              With it:
        //                                Option<&str>   == Option<&str>  ← ✓
        std::fs::remove_dir_all(db_path.parent().unwrap()).ok();
    }

    // CHG: §25 test_latest retired with latest() to
    // retired/fern_run/db_latest_best_read_api_2026_07_01.rs.

    // ──────────────────────────────────────────────────────────────────
    // §26  test_reenroll_resets_status — the UPSERT story
    //
    // THIS IS THE MOST IMPORTANT TEST for understanding the design.
    //
    // Scenario: a run fails, then you restart the same experiment.
    // The UPSERT should:
    //   1. NOT create a new row (same run_hash → conflict)
    //   2. Reset status back to 'running'
    //   3. Clear the old error_msg
    //   4. Keep the same db_id
    //
    // This is the crash-retry contract. Without it, every restart
    // would create a duplicate row, and the dashboard would show
    // ghost failures that were actually retried and might succeed.
    // ──────────────────────────────────────────────────────────────────

    #[test]
    fn test_reenroll_resets_status() {
        let db_path = temp_db_path("reenroll_resets_status");
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let reg = sample_reg();

        // ── First attempt: enroll and fail ──
        let h1 = enroll(&db_path, &reg).unwrap();
        set_run_lifecycle_fixture(&db_path, &h1.run_hash, "failed", None, Some("crash")).unwrap();

        // ── Second attempt: re-enroll same experiment ──
        let h2 = enroll(&db_path, &reg).unwrap();
        //                              │
        //                              └─ SAME &reg → SAME run_hash
        //                                 → ON CONFLICT fires
        //                                 → existing row updated, not new row inserted

        assert_eq!(h1.db_id, h2.db_id);
        //  │
        //  └─ Same row! The UPSERT updated the existing row.
        //     If this were a fresh INSERT, h2.db_id would be h1.db_id + 1.
        //     Equality proves the ON CONFLICT path was taken.

        let rows = vec![find_by_run_hash(&db_path, &h2.run_hash).unwrap().unwrap()];

        assert_eq!(rows.len(), 1); // still just one row
        assert!(rows[0].error_msg.is_none()); // error_msg cleared!
        //                       │
        //                       └─ .is_none(): true if Option is None.
        //                          The UPSERT set error_msg = NULL.
        //                          The old "crash" message is gone.
        //                          The run looks fresh, ready for retry.
        std::fs::remove_dir_all(db_path.parent().unwrap()).ok();
    }

    // CHG: a byte-identical copy of the §26 block and test body sat here after
    // a paste-repair (the copy was named test_reenroll_resets_status_duplicate_coverage
    // only to fix the duplicate-symbol compile error). The §26 story and the
    // real test live directly above; the duplicate is deleted.

    // ──────────────────────────────────────────────────────────────────
    // Provenance preimage stores (decision 2026-07-01-02)
    // ──────────────────────────────────────────────────────────────────

    #[test]
    fn test_enroll_registers_config_preimage() {
        let db_path = temp_db_path("enroll_registers_config_preimage");
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let mut reg = sample_reg();
        let config_json = r#"{"schema":"fern.cfg.v1","model":{"type":"fern"}}"#;
        reg.config_hash = Some(hash_json_str(config_json));
        reg.config_json = Some(config_json.into());

        enroll(&db_path, &reg).unwrap();

        let stored = find_config_json_by_hash(&db_path, reg.config_hash.as_deref().unwrap())
            .unwrap()
            .unwrap();
        assert!(stored.contains("fern.cfg.v1"));
        assert_eq!(hash_json_str(&stored), reg.config_hash.unwrap());
    }

    #[test]
    fn test_upsert_config_preimage_is_immutable() {
        let db_path = temp_db_path("upsert_config_preimage_is_immutable");
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let config_json = r#"{"schema":"fern.cfg.v1","model":{"type":"fern"}}"#;
        let config_hash = hash_json_str(config_json);

        let mut first = sample_reg();
        first.config_hash = Some(config_hash.clone());
        first.config_json = Some(config_json.into());
        enroll(&db_path, &first).unwrap();

        let drifted_json = r#"{"schema":"fern.cfg.v1","model":{"type":"dlinear"}}"#;
        let mut drifted = first.clone();
        drifted.run_hash = "runhash_config_preimage_drift".into();
        drifted.artifact_dir = std::env::temp_dir().join("fern_db_tests").join("config_preimage_drift");
        drifted.config_json = Some(drifted_json.into());
        enroll(&db_path, &drifted).unwrap();

        let stored = find_config_json_by_hash(&db_path, &config_hash).unwrap().unwrap();
        assert_eq!(stored, config_json);
        assert_eq!(hash_json_str(&stored), config_hash);
    }

    // ──────────────────────────────────────────────────────────────────
    // Materialized recipe targets (decision 2026-07-01-02, second round)
    // ──────────────────────────────────────────────────────────────────

    // ──────────────────────────────────────────────────────────────────
    // Findings as data — analyses (north-star Gap A, migration 010)
    // ──────────────────────────────────────────────────────────────────

    fn sample_analysis_registration() -> AnalysisRegistration {
        let schema = "fern.analysis.v1";
        let kind = "titration_summary";
        let query_json = r#"{"select":"recipe","recipe_hash":"recipehash123456","status":"done"}"#;
        let run_hashes_json = r#"["runhash000000001","runhash000000002"]"#;
        let scoring_fingerprint = "scorefp123456789";
        let params_json = r#"{"failure_threshold":2.0}"#;
        AnalysisRegistration {
            analysis_hash: analysis_hash_16hex(
                schema,
                kind,
                query_json,
                run_hashes_json,
                scoring_fingerprint,
                params_json,
            )
            .unwrap(),
            schema: schema.into(),
            kind: kind.into(),
            query_json: query_json.into(),
            run_hashes_json: run_hashes_json.into(),
            scoring_fingerprint: scoring_fingerprint.into(),
            params_json: params_json.into(),
            result_json: r#"{"curves":[{"metric":"mse","sigmas":[0.0,1.0],"means":[2.0,4.0]}]}"#.into(),
        }
    }

    #[test]
    fn test_upsert_analysis_preimage_is_immutable() {
        let db_path = temp_db_path("upsert_analysis_immutable");
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let analysis = sample_analysis_registration();

        assert_eq!(upsert_analysis(&db_path, &analysis).unwrap(), 1);

        // The hash covers inputs only, so a recompute whose RESULT drifted
        // (same inputs, same code) mints the same hash — and the insert is
        // ignored: the first recorded finding survives.
        let mut drifted = analysis.clone();
        drifted.result_json = r#"{"drifted":true}"#.into();
        assert_eq!(upsert_analysis(&db_path, &drifted).unwrap(), 0);

        let row = find_analysis_by_hash(&db_path, &analysis.analysis_hash)
            .unwrap()
            .unwrap();
        assert_eq!(row.result_json, analysis.result_json);
        assert!(find_analysis_by_hash(&db_path, "never_registered").unwrap().is_none());
    }

    #[test]
    fn test_analysis_row_round_trips_and_rehashes_to_its_analysis_hash() {
        let db_path = temp_db_path("analysis_round_trip");
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let analysis = sample_analysis_registration();
        upsert_analysis(&db_path, &analysis).unwrap();

        // Round trip: every stored input column survives verbatim, and
        // re-hashing the stored preimage reproduces analysis_hash —
        // determinism is exactly "same inputs + same code → same row".
        let row = find_analysis_by_hash(&db_path, &analysis.analysis_hash)
            .unwrap()
            .unwrap();
        assert_eq!(row.schema, "fern.analysis.v1");
        assert_eq!(row.kind, "titration_summary");
        assert_eq!(row.query_json, analysis.query_json);
        assert_eq!(row.run_hashes_json, analysis.run_hashes_json);
        assert_eq!(row.scoring_fingerprint, analysis.scoring_fingerprint);
        assert_eq!(row.params_json, analysis.params_json);
        assert!(!row.created_at.is_empty());
        let rehashed = analysis_hash_16hex(
            &row.schema,
            &row.kind,
            &row.query_json,
            &row.run_hashes_json,
            &row.scoring_fingerprint,
            &row.params_json,
        )
        .unwrap();
        assert_eq!(rehashed, row.analysis_hash);

        // Any drifted input mints a DIFFERENT hash: a changed run set is a
        // new finding, never an overwrite.
        let other_run_set = analysis_hash_16hex(
            &row.schema,
            &row.kind,
            &row.query_json,
            r#"["runhash000000001"]"#,
            &row.scoring_fingerprint,
            &row.params_json,
        )
        .unwrap();
        assert_ne!(other_run_set, row.analysis_hash);

        // list_analyses filters: exact kind, substring of query_json.
        assert_eq!(
            list_analyses(&db_path, Some("titration_summary"), None).unwrap().len(),
            1
        );
        assert_eq!(list_analyses(&db_path, Some("recipe_compare"), None).unwrap().len(), 0);
        assert_eq!(
            list_analyses(&db_path, None, Some("recipehash123456")).unwrap().len(),
            1
        );
        assert_eq!(list_analyses(&db_path, None, Some("no_such_recipe")).unwrap().len(), 0);
    }

    #[test]
    fn discard_run_deletes_allowed_artifact_tree_then_row() {
        let db_path = temp_db_path("discard_allowed");
        let allowed_root = db_path.parent().unwrap().join("artifacts");
        let artifact_dir = allowed_root.join("runhash000000001");
        std::fs::create_dir_all(&artifact_dir).unwrap();
        std::fs::write(artifact_dir.join("metrics.json"), b"measured").unwrap();
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let mut registration = sample_reg();
        registration.artifact_dir = artifact_dir.clone();
        let handle = enroll(&db_path, &registration).unwrap();

        let report: serde_json::Value = crate::csharp_registry::run_json(
            "discard-run-data",
            &db_path,
            &serde_json::json!({
                "run_hash": &handle.run_hash,
                "allowed_roots": [&allowed_root],
                "public_format": null,
            }),
        )
        .unwrap();
        assert_eq!(report["db_id"], handle.db_id);
        assert!(!artifact_dir.exists());
        assert!(find_by_run_hash(&db_path, &handle.run_hash).unwrap().is_none());
        let replay: serde_json::Value = crate::csharp_registry::run_json(
            "discard-run-data",
            &db_path,
            &serde_json::json!({
                "run_hash": &handle.run_hash,
                "allowed_roots": [&allowed_root],
                "public_format": null,
            }),
        )
        .unwrap();
        assert!(replay["db_id"].is_null());
    }

    #[test]
    #[cfg(unix)]
    fn discard_run_refuses_artifact_outside_allowed_roots_without_effect() {
        let db_path = temp_db_path("discard_refuses_outside_root");
        let allowed_root = db_path.parent().unwrap().join("allowed");
        std::fs::create_dir_all(&allowed_root).unwrap();
        let outside_root = temp_db_path("discard_outside_artifact").parent().unwrap().to_path_buf();
        let artifact_dir = outside_root.join("runhash000000001");
        std::fs::create_dir_all(&artifact_dir).unwrap();
        std::fs::write(artifact_dir.join("keep.txt"), b"keep").unwrap();
        let linked_artifact = allowed_root.join("linked-run");
        std::os::unix::fs::symlink(&artifact_dir, &linked_artifact).unwrap();
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let mut registration = sample_reg();
        registration.artifact_dir = linked_artifact.clone();
        let handle = enroll(&db_path, &registration).unwrap();

        let refusal = crate::csharp_registry::run_json::<_, serde_json::Value>(
            "discard-run-data",
            &db_path,
            &serde_json::json!({
                "run_hash": &handle.run_hash,
                "allowed_roots": [&allowed_root],
                "public_format": null,
            }),
        )
        .unwrap_err();
        assert!(refusal.contains("outside allowed roots"));
        assert!(linked_artifact.exists());
        assert_eq!(std::fs::read(artifact_dir.join("keep.txt")).unwrap(), b"keep");
        assert!(find_by_run_hash(&db_path, &handle.run_hash).unwrap().is_some());
    }

    // ──────────────────────────────────────────────────────────────────
    // ──────────────────────────────────────────────────────────────────
    #[test]
    fn test_reenroll_updates_code_fingerprint_to_latest_attempt() {
        let db_path = temp_db_path("reenroll_updates_code_fingerprint");
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let mut reg = sample_reg();
        reg.code_fingerprint_json = Some(r#"{"schema":"fern.code_fingerprint.v1","files":{"fr.py":"aaaa"}}"#.into());
        enroll(&db_path, &reg).unwrap();

        // Same run identity, different executable contents: the pre-insert
        // guardrail warns on stderr, and the row records the LATEST attempt.
        reg.code_fingerprint_json = Some(r#"{"schema":"fern.code_fingerprint.v1","files":{"fr.py":"bbbb"}}"#.into());
        enroll(&db_path, &reg).unwrap();

        let row = find_by_run_hash(&db_path, &reg.run_hash).unwrap().unwrap();
        assert!(row.code_fingerprint_json.unwrap().contains("bbbb"));
    }

    fn sample_reg_with_missing_artifact(run_hash: &str, suffix: &str) -> RunRegistration {
        let mut reg = sample_reg();
        reg.run_hash = run_hash.into();
        reg.artifact_dir = PathBuf::from(format!("target/fern_run_missing_artifact_tests/{suffix}"));
        reg
    }

    #[test]
    fn test_audit_run_artifact_storage_report_only_is_read_only() {
        let db_path = temp_db_path("audit_report_only");
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let reg = sample_reg_with_missing_artifact("auditreport00001", "report_only");
        let handle = enroll(&db_path, &reg).unwrap();
        set_run_lifecycle_fixture(&db_path, &handle.run_hash, "done", Some(&Metrics::default()), None).unwrap();
        let summary: serde_json::Value = crate::csharp_registry::run_json(
            "audit-run-storage-data",
            &db_path,
            &serde_json::json!({
                "policy": "report_only",
                "dry_run": true,
                "mark_unfinished_stale": true,
                "public_format": null,
            }),
        )
        .unwrap();

        assert_eq!(summary["checked"], 1);
        assert_eq!(summary["missing"], 1);
        assert_eq!(summary["marked_db_metrics_only"], 0);
        assert_eq!(summary["marked_stale"], 0);

        let row = find_by_run_hash(&db_path, &handle.run_hash).unwrap().unwrap();
        assert_eq!(row.status, "done");
        assert!(row.artifact_dir.is_some());
        std::fs::remove_dir_all(db_path.parent().unwrap()).ok();
    }

    #[test]
    fn test_audit_run_artifact_storage_mark_db_metrics_only_keeps_done_row() {
        let db_path = temp_db_path("audit_metrics_only");
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let reg = sample_reg_with_missing_artifact("auditmetric0001", "mark_db_metrics_only");
        let handle = enroll(&db_path, &reg).unwrap();
        set_run_lifecycle_fixture(&db_path, &handle.run_hash, "done", Some(&Metrics::default()), None).unwrap();
        let summary: serde_json::Value = crate::csharp_registry::run_json(
            "audit-run-storage-data",
            &db_path,
            &serde_json::json!({
                "policy": "mark_db_metrics_only",
                "dry_run": false,
                "mark_unfinished_stale": true,
                "public_format": null,
            }),
        )
        .unwrap();

        assert_eq!(summary["checked"], 1);
        assert_eq!(summary["missing"], 1);
        assert_eq!(summary["marked_db_metrics_only"], 1);
        assert_eq!(summary["marked_stale"], 0);

        let row = find_by_run_hash(&db_path, &handle.run_hash).unwrap().unwrap();
        assert_eq!(row.status, "done");
        assert_eq!(row.artifacts, "none");
        assert!(row.artifact_dir.is_none());
        std::fs::remove_dir_all(db_path.parent().unwrap()).ok();
    }

    #[test]
    fn test_audit_run_artifact_storage_stage_db_row_deletion_marks_stale() {
        let db_path = temp_db_path("audit_stage_delete");
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let reg = sample_reg_with_missing_artifact("auditstage00001", "stage_db_row_deletion");
        let handle = enroll(&db_path, &reg).unwrap();
        set_run_lifecycle_fixture(&db_path, &handle.run_hash, "done", Some(&Metrics::default()), None).unwrap();
        let summary: serde_json::Value = crate::csharp_registry::run_json(
            "audit-run-storage-data",
            &db_path,
            &serde_json::json!({
                "policy": "stage_db_row_deletion",
                "dry_run": false,
                "mark_unfinished_stale": true,
                "public_format": null,
            }),
        )
        .unwrap();

        assert_eq!(summary["checked"], 1);
        assert_eq!(summary["missing"], 1);
        assert_eq!(summary["marked_db_metrics_only"], 1);
        assert_eq!(summary["marked_stale"], 1);
        assert_eq!(summary["staged_db_row_deletion"], 1);

        let row = find_by_run_hash(&db_path, &handle.run_hash).unwrap().unwrap();
        assert_eq!(row.status, "stale");
        assert_eq!(row.artifacts, "none");
        assert!(row.artifact_dir.is_none());
        assert_eq!(
            row.error_msg.as_deref(),
            Some("artifact_dir missing; staged for deletion")
        );
        std::fs::remove_dir_all(db_path.parent().unwrap()).ok();
    }

    #[test]
    fn test_audit_run_artifact_storage_purge_db_rows_deletes_rows() {
        let db_path = temp_db_path("audit_purge");
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let reg = sample_reg_with_missing_artifact("auditpurge0001", "purge");
        let handle = enroll(&db_path, &reg).unwrap();
        set_run_lifecycle_fixture(&db_path, &handle.run_hash, "done", Some(&Metrics::default()), None).unwrap();
        let summary: serde_json::Value = crate::csharp_registry::run_json(
            "audit-run-storage-data",
            &db_path,
            &serde_json::json!({
                "policy": "purge_db_rows",
                "dry_run": false,
                "mark_unfinished_stale": true,
                "public_format": null,
            }),
        )
        .unwrap();

        assert_eq!(summary["checked"], 1);
        assert_eq!(summary["missing"], 1);
        assert_eq!(summary["purged_db_rows"], 1);

        let row = find_by_run_hash(&db_path, &handle.run_hash).unwrap();
        assert!(row.is_none());
        std::fs::remove_dir_all(db_path.parent().unwrap()).ok();
    }

    // ──────────────────────────────────────────────────────────────────
    // §27  test_pin_prevents_slim — artifact protection
    //
    // Verifies the pin_run → slim_run interaction:
    //   1. Register and finalize a run
    //   2. Pin it
    //   3. Try to slim → should return 0 (refused)
    //   4. Unpin
    //   5. Verify pinned = 0
    //
    // Note: this test uses a production-migrated file DB so pin/unpin can cross
    // the real C# child, but it still has no artifact_dir on disk. It tests the
    // DB-level guard; a filesystem integration test would create real files.
    // ──────────────────────────────────────────────────────────────────

    #[test]
    fn test_pin_prevents_slim() {
        let db_path = temp_db_path("pin_prevents_slim");
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let handle = enroll(&db_path, &sample_reg()).unwrap();
        set_run_lifecycle_fixture(&db_path, &handle.run_hash, "done", Some(&Metrics::default()), None).unwrap();

        // ── Pin ──
        let _: serde_json::Value = crate::csharp_registry::run_json(
            "protect-run-data",
            &db_path,
            &serde_json::json!({ "run_id": handle.db_id, "public_report": false }),
        )
        .unwrap();

        // ── Try to slim → should be refused ──
        let report: serde_json::Value = crate::csharp_registry::run_json(
            "slim-run-data",
            &db_path,
            &serde_json::json!({ "run_id": handle.db_id, "public_report": false }),
        )
        .unwrap();
        assert_eq!(report["freed_bytes"], 0);
        //  │
        //  └─ 0 bytes freed. slim_run saw pinned=1 and returned early.
        //     The artifacts (if they existed on disk) would be untouched.

        // ── Unpin and verify ──
        let _: serde_json::Value = crate::csharp_registry::run_json(
            "unprotect-run-data",
            &db_path,
            &serde_json::json!({ "run_id": handle.db_id, "public_report": false }),
        )
        .unwrap();
        let rows = vec![find_by_run_hash(&db_path, &handle.run_hash).unwrap().unwrap()];
        assert_eq!(rows[0].pinned, 0);
        //  │
        //  └─ pinned back to 0. The run is now eligible for slimming.
        std::fs::remove_dir_all(db_path.parent().unwrap()).ok();
    }

    // ──────────────────────────────────────────────────────────────────
    // Run event journal tests (migration 009)
    //
    // record/list round trip, and the property the journal exists for:
    // the lifecycle functions append their own events, so the sequence
    // enroll → mark_running → complete leaves enrolled/running/completed
    // in append order for that run_hash.
    // ──────────────────────────────────────────────────────────────────

    #[test]
    fn test_lifecycle_functions_append_expected_event_sequence() {
        let db_path = temp_db_path("lifecycle_fixture_events");
        crate::csharp_registry::migrate_test_registry_via_home(&db_path);
        let handle = enroll(&db_path, &sample_reg()).unwrap();
        set_run_lifecycle_fixture(&db_path, &handle.run_hash, "running", None, None).unwrap();
        set_run_lifecycle_fixture(&db_path, &handle.run_hash, "done", Some(&Metrics::default()), None).unwrap();

        let events = list_run_events(&db_path, &handle.run_hash).unwrap();
        let names: Vec<&str> = events.iter().map(|e| e.event.as_str()).collect();
        assert_eq!(names, ["enrolled", "running", "completed"]);

        // failed carries a 500-char error preview in its detail JSON.
        let long_error = "x".repeat(600);
        set_run_lifecycle_fixture(&db_path, &handle.run_hash, "failed", None, Some(&long_error)).unwrap();
        let events = list_run_events(&db_path, &handle.run_hash).unwrap();
        assert_eq!(events.last().unwrap().event, "failed");
        let detail: serde_json::Value =
            serde_json::from_str(events.last().unwrap().detail_json.as_deref().unwrap()).unwrap();
        assert_eq!(detail["error"].as_str().unwrap().chars().count(), 500);

        // an unknown run_hash gets no phantom transition event.
        set_run_lifecycle_fixture(&db_path, "no_such_run_hash", "running", None, None).unwrap();
        assert!(list_run_events(&db_path, "no_such_run_hash").unwrap().is_empty());
        std::fs::remove_dir_all(db_path.parent().unwrap()).ok();
    }

}
//  │
//  └─ End of mod tests. Everything above is compiled ONLY for `cargo test`.
//     The production binary has zero test code in it.
//
//     To run these tests:
//       cargo test -p fern_run          ← run all tests in this crate
//       cargo test test_compare         ← run one specific test by name
//
//     Each test gets its own in-memory DB, runs in isolation, and
//     leaves no state behind. You can run them in any order, in
//     parallel, and they'll always pass (or fail for real reasons).

/// One completed run, reduced to what the lifecycle needs to own it.
///
/// The typed C# lane reader returns this named wire shape so the lifecycle
/// importer never reconstructs an ordered SQL tuple or opens the lane itself.
#[derive(Debug, Clone, serde::Deserialize)]
pub struct CompletedRunIdentity {
    pub recipe_name: String,
    pub recipe_hash: Option<String>,
    pub target_hash: String,
    pub run_hash: String,
    pub finished_at: Option<String>,
    /// Carried so a caller can notice that ONE identity holds two different
    /// results. Measured 2026-08-05: 28 run hashes exist in two lanes with
    /// different MSE -- the same recipe fitted twice, which run identity calls
    /// one run. Comparing hashes alone reports no conflict at all.
    pub mse: Option<f64>,
    /// Which machine computed it, or None when nothing recorded that.
    pub machine_alias: Option<String>,
}

/// One completed run that belongs to no recipe, and the axes that place it.
///
/// A standalone `run-target` has no designed cell, so nothing in the lifecycle
/// could own it and an entire seed sweep sat outside the mechanism. Its
/// scientific position is still fully determined -- world, model, dose, which
/// trajectory, which noise draw -- and the seed is what distinguishes repeats at
/// that position, which is exactly a declared replica.
#[derive(Debug, Clone, serde::Deserialize)]
pub struct StandaloneRunIdentity {
    pub dataset: String,
    pub model_type: String,
    pub noise_sigma: f64,
    pub trajectory_draw_ordinal: i64,
    pub noise_draw_ordinal: i64,
    pub seed: i64,
    pub run_hash: String,
    pub finished_at: Option<String>,
    pub mse: Option<f64>,
    pub machine_alias: Option<String>,
}
