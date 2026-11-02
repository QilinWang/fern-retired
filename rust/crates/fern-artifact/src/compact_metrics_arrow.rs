//! Arrow IPC wire format for [`CompactMetricsV1`](crate::CompactMetricsV1).
//!
//! The Rust objects define meaning; this module pins the physical field order,
//! nullability, metadata, and round-trip checks.  Repeated numbers stay in
//! typed Arrow arrays.  JSON metadata is limited to small declarations and
//! artifact-reference relations.

use std::collections::HashMap;
use std::io::Cursor;
use std::sync::Arc;

use arrow_array::{
    Array, ArrayRef, Float64Array, ListArray, RecordBatch, StringArray, StructArray, UInt32Array, UInt64Array,
};
use arrow_buffer::{OffsetBuffer, ScalarBuffer};
use arrow_ipc::reader::FileReader;
use arrow_ipc::writer::FileWriter;
use arrow_schema::{DataType, Field, Fields, Schema};

use super::{
    ArtifactReference, COMPACT_METRICS_SCHEMA, CompactMetricsCaseV1, CompactMetricsV1, ContractError,
    LedgerDiagnosticV1, METRIC_VALUE_SCHEMA, MetricContractV1, MetricSourceRepresentation, MetricSpecV1,
    MetricTargetKind, MetricValueV1, NumericalToleranceV1, RiskPrimitiveV1, ScientificCaseId, ScientificContentDigest,
    domain_digest,
};

pub const COMPACT_METRICS_ARROW_WIRE_SCHEMA: &str = "fern.compact_metrics.arrow.v1";

const META_WIRE_SCHEMA: &str = "fern.wire_schema";
const META_SCHEMA_FINGERPRINT: &str = "fern.schema_fingerprint";
const META_ATTEMPT_ID: &str = "fern.attempt_id";
const META_REQUEST_DIGEST: &str = "fern.request_digest";
const META_METRIC_CONTRACT: &str = "fern.metric_contract";
const META_POLICY_DIGEST: &str = "fern.evaluation_policy_digest";
const META_TOLERANCE: &str = "fern.numerical_tolerance";
const META_CHECKED_ARTIFACTS: &str = "fern.checked_source_artifacts";

pub fn compact_metrics_arrow_schema_fingerprint() -> ScientificContentDigest {
    // This literal is the language-neutral V1 field contract.  It does not
    // depend on Rust debug formatting or Arrow's internal representation.
    let contract = b"case_id:utf8:not_null\n\
case_realization_digest:utf8:not_null\n\
risk_primitive:struct:not_null[element_count:u64:not_null,observed_squared_error_sum:f64:not_null,observed_absolute_error_sum:f64:not_null,reference_target_kind:utf8:nullable,reference_squared_error_sum:f64:nullable,injected_noise_squared_sum:f64:nullable,reference_residual_noise_inner_product_sum:f64:nullable]\n\
derived_diagnostic:struct:not_null[ledger_residual_sum:f64:nullable]\n\
metric_values:list:not_null[struct:not_null[metric_id:utf8:not_null,metric_version:u32:not_null,value:f64:not_null,source_representation:utf8:not_null,source_artifact_key:utf8:not_null,target_kind:utf8:not_null,metric_spec_digest:utf8:not_null,metric_spec_json:utf8:not_null]]\n\
metadata:fern.wire_schema,fern.schema_fingerprint,fern.attempt_id,fern.request_digest,fern.metric_contract,fern.evaluation_policy_digest,fern.numerical_tolerance,fern.checked_source_artifacts";
    domain_digest(b"fern.compact_metrics.arrow_schema.v1\0", contract)
}

