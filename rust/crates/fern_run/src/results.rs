//! fern_run/src/results.rs — Artifact I/O and run output containers.
//!
//! This is the Rust twin of the retired fr_result.py artifact layer. Python's
//! fr_result.py was ~1500 lines before its concerns moved to fr_tr.py,
//! fr_tr_exp.py, retired/fr_train/, retired/fr_post/, and retired/fr_result/.
//! holding 5 concerns:
//!   1. RunId — identity and paths        → caller supplies artifact_dir to db.rs
//!   2. RunMeta — metadata sidecar        → absorbed into db.rs (RunRow IS the metadata)
//!   3. RunResult — save/load artifacts   → THIS FILE
//!   4. SeqBundle — [S,T,D] arrays        → THIS FILE (native ndarray, not Python)
//!   5. Diagnostics — scipy/statsmodels   → diagnostics.rs (native Rust, not Python)
//!
//! What results.rs owns:
//!   "Given an artifact_dir, what files are there and how do I read them?" — plus
//!   the SeqBundle result container. The statistical / calibration math moved to
//!   diagnostics.rs; SeqBundle::diagnose / diagnose_joint call back into it.
//!
//! Python original → Rust mapping:
//!   RunId.tag / RunId.run_dir   → caller-owned artifact_dir
//!   RunId.seq_path / hist_path  → ArtifactPaths::new(dir) (this file)
//!   RunResult.save()            → training loop writes files under artifact_dir
//!   RunResult.load()            → DB row + SequenceTensors reader
//!   RunMeta.save/load           → DB row is primary; no sidecar manifest
//!   load_results() scan         → list_runs() SQL in db.rs (already done)
//!   quick_table()               → compare() SQL in db.rs (already done)
//!   residual_diagnostics() etc. → diagnostics.rs (native Rust)
//!   pp / pp_result / pp_compare → Leptos dashboard (future)
//!
//! Reading sequence:
//!   Pass ①  RunHandle (lib.rs) — the receipt from enroll
//!   Pass ②  ArtifactPaths      — "given a dir, where is each file?"
//!   Pass ③  Artifact I/O       — read safetensors/IPC side files, check what exists
//!   Pass ⑩  SeqBundle          — the [S,T,D] result container
//!   (the diagnostics math now lives in diagnostics.rs)

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use sha2::{Digest, Sha256};

//         │
//         └─ rayon::prelude imports the parallel iterator traits.
//            After this, any slice or Vec gets .par_iter() for free.
//            No thread pools to configure, no worker counts to tune.
//            rayon auto-detects your CPU core count and work-steals.
//
//            Python equivalent: there IS no equivalent.
//            multiprocessing.Pool(8) is the closest, but it:
//              • copies data between processes (expensive for numpy arrays)
//              • has startup overhead per pool
//              • doesn't work inside Jupyter without __main__ guard
//            rayon has none of these problems.

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Pass ①  RunHandle — the receipt from registration
//
// When C# `enroll-run-data` writes the pending run row, the caller gets a RunHandle.
// This is the ONLY thing the training loop needs from the DB layer:
//   • run_hash    → to call complete() or mark_failed() later
//   • db_id       → useful for display / older artifact row lookups
//   • artifact_dir → to know WHERE to write files
//
// The old Python fr_db.register_run() returned just an int (db_id). The caller
// had to separately compute or fetch the artifact_dir and then use db-id keyed
// finalize_run()/fail_run(). The current registry lifecycle is run-hash keyed:
// enroll-run-data → mark_running → complete | mark_failed | mark_stale.
// RunHandle carries both the durable identity and the storage location.
//
// The RunHandle is the "receipt" you carry from registration to
// completion. It connects the C# RunData registry to result.rs (the artifacts).
// The run_hash identifies the run, the artifact_dir identifies the filesystem
// location, and db_id remains available for row display / older readers.
//
// Python twin: the retired return value of register_run() was just `int`.
//              artifact_dir was scattered across RunId, RunContext, etc.
//              Five objects knew parts of the identity. The bridge-era
//              RunRegistry now calls Rust and receives this richer handle.
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

