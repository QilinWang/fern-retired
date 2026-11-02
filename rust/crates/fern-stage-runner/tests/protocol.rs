//! Protocol tests for the unprivileged Stage return checker.

use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

use fern_artifact::{
    ArtifactKey, ArtifactRole, OUTPUTS_DIR, REPORT_FILE, REPORTED_STAGE_RESULT_SCHEMA, REQUEST_FILE,
    ReportedArtifactV1, ReportedStageResultV1, STAGE_INVOCATION_SCHEMA, STAGE_RUNNER_PROTOCOL,
    STAGE_RUNNER_REPORT_SCHEMA, STAGE_RUNNER_REQUEST_SCHEMA, StageReceiptV1, StageRunnerOutcomeV1, StageRunnerReportV1,
    StageRunnerRequestV1, StageRunnerSuccessV1, require_workspace_relative,
};
use fern_experiment_types::{Sha256Digest, StageImplementationRefV1, StageOutputSpecV1, StageRunnerV1};

fn checker_bin() -> PathBuf {
    PathBuf::from(env!("CARGO_BIN_EXE_fern-stage-runner"))
}

fn temp_dir(name: &str) -> PathBuf {
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    let dir = std::env::temp_dir().join(format!("fern_stage_checker_{name}_{}_{}", std::process::id(), nanos));
    fs::create_dir_all(&dir).unwrap();
    dir
}

fn prepare_workspace(root: &Path) -> PathBuf {
    let workspace = root.join("workspace");
    for name in ["request", "inputs", OUTPUTS_DIR, "report", "logs"] {
        fs::create_dir_all(workspace.join(name)).unwrap();
    }
    workspace
}

fn write_runner_return(workspace: &Path) -> PathBuf {
    let output_bytes = b"python postprocess result\n";
    let output_path = workspace.join(OUTPUTS_DIR).join("metrics.json");
    fs::write(&output_path, output_bytes).unwrap();

    let implementation_digest = Sha256Digest::from_bytes(b"frozen-python-postprocess-bundle");
    let declared_outputs = BTreeMap::from([(
        "metrics".to_string(),
        StageOutputSpecV1 {
            role: "metrics".to_string(),
            schema_id: Some("fern.sequence_forecast_metrics.v1".to_string()),
        },
    )]);
    let invocation = fern_artifact::StageInvocationV1 {
        schema: STAGE_INVOCATION_SCHEMA.to_string(),
        plan_digest: Sha256Digest::from_bytes(b"plan"),
        stage_id: "postprocess".to_string(),
        stage_spec_digest: Sha256Digest::from_bytes(b"spec"),
        execution_key: fern_artifact::StageExecutionKey::from_bytes(b"key"),
        implementation: StageImplementationRefV1 {
            runner: StageRunnerV1::PythonModule,
            entrypoint: "fr_tr".to_string(),
            implementation_digest: implementation_digest.clone(),
        },
        parameters: serde_json::json!({}),
        resolved_inputs: BTreeMap::new(),
        declared_outputs: declared_outputs.clone(),
    };
    let request = StageRunnerRequestV1 {
        schema: STAGE_RUNNER_REQUEST_SCHEMA.to_string(),
        protocol_version: STAGE_RUNNER_PROTOCOL.to_string(),
        stage_attempt_id: "attempt-postprocess".to_string(),
        handler: "fern.postprocess".to_string(),
        invocation_digest: invocation.digest().unwrap(),
        execution_key: invocation.execution_key.clone(),
        implementation_digest: implementation_digest.clone(),
        invocation,
        input_files: BTreeMap::new(),
        declared_outputs,
    };
    request.validate().unwrap();
    let request_path = workspace.join(REQUEST_FILE);
    fs::write(
        &request_path,
        fern_json_contract::canonical_json_bytes(&request).unwrap(),
    )
    .unwrap();

    let reported = ReportedArtifactV1::from_payloads(
        ArtifactKey::parse("artifact:metrics").unwrap(),
        ArtifactRole::Metrics,
        "application/json",
        output_bytes,
        output_bytes,
        None,
        None,
    )
    .unwrap();
    let result = ReportedStageResultV1 {
        schema: REPORTED_STAGE_RESULT_SCHEMA.to_string(),
        invocation_digest: request.invocation_digest.clone(),
        execution_key: request.execution_key.clone(),
        implementation_digest,
        outputs: BTreeMap::from([("metrics".to_string(), reported)]),
    };
    let report = StageRunnerReportV1 {
        schema: STAGE_RUNNER_REPORT_SCHEMA.to_string(),
        stage_attempt_id: request.stage_attempt_id.clone(),
        request_digest: request.digest().unwrap(),
        outcome: StageRunnerOutcomeV1::Success(StageRunnerSuccessV1 {
            result,
            output_paths: BTreeMap::from([("metrics".to_string(), "outputs/metrics.json".to_string())]),
        }),
    };
    report.validate().unwrap();
    fs::write(
        workspace.join(REPORT_FILE),
        fern_json_contract::canonical_json_bytes(&report).unwrap(),
    )
    .unwrap();
    request_path
}

