CREATE TABLE "artifact_blob_locations" (
    location_id       TEXT PRIMARY KEY,
    sha256            TEXT NOT NULL REFERENCES artifact_blobs(sha256),
    storage_site_id   TEXT NOT NULL,
    path              TEXT NOT NULL,
    state             TEXT NOT NULL CHECK (state IN ('present', 'replaced', 'deleted', 'deleting')),
    first_recorded_at TEXT NOT NULL,
    last_verified_at  TEXT NOT NULL,
    removed_at        TEXT
);

CREATE TABLE artifact_blobs (
    sha256            TEXT PRIMARY KEY,
    byte_count        BIGINT NOT NULL CHECK (byte_count >= 0),
    first_recorded_at TEXT NOT NULL
);

CREATE TABLE artifact_gc_claims (
    gc_claim_id       TEXT PRIMARY KEY,
    location_id       TEXT NOT NULL REFERENCES artifact_blob_locations(location_id),
    storage_site_id   TEXT NOT NULL,
    capability_digest TEXT NOT NULL,
    state             TEXT NOT NULL CHECK (state IN ('prepared')),
    prepared_at       TEXT NOT NULL
);

CREATE TABLE artifact_holds (
    hold_id     TEXT PRIMARY KEY,
    sha256      TEXT NOT NULL REFERENCES artifact_blobs(sha256),
    hold_kind   TEXT NOT NULL,
    authority   TEXT NOT NULL,
    reason      TEXT NOT NULL,
    recorded_at TEXT NOT NULL,
    released_at TEXT
, capability_digest TEXT);

CREATE TABLE artifact_references (
    reference_id                TEXT PRIMARY KEY,
    sha256                      TEXT NOT NULL REFERENCES artifact_blobs(sha256),
    owner_kind                  TEXT NOT NULL,
    owner_id                    TEXT NOT NULL,
    purpose                     TEXT NOT NULL,
    materialization_requirement TEXT NOT NULL
        CHECK (materialization_requirement IN ('identity_only', 'bytes_required')),
    recorded_at                 TEXT NOT NULL,
    released_at                 TEXT,
    UNIQUE (owner_kind, owner_id, sha256, purpose)
);

CREATE TABLE artifact_snapshot_files (
    snapshot_id             TEXT NOT NULL REFERENCES artifact_snapshots(snapshot_id),
    relative_path           TEXT NOT NULL,
    role                    TEXT NOT NULL,
    byte_count              BIGINT NOT NULL,
    sha256                  TEXT NOT NULL,
    required_for_acceptance INTEGER NOT NULL CHECK (required_for_acceptance IN (0, 1)),
    PRIMARY KEY (snapshot_id, relative_path)
);

-- CHG 2026-09-11 night (slice 11 of the cell port): a sealed artifact
-- directory belongs to one attempt of one cell of a registered study, the
-- one-store rows; the Matrix coordinate and its attempt table are gone.
CREATE TABLE artifact_snapshots (
    snapshot_id    TEXT PRIMARY KEY,
    cell_id        INTEGER NOT NULL REFERENCES cells(id),
    attempt_id     INTEGER NOT NULL REFERENCES attempts(id),
    artifact_dir   TEXT NOT NULL,
    file_count     BIGINT NOT NULL,
    total_bytes    BIGINT NOT NULL,
    receipt_digest TEXT NOT NULL,
    recorded_at    TEXT NOT NULL
, run_hash TEXT);

CREATE TABLE device_grants (
    grant_id          TEXT PRIMARY KEY,
    device_id         TEXT NOT NULL,
    grant_generation  BIGINT NOT NULL CHECK (grant_generation > 0),
    owner_kind        TEXT NOT NULL CHECK (owner_kind IN ('cell', 'environment_install')),
    owner_attempt_id  TEXT NOT NULL,
    site_id           TEXT NOT NULL,
    granted_at_ms     BIGINT NOT NULL,
    released_at_ms    BIGINT,
    release_reason    TEXT,
    UNIQUE (device_id, grant_generation),
    CHECK (length(grant_id) > 0),
    CHECK (length(device_id) > 0),
    CHECK (length(owner_attempt_id) > 0),
    CHECK (length(site_id) > 0),
    CHECK (released_at_ms IS NULL OR release_reason IS NOT NULL)
);

