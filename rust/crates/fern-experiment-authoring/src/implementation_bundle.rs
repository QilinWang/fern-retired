//! Build one V2 executable implementation identity from observed source bytes
//! and an actual imported Python capability provider.
//!
//! Paths exist only at this authoring boundary.  The resulting bundle binds
//! canonical byte records, the generated C#/Python handler contracts, and a
//! protocol version into one digest; Experiment manifests retain only that
//! digest and the capability-manifest digest.

use std::collections::BTreeSet;
use std::fs;
use std::io::{Read, Write};
use std::path::{Component, Path, PathBuf};
use std::process::{Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};

use fern_experiment_types::ImplementationBundleHashV2;
use fern_json_contract::{JsonContract, checked_from_value};
use fern_recipes::{
    CapabilityGeneratorKindV2, CapabilityGeneratorV2, ExperimentComponentRoleV2, IMPLEMENTATION_BUNDLE_DRAFT_SCHEMA_V2,
    IMPLEMENTATION_BUNDLE_RESOLUTION_SCHEMA_V2, IMPLEMENTATION_CAPABILITIES_SCHEMA_V2, ImplementationBundleDraftV2,
    ImplementationBundleFileV2, ImplementationBundleResolutionV2, ImplementationCapabilitiesV2,
    ImplementationRuntimeV2, MODEL_LINK_RECEIPT_SCHEMA_V1, ModelLinkReceiptV1, PythonCapabilityProbeV2,
    PythonModelLinkProbeV2, PythonModelLinkResultV1,
};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};

use crate::ExperimentCompositionError;
use crate::csharp_resolution_warnings::static_handler_capabilities_v2_via_csharp;
use crate::experiment_composition::{canonical_json_bytes, normalize_capabilities};

const MAX_IMPLEMENTATION_FILE_BYTES: u64 = 32 * 1024 * 1024;
const MAX_IMPLEMENTATION_TOTAL_BYTES: u64 = 256 * 1024 * 1024;
const MAX_PROBE_OUTPUT_BYTES: usize = 4 * 1024 * 1024;
// Importing the real Fern worker also imports Torch and its maintained model
// adapters.  A cold CPU import takes about 18 seconds on the owner machine, so
// the bounded probe allows headroom without becoming an unbounded worker.
const PROBE_TIMEOUT: Duration = Duration::from_secs(45);
const CSHARP_CAPABILITY_SOURCES: [&str; 4] = [
    "csharp/Fern.Experiment.Cli/Program.cs",
    "csharp/Fern.Experiment/Catalog/recipe_source_capabilities_v1.json",
    "csharp/Fern.Experiment/RecipeCatalogRecords.cs",
    "csharp/Fern.Experiment/V2HandlerCapabilitiesCli.cs",
];
// Frozen manifests minted before the C# cutover retain their historic Rust
// provenance and remain readable. New authoring never uses this list to
// publish handlers; it only verifies the saved source observation.
const LEGACY_RUST_CAPABILITY_SOURCES: [&str; 4] = [
    "crates/fern-recipes/src/composition.rs",
    "crates/fern-recipes/src/dataset_catalog.rs",
    "crates/fern-recipes/src/extension.rs",
    "crates/fern-recipes/src/lib.rs",
];

const PYTHON_PROBE_SOURCE: &str = r#"
import importlib
import json
import os
import pathlib
import sys

root = pathlib.Path(os.environ.get("FERN_PROBE_SOURCE_ROOT", pathlib.Path.cwd())).resolve()
if str(root) not in sys.path:
    sys.path.insert(0, str(root))

def loaded_workspace_sources():
    paths = set()
    for loaded in tuple(sys.modules.values()):
        raw = getattr(loaded, "__file__", None)
        if raw is None:
            continue
        path = pathlib.Path(raw).resolve()
        try:
            relative = path.relative_to(root)
        except ValueError:
            continue
        if path.is_file():
            paths.add(relative.as_posix())
    return sorted(paths)

module = importlib.import_module(sys.argv[1])
entrypoint = getattr(module, "fern_experiment_capabilities_v2")
capabilities = entrypoint()
model_link_name = capabilities["model_link_probe"]["entrypoint"]
model_link_entrypoint = getattr(module, model_link_name)
if not callable(model_link_entrypoint):
    raise TypeError(f"Python model-link entrypoint {model_link_name!r} is not callable")
payload = {
    "provider_file": str(pathlib.Path(module.__file__).resolve()),
    "capabilities": capabilities,
    "loaded_source_files": loaded_workspace_sources(),
}
json.dump(payload, sys.stdout, sort_keys=True, separators=(",", ":"))
"#;

const PYTHON_MODEL_LINK_SOURCE: &str = r#"
import importlib
import json
import os
import pathlib
import sys

root = pathlib.Path(os.environ.get("FERN_PROBE_SOURCE_ROOT", pathlib.Path.cwd())).resolve()
if str(root) not in sys.path:
    sys.path.insert(0, str(root))

def loaded_workspace_sources():
    paths = set()
    for loaded in tuple(sys.modules.values()):
        raw = getattr(loaded, "__file__", None)
        if raw is None:
            continue
        path = pathlib.Path(raw).resolve()
        try:
            relative = path.relative_to(root)
        except ValueError:
            continue
        if path.is_file():
            paths.add(relative.as_posix())
    return sorted(paths)

request = json.loads(sys.argv[1]) if len(sys.argv) > 1 else json.load(sys.stdin)
module = importlib.import_module(request["module"])
entrypoint = getattr(module, request["entrypoint"])
result = entrypoint(
    request["config"],
    channels=request["channels"],
    action_dim=request["action_dim"],
)
json.dump(
    {"result": result, "loaded_source_files": loaded_workspace_sources()},
    sys.stdout,
    sort_keys=True,
    separators=(",", ":"),
)
"#;

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct PythonProbeEnvelope {
    provider_file: PathBuf,
    capabilities: Value,
    loaded_source_files: Vec<String>,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct PythonModelLinkEnvelope {
    result: Value,
    loaded_source_files: Vec<String>,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct PythonProbeObservation {
    pub(crate) provider_file: PathBuf,
    pub(crate) capabilities: PythonCapabilityProbeV2,
    pub(crate) loaded_source_files: Vec<String>,
}

#[derive(Serialize)]
struct ImplementationCapabilityBody<'a> {
    schema: &'static str,
    generators: &'a [CapabilityGeneratorV2],
    model_link_probe: &'a PythonModelLinkProbeV2,
    handlers: &'a [fern_recipes::HandlerCapabilityV2],
    /// Omit an absent field exactly as `ImplementationCapabilitiesV2` does. Without
    /// this the preimage writes `null` where the public type writes nothing, and
    /// every bundle probed before this field existed re-keys to a new identity.
    #[serde(skip_serializing_if = "Option::is_none")]
    stage_pipeline_template: Option<&'a fern_experiment_types::StagePipelineTemplateV1>,
}

#[derive(Serialize)]
struct ImplementationIdentityPreimage<'a> {
    schema: &'static str,
    runtime: &'a ImplementationRuntimeV2,
    protocol_version: u32,
    source_files: &'a [ImplementationBundleFileV2],
    dependency_lock_files: &'a [ImplementationBundleFileV2],
    active_feature_flags: &'a [String],
    capability_body_sha256: &'a str,
}

struct ObservedFile {
    record: ImplementationBundleFileV2,
    bytes: Vec<u8>,
}

