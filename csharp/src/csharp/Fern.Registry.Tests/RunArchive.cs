using Microsoft.Data.Sqlite;

namespace Fern.Registry.Tests;

/// <summary>
/// A campaign database of the kind the science readers open: the per-run
/// tables <c>runs</c>, <c>run_events</c> and <c>configs</c>.
///
/// CHG 2026-09-12: these used to be part of the Registry Home's own schema, so
/// a test could ask <c>ProductionHome.CreateRegistry</c> for a database and
/// write runs into it. No operation of a Home ever wrote one — a result is a
/// cell's document in the science store, and `list-runs --db`,
/// <see cref="Fern.RunData.RunViewsReader"/> and
/// <see cref="Fern.RunData.RunDataProjection"/> open the database they are
/// given, which is an archive. Schema version 8 dropped them from the Home, so
/// the DDL lives here, verbatim as it stood, because only an old file has it
/// and only these tests still need to make one.
/// </summary>
public static class RunArchive
{
    public const string Ddl =
        """
            CREATE TABLE configs (
                config_hash TEXT PRIMARY KEY,
                schema      TEXT NOT NULL,
                config_json TEXT NOT NULL,
                created_at  TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE run_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_hash TEXT NOT NULL,
                ts TEXT NOT NULL DEFAULT (datetime('now')),
                event TEXT NOT NULL,
                detail_json TEXT
            );

            CREATE TABLE "runs" (
                id            INTEGER PRIMARY KEY,
                artifact_dir  TEXT UNIQUE,

                dataset       TEXT NOT NULL,
                dataset_id    TEXT,
                model_type    TEXT NOT NULL,
                seed          INTEGER NOT NULL,
                pred_len      INTEGER NOT NULL,
                seq_len       INTEGER NOT NULL,
                noise_sigma   REAL DEFAULT 0.0,

                trajectory_draw_ordinal       INTEGER DEFAULT 0,
                noise_draw_ordinal     INTEGER DEFAULT 0,
                trajectory_draw_seed      INTEGER DEFAULT 42,
                noise_seed_base    INTEGER DEFAULT 1000,

                config_json   TEXT,
                config_hash   TEXT,
                invocation_json TEXT,
                invocation_hash TEXT,
                invocation_path TEXT,
                run_hash TEXT,
                success_hash    TEXT,
                config_name   TEXT,
                git_sha       TEXT,

                status        TEXT DEFAULT 'pending',
                started_at    TEXT,
                finished_at   TEXT,
                error_msg     TEXT,

                source        TEXT DEFAULT 'user_defined',
                artifacts     TEXT DEFAULT 'full',
                pinned        INTEGER DEFAULT 0,

                mse           REAL,
                mae           REAL,
                crps          REAL,
                swd           REAL,
                ept           REAL,

                template_id   INTEGER,
                n_features    INTEGER
            , trajectory_seed_base INTEGER DEFAULT 42, noise_draw_seed INTEGER DEFAULT 1000, ablation_study TEXT, ablation_name  TEXT, run_note       TEXT, code_fingerprint_json TEXT, target_hash TEXT, noise_law TEXT, split_treatment_scheme TEXT, noise_ar1_phi DOUBLE, relative_dose BOOLEAN, checkpoint_execution_mode TEXT, runtime_device TEXT, execution_kind TEXT, machine_alias TEXT, model_config_hash TEXT, clean_target_mse REAL, injected_target_noise_mse REAL, noise_forecast_overlap REAL, baseline_constant_mse REAL, baseline_context_mean_mse REAL, baseline_last_value_mse REAL);

            CREATE INDEX idx_run_events_run_hash ON run_events(run_hash);

            CREATE INDEX idx_runs_ablation ON runs(ablation_study, ablation_name);

            CREATE INDEX idx_runs_dataset_model ON runs(dataset, model_type);

            CREATE INDEX idx_runs_execution_slice
                ON runs (dataset, model_type, runtime_device, execution_kind);

            CREATE INDEX idx_runs_invocation_hash ON runs(invocation_hash);

            CREATE INDEX idx_runs_machine
                ON runs (machine_alias, dataset, model_type);

            CREATE INDEX idx_runs_model_build
                ON runs (model_config_hash, dataset, noise_sigma);

            CREATE INDEX idx_runs_protocol_slice
                ON runs (dataset, model_type, noise_law, split_treatment_scheme, noise_sigma);

            CREATE INDEX idx_runs_run_hash ON runs(run_hash);

            CREATE UNIQUE INDEX idx_runs_run_hash_unique ON runs(run_hash);

            CREATE INDEX idx_runs_status ON runs(status);

            CREATE INDEX idx_runs_success_hash ON runs(success_hash);

            CREATE INDEX idx_runs_target_hash ON runs(target_hash);

            CREATE INDEX idx_runs_template ON runs(template_id);
        """;

    /// <summary>An empty archive database at a fresh temporary path.</summary>
    public static string Create()
    {
        var directory = Directory.CreateTempSubdirectory("fern-run-archive-");
        var path = Path.Combine(directory.FullName, "fern.db");
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Ddl;
        command.ExecuteNonQuery();
        return path;
    }

    /// <summary>Add the per-run tables to a database that already exists, for a test that needs both shapes.</summary>
    public static void AddTo(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Ddl;
        command.ExecuteNonQuery();
    }
}
