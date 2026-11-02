using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Fern.Registry;

/// <summary>
/// How a Registry Home written under an older schema becomes current.
///
/// The Registry refuses a database whose schema differs from the embedded one
/// and, until 2026-09-10, offered no way to satisfy that refusal: changing one
/// column meant archiving every existing Home whole. That is not a property of
/// SQLite. It followed from identifying the schema ONLY by a SHA-256 of its own
/// text, which can say "different" and never "which version", so it could not
/// say which transformation to apply.
///
/// The version is the identity; the SHA verifies the embedded bytes.
/// docs/reference/textbook/data-is-the-identity.md.
///
/// The version lives in `PRAGMA user_version`, a SQLite header field rather
/// than a schema object, so it is invisible to the object-by-object comparison
/// and a database written before versions existed reads 0 without refusing on
/// the identity table itself.
/// </summary>
public static class RegistrySchemaTransforms
{
    /// <summary>Databases written before the version stamp existed read 0.</summary>
    public const long BeforeVersionsExisted = 0;

    /// <summary>The version the embedded schema is. Raise it in the same patch as a schema change, with the transform that reaches it.</summary>
    public const long Current = 22;

    /// <summary>One step from `From` to `From + 1`, applied inside the caller's transaction.</summary>
    /// <param name="Refuses">
    /// CHG 2026-09-25: a query run before the step, each of whose rows is one
    /// sentence naming a row the step cannot carry forward. Any row refuses
    /// the walk with those sentences, and nothing is written: a step that
    /// converts values converts only the values it was told how to.
    /// </param>
    /// <param name="RefusalRemedy">What a person does about the rows <paramref name="Refuses"/> names, so the same Home can walk.</param>
    /// <param name="DeletesRows">
    /// CHG 2026-09-27 (owner: "注册表升级别再让人手抄整库", a Registry upgrade must not
    /// make a person copy the whole database by hand): the step drops a table that
    /// may hold rows, or deletes rows. A walk with such a step on its way
    /// copies the whole Registry itself, before its first step, to
    /// <c>&lt;registry&gt;.before-walk-&lt;date&gt;</c>, and that copy is where the
    /// rows are kept. It replaces two older rules: that a dropped table be empty
    /// unless the step allowed its seeded rows, and that a copy named by the step
    /// already sit beside the Registry, made by hand, or the walk refused.
    /// </param>
    public sealed record Step(
        long From, string Why, string Sql, string? Refuses = null, string? RefusalRemedy = null,
        bool DeletesRows = false);

    /// <summary>
    /// CHG 2026-09-11 night (slice 8 of the cell port, rows 40 to 42): the
    /// tables of the Plan, Stage Plan, Matrix, evidence, queue and daemon
    /// families, whose last writer left the tree with them. Each is dropped
    /// only when empty; the one row the schema seeded in the queue revision
    /// counter is the one exception, and it is the schema's own.
    /// </summary>
    /// <summary>
    /// The model card vocabulary's tables. An architecture card said what a
    /// model family is and can do; an implementation card said which bundle,
    /// lock, runtime and backend realise it; a link admission recorded that a
    /// probe had proved the two fit. The compiler says all of it now and
    /// checks it per cell: tools/fern-compile declares the vocabulary in
    /// internal/wire/descriptors.go and enforces forty relations over it,
    /// ported one for one from this repository's own C# and Python rule sets.
    /// The tables were the SQL way of stating the same thing, admitted once;
    /// nothing outside their own tests ever wrote one.
    /// </summary>
    public static readonly IReadOnlyList<string> RetiredModelCardTables =
    [
        "model_architectures",
        "model_implementations",
        "model_link_admissions",
    ];

    /// <summary>
    /// The last of the verification-admission family. A smoke-run admission
    /// recorded, durably, that a smoke run had been admitted on a Site: its
    /// implementation, model, data, split, geometry, the environment and
    /// model-link admissions behind it, and its outcome. It leaves for the
    /// owner's own rule -- smoke never registers or enters a database -- and
    /// because the pass that consumed it, Pass 5 of the stage scheduler's
    /// admission sequence, retired with the daemon. Its sibling admissions and
    /// the model cards left on 2026-09-12; this table still carried a column
    /// pointing at one of them. What ran is an attempt; what a smoke proved is
    /// its own folder. retired/csharp/smoke-admissions-2026-09-13/.
    /// </summary>
    public static readonly IReadOnlyList<string> RetiredSmokeAdmissionTables =
    [
        "smoke_run_admissions",
    ];

