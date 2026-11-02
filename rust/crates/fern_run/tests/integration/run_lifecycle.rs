//! Remaining lifecycle tests that do not plant history with Diesel.
//!
//! The Diesel fixture body that used to share this file is preserved intact at
//! `retired/fern_run/run_lifecycle_diesel_fixture_tests_2026_08_28.rs`. Successor
//! and upstream-Matrix StartStep proofs now live on the C# PlanTerminal owner.

use std::path::{Path, PathBuf};

use fern_run::lifecycle::{
    ArtifactState, AttemptCommitState, AttemptState, AuthorityResultState, CoordinateState, EvidenceReleaseState,
    LaneProjectionState, LaneReconciliationDecision, LaneReconciliationFacts, LeaseState, MatrixState,
    SealedArtifactFileV1, decide_lane_reconciliation,
};
use fern_run::run_plan::{RunPlanDefinitionV1, RunPlanStepV1};

#[test]
fn pass_one_state_types_allow_only_named_forward_steps() {
    // This test is deliberately database-free. It pins the vocabulary before
    // any migration makes a raw status string look like an authority decision.
    assert_eq!(
        MatrixState::Registered.transition_to(MatrixState::Open).unwrap(),
        MatrixState::Open
    );
    let matrix_error = MatrixState::Registered
        .transition_to(MatrixState::Archived)
        .expect_err("a matrix cannot skip open and settled");
    assert_eq!(matrix_error.object_kind, "matrix");
    assert_eq!(matrix_error.from, "registered");
    assert_eq!(matrix_error.to, "archived");

    assert_eq!(
        CoordinateState::Declared
            .transition_to(CoordinateState::Satisfied)
            .unwrap(),
        CoordinateState::Satisfied
    );
    assert!(
        CoordinateState::Satisfied
            .transition_to(CoordinateState::Declared)
            .is_err(),
        "a Coordinate is never reopened; another proposed result belongs to a new Candidate"
    );

    assert!(AttemptState::Opened.is_active());
    assert!(AttemptState::Running.is_active());
    assert!(AttemptState::ResultRecorded.is_active());
    assert!(!AttemptState::Closed.is_active());
    assert!(!AttemptState::Failed.is_active());
    assert_eq!(
        AttemptState::Running
            .transition_to(AttemptState::ResultRecorded)
            .unwrap(),
        AttemptState::ResultRecorded
    );
    assert!(
        AttemptState::Failed.transition_to(AttemptState::Running).is_err(),
        "retry creates a new attempt; it does not revive the failed one"
    );

    assert_eq!(
        LeaseState::Active.transition_to(LeaseState::Expired).unwrap(),
        LeaseState::Expired
    );
    assert!(
        LeaseState::Expired.transition_to(LeaseState::Active).is_err(),
        "a replacement claim creates a new lease instead of reviving this one"
    );

    assert_eq!(
        ArtifactState::Sealed.transition_to(ArtifactState::Checked).unwrap(),
        ArtifactState::Checked
    );
    assert!(
        ArtifactState::Expected.transition_to(ArtifactState::Checked).is_err(),
        "an artifact must be written and sealed before it can be checked"
    );

    assert_eq!(
        EvidenceReleaseState::Accepted
            .transition_to(EvidenceReleaseState::Frozen)
            .unwrap(),
        EvidenceReleaseState::Frozen
    );
    assert!(
        EvidenceReleaseState::Frozen
            .transition_to(EvidenceReleaseState::Accepted)
            .is_err(),
        "a frozen release can be superseded or invalidated, never silently reopened"
    );
}

