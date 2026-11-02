//! Private C# Registry subprocess. SQL for domains that C# already owns
//! goes through this; the calling module keeps the computation.

use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

use serde::Serialize;
use serde::de::DeserializeOwned;

/// One local Registry child cannot hold the Run Plan authority indefinitely.
/// The boundary includes process startup, request-pipe handoff, SQLite work,
/// and receipt/refusal production.
const CSHARP_REGISTRY_PROCESS_DEADLINE: Duration = Duration::from_secs(30);
const CSHARP_REGISTRY_PROCESS_POLL_INTERVAL: Duration = Duration::from_millis(25);

pub fn run_json<T: Serialize, R: DeserializeOwned>(
    operation: &str,
    registry: &Path,
    input: &T,
) -> Result<R, String> {
    let binary = binary()?;
    let bytes = run_bytes_with_binary(&binary, operation, registry, input)?;
    serde_json::from_slice(&bytes).map_err(|error| format!("{operation} receipt is not JSON: {error}"))
}

pub(crate) fn run_optional_json<T: Serialize, R: DeserializeOwned>(
    operation: &str,
    registry: &Path,
    input: &T,
) -> Result<Option<R>, String> {
    let binary = binary()?;
    let bytes = run_bytes_with_binary(&binary, operation, registry, input)?;
    let trimmed = std::str::from_utf8(&bytes)
        .map_err(|error| format!("{operation} receipt is not utf-8: {error}"))?
        .trim();
    if trimmed.is_empty() {
        return Err(format!("{operation} returned no receipt"));
    }
    if trimmed == "null" {
        return Ok(None);
    }
    serde_json::from_str(trimmed)
        .map(|value| Some(value))
        .map_err(|error| format!("{operation} receipt is not JSON: {error}"))
}

pub(crate) fn run_bytes<T: Serialize>(operation: &str, registry: &Path, input: &T) -> Result<Vec<u8>, String> {
    let binary = binary()?;
    run_bytes_with_binary(&binary, operation, registry, input)
}

fn run_bytes_with_binary<T: Serialize>(
    binary: &Path,
    operation: &str,
    registry: &Path,
    input: &T,
) -> Result<Vec<u8>, String> {
    let input_json = serde_json::to_vec(input).map_err(|error| format!("{operation} request is not JSON: {error}"))?;
    let output = invoke_process_before_deadline(
        binary,
        operation,
        registry,
        input_json,
        CSHARP_REGISTRY_PROCESS_DEADLINE,
    )?;
    if !output.status.success() {
        return Err(format!(
            "{operation}: {}",
            String::from_utf8_lossy(&output.stderr).trim()
        ));
    }
    Ok(output.stdout)
}

/// Start the actual C# child and bound all of its request/response handoff.
/// Writing occurs on a separate thread so an oversized JSON request cannot
/// block before the named child deadline begins.
fn invoke_process_before_deadline(
    binary: &Path,
    operation: &str,
    registry: &Path,
    input_json: Vec<u8>,
    deadline: Duration,
) -> Result<std::process::Output, String> {
    let started = Instant::now();
    let mut child = Command::new(&binary)
        .args([operation, "--registry"])
        .arg(registry)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| format!("cannot start {}: {error}", binary.display()))?;
    let mut stdin = child
        .stdin
        .take()
        .ok_or_else(|| format!("{operation} did not expose stdin"))?;
    let input_writer = std::thread::spawn(move || stdin.write_all(&input_json));
    let output = collect_process_output_before_deadline(child, operation, started, deadline);
    let input_result = input_writer
        .join()
        .map_err(|_| format!("{operation} request writer panicked"))?;

    let output = output?;
    if output.status.success() {
        input_result.map_err(|error| format!("cannot write {operation} request: {error}"))?;
    }
    Ok(output)
}

/// Kill and reap a child that outlives its explicit Registry handoff budget.
fn collect_process_output_before_deadline(
    mut child: std::process::Child,
    operation: &str,
    started: Instant,
    deadline: Duration,
) -> Result<std::process::Output, String> {
    let child_id = child.id();
    loop {
        match child
            .try_wait()
            .map_err(|error| format!("wait for {child_id}: {error}"))?
        {
            Some(_) => {
                return child
                    .wait_with_output()
                    .map_err(|error| format!("wait for {child_id}: {error}"));
            }
            None if started.elapsed() >= deadline => {
                let _ = child.kill();
                let _ = child.wait_with_output();
                return Err(format!(
                    "C# Registry deadline {deadline:?} exhausted while running {operation}"
                ));
            }
            None => std::thread::sleep(CSHARP_REGISTRY_PROCESS_POLL_INTERVAL),
        }
    }
}