    /// <summary>
    /// The frozen source's catalogue. A source content row was the content
    /// address of one declared file set and the bytes lived beside it in a
    /// store under the deploy root; an observation recorded the window that
    /// proved the snapshot was one instant; the implementation rows bound a
    /// source to the bundle that realised it, and the legacy map pointed the
    /// pre-2026-09 snapshot ids at content ids.
    ///
    /// All of it answered "which bytes of the tree were present", which a
    /// commit answers, and which the cell identity has not needed since it
    /// began naming the configuration, the four layers of the data, the
    /// declaration version and the stage. A study's source is now one `sources`
    /// row: the commit, the declaration of every file that code is, and a
    /// digest over it. retired/csharp/frozen-source-2026-09-13/.
    /// </summary>
    public static readonly IReadOnlyList<string> RetiredFrozenSourceTables =
    [
        "implementation_bindings",
        "implementation_source_snapshots",
        "legacy_source_snapshot_maps",
        "source_contents",
        "source_implementations",
        "source_observations",
    ];

    /// <summary>
    /// SQL-first agent memory: what an agent remembered, searchable by a
    /// full-text index, and the sessions and steps around it. Its own design
    /// note (docs/design/decisions/2026-07-04-02-sql-first-agent-memory.md)
    /// records that it was superseded for durable knowledge by the card system,
    /// and the card system itself retired into the `work` tool on 2026-09-02.
    /// What an agent knows now is text in git: `work` judgments, the records
    /// under `.ai/`, and the memory files. The search index's five shadow
    /// tables leave with the virtual table that owns them.
    /// </summary>
    public static readonly IReadOnlyList<string> RetiredAgentMemoryTables =
    [
        "agent_memory_fts",
        "agent_steps",
        "agent_sessions",
        "agent_memory",
    ];

    /// <summary>The per-run tables of an archive database, which a Registry Home never wrote.</summary>
    public static readonly IReadOnlyList<string> RetiredRunStoreTables =
    [
        "configs",
        "run_events",
        "runs",
    ];

    /// <summary>Slice 11: the Matrix tables a surviving table referenced until then, and the accepted-results table.</summary>
    public static readonly IReadOnlyList<string> RetiredMatrixTables =
    [
        "accepted_results",
        "coordinate_attempts",
        "matrix_coordinates",
        "experiment_matrices",
        "run_plans",
    ];

    /// <summary>
    /// CHG 2026-09-11 night (slice 9 of the cell port): the old Worker Site
    /// registry and the two admissions it keyed, whose facts live on the
    /// `sites` row now. `provider_stop_requests` referenced the registry and is
    /// recreated without the reference, empty.
    /// </summary>
    public static readonly IReadOnlyList<string> RetiredSiteRegistryTables =
    [
        "machine_admissions", "site_environment_admissions", "worker_site_capabilities", "worker_sites",
    ];

    public static readonly IReadOnlyList<string> RetiredTables =
    [
        "attempt_implementation_sources",
        "attempt_leases",
        "attempt_liveness",
        "attempt_progress_events",
        "cell_attempts",
        "cell_dispatch_settings",
        "cell_phase_outputs",
        "cell_phase_receipts",
        "cell_remote_execution_reconciliations",
        "cell_transport_attempts",
        "checked_evidence_commits",
        "coordinate_authorities",
        "coordinate_authority_leases",
        "coordinate_implementations",
        "coordinator_authorities",
        "coordinator_leases",
        "dispatch_queue_revision",
        "evidence_acceptance_decisions",
        "evidence_policies",
        "evidence_release_events",
        "evidence_release_members",
        "evidence_releases",
        "experiment_contrast_arms",
        "experiment_contrasts",
        "implementation_lifecycle",
        "knowledge_events",
        "matrix_implementation_sources",
        "plan_dispatch_blocks",
        "plan_dispatch_settings",
        "plan_watch_events",
        "receipt_consumer_validations",
        "resource_slot_leases",
        "run_plan_attempts",
        "run_plan_leases",
        "run_plan_steps",
        "scheduler_authorities",
        "scheduler_cycle_stamps",
        "scheduler_leases",
        "scheduler_service_attempts",
        "scheduler_stop_requests",
        "scheduling_pauses",
        "stage_attempt_provenance",
        "stage_attempts",
        "stage_plan_instance_inputs",
        "stage_plan_instance_site_lowerings",
        "stage_plan_instances",
        "stage_plan_nodes",
        "stage_plans",
        "task_dependencies",
        "task_plans",
        "task_receipts",
        "verified_evidence_candidates",
    ];

