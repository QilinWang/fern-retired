//! Physical Parquet adapter for the release-only metrics table.
//!
//! The typed [`ReleaseMetricsTableV1`] remains the scientific content.
//! Parquet is a rebuildable, query-oriented encoding: member-wide contracts
//! live once in file metadata, while identities and numeric values that vary
//! inside a release are ordinary columns. Decoding reconstructs the typed
//! object and rejects every duplicated query column that disagrees with it.

use std::collections::{BTreeMap, HashMap};
use std::sync::Arc;

use arrow_array::{
    Array, ArrayRef, Float64Array, Int64Array, LargeStringArray, RecordBatch, StringArray, StringViewArray, UInt64Array,
};
use arrow_schema::{DataType, Field, Schema};
use arrow_select::concat::concat_batches;
use bytes::Bytes;
use fern_artifact::{
    ArtifactReference, CompactMetricsCaseV1, CompactMetricsV1, LedgerDiagnosticV1, MetricContractV1,
    MetricSourceRepresentation, MetricSpecV1, MetricTargetKind, MetricValueV1, NumericalToleranceV1,
    ReportedArtifactV1, RiskPrimitiveV1, ScientificCaseId, ScientificContentDigest,
};
use fern_experiment_types::{DeclaredReplicaPurpose, DeclaredReplicaV1, ModelImplementationHash};
use parquet::arrow::{arrow_reader::ParquetRecordBatchReaderBuilder, arrow_writer::ArrowWriter};
use parquet::basic::{Compression, ZstdLevel};
use parquet::file::metadata::KeyValue;
use parquet::file::properties::WriterProperties;
use serde::{Deserialize, Serialize, de::DeserializeOwned};

use super::{
    AcceptanceDecisionDigest, AcceptanceDecisionV1, CheckedCompactMetricsV1, EvidenceError, EvidencePolicyCardV1,
    EvidencePolicyDigest, RELEASE_METRICS_TABLE_SCHEMA, ReleaseMetricsMemberV1, ReleaseMetricsTableV1,
};

const METADATA_SCHEMA_KEY: &str = "fern.release_metrics.schema";
const METADATA_MANIFEST_KEY: &str = "fern.release_metrics.manifest";
const METADATA_CONTENT_DIGEST_KEY: &str = "fern.release_metrics.content_digest";