/// Probe the declared Python module in `python`, then bind its generated
/// handlers and the C# static implementation handlers to one stable source
/// observation.  No file is written and no Plan is registered.
pub fn probe_implementation_bundle_v2(
    root: &Path,
    python: &Path,
    draft: ImplementationBundleDraftV2,
) -> Result<ImplementationBundleResolutionV2, ExperimentCompositionError> {
    build_implementation_bundle_v2(root, draft, |root, module| run_python_probe(root, python, module))
}

/// Re-import one saved implementation with the selected worker interpreter
/// and require the complete bundle projection to remain byte-for-byte equal.
/// This is the Plan-time capability check: a stale source tree or a different
/// installed provider fails before a worker or Registry row exists.
pub fn reprobe_implementation_bundle_v2(
    root: &Path,
    python: &Path,
    expected: &ImplementationBundleResolutionV2,
) -> Result<(), ExperimentCompositionError> {
    verify_implementation_bundle_v2(expected)?;
    let draft = implementation_draft_from_resolution(expected);
    let observed = probe_implementation_bundle_v2(root, python, draft)?;
    if observed != *expected {
        return Err(ExperimentCompositionError::new(format!(
            "installed worker/source capability no longer matches frozen implementation {}; rerun probe-implementation and resolve-experiment instead of executing stale authoring",
            expected.implementation.as_str()
        )));
    }
    Ok(())
}

/// Python source used by a remote execution profile. The transport may execute
/// it, but Rust still parses the observation and reconstructs the identity.
pub fn python_capability_probe_source() -> &'static str {
    PYTHON_PROBE_SOURCE
}

/// Reconstruct the exact checked implementation from two independently
/// observed remote Python imports. The remote absolute provider path is mapped
/// back into the local content-addressed source root before identity checking.
pub fn reprobe_implementation_bundle_v2_from_python_outputs(
    local_root: &Path,
    remote_root: &Path,
    expected: &ImplementationBundleResolutionV2,
    discovery_stdout: &[u8],
    observation_stdout: &[u8],
) -> Result<(), ExperimentCompositionError> {
    verify_implementation_bundle_v2(expected)?;
    let draft = implementation_draft_from_resolution(expected);
    let observations = [
        parse_python_probe_output(local_root, remote_root, discovery_stdout, &[])?,
        parse_python_probe_output(local_root, remote_root, observation_stdout, &[])?,
    ];
    let mut observations = observations.into_iter();
    let observed = build_implementation_bundle_v2(local_root, draft, |_root, _module| {
        observations.next().ok_or_else(|| {
            ExperimentCompositionError::new("remote capability probe did not supply both required observations")
        })
    })?;
    if observed != *expected {
        return Err(ExperimentCompositionError::new(format!(
            "remote worker/source capability no longer matches frozen implementation {}; redeploy or re-author instead of executing stale bytes",
            expected.implementation.as_str()
        )));
    }
    Ok(())
}

/// Execute the implementation-bound production model-link probe for one
/// concrete resolved config.  The caller supplies a CPU/float32 fixture;
/// worker-device readiness is checked separately by the execution profile.
pub fn probe_model_link_v2(
    root: &Path,
    python: &Path,
    implementation: &ImplementationBundleResolutionV2,
    config_hash: &str,
    config: &Value,
    channels: u64,
    action_dim: Option<u64>,
) -> Result<ModelLinkReceiptV1, ExperimentCompositionError> {
    verify_implementation_bundle_v2(implementation)?;
    if channels == 0 {
        return Err(ExperimentCompositionError::new("model-link channels must be positive"));
    }
    let request = python_model_link_request(implementation, config, channels, action_dim)?;
    let mut child = read_only_python_probe_command(root, python)
        .args(["-c", python_model_link_probe_source()])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| {
            ExperimentCompositionError::new(format!(
                "cannot start Python model-link probe with {}: {error}",
                python.display()
            ))
        })?;
    child
        .stdin
        .take()
        .ok_or_else(|| ExperimentCompositionError::new("Python model-link stdin pipe is unavailable"))?
        .write_all(&request)
        .map_err(|error| ExperimentCompositionError::new(format!("cannot write Python model-link request: {error}")))?;
    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| ExperimentCompositionError::new("Python model-link stdout pipe is unavailable"))?;
    let stderr = child
        .stderr
        .take()
        .ok_or_else(|| ExperimentCompositionError::new("Python model-link stderr pipe is unavailable"))?;
    let stdout_reader = thread::spawn(move || read_limited(stdout));
    let stderr_reader = thread::spawn(move || read_limited(stderr));
    let started = Instant::now();
    let status = loop {
        if let Some(status) = child
            .try_wait()
            .map_err(|error| ExperimentCompositionError::new(format!("cannot poll Python model-link probe: {error}")))?
        {
            break status;
        }
        if started.elapsed() >= PROBE_TIMEOUT {
            child.kill().map_err(|error| {
                ExperimentCompositionError::new(format!("cannot stop timed-out model-link probe: {error}"))
            })?;
            let _ = child.wait();
            return Err(ExperimentCompositionError::new(format!(
                "Python model-link probe exceeded {} seconds",
                PROBE_TIMEOUT.as_secs()
            )));
        }
        thread::sleep(Duration::from_millis(10));
    };
    let stdout = join_probe_reader(stdout_reader, "model-link stdout")?;
    let stderr = join_probe_reader(stderr_reader, "model-link stderr")?;
    if !status.success() {
        return Err(ExperimentCompositionError::new(format!(
            "Python model-link probe exited {status}: {}",
            String::from_utf8_lossy(&stderr).trim()
        )));
    }
    model_link_receipt_from_python_output(
        implementation,
        config_hash,
        config,
        channels,
        action_dim,
        &stdout,
        &stderr,
    )
}

pub fn python_model_link_probe_source() -> &'static str {
    PYTHON_MODEL_LINK_SOURCE
}

pub fn python_model_link_request(
    implementation: &ImplementationBundleResolutionV2,
    config: &Value,
    channels: u64,
    action_dim: Option<u64>,
) -> Result<Vec<u8>, ExperimentCompositionError> {
    verify_implementation_bundle_v2(implementation)?;
    if channels == 0 {
        return Err(ExperimentCompositionError::new("model-link channels must be positive"));
    }
    let module = normalize_runtime(&implementation.runtime)?;
    serde_json::to_vec(&serde_json::json!({
        "module": module,
        "entrypoint": implementation.capabilities.model_link_probe.entrypoint,
        "config": config,
        "channels": channels,
        "action_dim": action_dim,
    }))
    .map_err(|error| ExperimentCompositionError::new(format!("cannot encode Python model-link request: {error}")))
}

