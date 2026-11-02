//! Install the resident scheduler as a user-session systemd service.
//!
//! This boundary deliberately owns local service materialization only. SSH
//! Worker-Site deployment remains in `binary_deploy.rs`; the elected scheduler
//! loop remains in `run_sweep.rs`. Keeping those three responsibilities apart
//! prevents an install command from becoming a second scheduler or an SSH
//! deployment wrapper.

use std::env;
#[cfg(test)]
use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::process::{Command, ExitStatus, Stdio};
use std::time::{Duration, Instant};
#[cfg(test)]
use std::time::{SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result, ensure};
#[cfg(test)]
use fern_artifact::ByteDigest;
use serde::Deserialize;
#[cfg(test)]
use serde::Serialize;

#[cfg(test)]
use crate::paths::DEFAULT_DEPLOYMENT_ROOT;

#[cfg(test)]
const SCHEDULER_SERVICE_RECEIPT_SCHEMA: &str = "fern.scheduler_service_installation.v8";
#[cfg(test)]
const SCHEDULER_RECEIPT_DIRECTORY: &str = "scheduler-services";
const IDENTITY_PROBE_TIMEOUT: Duration = Duration::from_secs(5);
#[cfg(test)]
const IDENTITY_PROBE_MAX_OUTPUT_BYTES: usize = 64 * 1024;

#[cfg(test)]
#[derive(Debug, Serialize, Deserialize)]
struct InstalledSchedulerReceiptView {
    schema: String,
    installed_at_unix_ms: u128,
    service_name: String,
    unit_path: PathBuf,
    registry: PathBuf,
    workspace_root: PathBuf,
    work_root: PathBuf,
    cycle_seconds: u64,
    scheduler_binary_sha256: String,
    scheduler_binary_path: PathBuf,
    worker_binary_sha256: String,
    worker_binary_path: PathBuf,
    home_binary_sha256: String,
    home_binary_path: PathBuf,
    site_binary_sha256: String,
    site_binary_path: PathBuf,
    stage_runner_binary_sha256: String,
    stage_runner_binary_path: PathBuf,
    stage_runner_capability: serde_json::Value,
    card_binary_sha256: String,
    card_binary_path: PathBuf,
    experiment_binary_sha256: String,
    experiment_binary_path: PathBuf,
    run_binary_sha256: String,
    run_binary_path: PathBuf,
    binary_set_id: String,
    run_plan_protocol: String,
    recipe_handler_capabilities_sha256: String,
    recipe_handler_capabilities: fern_recipes::RecipeHandlerCapabilitiesV1,
    unit_sha256: String,
    residency: String,
    activation: String,
}

#[cfg(test)]
fn require_executable(path: &Path, role: &str) -> Result<()> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;

        let mode = fs::metadata(path)
            .with_context(|| format!("cannot inspect {role} {}", path.display()))?
            .permissions()
            .mode();
        ensure!(
            mode & 0o111 != 0,
            "{role} {} has no executable bit; installation wrote nothing",
            path.display()
        );
    }
    Ok(())
}

#[cfg(test)]
fn inspect_binary_role(
    path: &Path,
    expected_role: crate::build_identity::InstalledBinaryRole,
    label: &str,
    timeout: Duration,
    max_output_bytes: usize,
) -> Result<crate::build_identity::InstallationIdentityV4> {
    require_executable(path, label)?;
    let output = run_identity_probe(path, label, timeout, max_output_bytes)?;
    ensure!(
        output.status.success(),
        "{label} identity probe {} exited {}: {}; installation wrote nothing",
        path.display(),
        output.status,
        String::from_utf8_lossy(&output.stderr).trim()
    );
    let identity = crate::build_identity::parse_installation_identity(&output.stdout)
        .map_err(anyhow::Error::msg)
        .with_context(|| format!("{label} identity probe {} did not return typed JSON", path.display()))?;
    ensure!(
        identity.role == expected_role,
        "{label} {} reports role {:?}, expected {:?}; installation wrote nothing",
        path.display(),
        identity.role,
        expected_role
    );
    ensure!(
        identity.run_plan_protocol == crate::build_identity::RUN_PLAN_PROTOCOL,
        "{label} {} speaks protocol {:?}, but this installer expects {:?}",
        path.display(),
        identity.run_plan_protocol,
        crate::build_identity::RUN_PLAN_PROTOCOL
    );
    Ok(identity)
}