const RELEASE_METRIC_COLUMNS: &[&str] = &[
    "decision_digest",
    "matrix_id",
    "coordinate_id",
    "target_hash",
    "replica_ordinal",
    "replica_purpose",
    "attempt_id",
    "run_hash",
    "implementation_hash",
    "case_id",
    "case_realization_digest",
    "element_count",
    "observed_squared_error_sum",
    "observed_absolute_error_sum",
    "reference_target_kind",
    "reference_squared_error_sum",
    "injected_noise_squared_sum",
    "reference_residual_noise_inner_product_sum",
    "ledger_residual_sum",
    "metric_id",
    "metric_target_kind",
    "metric_source_representation",
    "ept_threshold_numerator",
    "ept_threshold_denominator",
    "metric_spec_json",
    "metric_value",
];

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct ReleaseMetricsFileManifestV1 {
    schema: String,
    policy_digest: EvidencePolicyDigest,
    members: Vec<ReleaseMetricsMemberManifestV1>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct ReleaseMetricsMemberManifestV1 {
    decision_digest: AcceptanceDecisionDigest,
    compact_metrics_reported_artifact: ReportedArtifactV1,
    compact_schema: String,
    request_digest: ScientificContentDigest,
    metric_contract: MetricContractV1,
    numerical_tolerance: NumericalToleranceV1,
    checked_source_artifacts: Vec<ArtifactReference>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct MemberIdentityV1 {
    matrix_id: String,
    coordinate_id: String,
    target_hash: String,
    replica: Option<DeclaredReplicaV1>,
    attempt_id: String,
    run_hash: String,
    implementation: ModelImplementationHash,
}

#[derive(Debug, Clone, PartialEq)]
struct CaseAccumulatorV1 {
    case_id: ScientificCaseId,
    case_realization_digest: ScientificContentDigest,
    risk_primitive: RiskPrimitiveV1,
    derived_diagnostic: LedgerDiagnosticV1,
    metric_values: Vec<MetricValueV1>,
}

#[derive(Default)]
struct MemberAccumulatorV1 {
    identity: Option<MemberIdentityV1>,
    cases: BTreeMap<String, CaseAccumulatorV1>,
}

#[derive(Default)]
struct ReleaseMetricColumns {
    decision_digest: Vec<String>,
    matrix_id: Vec<String>,
    coordinate_id: Vec<String>,
    target_hash: Vec<String>,
    replica_ordinal: Vec<Option<i64>>,
    replica_purpose: Vec<Option<String>>,
    attempt_id: Vec<String>,
    run_hash: Vec<String>,
    implementation_hash: Vec<String>,
    case_id: Vec<String>,
    case_realization_digest: Vec<String>,
    element_count: Vec<u64>,
    observed_squared_error_sum: Vec<f64>,
    observed_absolute_error_sum: Vec<f64>,
    reference_target_kind: Vec<Option<String>>,
    reference_squared_error_sum: Vec<Option<f64>>,
    injected_noise_squared_sum: Vec<Option<f64>>,
    reference_residual_noise_inner_product_sum: Vec<Option<f64>>,
    ledger_residual_sum: Vec<Option<f64>>,
    metric_id: Vec<String>,
    metric_target_kind: Vec<String>,
    metric_source_representation: Vec<String>,
    ept_threshold_numerator: Vec<Option<u64>>,
    ept_threshold_denominator: Vec<Option<u64>>,
    metric_spec_json: Vec<String>,
    metric_value: Vec<f64>,
}

fn string_values(values: &[String]) -> ArrayRef {
    Arc::new(StringArray::from_iter_values(values.iter().map(String::as_str)))
}

fn optional_string_values(values: &[Option<String>]) -> ArrayRef {
    Arc::new(StringArray::from_iter(values.iter().map(|value| value.as_deref())))
}

impl ReleaseMetricColumns {
    fn push(
        &mut self,
        member: &ReleaseMetricsMemberV1,
        case: &CompactMetricsCaseV1,
        metric: &MetricValueV1,
    ) -> Result<(), EvidenceError> {
        let (metric_id, target_kind, source_representation, threshold) = metric_query_identity(&metric.spec);
        self.decision_digest.push(member.decision_digest.as_str().to_string());
        self.matrix_id.push(member.matrix_id.clone());
        self.coordinate_id.push(member.coordinate_id.clone());
        self.target_hash.push(member.target_hash.clone());
        self.replica_ordinal
            .push(member.replica.as_ref().map(|replica| replica.ordinal));
        self.replica_purpose.push(
            member
                .replica
                .as_ref()
                .map(|replica| replica.purpose.value().to_string()),
        );
        self.attempt_id.push(member.attempt_id.clone());
        self.run_hash.push(member.run_hash.clone());
        self.implementation_hash
            .push(member.implementation.as_str().to_string());
        self.case_id.push(case.case_id.as_str().to_string());
        self.case_realization_digest
            .push(case.case_realization_digest.as_str().to_string());
        self.element_count.push(case.risk_primitive.element_count);
        self.observed_squared_error_sum
            .push(case.risk_primitive.observed_squared_error_sum);
        self.observed_absolute_error_sum
            .push(case.risk_primitive.observed_absolute_error_sum);
        self.reference_target_kind.push(
            case.risk_primitive
                .reference_target_kind
                .map(enum_wire_name)
                .transpose()?,
        );
        self.reference_squared_error_sum
            .push(case.risk_primitive.reference_squared_error_sum);
        self.injected_noise_squared_sum
            .push(case.risk_primitive.injected_noise_squared_sum);
        self.reference_residual_noise_inner_product_sum
            .push(case.risk_primitive.reference_residual_noise_inner_product_sum);
        self.ledger_residual_sum
            .push(case.derived_diagnostic.ledger_residual_sum);
        self.metric_id.push(metric_id.to_string());
        self.metric_target_kind.push(enum_wire_name(target_kind)?);
        self.metric_source_representation
            .push(enum_wire_name(source_representation)?);
        self.ept_threshold_numerator.push(threshold.map(|ratio| ratio.0));
        self.ept_threshold_denominator.push(threshold.map(|ratio| ratio.1));
        self.metric_spec_json.push(
            serde_json::to_string(&metric.spec)
                .map_err(|error| evidence_error("release_metrics_parquet.metric_spec", error))?,
        );
        self.metric_value.push(metric.value);
        Ok(())
    }

    fn into_batch(self) -> Result<RecordBatch, EvidenceError> {
        let schema = Arc::new(Schema::new(vec![
            Field::new("decision_digest", DataType::Utf8, false),
            Field::new("matrix_id", DataType::Utf8, false),
            Field::new("coordinate_id", DataType::Utf8, false),
            Field::new("target_hash", DataType::Utf8, false),
            Field::new("replica_ordinal", DataType::Int64, true),
            Field::new("replica_purpose", DataType::Utf8, true),
            Field::new("attempt_id", DataType::Utf8, false),
            Field::new("run_hash", DataType::Utf8, false),
            Field::new("implementation_hash", DataType::Utf8, false),
            Field::new("case_id", DataType::Utf8, false),
            Field::new("case_realization_digest", DataType::Utf8, false),
            Field::new("element_count", DataType::UInt64, false),
            Field::new("observed_squared_error_sum", DataType::Float64, false),
            Field::new("observed_absolute_error_sum", DataType::Float64, false),
            Field::new("reference_target_kind", DataType::Utf8, true),
            Field::new("reference_squared_error_sum", DataType::Float64, true),
            Field::new("injected_noise_squared_sum", DataType::Float64, true),
            Field::new("reference_residual_noise_inner_product_sum", DataType::Float64, true),
            Field::new("ledger_residual_sum", DataType::Float64, true),
            Field::new("metric_id", DataType::Utf8, false),
            Field::new("metric_target_kind", DataType::Utf8, false),
            Field::new("metric_source_representation", DataType::Utf8, false),
            Field::new("ept_threshold_numerator", DataType::UInt64, true),
            Field::new("ept_threshold_denominator", DataType::UInt64, true),
            Field::new("metric_spec_json", DataType::Utf8, false),
            Field::new("metric_value", DataType::Float64, false),
        ]));
        RecordBatch::try_new(
            schema,
            vec![
                string_values(&self.decision_digest),
                string_values(&self.matrix_id),
                string_values(&self.coordinate_id),
                string_values(&self.target_hash),
                Arc::new(Int64Array::from(self.replica_ordinal)),
                optional_string_values(&self.replica_purpose),
                string_values(&self.attempt_id),
                string_values(&self.run_hash),
                string_values(&self.implementation_hash),
                string_values(&self.case_id),
                string_values(&self.case_realization_digest),
                Arc::new(UInt64Array::from(self.element_count)),
                Arc::new(Float64Array::from(self.observed_squared_error_sum)),
                Arc::new(Float64Array::from(self.observed_absolute_error_sum)),
                optional_string_values(&self.reference_target_kind),
                Arc::new(Float64Array::from(self.reference_squared_error_sum)),
                Arc::new(Float64Array::from(self.injected_noise_squared_sum)),
                Arc::new(Float64Array::from(self.reference_residual_noise_inner_product_sum)),
                Arc::new(Float64Array::from(self.ledger_residual_sum)),
                string_values(&self.metric_id),
                string_values(&self.metric_target_kind),
                string_values(&self.metric_source_representation),
                Arc::new(UInt64Array::from(self.ept_threshold_numerator)),
                Arc::new(UInt64Array::from(self.ept_threshold_denominator)),
                string_values(&self.metric_spec_json),
                Arc::new(Float64Array::from(self.metric_value)),
            ],
        )
        .map_err(|error| evidence_error("release_metrics_parquet.columns", error))
    }
}

pub(super) fn write_release_batch(batch: &RecordBatch, metadata: Vec<KeyValue>) -> Result<Vec<u8>, EvidenceError> {
    let properties = WriterProperties::builder()
        .set_compression(Compression::ZSTD(ZstdLevel::default()))
        .set_key_value_metadata(Some(metadata))
        .build();
    let mut bytes = Vec::new();
    let mut writer = ArrowWriter::try_new(&mut bytes, batch.schema(), Some(properties))
        .map_err(|error| evidence_error("release_metrics_parquet.write", error))?;
    writer
        .write(batch)
        .map_err(|error| evidence_error("release_metrics_parquet.write", error))?;
    writer
        .close()
        .map_err(|error| evidence_error("release_metrics_parquet.write", error))?;
    Ok(bytes)
}

pub(super) fn read_release_batch(bytes: &[u8]) -> Result<(RecordBatch, Vec<KeyValue>), EvidenceError> {
    let builder = ParquetRecordBatchReaderBuilder::try_new(Bytes::copy_from_slice(bytes))
        .map_err(|error| evidence_error("release_metrics_parquet.metadata", error))?;
    let metadata = builder
        .metadata()
        .file_metadata()
        .key_value_metadata()
        .cloned()
        .unwrap_or_default();
    let schema = builder.schema().clone();
    let batches = builder
        .build()
        .map_err(|error| evidence_error("release_metrics_parquet.read", error))?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|error| evidence_error("release_metrics_parquet.read", error))?;
    let batch =
        concat_batches(&schema, &batches).map_err(|error| evidence_error("release_metrics_parquet.read", error))?;
    Ok((batch, metadata))
}

/// Encode one already-accepted selection into deterministic scientific rows.
///
/// The returned bytes still need an [`fern_artifact::ReportedArtifactV1`] and
/// frozen registry release before a paper reader may consume them.
pub fn encode_release_metrics_parquet(
    table: &ReleaseMetricsTableV1,
    policy: &EvidencePolicyCardV1,
    accepted: &[AcceptanceDecisionV1],
) -> Result<Vec<u8>, EvidenceError> {
    let canonical = table.clone().canonicalize_against_selection(policy, accepted)?;
    let manifest = ReleaseMetricsFileManifestV1 {
        schema: RELEASE_METRICS_TABLE_SCHEMA.to_string(),
        policy_digest: canonical.policy_digest.clone(),
        members: canonical
            .members
            .iter()
            .map(|member| ReleaseMetricsMemberManifestV1 {
                decision_digest: member.decision_digest.clone(),
                compact_metrics_reported_artifact: member.checked_compact_metrics.reported_artifact().clone(),
                compact_schema: member.checked_compact_metrics.metrics().schema.clone(),
                request_digest: member.checked_compact_metrics.metrics().request_digest.clone(),
                metric_contract: member.checked_compact_metrics.metrics().metric_contract.clone(),
                numerical_tolerance: member.checked_compact_metrics.metrics().numerical_tolerance,
                checked_source_artifacts: member
                    .checked_compact_metrics
                    .metrics()
                    .checked_source_artifacts
                    .clone(),
            })
            .collect(),
    };
    let manifest_json =
        serde_json::to_string(&manifest).map_err(|error| evidence_error("release_metrics_parquet.manifest", error))?;
    let content_digest = canonical.content_digest()?;

    let mut columns = ReleaseMetricColumns::default();
    for member in &canonical.members {
        for case in &member.checked_compact_metrics.metrics().cases {
            for metric in &case.metric_values {
                columns.push(member, case, metric)?;
            }
        }
    }
    let batch = columns.into_batch()?;
    write_release_batch(
        &batch,
        vec![
            KeyValue::new(
                METADATA_SCHEMA_KEY.to_string(),
                Some(RELEASE_METRICS_TABLE_SCHEMA.to_string()),
            ),
            KeyValue::new(METADATA_MANIFEST_KEY.to_string(), Some(manifest_json)),
            KeyValue::new(
                METADATA_CONTENT_DIGEST_KEY.to_string(),
                Some(content_digest.as_str().to_string()),
            ),
        ],
    )
}

/// Decode Parquet into the canonical typed table.
///
/// This proves file/schema/content consistency only. The caller must still
/// pass the decoded value and exact bytes through the frozen-release registry
/// gate before using it as paper evidence.
pub fn decode_release_metrics_parquet(bytes: &[u8]) -> Result<ReleaseMetricsTableV1, EvidenceError> {
    let (batch, file_metadata) = read_release_batch(bytes)?;
    let mut metadata = HashMap::new();
    for item in file_metadata {
        if !item.key.starts_with("fern.release_metrics.") {
            continue;
        }
        let value = item
            .value
            .ok_or_else(|| EvidenceError::new("release_metrics_parquet.metadata", "Fern metadata value is null"))?;
        if metadata.insert(item.key.clone(), value).is_some() {
            return Err(EvidenceError::new(
                "release_metrics_parquet.metadata",
                format!("duplicate metadata key {:?}", item.key),
            ));
        }
    }
    let schema = required_metadata(&metadata, METADATA_SCHEMA_KEY)?;
    if schema != RELEASE_METRICS_TABLE_SCHEMA {
        return Err(EvidenceError::new(
            "release_metrics_parquet.schema",
            format!("expected {RELEASE_METRICS_TABLE_SCHEMA:?}, found {schema:?}"),
        ));
    }
    let manifest: ReleaseMetricsFileManifestV1 =
        serde_json::from_str(required_metadata(&metadata, METADATA_MANIFEST_KEY)?)
            .map_err(|error| evidence_error("release_metrics_parquet.manifest", error))?;
    if manifest.schema != RELEASE_METRICS_TABLE_SCHEMA {
        return Err(EvidenceError::new(
            "release_metrics_parquet.manifest.schema",
            "does not match the physical table schema",
        ));
    }

    validate_columns(&batch)?;
    if batch.num_rows() == 0 {
        return Err(EvidenceError::new(
            "release_metrics_parquet.rows",
            "must contain at least one metric row",
        ));
    }

    let mut member_manifests = manifest
        .members
        .into_iter()
        .map(|member| (member.decision_digest.as_str().to_string(), member))
        .collect::<BTreeMap<_, _>>();
    if member_manifests.is_empty() {
        return Err(EvidenceError::new(
            "release_metrics_parquet.manifest.members",
            "must name at least one accepted decision",
        ));
    }
    let mut members: BTreeMap<String, MemberAccumulatorV1> = BTreeMap::new();
    for row in 0..batch.num_rows() {
        let decision_digest = required_string(&batch, "decision_digest", row)?;
        if !member_manifests.contains_key(&decision_digest) {
            return Err(EvidenceError::new(
                "release_metrics_parquet.decision_digest",
                "row names a decision absent from the file manifest",
            ));
        }
        let identity = member_identity(&batch, row)?;
        let case_id = required_string(&batch, "case_id", row)?;
        let parsed_case_id = ScientificCaseId::parse(case_id.clone())
            .map_err(|error| evidence_error("release_metrics_parquet.case_id", error))?;
        let case_realization_digest =
            ScientificContentDigest::parse(required_string(&batch, "case_realization_digest", row)?)
                .map_err(|error| evidence_error("release_metrics_parquet.case_realization_digest", error))?;
        let risk_primitive = risk_primitive(&batch, row)?;
        let derived_diagnostic = LedgerDiagnosticV1 {
            ledger_residual_sum: optional_f64(&batch, "ledger_residual_sum", row)?,
        };
        let metric: MetricValueV1 = MetricValueV1 {
            schema: fern_artifact::METRIC_VALUE_SCHEMA.to_string(),
            spec: serde_json::from_str(&required_string(&batch, "metric_spec_json", row)?)
                .map_err(|error| evidence_error("release_metrics_parquet.metric_spec_json", error))?,
            value: required_f64(&batch, "metric_value", row)?,
        };
        validate_metric_query_columns(&batch, row, &metric.spec)?;

        let member = members.entry(decision_digest).or_default();
        match &member.identity {
            Some(existing) if existing != &identity => {
                return Err(EvidenceError::new(
                    "release_metrics_parquet.member_identity",
                    "one decision has inconsistent identity columns",
                ));
            }
            None => member.identity = Some(identity),
            _ => {}
        }
        let case = member
            .cases
            .entry(case_id.clone())
            .or_insert_with(|| CaseAccumulatorV1 {
                case_id: parsed_case_id,
                case_realization_digest: case_realization_digest.clone(),
                risk_primitive: risk_primitive.clone(),
                derived_diagnostic: derived_diagnostic.clone(),
                metric_values: Vec::new(),
            });
        if case.case_realization_digest != case_realization_digest
            || case.risk_primitive != risk_primitive
            || case.derived_diagnostic != derived_diagnostic
        {
            return Err(EvidenceError::new(
                "release_metrics_parquet.case_facts",
                "repeated rows for one case disagree on primitive ledger facts",
            ));
        }
        case.metric_values.push(metric);
    }

    let mut table_members = Vec::with_capacity(member_manifests.len());
    for (decision_text, member_manifest) in std::mem::take(&mut member_manifests) {
        let member = members.remove(&decision_text).ok_or_else(|| {
            EvidenceError::new(
                "release_metrics_parquet.rows",
                format!("accepted decision {decision_text} has no metric rows"),
            )
        })?;
        let identity = member
            .identity
            .ok_or_else(|| EvidenceError::new("release_metrics_parquet.member_identity", "missing member identity"))?;
        let metrics = CompactMetricsV1 {
            schema: member_manifest.compact_schema,
            attempt_id: identity.attempt_id.clone(),
            request_digest: member_manifest.request_digest,
            metric_contract: member_manifest.metric_contract,
            numerical_tolerance: member_manifest.numerical_tolerance,
            checked_source_artifacts: member_manifest.checked_source_artifacts,
            cases: member.cases.into_values().map(case_into_metrics).collect(),
        };
        table_members.push(ReleaseMetricsMemberV1 {
            decision_digest: member_manifest.decision_digest,
            matrix_id: identity.matrix_id,
            coordinate_id: identity.coordinate_id,
            target_hash: identity.target_hash,
            replica: identity.replica,
            attempt_id: identity.attempt_id.clone(),
            run_hash: identity.run_hash,
            implementation: identity.implementation,
            checked_compact_metrics: CheckedCompactMetricsV1::from_checked_artifact(
                member_manifest.compact_metrics_reported_artifact,
                metrics,
            )?,
        });
    }
    if !members.is_empty() {
        return Err(EvidenceError::new(
            "release_metrics_parquet.rows",
            "rows remain for decisions absent from the manifest",
        ));
    }

    let table = ReleaseMetricsTableV1 {
        schema: manifest.schema,
        policy_digest: manifest.policy_digest,
        members: table_members,
    }
    .canonicalize_structure()?;
    let expected_content = required_metadata(&metadata, METADATA_CONTENT_DIGEST_KEY)?;
    if table.content_digest()?.as_str() != expected_content {
        return Err(EvidenceError::new(
            "release_metrics_parquet.content_digest",
            "decoded typed rows do not match the file's scientific-content digest",
        ));
    }
    Ok(table)
}

fn case_into_metrics(case: CaseAccumulatorV1) -> CompactMetricsCaseV1 {
    CompactMetricsCaseV1 {
        case_id: case.case_id,
        case_realization_digest: case.case_realization_digest,
        risk_primitive: case.risk_primitive,
        derived_diagnostic: case.derived_diagnostic,
        metric_values: case.metric_values,
    }
}

fn required_metadata<'a>(metadata: &'a HashMap<String, String>, key: &str) -> Result<&'a str, EvidenceError> {
    metadata
        .get(key)
        .map(String::as_str)
        .ok_or_else(|| EvidenceError::new("release_metrics_parquet.metadata", format!("missing {key:?}")))
}

