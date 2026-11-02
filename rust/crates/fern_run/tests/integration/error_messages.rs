//! The operator-facing text of the registry errors, pinned.
//!
//! These three enums moved from a hand-written `Display` match to a
//! `thiserror` derive. Nothing asserted their wording before, so the
//! conversion could have silently reworded a message an operator reads at
//! 2 a.m. and nothing would have failed. The point of this file is not the
//! derive — it is that the text is now a fact a test holds, whichever way it
//! is produced.

use fern_experiment_types::{FernArchitectureVariantV1, ModelImplementationHash};
use fern_run::model_architecture::ResolvedModelImplementationError;

/// A real `ArchitectureCardError`, since its constructor is crate-private.
fn a_card_error() -> fern_experiment_types::ArchitectureCardError {
    FernArchitectureVariantV1::parse("no_such_variant").unwrap_err()
}

/// The same, for the implementation side.
fn an_implementation_card_error() -> fern_experiment_types::ImplementationCardError {
    ModelImplementationHash::parse("not-a-hash").unwrap_err()
}

#[test]
fn the_resolved_model_implementation_messages_are_unchanged() {
    let card = a_card_error();
    let expected = format!("cannot resolve model architecture: {card}");
    let wrapped: ResolvedModelImplementationError = card.into();
    assert_eq!(wrapped.to_string(), expected);

    let card = an_implementation_card_error();
    let expected = format!("cannot resolve model implementation: {card}");
    let wrapped: ResolvedModelImplementationError = card.into();
    assert_eq!(wrapped.to_string(), expected);
}

/// The lifecycle refusals and error wrappers, same discipline: the text an
/// operator reads at 2 a.m. is a fact a test holds, whichever way it is
/// produced. One pin per variant, exact string.
#[test]
fn the_lifecycle_messages_are_unchanged() {
    use fern_run::lifecycle::{LifecycleError, LifecycleRefusal};

    let pins: Vec<(LifecycleRefusal, &str)> = vec![
        (
            LifecycleRefusal::AttemptNotRunning {
                attempt_id: "a1".into(),
                state: "finished".into(),
            },
            "attempt a1 is finished, so it has nothing left to report",
        ),
        (
            LifecycleRefusal::CoordinateNotDeclared {
                matrix_id: "m1".into(),
                target_hash: "t1".into(),
                replica_ordinal: 2,
            },
            "matrix m1 declares no coordinate for target t1 replica 2. A repeat run is only acceptable when it was declared with a reason; otherwise it is an accidental duplicate.",
        ),
        (
            LifecycleRefusal::AlreadyAccepted {
                coordinate_id: "c1".into(),
                run_hash: "r1".into(),
                accepted_at: "now".into(),
            },
            "coordinate c1 already accepted run r1 at now. Declare a replica if a second run is intended.",
        ),
        (
            LifecycleRefusal::RunAcceptedElsewhere { run_hash: "r1".into() },
            "run r1 is already the accepted result of another coordinate",
        ),
        (
            LifecycleRefusal::SealedArtifactDigestMismatch {
                attempt_id: "a1".into(),
                run_hash: "r1".into(),
                expected_receipt_digest: "want".into(),
                actual_receipt_digest: "got".into(),
            },
            "attempt a1 run r1 sealed a different artifact manifest (got) than the acceptance request named (want)",
        ),
        (
            LifecycleRefusal::AttemptNotFinished {
                attempt_id: "a1".into(),
                state: "claimed".into(),
            },
            "attempt a1 is claimed; only a finished attempt has a result to accept",
        ),
        (
            LifecycleRefusal::IdempotencyKeyConflict {
                idempotency_key: "k1".into(),
            },
            "idempotency key k1 was already used for a different request; choose a new key instead of making its previous outcome ambiguous",
        ),
        (
            LifecycleRefusal::ResourceSlotNotDeclared {
                resource_slot_id: "s1".into(),
                execution_profile_revision: "p@1".into(),
            },
            "resource slot s1 is not declared for execution profile revision p@1; run profile preflight/registration before starting work",
        ),
        (
            LifecycleRefusal::AuthorityNoLongerCurrent {
                authority_kind: "coordinator",
                lease_id: "l1".into(),
                fencing_epoch: 3,
            },
            "coordinator lease l1 at epoch 3 is no longer current; stop this stale worker instead of reporting its result",
        ),
        (
            LifecycleRefusal::LeaseCapabilityRejected {
                attempt_id: "a1".into(),
            },
            "worker capability for attempt a1 is not the capability issued with its current lease",
        ),
        (
            LifecycleRefusal::CoordinatorCapabilityRejected {
                coordinator_lease_id: "l1".into(),
            },
            "queue capability is not the capability issued with current coordinator lease l1",
        ),
        (
            LifecycleRefusal::CoordinatorHasActiveResource {
                coordinator_lease_id: "l1".into(),
                resource_slot_id: "s1".into(),
            },
            "coordinator lease l1 still owns resource slot s1; wait for its worker result before releasing the queue",
        ),
        (
            LifecycleRefusal::ArtifactNotSealed {
                attempt_id: "a1".into(),
                run_hash: "r1".into(),
            },
            "attempt a1 cannot be selected for run r1 until its matching artifact manifest is sealed",
        ),
    ];
    for (refusal, expected) in pins {
        assert_eq!(refusal.to_string(), expected);
        // The transparent wrapper adds nothing around a refusal.
        assert_eq!(LifecycleError::from(refusal).to_string(), expected);
    }

    assert_eq!(
        LifecycleError::InvalidRequest("who did what".into()).to_string(),
        "invalid lifecycle request: who did what"
    );
    let serialization = LifecycleError::from(serde_json::from_str::<serde_json::Value>("{").unwrap_err());
    assert!(
        serialization
            .to_string()
            .starts_with("cannot serialize lifecycle receipt: "),
        "{serialization}"
    );
}
