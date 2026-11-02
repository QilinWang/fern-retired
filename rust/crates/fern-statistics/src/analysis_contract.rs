//! The durable hand-off around one scientific analysis.
//!
//! Python owns the mathematics. It does not own evidence membership and it
//! does not write the campaign registry. Rust resolves a declared scientific
//! query to one exact cohort, Python reports outputs for that request, and Go
//! Registry Home reopens those exact inputs and outputs before registering an
//! immutable finding.

use std::collections::BTreeSet;
#[cfg(feature = "registry")]
use std::io::Read;
use std::path::{Component, Path, PathBuf};
#[cfg(feature = "registry")]
use std::process::{Command, Stdio};
#[cfg(feature = "registry")]
use std::time::{Duration, Instant};

use arrow_array::{Array, BooleanArray, Float64Array, Int64Array, RecordBatch, StringViewArray};
use arrow_select::filter::filter_record_batch;
use fern_json_contract::{ContractRegistration, JsonContract, checked_from_slice};
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};

use crate::postprocess_io::{read_ipc_record_batch, record_batch_ipc_bytes};
use crate::{Fallible, invalid_input, write_file_durably, write_json_file};
pub use fern_experiment_types::{
    AcceptedMatrixQueryV1, AnalysisDeclarationV1, AnalysisEvidenceDeclarationV1, AnalysisOutputSpecV1,
    GridMultiplicityV1,
};

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct HashedAnalysisFileV1 {
    pub path: String,
    pub sha256: String,
    pub bytes: u64,
    pub schema_id: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema, PartialEq)]
#[serde(tag = "source", rename_all = "snake_case", deny_unknown_fields)]
pub enum ResolvedAnalysisEvidenceV1 {
    Artifacts {
        inputs: Vec<HashedAnalysisFileV1>,
    },
    AcceptedMatrix {
        source_table: HashedAnalysisFileV1,
        source_manifest: HashedAnalysisFileV1,
        query: AcceptedMatrixQueryV1,
        cohort_table: HashedAnalysisFileV1,
        row_count: u64,
        run_hashes: Vec<String>,
    },
    AcceptedMatrixWithArtifacts {
        source_table: HashedAnalysisFileV1,
        source_manifest: HashedAnalysisFileV1,
        query: AcceptedMatrixQueryV1,
        cohort_table: HashedAnalysisFileV1,
        row_count: u64,
        run_hashes: Vec<String>,
        inputs: Vec<HashedAnalysisFileV1>,
    },
}

impl ResolvedAnalysisEvidenceV1 {
    /// Identity of the evidence itself, without repository placement paths.
    #[cfg(test)]
    fn analysis_query_identity(&self) -> Value {
        match self {
            Self::Artifacts { inputs } => {
                let mut identities = inputs
                    .iter()
                    .map(|input| {
                        json!({
                            "sha256": input.sha256,
                            "bytes": input.bytes,
                            "schema_id": input.schema_id,
                        })
                    })
                    .collect::<Vec<_>>();
                identities.sort_by_key(|value| serde_json::to_string(value).unwrap_or_default());
                json!({"source": "artifacts", "inputs": identities})
            }
            Self::AcceptedMatrix {
                query,
                cohort_table,
                row_count,
                ..
            } => {
                let sorted = |items: &[String]| {
                    let mut values = items.to_vec();
                    values.sort();
                    values.dedup();
                    values
                };
                json!({
                    "source": "accepted_matrix",
                    "query": {
                        "grids": sorted(&query.grids),
                        "grid_patterns": sorted(&query.grid_patterns),
                        "datasets": sorted(&query.datasets),
                        "models": sorted(&query.models),
                        "noise_law": query.noise_law,
                        "split_treatment_scheme": query.split_treatment_scheme,
                        "runtime_device": query.runtime_device,
                        "include_replicas": query.include_replicas,
                        "grid_multiplicity": query.grid_multiplicity,
                    },
                    "cohort_sha256": cohort_table.sha256,
                    "row_count": row_count,
                })
            }
            Self::AcceptedMatrixWithArtifacts {
                query,
                cohort_table,
                row_count,
                inputs,
                ..
            } => {
                let sorted = |items: &[String]| {
                    let mut values = items.to_vec();
                    values.sort();
                    values.dedup();
                    values
                };
                let mut artifact_identities = inputs
                    .iter()
                    .map(|input| {
                        json!({
                            "sha256": input.sha256,
                            "bytes": input.bytes,
                            "schema_id": input.schema_id,
                        })
                    })
                    .collect::<Vec<_>>();
                artifact_identities.sort_by_key(|value| serde_json::to_string(value).unwrap_or_default());
                json!({
                    "source": "accepted_matrix_with_artifacts",
                    "query": {
                        "grids": sorted(&query.grids),
                        "grid_patterns": sorted(&query.grid_patterns),
                        "datasets": sorted(&query.datasets),
                        "models": sorted(&query.models),
                        "noise_law": query.noise_law,
                        "split_treatment_scheme": query.split_treatment_scheme,
                        "runtime_device": query.runtime_device,
                        "include_replicas": query.include_replicas,
                        "grid_multiplicity": query.grid_multiplicity,
                    },
                    "cohort_sha256": cohort_table.sha256,
                    "row_count": row_count,
                    "inputs": artifact_identities,
                })
            }
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema, PartialEq)]
#[serde(deny_unknown_fields)]
pub struct DeclaredAnalysisRequestV1 {
    pub schema: String,
    pub request_hash: String,
    pub analysis_schema: String,
    pub kind: String,
    pub generator: String,
    pub implementation_fingerprint: String,
    #[serde(default)]
    pub parameters: Value,
    pub evidence: ResolvedAnalysisEvidenceV1,
    pub promised_outputs: Vec<AnalysisOutputSpecV1>,
}

impl JsonContract for DeclaredAnalysisRequestV1 {
    const SCHEMA_ID: &'static str = "fern.declared_analysis_request.v1";
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema, PartialEq)]
#[serde(deny_unknown_fields)]
pub struct ReportedAnalysisResultV1 {
    pub schema: String,
    pub request_hash: String,
    pub outputs: Vec<HashedAnalysisFileV1>,
    #[serde(default)]
    pub summary: Value,
}

impl JsonContract for ReportedAnalysisResultV1 {
    const SCHEMA_ID: &'static str = "fern.reported_analysis_result.v1";
}

/// One Knowledge outbox event this registration stated.
///
/// A Card is a human projection of a scientific fact, so registering the fact
/// no longer writes the Card. It states one event per evidence path and
/// returns; `fern-card reconcile-events` writes the Update later. The receipt
/// therefore names what was stated, not what was attached — a claim it can
/// keep, because the event is written in the registration's own transaction.
#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
pub struct StatedKnowledgeEventV1 {
    pub event_id: String,
    pub design_ref: String,
    /// `appended` for a new event, `already_appended` when this exact fact was
    /// already stated by an earlier identical registration.
    pub status: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema, PartialEq)]