    /// <summary>
    /// The ordered steps, keyed by the version each one starts from.
    ///
    /// Empty at version 1 on purpose: every Home in existence on 2026-09-10 was
    /// written under this schema and only lacks the stamp, which
    /// <see cref="RegistrySchema"/> adopts without transforming anything. The
    /// first real entry arrives with the first schema change.
    ///
    /// CHG 2026-09-11 night (slice 1 of the cell port): the first real entry.
    /// Its SQL is not typed here but taken from the embedded schema by object
    /// name, so the objects a walked-forward Home holds are byte-identical to
    /// the ones a fresh Home is created with, which is what RequireCurrent
    /// compares object by object.
    ///
    /// CHG 2026-09-27: RequireCurrent compares structure (columns by PRAGMA
    /// table_info) rather than text, so a step may ALTER TABLE ADD COLUMN and
    /// Schema.sql write the column plainly. Where a step's comment below says its rows go only
    /// when its table is empty, or only once a copy of the whole Registry sits
    /// beside it, the step now declares DeletesRows and the walk makes that
    /// copy itself.
    /// </summary>
    public static IReadOnlyList<Step> Steps => StepsByVersion.Value;

    static readonly Lazy<IReadOnlyList<Step>> StepsByVersion = new(() =>
    [
        new Step(
            From: 1,
            Why: "slice 1 of the cell port: one resident supervisor per Site, and the pause, stop and hold requests an operator addresses to it",
            Sql: RegistrySchema.StatementsFor(
                "supervisors", "operator_requests", "idx_supervisors_site",
                "supervisors_no_delete", "operator_requests_no_delete")),
        new Step(
            From: 2,
            Why: "slice 6 of the cell port: which declared source a Site holds, verified by declared bytes, checked by the claim",
            // CHG 2026-09-26: the table left Schema.sql at step 16; its text is kept here.
            Sql: SiteSourcesAsCreated),
        new Step(
            From: 3,
            Why: "slice 7 of the cell port: the order queued work is offered in, a priority per study or cell",
            // CHG 2026-09-25: typed out as it was, because step 13 converts
            // this table and the embedded schema now holds the converted one.
            Sql: NumberedPriorities),
        new Step(
            From: 4,
            Why: "slice 8 of the cell port: the Plan, Stage Plan, Matrix, evidence, queue and daemon tables leave with their writers",
            Sql: string.Join("\n", RetiredTables.Select(table => $"DROP TABLE IF EXISTS {table};")),
            DeletesRows: true),
        new Step(
            From: 5,
            Why: "slice 9 of the cell port: the old Worker Site registry and its two admissions move to the Site row",
            Sql: "DROP TABLE IF EXISTS provider_stop_requests;\n"
                + RegistrySchema.StatementsFor(
                    "provider_stop_requests", "provider_stop_requests_one_active_pod", "provider_stop_requests_reconcile",
                    "provider_stop_requests_identity_immutable", "provider_stop_requests_no_delete",
                    "provider_stop_requests_no_submission_expiry_immutable", "provider_stop_requests_terminal_receipt_immutable") + "\n"
                + string.Join("\n", RetiredSiteRegistryTables.Select(table => $"DROP TABLE IF EXISTS {table};")),
            DeletesRows: true),
        // CHG 2026-09-11 night (slice 11 of the cell port): the last references
        // into the Matrix tables move to cells and attempts. artifact_snapshots
        // (a sealed artifact directory of one attempt) is keyed by cells(id)
        // and attempts(id) instead of the coordinate and its attempt;
        // preparation_operations loses plan_id, which every writer left NULL
        // since the Plan went; accepted_results, whose one fact "this attempt
        // is the cell's accepted one" is cells.accepted_attempt_id, leaves with
        // the four Matrix tables. Each table dropped only when empty.
        new Step(
            From: 6,
            Why: "slice 11 of the cell port: artifact snapshots and preparation operations reference cells and attempts; the Matrix tables and accepted_results leave",
            Sql: "DROP TABLE IF EXISTS artifact_snapshots;\nDROP TABLE IF EXISTS preparation_operations;\n"
                + RegistrySchema.StatementsFor(
                    "artifact_snapshots", "idx_artifact_snapshots_cell", "one_artifact_snapshot_per_attempt",
                    "preparation_operations", "preparation_operations_guard_update") + "\n"
                + string.Join("\n", RetiredMatrixTables.Select(table => $"DROP TABLE IF EXISTS {table};")),
            DeletesRows: true),
        // CHG 2026-09-12: the Home is not a run store. `runs`, `run_events` and
        // `configs` are the per-run tables of the campaign databases the
        // science readers open BY ARGUMENT (`list-runs --db <fern.db>`,
        // RunDataProjection, RunViewsReader): no operation of this Home has
        // written one since results became a cell's own document in the science
        // store, and the readers keep reading the archives that do hold them.
        new Step(
            From: 7,
            Why: "2026-09-12: the Home is not a run store; the per-run archive tables leave and their readers keep opening archives",
            Sql: string.Join("\n", RetiredRunStoreTables.Select(table => $"DROP TABLE IF EXISTS {table};")),
            DeletesRows: true),
        // CHG 2026-09-12, the owner's cut: "i think I am ready to make a
        // removal of this now we have compiler language". The card vocabulary
        // and its storage leave together; what identified a model is the
        // compiler's typed vocabulary and the configuration identity it mints.
        new Step(
            From: 8,
            Why: "2026-09-12: the model card vocabulary retires; the compiler declares and checks what a card described",
            Sql: string.Join("\n", RetiredModelCardTables.Select(table => $"DROP TABLE IF EXISTS {table};")),
            DeletesRows: true),
        // CHG 2026-09-13, the owner's cut continued: the last of the
        // verification admissions follows the model cards out. The owner's own
        // rule is that smoke never registers or enters a database, and this
        // table's only purpose was to register smoke runs.
        new Step(
            From: 9,
            Why: "2026-09-13: smoke never enters a database; the last verification admission leaves with the family it belonged to",
            Sql: string.Join("\n", RetiredSmokeAdmissionTables.Select(table => $"DROP TABLE IF EXISTS {table};")),
            DeletesRows: true),
        // CHG 2026-09-13, the owner's cut: the frozen source's catalogue leaves.
        // Git content-addresses trees, so the second copy of the bytes and the
        // six tables that indexed it go; the `sources` row that names the commit
        // and declares the files stays, and is what an install reads.
        new Step(
            From: 10,
            Why: "2026-09-13: a source is its commit and the declaration beside it; the content store and its catalogue leave",
            Sql: string.Join("\n", RetiredFrozenSourceTables.Select(table => $"DROP TABLE IF EXISTS {table};")),
            DeletesRows: true),
        // CHG 2026-09-14, the owner's cut: SQL-first agent memory leaves. What
        // superseded it retired two weeks ago, so what an agent knows is text in
        // git. The triggers that fed the search index go first, because a drop
        // of the table they read would leave them pointing at nothing.
        new Step(
            From: 11,
            Why: "2026-09-14: SQL-first agent memory retires; what an agent knows is text in git, and what superseded this retired first",
            Sql: "DROP TRIGGER IF EXISTS agent_memory_fts_ai;\n"
                + "DROP TRIGGER IF EXISTS agent_memory_fts_ad;\n"
                + "DROP TRIGGER IF EXISTS agent_memory_fts_au;\n"
                + string.Join("\n", RetiredAgentMemoryTables.Select(table => $"DROP TABLE IF EXISTS {table};")),
            DeletesRows: true),
        // CHG 2026-09-14: round 3 of the stage-vocabulary design. The compiler
        // has minted one digest per section of a configuration since
        // 2026-09-12, and Python has recorded them in the compiled study, but
        // the Home never carried them -- so "these two cells differ; where?"
        // could only be answered by finding two files on disk. The column is
        // carried and never interpreted: this host does not own the vocabulary
        // that says what `model` or `train` means.
        //
        // The first step that ADDS rather than drops, and a TABLE rather than a
        // column: this schema compares every object against the embedded text,
        // and `ALTER TABLE ADD COLUMN` writes a CREATE TABLE that no clean
        // Schema.sql can reproduce -- SQLite appends the column after the table
        // constraints, where a freshly written definition cannot put it. A Home
        // stepped that way would be refused by the identity check the step
        // exists to satisfy. A cell registered before this simply has no rows,
        // which reads as "compiled before the digests were carried".
        new Step(
            From: 12,
            Why: "2026-09-14: a cell carries the region digests the compiler minted, so a comparison is a query rather than two files on disk",
            // CHG 2026-09-26: the table left Schema.sql at step 17; its text is kept here.
            Sql: CellConfigRegionsAsCreated),
        // CHG 2026-09-25 (slices P2 and P3 of the scheduling loop): a priority
        // becomes one of four ranks, and a person may move work to the back of
        // its rank once. The table is rebuilt rather than altered, for the
        // reason step 12 gives, and its rows are carried, not dropped: each
        // number becomes a rank by the owner's rule of 2026-09-25 -- 1000 and
        // above immediate; 60 to 999 urgent; 0 to 59 normal; below 0 slow --
        // and keeps the number in converted_from. (His first answer mapped
        // only the numbers then waiting; the walk's preview found 198 rows with
        // others, and he chose this rule, "good", the same evening.) A number
        // the rule does not place refuses the walk and is named.
        new Step(
            From: 13,
            Why: "2026-09-25: a priority is one of four ranks, immediate, urgent, normal or slow; a move puts work behind its rank once",
            // CHG 2026-09-25: the rename carries the table's triggers with it,
            // the observation triggers included, whose names the new table's
            // are made with; they go before the new table is created (found
            // by preview-registry-walk on the live Home).
            Sql: "ALTER TABLE dispatch_priorities RENAME TO dispatch_priorities_as_numbers;\n"
                + "DROP TRIGGER dispatch_priorities_no_delete;\n"
                + "DROP TRIGGER IF EXISTS fern_observe_dispatch_priorities_INSERT;\n"
                + "DROP TRIGGER IF EXISTS fern_observe_dispatch_priorities_UPDATE;\n"
                + "DROP TRIGGER IF EXISTS fern_observe_dispatch_priorities_DELETE;\n"
                + RegistrySchema.StatementsFor(
                    "dispatch_priorities", "dispatch_priorities_no_delete",
                    "dispatch_last_moves", "dispatch_last_moves_no_delete") + "\n"
                + $"""
                  INSERT INTO dispatch_priorities (scope, priority, reason, set_by, set_at, converted_from)
                  SELECT scope, {RankOfNumber("priority")}, reason, set_by, set_at, priority
                  FROM dispatch_priorities_as_numbers;
                  DROP TABLE dispatch_priorities_as_numbers;
                  """,
            Refuses: $"""
                      SELECT 'dispatch priority ' || quote(priority) || ' over ' || scope || ' is not a whole number, so the owner''s rule places it at no rank'
                      FROM dispatch_priorities
                      WHERE {RankOfNumber("priority")} IS NULL
                      ORDER BY scope
                      """,
            // Only a binary of schema 13 opens this Home before the walk, and
            // its set-priority takes numbers: the remedy is a mapped number,
            // or a mapping the owner extends in this step.
            RefusalRemedy: "With the fern-run that still opens this Home (schema 13), set each named priority to a whole "
                + "number, then walk this same Home again"),
        // CHG 2026-09-25 (slice M2 of the stop design): which compiler compiled a study.
        new Step(
            From: 14,
            Why: "2026-09-25: a study records the compiler that compiled it, so a supervisor with another does not claim its cells",
            Sql: RegistrySchema.StatementsFor("study_compilers", "study_compilers_no_delete")),
        // CHG 2026-09-26 (docs/programs/2026-09-08-one-route-from-card-to-store/2026-09-26-the-study-source-carries-its-compiler):
        // which compilers a Site holds, by digest, and where. A table rather
        // than a key in the Site row's capabilities_json, for the reason held
        // sources left that JSON for site_sources on 2026-09-11: it is a fact
        // per Site and per thing held, read by a claim, and replaced in place.
        // Only adds, so no row of an older Home is touched; a Site simply holds
        // no recorded compiler until install-study-source carries one.
        new Step(
            From: 15,
            Why: "2026-09-26: install-study-source records which compilers a Site holds, so a supervisor finds the one a study names without a --source-compiler pair",
            // CHG 2026-09-26: the table left Schema.sql at step 16; its text is kept here.
            Sql: SiteCompilersAsCreated),
        // CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
        // slice 4; owner: "远端and registry 都删"): installed sources and carried
        // compilers are retired, so the two tables that recorded them go. Their
        // rows are the history of what was installed where; the walk drops them
        // only once a copy of the whole Registry sits beside it
        // (<registry>.before-schema-17), which is where that history is kept.
        new Step(
            From: 16,
            Why: "2026-09-26: installed sources and carried compilers are retired; a Site takes each cell's code from its repository",
            Sql: """
                 DROP TABLE IF EXISTS site_sources;
                 DROP TABLE IF EXISTS site_compilers;
                 """,
            DeletesRows: true),
        // CHG 2026-09-26 (docs/programs/2026-09-26-cards-without-hashes; owner: "the
        // card diff section supersedes this, so this should be retired"): the region
        // digests leave. What they summarised, each cell's whole configuration, stays
        // in cells.config_json, and monitor-study compares it field by field. The
        // rows go only once a copy of the whole Registry sits beside it
        // (<registry>.before-schema-18). Dropping the table drops its triggers.
        new Step(
            From: 17,
            Why: "2026-09-26: region digests retire; a cell's configuration is compared field by field from cells.config_json",
            Sql: "DROP TABLE IF EXISTS cell_config_regions;",
            DeletesRows: true),
        // CHG 2026-09-26 (docs/programs/2026-09-26-cards-without-hashes, design
        // "Registry schema step 18" parts 2 and 3; owner: "get rid of 256
        // entirely"): an operation outcome keeps the request it answered as
        // text instead of its SHA-256, and a transition receipt loses its hash
        // chain. The two tables are rebuilt together, because an outcome's
        // transition_receipt_id references a receipt: renaming a table rewrites
        // every foreign key that names it, so both move aside, both are created
        // again from Schema.sql, their rows are copied across, and the moved
        // tables are dropped, the outcomes before the receipts they reference.
        // The rename carries each table's triggers and indexes with it, the
        // observation triggers included, whose names the new tables use; they
        // go before the new tables are created, as in step 13.
        //
        // Every row is carried. An outcome written before this step keeps its
        // answer, and its request_text is NULL: only a digest of the request
        // was kept, and a digest cannot be turned back into the request. A
        // receipt keeps its id, whatever form that id had. Nothing is dropped
        // but the digests themselves, so no copy of the Registry is asked for.
        //
        // CHG 2026-09-26 (owner, on the 6,157 old outcomes: "like useless"): the old
        // outcomes are not carried. Each answered one call a remote supervisor made,
        // kept so a retry of that call could be answered again; those retries ended long
        // ago. operation_outcomes is dropped first, so the walk drops its rows only beside
        // a copy of the whole Registry (<registry>.before-schema-18, the copy step 17 asks
        // for), and it is created again empty. Only transition_receipts moves aside and is
        // copied; nothing names it once the outcomes are gone, so its rename rewrites no
        // other table. request_text is then NOT NULL: every row has its request.
        new Step(
            From: 18,
            Why: "2026-09-26: an operation outcome stores its request as text, not a digest; transition receipts lose their hash chain",
            Sql: """
                 DROP TABLE IF EXISTS operation_outcomes;
                 ALTER TABLE transition_receipts RENAME TO transition_receipts_with_hash_chain;
                 DROP TRIGGER transition_receipts_no_delete;
                 DROP TRIGGER transition_receipts_no_update;
                 DROP TRIGGER IF EXISTS fern_observe_transition_receipts_INSERT;
                 DROP TRIGGER IF EXISTS fern_observe_transition_receipts_UPDATE;
                 DROP TRIGGER IF EXISTS fern_observe_transition_receipts_DELETE;
                 DROP INDEX idx_transition_receipts_attempt;
                 DROP INDEX idx_transition_receipts_object;
                 """
                + RegistrySchema.StatementsFor(
                    "transition_receipts", "idx_transition_receipts_attempt", "idx_transition_receipts_object",
                    "transition_receipts_no_delete", "transition_receipts_no_update",
                    "operation_outcomes", "operation_outcomes_no_delete", "operation_outcomes_no_update") + "\n"
                + """
                  INSERT INTO transition_receipts (
                      transition_receipt_id, operation_name, object_kind, object_id, object_sequence,
                      prior_state, next_state, actor_kind, actor_id, attempt_id,
                      coordinator_lease_id, coordinate_lease_id, resource_slot_lease_id, fencing_epoch,
                      input_identity_json, output_identity_json, policy_revision, correlation_id,
                      causation_receipt_id, error_category, occurred_at_ms)
                  SELECT
                      transition_receipt_id, operation_name, object_kind, object_id, object_sequence,
                      prior_state, next_state, actor_kind, actor_id, attempt_id,
                      coordinator_lease_id, coordinate_lease_id, resource_slot_lease_id, fencing_epoch,
                      input_identity_json, output_identity_json, policy_revision, correlation_id,
                      causation_receipt_id, error_category, occurred_at_ms
                  FROM transition_receipts_with_hash_chain;
                  DROP TABLE transition_receipts_with_hash_chain;
                  """,
            DeletesRows: true),
        // CHG 2026-09-26 (docs/programs/2026-09-26-cards-without-hashes; owner:
        // "Analysis names: remove, even this analysis entering registry is
        // suspicious slop"): statistic admission leaves the Registry, and with it
        // the table it wrote. A statistic is what `python -m fern_analysis compute`
        // writes beside its report; nothing is stored for it here. The table has
        // been in the schema since version 1, so no step created it and no text
        // of it is kept. Its rows go only once a copy of the whole Registry sits
        // beside it (<registry>.before-schema-20), which is where they are kept.
        new Step(
            From: 19,
            Why: "2026-09-26: statistic admission retires; a statistic is the report and outputs compute writes, not a Registry row",
            Sql: "DROP TABLE IF EXISTS analyses;",
            DeletesRows: true),
        // CHG 2026-09-26 (docs/programs/2026-09-26-cards-without-hashes; owner: "registry
        // should not record those too ... we delete it anyways"): a source keeps how it was
        // registered in words (registered_from) where it kept a SHA-256 whose prefix said so.
        // Every row is carried: source-commit-v1 becomes commit, source-worktree-v1 becomes
        // working-tree, anything older unknown. The column is added and bundle_sha256 dropped
        // in place: renaming the table would rewrite the reference studies.source_id holds
        // (SQLite does so whenever foreign keys are on, which they are during a walk), and
        // Schema.sql states the definition SQLite writes this way. Only a digest leaves, so
        // no copy is asked for.
        new Step(
            From: 20,
            Why: "2026-09-26: a source records how it was registered in words, not in the prefix of a SHA-256 over its files",
            Sql: """
                 ALTER TABLE sources ADD COLUMN registered_from TEXT NOT NULL DEFAULT 'unknown' CHECK (registered_from IN ('commit', 'working-tree', 'unknown'));
                 UPDATE sources SET registered_from =
                     CASE WHEN bundle_sha256 LIKE 'source-commit-v1:%' THEN 'commit'
                          WHEN bundle_sha256 LIKE 'source-worktree-v1:%' THEN 'working-tree'
                          ELSE 'unknown' END;
                 ALTER TABLE sources DROP COLUMN bundle_sha256;
                 """),
        // CHG 2026-09-27 (docs/programs/2026-09-26-cards-without-hashes/2026-09-27-config-hash-leaves,
        // slice 4; owner: "我建议 config_hash 做"): the sixteen-character hash the compiler
        // minted over a cell's configuration is not stored; the configuration itself,
        // in config_json, is the cell's identity and is compared field by field. The
        // column is dropped in place, as step 20 dropped bundle_sha256; every row stays.
        new Step(
            From: 21,
            Why: "2026-09-27: a cell's configuration is its identity, compared field by field; no hash of it is stored",
            Sql: "ALTER TABLE cells DROP COLUMN payload_sha256;"),
    ]);

