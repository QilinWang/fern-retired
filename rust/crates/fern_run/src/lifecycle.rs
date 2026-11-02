//! Who owns a unit of work, and what counts as its result.
//!
//! WHY THIS EXISTS. Ownership used to be decided by a file lock, plus a JSON
//! receipt, plus whatever each queue script believed. In one campaign that
//! produced two owners racing on one database, a receipt reading `running`
//! hours after its owner died, a cost guard killing a fresh sweep from a stale
//! receipt, and one coordinate fitted on two machines with nothing anywhere
//! recording that it had happened. Every one of those is the same failure: a
//! question about ownership answered somewhere other than the database holding
//! the work.
//!
//! THE FIVE OPERATIONS, and the promise each makes.
//!
//!   claim   two callers arriving at once produce exactly ONE owner. Not by
//!           being careful -- by a unique index that permits one `claimed`
//!           attempt per coordinate. The loser is told who won.
//!   renew   an owner that is still alive extends its lease. An owner whose
//!           lease was already replaced cannot renew, which is how it learns.
//!   finish  only the holder of the CURRENT lease may report. A worker that
//!           was superseded while it was busy is refused, so its late result
//!           cannot overwrite the one that replaced it.
//!   reject  ends an attempt without producing a result, with a stated reason.
//!   accept  records the single run that speaks for a coordinate. A second
//!           accept FAILS rather than overwriting -- an overwrite is invisible,
//!           a failure is not.
//!
//! DECLARED REPLICAS. Running one cell twice is sometimes the experiment (a
//! seed sweep, a cross-machine check) and sometimes an accident (two queues on
//! one database). The two are indistinguishable after the fact, so they are
//! separated before: a replica is a COORDINATE with an ordinal and a written
//! reason. An undeclared second run has no coordinate to be accepted at, and is
//! refused by name.
//!
//! SQLITE ONLY, DELIBERATELY. This is the single-controller design: one process
//! at a time arbitrates, and SQLite's transactions are enough for that. The
//! shape is chosen so a multi-controller backend can replace the storage
//! without changing any of the five promises above.

use std::path::{Path, PathBuf};

use chrono::Utc;
use fern_execution_profile::ObservedExecutionFingerprint;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::results::{ArtifactManifest, sha256_file};

/// Every way the lifecycle can refuse, each naming what the caller should do.
///
/// These are values rather than strings because the caller acts on them: a
/// worker whose fenced grant is no longer current exits, while a duplicate
/// accept is a scientific question for a person.
#[derive(Debug, Clone, PartialEq, thiserror::Error)]
pub enum LifecycleRefusal {
    /// The attempt already ended, so it has nothing left to report.
    #[error("attempt {attempt_id} is {state}, so it has nothing left to report")]
    AttemptNotRunning { attempt_id: String, state: String },
    /// Accepting a run at a coordinate that was never declared.
    #[error(
        "matrix {matrix_id} declares no coordinate for target {target_hash} replica {replica_ordinal}. A repeat run is only acceptable when it was declared with a reason; otherwise it is an accidental duplicate."
    )]
    CoordinateNotDeclared {
        matrix_id: String,
        target_hash: String,
        replica_ordinal: i64,
    },
    /// A coordinate already has the result that speaks for it.
    #[error(
        "coordinate {coordinate_id} already accepted run {run_hash} at {accepted_at}. Declare a replica if a second run is intended."
    )]
    AlreadyAccepted {
        coordinate_id: String,
        run_hash: String,
        accepted_at: String,
    },
    /// One run cannot be the accepted result of two coordinates.
    #[error("run {run_hash} is already the accepted result of another coordinate")]
    RunAcceptedElsewhere { run_hash: String },
    /// The caller planned to select one sealed artifact manifest, but the
    /// attempt now points at another. A candidate is never selected against a
    /// file set it did not name before the selection operation began.
    #[error(
        "attempt {attempt_id} run {run_hash} sealed a different artifact manifest ({actual_receipt_digest}) than the acceptance request named ({expected_receipt_digest})"
    )]
    SealedArtifactDigestMismatch {
        attempt_id: String,
        run_hash: String,
        expected_receipt_digest: String,
        actual_receipt_digest: String,
    },
    /// An attempt must have finished before its result can be accepted.
    #[error("attempt {attempt_id} is {state}; only a finished attempt has a result to accept")]
    AttemptNotFinished { attempt_id: String, state: String },
    /// Reusing one idempotency key for a different request would make replay
    /// ambiguous, so the caller must mint a new operation key deliberately.
    #[error(
        "idempotency key {idempotency_key} was already used for a different request; choose a new key instead of making its previous outcome ambiguous"
    )]
    IdempotencyKeyConflict { idempotency_key: String },
    /// A resource slot is an execution-profile declaration, not a string a
    /// launcher may invent while it is about to spawn work.
    #[error(
        "resource slot {resource_slot_id} is not declared for execution profile revision {execution_profile_revision}; run profile preflight/registration before starting work"
    )]
    ResourceSlotNotDeclared {
        resource_slot_id: String,
        execution_profile_revision: String,
    },
    /// A worker report named an authority that is no longer the database's
    /// current grant, even if that old process is still alive.
    #[error(
        "{authority_kind} lease {lease_id} at epoch {fencing_epoch} is no longer current; stop this stale worker instead of reporting its result"
    )]
    AuthorityNoLongerCurrent {
        authority_kind: &'static str,
        lease_id: String,
        fencing_epoch: i64,
    },
    /// Lease ids and epochs are public identifiers; this secret check stops a
    /// database reader from forging a worker report with those identifiers.
    #[error("worker capability for attempt {attempt_id} is not the capability issued with its current lease")]
    LeaseCapabilityRejected { attempt_id: String },
    /// A queue-parent operation did not present the capability issued to the
    /// current coordinator grant. This is separate from a worker permit: the
    /// two secrets intentionally cannot substitute for each other.
    #[error("queue capability is not the capability issued with current coordinator lease {coordinator_lease_id}")]
    CoordinatorCapabilityRejected { coordinator_lease_id: String },
    /// A coordinator cannot be released while one of its workers still holds a
    /// real resource slot. Releasing then would turn a live worker into an
    /// orphan that can no longer prove its authority.
    #[error(
        "coordinator lease {coordinator_lease_id} still owns resource slot {resource_slot_id}; wait for its worker result before releasing the queue"
    )]
    CoordinatorHasActiveResource {
        coordinator_lease_id: String,
        resource_slot_id: String,
    },
    /// A new-style evidence decision may select only an attempt that sealed an
    /// artifact manifest for this exact run identity.
    #[error(
        "attempt {attempt_id} cannot be selected for run {run_hash} until its matching artifact manifest is sealed"
    )]
    ArtifactNotSealed { attempt_id: String, run_hash: String },
}

#[derive(Debug, thiserror::Error)]
pub enum LifecycleError {
    #[error(transparent)]
    Refused(#[from] LifecycleRefusal),
    #[error("cannot serialize lifecycle receipt: {0}")]
    Serialization(#[from] serde_json::Error),
    #[error("invalid lifecycle request: {0}")]
    InvalidRequest(String),
}

pub type LifecycleResult<T> = Result<T, LifecycleError>;

fn refuse<T>(refusal: LifecycleRefusal) -> LifecycleResult<T> {
    Err(LifecycleError::Refused(refusal))
}

fn require_lower_sha256(value: &str, label: &str) -> LifecycleResult<()> {
    if value.len() == 64
        && value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        Ok(())
    } else {
        Err(LifecycleError::InvalidRequest(format!(
            "{label} must be exactly 64 lowercase hexadecimal characters"
        )))
    }
}

fn now_text() -> String {
    Utc::now().to_rfc3339()
}

// ── Pass 1: pure vocabulary for the future authority lifecycle ────────────
//
// These types deliberately do NOT change today's SQLite rows.  They make the
// intended states and their legal direction testable first; Passes 2–4 will
// decide how an authority operation persists each transition.  Keeping this
// layer pure means a bad transition can be rejected in a unit test without
// launching a worker, touching a queue, or rewriting an experiment database.

/// A requested state change skips a lifecycle step or tries to revive history.
///
/// This is separate from `LifecycleRefusal`: the latter explains a refusal from
/// today's live database, while this value describes the pure target state
/// machine that later authority operations will enforce durably.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct IllegalLifecycleTransition {
    pub object_kind: &'static str,
    pub from: &'static str,
    pub to: &'static str,
}

impl std::fmt::Display for IllegalLifecycleTransition {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "cannot move {} from {} to {}; use its next named lifecycle operation",
            self.object_kind, self.from, self.to
        )
    }
}

impl std::error::Error for IllegalLifecycleTransition {}

fn reject_state_transition<T>(
    object_kind: &'static str,
    from: &'static str,
    to: &'static str,
) -> Result<T, IllegalLifecycleTransition> {
    Err(IllegalLifecycleTransition { object_kind, from, to })
}

/// The durable lifecycle of a registered experiment matrix.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MatrixState {
    Registered,
    Open,
    Settled,
    Archived,
}

impl MatrixState {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Registered => "registered",
            Self::Open => "open",
            Self::Settled => "settled",
            Self::Archived => "archived",
        }
    }

    /// Validate one forward matrix transition without mutating any store.
    pub fn transition_to(self, next: Self) -> Result<Self, IllegalLifecycleTransition> {
        match (self, next) {
            (Self::Registered, Self::Open) | (Self::Open, Self::Settled) | (Self::Settled, Self::Archived) => Ok(next),
            _ => reject_state_transition("matrix", self.as_str(), next.as_str()),
        }
    }
}