fn validate_columns(batch: &RecordBatch) -> Result<(), EvidenceError> {
    let schema = batch.schema();
    let observed = schema
        .fields()
        .iter()
        .map(|field| field.name().as_str())
        .collect::<Vec<_>>();
    if observed != RELEASE_METRIC_COLUMNS {
        return Err(EvidenceError::new(
            "release_metrics_parquet.columns",
            format!("expected {RELEASE_METRIC_COLUMNS:?}, found {observed:?}"),
        ));
    }
    Ok(())
}

fn member_identity(batch: &RecordBatch, row: usize) -> Result<MemberIdentityV1, EvidenceError> {
    let replica_ordinal = optional_i64(batch, "replica_ordinal", row)?;
    let replica_purpose = optional_string(batch, "replica_purpose", row)?;
    let replica = match (replica_ordinal, replica_purpose) {
        (None, None) => None,
        (Some(ordinal), Some(purpose)) => Some(
            DeclaredReplicaV1::new(
                ordinal,
                DeclaredReplicaPurpose::parse(&purpose)
                    .map_err(|error| EvidenceError::new("release_metrics_parquet.replica_purpose", error))?,
            )
            .map_err(|error| EvidenceError::new("release_metrics_parquet.replica", error))?,
        ),
        _ => {
            return Err(EvidenceError::new(
                "release_metrics_parquet.replica",
                "ordinal and purpose must be both null or both present",
            ));
        }
    };
    Ok(MemberIdentityV1 {
        matrix_id: required_string(batch, "matrix_id", row)?,
        coordinate_id: required_string(batch, "coordinate_id", row)?,
        target_hash: required_string(batch, "target_hash", row)?,
        replica,
        attempt_id: required_string(batch, "attempt_id", row)?,
        run_hash: required_string(batch, "run_hash", row)?,
        implementation: ModelImplementationHash::parse(required_string(batch, "implementation_hash", row)?)
            .map_err(|error| evidence_error("release_metrics_parquet.implementation_hash", error))?,
    })
}