#[test]
fn pass_one_transition_tables_reject_every_undeclared_edge() {
    // A single example per object explains the rule above; this complete table
    // protects it. Adding a future state without teaching its transitions makes
    // at least one of these pairs fail instead of becoming silently allowed.
    macro_rules! assert_transition_table {
        (
            $object:literal;
            [$($state:expr),+ $(,)?];
            [$($from:expr => $to:expr),* $(,)?]
        ) => {{
            let states = [$($state),+];
            let allowed = [$(( $from, $to )),*];
            for &from in &states {
                for &to in &states {
                    let expected = allowed.iter().any(|(allowed_from, allowed_to)| {
                        *allowed_from == from && *allowed_to == to
                    });
                    assert_eq!(
                        from.transition_to(to).is_ok(),
                        expected,
                        "{}: {} -> {}",
                        $object,
                        from.as_str(),
                        to.as_str(),
                    );
                }
            }
        }};
    }

    assert_transition_table!(
        "matrix";
        [MatrixState::Registered, MatrixState::Open, MatrixState::Settled, MatrixState::Archived];
        [
            MatrixState::Registered => MatrixState::Open,
            MatrixState::Open => MatrixState::Settled,
            MatrixState::Settled => MatrixState::Archived,
        ]
    );
    assert_transition_table!(
        "coordinate";
        [
            CoordinateState::Declared,
            CoordinateState::Satisfied,
            CoordinateState::Retired,
        ];
        [
            CoordinateState::Declared => CoordinateState::Satisfied,
            CoordinateState::Declared => CoordinateState::Retired,
            CoordinateState::Satisfied => CoordinateState::Retired,
        ]
    );
    assert_transition_table!(
        "attempt";
        [
            AttemptState::Opened,
            AttemptState::Running,
            AttemptState::ResultRecorded,
            AttemptState::Closed,
            AttemptState::Failed,
            AttemptState::Lost,
            AttemptState::Expired,
        ];
        [
            AttemptState::Opened => AttemptState::Running,
            AttemptState::Opened => AttemptState::Failed,
            AttemptState::Opened => AttemptState::Lost,
            AttemptState::Opened => AttemptState::Expired,
            AttemptState::Running => AttemptState::ResultRecorded,
            AttemptState::Running => AttemptState::Failed,
            AttemptState::Running => AttemptState::Lost,
            AttemptState::Running => AttemptState::Expired,
            AttemptState::ResultRecorded => AttemptState::Closed,
        ]
    );
    assert_transition_table!(
        "lease";
        [LeaseState::Active, LeaseState::Revoked, LeaseState::Expired];
        [
            LeaseState::Active => LeaseState::Revoked,
            LeaseState::Active => LeaseState::Expired,
        ]
    );
    assert_transition_table!(
        "artifact";
        [
            ArtifactState::Expected,
            ArtifactState::Written,
            ArtifactState::Sealed,
            ArtifactState::Checked,
            ArtifactState::Rejected,
            ArtifactState::Invalidated,
            ArtifactState::Tombstoned,
        ];
        [
            ArtifactState::Expected => ArtifactState::Written,
            ArtifactState::Written => ArtifactState::Sealed,
            ArtifactState::Sealed => ArtifactState::Checked,
            ArtifactState::Sealed => ArtifactState::Rejected,
            ArtifactState::Checked => ArtifactState::Invalidated,
            ArtifactState::Checked => ArtifactState::Tombstoned,
            ArtifactState::Rejected => ArtifactState::Tombstoned,
            ArtifactState::Invalidated => ArtifactState::Tombstoned,
        ]
    );
    assert_transition_table!(
        "evidence release";
        [
            EvidenceReleaseState::Candidate,
            EvidenceReleaseState::Accepted,
            EvidenceReleaseState::Frozen,
            EvidenceReleaseState::Superseded,
            EvidenceReleaseState::Invalidated,
        ];
        [
            EvidenceReleaseState::Candidate => EvidenceReleaseState::Accepted,
            EvidenceReleaseState::Candidate => EvidenceReleaseState::Invalidated,
            EvidenceReleaseState::Accepted => EvidenceReleaseState::Frozen,
            EvidenceReleaseState::Accepted => EvidenceReleaseState::Superseded,
            EvidenceReleaseState::Accepted => EvidenceReleaseState::Invalidated,
            EvidenceReleaseState::Frozen => EvidenceReleaseState::Superseded,
            EvidenceReleaseState::Frozen => EvidenceReleaseState::Invalidated,
        ]
    );
}

#[test]
fn pass_six_c_v3_facts_control_the_lane_projection_decision_table() {
    use AttemptCommitState as Artifact;
    use AuthorityResultState as ResultState;
    use LaneProjectionState as Lane;
    use LaneReconciliationDecision as Decision;

    let decision = |has_current_authority, result, artifact, lane| {
        decide_lane_reconciliation(LaneReconciliationFacts {
            has_current_authority,
            result,
            artifact,
            lane,
        })
    };

    assert_eq!(
        decision(true, ResultState::Recorded, Artifact::Committed, Lane::Pending),
        Decision::ProjectCommittedResult,
        "a committed V3 result repairs the compatibility projection, never the reverse"
    );
    assert_eq!(
        decision(true, ResultState::Recorded, Artifact::Committed, Lane::Done),
        Decision::InSync
    );
    assert_eq!(
        decision(true, ResultState::Missing, Artifact::PreparedInStaging, Lane::Pending),
        Decision::VerifyPreparedAttempt,
        "sealed staging is a candidate to verify, not a completed result"
    );
    assert_eq!(
        decision(true, ResultState::Recorded, Artifact::Missing, Lane::Done),
        Decision::HoldResultUntilArtifactCommit,
        "neither a V3 result nor lane=done can replace the immutable artifact commit"
    );
    assert_eq!(
        decision(false, ResultState::Missing, Artifact::Committed, Lane::Running),
        Decision::IsolateUnauthorizedOutput,
        "an old worker's files do not regain authority"
    );
    assert_eq!(
        decision(false, ResultState::Missing, Artifact::Missing, Lane::Done),
        Decision::RefuseLaneOnlyCompletion,
        "lane DB is a projection and cannot declare completion by itself"
    );
    assert_eq!(
        decision(true, ResultState::Recorded, Artifact::Invalid, Lane::Done),
        Decision::RefuseInvalidArtifact
    );
}