struct IdentityProbeOutput {
    status: ExitStatus,
    stdout: Vec<u8>,
    stderr: Vec<u8>,
}

#[cfg(test)]
fn run_identity_probe(
    path: &Path,
    label: &str,
    timeout: Duration,
    max_output_bytes: usize,
) -> Result<IdentityProbeOutput> {
    run_bounded_probe(path, &["installation-identity"], label, timeout, max_output_bytes)
}

fn run_bounded_probe(
    path: &Path,
    arguments: &[&str],
    label: &str,
    timeout: Duration,
    max_output_bytes: usize,
) -> Result<IdentityProbeOutput> {
    let mut child = Command::new(path)
        .args(arguments)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .with_context(|| format!("cannot run {label} identity probe {}", path.display()))?;
    let stdout = child
        .stdout
        .take()
        .context("identity probe stdout pipe is unavailable")?;
    let stderr = child
        .stderr
        .take()
        .context("identity probe stderr pipe is unavailable")?;
    let read_stdout = std::thread::spawn(move || read_probe_stream(stdout, max_output_bytes));
    let read_stderr = std::thread::spawn(move || read_probe_stream(stderr, max_output_bytes));
    let started = Instant::now();
    let status = loop {
        if let Some(status) = child
            .try_wait()
            .with_context(|| format!("cannot poll {label} identity probe {}", path.display()))?
        {
            break status;
        }
        if started.elapsed() >= timeout {
            child
                .kill()
                .with_context(|| format!("cannot stop timed-out {label} identity probe {}", path.display()))?;
            let _ = child.wait();
            // Do not join here. A hostile probe can fork a descendant that
            // inherits these pipes; waiting for EOF would defeat the timeout.
            drop(read_stdout);
            drop(read_stderr);
            anyhow::bail!(
                "{label} identity probe {} exceeded {} ms; installation wrote nothing",
                path.display(),
                timeout.as_millis()
            );
        }
        std::thread::sleep(Duration::from_millis(10));
    };
    let stdout = join_probe_reader(read_stdout, label, "stdout")?;
    let stderr = join_probe_reader(read_stderr, label, "stderr")?;
    ensure!(
        stdout.len() <= max_output_bytes && stderr.len() <= max_output_bytes,
        "{label} identity probe {} exceeded the {max_output_bytes}-byte output limit; installation wrote nothing",
        path.display()
    );
    Ok(IdentityProbeOutput { status, stdout, stderr })
}

fn read_probe_stream(stream: impl Read, max_output_bytes: usize) -> std::io::Result<Vec<u8>> {
    let mut bytes = Vec::new();
    stream
        .take(u64::try_from(max_output_bytes).unwrap_or(u64::MAX).saturating_add(1))
        .read_to_end(&mut bytes)?;
    Ok(bytes)
}

fn join_probe_reader(
    reader: std::thread::JoinHandle<std::io::Result<Vec<u8>>>,
    label: &str,
    stream: &str,
) -> Result<Vec<u8>> {
    reader
        .join()
        .map_err(|_| anyhow::anyhow!("{label} identity probe {stream} reader panicked"))?
        .with_context(|| format!("cannot read {label} identity probe {stream}"))
}

#[derive(Debug, Deserialize)]
struct InstalledControlInspection {
    state: String,
    reason: Option<String>,
    service_name: Option<String>,
    recipe_handler_capabilities: Option<fern_recipes::RecipeHandlerCapabilitiesV1>,
    home_binary_sha256: Option<String>,
    home_binary_path: Option<PathBuf>,
    home_state: Option<String>,
    home_reason: Option<String>,
}