fn risk_primitive(batch: &RecordBatch, row: usize) -> Result<RiskPrimitiveV1, EvidenceError> {
    Ok(RiskPrimitiveV1 {
        element_count: required_u64(batch, "element_count", row)?,
        observed_squared_error_sum: required_f64(batch, "observed_squared_error_sum", row)?,
        observed_absolute_error_sum: required_f64(batch, "observed_absolute_error_sum", row)?,
        reference_target_kind: optional_string(batch, "reference_target_kind", row)?
            .map(|name| enum_from_wire_name(&name, "reference_target_kind"))
            .transpose()?,
        reference_squared_error_sum: optional_f64(batch, "reference_squared_error_sum", row)?,
        injected_noise_squared_sum: optional_f64(batch, "injected_noise_squared_sum", row)?,
        reference_residual_noise_inner_product_sum: optional_f64(
            batch,
            "reference_residual_noise_inner_product_sum",
            row,
        )?,
    })
}

fn validate_metric_query_columns(batch: &RecordBatch, row: usize, spec: &MetricSpecV1) -> Result<(), EvidenceError> {
    let (metric_id, target_kind, source_representation, threshold) = metric_query_identity(spec);
    let observed_threshold = (
        optional_u64(batch, "ept_threshold_numerator", row)?,
        optional_u64(batch, "ept_threshold_denominator", row)?,
    );
    let expected_threshold = match threshold {
        Some((numerator, denominator)) => (Some(numerator), Some(denominator)),
        None => (None, None),
    };
    if required_string(batch, "metric_id", row)? != metric_id
        || enum_from_wire_name::<MetricTargetKind>(
            &required_string(batch, "metric_target_kind", row)?,
            "metric_target_kind",
        )? != target_kind
        || enum_from_wire_name::<MetricSourceRepresentation>(
            &required_string(batch, "metric_source_representation", row)?,
            "metric_source_representation",
        )? != source_representation
        || observed_threshold != expected_threshold
    {
        return Err(EvidenceError::new(
            "release_metrics_parquet.metric_query_columns",
            "query columns disagree with the typed metric specification",
        ));
    }
    Ok(())
}