/// The lifecycle of one predeclared scientific coordinate or declared replica.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CoordinateState {
    Declared,
    Satisfied,
    Retired,
}

impl CoordinateState {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Declared => "declared",
            Self::Satisfied => "satisfied",
            Self::Retired => "retired",
        }
    }

    /// Validate the Coordinate's deliberately small scientific lifecycle.
    /// Temporary availability, every candidate, and every artifact check live
    /// on their own objects.  The Coordinate is therefore only declared,
    /// associated with one selected candidate, or retired.
    pub fn transition_to(self, next: Self) -> Result<Self, IllegalLifecycleTransition> {
        match (self, next) {
            (Self::Declared, Self::Satisfied) | (Self::Declared, Self::Retired) | (Self::Satisfied, Self::Retired) => {
                Ok(next)
            }
            _ => reject_state_transition("coordinate", self.as_str(), next.as_str()),
        }
    }
}

/// One concrete execution attempt. Its history is append-only; retry creates
/// another attempt rather than moving a failed one back to `Running`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AttemptState {
    Opened,
    Running,
    ResultRecorded,
    Closed,
    Failed,
    Lost,
    Expired,
}

impl AttemptState {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Opened => "opened",
            Self::Running => "running",
            Self::ResultRecorded => "result-recorded",
            Self::Closed => "closed",
            Self::Failed => "failed",
            Self::Lost => "lost",
            Self::Expired => "expired",
        }
    }

    /// True while the partial unique constraint must still exclude another
    /// owner for this coordinate. This is intentionally one typed predicate,
    /// not a future-prone check for one spelling such as `claimed`.
    pub const fn is_active(self) -> bool {
        matches!(self, Self::Opened | Self::Running | Self::ResultRecorded)
    }

    /// Validate one forward attempt transition without changing its history.
    pub fn transition_to(self, next: Self) -> Result<Self, IllegalLifecycleTransition> {
        match (self, next) {
            (Self::Opened, Self::Running)
            | (Self::Opened, Self::Failed)
            | (Self::Opened, Self::Lost)
            | (Self::Opened, Self::Expired)
            | (Self::Running, Self::ResultRecorded)
            | (Self::Running, Self::Failed)
            | (Self::Running, Self::Lost)
            | (Self::Running, Self::Expired)
            | (Self::ResultRecorded, Self::Closed) => Ok(next),
            _ => reject_state_transition("attempt", self.as_str(), next.as_str()),
        }
    }
}

/// A lease is either current, deliberately revoked, or ended by registry time.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LeaseState {
    Active,
    Revoked,
    Expired,
}

impl LeaseState {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Active => "active",
            Self::Revoked => "revoked",
            Self::Expired => "expired",
        }
    }

    /// Validate a terminal lease transition. A new claim creates a new lease;
    /// it never makes an old lease active again.
    pub fn transition_to(self, next: Self) -> Result<Self, IllegalLifecycleTransition> {
        match (self, next) {
            (Self::Active, Self::Revoked) | (Self::Active, Self::Expired) => Ok(next),
            _ => reject_state_transition("lease", self.as_str(), next.as_str()),
        }
    }
}

/// The verification and retention lifecycle of one attempt-scoped artifact.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ArtifactState {
    Expected,
    Written,
    Sealed,
    Checked,
    Rejected,
    Invalidated,
    Tombstoned,
}

impl ArtifactState {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Expected => "expected",
            Self::Written => "written",
            Self::Sealed => "sealed",
            Self::Checked => "checked",
            Self::Rejected => "rejected",
            Self::Invalidated => "invalidated",
            Self::Tombstoned => "tombstoned",
        }
    }

    /// Validate an artifact transition. Invalidating or tombstoning records a
    /// new fact; neither pretends the earlier check or file never existed.
    pub fn transition_to(self, next: Self) -> Result<Self, IllegalLifecycleTransition> {
        match (self, next) {
            (Self::Expected, Self::Written)
            | (Self::Written, Self::Sealed)
            | (Self::Sealed, Self::Checked)
            | (Self::Sealed, Self::Rejected)
            | (Self::Checked, Self::Invalidated)
            | (Self::Checked, Self::Tombstoned)
            | (Self::Rejected, Self::Tombstoned)
            | (Self::Invalidated, Self::Tombstoned) => Ok(next),
            _ => reject_state_transition("artifact", self.as_str(), next.as_str()),
        }
    }
}

/// The lifecycle of a named evidence release, distinct from artifact checking.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EvidenceReleaseState {
    Candidate,
    Accepted,
    Frozen,
    Superseded,
    Invalidated,
}

impl EvidenceReleaseState {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Candidate => "candidate",
            Self::Accepted => "accepted",
            Self::Frozen => "frozen",
            Self::Superseded => "superseded",
            Self::Invalidated => "invalidated",
        }
    }

    /// Validate an evidence transition. A frozen release can be superseded or
    /// invalidated by a later event, but never reopened and silently edited.
    pub fn transition_to(self, next: Self) -> Result<Self, IllegalLifecycleTransition> {
        match (self, next) {
            (Self::Candidate, Self::Accepted)
            | (Self::Candidate, Self::Invalidated)
            | (Self::Accepted, Self::Frozen)
            | (Self::Accepted, Self::Superseded)
            | (Self::Accepted, Self::Invalidated)
            | (Self::Frozen, Self::Superseded)
            | (Self::Frozen, Self::Invalidated) => Ok(next),
            _ => reject_state_transition("evidence release", self.as_str(), next.as_str()),
        }
    }
}

/// One intended unit of work, as declared before anything runs.
#[derive(Debug, Clone)]
pub struct DeclaredCoordinate {
    pub target_hash: String,
    /// 0 is the unit itself. A positive ordinal is a repeat that was MEANT.
    pub replica_ordinal: i64,
    /// Required for a positive ordinal: why this cell is being run again.
    pub replica_reason: Option<String>,
    pub coordinate_json: String,
    /// Execution-cohort partition only; never persisted as scientific target
    /// data and never enters `target_hash`.
    matrix_implementation_source_snapshot_id: Option<String>,
}

impl DeclaredCoordinate {
    pub fn unit(target_hash: impl Into<String>, coordinate_json: impl Into<String>) -> Self {
        Self {
            target_hash: target_hash.into(),
            replica_ordinal: 0,
            replica_reason: None,
            coordinate_json: coordinate_json.into(),
            matrix_implementation_source_snapshot_id: None,
        }
    }

    /// A repeat of one cell that the experiment intends, with its reason.
    pub fn replica(
        target_hash: impl Into<String>,
        ordinal: i64,
        reason: impl Into<String>,
        coordinate_json: impl Into<String>,
    ) -> Self {
        Self {
            target_hash: target_hash.into(),
            replica_ordinal: ordinal,
            replica_reason: Some(reason.into()),
            coordinate_json: coordinate_json.into(),
            matrix_implementation_source_snapshot_id: None,
        }
    }

    /// Keep one scientific Coordinate while placing its execution in the
    /// Matrix owned by this immutable implementation source snapshot.
    pub fn with_matrix_implementation_source(mut self, snapshot_id: impl Into<String>) -> Self {
        self.matrix_implementation_source_snapshot_id = Some(snapshot_id.into());
        self
    }
}

// ── Pass 3: a worker may spawn only after one atomic authority decision ───
//
// These types are intentionally more precise than a generic `RunRequest`.
// One launcher needs three different permissions at once: advance a Matrix,
// compute a Coordinate, and occupy one physical ResourceSlot.  Keeping their
// names in the request makes a missing authority visible before a subprocess
// exists, instead of relying on a later OOM or a misleading PID check.

/// What kind of program asked the registry to change authority.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum LifecycleActorKind {
    Cli,
    Tui,
    Agent,
    Human,
    Supervisor,
    Worker,
    LegacyImport,
}

impl LifecycleActorKind {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Cli => "cli",
            Self::Tui => "tui",
            Self::Agent => "agent",
            Self::Human => "human",
            Self::Supervisor => "supervisor",
            Self::Worker => "worker",
            Self::LegacyImport => "legacy-import",
        }
    }
}

/// The actor is named separately from the machine it happens to use.  One
/// agent may move from WSL to RunPod; its identity remains traceable while the
/// observed host facts belong in an Attempt receipt later.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct LifecycleActor {
    pub kind: LifecycleActorKind,
    pub actor_id: String,
    pub owner_host: String,
    pub owner_pid: i64,
}
/// A capacity-one resource declared by one execution-profile revision.
/// A four-way CPU pool is four declarations, not a mutable counter that can
/// accidentally over-allocate when two launchers read it at once.
#[derive(Clone, PartialEq, Eq)]
pub struct ResourceSlotDeclaration {
    pub resource_slot_id: String,
    pub execution_profile_revision: String,
    /// A small, canonical JSON description such as `{\"kind\":\"cuda\",\"index\":0}`.
    pub description_json: String,
    pub actor: LifecycleActor,
    pub policy_revision: String,
}

/// Exact source provenance observed by the process claiming an Attempt.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct AttemptImplementationSourceV1 {
    pub snapshot_id: String,
    pub worker_binary_sha256: String,
    pub implementation_source_archive_sha256: String,
}

impl AttemptImplementationSourceV1 {
    pub fn validate(&self) -> LifecycleResult<()> {
        let snapshot_digest = self
            .snapshot_id
            .strip_prefix("implementation-source-snapshot:")
            .unwrap_or_default();
        require_lower_sha256(snapshot_digest, "implementation source snapshot id")?;
        require_lower_sha256(&self.worker_binary_sha256, "worker binary sha256")?;
        require_lower_sha256(
            &self.implementation_source_archive_sha256,
            "implementation source archive sha256",
        )?;
        Ok(())
    }
}