pub fn model_link_receipt_from_python_output(
    implementation: &ImplementationBundleResolutionV2,
    config_hash: &str,
    config: &Value,
    channels: u64,
    action_dim: Option<u64>,
    stdout: &[u8],
    stderr: &[u8],
) -> Result<ModelLinkReceiptV1, ExperimentCompositionError> {
    verify_implementation_bundle_v2(implementation)?;
    if channels == 0 {
        return Err(ExperimentCompositionError::new("model-link channels must be positive"));
    }
    let envelope = serde_json::from_slice::<PythonModelLinkEnvelope>(stdout).map_err(|error| {
        ExperimentCompositionError::new(format!(
            "Python model-link probe did not emit its checked envelope: {error}; stderr={}",
            String::from_utf8_lossy(stderr).trim()
        ))
    })?;
    require_loaded_sources_declared(
        &envelope.loaded_source_files,
        &implementation.source_files,
        "production model-link",
    )?;
    let result = checked_from_value::<PythonModelLinkResultV1>(envelope.result).map_err(|error| {
        ExperimentCompositionError::new(format!(
            "Python model-link probe did not emit a strict {} receipt: {error}",
            PythonModelLinkResultV1::SCHEMA_ID,
        ))
    })?;
    if result.linked_parameter_groups.is_empty() {
        return Err(ExperimentCompositionError::new(
            "Python model-link probe linked_parameter_groups must not be empty",
        ));
    }
    let mut normalized_groups = result.linked_parameter_groups.clone();
    normalized_groups.sort();
    normalized_groups.dedup();
    if normalized_groups != result.linked_parameter_groups {
        return Err(ExperimentCompositionError::new(
            "Python model-link probe linked_parameter_groups must be sorted and duplicate-free",
        ));
    }
    let fixture_sha256 = format!(
        "sha256:{:x}",
        Sha256::digest(canonical_json_bytes(config).map_err(|error| {
            ExperimentCompositionError::new(format!("cannot identify model-link fixture: {error}"))
        })?)
    );
    Ok(ModelLinkReceiptV1 {
        schema: MODEL_LINK_RECEIPT_SCHEMA_V1.to_string(),
        implementation: implementation.implementation.clone(),
        config_hash: config_hash.to_string(),
        fixture_sha256,
        channels,
        action_dim,
        result,
    })
}

/// Recompute the implementation and capability identities in a saved bundle.
pub fn verify_implementation_bundle_v2(
    resolution: &ImplementationBundleResolutionV2,
) -> Result<(), ExperimentCompositionError> {
    require_schema(
        &resolution.schema,
        IMPLEMENTATION_BUNDLE_RESOLUTION_SCHEMA_V2,
        "implementation bundle resolution",
    )?;
    normalize_runtime(&resolution.runtime)?;
    if resolution.protocol_version == 0 {
        return Err(ExperimentCompositionError::new(
            "implementation bundle protocol_version must be positive",
        ));
    }
    validate_file_records(&resolution.source_files, "source_files")?;
    validate_file_records(&resolution.dependency_lock_files, "dependency_lock_files")?;
    let mut flags = resolution.active_feature_flags.clone();
    normalize_tokens(&mut flags, "implementation active_feature_flags")?;
    if flags != resolution.active_feature_flags {
        return Err(ExperimentCompositionError::new(
            "implementation active_feature_flags are not in canonical order",
        ));
    }
    require_disjoint_file_records(&resolution.source_files, &resolution.dependency_lock_files)?;

    let normalized_capabilities = normalize_capabilities(resolution.capabilities.clone(), &resolution.implementation)?;
    if normalized_capabilities != resolution.capabilities {
        return Err(ExperimentCompositionError::new(
            "implementation capabilities are not canonical",
        ));
    }
    verify_capability_generators(resolution)?;
    let capability_body_sha256 = capability_body_sha256(&resolution.capabilities)?;
    let expected = implementation_identity(
        &resolution.runtime,
        resolution.protocol_version,
        &resolution.source_files,
        &resolution.dependency_lock_files,
        &resolution.active_feature_flags,
        &capability_body_sha256,
    )?;
    if expected != resolution.implementation {
        return Err(ExperimentCompositionError::new(format!(
            "implementation bundle identity {} does not match its checked content {}",
            resolution.implementation.as_str(),
            expected.as_str()
        )));
    }
    Ok(())
}

pub(crate) fn build_implementation_bundle_v2<F>(
    root: &Path,
    mut draft: ImplementationBundleDraftV2,
    mut probe: F,
) -> Result<ImplementationBundleResolutionV2, ExperimentCompositionError>
where
    F: FnMut(&Path, &str) -> Result<PythonProbeObservation, ExperimentCompositionError>,
{
    require_schema(
        &draft.schema,
        IMPLEMENTATION_BUNDLE_DRAFT_SCHEMA_V2,
        "implementation bundle draft",
    )?;
    let root = root.canonicalize().map_err(|error| {
        ExperimentCompositionError::new(format!(
            "cannot canonicalize implementation source root {}: {error}",
            root.display()
        ))
    })?;
    let module = normalize_runtime(&draft.runtime)?;
    if draft.protocol_version == 0 {
        return Err(ExperimentCompositionError::new(
            "implementation bundle protocol_version must be positive",
        ));
    }
    normalize_path_list(&mut draft.source_files, "source_files")?;
    normalize_path_list(&mut draft.dependency_lock_files, "dependency_lock_files")?;
    normalize_tokens(&mut draft.active_feature_flags, "implementation active_feature_flags")?;
    require_disjoint_paths(&draft.source_files, &draft.dependency_lock_files)?;
    for required in CSHARP_CAPABILITY_SOURCES {
        if !draft.source_files.iter().any(|path| path == required) {
            return Err(ExperimentCompositionError::new(format!(
                "implementation source_files must include C# capability authority {required:?}"
            )));
        }
    }

    let lock_observation = observe_files(&root, &draft.dependency_lock_files, "dependency lock")?;
    let discovery = probe(&root, module)?;
    let mut loaded_source_files = discovery.loaded_source_files.clone();
    normalize_path_list(&mut loaded_source_files, "capability import loaded_source_files")?;
    if loaded_source_files != discovery.loaded_source_files {
        return Err(ExperimentCompositionError::new(
            "capability import loaded_source_files are not in canonical order",
        ));
    }
    let declared = draft.source_files.iter().cloned().collect::<BTreeSet<_>>();
    draft
        .source_files
        .extend(loaded_source_files.into_iter().filter(|path| !declared.contains(path)));
    normalize_path_list(
        &mut draft.source_files,
        "source_files plus observed Python import closure",
    )?;
    require_disjoint_paths(&draft.source_files, &draft.dependency_lock_files)?;
    let source_observation = observe_files(&root, &draft.source_files, "source")?;
    let observation = probe(&root, module)?;
    if observation != discovery {
        return Err(ExperimentCompositionError::new(
            "Python capability/import closure changed between discovery and the identity-bearing source observation",
        ));
    }
    let provider_file = observation.provider_file;
    let mut python_probe = observation.capabilities;
    require_schema(
        &python_probe.schema,
        PythonCapabilityProbeV2::SCHEMA_ID,
        "Python capability probe",
    )?;
    if python_probe.handlers.is_empty() {
        return Err(ExperimentCompositionError::new(
            "Python capability probe returned no model handlers",
        ));
    }
    validate_model_link_probe_v2(&python_probe.model_link_probe)?;
    normalize_path_list(
        &mut python_probe.generator_source_files,
        "Python capability generator_source_files",
    )?;
    for handler in &python_probe.handlers {
        if matches!(
            handler.role,
            ExperimentComponentRoleV2::World
                | ExperimentComponentRoleV2::Evaluator
                | ExperimentComponentRoleV2::Loader
                | ExperimentComponentRoleV2::Trainer
        ) {
            return Err(ExperimentCompositionError::new(format!(
                "Python provider attempted to claim C#-owned {} handler {:?}",
                handler.role.value(),
                handler.handler
            )));
        }
    }
    let csharp_publication = static_handler_capabilities_v2_via_csharp()?;
    let expected_csharp_paths = CSHARP_CAPABILITY_SOURCES
        .iter()
        .map(|path| (*path).to_string())
        .collect::<Vec<_>>();
    if csharp_publication.source_paths != expected_csharp_paths {
        return Err(ExperimentCompositionError::new(format!(
            "C# static capability publication names source paths {:?}, expected {:?}",
            csharp_publication.source_paths, expected_csharp_paths
        )));
    }
    verify_observation_unchanged(&root, &source_observation, "source")?;
    verify_observation_unchanged(&root, &lock_observation, "dependency lock")?;

    let source_files = records(&source_observation);
    let dependency_lock_files = records(&lock_observation);
    let provider_relative = provider_relative_path(&root, &provider_file)?;
    require_loaded_sources_declared(&observation.loaded_source_files, &source_files, "capability import")?;
    source_files
        .iter()
        .find(|record| record.path == provider_relative)
        .ok_or_else(|| {
            ExperimentCompositionError::new(format!(
                "imported Python capability provider {provider_relative:?} is not one of the declared source_files"
            ))
        })?;
    if !python_probe
        .generator_source_files
        .iter()
        .any(|path| path == &provider_relative)
    {
        return Err(ExperimentCompositionError::new(format!(
            "Python capability generator_source_files must include imported provider {provider_relative:?}"
        )));
    }
    let python_source_records = records_for_paths(
        &source_files,
        &python_probe.generator_source_files,
        "Python capability generator",
    )?;
    let csharp_source_records = CSHARP_CAPABILITY_SOURCES
        .iter()
        .map(|path| {
            source_files
                .iter()
                .find(|record| record.path == *path)
                .cloned()
                .ok_or_else(|| {
                    ExperimentCompositionError::new(format!("missing observed C# capability source {path:?}"))
                })
        })
        .collect::<Result<Vec<_>, _>>()?;
    let csharp_source_sha256 = plain_sha256(&csharp_source_records)?;

    let mut handlers = csharp_publication.handlers;
    handlers.extend(python_probe.handlers);
    let generators = vec![
        CapabilityGeneratorV2 {
            kind: CapabilityGeneratorKindV2::CSharpExperiment,
            provider: "Fern.Experiment".to_string(),
            source_paths: expected_csharp_paths,
            source_sha256: csharp_source_sha256,
        },
        CapabilityGeneratorV2 {
            kind: CapabilityGeneratorKindV2::PythonConfigSchema,
            provider: module.to_string(),
            source_paths: python_probe.generator_source_files,
            source_sha256: plain_sha256(&python_source_records)?,
        },
    ];

    let placeholder = ImplementationBundleHashV2::parse(format!("implementation-bundle:sha256:{}", "0".repeat(64)))
        .map_err(|error| ExperimentCompositionError::new(error.to_string()))?;
    let capabilities = normalize_capabilities(
        ImplementationCapabilitiesV2 {
            schema: IMPLEMENTATION_CAPABILITIES_SCHEMA_V2.to_string(),
            implementation: placeholder.clone(),
            generators,
            model_link_probe: python_probe.model_link_probe,
            handlers,
            stage_pipeline_template: python_probe
                .stage_pipeline_template
                .or_else(|| Some(fern_experiment_types::StagePipelineTemplateV1::forecast_origin_pipeline())),
        },
        &placeholder,
    )?;
    let capability_body_sha256 = capability_body_sha256(&capabilities)?;
    let implementation = implementation_identity(
        &draft.runtime,
        draft.protocol_version,
        &source_files,
        &dependency_lock_files,
        &draft.active_feature_flags,
        &capability_body_sha256,
    )?;
    let mut capabilities = capabilities;
    capabilities.implementation = implementation.clone();
    let resolution = ImplementationBundleResolutionV2 {
        schema: IMPLEMENTATION_BUNDLE_RESOLUTION_SCHEMA_V2.to_string(),
        implementation,
        runtime: draft.runtime,
        protocol_version: draft.protocol_version,
        source_files,
        dependency_lock_files,
        active_feature_flags: draft.active_feature_flags,
        capabilities,
    };
    verify_implementation_bundle_v2(&resolution)?;
    Ok(resolution)
}

