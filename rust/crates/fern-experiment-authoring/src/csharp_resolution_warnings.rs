//! Typed local-process adapters for C#-owned pure V2 authoring projections.
//!
//! `fern-experiment-authoring` intentionally cannot depend on `fern_run`:
//! authoring decides whether a draft becomes a Plan, while `fern_run` owns the
//! runtime path. This module is therefore the one shared authoring adapter to
//! the same C# Experiment operation that runtime callers use.

use std::io::{Read, Write};
use std::path::PathBuf;
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

use fern_recipes::{CapabilityGeneratorKindV2, HandlerCapabilityV2, ResolvedExperimentArmV2};
use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::ExperimentCompositionError;

const PROCESS_DEADLINE: Duration = Duration::from_secs(30);
const PROCESS_POLL_INTERVAL: Duration = Duration::from_millis(25);

#[derive(Serialize)]
struct ArmRequest<'a> {
    name: &'a str,
    evaluator_parameters: &'a Value,
    trainer_parameters: &'a Value,
}

#[derive(Serialize)]
struct Request<'a> {
    schema: &'static str,
    arms: Vec<ArmRequest<'a>>,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct Receipt {
    schema: String,
    arm_names: Vec<String>,
    warnings: Vec<String>,
}

#[derive(Serialize)]
struct StaticCapabilityRequest {
    schema: &'static str,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct StaticCapabilityReceipt {
    schema: String,
    generator: StaticCapabilityGenerator,
    handlers: Vec<HandlerCapabilityV2>,
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct StaticCapabilityGenerator {
    kind: CapabilityGeneratorKindV2,
    provider: String,
    source_paths: Vec<String>,
}

/// The untrusted child receipt transported to the implementation-bundle
/// authoring transaction. `implementation_bundle` validates the generator
/// identity against the frozen source observation before it records a digest.
pub(crate) struct StaticCapabilityPublication {
    pub(crate) source_paths: Vec<String>,
    pub(crate) handlers: Vec<HandlerCapabilityV2>,
}

/// Resolve warnings for one complete ordered arm set. The C# operation is
/// pure: a refusal returns before a Plan, component file, Registry, Scheduler,
/// Stage, Site, artifact, worker, or durable refusal row can be changed.
pub(crate) fn resolution_warnings_via_csharp(
    arms: &[(&str, &ResolvedExperimentArmV2)],
) -> Result<Vec<String>, ExperimentCompositionError> {
    let expected_arm_names = arms.iter().map(|(name, _)| (*name).to_string()).collect::<Vec<_>>();
    let request = Request {
        schema: "fern.experiment_resolution_warnings.request.v1",
        arms: arms
            .iter()
            .map(|(name, arm)| ArmRequest {
                name,
                evaluator_parameters: &arm.manifest.components.recipe.evaluator.parameters,
                trainer_parameters: &arm.manifest.components.recipe.trainer.parameters,
            })
            .collect(),
    };
    let bytes = serde_json::to_vec(&request)
        .map_err(|error| ExperimentCompositionError::new(format!("cannot encode C# warning request: {error}")))?;
    let output = invoke(binary()?, "resolve-experiment-warnings", bytes, "warning")?;
    if !output.status.success() {
        return Err(ExperimentCompositionError::new(format!(
            "resolve-experiment-warnings: {}",
            String::from_utf8_lossy(&output.stderr).trim()
        )));
    }
    let receipt: Receipt = serde_json::from_slice(&output.stdout).map_err(|error| {
        ExperimentCompositionError::new(format!("resolve-experiment-warnings receipt is not JSON: {error}"))
    })?;
    if receipt.schema != "fern.experiment_resolution_warnings.v1" {
        return Err(ExperimentCompositionError::new(format!(
            "resolve-experiment-warnings returned schema {:?}, expected fern.experiment_resolution_warnings.v1",
            receipt.schema
        )));
    }
    if receipt.arm_names != expected_arm_names {
        return Err(ExperimentCompositionError::new(format!(
            "resolve-experiment-warnings changed requested arm names {:?} into {:?}",
            expected_arm_names, receipt.arm_names
        )));
    }
    Ok(receipt.warnings)
}

/// Read C#'s complete static handler publication. This pure child call has no
/// Plan, Registry, Scheduler, Stage, Site, artifact, worker, component-file,
/// or durable refusal mutation; the caller binds its immutable receipt to one
/// separately stable implementation source observation before authoring can
/// return a bundle.
pub(crate) fn static_handler_capabilities_v2_via_csharp(
) -> Result<StaticCapabilityPublication, ExperimentCompositionError> {
    let request = StaticCapabilityRequest {
        schema: "fern.implementation_handler_capability_publication.request.v1",
    };
    let bytes = serde_json::to_vec(&request).map_err(|error| {
        ExperimentCompositionError::new(format!("cannot encode C# static capability request: {error}"))
    })?;
    let output = invoke(
        binary()?,
        "publish-implementation-handler-capabilities",
        bytes,
        "static capability publication",
    )?;
    if !output.status.success() {
        return Err(ExperimentCompositionError::new(format!(
            "publish-implementation-handler-capabilities: {}",
            String::from_utf8_lossy(&output.stderr).trim()
        )));
    }
    let receipt: StaticCapabilityReceipt = serde_json::from_slice(&output.stdout).map_err(|error| {
        ExperimentCompositionError::new(format!(
            "publish-implementation-handler-capabilities receipt is not JSON: {error}"
        ))
    })?;
    if receipt.schema != "fern.implementation_handler_capability_publication.v1" {
        return Err(ExperimentCompositionError::new(format!(
            "publish-implementation-handler-capabilities returned schema {:?}, expected fern.implementation_handler_capability_publication.v1",
            receipt.schema
        )));
    }
    if receipt.generator.kind != CapabilityGeneratorKindV2::CSharpExperiment
        || receipt.generator.provider != "Fern.Experiment"
    {
        return Err(ExperimentCompositionError::new(format!(
            "publish-implementation-handler-capabilities returned generator {:?} from {:?}, expected csharp_experiment Fern.Experiment",
            receipt.generator.kind, receipt.generator.provider
        )));
    }
    if receipt.handlers.is_empty() {
        return Err(ExperimentCompositionError::new(
            "publish-implementation-handler-capabilities returned no handlers",
        ));
    }
    Ok(StaticCapabilityPublication {
        source_paths: receipt.generator.source_paths,
        handlers: receipt.handlers,
    })
}

struct ChildOutput {
    status: std::process::ExitStatus,
    stdout: Vec<u8>,
    stderr: Vec<u8>,
}

fn invoke(
    binary: PathBuf,
    operation: &str,
    input: Vec<u8>,
    boundary: &str,
) -> Result<ChildOutput, ExperimentCompositionError> {
    let mut child = Command::new(binary)
        .arg(operation)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| ExperimentCompositionError::new(format!("cannot start C# Experiment {boundary} child: {error}")))?;
    let mut stdin = child.stdin.take().ok_or_else(|| ExperimentCompositionError::new(format!("C# Experiment {boundary} child has no stdin")))?;
    stdin
        .write_all(&input)
        .map_err(|error| ExperimentCompositionError::new(format!("cannot write C# {boundary} request: {error}")))?;
    drop(stdin);
    let stdout = child.stdout.take().ok_or_else(|| ExperimentCompositionError::new(format!("C# Experiment {boundary} child has no stdout")))?;
    let stderr = child.stderr.take().ok_or_else(|| ExperimentCompositionError::new(format!("C# Experiment {boundary} child has no stderr")))?;
    let stdout_boundary = boundary.to_string();
    let stderr_boundary = boundary.to_string();
    let stdout_reader = std::thread::spawn(move || read_all(stdout, "stdout", &stdout_boundary));
    let stderr_reader = std::thread::spawn(move || read_all(stderr, "stderr", &stderr_boundary));
    let deadline = Instant::now() + PROCESS_DEADLINE;
    let status = loop {
        match child.try_wait().map_err(|error| ExperimentCompositionError::new(format!("cannot poll C# Experiment {boundary} child: {error}")))? {
            Some(status) => break status,
            None if Instant::now() >= deadline => {
                let _ = child.kill();
                let _ = child.wait();
                let _ = stdout_reader.join();
                let _ = stderr_reader.join();
                return Err(ExperimentCompositionError::new(format!(
                    "C# Experiment {boundary} boundary deadline {PROCESS_DEADLINE:?} exhausted"
                )));
            }
            None => std::thread::sleep(PROCESS_POLL_INTERVAL),
        }
    };
    let stdout = stdout_reader
        .join()
        .map_err(|_| ExperimentCompositionError::new(format!("C# Experiment {boundary} stdout reader panicked")))??;
    let stderr = stderr_reader
        .join()
        .map_err(|_| ExperimentCompositionError::new(format!("C# Experiment {boundary} stderr reader panicked")))??;
    Ok(ChildOutput { status, stdout, stderr })
}