/// One request to start a concrete, already-declared Coordinate.
///
/// `coordinator_capability` is present only when a currently authorized queue
/// parent asks to schedule another Coordinate. On the first claim, the
/// registry generates a queue-only secret and returns it once in the decision;
/// the registry stores only its digest.  A worker receives a different secret
/// for progress and completion, so it cannot impersonate its queue parent.
pub struct StartOrResumeRequest {
    pub matrix_id: String,
    pub coordinate_id: String,
    pub execution_profile_revision: String,
    /// Typed machine facts captured by the controlling launch preflight. The
    /// registry stores this object in the transition receipt so a profile
    /// alias can never stand in for what the launcher actually saw.
    pub observed_execution: ObservedExecutionFingerprint,
    pub resource_slot_id: String,
    pub actor: LifecycleActor,
    pub lease_seconds: i64,
    /// The Plan names the snapshot in advance; the claiming process supplies
    /// the executable bytes and embedded archive identity it actually runs.
    pub implementation_source: Option<AttemptImplementationSourceV1>,
    /// The immutable manifest reopened by the public Registry Home reader
    /// before this transaction. The snapshot row cannot be updated or deleted,
    /// so the transaction rechecks its content identity and consumes the
    /// manifest without retaining a second Rust SQL reader.
    pub checked_implementation_source_snapshot:
        Option<crate::implementation_source_snapshot::ImplementationSourceSnapshotV1>,
    pub coordinator_capability: Option<String>,
    pub idempotency_key: String,
    pub policy_revision: String,
}

impl std::fmt::Debug for StartOrResumeRequest {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("StartOrResumeRequest")
            .field("matrix_id", &self.matrix_id)
            .field("coordinate_id", &self.coordinate_id)
            .field("execution_profile_revision", &self.execution_profile_revision)
            .field(
                "observed_execution_content_digest",
                &self.observed_execution_content_digest().ok(),
            )
            .field("resource_slot_id", &self.resource_slot_id)
            .field("actor", &self.actor)
            .field("lease_seconds", &self.lease_seconds)
            .field("implementation_source", &self.implementation_source)
            .field(
                "checked_implementation_source_snapshot",
                &self
                    .checked_implementation_source_snapshot
                    .as_ref()
                    .map(|snapshot| snapshot.snapshot_id.as_str()),
            )
            .field(
                "coordinator_capability",
                &self.coordinator_capability.as_ref().map(|_| "[redacted]"),
            )
            .field("idempotency_key", &self.idempotency_key)
            .field("policy_revision", &self.policy_revision)
            .finish()
    }
}

impl StartOrResumeRequest {
    pub fn validate(&self) -> LifecycleResult<()> {
        if self.matrix_id.is_empty()
            || self.coordinate_id.is_empty()
            || self.execution_profile_revision.is_empty()
            || self.resource_slot_id.is_empty()
            || self.actor.actor_id.is_empty()
            || self.idempotency_key.is_empty()
            || self.policy_revision.is_empty()
        {
            return Err(LifecycleError::InvalidRequest(
                "matrix, coordinate, execution profile, observed execution fingerprint, resource slot, actor, idempotency key, and policy revision are required"
                    .to_string(),
            ));
        }
        let observation_issues = self.observed_execution.validation_issues();
        if !observation_issues.is_empty() {
            return Err(LifecycleError::InvalidRequest(format!(
                "observed execution fingerprint is internally inconsistent: {}",
                observation_issues.join("; ")
            )));
        }
        if self.lease_seconds <= 0 {
            return Err(LifecycleError::InvalidRequest(
                "lease duration must be positive seconds".to_string(),
            ));
        }
        match (
            &self.implementation_source,
            &self.checked_implementation_source_snapshot,
        ) {
            (Some(source), Some(snapshot)) => {
                source.validate()?;
                snapshot
                    .validate()
                    .map_err(|error| LifecycleError::InvalidRequest(error.to_string()))?;
                if snapshot.snapshot_id != source.snapshot_id {
                    return Err(LifecycleError::InvalidRequest(format!(
                        "checked source snapshot {} does not match Attempt source {}",
                        snapshot.snapshot_id, source.snapshot_id
                    )));
                }
            }
            (Some(source), None) => {
                return Err(LifecycleError::InvalidRequest(format!(
                    "implementation source snapshot {} was not reopened through Registry Home before authorization",
                    source.snapshot_id
                )));
            }
            (None, Some(snapshot)) => {
                return Err(LifecycleError::InvalidRequest(format!(
                    "checked source snapshot {} was supplied without Attempt implementation source bytes",
                    snapshot.snapshot_id
                )));
            }
            (None, None) => {}
        }
        if self
            .coordinator_capability
            .as_deref()
            .is_some_and(|capability| capability.len() < 32)
        {
            return Err(LifecycleError::InvalidRequest(
                "coordinator capability must contain at least 32 opaque bytes".to_string(),
            ));
        }
        Ok(())
    }

    pub fn observed_execution_content_digest(&self) -> LifecycleResult<String> {
        self.observed_execution
            .environment_content_digest()
            .map(|digest| digest.trim_start_matches("sha256:").to_string())
            .map_err(LifecycleError::from)
    }

    fn coordinator_capability_digest(&self) -> Option<String> {
        self.coordinator_capability
            .as_deref()
            .map(|capability| sha256_hex(capability.as_bytes()))
    }

    pub fn request_digest(&self) -> LifecycleResult<String> {
        #[derive(Serialize)]
        struct Identity<'a> {
            matrix_id: &'a str,
            coordinate_id: &'a str,
            execution_profile_revision: &'a str,
            observed_execution_content_digest: String,
            resource_slot_id: &'a str,
            actor_kind: &'a str,
            actor_id: &'a str,
            owner_host: &'a str,
            owner_pid: i64,
            lease_seconds: i64,
            implementation_source: &'a Option<AttemptImplementationSourceV1>,
            coordinator_capability_digest: Option<String>,
            policy_revision: &'a str,
        }
        let identity = Identity {
            matrix_id: &self.matrix_id,
            coordinate_id: &self.coordinate_id,
            execution_profile_revision: &self.execution_profile_revision,
            observed_execution_content_digest: self.observed_execution_content_digest()?,
            resource_slot_id: &self.resource_slot_id,
            actor_kind: self.actor.kind.as_str(),
            actor_id: &self.actor.actor_id,
            owner_host: &self.actor.owner_host,
            owner_pid: self.actor.owner_pid,
            lease_seconds: self.lease_seconds,
            implementation_source: &self.implementation_source,
            coordinator_capability_digest: self.coordinator_capability_digest(),
            policy_revision: &self.policy_revision,
        };
        Ok(sha256_hex(&serde_json::to_vec(&identity)?))
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SpawnAuthorization {
    pub attempt_id: String,
    pub policy_revision: String,
    pub coordinator_lease_id: String,
    pub coordinator_epoch: i64,
    pub coordinate_lease_id: String,
    pub coordinate_epoch: i64,
    pub resource_slot_lease_id: String,
    pub resource_epoch: i64,
    pub lease_expires_at_ms: i64,
}

/// The durable result of a start operation. This goes in `operation_outcomes`;
/// it is safe to replay because it contains only public ids and epochs.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum StartOrResumeOutcome {
    Started { authorization: SpawnAuthorization },
    Blocked { reason: StartBlockReason },
}

/// Plain-language reasons a valid request may not start right now.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum StartBlockReason {
    /// SQLite could not begin the short authority transaction within its
    /// bounded wait. No registry decision was committed, so retrying the same
    /// idempotency key is safe and may still become the first application.
    RegistryBusy,
    CoordinatorHeld {
        coordinator_lease_id: String,
        expires_at_ms: i64,
    },
    CoordinateOwned {
        coordinate_lease_id: String,
        expires_at_ms: i64,
    },
    ResourceOccupied {
        resource_slot_id: String,
        resource_slot_lease_id: String,
        expires_at_ms: i64,
    },
    CoordinateSatisfied {
        run_hash: String,
    },
    LegacyAttemptActive {
        attempt_id: String,
    },
}

/// The first committed call is the only one that may spawn. A later replay
/// receives the same durable outcome but `may_spawn=false`, which prevents an
/// idempotency retry from becoming a second subprocess.
#[derive(Clone, PartialEq, Eq)]
pub struct StartOrResumeDecision {
    pub outcome: StartOrResumeOutcome,
    pub may_spawn: bool,
    /// Present only when this request had to create a coordinator. It is for
    /// the queue parent, not for the worker it is about to start.
    pub coordinator_capability: Option<String>,
    /// Present only in the first committed response. A replay sees the same
    /// durable outcome but not this worker-only secret, so it cannot create a
    /// second subprocess or forge its reports.
    pub worker_capability: Option<String>,
}

impl std::fmt::Debug for StartOrResumeDecision {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("StartOrResumeDecision")
            .field("outcome", &self.outcome)
            .field("may_spawn", &self.may_spawn)
            .field(
                "coordinator_capability",
                &self.coordinator_capability.as_ref().map(|_| "[redacted]"),
            )
            .field(
                "worker_capability",
                &self.worker_capability.as_ref().map(|_| "[redacted]"),
            )
            .finish()
    }
}

