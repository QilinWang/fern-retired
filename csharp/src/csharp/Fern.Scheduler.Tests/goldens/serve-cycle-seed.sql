INSERT INTO experiment_matrices (matrix_id, lane, coordinate_count, declared_at, declared_by)
VALUES ('matrix-golden', 'golden-lane', 1, 'now', 'golden');
INSERT INTO matrix_coordinates (coordinate_id, matrix_id, target_hash, coordinate_json, created_at)
VALUES ('coordinate-golden', 'matrix-golden', 'target-golden', '{}', 'now');
INSERT INTO coordinate_attempts
    (attempt_id, coordinate_id, state, owner_host, owner_pid, claimed_at, ended_at, outcome)
VALUES ('attempt-golden', 'coordinate-golden', 'finished', 'golden-host', 7, 'now', 'now', 'ok');
INSERT INTO artifact_snapshots
    (snapshot_id, coordinate_id, attempt_id, artifact_dir, file_count, total_bytes,
     receipt_digest, recorded_at, run_hash)
VALUES ('snapshot-golden', 'coordinate-golden', 'attempt-golden', '/artifact', 1, 1,
        'receipt-golden', 'now', 'run-golden');
INSERT INTO run_plans (
    plan_id, plan_name, definition_json, definition_digest,
    execution_profile_id, execution_profile_revision, created_by,
    created_at_ms, authority_version, updated_at_ms
) VALUES ('plan-golden', 'ready', '{"dependencies":[]}', 'definition', 'local', 'revision',
    'golden', 100, 0, 100);
INSERT INTO run_plan_steps (plan_id, step_ordinal, step_id, step_json, state)
VALUES ('plan-golden', 0, 'step-0', '{}', 'planned');