pub fn compact_metrics_arrow_schema_v1(compact: &CompactMetricsV1) -> Result<Schema, ContractError> {
    compact.validate()?;
    let mut checked = compact.checked_source_artifacts.clone();
    checked.sort_by(|left, right| left.artifact_key.cmp(&right.artifact_key));
    let metadata = HashMap::from([
        (
            META_WIRE_SCHEMA.to_string(),
            COMPACT_METRICS_ARROW_WIRE_SCHEMA.to_string(),
        ),
        (
            META_SCHEMA_FINGERPRINT.to_string(),
            compact_metrics_arrow_schema_fingerprint().as_str().to_string(),
        ),
        (META_ATTEMPT_ID.to_string(), compact.attempt_id.clone()),
        (
            META_REQUEST_DIGEST.to_string(),
            compact.request_digest.as_str().to_string(),
        ),
        (
            META_METRIC_CONTRACT.to_string(),
            serde_json::to_string(&compact.metric_contract).map_err(json_error)?,
        ),
        (
            META_POLICY_DIGEST.to_string(),
            compact.metric_contract.digest()?.as_str().to_string(),
        ),
        (
            META_TOLERANCE.to_string(),
            serde_json::to_string(&compact.numerical_tolerance).map_err(json_error)?,
        ),
        (
            META_CHECKED_ARTIFACTS.to_string(),
            serde_json::to_string(&checked).map_err(json_error)?,
        ),
    ]);
    Ok(Schema::new_with_metadata(compact_metrics_fields(), metadata))
}

fn compact_metrics_fields() -> Vec<Field> {
    vec![
        Field::new("case_id", DataType::Utf8, false),
        Field::new("case_realization_digest", DataType::Utf8, false),
        Field::new("risk_primitive", DataType::Struct(risk_fields()), false),
        Field::new("derived_diagnostic", DataType::Struct(diagnostic_fields()), false),
        Field::new(
            "metric_values",
            DataType::List(Arc::new(Field::new("item", DataType::Struct(metric_fields()), false))),
            false,
        ),
    ]
}

fn risk_fields() -> Fields {
    vec![
        Field::new("element_count", DataType::UInt64, false),
        Field::new("observed_squared_error_sum", DataType::Float64, false),
        Field::new("observed_absolute_error_sum", DataType::Float64, false),
        Field::new("reference_target_kind", DataType::Utf8, true),
        Field::new("reference_squared_error_sum", DataType::Float64, true),
        Field::new("injected_noise_squared_sum", DataType::Float64, true),
        Field::new("reference_residual_noise_inner_product_sum", DataType::Float64, true),
    ]
    .into()
}

fn diagnostic_fields() -> Fields {
    vec![Field::new("ledger_residual_sum", DataType::Float64, true)].into()
}

fn metric_fields() -> Fields {
    vec![
        Field::new("metric_id", DataType::Utf8, false),
        Field::new("metric_version", DataType::UInt32, false),
        Field::new("value", DataType::Float64, false),
        Field::new("source_representation", DataType::Utf8, false),
        Field::new("source_artifact_key", DataType::Utf8, false),
        Field::new("target_kind", DataType::Utf8, false),
        Field::new("metric_spec_digest", DataType::Utf8, false),
        Field::new("metric_spec_json", DataType::Utf8, false),
    ]
    .into()
}