CREATE TABLE environment_install_lease_roots (
    machine_generation TEXT NOT NULL,
    mutation_root      TEXT NOT NULL,
    lease_id           TEXT NOT NULL REFERENCES environment_install_leases(lease_id),
    PRIMARY KEY (machine_generation, mutation_root),
    CHECK (length(machine_generation) > 0),
    CHECK (length(mutation_root) > 0)
);

CREATE TABLE environment_install_leases (
    lease_id           TEXT PRIMARY KEY,
    machine_generation TEXT NOT NULL,
    interpreter        TEXT NOT NULL,
    site_id            TEXT NOT NULL,
    mutation_roots_json TEXT NOT NULL,
    fencing_token      INTEGER NOT NULL,
    owner_attempt_id   TEXT NOT NULL,
    owner_actor        TEXT NOT NULL,
    issued_at_ms       BIGINT NOT NULL,
    deadline_ms        BIGINT NOT NULL,
    released_at_ms     BIGINT,
    release_reason     TEXT,
    CHECK (length(lease_id) > 0),
    CHECK (length(machine_generation) > 0),
    CHECK (length(interpreter) > 0),
    CHECK (length(site_id) > 0),
    CHECK (length(mutation_roots_json) > 0),
    CHECK (fencing_token > 0),
    CHECK (length(owner_attempt_id) > 0),
    CHECK (length(owner_actor) > 0),
    CHECK (deadline_ms > issued_at_ms)
);

-- CHG 2026-09-26 (schema 19, docs/programs/2026-09-26-cards-without-hashes;
-- owner: "get rid of 256 entirely"): an outcome keeps the request it answered
-- as text, not a SHA-256 of it, so a key sent again with other arguments is
-- refused with both requests in hand. Rows written before schema 19 hold no
-- text (only their digest was kept, and it cannot be turned back into one), so
-- request_text is NULL for them and a replay of their key is refused by name.
-- CHG 2026-09-26 (owner, on those rows: "like useless"): step 18 does not carry them
-- (they stay in the copy beside the Registry), so every row has its text: NOT NULL.
CREATE TABLE "operation_outcomes" (
    operation_scope            TEXT NOT NULL,
    actor_id                   TEXT NOT NULL,
    idempotency_key            TEXT NOT NULL,
    operation_name             TEXT NOT NULL,
    request_text               TEXT NOT NULL,
    outcome_kind               TEXT NOT NULL CHECK (outcome_kind IN (
        'started', 'already_running', 'blocked', 'refused', 'renewed',
        'completed', 'cancelled', 'already_inactive'
    )),
    result_json                TEXT NOT NULL,
    error_category             TEXT,
    transition_receipt_id      TEXT REFERENCES transition_receipts(transition_receipt_id),
    created_at_ms              BIGINT NOT NULL,
    PRIMARY KEY (operation_scope, actor_id, idempotency_key)
);

CREATE TABLE preparation_operations (
    operation_id TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL CHECK (revision > 0),
    state TEXT NOT NULL CHECK (state IN ('Running','Succeeded','Refused','Unknown','Cancelled')),
    snapshot_json TEXT NOT NULL CHECK (json_valid(snapshot_json) AND length(CAST(snapshot_json AS BLOB)) <= 1048576),
    started_at_ms INTEGER NOT NULL,
    observed_at_ms INTEGER NOT NULL CHECK (observed_at_ms >= started_at_ms),
    finished_at_ms INTEGER,
    CHECK ((state = 'Running' AND finished_at_ms IS NULL) OR (state <> 'Running' AND finished_at_ms = observed_at_ms))
);

CREATE TRIGGER preparation_operations_guard_update
BEFORE UPDATE ON preparation_operations
WHEN NEW.operation_id <> OLD.operation_id OR NEW.started_at_ms <> OLD.started_at_ms
    OR NEW.revision <> OLD.revision + 1 OR NEW.observed_at_ms < OLD.observed_at_ms
    OR (OLD.state <> 'Running' AND (NEW.state <> OLD.state OR NEW.snapshot_json <> OLD.snapshot_json
        OR NEW.observed_at_ms <> OLD.observed_at_ms OR NEW.finished_at_ms IS NOT OLD.finished_at_ms))