// ──────────────────────────────────────────────────────────────────────
// §1  RunHandle — your training loop carries this from start to finish
//
// Lifecycle:
//   let handle = enroll_run_via_csharp(database, &reg)?;
//   mark_running(conn, &handle.run_hash)?;
//   // handle.artifact_dir → write model.safetensors here
//   // handle.artifact_dir → write sequence_tensors.safetensors, hist.ipc here
//   // ... train for 100 epochs ...
//   complete(conn, &handle.run_hash, &metrics)?;
//
// Or on failure:
//   mark_failed(conn, &handle.run_hash, "OOM at epoch 50")?;
//
// The handle doesn't know about the pool — it's pure data.
// The training loop passes handle.run_hash back to db.rs lifecycle functions.
// ──────────────────────────────────────────────────────────────────────

// RunHandle is defined once in lib.rs (crate::RunHandle): the registry (db.rs)
// produces it, and it carries the content-addressed run_hash. results.rs only uses
// it; `ensure_dir()` now lives on the lib.rs definition. (The local duplicate that
// lacked run_hash has been removed.)

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Pass ②  ArtifactPaths — "given a dir, where is each file?"
//
// Python had these as @property on RunId:
//   run_id.seq_path       → results/lorenz_fern_s42_p336/sequence_tensors.safetensors
//   run_id.model_path     → results/lorenz_fern_s42_p336/model.safetensors
//   run_id.metrics_path   → results/lorenz_fern_s42_p336/metrics.ipc
//   etc.
//
// Rust: a struct that computes all paths from the artifact_dir.
// This is a pure value type — no I/O, just path arithmetic.
//
// Why a separate struct instead of methods on RunHandle?
// Because you might want paths from a DB row (RunRow.artifact_dir)
// without having a RunHandle. ArtifactPaths works with any directory.
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

// §3  ArtifactPaths

pub struct ArtifactPaths {
    pub dir: PathBuf,
    //       │
    //       └─ The artifact directory: "results/lorenz_fern_s42_p336"
    //          Everything else is derived from this.
}

impl ArtifactPaths {
    pub fn new(dir: impl Into<PathBuf>) -> Self {
        //              │
        //              └─ `impl Into<PathBuf>`: accepts anything convertible to PathBuf.
        //                 You can pass:
        //                   ArtifactPaths::new("results/lorenz_fern_s42_p336")  ← &str
        //                   ArtifactPaths::new(handle.artifact_dir)             ← PathBuf
        //                   ArtifactPaths::new(Path::new("..."))                ← &Path
        //
        //                 `impl Into<T>` is Rust's way of saying "any type that
        //                 can be converted to T." It's generic but reads like English.
        //                 Python: no equivalent — duck typing handles this implicitly.

        Self { dir: dir.into() }
    }

    // CHG: `from_handle(&RunHandle)` retired to
    // retired/fern_run/results_artifact_bytes_api_2026_07_01.rs — no caller;
    // `ArtifactPaths::new(handle.artifact_dir)` covers the same case.

    // ── File paths ──
    // Each method returns a PathBuf: the full path to that artifact.
    // Python: these were @property on RunId.
    // Rust: methods on ArtifactPaths. Same data, different home.

    pub fn metrics_ipc(&self) -> PathBuf {
        self.dir.join(METRICS_IPC_FILE)
    }
    pub fn sequence_tensors_safetensors(&self) -> PathBuf {
        self.dir.join(SEQUENCE_TENSORS_FILE)
    }
    pub fn hist_ipc(&self) -> PathBuf {
        self.dir.join(HIST_IPC_FILE)
    }
    pub fn samples_ipc(&self) -> PathBuf {
        self.dir.join(SAMPLES_IPC_FILE)
    }
    pub fn model_safetensors(&self) -> PathBuf {
        self.dir.join(MODEL_SAFETENSORS_FILE)
    }
    pub fn raw_tensors_safetensors(&self) -> PathBuf {
        self.dir.join(RAW_TENSORS_FILE)
    }
    pub fn fern_json(&self) -> PathBuf {
        self.dir.join(FERN_JSON_FILE)
    }
    //  │
    //  └─ Each is a one-liner: dir.join(FILENAME_CONST).
    //     Python: @property def model_path(self): return self.run_dir / "model.safetensors"
    //     Rust: pub fn model_safetensors(&self) -> PathBuf { self.dir.join(MODEL_SAFETENSORS_FILE) }
    //     Same logic, different syntax.
    //
    //     Why methods instead of stored fields?
    //     Because the paths are deterministic from `dir`.
    //     Storing them would be redundant data.
    //     Computing them is a string concat — effectively free.
    pub fn hist_json(&self) -> PathBuf {
        self.dir.join(HIST_JSON_FILE)
    }
}

