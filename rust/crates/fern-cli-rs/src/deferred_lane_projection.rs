//! Compatibility-lane projection held until a typed Attempt is accepted.
//!
//! The Python training worker computes the metrics. Operator `reconcile` and
//! the scheduler project them into the historical lane only after Registry
//! Home has accepted the Attempt. This file is the shared seam so those
//! commands do not compile the training spawn.

use std::path::Path;

use serde::Deserialize;
use serde_json::{Value, json};

use fern_run::db::Metrics;

use crate::{Fallible, invalid_input, read_json_file};

/// Compatibility facts held by the Rust parent until the typed Attempt has
/// committed.  This is not a second authority object: it can only project the
/// already-computed metrics into the old lane DB after Pass 6 succeeds.
#[derive(Debug, Clone)]
pub(crate) struct DeferredLaneProjection {
    pub(crate) run_hash: String,
    pub(crate) metrics: Metrics,
}

#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct AcceptedCandidateIdentity {
    pub(crate) coordinate_id: String,
    pub(crate) attempt_id: String,
    pub(crate) run_hash: String,
}

/// Read the durable Candidate selection from Registry Home's Go owner.
///
/// Explicit `None` means no Candidate was accepted. Every projection caller
/// must treat a child refusal or malformed identity as an error, never as the
/// absence of an accepted result.
pub(crate) fn accepted_candidate_via_registry_home(
    home_binary: &Path,
    lifecycle_db: &Path,
    coordinate_id: &str,
) -> Fallible<Option<AcceptedCandidateIdentity>> {
    let bytes = crate::run_go_home(
        home_binary,
        "inspect-accepted-candidate",
        lifecycle_db,
        &json!({"coordinate_id": coordinate_id}),
    )?;
    let identity: Option<AcceptedCandidateIdentity> = serde_json::from_slice(&bytes)?;
    if let Some(identity) = identity.as_ref()
        && identity.coordinate_id != coordinate_id
    {
        return Err(invalid_input(format!(
            "Registry Home returned accepted Candidate for coordinate {}, expected {coordinate_id}",
            identity.coordinate_id
        )));
    }
    Ok(identity)
}

/// Project a typed-successful Attempt into the historical lane exactly once.
///
/// The caller must present this value only after the immutable Attempt commit
/// has been reopened and checked. Replaying the projection is safe because
/// `db::project_accepted_typed_result` is an exact one-row transition for the
/// same run and accepted Attempt.
pub(crate) fn complete_deferred_lane_projection(
    lifecycle_db: &Path,
    db_path: &Path,
    projection: &DeferredLaneProjection,
    coordinate_id: &str,
    attempt_id: &str,
) -> Fallible<usize> {
    let home_binary = crate::go_home_binary(lifecycle_db)?;
    let accepted = accepted_candidate_via_registry_home(&home_binary, lifecycle_db, coordinate_id)?;
    match accepted {
        Some(accepted) if accepted.attempt_id == attempt_id && accepted.run_hash == projection.run_hash => {}
        Some(accepted) => {
            return Err(invalid_input(format!(
                "cannot project typed result into the historical lane before Registry Home accepts it: coordinate {coordinate_id} selected attempt {} / run {}, not requested attempt {attempt_id} / run {}; refusing compatibility projection",
                accepted.attempt_id, accepted.run_hash, projection.run_hash
            )));
        }
        None => {
            return Err(invalid_input(format!(
                "cannot project typed result into the historical lane before Registry Home accepts it: coordinate {coordinate_id} has no accepted V3 candidate; refusing compatibility projection for attempt {attempt_id} / run {}",
                projection.run_hash
            )));
        }
    }
    let receipt = crate::run_csharp_registry(
        "project-accepted-run-result-data",
        db_path,
        &serde_json::json!({
            "run_hash": &projection.run_hash,
            "metrics": &projection.metrics,
            "coordinate_id": coordinate_id,
            "attempt_id": attempt_id,
        }),
        crate::CSHARP_REGISTRY_PROCESS_DEADLINE,
    )?;
    let receipt: serde_json::Value = serde_json::from_slice(&receipt)?;
    if receipt["schema"] != "fern.accepted_typed_run_projection.v1"
        || receipt["run_hash"].as_str() != Some(projection.run_hash.as_str())
        || receipt["rows_changed"].as_i64().is_none_or(|rows| rows < 0)
    {
        return Err(invalid_input("C# accepted typed Run projection returned a mismatched typed receipt"));
    }
    Ok(usize::try_from(receipt["rows_changed"].as_i64().unwrap())?)
}

