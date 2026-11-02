//! Pure evidence-policy and frozen-release contracts.
//!
//! `fern-artifact` answers whether one Attempt produced intact, interpretable
//! files. This crate answers the later scientific question: which checked
//! Attempt was selected, under which predeclared policy, for which immutable
//! release? It deliberately has no database, filesystem, CLI, or worker code.

use std::collections::{BTreeSet, HashSet};
use std::fmt::{Display, Formatter};

#[cfg(feature = "parquet")]
use fern_artifact::ArtifactKey;
use fern_artifact::{
    ArtifactReference, ArtifactRole, COMPACT_METRICS_MEDIA_TYPE, CompactMetricsV1, ImplementationDigest,
    ReportedArtifactV1, ScientificContentDigest, decode_compact_metrics_arrow,
};
use fern_experiment_types::{DeclaredReplicaPurpose, DeclaredReplicaV1, ModelImplementationHash};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

#[cfg(feature = "parquet")]
mod campaign_ledger_parquet;

#[cfg(feature = "parquet")]
pub use campaign_ledger_parquet::*;

#[cfg(feature = "parquet")]
mod release_metrics_parquet;

#[cfg(feature = "parquet")]
pub use release_metrics_parquet::*;

pub const EVIDENCE_POLICY_SCHEMA: &str = "fern.evidence_policy.v1";
pub const CHECKED_COMMIT_SCHEMA: &str = "fern.checked_artifact_commit.v1";
pub const VERIFIED_CANDIDATE_SCHEMA: &str = "fern.verified_evidence_candidate.v1";
pub const ACCEPTANCE_DECISION_SCHEMA: &str = "fern.evidence_acceptance_decision.v1";
pub const EVIDENCE_RELEASE_SCHEMA: &str = "fern.evidence_release.v1";
pub const RELEASE_METRICS_TABLE_SCHEMA: &str = "fern.release_metrics_table.v1";
pub const RELEASE_METRICS_MEDIA_TYPE: &str = "application/vnd.apache.parquet";
pub const CAMPAIGN_LEDGER_SCHEMA: &str = "fern.campaign_ledger.v1";

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EvidenceError {
    field: String,
    message: String,
}

impl EvidenceError {
    fn new(field: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            field: field.into(),
            message: message.into(),
        }
    }

    pub fn field(&self) -> &str {
        &self.field
    }
}

impl Display for EvidenceError {
    fn fmt(&self, formatter: &mut Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "{}: {}", self.field, self.message)
    }
}

impl std::error::Error for EvidenceError {}

macro_rules! evidence_digest_type {
    ($name:ident, $prefix:literal, $field:literal) => {
        #[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
        #[serde(try_from = "String", into = "String")]
        pub struct $name(String);

        impl $name {
            fn from_domain_bytes(domain: &[u8], bytes: &[u8]) -> Self {
                let mut hasher = Sha256::new();
                hasher.update(domain);
                hasher.update(bytes);
                Self(format!(concat!($prefix, "{:x}"), hasher.finalize()))
            }

            pub fn parse(value: impl Into<String>) -> Result<Self, EvidenceError> {
                let value = value.into();
                let Some(hex) = value.strip_prefix($prefix) else {
                    return Err(EvidenceError::new($field, concat!("must start with ", $prefix)));
                };
                if hex.len() != 64
                    || !hex
                        .bytes()
                        .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
                {
                    return Err(EvidenceError::new(
                        $field,
                        "must end with exactly 64 lowercase hexadecimal digits",
                    ));
                }
                Ok(Self(value))
            }

            pub fn as_str(&self) -> &str {
                &self.0
            }
        }

        impl TryFrom<String> for $name {
            type Error = EvidenceError;

            fn try_from(value: String) -> Result<Self, Self::Error> {
                Self::parse(value)
            }
        }

        impl From<$name> for String {
            fn from(value: $name) -> Self {
                value.0
            }
        }
    };
}

evidence_digest_type!(
    EvidencePolicyDigest,
    "evidence-policy:sha256:",
    "evidence_policy_digest"
);
evidence_digest_type!(CheckedCommitDigest, "checked-commit:sha256:", "checked_commit_digest");
evidence_digest_type!(CandidateDigest, "evidence-candidate:sha256:", "candidate_digest");
evidence_digest_type!(AcceptanceDecisionDigest, "acceptance:sha256:", "acceptance_digest");
evidence_digest_type!(CoordinateSetDigest, "coordinate-set:sha256:", "coordinate_set_digest");
evidence_digest_type!(
    EvidenceReleaseDigest,
    "evidence-release:sha256:",
    "evidence_release_digest"
);

fn validate_logical_id(field: &str, value: &str) -> Result<(), EvidenceError> {
    if value.is_empty()
        || value.starts_with('/')
        || value.starts_with('\\')
        || value.contains("..")
        || !value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b':' | b'.' | b'_' | b'-'))
    {
        return Err(EvidenceError::new(
            field,
            "must be a non-empty portable logical identity, not a filesystem path",
        ));
    }
    Ok(())
}

fn canonical_json<T: Serialize>(field: &str, value: &T) -> Result<Vec<u8>, EvidenceError> {
    serde_json::to_vec(value).map_err(|error| EvidenceError::new(field, format!("cannot encode: {error}")))
}

/// Which intentionally repeated executions may be selected by this policy.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ReplicaAcceptancePolicy {
    PrimaryOnly,
    AllowDeclaredPurposes(Vec<DeclaredReplicaPurpose>),
}

impl ReplicaAcceptancePolicy {
    fn canonicalize(&mut self) -> Result<(), EvidenceError> {
        if let Self::AllowDeclaredPurposes(purposes) = self {
            purposes.sort_by_key(|purpose| purpose.value());
            purposes.dedup();
            if purposes.is_empty() {
                return Err(EvidenceError::new(
                    "evidence_policy.replica_policy",
                    "must name at least one bounded purpose or use primary_only",
                ));
            }
        }
        Ok(())
    }

    fn permits(&self, replica: Option<&DeclaredReplicaV1>) -> bool {
        match (self, replica) {
            (Self::PrimaryOnly, None) => true,
            (Self::AllowDeclaredPurposes(_), None) => true,
            (Self::AllowDeclaredPurposes(purposes), Some(replica)) => purposes.contains(&replica.purpose),
            (Self::PrimaryOnly, Some(_)) => false,
        }
    }
}

/// One scientific Coordinate named without relying on a lane or directory.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct EvidenceCoordinateV1 {
    pub matrix_id: String,
    pub coordinate_id: String,
}

impl EvidenceCoordinateV1 {
    fn validate(&self) -> Result<(), EvidenceError> {
        validate_logical_id("evidence_coordinate.matrix_id", &self.matrix_id)?;
        validate_logical_id("evidence_coordinate.coordinate_id", &self.coordinate_id)
    }
}

fn canonical_coordinate_set(coordinates: &[EvidenceCoordinateV1]) -> Result<Vec<EvidenceCoordinateV1>, EvidenceError> {
    let mut canonical = coordinates.to_vec();
    for coordinate in &canonical {
        coordinate.validate()?;
    }
    canonical.sort();
    canonical.dedup();
    if canonical.is_empty() {
        return Err(EvidenceError::new(
            "evidence_policy.selection_scope",
            "must name at least one required Coordinate",
        ));
    }
    Ok(canonical)
}

pub fn coordinate_set_digest(coordinates: &[EvidenceCoordinateV1]) -> Result<CoordinateSetDigest, EvidenceError> {
    let canonical = canonical_coordinate_set(coordinates)?;
    Ok(CoordinateSetDigest::from_domain_bytes(
        b"fern.evidence_coordinate_set.v1\0",
        &canonical_json("evidence_coordinate_set", &canonical)?,
    ))
}

/// Predeclared membership rule for one release.
///
/// `AllDeclaredCoordinates` snapshots the declared set by digest when the
/// policy is registered. `ExplicitCoordinates` supports a smaller scientific
/// subset, but the subset itself is part of the immutable policy card.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum ReleaseSelectionScopeV1 {
    AllDeclaredCoordinates { coordinate_set_digest: CoordinateSetDigest },
    ExplicitCoordinates { coordinates: Vec<EvidenceCoordinateV1> },
}

impl ReleaseSelectionScopeV1 {
    fn canonicalize(&mut self, matrix_ids: &[String]) -> Result<(), EvidenceError> {
        match self {
            Self::AllDeclaredCoordinates { .. } => Ok(()),
            Self::ExplicitCoordinates { coordinates } => {
                *coordinates = canonical_coordinate_set(coordinates)?;
                if let Some(outside) = coordinates
                    .iter()
                    .find(|coordinate| !matrix_ids.contains(&coordinate.matrix_id))
                {
                    return Err(EvidenceError::new(
                        "evidence_policy.selection_scope.coordinates",
                        format!(
                            "Coordinate {:?}/{:?} belongs to a Matrix absent from this policy",
                            outside.matrix_id, outside.coordinate_id
                        ),
                    ));
                }
                Ok(())
            }
        }
    }
}

/// Immutable, predeclared rule for selecting checked results.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct EvidencePolicyCardV1 {
    pub schema: String,
    pub policy_id: String,
    pub revision: u32,
    pub matrix_ids: Vec<String>,
    pub selection_scope: ReleaseSelectionScopeV1,
    pub required_artifact_roles: Vec<ArtifactRole>,
    pub replica_policy: ReplicaAcceptancePolicy,
}