fn binary() -> Result<PathBuf, String> {
    if let Ok(path) = std::env::var("FERN_REGISTRY") {
        let path = PathBuf::from(path);
        if path.is_file() {
            return Ok(path);
        }
        return Err(format!("FERN_REGISTRY is not a file: {}", path.display()));
    }
    if let Some(path) = look_path("fern-registry") {
        return Ok(path);
    }
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .ok_or_else(|| "cannot locate the repository root for fern-registry".to_string())?
        .to_path_buf();
    let debug = root.join("csharp/Fern.Registry.Cli/bin/Debug/net9.0/fern-registry");
    let release = root.join("csharp/Fern.Registry.Cli/bin/Release/net9.0/fern-registry");
    match (debug.is_file(), release.is_file()) {
        (true, true) => {
            let debug_time = std::fs::metadata(&debug)
                .and_then(|meta| meta.modified())
                .map_err(|error| error.to_string())?;
            let release_time = std::fs::metadata(&release)
                .and_then(|meta| meta.modified())
                .map_err(|error| error.to_string())?;
            Ok(if release_time >= debug_time { release } else { debug })
        }
        (true, false) => Ok(debug),
        (false, true) => Ok(release),
        (false, false) => Err(format!(
            "fern-registry is not on PATH and was not built under {}",
            root.join("csharp/Fern.Registry.Cli/bin").display()
        )),
    }
}

fn look_path(name: &str) -> Option<PathBuf> {
    let path = std::env::var_os("PATH")?;
    std::env::split_paths(&path).find_map(|directory| {
        let candidate = directory.join(name);
        candidate.is_file().then_some(candidate)
    })
}

#[cfg(test)]
pub(crate) fn migrate_test_registry_via_home(registry: &Path) {
    let receipt: serde_json::Value = run_json(
        "apply-registry-migrations",
        registry,
        &serde_json::json!({}),
    )
    .expect("C# apply-registry-migrations");
    assert_eq!(receipt["schema"], "fern.registry_migrations.v1");
    assert_eq!(receipt["registry"], registry.display().to_string());
}

#[cfg(test)]
mod tests {
    use serde_json::{Value, json};
    use std::ffi::OsString;
    use std::sync::Mutex;

    use super::*;

    static FERN_REGISTRY_ENV_LOCK: Mutex<()> = Mutex::new(());

    struct PinnedRegistryEnvironment {
        previous: Option<OsString>,
    }

    impl PinnedRegistryEnvironment {
        fn install(binary: &Path) -> Self {
            let previous = std::env::var_os("FERN_REGISTRY");
            // SAFETY: the test holds the process-wide FERN_REGISTRY lock and
            // restores the prior value in Drop before releasing it.
            unsafe { std::env::set_var("FERN_REGISTRY", binary) };
            Self { previous }
        }
    }

    impl Drop for PinnedRegistryEnvironment {
        fn drop(&mut self) {
            // SAFETY: this restores the process-wide value installed by the
            // test before another test may acquire FERN_REGISTRY_ENV_LOCK.
            unsafe {
                if let Some(previous) = &self.previous {
                    std::env::set_var("FERN_REGISTRY", previous);
                } else {
                    std::env::remove_var("FERN_REGISTRY");
                }
            }
        }
    }

    fn tested_registry_binary() -> PathBuf {
        let binary = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .expect("fern_run lives below the workspace root")
            .join("csharp/Fern.Registry.Cli/bin/Debug/net9.0/fern-registry");
        assert!(
            binary.is_file(),
            "build and test the pinned C# Registry executable before this Rust process test: {}",
            binary.display()
        );
        binary
    }

    fn run_plan_request(profile_revision: &str) -> Value {
        let plan_name = "csharp-registry-process-plan";
        let definition_json = concat!(
            "{\"schema\":\"fern.run_plan.v1\",",
            "\"plan_name\":\"csharp-registry-process-plan\",",
            "\"steps\":[{\"step_id\":\"stage-0\",\"lane\":\"test-lane\",",
            "\"preset\":\"test-preset\",\"model\":\"test-model\",",
            "\"limit\":1,\"keep_forecast_origins\":false}]}"
        );
        let step_json = concat!(
            "{\"step_id\":\"stage-0\",\"lane\":\"test-lane\",",
            "\"preset\":\"test-preset\",\"model\":\"test-model\",",
            "\"limit\":1,\"keep_forecast_origins\":false}"
        );
        let definition_digest = crate::lifecycle::sha256_hex(definition_json.as_bytes());
        let plan_id = crate::lifecycle::sha256_hex(
            format!(
                "fern.run_plan.v1\u{1f}{plan_name}\u{1f}{definition_digest}\u{1f}local-test-profile\u{1f}{profile_revision}"
            )
            .as_bytes(),
        );
        json!({
            "plan_id": plan_id,
            "plan_name": plan_name,
            "definition_json": definition_json,
            "definition_digest": definition_digest,
            "execution_profile_id": "local-test-profile",
            "execution_profile_revision": profile_revision,
            "created_by": "csharp-registry-process-test",
            "actor_kind": "cli",
            "actor_id": "csharp-registry-process-test",
            "steps": [{
                "step_id": "stage-0",
                "step_json": step_json
            }],
            "instances": []
        })
    }