    /// <summary>sources as schema 20 held it, with bundle_sha256 where registered_from is; step 20 rebuilt it.</summary>
    internal const string SourcesAsVersionTwenty =
        """
        CREATE TABLE sources (
            id                  INTEGER PRIMARY KEY,
            commit_sha          TEXT NOT NULL,
            declared_files_json TEXT NOT NULL,
            bundle_sha256       TEXT NOT NULL,
            created_at          TEXT NOT NULL
        );
        """;

    /// <summary>
    /// operation_outcomes and transition_receipts, their indexes and their
    /// triggers, as schema 18 held them; step 18 rebuilt them for schema 19.
    /// Kept so a test can put a Home back to version 18 and walk it.
    /// </summary>
    internal const string JournalsAsVersionEighteen =
        """
        CREATE TABLE "operation_outcomes" (
            operation_scope            TEXT NOT NULL,
            actor_id                   TEXT NOT NULL,
            idempotency_key            TEXT NOT NULL,
            operation_name             TEXT NOT NULL,
            request_digest             TEXT NOT NULL,
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
            previous_receipt_hash      TEXT,
            receipt_hash               TEXT NOT NULL,
            occurred_at_ms             BIGINT NOT NULL,
            UNIQUE (object_kind, object_id, object_sequence)
        );
        CREATE INDEX idx_transition_receipts_attempt
            ON transition_receipts (attempt_id, occurred_at_ms);
        CREATE INDEX idx_transition_receipts_object
            ON transition_receipts (object_kind, object_id, occurred_at_ms);
        CREATE TRIGGER operation_outcomes_no_delete
        BEFORE DELETE ON operation_outcomes BEGIN
            SELECT RAISE(ABORT, 'operation outcomes are append-only');
        END;
        CREATE TRIGGER operation_outcomes_no_update
        BEFORE UPDATE ON operation_outcomes BEGIN
            SELECT RAISE(ABORT, 'operation outcomes are append-only');
        END;
        CREATE TRIGGER transition_receipts_no_delete
        BEFORE DELETE ON transition_receipts BEGIN
            SELECT RAISE(ABORT, 'transition receipts are append-only');
        END;
        CREATE TRIGGER transition_receipts_no_update
        BEFORE UPDATE ON transition_receipts BEGIN
            SELECT RAISE(ABORT, 'transition receipts are append-only');
        END;
        """;