pub fn encode_compact_metrics_arrow(compact: &CompactMetricsV1) -> Result<Vec<u8>, ContractError> {
    compact.validate()?;
    let schema = Arc::new(compact_metrics_arrow_schema_v1(compact)?);
    let mut cases = compact.cases.clone();
    cases.sort_by(|left, right| left.case_id.cmp(&right.case_id));
    for case in &mut cases {
        case.metric_values.sort_by(|left, right| {
            left.spec
                .identity_digest()
                .expect("validated metric spec")
                .cmp(&right.spec.identity_digest().expect("validated metric spec"))
        });
    }

    let case_ids = Arc::new(StringArray::from(
        cases.iter().map(|case| case.case_id.as_str()).collect::<Vec<_>>(),
    )) as ArrayRef;
    let realization_digests = Arc::new(StringArray::from(
        cases
            .iter()
            .map(|case| case.case_realization_digest.as_str())
            .collect::<Vec<_>>(),
    )) as ArrayRef;
    let risk = Arc::new(StructArray::new(
        risk_fields(),
        vec![
            Arc::new(UInt64Array::from(
                cases
                    .iter()
                    .map(|case| case.risk_primitive.element_count)
                    .collect::<Vec<_>>(),
            )),
            Arc::new(Float64Array::from(
                cases
                    .iter()
                    .map(|case| case.risk_primitive.observed_squared_error_sum)
                    .collect::<Vec<_>>(),
            )),
            Arc::new(Float64Array::from(
                cases
                    .iter()
                    .map(|case| case.risk_primitive.observed_absolute_error_sum)
                    .collect::<Vec<_>>(),
            )),
            Arc::new(StringArray::from(
                cases
                    .iter()
                    .map(|case| case.risk_primitive.reference_target_kind.map(metric_target_kind_name))
                    .collect::<Vec<_>>(),
            )),
            Arc::new(Float64Array::from(
                cases
                    .iter()
                    .map(|case| case.risk_primitive.reference_squared_error_sum)
                    .collect::<Vec<_>>(),
            )),
            Arc::new(Float64Array::from(
                cases
                    .iter()
                    .map(|case| case.risk_primitive.injected_noise_squared_sum)
                    .collect::<Vec<_>>(),
            )),
            Arc::new(Float64Array::from(
                cases
                    .iter()
                    .map(|case| case.risk_primitive.reference_residual_noise_inner_product_sum)
                    .collect::<Vec<_>>(),
            )),
        ],
        None,
    )) as ArrayRef;
    let diagnostic = Arc::new(StructArray::new(
        diagnostic_fields(),
        vec![Arc::new(Float64Array::from(
            cases
                .iter()
                .map(|case| case.derived_diagnostic.ledger_residual_sum)
                .collect::<Vec<_>>(),
        ))],
        None,
    )) as ArrayRef;

    let mut offsets = Vec::with_capacity(cases.len() + 1);
    offsets.push(0_i32);
    let mut flattened = Vec::new();
    for case in &cases {
        flattened.extend(case.metric_values.iter());
        offsets.push(i32::try_from(flattened.len()).map_err(|_| {
            ContractError::new(
                "compact_metrics.metric_values",
                "row count exceeds Arrow i32 list offsets",
            )
        })?);
    }
    let metric_struct = Arc::new(metric_struct_array(&flattened)?) as ArrayRef;
    let metric_list = Arc::new(ListArray::new(
        Arc::new(Field::new("item", DataType::Struct(metric_fields()), false)),
        OffsetBuffer::new(ScalarBuffer::from(offsets)),
        metric_struct,
        None,
    )) as ArrayRef;
    let batch = RecordBatch::try_new(
        Arc::clone(&schema),
        vec![case_ids, realization_digests, risk, diagnostic, metric_list],
    )
    .map_err(arrow_error)?;
    let mut bytes = Vec::new();
    {
        let mut writer = FileWriter::try_new(&mut bytes, &schema).map_err(arrow_error)?;
        writer.write(&batch).map_err(arrow_error)?;
        writer.finish().map_err(arrow_error)?;
    }
    Ok(bytes)
}

fn metric_struct_array(metrics: &[&MetricValueV1]) -> Result<StructArray, ContractError> {
    let mut ids = Vec::with_capacity(metrics.len());
    let mut versions = Vec::with_capacity(metrics.len());
    let mut values = Vec::with_capacity(metrics.len());
    let mut representations = Vec::with_capacity(metrics.len());
    let mut source_keys = Vec::with_capacity(metrics.len());
    let mut targets = Vec::with_capacity(metrics.len());
    let mut digests = Vec::with_capacity(metrics.len());
    let mut specs = Vec::with_capacity(metrics.len());
    for metric in metrics {
        ids.push(metric_id(&metric.spec));
        versions.push(metric_version(&metric.spec));
        values.push(metric.value);
        representations.push(source_representation(&metric.spec));
        source_keys.push(source_reference(&metric.spec).artifact_key.as_str());
        targets.push(target_kind(&metric.spec));
        digests.push(metric.spec.identity_digest()?.as_str().to_string());
        specs.push(serde_json::to_string(&metric.spec).map_err(json_error)?);
    }
    Ok(StructArray::new(
        metric_fields(),
        vec![
            Arc::new(StringArray::from(ids)),
            Arc::new(UInt32Array::from(versions)),
            Arc::new(Float64Array::from(values)),
            Arc::new(StringArray::from(representations)),
            Arc::new(StringArray::from(source_keys)),
            Arc::new(StringArray::from(targets)),
            Arc::new(StringArray::from(digests)),
            Arc::new(StringArray::from(specs)),
        ],
        None,
    ))
}

