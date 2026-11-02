//! Typed JSON receipts required by every committed Attempt.
//!
//! These are small audit records, not a second lifecycle database.  The
//! invocation receipt proves which run was invoked; the environment receipt
//! records the numerical stack the Python worker actually observed.

use std::collections::HashSet;

use fern_json_contract::JsonContract;
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};
use serde_json::Value;

use super::{ContractError, WorkerRequestV1};
use fern_experiment_types::{
    Float32MatmulPrecisionV1, ImplementationRuntimeV1, ModelImplementationCardV1, ModelImplementationHash,
};

pub const RUN_INVOCATION_RECEIPT_SCHEMA: &str = "fern.run_invocation.v3";
pub const WORKER_EXECUTION_ENVIRONMENT_SCHEMA: &str = "fern.execution_environment.v1";

/// Existing Rust-owned invocation envelope, parsed before artifact commit.
///
/// `identity` and `provenance` remain JSON because their nested vocabulary is
/// owned by the run packet and legacy receipt respectively.  Their outer
/// relationship to `run_hash` is nevertheless checked here.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct RunInvocationReceiptV3 {
    pub schema: String,
    pub identity: Value,
    pub provenance: Value,
    pub invocation_hash: String,
    pub invocation_hash_algorithm: String,
}

impl JsonContract for RunInvocationReceiptV3 {
    const SCHEMA_ID: &'static str = RUN_INVOCATION_RECEIPT_SCHEMA;
}

impl RunInvocationReceiptV3 {
    pub fn decode_json(bytes: &[u8]) -> Result<Self, ContractError> {
        fern_json_contract::checked_from_slice(bytes).map_err(|error| {
            ContractError::new(
                "run_invocation_receipt",
                format!("is not valid {RUN_INVOCATION_RECEIPT_SCHEMA} JSON: {error}"),
            )
        })
    }

    pub fn validate_against_request(&self, request: &WorkerRequestV1) -> Result<(), ContractError> {
        request.validate()?;
        if self.schema != RUN_INVOCATION_RECEIPT_SCHEMA {
            return Err(ContractError::new(
                "run_invocation_receipt.schema",
                format!("expected {RUN_INVOCATION_RECEIPT_SCHEMA:?}, found {:?}", self.schema),
            ));
        }
        if !self.identity.is_object() {
            return Err(ContractError::new(
                "run_invocation_receipt.identity",
                "must be the run packet's JSON object",
            ));
        }
        let Some(provenance) = self.provenance.as_object() else {
            return Err(ContractError::new(
                "run_invocation_receipt.provenance",
                "must be a JSON object",
            ));
        };
        for required in ["artifact_dir", "results_root", "written_at"] {
            if provenance
                .get(required)
                .and_then(Value::as_str)
                .is_none_or(str::is_empty)
            {
                return Err(ContractError::new(
                    format!("run_invocation_receipt.provenance.{required}"),
                    "must be a non-empty string",
                ));
            }
        }
        if self.invocation_hash != request.run_hash {
            return Err(ContractError::new(
                "run_invocation_receipt.invocation_hash",
                "does not match the worker request run_hash",
            ));
        }
        if self.invocation_hash_algorithm != "sha256[:16]" {
            return Err(ContractError::new(
                "run_invocation_receipt.invocation_hash_algorithm",
                "must be sha256[:16]",
            ));
        }
        Ok(())
    }