fn read_all(mut stream: impl Read, label: &str, boundary: &str) -> Result<Vec<u8>, ExperimentCompositionError> {
    let mut bytes = Vec::new();
    stream
        .read_to_end(&mut bytes)
        .map_err(|error| ExperimentCompositionError::new(format!("cannot read C# Experiment {boundary} {label}: {error}")))?;
    Ok(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;
    use fern_recipes::ExperimentComponentRoleV2;

    #[test]
    fn authoring_caller_receives_the_complete_ordered_csharp_static_capability_publication() {
        let publication = static_handler_capabilities_v2_via_csharp().unwrap();
        assert_eq!(
            publication.handlers.last().map(|handler| (&handler.role, handler.handler.as_str())),
            Some((&ExperimentComponentRoleV2::Evaluator, "resolved_loss"))
        );
        assert_eq!(
            publication
                .handlers
                .iter()
                .rev()
                .nth(1)
                .map(|handler| (&handler.role, handler.handler.as_str())),
            Some((&ExperimentComponentRoleV2::Trainer, "python_training_worker"))
        );
        assert_eq!(
            publication
                .handlers
                .iter()
                .rev()
                .nth(2)
                .map(|handler| (&handler.role, handler.handler.as_str())),
            Some((&ExperimentComponentRoleV2::Loader, "explicit_loader"))
        );
        assert!(publication.handlers[..publication.handlers.len() - 3]
            .iter()
            .all(|handler| handler.role == ExperimentComponentRoleV2::World));
        assert_eq!(
            publication.source_paths,
            [
                "csharp/Fern.Experiment.Cli/Program.cs",
                "csharp/Fern.Experiment/Catalog/recipe_source_capabilities_v1.json",
                "csharp/Fern.Experiment/RecipeCatalogRecords.cs",
                "csharp/Fern.Experiment/V2HandlerCapabilitiesCli.cs",
            ]
            .map(str::to_string)
            .to_vec()
        );
    }
}

fn binary() -> Result<PathBuf, ExperimentCompositionError> {
    if let Ok(path) = std::env::var("FERN_EXPERIMENT") {
        let path = PathBuf::from(path);
        if path.is_file() {
            return Ok(path);
        }
        return Err(ExperimentCompositionError::new(format!(
            "FERN_EXPERIMENT is not a file: {}",
            path.display()
        )));
    }
    if let Some(path) = std::env::var_os("PATH").and_then(|path| {
        std::env::split_paths(&path).find_map(|directory| {
            let candidate = directory.join("fern-experiment");
            candidate.is_file().then_some(candidate)
        })
    }) {
        return Ok(path);
    }
    let manifest_directory = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let root = manifest_directory
        .parent()
        .and_then(|directory| directory.parent())
        .ok_or_else(|| ExperimentCompositionError::new("cannot locate repository root for fern-experiment"))?;
    let debug = root.join("csharp/Fern.Experiment.Cli/bin/Debug/net9.0/fern-experiment");
    let release = root.join("csharp/Fern.Experiment.Cli/bin/Release/net9.0/fern-experiment");
    match (debug.is_file(), release.is_file()) {
        (true, true) => {
            let debug_time = std::fs::metadata(&debug)
                .and_then(|metadata| metadata.modified())
                .map_err(ExperimentCompositionError::from)?;
            let release_time = std::fs::metadata(&release)
                .and_then(|metadata| metadata.modified())
                .map_err(ExperimentCompositionError::from)?;
            Ok(if release_time >= debug_time { release } else { debug })
        }
        (true, false) => Ok(debug),
        (false, true) => Ok(release),
        (false, false) => Err(ExperimentCompositionError::new(format!(
            "fern-experiment is not on PATH and was not built under {}",
            root.join("csharp/Fern.Experiment.Cli/bin").display()
        ))),
    }
}