fn inspect_installed_control(registry: &Path, deployment_root: &Path) -> Result<InstalledControlInspection> {
    let scheduler =
        crate::command_runtime::csharp_scheduler_binary().map_err(|error| anyhow::anyhow!(error.to_string()))?;
    let registry_text = registry.to_string_lossy();
    let deployment_text = deployment_root.to_string_lossy();
    let output = run_bounded_probe(
        &scheduler,
        &[
            "inspect-installed-control",
            "--registry",
            &registry_text,
            "--deployment-root",
            &deployment_text,
        ],
        "C# installed-control inspector",
        IDENTITY_PROBE_TIMEOUT,
        1024 * 1024,
    )?;
    ensure!(
        output.status.success(),
        "C# installed-control inspector {} exited {}: {}",
        scheduler.display(),
        output.status,
        String::from_utf8_lossy(&output.stderr).trim()
    );
    serde_json::from_slice(&output.stdout).context("C# installed-control inspector returned invalid JSON")
}

/// What the installed dispatcher for one Registry Home can name, if anything.
///
/// `Unknown` is a real answer and never means agreement. A caller must say so
/// rather than treat a missing receipt as a passed check.
pub(crate) enum InstalledDispatcherVocabulary {
    Known {
        service_name: String,
        capabilities: Box<fern_recipes::RecipeHandlerCapabilitiesV1>,
    },
    Unknown(String),
}

/// Ask the C# scheduler for the handler vocabulary installed against `registry`.
///
/// The election gate deliberately refuses to look for its own receipt, because
/// a search there could swing a gate OPEN. This search runs in the opposite
/// direction: it can only ADD a refusal at registration, and finding nothing
/// returns `Unknown`, so a missing or unreadable receipt can never be read as
/// compatibility.
pub(crate) fn installed_dispatcher_vocabulary_via_csharp_scheduler(
    registry: &Path,
    deployment_root: &Path,
) -> InstalledDispatcherVocabulary {
    match inspect_installed_control(registry, deployment_root) {
        Ok(inspection) if inspection.state == "known" => {
            match (inspection.service_name, inspection.recipe_handler_capabilities) {
                (Some(service_name), Some(capabilities)) => InstalledDispatcherVocabulary::Known {
                    service_name,
                    capabilities: Box::new(capabilities),
                },
                _ => InstalledDispatcherVocabulary::Unknown(
                    "C# installed-control inspection omitted dispatcher vocabulary".to_string(),
                ),
            }
        }
        Ok(inspection) => InstalledDispatcherVocabulary::Unknown(
            inspection
                .reason
                .unwrap_or_else(|| format!("C# installed-control inspection returned state {:?}", inspection.state)),
        ),
        Err(error) => InstalledDispatcherVocabulary::Unknown(error.to_string()),
    }
}

/// Resolve the installed Registry Home executable for one Registry.
///
/// Human commands do not guess a working-tree path. The C# scheduler selects
/// the newest typed installation receipt for the Registry, re-hashes the
/// binary, and probes its identity before returning the immutable path.
pub(crate) fn installed_home_binary_via_csharp_scheduler(
    registry: &Path,
    deployment_root: &Path,
) -> Result<fern_run::binary_deployment::BinaryVersionIdentity> {
    let inspection = inspect_installed_control(registry, deployment_root)?;
    ensure!(
        inspection.state == "known",
        "{}",
        inspection
            .reason
            .unwrap_or_else(|| "no installed control set governs this Registry Home".to_string())
    );
    ensure!(
        inspection.home_state.as_deref() == Some("verified"),
        "{}",
        inspection
            .home_reason
            .unwrap_or_else(|| "installed Registry Home was not verified".to_string())
    );
    let path = inspection
        .home_binary_path
        .context("C# installed-control inspection omitted Registry Home path")?;
    let digest = inspection
        .home_binary_sha256
        .context("C# installed-control inspection omitted Registry Home digest")?;
    fern_run::binary_deployment::BinaryVersionIdentity::parse(&digest, &path)
        .context("C# installed-control inspection returned an invalid Registry Home identity")
}