/// What a worker presents on progress, result, or failure. The capability
/// crosses the private launcher→worker handoff but never enters a receipt,
/// debug string, or operation outcome.
#[derive(Clone)]
pub struct FencedWorkerPermit {
    pub authorization: SpawnAuthorization,
    pub lease_capability: String,
    pub actor: LifecycleActor,
}

impl std::fmt::Debug for FencedWorkerPermit {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FencedWorkerPermit")
            .field("authorization", &self.authorization)
            .field("lease_capability", &"[redacted]")
            .field("actor", &self.actor)
            .finish()
    }
}

/// Decide that one finished Attempt is the candidate result for its Coordinate.
///
/// This is deliberately not the same operation as sealing files. The caller
/// names the exact sealed-manifest digest it inspected, why the selection is
/// allowed, and one idempotency key. A lost response can therefore replay the
/// same decision without producing a second evidence receipt.
pub struct AcceptSealedCandidateRequest {
    pub attempt_id: String,
    pub run_hash: String,
    pub expected_artifact_receipt_digest: String,
    pub selection_reason: String,
    pub actor: LifecycleActor,
    pub idempotency_key: String,
    pub policy_revision: String,
}

impl std::fmt::Debug for AcceptSealedCandidateRequest {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AcceptSealedCandidateRequest")
            .field("attempt_id", &self.attempt_id)
            .field("run_hash", &self.run_hash)
            .field(
                "expected_artifact_receipt_digest",
                &self.expected_artifact_receipt_digest,
            )
            .field("selection_reason", &self.selection_reason)
            .field("actor", &self.actor)
            .field("idempotency_key", &self.idempotency_key)
            .field("policy_revision", &self.policy_revision)
            .finish()
    }
}

/// The durable result of selecting one sealed candidate. A replay returns this
/// exact value and emits no new receipt.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AcceptSealedCandidateOutcome {
    pub coordinate_id: String,
    pub run_hash: String,
    pub artifact_snapshot_id: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AcceptSealedCandidateDecision {
    pub outcome: AcceptSealedCandidateOutcome,
}

// ── Pass 2: one application boundary for lifecycle-changing operations ────
//
// A CLI, TUI, agent, or API asks this one Rust surface to change lifecycle
// facts. `start_or_resume` atomically acquires coordinator, Coordinate, and
// resource authority; no token-only claim API exists beside it.

// ── Pass 3 database operations ───────────────────────────────────────────
//
// These functions are the concrete lifecycle boundaries. Callers invoke the
// operation they need directly instead of borrowing an unrelated connection
// through a facade that neither owns a transaction nor changes the operation.

/// Declare one resource slot, or revise its description while it is idle.
///
/// A slot description is deployment metadata, so adding a field such as the
/// Worker Site must not permanently brick a machine whose old row has no live
/// owner. The revision is an authority transition, not an in-place rewrite:
/// old leases remain in history and a before/after receipt records the change.
/// While any current grant is unexpired, the description remains immutable.
pub fn declare_resource_slot(registry: &Path, declaration: &ResourceSlotDeclaration) -> LifecycleResult<()> {
    #[derive(Deserialize)]
    struct Receipt {
        schema: String,
        resource_slot_id: String,
        status: String,
    }
    let request = serde_json::json!({"resource_slot_id":declaration.resource_slot_id,
        "execution_profile_revision":declaration.execution_profile_revision,
        "description_json":declaration.description_json,"actor":{"kind":declaration.actor.kind.as_str(),
        "actor_id":declaration.actor.actor_id,"owner_host":declaration.actor.owner_host,"owner_pid":declaration.actor.owner_pid},
        "policy_revision":declaration.policy_revision});
    let receipt: Receipt = crate::csharp_registry::run_json("declare-resource-slot", registry, &request)
        .map_err(LifecycleError::InvalidRequest)?;
    if receipt.schema != "fern.resource_slot_declaration.v1"
        || receipt.resource_slot_id != declaration.resource_slot_id
        || !matches!(receipt.status.as_str(), "declared" | "replayed" | "revised")
    {
        return Err(LifecycleError::InvalidRequest(
            "C# declare-resource-slot returned an invalid receipt".to_string(),
        ));
    }
    Ok(())
}
/// Register a grid of intended work. Redeclaring the same grid is a no-op.
///
/// THE KEY DELIBERATELY EXCLUDES THE LANE. A matrix is an EXPERIMENT, and the
/// lane is only the directory a machine happened to write into -- so keying on
/// the lane would give the Hard-Switch grid two matrices, one per machine, and
/// the very duplicate this exists to catch would sit in two separate grids
/// looking perfectly legitimate. Keyed on the preset and the coordinates
/// instead, both machines converge on ONE matrix and split its cells, which is
/// what splitting work across machines actually means.
///
/// Nothing is over-merged by this. Anything that makes two runs scientifically
/// different -- device, protocol, seed, bank -- is already inside the target
/// hash, so two grids that differ in any of them have different coordinates.
/// The implementation source snapshot is NOT a scientific coordinate; it
/// partitions execution Matrices because a Matrix has the stricter invariant
/// that all of its Attempts use one source snapshot. A repaired implementation
/// therefore keeps the same target hashes while opening a new Matrix and new
/// Attempts.
///
/// `lane` is kept as a field because it is worth knowing where a grid was first
/// declared; it just does not decide identity.
#[derive(Serialize)]
struct DeclareMatrixRequest<'a> {
    lane: &'a str,
    preset: Option<&'a str>,
    recipe_hash: Option<&'a str>,
    coordinates: Vec<DeclareMatrixCoordinateRequest<'a>>,
    declared_by: &'a str,
    note: Option<&'a str>,
}

#[derive(Serialize)]
struct DeclareMatrixCoordinateRequest<'a> {
    target_hash: &'a str,
    replica_ordinal: i64,
    coordinate_json: &'a str,
    replica_reason: Option<&'a str>,
    implementation_source_snapshot_id: Option<&'a str>,
}

#[derive(Deserialize)]
struct DeclareMatrixReceipt {
    schema: String,
    matrix_id: String,
}

/// Declare one complete Matrix through the C# Registry writer.
///
/// Callers prepare the production migration chain before crossing this
/// boundary. The child owns the entire declaration transaction, including the
/// Matrix row, every Coordinate, and its declaration receipt.
pub fn declare_matrix(
    registry: &Path,
    lane: &str,
    preset: Option<&str>,
    recipe_hash: Option<&str>,
    coordinates: &[DeclaredCoordinate],
    declared_by: &str,
    note: Option<&str>,
) -> LifecycleResult<String> {
    let request = DeclareMatrixRequest {
        lane,
        preset,
        recipe_hash,
        coordinates: coordinates
            .iter()
            .map(|coordinate| DeclareMatrixCoordinateRequest {
                target_hash: &coordinate.target_hash,
                replica_ordinal: coordinate.replica_ordinal,
                coordinate_json: &coordinate.coordinate_json,
                replica_reason: coordinate.replica_reason.as_deref(),
                implementation_source_snapshot_id: coordinate.matrix_implementation_source_snapshot_id.as_deref(),
            })
            .collect(),
        declared_by,
        note,
    };
    let receipt: DeclareMatrixReceipt = crate::csharp_registry::run_json("declare-matrix", registry, &request)
        .map_err(LifecycleError::InvalidRequest)?;
    if receipt.schema != "fern.declare_matrix.v1" || receipt.matrix_id.trim().is_empty() {
        return Err(LifecycleError::InvalidRequest(
            "C# declare-matrix returned an invalid receipt".to_string(),
        ));
    }
    Ok(receipt.matrix_id)
}

/// One Matrix's complete view of one cell, for `cell_provenance`.
#[derive(Debug, Clone, serde::Deserialize)]
pub struct CellDeclarationStory {
    pub matrix_id: String,
    pub lane: String,
    pub preset: Option<String>,
    pub recipe_hash: Option<String>,
    pub declared_at: String,
    pub declared_by: String,
    pub note: Option<String>,
    pub coordinate_id: String,
    pub replica_ordinal: i64,
    pub replica_reason: Option<String>,
    /// Grid context: how big this Matrix is and how much of it is accepted,
    /// so a one-cell probe reads differently from a 56-cell campaign grid.
    pub matrix_declared_cells: i64,
    pub matrix_accepted_cells: i64,
    pub implementation_hash: Option<String>,
    pub attempts: Vec<CellAttemptStory>,
    pub acceptance: Option<CellAcceptanceStory>,
    pub artifacts: Vec<CellArtifactStory>,
    pub plan_steps: Vec<CellPlanStepStory>,
}

#[derive(Debug, Clone, serde::Deserialize)]
pub struct CellAttemptStory {
    pub attempt_id: String,
    pub state: String,
    pub owner_host: String,
    pub owner_pid: i64,
    pub claimed_at: String,
    pub ended_at: Option<String>,
    pub outcome: Option<String>,
    pub detail: Option<String>,
}

#[derive(Debug, Clone, serde::Deserialize)]
pub struct CellAcceptanceStory {
    pub run_hash: String,
    pub attempt_id: String,
    pub accepted_at: String,
    pub accepted_by: String,
}

#[derive(Debug, Clone, serde::Deserialize)]
pub struct CellArtifactStory {
    pub artifact_dir: String,
    pub file_count: i64,
    pub run_hash: Option<String>,
}

#[derive(Debug, Clone, serde::Deserialize)]
pub struct CellPlanStepStory {
    pub plan_name: String,
    pub step_id: String,
    pub state: String,
    pub failure_detail: Option<String>,
}

