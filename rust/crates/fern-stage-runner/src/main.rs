//! Unprivileged checker for Fern Stage returns.
//!
//! This binary does not run scientific kernels, open a database, hold a
//! StagePermit, accept a Coordinate, release a consumer reference, or delete
//! files. It reopens a runner request, report, and output bytes under the
//! Attempt workspace and writes the checked scientific receipt.

use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};

use clap::{Parser, Subcommand};
use fern_artifact::{
    CheckedArtifactV1, REPORT_FILE, STAGE_RUNNER_CAPABILITY_SCHEMA, STAGE_RUNNER_PROTOCOL, StageRunnerCapabilityV1,
    StageRunnerOutcomeV1, StageRunnerReportV1, StageRunnerRequestV1, check_stage_runner_report,
    require_workspace_relative, stage_implementation_digest,
};
use fern_experiment_types::{Sha256Digest, StageRunnerV1};

const ENTRYPOINT: &str = "fern-stage-runner";
const HANDLERS: &[&str] = &["fern.stage.return.check"];
const RECEIPT_FILE: &str = "report/stage_receipt.v1.json";

#[derive(Parser)]
#[command(name = "fern-stage-runner", about = "Unprivileged Fern Stage return checker")]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    /// Print the live capability receipt for this binary.
    Probe,
    /// Reopen one runner report and its output bytes into a scientific receipt.
    CheckReturn {
        /// Path to the Home-issued stage_runner_request.v1.json.
        #[arg(long)]
        request: PathBuf,
        /// Attempt workspace containing the runner report and outputs.
        #[arg(long)]
        workspace: PathBuf,
    },
}

fn main() {
    if let Err(error) = try_run() {
        eprintln!("fern-stage-runner failed: {error}");
        std::process::exit(1);
    }
}

fn try_run() -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    match Cli::parse().command {
        Command::Probe => {
            let capability = capability_receipt()?;
            println!("{}", serde_json::to_string_pretty(&capability)?);
            Ok(())
        }
        Command::CheckReturn { request, workspace } => check_return(&request, &workspace),
    }
}

fn capability_receipt() -> Result<StageRunnerCapabilityV1, Box<dyn std::error::Error + Send + Sync>> {
    Ok(StageRunnerCapabilityV1 {
        schema: STAGE_RUNNER_CAPABILITY_SCHEMA.to_string(),
        protocol_version: STAGE_RUNNER_PROTOCOL.to_string(),
        implementation_digest: this_binary_implementation_digest()?,
        handlers: HANDLERS.iter().map(|handler| handler.to_string()).collect(),
        entrypoint: ENTRYPOINT.to_string(),
    })
}

fn this_binary_implementation_digest() -> Result<Sha256Digest, Box<dyn std::error::Error + Send + Sync>> {
    let bytes = fs::read(std::env::current_exe()?)?;
    let artifact = Sha256Digest::from_bytes(&bytes);
    Ok(stage_implementation_digest(
        StageRunnerV1::RustBinary,
        ENTRYPOINT,
        HANDLERS,
        &artifact,
    ))
}

fn check_return(request_path: &Path, workspace: &Path) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let request: StageRunnerRequestV1 = fern_json_contract::checked_from_slice(&fs::read(request_path)?)?;
    request.validate()?;
    let report_path = workspace.join(REPORT_FILE);
    refuse_symlink(&report_path)?;
    let report: StageRunnerReportV1 = fern_json_contract::checked_from_slice(&fs::read(&report_path)?)?;
    report.validate()?;
    let success = match &report.outcome {
        StageRunnerOutcomeV1::Success(success) => success,
        StageRunnerOutcomeV1::Failure(failure) => {
            return Err(format!("runner failed with {}: {}", failure.code, failure.message).into());
        }
    };
    let mut checked_outputs = BTreeMap::<String, CheckedArtifactV1>::new();
    for (port, relative_path) in &success.output_paths {
        require_workspace_relative("outputs", relative_path).map_err(|error| format!("output {port}: {error}"))?;
        let path = workspace.join(relative_path);
        refuse_symlink(&path)?;
        let bytes = fs::read(&path)?;
        let reported = success
            .result
            .outputs
            .get(port)
            .ok_or_else(|| format!("runner report has no artifact for output port {port}"))?;
        let checked = reported.check_reopened_payloads(&bytes, &bytes)?;
        checked_outputs.insert(port.clone(), checked);
    }
    let receipt = check_stage_runner_report(&request, &report, checked_outputs)?;
    let receipt_path = workspace.join(RECEIPT_FILE);
    if let Some(parent) = receipt_path.parent() {
        fs::create_dir_all(parent)?;
    }
    let tmp = receipt_path.with_extension("json.partial");
    fs::write(&tmp, serde_json::to_vec_pretty(receipt.reported())?)?;
    fs::rename(tmp, receipt_path)?;
    Ok(())
}

fn refuse_symlink(path: &Path) -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let metadata = fs::symlink_metadata(path)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        return Err(format!("{} must be a regular file", path.display()).into());
    }
    Ok(())
}