#[serde(deny_unknown_fields)]
pub struct CheckedAnalysisReceiptV1 {
    pub schema: String,
    pub request_hash: String,
    pub analysis_hash: String,
    pub analysis_schema: String,
    pub kind: String,
    pub evidence: ResolvedAnalysisEvidenceV1,
    pub checked_outputs: Vec<HashedAnalysisFileV1>,
    pub registry: String,
    pub checked_at: String,
    /// Knowledge outbox events stated for this registration, one per evidence
    /// path. Their Card projection happens later in `fern-card
    /// reconcile-events`; Go Registry Home says which are still waiting.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub knowledge_events: Vec<StatedKnowledgeEventV1>,
}

impl JsonContract for CheckedAnalysisReceiptV1 {
    const SCHEMA_ID: &'static str = "fern.checked_analysis_receipt.v1";
}

#[derive(Debug, Serialize)]
struct StatisticAdmissionRequestV1 {
    schema: &'static str,
    root: String,
    request_path: String,
    report_path: String,
}

pub fn json_contracts() -> Vec<ContractRegistration> {
    vec![
        ContractRegistration::of::<AnalysisDeclarationV1>("Scientific analysis declaration before evidence resolution"),
        ContractRegistration::of::<DeclaredAnalysisRequestV1>(
            "Analysis request with one exact Rust-resolved evidence input",
        ),
        ContractRegistration::of::<ReportedAnalysisResultV1>("Outputs reported by the analysis implementation"),
        ContractRegistration::of::<CheckedAnalysisReceiptV1>("Outputs reopened and registered by Fern"),
    ]
}

fn relative_path(root: &Path, value: &str) -> Fallible<(PathBuf, String)> {
    let relative = Path::new(value);
    if relative.is_absolute()
        || relative
            .components()
            .any(|part| matches!(part, Component::ParentDir | Component::RootDir | Component::Prefix(_)))
    {
        return Err(invalid_input(format!(
            "analysis path must stay relative to the repository: {value:?}"
        )));
    }
    let canonical_root = std::fs::canonicalize(root)?;
    let joined = root.join(relative);
    let canonical = std::fs::canonicalize(&joined)?;
    if !canonical.starts_with(&canonical_root) {
        return Err(invalid_input(format!(
            "analysis path escapes the repository: {value:?}"
        )));
    }
    Ok((canonical, relative.to_string_lossy().replace('\\', "/")))
}

fn future_relative_path(root: &Path, value: &str) -> Fallible<(PathBuf, String)> {
    let relative = Path::new(value);
    if relative.as_os_str().is_empty()
        || relative.is_absolute()
        || relative
            .components()
            .any(|part| matches!(part, Component::ParentDir | Component::RootDir | Component::Prefix(_)))
    {
        return Err(invalid_input(format!(
            "analysis output path must stay relative to the repository: {value:?}"
        )));
    }
    let canonical_root = std::fs::canonicalize(root)?;
    let parent = root
        .join(relative)
        .parent()
        .ok_or_else(|| invalid_input("analysis output has no parent"))?
        .to_path_buf();
    std::fs::create_dir_all(&parent)?;
    let canonical_parent = std::fs::canonicalize(&parent)?;
    if !canonical_parent.starts_with(&canonical_root) {
        return Err(invalid_input(format!(
            "analysis output escapes the repository: {value:?}"
        )));
    }
    Ok((root.join(relative), relative.to_string_lossy().replace('\\', "/")))
}

fn json_schema_id(path: &Path) -> Option<String> {
    let value: Value = serde_json::from_slice(&std::fs::read(path).ok()?).ok()?;
    value.get("schema").and_then(Value::as_str).map(str::to_string)
}

fn hashed_file(root: &Path, relative: &str, promised_schema: Option<&str>) -> Fallible<HashedAnalysisFileV1> {
    let (path, normalized) = relative_path(root, relative)?;
    if !path.is_file() {
        return Err(invalid_input(format!("analysis input is not a file: {normalized}")));
    }
    let bytes = std::fs::read(&path)?;
    let observed_schema = json_schema_id(&path);
    if let Some(expected) = promised_schema
        && observed_schema.as_deref() != Some(expected)
    {
        return Err(invalid_input(format!(
            "{} carries schema {:?}; expected {:?}",
            normalized, observed_schema, expected
        )));
    }
    Ok(HashedAnalysisFileV1 {
        path: normalized,
        sha256: crate::sha256_hex(&bytes),
        bytes: bytes.len() as u64,
        schema_id: observed_schema,
    })
}

fn implementation_fingerprint(root: &Path, sources: &[String]) -> Fallible<String> {
    if sources.is_empty() {
        return Err(invalid_input(
            "an analysis must name at least one implementation source",
        ));
    }
    let mut inputs = Vec::new();
    for source in sources {
        inputs.push(hashed_file(root, source, None)?);
    }
    inputs.sort_by(|left, right| left.path.cmp(&right.path));
    Ok(crate::sha256_hex(&serde_json::to_vec(&inputs)?))
}

fn resolved_artifact_inputs(root: &Path, paths: &[String]) -> Fallible<Vec<HashedAnalysisFileV1>> {
    if paths.is_empty() {
        return Err(invalid_input(
            "an artifact-derived analysis must name at least one input",
        ));
    }
    let mut inputs = paths
        .iter()
        .map(|path| hashed_file(root, path, None))
        .collect::<Result<Vec<_>, _>>()?;
    inputs.sort_by(|left, right| left.path.cmp(&right.path));
    Ok(inputs)
}

fn request_hash(request: &DeclaredAnalysisRequestV1) -> Fallible<String> {
    let mut preimage = serde_json::to_value(request)?;
    preimage["request_hash"] = Value::String(String::new());
    Ok(format!(
        "analysis-request:{}",
        crate::sha256_hex(&fern_json_contract::canonical_json_bytes(&preimage)?)
    ))
}

fn validate_reason(reason: &Option<String>, what: &str) -> Fallible<()> {
    if reason.as_ref().is_none_or(|text| text.trim().len() < 25) {
        return Err(invalid_input(format!(
            "{what} needs a specific reason of at least 25 characters"
        )));
    }
    Ok(())
}

fn matches_text(value: Option<&str>, wanted: &Option<String>) -> bool {
    wanted.as_ref().is_none_or(|expected| value == Some(expected.as_str()))
}

fn matches_set(value: Option<&str>, wanted: &BTreeSet<&str>) -> bool {
    wanted.is_empty() || value.is_some_and(|observed| wanted.contains(observed))
}

fn wildcard_match(value: &str, pattern: &str) -> bool {
    let parts = pattern.split('*').collect::<Vec<_>>();
    if parts.len() == 1 {
        return value == pattern;
    }
    let mut remainder = value;
    for (index, part) in parts.iter().enumerate() {
        if part.is_empty() {
            continue;
        }
        if index == 0 {
            let Some(next) = remainder.strip_prefix(part) else {
                return false;
            };
            remainder = next;
        } else if index == parts.len() - 1 {
            return remainder.ends_with(part);
        } else {
            let Some(position) = remainder.find(part) else {
                return false;
            };
            remainder = &remainder[position + part.len()..];
        }
    }
    pattern.ends_with('*') || remainder.is_empty()
}

