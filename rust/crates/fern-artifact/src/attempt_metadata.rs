//! Attempt JSON census: one request, one report, Registry authority, object store.
//!
//! JSON itself is not the problem. Duplicate authority centers are. An Attempt
//! may persist Home intent and the runner claim. Checked decisions live in
//! Registry Home. Compiled Experiment, Stage Plan, and implementation bundle
//! stay content-addressed objects; the Attempt stores only their digests.
//! Remote envelopes are temporary wrappers derived from request and report.

use std::collections::BTreeMap;

use fern_json_contract::JsonContract;
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};

pub const ATTEMPT_METADATA_PROJECTION_SCHEMA: &str = "fern.attempt_metadata_projection.v1";

/// Where one Attempt JSON gets its authority, if it has any.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum AttemptJsonClass {
    /// Home intent. Persist as `request.json` in the worker or Stage spelling.
    HomeIntent,
    /// Runner claim. Persist as `report.json` in the worker or Stage spelling.
    RunnerClaim,
    /// Checked decision. Registry Home is the authority; the file is a leftover sidecar.
    CheckedAuthority,
    /// Immutable payload or content-addressed object. Store bytes; Attempt keeps a digest.
    ImmutablePayload,
    /// Derived or transport-only. Do not treat as a second source of truth.
    DerivedTransport,
}

/// One counted Attempt JSON path and the only class it may hold.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AttemptJsonCensusEntry {
    pub relative_path: &'static str,
    pub class: AttemptJsonClass,
    pub schema_id: Option<&'static str>,
}

/// The fifteen Attempt metadata JSON files. Payload tensors and `inputs/` /
/// `outputs/` objects are classified by prefix, not by growing this list.
pub const ATTEMPT_METADATA_JSON: &[AttemptJsonCensusEntry] = &[
    AttemptJsonCensusEntry {
        relative_path: "worker_request.v1.json",
        class: AttemptJsonClass::HomeIntent,
        schema_id: Some("fern.worker_request.v1"),
    },
    AttemptJsonCensusEntry {
        relative_path: "request/stage_runner_request.v1.json",
        class: AttemptJsonClass::HomeIntent,
        schema_id: Some("fern.stage_runner_request.v1"),
    },
    AttemptJsonCensusEntry {
        relative_path: "worker_result_report.v1.json",
        class: AttemptJsonClass::RunnerClaim,
        schema_id: Some("fern.worker_result_report.v1"),
    },
    AttemptJsonCensusEntry {
        relative_path: "report/stage_runner_report.v1.json",
        class: AttemptJsonClass::RunnerClaim,
        schema_id: Some("fern.stage_runner_report.v1"),
    },
    AttemptJsonCensusEntry {
        relative_path: "attempt_artifact_commit.v1.json",
        class: AttemptJsonClass::CheckedAuthority,
        schema_id: Some("fern.attempt_artifact_commit.v1"),
    },
    AttemptJsonCensusEntry {
        relative_path: "invocation.json",
        class: AttemptJsonClass::CheckedAuthority,
        schema_id: Some("fern.run_invocation.v3"),
    },
    AttemptJsonCensusEntry {
        relative_path: "execution_environment.json",
        class: AttemptJsonClass::ImmutablePayload,
        schema_id: Some("fern.execution_environment.v1"),
    },
    AttemptJsonCensusEntry {
        relative_path: "fern.json",
        class: AttemptJsonClass::ImmutablePayload,
        schema_id: None,
    },
    AttemptJsonCensusEntry {
        relative_path: "request.json",
        class: AttemptJsonClass::ImmutablePayload,
        schema_id: Some("fern.training_worker_request.v1"),
    },
    AttemptJsonCensusEntry {
        relative_path: "scientific_case/scientific_case_files.v1.json",
        class: AttemptJsonClass::ImmutablePayload,
        schema_id: Some("fern.scientific_case_files.v1"),
    },
    AttemptJsonCensusEntry {
        relative_path: "remote_attempt_instructions.v1.json",
        class: AttemptJsonClass::DerivedTransport,
        schema_id: Some("fern.remote_attempt_instructions.v1"),
    },
    AttemptJsonCensusEntry {
        relative_path: "remote_attempt_report.v1.json",
        class: AttemptJsonClass::DerivedTransport,
        schema_id: Some("fern.remote_attempt_report.v1"),
    },
    AttemptJsonCensusEntry {
        relative_path: "heartbeat.json",
        class: AttemptJsonClass::DerivedTransport,
        schema_id: None,
    },
    AttemptJsonCensusEntry {
        relative_path: "run_packet.json",
        class: AttemptJsonClass::DerivedTransport,
        schema_id: Some("fern.run_packet.v1"),
    },
    AttemptJsonCensusEntry {
        relative_path: "registration.json",
        class: AttemptJsonClass::DerivedTransport,
        schema_id: None,
    },
];