fn test_run_plan(name: &str, models: &[&str]) -> RunPlanDefinitionV1 {
    RunPlanDefinitionV1 {
        schema: fern_run::run_plan::RUN_PLAN_SCHEMA.to_string(),
        plan_name: name.to_string(),
        implementation_source_snapshot_id: None,
        compiled_ablation: None,
        compiled_experiment: None,
        // A V1 Matrix plan: no Stage Plan set, and the one-source-authority rule
        // that governs Stage-driven plans does not reach it.
        stage_plan_set_digest: None,
        stage_plan_digest: None,
        training_worker_request_digest: None,
        implementation_id: None,
        implementation_binding_id: None,
        owner_storage_override_reason: None,
        dependencies: Vec::new(),
        steps: models
            .iter()
            .enumerate()
            .map(|(ordinal, model)| RunPlanStepV1 {
                step_id: format!("step-{ordinal}"),
                lane: "plan-test-lane".to_string(),
                preset: "plan-test-preset".to_string(),
                model: (*model).to_string(),
                limit: 1,
                keep_forecast_origins: false,
                recipe_snapshot: None,
                compiled_experiment: None,
            })
            .collect(),
    }
}

#[test]
fn v1_run_plan_json_does_not_gain_a_v2_field() {
    let definition = test_run_plan("v1-json-shape", &["fern"]);
    let json = serde_json::to_string(&definition).unwrap();
    assert_eq!(
        json,
        r#"{"schema":"fern.run_plan.v1","plan_name":"v1-json-shape","dependencies":[],"steps":[{"step_id":"step-0","lane":"plan-test-lane","preset":"plan-test-preset","model":"fern","limit":1,"keep_forecast_origins":false}]}"#,
    );
}

/// A Plan carrying only `stage_plan_set_digest` is Stage-driven.
///
/// `run-experiment` writes that field and leaves the one-cell
/// `stage_plan_digest` None, so a dispatcher that asks only about the one-cell
/// field routes every V2 Experiment into the Matrix-step preflight, which
/// refuses its empty `steps` as "run plan has no steps". The gate-5 Pendulum
/// canary sat registered and undispatchable for exactly that reason while its
/// monitor projection rendered all three Stages correctly. Both shapes, and
/// the V1 shape that is neither, are pinned here.
#[test]
fn a_stage_plan_set_digest_alone_makes_a_plan_stage_driven() {
    let mut definition = test_run_plan("stage-driven-shapes", &["fern"]);
    assert!(
        !definition.is_stage_driven(),
        "a V1 Matrix plan declares neither Stage digest"
    );

    definition.steps.clear();
    definition.stage_plan_set_digest = Some("set-digest".to_string());
    assert!(
        definition.is_stage_driven(),
        "the ordered instance collection every run-experiment registration writes"
    );

    definition.stage_plan_set_digest = None;
    definition.stage_plan_digest = Some("one-cell-digest".to_string());
    assert!(
        definition.is_stage_driven(),
        "the stored 9B one-cell form stays Stage-driven"
    );
}

#[test]
fn run_plan_definition_validation_names_the_bad_value_and_repair() {
    let mut wrong_schema = test_run_plan("validation-fixture", &["dlinear"]);
    wrong_schema.schema = "fern.run_plan.v0".to_string();
    let error = wrong_schema.validate().unwrap_err().to_string();
    assert!(error.contains("validation-fixture"));
    assert!(error.contains("fern.run_plan.v0"));
    assert!(error.contains("regenerate the definition with the current planner"));

    let mut incomplete_step = test_run_plan("validation-fixture", &["dlinear"]);
    incomplete_step.steps[0].lane.clear();
    incomplete_step.steps[0].model.clear();
    let error = incomplete_step.validate().unwrap_err().to_string();
    assert!(error.contains("step at ordinal 0 is missing lane, model"));
    assert!(error.contains("Add the missing field(s) before registration"));

    let mut empty_step = test_run_plan("validation-fixture", &["dlinear"]);
    empty_step.steps[0].limit = 0;
    let error = empty_step.validate().unwrap_err().to_string();
    assert!(error.contains("step \"step-0\" has limit 0"));
    assert!(error.contains("set a positive limit or remove the step"));
}

#[test]
fn sealed_artifact_path_joins_relative_dirs_to_the_repository_root() {
    let relative = SealedArtifactFileV1 {
        snapshot_id: "s".into(),
        coordinate_id: "c".into(),
        attempt_id: "a".into(),
        run_hash: None,
        artifact_dir: "results/lane/run".into(),
        relative_path: "point.json".into(),
        role: "point_forecast".into(),
        byte_count: 1,
        sha256: "a".repeat(64),
        required_for_acceptance: true,
    };
    assert_eq!(
        relative.resolved_path(Path::new("/repo")),
        PathBuf::from("/repo/results/lane/run/point.json")
    );
    let mut absolute = relative;
    absolute.artifact_dir = "/abs/run".into();
    assert_eq!(
        absolute.resolved_path(Path::new("/repo")),
        PathBuf::from("/abs/run/point.json")
    );
}