BEGIN SELECT RAISE(ABORT, 'preparation operation identity or terminal snapshot is immutable'); END;

CREATE TABLE "provider_stop_requests" (
    provider_stop_request_id              TEXT PRIMARY KEY,
    provider_kind                         TEXT NOT NULL CHECK (provider_kind IN ('runpod')),
    provider_pod_id                       TEXT NOT NULL,
    site_id                               TEXT NOT NULL,
    site_profile_revision                 TEXT NOT NULL,
    desired_status                        TEXT NOT NULL CHECK (desired_status IN ('EXITED')),
    actor_kind                            TEXT NOT NULL,
    actor_id                              TEXT NOT NULL,
    idempotency_key                       TEXT NOT NULL,
    deadline_duration_ms                  BIGINT NOT NULL,
    deadline_at_ms                        BIGINT NOT NULL,
    state                                 TEXT NOT NULL CHECK (state IN ('prepared', 'network_unknown', 'expired_without_submission', 'terminal')),
    network_unknown_detail                TEXT,
    network_unknown_at_ms                 BIGINT,
    expired_without_submission_detail     TEXT,
    expired_without_submission_at_ms      BIGINT,
    terminal_receipt_json                 TEXT,
    terminal_received_at_ms               BIGINT,
    created_at_ms                         BIGINT NOT NULL,
    updated_at_ms                         BIGINT NOT NULL,
    UNIQUE (provider_kind, actor_id, idempotency_key),
    CHECK (deadline_duration_ms > 0),
    CHECK (deadline_at_ms = created_at_ms + deadline_duration_ms),
    CHECK (
        (state = 'prepared'
            AND network_unknown_detail IS NULL AND network_unknown_at_ms IS NULL
            AND expired_without_submission_detail IS NULL AND expired_without_submission_at_ms IS NULL
            AND terminal_receipt_json IS NULL AND terminal_received_at_ms IS NULL)
        OR
        (state = 'network_unknown'
            AND network_unknown_detail IS NOT NULL AND network_unknown_at_ms IS NOT NULL
            AND expired_without_submission_detail IS NULL AND expired_without_submission_at_ms IS NULL
            AND terminal_receipt_json IS NULL AND terminal_received_at_ms IS NULL)
        OR
        (state = 'expired_without_submission'
            AND network_unknown_detail IS NULL AND network_unknown_at_ms IS NULL
            AND expired_without_submission_detail IS NOT NULL AND expired_without_submission_at_ms IS NOT NULL
            AND terminal_receipt_json IS NULL AND terminal_received_at_ms IS NULL)
        OR
        (state = 'terminal'
            AND expired_without_submission_detail IS NULL AND expired_without_submission_at_ms IS NULL
            AND terminal_receipt_json IS NOT NULL AND terminal_received_at_ms IS NOT NULL)
    )
);

CREATE TABLE registry_observation (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    identity TEXT NOT NULL CHECK (length(identity) = 32),
    revision INTEGER NOT NULL CHECK (typeof(revision) = 'integer' AND revision >= 0)
);

CREATE TABLE registry_schema (singleton INTEGER PRIMARY KEY CHECK (singleton = 1), schema_identity TEXT NOT NULL);

CREATE TABLE resource_slots (
    resource_slot_id           TEXT PRIMARY KEY,
    execution_profile_revision TEXT NOT NULL,
    description_json           TEXT NOT NULL,
    declared_by                TEXT NOT NULL,
    declared_at_ms             BIGINT NOT NULL,
    current_lease_id           TEXT,
    authority_version          BIGINT NOT NULL DEFAULT 0,
    updated_at_ms              BIGINT NOT NULL
);