fn read_only_python_probe_command(root: &Path, python: &Path) -> Command {
    let mut command = Command::new(python);
    command
        .current_dir(root)
        .env("PYTHONDONTWRITEBYTECODE", "1")
        .env("FERN_PROBE_SOURCE_ROOT", root);
    command
}

fn implementation_draft_from_resolution(expected: &ImplementationBundleResolutionV2) -> ImplementationBundleDraftV2 {
    ImplementationBundleDraftV2 {
        schema: IMPLEMENTATION_BUNDLE_DRAFT_SCHEMA_V2.to_string(),
        runtime: expected.runtime.clone(),
        protocol_version: expected.protocol_version,
        source_files: expected.source_files.iter().map(|file| file.path.clone()).collect(),
        dependency_lock_files: expected
            .dependency_lock_files
            .iter()
            .map(|file| file.path.clone())
            .collect(),
        active_feature_flags: expected.active_feature_flags.clone(),
    }
}

fn parse_python_probe_output(
    local_root: &Path,
    observed_root: &Path,
    stdout: &[u8],
    stderr: &[u8],
) -> Result<PythonProbeObservation, ExperimentCompositionError> {
    let envelope: PythonProbeEnvelope = serde_json::from_slice(stdout).map_err(|error| {
        ExperimentCompositionError::new(format!(
            "Python capability probe did not emit its checked JSON envelope: {error}; stderr={}",
            String::from_utf8_lossy(stderr).trim()
        ))
    })?;
    let provider_relative = envelope.provider_file.strip_prefix(observed_root).map_err(|_| {
        ExperimentCompositionError::new(format!(
            "Python capability provider {} is outside observed source root {}",
            envelope.provider_file.display(),
            observed_root.display()
        ))
    })?;
    let capabilities = checked_from_value::<PythonCapabilityProbeV2>(envelope.capabilities)
        .map_err(|error| ExperimentCompositionError::new(format!("Python capability payload is invalid: {error}")))?;
    Ok(PythonProbeObservation {
        provider_file: local_root.join(provider_relative),
        capabilities,
        loaded_source_files: envelope.loaded_source_files,
    })
}

fn run_python_probe(
    root: &Path,
    python: &Path,
    module: &str,
) -> Result<PythonProbeObservation, ExperimentCompositionError> {
    let mut child = read_only_python_probe_command(root, python)
        .args(["-c", PYTHON_PROBE_SOURCE, module])
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| {
            ExperimentCompositionError::new(format!(
                "cannot start Python capability probe with {}: {error}",
                python.display()
            ))
        })?;
    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| ExperimentCompositionError::new("Python capability probe stdout pipe is unavailable"))?;
    let stderr = child
        .stderr
        .take()
        .ok_or_else(|| ExperimentCompositionError::new("Python capability probe stderr pipe is unavailable"))?;
    let stdout_reader = thread::spawn(move || read_limited(stdout));
    let stderr_reader = thread::spawn(move || read_limited(stderr));
    let started = Instant::now();
    let status = loop {
        if let Some(status) = child
            .try_wait()
            .map_err(|error| ExperimentCompositionError::new(format!("cannot poll Python capability probe: {error}")))?
        {
            break status;
        }
        if started.elapsed() >= PROBE_TIMEOUT {
            child.kill().map_err(|error| {
                ExperimentCompositionError::new(format!("cannot stop timed-out Python capability probe: {error}"))
            })?;
            let _ = child.wait();
            return Err(ExperimentCompositionError::new(format!(
                "Python capability probe exceeded {} seconds",
                PROBE_TIMEOUT.as_secs()
            )));
        }
        thread::sleep(Duration::from_millis(10));
    };
    let stdout = join_probe_reader(stdout_reader, "stdout")?;
    let stderr = join_probe_reader(stderr_reader, "stderr")?;
    if !status.success() {
        return Err(ExperimentCompositionError::new(format!(
            "Python capability probe exited {status}: {}",
            String::from_utf8_lossy(&stderr).trim()
        )));
    }
    parse_python_probe_output(root, root, &stdout, &stderr)
}