    /// <summary>cell_config_regions as step 12 created it; Schema.sql dropped it at step 17.</summary>
    internal const string CellConfigRegionsAsCreated =
        """
        CREATE TABLE cell_config_regions (
            cell_id INTEGER NOT NULL REFERENCES cells(id),
            region  TEXT NOT NULL,
            digest  TEXT NOT NULL,
            PRIMARY KEY (cell_id, region)
        );
        CREATE TRIGGER cell_config_regions_no_delete
        BEFORE DELETE ON cell_config_regions BEGIN
            SELECT RAISE(ABORT, 'a cell''s region digests are the identity it was compiled under and are never deleted');
        END;
        """;

    /// <summary>site_sources as step 2 created it; Schema.sql dropped it at step 16.</summary>
    const string SiteSourcesAsCreated =
        """
        CREATE TABLE site_sources (
            site_id       INTEGER NOT NULL REFERENCES sites(id),
            source_id     INTEGER NOT NULL REFERENCES sources(id),
            root          TEXT NOT NULL,
            bundle_sha256 TEXT NOT NULL,
            file_count    INTEGER NOT NULL,
            installed_at  TEXT NOT NULL,
            installed_by  TEXT NOT NULL,
            PRIMARY KEY (site_id, source_id)
        );
        CREATE TRIGGER site_sources_no_delete
        BEFORE DELETE ON site_sources BEGIN
            SELECT RAISE(ABORT, 'site_sources rows are replaced by a later install, never deleted');
        END;
        """;