// Canonical artifact file names. CHG: these were string literals duplicated
// between the methods above and db.rs HEAVY_FILES (the slim deletion list);
// the filename contract now lives in exactly one place.
pub const METRICS_IPC_FILE: &str = "metrics.ipc";
pub const POINT_FORECAST_FILE: &str = "point_forecast.safetensors";
pub const SEQUENCE_TENSORS_FILE: &str = "sequence_tensors.safetensors";
pub const HIST_IPC_FILE: &str = "hist.ipc";
pub const SAMPLES_IPC_FILE: &str = "samples.ipc";
pub const MODEL_SAFETENSORS_FILE: &str = "model.safetensors";
pub const RAW_TENSORS_FILE: &str = "raw_tensors.safetensors";
pub const FERN_JSON_FILE: &str = "fern.json";
pub const HIST_JSON_FILE: &str = "hist.json";
pub const INVOCATION_FILE: &str = "invocation.json";
pub const EXECUTION_ENVIRONMENT_FILE: &str = "execution_environment.json";

/// Why one file belongs in an artifact snapshot.
///
/// This is deliberately a bounded vocabulary rather than an arbitrary filename
/// label.  Analysis and retention can ask whether an entry is a checkpoint,
/// raw evaluation tensor, or receipt without inventing another string parser.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ArtifactFileRole {
    MetricsReceipt,
    Invocation,
    ExecutionEnvironment,
    Checkpoint,
    PointForecast,
    RawEvaluationTensors,
    SequenceTensors,
    ForecastCalibration,
    QuantileDiagnostics,
    Diagnostics,
    Other,
}

impl ArtifactFileRole {
    pub fn for_file_name(name: &str) -> Self {
        match name {
            METRICS_IPC_FILE | "compact_metrics.arrow" => Self::MetricsReceipt,
            INVOCATION_FILE => Self::Invocation,
            EXECUTION_ENVIRONMENT_FILE => Self::ExecutionEnvironment,
            MODEL_SAFETENSORS_FILE => Self::Checkpoint,
            POINT_FORECAST_FILE => Self::PointForecast,
            RAW_TENSORS_FILE => Self::RawEvaluationTensors,
            SEQUENCE_TENSORS_FILE => Self::SequenceTensors,
            "forecast_calibration.json" => Self::ForecastCalibration,
            "quantile_diag.json" => Self::QuantileDiagnostics,
            "diagnostics.json" | "calibration.json" => Self::Diagnostics,
            _ => Self::Other,
        }
    }

    pub fn as_str(self) -> &'static str {
        match self {
            Self::MetricsReceipt => "metrics_receipt",
            Self::Invocation => "invocation",
            Self::ExecutionEnvironment => "execution_environment",
            Self::Checkpoint => "checkpoint",
            Self::PointForecast => "point_forecast",
            Self::RawEvaluationTensors => "raw_evaluation_tensors",
            Self::SequenceTensors => "sequence_tensors",
            Self::ForecastCalibration => "forecast_calibration",
            Self::QuantileDiagnostics => "quantile_diagnostics",
            Self::Diagnostics => "diagnostics",
            Self::Other => "other",
        }
    }

    /// Map a stored snapshot role string back to this vocabulary.
    ///
    /// `None` is an unknown stored role. Automatic GC must refuse it rather
    /// than treating an unrecognized label as identity-only.
    pub fn from_stored(role: &str) -> Option<Self> {
        match role {
            "metrics_receipt" => Some(Self::MetricsReceipt),
            "invocation" => Some(Self::Invocation),
            "execution_environment" => Some(Self::ExecutionEnvironment),
            "checkpoint" => Some(Self::Checkpoint),
            "point_forecast" => Some(Self::PointForecast),
            "raw_evaluation_tensors" => Some(Self::RawEvaluationTensors),
            "sequence_tensors" => Some(Self::SequenceTensors),
            "forecast_calibration" => Some(Self::ForecastCalibration),
            "quantile_diagnostics" => Some(Self::QuantileDiagnostics),
            "diagnostics" => Some(Self::Diagnostics),
            "other" => Some(Self::Other),
            _ => None,
        }
    }

    pub fn required_for_acceptance(self) -> bool {
        // CHG (2026-08-19): this boolean is the acceptance requirement only.
        // It is not a retention declaration and not automatic-GC permission.
        matches!(
            self,
            Self::MetricsReceipt | Self::Invocation | Self::ExecutionEnvironment
        )
    }
}