fn require_loaded_sources_declared(
    loaded_source_files: &[String],
    declared_source_files: &[ImplementationBundleFileV2],
    probe_name: &str,
) -> Result<(), ExperimentCompositionError> {
    let mut normalized = loaded_source_files.to_vec();
    normalize_path_list(&mut normalized, &format!("{probe_name} loaded_source_files"))?;
    if normalized != loaded_source_files {
        return Err(ExperimentCompositionError::new(format!(
            "{probe_name} loaded_source_files are not in canonical order"
        )));
    }
    let declared = declared_source_files
        .iter()
        .map(|source| source.path.as_str())
        .collect::<BTreeSet<_>>();
    let missing = loaded_source_files
        .iter()
        .filter(|path| !declared.contains(path.as_str()))
        .cloned()
        .collect::<Vec<_>>();
    if !missing.is_empty() {
        return Err(ExperimentCompositionError::new(format!(
            "{probe_name} imported workspace source files absent from implementation source_files: {missing:?}"
        )));
    }
    Ok(())
}

fn read_limited(mut reader: impl Read) -> Result<Vec<u8>, std::io::Error> {
    let mut bytes = Vec::new();
    reader
        .by_ref()
        .take((MAX_PROBE_OUTPUT_BYTES + 1) as u64)
        .read_to_end(&mut bytes)?;
    Ok(bytes)
}

fn join_probe_reader(
    reader: thread::JoinHandle<Result<Vec<u8>, std::io::Error>>,
    stream: &str,
) -> Result<Vec<u8>, ExperimentCompositionError> {
    let bytes = reader
        .join()
        .map_err(|_| ExperimentCompositionError::new(format!("Python capability probe {stream} reader panicked")))?
        .map_err(|error| {
            ExperimentCompositionError::new(format!("cannot read Python capability probe {stream}: {error}"))
        })?;
    if bytes.len() > MAX_PROBE_OUTPUT_BYTES {
        return Err(ExperimentCompositionError::new(format!(
            "Python capability probe {stream} exceeds the {} byte limit",
            MAX_PROBE_OUTPUT_BYTES
        )));
    }
    Ok(bytes)
}

fn normalize_runtime(runtime: &ImplementationRuntimeV2) -> Result<&str, ExperimentCompositionError> {
    match runtime {
        ImplementationRuntimeV2::PythonModule { module } => {
            if module.is_empty()
                || module.split('.').any(|segment| {
                    segment.is_empty()
                        || segment.starts_with(|character: char| character.is_ascii_digit())
                        || !segment
                            .chars()
                            .all(|character| character.is_ascii_alphanumeric() || character == '_')
                })
            {
                return Err(ExperimentCompositionError::new(format!(
                    "Python capability module {module:?} is not a dotted Python identifier"
                )));
            }
            Ok(module)
        }
    }
}

fn normalize_path_list(paths: &mut Vec<String>, field: &str) -> Result<(), ExperimentCompositionError> {
    if paths.is_empty() {
        return Err(ExperimentCompositionError::new(format!(
            "implementation {field} must not be empty"
        )));
    }
    for path in paths.iter() {
        validate_relative_path(path, field)?;
    }
    paths.sort();
    for pair in paths.windows(2) {
        if pair[0] == pair[1] {
            return Err(ExperimentCompositionError::new(format!(
                "implementation {field} repeats path {:?}",
                pair[0]
            )));
        }
    }
    Ok(())
}

fn validate_relative_path(path: &str, field: &str) -> Result<(), ExperimentCompositionError> {
    let path_value = Path::new(path);
    if path.is_empty()
        || path_value.is_absolute()
        || !path_value
            .components()
            .all(|component| matches!(component, Component::Normal(_)))
    {
        return Err(ExperimentCompositionError::new(format!(
            "implementation {field} path {path:?} must be a safe relative path"
        )));
    }
    Ok(())
}

fn normalize_tokens(values: &mut Vec<String>, field: &str) -> Result<(), ExperimentCompositionError> {
    for value in values.iter() {
        if value.is_empty()
            || !value
                .chars()
                .all(|character| character.is_ascii_alphanumeric() || matches!(character, '.' | '_' | '-' | ':' | '+'))
        {
            return Err(ExperimentCompositionError::new(format!(
                "{field} value {value:?} contains unsupported characters"
            )));
        }
    }
    values.sort();
    for pair in values.windows(2) {
        if pair[0] == pair[1] {
            return Err(ExperimentCompositionError::new(format!(
                "{field} repeats value {:?}",
                pair[0]
            )));
        }
    }
    Ok(())
}

pub(crate) fn validate_model_link_probe_v2(probe: &PythonModelLinkProbeV2) -> Result<(), ExperimentCompositionError> {
    if probe.protocol_version == 0 {
        return Err(ExperimentCompositionError::new(
            "Python model-link probe protocol_version must be positive",
        ));
    }
    if probe.entrypoint.is_empty()
        || !probe
            .entrypoint
            .chars()
            .all(|character| character.is_ascii_alphanumeric() || character == '_')
    {
        return Err(ExperimentCompositionError::new(format!(
            "Python model-link probe entrypoint {:?} must be a non-empty Python identifier",
            probe.entrypoint
        )));
    }
    Ok(())
}

fn require_disjoint_paths(source: &[String], locks: &[String]) -> Result<(), ExperimentCompositionError> {
    let source = source.iter().collect::<BTreeSet<_>>();
    let overlap = locks
        .iter()
        .filter(|path| source.contains(path))
        .cloned()
        .collect::<Vec<_>>();
    if !overlap.is_empty() {
        return Err(ExperimentCompositionError::new(format!(
            "implementation source_files and dependency_lock_files overlap: {overlap:?}"
        )));
    }
    Ok(())
}

fn observe_files(root: &Path, paths: &[String], label: &str) -> Result<Vec<ObservedFile>, ExperimentCompositionError> {
    let mut observed = Vec::with_capacity(paths.len());
    let mut total = 0_u64;
    for relative in paths {
        let path = root.join(relative);
        let metadata = fs::symlink_metadata(&path).map_err(|error| {
            ExperimentCompositionError::new(format!(
                "cannot inspect implementation {label} {}: {error}",
                path.display()
            ))
        })?;
        if metadata.file_type().is_symlink() || !metadata.is_file() {
            return Err(ExperimentCompositionError::new(format!(
                "implementation {label} {} must be one regular file",
                path.display()
            )));
        }
        if metadata.len() > MAX_IMPLEMENTATION_FILE_BYTES {
            return Err(ExperimentCompositionError::new(format!(
                "implementation {label} {} is {} bytes; per-file maximum is {} bytes",
                path.display(),
                metadata.len(),
                MAX_IMPLEMENTATION_FILE_BYTES
            )));
        }
        total = total
            .checked_add(metadata.len())
            .ok_or_else(|| ExperimentCompositionError::new("implementation observation byte count overflows u64"))?;
        if total > MAX_IMPLEMENTATION_TOTAL_BYTES {
            return Err(ExperimentCompositionError::new(format!(
                "implementation {label} files exceed the {} byte total limit",
                MAX_IMPLEMENTATION_TOTAL_BYTES
            )));
        }
        let bytes = fs::read(&path).map_err(|error| {
            ExperimentCompositionError::new(format!(
                "cannot read implementation {label} {}: {error}",
                path.display()
            ))
        })?;
        observed.push(ObservedFile {
            record: ImplementationBundleFileV2 {
                path: relative.clone(),
                byte_sha256: format!("sha256:{:x}", Sha256::digest(&bytes)),
                bytes: bytes.len() as u64,
            },
            bytes,
        });
    }
    Ok(observed)
}