fn metric_query_identity(
    spec: &MetricSpecV1,
) -> (
    &'static str,
    MetricTargetKind,
    MetricSourceRepresentation,
    Option<(u64, u64)>,
) {
    match spec {
        MetricSpecV1::EffectivePredictionTime {
            target_kind,
            threshold_multiplier,
            source_representation,
            ..
        } => (
            "effective_prediction_time",
            *target_kind,
            *source_representation,
            Some((threshold_multiplier.numerator, threshold_multiplier.denominator)),
        ),
        MetricSpecV1::PerChannelSquaredWasserstein {
            target_kind,
            source_representation,
            ..
        } => (
            "per_channel_squared_wasserstein",
            *target_kind,
            *source_representation,
            None,
        ),
    }
}

fn enum_wire_name<T: Serialize>(value: T) -> Result<String, EvidenceError> {
    serde_json::to_value(value)
        .map_err(|error| evidence_error("release_metrics_parquet.enum", error))?
        .as_str()
        .map(str::to_string)
        .ok_or_else(|| EvidenceError::new("release_metrics_parquet.enum", "expected a string enum"))
}

fn enum_from_wire_name<T: DeserializeOwned>(name: &str, field: &str) -> Result<T, EvidenceError> {
    serde_json::from_value(serde_json::Value::String(name.to_string()))
        .map_err(|error| evidence_error(&format!("release_metrics_parquet.{field}"), error))
}