pub fn decode_compact_metrics_arrow(bytes: &[u8]) -> Result<CompactMetricsV1, ContractError> {
    let mut reader = FileReader::try_new(Cursor::new(bytes), None).map_err(arrow_error)?;
    let schema = reader.schema();
    if schema.fields() != &Fields::from(compact_metrics_fields()) {
        return Err(ContractError::new(
            "compact_metrics.arrow_schema",
            "field order, type, or nullability does not match V1",
        ));
    }
    let metadata = schema.metadata();
    require_metadata(metadata, META_WIRE_SCHEMA, COMPACT_METRICS_ARROW_WIRE_SCHEMA)?;
    require_metadata(
        metadata,
        META_SCHEMA_FINGERPRINT,
        compact_metrics_arrow_schema_fingerprint().as_str(),
    )?;
    let attempt_id = metadata_value(metadata, META_ATTEMPT_ID)?.to_string();
    let request_digest = ScientificContentDigest::parse(metadata_value(metadata, META_REQUEST_DIGEST)?)?;
    let metric_contract = serde_json::from_str::<MetricContractV1>(metadata_value(metadata, META_METRIC_CONTRACT)?)
        .map_err(json_error)?;
    let policy_digest = metric_contract.digest()?;
    require_metadata(metadata, META_POLICY_DIGEST, policy_digest.as_str())?;
    let numerical_tolerance =
        serde_json::from_str::<NumericalToleranceV1>(metadata_value(metadata, META_TOLERANCE)?).map_err(json_error)?;
    let checked_source_artifacts =
        serde_json::from_str::<Vec<ArtifactReference>>(metadata_value(metadata, META_CHECKED_ARTIFACTS)?)
            .map_err(json_error)?;
    let batches = reader.by_ref().collect::<Result<Vec<_>, _>>().map_err(arrow_error)?;
    if batches.len() != 1 {
        return Err(ContractError::new(
            "compact_metrics.arrow_batches",
            format!("requires exactly one record batch, found {}", batches.len()),
        ));
    }
    let batch = &batches[0];
    let case_ids = string_column(batch.column(0), "case_id")?;
    let realization_digests = string_column(batch.column(1), "case_realization_digest")?;
    let risk = struct_column(batch.column(2), "risk_primitive")?;
    let diagnostic = struct_column(batch.column(3), "derived_diagnostic")?;
    let metric_lists = list_column(batch.column(4), "metric_values")?;
    let mut cases = Vec::with_capacity(batch.num_rows());
    for row in 0..batch.num_rows() {
        let risk_primitive = RiskPrimitiveV1 {
            element_count: u64_value(risk, "element_count", row)?,
            observed_squared_error_sum: f64_value(risk, "observed_squared_error_sum", row)?,
            observed_absolute_error_sum: f64_value(risk, "observed_absolute_error_sum", row)?,
            reference_target_kind: optional_string_value(risk, "reference_target_kind", row)?
                .map(parse_metric_target_kind)
                .transpose()?,
            reference_squared_error_sum: optional_f64_value(risk, "reference_squared_error_sum", row)?,
            injected_noise_squared_sum: optional_f64_value(risk, "injected_noise_squared_sum", row)?,
            reference_residual_noise_inner_product_sum: optional_f64_value(
                risk,
                "reference_residual_noise_inner_product_sum",
                row,
            )?,
        };
        let derived_diagnostic = LedgerDiagnosticV1 {
            ledger_residual_sum: optional_f64_value(diagnostic, "ledger_residual_sum", row)?,
        };
        let values = metric_lists.value(row);
        let values = values
            .as_any()
            .downcast_ref::<StructArray>()
            .ok_or_else(|| ContractError::new("compact_metrics.metric_values", "list item is not a struct"))?;
        let mut metric_values = Vec::with_capacity(values.len());
        for metric_row in 0..values.len() {
            metric_values.push(decode_metric(values, metric_row)?);
        }
        cases.push(CompactMetricsCaseV1 {
            case_id: ScientificCaseId::parse(case_ids.value(row))?,
            case_realization_digest: ScientificContentDigest::parse(realization_digests.value(row))?,
            risk_primitive,
            derived_diagnostic,
            metric_values,
        });
    }
    let compact = CompactMetricsV1 {
        schema: COMPACT_METRICS_SCHEMA.to_string(),
        attempt_id,
        request_digest,
        metric_contract,
        numerical_tolerance,
        checked_source_artifacts,
        cases,
    };
    compact.validate()?;
    Ok(compact)
}