/// One content-addressed stable file captured at the moment a run is accepted.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ArtifactFileReceipt {
    pub relative_path: String,
    pub role: ArtifactFileRole,
    pub byte_count: i64,
    pub sha256: String,
}

/// The complete stable evidence set one attempt leaves in its artifact directory.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ArtifactManifest {
    pub files: Vec<ArtifactFileReceipt>,
    pub total_bytes: i64,
    pub receipt_digest: String,
}

const VOLATILE_ARTIFACT_FILES: &[&str] = &["heartbeat.json"];

/// Read and sign every stable artifact file for one completed run.
///
/// A heartbeat is deliberately excluded: it is a liveness signal that may move
/// after a worker's useful output is complete, so treating it as evidence would
/// manufacture false stale alarms. Every other regular file is signed with its
/// path relative to the Attempt root. Nested typed-output directories are
/// allowed; symlinks and other non-regular entries are refused at every depth. The
/// three required receipts prove the run has a metric, its declared invocation,
/// and the machine environment that actually produced it.
pub fn capture_artifact_manifest(directory: &Path) -> io::Result<ArtifactManifest> {
    let mut files = Vec::new();
    capture_artifact_files(directory, directory, &mut files)?;
    files.sort_by(|left, right| left.relative_path.cmp(&right.relative_path));

    for role in [
        ArtifactFileRole::MetricsReceipt,
        ArtifactFileRole::Invocation,
        ArtifactFileRole::ExecutionEnvironment,
    ] {
        if !files.iter().any(|file| file.role == role && file.byte_count > 0) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!(
                    "completed artifact directory {} lacks a non-empty {} receipt",
                    directory.display(),
                    role.as_str()
                ),
            ));
        }
    }

    let total_bytes = files.iter().map(|file| file.byte_count).sum();
    let mut receipt_digest = Sha256::new();
    for file in &files {
        for part in [
            &file.relative_path,
            file.role.as_str(),
            &file.byte_count.to_string(),
            &file.sha256,
        ] {
            receipt_digest.update(part.as_bytes());
            receipt_digest.update(b"\x1f");
        }
    }
    Ok(ArtifactManifest {
        files,
        total_bytes,
        receipt_digest: format!("{:x}", receipt_digest.finalize()),
    })
}

fn capture_artifact_files(root: &Path, directory: &Path, files: &mut Vec<ArtifactFileReceipt>) -> io::Result<()> {
    for entry in fs::read_dir(directory)? {
        let entry = entry?;
        let file_type = entry.file_type()?;
        let file_name = entry.file_name().to_string_lossy().to_string();
        if VOLATILE_ARTIFACT_FILES.contains(&file_name.as_str()) {
            continue;
        }
        if file_type.is_symlink() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!(
                    "artifact directory {} contains non-regular entry {file_name:?}",
                    directory.display()
                ),
            ));
        }
        if file_type.is_dir() {
            capture_artifact_files(root, &entry.path(), files)?;
            continue;
        }
        if !file_type.is_file() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!(
                    "artifact directory {} contains non-regular entry {file_name:?}",
                    directory.display()
                ),
            ));
        }
        let metadata = entry.metadata()?;
        let byte_count = i64::try_from(metadata.len()).map_err(|_| {
            io::Error::new(
                io::ErrorKind::InvalidData,
                format!("artifact {file_name:?} is too large to record"),
            )
        })?;
        let role = ArtifactFileRole::for_file_name(&file_name);
        let relative_path = entry
            .path()
            .strip_prefix(root)
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "artifact escaped its root"))?
            .to_string_lossy()
            .replace('\\', "/");
        files.push(ArtifactFileReceipt {
            relative_path,
            role,
            byte_count,
            sha256: sha256_file(&entry.path())?,
        });
    }
    Ok(())
}