fn column<'a>(batch: &'a RecordBatch, name: &str) -> Result<&'a dyn Array, EvidenceError> {
    batch
        .column_by_name(name)
        .map(|column| column.as_ref())
        .ok_or_else(|| EvidenceError::new("release_metrics_parquet.columns", format!("missing column {name:?}")))
}

fn required_string(batch: &RecordBatch, name: &str, row: usize) -> Result<String, EvidenceError> {
    optional_string(batch, name, row)?
        .ok_or_else(|| EvidenceError::new(format!("release_metrics_parquet.{name}"), "must not be null"))
}

fn optional_string(batch: &RecordBatch, name: &str, row: usize) -> Result<Option<String>, EvidenceError> {
    let array = column(batch, name)?;
    if array.is_null(row) {
        return Ok(None);
    }
    if let Some(array) = array.as_any().downcast_ref::<StringArray>() {
        return Ok(Some(array.value(row).to_string()));
    }
    if let Some(array) = array.as_any().downcast_ref::<StringViewArray>() {
        return Ok(Some(array.value(row).to_string()));
    }
    if let Some(array) = array.as_any().downcast_ref::<LargeStringArray>() {
        return Ok(Some(array.value(row).to_string()));
    }
    Err(EvidenceError::new(
        "release_metrics_parquet.columns",
        format!("column {name:?} must be a string, found {:?}", array.data_type()),
    ))
}