const ATTEMPT_PAYLOAD_JSON: &[&str] = &[
    "forecast_calibration.json",
    "calibration.json",
    "hist.json",
    "result.json",
    "logs/training_worker_result.json",
    "evidence/quantile/v1/manifest.json",
    "evidence/quantile/v1/checked.json",
    "evidence/quantile/v1/diagnostics.json",
    "quantile_diag.json",
    "diagnostics.json",
];

/// Classify one Attempt-relative JSON path.
///
/// Unknown paths are refused so a new authoritative sidecar cannot appear by
/// accident. Declared `inputs/` and `outputs/` objects are payloads.
pub fn classify_attempt_json(relative_path: &str) -> Result<AttemptJsonClass, String> {
    let relative_path = relative_path.trim_start_matches("./");
    if relative_path.is_empty() || relative_path.contains('\\') || relative_path.contains("..") {
        return Err(format!(
            "Attempt JSON path {relative_path:?} is not a workspace-relative file"
        ));
    }
    if let Some(entry) = ATTEMPT_METADATA_JSON
        .iter()
        .find(|entry| entry.relative_path == relative_path)
    {
        return Ok(entry.class);
    }
    if ATTEMPT_PAYLOAD_JSON.contains(&relative_path)
        || relative_path.starts_with("inputs/")
        || relative_path.starts_with("outputs/")
        || relative_path.starts_with("scientific_case/")
    {
        return Ok(AttemptJsonClass::ImmutablePayload);
    }
    Err(format!(
        "new authoritative Attempt sidecar {relative_path:?} is refused; persist Home intent as request, the runner claim as report, checked decisions in Registry, or mark the file derived/transport-only"
    ))
}

/// One observed Attempt JSON, identified by path and digest, not by copied object bytes.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct AttemptJsonRecordV1 {
    pub relative_path: String,
    pub class: AttemptJsonClass,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub schema_id: Option<String>,
    pub content_digest: String,
}

/// Content-addressed object an Attempt names but must not rewrite.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct AttemptObjectRefV1 {
    pub kind: String,
    pub content_digest: String,
}

/// Registry Home's checked facts for this Attempt. Digests only.
#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct AttemptCheckedAuthorityV1 {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub receipt_digest: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub commit_digest: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub stage_plan_digest: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub compiled_experiment_digest: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub implementation_digest: Option<String>,
}

/// Merged read-only view of one Attempt's JSON responsibilities.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct AttemptMetadataProjectionV1 {
    pub schema: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub attempt_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub home_intent: Option<AttemptJsonRecordV1>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub runner_claim: Option<AttemptJsonRecordV1>,
    pub checked_authority: AttemptCheckedAuthorityV1,
    pub objects: Vec<AttemptObjectRefV1>,
    pub derived_transport: Vec<AttemptJsonRecordV1>,
}

impl JsonContract for AttemptMetadataProjectionV1 {
    const SCHEMA_ID: &'static str = ATTEMPT_METADATA_PROJECTION_SCHEMA;
}

/// One file fed to the projection. The caller hashed the bytes; this type does not open disk.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ObservedAttemptJson {
    pub relative_path: String,
    pub schema_id: Option<String>,
    pub content_digest: String,
    pub body: Option<serde_json::Value>,
}