fn matches_grids(value: Option<&str>, exact: &BTreeSet<&str>, patterns: &[String]) -> bool {
    if exact.is_empty() && patterns.is_empty() {
        return true;
    }
    value.is_some_and(|observed| {
        exact.contains(observed) || patterns.iter().any(|pattern| wildcard_match(observed, pattern))
    })
}

fn table_column<'a>(batch: &'a RecordBatch, name: &str) -> Fallible<&'a dyn Array> {
    batch
        .column_by_name(name)
        .map(|column| column.as_ref())
        .ok_or_else(|| invalid_input(format!("accepted evidence table has no {name:?} column")))
}

fn optional_string_at<'a>(batch: &'a RecordBatch, name: &str, row: usize) -> Fallible<Option<&'a str>> {
    let column = table_column(batch, name)?;
    let values = column.as_any().downcast_ref::<StringViewArray>().ok_or_else(|| {
        invalid_input(format!(
            "accepted evidence column {name:?} has type {:?}; expected Utf8View",
            column.data_type()
        ))
    })?;
    Ok((!values.is_null(row)).then(|| values.value(row)))
}

fn optional_i64_at(batch: &RecordBatch, name: &str, row: usize) -> Fallible<Option<i64>> {
    let column = table_column(batch, name)?;
    let values = column.as_any().downcast_ref::<Int64Array>().ok_or_else(|| {
        invalid_input(format!(
            "accepted evidence column {name:?} has type {:?}; expected Int64",
            column.data_type()
        ))
    })?;
    Ok((!values.is_null(row)).then(|| values.value(row)))
}

fn optional_f64_at(batch: &RecordBatch, name: &str, row: usize) -> Fallible<Option<f64>> {
    let column = table_column(batch, name)?;
    let values = column.as_any().downcast_ref::<Float64Array>().ok_or_else(|| {
        invalid_input(format!(
            "accepted evidence column {name:?} has type {:?}; expected Float64",
            column.data_type()
        ))
    })?;
    Ok((!values.is_null(row)).then(|| values.value(row)))
}

fn optional_bool_at(batch: &RecordBatch, name: &str, row: usize) -> Fallible<Option<bool>> {
    let column = table_column(batch, name)?;
    let values = column.as_any().downcast_ref::<BooleanArray>().ok_or_else(|| {
        invalid_input(format!(
            "accepted evidence column {name:?} has type {:?}; expected Boolean",
            column.data_type()
        ))
    })?;
    Ok((!values.is_null(row)).then(|| values.value(row)))
}

fn validate_accepted_matrix_manifest_identity(manifest: &Value) -> Fallible<()> {
    let object = manifest
        .as_object()
        .ok_or_else(|| invalid_input("accepted evidence manifest must be a JSON object"))?;
    let schema = object.get("schema").and_then(Value::as_str);
    match schema {
        Some("fern.accepted_matrix.v1") => Ok(()),
        Some("fern.accepted_matrix.v2") => {
            // CHG 2026-08-28: the C# accepted-matrix export (csharp/Fern.Registry/
            // AcceptedMatrix.cs, commit 9b8e2b31) names its own Arrow writer. The
            // two writers emit different IPC bytes for the same rows (8,754 vs
            // 9,626 on the golden fixture) and identical tables, so the field
            // records who wrote the bytes rather than claiming Go's string.
            if !matches!(
                object.get("table_encoding").and_then(Value::as_str),
                Some("arrow-ipc-file.apache-arrow-go.v18.7.0") | Some("arrow-ipc-file.apache-arrow-csharp.v18.1.0")
            ) {
                return Err(invalid_input(
                    "accepted evidence manifest v2 has unsupported table_encoding",
                ));
            }
            if object.get("missing_run_row_policy").and_then(Value::as_str) != Some("named_in_manifest") {
                return Err(invalid_input(
                    "accepted evidence manifest v2 has unsupported missing_run_row_policy",
                ));
            }
            let lane_digests = object
                .get("lane_content_digests")
                .and_then(Value::as_object)
                .ok_or_else(|| invalid_input("accepted evidence manifest v2 has invalid lane_content_digests"))?;
            if lane_digests.values().any(|digest| !digest.is_string()) {
                return Err(invalid_input(
                    "accepted evidence manifest v2 has invalid lane_content_digests",
                ));
            }
            if let Some(run_store_digests) = object.get("run_store_content_digests") {
                let run_store_digests = run_store_digests.as_object().ok_or_else(|| {
                    invalid_input("accepted evidence manifest v2 has invalid run_store_content_digests")
                })?;
                if run_store_digests.values().any(|digest| !digest.is_string()) {
                    return Err(invalid_input(
                        "accepted evidence manifest v2 has invalid run_store_content_digests",
                    ));
                }
            }
            Ok(())
        }
        _ => Err(invalid_input(format!(
            "accepted evidence manifest has unsupported schema {schema:?}"
        ))),
    }
}

#[cfg(feature = "registry")]
fn check_accepted_export_via_home(
    fern_home: &Path,
    manifest_path: &Path,
    campaign_root: &Path,
    source_lanes: &str,
) -> Fallible<i32> {
    const OUTPUT_LIMIT: u64 = 1024 * 1024;
    const DEADLINE: Duration = Duration::from_secs(30);

    let mut child = Command::new(fern_home)
        .args(["check-accepted-export", "--manifest"])
        .arg(manifest_path)
        .arg("--campaign-root")
        .arg(campaign_root)
        .arg("--lanes")
        .arg(source_lanes)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| invalid_input(format!("cannot start {}: {error}", fern_home.display())))?;
    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| invalid_input("fern-home freshness stdout is unavailable"))?;
    let stderr = child
        .stderr
        .take()
        .ok_or_else(|| invalid_input("fern-home freshness stderr is unavailable"))?;
    let stdout_reader = std::thread::spawn(move || read_bounded(stdout, OUTPUT_LIMIT));
    let stderr_reader = std::thread::spawn(move || read_bounded(stderr, OUTPUT_LIMIT));
    let started = Instant::now();
    let status = loop {
        if let Some(status) = child.try_wait()? {
            break status;
        }
        if started.elapsed() >= DEADLINE {
            let _ = child.kill();
            let _ = child.wait();
            let _ = stdout_reader.join();
            let stderr = stderr_reader.join().ok().and_then(Result::ok).unwrap_or_default();
            return Err(invalid_input(format!(
                "fern-home check-accepted-export exceeded 30 seconds: {}",
                String::from_utf8_lossy(&stderr).trim()
            )));
        }
        std::thread::sleep(Duration::from_millis(10));
    };
    let stdout = stdout_reader
        .join()
        .map_err(|_| invalid_input("fern-home freshness stdout reader panicked"))??;
    let stderr = stderr_reader
        .join()
        .map_err(|_| invalid_input("fern-home freshness stderr reader panicked"))??;
    let code = status
        .code()
        .ok_or_else(|| invalid_input("fern-home check-accepted-export ended without an exit code"))?;
    match code {
        0 if stdout.is_empty() => Ok(0),
        0 => Err(invalid_input(
            "fern-home check-accepted-export returned output for a current manifest",
        )),
        3 => {
            let projection = std::str::from_utf8(&stdout)
                .map_err(|_| invalid_input("fern-home check-accepted-export returned non-UTF-8 stale details"))?;
            if projection.trim().is_empty() {
                return Err(invalid_input(
                    "fern-home check-accepted-export returned stale without a reason",
                ));
            }
            print!("{projection}");
            Ok(3)
        }
        _ => Err(invalid_input(format!(
            "fern-home check-accepted-export refused with {code}: {}",
            String::from_utf8_lossy(&stderr).trim()
        ))),
    }
}