impl EvidencePolicyCardV1 {
    pub fn canonicalize(mut self) -> Result<Self, EvidenceError> {
        if self.schema != EVIDENCE_POLICY_SCHEMA {
            return Err(EvidenceError::new(
                "evidence_policy.schema",
                format!("expected {EVIDENCE_POLICY_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        validate_logical_id("evidence_policy.policy_id", &self.policy_id)?;
        if self.revision == 0 {
            return Err(EvidenceError::new("evidence_policy.revision", "must be positive"));
        }
        self.matrix_ids.sort();
        self.matrix_ids.dedup();
        if self.matrix_ids.is_empty() {
            return Err(EvidenceError::new(
                "evidence_policy.matrix_ids",
                "must predeclare at least one matrix",
            ));
        }
        for matrix_id in &self.matrix_ids {
            validate_logical_id("evidence_policy.matrix_ids", matrix_id)?;
        }
        self.selection_scope.canonicalize(&self.matrix_ids)?;
        self.required_artifact_roles.sort();
        self.required_artifact_roles.dedup();
        if self.required_artifact_roles.is_empty() {
            return Err(EvidenceError::new(
                "evidence_policy.required_artifact_roles",
                "must name the artifacts required by the release",
            ));
        }
        self.replica_policy.canonicalize()?;
        Ok(self)
    }

    /// Verify the predeclared scope against the registry's immutable Matrix
    /// declarations and return the exact Coordinate set a release must contain.
    pub fn required_release_coordinates(
        &self,
        declared_coordinates: &[EvidenceCoordinateV1],
    ) -> Result<Vec<EvidenceCoordinateV1>, EvidenceError> {
        let canonical = self.clone().canonicalize()?;
        let declared = canonical_coordinate_set(declared_coordinates)?;
        if let Some(outside) = declared
            .iter()
            .find(|coordinate| !canonical.matrix_ids.contains(&coordinate.matrix_id))
        {
            return Err(EvidenceError::new(
                "evidence_policy.declared_coordinates",
                format!(
                    "registry returned Coordinate {:?}/{:?} outside the policy Matrix set",
                    outside.matrix_id, outside.coordinate_id
                ),
            ));
        }
        match canonical.selection_scope {
            ReleaseSelectionScopeV1::AllDeclaredCoordinates {
                coordinate_set_digest: expected,
            } => {
                if coordinate_set_digest(&declared)? != expected {
                    return Err(EvidenceError::new(
                        "evidence_policy.selection_scope.coordinate_set_digest",
                        "registered Matrix Coordinates differ from the predeclared set digest",
                    ));
                }
                Ok(declared)
            }
            ReleaseSelectionScopeV1::ExplicitCoordinates { coordinates } => {
                let declared = declared.into_iter().collect::<BTreeSet<_>>();
                if let Some(missing) = coordinates.iter().find(|coordinate| !declared.contains(*coordinate)) {
                    return Err(EvidenceError::new(
                        "evidence_policy.selection_scope.coordinates",
                        format!(
                            "predeclared Coordinate {:?}/{:?} is absent from the registered Matrix",
                            missing.matrix_id, missing.coordinate_id
                        ),
                    ));
                }
                Ok(coordinates)
            }
        }
    }

    pub fn digest(&self) -> Result<EvidencePolicyDigest, EvidenceError> {
        let canonical = self.clone().canonicalize()?;
        Ok(EvidencePolicyDigest::from_domain_bytes(
            b"fern.evidence_policy.v1\0",
            &canonical_json("evidence_policy", &canonical)?,
        ))
    }

    /// Derive the non-discretionary decision from policy, checked outputs, and
    /// the registry's implementation binding.
    ///
    /// A human may still place an otherwise acceptable candidate on a
    /// scientific-audit hold. Missing artifacts, forbidden replicas, and an
    /// implementation mismatch are objective facts and cannot be relabeled by
    /// the caller.
    pub fn required_decision_for_candidate(
        &self,
        candidate: &VerifiedEvidenceCandidateV1,
        implementation_matches_declared_coordinate: bool,
    ) -> Result<(EvidenceDecision, EvidenceDecisionReason), EvidenceError> {
        let policy = self.clone().canonicalize()?;
        let candidate = candidate.clone().canonicalize()?;
        let replica_permitted = policy.replica_policy.permits(candidate.replica.as_ref());
        if !replica_permitted {
            return Ok((EvidenceDecision::Rejected, EvidenceDecisionReason::ReplicaNotPermitted));
        }
        let present_roles = candidate
            .checked_commit
            .outputs
            .iter()
            .map(|output| output.role)
            .collect::<HashSet<_>>();
        if policy
            .required_artifact_roles
            .iter()
            .any(|role| !present_roles.contains(role))
        {
            return Ok((
                EvidenceDecision::Rejected,
                EvidenceDecisionReason::MissingRequiredArtifact,
            ));
        }
        if !implementation_matches_declared_coordinate {
            return Ok((EvidenceDecision::Held, EvidenceDecisionReason::ImplementationConflict));
        }
        Ok((EvidenceDecision::Accepted, EvidenceDecisionReason::PolicyMatch))
    }
}

/// Pass-6 output after an Attempt commit has been structurally and
/// scientifically checked. This object is not an acceptance decision.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CheckedArtifactCommitV1 {
    pub schema: String,
    pub check_id: String,
    pub attempt_id: String,
    pub run_hash: String,
    pub commit_content_digest: ScientificContentDigest,
    pub checker_implementation: ImplementationDigest,
    pub checker_actor_id: String,
    pub checked_at_unix_ms: i64,
    /// Full reported_artifacts intentionally retain both the byte digest and the
    /// canonical scientific-content digest. Neither can answer the other's
    /// integrity question.
    pub outputs: Vec<ReportedArtifactV1>,
}

impl CheckedArtifactCommitV1 {
    pub fn canonicalize(mut self) -> Result<Self, EvidenceError> {
        if self.schema != CHECKED_COMMIT_SCHEMA {
            return Err(EvidenceError::new(
                "checked_commit.schema",
                format!("expected {CHECKED_COMMIT_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        validate_logical_id("checked_commit.check_id", &self.check_id)?;
        validate_logical_id("checked_commit.attempt_id", &self.attempt_id)?;
        validate_logical_id("checked_commit.run_hash", &self.run_hash)?;
        validate_logical_id("checked_commit.checker_actor_id", &self.checker_actor_id)?;
        if self.checked_at_unix_ms <= 0 {
            return Err(EvidenceError::new(
                "checked_commit.checked_at_unix_ms",
                "must be positive",
            ));
        }
        self.outputs
            .sort_by(|left, right| left.artifact_key.cmp(&right.artifact_key));
        let mut keys = HashSet::new();
        if self.outputs.is_empty() {
            return Err(EvidenceError::new(
                "checked_commit.outputs",
                "must contain checked artifacts",
            ));
        }
        for output in &self.outputs {
            output
                .validate()
                .map_err(|error| EvidenceError::new("checked_commit.outputs", error.to_string()))?;
            if !keys.insert(output.artifact_key.clone()) {
                return Err(EvidenceError::new(
                    "checked_commit.outputs",
                    format!("duplicate artifact key {:?}", output.artifact_key.as_str()),
                ));
            }
        }
        Ok(self)
    }

    pub fn digest(&self) -> Result<CheckedCommitDigest, EvidenceError> {
        let canonical = self.clone().canonicalize()?;
        Ok(CheckedCommitDigest::from_domain_bytes(
            b"fern.checked_artifact_commit.v1\0",
            &canonical_json("checked_commit", &canonical)?,
        ))
    }
}

/// One checked Attempt eligible for a later acceptance decision.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct VerifiedEvidenceCandidateV1 {
    pub schema: String,
    pub matrix_id: String,
    pub coordinate_id: String,
    pub target_hash: String,
    pub replica: Option<DeclaredReplicaV1>,
    pub attempt_id: String,
    pub run_hash: String,
    pub implementation: ModelImplementationHash,
    pub checked_commit: CheckedArtifactCommitV1,
}

impl VerifiedEvidenceCandidateV1 {
    pub fn canonicalize(mut self) -> Result<Self, EvidenceError> {
        if self.schema != VERIFIED_CANDIDATE_SCHEMA {
            return Err(EvidenceError::new(
                "candidate.schema",
                format!("expected {VERIFIED_CANDIDATE_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        for (field, value) in [
            ("matrix_id", self.matrix_id.as_str()),
            ("coordinate_id", self.coordinate_id.as_str()),
            ("target_hash", self.target_hash.as_str()),
            ("attempt_id", self.attempt_id.as_str()),
            ("run_hash", self.run_hash.as_str()),
        ] {
            validate_logical_id(&format!("candidate.{field}"), value)?;
        }
        if let Some(replica) = &self.replica {
            replica
                .validate()
                .map_err(|message| EvidenceError::new("candidate.replica", message))?;
        }
        self.checked_commit = self.checked_commit.canonicalize()?;
        if self.checked_commit.attempt_id != self.attempt_id || self.checked_commit.run_hash != self.run_hash {
            return Err(EvidenceError::new(
                "candidate.checked_commit",
                "must belong to the candidate's exact Attempt and run",
            ));
        }
        Ok(self)
    }

    pub fn digest(&self) -> Result<CandidateDigest, EvidenceError> {
        let canonical = self.clone().canonicalize()?;
        Ok(CandidateDigest::from_domain_bytes(
            b"fern.verified_evidence_candidate.v1\0",
            &canonical_json("candidate", &canonical)?,
        ))
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum EvidenceDecision {
    Accepted,
    Rejected,
    Held,
}

impl EvidenceDecision {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Accepted => "accepted",
            Self::Rejected => "rejected",
            Self::Held => "held",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum EvidenceDecisionReason {
    PolicyMatch,
    MissingRequiredArtifact,
    ReplicaNotPermitted,
    ImplementationConflict,
    ScientificAuditHold,
}

impl EvidenceDecisionReason {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::PolicyMatch => "policy_match",
            Self::MissingRequiredArtifact => "missing_required_artifact",
            Self::ReplicaNotPermitted => "replica_not_permitted",
            Self::ImplementationConflict => "implementation_conflict",
            Self::ScientificAuditHold => "scientific_audit_hold",
        }
    }
}

/// Append-only scientific judgement made after artifact checking.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct AcceptanceDecisionV1 {
    pub schema: String,
    pub decision_id: String,
    pub idempotency_key: String,
    pub policy_digest: EvidencePolicyDigest,
    pub candidate: VerifiedEvidenceCandidateV1,
    pub candidate_digest: CandidateDigest,
    pub decision: EvidenceDecision,
    pub reason: EvidenceDecisionReason,
    pub actor_id: String,
    pub decided_at_unix_ms: i64,
    pub explanation: String,
}

impl AcceptanceDecisionV1 {
    pub fn validate_against(&self, policy: &EvidencePolicyCardV1) -> Result<(), EvidenceError> {
        if self.schema != ACCEPTANCE_DECISION_SCHEMA {
            return Err(EvidenceError::new(
                "acceptance.schema",
                format!("expected {ACCEPTANCE_DECISION_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        for (field, value) in [
            ("decision_id", self.decision_id.as_str()),
            ("idempotency_key", self.idempotency_key.as_str()),
            ("actor_id", self.actor_id.as_str()),
        ] {
            validate_logical_id(&format!("acceptance.{field}"), value)?;
        }
        if self.decided_at_unix_ms <= 0 {
            return Err(EvidenceError::new("acceptance.decided_at_unix_ms", "must be positive"));
        }
        if self.explanation.trim().is_empty() {
            return Err(EvidenceError::new(
                "acceptance.explanation",
                "must explain the decision",
            ));
        }
        let policy = policy.clone().canonicalize()?;
        if self.policy_digest != policy.digest()? {
            return Err(EvidenceError::new(
                "acceptance.policy_digest",
                "does not match the policy card",
            ));
        }
        let candidate = self.candidate.clone().canonicalize()?;
        if self.candidate_digest != candidate.digest()? {
            return Err(EvidenceError::new(
                "acceptance.candidate_digest",
                "does not match the checked candidate",
            ));
        }
        if !policy.matrix_ids.contains(&candidate.matrix_id) {
            return Err(EvidenceError::new(
                "acceptance.candidate.matrix_id",
                "is outside the policy's predeclared matrices",
            ));
        }
        if let ReleaseSelectionScopeV1::ExplicitCoordinates { coordinates } = &policy.selection_scope
            && !coordinates.contains(&EvidenceCoordinateV1 {
                matrix_id: candidate.matrix_id.clone(),
                coordinate_id: candidate.coordinate_id.clone(),
            })
        {
            return Err(EvidenceError::new(
                "acceptance.candidate.coordinate_id",
                "is outside the policy's predeclared Coordinate subset",
            ));
        }
        let required_without_registry_conflict = policy.required_decision_for_candidate(&candidate, true)?;
        let submitted = (self.decision, self.reason);
        let discretionary_hold = matches!(
            submitted,
            (EvidenceDecision::Held, EvidenceDecisionReason::ImplementationConflict)
                | (EvidenceDecision::Held, EvidenceDecisionReason::ScientificAuditHold)
        );
        if submitted != required_without_registry_conflict
            && !(discretionary_hold
                && required_without_registry_conflict
                    == (EvidenceDecision::Accepted, EvidenceDecisionReason::PolicyMatch))
        {
            return Err(EvidenceError::new(
                "acceptance.decision",
                "decision/reason does not match the policy and checked candidate facts",
            ));
        }
        Ok(())
    }

    pub fn digest_against(&self, policy: &EvidencePolicyCardV1) -> Result<AcceptanceDecisionDigest, EvidenceError> {
        let canonical = self.clone().canonicalize_against(policy)?;
        Ok(AcceptanceDecisionDigest::from_domain_bytes(
            b"fern.evidence_acceptance_decision.v1\0",
            &canonical_json("acceptance", &canonical)?,
        ))
    }

    pub fn canonicalize_against(mut self, policy: &EvidencePolicyCardV1) -> Result<Self, EvidenceError> {
        self.candidate = self.candidate.canonicalize()?;
        self.candidate_digest = self.candidate.digest()?;
        self.validate_against(policy)?;
        Ok(self)
    }
}

/// One typed compact ledger proven to come from one checked Arrow artifact.
///
/// The checked artifact and decoded values are one object so a release
/// builder cannot accidentally pair a legitimate artifact reference with
/// numbers from another file or an in-memory recomputation.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CheckedCompactMetricsV1 {
    reported_artifact: ReportedArtifactV1,
    metrics: CompactMetricsV1,
}

/// One policy-specific judgement visible in the operational campaign ledger.
///
/// Several policy revisions may legitimately judge the same checked Attempt,
/// so the ledger preserves every decision instead of collapsing them into a
/// misleading single `accepted` flag.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CampaignDecisionSummaryV1 {
    pub policy_digest: EvidencePolicyDigest,
    pub decision_digest: AcceptanceDecisionDigest,
    pub decision: EvidenceDecision,
    pub reason: EvidenceDecisionReason,
}

/// One checked compact ledger in the operational campaign view.
///
/// There is intentionally no release or policy identity here. That omission
/// makes this useful for anomaly review while making it structurally
/// ineligible for the frozen-release reader gate.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CampaignLedgerMemberV1 {
    pub checked_commit_digest: CheckedCommitDigest,
    pub decisions: Vec<CampaignDecisionSummaryV1>,
    pub matrix_id: String,
    pub coordinate_id: String,
    pub target_hash: String,
    pub replica: Option<DeclaredReplicaV1>,
    pub attempt_id: String,
    pub run_hash: String,
    pub implementation: ModelImplementationHash,
    pub checked_compact_metrics: CheckedCompactMetricsV1,
}

/// Rebuildable operational view over all checked compact ledgers.
///
/// This object is not an EvidenceRelease and cannot authorize a paper reader.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CampaignLedgerV1 {
    pub schema: String,
    pub members: Vec<CampaignLedgerMemberV1>,
}

impl CampaignLedgerV1 {
    pub fn canonicalize(mut self) -> Result<Self, EvidenceError> {
        if self.schema != CAMPAIGN_LEDGER_SCHEMA {
            return Err(EvidenceError::new(
                "campaign_ledger.schema",
                format!("expected {CAMPAIGN_LEDGER_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        if self.members.is_empty() {
            return Err(EvidenceError::new(
                "campaign_ledger.members",
                "must contain at least one checked compact ledger",
            ));
        }
        let mut checked_commits = HashSet::new();
        for member in &mut self.members {
            for (field, value) in [
                ("matrix_id", member.matrix_id.as_str()),
                ("coordinate_id", member.coordinate_id.as_str()),
                ("target_hash", member.target_hash.as_str()),
                ("attempt_id", member.attempt_id.as_str()),
                ("run_hash", member.run_hash.as_str()),
            ] {
                validate_logical_id(&format!("campaign_ledger.members.{field}"), value)?;
            }
            if let Some(replica) = &member.replica {
                replica
                    .validate()
                    .map_err(|message| EvidenceError::new("campaign_ledger.members.replica", message))?;
            }
            member.checked_compact_metrics.validate()?;
            member.decisions.sort_by(|left, right| {
                (left.policy_digest.as_str(), left.decision_digest.as_str())
                    .cmp(&(right.policy_digest.as_str(), right.decision_digest.as_str()))
            });
            let mut decision_digests = HashSet::new();
            if !member
                .decisions
                .iter()
                .all(|decision| decision_digests.insert(decision.decision_digest.clone()))
            {
                return Err(EvidenceError::new(
                    "campaign_ledger.members.decisions",
                    "contains a duplicate decision digest",
                ));
            }
            if !checked_commits.insert(member.checked_commit_digest.clone()) {
                return Err(EvidenceError::new(
                    "campaign_ledger.members.checked_commit_digest",
                    "appears more than once",
                ));
            }
        }
        self.members.sort_by(|left, right| {
            (
                left.matrix_id.as_str(),
                left.coordinate_id.as_str(),
                left.attempt_id.as_str(),
            )
                .cmp(&(
                    right.matrix_id.as_str(),
                    right.coordinate_id.as_str(),
                    right.attempt_id.as_str(),
                ))
        });
        Ok(self)
    }

    pub fn content_digest(&self) -> Result<ScientificContentDigest, EvidenceError> {
        let canonical = self.clone().canonicalize()?;
        let bytes = canonical_json("campaign_ledger", &canonical)?;
        let mut hasher = Sha256::new();
        hasher.update(b"fern.campaign_ledger.v1\0");
        hasher.update(bytes);
        Ok(
            ScientificContentDigest::parse(format!("sha256:{:x}", hasher.finalize()))
                .map_err(|error| EvidenceError::new("campaign_ledger.content_digest", error.to_string()))?,
        )
    }
}

impl CheckedCompactMetricsV1 {
    /// Reopen and decode the exact checked file before it may enter a release.
    pub fn from_reopened_arrow(
        reported_artifact: ReportedArtifactV1,
        stored_bytes: &[u8],
    ) -> Result<Self, EvidenceError> {
        let metrics = decode_compact_metrics_arrow(stored_bytes)
            .map_err(|error| EvidenceError::new("checked_compact_metrics.arrow", error.to_string()))?;
        let canonical = metrics
            .canonical_bytes()
            .map_err(|error| EvidenceError::new("checked_compact_metrics.metrics", error.to_string()))?;
        reported_artifact
            .check_reopened_payloads(stored_bytes, &canonical)
            .map_err(|error| EvidenceError::new("checked_compact_metrics.reported_artifact", error.to_string()))?;
        Self::from_checked_artifact(reported_artifact, metrics)
    }

    fn from_checked_artifact(
        reported_artifact: ReportedArtifactV1,
        metrics: CompactMetricsV1,
    ) -> Result<Self, EvidenceError> {
        let checked = Self {
            reported_artifact,
            metrics,
        };
        checked.validate()?;
        Ok(checked)
    }

    fn validate(&self) -> Result<(), EvidenceError> {
        if self.reported_artifact.role != ArtifactRole::CompactMetrics
            || self.reported_artifact.media_type != COMPACT_METRICS_MEDIA_TYPE
        {
            return Err(EvidenceError::new(
                "checked_compact_metrics.reported_artifact",
                format!("must be a checked compact_metrics artifact with media type {COMPACT_METRICS_MEDIA_TYPE:?}"),
            ));
        }
        let canonical = self
            .metrics
            .canonical_bytes()
            .map_err(|error| EvidenceError::new("checked_compact_metrics.metrics", error.to_string()))?;
        self.reported_artifact
            .verify_canonical_content(&canonical)
            .map_err(|error| EvidenceError::new("checked_compact_metrics.reported_artifact", error.to_string()))
    }

    pub fn reported_artifact(&self) -> &ReportedArtifactV1 {
        &self.reported_artifact
    }

    pub fn artifact_reference(&self) -> ArtifactReference {
        ArtifactReference {
            artifact_key: self.reported_artifact.artifact_key.clone(),
            role: self.reported_artifact.role,
            content_digest: self.reported_artifact.content_digest.clone(),
        }
    }

    pub fn metrics(&self) -> &CompactMetricsV1 {
        &self.metrics
    }
}

/// One checked per-Attempt compact ledger selected for a release table.
///
/// Identity fields are repeated deliberately: Parquet readers can filter the
/// table without reopening the registry, while validation proves those columns
/// still say exactly what the accepted decision says.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ReleaseMetricsMemberV1 {
    pub decision_digest: AcceptanceDecisionDigest,
    pub matrix_id: String,
    pub coordinate_id: String,
    pub target_hash: String,
    pub replica: Option<DeclaredReplicaV1>,
    pub attempt_id: String,
    pub run_hash: String,
    pub implementation: ModelImplementationHash,
    pub checked_compact_metrics: CheckedCompactMetricsV1,
}

/// Canonical scientific content of `release_metrics.parquet`.
///
/// Parquet is only the query-oriented byte encoding. This typed object is what
/// the release writer hashes and what the reader reconstructs before asking the
/// registry whether the release is still current.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ReleaseMetricsTableV1 {
    pub schema: String,
    pub policy_digest: EvidencePolicyDigest,
    pub members: Vec<ReleaseMetricsMemberV1>,
}

impl ReleaseMetricsTableV1 {
    pub fn canonicalize_against(self, release: &EvidenceReleaseV1) -> Result<Self, EvidenceError> {
        let release = release.clone().canonicalize()?;
        self.canonicalize_against_selection(&release.policy, &release.accepted)
    }

    /// Validate and order decoded table content without granting it
    /// evidence authority. A physical reader uses this before the registry
    /// decides whether the bytes belong to a current frozen release.
    pub fn canonicalize_structure(mut self) -> Result<Self, EvidenceError> {
        if self.schema != RELEASE_METRICS_TABLE_SCHEMA {
            return Err(EvidenceError::new(
                "release_metrics_table.schema",
                format!("expected {RELEASE_METRICS_TABLE_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        if self.members.is_empty() {
            return Err(EvidenceError::new(
                "release_metrics_table.members",
                "must contain at least one accepted compact ledger",
            ));
        }
        let mut observed_decisions = HashSet::new();
        for member in &mut self.members {
            for (field, value) in [
                ("matrix_id", member.matrix_id.as_str()),
                ("coordinate_id", member.coordinate_id.as_str()),
                ("target_hash", member.target_hash.as_str()),
                ("attempt_id", member.attempt_id.as_str()),
                ("run_hash", member.run_hash.as_str()),
            ] {
                validate_logical_id(&format!("release_metrics_table.members.{field}"), value)?;
            }
            if let Some(replica) = &member.replica {
                replica
                    .validate()
                    .map_err(|message| EvidenceError::new("release_metrics_table.members.replica", message))?;
            }
            if !observed_decisions.insert(member.decision_digest.clone()) {
                return Err(EvidenceError::new(
                    "release_metrics_table.members.decision_digest",
                    "appears more than once",
                ));
            }
            member.checked_compact_metrics.validate()?;
            let compact_bytes = member
                .checked_compact_metrics
                .metrics
                .canonical_bytes()
                .map_err(|error| {
                    EvidenceError::new("release_metrics_table.members.compact_metrics", error.to_string())
                })?;
            member.checked_compact_metrics.metrics = serde_json::from_slice(&compact_bytes).map_err(|error| {
                EvidenceError::new(
                    "release_metrics_table.members.compact_metrics",
                    format!("cannot decode its canonical form: {error}"),
                )
            })?;
            if member.checked_compact_metrics.metrics.attempt_id != member.attempt_id {
                return Err(EvidenceError::new(
                    "release_metrics_table.members.compact_metrics.attempt_id",
                    "does not match the member Attempt",
                ));
            }
        }
        self.members.sort_by(|left, right| {
            (
                left.matrix_id.as_str(),
                left.coordinate_id.as_str(),
                left.decision_digest.as_str(),
            )
                .cmp(&(
                    right.matrix_id.as_str(),
                    right.coordinate_id.as_str(),
                    right.decision_digest.as_str(),
                ))
        });
        Ok(self)
    }

    pub fn canonical_bytes(&self) -> Result<Vec<u8>, EvidenceError> {
        canonical_json("release_metrics_table", &self.clone().canonicalize_structure()?)
    }

    pub fn content_digest(&self) -> Result<ScientificContentDigest, EvidenceError> {
        let mut bytes = b"fern.release_metrics_table.v1\0".to_vec();
        bytes.extend(self.canonical_bytes()?);
        Ok(ScientificContentDigest::from_bytes(&bytes))
    }

    /// Validate a table before the enclosing release artifact report exists.
    ///
    /// The table bytes must be produced before their byte/content digests
    /// can be embedded in [`EvidenceReleaseV1`].  This boundary therefore binds
    /// the table to the already accepted selection, without pretending a
    /// not-yet-created release has authorized it.  Readers use
    /// [`Self::canonicalize_against`] after the release is frozen.
    pub fn canonicalize_against_selection(
        self,
        policy: &EvidencePolicyCardV1,
        accepted: &[AcceptanceDecisionV1],
    ) -> Result<Self, EvidenceError> {
        let table = self.canonicalize_structure()?;
        let policy = policy.clone().canonicalize()?;
        let accepted = accepted
            .iter()
            .cloned()
            .map(|decision| decision.canonicalize_against(&policy))
            .collect::<Result<Vec<_>, _>>()?;
        if table.policy_digest != policy.digest()? {
            return Err(EvidenceError::new(
                "release_metrics_table.policy_digest",
                "does not match the frozen release policy",
            ));
        }
        if table.members.len() != accepted.len() {
            return Err(EvidenceError::new(
                "release_metrics_table.members",
                "must contain exactly one compact ledger for every accepted decision",
            ));
        }

        for member in &table.members {
            let decision = accepted
                .iter()
                .find(|decision| {
                    decision
                        .digest_against(&policy)
                        .is_ok_and(|digest| digest == member.decision_digest)
                })
                .ok_or_else(|| {
                    EvidenceError::new(
                        "release_metrics_table.members.decision_digest",
                        "does not name an accepted decision in the frozen release",
                    )
                })?;
            let candidate = &decision.candidate;
            if member.matrix_id != candidate.matrix_id
                || member.coordinate_id != candidate.coordinate_id
                || member.target_hash != candidate.target_hash
                || member.replica != candidate.replica
                || member.attempt_id != candidate.attempt_id
                || member.run_hash != candidate.run_hash
                || member.implementation != candidate.implementation
            {
                return Err(EvidenceError::new(
                    "release_metrics_table.members",
                    "query identity columns differ from the accepted candidate",
                ));
            }
            let checked_reported_artifact = member.checked_compact_metrics.reported_artifact();
            let matching_outputs = candidate
                .checked_commit
                .outputs
                .iter()
                .filter(|output| *output == checked_reported_artifact)
                .count();
            if matching_outputs != 1 {
                return Err(EvidenceError::new(
                    "release_metrics_table.members.checked_compact_metrics",
                    "reported_artifact must match exactly one checked output in the accepted candidate",
                ));
            }
        }

        Ok(table)
    }

    pub fn canonical_bytes_against_selection(
        &self,
        policy: &EvidencePolicyCardV1,
        accepted: &[AcceptanceDecisionV1],
    ) -> Result<Vec<u8>, EvidenceError> {
        canonical_json(
            "release_metrics_table",
            &self.clone().canonicalize_against_selection(policy, accepted)?,
        )
    }

    pub fn canonical_bytes_against(&self, release: &EvidenceReleaseV1) -> Result<Vec<u8>, EvidenceError> {
        canonical_json("release_metrics_table", &self.clone().canonicalize_against(release)?)
    }

    pub fn content_digest_against(
        &self,
        release: &EvidenceReleaseV1,
    ) -> Result<ScientificContentDigest, EvidenceError> {
        let mut bytes = b"fern.release_metrics_table.v1\0".to_vec();
        bytes.extend(self.canonical_bytes_against(release)?);
        Ok(ScientificContentDigest::from_bytes(&bytes))
    }

    pub fn content_digest_against_selection(
        &self,
        policy: &EvidencePolicyCardV1,
        accepted: &[AcceptanceDecisionV1],
    ) -> Result<ScientificContentDigest, EvidenceError> {
        let mut bytes = b"fern.release_metrics_table.v1\0".to_vec();
        bytes.extend(self.canonical_bytes_against_selection(policy, accepted)?);
        Ok(ScientificContentDigest::from_bytes(&bytes))
    }
}

/// Identity of the code that deterministically builds the release table.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ReleaseGeneratorV1 {
    pub generator_id: String,
    pub implementation: ModelImplementationHash,
}

/// One accepted decision and the exact compact ledger reopened for it.
///
/// This is an in-memory trust-stage object, not another serialized manifest.
/// The decision says *which Attempt was accepted*; the checked ledger proves
/// *which bytes supply that Attempt's numbers*.
#[derive(Debug, Clone, PartialEq)]
pub struct CheckedReleaseMemberV1 {
    pub decision: AcceptanceDecisionV1,
    pub compact_metrics: CheckedCompactMetricsV1,
}

/// Release objects produced together before the registry freezes them.
///
/// Keeping the typed table, physical Parquet bytes, and release descriptor in
/// one return value prevents a caller from accidentally hashing one table and
/// writing another. Registry authority is still a later, explicit operation.
#[cfg(feature = "parquet")]
#[derive(Debug, Clone, PartialEq)]
pub struct BuiltEvidenceReleaseV1 {
    pub release: EvidenceReleaseV1,
    pub metrics_table: ReleaseMetricsTableV1,
    pub metrics_parquet: Vec<u8>,
}

/// Build one release-only table and its immutable descriptor from already
/// accepted decisions and independently reopened compact ledgers.
///
/// This function does not read a database, choose candidates, or freeze a
/// release. The registry remains responsible for proving that the policy and
/// every embedded decision are authoritative before publication.
#[cfg(feature = "parquet")]
pub fn build_evidence_release(
    release_id: impl Into<String>,
    policy: &EvidencePolicyCardV1,
    generator: ReleaseGeneratorV1,
    metrics_artifact_key: ArtifactKey,
    members: Vec<CheckedReleaseMemberV1>,
) -> Result<BuiltEvidenceReleaseV1, EvidenceError> {
    let policy = policy.clone().canonicalize()?;
    if members.is_empty() {
        return Err(EvidenceError::new(
            "release_builder.members",
            "must contain at least one accepted decision and checked compact ledger",
        ));
    }

    let mut accepted = Vec::with_capacity(members.len());
    let mut table_members = Vec::with_capacity(members.len());
    for member in members {
        let decision = member.decision.canonicalize_against(&policy)?;
        if decision.decision != EvidenceDecision::Accepted {
            return Err(EvidenceError::new(
                "release_builder.members.decision",
                "only an accepted decision may enter a release build",
            ));
        }
        let decision_digest = decision.digest_against(&policy)?;
        let candidate = &decision.candidate;
        table_members.push(ReleaseMetricsMemberV1 {
            decision_digest,
            matrix_id: candidate.matrix_id.clone(),
            coordinate_id: candidate.coordinate_id.clone(),
            target_hash: candidate.target_hash.clone(),
            replica: candidate.replica.clone(),
            attempt_id: candidate.attempt_id.clone(),
            run_hash: candidate.run_hash.clone(),
            implementation: candidate.implementation.clone(),
            checked_compact_metrics: member.compact_metrics,
        });
        accepted.push(decision);
    }

    let metrics_table = ReleaseMetricsTableV1 {
        schema: RELEASE_METRICS_TABLE_SCHEMA.to_string(),
        policy_digest: policy.digest()?,
        members: table_members,
    }
    .canonicalize_against_selection(&policy, &accepted)?;
    let metrics_parquet = encode_release_metrics_parquet(&metrics_table, &policy, &accepted)?;
    let canonical_metrics = metrics_table.canonical_bytes_against_selection(&policy, &accepted)?;
    let release_metrics_table = ReportedArtifactV1::from_payloads(
        metrics_artifact_key,
        ArtifactRole::ReleaseMetricsTable,
        RELEASE_METRICS_MEDIA_TYPE,
        &metrics_parquet,
        &canonical_metrics,
        None,
        None,
    )
    .map_err(|error| EvidenceError::new("release_builder.release_metrics_table", error.to_string()))?;
    let release = EvidenceReleaseV1 {
        schema: EVIDENCE_RELEASE_SCHEMA.to_string(),
        release_id: release_id.into(),
        policy,
        policy_digest: metrics_table.policy_digest.clone(),
        generator,
        accepted,
        release_metrics_table,
    }
    .canonicalize()?;

    // Recheck the just-built physical bytes through the same relation used by
    // a later reader. This is cheap and prevents builder/reader drift.
    let decoded = decode_release_metrics_parquet(&metrics_parquet)?.canonicalize_against(&release)?;
    release
        .release_metrics_table
        .check_reopened_payloads(
            &metrics_parquet,
            &release_metrics_table_canonical_bytes(&decoded, &release)?,
        )
        .map_err(|error| EvidenceError::new("release_builder.release_metrics_table", error.to_string()))?;

    Ok(BuiltEvidenceReleaseV1 {
        release,
        metrics_table,
        metrics_parquet,
    })
}

#[cfg(feature = "parquet")]
fn release_metrics_table_canonical_bytes(
    table: &ReleaseMetricsTableV1,
    release: &EvidenceReleaseV1,
) -> Result<Vec<u8>, EvidenceError> {
    table.canonical_bytes_against(release)
}

/// Immutable release: accepted decisions plus the exact table bytes and
/// scientific content they generated. Supersession/invalidation are later
/// append-only receipts; this object is never edited.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct EvidenceReleaseV1 {
    pub schema: String,
    pub release_id: String,
    pub policy: EvidencePolicyCardV1,
    pub policy_digest: EvidencePolicyDigest,
    pub generator: ReleaseGeneratorV1,
    pub accepted: Vec<AcceptanceDecisionV1>,
    pub release_metrics_table: fern_artifact::ReportedArtifactV1,
}

impl EvidenceReleaseV1 {
    pub fn canonicalize(mut self) -> Result<Self, EvidenceError> {
        if self.schema != EVIDENCE_RELEASE_SCHEMA {
            return Err(EvidenceError::new(
                "evidence_release.schema",
                format!("expected {EVIDENCE_RELEASE_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        validate_logical_id("evidence_release.release_id", &self.release_id)?;
        validate_logical_id("evidence_release.generator.generator_id", &self.generator.generator_id)?;
        self.policy = self.policy.canonicalize()?;
        if self.policy_digest != self.policy.digest()? {
            return Err(EvidenceError::new(
                "evidence_release.policy_digest",
                "does not match the embedded policy card",
            ));
        }
        if self.release_metrics_table.role != ArtifactRole::ReleaseMetricsTable {
            return Err(EvidenceError::new(
                "evidence_release.release_metrics_table.role",
                "must have the dedicated release_metrics_table role",
            ));
        }
        if self.release_metrics_table.media_type != RELEASE_METRICS_MEDIA_TYPE {
            return Err(EvidenceError::new(
                "evidence_release.release_metrics_table.media_type",
                format!("must be {RELEASE_METRICS_MEDIA_TYPE:?}"),
            ));
        }
        self.release_metrics_table
            .validate()
            .map_err(|error| EvidenceError::new("evidence_release.release_metrics_table", error.to_string()))?;
        if self.accepted.is_empty() {
            return Err(EvidenceError::new(
                "evidence_release.accepted",
                "must contain at least one accepted candidate",
            ));
        }
        let mut coordinate_keys = HashSet::new();
        let mut decision_ids = HashSet::new();
        let mut idempotency_keys = HashSet::new();
        for decision in &mut self.accepted {
            decision.validate_against(&self.policy)?;
            if decision.decision != EvidenceDecision::Accepted {
                return Err(EvidenceError::new(
                    "evidence_release.accepted",
                    "cannot contain a rejected or held decision",
                ));
            }
            let key = (
                decision.candidate.matrix_id.clone(),
                decision.candidate.coordinate_id.clone(),
            );
            if !coordinate_keys.insert(key) {
                return Err(EvidenceError::new(
                    "evidence_release.accepted",
                    "contains more than one accepted Attempt for the same declared Coordinate",
                ));
            }
            if !decision_ids.insert(decision.decision_id.clone()) {
                return Err(EvidenceError::new(
                    "evidence_release.accepted.decision_id",
                    "contains a duplicate decision identity",
                ));
            }
            if !idempotency_keys.insert(decision.idempotency_key.clone()) {
                return Err(EvidenceError::new(
                    "evidence_release.accepted.idempotency_key",
                    "contains a duplicate idempotency key",
                ));
            }
            // The decision digest already proves these values, but the release
            // embeds the whole object for offline audit. Canonicalize the
            // embedded copy too so input ordering cannot change release ID.
            decision.candidate = decision.candidate.clone().canonicalize()?;
        }
        self.accepted.sort_by(|left, right| {
            (
                left.candidate.matrix_id.as_str(),
                left.candidate.coordinate_id.as_str(),
                left.decision_id.as_str(),
            )
                .cmp(&(
                    right.candidate.matrix_id.as_str(),
                    right.candidate.coordinate_id.as_str(),
                    right.decision_id.as_str(),
                ))
        });
        Ok(self)
    }

    /// Prove that the frozen membership is exactly the predeclared Coordinate
    /// set. Artifact validity alone cannot prevent a post-hoc partial release.
    pub fn canonicalize_against_required_coordinates(
        self,
        required_coordinates: &[EvidenceCoordinateV1],
    ) -> Result<Self, EvidenceError> {
        let canonical = self.canonicalize()?;
        let required = canonical_coordinate_set(required_coordinates)?;
        let actual = canonical
            .accepted
            .iter()
            .map(|decision| EvidenceCoordinateV1 {
                matrix_id: decision.candidate.matrix_id.clone(),
                coordinate_id: decision.candidate.coordinate_id.clone(),
            })
            .collect::<Vec<_>>();
        let actual = canonical_coordinate_set(&actual)?;
        if actual != required {
            let required_set = required.iter().cloned().collect::<BTreeSet<_>>();
            let actual_set = actual.iter().cloned().collect::<BTreeSet<_>>();
            let missing = required_set.difference(&actual_set).cloned().collect::<Vec<_>>();
            let unexpected = actual_set.difference(&required_set).cloned().collect::<Vec<_>>();
            return Err(EvidenceError::new(
                "evidence_release.accepted",
                format!(
                    "does not equal the predeclared Coordinate set; missing={missing:?}, unexpected={unexpected:?}"
                ),
            ));
        }
        Ok(canonical)
    }

    pub fn digest(&self) -> Result<EvidenceReleaseDigest, EvidenceError> {
        let canonical = self.clone().canonicalize()?;
        Ok(EvidenceReleaseDigest::from_domain_bytes(
            b"fern.evidence_release.v1\0",
            &canonical_json("evidence_release", &canonical)?,
        ))
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use fern_artifact::{
        ArtifactKey, ByteDigest, COMPACT_METRICS_SCHEMA, CompactMetricsCaseV1, ExactRatio, LedgerDiagnosticV1,
        METRIC_VALUE_SCHEMA, MetricAggregationV1, MetricContractV1, MetricSourceRepresentation, MetricSpecV1,
        MetricTargetKind, MetricValueSpace, MetricValueV1, NumericalToleranceV1, RiskPrimitiveV1, ScientificCaseId,
    };

    pub(crate) fn implementation(byte: u8) -> ModelImplementationHash {
        ModelImplementationHash::parse(format!("implementation:sha256:{}", format!("{byte:02x}").repeat(32))).unwrap()
    }

    fn content(byte: u8) -> ScientificContentDigest {
        ScientificContentDigest::parse(format!("sha256:{}", format!("{byte:02x}").repeat(32))).unwrap()
    }

    fn checker(byte: u8) -> ImplementationDigest {
        ImplementationDigest::parse(format!("sha256:{}", format!("{byte:02x}").repeat(32))).unwrap()
    }

    fn artifact(role: ArtifactRole, key: &str, byte: u8) -> ReportedArtifactV1 {
        ReportedArtifactV1 {
            schema: fern_artifact::REPORTED_ARTIFACT_SCHEMA.to_string(),
            artifact_key: ArtifactKey::parse(key).unwrap(),
            role,
            media_type: "application/octet-stream".to_string(),
            byte_count: 1,
            byte_digest: ByteDigest::parse(format!("sha256:{}", format!("{byte:02x}").repeat(32))).unwrap(),
            content_digest: content(byte),
            tensor_layout: None,
            reported_forecast_meaning: None,
        }
    }

    fn artifact_reference(role: ArtifactRole, key: &str, byte: u8) -> ArtifactReference {
        ArtifactReference {
            artifact_key: ArtifactKey::parse(key).unwrap(),
            role,
            content_digest: content(byte),
        }
    }

    pub(crate) fn compact_metrics() -> CompactMetricsV1 {
        let observed = artifact_reference(ArtifactRole::FinalNoisyInput, "observed", 5);
        let point = artifact_reference(ArtifactRole::DensePointForecast, "point", 6);
        let scale = artifact_reference(ArtifactRole::TrainingScale, "scale", 7);
        let aggregation = MetricAggregationV1::point_default();
        let mut metric_values = [
            ExactRatio::new(1, 1).unwrap(),
            ExactRatio::new(3, 2).unwrap(),
            ExactRatio::new(2, 1).unwrap(),
        ]
        .into_iter()
        .enumerate()
        .map(|(index, threshold_multiplier)| MetricValueV1 {
            schema: METRIC_VALUE_SCHEMA.to_string(),
            spec: MetricSpecV1::EffectivePredictionTime {
                metric_version: 1,
                target_kind: MetricTargetKind::ObservedCorrupted,
                target: observed.clone(),
                threshold_source: scale.clone(),
                threshold_multiplier,
                horizon_length: 4,
                source_representation: MetricSourceRepresentation::Point,
                source: point.clone(),
                aggregation,
            },
            value: (index + 1) as f64,
        })
        .collect::<Vec<_>>();
        metric_values.push(MetricValueV1 {
            schema: METRIC_VALUE_SCHEMA.to_string(),
            spec: MetricSpecV1::PerChannelSquaredWasserstein {
                metric_version: 1,
                target_kind: MetricTargetKind::ObservedCorrupted,
                target: observed.clone(),
                horizon_length: 4,
                value_space: MetricValueSpace::Physical,
                scale_reference: None,
                source_representation: MetricSourceRepresentation::Point,
                source: point.clone(),
                aggregation,
            },
            value: 0.25,
        });
        let (risk_primitive, derived_diagnostic) = RiskPrimitiveV1::from_aligned(&[0.0], &[1.0], None).unwrap();
        CompactMetricsV1 {
            schema: COMPACT_METRICS_SCHEMA.to_string(),
            attempt_id: "attempt-1".to_string(),
            request_digest: content(8),
            metric_contract: MetricContractV1::time_series_v1(vec![MetricTargetKind::ObservedCorrupted]).unwrap(),
            numerical_tolerance: NumericalToleranceV1::f64_default(),
            checked_source_artifacts: vec![observed, point, scale],
            cases: vec![CompactMetricsCaseV1 {
                case_id: ScientificCaseId::parse("case-1").unwrap(),
                case_realization_digest: content(9),
                risk_primitive,
                derived_diagnostic: LedgerDiagnosticV1 {
                    ledger_residual_sum: derived_diagnostic.ledger_residual_sum,
                },
                metric_values,
            }],
        }
    }

    fn release_metrics_table(policy: &EvidencePolicyCardV1, decision: &AcceptanceDecisionV1) -> ReleaseMetricsTableV1 {
        let candidate = &decision.candidate;
        let compact_metrics = compact_metrics();
        let stored = fern_artifact::encode_compact_metrics_arrow(&compact_metrics).unwrap();
        let canonical = compact_metrics.canonical_bytes().unwrap();
        let reported_artifact = ReportedArtifactV1::from_payloads(
            ArtifactKey::parse("compact-metrics").unwrap(),
            ArtifactRole::CompactMetrics,
            COMPACT_METRICS_MEDIA_TYPE,
            &stored,
            &canonical,
            None,
            None,
        )
        .unwrap();
        ReleaseMetricsTableV1 {
            schema: RELEASE_METRICS_TABLE_SCHEMA.to_string(),
            policy_digest: policy.digest().unwrap(),
            members: vec![ReleaseMetricsMemberV1 {
                decision_digest: decision.digest_against(policy).unwrap(),
                matrix_id: candidate.matrix_id.clone(),
                coordinate_id: candidate.coordinate_id.clone(),
                target_hash: candidate.target_hash.clone(),
                replica: candidate.replica.clone(),
                attempt_id: candidate.attempt_id.clone(),
                run_hash: candidate.run_hash.clone(),
                implementation: candidate.implementation.clone(),
                checked_compact_metrics: CheckedCompactMetricsV1::from_reopened_arrow(reported_artifact, &stored)
                    .unwrap(),
            }],
        }
    }

    fn policy(replica_policy: ReplicaAcceptancePolicy) -> EvidencePolicyCardV1 {
        EvidencePolicyCardV1 {
            schema: EVIDENCE_POLICY_SCHEMA.to_string(),
            policy_id: "paper-release".to_string(),
            revision: 1,
            matrix_ids: vec!["matrix-b".to_string(), "matrix-a".to_string()],
            selection_scope: ReleaseSelectionScopeV1::AllDeclaredCoordinates {
                coordinate_set_digest: coordinate_set_digest(&[
                    EvidenceCoordinateV1 {
                        matrix_id: "matrix-a".to_string(),
                        coordinate_id: "coordinate-1".to_string(),
                    },
                    EvidenceCoordinateV1 {
                        matrix_id: "matrix-b".to_string(),
                        coordinate_id: "coordinate-2".to_string(),
                    },
                ])
                .unwrap(),
            },
            required_artifact_roles: vec![ArtifactRole::InvocationReceipt, ArtifactRole::CompactMetrics],
            replica_policy,
        }
    }

    pub(crate) fn candidate(replica: Option<DeclaredReplicaV1>) -> VerifiedEvidenceCandidateV1 {
        let compact = compact_metrics();
        let stored = fern_artifact::encode_compact_metrics_arrow(&compact).unwrap();
        let reported_artifact = ReportedArtifactV1::from_payloads(
            ArtifactKey::parse("compact-metrics").unwrap(),
            ArtifactRole::CompactMetrics,
            COMPACT_METRICS_MEDIA_TYPE,
            &stored,
            &compact.canonical_bytes().unwrap(),
            None,
            None,
        )
        .unwrap();
        let outputs = vec![
            reported_artifact,
            artifact(ArtifactRole::InvocationReceipt, "invocation", 2),
        ];
        VerifiedEvidenceCandidateV1 {
            schema: VERIFIED_CANDIDATE_SCHEMA.to_string(),
            matrix_id: "matrix-a".to_string(),
            coordinate_id: "coordinate-1".to_string(),
            target_hash: "target-1".to_string(),
            replica,
            attempt_id: "attempt-1".to_string(),
            run_hash: "run-1".to_string(),
            implementation: implementation(1),
            checked_commit: CheckedArtifactCommitV1 {
                schema: CHECKED_COMMIT_SCHEMA.to_string(),
                check_id: "check-attempt-1".to_string(),
                attempt_id: "attempt-1".to_string(),
                run_hash: "run-1".to_string(),
                commit_content_digest: content(3),
                checker_implementation: checker(2),
                checker_actor_id: "artifact-verifier".to_string(),
                checked_at_unix_ms: 1,
                outputs,
            },
        }
    }

    pub(crate) fn campaign_ledger() -> CampaignLedgerV1 {
        let candidate = candidate(None);
        let compact = compact_metrics();
        let stored = fern_artifact::encode_compact_metrics_arrow(&compact).unwrap();
        let reported_artifact = ReportedArtifactV1::from_payloads(
            ArtifactKey::parse("compact-metrics").unwrap(),
            ArtifactRole::CompactMetrics,
            COMPACT_METRICS_MEDIA_TYPE,
            &stored,
            &compact.canonical_bytes().unwrap(),
            None,
            None,
        )
        .unwrap();
        CampaignLedgerV1 {
            schema: CAMPAIGN_LEDGER_SCHEMA.to_string(),
            members: vec![CampaignLedgerMemberV1 {
                checked_commit_digest: candidate.checked_commit.digest().unwrap(),
                decisions: Vec::new(),
                matrix_id: candidate.matrix_id,
                coordinate_id: candidate.coordinate_id,
                target_hash: candidate.target_hash,
                replica: candidate.replica,
                attempt_id: candidate.attempt_id,
                run_hash: candidate.run_hash,
                implementation: candidate.implementation,
                checked_compact_metrics: CheckedCompactMetricsV1::from_reopened_arrow(reported_artifact, &stored)
                    .unwrap(),
            }],
        }
    }

    fn accepted(policy: &EvidencePolicyCardV1, candidate: VerifiedEvidenceCandidateV1) -> AcceptanceDecisionV1 {
        AcceptanceDecisionV1 {
            schema: ACCEPTANCE_DECISION_SCHEMA.to_string(),
            decision_id: "decision-1".to_string(),
            idempotency_key: "accept-coordinate-1".to_string(),
            policy_digest: policy.digest().unwrap(),
            candidate_digest: candidate.digest().unwrap(),
            candidate,
            decision: EvidenceDecision::Accepted,
            reason: EvidenceDecisionReason::PolicyMatch,
            actor_id: "test-actor".to_string(),
            decided_at_unix_ms: 1,
            explanation: "predeclared candidate satisfies the policy".to_string(),
        }
    }

    fn release(policy: EvidencePolicyCardV1, decision: AcceptanceDecisionV1) -> EvidenceReleaseV1 {
        let mut release_metrics_table = artifact(ArtifactRole::ReleaseMetricsTable, "release-metrics", 4);
        release_metrics_table.media_type = RELEASE_METRICS_MEDIA_TYPE.to_string();
        EvidenceReleaseV1 {
            schema: EVIDENCE_RELEASE_SCHEMA.to_string(),
            release_id: "aaai27-supplement-v1".to_string(),
            policy_digest: policy.digest().unwrap(),
            policy,
            generator: ReleaseGeneratorV1 {
                generator_id: "release-metrics-builder".to_string(),
                implementation: implementation(3),
            },
            accepted: vec![decision],
            release_metrics_table,
        }
    }

    #[test]
    fn policy_digest_is_stable_under_set_ordering() {
        let first = policy(ReplicaAcceptancePolicy::AllowDeclaredPurposes(vec![
            DeclaredReplicaPurpose::ExecutionPlatformRobustness,
            DeclaredReplicaPurpose::ForecastOriginEvidence,
        ]));
        let mut second = first.clone();
        second.matrix_ids.reverse();
        second.required_artifact_roles.reverse();
        second.replica_policy = ReplicaAcceptancePolicy::AllowDeclaredPurposes(vec![
            DeclaredReplicaPurpose::ForecastOriginEvidence,
            DeclaredReplicaPurpose::ExecutionPlatformRobustness,
        ]);
        assert_eq!(first.digest().unwrap(), second.digest().unwrap());
    }

    #[test]
    fn primary_only_policy_refuses_a_forecast_origin_replica() {
        let policy = policy(ReplicaAcceptancePolicy::PrimaryOnly);
        let candidate = candidate(Some(
            DeclaredReplicaV1::new(1, DeclaredReplicaPurpose::ForecastOriginEvidence).unwrap(),
        ));
        let mut decision = accepted(&policy, candidate);
        let error = decision.validate_against(&policy).unwrap_err();
        assert_eq!(error.field(), "acceptance.decision");

        decision.decision = EvidenceDecision::Rejected;
        decision.reason = EvidenceDecisionReason::ReplicaNotPermitted;
        decision.validate_against(&policy).unwrap();
    }

    #[test]
    fn policy_derives_implementation_conflict_instead_of_trusting_a_reason_string() {
        let policy = policy(ReplicaAcceptancePolicy::PrimaryOnly);
        let candidate = candidate(None);

        assert_eq!(
            policy.required_decision_for_candidate(&candidate, true).unwrap(),
            (EvidenceDecision::Accepted, EvidenceDecisionReason::PolicyMatch)
        );
        assert_eq!(
            policy.required_decision_for_candidate(&candidate, false).unwrap(),
            (EvidenceDecision::Held, EvidenceDecisionReason::ImplementationConflict)
        );
    }

    #[test]
    fn an_implementation_hold_cannot_hide_a_missing_required_artifact() {
        let policy = policy(ReplicaAcceptancePolicy::PrimaryOnly);
        let mut candidate = candidate(None);
        candidate
            .checked_commit
            .outputs
            .retain(|output| output.role != ArtifactRole::InvocationReceipt);
        let mut decision = accepted(&policy, candidate);
        decision.decision = EvidenceDecision::Held;
        decision.reason = EvidenceDecisionReason::ImplementationConflict;

        let error = decision.validate_against(&policy).unwrap_err();
        assert_eq!(error.field(), "acceptance.decision");
        assert_eq!(
            policy
                .required_decision_for_candidate(&decision.candidate, false)
                .unwrap(),
            (
                EvidenceDecision::Rejected,
                EvidenceDecisionReason::MissingRequiredArtifact
            )
        );
    }

    #[test]
    fn acceptance_binds_the_exact_checked_candidate() {
        let policy = policy(ReplicaAcceptancePolicy::PrimaryOnly);
        let candidate = candidate(None);
        let mut decision = accepted(&policy, candidate);
        decision.candidate.checked_commit.commit_content_digest = content(9);
        let error = decision.validate_against(&policy).unwrap_err();
        assert_eq!(error.field(), "acceptance.candidate_digest");
    }

    #[test]
    fn candidate_and_release_digests_ignore_input_collection_order() {
        let first_candidate = candidate(None);
        let mut reordered_candidate = first_candidate.clone();
        reordered_candidate.checked_commit.outputs.reverse();
        assert_eq!(first_candidate.digest().unwrap(), reordered_candidate.digest().unwrap());

        let policy = policy(ReplicaAcceptancePolicy::PrimaryOnly);
        let first_decision = accepted(&policy, first_candidate);
        let reordered_decision = accepted(&policy, reordered_candidate);
        assert_eq!(
            first_decision.digest_against(&policy).unwrap(),
            reordered_decision.digest_against(&policy).unwrap()
        );
        let mut second_candidate = candidate(None);
        second_candidate.matrix_id = "matrix-b".to_string();
        second_candidate.coordinate_id = "coordinate-2".to_string();
        second_candidate.attempt_id = "attempt-2".to_string();
        second_candidate.run_hash = "run-2".to_string();
        second_candidate.checked_commit.check_id = "check-attempt-2".to_string();
        second_candidate.checked_commit.attempt_id = "attempt-2".to_string();
        second_candidate.checked_commit.run_hash = "run-2".to_string();
        let mut second_decision = accepted(&policy, second_candidate);
        second_decision.decision_id = "decision-2".to_string();
        second_decision.idempotency_key = "accept-coordinate-2".to_string();

        let mut forward = release(policy.clone(), first_decision.clone());
        forward.accepted.push(second_decision.clone());
        let mut reverse = release(policy, second_decision);
        reverse.accepted.push(first_decision);
        assert_eq!(forward.digest().unwrap(), reverse.digest().unwrap());
    }

    #[test]
    fn frozen_release_refuses_held_or_duplicate_coordinate_decisions() {
        let policy = policy(ReplicaAcceptancePolicy::PrimaryOnly);
        let candidate = candidate(None);
        let decision = accepted(&policy, candidate);

        let mut held_release = release(policy.clone(), decision.clone());
        held_release.accepted[0].decision = EvidenceDecision::Held;
        held_release.accepted[0].reason = EvidenceDecisionReason::ScientificAuditHold;
        assert!(
            held_release
                .digest()
                .unwrap_err()
                .to_string()
                .contains("rejected or held")
        );

        let mut duplicate_release = release(policy, decision.clone());
        let mut duplicate = decision;
        duplicate.decision_id = "decision-2".to_string();
        duplicate.idempotency_key = "accept-coordinate-1-again".to_string();
        duplicate_release.accepted.push(duplicate);
        assert!(
            duplicate_release
                .digest()
                .unwrap_err()
                .to_string()
                .contains("more than one accepted")
        );
    }

    #[test]
    fn release_digest_is_stable_but_changes_with_table_content() {
        let policy = policy(ReplicaAcceptancePolicy::PrimaryOnly);
        let decision = accepted(&policy, candidate(None));
        let first = release(policy, decision);
        let same = first.clone();
        assert_eq!(first.digest().unwrap(), same.digest().unwrap());

        let mut changed = first;
        changed.release_metrics_table.content_digest = content(8);
        assert_ne!(changed.digest().unwrap(), same.digest().unwrap());
    }

    #[test]
    fn release_metrics_table_binds_every_member_to_the_accepted_selection() {
        let policy = policy(ReplicaAcceptancePolicy::PrimaryOnly);
        let decision = accepted(&policy, candidate(None));
        let table = release_metrics_table(&policy, &decision);

        let canonical = table
            .clone()
            .canonicalize_against_selection(&policy, std::slice::from_ref(&decision))
            .unwrap();
        assert_eq!(canonical.members[0].attempt_id, "attempt-1");

        let frozen = release(policy, decision);
        table.canonicalize_against(&frozen).unwrap();
    }

    #[test]
    fn release_metrics_table_refuses_a_query_identity_drift() {
        let policy = policy(ReplicaAcceptancePolicy::PrimaryOnly);
        let decision = accepted(&policy, candidate(None));
        let mut table = release_metrics_table(&policy, &decision);
        table.members[0].run_hash = "run-different".to_string();

        let error = table.canonicalize_against_selection(&policy, &[decision]).unwrap_err();
        assert_eq!(error.field(), "release_metrics_table.members");
    }

    #[test]
    fn release_metrics_table_refuses_unchecked_compact_metrics() {
        let policy = policy(ReplicaAcceptancePolicy::PrimaryOnly);
        let decision = accepted(&policy, candidate(None));
        let mut table = release_metrics_table(&policy, &decision);
        table.members[0]
            .checked_compact_metrics
            .reported_artifact
            .content_digest = content(99);

        let error = table.canonicalize_against_selection(&policy, &[decision]).unwrap_err();
        assert_eq!(error.field(), "checked_compact_metrics.reported_artifact");
    }

    #[test]
    fn release_metrics_table_refuses_values_not_decoded_from_the_checked_artifact() {
        let policy = policy(ReplicaAcceptancePolicy::PrimaryOnly);
        let decision = accepted(&policy, candidate(None));
        let table = release_metrics_table(&policy, &decision);
        let mut changed = table;
        changed.members[0].checked_compact_metrics.metrics.cases[0]
            .metric_values
            .iter_mut()
            .find(|metric| matches!(metric.spec, MetricSpecV1::PerChannelSquaredWasserstein { .. }))
            .unwrap()
            .value = 99.0;
        let error = changed
            .content_digest_against_selection(&policy, &[decision])
            .unwrap_err();
        assert_eq!(error.field(), "checked_compact_metrics.reported_artifact");
    }

    #[test]
    fn frozen_release_requires_every_predeclared_coordinate() {
        let policy = policy(ReplicaAcceptancePolicy::PrimaryOnly);
        let decision = accepted(&policy, candidate(None));
        let release = release(policy.clone(), decision);
        let required = policy
            .required_release_coordinates(&[
                EvidenceCoordinateV1 {
                    matrix_id: "matrix-a".to_string(),
                    coordinate_id: "coordinate-1".to_string(),
                },
                EvidenceCoordinateV1 {
                    matrix_id: "matrix-b".to_string(),
                    coordinate_id: "coordinate-2".to_string(),
                },
            ])
            .unwrap();

        let error = release
            .canonicalize_against_required_coordinates(&required)
            .unwrap_err();
        assert_eq!(error.field(), "evidence_release.accepted");
        assert!(error.to_string().contains("coordinate-2"));
    }

    #[cfg(feature = "parquet")]
    #[test]
    fn release_metrics_parquet_round_trip_rebuilds_the_typed_table() {
        let policy = policy(ReplicaAcceptancePolicy::PrimaryOnly);
        let decision = accepted(&policy, candidate(None));
        let table = release_metrics_table(&policy, &decision);

        let bytes = encode_release_metrics_parquet(&table, &policy, std::slice::from_ref(&decision)).unwrap();
        assert_eq!(&bytes[..4], b"PAR1");
        let decoded = decode_release_metrics_parquet(&bytes).unwrap();
        assert_eq!(
            table
                .content_digest_against_selection(&policy, std::slice::from_ref(&decision))
                .unwrap(),
            decoded.content_digest().unwrap()
        );

        let canonical_content = decoded.canonical_bytes().unwrap();
        let reported_artifact = ReportedArtifactV1::from_payloads(
            ArtifactKey::parse("release-metrics-parquet").unwrap(),
            ArtifactRole::ReleaseMetricsTable,
            RELEASE_METRICS_MEDIA_TYPE,
            &bytes,
            &canonical_content,
            None,
            None,
        )
        .unwrap();
        reported_artifact
            .check_reopened_payloads(&bytes, &canonical_content)
            .unwrap();

        let mut frozen = release(policy, decision);
        frozen.release_metrics_table = reported_artifact;
        decoded.canonicalize_against(&frozen).unwrap();
    }

    #[cfg(feature = "parquet")]
    #[test]
    fn release_builder_keeps_decision_ledger_parquet_and_descriptor_in_one_relation() {
        let policy = policy(ReplicaAcceptancePolicy::PrimaryOnly);
        let decision = accepted(&policy, candidate(None));
        let mut table = release_metrics_table(&policy, &decision);
        let checked_compact_metrics = table.members.remove(0).checked_compact_metrics;

        let built = build_evidence_release(
            "release-1",
            &policy,
            ReleaseGeneratorV1 {
                generator_id: "fern-release-builder".to_string(),
                implementation: implementation(7),
            },
            ArtifactKey::parse("release-metrics").unwrap(),
            vec![CheckedReleaseMemberV1 {
                decision,
                compact_metrics: checked_compact_metrics,
            }],
        )
        .unwrap();

        let decoded = decode_release_metrics_parquet(&built.metrics_parquet).unwrap();
        assert_eq!(decoded, built.metrics_table);
        assert_eq!(built.release.release_id, "release-1");
        assert_eq!(
            built.release.release_metrics_table.role,
            ArtifactRole::ReleaseMetricsTable
        );
        let canonical = decoded.canonical_bytes_against(&built.release).unwrap();
        built
            .release
            .release_metrics_table
            .check_reopened_payloads(&built.metrics_parquet, &canonical)
            .unwrap();
    }

    #[cfg(feature = "parquet")]
    #[test]
    fn release_metrics_parquet_encoding_canonicalizes_inner_collection_order() {
        let policy = policy(ReplicaAcceptancePolicy::PrimaryOnly);
        let decision = accepted(&policy, candidate(None));
        let table = release_metrics_table(&policy, &decision);
        let first = encode_release_metrics_parquet(&table, &policy, std::slice::from_ref(&decision)).unwrap();

        let mut reordered = table;
        reordered.members[0]
            .checked_compact_metrics
            .metrics
            .checked_source_artifacts
            .reverse();
        reordered.members[0].checked_compact_metrics.metrics.cases[0]
            .metric_values
            .reverse();
        let second = encode_release_metrics_parquet(&reordered, &policy, &[decision]).unwrap();
        assert_eq!(first, second);
    }

    #[cfg(feature = "parquet")]
    #[test]
    fn release_metrics_parquet_reader_refuses_truncated_bytes() {
        let policy = policy(ReplicaAcceptancePolicy::PrimaryOnly);
        let decision = accepted(&policy, candidate(None));
        let table = release_metrics_table(&policy, &decision);
        let mut bytes = encode_release_metrics_parquet(&table, &policy, &[decision]).unwrap();
        bytes.truncate(bytes.len() - 8);
        assert!(decode_release_metrics_parquet(&bytes).is_err());
    }

    #[cfg(feature = "parquet")]
    #[test]
    fn release_metrics_parquet_reader_refuses_a_query_column_that_disagrees_with_the_typed_spec() {
        use std::sync::Arc;

        use arrow_array::{Array, ArrayRef, RecordBatch, StringArray};

        let policy = policy(ReplicaAcceptancePolicy::PrimaryOnly);
        let decision = accepted(&policy, candidate(None));
        let table = release_metrics_table(&policy, &decision);
        let bytes = encode_release_metrics_parquet(&table, &policy, &[decision]).unwrap();

        let (batch, metadata) = crate::release_metrics_parquet::read_release_batch(&bytes).unwrap();
        let metric_id_index = batch.schema().index_of("metric_id").unwrap();
        let metric_id_column = batch
            .column(metric_id_index)
            .as_any()
            .downcast_ref::<StringArray>()
            .unwrap()
            .iter()
            .map(|value| value.unwrap().to_string())
            .collect::<Vec<_>>();
        let mut metric_ids = metric_id_column;
        metric_ids[0] = "wrong_metric".to_string();
        let mut columns = batch.columns().to_vec();
        columns[metric_id_index] = Arc::new(StringArray::from_iter_values(metric_ids)) as ArrayRef;
        let changed_batch = RecordBatch::try_new(batch.schema(), columns).unwrap();
        let changed_bytes = crate::release_metrics_parquet::write_release_batch(&changed_batch, metadata).unwrap();

        let error = decode_release_metrics_parquet(&changed_bytes).unwrap_err();
        assert_eq!(error.field(), "release_metrics_parquet.metric_query_columns");
    }

    #[test]
    fn active_evidence_source_uses_table_names_instead_of_retired_projection_names() {
        let active_source = [include_str!("lib.rs"), include_str!("release_metrics_parquet.rs")].concat();
        for retired_identifier in [
            ["ReleaseMetrics", "ProjectionV1"].concat(),
            ["ReleaseMetrics", "Projection"].concat(),
            ["CampaignMetrics", "Projection"].concat(),
        ] {
            let remains = active_source
                .split(|character: char| !(character.is_ascii_alphanumeric() || character == '_'))
                .any(|token| token == retired_identifier);
            assert!(
                !remains,
                "active fern-evidence source still contains retired Rust name {retired_identifier:?}"
            );
        }
        for retired_wire_name in [
            ["release_metrics_", "projection"].concat(),
            ["campaign_metrics_", "projection"].concat(),
        ] {
            assert!(
                !active_source.contains(&retired_wire_name),
                "active fern-evidence source still contains retired wire name {retired_wire_name:?}"
            );
        }
    }
}