/// Merge observed Attempt JSON into one request / report / Registry / object view.
pub fn project_attempt_metadata(
    files: &[ObservedAttemptJson],
    checked_authority: AttemptCheckedAuthorityV1,
) -> Result<AttemptMetadataProjectionV1, String> {
    let mut home_intent = None;
    let mut runner_claim = None;
    let mut derived_transport = Vec::new();
    let mut objects = BTreeMap::new();
    let mut attempt_id = None;
    for file in files {
        let class = classify_attempt_json(&file.relative_path)?;
        let record = AttemptJsonRecordV1 {
            relative_path: file.relative_path.clone(),
            class,
            schema_id: file.schema_id.clone(),
            content_digest: file.content_digest.clone(),
        };
        match class {
            AttemptJsonClass::HomeIntent => {
                if home_intent.is_some() {
                    return Err("an Attempt may persist exactly one Home intent request".to_string());
                }
                attempt_id = attempt_id.or_else(|| attempt_id_from_request(file.body.as_ref()));
                collect_object_refs(file.body.as_ref(), &mut objects);
                home_intent = Some(record);
            }
            AttemptJsonClass::RunnerClaim => {
                if runner_claim.is_some() {
                    return Err("an Attempt may persist exactly one runner-claim report".to_string());
                }
                attempt_id = attempt_id.or_else(|| attempt_id_from_report(file.body.as_ref()));
                runner_claim = Some(record);
            }
            AttemptJsonClass::CheckedAuthority => {
                // Leftover sidecar. Projection prefers Registry digests.
            }
            AttemptJsonClass::ImmutablePayload => {
                objects.entry(file.relative_path.clone()).or_insert(AttemptObjectRefV1 {
                    kind: file
                        .schema_id
                        .clone()
                        .unwrap_or_else(|| "fern.attempt_payload".to_string()),
                    content_digest: file.content_digest.clone(),
                });
            }
            AttemptJsonClass::DerivedTransport => derived_transport.push(record),
        }
    }
    derived_transport.sort_by(|left, right| left.relative_path.cmp(&right.relative_path));
    Ok(AttemptMetadataProjectionV1 {
        schema: ATTEMPT_METADATA_PROJECTION_SCHEMA.to_string(),
        attempt_id,
        home_intent,
        runner_claim,
        checked_authority,
        objects: objects.into_values().collect(),
        derived_transport,
    })
}

fn attempt_id_from_request(body: Option<&serde_json::Value>) -> Option<String> {
    let body = body?;
    body.get("attempt_id")
        .and_then(serde_json::Value::as_str)
        .or_else(|| body.get("stage_attempt_id").and_then(serde_json::Value::as_str))
        .map(str::to_string)
}

fn attempt_id_from_report(body: Option<&serde_json::Value>) -> Option<String> {
    attempt_id_from_request(body)
}

