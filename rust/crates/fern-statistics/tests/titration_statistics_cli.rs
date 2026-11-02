use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::OnceLock;
use std::time::{SystemTime, UNIX_EPOCH};

use fern_run::db::{Metrics, RunRegistration};
use serde_json::{Value, json};

fn workspace_root() -> &'static Path {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .and_then(Path::parent)
        .expect("fern-statistics lives under the workspace crates directory")
}

fn temp_case_dir(name: &str) -> PathBuf {
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
    let dir = std::env::temp_dir().join(format!("fern_statistics_{name}_{}_{}", std::process::id(), nanos));
    fs::create_dir_all(&dir).unwrap();
    dir
}

fn everyday_python() -> PathBuf {
    let python = workspace_root()
        .parent()
        .expect("the workspace has a parent directory")
        .join(".venv/bin/python");
    assert!(
        python.is_file(),
        "the declared everyday Fern interpreter is missing at {}",
        python.display()
    );
    python
}

fn go_home_binary() -> &'static PathBuf {
    static HOME: OnceLock<PathBuf> = OnceLock::new();
    HOME.get_or_init(|| {
        let output = std::env::temp_dir().join(format!("fern-home-stat-cli-tests-{}", std::process::id()));
        let status = Command::new("go")
            .args(["build", "-o"])
            .arg(&output)
            .arg("./cmd/fern-home")
            .env("GOCACHE", "/tmp/fern-control-go-cache")
            .current_dir(workspace_root().join("tools/fern-control"))
            .status()
            .expect("build fern-home CLI test writer");
        assert!(status.success(), "go build ./cmd/fern-home failed");
        output
    })
}

