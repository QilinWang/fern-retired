//! Run-set selection and Python hand-off for interval calibration.
//!
//! SciPy/NumPy own all coverage, PIT, Wilson, chi-square, and sample-region
//! calculations. Rust resolves run identity and asks the execution-side Serie
//! owner to reopen durable ScientificCase, materialized-cell, and forecast
//! inputs. The legacy Serie bank-regeneration bridge is deliberately refused.

use std::path::PathBuf;

use clap::Args;
use serde::Deserialize;
use serde_json::json;

use fern_run::csharp_registry;
use fern_run::db::RunRow;

use crate::{Fallible, invalid_input};

pub type RecipeHashResolver = fn(&str) -> Fallible<String>;

#[derive(Deserialize)]
struct StatisticsRunsReport { schema: String, runs: Vec<RunRow> }

#[derive(Debug, Args)]
pub struct ComputeRunSetIntervalCalibration {
    #[arg(long)]
    db: PathBuf,
    #[arg(long)]
    recipe: Option<String>,
    #[arg(long)]
    campaign_id: Option<i64>,
    #[arg(long, default_value_t = 0.05)]
    epsilon: f64,
    #[arg(long = "level")]
    levels: Vec<f64>,
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

pub fn try_compute_run_set_interval_calibration(
    args: ComputeRunSetIntervalCalibration,
    resolve_recipe_hash: RecipeHashResolver,
) -> Fallible<()> {
    if !args.epsilon.is_finite() || args.epsilon <= 0.0 {
        return Err(invalid_input("epsilon must be finite and greater than zero"));
    }
    let mut levels = if args.levels.is_empty() {
        vec![0.5, 0.8, 0.9, 0.95]
    } else {
        args.levels
    };
    if levels
        .iter()
        .any(|level| !level.is_finite() || *level <= 0.0 || *level >= 1.0)
    {
        return Err(invalid_input("coverage levels must be finite values in (0, 1)"));
    }
    levels.sort_by(f64::total_cmp);
    levels.dedup();
    let (query, mut rows) = match (&args.recipe, args.campaign_id) {
        (Some(recipe), None) => {
            let recipe_hash = resolve_recipe_hash(recipe)?;
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
            (
                json!({"select": "recipe", "recipe_name": recipe, "recipe_hash": recipe_hash, "status": "done"}),
                rows,
            )
        }
        (None, Some(campaign_id)) => {
            let report: StatisticsRunsReport = csharp_registry::run_json(
                "list-statistics-runs-run-data",
                &args.db,
                &json!({ "recipe_hash": null, "campaign_id": campaign_id }),
            )
            .map_err(invalid_input)?;
            if report.schema != "fern.run_list.v1" {
                return Err(invalid_input("C# statistics Run reader returned a changed schema"));
            }
            let rows = report.runs;
            (
                json!({"select": "campaign", "campaign_id": campaign_id, "status": "done"}),
                rows,
            )
        }
        _ => return Err(invalid_input("pass exactly one of --recipe or --campaign-id")),
    };
    if rows.is_empty() {
        return Err(invalid_input("the selected run set has no completed runs"));
    }
    rows.sort_by(|left, right| left.run_hash.cmp(&right.run_hash));
    let _deferred_handoff_fields = (
        query,
        rows,
        levels,
        args.root,
        args.registry,
        args.python,
        args.fern_home,
    );
    Err(invalid_input(
        "run-set interval calibration is not wired: selected done rows do not carry typed durable materialized-cell, ScientificCase manifest+digest, and forecast artifact bindings; refusing legacy serie_rs bank regeneration",
    ))
}
