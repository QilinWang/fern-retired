using Fern.Registry;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fern.Registry.Tests;

/// <summary>
/// The first registered transform step (schema version 1 to 2, slice 1 of the
/// cell port). A Home written under version 1 lacks `supervisors` and
/// `operator_requests`; initializing it again walks it forward, and the walked
/// Home passes the object-by-object comparison a fresh Home passes, because
/// the step's DDL is read from the embedded schema rather than typed twice.
/// </summary>
public sealed class RegistrySchemaTransformStepTests
{
    static SqliteConnection Raw(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>
    /// CHG 2026-09-27: walk a Home whose way holds a step that deletes rows, and
    /// return the copy of the whole Registry the walk made beside it before its
    /// first step, as the receipt names it.
    /// </summary>
    static string WalkAndReadCopy(string path)
    {
        var walked = Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(path));
        var copy = walked.Receipt.CopiedBeforeWalk;
        Assert.NotNull(copy);
        Assert.StartsWith(path + ".before-walk-", copy!, StringComparison.Ordinal);
        return copy!;
    }

    static long Count(string path, string sql)
    {
        using var connection = Raw(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static long VersionOfFile(string path)
    {
        using var connection = Raw(path);
        return RegistrySchemaTransforms.ReadVersion(connection);
    }

    /// <summary>
    /// CHG 2026-09-25: what the steps from version 12 on created, taken back
    /// out of a current Home — the region digests (step 12), the moves and the
    /// ranks (step 13), the study compilers (step 14) — so a Home stamped with an older version holds what
    /// that version held and the walk creates each object once.
    /// CHG 2026-09-26: and the compilers a Site holds (step 15).
    /// CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): a current
    /// Home has neither site_compilers nor site_sources (step 16 drops both), so
    /// site_sources, which version 12 held, is put back.
    /// CHG 2026-09-26 (cards-without-hashes): a current Home has no
    /// cell_config_regions either (step 17 drops it), so there is none to take out.
    /// CHG 2026-09-26 (schema 19): and its two journals are put back as version
    /// 18 held them, which every version before 19 did (<see cref="BackToVersionEighteen"/>).
    /// </summary>
    static void BackToVersionTwelve(SqliteConnection connection)
    {
        BackToVersionEighteen(connection);
        using var command = connection.CreateCommand();
        command.CommandText =
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
            DROP TRIGGER study_compilers_no_delete;
            DROP TABLE study_compilers;
            DROP TRIGGER dispatch_last_moves_no_delete;
            DROP TABLE dispatch_last_moves;
            DROP TRIGGER dispatch_priorities_no_delete;
            DROP TABLE dispatch_priorities;
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
        command.ExecuteNonQuery();
        // CHG 2026-09-25: a live Home's tables carry the observation triggers
        // initialization installs on every table; the preview of the live Home
        // found step 13 colliding with them, which a fixture without them hid.
        foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            using var observe = connection.CreateCommand();
            observe.CommandText = $"""
                CREATE TRIGGER "fern_observe_dispatch_priorities_{operation}" AFTER {operation} ON "dispatch_priorities"
                BEGIN UPDATE registry_observation SET revision = revision + 1 WHERE singleton = 1; END
                """;
            observe.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// CHG 2026-09-26 (schema 19): operation_outcomes and transition_receipts
    /// taken out of a current Home and created again as version 18 held them,
    /// request digests and hash chain included, with the observation triggers
    /// a live Home carries on every table, so step 18 meets what it will meet.
    /// </summary>
    static void BackToVersionEighteen(SqliteConnection connection)
    {
        // CHG 2026-09-26 (schema 20): version 18 held the analyses table, as every version before 20 did.
        BackToVersionNineteen(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE operation_outcomes;\nDROP TABLE transition_receipts;\n"
            + RegistrySchemaTransforms.JournalsAsVersionEighteen;
        command.ExecuteNonQuery();
        foreach (var table in new[] { "operation_outcomes", "transition_receipts" })
        foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            using var observe = connection.CreateCommand();
            observe.CommandText = $"""
                CREATE TRIGGER "fern_observe_{table}_{operation}" AFTER {operation} ON "{table}"
                BEGIN UPDATE registry_observation SET revision = revision + 1 WHERE singleton = 1; END
                """;
            observe.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// CHG 2026-09-26 (schema 20): the analyses table as the original schema
    /// created it and every version up to 19 held it. Step 19 drops it, and no
    /// step created it, so the transforms keep no text of it; this copy exists
    /// only to put a Home back to version 19.
    /// </summary>
    const string AnalysesAsVersionNineteen =
        """
        CREATE TABLE analyses (
            analysis_hash       TEXT PRIMARY KEY,
            schema              TEXT NOT NULL,
            kind                TEXT NOT NULL,
            query_json          TEXT NOT NULL,
            run_hashes_json     TEXT NOT NULL,
            scoring_fingerprint TEXT NOT NULL,
            params_json         TEXT NOT NULL,
            result_json         TEXT NOT NULL,
            created_at          TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """;

    /// <summary>
    /// CHG 2026-09-26 (schema 20): the analyses table put back into a current
    /// Home, with the observation triggers a live Home carries on every table,
    /// so step 19 meets what it will meet.
    /// </summary>
    /// <summary>
    /// CHG 2026-09-27 (config-hash-leaves, schema 22): cells as version 21 held it,
    /// with payload_sha256, so step 21 meets the column it drops. Added at the end:
    /// the position a column had does not change what dropping it does.
    /// </summary>
    static void BackToVersionTwentyOne(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE cells ADD COLUMN payload_sha256 TEXT NOT NULL DEFAULT ''";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// CHG 2026-09-26 (cards-without-hashes, schema 21): sources as version 20 held it,
    /// with bundle_sha256 where registered_from is, and a live Home's observation
    /// triggers, so step 20 meets what a real Home has. A fresh Home's sources is
    /// empty, so it is dropped and made again.
    /// </summary>
    static void BackToVersionTwenty(SqliteConnection connection)
    {
        BackToVersionTwentyOne(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE sources;\n" + RegistrySchemaTransforms.SourcesAsVersionTwenty;
        command.ExecuteNonQuery();
        foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            using var observe = connection.CreateCommand();
            observe.CommandText = $"""
                CREATE TRIGGER "fern_observe_sources_{operation}" AFTER {operation} ON "sources"
                BEGIN UPDATE registry_observation SET revision = revision + 1 WHERE singleton = 1; END
                """;
            observe.ExecuteNonQuery();
        }
    }

    static void BackToVersionNineteen(SqliteConnection connection)
    {
        // CHG 2026-09-26 (schema 21): version 19 held sources as 20 did.
        BackToVersionTwenty(connection);
        using var command = connection.CreateCommand();
        command.CommandText = AnalysesAsVersionNineteen;
        command.ExecuteNonQuery();
        foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            using var observe = connection.CreateCommand();
            observe.CommandText = $"""
                CREATE TRIGGER "fern_observe_analyses_{operation}" AFTER {operation} ON "analyses"
                BEGIN UPDATE registry_observation SET revision = revision + 1 WHERE singleton = 1; END
                """;
            observe.ExecuteNonQuery();
        }
    }

    /// <summary>A current Home with the slice-1 objects dropped and the header stamped 1: what every Home looked like before tonight.</summary>
    static string VersionOneHome()
    {
        var path = ProductionHome.CreateRegistry();
        using var connection = Raw(path);
        BackToVersionTwelve(connection);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DROP TRIGGER supervisors_no_delete;
            DROP TRIGGER operator_requests_no_delete;
            DROP TRIGGER site_sources_no_delete;
            DROP TRIGGER dispatch_priorities_no_delete;
            CREATE TABLE worker_sites (site_id TEXT PRIMARY KEY);
            DROP TABLE operator_requests;
            DROP TABLE supervisors;
            DROP TABLE site_sources;
            DROP TABLE dispatch_priorities;
            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
        return path;
    }

    [Fact]
    public void TheEmbeddedSchemaIsVersionTwentyTwoAndEachStepLeavesTheVersionBeforeIt()
    {
        Assert.Equal(22, RegistrySchemaTransforms.Current);
        var step = RegistrySchemaTransforms.From(1);
        Assert.NotNull(step);
        Assert.Contains("CREATE TABLE supervisors", step!.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE operator_requests", step.Sql, StringComparison.Ordinal);
        Assert.Contains("fern_observe_supervisors_INSERT", step.Sql, StringComparison.Ordinal);
        // Slice 6 (row 31): which declared source a Site holds.
        var sources = RegistrySchemaTransforms.From(2);
        Assert.NotNull(sources);
        Assert.Contains("CREATE TABLE site_sources", sources!.Sql, StringComparison.Ordinal);
        Assert.Contains("site_sources_no_delete", sources.Sql, StringComparison.Ordinal);
        // Slice 7 (row 12): the order queued work is offered in.
        var priorities = RegistrySchemaTransforms.From(3);
        Assert.NotNull(priorities);
        Assert.Contains("CREATE TABLE dispatch_priorities", priorities!.Sql, StringComparison.Ordinal);
        // Slice 8 (rows 40 to 42): the retired families' tables leave, empty.
        var retired = RegistrySchemaTransforms.From(4);
        Assert.NotNull(retired);
        Assert.Contains("DROP TABLE IF EXISTS stage_plans;", retired!.Sql, StringComparison.Ordinal);
        Assert.True(retired.DeletesRows);
        // Slice 9: the old Site registry and its admissions leave for the Site row.
        var registry = RegistrySchemaTransforms.From(5);
        Assert.NotNull(registry);
        Assert.Contains("DROP TABLE IF EXISTS worker_sites;", registry!.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"provider_stop_requests\"", registry.Sql, StringComparison.Ordinal);
        Assert.True(registry.DeletesRows);
        // Slice 11: the last Matrix references move to cells and attempts; the
        // Matrix tables and accepted_results leave.
        var matrix = RegistrySchemaTransforms.From(6);
        Assert.NotNull(matrix);
        Assert.Contains("DROP TABLE IF EXISTS accepted_results;", matrix!.Sql, StringComparison.Ordinal);
        Assert.Contains("DROP TABLE IF EXISTS run_plans;", matrix.Sql, StringComparison.Ordinal);
        Assert.Contains("REFERENCES cells(id)", matrix.Sql, StringComparison.Ordinal);
        Assert.Contains("REFERENCES attempts(id)", matrix.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("plan_id", matrix.Sql, StringComparison.Ordinal);
        Assert.True(matrix.DeletesRows);
        // 2026-09-12: the per-run archive tables leave; the Home is not a run store.
        var runStore = RegistrySchemaTransforms.From(7);
        Assert.NotNull(runStore);
        Assert.True(runStore!.DeletesRows);
        Assert.Contains("DROP TABLE IF EXISTS runs;", runStore.Sql, StringComparison.Ordinal);
        // 2026-09-12, the owner's cut: the model card vocabulary retires.
        var cards = RegistrySchemaTransforms.From(8);
        Assert.NotNull(cards);
        Assert.True(cards!.DeletesRows);
        Assert.Contains("DROP TABLE IF EXISTS model_architectures;", cards.Sql, StringComparison.Ordinal);
        // 2026-09-13, the same cut continued: the last verification admission
        // leaves. Smoke never registers or enters a database, which is the
        // owner's rule this table had been contradicting.
        var smoke = RegistrySchemaTransforms.From(9);
        Assert.NotNull(smoke);
        Assert.True(smoke!.DeletesRows);
        Assert.Contains("DROP TABLE IF EXISTS smoke_run_admissions;", smoke.Sql, StringComparison.Ordinal);
        // 2026-09-13: the frozen source's catalogue leaves; a source is its
        // commit and the declaration beside it, and git holds the bytes.
        var frozen = RegistrySchemaTransforms.From(10);
        Assert.NotNull(frozen);
        Assert.True(frozen!.DeletesRows);
        Assert.Contains("DROP TABLE IF EXISTS source_contents;", frozen.Sql, StringComparison.Ordinal);
        // 2026-09-14: SQL-first agent memory leaves, index first, and the five
        // shadow tables the search index owns leave with it.
        var memory = RegistrySchemaTransforms.From(11);
        Assert.NotNull(memory);
        Assert.True(memory!.DeletesRows);
        Assert.Contains("DROP TRIGGER IF EXISTS agent_memory_fts_ai;", memory.Sql, StringComparison.Ordinal);
        // 2026-09-14: a cell carries the region digests the compiler minted.
        Assert.Contains("CREATE TABLE cell_config_regions", RegistrySchemaTransforms.From(12)!.Sql, StringComparison.Ordinal);
        // 2026-09-25: a priority becomes one of four ranks, and a move to the back of a rank is kept.
        var ranks = RegistrySchemaTransforms.From(13);
        Assert.NotNull(ranks);
        Assert.Contains("CREATE TABLE dispatch_last_moves", ranks!.Sql, StringComparison.Ordinal);
        Assert.NotNull(ranks.Refuses);
        // CHG 2026-09-27: steps that only add, or carry every row, make the walk copy nothing.
        foreach (var from in new long[] { 1, 2, 3, 12, 13, 14, 15, 20 })
            Assert.False(RegistrySchemaTransforms.From(from)!.DeletesRows, $"step from {from} deletes no rows");
        // 2026-09-25 (slice M2 of the stop design): which compiler compiled a study.
        Assert.Contains("CREATE TABLE study_compilers", RegistrySchemaTransforms.From(14)!.Sql, StringComparison.Ordinal);
        // 2026-09-26: which compilers a Site holds, written by install-study-source.
        Assert.Contains("CREATE TABLE site_compilers", RegistrySchemaTransforms.From(15)!.Sql, StringComparison.Ordinal);
        Assert.Contains("site_compilers_no_delete", RegistrySchemaTransforms.From(15)!.Sql, StringComparison.Ordinal);
        // 2026-09-26 (source-roots-stop-being-full-copies, slice 4): installed sources and carried compilers retire.
        var installedSourcesRetire = RegistrySchemaTransforms.From(16);
        Assert.NotNull(installedSourcesRetire);
        Assert.Contains("DROP TABLE IF EXISTS site_sources;", installedSourcesRetire!.Sql, StringComparison.Ordinal);
        Assert.True(installedSourcesRetire.DeletesRows);
        // 2026-09-26 (cards-without-hashes): the region digests retire.
        var regionsRetire = RegistrySchemaTransforms.From(17);
        Assert.NotNull(regionsRetire);
        Assert.Contains("DROP TABLE IF EXISTS cell_config_regions;", regionsRetire!.Sql, StringComparison.Ordinal);
        Assert.True(regionsRetire.DeletesRows);
        // 2026-09-26 (cards-without-hashes, schema 19): the two journals are rebuilt
        // without digests; every row is carried, so no table is dropped with rows
        // and no copy is asked for.
        var journalsRebuilt = RegistrySchemaTransforms.From(18);
        Assert.NotNull(journalsRebuilt);
        // CHG 2026-09-26 (owner: "like useless"): the old outcomes are dropped beside a copy.
        Assert.True(journalsRebuilt!.DeletesRows);
        Assert.Contains("request_text", journalsRebuilt.Sql, StringComparison.Ordinal);
        // 2026-09-26 (cards-without-hashes, schema 20; owner: "Analysis names: remove"):
        // statistic admission retires, and the table it wrote leaves beside a copy.
        var analysesRetire = RegistrySchemaTransforms.From(19);
        Assert.NotNull(analysesRetire);
        Assert.Contains("DROP TABLE IF EXISTS analyses;", analysesRetire!.Sql, StringComparison.Ordinal);
        Assert.True(analysesRetire.DeletesRows);
        // 2026-09-26 (cards-without-hashes, schema 21): a source says how it was registered in words.
        var sourcesInWords = RegistrySchemaTransforms.From(20);
        Assert.NotNull(sourcesInWords);
        Assert.Contains("registered_from", sourcesInWords!.Sql, StringComparison.Ordinal);
        // CHG 2026-09-27 (config-hash-leaves): the cell's configuration hash leaves, rows kept.
        var configHashLeaves = RegistrySchemaTransforms.From(21);
        Assert.NotNull(configHashLeaves);
        Assert.Contains("DROP COLUMN payload_sha256", configHashLeaves!.Sql, StringComparison.Ordinal);
        Assert.False(configHashLeaves.DeletesRows);
        Assert.Null(RegistrySchemaTransforms.From(22));
    }

    /// <summary>
    /// CHG 2026-09-26 (cards-without-hashes, schema 21): step 20 carries every source,
    /// turning the prefix of its bundle digest into registered_from, and leaves the
    /// studies that name a source pointing at the same row; the walked Home is current
    /// object for object, so the reference in studies still names sources.
    /// </summary>
    /// <summary>
    /// CHG 2026-09-27 (config-hash-leaves, schema 22): step 21 drops the cell's
    /// configuration hash in place; every cell stays with its configuration.
    /// </summary>
    [Fact]
    public void StepTwentyOneDropsTheConfigurationHashAndKeepsEveryCell()
    {
        var path = ProductionHome.CreateRegistry();
        using (var connection = Raw(path))
        {
            BackToVersionTwentyOne(connection);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO sources (id, commit_sha, declared_files_json, registered_from, created_at)
                VALUES (1, 'c1', '[]', 'commit', '2026-09-26');
                INSERT INTO studies (id, name, revision, compiled_study_json, source_id, requested_state, created_at)
                VALUES (1, 'kept', 1, '{}', 1, 'run', '2026-09-26');
                -- Version 21's columns with payload_sha256 last, where BackToVersionTwentyOne
                -- adds it; unnamed because payload_sha256 is not a column of this build's schema.
                INSERT INTO cells VALUES
                    (1, 1, 0, 'cell-0', 'lorenz.series', 42, '{"schema":"fern.training.v2"}', '{}', '{}', NULL,
                     '2026-09-26', '0123456789abcdef');
                PRAGMA user_version = 21;
                """;
            command.ExecuteNonQuery();
        }

        Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(path));
        using var walked = Raw(path);
        Assert.Equal(22, RegistrySchemaTransforms.ReadVersion(walked));
        using var columns = walked.CreateCommand();
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('cells') WHERE name = 'payload_sha256'";
        Assert.Equal(0L, Convert.ToInt64(columns.ExecuteScalar()));
        using var cells = walked.CreateCommand();
        cells.CommandText = "SELECT name, config_json FROM cells";
        using var reader = cells.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("cell-0", reader.GetString(0));
        Assert.Equal("{\"schema\":\"fern.training.v2\"}", reader.GetString(1));
        Assert.False(reader.Read());
    }

    [Fact]
    public void StepTwentyCarriesEverySourceWithHowItWasRegisteredInWords()
    {
        var path = ProductionHome.CreateRegistry();
        using (var connection = Raw(path))
        {
            BackToVersionTwenty(connection);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                -- Version 20's column order (id, commit_sha, declared_files_json, bundle_sha256,
                -- created_at), unnamed because bundle_sha256 is not a column of this build's schema.
                INSERT INTO sources VALUES
                    (1, 'c1', '[]', 'source-commit-v1:aa', '2026-09-20'),
                    (2, 'c2', '[]', 'source-worktree-v1:bb', '2026-09-21'),
                    (3, 'c3', '[]', 'sha256:cc', '2026-09-10');
                INSERT INTO studies (id, name, revision, compiled_study_json, source_id, requested_state, created_at)
                VALUES (1, 'kept', 1, '{}', 2, 'run', '2026-09-21');
                PRAGMA user_version = 20;
                """;
            command.ExecuteNonQuery();
        }

        Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(path));
        using var walked = Raw(path);
        Assert.Equal(RegistrySchemaTransforms.Current, RegistrySchemaTransforms.ReadVersion(walked));
        using var rows = walked.CreateCommand();
        rows.CommandText = "SELECT id, commit_sha, registered_from FROM sources ORDER BY id";
        var found = new List<(long, string, string)>();
        using (var reader = rows.ExecuteReader())
        {
            while (reader.Read()) found.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }
        Assert.Equal([(1L, "c1", "commit"), (2L, "c2", "working-tree"), (3L, "c3", "unknown")], found);
        using var study = walked.CreateCommand();
        study.CommandText = "SELECT s.commit_sha FROM studies t JOIN sources s ON s.id = t.source_id WHERE t.name = 'kept'";
        Assert.Equal("c2", study.ExecuteScalar());
        using var leftover = walked.CreateCommand();
        leftover.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'sources_with_digests' OR sql LIKE '%sources_with_digests%'";
        Assert.Equal(0L, Convert.ToInt64(leftover.ExecuteScalar()));
    }

    /// <summary>
    /// CHG 2026-09-26 (schema 19, cards-without-hashes; owner, on the old outcomes:
    /// "like useless"): a version-18 Home holding outcomes, one of them tied to a
    /// receipt, walks to 19 only beside a copy of the whole Registry, which is where
    /// its outcomes are kept; the walked Home holds no outcome, the receipt keeps its
    /// id and loses its two hashes, and the Home passes the object-by-object check.
    /// CHG 2026-09-27: the walk makes that copy itself; it is the Home at version 18.
    /// </summary>
    [Fact]
    public void StepEighteenKeepsTheOldOutcomesInTheWalksCopyAndCarriesEveryReceipt()
    {
        var path = ProductionHome.CreateRegistry();
        using (var connection = Raw(path))
        {
            BackToVersionEighteen(connection);
            using var command = connection.CreateCommand();
            // The rows are written in version 18's column order and without a
            // column list: they name columns Schema.sql no longer has, which is
            // the point, and TestSqlNamesRealColumnsTests checks a column list
            // against the current schema.
            command.CommandText =
                $$"""
                 INSERT INTO transition_receipts
                 VALUES ('{{new string('a', 64)}}', 'artifact:hold-acquire', 'csharp:artifact-hold', 'hold-1', 1,
                     NULL, 'held', 'cli', 'tester', NULL, NULL, NULL, NULL, NULL,
                     '{}', '{}', 'policy@1', 'corr-1', NULL, NULL, NULL, '{{new string('b', 64)}}', 5);
                 INSERT INTO operation_outcomes
                 VALUES ('home-http', 'home-client', 'key-0', 'claim', '{{new string('c', 64)}}',
                     'completed', '{"accepted":true}', NULL, NULL, 6),
                        ('csharp:artifact:hold-acquire:x', 'tester', 'key-1', 'artifact:hold-acquire', '{{new string('d', 64)}}',
                     'completed', '{"hold_id":"hold-1"}', NULL, '{{new string('a', 64)}}', 7);
                 PRAGMA user_version = 18;
                 """;
            command.ExecuteNonQuery();
        }

        var copy = WalkAndReadCopy(path);
        Assert.Equal(18L, VersionOfFile(copy));
        Assert.Equal(2L, Count(copy, "SELECT COUNT(*) FROM operation_outcomes"));
        using var walked = Raw(path);
        Assert.Equal(RegistrySchemaTransforms.Current, RegistrySchemaTransforms.ReadVersion(walked));
        RegistrySchema.RequireCurrent(walked, path);
        using var outcomes = walked.CreateCommand();
        outcomes.CommandText = "SELECT COUNT(*) FROM operation_outcomes";
        Assert.Equal(0L, Convert.ToInt64(outcomes.ExecuteScalar()));
        using var receipts = walked.CreateCommand();
        receipts.CommandText = "SELECT transition_receipt_id, occurred_at_ms FROM transition_receipts";
        using (var rows = receipts.ExecuteReader())
        {
            Assert.True(rows.Read());
            Assert.Equal(new string('a', 64), rows.GetString(0));
            Assert.Equal(5L, rows.GetInt64(1));
            Assert.False(rows.Read());
        }
        using var leftovers = walked.CreateCommand();
        leftovers.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE '%with_hash_chain%' OR sql LIKE '%with_hash_chain%'";
        Assert.Equal(0L, Convert.ToInt64(leftovers.ExecuteScalar()));
    }

    /// <summary>
    /// A current Home put back to version 16, with one row in each of the two
    /// tables step 16 drops: what the live Home held on 2026-09-26.
    /// </summary>
    static string VersionSixteenHomeWithRows()
    {
        var path = ProductionHome.CreateRegistry();
        using var connection = Raw(path);
        // CHG 2026-09-26 (schema 19): version 16 held the journals as 18 did.
        BackToVersionEighteen(connection);
        using var command = connection.CreateCommand();
        var compiler = new string('a', 64);
        command.CommandText =
            $$"""
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
            CREATE TABLE site_compilers (
                site_id         INTEGER NOT NULL REFERENCES sites(id),
                compiler_sha256 TEXT NOT NULL,
                path            TEXT NOT NULL,
                installed_at    TEXT NOT NULL,
                installed_by    TEXT NOT NULL,
                PRIMARY KEY (site_id, compiler_sha256),
                CHECK (length(compiler_sha256) = 64)
            );
            INSERT INTO sites (id, name, capabilities_json, capacity_json, created_at) VALUES (1, 'local-4070', '{}', '{}', '2026-09-26');
            INSERT INTO sources VALUES (1, 'c', '[]', 'b', '2026-09-26');
            INSERT INTO site_sources VALUES (1, 1, '/deploy/study-source-roots/a/b', 'b', 1, '2026-09-26', 'install-study-source');
            INSERT INTO site_compilers VALUES (1, '{{compiler}}', '/deploy/compile-binaries/fern-compile.a', '2026-09-26', 'install-study-source');
            PRAGMA user_version = 16;
            """
            // CHG 2026-09-26 (cards-without-hashes): version 16 held the region table, empty here.
            + RegistrySchemaTransforms.CellConfigRegionsAsCreated;
        command.ExecuteNonQuery();
        return path;
    }

    /// <summary>
    /// CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4; owner:
    /// "registry 都删"): step 16 drops site_sources and site_compilers with their
    /// rows only when a copy of the whole Registry sits beside it, which is where
    /// those rows are kept. Without the copy the walk refuses, names the copy to
    /// make, and changes nothing.
    /// CHG 2026-09-27: the walk makes the copy itself, and the rows are in it.
    /// </summary>
    [Fact]
    public void StepSixteenKeepsTheInstalledSourceRowsInTheWalksCopy()
    {
        var path = VersionSixteenHomeWithRows();
        var copy = WalkAndReadCopy(path);
        Assert.Equal(16L, VersionOfFile(copy));
        Assert.Equal(1L, Count(copy, "SELECT COUNT(*) FROM site_sources"));
        Assert.Equal(1L, Count(copy, "SELECT COUNT(*) FROM site_compilers"));
        using var walked = Raw(path);
        // CHG 2026-09-26: the walk goes on to 18; the empty region table needs no copy.
        Assert.Equal(RegistrySchemaTransforms.Current, RegistrySchemaTransforms.ReadVersion(walked));
        using var tables = walked.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('site_sources', 'site_compilers')";
        Assert.Equal(0L, Convert.ToInt64(tables.ExecuteScalar()));
    }

    /// <summary>
    /// CHG 2026-09-26 (cards-without-hashes; owner: "the card diff section
    /// supersedes this, so this should be retired"): step 17 drops
    /// cell_config_regions with its rows only when a copy of the whole Registry
    /// sits beside it. Without the copy the walk refuses, names the copy to
    /// make, and changes nothing.
    /// CHG 2026-09-27: the walk makes the copy itself, and the digests are in it.
    /// </summary>
    [Fact]
    public void StepSeventeenKeepsTheRegionDigestsInTheWalksCopy()
    {
        var path = ProductionHome.CreateRegistry();
        using (var connection = Raw(path))
        {
            // CHG 2026-09-26 (schema 19): version 17 held the journals as 18 did.
            BackToVersionEighteen(connection);
            using var command = connection.CreateCommand();
            command.CommandText = RegistrySchemaTransforms.CellConfigRegionsAsCreated + "\n"
                + """
                  INSERT INTO sources VALUES (1, '5e87434e', '[]', 'sha256:0', '2026-09-14');
                  INSERT INTO studies (id, name, revision, compiled_study_json, source_id, requested_state, created_at)
                  VALUES (1, 'swept', 1, '{}', 1, 'run', '2026-09-14');
                  -- CHG 2026-09-27: positional, payload_sha256 last where BackToVersionTwentyOne adds it;
                  -- unnamed because payload_sha256 is not a column of this build's schema.
                  INSERT INTO cells VALUES (1, 1, 0, 'cell-0', 'lorenz.series', 42, '{}', '{}', '{}', NULL, '2026-09-14', 'sha256:a');
                  INSERT INTO cell_config_regions (cell_id, region, digest) VALUES (1, 'model', '0123456789abcdef');
                  PRAGMA user_version = 17;
                  """;
            command.ExecuteNonQuery();
        }
        var copy = WalkAndReadCopy(path);
        Assert.Equal(17L, VersionOfFile(copy));
        Assert.Equal(1L, Count(copy, "SELECT COUNT(*) FROM cell_config_regions"));
        using var walked = Raw(path);
        // CHG 2026-09-26 (schema 19): the walk goes on through step 18 to 19.
        Assert.Equal(RegistrySchemaTransforms.Current, RegistrySchemaTransforms.ReadVersion(walked));
        using var tables = walked.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE tbl_name = 'cell_config_regions'";
        Assert.Equal(0L, Convert.ToInt64(tables.ExecuteScalar()));
    }

    /// <summary>
    /// CHG 2026-09-26 (cards-without-hashes, schema 20; owner: "Analysis names:
    /// remove, even this analysis entering registry is suspicious slop"): step
    /// 19 drops the analyses table with its rows only when a copy of the whole
    /// Registry sits beside it. Without the copy the walk refuses, names the
    /// copy to make, and changes nothing; with it, the Home walks to 20 without
    /// the table, and the rows are still in the copy.
    /// CHG 2026-09-27: the walk makes the copy itself.
    /// </summary>
    [Fact]
    public void StepNineteenKeepsTheAnalysesInTheWalksCopy()
    {
        var path = ProductionHome.CreateRegistry();
        using (var connection = Raw(path))
        {
            BackToVersionNineteen(connection);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO analyses (analysis_hash, schema, kind, query_json, run_hashes_json,
                                      scoring_fingerprint, params_json, result_json, created_at)
                VALUES ('69a3d39938f99287', 'fern.main_table.v1', 'main_results_table', '{}', '[]',
                        'python-implementation:0', '{}', '{"summary":{"mse":0.31}}', '2026-09-04 12:00:00'),
                       ('0123456789abcdef', 'fern.clean_target_response.v4', 'clean_target_response', '{}', '[]',
                        'python-implementation:1', '{}', '{"summary":{}}', '2026-09-05 12:00:00');
                PRAGMA user_version = 19;
                """;
            command.ExecuteNonQuery();
        }

        var copy = WalkAndReadCopy(path);
        Assert.Equal(19L, VersionOfFile(copy));
        using (var walked = Raw(path))
        {
            Assert.Equal(RegistrySchemaTransforms.Current, RegistrySchemaTransforms.ReadVersion(walked));
            RegistrySchema.RequireCurrent(walked, path);
            using var tables = walked.CreateCommand();
            tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE tbl_name = 'analyses'";
            Assert.Equal(0L, Convert.ToInt64(tables.ExecuteScalar()));
        }

        using var archived = Raw(copy);
        using var kept = archived.CreateCommand();
        kept.CommandText = "SELECT analysis_hash FROM analyses ORDER BY analysis_hash";
        using var reader = kept.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("0123456789abcdef", reader.GetString(0));
        Assert.True(reader.Read());
        Assert.Equal("69a3d39938f99287", reader.GetString(0));
        Assert.False(reader.Read());
    }

    /// <summary>
    /// A current Home put back to version 13: the priority table as numbers,
    /// holding <paramref name="priorities"/>, and no table of moves.
    /// </summary>
    static string VersionThirteenHome(params (string Scope, long Priority)[] priorities)
    {
        var path = ProductionHome.CreateRegistry();
        using var connection = Raw(path);
        using var command = connection.CreateCommand();
        BackToVersionTwelve(connection);
        // Version 13 is version 12 with the region digests.
        command.CommandText =
            RegistrySchemaTransforms.CellConfigRegionsAsCreated + "\n"
            + """
            PRAGMA user_version = 13;
            """;
        command.ExecuteNonQuery();
        foreach (var (scope, priority) in priorities)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO dispatch_priorities VALUES ($scope, $priority, 'as the lane wanted', 'qilin', '2026-09-24T12:00:00.0000000+00:00')";
            insert.Parameters.AddWithValue("$scope", scope);
            insert.Parameters.AddWithValue("$priority", priority);
            insert.ExecuteNonQuery();
        }
        return path;
    }

    [Fact]
    public void StepThirteenGivesEachNumberItsRankByTheOwnersRuleAndKeepsTheNumber()
    {
        // The owner's rule of 2026-09-25: 1000 and above immediate; 60 to 999
        // urgent; 0 to 59 normal; below 0 slow — each edge on both sides.
        var path = VersionThirteenHome(
            ("study:1", 2700), ("study:2", 100), ("cell:3", 90), ("cell:4", 50), ("cell:5", 10), ("study:6", 0),
            ("study:7", -1000), ("study:8", 1000), ("study:9", 999), ("study:10", 60), ("study:11", 59), ("study:12", -1),
            ("cell:13", 1000004));
        Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(path));

        using var reader = RegistryHome.OpenReader(path);
        var expected = new (string Scope, string Rank, long Number)[]
        {
            ("study:1", "immediate", 2700), ("study:2", "urgent", 100), ("cell:3", "urgent", 90),
            ("cell:4", "normal", 50), ("cell:5", "normal", 10), ("study:6", "normal", 0), ("study:7", "slow", -1000),
            ("study:8", "immediate", 1000), ("study:9", "urgent", 999), ("study:10", "urgent", 60),
            ("study:11", "normal", 59), ("study:12", "slow", -1), ("cell:13", "immediate", 1000004),
        };
        foreach (var (scope, rank, number) in expected)
        {
            var read = RegistryOperations.ReadPriority(reader.Connection, scope);
            Assert.NotNull(read);
            Assert.Equal(rank, read!.Priority);
            Assert.Equal(number, read.ConvertedFrom);
            // Who set it, why and when are the person's, and are kept.
            Assert.Equal("as the lane wanted", read.Reason);
            Assert.Equal("qilin", read.SetBy);
            Assert.Equal("2026-09-24T12:00:00.0000000+00:00", read.SetAt);
        }
        using var raw = Raw(path);
        Assert.Equal(RegistrySchemaTransforms.Current, RegistrySchemaTransforms.ReadVersion(raw));
    }

    /// <summary>
    /// preview-registry-walk walks a copy: the copy is one consistent moment
    /// of the Home, and walking it leaves the Home at its own version.
    /// </summary>
    [Fact]
    public void APreviewCopyWalksWhileTheHomeKeepsItsVersionAndItsNumbers()
    {
        var home = VersionThirteenHome(("study:1", 2700), ("study:2", 90));
        var folder = Directory.CreateTempSubdirectory("walk-preview-test-").FullName;
        try
        {
            var copy = Path.Combine(folder, "home.db");
            RegistrySchema.CopyHome(home, copy);
            Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(copy));
            using (var reader = RegistryHome.OpenReader(copy))
            {
                Assert.Equal("immediate", RegistryOperations.ReadPriority(reader.Connection, "study:1")!.Priority);
                Assert.Equal(2, RegistryOperations.ListPriorities(reader.Connection).Count);
            }
            using var raw = Raw(home);
            Assert.Equal(13, RegistrySchemaTransforms.ReadVersion(raw));
            using var command = raw.CreateCommand();
            command.CommandText = "SELECT priority FROM dispatch_priorities WHERE scope = 'study:2'";
            Assert.Equal(90L, Convert.ToInt64(command.ExecuteScalar()));
            Assert.Throws<InvalidOperationException>(() => RegistrySchema.CopyHome(home, copy));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void StepThirteenRefusesAPriorityThatIsNotAWholeNumberAndWritesNothing()
    {
        var path = VersionThirteenHome(("study:1", 2700));
        using (var connection = Raw(path))
        {
            // The numbered table's INTEGER column still holds text SQLite cannot convert.
            using var odd = connection.CreateCommand();
            odd.CommandText = "INSERT INTO dispatch_priorities VALUES ('study:2', 'high', 'by hand', 'qilin', '2026-09-24T12:00:00.0000000+00:00')";
            odd.ExecuteNonQuery();
        }

        var refused = Assert.IsType<RegistryInitializationOutcome.Refused>(RegistrySchema.Initialize(path));
        Assert.Contains("dispatch priority 'high' over study:2 is not a whole number", refused.Detail, StringComparison.Ordinal);
        // The remedy is a whole number set by the binary that still opens this Home, never an archive.
        Assert.Contains("set each named priority to a whole number", refused.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("archive", refused.Detail, StringComparison.Ordinal);
        // CHG 2026-09-27: the walk copied the Home before its first step (steps 16 on
        // delete rows); the refusal rolled the Home back, and the copy went with it.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".before-walk-*"));
        using var raw = Raw(path);
        Assert.Equal(13, RegistrySchemaTransforms.ReadVersion(raw));
        using var command = raw.CreateCommand();
        command.CommandText = "SELECT priority FROM dispatch_priorities WHERE scope = 'study:1'";
        Assert.Equal(2700L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public void AVersionOneHomeIsRefusedAsIsAndWalksForwardWhenInitializedAgain()
    {
        var path = VersionOneHome();
        using (var connection = Raw(path))
        {
            Assert.Equal(1, RegistrySchemaTransforms.ReadVersion(connection));
        }
        // As it stands, the current binary refuses it: the objects it expects are missing.
        // CHG 2026-09-26: the version stamp is read first now, so the refusal
        // says the Home is older and how to walk it, before any object is named.
        var fault = Assert.Throws<RegistryFault>(() => RegistryHome.OpenReader(path).Dispose());
        Assert.Contains("older than this binary's", fault.Message, StringComparison.Ordinal);

        var walked = RegistrySchema.Initialize(path);
        Assert.IsType<RegistryInitializationOutcome.Initialized>(walked);

        using (var connection = Raw(path))
        {
            Assert.Equal(RegistrySchemaTransforms.Current, RegistrySchemaTransforms.ReadVersion(connection));
        }
        // Current in every way a fresh Home is: the reader opens it, and the new operations run on it.
        using var writer = RegistryHome.OpenWriter(path);
        using (var site = writer.Connection.CreateCommand())
        {
            site.CommandText =
                """
                INSERT INTO sites (name, capabilities_json, capacity_json, created_at)
                VALUES ('walked', '{"device":"cuda"}', '{"slots":1}', '2026-09-11') RETURNING id
                """;
            var siteId = Convert.ToInt64(site.ExecuteScalar());
            var started = RegistryOperations.StartSupervisor(writer, siteId, "alpha", 100, "sha256:aa");
            Assert.True(started.TryGetValue(out var permit));
            Assert.Equal(1, permit.Epoch);
        }
    }

    /// <summary>
    /// CHG 2026-09-27: every step together reaches the schema a fresh Home has,
    /// compared as RequireCurrent now compares, by structure. A table
    /// is compared by its columns as PRAGMA table_info reports them and its
    /// foreign keys as PRAGMA foreign_key_list does, so a column added by ALTER
    /// TABLE equals one a fresh CREATE TABLE writes; an index, trigger or view by
    /// its text, which a step takes from the embedded schema. A CHECK constraint
    /// is in a table's text only, so it is not compared.
    /// </summary>
    [Fact]
    public void AWalkedHomeAndAFreshHomeHoldTheSameObjectsByDefinition()
    {
        var walkedPath = VersionOneHome();
        Assert.IsType<RegistryInitializationOutcome.Initialized>(RegistrySchema.Initialize(walkedPath));
        var freshPath = ProductionHome.CreateRegistry();

        static SortedDictionary<string, string> Objects(string path)
        {
            using var connection = Raw(path);
            var found = new List<(string Type, string Name, string Sql)>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT type, name, sql FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'";
                using var reader = command.ExecuteReader();
                while (reader.Read()) found.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            var objects = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (type, name, sql) in found)
            {
                if (type != "table")
                {
                    objects[$"{type}:{name}"] = sql;
                    continue;
                }
                var lines = new List<string>();
                using var columns = connection.CreateCommand();
                columns.CommandText =
                    """
                    SELECT 'column ' || cid || ' ' || name || ' ' || type || ' notnull=' || "notnull" || ' default=' || ifnull(dflt_value, '-') || ' pk=' || pk
                    FROM pragma_table_info($table) ORDER BY cid
                    """;
                columns.Parameters.AddWithValue("$table", name);
                using (var reader = columns.ExecuteReader())
                {
                    while (reader.Read()) lines.Add(reader.GetString(0));
                }
                using var keys = connection.CreateCommand();
                keys.CommandText =
                    """
                    SELECT 'foreign key ' || id || '.' || seq || ' ' || "from" || ' -> ' || "table" || '(' || ifnull("to", '-') || ') on update ' || on_update || ' on delete ' || on_delete
                    FROM pragma_foreign_key_list($table) ORDER BY id, seq
                    """;
                keys.Parameters.AddWithValue("$table", name);
                using (var reader = keys.ExecuteReader())
                {
                    while (reader.Read()) lines.Add(reader.GetString(0));
                }
                objects[$"table:{name}"] = string.Join("\n", lines);
            }
            return objects;
        }

        Assert.Equal(Objects(freshPath), Objects(walkedPath));
    }

    /// <summary>
    /// Slice 8: a Home still carrying a retired family's table walks forward
    /// only when that table is empty. A row in it is evidence nobody archived;
    /// the walk refuses and names the table, and after the row is archived
    /// away the same Home walks and the table is gone.
    /// CHG 2026-09-27: the walk archives it itself: the table is dropped with its
    /// row, and the row is in the copy the walk made before its first step.
    /// </summary>
    [Fact]
    public void ARetiredTableWithRowsIsDroppedAndItsRowsAreInTheWalksCopy()
    {
        var path = ProductionHome.CreateRegistry();
        using (var connection = Raw(path))
        {
            BackToVersionTwelve(connection);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE stage_plans (stage_plan_id TEXT PRIMARY KEY, name TEXT);
                INSERT INTO stage_plans VALUES ('plan-1', 'left behind');
                PRAGMA user_version = 4;
                """;
            command.ExecuteNonQuery();
        }
        var copy = WalkAndReadCopy(path);
        Assert.Equal(1L, Count(copy, "SELECT COUNT(*) FROM stage_plans"));
        using (var connection = Raw(path))
        {
            Assert.Equal(RegistrySchemaTransforms.Current, RegistrySchemaTransforms.ReadVersion(connection));
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'stage_plans'";
            Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()));
        }
    }
}