fn migrate_registry(database: &Path) {
    let output = Command::new(go_home_binary())
        .args(["registry-apply-migrations", "--registry"])
        .arg(database)
        .output()
        .expect("run fern-home registry-apply-migrations fixture");
    assert!(
        output.status.success(),
        "registry migration fixture failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    let receipt: Value = serde_json::from_slice(&output.stdout).expect("migration receipt is JSON");
    assert_eq!(receipt["schema"], "fern.registry_migrations.v1");
}

fn enroll_run_data(database: &Path, registration: &RunRegistration) {
    let receipt: Value =
        fern_run::csharp_registry::run_json("enroll-run-data", database, registration).unwrap();
    assert_eq!(receipt["schema"], "fern.run_enrollment_write.v1");
    assert_eq!(receipt["run_hash"], registration.run_hash);
}

fn record_run_event_data(database: &Path, run_hash: &str, event: &str, detail_json: Option<&str>) {
    let receipt: Value = fern_run::csharp_registry::run_json(
        "record-run-event-data",
        database,
        &json!({
            "run_hash": run_hash,
            "event": event,
            "detail_json": detail_json,
        }),
    )
    .unwrap();
    assert_eq!(receipt["schema"], "fern.run_event_write.v1");
    assert_eq!(receipt["event"]["run_hash"], run_hash);
    assert_eq!(receipt["event"]["event"], event);
}

fn set_run_lifecycle_fixture_data(
    database: &Path,
    run_hash: &str,
    state: &str,
    metrics: Option<&Metrics>,
    detail: Option<&str>,
) -> usize {
    let receipt: Value = fern_run::csharp_registry::run_json(
        "set-run-lifecycle-fixture-data",
        database,
        &json!({
            "run_hash": run_hash,
            "state": state,
            "metrics": metrics,
            "detail": detail,
        }),
    )
    .unwrap();
    assert_eq!(receipt["schema"], "fern.run_lifecycle_fixture.v1");
    assert_eq!(receipt["run_hash"], run_hash);
    assert_eq!(receipt["state"], state);
    usize::try_from(receipt["rows_changed"].as_u64().unwrap()).unwrap()
}

fn run_fern_stat_json(args: &[&str]) -> Value {
    let output = Command::new(env!("CARGO_BIN_EXE_fern-stat"))
        .args(args)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "fern-stat failed\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    serde_json::from_slice(&output.stdout).unwrap()
}

fn sample_registration(campaign_id: i64, artifact_dir: PathBuf, run_hash: &str) -> RunRegistration {
    RunRegistration {
        schema: RunRegistration::SCHEMA_ID.to_string(),
        dataset: "lorenz".to_string(),
        dataset_id: "lorenz_traj0".to_string(),
        model_type: "fern".to_string(),
        seed: 42,
        pred_len: 64,
        seq_len: 32,
        noise_sigma: 0.1,
        trajectory_draw_ordinal: 0,
        noise_draw_ordinal: 0,
        trajectory_seed_base: 42,
        noise_seed_base: 1000,
        trajectory_draw_seed: 2684470948,
        noise_draw_seed: 1484808508,
        config_json: Some("{\"model_type\":\"fern\"}".to_string()),
        config_hash: Some("cfg123".to_string()),
        git_sha: String::new(),
        code_fingerprint_json: None,
        template_id: Some(campaign_id),
        source: "test".to_string(),
        config_name: Some("lorenz".to_string()),
        recipe_name: None,
        recipe_hash: None,
        target_hash: None,
        run_note: None,
        invocation_json: None,
        run_hash: run_hash.to_string(),
        n_features: Some(3),
        artifact_dir,
    }
}

#[test]
fn cli_interval_calibration_refuses_missing_durable_bindings_before_launch_or_request_write() {
    let dir = temp_case_dir("interval_calibration_durable_binding_refusal");
    let db_path = dir.join("fern.db");
    let run_hash = "calibrationrefuse1";
    {
        migrate_registry(&db_path);
        enroll_run_data(
            &db_path,
            &sample_registration(51, dir.join("legacy-artifacts"), run_hash),
        );
        assert_eq!(
            set_run_lifecycle_fixture_data(&db_path, run_hash, "done", Some(&Metrics::default()), None),
            1
        );
    }

    let output = Command::new(env!("CARGO_BIN_EXE_fern-stat"))
        .args([
            "run-set-compute-interval-calibration",
            "--db",
            db_path.to_str().unwrap(),
            "--campaign-id",
            "51",
            "--root",
            dir.to_str().unwrap(),
            "--registry",
            db_path.to_str().unwrap(),
            "--python",
            dir.join("must-not-launch-python").to_str().unwrap(),
            "--fern-home",
            dir.join("must-not-launch-fern-home").to_str().unwrap(),
        ])
        .output()
        .unwrap();

    assert!(!output.status.success());
    assert!(output.stdout.is_empty());
    assert_eq!(
        String::from_utf8(output.stderr).unwrap(),
        "run-set-compute-interval-calibration failed: run-set interval calibration is not wired: selected done rows do not carry typed durable materialized-cell, ScientificCase manifest+digest, and forecast artifact bindings; refusing legacy serie_rs bank regeneration\n"
    );
    assert!(
        !dir.join("results/paper_campaign/runtime-statistics").exists(),
        "typed refusal must happen before the cohort request directory is created"
    );
}

/// Re-hash the six stored input columns of an analyses row and assert the
/// result equals its analysis_hash — the content-address round trip.
fn assert_analysis_preimage_matches_hash(db_path: &Path, analysis_hash: &str) -> fern_run::db::AnalysisRow {
    let receipt: serde_json::Value = fern_run::csharp_registry::run_json(
        "load-analysis-by-hash-data",
        db_path,
        &serde_json::json!({ "analysis_hash": analysis_hash }),
    )
    .unwrap();
    assert_eq!(receipt["schema"], "fern.analysis_read.v1");
    assert_eq!(receipt["requested_analysis_hash"], analysis_hash);
    let row: fern_run::db::AnalysisRow = serde_json::from_value(receipt["analysis"].clone())
        .expect("C# RunData analysis receipt must contain a typed row");
    let rehashed = fern_run::db::analysis_hash_16hex(
        &row.schema,
        &row.kind,
        &row.query_json,
        &row.run_hashes_json,
        &row.scoring_fingerprint,
        &row.params_json,
    )
    .unwrap();
    assert_eq!(rehashed, analysis_hash);
    row
}

#[test]
fn cli_campaign_compute_titration_statistics_mints_content_addressed_analysis() {
    let dir = temp_case_dir("summarize_campaign");
    let db_path = dir.join("fern.db");
    let db = db_path.to_str().unwrap();
    let root = workspace_root().to_str().unwrap();
    let python = everyday_python();
    let python = python.to_str().unwrap();
    let fern_home = go_home_binary().to_str().unwrap();

    // Synthetic population: one campaign, 2 sigmas x 2 seeds with hand-chosen
    // mse values, enrolled and completed through the production Registry API.
    //   sigma 0.0: mse {1.0, 3.0} → mean 2.0, sample std sqrt(2)
    //   sigma 1.0: mse {3.0, 5.0} → mean 4.0
    let campaign_id = 41;
    let population: [(f64, i64, f64, &str); 4] = [
        (0.0, 42, 1.0, "sumcamp000000001"),
        (0.0, 43, 3.0, "sumcamp000000002"),
        (1.0, 42, 3.0, "sumcamp000000003"),
        (1.0, 43, 5.0, "sumcamp000000004"),
    ];
    {
        migrate_registry(&db_path);
        for (sigma, seed, mse, run_hash) in population {
            let artifact_dir = dir.join("runs").join(run_hash);
            let mut registration = sample_registration(campaign_id, artifact_dir, run_hash);
            registration.noise_sigma = sigma;
            registration.seed = seed;
            enroll_run_data(&db_path, &registration);

            let metrics = Metrics {
                mse: Some(mse),
                ..Metrics::default()
            };
            assert_eq!(
                set_run_lifecycle_fixture_data(&db_path, run_hash, "done", Some(&metrics), None),
                1
            );
        }
    }

    // Hand-chosen worker_exited durations for the timing-budget block:
    // {100, 200, 300, 400} ms → median 250, mean 250, max 400.
    {
        for (duration_ms, (_, _, _, run_hash)) in [100, 200, 300, 400].into_iter().zip(population) {
            let detail = format!(r#"{{"exit_code":0,"duration_ms":{duration_ms}}}"#);
            record_run_event_data(&db_path, run_hash, "worker_exited", Some(&detail));
        }
    }

    let analysis = run_fern_stat_json(&[
        "campaign-compute-titration-statistics",
        "--db",
        db,
        "--campaign-id",
        "41",
        "--root",
        root,
        "--registry",
        db,
        "--python",
        python,
        "--fern-home",
        fern_home,
    ]);
    assert_eq!(analysis["schema"], "fern.checked_analysis_receipt.v1");
    assert_eq!(analysis["analysis_schema"], "fern.titration_statistics.v1");
    assert_eq!(analysis["kind"], "titration_summary");
    let analysis_hash = analysis["analysis_hash"].as_str().unwrap().to_string();
    assert_eq!(analysis_hash.len(), 16);

    // The row exists in the DB and its stored preimage re-hashes to
    // analysis_hash (the result is NOT part of the hash).
    let row = assert_analysis_preimage_matches_hash(&db_path, &analysis_hash);

    // Registry identity is now the exact typed cohort artifact Python opened.
    let query: Value = serde_json::from_str(&row.query_json).unwrap();
    assert_eq!(query["source"], "artifacts");
    assert_eq!(query["inputs"].as_array().unwrap().len(), 1);
    assert_eq!(query["inputs"][0]["schema_id"], "fern.titration_statistics_input.v1");
    let run_hashes: Vec<String> = serde_json::from_str(&row.run_hashes_json).unwrap();
    assert!(run_hashes.is_empty());

    // The threshold is data, not code: params_json carries it.
    let params: Value = serde_json::from_str(&row.params_json).unwrap();
    assert_eq!(params["failure_threshold"].as_f64().unwrap(), 2.0);

    // Hand-computed finding, exact.
    let stored: Value = serde_json::from_str(&row.result_json).unwrap();
    let result = &stored["summary"];
    assert_eq!(result["query"]["select"], "campaign");
    assert_eq!(result["query"]["campaign_id"], 41);
    assert_eq!(
        result["run_hashes"],
        json!([
            "sumcamp000000001",
            "sumcamp000000002",
            "sumcamp000000003",
            "sumcamp000000004"
        ])
    );
    assert_eq!(result["run_count"], 4);
    let cells = result["cells"].as_array().unwrap();
    assert_eq!(cells.len(), 2);
    assert_eq!(cells[0]["noise_sigma"].as_f64().unwrap(), 0.0);
    assert_eq!(cells[0]["n_runs"], 2);
    assert_eq!(cells[0]["metrics"]["mse"]["mean"].as_f64().unwrap(), 2.0);
    assert_eq!(cells[0]["metrics"]["mse"]["std"].as_f64().unwrap(), 2.0_f64.sqrt());
    assert_eq!(cells[0]["metrics"]["mse"]["count"], 2);
    assert_eq!(cells[0]["metrics"]["crps"]["count"], 0);
    assert!(cells[0]["metrics"]["crps"]["mean"].is_null());
    assert_eq!(cells[1]["noise_sigma"].as_f64().unwrap(), 1.0);
    assert_eq!(cells[1]["metrics"]["mse"]["mean"].as_f64().unwrap(), 4.0);

    // One curve (only mse has values): means [2, 4] over sigmas [0, 1].
    // Degradation AUC = trapezoid / span = ((1-0)·(2+4)/2) / 1 = 3.
    // Failure dose: boundary 2.0 × 2.0 = 4.0, first reached at sigma 1.0.
    let curves = result["curves"].as_array().unwrap();
    assert_eq!(curves.len(), 1);
    assert_eq!(curves[0]["metric"], "mse");
    assert_eq!(curves[0]["sigmas"], serde_json::json!([0.0, 1.0]));
    assert_eq!(curves[0]["means"], serde_json::json!([2.0, 4.0]));
    assert_eq!(curves[0]["degradation_auc"].as_f64().unwrap(), 3.0);
    assert_eq!(curves[0]["failure_dose"].as_f64().unwrap(), 1.0);

    // Durations block: one (model_type, dataset) group over the run set.
    let durations = result["durations"].as_array().unwrap();
    assert_eq!(durations.len(), 1);
    assert_eq!(durations[0]["model_type"], "fern");
    assert_eq!(durations[0]["dataset"], "lorenz");
    assert_eq!(durations[0]["n_events"], 4);
    assert_eq!(durations[0]["median_ms"].as_f64().unwrap(), 250.0);
    assert_eq!(durations[0]["mean_ms"].as_f64().unwrap(), 250.0);
    assert_eq!(durations[0]["max_ms"].as_f64().unwrap(), 400.0);

    // Determinism: a second invocation with identical inputs mints the SAME
    // analysis_hash, and no second row appears.
    let again = run_fern_stat_json(&[
        "campaign-compute-titration-statistics",
        "--db",
        db,
        "--campaign-id",
        "41",
        "--root",
        root,
        "--registry",
        db,
        "--python",
        python,
        "--fern-home",
        fern_home,
    ]);
    assert_eq!(again["analysis_hash"].as_str(), Some(analysis_hash.as_str()));
    let listed: serde_json::Value = fern_run::csharp_registry::run_json(
        "list-analysis-data",
        &db_path,
        &serde_json::json!({
            "kind_filter": "titration_summary",
            "query_contains": null,
        }),
    )
    .unwrap();
    assert_eq!(listed["schema"], "fern.analysis_list.v1");
    let listed: Vec<fern_run::db::AnalysisRow> =
        serde_json::from_value(listed["analyses"].clone()).unwrap();
    assert_eq!(listed.len(), 1);

    fs::remove_dir_all(dir).ok();
}

#[test]
fn cli_recipe_compute_titration_statistics_resolves_catalog_recipe_hash() {
    let dir = temp_case_dir("summarize_recipe");
    let db_path = dir.join("fern.db");
    let db = db_path.to_str().unwrap();
    let root = workspace_root().to_str().unwrap();
    let python = everyday_python();
    let python = python.to_str().unwrap();
    let fern_home = go_home_binary().to_str().unwrap();
    let recipe = "rossler_base_fern_huber_p70_20_10_none_bs95_titration_h64_h192_sigmas";

    // Seed the published C# catalog hash. The command below must resolve this
    // same identity through its real typed C# child before it can select the
    // row; the C# full-catalog golden pins this literal to the old executable.
    let recipe_hash = "79157fc57424f2c6".to_string();
    let run_hash = "sumrecipe0000001".to_string();
    {
        migrate_registry(&db_path);
        let mut registration = sample_registration(35, dir.join("runs").join(&run_hash), &run_hash);
        registration.dataset = "rossler".to_string();
        registration.dataset_id = "rossler_traj0".to_string();
        registration.config_name = Some("rossler".to_string());
        registration.recipe_name = Some(recipe.to_string());
        registration.recipe_hash = Some(recipe_hash.clone());
        enroll_run_data(&db_path, &registration);
        let metrics = Metrics {
            mse: Some(0.5),
            ..Metrics::default()
        };
        assert_eq!(
            set_run_lifecycle_fixture_data(&db_path, &run_hash, "done", Some(&metrics), None),
            1
        );
        record_run_event_data(
            &db_path,
            &run_hash,
            "worker_exited",
            Some(r#"{"exit_code":0,"duration_ms":125}"#),
        );
    }

    let analysis = run_fern_stat_json(&[
        "recipe-compute-titration-statistics",
        "--db",
        db,
        "--recipe",
        recipe,
        "--root",
        root,
        "--registry",
        db,
        "--python",
        python,
        "--fern-home",
        fern_home,
    ]);
    assert_eq!(analysis["schema"], "fern.checked_analysis_receipt.v1");
    assert_eq!(analysis["analysis_schema"], "fern.titration_statistics.v1");
    assert_eq!(analysis["kind"], "titration_summary");
    let analysis_hash = analysis["analysis_hash"].as_str().unwrap().to_string();
    let row = assert_analysis_preimage_matches_hash(&db_path, &analysis_hash);

    // The Registry identity is the exact cohort artifact Python opened.
    let query: Value = serde_json::from_str(&row.query_json).unwrap();
    assert_eq!(query["source"], "artifacts");
    assert_eq!(query["inputs"].as_array().unwrap().len(), 1);
    assert_eq!(query["inputs"][0]["schema_id"], "fern.titration_statistics_input.v1");
    let run_hashes: Vec<String> = serde_json::from_str(&row.run_hashes_json).unwrap();
    assert!(run_hashes.is_empty());

    // One run at one sigma: no sigma span, no sigma=0 crossing to report.
    let stored: Value = serde_json::from_str(&row.result_json).unwrap();
    let result = &stored["summary"];
    assert_eq!(result["query"]["select"], "recipe");
    assert_eq!(result["query"]["recipe_name"].as_str(), Some(recipe));
    assert_eq!(result["query"]["recipe_hash"].as_str(), Some(recipe_hash.as_str()));
    assert_eq!(result["run_hashes"], json!([run_hash.clone()]));
    assert_eq!(result["run_count"], 1);
    let cells = result["cells"].as_array().unwrap();
    assert_eq!(cells.len(), 1);
    assert_eq!(cells[0]["dataset"], "rossler");
    assert_eq!(cells[0]["n_runs"], 1);
    assert_eq!(cells[0]["metrics"]["mse"]["mean"].as_f64().unwrap(), 0.5);
    assert!(cells[0]["metrics"]["mse"]["std"].is_null());
    let curves = result["curves"].as_array().unwrap();
    assert_eq!(curves.len(), 1);
    assert!(curves[0]["degradation_auc"].is_null());
    assert!(curves[0]["failure_dose"].is_null());

    // The seeded worker_exited event keeps the duration contract at the same
    // one-event boundary as the former failed-spawn fixture.
    let durations = result["durations"].as_array().unwrap();
    assert_eq!(durations.len(), 1);
    assert_eq!(durations[0]["model_type"], "fern");
    assert_eq!(durations[0]["dataset"], "rossler");
    assert_eq!(durations[0]["n_events"], 1);

    // Determinism holds on the recipe path too.
    let again = run_fern_stat_json(&[
        "recipe-compute-titration-statistics",
        "--db",
        db,
        "--recipe",
        recipe,
        "--root",
        root,
        "--registry",
        db,
        "--python",
        python,
        "--fern-home",
        fern_home,
    ]);
    assert_eq!(again["analysis_hash"].as_str(), Some(analysis_hash.as_str()));

    fs::remove_dir_all(dir).ok();
}