macro_rules! numeric_reader {
    ($required:ident, $optional:ident, $native:ty, $array:ty, $dtype:literal) => {
        fn $optional(batch: &RecordBatch, name: &str, row: usize) -> Result<Option<$native>, EvidenceError> {
            let array = column(batch, name)?;
            let values = array.as_any().downcast_ref::<$array>().ok_or_else(|| {
                EvidenceError::new(
                    "release_metrics_parquet.columns",
                    format!(
                        "column {name:?} must be {}, found {:?}",
                        $dtype,
                        array.data_type()
                    ),
                )
            })?;
            Ok((!values.is_null(row)).then(|| values.value(row)))
        }

        fn $required(batch: &RecordBatch, name: &str, row: usize) -> Result<$native, EvidenceError> {
            $optional(batch, name, row)?
                .ok_or_else(|| EvidenceError::new(format!("release_metrics_parquet.{name}"), "must not be null"))
        }
    };
}

numeric_reader!(required_u64, optional_u64, u64, UInt64Array, "UInt64");
numeric_reader!(required_f64, optional_f64, f64, Float64Array, "Float64");

fn optional_i64(batch: &RecordBatch, name: &str, row: usize) -> Result<Option<i64>, EvidenceError> {
    let array = column(batch, name)?;
    let values = array.as_any().downcast_ref::<Int64Array>().ok_or_else(|| {
        EvidenceError::new(
            "release_metrics_parquet.columns",
            format!("column {name:?} must be Int64, found {:?}", array.data_type()),
        )
    })?;
    Ok((!values.is_null(row)).then(|| values.value(row)))
}

fn evidence_error(field: &str, error: impl std::fmt::Display) -> EvidenceError {
    EvidenceError::new(field, error.to_string())
}