fn resolve_accepted_matrix(
    root: &Path,
    table_name: &str,
    manifest_name: &str,
    query: &AcceptedMatrixQueryV1,
    cohort_output: &Path,
    fern_home: &Path,
) -> Fallible<ResolvedAnalysisEvidenceV1> {
    let table = hashed_file(root, table_name, None)?;
    let manifest = hashed_file(root, manifest_name, None)?;
    let manifest_value: Value = serde_json::from_slice(&std::fs::read(root.join(&manifest.path))?)?;
    validate_accepted_matrix_manifest_identity(&manifest_value)?;
    let recorded_sha = manifest_value.get("table_sha256").and_then(Value::as_str);
    if recorded_sha != Some(table.sha256.as_str()) {
        return Err(invalid_input(format!(
            "accepted evidence table {} does not match its manifest",
            table.path
        )));
    }

    let mut batch = read_ipc_record_batch(&root.join(&table.path))?;
    let grids = query.grids.iter().map(String::as_str).collect::<BTreeSet<_>>();
    let datasets = query.datasets.iter().map(String::as_str).collect::<BTreeSet<_>>();
    let models = query.models.iter().map(String::as_str).collect::<BTreeSet<_>>();
    let mask_values = (0..batch.num_rows())
        .map(|row| {
            Ok(
                matches_grids(optional_string_at(&batch, "preset", row)?, &grids, &query.grid_patterns)
                    && matches_set(optional_string_at(&batch, "dataset", row)?, &datasets)
                    && matches_set(optional_string_at(&batch, "model_type", row)?, &models)
                    && matches_text(optional_string_at(&batch, "noise_law", row)?, &query.noise_law)
                    && matches_text(
                        optional_string_at(&batch, "split_treatment_scheme", row)?,
                        &query.split_treatment_scheme,
                    )
                    && matches_text(
                        optional_string_at(&batch, "runtime_device", row)?,
                        &query.runtime_device,
                    )
                    && (query.include_replicas || optional_i64_at(&batch, "replica_ordinal", row)? == Some(0)),
            )
        })
        .collect::<Fallible<Vec<bool>>>()?;
    let mask = BooleanArray::from(mask_values);
    batch = filter_record_batch(&batch, &mask)?;
    if batch.num_rows() == 0 {
        return Err(invalid_input(
            "the declared scientific query resolves to zero evidence rows",
        ));
    }
    let unsigned = (0..batch.num_rows())
        .map(|row| optional_string_at(&batch, "verification_state", row))
        .collect::<Fallible<Vec<_>>>()?
        .into_iter()
        .filter(|state| matches!(state, Some("unsigned" | "changed_since_signing")))
        .count();
    if unsigned > 0 {
        validate_reason(
            &query.accept_unsigned_reason,
            &format!("accepting {unsigned} unsigned evidence rows"),
        )?;
    }

    // Freshness is scoped to the lanes that supplied this exact cohort. Lane
    // is provenance here, never a selection axis: Rust discovers it only
    // after the scientific query has resolved.
    let mut source_lanes = (0..batch.num_rows())
        .map(|row| optional_string_at(&batch, "lane", row).map(|value| value.map(str::to_string)))
        .collect::<Fallible<Vec<_>>>()?
        .into_iter()
        .flatten()
        .collect::<Vec<_>>();
    source_lanes.sort();
    source_lanes.dedup();
    let campaign_root_value = manifest_value
        .get("campaign_root")
        .and_then(Value::as_str)
        .ok_or_else(|| invalid_input("accepted evidence manifest has no campaign_root"))?;
    let (campaign_root, _) = relative_path(root, campaign_root_value)?;
    let manifest_path = root.join(&manifest.path);
    let source_lane_list = source_lanes.join(",");
    #[cfg(feature = "registry")]
    {
        let freshness = check_accepted_export_via_home(fern_home, &manifest_path, &campaign_root, &source_lane_list)?;
        if freshness == 3 {
            validate_reason(
                &query.accept_stale_export_reason,
                "using an accepted evidence export whose selected source lanes have changed",
            )?;
        } else if freshness != 0 {
            return Err(invalid_input(format!(
                "accepted evidence freshness check returned unsupported status {freshness}"
            )));
        }
    }
    #[cfg(not(feature = "registry"))]
    let _ = (manifest_path, campaign_root, source_lane_list, fern_home);

    // A query that still contains two rows for one scientific cell is not one
    // estimand. This is the Rust equivalent of `one_estimand`'s fail-closed
    // duplicate check, before Python receives any rows.
    let mut scientific_cells = BTreeSet::new();
    for row in 0..batch.num_rows() {
        let grid_stratum = match query.grid_multiplicity {
            GridMultiplicityV1::OneScientificCell => None,
            GridMultiplicityV1::DistinctGridStrata => optional_string_at(&batch, "preset", row)?,
        };
        let cell = json!([
            grid_stratum,
            optional_string_at(&batch, "dataset", row)?,
            optional_string_at(&batch, "model_type", row)?,
            optional_f64_at(&batch, "noise_sigma", row)?,
            optional_i64_at(&batch, "trajectory_draw_ordinal", row)?,
            optional_i64_at(&batch, "noise_draw_ordinal", row)?,
            optional_string_at(&batch, "noise_law", row)?,
            optional_string_at(&batch, "split_treatment_scheme", row)?,
            optional_f64_at(&batch, "noise_ar1_phi", row)?,
            optional_bool_at(&batch, "relative_dose", row)?,
            optional_string_at(&batch, "checkpoint_execution_mode", row)?,
            optional_string_at(&batch, "runtime_device", row)?,
            optional_i64_at(&batch, "replica_ordinal", row)?,
            optional_string_at(&batch, "model_config_hash", row)?,
        ]);
        if !scientific_cells.insert(serde_json::to_string(&cell)?) {
            return Err(invalid_input(format!(
                "the declared query resolves one scientific cell more than once: {cell}"
            )));
        }
    }

    let mut run_hashes = (0..batch.num_rows())
        .map(|row| optional_string_at(&batch, "run_hash", row).map(|value| value.map(str::to_string)))
        .collect::<Fallible<Vec<_>>>()?
        .into_iter()
        .flatten()
        .collect::<Vec<_>>();
    run_hashes.sort();
    run_hashes.dedup();

    // A request owns the exact table bytes Python will read. Source-row order
    // is preserved and pinned by both source and cohort hashes.
    let bytes = record_batch_ipc_bytes(&batch)?;
    write_file_durably(cohort_output, &bytes)?;
    let cohort_relative = cohort_output
        .strip_prefix(root)
        .map_err(|_| invalid_input("cohort output is outside repository"))?;
    let cohort = hashed_file(root, &cohort_relative.to_string_lossy(), None)?;
    Ok(ResolvedAnalysisEvidenceV1::AcceptedMatrix {
        source_table: table,
        source_manifest: manifest,
        query: query.clone(),
        cohort_table: cohort,
        row_count: batch.num_rows() as u64,
        run_hashes,
    })
}