fn decode_metric(values: &StructArray, row: usize) -> Result<MetricValueV1, ContractError> {
    let spec_json = string_value(values, "metric_spec_json", row)?;
    let spec = serde_json::from_str::<MetricSpecV1>(spec_json).map_err(json_error)?;
    spec.validate()?;
    if string_value(values, "metric_id", row)? != metric_id(&spec)
        || u32_value(values, "metric_version", row)? != metric_version(&spec)
        || string_value(values, "source_representation", row)? != source_representation(&spec)
        || string_value(values, "source_artifact_key", row)? != source_reference(&spec).artifact_key.as_str()
        || string_value(values, "target_kind", row)? != target_kind(&spec)
        || string_value(values, "metric_spec_digest", row)? != spec.identity_digest()?.as_str()
    {
        return Err(ContractError::new(
            "compact_metrics.metric_values",
            "typed index columns disagree with metric_spec_json",
        ));
    }
    let metric = MetricValueV1 {
        schema: METRIC_VALUE_SCHEMA.to_string(),
        spec,
        value: f64_value(values, "value", row)?,
    };
    metric.validate()?;
    Ok(metric)
}

fn metric_id(spec: &MetricSpecV1) -> &'static str {
    match spec {
        MetricSpecV1::EffectivePredictionTime { .. } => "effective_prediction_time",
        MetricSpecV1::PerChannelSquaredWasserstein { .. } => "per_channel_squared_wasserstein",
    }
}

fn metric_version(spec: &MetricSpecV1) -> u32 {
    match spec {
        MetricSpecV1::EffectivePredictionTime { metric_version, .. }
        | MetricSpecV1::PerChannelSquaredWasserstein { metric_version, .. } => *metric_version,
    }
}

fn source_representation(spec: &MetricSpecV1) -> &'static str {
    match spec.source_representation() {
        MetricSourceRepresentation::Point => "point",
        MetricSourceRepresentation::PredictiveSamples => "predictive_samples",
    }
}

fn source_reference(spec: &MetricSpecV1) -> &ArtifactReference {
    match spec {
        MetricSpecV1::EffectivePredictionTime { source, .. }
        | MetricSpecV1::PerChannelSquaredWasserstein { source, .. } => source,
    }
}

fn target_kind(spec: &MetricSpecV1) -> &'static str {
    metric_target_kind_name(spec.target_kind())
}

fn metric_target_kind_name(kind: MetricTargetKind) -> &'static str {
    match kind {
        MetricTargetKind::ObservedCorrupted => "observed_corrupted",
        MetricTargetKind::RecordedReference => "recorded_reference",
        MetricTargetKind::LatentClean => "latent_clean",
    }
}

fn parse_metric_target_kind(value: &str) -> Result<MetricTargetKind, ContractError> {
    match value {
        "observed_corrupted" => Ok(MetricTargetKind::ObservedCorrupted),
        "recorded_reference" => Ok(MetricTargetKind::RecordedReference),
        "latent_clean" => Ok(MetricTargetKind::LatentClean),
        _ => Err(ContractError::new(
            "compact_metrics.reference_target_kind",
            format!("unknown target kind {value:?}"),
        )),
    }
}

fn metadata_value<'a>(metadata: &'a HashMap<String, String>, key: &str) -> Result<&'a str, ContractError> {
    metadata
        .get(key)
        .map(String::as_str)
        .ok_or_else(|| ContractError::new("compact_metrics.arrow_metadata", format!("missing {key}")))
}

fn require_metadata(metadata: &HashMap<String, String>, key: &str, expected: &str) -> Result<(), ContractError> {
    let observed = metadata_value(metadata, key)?;
    if observed != expected {
        return Err(ContractError::new(
            "compact_metrics.arrow_metadata",
            format!("{key} expected {expected:?}, found {observed:?}"),
        ));
    }
    Ok(())
}

fn string_column<'a>(array: &'a ArrayRef, field: &str) -> Result<&'a StringArray, ContractError> {
    array
        .as_any()
        .downcast_ref::<StringArray>()
        .ok_or_else(|| ContractError::new("compact_metrics.arrow_schema", format!("{field} is not Utf8")))
}