    /// <summary>site_compilers as step 15 created it; Schema.sql dropped it at step 16.</summary>
    const string SiteCompilersAsCreated =
        """
        CREATE TABLE site_compilers (
            site_id         INTEGER NOT NULL REFERENCES sites(id),
            compiler_sha256 TEXT NOT NULL,
            path            TEXT NOT NULL,
            installed_at    TEXT NOT NULL,
            installed_by    TEXT NOT NULL,
            PRIMARY KEY (site_id, compiler_sha256),
            CHECK (length(compiler_sha256) = 64)
        );
        CREATE TRIGGER site_compilers_no_delete
        BEFORE DELETE ON site_compilers BEGIN
            SELECT RAISE(ABORT, 'site_compilers rows are replaced by a later install, never deleted');
        END;
        """;

    /// <summary>
    /// The owner's rule of 2026-09-25 from the numbers the Registry held to
    /// the four ranks, as a SQL expression over <paramref name="column"/>:
    /// 1000 and above immediate, 60 to 999 urgent, 0 to 59 normal, below 0
    /// slow. A value that is not an integer maps to NULL, which step 13 refuses.
    /// </summary>
    static string RankOfNumber(string column) =>
        $"CASE WHEN typeof({column}) != 'integer' THEN NULL "
        + $"WHEN {column} >= 1000 THEN 'immediate' "
        + $"WHEN {column} >= 60 THEN 'urgent' "
        + $"WHEN {column} >= 0 THEN 'normal' "
        + "ELSE 'slow' END";