#[derive(Debug, Clone, serde::Deserialize)]
pub struct CellReceiptStory {
    pub occurred_at_ms: i64,
    pub operation_name: String,
    pub object_kind: String,
    pub prior_state: Option<String>,
    pub next_state: Option<String>,
    pub actor_id: String,
    pub error_category: Option<String>,
}

/// Everything the registry knows about one scientific cell.
#[derive(Debug, Clone)]
pub struct CellProvenance {
    pub target_hash: String,
    /// Every Matrix declaring this cell, oldest declaration first.
    pub declarations: Vec<CellDeclarationStory>,
    /// Every transition receipt touching this cell's coordinates or attempts,
    /// oldest first: who changed what, and why it was allowed.
    pub receipts: Vec<CellReceiptStory>,
}

/// Everything Registry Home knows about one scientific cell, read through the
/// typed C# Matrix authority process.
#[derive(serde::Deserialize)]
struct CellProvenanceWire {
    schema: String,
    target_hash: String,
    declarations: Vec<CellDeclarationStory>,
    receipts: Vec<CellReceiptStory>,
}

fn validate_cell_provenance_wire(
    report: CellProvenanceWire,
    requested_target_hash: &str,
) -> Result<CellProvenance, String> {
    if report.schema != "fern.cell_provenance.v1" {
        return Err(format!("C# cell-provenance reader returned schema {:?}", report.schema));
    }
    if report.target_hash != requested_target_hash {
        return Err(format!(
            "C# cell-provenance reader returned target hash {:?} for requested target {:?}",
            report.target_hash, requested_target_hash
        ));
    }
    Ok(CellProvenance {
        target_hash: report.target_hash,
        declarations: report.declarations,
        receipts: report.receipts,
    })
}

pub fn cell_provenance(registry: &Path, target_hash: &str) -> Result<CellProvenance, String> {
    let report: CellProvenanceWire = crate::csharp_registry::run_json(
        "load-cell-provenance",
        registry,
        &serde_json::json!({ "target_hash": target_hash }),
    )?;
    validate_cell_provenance_wire(report, target_hash)
}

#[cfg(test)]
mod cell_provenance_wire_tests {
    use super::*;

    #[test]
    fn cell_provenance_refuses_a_wrong_target_child_response() {
        let error = validate_cell_provenance_wire(
            CellProvenanceWire {
                schema: "fern.cell_provenance.v1".to_string(),
                target_hash: "different-cell".to_string(),
                declarations: Vec::new(),
                receipts: Vec::new(),
            },
            "requested-cell",
        )
        .expect_err("a child response for another cell must not cross the typed boundary");
        assert!(error.contains("different-cell"), "{error}");
        assert!(error.contains("requested-cell"), "{error}");
    }
}

/// One sealed file as the registry recorded it, with no claim about whether
/// the bytes are still on this machine.
///
/// The presence check belongs to the caller because it is a fact about a
/// filesystem, not about the registry: the same row is present on the machine
/// that wrote it and absent everywhere else, and both readings are correct.
#[derive(Debug, Clone, PartialEq, Eq, serde::Deserialize)]
pub struct SealedArtifactFileV1 {
    pub snapshot_id: String,
    pub coordinate_id: String,
    pub attempt_id: String,
    pub run_hash: Option<String>,
    /// Recorded exactly as written: some rows are absolute and some are
    /// relative to the repository root. A caller resolving these must handle
    /// both rather than assuming one form.
    pub artifact_dir: String,
    pub relative_path: String,
    pub role: String,
    pub byte_count: i64,
    pub sha256: String,
    /// A missing file with this set is evidence loss.
    /// CHG (2026-08-19): `required_for_acceptance=false` is not permission to
    /// delete. Automatic GC still needs an explicit retention declaration.
    /// Absence of an optional sealed file is a presence fact, not a GC grant.
    pub required_for_acceptance: bool,
}

impl SealedArtifactFileV1 {
    /// Resolve the stored directory against the repository root.
    ///
    /// Some rows are absolute. Some are relative to the repository root. An
    /// absolute directory replaces the base; a relative one never joins
    /// against the process working directory.
    pub fn resolved_path(&self, repository_root: &Path) -> PathBuf {
        let stored = Path::new(&self.artifact_dir);
        if stored.is_absolute() {
            stored.join(&self.relative_path)
        } else {
            repository_root.join(stored).join(&self.relative_path)
        }
    }
}

/// Record a verified snapshot move through the C# Registry authority.
///
/// The caller owns moving and digest-verifying bytes. C# owns the one immediate
/// transaction that fences the old home, moves catalog facts, and appends the
/// durable transition receipt.
pub fn relocate_artifact_snapshot(
    registry: &Path,
    snapshot_id: &str,
    attempt_id: &str,
    actor: &LifecycleActor,
    old_dir: &str,
    new_dir: &str,
) -> LifecycleResult<()> {
    #[derive(Serialize)]
    struct Request<'a> {
        snapshot_id: &'a str,
        attempt_id: &'a str,
        actor_kind: &'a str,
        actor_id: &'a str,
        old_dir: &'a str,
        new_dir: &'a str,
    }
    #[derive(Deserialize)]
    struct Receipt {
        schema: String,
        snapshot_id: String,
        artifact_dir: String,
        transition_receipt_id: String,
    }

    let receipt: Receipt = crate::csharp_registry::run_json(
        "relocate-artifact-snapshot",
        registry,
        &Request {
            snapshot_id,
            attempt_id,
            actor_kind: actor.kind.as_str(),
            actor_id: &actor.actor_id,
            old_dir,
            new_dir,
        },
    )
    .map_err(LifecycleError::InvalidRequest)?;
    if receipt.schema != "fern.artifact_snapshot_relocation.v1"
        || receipt.snapshot_id != snapshot_id
        || receipt.artifact_dir != new_dir
        || receipt.transition_receipt_id.is_empty()
    {
        return Err(LifecycleError::InvalidRequest(
            "C# artifact relocation returned the wrong receipt identity".to_string(),
        ));
    }
    Ok(())
}

/// One pinned supervisor binary observed or reclaimed by the C# Registry.
#[derive(Debug, Clone, Deserialize)]
pub struct PinnedBinaryReclaimItem {
    pub path: String,
    pub bytes: i64,
    pub decision: String,
    pub reason: String,
}

/// Typed result of the C# Registry-owned pinned-binary reclamation boundary.
#[derive(Debug, Clone, Deserialize)]
pub struct PinnedBinaryReclaimReport {
    pub schema: String,
    pub applied: bool,
    pub removed_bytes: i64,
    pub items: Vec<PinnedBinaryReclaimItem>,
}

/// Observe or reclaim pinned supervisor binaries through the C# Registry.
///
/// The caller supplies the local host because a foreign Worker Site's pid must
/// never be compared with this machine's `/proc`. `apply=true` asks C# to
/// prove that no current Registry authority pointer exists before any unlink.
pub fn reclaim_pinned_binaries(
    registry: &Path,
    work_root: &Path,
    local_host: &str,
    apply: bool,
) -> LifecycleResult<PinnedBinaryReclaimReport> {
    #[derive(Serialize)]
    struct Request<'a> {
        work_root: &'a str,
        local_host: &'a str,
        apply: bool,
    }

    let report: PinnedBinaryReclaimReport = crate::csharp_registry::run_json(
        "reclaim-pinned-binaries",
        registry,
        &Request {
            work_root: &work_root.display().to_string(),
            local_host,
            apply,
        },
    )
    .map_err(LifecycleError::InvalidRequest)?;
    if report.schema != "fern.pinned_binary_reclaim.v1" || report.applied != apply || report.removed_bytes < 0 {
        return Err(LifecycleError::InvalidRequest(
            "C# pinned-binary reclaim returned the wrong receipt identity".to_string(),
        ));
    }
    for item in &report.items {
        if item.bytes < 0 || !matches!(item.decision.as_str(), "keep" | "removable") {
            return Err(LifecycleError::InvalidRequest(
                "C# pinned-binary reclaim returned an invalid item".to_string(),
            ));
        }
    }
    Ok(report)
}

/// A user's stop order, as durable authority every launch path must consult.
///
/// Why this exists (2026-08-14): the author ordered all local training
/// stopped while an idle-machine relauncher design existed whose only inputs
/// were "newest failed plan + idle card". Nothing durable said "the user
/// paused this machine", so nothing obliged any launch path to honor it.
/// While a pause is active, `require_scheduling_not_paused` refuses — before
/// any authority is claimed — naming who paused, why, and how to release.
/// A pause never touches running work: a live stage keeps its grants and
/// finishes; the pause stops the NEXT dispatch, exactly like the blocked-head
/// rule stops one lane. Cancel is a different, per-plan act.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SchedulingPause {
    pub pause_id: String,
    pub reason: String,
    pub paused_by: String,
    pub paused_at_ms: i64,
}

pub fn current_scheduling_pause(registry: &Path) -> LifecycleResult<Option<SchedulingPause>> {
    crate::csharp_registry::run_optional_json("inspect-scheduling-pause", registry, &serde_json::json!({}))
        .map_err(LifecycleError::InvalidRequest)
}

/// The launch gate. Called before any authority is claimed; the refusal
/// carries who, why, and the release route, so a refused launch is an
/// answer rather than a mystery.
pub fn require_scheduling_not_paused(registry: &Path) -> LifecycleResult<()> {
    match current_scheduling_pause(registry)? {
        None => Ok(()),
        Some(active) => Err(LifecycleError::InvalidRequest(format!(
            "scheduling is paused by {} ({}). No new work will be dispatched until \
             `fern-run release-scheduling-pause` records why the pause ends. Running stages finish; \
             this blocks only new dispatch. No GPU work started.",
            active.paused_by, active.reason
        ))),
    }
}