-- CHG 2026-09-26 (schema 19, docs/programs/2026-09-26-cards-without-hashes):
-- the hash chain is gone. previous_receipt_hash and receipt_hash made an edited
-- or reordered receipt detectable; object_sequence, unique per object, and the
-- append-only triggers below keep the order, and the id is written out as
-- <object_kind>:<object_id>:<object_sequence>.
CREATE TABLE transition_receipts (
    transition_receipt_id      TEXT PRIMARY KEY,
    operation_name             TEXT NOT NULL,
    object_kind                TEXT NOT NULL,
    object_id                  TEXT NOT NULL,
    object_sequence            BIGINT NOT NULL CHECK (object_sequence > 0),
    prior_state                TEXT,
    next_state                 TEXT,
    actor_kind                 TEXT NOT NULL,
    actor_id                   TEXT NOT NULL,
    attempt_id                 TEXT,
    coordinator_lease_id       TEXT,
    coordinate_lease_id        TEXT,
    resource_slot_lease_id     TEXT,
    fencing_epoch              BIGINT,
    input_identity_json        TEXT NOT NULL,
    output_identity_json       TEXT NOT NULL,
    policy_revision            TEXT NOT NULL,
    correlation_id             TEXT NOT NULL,
    causation_receipt_id       TEXT,
    error_category             TEXT,
    occurred_at_ms             BIGINT NOT NULL,
    UNIQUE (object_kind, object_id, object_sequence)
);

CREATE INDEX device_grants_live
    ON device_grants (device_id)
    WHERE released_at_ms IS NULL;

CREATE INDEX environment_install_leases_live
    ON environment_install_leases (machine_generation, interpreter)
    WHERE released_at_ms IS NULL;

CREATE INDEX idx_artifact_blob_locations_digest
    ON artifact_blob_locations(sha256, state);

CREATE UNIQUE INDEX idx_artifact_blob_locations_one_present_site_path
    ON artifact_blob_locations(storage_site_id, path)
    WHERE state = 'present';

CREATE UNIQUE INDEX idx_artifact_gc_claims_one_prepared_location
    ON artifact_gc_claims(location_id)
    WHERE state = 'prepared';

CREATE INDEX idx_artifact_holds_live
    ON artifact_holds(sha256)
    WHERE released_at IS NULL;

CREATE INDEX idx_artifact_references_live_materialization
    ON artifact_references(sha256, materialization_requirement)
    WHERE released_at IS NULL;

CREATE INDEX idx_artifact_snapshot_files_snapshot
    ON artifact_snapshot_files (snapshot_id);

CREATE INDEX idx_artifact_snapshots_cell
    ON artifact_snapshots (cell_id);

CREATE INDEX idx_transition_receipts_attempt
    ON transition_receipts (attempt_id, occurred_at_ms);

CREATE INDEX idx_transition_receipts_object
    ON transition_receipts (object_kind, object_id, occurred_at_ms);

CREATE UNIQUE INDEX one_artifact_snapshot_per_attempt
    ON artifact_snapshots (attempt_id);

CREATE UNIQUE INDEX provider_stop_requests_one_active_pod
ON provider_stop_requests (provider_kind, provider_pod_id)
WHERE state IN ('prepared', 'network_unknown');

CREATE INDEX provider_stop_requests_reconcile
ON provider_stop_requests (provider_kind, actor_id, idempotency_key);

CREATE TRIGGER operation_outcomes_no_delete
BEFORE DELETE ON operation_outcomes BEGIN
    SELECT RAISE(ABORT, 'operation outcomes are append-only');
END;

CREATE TRIGGER operation_outcomes_no_update
BEFORE UPDATE ON operation_outcomes BEGIN
    SELECT RAISE(ABORT, 'operation outcomes are append-only');
END;

CREATE TRIGGER provider_stop_requests_identity_immutable
BEFORE UPDATE OF
    provider_kind, provider_pod_id, site_id, site_profile_revision,
    desired_status, actor_kind, actor_id, idempotency_key,
    deadline_duration_ms, deadline_at_ms
ON provider_stop_requests
BEGIN
    SELECT RAISE(ABORT, 'provider stop request identity is immutable');
END;

CREATE TRIGGER provider_stop_requests_no_delete
BEFORE DELETE ON provider_stop_requests
BEGIN
    SELECT RAISE(ABORT, 'provider stop requests are durable authority facts');
END;