fn verify_observation_unchanged(
    root: &Path,
    observed: &[ObservedFile],
    label: &str,
) -> Result<(), ExperimentCompositionError> {
    let paths = observed.iter().map(|file| file.record.path.clone()).collect::<Vec<_>>();
    let after = observe_files(root, &paths, label)?;
    for (before, after) in observed.iter().zip(after) {
        if before.record != after.record || before.bytes != after.bytes {
            return Err(ExperimentCompositionError::new(format!(
                "implementation {label} {:?} changed while capabilities were probed; retry from one stable source window",
                before.record.path
            )));
        }
    }
    Ok(())
}

fn records(observed: &[ObservedFile]) -> Vec<ImplementationBundleFileV2> {
    observed.iter().map(|file| file.record.clone()).collect()
}

fn records_for_paths(
    records: &[ImplementationBundleFileV2],
    paths: &[String],
    label: &str,
) -> Result<Vec<ImplementationBundleFileV2>, ExperimentCompositionError> {
    paths
        .iter()
        .map(|path| {
            records
                .iter()
                .find(|record| &record.path == path)
                .cloned()
                .ok_or_else(|| {
                    ExperimentCompositionError::new(format!(
                        "{label} source path {path:?} is not one of the bundle source_files"
                    ))
                })
        })
        .collect()
}

fn provider_relative_path(root: &Path, provider_file: &Path) -> Result<String, ExperimentCompositionError> {
    let provider = provider_file.canonicalize().map_err(|error| {
        ExperimentCompositionError::new(format!(
            "cannot canonicalize imported Python capability provider {}: {error}",
            provider_file.display()
        ))
    })?;
    let relative = provider.strip_prefix(root).map_err(|_| {
        ExperimentCompositionError::new(format!(
            "imported Python capability provider {} is outside implementation root {}",
            provider.display(),
            root.display()
        ))
    })?;
    relative
        .to_str()
        .map(str::to_string)
        .ok_or_else(|| ExperimentCompositionError::new("Python capability provider path is not UTF-8"))
}

fn capability_body_sha256(capabilities: &ImplementationCapabilitiesV2) -> Result<String, ExperimentCompositionError> {
    plain_sha256(&ImplementationCapabilityBody {
        schema: IMPLEMENTATION_CAPABILITIES_SCHEMA_V2,
        generators: &capabilities.generators,
        model_link_probe: &capabilities.model_link_probe,
        handlers: &capabilities.handlers,
        stage_pipeline_template: capabilities.stage_pipeline_template.as_ref(),
    })
}

fn verify_capability_generators(
    resolution: &ImplementationBundleResolutionV2,
) -> Result<(), ExperimentCompositionError> {
    if resolution.capabilities.generators.len() != 2 {
        return Err(ExperimentCompositionError::new(
            "implementation bundle must contain exactly one static and one Python capability generator",
        ));
    }
    let static_generator = resolution
        .capabilities
        .generators
        .iter()
        .find(|generator| {
            matches!(
                generator.kind,
                CapabilityGeneratorKindV2::CSharpExperiment | CapabilityGeneratorKindV2::RustTypes
            )
        })
        .ok_or_else(|| ExperimentCompositionError::new("implementation bundle has no static capability generator"))?;
    let python = resolution
        .capabilities
        .generators
        .iter()
        .find(|generator| generator.kind == CapabilityGeneratorKindV2::PythonConfigSchema)
        .ok_or_else(|| ExperimentCompositionError::new("implementation bundle has no Python capability generator"))?;
    let expected_csharp_paths = CSHARP_CAPABILITY_SOURCES
        .iter()
        .map(|path| (*path).to_string())
        .collect::<Vec<_>>();
    let expected_legacy_rust_paths = LEGACY_RUST_CAPABILITY_SOURCES
        .iter()
        .map(|path| (*path).to_string())
        .collect::<Vec<_>>();
    match static_generator.kind {
        CapabilityGeneratorKindV2::CSharpExperiment
            if static_generator.provider == "Fern.Experiment"
                && static_generator.source_paths == expected_csharp_paths => {}
        CapabilityGeneratorKindV2::RustTypes
            if static_generator.provider == "fern-recipes"
                && static_generator.source_paths == expected_legacy_rust_paths => {}
        _ => {
            return Err(ExperimentCompositionError::new(
                "static capability generator does not name either the C# Experiment authority or the recoverable historic Rust authority sources",
            ));
        }
    }
    let ImplementationRuntimeV2::PythonModule { module } = &resolution.runtime;
    if &python.provider != module {
        return Err(ExperimentCompositionError::new(format!(
            "Python capability generator provider {:?} differs from imported runtime module {module:?}",
            python.provider
        )));
    }
    for generator in [static_generator, python] {
        let records = records_for_paths(
            &resolution.source_files,
            &generator.source_paths,
            &format!("{} capability generator", generator.provider),
        )?;
        let expected = plain_sha256(&records)?;
        if generator.source_sha256 != expected {
            return Err(ExperimentCompositionError::new(format!(
                "{} capability generator source digest {} does not match its declared source records {}",
                generator.provider, generator.source_sha256, expected
            )));
        }
    }
    Ok(())
}

fn implementation_identity(
    runtime: &ImplementationRuntimeV2,
    protocol_version: u32,
    source_files: &[ImplementationBundleFileV2],
    dependency_lock_files: &[ImplementationBundleFileV2],
    active_feature_flags: &[String],
    capability_body_sha256: &str,
) -> Result<ImplementationBundleHashV2, ExperimentCompositionError> {
    let value = serde_json::to_value(ImplementationIdentityPreimage {
        schema: IMPLEMENTATION_BUNDLE_RESOLUTION_SCHEMA_V2,
        runtime,
        protocol_version,
        source_files,
        dependency_lock_files,
        active_feature_flags,
        capability_body_sha256,
    })
    .map_err(|error| {
        ExperimentCompositionError::new(format!("cannot encode implementation identity preimage: {error}"))
    })?;
    Ok(ImplementationBundleHashV2::from_content_bytes(&canonical_json_bytes(
        &value,
    )?))
}

fn plain_sha256<T: Serialize>(value: &T) -> Result<String, ExperimentCompositionError> {
    let value = serde_json::to_value(value)
        .map_err(|error| ExperimentCompositionError::new(format!("cannot encode SHA-256 preimage: {error}")))?;
    Ok(format!("sha256:{:x}", Sha256::digest(canonical_json_bytes(&value)?)))
}

fn validate_file_records(
    records: &[ImplementationBundleFileV2],
    field: &str,
) -> Result<(), ExperimentCompositionError> {
    if records.is_empty() {
        return Err(ExperimentCompositionError::new(format!(
            "implementation bundle {field} must not be empty"
        )));
    }
    let mut previous: Option<&str> = None;
    for record in records {
        validate_relative_path(&record.path, field)?;
        validate_sha256(&record.byte_sha256, &format!("{field} byte_sha256"))?;
        if record.bytes > MAX_IMPLEMENTATION_FILE_BYTES {
            return Err(ExperimentCompositionError::new(format!(
                "implementation bundle {field} {:?} exceeds the per-file byte limit",
                record.path
            )));
        }
        if previous.is_some_and(|previous| previous >= record.path.as_str()) {
            return Err(ExperimentCompositionError::new(format!(
                "implementation bundle {field} paths are not unique canonical order"
            )));
        }
        previous = Some(&record.path);
    }
    Ok(())
}

