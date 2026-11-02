//! Run-set selection and Python hand-off for dose-response statistics.
//!
//! Numerical formulas live in `python/fern_analysis/titration_statistics.py`.
//! This module selects completed Registry rows, freezes their exact scalar
//! fields and timing events, then asks Go Home to admit Python's result.

use std::path::PathBuf;

use clap::Args;
use fern_json_contract::JsonContract;
use serde::Deserialize;
use serde_json::{Value, json};

use fern_run::csharp_registry;
use fern_run::db::{RunEventRow, RunRow};

use crate::analysis_contract::{DynamicArtifactStatistic, execute_dynamic_artifact_statistic};
use crate::{Fallible, invalid_input, write_file_durably};

pub type RecipeHashResolver = fn(&str) -> Fallible<String>;

#[derive(Deserialize)]
struct StatisticsRunsReport { schema: String, runs: Vec<RunRow> }

#[derive(Deserialize)]
struct StatisticsEventsReport { schema: String, events: Vec<RunEventRow> }

#[derive(Debug, Args)]
pub struct ComputeRecipeTitrationStatistics {
    #[arg(long)]
    db: PathBuf,
    #[arg(long)]
    recipe: String,
    #[arg(long, default_value_t = 2.0)]
    failure_threshold: f64,
    #[arg(long, default_value = ".")]
    root: PathBuf,
    #[arg(long, default_value = "results/paper_campaign/lifecycle.db")]
    registry: PathBuf,
    #[arg(long)]
    python: Option<PathBuf>,
    /// Digest-fixed Registry Home binary; no PATH fallback is allowed.
    #[arg(long)]
    fern_home: PathBuf,
}

#[derive(Debug, Args)]
pub struct ComputeCampaignTitrationStatistics {
    #[arg(long)]
    db: PathBuf,
    #[arg(long)]
    campaign_id: i64,
    #[arg(long, default_value_t = 2.0)]
    failure_threshold: f64,
    #[arg(long, default_value = ".")]
    root: PathBuf,
    #[arg(long, default_value = "results/paper_campaign/lifecycle.db")]
    registry: PathBuf,
    #[arg(long)]
    python: Option<PathBuf>,
    /// Digest-fixed Registry Home binary; no PATH fallback is allowed.
    #[arg(long)]
    fern_home: PathBuf,
}

pub fn try_compute_recipe_titration_statistics(
    args: ComputeRecipeTitrationStatistics,
    resolve_recipe_hash: RecipeHashResolver,
) -> Fallible<()> {
    validate_failure_threshold(args.failure_threshold)?;
    let recipe_hash = resolve_recipe_hash(&args.recipe)?;
    let report: StatisticsRunsReport = csharp_registry::run_json(
        "list-statistics-runs-run-data",
        &args.db,
        &json!({ "recipe_hash": recipe_hash, "campaign_id": null }),
    )
    .map_err(invalid_input)?;
    if report.schema != "fern.run_list.v1" {
        return Err(invalid_input("C# statistics Run reader returned a changed schema"));
    }
    let rows = report.runs;
    if rows.is_empty() {
        return Err(invalid_input(format!(
            "no completed runs carry recipe_hash {recipe_hash} for recipe {:?}",
            args.recipe
        )));
    }
    let query = json!({
        "select": "recipe",
        "recipe_name": args.recipe,
        "recipe_hash": recipe_hash,
        "status": "done",
    });
    compute_selected_titration_statistics(
        args.db,
        rows,
        query,
        args.failure_threshold,
        args.root,
        args.registry,
        args.python,
        args.fern_home,
    )
}