/// Test-only standalone publisher used by the Stage package process proof.
/// Production scheduler and Site publication belongs to the C# install verb.
#[cfg(test)]
pub(crate) fn publish_dotnet_single_file(
    workspace_root: &Path,
    project: &str,
    executable_name: &str,
    output_directory: &Path,
    trim: bool,
) -> Result<PathBuf> {
    let project = workspace_root.join(project);
    let mut publish = Command::new("dotnet");
    publish
        .args([
            "publish",
            "--nologo",
            "-c",
            "Release",
            "-r",
            "linux-x64",
            "--self-contained",
            "true",
            "-p:PublishSingleFile=true",
            "-p:EnableCompressionInSingleFile=true",
            "-o",
        ])
        .arg(output_directory)
        .arg(&project);
    if trim {
        publish.arg("-p:PublishTrimmed=true");
    }
    let published = publish
        .output()
        .with_context(|| format!("cannot publish C# control executable from {}", project.display()))?;
    ensure!(
        published.status.success(),
        "dotnet publish {} failed: {}",
        project.display(),
        String::from_utf8_lossy(&published.stderr).trim()
    );
    let executable = output_directory.join(executable_name);
    require_executable(&executable, executable_name)?;
    Ok(executable)
}

#[cfg(test)]
fn canonical_existing_file(path: &Path, role: &str) -> Result<PathBuf> {
    ensure!(path.is_file(), "{role} {} is not a regular file", path.display());
    path.canonicalize()
        .with_context(|| format!("cannot resolve {role} {}", path.display()))
}