fn check_return(request: &Path, workspace: &Path) -> std::process::Output {
    Command::new(checker_bin())
        .args([
            "check-return",
            "--request",
            request.to_str().unwrap(),
            "--workspace",
            workspace.to_str().unwrap(),
        ])
        .output()
        .unwrap()
}

#[test]
fn checker_accepts_python_postprocess_return() {
    let dir = temp_dir("success");
    let workspace = prepare_workspace(&dir);
    let request = write_runner_return(&workspace);
    let output = check_return(&request, &workspace);
    assert!(output.status.success(), "{}", String::from_utf8_lossy(&output.stderr));
    let receipt: StageReceiptV1 =
        fern_json_contract::checked_from_slice(&fs::read(workspace.join("report/stage_receipt.v1.json")).unwrap())
            .unwrap();
    // CHG 2026-08-26: StageReceiptV1 lost stage_attempt_id when 41344f35 cut the
    // checked Stage return over to Go, and this assertion has not compiled
    // since. The attempt id was standing in for the real question -- is this
    // receipt for the execution that was asked for -- and execution_key answers
    // it directly, being a digest over the invocation rather than a label
    // beside it. So the repair asserts the stronger fact the new shape offers,
    // not a weaker one.
    let asked: StageRunnerRequestV1 =
        fern_json_contract::checked_from_slice(&fs::read(&request).unwrap()).unwrap();
    assert_eq!(receipt.execution_key, asked.execution_key);
    assert_eq!(receipt.stage_id, asked.invocation.stage_id);
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn checker_refuses_changed_output_bytes() {
    let dir = temp_dir("changed_output");
    let workspace = prepare_workspace(&dir);
    let request = write_runner_return(&workspace);
    fs::write(
        workspace.join(OUTPUTS_DIR).join("metrics.json"),
        b"changed after runner exit",
    )
    .unwrap();

    let output = check_return(&request, &workspace);
    assert!(!output.status.success());
    assert!(
        String::from_utf8_lossy(&output.stderr).contains("artifact.byte_count"),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(!workspace.join("report/stage_receipt.v1.json").exists());
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn checker_refuses_runner_failure_report() {
    let dir = temp_dir("failure_report");
    let workspace = prepare_workspace(&dir);
    let request = write_runner_return(&workspace);
    let mut report: StageRunnerReportV1 =
        fern_json_contract::checked_from_slice(&fs::read(workspace.join(REPORT_FILE)).unwrap()).unwrap();
    report.outcome = StageRunnerOutcomeV1::Failure(fern_artifact::StageRunnerFailureV1 {
        code: "python_failed".to_string(),
        message: "postprocess refused input".to_string(),
    });
    fs::write(
        workspace.join(REPORT_FILE),
        fern_json_contract::canonical_json_bytes(&report).unwrap(),
    )
    .unwrap();

    let output = check_return(&request, &workspace);
    assert!(!output.status.success());
    assert!(String::from_utf8_lossy(&output.stderr).contains("postprocess refused input"));
    fs::remove_dir_all(dir).unwrap();
}

#[test]
fn report_paths_must_stay_under_outputs() {
    assert!(require_workspace_relative("outputs", "outputs/metrics.json").is_ok());
    assert!(require_workspace_relative("outputs", "../outputs/metrics.json").is_err());
    assert!(require_workspace_relative("outputs", "/tmp/metrics.json").is_err());
}