/// A full target hash from a unique prefix, or a refusal naming the choices.
/// Target hashes are long and humans paste prefixes; ambiguity must be an
/// error that lists candidates, never a silent first match. The C# Registry
/// owns this read so the Rust command does not keep a second Diesel projection.
pub fn resolve_cell_prefix(registry: &Path, prefix: &str) -> LifecycleResult<String> {
    resolve_cell_lookup("resolve-cell-prefix", registry, prefix)
}

/// The cell an accepted run belongs to, from a run-hash prefix. This is the
/// same read-only C# Registry boundary as `resolve_cell_prefix`; an unaccepted
/// run is deliberately refused before the provenance story is read.
pub fn resolve_run_hash_cell(registry: &Path, run_hash_prefix: &str) -> LifecycleResult<String> {
    resolve_cell_lookup("resolve-accepted-run-hash-cell", registry, run_hash_prefix)
}

fn resolve_cell_lookup(operation: &str, registry: &Path, prefix: &str) -> LifecycleResult<String> {
    crate::csharp_registry::run_json(operation, registry, &serde_json::json!({ "prefix": prefix }))
        .map_err(|error| LifecycleError::InvalidRequest(error.to_string()))
}

/// The coordinate id for one declared cell, or a refusal naming what is missing.
pub fn coordinate_id_of(
    registry: &Path,
    matrix_id: &str,
    target_hash: &str,
    replica_ordinal: i64,
) -> LifecycleResult<String> {
    #[derive(Deserialize)]
    struct CoordinateView {
        coordinate_id: String,
    }
    #[derive(Deserialize)]
    struct ExecutionView {
        schema: String,
        matrix_id: String,
        coordinates: Vec<CoordinateView>,
    }
    let view: ExecutionView = match crate::csharp_registry::run_json(
        "load-matrix-execution-view",
        registry,
        &serde_json::json!({
            "matrix_id": matrix_id,
            "coordinates": [{ "target_hash": target_hash, "replica_ordinal": replica_ordinal }],
        }),
    ) {
        Ok(view) => view,
        Err(error) if error.contains("undeclared Coordinate") || error.contains("is not declared on matrix") => {
            return refuse(LifecycleRefusal::CoordinateNotDeclared {
                matrix_id: matrix_id.to_string(),
                target_hash: target_hash.to_string(),
                replica_ordinal,
            });
        }
        Err(error) => return Err(LifecycleError::InvalidRequest(error)),
    };
    if view.schema != "fern.matrix_execution_view.v1" || view.matrix_id != matrix_id || view.coordinates.len() != 1 {
        return Err(LifecycleError::InvalidRequest(
            "C# Matrix execution view returned an invalid single-Coordinate receipt".to_string(),
        ));
    }
    Ok(view
        .coordinates
        .into_iter()
        .next()
        .expect("length checked")
        .coordinate_id)
}

/// Record a complete, file-level artifact manifest before accepting one attempt.
///
/// This does not decide that the attempt speaks for its coordinate; candidate
/// selection still makes that decision. Keeping the two operations separate means an
/// accidental duplicate can leave a signed forensic record without silently
/// becoming official evidence.
pub fn record_artifact_snapshot(
    registry: &Path,
    attempt_id: &str,
    run_hash: &str,
    artifact_dir: &str,
    manifest: &ArtifactManifest,
) -> LifecycleResult<String> {
    #[derive(Serialize)]
    struct FileWrite<'a> {
        relative_path: &'a str,
        role: &'a str,
        byte_count: i64,
        sha256: &'a str,
    }
    #[derive(Serialize)]
    struct Request<'a> {
        attempt_id: &'a str,
        run_hash: &'a str,
        artifact_dir: &'a str,
        files: Vec<FileWrite<'a>>,
        total_bytes: i64,
        receipt_digest: &'a str,
    }
    #[derive(Deserialize)]
    #[serde(deny_unknown_fields)]
    struct Receipt {
        schema: String,
        snapshot_id: String,
        replayed: bool,
    }

    let files = manifest
        .files
        .iter()
        .map(|file| FileWrite {
            relative_path: &file.relative_path,
            role: file.role.as_str(),
            byte_count: file.byte_count,
            sha256: &file.sha256,
        })
        .collect();
    let receipt: Receipt = crate::csharp_registry::run_json(
        "record-artifact-snapshot",
        registry,
        &Request {
            attempt_id,
            run_hash,
            artifact_dir,
            files,
            total_bytes: manifest.total_bytes,
            receipt_digest: &manifest.receipt_digest,
        },
    )
    .map_err(LifecycleError::InvalidRequest)?;
    if receipt.schema != "fern.artifact_snapshot_admission.v1"
        || receipt.snapshot_id.len() != 16
        || !receipt
            .snapshot_id
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return Err(LifecycleError::InvalidRequest(format!(
            "C# artifact snapshot receipt mismatched schema or identity: {:?}/{:?}",
            receipt.schema, receipt.snapshot_id
        )));
    }
    let _ = receipt.replayed;
    Ok(receipt.snapshot_id)
}

/// The file-level verdict for one accepted coordinate.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ArtifactVerification {
    pub matrix_id: String,
    pub coordinate_id: String,
    pub run_hash: String,
    pub state: ArtifactVerificationState,
    pub detail: String,
}

/// A small, user-visible vocabulary for an artifact verification result.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ArtifactVerificationState {
    Verified,
    MissingSnapshot,
    SnapshotForDifferentRun,
    IncompleteManifest,
    MissingFile,
    ChangedFile,
    UnreadableFile,
}

impl ArtifactVerificationState {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Verified => "verified",
            Self::MissingSnapshot => "missing_snapshot",
            Self::SnapshotForDifferentRun => "snapshot_for_different_run",
            Self::IncompleteManifest => "incomplete_manifest",
            Self::MissingFile => "missing_file",
            Self::ChangedFile => "changed_file",
            Self::UnreadableFile => "unreadable_file",
        }
    }
}