fn collect_object_refs(body: Option<&serde_json::Value>, objects: &mut BTreeMap<String, AttemptObjectRefV1>) {
    let Some(body) = body else {
        return;
    };
    if let Some(digest) = body
        .pointer("/invocation/plan_digest")
        .and_then(serde_json::Value::as_str)
    {
        objects.insert(
            "stage_plan".to_string(),
            AttemptObjectRefV1 {
                kind: "fern.stage_plan.v1".to_string(),
                content_digest: digest.to_string(),
            },
        );
    }
    if let Some(digest) = body
        .pointer("/implementation_digest")
        .and_then(serde_json::Value::as_str)
        .or_else(|| {
            body.pointer("/invocation/implementation/implementation_digest")
                .and_then(serde_json::Value::as_str)
        })
        .or_else(|| body.get("implementation").and_then(serde_json::Value::as_str))
    {
        objects.insert(
            "implementation".to_string(),
            AttemptObjectRefV1 {
                kind: "fern.implementation".to_string(),
                content_digest: digest.to_string(),
            },
        );
    }
    if let Some(digest) = body.get("model_architecture").and_then(serde_json::Value::as_str) {
        objects.insert(
            "model_architecture".to_string(),
            AttemptObjectRefV1 {
                kind: "fern.model_architecture".to_string(),
                content_digest: digest.to_string(),
            },
        );
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use fern_json_contract::JsonContract;
    use serde_json::json;

    #[test]
    fn census_counts_fifteen_metadata_files() {
        assert_eq!(ATTEMPT_METADATA_JSON.len(), 15);
        let mut paths = std::collections::BTreeSet::new();
        for entry in ATTEMPT_METADATA_JSON {
            assert!(paths.insert(entry.relative_path), "{}", entry.relative_path);
            classify_attempt_json(entry.relative_path).unwrap();
        }
        assert_eq!(
            ATTEMPT_METADATA_JSON
                .iter()
                .filter(|entry| entry.class == AttemptJsonClass::HomeIntent)
                .count(),
            2
        );
        assert_eq!(
            ATTEMPT_METADATA_JSON
                .iter()
                .filter(|entry| entry.class == AttemptJsonClass::RunnerClaim)
                .count(),
            2
        );
    }

    #[test]
    fn unknown_sidecar_is_refused() {
        let error = classify_attempt_json("authority.json").unwrap_err();
        assert!(error.contains("refused"), "{error}");
        assert!(classify_attempt_json("inputs/training_worker_request.json").is_ok());
        assert!(classify_attempt_json("outputs/metrics.json").is_ok());
    }

    #[test]
    fn projection_keeps_one_request_one_report_and_object_digests() {
        let files = vec![
            ObservedAttemptJson {
                relative_path: "request/stage_runner_request.v1.json".to_string(),
                schema_id: Some("fern.stage_runner_request.v1".to_string()),
                content_digest: "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa".to_string(),
                body: Some(json!({
                    "schema": "fern.stage_runner_request.v1",
                    "stage_attempt_id": "attempt-1",
                    "handler": "fern.train.current",
                    "implementation_digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    "invocation": {
                        "plan_digest": "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                    }
                })),
            },
            ObservedAttemptJson {
                relative_path: "report/stage_runner_report.v1.json".to_string(),
                schema_id: Some("fern.stage_runner_report.v1".to_string()),
                content_digest: "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd".to_string(),
                body: Some(json!({
                    "schema": "fern.stage_runner_report.v1",
                    "stage_attempt_id": "attempt-1"
                })),
            },
            ObservedAttemptJson {
                relative_path: "remote_attempt_instructions.v1.json".to_string(),
                schema_id: Some("fern.remote_attempt_instructions.v1".to_string()),
                content_digest: "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee".to_string(),
                body: None,
            },
            ObservedAttemptJson {
                relative_path: "attempt_artifact_commit.v1.json".to_string(),
                schema_id: Some("fern.attempt_artifact_commit.v1".to_string()),
                content_digest: "sha256:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff".to_string(),
                body: None,
            },
        ];
        let projection = project_attempt_metadata(
            &files,
            AttemptCheckedAuthorityV1 {
                receipt_digest: Some(
                    "sha256:1111111111111111111111111111111111111111111111111111111111111111".to_string(),
                ),
                commit_digest: None,
                stage_plan_digest: Some(
                    "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc".to_string(),
                ),
                compiled_experiment_digest: None,
                implementation_digest: Some(
                    "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb".to_string(),
                ),
            },
        )
        .unwrap();
        assert_eq!(projection.attempt_id.as_deref(), Some("attempt-1"));
        assert_eq!(
            projection.home_intent.as_ref().unwrap().relative_path,
            "request/stage_runner_request.v1.json"
        );
        assert_eq!(
            projection.runner_claim.as_ref().unwrap().relative_path,
            "report/stage_runner_report.v1.json"
        );
        assert_eq!(projection.derived_transport.len(), 1);
        assert!(
            projection
                .objects
                .iter()
                .any(|object| object.kind == "fern.stage_plan.v1")
        );
        assert!(projection.checked_authority.receipt_digest.is_some());
        let rendered = serde_json::to_string(&projection).unwrap();
        assert!(
            !rendered.contains("fern.train.current"),
            "projection must reference objects, not copy handler names: {rendered}"
        );
        projection.checked_value().unwrap();
    }

    #[test]
    fn two_home_intents_are_refused() {
        let files = vec![
            ObservedAttemptJson {
                relative_path: "worker_request.v1.json".to_string(),
                schema_id: None,
                content_digest: "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa".to_string(),
                body: None,
            },
            ObservedAttemptJson {
                relative_path: "request/stage_runner_request.v1.json".to_string(),
                schema_id: None,
                content_digest: "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb".to_string(),
                body: None,
            },
        ];
        let error = project_attempt_metadata(&files, AttemptCheckedAuthorityV1::default()).unwrap_err();
        assert!(error.contains("exactly one Home intent"), "{error}");
    }
}