#[cfg(test)]
fn unix_time_millis() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[cfg(unix)]
    use std::os::unix::fs::PermissionsExt;

    /// One installed receipt, with whatever handler vocabulary the test wants
    /// the dispatcher to have. Written through the real serializer so a field
    /// rename cannot leave the fixture agreeing with a schema nothing uses.
    fn write_receipt_fixture(
        deployment_root: &Path,
        registry: &Path,
        service_name: &str,
        capabilities: fern_recipes::RecipeHandlerCapabilitiesV1,
    ) -> PathBuf {
        let directory = deployment_root.join(SCHEDULER_RECEIPT_DIRECTORY).join(service_name);
        fs::create_dir_all(&directory).unwrap();
        let receipt = InstalledSchedulerReceiptView {
            schema: SCHEDULER_SERVICE_RECEIPT_SCHEMA.to_string(),
            installed_at_unix_ms: 1,
            service_name: service_name.to_string(),
            unit_path: directory.join("unit.service"),
            registry: registry.to_path_buf(),
            workspace_root: directory.to_path_buf(),
            work_root: directory.join("work"),
            cycle_seconds: 5,
            scheduler_binary_sha256: "0".repeat(64),
            scheduler_binary_path: directory.join("fern-scheduler"),
            worker_binary_sha256: "1".repeat(64),
            worker_binary_path: directory.join("fern-worker"),
            home_binary_sha256: "2".repeat(64),
            home_binary_path: directory.join("fern-home"),
            site_binary_sha256: "3".repeat(64),
            site_binary_path: directory.join("fern-site"),
            stage_runner_binary_sha256: "5".repeat(64),
            stage_runner_binary_path: directory.join("fern-stage-runner"),
            stage_runner_capability: serde_json::json!({
                "schema": "fern.stage_runner_capability.v1",
                "protocol_version": "fern.stage_runner.v1",
                "implementation_digest": format!("sha256:{}", "a".repeat(64)),
                "handlers": ["fern.postprocess"],
                "entrypoint": "fern-stage-runner"
            }),
            card_binary_sha256: "6".repeat(64),
            card_binary_path: directory.join("fern-card"),
            experiment_binary_sha256: "7".repeat(64),
            experiment_binary_path: directory.join("fern-experiment"),
            run_binary_sha256: "8".repeat(64),
            run_binary_path: directory.join("fern-run"),
            binary_set_id: "sha256:".to_string() + &"9".repeat(64),
            run_plan_protocol: crate::build_identity::RUN_PLAN_PROTOCOL.to_string(),
            recipe_handler_capabilities_sha256: crate::build_identity::recipe_handler_capabilities_sha256(
                &capabilities,
            ),
            recipe_handler_capabilities: capabilities,
            unit_sha256: "sha256:".to_string() + &"4".repeat(64),
            residency: "test".to_string(),
            activation: "test".to_string(),
        };
        let path = directory.join("fixture.json");
        fs::write(&path, serde_json::to_vec_pretty(&receipt).unwrap()).unwrap();
        path
    }

    /// The whole point of the field: a dispatcher installed before a handler
    /// existed must be named as such at registration, not at dispatch.
    #[test]
    fn an_installed_dispatcher_missing_a_handler_is_reported_by_name() {
        // The exact 2026-08-17 failure: a plan froze this model while the
        // installed dispatcher's vocabulary predated it.
        const MISSING_MODEL: &str = "fern_wide_scale";

        let workspace = tempfile::tempdir().unwrap();
        let registry = workspace.path().join("lifecycle.db");
        fs::write(&registry, b"not a real database").unwrap();
        let deployment_root = workspace.path().join(DEFAULT_DEPLOYMENT_ROOT);

        let mut older = fern_recipes::recipe_handler_capabilities();
        older.models.retain(|model| model.name != MISSING_MODEL);
        write_receipt_fixture(&deployment_root, &registry, "fern-scheduler-fixture", older);

        let vocabulary = installed_dispatcher_vocabulary_via_csharp_scheduler(&registry, &deployment_root);
        let InstalledDispatcherVocabulary::Known {
            service_name,
            capabilities,
        } = vocabulary
        else {
            panic!("a receipt governing this registry must be found");
        };
        assert_eq!(service_name, "fern-scheduler-fixture");
        assert!(!capabilities.models.iter().any(|model| model.name == MISSING_MODEL));

        // Taken from the catalog rather than named by a literal, so renaming a
        // recipe cannot make this test look at nothing: it fails for the reason
        // it exists for, or not at all.
        let recipe = fern_recipes::recipe_catalog_builds()
            .into_iter()
            .find(|recipe| recipe.model.value() == MISSING_MODEL)
            .expect("the catalog registers a recipe for the model this dispatcher lacks");
        let identity = fern_run::csharp_experiment::resolve_recipe_run_targets_from_blueprint(
            &recipe,
            fern_run::resolved_cfg::DeviceKind::Cuda,
            42,
            1000,
        )
        .unwrap()
        .recipe_identity;
        assert_eq!(
            fern_run::csharp_experiment::missing_recipe_handler_names_via_csharp(
                &identity,
                &capabilities,
            )
            .unwrap(),
            [format!("model: {MISSING_MODEL:?}")]
        );
    }

    /// A registry with no installed dispatcher must return `Unknown`, never a
    /// silent pass: nothing here is evidence that dispatch would work.
    #[test]
    fn a_registry_with_no_installed_dispatcher_is_unknown_not_compatible() {
        let workspace = tempfile::tempdir().unwrap();
        let registry = workspace.path().join("lifecycle.db");
        fs::write(&registry, b"not a real database").unwrap();

        let vocabulary = installed_dispatcher_vocabulary_via_csharp_scheduler(
            &registry,
            &workspace.path().join(DEFAULT_DEPLOYMENT_ROOT),
        );
        assert!(matches!(vocabulary, InstalledDispatcherVocabulary::Unknown(_)));
    }

    /// A receipt for a DIFFERENT Registry Home says nothing about this one.
    #[test]
    fn a_receipt_for_another_registry_is_not_this_registrys_dispatcher() {
        let workspace = tempfile::tempdir().unwrap();
        let mine = workspace.path().join("mine.db");
        let theirs = workspace.path().join("theirs.db");
        fs::write(&mine, b"one").unwrap();
        fs::write(&theirs, b"two").unwrap();
        let deployment_root = workspace.path().join(DEFAULT_DEPLOYMENT_ROOT);
        write_receipt_fixture(
            &deployment_root,
            &theirs,
            "fern-scheduler-elsewhere",
            fern_recipes::recipe_handler_capabilities(),
        );

        let vocabulary = installed_dispatcher_vocabulary_via_csharp_scheduler(&mine, &deployment_root);
        assert!(matches!(vocabulary, InstalledDispatcherVocabulary::Unknown(_)));
    }

    #[test]
    #[cfg(unix)]
    fn operator_resolves_home_from_receipt_and_rehashes_before_use() {
        let workspace = tempfile::tempdir().unwrap();
        let registry = workspace.path().join("lifecycle.db");
        fs::write(&registry, b"fixture registry").unwrap();
        let deployment_root = workspace.path().join(DEFAULT_DEPLOYMENT_ROOT);
        let receipt_path = write_receipt_fixture(
            &deployment_root,
            &registry,
            "fern-scheduler-fixture",
            fern_recipes::recipe_handler_capabilities(),
        );
        let identity = serde_json::to_string(&crate::build_identity::installation_identity(
            crate::build_identity::InstalledBinaryRole::RegistryHome,
        ))
        .unwrap();
        let source = workspace.path().join("fern-home-source");
        write_identity_fixture(&source, "installation-identity", &identity);
        let bytes = fs::read(&source).unwrap();
        let digest = ByteDigest::from_bytes(&bytes)
            .as_str()
            .strip_prefix("sha256:")
            .unwrap()
            .to_string();
        let home = deployment_root
            .join("registry-home-binaries")
            .join(format!("fern-home.{digest}"));
        fs::create_dir_all(home.parent().unwrap()).unwrap();
        fs::copy(&source, &home).unwrap();
        let mut receipt: InstalledSchedulerReceiptView =
            serde_json::from_slice(&fs::read(&receipt_path).unwrap()).unwrap();
        receipt.home_binary_sha256 = digest;
        receipt.home_binary_path = home.clone();
        fs::write(&receipt_path, serde_json::to_vec_pretty(&receipt).unwrap()).unwrap();

        let installed = installed_home_binary_via_csharp_scheduler(&registry, &deployment_root).unwrap();
        assert_eq!(installed.executable_path(), home);

        fs::write(&home, b"changed after installation").unwrap();
        let error = installed_home_binary_via_csharp_scheduler(&registry, &deployment_root).unwrap_err();
        let error_chain = format!("{error:#}");
        assert!(error_chain.contains("failed byte verification"), "{error_chain}");
    }

    #[cfg(unix)]
    fn write_identity_fixture(path: &Path, argument: &str, json: &str) {
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(
            path,
            format!("#!/bin/sh\n[ \"$1\" = \"{argument}\" ] || exit 64\nprintf '%s\\n' '{json}'\n"),
        )
        .unwrap();
        fs::set_permissions(path, fs::Permissions::from_mode(0o755)).unwrap();
    }

    fn temp_root(name: &str) -> PathBuf {
        let root = env::temp_dir().join(format!("fern-scheduler-service-{name}-{}", unix_time_millis()));
        fs::create_dir_all(&root).unwrap();
        root
    }
    #[test]
    #[cfg(unix)]
    fn identity_probe_refuses_a_program_that_never_returns() {
        let root = temp_root("identity-timeout");
        let scheduler = root.join("fern-scheduler");
        fs::write(&scheduler, "#!/bin/sh\nwhile :; do :; done\n").unwrap();
        fs::set_permissions(&scheduler, fs::Permissions::from_mode(0o755)).unwrap();

        let started = Instant::now();
        let error = inspect_binary_role(
            &scheduler,
            crate::build_identity::InstalledBinaryRole::Scheduler,
            "scheduler executable",
            Duration::from_millis(50),
            1024,
        )
        .unwrap_err()
        .to_string();
        assert!(error.contains("exceeded 50 ms"), "{error}");
        assert!(started.elapsed() < Duration::from_secs(2));

        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    #[cfg(unix)]
    fn identity_probe_refuses_unbounded_output() {
        let root = temp_root("identity-output");
        let scheduler = root.join("fern-scheduler");
        fs::write(&scheduler, "#!/bin/sh\nprintf '0123456789'\n").unwrap();
        fs::set_permissions(&scheduler, fs::Permissions::from_mode(0o755)).unwrap();

        let error = inspect_binary_role(
            &scheduler,
            crate::build_identity::InstalledBinaryRole::Scheduler,
            "scheduler executable",
            Duration::from_secs(1),
            4,
        )
        .unwrap_err()
        .to_string();
        assert!(error.contains("4-byte output limit"), "{error}");

        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    #[cfg(unix)]
    fn identity_probe_refuses_scheduler_scientific_handler_capabilities() {
        let root = temp_root("handler-capability-drift");
        let scheduler = root.join("fern-scheduler");
        let mut scheduler_identity =
            crate::build_identity::installation_identity(crate::build_identity::InstalledBinaryRole::Scheduler);
        let capabilities = fern_recipes::recipe_handler_capabilities();
        scheduler_identity.recipe_handler_capabilities_sha256 =
            Some(crate::build_identity::recipe_handler_capabilities_sha256(&capabilities));
        scheduler_identity.recipe_handler_capabilities = Some(capabilities);
        write_identity_fixture(
            &scheduler,
            "installation-identity",
            &serde_json::to_string(&scheduler_identity).unwrap(),
        );
        let error = inspect_binary_role(
            &scheduler,
            crate::build_identity::InstalledBinaryRole::Scheduler,
            "scheduler executable",
            Duration::from_secs(1),
            64 * 1024,
        )
        .unwrap_err();
        let error_chain = format!("{error:#}");
        assert!(
            error_chain.contains("non-worker installation identity must not carry recipe handler capabilities"),
            "{error_chain}"
        );

        fs::remove_dir_all(root).unwrap();
    }

    /// The C# operator verb owns publication of every prepared control binary.
    /// This crosses that real child route and reaches the installer's first
    /// typed refusal only after both C# publishes and both Go builds succeed.
    #[test]
    #[cfg(unix)]
    fn csharp_scheduler_and_site_publish_as_installable_children() {
        let workspace_root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .unwrap()
            .to_path_buf();
        let scheduler = crate::command_runtime::csharp_scheduler_binary().unwrap();
        let root = temp_root("csharp-prepares-source-set");
        let missing = root.join("missing");
        let deployment_root = root.join("deploy");
        let output = Command::new(scheduler)
            .arg("install-scheduler-service")
            .args(["--registry", missing.to_str().unwrap()])
            .args(["--worker-binary", missing.to_str().unwrap()])
            .args(["--stage-runner-binary", missing.to_str().unwrap()])
            .args(["--experiment-binary", missing.to_str().unwrap()])
            .args(["--deployment-root", deployment_root.to_str().unwrap()])
            .current_dir(&workspace_root)
            .env("FERN_INSTALL_OPERATOR_MODE", "1")
            .env("FERN_INSTALLING_RUN_BINARY", std::env::current_exe().unwrap())
            .env("FERN_INSTALLING_SOURCE_REVISION", "test")
            .output()
            .unwrap();
        let installer_refusal = String::from_utf8_lossy(&output.stderr);
        assert!(
            !output.status.success() && installer_refusal.contains("Registry Home database"),
            "got: {installer_refusal}"
        );
        fs::remove_dir_all(root).unwrap();
    }

}