// ── Pass 6C: one authority-to-projection reconciliation decision ─────────
//
// The V3 registry and immutable Attempt commit are facts. `fern.db:runs` is a
// compatibility projection for existing analysis readers. This pure decision
// table prevents a future reconciler from letting the projection vote on what
// happened or from treating a worker-created file as authorized merely because
// it exists.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AuthorityResultState {
    Missing,
    Recorded,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AttemptCommitState {
    Missing,
    PreparedInStaging,
    Committed,
    Invalid,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LaneProjectionState {
    Missing,
    Pending,
    Running,
    Done,
    Failed,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct LaneReconciliationFacts {
    pub has_current_authority: bool,
    pub result: AuthorityResultState,
    pub artifact: AttemptCommitState,
    pub lane: LaneProjectionState,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LaneReconciliationDecision {
    /// V3 result + immutable commit and the compatibility row already agree.
    InSync,
    /// Idempotently copy the V3 result into the legacy lane projection.
    ProjectCommittedResult,
    /// Verify/promote the prepared Attempt before recording a V3 result.
    VerifyPreparedAttempt,
    /// A V3 result exists but no immutable artifact commit supports it.
    HoldResultUntilArtifactCommit,
    /// The legacy row says done without the two V3 authority facts.
    RefuseLaneOnlyCompletion,
    /// A stale/no-longer-authorized producer wrote output; isolate it.
    IsolateUnauthorizedOutput,
    /// No terminal fact exists and the compatibility projection must not move.
    AwaitAuthority,
    /// A malformed artifact state requires explicit repair, never projection.
    RefuseInvalidArtifact,
}

pub fn decide_lane_reconciliation(facts: LaneReconciliationFacts) -> LaneReconciliationDecision {
    use AttemptCommitState as Artifact;
    use AuthorityResultState as ResultState;
    use LaneProjectionState as Lane;
    use LaneReconciliationDecision as Decision;

    if facts.artifact == Artifact::Invalid {
        return Decision::RefuseInvalidArtifact;
    }
    if !facts.has_current_authority
        && facts.result == ResultState::Missing
        && facts.lane == Lane::Running
        && matches!(facts.artifact, Artifact::PreparedInStaging | Artifact::Committed)
    {
        return Decision::IsolateUnauthorizedOutput;
    }
    match (facts.result, facts.artifact, facts.lane) {
        (ResultState::Recorded, Artifact::Committed, Lane::Done) => Decision::InSync,
        (ResultState::Recorded, Artifact::Committed, _) => Decision::ProjectCommittedResult,
        (ResultState::Recorded, Artifact::PreparedInStaging, _) => Decision::VerifyPreparedAttempt,
        (ResultState::Missing, Artifact::PreparedInStaging, Lane::Done) => Decision::RefuseLaneOnlyCompletion,
        (ResultState::Missing, Artifact::PreparedInStaging, _) => Decision::VerifyPreparedAttempt,
        (ResultState::Recorded, Artifact::Missing, _) => Decision::HoldResultUntilArtifactCommit,
        (ResultState::Missing, Artifact::Missing, Lane::Done) => Decision::RefuseLaneOnlyCompletion,
        (ResultState::Missing, Artifact::Committed, Lane::Done) => Decision::RefuseLaneOnlyCompletion,
        (ResultState::Missing, Artifact::Committed, _) => Decision::IsolateUnauthorizedOutput,
        (ResultState::Missing, Artifact::Missing, _) => Decision::AwaitAuthority,
        (_, Artifact::Invalid, _) => Decision::RefuseInvalidArtifact,
    }
}

/// Re-hash every accepted attempt's signed files.  Any uncertainty is a failure.
pub fn verify_accepted_artifacts(registry: &Path) -> LifecycleResult<Vec<ArtifactVerification>> {
    verify_accepted_artifacts_in_scope(registry, None)
}

/// Re-hash accepted artifacts for exactly one Matrix.
pub fn verify_accepted_artifacts_for_matrix(
    registry: &Path,
    matrix_id: &str,
) -> LifecycleResult<Vec<ArtifactVerification>> {
    verify_accepted_artifacts_in_scope(registry, Some(matrix_id))
}

#[derive(Deserialize)]
struct AcceptedArtifactVerificationFile {
    relative_path: String,
    byte_count: i64,
    sha256: String,
    required_for_acceptance: bool,
}

#[derive(Deserialize)]
struct AcceptedArtifactVerificationInput {
    matrix_id: String,
    coordinate_id: String,
    attempt_id: String,
    run_hash: String,
    snapshot_id: Option<String>,
    snapshot_run_hash: Option<String>,
    artifact_dir: Option<String>,
    files: Vec<AcceptedArtifactVerificationFile>,
}

#[derive(Deserialize)]
struct AcceptedArtifactVerificationInputsReport {
    schema: String,
    matrix_id: Option<String>,
    inputs: Vec<AcceptedArtifactVerificationInput>,
}

fn verify_accepted_artifacts_in_scope(
    registry: &Path,
    matrix_filter: Option<&str>,
) -> LifecycleResult<Vec<ArtifactVerification>> {
    let report: AcceptedArtifactVerificationInputsReport = crate::csharp_registry::run_json(
        "load-accepted-artifact-verification-inputs",
        registry,
        &serde_json::json!({ "matrix_id": matrix_filter }),
    )
    .map_err(LifecycleError::InvalidRequest)?;
    if report.schema != "fern.accepted_artifact_verification_inputs.v1" || report.matrix_id.as_deref() != matrix_filter
    {
        return Err(LifecycleError::InvalidRequest(
            "C# accepted-artifact verification projection returned an invalid receipt".to_string(),
        ));
    }

    let mut verdicts = Vec::with_capacity(report.inputs.len());
    for input in report.inputs {
        let AcceptedArtifactVerificationInput {
            matrix_id,
            coordinate_id,
            attempt_id: _,
            run_hash,
            snapshot_id,
            snapshot_run_hash,
            artifact_dir,
            files,
        } = input;
        let Some(_snapshot_id) = snapshot_id else {
            verdicts.push(ArtifactVerification {
                matrix_id,
                coordinate_id,
                run_hash,
                state: ArtifactVerificationState::MissingSnapshot,
                detail: "accepted result has no signed artifact snapshot".to_string(),
            });
            continue;
        };
        if snapshot_run_hash.as_deref() != Some(run_hash.as_str()) {
            verdicts.push(ArtifactVerification {
                matrix_id,
                coordinate_id,
                run_hash,
                state: ArtifactVerificationState::SnapshotForDifferentRun,
                detail: "signed snapshot does not name this accepted run".to_string(),
            });
            continue;
        }
        if files.is_empty() || !files.iter().any(|file| file.required_for_acceptance) {
            verdicts.push(ArtifactVerification {
                matrix_id,
                coordinate_id,
                run_hash,
                state: ArtifactVerificationState::IncompleteManifest,
                detail: "snapshot has no file-level required receipts".to_string(),
            });
            continue;
        }
        let Some(artifact_dir) = artifact_dir else {
            return Err(LifecycleError::InvalidRequest(
                "C# accepted-artifact verification projection omitted a snapshot directory".to_string(),
            ));
        };
        let directory = std::path::Path::new(&artifact_dir);
        let mut failed = None;
        for file in files {
            let relative_path = file.relative_path;
            let expected_bytes = file.byte_count;
            let expected_sha256 = file.sha256;
            let relative = std::path::Path::new(&relative_path);
            let components = relative.components().collect::<Vec<_>>();
            if components.is_empty()
                || components
                    .iter()
                    .any(|component| !matches!(component, std::path::Component::Normal(_)))
            {
                failed = Some((
                    ArtifactVerificationState::IncompleteManifest,
                    format!("snapshot recorded unsafe relative path {relative_path:?}"),
                ));
                break;
            }
            let path = directory.join(relative);
            let mut current = directory.to_path_buf();
            let mut final_metadata = None;
            for (index, component) in components.iter().enumerate() {
                let std::path::Component::Normal(name) = component else {
                    unreachable!("components were validated above")
                };
                current.push(name);
                match std::fs::symlink_metadata(&current) {
                    Ok(metadata) if metadata.file_type().is_symlink() => {
                        failed = Some((
                            ArtifactVerificationState::UnreadableFile,
                            format!("signed path {} contains a symbolic link", current.display()),
                        ));
                        break;
                    }
                    Ok(metadata) if index + 1 < components.len() && !metadata.is_dir() => {
                        failed = Some((
                            ArtifactVerificationState::UnreadableFile,
                            format!("signed path parent {} is not a directory", current.display()),
                        ));
                        break;
                    }
                    Ok(metadata) if index + 1 == components.len() => final_metadata = Some(metadata),
                    Ok(_) => {}
                    Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                        failed = Some((
                            ArtifactVerificationState::MissingFile,
                            format!("signed file {} is absent", path.display()),
                        ));
                        break;
                    }
                    Err(error) => {
                        failed = Some((
                            ArtifactVerificationState::UnreadableFile,
                            format!("cannot inspect signed path {}: {error}", current.display()),
                        ));
                        break;
                    }
                }
            }
            if failed.is_some() {
                break;
            }
            let metadata = match final_metadata {
                Some(metadata) if metadata.is_file() => metadata,
                None => {
                    failed = Some((
                        ArtifactVerificationState::MissingFile,
                        format!("signed file {} is absent", path.display()),
                    ));
                    break;
                }
                Some(_) => {
                    failed = Some((
                        ArtifactVerificationState::UnreadableFile,
                        format!("signed path {} is not a regular file", path.display()),
                    ));
                    break;
                }
            };
            if metadata.len() != expected_bytes as u64 {
                failed = Some((
                    ArtifactVerificationState::ChangedFile,
                    format!("signed file {} changed size", path.display()),
                ));
                break;
            }
            match sha256_file(&path) {
                Ok(actual) if actual == expected_sha256 => {}
                Ok(_) => {
                    failed = Some((
                        ArtifactVerificationState::ChangedFile,
                        format!("signed file {} changed contents", path.display()),
                    ));
                    break;
                }
                Err(error) => {
                    failed = Some((
                        ArtifactVerificationState::UnreadableFile,
                        format!("cannot hash signed file {}: {error}", path.display()),
                    ));
                    break;
                }
            }
        }
        let (state, detail) = failed.unwrap_or((
            ArtifactVerificationState::Verified,
            "all signed files are present and unchanged".to_string(),
        ));
        verdicts.push(ArtifactVerification {
            matrix_id,
            coordinate_id,
            run_hash,
            state,
            detail,
        });
    }
    Ok(verdicts)
}

/// One coordinate a restarting controller may take, and why it is available.
/// How much of a declared matrix is settled, for a status line a human reads.
#[derive(Debug, Clone, PartialEq, Deserialize)]
pub struct MatrixProgress {
    pub declared: i64,
    pub accepted: i64,
    pub in_flight: i64,
    pub available: i64,
}

pub fn matrix_progress(registry: &Path, matrix_id: &str) -> LifecycleResult<MatrixProgress> {
    crate::csharp_registry::run_json(
        "matrix-progress",
        registry,
        &serde_json::json!({ "matrix_id": matrix_id }),
    )
    .map_err(LifecycleError::InvalidRequest)
}

pub fn rejected_attempt_details(registry: &Path, matrix_id: &str) -> LifecycleResult<Vec<Option<String>>> {
    crate::csharp_registry::run_json(
        "list-rejected-attempt-details",
        registry,
        &serde_json::json!({ "matrix_id": matrix_id }),
    )
    .map_err(LifecycleError::InvalidRequest)
}

/// Why the registry last refused to start work on this matrix, if it did.
///
/// A supervisor sees only its child's exit code, which cannot distinguish "the
/// worker crashed" from "another owner holds this machine and the registry
/// correctly said no". Those need opposite responses: the first is a retry, the
/// second is a wait. The registry already recorded the difference when it made
/// the decision, so read it from there rather than inventing an exit code.
///
/// `since_ms` bounds the answer to the child that just stopped; the outcome
/// table is append-only, so without it a block from days ago would be read as
/// the reason for today's exit.
pub fn latest_start_block_for_matrix(
    registry: &Path,
    matrix_id: &str,
    since_ms: i64,
) -> LifecycleResult<Option<String>> {
    crate::csharp_registry::run_optional_json(
        "load-latest-matrix-start-block",
        registry,
        &serde_json::json!({ "matrix_id": matrix_id, "since_ms": since_ms }),
    )
    .map_err(LifecycleError::InvalidRequest)
}

#[derive(Debug, Clone, PartialEq, Deserialize)]
pub struct DeclaredMatrixProgress {
    pub matrix_id: String,
    pub lane: String,
    pub preset: Option<String>,
    pub recipe_hash: Option<String>,
    pub declared: i64,
    pub accepted: i64,
    pub in_flight: i64,
    pub available: i64,
}

pub fn declared_matrix_progress(registry: &Path) -> LifecycleResult<Vec<DeclaredMatrixProgress>> {
    crate::csharp_registry::run_json("list-declared-matrix-progress", registry, &serde_json::json!({}))
        .map_err(LifecycleError::InvalidRequest)
}

/// Whether a currently authorized Attempt has produced accepted progress.
///
/// This is a monitor projection, not another durable lifecycle state.  In
/// particular, a process, log line, or stale heartbeat can never manufacture
/// `AuthorizedAndMakingProgress` without all three current grants agreeing.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum AuthorizedProgressStatus {
    AuthorizedNoAcceptedProgress,
    AuthorizedAndMakingProgress,
    AuthorizedButStalled,
}

/// One queue parent that currently owns a Matrix on one profile revision.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
pub struct CurrentCoordinatorProjection {
    pub matrix_id: String,
    pub execution_profile_revision: String,
    pub coordinator_lease_id: String,
    pub coordinator_epoch: i64,
    pub owner_actor: String,
    pub owner_host: String,
    pub owner_pid: i64,
    pub expires_at_ms: i64,
}

/// One currently authorized worker, with the three grants that permit it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, schemars::JsonSchema)]
pub struct CurrentAttemptProjection {
    pub matrix_id: String,
    pub coordinate_id: String,
    pub target_hash: String,
    pub attempt_id: String,
    pub execution_profile_revision: String,
    pub resource_slot_id: String,
    pub owner_actor: String,
    pub owner_host: String,
    pub owner_pid: i64,
    pub coordinator_lease_id: String,
    pub coordinator_epoch: i64,
    pub coordinate_lease_id: String,
    pub coordinate_epoch: i64,
    pub resource_slot_lease_id: String,
    pub resource_epoch: i64,
    /// Database time when this exact Attempt acquired its Coordinate grant.
    /// Cost guards use this stable start clock; heartbeat time answers only
    /// whether the current owner is still making progress.
    pub attempt_started_at_ms: i64,
    pub lease_expires_at_ms: i64,
    pub last_accepted_progress_at_ms: Option<i64>,
    pub latest_progress_json: Option<String>,
    pub progress_status: AuthorizedProgressStatus,
}

/// Read-only control-plane truth for monitors and TUI clients.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CurrentAuthoritySnapshot {
    pub observed_at_ms: i64,
    pub progress_fresh_within_ms: i64,
    pub coordinators: Vec<CurrentCoordinatorProjection>,
    pub attempts: Vec<CurrentAttemptProjection>,
    /// Attempts still marked `claimed` that hold no current grant.
    ///
    /// This is what a fenced-out worker looks like, and what a host restart
    /// leaves behind: the Attempt row survives while its leases do not. It is
    /// worth reporting -- `abandon-attempt` exists to resolve it -- but it is
    /// not a reason for a read-only status query to fail, which is what it used
    /// to be. Not being current IS the answer to "is this attempt current".
    pub attempts_without_a_current_grant: Vec<String>,
}

