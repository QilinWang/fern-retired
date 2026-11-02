# result.rs: Artifact I/O — Annotated Walkthrough

## How to use this guide

~~`result_annotated.rs`~~ `results.rs` is the Rust replacement for the old
fr_result.py artifact layer. It's MUCH thinner than the Python original because
fr_result.py was retired and its concerns were absorbed elsewhere:

```
retired/fr_result/rich_pretty_legacy.py (historical source)
    │
    ├─ RunId / RunResult         → fr_tr.py (temporary Python trainer surface)
    ├─ RunMeta sidecar           → db.rs  (RunRow IS the metadata)
    ├─ SeqBundle                 → retired from Python; Rust keeps internal stitch bundle
    ├─ SeqKind / frame views     → retired with fr_vis.py / sequence legacy notes
    ├─ sequence tensor writer    → fern-run write-sequence-tensors
    ├─ EnbPIResult               → fr_tr_exp.py
    ├─ Fingerprints              → fr_train.py
    ├─ Rich pretty-printing      → retired/fr_result/ + docs decision note
    └─ Diagnostics               → fern_post::diagnostics
```

result.rs answers ONE question: **"given an artifact directory, what files
are there and how do I read them?"**

---

## Reading sequence

### Pass ① RunHandle — the receipt from registration

| Section | Item | What you learn |
|---------|------|----------------|
| §1 | `RunHandle` struct | ~~Two fields: db_id + artifact_dir~~ Three fields: db_id + run_hash + artifact_dir (run_hash is the lifecycle identity; see lib.rs). The training loop's passport. |
| §1 | `impl RunHandle` | `impl` block separates data from behavior, `Self` as return type, `ensure_dir()` |

**Key concept:** Python scattered the run identity across RunId, RunContext,
RunMeta. Rust: ~~two~~ three fields on one struct. The `run_hash` connects to
the db.rs lifecycle, the `db_id` to row display/maintenance, and the
`artifact_dir` to the filesystem.

---

### Pass ② ArtifactPaths — path arithmetic

| Section | Item | What you learn |
|---------|------|----------------|
| §2 | `ArtifactPaths` struct | `impl Into<PathBuf>` for flexible constructors |
| §2 | File path methods | One-liner `.join()` methods replacing Python's `@property` |

**Python twin:** RunId's `@property` methods (seq_path, model_path, etc.).
Rust: methods on a standalone struct. Works with any directory — not tied
to RunHandle or a DB row.

---

### Pass ③ Artifact I/O — status detection and byte reading

| Section | Item | What you learn |
|---------|------|----------------|
| §3 | `ArtifactStatus` enum | `Full` / `Slim` / `None_`, `.detect()` checks filesystem |
| ~~§3~~ | ~~`read_artifact_bytes()`~~ | ~~`match` dispatch on file kind, `std::fs::read` → `Vec<u8>`~~ → retired (2026-07): the byte-serving endpoint it anticipated was never rebuilt; frozen in `retired/fern_run/results_artifact_bytes_api_2026_07_01.rs` |

**Key concept:** sequence data is now one grouped tensor artifact:
`sequence_tensors.safetensors`. The active Rust reader is
`fern_post::sequence_tensors::SequenceTensors`, which validates `[S,T,D]`
shape and feature metadata. ~~`read_artifact_bytes()` is only the raw-byte escape
for simple file endpoints.~~

---

### Pass ⑥ Diagnostics bridge — active direction

The active bridge is Rust-native diagnostics called by the Rust parent after
the Python worker writes final tensor handoffs. Python remains the training
rectangle; Rust owns the validated postprocess math that no longer needs
scipy/statsmodels at runtime.

The retired Python implementations stay under `retired/` as recovery material
and parity references. Human inspection can still use explicit Rust CLI
commands such as `fern-run calibrate`, `fern-run write-sequence-tensors`, and
`fern-run score-sequence-forecast`; runtime worker scoring should stay in the
Rust parent instead of shelling back through Python.

---

## fr_result.py retirement map

| Python code | Where it goes | Status |
|-------------|---------------|--------|
| `RunId` class (all of it) | `fr_tr.py` now; Rust target is caller-owned `artifact_dir` + DB row | **Moved / temporary** |
| `RunId.tag` / path properties | `fr_tr.py` now; Rust target is `ArtifactPaths` in result.rs | **Moved / temporary** |
| `RunMeta` class | `RunRow` in db.rs; sidecar manifests are not active runtime state | **Retired** |
| `RunResult.save()` | `fr_tr.py` now; Rust target is direct artifact writes | **Moved / temporary** |
| `RunResult.load()` | `fr_tr.py` now; metrics + feature names only; Rust target is `SequenceTensors::load_dir()` + DB row | **Moved / shrinking** |
| `SeqKind` enum | Retired with `fr_vis.py`; tensor names are the canonical artifact contract | **Retired** |
| `SeqBundle` class | Retired from active Python; Rust keeps an internal stitch bundle, public readers use `SequenceTensors` | **Retired from Python** |
| `array3_to_frame()` / `sequence_bundle_to_frame()` / `load_sequence_frame()` | Retired dataframe compatibility views over `sequence_tensors.safetensors` | **Retired** |
| `load_results()` scanner | `list_runs()` SQL in db.rs | **Retired** |
| `quick_table()` | `compare()` SQL in db.rs | **Retired** |
| `Session` class | DB-tracked sessions (future) | **Redesigned** |
| `cfg_fingerprint()` | `fr_train.py` (Python training identity) | **Moved** |
| `tee_log()` | Training utility (stays Python) | **Stays** |
| `residual_diagnostics()` | `fern_post::diagnostics` + `fern-run diagnose` | **Ported** |
| `pvalue_uniformity()` | `fern_post::diagnostics` | **Ported** |
| `patch_normality_fdr()` | `fern_post::diagnostics` | **Ported** |
| `titration_diagnostics()` | `fern_post::diagnostics` | **Ported** |
| `EnbPIResult` | `fr_tr_exp.py`; algorithm parity still separate | **Moved** |
| `pp` / `pp_result` / `pp_compare` | `retired/fr_result/rich_pretty_legacy.py` + Rich/TUI style decision note | **Retired as runtime / preserved as style source** |
| `_manifest_row()` | DB row already has this data | **Retired** |

---

## The three-file architecture

```
db.rs       — WHO is this run?
               Identity, lifecycle, queries.
               RunRegistration → enroll → RunRow.  (register_run is the
               retired Python-era name.)
               "The registry."

results.rs  — WHAT did this run produce?
               File paths, artifact status.
               ArtifactPaths → SequenceTensors.
               "The archive."

fern_dash/server.ts — WHO can see it?
               (The Leptos, Axum, and terminal result views are retired;
               SvelteKit reads the Bun/Elysia JSON API.)
               GET /api/runs → read-only SQLite → JSON.
               "The window."
```

A request flows: **browser → fern_dash/server.ts → fern.db / safetensors → browser**

---

## What to do after reading

1. **Read pass by pass** — same drill as db.rs
2. **Cross-reference db.rs** — RunHandle is created there, used here
3. **Cross-reference `fern_dash/server.ts`** — the retired Rust views remain
   historical; Bun reads DB rows and safetensors directly
4. **Notice what's MISSING** — no DataFrames, no numpy, no plotting.
   Rust handles bytes, tensor metadata, and postprocess diagnostics. Python
   handles training until the remaining trainer rectangle is retired.