pub fn declare_analysis(
    root: &Path,
    declaration_path: &Path,
    request_path: &Path,
    fern_home: &Path,
) -> Fallible<DeclaredAnalysisRequestV1> {
    let root = std::fs::canonicalize(root)?;
    let declaration_path = if declaration_path.is_absolute() {
        declaration_path.to_path_buf()
    } else {
        root.join(declaration_path)
    };
    let request_path = if request_path.is_absolute() {
        request_path.to_path_buf()
    } else {
        root.join(request_path)
    };
    let declaration = checked_from_slice::<AnalysisDeclarationV1>(&std::fs::read(&declaration_path)?)?;
    let expected_source = format!("python/{}.py", declaration.generator.replace('.', "/"));
    if !declaration.generator.starts_with("fern_analysis.")
        || declaration.implementation_sources.first() != Some(&expected_source)
    {
        return Err(invalid_input(format!(
            "generator must be a fern_analysis module and implementation_sources must name its source first: expected {expected_source:?}"
        )));
    }
    let fingerprint = implementation_fingerprint(&root, &declaration.implementation_sources)?;
    let evidence = match &declaration.evidence {
        AnalysisEvidenceDeclarationV1::Artifacts { paths } => {
            let inputs = resolved_artifact_inputs(&root, paths)?;
            ResolvedAnalysisEvidenceV1::Artifacts { inputs }
        }
        AnalysisEvidenceDeclarationV1::AcceptedMatrix { table, manifest, query } => {
            let cohort_path = request_path.with_extension("cohort.ipc");
            if !cohort_path.starts_with(&root) {
                return Err(invalid_input(
                    "analysis request and cohort must be written inside the repository",
                ));
            }
            resolve_accepted_matrix(&root, table, manifest, query, &cohort_path, fern_home)?
        }
        AnalysisEvidenceDeclarationV1::AcceptedMatrixWithArtifacts {
            table,
            manifest,
            query,
            paths,
        } => {
            let cohort_path = request_path.with_extension("cohort.ipc");
            if !cohort_path.starts_with(&root) {
                return Err(invalid_input(
                    "analysis request and cohort must be written inside the repository",
                ));
            }
            let resolved = resolve_accepted_matrix(&root, table, manifest, query, &cohort_path, fern_home)?;
            let ResolvedAnalysisEvidenceV1::AcceptedMatrix {
                source_table,
                source_manifest,
                query,
                cohort_table,
                row_count,
                run_hashes,
            } = resolved
            else {
                unreachable!("accepted-matrix resolver returned a different evidence kind")
            };
            ResolvedAnalysisEvidenceV1::AcceptedMatrixWithArtifacts {
                source_table,
                source_manifest,
                query,
                cohort_table,
                row_count,
                run_hashes,
                inputs: resolved_artifact_inputs(&root, paths)?,
            }
        }
    };
    if declaration.promised_outputs.is_empty() {
        return Err(invalid_input("an analysis must promise at least one output"));
    }
    let mut seen = BTreeSet::new();
    for output in &declaration.promised_outputs {
        let (_, normalized) = future_relative_path(&root, &output.path)?;
        if !seen.insert(normalized) {
            return Err(invalid_input("analysis promises the same output more than once"));
        }
    }
    let mut request = DeclaredAnalysisRequestV1 {
        schema: DeclaredAnalysisRequestV1::SCHEMA_ID.to_string(),
        request_hash: String::new(),
        analysis_schema: declaration.analysis_schema,
        kind: declaration.kind,
        generator: declaration.generator,
        implementation_fingerprint: fingerprint,
        parameters: declaration.parameters,
        evidence,
        promised_outputs: declaration.promised_outputs,
    };
    request.request_hash = request_hash(&request)?;
    request.checked_value()?;
    write_json_file(&request_path, &request)?;
    Ok(request)
}

/// One already-frozen Python statistic execution followed by Go Home admission.
///
/// The request must exist before this boundary. Python may only compute and
/// report its promised files; Go Home reopens them and is the only Registry
/// writer. Both the checked-in declaration route and dynamic run-set selectors
/// use this same hand-off.
#[cfg(feature = "registry")]
pub struct PythonStatisticExecution {
    pub root: PathBuf,
    pub request: DeclaredAnalysisRequestV1,
    pub request_path: PathBuf,
    pub report_path: PathBuf,
    pub receipt_path: PathBuf,
    pub registry_path: PathBuf,
    pub python: Option<PathBuf>,
    pub fern_home: PathBuf,
}

/// A run-set selector has already written one immutable JSON input. Freeze that
/// file with the Python source bytes, execute the numerical module, and hand
/// the reported output to Go Home. This is the dynamic counterpart of a
/// checked-in analysis declaration; it does not select runs or compute values.
#[cfg(feature = "registry")]
pub struct DynamicArtifactStatistic {
    pub root: PathBuf,
    pub name: String,
    pub analysis_schema: String,
    pub kind: String,
    pub generator: String,
    pub implementation_sources: Vec<String>,
    pub parameters: Value,
    pub input_path: PathBuf,
    pub input_schema: String,
    pub additional_input_paths: Vec<PathBuf>,
    pub output_schema: String,
    pub registry_path: PathBuf,
    pub python: Option<PathBuf>,
    pub fern_home: PathBuf,
}