fn struct_column<'a>(array: &'a ArrayRef, field: &str) -> Result<&'a StructArray, ContractError> {
    array
        .as_any()
        .downcast_ref::<StructArray>()
        .ok_or_else(|| ContractError::new("compact_metrics.arrow_schema", format!("{field} is not Struct")))
}

fn list_column<'a>(array: &'a ArrayRef, field: &str) -> Result<&'a ListArray, ContractError> {
    array
        .as_any()
        .downcast_ref::<ListArray>()
        .ok_or_else(|| ContractError::new("compact_metrics.arrow_schema", format!("{field} is not List")))
}

fn child<'a>(values: &'a StructArray, name: &str) -> Result<&'a ArrayRef, ContractError> {
    values
        .column_by_name(name)
        .ok_or_else(|| ContractError::new("compact_metrics.arrow_schema", format!("missing field {name}")))
}

fn string_value<'a>(values: &'a StructArray, name: &str, row: usize) -> Result<&'a str, ContractError> {
    Ok(string_column(child(values, name)?, name)?.value(row))
}

fn u32_value(values: &StructArray, name: &str, row: usize) -> Result<u32, ContractError> {
    child(values, name)?
        .as_any()
        .downcast_ref::<UInt32Array>()
        .map(|array| array.value(row))
        .ok_or_else(|| ContractError::new("compact_metrics.arrow_schema", format!("{name} is not UInt32")))
}

fn u64_value(values: &StructArray, name: &str, row: usize) -> Result<u64, ContractError> {
    child(values, name)?
        .as_any()
        .downcast_ref::<UInt64Array>()
        .map(|array| array.value(row))
        .ok_or_else(|| ContractError::new("compact_metrics.arrow_schema", format!("{name} is not UInt64")))
}

fn f64_value(values: &StructArray, name: &str, row: usize) -> Result<f64, ContractError> {
    child(values, name)?
        .as_any()
        .downcast_ref::<Float64Array>()
        .map(|array| array.value(row))
        .ok_or_else(|| ContractError::new("compact_metrics.arrow_schema", format!("{name} is not Float64")))
}

fn optional_f64_value(values: &StructArray, name: &str, row: usize) -> Result<Option<f64>, ContractError> {
    let array = child(values, name)?
        .as_any()
        .downcast_ref::<Float64Array>()
        .ok_or_else(|| ContractError::new("compact_metrics.arrow_schema", format!("{name} is not Float64")))?;
    Ok((!array.is_null(row)).then(|| array.value(row)))
}

fn optional_string_value<'a>(
    values: &'a StructArray,
    name: &str,
    row: usize,
) -> Result<Option<&'a str>, ContractError> {
    let array = child(values, name)?
        .as_any()
        .downcast_ref::<StringArray>()
        .ok_or_else(|| ContractError::new("compact_metrics.arrow_schema", format!("{name} is not Utf8")))?;
    Ok((!array.is_null(row)).then(|| array.value(row)))
}

fn arrow_error(error: impl std::fmt::Display) -> ContractError {
    ContractError::new("compact_metrics.arrow", error.to_string())
}