    /// <summary>The priority table as step 3 created it, before step 13 converted its numbers to ranks.</summary>
    const string NumberedPriorities =
        """
        CREATE TABLE dispatch_priorities (
            scope    TEXT PRIMARY KEY,
            priority INTEGER NOT NULL,
            reason   TEXT NOT NULL,
            set_by   TEXT NOT NULL,
            set_at   TEXT NOT NULL,
            CHECK (scope LIKE 'study:%' OR scope LIKE 'cell:%')
        );
        CREATE TRIGGER dispatch_priorities_no_delete
        BEFORE DELETE ON dispatch_priorities BEGIN
            SELECT RAISE(ABORT, 'dispatch_priorities rows are replaced by set-priority, never deleted');
        END;
        """;



    /// <summary>The step that leaves `version`, or null when none is registered.</summary>
    public static Step? From(long version)
    {
        foreach (var step in Steps)
        {
            if (step.From == version) return step;
        }
        return null;
    }

    /// <summary>Read the version stamped in the database header; 0 means it predates the stamp.</summary>
    public static long ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return command.ExecuteScalar() is long value ? value : BeforeVersionsExisted;
    }

    /// <summary>Stamp the version. PRAGMA takes no parameter, so the value is checked rather than bound.</summary>
    public static void StampVersion(SqliteConnection connection, long version)
    {
        // PRAGMA takes no parameter, so the value is checked rather than bound —
        // safe because it is a long, and formatted invariantly so a machine's
        // locale cannot write a version SQLite reads back differently.
        if (version < 0) throw new InvalidOperationException($"a schema version is not negative, got {version}");
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version = " + version.ToString(CultureInfo.InvariantCulture);
        command.ExecuteNonQuery();
    }
}
