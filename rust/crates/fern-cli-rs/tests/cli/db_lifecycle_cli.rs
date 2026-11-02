use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::OnceLock;
use std::time::{SystemTime, UNIX_EPOCH};

use serde_json::{Value, json};

fn workspace_root() -> &'static Path {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("fern-cli-rs lives in the workspace")
}

fn go_home_binary() -> &'static PathBuf {
    static HOME: OnceLock<PathBuf> = OnceLock::new();
    HOME.get_or_init(|| {
        let output = std::env::temp_dir().join(format!("fern-home-cli-tests-{}", std::process::id()));
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

fn invoke_go_home(registry: &Path, operation: &str, input: &Value) -> Value {
    let mut child = Command::new(go_home_binary())
        .args([operation, "--registry"])
        .arg(registry)
        .stdin(std::process::Stdio::piped())
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::piped())
        .spawn()
        .expect("start fern-home CLI test writer");
    serde_json::to_writer(child.stdin.as_mut().unwrap(), input).unwrap();
    drop(child.stdin.take());
    let output = child.wait_with_output().unwrap();
    assert!(
        output.status.success(),
        "fern-home {operation} failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    serde_json::from_slice(&output.stdout).unwrap()
}

fn migrate_registry(registry: &Path) {
    let receipt = invoke_go_home(registry, "registry-apply-migrations", &json!({}));
    assert_eq!(receipt["schema"], "fern.registry_migrations.v1");
    assert_eq!(receipt["registry"], registry.to_str().unwrap());
}

fn csharp_registry_binary() -> PathBuf {
    let binary = workspace_root().join("csharp/Fern.Registry.Cli/bin/Debug/net9.0/fern-registry");
    assert!(
        binary.is_file(),
        "C# Registry test binary is missing at {}",
        binary.display()
    );
    binary
}

fn invoke_csharp_registry(registry: &Path, operation: &str, input: &Value) -> Value {
    let mut child = Command::new(csharp_registry_binary())
        .args([operation, "--registry"])
        .arg(registry)
        .stdin(std::process::Stdio::piped())
        .stdout(std::process::Stdio::piped())
        .stderr(std::process::Stdio::piped())
        .spawn()
        .expect("start C# Registry fixture authority");
    serde_json::to_writer(child.stdin.as_mut().unwrap(), input).unwrap();
    drop(child.stdin.take());
    let output = child.wait_with_output().unwrap();
    assert!(
        output.status.success(),
        "C# Registry {operation} failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    serde_json::from_slice(&output.stdout).unwrap()
}

/// Every file under this package's `src/` whose text contains `needle`, as
/// paths relative to `src/`, sorted.
///
/// A source scan rather than a type-level check because what is being defended
/// is "only one place in the crate does this", which no signature can state.
fn source_files_containing(needle: &str) -> Vec<String> {
    let src = Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
    let mut found = Vec::new();
    let mut directories = vec![src.clone()];
    while let Some(directory) = directories.pop() {
        for entry in fs::read_dir(&directory).expect("the package has a src directory") {
            let path = entry.expect("a readable directory entry").path();
            if path.is_dir() {
                directories.push(path);
                continue;
            }
            if path.extension().and_then(|extension| extension.to_str()) != Some("rs") {
                continue;
            }
            let text = fs::read_to_string(&path).expect("a readable Rust source file");
            if text.contains(needle) {
                found.push(
                    path.strip_prefix(&src)
                        .expect("every scanned file is under src")
                        .to_string_lossy()
                        .into_owned(),
                );
            }
        }
    }
    found.sort();
    found
}

fn temp_case_dir(name: &str) -> PathBuf {
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
    let dir = std::env::temp_dir().join(format!("fern_cli_rs_{name}_{}_{}", std::process::id(), nanos));
    fs::create_dir_all(&dir).unwrap();
    dir
}