/// Read-only terminal fact for one exact Attempt.
///
/// Supervisors use this after a child exits to distinguish an authorized
/// scientific failure from abrupt process loss. It is not a second lifecycle
/// state: the values come directly from the append-only Attempt row.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AttemptProjection {
    pub attempt_id: String,
    pub coordinate_id: String,
    pub state: String,
    pub outcome: Option<String>,
    pub detail: Option<String>,
}

pub fn attempt_projection(registry: &Path, attempt_id: &str) -> LifecycleResult<Option<AttemptProjection>> {
    crate::csharp_registry::run_optional_json(
        "load-coordinate-attempt-projection",
        registry,
        &serde_json::json!({ "attempt_id": attempt_id }),
    )
    .map_err(LifecycleError::InvalidRequest)
}

/// Join the three current authorities with the rebuildable accepted-progress
/// projection.  Missing or expired grants are never reported as authorized.
/// Inconsistent *current* pointers fail closed so a monitor cannot turn a
/// registry fault into the reassuring word "idle".
pub fn current_authority_snapshot(
    registry: &Path,
    progress_fresh_within_ms: i64,
) -> LifecycleResult<CurrentAuthoritySnapshot> {
    crate::csharp_registry::run_json(
        "load-current-authority-snapshot",
        registry,
        &serde_json::json!({ "progress_fresh_within_ms": progress_fresh_within_ms }),
    )
    .map_err(LifecycleError::InvalidRequest)
}

/// The one lifecycle database for a whole campaign.
///
/// WHY NOT PER LANE. Ownership is campaign-wide and the runs table is not. The
/// duplicates that cost this project time were all CROSS-lane -- one grid split
/// across two machines, an eight-bank lane overlapping the four-bank lane it
/// extends -- and per-lane lifecycle tables cannot see any of them, because each
/// lane's database only ever holds its own half. Measured 2026-08-05: zero
/// target hashes are duplicated inside any single lane, and hundreds are
/// duplicated across them. A per-lane guard would have caught nothing.
pub fn campaign_lifecycle_db(campaign_root: &std::path::Path) -> std::path::PathBuf {
    campaign_root.join("lifecycle.db")
}

/// Which run already speaks for a coordinate, if one does.
pub fn accepted_run_for(registry: &Path, coordinate_id: &str) -> LifecycleResult<Option<String>> {
    crate::csharp_registry::run_optional_json(
        "load-accepted-run-for-coordinate",
        registry,
        &serde_json::json!({ "coordinate_id": coordinate_id }),
    )
    .map_err(LifecycleError::InvalidRequest)
}

/// Full sha256 of some bytes, hex. Lives here so the CLI needs no hashing crate
/// of its own -- a second hashing dependency is a second place a digest can be
/// computed slightly differently.
pub fn sha256_hex(bytes: &[u8]) -> String {
    let mut hasher = Sha256::new();
    hasher.update(bytes);
    hasher.finalize().iter().map(|b| format!("{b:02x}")).collect()
}

/// Now, in the same format every receipt in this crate uses.
pub fn now_rfc3339() -> String {
    now_text()
}

/// Turn `lane/run_hash` back into a directory, against a campaign root.
///
/// The export carries keys rather than paths because a path is a fact about one
/// machine: the same run lives somewhere else on a rented pod, so a table full
/// of paths stops being true the moment it moves. Resolution is Rust's job so
/// that `../../elsewhere` is refused rather than followed -- an analysis must
/// not be able to read outside the campaign it was given.
pub fn resolve_artifact_key(campaign_root: &std::path::Path, key: &str) -> Result<std::path::PathBuf, String> {
    // `lane/runs/<recipe>/<short id>` -- the lane, then the path INSIDE it. The
    // inner layout is derived from the experiment (recipe name, cell short id),
    // so it is the same on every machine; only the root differs, and the root is
    // what the caller supplies. A first version keyed on `lane/run_hash` and
    // resolved to a directory that does not exist anywhere: run_hash is not in
    // the path. It returned 200 keys and 0 real directories.
    let mut parts = key.split('/');
    let lane = parts.next().unwrap_or_default();
    let inside: Vec<&str> = parts.collect();
    if lane.is_empty() || inside.is_empty() {
        return Err(format!("artifact key {key:?} is not `lane/path-inside-the-lane`"));
    }
    for part in std::iter::once(lane).chain(inside.iter().copied()) {
        if part.is_empty() || part == ".." || part == "." || part.contains('\\') {
            return Err(format!(
                "artifact key {key:?} has a component that could leave the campaign \
                 root, so resolution refuses it"
            ));
        }
    }
    let mut path = campaign_root.join(lane);
    for part in inside {
        path.push(part);
    }
    Ok(path)
}

/// One exact file from one Matrix Coordinate's accepted, sealed Attempt.
///
/// This is the read boundary for derived analyses. It deliberately joins the
/// Matrix declaration, accepted candidate, sealed snapshot, and file receipt
/// before returning a path. A lane `done` row or a file that merely happens to
/// exist is never an accepted analysis input.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
pub struct AcceptedArtifactSource {
    pub matrix_id: String,
    pub recipe_hash: String,
    pub coordinate_id: String,
    pub target_hash: String,
    pub replica_ordinal: i64,
    pub coordinate_json: String,
    pub attempt_id: String,
    pub run_hash: String,
    pub snapshot_id: String,
    pub snapshot_receipt_digest: String,
    pub artifact_dir: String,
    pub relative_path: String,
    pub byte_count: i64,
    pub sha256: String,
}

/// Resolve one signed file for every accepted Coordinate in exactly one Matrix.
///
/// The returned digest is still rechecked by the byte consumer after opening
/// the file. Keeping that final check at the consumer closes the race between
/// this registry read and parsing the payload.
pub fn accepted_artifact_sources_for_matrix(
    registry: &Path,
    matrix_id: &str,
    relative_path: &str,
) -> LifecycleResult<Vec<AcceptedArtifactSource>> {
    #[derive(Deserialize)]
    struct Report {
        schema: String,
        matrix_id: String,
        relative_path: String,
        sources: Vec<AcceptedArtifactSource>,
    }
    let report: Report = crate::csharp_registry::run_json(
        "load-accepted-artifact-sources",
        registry,
        &serde_json::json!({ "matrix_id": matrix_id, "relative_path": relative_path }),
    )
    .map_err(LifecycleError::InvalidRequest)?;
    if report.schema != "fern.accepted_artifact_sources.v1"
        || report.matrix_id != matrix_id
        || report.relative_path != relative_path
    {
        return Err(LifecycleError::InvalidRequest(
            "C# accepted-artifact source projection returned an invalid receipt".to_string(),
        ));
    }
    Ok(report.sources)
}