    #[test]
    fn pinned_registry_run_plan_process_preserves_exact_receipts_refusal_and_row_census() {
        let _environment_lock = FERN_REGISTRY_ENV_LOCK
            .lock()
            .expect("lock FERN_REGISTRY for this process test");
        let home = tempfile::tempdir().expect("create Registry Home");
        let registry = home.path().join("fern.db");
        migrate_test_registry_via_home(&registry);
        let pinned_binary = tested_registry_binary();
        let _pinned_registry = PinnedRegistryEnvironment::install(&pinned_binary);
        assert_eq!(binary().expect("resolve the pinned FERN_REGISTRY child"), pinned_binary);
        let accepted = run_plan_request("revision-1");
        let accepted_plan_id = accepted["plan_id"]
            .as_str()
            .expect("the fixture carries its derived plan id");

        let registered = run_bytes("register-run-plan", &registry, &accepted)
            .expect("the pinned Registry child accepts the initial immutable Run Plan");
        assert_eq!(
            registered,
            format!("{{\"plan_id\":\"{accepted_plan_id}\",\"status\":\"registered\"}}\n").as_bytes()
        );

        let raw_projection = run_bytes("project-run-plan", &registry, &json!({ "plan_id": accepted_plan_id }))
            .expect("the real C# child projects the registered Run Plan");
        assert_eq!(
            raw_projection,
            format!(
                "{{\"plan_id\":\"{accepted_plan_id}\",\"plan_name\":\"csharp-registry-process-plan\",\"execution_profile_id\":\"local-test-profile\",\"execution_profile_revision\":\"revision-1\",\"current_attempt_id\":null,\"current_lease_expires_at_ms\":null,\"latest_attempt_state\":null,\"latest_attempt_detail\":null,\"dependencies\":[],\"steps\":[{{\"ordinal\":0,\"step_id\":\"stage-0\",\"lane\":\"test-lane\",\"preset\":\"test-preset\",\"model\":\"test-model\",\"matrix_id\":null,\"state\":\"planned\",\"failure_detail\":null}}],\"stage_nodes\":[]}}\n"
            )
            .as_bytes(),
            "the raw child wire must preserve explicit nulls, field order, and the complete projection"
        );

        let replay = run_bytes("register-run-plan", &registry, &accepted)
            .expect("the pinned Registry child accepts identical replay");
        assert_eq!(
            replay,
            format!("{{\"plan_id\":\"{accepted_plan_id}\",\"status\":\"already_registered\"}}\n").as_bytes()
        );

        let changed = run_plan_request("revision-2");
        let raw_refusal = invoke_process_before_deadline(
            &binary().expect("resolve the pinned FERN_REGISTRY child for raw refusal capture"),
            "register-run-plan",
            &registry,
            serde_json::to_vec(&changed).expect("serialize changed immutable request"),
            CSHARP_REGISTRY_PROCESS_DEADLINE,
        )
        .expect("the Registry child itself exits with its typed refusal");
        assert_eq!(raw_refusal.status.code(), Some(2));
        assert_eq!(raw_refusal.stdout, b"");
        assert_eq!(
            raw_refusal.stderr,
            format!("fern-registry: plan name csharp-registry-process-plan already carries a different immutable definition (plan {accepted_plan_id}); retry that plan as a new attempt, or declare a revision under a new name\n").as_bytes()
        );

        let refusal = run_bytes("register-run-plan", &registry, &changed)
            .expect_err("a typed Registry refusal must not become a Rust success");
        assert_eq!(
            refusal,
            format!(
                "register-run-plan: fern-registry: plan name csharp-registry-process-plan already carries a different immutable definition (plan {accepted_plan_id}); retry that plan as a new attempt, or declare a revision under a new name"
            )
        );
    }
}