    pub fn canonical_bytes_against(&self, request: &WorkerRequestV1) -> Result<Vec<u8>, ContractError> {
        self.validate_against_request(request)?;
        self.checked_value()
            .and_then(|value| serde_json::to_vec(&value).map_err(Into::into))
            .map_err(|error| {
                ContractError::new(
                    "run_invocation_receipt",
                    format!("cannot canonicalize invocation receipt: {error}"),
                )
            })
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct WorkerPythonEnvironmentV1 {
    pub version: String,
    pub implementation: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct WorkerPlatformEnvironmentV1 {
    pub system: String,
    pub release: String,
    pub machine: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct WorkerTorchEnvironmentV1 {
    pub version: String,
    pub cuda_runtime_version: Option<String>,
    pub cudnn_version: Option<u64>,
    pub deterministic_algorithms: bool,
    pub deterministic_warn_only: Option<bool>,
    pub float32_matmul_precision: String,
    pub cuda_matmul_allow_tf32: bool,
    pub cudnn_allow_tf32: bool,
    pub cudnn_deterministic: bool,
    pub cudnn_benchmark: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct WorkerCudaDeviceV1 {
    pub index: u32,
    pub name: String,
    pub capability: [u32; 2],
    pub total_memory_bytes: u64,
    pub multi_processor_count: u32,
}

/// Numerical environment observed inside the Python worker process.
///
/// This intentionally differs from the richer preflight fingerprint.  The
/// preflight says what Rust saw before launch; this receipt says what the
/// actual numerical process saw while producing outputs.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct WorkerExecutionEnvironmentV1 {
    pub schema: String,
    pub python: WorkerPythonEnvironmentV1,
    pub platform: WorkerPlatformEnvironmentV1,
    pub torch: WorkerTorchEnvironmentV1,
    pub cuda_available: bool,
    pub cuda_current_device: Option<u32>,
    pub cuda_devices: Vec<WorkerCudaDeviceV1>,
}

impl JsonContract for WorkerExecutionEnvironmentV1 {
    const SCHEMA_ID: &'static str = WORKER_EXECUTION_ENVIRONMENT_SCHEMA;
}

pub fn json_contracts() -> Vec<fern_json_contract::ContractRegistration> {
    vec![
        fern_json_contract::ContractRegistration::of::<RunInvocationReceiptV3>(
            "Rust-owned invocation receipt checked against one WorkerRequest",
        ),
        fern_json_contract::ContractRegistration::of::<WorkerExecutionEnvironmentV1>(
            "Numerical environment observed inside the Python worker",
        ),
        fern_json_contract::ContractRegistration::of::<WorkerRequestV1>(
            "Immutable, path-free work order sent from Fern to one worker Attempt",
        ),
        fern_json_contract::ContractRegistration::of::<super::WorkerResultReportV1>(
            "Candidate outputs reported by one worker against its immutable work order",
        ),
        fern_json_contract::ContractRegistration::of::<super::AttemptArtifactCommitV1>(
            "Checked immutable output set committed by one worker Attempt",
        ),
        fern_json_contract::ContractRegistration::of::<super::StageInvocationV1>(
            "Path-free stage invocation bound to independently checked input artifacts",
        ),
        fern_json_contract::ContractRegistration::of::<super::ReportedStageResultV1>(
            "Successful stage result reported by a runner against one invocation",
        ),
        fern_json_contract::ContractRegistration::of::<super::StageReceiptV1>(
            "Serializable stage receipt; parsing it does not make it trusted",
        ),
        fern_json_contract::ContractRegistration::of::<super::StageArtifactMaterializationV1>(
            "Storage-site materialization of one checked stage output; site and path are not scientific identity",
        ),
        fern_json_contract::ContractRegistration::of::<super::StageReturnValidationV1>(
            "Fixed-checker receipt binding scientific identity and checked artifact bytes to one Stage Attempt",
        ),
        fern_json_contract::ContractRegistration::of::<super::StageRunnerRequestV1>(
            "Home intent a Stage runner may execute; not a checked receipt",
        ),
        fern_json_contract::ContractRegistration::of::<super::StageRunnerReportV1>(
            "Runner claim for one Stage Attempt; Home reopens outputs before commit",
        ),
        fern_json_contract::ContractRegistration::of::<super::AttemptMetadataProjectionV1>(
            "Read-only merge of Attempt request, report, Registry digests, and object refs",
        ),
    ]
}

/// A worker-reported numerical environment checked against the immutable
/// implementation declaration that authorized the Attempt.
///
/// Fields are private: parsing a producer's JSON only yields
/// [`WorkerExecutionEnvironmentV1`], never this stronger stage.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CheckedWorkerExecutionEnvironmentV1 {
    reported: WorkerExecutionEnvironmentV1,
    implementation: ModelImplementationHash,
}

impl CheckedWorkerExecutionEnvironmentV1 {
    pub fn reported(&self) -> &WorkerExecutionEnvironmentV1 {
        &self.reported
    }

    pub fn implementation(&self) -> &ModelImplementationHash {
        &self.implementation
    }
}

impl WorkerExecutionEnvironmentV1 {
    pub fn decode_json(bytes: &[u8]) -> Result<Self, ContractError> {
        fern_json_contract::checked_from_slice(bytes).map_err(|error| {
            ContractError::new(
                "worker_execution_environment",
                format!("is not valid {WORKER_EXECUTION_ENVIRONMENT_SCHEMA} JSON: {error}"),
            )
        })
    }

    pub fn validate(&self) -> Result<(), ContractError> {
        if self.schema != WORKER_EXECUTION_ENVIRONMENT_SCHEMA {
            return Err(ContractError::new(
                "worker_execution_environment.schema",
                format!(
                    "expected {WORKER_EXECUTION_ENVIRONMENT_SCHEMA:?}, found {:?}",
                    self.schema
                ),
            ));
        }
        for (field, value) in [
            ("python.version", self.python.version.as_str()),
            ("python.implementation", self.python.implementation.as_str()),
            ("platform.system", self.platform.system.as_str()),
            ("platform.release", self.platform.release.as_str()),
            ("platform.machine", self.platform.machine.as_str()),
            ("torch.version", self.torch.version.as_str()),
            (
                "torch.float32_matmul_precision",
                self.torch.float32_matmul_precision.as_str(),
            ),
        ] {
            if value.trim().is_empty() {
                return Err(ContractError::new(
                    format!("worker_execution_environment.{field}"),
                    "must be non-empty",
                ));
            }
        }
        let mut indices = HashSet::new();
        for device in &self.cuda_devices {
            if !indices.insert(device.index) {
                return Err(ContractError::new(
                    "worker_execution_environment.cuda_devices",
                    format!("CUDA index {} is duplicated", device.index),
                ));
            }
            if device.name.trim().is_empty()
                || device.capability[0] == 0
                || device.total_memory_bytes == 0
                || device.multi_processor_count == 0
            {
                return Err(ContractError::new(
                    "worker_execution_environment.cuda_devices",
                    format!("CUDA index {} has incomplete device facts", device.index),
                ));
            }
        }
        if self.cuda_available {
            let current = self.cuda_current_device.ok_or_else(|| {
                ContractError::new(
                    "worker_execution_environment.cuda_current_device",
                    "is required when CUDA is available",
                )
            })?;
            if !indices.contains(&current) {
                return Err(ContractError::new(
                    "worker_execution_environment.cuda_current_device",
                    format!("CUDA index {current} is absent from cuda_devices"),
                ));
            }
        } else if self.cuda_current_device.is_some() || !self.cuda_devices.is_empty() {
            return Err(ContractError::new(
                "worker_execution_environment.cuda_available",
                "false requires no current device and an empty device list",
            ));
        }
        Ok(())
    }

    pub fn canonical_bytes(&self) -> Result<Vec<u8>, ContractError> {
        self.validate()?;
        self.checked_value()
            .and_then(|value| serde_json::to_vec(&value).map_err(Into::into))
            .map_err(|error| {
                ContractError::new(
                    "worker_execution_environment",
                    format!("cannot canonicalize environment receipt: {error}"),
                )
            })
    }

    /// Compare every numerical-policy field that V1 observes inside PyTorch.
    /// Parameter/autocast dtype and the selected kernel backend are not present
    /// in this receipt yet; live cutover must extend the observed receipt before
    /// claiming those implementation fields were checked.
    pub fn check_against_implementation(
        &self,
        implementation: &ModelImplementationCardV1,
    ) -> Result<CheckedWorkerExecutionEnvironmentV1, ContractError> {
        self.validate()?;
        if implementation.runtime() != ImplementationRuntimeV1::PythonTorch {
            return Err(ContractError::new(
                "worker_execution_environment.runtime",
                "V1 can check only the python_torch implementation runtime",
            ));
        }
        let policy = implementation.numerical_policy();
        let expected_precision = match policy.float32_matmul_precision {
            Float32MatmulPrecisionV1::Highest => "highest",
            Float32MatmulPrecisionV1::High => "high",
            Float32MatmulPrecisionV1::Medium => "medium",
        };
        let comparisons = [
            (
                "torch.float32_matmul_precision",
                self.torch.float32_matmul_precision == expected_precision,
            ),
            (
                "torch.cuda_matmul_allow_tf32",
                self.torch.cuda_matmul_allow_tf32 == policy.cuda_matmul_allow_tf32,
            ),
            (
                "torch.cudnn_allow_tf32",
                self.torch.cudnn_allow_tf32 == policy.cudnn_allow_tf32,
            ),
            (
                "torch.deterministic_algorithms",
                self.torch.deterministic_algorithms == policy.deterministic_algorithms,
            ),
            (
                "torch.cudnn_deterministic",
                self.torch.cudnn_deterministic == policy.cudnn_deterministic,
            ),
            (
                "torch.cudnn_benchmark",
                self.torch.cudnn_benchmark == policy.cudnn_benchmark,
            ),
        ];
        if let Some((field, _)) = comparisons.into_iter().find(|(_, agrees)| !agrees) {
            return Err(ContractError::new(
                format!("worker_execution_environment.{field}"),
                "observed PyTorch setting differs from the registered implementation policy",
            ));
        }
        Ok(CheckedWorkerExecutionEnvironmentV1 {
            reported: self.clone(),
            implementation: implementation.implementation_hash().map_err(|error| {
                ContractError::new("worker_execution_environment.implementation", error.to_string())
            })?,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        ArtifactKey, ArtifactReference, ArtifactRole, ByteDigest, CASE_REALIZATION_SCHEMA, CaseRealization,
        DECLARED_FORECAST_OUTPUT_SCHEMA, DeclaredForecastMeaningV1, DeclaredForecastOutputV1,
        DeclaredPointDerivationV1, ExactRatio, ForecastValueSpace, MetricContractV1, MetricTargetKind,
        ModelImplementationHash, OutputContract, PointKind, SCIENTIFIC_CASE_SCHEMA, ScientificCaseId,
        ScientificCaseReference, ScientificContentDigest, TENSOR_LAYOUT_SCHEMA, TensorAxis, TensorByteOrder,
        TensorDtype, TensorLayout, TensorStorageOrder, WORKER_REQUEST_SCHEMA, WorkerAuthorityReference,
    };
    use fern_experiment_types::{
        DependencyLockHash, NumericalBackendV1, NumericalDtypeV1, RawModelImplementationCardV1, RawNumericalPolicyV1,
        SourceBundleHash,
    };

    fn model_implementation() -> ModelImplementationHash {
        ModelImplementationHash::parse(format!("implementation:sha256:{}", "77".repeat(32))).unwrap()
    }

    fn implementation_card() -> ModelImplementationCardV1 {
        ModelImplementationCardV1::canonicalize(RawModelImplementationCardV1 {
            schema_version: 1,
            architecture_hash: request().model_architecture,
            source_bundle_hash: SourceBundleHash::from_content_bytes(b"receipt-test source"),
            dependency_lock_hash: DependencyLockHash::from_content_bytes(b"receipt-test dependencies"),
            runtime: ImplementationRuntimeV1::PythonTorch,
            numerical_backend: NumericalBackendV1::TorchEager,
            worker_protocol_version: 4,
            numerical_policy: RawNumericalPolicyV1 {
                parameter_dtype: NumericalDtypeV1::Float32,
                autocast_dtype: None,
                float32_matmul_precision: Float32MatmulPrecisionV1::Highest,
                cuda_matmul_allow_tf32: false,
                cudnn_allow_tf32: true,
                deterministic_algorithms: false,
                cudnn_deterministic: false,
                cudnn_benchmark: false,
            },
            active_feature_flags: vec![],
        })
        .unwrap()
    }

    fn execution_environment() -> WorkerExecutionEnvironmentV1 {
        WorkerExecutionEnvironmentV1 {
            schema: WORKER_EXECUTION_ENVIRONMENT_SCHEMA.to_string(),
            python: WorkerPythonEnvironmentV1 {
                version: "3.13.0".to_string(),
                implementation: "CPython".to_string(),
            },
            platform: WorkerPlatformEnvironmentV1 {
                system: "Linux".to_string(),
                release: "6.6".to_string(),
                machine: "x86_64".to_string(),
            },
            torch: WorkerTorchEnvironmentV1 {
                version: "2.8.0".to_string(),
                cuda_runtime_version: Some("12.8".to_string()),
                cudnn_version: Some(91002),
                deterministic_algorithms: false,
                deterministic_warn_only: Some(false),
                float32_matmul_precision: "highest".to_string(),
                cuda_matmul_allow_tf32: false,
                cudnn_allow_tf32: true,
                cudnn_deterministic: false,
                cudnn_benchmark: false,
            },
            cuda_available: true,
            cuda_current_device: Some(0),
            cuda_devices: vec![WorkerCudaDeviceV1 {
                index: 0,
                name: "NVIDIA GeForce RTX 4070 Laptop GPU".to_string(),
                capability: [8, 9],
                total_memory_bytes: 8 * 1024 * 1024 * 1024,
                multi_processor_count: 36,
            }],
        }
    }

    fn request() -> WorkerRequestV1 {
        let scientific_case = ScientificCaseReference {
            case_id: ScientificCaseId::parse("case:receipt-test").unwrap(),
            manifest_digest: ScientificContentDigest::from_bytes(SCIENTIFIC_CASE_SCHEMA.as_bytes()),
        };
        let point_layout = TensorLayout {
            schema: TENSOR_LAYOUT_SCHEMA.to_string(),
            dtype: TensorDtype::Float32,
            byte_order: TensorByteOrder::LittleEndian,
            storage_order: TensorStorageOrder::RowMajor,
            axes: ["case", "forecast_origin", "horizon", "channel"]
                .into_iter()
                .map(|name| TensorAxis::try_new(name, 1).unwrap())
                .collect(),
        };
        let point_contract = DeclaredForecastOutputV1 {
            schema: DECLARED_FORECAST_OUTPUT_SCHEMA.to_string(),
            scientific_cases: vec![scientific_case.clone()],
            forecast_origin_plan: ArtifactReference {
                artifact_key: ArtifactKey::parse("artifact:receipt-test:origins").unwrap(),
                role: ArtifactRole::ForecastOriginPlan,
                content_digest: ScientificContentDigest::from_bytes(b"origins"),
            },
            channel_order: vec!["x".to_string()],
            value_space: ForecastValueSpace::Physical,
            scale_reference: None,
            meaning: DeclaredForecastMeaningV1::DensePoint {
                point_kind: PointKind::NativePointHead,
                derivation: DeclaredPointDerivationV1::NativePointHead {
                    producer_implementation: model_implementation(),
                },
            },
        };
        WorkerRequestV1 {
            schema: WORKER_REQUEST_SCHEMA.to_string(),
            matrix_id: "matrix-receipt-test".to_string(),
            coordinate_id: "coordinate-receipt-test".to_string(),
            attempt_id: "attempt-receipt-test".to_string(),
            run_hash: "0123456789abcdef".to_string(),
            scientific_case,
            realization: CaseRealization {
                schema: CASE_REALIZATION_SCHEMA.to_string(),
                dose: ExactRatio::new(0, 1).unwrap(),
                final_input_content_digest: ScientificContentDigest::from_bytes(b"input"),
                retained_final_input: None,
            },
            model_architecture: crate::ModelArchitectureHash::parse(format!("architecture:sha256:{}", "22".repeat(32)))
                .unwrap(),
            implementation: model_implementation(),
            execution_profile_revision: ByteDigest::from_bytes(b"profile").as_str().to_string(),
            authority: WorkerAuthorityReference {
                coordinator_lease_id: "coordinator-receipt-test".to_string(),
                coordinator_epoch: 1,
                coordinate_lease_id: "coordinate-receipt-test".to_string(),
                coordinate_epoch: 1,
                resource_lease_id: "resource-receipt-test".to_string(),
                resource_epoch: 1,
            },
            metric_contract: MetricContractV1::time_series_v1(vec![MetricTargetKind::ObservedCorrupted]).unwrap(),
            outputs: vec![
                OutputContract::required(
                    ArtifactKey::parse("artifact:receipt-test:metrics").unwrap(),
                    ArtifactRole::CompactMetrics,
                    None,
                ),
                OutputContract::required(
                    ArtifactKey::parse("artifact:receipt-test:invocation").unwrap(),
                    ArtifactRole::InvocationReceipt,
                    None,
                ),
                OutputContract::required(
                    ArtifactKey::parse("artifact:receipt-test:environment").unwrap(),
                    ArtifactRole::ExecutionEnvironment,
                    None,
                ),
                OutputContract::required_forecast(
                    ArtifactKey::parse("artifact:receipt-test:point").unwrap(),
                    ArtifactRole::DensePointForecast,
                    point_layout,
                    point_contract,
                ),
            ],
        }
    }

    #[test]
    fn invocation_binds_the_current_run_and_environment_checks_cuda_consistency() {
        let request = request();
        let invocation = RunInvocationReceiptV3 {
            schema: RUN_INVOCATION_RECEIPT_SCHEMA.to_string(),
            identity: serde_json::json!({"context": {}}),
            provenance: serde_json::json!({
                "artifact_dir": "/tmp/artifacts/run",
                "results_root": "/tmp/results",
                "written_at": "2026-08-06T12:00:00+00:00"
            }),
            invocation_hash: request.run_hash.clone(),
            invocation_hash_algorithm: "sha256[:16]".to_string(),
        };
        invocation.validate_against_request(&request).unwrap();
        let mut wrong_run = invocation.clone();
        wrong_run.invocation_hash = "fedcba9876543210".to_string();
        assert!(wrong_run.validate_against_request(&request).is_err());

        let environment = execution_environment();
        environment.validate().unwrap();
        let mut inconsistent = environment;
        inconsistent.cuda_current_device = Some(1);
        assert!(inconsistent.validate().is_err());
    }

    #[test]
    fn environment_becomes_checked_only_when_observed_settings_match_the_implementation() {
        let environment = execution_environment();
        let implementation = implementation_card();
        let checked = environment.check_against_implementation(&implementation).unwrap();
        assert_eq!(checked.reported(), &environment);
        assert_eq!(checked.implementation(), &implementation.implementation_hash().unwrap());

        let mut drifted = environment;
        drifted.torch.cuda_matmul_allow_tf32 = true;
        let error = drifted.check_against_implementation(&implementation).unwrap_err();
        assert_eq!(error.field, "worker_execution_environment.torch.cuda_matmul_allow_tf32");
    }
}