CREATE TRIGGER provider_stop_requests_no_submission_expiry_immutable
BEFORE UPDATE OF expired_without_submission_detail, expired_without_submission_at_ms
ON provider_stop_requests
WHEN OLD.expired_without_submission_detail IS NOT NULL
 AND (NEW.expired_without_submission_detail IS NOT OLD.expired_without_submission_detail
      OR NEW.expired_without_submission_at_ms IS NOT OLD.expired_without_submission_at_ms)
BEGIN
    SELECT RAISE(ABORT, 'provider stop no-submission expiry receipt is append-only');
END;

CREATE TRIGGER provider_stop_requests_terminal_receipt_immutable
BEFORE UPDATE OF terminal_receipt_json, terminal_received_at_ms
ON provider_stop_requests
WHEN OLD.terminal_receipt_json IS NOT NULL
 AND (NEW.terminal_receipt_json IS NOT OLD.terminal_receipt_json
      OR NEW.terminal_received_at_ms IS NOT OLD.terminal_received_at_ms)
BEGIN
    SELECT RAISE(ABORT, 'provider stop terminal receipt is append-only');
END;

CREATE TRIGGER registry_observation_keep_identity
BEFORE UPDATE OF identity, singleton ON registry_observation
BEGIN SELECT RAISE(ABORT, 'Registry observation identity is immutable'); END;

CREATE TRIGGER registry_observation_keep_row
BEFORE DELETE ON registry_observation
BEGIN SELECT RAISE(ABORT, 'Registry observation coordinate cannot be deleted'); END;

CREATE TRIGGER registry_schema_no_delete BEFORE DELETE ON registry_schema BEGIN SELECT RAISE(ABORT, 'Registry schema identity is immutable'); END;

CREATE TRIGGER registry_schema_no_update BEFORE UPDATE ON registry_schema BEGIN SELECT RAISE(ABORT, 'Registry schema identity is immutable'); END;

CREATE TRIGGER transition_receipts_no_delete
BEFORE DELETE ON transition_receipts BEGIN
    SELECT RAISE(ABORT, 'transition receipts are append-only');
END;

CREATE TRIGGER transition_receipts_no_update
BEFORE UPDATE ON transition_receipts BEGIN
    SELECT RAISE(ABORT, 'transition receipts are append-only');
END;

INSERT INTO registry_observation VALUES (1, lower(hex(randomblob(16))), 0);