fn require_disjoint_file_records(
    sources: &[ImplementationBundleFileV2],
    locks: &[ImplementationBundleFileV2],
) -> Result<(), ExperimentCompositionError> {
    require_disjoint_paths(
        &sources.iter().map(|record| record.path.clone()).collect::<Vec<_>>(),
        &locks.iter().map(|record| record.path.clone()).collect::<Vec<_>>(),
    )
}

fn validate_sha256(value: &str, field: &str) -> Result<(), ExperimentCompositionError> {
    let Some(hex) = value.strip_prefix("sha256:") else {
        return Err(ExperimentCompositionError::new(format!(
            "implementation bundle {field} must start with sha256:"
        )));
    };
    if hex.len() != 64
        || !hex
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return Err(ExperimentCompositionError::new(format!(
            "implementation bundle {field} must end with 64 lowercase hexadecimal digits"
        )));
    }
    Ok(())
}

fn require_schema(observed: &str, expected: &str, noun: &str) -> Result<(), ExperimentCompositionError> {
    if observed != expected {
        return Err(ExperimentCompositionError::new(format!(
            "{noun} declares schema {observed:?}; expected {expected:?}"
        )));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use fern_recipes::{ARCHITECTURE_COMPONENT_CARD_SCHEMA_V2, HandlerCapabilityV2, PYTHON_CAPABILITY_PROBE_SCHEMA_V2};
    use serde_json::json;
    use std::sync::atomic::{AtomicU64, Ordering};

    /// One real bundle, minted before `stage_pipeline_template` existed.
    ///
    /// This is the only test here whose expected value was not written by
    /// whoever wrote the code: the identity is the one recorded inside the file
    /// itself, so the preimage and the public type cannot drift apart without
    /// this failing. Both halves of that pair have to agree about whether an
    /// absent optional field is written, and only an artifact minted under the
    /// earlier shape can prove they still do.
    #[test]
    #[ignore = "the historical research bundle is not included in the public archive"]
    fn a_bundle_minted_before_an_optional_field_still_derives_its_own_identity() {
        let fixture_path =
            Path::new(env!("CARGO_MANIFEST_DIR")).join("../../private-fixtures/legacy-implementation-bundle.json");
        let fixture = fs::read_to_string(fixture_path)
            .expect("the historical research bundle is not included in the public archive");
        let resolution: ImplementationBundleResolutionV2 =
            serde_json::from_str(&fixture).expect("stored bundle parses");
        assert!(
            resolution.capabilities.stage_pipeline_template.is_none(),
            "this fixture is only meaningful while the optional field is absent from it"
        );
        let capability_body = capability_body_sha256(&resolution.capabilities).expect("capability body digest");
        let derived = implementation_identity(
            &resolution.runtime,
            resolution.protocol_version,
            &resolution.source_files,
            &resolution.dependency_lock_files,
            &resolution.active_feature_flags,
            &capability_body,
        )
        .expect("identity derives");
        assert_eq!(
            derived, resolution.implementation,
            "the identity preimage and the public capability type disagree about an absent optional field"
        );
        verify_implementation_bundle_v2(&resolution).expect("the stored bundle verifies end to end");
    }

    static SCRATCH_SEQUENCE: AtomicU64 = AtomicU64::new(0);

    struct ScratchDirectory(PathBuf);

    impl ScratchDirectory {
        fn new() -> Self {
            let sequence = SCRATCH_SEQUENCE.fetch_add(1, Ordering::Relaxed);
            let path =
                std::env::temp_dir().join(format!("fern-implementation-bundle-{}-{sequence}", std::process::id()));
            fs::create_dir(&path).unwrap();
            Self(path)
        }
    }

    impl Drop for ScratchDirectory {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn write_fixture_tree(root: &Path) {
        for path in CSHARP_CAPABILITY_SOURCES {
            let full = root.join(path);
            fs::create_dir_all(full.parent().unwrap()).unwrap();
            fs::write(full, format!("fixture {path}\n")).unwrap();
        }
        fs::write(root.join("provider.py"), "# provider\n").unwrap();
        fs::write(root.join("uv.lock"), "lock\n").unwrap();
    }

    fn draft() -> ImplementationBundleDraftV2 {
        let mut source_files = CSHARP_CAPABILITY_SOURCES
            .into_iter()
            .map(str::to_string)
            .collect::<Vec<_>>();
        source_files.push("provider.py".to_string());
        ImplementationBundleDraftV2 {
            schema: IMPLEMENTATION_BUNDLE_DRAFT_SCHEMA_V2.to_string(),
            runtime: ImplementationRuntimeV2::PythonModule {
                module: "provider".to_string(),
            },
            protocol_version: 1,
            source_files,
            dependency_lock_files: vec!["uv.lock".to_string()],
            active_feature_flags: Vec::new(),
        }
    }

    fn python_probe(role: ExperimentComponentRoleV2) -> PythonCapabilityProbeV2 {
        PythonCapabilityProbeV2 {
            schema: PYTHON_CAPABILITY_PROBE_SCHEMA_V2.to_string(),
            generator_source_files: vec!["provider.py".to_string()],
            model_link_probe: PythonModelLinkProbeV2 {
                entrypoint: "model_link_probe".to_string(),
                protocol_version: 1,
            },
            handlers: vec![HandlerCapabilityV2 {
                role,
                handler: "fixture.model".to_string(),
                parameter_schema: json!({
                    "type": "object",
                    "properties": {"width": {"type": "integer", "minimum": 1}},
                    "required": ["width"],
                    "additionalProperties": false
                }),
                composition_schema: Some(json!({
                    "type": "object",
                    "required": ["architecture"]
                })),
                cross_component_constraints: Vec::new(),
                emits: std::collections::BTreeMap::new(),
                provides: vec!["fern.geometry.v2".to_string()],
                requires: Vec::new(),
            }],
            stage_pipeline_template: None,
        }
    }

    fn probe_observation(provider_file: PathBuf, role: ExperimentComponentRoleV2) -> PythonProbeObservation {
        PythonProbeObservation {
            provider_file,
            capabilities: python_probe(role),
            loaded_source_files: vec!["provider.py".to_string()],
        }
    }

    fn model_link_result() -> Value {
        json!({
            "schema": "fern.model-link-probe-result.v1",
            "model": "fern",
            "density": "single_gaussian",
            "predictive_law": "single_gaussian",
            "action_dim": null,
            "shape_mode": "fake_tensor",
            "model_construction": "production_factory",
            "parameter_contract": "cpu_float32",
            "output_contract": "checked",
            "loss_backward": "finite_connected",
            "optimizer_membership": "exact_trainable_parameters",
            "linked_parameter_groups": ["fern_rotation", "fern_scale", "fern_shift"],
            "checkpoint_round_trip": "strict_safetensors"
        })
    }

    #[test]
    fn bundle_binds_actual_source_and_generated_capabilities() {
        let root = ScratchDirectory::new();
        write_fixture_tree(&root.0);
        let provider = root.0.join("provider.py");
        let first = build_implementation_bundle_v2(&root.0, draft(), |_, module| {
            assert_eq!(module, "provider");
            Ok(probe_observation(
                provider.clone(),
                ExperimentComponentRoleV2::Architecture,
            ))
        })
        .unwrap();
        verify_implementation_bundle_v2(&first).unwrap();
        assert_eq!(first.capabilities.implementation, first.implementation);
        assert_eq!(first.capabilities.generators.len(), 2);

        fs::write(root.0.join("provider.py"), "# changed provider\n").unwrap();
        let second = build_implementation_bundle_v2(&root.0, draft(), |_, _| {
            Ok(probe_observation(
                provider.clone(),
                ExperimentComponentRoleV2::Architecture,
            ))
        })
        .unwrap();
        assert_ne!(first.implementation, second.implementation);
    }

    #[test]
    fn remote_python_outputs_must_reconstruct_the_exact_checked_bundle() {
        let root = ScratchDirectory::new();
        write_fixture_tree(&root.0);
        let expected = build_implementation_bundle_v2(&root.0, draft(), |_, _| {
            Ok(probe_observation(
                root.0.join("provider.py"),
                ExperimentComponentRoleV2::Architecture,
            ))
        })
        .unwrap();
        let remote_root = Path::new("/immutable/remote/source");
        let envelope = serde_json::to_vec(&json!({
            "provider_file": remote_root.join("provider.py"),
            "capabilities": python_probe(ExperimentComponentRoleV2::Architecture),
            "loaded_source_files": ["provider.py"]
        }))
        .unwrap();

        reprobe_implementation_bundle_v2_from_python_outputs(&root.0, remote_root, &expected, &envelope, &envelope)
            .unwrap();

        let mut changed: Value = serde_json::from_slice(&envelope).unwrap();
        changed["capabilities"]["handlers"][0]["handler"] = json!("fixture.changed");
        let changed = serde_json::to_vec(&changed).unwrap();
        let error =
            reprobe_implementation_bundle_v2_from_python_outputs(&root.0, remote_root, &expected, &envelope, &changed)
                .unwrap_err()
                .to_string();
        assert!(error.contains("changed between discovery"), "{error}");
    }

    #[test]
    fn python_reported_stage_template_replaces_the_legacy_training_fallback() {
        let root = ScratchDirectory::new();
        write_fixture_tree(&root.0);
        let mut reported = fern_experiment_types::StagePipelineTemplateV1::forecast_origin_pipeline();
        reported.stages[0].handler = "oracle.controlled_linear.generate".to_string();
        let expected = reported.clone();

        let resolution = build_implementation_bundle_v2(&root.0, draft(), |_, _| {
            let mut observation =
                probe_observation(root.0.join("provider.py"), ExperimentComponentRoleV2::Architecture);
            observation.capabilities.stage_pipeline_template = Some(reported.clone());
            Ok(observation)
        })
        .unwrap();

        assert_eq!(
            resolution.capabilities.stage_pipeline_template.as_ref(),
            Some(&expected)
        );
    }

    #[test]
    fn python_provider_cannot_claim_a_rust_scheduler_handler() {
        let root = ScratchDirectory::new();
        write_fixture_tree(&root.0);
        let error = build_implementation_bundle_v2(&root.0, draft(), |_, _| {
            Ok(probe_observation(
                root.0.join("provider.py"),
                ExperimentComponentRoleV2::Trainer,
            ))
        })
        .unwrap_err()
        .to_string();
        assert!(error.contains("C#-owned trainer"), "{error}");
    }

    #[test]
    fn imported_workspace_module_is_added_to_implementation_identity() {
        let root = ScratchDirectory::new();
        write_fixture_tree(&root.0);
        fs::write(root.0.join("lazy_runtime.py"), "# loaded runtime\n").unwrap();
        let resolution = build_implementation_bundle_v2(&root.0, draft(), |_, _| {
            Ok(PythonProbeObservation {
                provider_file: root.0.join("provider.py"),
                capabilities: python_probe(ExperimentComponentRoleV2::Architecture),
                loaded_source_files: vec!["lazy_runtime.py".to_string(), "provider.py".to_string()],
            })
        })
        .unwrap();
        assert!(
            resolution
                .source_files
                .iter()
                .any(|source| source.path == "lazy_runtime.py")
        );
    }

    #[test]
    fn python_provider_must_declare_a_valid_model_link_entrypoint() {
        let root = ScratchDirectory::new();
        write_fixture_tree(&root.0);
        let error = build_implementation_bundle_v2(&root.0, draft(), |_, _| {
            let mut probe = python_probe(ExperimentComponentRoleV2::Architecture);
            probe.model_link_probe.entrypoint = "test-only model".to_string();
            probe.model_link_probe.protocol_version = 0;
            Ok(PythonProbeObservation {
                provider_file: root.0.join("provider.py"),
                capabilities: probe,
                loaded_source_files: vec!["provider.py".to_string()],
            })
        })
        .unwrap_err()
        .to_string();
        assert!(error.contains("protocol_version must be positive"), "{error}");
    }

    #[test]
    fn model_link_result_is_a_closed_typed_receipt() {
        checked_from_value::<PythonModelLinkResultV1>(model_link_result()).unwrap();

        let mut missing_gate = model_link_result();
        missing_gate.as_object_mut().unwrap().remove("optimizer_membership");
        let error = checked_from_value::<PythonModelLinkResultV1>(missing_gate)
            .unwrap_err()
            .to_string();
        assert!(error.contains("optimizer_membership"), "{error}");

        let mut extra_claim = model_link_result();
        extra_claim["trust_me"] = json!(true);
        let error = checked_from_value::<PythonModelLinkResultV1>(extra_claim)
            .unwrap_err()
            .to_string();
        assert!(error.contains("unknown field"), "{error}");
    }

    #[test]
    fn production_python_probes_disable_bytecode_writes() {
        let root = ScratchDirectory::new();
        let command = read_only_python_probe_command(&root.0, Path::new("python"));
        assert_eq!(command.get_current_dir(), Some(root.0.as_path()));
        let value = command
            .get_envs()
            .find_map(|(key, value)| (key == "PYTHONDONTWRITEBYTECODE").then_some(value))
            .flatten();
        assert_eq!(value, Some(std::ffi::OsStr::new("1")));
    }

    #[test]
    fn source_change_during_probe_refuses_the_whole_observation() {
        let root = ScratchDirectory::new();
        write_fixture_tree(&root.0);
        let calls = std::cell::Cell::new(0usize);
        let error = build_implementation_bundle_v2(&root.0, draft(), |_, _| {
            let call = calls.get();
            calls.set(call + 1);
            if call == 1 {
                fs::write(root.0.join("provider.py"), "# changed during probe\n").unwrap();
            }
            Ok(probe_observation(
                root.0.join("provider.py"),
                ExperimentComponentRoleV2::Architecture,
            ))
        })
        .unwrap_err()
        .to_string();
        assert!(error.contains("stable source window"), "{error}");
    }

    #[test]
    fn bundle_verification_rejects_capability_tampering() {
        let root = ScratchDirectory::new();
        write_fixture_tree(&root.0);
        let mut resolution = build_implementation_bundle_v2(&root.0, draft(), |_, _| {
            Ok(probe_observation(
                root.0.join("provider.py"),
                ExperimentComponentRoleV2::Architecture,
            ))
        })
        .unwrap();
        resolution.capabilities.handlers.last_mut().unwrap().handler =
            ARCHITECTURE_COMPONENT_CARD_SCHEMA_V2.to_string();
        let error = verify_implementation_bundle_v2(&resolution).unwrap_err().to_string();
        assert!(
            error.contains("not canonical") || error.contains("does not match"),
            "{error}"
        );
    }
}