#[cfg(feature = "registry")]
pub fn execute_dynamic_artifact_statistic(statistic: DynamicArtifactStatistic) -> Fallible<CheckedAnalysisReceiptV1> {
    let root = std::fs::canonicalize(&statistic.root)?;
    let input_relative = statistic
        .input_path
        .strip_prefix(&root)
        .map_err(|_| invalid_input("dynamic statistic input must stay inside the repository"))?
        .to_string_lossy()
        .replace('\\', "/");
    let input = hashed_file(&root, &input_relative, Some(&statistic.input_schema))?;
    let mut evidence_paths = vec![input_relative];
    let mut evidence_digests = vec![input.sha256.clone()];
    for path in &statistic.additional_input_paths {
        let relative = path
            .strip_prefix(&root)
            .map_err(|_| invalid_input("dynamic statistic evidence must stay inside the repository"))?
            .to_string_lossy()
            .replace('\\', "/");
        let evidence = hashed_file(&root, &relative, None)?;
        evidence_digests.push(evidence.sha256);
        evidence_paths.push(relative);
    }
    evidence_paths.sort();
    evidence_paths.dedup();
    evidence_digests.sort();
    let implementation = implementation_fingerprint(&root, &statistic.implementation_sources)?;
    let identity = crate::sha256_hex(&fern_json_contract::canonical_json_bytes(&json!({
        "name": &statistic.name,
        "evidence_sha256": &evidence_digests,
        "implementation_fingerprint": &implementation,
        "parameters": &statistic.parameters,
    }))?);
    let stem = format!("{}-{}", statistic.name, &identity[..16]);
    let directory = root.join("results/paper_campaign/runtime-statistics");
    std::fs::create_dir_all(&directory)?;
    let declaration_path = directory.join(format!("{stem}.declaration.json"));
    let request_path = directory.join(format!("{stem}.request.json"));
    let report_path = directory.join(format!("{stem}.report.json"));
    let receipt_path = directory.join(format!("{stem}.checked.json"));
    let output_path = directory.join(format!("{stem}.result.json"));
    let output_relative = output_path
        .strip_prefix(&root)
        .expect("runtime statistics directory is below root")
        .to_string_lossy()
        .replace('\\', "/");
    let declaration = AnalysisDeclarationV1 {
        schema: "fern.declared_analysis.v1".to_string(),
        analysis_schema: statistic.analysis_schema,
        kind: statistic.kind,
        implementation: None,
        generator: statistic.generator,
        implementation_sources: statistic.implementation_sources,
        parameters: statistic.parameters,
        evidence: AnalysisEvidenceDeclarationV1::Artifacts { paths: evidence_paths },
        promised_outputs: vec![AnalysisOutputSpecV1 {
            path: output_relative,
            schema_id: Some(statistic.output_schema),
        }],
    };
    crate::write_json_file(&declaration_path, &declaration)?;
    let request = declare_analysis(&root, &declaration_path, &request_path, &statistic.fern_home)?;
    execute_python_statistic(PythonStatisticExecution {
        root,
        request,
        request_path,
        report_path,
        receipt_path,
        registry_path: statistic.registry_path,
        python: statistic.python,
        fern_home: statistic.fern_home,
    })
}

#[cfg(feature = "registry")]
pub fn execute_python_statistic(execution: PythonStatisticExecution) -> Fallible<CheckedAnalysisReceiptV1> {
    let root = std::fs::canonicalize(&execution.root)?;
    let resolve = |path: &Path| {
        if path.is_absolute() {
            path.to_path_buf()
        } else {
            root.join(path)
        }
    };
    let request_path = resolve(&execution.request_path);
    let report_path = resolve(&execution.report_path);
    let report_name = report_path
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("analysis.report.json");
    let fresh_report_path = report_path.with_file_name(format!("{report_name}.{}.partial", std::process::id()));
    if fresh_report_path.exists() {
        return Err(invalid_input(format!(
            "analysis report staging path already exists: {}; remove it only after checking whether an earlier process is live",
            fresh_report_path.display()
        )));
    }
    let existing_python_path = std::env::var_os("PYTHONPATH");
    let mut python_paths = vec![root.join("python")];
    if let Some(existing) = existing_python_path {
        python_paths.extend(std::env::split_paths(&existing));
    }
    let python_path = std::env::join_paths(python_paths)
        .map_err(|error| invalid_input(format!("analysis Python path cannot be constructed: {error}")))?;
    let python = crate::python_interpreter(execution.python.as_deref())?;
    let status = Command::new(&python)
        .arg("-m")
        .arg("fern_analysis")
        .arg(
            execution
                .request
                .generator
                .strip_prefix("fern_analysis.")
                .unwrap_or(&execution.request.generator)
                .replace('_', "-"),
        )
        .arg("--request")
        .arg(&request_path)
        .arg("--report")
        .arg(&fresh_report_path)
        .current_dir(&root)
        .env("FERN_ANALYSIS_REQUEST", &request_path)
        .env("FERN_ANALYSIS_REPORT", &fresh_report_path)
        .env("PYTHONPATH", python_path)
        .env("MPLCONFIGDIR", "/tmp/fern-matplotlib-cache")
        .status()
        .map_err(|error| invalid_input(format!("analysis Python could not start: {error}")))?;
    if !status.success() {
        let _ = std::fs::remove_file(&fresh_report_path);
        return Err(invalid_input(format!(
            "analysis Python exited {status}; outputs were not checked or registered"
        )));
    }
    let fresh_report = std::fs::read(&fresh_report_path).map_err(|error| {
        invalid_input(format!(
            "analysis Python exited successfully but wrote no fresh report: {error}"
        ))
    })?;
    crate::write_file_durably(&report_path, &fresh_report)?;
    let _ = std::fs::remove_file(&fresh_report_path);
    admit_statistic_via_home(
        &execution.fern_home,
        &root,
        &request_path,
        &report_path,
        &execution.registry_path,
        &execution.receipt_path,
    )
}

#[cfg(feature = "registry")]
pub fn admit_statistic_via_home(
    fern_home: &Path,
    root: &Path,
    request_path: &Path,
    report_path: &Path,
    registry_path: &Path,
    receipt_path: &Path,
) -> Fallible<CheckedAnalysisReceiptV1> {
    const OUTPUT_LIMIT: u64 = 1024 * 1024;
    const DEADLINE: Duration = Duration::from_secs(30);

    let root = std::fs::canonicalize(root)?;
    let resolve = |path: &Path| {
        if path.is_absolute() {
            path.to_path_buf()
        } else {
            root.join(path)
        }
    };
    let registry_path = resolve(registry_path);
    let receipt_path = resolve(receipt_path);
    let request = StatisticAdmissionRequestV1 {
        schema: "fern.analysis_check.v1",
        root: root.to_string_lossy().into_owned(),
        request_path: resolve(request_path).to_string_lossy().into_owned(),
        report_path: resolve(report_path).to_string_lossy().into_owned(),
    };
    let mut child = Command::new(fern_home)
        .args(["registry-admit-statistic", "--registry"])
        .arg(&registry_path)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| invalid_input(format!("cannot start {}: {error}", fern_home.display())))?;
    serde_json::to_writer(
        child
            .stdin
            .as_mut()
            .ok_or_else(|| invalid_input("fern-home did not expose its analysis request input"))?,
        &request,
    )?;
    drop(child.stdin.take());

    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| invalid_input("fern-home stdout is unavailable"))?;
    let stderr = child
        .stderr
        .take()
        .ok_or_else(|| invalid_input("fern-home stderr is unavailable"))?;
    let stdout_reader = std::thread::spawn(move || read_bounded(stdout, OUTPUT_LIMIT));
    let stderr_reader = std::thread::spawn(move || read_bounded(stderr, OUTPUT_LIMIT));
    let started = Instant::now();
    let status = loop {
        if let Some(status) = child.try_wait()? {
            break status;
        }
        if started.elapsed() >= DEADLINE {
            let _ = child.kill();
            let _ = child.wait();
            let _ = stdout_reader.join();
            let stderr = stderr_reader.join().ok().and_then(Result::ok).unwrap_or_default();
            return Err(invalid_input(format!(
                "fern-home registry-admit-statistic exceeded 30 seconds: {}",
                String::from_utf8_lossy(&stderr).trim()
            )));
        }
        std::thread::sleep(Duration::from_millis(10));
    };
    let stdout = stdout_reader
        .join()
        .map_err(|_| invalid_input("fern-home stdout reader panicked"))??;
    let stderr = stderr_reader
        .join()
        .map_err(|_| invalid_input("fern-home stderr reader panicked"))??;
    if !status.success() {
        return Err(invalid_input(format!(
            "fern-home registry-admit-statistic refused: {}",
            String::from_utf8_lossy(&stderr).trim()
        )));
    }
    let receipt = checked_from_slice::<CheckedAnalysisReceiptV1>(&stdout)?;
    write_file_durably(&receipt_path, &stdout)?;
    Ok(receipt)
}