-- ─────────────────────────────────────────────────────────────────────────────
-- The one-store schema (owner, 2026-09-11: "one transaction registry and one
-- science db, only; other retire"; and the word Plan goes).
--
-- There is no Plan. The registered object is the STUDY: the compiled study and
-- its cells, plus what registering adds -- the frozen source, the requested
-- state, the supervisor lease.
--
-- Six tables and nothing else. The science database is Python's and unchanged.
-- Added BESIDE the old tables in slice 1: nothing here replaces or reads them,
-- and a new Home is a new file, so old Homes stay readable by the binaries that
-- wrote them.
--
-- What the shape encodes, and why each choice is a choice:
--   * No hash is a key or a join. `payload_sha256` and `bundle_sha256` verify
--     BYTES and nothing else (docs/reference/textbook/data-is-the-identity.md); a cell is
--     found by (study_id, ordinal) and a study by (name, revision).
--   * The accepted result is a COLUMN on the cell, not a table: a cell has at
--     most one accepted attempt, and a table would let it have two.
--   * The study's supervisor lease and epoch are two COLUMNS, not a table, for
--     the same reason: one supervisor at a time is a property of the study.
--   * Every phase receipt, progress observation and output disposition is a row
--     in `events`. There is no second ledger to disagree with the first.
--   * Fencing lives in the WRITE: an `UPDATE attempts ... WHERE id = ? AND
--     fencing_epoch = ?` that touches zero rows IS the refusal. No read-then-act.
-- ─────────────────────────────────────────────────────────────────────────────

CREATE TABLE sites (
    id                INTEGER PRIMARY KEY,
    name              TEXT NOT NULL UNIQUE,
    capabilities_json TEXT NOT NULL,
    capacity_json     TEXT NOT NULL,
    created_at        TEXT NOT NULL
);

-- CHG 2026-09-26 (docs/programs/2026-09-26-cards-without-hashes; owner: "registry
-- should not record those too ... we delete it anyways"): bundle_sha256, a SHA-256
-- over the declared files whose prefix said how the source was registered, became
-- registered_from at schema 21, which says it in words: commit, working-tree, or
-- unknown for a source registered under an older scheme. Declared files carry no digest.
-- The column is written the way SQLite writes it after ALTER TABLE ADD COLUMN, after
-- created_at and on its own line: step 20 adds it and drops bundle_sha256 in place,
-- because renaming the table would rewrite the reference studies holds to it, and a
-- walked Home must hold this exact definition.
-- CHG 2026-09-27: a Home's tables are compared by their columns as PRAGMA table_info
-- reports them, not by this text, so the column is written as any other; it is still
-- the last column, where ADD COLUMN puts it.
CREATE TABLE sources (
    id                  INTEGER PRIMARY KEY,
    commit_sha          TEXT NOT NULL,
    declared_files_json TEXT NOT NULL,
    created_at          TEXT NOT NULL,
    registered_from     TEXT NOT NULL DEFAULT 'unknown' CHECK (registered_from IN ('commit', 'working-tree', 'unknown'))
);

CREATE TABLE studies (
    id                  INTEGER PRIMARY KEY,
    name                TEXT NOT NULL,
    revision            INTEGER NOT NULL,
    compiled_study_json TEXT NOT NULL,
    source_id           INTEGER NOT NULL REFERENCES sources(id),
    requested_state     TEXT NOT NULL CHECK (requested_state IN ('run', 'pause', 'cancel')),
    supervisor_lease    TEXT,
    supervisor_epoch    INTEGER NOT NULL DEFAULT 0,
    created_at          TEXT NOT NULL,
    UNIQUE (name, revision)
);

CREATE TABLE cells (
    id                  INTEGER PRIMARY KEY,
    study_id            INTEGER NOT NULL REFERENCES studies(id),
    ordinal             INTEGER NOT NULL,
    name                TEXT NOT NULL,
    dataset             TEXT NOT NULL,
    seed                INTEGER NOT NULL,
    config_json         TEXT NOT NULL,
    requirements_json   TEXT NOT NULL,
    retention_json      TEXT NOT NULL,
    accepted_attempt_id INTEGER REFERENCES attempts(id),
    created_at          TEXT NOT NULL,
    UNIQUE (study_id, ordinal)
);

CREATE TABLE attempts (
    id                  INTEGER PRIMARY KEY,
    cell_id             INTEGER NOT NULL REFERENCES cells(id),
    ordinal             INTEGER NOT NULL,
    site_id             INTEGER NOT NULL REFERENCES sites(id),
    fencing_epoch       INTEGER NOT NULL,
    state               TEXT NOT NULL CHECK (state IN (
                            'opened', 'running', 'completed', 'failed', 'lost', 'cancelled')),
    remote_execution_id TEXT,
    request_json        TEXT NOT NULL,
    result_json         TEXT,
    started_at          TEXT NOT NULL,
    last_progress_at    TEXT,
    ended_at            TEXT,
    UNIQUE (cell_id, ordinal)
);

CREATE TABLE events (
    id          INTEGER PRIMARY KEY,
    attempt_id  INTEGER NOT NULL REFERENCES attempts(id),
    at          TEXT NOT NULL,
    kind        TEXT NOT NULL,
    phase       TEXT,
    detail_json TEXT NOT NULL
);

CREATE INDEX idx_cells_study ON cells(study_id, ordinal);
CREATE INDEX idx_attempts_cell ON attempts(cell_id, ordinal);
CREATE INDEX idx_events_attempt ON events(attempt_id, id);

-- The one-store Home is append-only where a record is evidence: an event is
-- never changed or removed, an attempt is never removed (its state changes
-- through the fenced writes only), and a cell or study is never removed.
CREATE TRIGGER events_no_update
BEFORE UPDATE ON events BEGIN
    SELECT RAISE(ABORT, 'events are append-only');
END;

CREATE TRIGGER events_no_delete
BEFORE DELETE ON events BEGIN
    SELECT RAISE(ABORT, 'events are append-only');
END;

CREATE TRIGGER attempts_no_delete
BEFORE DELETE ON attempts BEGIN
    SELECT RAISE(ABORT, 'attempts are never deleted; their state changes through fenced writes');
END;

CREATE TRIGGER cells_no_delete
BEFORE DELETE ON cells BEGIN
    SELECT RAISE(ABORT, 'cells are never deleted');
END;

CREATE TRIGGER studies_no_delete
BEFORE DELETE ON studies BEGIN
    SELECT RAISE(ABORT, 'studies are never deleted; request cancel instead');
END;

-- CHG 2026-09-11 night, slice 1 of the port (design note
-- docs/design/original/by_ai/2026-09-11-restore-the-behavior-port-the-input.md):
-- the resident supervisor of one Site, and the requests an operator addresses
-- to it. `supervisors` is the retired scheduler office and its service attempt
-- in one row per supervisor lifetime, per Site instead of per Home: the epoch
-- fences a displaced supervisor, `lease_until` is the written expiry a reader
-- judges without knowing this code's constants, and a row whose `ended_at` is
-- NULL after its lease ran out is a supervisor that died unconfirmed, never one
-- that stopped. `operator_requests` carries pause, stop and hold with a scope,
-- so one table answers "what did a person ask of the machines".
CREATE TABLE supervisors (
    id            INTEGER PRIMARY KEY,
    site_id       INTEGER NOT NULL REFERENCES sites(id),
    host          TEXT NOT NULL,
    pid           INTEGER NOT NULL,
    epoch         INTEGER NOT NULL,
    lease_until   TEXT NOT NULL,
    binary_sha256 TEXT NOT NULL,
    started_at    TEXT NOT NULL,
    ended_at      TEXT,
    outcome       TEXT,
    disposition   TEXT CHECK (disposition IS NULL OR disposition IN (
                      'completed', 'retryable', 'terminal_configuration', 'fatal')),
    exit_code     INTEGER,
    UNIQUE (site_id, epoch)
);

CREATE TABLE operator_requests (
    id             INTEGER PRIMARY KEY,
    kind           TEXT NOT NULL CHECK (kind IN ('pause', 'stop', 'hold')),
    scope          TEXT NOT NULL,
    reason         TEXT NOT NULL,
    requested_by   TEXT NOT NULL,
    requested_at   TEXT NOT NULL,
    consumed_at    TEXT,
    consumed_by    INTEGER REFERENCES supervisors(id),
    released_at    TEXT,
    released_by    TEXT,
    release_reason TEXT
);

CREATE INDEX idx_supervisors_site ON supervisors(site_id, epoch);

CREATE TRIGGER supervisors_no_delete
BEFORE DELETE ON supervisors BEGIN
    SELECT RAISE(ABORT, 'supervisor lifetimes are evidence and are never deleted');
END;

CREATE TRIGGER operator_requests_no_delete
BEFORE DELETE ON operator_requests BEGIN
    SELECT RAISE(ABORT, 'operator requests are evidence and are never deleted; release one instead');
END;

-- CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
-- slice 4; owner: installed sources retired): site_sources, which declared
-- source a Site held and where, is dropped by the walk from 16 to 17 after the
-- Registry is copied; its CREATE is kept in RegistrySchemaTransforms for the
-- step that once added it.

-- CHG 2026-09-14 (round 3 of the stage-vocabulary design): one digest per
-- top-level section of a cell's configuration, as the Go compiler minted them
-- beside the identity. Two cells share a section exactly when they share its
-- digest, which is what lets a reader ask "these two cells differ; where?" and
-- be answered from the Home instead of by finding two files on disk.
--
-- Carried, never interpreted: this host does not own the vocabulary that says
-- what `model` or `train` means, and one that checked it would be re-acquiring
-- exactly what the 2026-09-11 cut removed. A row per region rather than a JSON
-- column on `cells` because the question is a GROUP BY, and because a column
-- cannot be added to a table this schema compares by its exact text.
--
-- CHG 2026-09-26 (docs/programs/2026-09-26-cards-without-hashes; owner: "the
-- card diff section supersedes this, so this should be retired"):
-- cell_config_regions is dropped by the walk from 17 to 18 after the Registry
-- is copied; monitor-study compares cells.config_json field by field instead.
-- Its CREATE is kept in RegistrySchemaTransforms for step 12, which added it.

-- CHG 2026-09-11 night (slice 7 of the cell port, row 12): the order queued
-- work is offered in. A running cell is never moved, because a cell with an
-- open attempt is not in the offer at all. set-priority writes it, replacing
-- the row in place; nothing deletes one. The hold half of the retired queue is
-- an operator_requests row of kind hold (slice 2).
--
-- CHG 2026-09-25 (slices P2 and P3 of the scheduling loop,
-- docs/programs/2026-09-24-gleam-scheduler-decisions/2026-09-24-the-cycle-that-claims-and-dispatches):
-- a priority is one of four ranks, highest first immediate, urgent, normal,
-- slow, and no longer a number. A cell's own rank overrides its study's; a
-- study or cell without a row is normal. Within one rank the study registered
-- first goes first, and inside a study the cell compiled first. Rows written
-- as numbers before this were converted by the owner's mapping of 2026-09-25,
-- and converted_from keeps the number each one had.
CREATE TABLE dispatch_priorities (
    scope          TEXT PRIMARY KEY,
    priority       TEXT NOT NULL,
    reason         TEXT NOT NULL,
    set_by         TEXT NOT NULL,
    set_at         TEXT NOT NULL,
    converted_from INTEGER,
    CHECK (scope LIKE 'study:%' OR scope LIKE 'cell:%'),
    CHECK (priority IN ('immediate', 'urgent', 'normal', 'slow'))
);

CREATE TRIGGER dispatch_priorities_no_delete
BEFORE DELETE ON dispatch_priorities BEGIN
    SELECT RAISE(ABORT, 'dispatch_priorities rows are replaced by set-priority, never deleted');
END;

-- CHG 2026-09-25 (slice P2 of the scheduling loop): a person's move of a study
-- or a cell to the back of its rank, once. behind_study_id is the latest
-- registered study when the move was made: the moved work waits behind every
-- study registered so far and ahead of every study registered afterwards, and
-- two moves made behind the same study keep the order they were made in
-- (set_at). A cell's own move overrides its study's. move-last writes it,
-- replacing the row in place when the same work is moved again.
CREATE TABLE dispatch_last_moves (
    scope           TEXT PRIMARY KEY,
    behind_study_id INTEGER NOT NULL,
    reason          TEXT NOT NULL,
    set_by          TEXT NOT NULL,
    set_at          TEXT NOT NULL,
    CHECK (scope LIKE 'study:%' OR scope LIKE 'cell:%')
);

CREATE TRIGGER dispatch_last_moves_no_delete
BEFORE DELETE ON dispatch_last_moves BEGIN
    SELECT RAISE(ABORT, 'dispatch_last_moves rows are replaced by move-last, never deleted');
END;

-- CHG 2026-09-25 (slice M2 of
-- docs/programs/2026-09-24-gleam-scheduler-decisions/2026-09-25-what-stops-work-and-who-lifts-it): the
-- compiler that compiled a study, by the sha256 of its bytes and the path it
-- ran from. register-study records it from the compiled study; a study
-- registered before this has no row, and a supervisor then uses its own
-- compiler as before. A supervisor whose compiler for the study's source is
-- another does not claim the study's cells, and says which to install.
CREATE TABLE study_compilers (
    study_id        INTEGER PRIMARY KEY REFERENCES studies(id),
    compiler_sha256 TEXT NOT NULL,
    compiler_path   TEXT NOT NULL,
    recorded_by     TEXT NOT NULL,
    recorded_at     TEXT NOT NULL,
    CHECK (length(compiler_sha256) = 64)
);

CREATE TRIGGER study_compilers_no_delete
BEFORE DELETE ON study_compilers BEGIN
    SELECT RAISE(ABORT, 'study_compilers rows are replaced by set-study-compiler, never deleted');
END;

-- CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4; owner:
-- "compile远端其实只是检查，本地compile卡片即可"): site_compilers, which compilers
-- a Site held, is dropped by the walk from 16 to 17 with site_sources; no
-- compiler is carried to a Site any more. Its CREATE is kept in
-- RegistrySchemaTransforms.