pub fn try_compute_campaign_titration_statistics(args: ComputeCampaignTitrationStatistics) -> Fallible<()> {
    validate_failure_threshold(args.failure_threshold)?;
    let report: StatisticsRunsReport = csharp_registry::run_json(
        "list-statistics-runs-run-data",
        &args.db,
        &json!({ "recipe_hash": null, "campaign_id": args.campaign_id }),
    )
    .map_err(invalid_input)?;
    if report.schema != "fern.run_list.v1" {
        return Err(invalid_input("C# statistics Run reader returned a changed schema"));
    }
    let rows = report.runs;
    if rows.is_empty() {
        return Err(invalid_input(format!(
            "no completed runs carry campaign id {}",
            args.campaign_id
        )));
    }
    let query = json!({
        "select": "campaign",
        "campaign_id": args.campaign_id,
        "status": "done",
    });
    compute_selected_titration_statistics(
        args.db,
        rows,
        query,
        args.failure_threshold,
        args.root,
        args.registry,
        args.python,
        args.fern_home,
    )
}

fn validate_failure_threshold(value: f64) -> Fallible<()> {
    if !value.is_finite() || value <= 0.0 {
        return Err(invalid_input("failure threshold must be finite and greater than zero"));
    }
    Ok(())
}

fn run_value(row: &RunRow) -> Value {
    json!({
        "run_hash": row.run_hash,
        "dataset": row.dataset,
        "model_type": row.model_type,
        "pred_len": row.pred_len,
        "noise_sigma": row.noise_sigma,
        "mse": row.mse,
        "mae": row.mae,
        "crps": row.crps,
        "swd": row.swd,
        "ept": row.ept,
    })
}

fn event_value(event: &RunEventRow) -> Value {
    json!({
        "run_hash": event.run_hash,
        "detail_json": event.detail_json,
    })
}

#[allow(clippy::too_many_arguments)]
fn compute_selected_titration_statistics(
    database: PathBuf,
    rows: Vec<RunRow>,
    query: Value,
    failure_threshold: f64,
    root: PathBuf,
    registry: PathBuf,
    python: Option<PathBuf>,
    fern_home: PathBuf,
) -> Fallible<()> {
    let root = std::fs::canonicalize(root)?;
    let mut run_hashes = rows.iter().filter_map(|row| row.run_hash.clone()).collect::<Vec<_>>();
    run_hashes.sort();
    let report: StatisticsEventsReport = csharp_registry::run_json(
        "list-statistics-worker-exit-run-data",
        &database,
        &json!({ "run_hashes": &run_hashes }),
    )
    .map_err(invalid_input)?;
    if report.schema != "fern.run_event_list.v1" {
        return Err(invalid_input("C# statistics event reader returned a changed schema"));
    }
    let events = report.events;
    let input = json!({
        "schema": "fern.titration_statistics_input.v1",
        "query": query,
        "run_hashes": run_hashes,
        "failure_threshold": failure_threshold,
        "rows": rows.iter().map(run_value).collect::<Vec<_>>(),
        "events": events.iter().map(event_value).collect::<Vec<_>>(),
    });
    let input_bytes = serde_json::to_vec_pretty(&input)?;
    let directory = root.join("results/paper_campaign/runtime-statistics");
    std::fs::create_dir_all(&directory)?;
    let input_path = directory.join(format!(
        "titration-statistics-{}.input.json",
        &crate::sha256_hex(&input_bytes)[..16]
    ));
    write_file_durably(&input_path, &[input_bytes, b"\n".to_vec()].concat())?;
    let receipt = execute_dynamic_artifact_statistic(DynamicArtifactStatistic {
        root,
        name: "titration-statistics".to_string(),
        analysis_schema: "fern.titration_statistics.v1".to_string(),
        kind: "titration_summary".to_string(),
        generator: "fern_analysis.titration_statistics".to_string(),
        implementation_sources: vec![
            "python/fern_analysis/titration_statistics.py".to_string(),
            "python/fern_analysis/receipt.py".to_string(),
            "uv.lock".to_string(),
            "analysis_declarations/runtime/default-venv.json".to_string(),
        ],
        parameters: json!({
            "failure_threshold": failure_threshold,
            "scalar_metrics": ["mse", "mae", "crps", "swd", "ept"],
        }),
        input_path,
        input_schema: "fern.titration_statistics_input.v1".to_string(),
        additional_input_paths: Vec::new(),
        output_schema: "fern.titration_statistics.v1".to_string(),
        registry_path: registry,
        python,
        fern_home,
    })?;
    println!("{}", receipt.checked_pretty_json()?);
    Ok(())
}