/// Hash a regular file without loading tensor payloads into memory.
pub fn sha256_file(path: &Path) -> io::Result<String> {
    use std::io::Read;

    let mut reader = fs::File::open(path)?;
    let mut digest = Sha256::new();
    let mut buffer = [0_u8; 1024 * 1024];
    loop {
        let read = reader.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        digest.update(&buffer[..read]);
    }
    Ok(format!("{:x}", digest.finalize()))
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Pass ③  Artifact I/O — what's on disk? read it.
//
// Python: RunResult.load() read everything into memory (Polars DataFrames).
// Rust splits this into smaller pieces:
//
//   artifact_status() — what kind of artifacts exist? (full/slim/none)
//   read_artifact_bytes() — read a side file as raw bytes (for the API)
//
// The active dashboard now reads sequence_tensors.safetensors through
// fern_post::sequence_tensors. read_artifact_bytes() remains for simple file
// endpoints such as metrics.ipc, hist.ipc, model.safetensors, or the grouped
// sequence tensor file itself.
//
// For Python-side reading (Jupyter, diagnostics), the Python code
// still uses pl.read_ipc(). That stays in Python.
//
// Python twin: RunResult.load() + _is_done() + artifact detection
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

// §4  ArtifactStatus — what's on disk?

#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize)]
pub enum ArtifactStatus {
    Full, // model + sequences + metrics — everything present
    Slim, // heavy files deleted, metrics remain
    None_, // nothing on disk (CSV import, metrics-only DB row)
          //  └─ Trailing underscore: `None` is a keyword in Rust (the Option variant).
          //     Same workaround as other keyword-escape names.
}

impl ArtifactStatus {
    /// Detect artifact status by checking which files exist.
    pub fn detect(paths: &ArtifactPaths) -> Self {
        //                   │
        //                   └─ Takes &ArtifactPaths — borrows, doesn't consume.

        if !paths.dir.exists() {
            return ArtifactStatus::None_;
        }

        let has_model = paths.model_safetensors().exists();
        let has_seq = paths.sequence_tensors_safetensors().exists();
        //                             │
        //                             └─ .exists(): does this file exist on disk?
        //                                Python: path.exists()
        //                                Same method name, same semantics.

        if has_model && has_seq {
            ArtifactStatus::Full
        } else if paths.metrics_ipc().exists() {
            ArtifactStatus::Slim
        } else {
            ArtifactStatus::None_
        }
        // Python's backfill() had the same logic:
        //   if has_model and has_seq: art = "full"
        //   elif metrics_path.exists(): art = "slim"
        //   else: art = "none"
    }

    /// To the string stored in the DB `artifacts` column.
    pub fn as_str(&self) -> &str {
        match self {
            ArtifactStatus::Full => "full",
            ArtifactStatus::Slim => "slim",
            ArtifactStatus::None_ => "none",
        }
    }
}

// CHG: §5 read_artifact_bytes retired to
// retired/fern_run/results_artifact_bytes_api_2026_07_01.rs — the byte-serving
// endpoint it anticipated (Leptos/Bun-era dashboard) was never rebuilt.

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Pass ⑩  SeqBundle — the [S, T, D] result container
//
// When a model finishes predicting, it produces:
//   • preds:   [S, T, D]  — S samples of T timesteps across D features
//   • targets: [1, T, D]  — ground truth (always one "sample")
//   • optionally: Fern-specific eigenvalue diagnostics [S, T, D]
//
// Python stored these as numpy arrays inside an attrs dataclass.
// Rust stores them as nested Vecs with computed properties as methods.
//
// Why 3D (S dimension)?
//   For deterministic models (DLinear, PatchTST): S = 1. One prediction.
//   For probabilistic models (Fern): S > 1. Multiple samples from the
//   learned distribution. The Brenier map generates S independent draws.
//   The ensemble mean is the point forecast. The ensemble spread
//   measures predictive uncertainty.
//
// Data flow:
//   Stage 1 (now):
//     Python finalizer writes raw eval tensors, then shells out to
//     `fern-run write-sequence-tensors`.
//     Rust stitch.rs overlap-averages windows, derives Fern eigenvalue
//     summaries, and writes sequence_tensors.safetensors.
//
//   Stage 2 (PyO3 stitch):
//     Python training loop → in-memory tensor views → Rust stitcher → safetensors.
//     No .npy bridge.
//
//   Stage 3 (Burn):
//     Burn inference → Tensor<B, 3> → .into_data() → SeqBundle
//     No Python in the loop.
//
// The diagnostics from passes ⑦-⑨ consume SeqBundle.resid() directly:
//   let resid = bundle.resid();  // [T][D] row-major
//   residual_diagnostics(&resid, &names, 0.05, sigma)?;
//
// Python twin: class SeqBundle in fr_tr.py (temporary staging)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
// Pass ⑩  SeqBundle — the [S, T, D] result container (ndarray edition)
//
// When a model finishes predicting, it produces:
//   • preds:   [S, T, D]  — S samples of T timesteps across D features
//   • targets: [1, T, D]  — ground truth (always one "sample")
//   • optionally: Fern-specific eigenvalue diagnostics [S, T, D]
//
// Now using ndarray::Array3<f64> and Array2<f64> instead of Vec<Vec<Vec<f64>>>.
// Why ndarray:
//   • Math reads like numpy: &preds.mean_axis(Axis(0)) - &targets.slice(s![0,..,..]
//   • Slicing is O(1) (view, not copy): preds.slice(s![0, .., ..])
//   • PyO3 numpy interop is zero-copy: numpy array → ArrayView3
//   • Shape is part of the type: Array3 = always 3D, can't accidentally pass 2D
//
// Cargo.toml:
//   ndarray = "0.16"
//
// Data flow:
//   Stage 1 (now):     Python writes raw_tensors.safetensors → Rust CLI writes sequence_tensors.safetensors
//   Stage 2 (PyO3):    tensor/array views → Rust stitcher → safetensors
//   Stage 3 (Burn):    Burn Tensor → .into_data().to_vec() → Array3::from_shape_vec()
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

use ndarray::Array3;
//            │       │       │      │     │
//            │       │       │      │     └─ s![] macro for slicing.
//            │       │       │      │        s![0, .., ..] = "first element along axis 0, all of the rest"
//            │       │       │      │        Same as numpy's [0, :, :]
//            │       │       │      │
//            │       │       │      └─ Axis(0), Axis(1), Axis(2): named axis indices.
//            │       │       │         Axis(0) = S dimension (samples)
//            │       │       │         Axis(1) = T dimension (timesteps)
//            │       │       │         Axis(2) = D dimension (features)
//            │       │       │         numpy: axis=0. ndarray: Axis(0). Same idea, typed.
//            │       │       │
//            │       │       └─ Array3<f64>: owned 3D array. Like numpy ndarray with ndim=3.
//            │       │          Stores data as a flat Vec<f64> + shape + strides.
//            │       │          No nested Vecs. One contiguous allocation.
//            │       │
//            │       └─ Array2<f64>: owned 2D array. [T, D] for residuals (after collapsing S).//            │
//            └─ Array1<f64>: owned 1D array. For OOB residuals in EnbPIResult.

// ──────────────────────────────────────────────────────────────────────
// §17  SeqBundle
// ──────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone)]
pub struct SeqBundle {
    pub preds: Array3<f64>,
    //         │
    //         └─ [S, T, D]: S samples × T timesteps × D features.
    //            Shape: preds.shape() → [100, 336, 3] for 100-sample Fern on Lorenz.
    //
    //            Contiguous memory: 100 × 336 × 3 = 100,800 f64s in one flat buffer.
    //            Vec<Vec<Vec<f64>>> would be 100 allocations (one per sample) +
    //            100 × 336 allocations (one per row). ndarray: ONE allocation.
    pub targets: Array3<f64>,
    //           │
    //           └─ [1, T, D]: always S=1.
    //              targets.shape() → [1, 336, 3].
    //              Access ground truth: targets.slice(s![0, .., ..]) → ArrayView2 [T, D]

    // ── Fern-specific diagnostics ──
    pub eigenvalue_max: Option<Array3<f64>>,
    //            └─ [S, T, D]: max eigenvalue of SPD transport matrix per timestep.
    //               None for non-Fern models.
    pub eigenvalue_sum: Option<Array3<f64>>,    // [S, T, D]: trace of SPD matrix
    pub eigenvalue_logdet: Option<Array3<f64>>, // [S, T, D]: log-determinant
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::RunHandle;

    #[test]
    fn test_artifact_paths() {
        let paths = ArtifactPaths::new("results/lorenz_fern_s42_p336");
        assert_eq!(
            paths.sequence_tensors_safetensors(),
            PathBuf::from("results/lorenz_fern_s42_p336/sequence_tensors.safetensors")
        );
        assert_eq!(
            paths.model_safetensors(),
            PathBuf::from("results/lorenz_fern_s42_p336/model.safetensors")
        );
    }

    #[test]
    fn canonical_point_forecast_has_a_named_manifest_role() {
        assert_eq!(
            ArtifactFileRole::for_file_name(POINT_FORECAST_FILE),
            ArtifactFileRole::PointForecast
        );
        assert_eq!(ArtifactFileRole::PointForecast.as_str(), "point_forecast");
        assert_eq!(
            ArtifactFileRole::from_stored("point_forecast"),
            Some(ArtifactFileRole::PointForecast)
        );
        assert_eq!(ArtifactFileRole::from_stored("other"), Some(ArtifactFileRole::Other));
        assert_eq!(ArtifactFileRole::from_stored("not-a-role"), None);
    }

    #[test]
    fn artifact_manifest_recurses_typed_outputs_and_keeps_relative_paths() {
        let root = std::env::temp_dir().join(format!("fern_nested_manifest_{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(root.join("outputs")).unwrap();
        fs::write(root.join("outputs/metrics.ipc"), b"metrics").unwrap();
        fs::write(root.join("outputs/invocation.json"), b"{}").unwrap();
        fs::write(root.join("outputs/execution_environment.json"), b"{}").unwrap();
        fs::write(root.join("worker_request.v1.json"), b"{}").unwrap();

        let manifest = capture_artifact_manifest(&root).unwrap();
        let paths = manifest
            .files
            .iter()
            .map(|file| file.relative_path.as_str())
            .collect::<Vec<_>>();
        assert_eq!(
            paths,
            vec![
                "outputs/execution_environment.json",
                "outputs/invocation.json",
                "outputs/metrics.ipc",
                "worker_request.v1.json",
            ]
        );
        fs::remove_dir_all(root).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn artifact_manifest_refuses_a_nested_symlink() {
        use std::os::unix::fs::symlink;

        let root = std::env::temp_dir().join(format!("fern_nested_symlink_{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(root.join("outputs")).unwrap();
        symlink("/etc/passwd", root.join("outputs/invocation.json")).unwrap();
        let error = capture_artifact_manifest(&root).unwrap_err().to_string();
        assert!(error.contains("non-regular entry"));
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn test_artifact_status_nonexistent_dir() {
        let paths = ArtifactPaths::new("/tmp/does_not_exist_fern_test");
        assert_eq!(ArtifactStatus::detect(&paths), ArtifactStatus::None_);
    }

    #[test]
    fn test_run_handle_ensure_dir() {
        let dir = std::env::temp_dir().join("fern_test_handle");
        let _ = std::fs::remove_dir_all(&dir); // clean up from previous run

        let handle = RunHandle::new(1, "testhash", dir.clone());
        handle.ensure_dir().unwrap();
        assert!(dir.exists());

        std::fs::remove_dir_all(&dir).ok(); // cleanup
    }
}