#[cfg(feature = "registry")]
fn read_bounded(mut reader: impl Read, limit: u64) -> Fallible<Vec<u8>> {
    let mut bytes = Vec::new();
    reader.by_ref().take(limit + 1).read_to_end(&mut bytes)?;
    if bytes.len() as u64 > limit {
        return Err(invalid_input(format!("fern-home output exceeded {limit} bytes")));
    }
    Ok(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn public_contracts_generate_their_schema_ids() {
        for contract in json_contracts() {
            let schema = (contract.schema_document)().unwrap();
            assert_eq!(schema["$id"], contract.schema_id);
        }
    }

    #[test]
    fn analysis_query_has_no_lane_selector() {
        // This type is intentionally not a top-level JsonContract, so inspect
        // its generated shape directly.
        let schema = serde_json::to_value(schemars::schema_for!(AcceptedMatrixQueryV1)).unwrap();
        let properties = schema["properties"].as_object().unwrap();
        assert!(!properties.contains_key("lane"));
        assert!(!properties.contains_key("lanes"));
    }

    #[test]
    fn accepted_matrix_manifest_dual_reads_rust_v1_and_strict_go_v2() {
        validate_accepted_matrix_manifest_identity(&json!({
            "schema": "fern.accepted_matrix.v1"
        }))
        .unwrap();
        validate_accepted_matrix_manifest_identity(&json!({
            "schema": "fern.accepted_matrix.v2",
            "table_encoding": "arrow-ipc-file.apache-arrow-go.v18.7.0",
            "missing_run_row_policy": "named_in_manifest",
            "lane_content_digests": {"lane_a": "sha256:abc"},
            "run_store_content_digests": {
                "lane_a": "sha256:abc",
                "standalone": "sha256:def"
            }
        }))
        .unwrap();

        // The C# export names its own writer; the table it wraps is the same.
        validate_accepted_matrix_manifest_identity(&json!({
            "schema": "fern.accepted_matrix.v2",
            "table_encoding": "arrow-ipc-file.apache-arrow-csharp.v18.1.0",
            "missing_run_row_policy": "named_in_manifest",
            "lane_content_digests": {"lane_a": "sha256:abc"},
            "run_store_content_digests": {
                "lane_a": "sha256:abc",
                "standalone": "sha256:def"
            }
        }))
        .unwrap();
        for malformed in [
            json!({"schema": "fern.accepted_matrix.v3"}),
            json!({
                "schema": "fern.accepted_matrix.v2",
                "table_encoding": "arrow-ipc-stream",
                "missing_run_row_policy": "named_in_manifest",
                "lane_content_digests": {}
            }),
            json!({
                "schema": "fern.accepted_matrix.v2",
                "table_encoding": "arrow-ipc-file.apache-arrow-go.v18.7.0",
                "missing_run_row_policy": "silently_drop",
                "lane_content_digests": {}
            }),
            json!({
                "schema": "fern.accepted_matrix.v2",
                "table_encoding": "arrow-ipc-file.apache-arrow-go.v18.7.0",
                "missing_run_row_policy": "named_in_manifest",
                "lane_content_digests": null
            }),
            json!({
                "schema": "fern.accepted_matrix.v2",
                "table_encoding": "arrow-ipc-file.apache-arrow-go.v18.7.0",
                "missing_run_row_policy": "named_in_manifest",
                "lane_content_digests": {},
                "run_store_content_digests": null
            }),
            json!({
                "schema": "fern.accepted_matrix.v2",
                "table_encoding": "arrow-ipc-file.apache-arrow-go.v18.7.0",
                "missing_run_row_policy": "named_in_manifest",
                "lane_content_digests": {},
                "run_store_content_digests": {"standalone": 7}
            }),
        ] {
            assert!(validate_accepted_matrix_manifest_identity(&malformed).is_err());
        }
    }

    #[cfg(feature = "registry")]
    #[test]
    fn rust_accepted_freshness_caller_crosses_the_go_home_process() {
        let root = std::env::temp_dir().join(format!(
            "fern-accepted-freshness-go-home-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let campaign_root = root.join("campaign");
        std::fs::create_dir_all(&campaign_root).unwrap();
        let manifest_path = root.join("accepted.json");
        std::fs::write(
            &manifest_path,
            serde_json::to_vec(&json!({
                "schema": "fern.accepted_matrix.v2",
                "table_encoding": "arrow-ipc-file.apache-arrow-go.v18.7.0",
                "missing_run_row_policy": "named_in_manifest",
                "lane_content_digests": {}
            }))
            .unwrap(),
        )
        .unwrap();
        let home = root.join("fern-home");
        let control_root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../tools/fern-control");
        let status = Command::new("go")
            .args(["build", "-o"])
            .arg(&home)
            .arg("./cmd/fern-home")
            .current_dir(control_root)
            .status()
            .unwrap();
        assert!(status.success(), "go build ./cmd/fern-home failed");

        assert_eq!(
            check_accepted_export_via_home(&home, &manifest_path, &campaign_root, "").unwrap(),
            0
        );

        std::fs::write(
            &manifest_path,
            serde_json::to_vec(&json!({"schema": "fern.accepted_matrix.v3"})).unwrap(),
        )
        .unwrap();
        let after_start_error = check_accepted_export_via_home(&home, &manifest_path, &campaign_root, "").unwrap_err();
        assert!(after_start_error.to_string().contains("refused with 1"));

        let before_start_error =
            check_accepted_export_via_home(&root.join("missing-fern-home"), &manifest_path, &campaign_root, "")
                .unwrap_err();
        assert!(before_start_error.to_string().contains("cannot start"));
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn scientific_grid_patterns_are_anchored_and_have_only_one_wildcard_kind() {
        assert!(wildcard_match("rossler_base_tirex_h64", "rossler_base_*_h64"));
        assert!(!wildcard_match("x_rossler_base_tirex_h64", "rossler_base_*_h64"));
        assert!(!wildcard_match("rossler_base_tirex_h64_extra", "rossler_base_*_h64"));
    }

    #[test]
    fn repository_paths_do_not_change_scientific_analysis_identity() {
        let file = |path: &str| HashedAnalysisFileV1 {
            path: path.to_string(),
            sha256: "same-bytes".to_string(),
            bytes: 12,
            schema_id: Some("fern.input.v1".to_string()),
        };
        let first = ResolvedAnalysisEvidenceV1::Artifacts {
            inputs: vec![file("machine-a/input.json")],
        };
        let second = ResolvedAnalysisEvidenceV1::Artifacts {
            inputs: vec![file("machine-b/input.json")],
        };
        assert_eq!(first.analysis_query_identity(), second.analysis_query_identity());
    }

    #[test]
    fn combined_evidence_identity_includes_cohort_and_artifact_bytes_not_paths() {
        let evidence = ResolvedAnalysisEvidenceV1::AcceptedMatrixWithArtifacts {
            source_table: HashedAnalysisFileV1 {
                path: "accepted.ipc".to_string(),
                sha256: "source".to_string(),
                bytes: 1,
                schema_id: None,
            },
            source_manifest: HashedAnalysisFileV1 {
                path: "accepted.json".to_string(),
                sha256: "manifest".to_string(),
                bytes: 1,
                schema_id: None,
            },
            query: AcceptedMatrixQueryV1::default(),
            cohort_table: HashedAnalysisFileV1 {
                path: "requests/a.cohort.ipc".to_string(),
                sha256: "cohort".to_string(),
                bytes: 2,
                schema_id: None,
            },
            row_count: 3,
            run_hashes: vec!["run-a".to_string()],
            inputs: vec![HashedAnalysisFileV1 {
                path: "results/upstream.json".to_string(),
                sha256: "artifact".to_string(),
                bytes: 4,
                schema_id: Some("fern.upstream.v1".to_string()),
            }],
        };
        let identity = evidence.analysis_query_identity();
        assert_eq!(identity["source"], "accepted_matrix_with_artifacts");
        assert_eq!(identity["cohort_sha256"], "cohort");
        assert_eq!(identity["inputs"][0]["sha256"], "artifact");
        assert!(
            serde_json::to_string(&identity)
                .unwrap()
                .find("results/upstream.json")
                .is_none()
        );
        assert_eq!(
            serde_json::to_string(&identity).unwrap(),
            r#"{"cohort_sha256":"cohort","inputs":[{"bytes":4,"schema_id":"fern.upstream.v1","sha256":"artifact"}],"query":{"datasets":[],"grid_multiplicity":"one_scientific_cell","grid_patterns":[],"grids":[],"include_replicas":false,"models":[],"noise_law":null,"runtime_device":null,"split_treatment_scheme":null},"row_count":3,"source":"accepted_matrix_with_artifacts"}"#
        );
    }

    #[test]
    fn request_hash_matches_the_go_home_fixture() {
        let request = DeclaredAnalysisRequestV1 {
            schema: DeclaredAnalysisRequestV1::SCHEMA_ID.to_string(),
            request_hash: String::new(),
            analysis_schema: "fern.fixture_analysis.v1".to_string(),
            kind: "fixture_analysis".to_string(),
            generator: "fern_analysis.fixture".to_string(),
            implementation_fingerprint: "sha256:python-source".to_string(),
            parameters: json!({"alpha": 0.05, "threshold": 2.0}),
            evidence: ResolvedAnalysisEvidenceV1::Artifacts {
                inputs: vec![HashedAnalysisFileV1 {
                    path: "input.json".to_string(),
                    sha256: "dce9f1bb1b528f08e85794b8f67c042680fde5c37498cbf66fa2eb2c1c0fe236".to_string(),
                    bytes: 37,
                    schema_id: Some("fern.input.v1".to_string()),
                }],
            },
            promised_outputs: vec![AnalysisOutputSpecV1 {
                path: "results/output.json".to_string(),
                schema_id: Some("fern.result.v1".to_string()),
            }],
        };
        assert_eq!(
            request_hash(&request).unwrap(),
            "analysis-request:55a2d9955085716fdde11574ea03a2ac51bfe3d6532631b8d7e6a32e364cae29"
        );
    }

    #[cfg(feature = "registry")]
    #[test]
    fn rust_front_door_receives_the_go_home_receipt_unchanged() {
        let root = std::env::temp_dir().join(format!(
            "fern-analysis-go-home-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        std::fs::create_dir_all(root.join("results")).unwrap();
        std::fs::write(root.join("input.json"), b"{\"schema\":\"fern.input.v1\",\"value\":1}\n").unwrap();
        std::fs::write(
            root.join("results/output.json"),
            b"{\"schema\":\"fern.result.v1\",\"value\":2}\n",
        )
        .unwrap();
        let input = hashed_file(&root, "input.json", Some("fern.input.v1")).unwrap();
        let output = hashed_file(&root, "results/output.json", Some("fern.result.v1")).unwrap();
        let mut request = DeclaredAnalysisRequestV1 {
            schema: DeclaredAnalysisRequestV1::SCHEMA_ID.to_string(),
            request_hash: String::new(),
            analysis_schema: "fern.fixture_analysis.v1".to_string(),
            kind: "fixture_analysis".to_string(),
            generator: "fern_analysis.fixture".to_string(),
            implementation_fingerprint: "sha256:python-source".to_string(),
            parameters: json!({"alpha": 0.05}),
            evidence: ResolvedAnalysisEvidenceV1::Artifacts { inputs: vec![input] },
            promised_outputs: vec![AnalysisOutputSpecV1 {
                path: output.path.clone(),
                schema_id: output.schema_id.clone(),
            }],
        };
        request.request_hash = request_hash(&request).unwrap();
        let report = ReportedAnalysisResultV1 {
            schema: ReportedAnalysisResultV1::SCHEMA_ID.to_string(),
            request_hash: request.request_hash.clone(),
            outputs: vec![output],
            summary: json!({"rows": 1}),
        };
        write_json_file(&root.join("request.json"), &request).unwrap();
        write_json_file(&root.join("report.json"), &report).unwrap();
        std::fs::write(root.join("lifecycle.db"), b"").unwrap();

        let home = root.join("fern-home");
        let control_root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../tools/fern-control");
        let status = Command::new("go")
            .args(["build", "-o"])
            .arg(&home)
            .arg("./cmd/fern-home")
            .current_dir(control_root)
            .status()
            .unwrap();
        assert!(status.success(), "go build ./cmd/fern-home failed");
        let receipt_path = root.join("checked.json");
        let receipt = admit_statistic_via_home(
            &home,
            &root,
            Path::new("request.json"),
            Path::new("report.json"),
            Path::new("lifecycle.db"),
            &receipt_path,
        )
        .unwrap();
        let receipt_bytes = std::fs::read(&receipt_path).unwrap();
        let reopened = checked_from_slice::<CheckedAnalysisReceiptV1>(&receipt_bytes).unwrap();
        assert_eq!(receipt, reopened);
        assert_eq!(receipt.request_hash, request.request_hash);
        assert_eq!(receipt.knowledge_events.len(), 1);
        std::fs::remove_dir_all(root).unwrap();
    }
}