fn json_error(error: impl std::fmt::Display) -> ContractError {
    ContractError::new("compact_metrics.json", error.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        ArtifactKey, ArtifactRole, COMPACT_METRICS_SCHEMA, ExactRatio, MetricAggregationV1, MetricValueSpace,
        SampleAggregation,
    };

    fn reference(role: ArtifactRole, name: &str) -> ArtifactReference {
        ArtifactReference {
            artifact_key: ArtifactKey::parse(format!("artifact:{name}")).unwrap(),
            role,
            content_digest: ScientificContentDigest::from_bytes(name.as_bytes()),
        }
    }

    fn compact() -> CompactMetricsV1 {
        let observed = reference(ArtifactRole::FinalNoisyInput, "observed");
        let point = reference(ArtifactRole::DensePointForecast, "point");
        let scale = reference(ArtifactRole::TrainingScale, "scale");
        let aggregation = MetricAggregationV1::point_default();
        let mut metrics = [
            ExactRatio::new(1, 1).unwrap(),
            ExactRatio::new(3, 2).unwrap(),
            ExactRatio::new(2, 1).unwrap(),
        ]
        .into_iter()
        .enumerate()
        .map(|(index, threshold_multiplier)| MetricValueV1 {
            schema: METRIC_VALUE_SCHEMA.to_string(),
            spec: MetricSpecV1::EffectivePredictionTime {
                metric_version: 1,
                target_kind: MetricTargetKind::ObservedCorrupted,
                target: observed.clone(),
                threshold_source: scale.clone(),
                threshold_multiplier,
                horizon_length: 4,
                source_representation: MetricSourceRepresentation::Point,
                source: point.clone(),
                aggregation,
            },
            value: (index + 1) as f64,
        })
        .collect::<Vec<_>>();
        metrics.push(MetricValueV1 {
            schema: METRIC_VALUE_SCHEMA.to_string(),
            spec: MetricSpecV1::PerChannelSquaredWasserstein {
                metric_version: 1,
                target_kind: MetricTargetKind::ObservedCorrupted,
                target: observed.clone(),
                horizon_length: 4,
                value_space: MetricValueSpace::Physical,
                scale_reference: None,
                source_representation: MetricSourceRepresentation::Point,
                source: point.clone(),
                aggregation,
            },
            value: 0.25,
        });
        let (risk, diagnostic) = RiskPrimitiveV1::from_aligned(&[0.0], &[1.0], None).unwrap();
        CompactMetricsV1 {
            schema: COMPACT_METRICS_SCHEMA.to_string(),
            attempt_id: "attempt-1".to_string(),
            request_digest: ScientificContentDigest::from_bytes(b"request"),
            metric_contract: MetricContractV1::time_series_v1(vec![MetricTargetKind::ObservedCorrupted]).unwrap(),
            numerical_tolerance: NumericalToleranceV1::f64_default(),
            checked_source_artifacts: vec![observed, point, scale],
            cases: vec![CompactMetricsCaseV1 {
                case_id: ScientificCaseId::parse("case-1").unwrap(),
                case_realization_digest: ScientificContentDigest::from_bytes(b"case"),
                risk_primitive: risk,
                derived_diagnostic: diagnostic,
                metric_values: metrics,
            }],
        }
    }

    #[test]
    fn compact_metrics_arrow_round_trip_preserves_nulls_specs_and_identity() {
        let compact = compact();
        let bytes = encode_compact_metrics_arrow(&compact).unwrap();
        let decoded = decode_compact_metrics_arrow(&bytes).unwrap();
        assert_eq!(compact.digest().unwrap(), decoded.digest().unwrap());
        assert_eq!(decoded.cases[0].risk_primitive.reference_target_kind, None);
        assert_eq!(decoded.cases[0].risk_primitive.reference_squared_error_sum, None);

        let mut with_measured_zero = compact;
        with_measured_zero.cases[0].risk_primitive.reference_target_kind = Some(MetricTargetKind::RecordedReference);
        with_measured_zero.cases[0].risk_primitive.reference_squared_error_sum = Some(0.0);
        with_measured_zero.cases[0].risk_primitive.injected_noise_squared_sum = Some(0.0);
        with_measured_zero.cases[0]
            .risk_primitive
            .reference_residual_noise_inner_product_sum = Some(0.0);
        with_measured_zero.cases[0].derived_diagnostic.ledger_residual_sum = Some(1.0);
        let bytes = encode_compact_metrics_arrow(&with_measured_zero).unwrap();
        let decoded = decode_compact_metrics_arrow(&bytes).unwrap();
        assert_eq!(
            decoded.cases[0].risk_primitive.reference_target_kind,
            Some(MetricTargetKind::RecordedReference)
        );
        assert_eq!(decoded.cases[0].risk_primitive.reference_squared_error_sum, Some(0.0));
    }

    #[test]
    fn compact_metrics_arrow_schema_fingerprint_is_golden() {
        assert_eq!(
            compact_metrics_arrow_schema_fingerprint().as_str(),
            "sha256:8252cd552e87baf7d691b91dce21310bc9dff0ec05833e5658e9d78569cd492c"
        );
    }

    #[test]
    fn source_aggregation_enum_is_not_a_free_form_arrow_string() {
        // The Arrow column duplicates a typed spec only for indexed querying;
        // decoding compares both and rejects disagreement.  Keep the enum
        // exhaustiveness visible in this wire-format test.
        assert_eq!(
            MetricAggregationV1::point_default().sample,
            SampleAggregation::NotPresent
        );
    }
}
