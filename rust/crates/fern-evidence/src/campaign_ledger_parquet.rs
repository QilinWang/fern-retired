//! Parquet adapter for the rebuildable operational campaign ledger.
//!
//! The file is deliberately not a release table. It contains no policy or
//! release identity, and the frozen-release authorizer therefore cannot accept
//! it as paper evidence.

use std::collections::HashMap;
use std::sync::Arc;

use arrow_array::{
    Array, ArrayRef, Float64Array, Int64Array, LargeStringArray, RecordBatch, StringArray, StringViewArray, UInt64Array,
};
use arrow_schema::{DataType, Field, Schema};
use arrow_select::concat::concat_batches;
use bytes::Bytes;
use parquet::arrow::{arrow_reader::ParquetRecordBatchReaderBuilder, arrow_writer::ArrowWriter};
use parquet::basic::{Compression, ZstdLevel};
use parquet::file::metadata::KeyValue;
use parquet::file::properties::WriterProperties;

use super::{CAMPAIGN_LEDGER_SCHEMA, CampaignLedgerMemberV1, CampaignLedgerV1, EvidenceError};

const METADATA_SCHEMA_KEY: &str = "fern.campaign_ledger.schema";
const METADATA_CONTENT_DIGEST_KEY: &str = "fern.campaign_ledger.content_digest";
const COLUMNS: &[&str] = &[
    "checked_commit_digest",
    "decision_count",
    "decision_states_json",
    "matrix_id",
    "coordinate_id",
    "target_hash",
    "replica_ordinal",
    "replica_purpose",
    "attempt_id",
    "run_hash",
    "implementation_hash",
    "case_count",
    "metric_count",
    "observed_squared_error_sum",
    "member_json",
];

fn campaign_error(field: &str, message: impl std::fmt::Display) -> EvidenceError {
    EvidenceError::new(field, message.to_string())
}

fn string_values(values: &[String]) -> ArrayRef {
    Arc::new(StringArray::from_iter_values(values.iter().map(String::as_str)))
}

fn optional_string_values(values: &[Option<String>]) -> ArrayRef {
    Arc::new(StringArray::from_iter(values.iter().map(|value| value.as_deref())))
}

fn write_campaign_batch(batch: &RecordBatch, metadata: Vec<KeyValue>) -> Result<Vec<u8>, EvidenceError> {
    let properties = WriterProperties::builder()
        .set_compression(Compression::ZSTD(ZstdLevel::default()))
        .set_key_value_metadata(Some(metadata))
        .build();
    let mut bytes = Vec::new();
    let mut writer = ArrowWriter::try_new(&mut bytes, batch.schema(), Some(properties))
        .map_err(|source| campaign_error("campaign_ledger.write", source))?;
    writer
        .write(batch)
        .map_err(|source| campaign_error("campaign_ledger.write", source))?;
    writer
        .close()
        .map_err(|source| campaign_error("campaign_ledger.write", source))?;
    Ok(bytes)
}

fn read_campaign_batch(bytes: &[u8]) -> Result<(RecordBatch, Vec<KeyValue>), EvidenceError> {
    let builder = ParquetRecordBatchReaderBuilder::try_new(Bytes::copy_from_slice(bytes))
        .map_err(|source| campaign_error("campaign_ledger.metadata", source))?;
    let metadata = builder
        .metadata()
        .file_metadata()
        .key_value_metadata()
        .cloned()
        .unwrap_or_default();
    let schema = builder.schema().clone();
    let batches = builder
        .build()
        .map_err(|source| campaign_error("campaign_ledger.read", source))?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|source| campaign_error("campaign_ledger.read", source))?;
    let batch = concat_batches(&schema, &batches).map_err(|source| campaign_error("campaign_ledger.read", source))?;
    Ok((batch, metadata))
}

fn column<'a>(batch: &'a RecordBatch, name: &str, field: &str) -> Result<&'a dyn Array, EvidenceError> {
    batch
        .column_by_name(name)
        .map(|column| column.as_ref())
        .ok_or_else(|| campaign_error(field, format!("missing column {name:?}")))
}

fn optional_string(batch: &RecordBatch, name: &str, row: usize, field: &str) -> Result<Option<String>, EvidenceError> {
    let array = column(batch, name, field)?;
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
    Err(campaign_error(
        field,
        format!("column {name:?} must be a string, found {:?}", array.data_type()),
    ))
}

fn optional_i64(batch: &RecordBatch, name: &str, row: usize, field: &str) -> Result<Option<i64>, EvidenceError> {
    let array = column(batch, name, field)?;
    let array = array.as_any().downcast_ref::<Int64Array>().ok_or_else(|| {
        campaign_error(
            field,
            format!("column {name:?} must be Int64, found {:?}", array.data_type()),
        )
    })?;
    Ok((!array.is_null(row)).then(|| array.value(row)))
}

fn required_u64(batch: &RecordBatch, name: &str, row: usize, field: &str) -> Result<u64, EvidenceError> {
    let array = column(batch, name, field)?;
    let array = array.as_any().downcast_ref::<UInt64Array>().ok_or_else(|| {
        campaign_error(
            field,
            format!("column {name:?} must be UInt64, found {:?}", array.data_type()),
        )
    })?;
    (!array.is_null(row))
        .then(|| array.value(row))
        .ok_or_else(|| campaign_error(field, format!("{name} row {row} is null")))
}

fn required_f64(batch: &RecordBatch, name: &str, row: usize, field: &str) -> Result<f64, EvidenceError> {
    let array = column(batch, name, field)?;
    let array = array.as_any().downcast_ref::<Float64Array>().ok_or_else(|| {
        campaign_error(
            field,
            format!("column {name:?} must be Float64, found {:?}", array.data_type()),
        )
    })?;
    (!array.is_null(row))
        .then(|| array.value(row))
        .ok_or_else(|| campaign_error(field, format!("{name} row {row} is null")))
}

pub fn encode_campaign_ledger_parquet(table: &CampaignLedgerV1) -> Result<Vec<u8>, EvidenceError> {
    let canonical = table.clone().canonicalize()?;
    let content_digest = canonical.content_digest()?;
    let mut checked_commit_digest = Vec::new();
    let mut decision_count = Vec::new();
    let mut decision_states_json = Vec::new();
    let mut matrix_id = Vec::new();
    let mut coordinate_id = Vec::new();
    let mut target_hash = Vec::new();
    let mut replica_ordinal = Vec::new();
    let mut replica_purpose = Vec::new();
    let mut attempt_id = Vec::new();
    let mut run_hash = Vec::new();
    let mut implementation_hash = Vec::new();
    let mut case_count = Vec::new();
    let mut metric_count = Vec::new();
    let mut observed_squared_error_sum = Vec::new();
    let mut member_json = Vec::new();

    for member in &canonical.members {
        checked_commit_digest.push(member.checked_commit_digest.as_str().to_string());
        decision_count.push(
            u64::try_from(member.decisions.len())
                .map_err(|source| campaign_error("campaign_ledger.decision_count", source))?,
        );
        decision_states_json.push(
            serde_json::to_string(
                &member
                    .decisions
                    .iter()
                    .map(|decision| decision.decision.as_str())
                    .collect::<Vec<_>>(),
            )
            .map_err(|source| campaign_error("campaign_ledger.decision_states_json", source))?,
        );
        matrix_id.push(member.matrix_id.clone());
        coordinate_id.push(member.coordinate_id.clone());
        target_hash.push(member.target_hash.clone());
        replica_ordinal.push(member.replica.as_ref().map(|replica| replica.ordinal));
        replica_purpose.push(
            member
                .replica
                .as_ref()
                .map(|replica| replica.purpose.value().to_string()),
        );
        attempt_id.push(member.attempt_id.clone());
        run_hash.push(member.run_hash.clone());
        implementation_hash.push(member.implementation.as_str().to_string());
        let cases = &member.checked_compact_metrics.metrics().cases;
        case_count
            .push(u64::try_from(cases.len()).map_err(|source| campaign_error("campaign_ledger.case_count", source))?);
        metric_count.push(
            u64::try_from(cases.iter().map(|case| case.metric_values.len()).sum::<usize>())
                .map_err(|source| campaign_error("campaign_ledger.metric_count", source))?,
        );
        observed_squared_error_sum.push(
            cases
                .iter()
                .map(|case| case.risk_primitive.observed_squared_error_sum)
                .sum::<f64>(),
        );
        member_json.push(
            serde_json::to_string(member).map_err(|source| campaign_error("campaign_ledger.member_json", source))?,
        );
    }

    let schema = Arc::new(Schema::new(vec![
        Field::new("checked_commit_digest", DataType::Utf8, false),
        Field::new("decision_count", DataType::UInt64, false),
        Field::new("decision_states_json", DataType::Utf8, false),
        Field::new("matrix_id", DataType::Utf8, false),
        Field::new("coordinate_id", DataType::Utf8, false),
        Field::new("target_hash", DataType::Utf8, false),
        Field::new("replica_ordinal", DataType::Int64, true),
        Field::new("replica_purpose", DataType::Utf8, true),
        Field::new("attempt_id", DataType::Utf8, false),
        Field::new("run_hash", DataType::Utf8, false),
        Field::new("implementation_hash", DataType::Utf8, false),
        Field::new("case_count", DataType::UInt64, false),
        Field::new("metric_count", DataType::UInt64, false),
        Field::new("observed_squared_error_sum", DataType::Float64, false),
        Field::new("member_json", DataType::Utf8, false),
    ]));
    let batch = RecordBatch::try_new(
        schema,
        vec![
            string_values(&checked_commit_digest),
            Arc::new(UInt64Array::from(decision_count)),
            string_values(&decision_states_json),
            string_values(&matrix_id),
            string_values(&coordinate_id),
            string_values(&target_hash),
            Arc::new(Int64Array::from(replica_ordinal)),
            optional_string_values(&replica_purpose),
            string_values(&attempt_id),
            string_values(&run_hash),
            string_values(&implementation_hash),
            Arc::new(UInt64Array::from(case_count)),
            Arc::new(UInt64Array::from(metric_count)),
            Arc::new(Float64Array::from(observed_squared_error_sum)),
            string_values(&member_json),
        ],
    )
    .map_err(|source| campaign_error("campaign_ledger.columns", source))?;
    write_campaign_batch(
        &batch,
        vec![
            KeyValue::new(
                METADATA_SCHEMA_KEY.to_string(),
                Some(CAMPAIGN_LEDGER_SCHEMA.to_string()),
            ),
            KeyValue::new(
                METADATA_CONTENT_DIGEST_KEY.to_string(),
                Some(content_digest.as_str().to_string()),
            ),
        ],
    )
}

pub fn decode_campaign_ledger_parquet(bytes: &[u8]) -> Result<CampaignLedgerV1, EvidenceError> {
    let (batch, file_metadata) = read_campaign_batch(bytes)?;
    let mut metadata = HashMap::new();
    for item in file_metadata {
        if item.key.starts_with("fern.campaign_ledger.") {
            let value = item
                .value
                .ok_or_else(|| campaign_error("campaign_ledger.metadata", "metadata value is null"))?;
            if metadata.insert(item.key.clone(), value).is_some() {
                return Err(campaign_error(
                    "campaign_ledger.metadata",
                    format!("duplicate key {:?}", item.key),
                ));
            }
        }
    }
    if metadata.get(METADATA_SCHEMA_KEY).map(String::as_str) != Some(CAMPAIGN_LEDGER_SCHEMA) {
        return Err(campaign_error(
            "campaign_ledger.schema",
            "missing or wrong campaign-ledger schema metadata",
        ));
    }
    let schema = batch.schema();
    let observed_columns = schema
        .fields()
        .iter()
        .map(|field| field.name().as_str())
        .collect::<Vec<_>>();
    if observed_columns != COLUMNS {
        return Err(campaign_error(
            "campaign_ledger.columns",
            format!("expected {COLUMNS:?}, found {observed_columns:?}"),
        ));
    }
    let mut members = Vec::with_capacity(batch.num_rows());
    for row in 0..batch.num_rows() {
        let value = optional_string(&batch, "member_json", row, "campaign_ledger.member_json")?
            .ok_or_else(|| campaign_error("campaign_ledger.member_json", format!("row {row} is null")))?;
        let member: CampaignLedgerMemberV1 =
            serde_json::from_str(&value).map_err(|source| campaign_error("campaign_ledger.member_json", source))?;
        validate_query_columns(&batch, row, &member)?;
        members.push(member);
    }
    let table = CampaignLedgerV1 {
        schema: CAMPAIGN_LEDGER_SCHEMA.to_string(),
        members,
    }
    .canonicalize()?;
    let expected = metadata
        .get(METADATA_CONTENT_DIGEST_KEY)
        .ok_or_else(|| campaign_error("campaign_ledger.content_digest", "missing metadata"))?;
    if table.content_digest()?.as_str() != expected {
        return Err(campaign_error(
            "campaign_ledger.content_digest",
            "decoded typed content differs from the recorded digest",
        ));
    }
    Ok(table)
}

fn validate_query_columns(
    batch: &RecordBatch,
    row: usize,
    member: &CampaignLedgerMemberV1,
) -> Result<(), EvidenceError> {
    for (column, expected) in [
        ("checked_commit_digest", member.checked_commit_digest.as_str()),
        ("matrix_id", member.matrix_id.as_str()),
        ("coordinate_id", member.coordinate_id.as_str()),
        ("target_hash", member.target_hash.as_str()),
        ("attempt_id", member.attempt_id.as_str()),
        ("run_hash", member.run_hash.as_str()),
        ("implementation_hash", member.implementation.as_str()),
    ] {
        let observed = optional_string(batch, column, row, "campaign_ledger.query_columns")?
            .ok_or_else(|| campaign_error("campaign_ledger.query_columns", format!("{column} row {row} is null")))?;
        if observed != expected {
            return Err(campaign_error(
                "campaign_ledger.query_columns",
                format!("{column} row {row} disagrees with typed member"),
            ));
        }
    }

    let expected_decision_states = serde_json::to_string(
        &member
            .decisions
            .iter()
            .map(|decision| decision.decision.as_str())
            .collect::<Vec<_>>(),
    )
    .map_err(|source| campaign_error("campaign_ledger.query_columns", source))?;
    validate_required_string(batch, row, "decision_states_json", &expected_decision_states)?;
    validate_required_u64(batch, row, "decision_count", member.decisions.len())?;

    match &member.replica {
        Some(replica) => {
            validate_optional_i64(batch, row, "replica_ordinal", Some(replica.ordinal))?;
            validate_optional_string(batch, row, "replica_purpose", Some(replica.purpose.value()))?;
        }
        None => {
            validate_optional_i64(batch, row, "replica_ordinal", None)?;
            validate_optional_string(batch, row, "replica_purpose", None)?;
        }
    }

    let cases = &member.checked_compact_metrics.metrics().cases;
    validate_required_u64(batch, row, "case_count", cases.len())?;
    validate_required_u64(
        batch,
        row,
        "metric_count",
        cases.iter().map(|case| case.metric_values.len()).sum(),
    )?;
    validate_required_f64(
        batch,
        row,
        "observed_squared_error_sum",
        cases
            .iter()
            .map(|case| case.risk_primitive.observed_squared_error_sum)
            .sum(),
    )?;
    Ok(())
}

fn validate_required_string(
    batch: &RecordBatch,
    row: usize,
    column: &str,
    expected: &str,
) -> Result<(), EvidenceError> {
    let observed = optional_string(batch, column, row, "campaign_ledger.query_columns")?
        .ok_or_else(|| campaign_error("campaign_ledger.query_columns", format!("{column} row {row} is null")))?;
    if observed != expected {
        return Err(campaign_error(
            "campaign_ledger.query_columns",
            format!("{column} row {row} disagrees with typed member"),
        ));
    }
    Ok(())
}

fn validate_optional_string(
    batch: &RecordBatch,
    row: usize,
    column: &str,
    expected: Option<&str>,
) -> Result<(), EvidenceError> {
    let observed = optional_string(batch, column, row, "campaign_ledger.query_columns")?;
    if observed.as_deref() != expected {
        return Err(campaign_error(
            "campaign_ledger.query_columns",
            format!("{column} row {row} disagrees with typed member"),
        ));
    }
    Ok(())
}

fn validate_required_u64(batch: &RecordBatch, row: usize, column: &str, expected: usize) -> Result<(), EvidenceError> {
    let expected = u64::try_from(expected).map_err(|source| campaign_error("campaign_ledger.query_columns", source))?;
    let observed = required_u64(batch, column, row, "campaign_ledger.query_columns")?;
    if observed != expected {
        return Err(campaign_error(
            "campaign_ledger.query_columns",
            format!("{column} row {row} disagrees with typed member"),
        ));
    }
    Ok(())
}

fn validate_optional_i64(
    batch: &RecordBatch,
    row: usize,
    column: &str,
    expected: Option<i64>,
) -> Result<(), EvidenceError> {
    let observed = optional_i64(batch, column, row, "campaign_ledger.query_columns")?;
    if observed != expected {
        return Err(campaign_error(
            "campaign_ledger.query_columns",
            format!("{column} row {row} disagrees with typed member"),
        ));
    }
    Ok(())
}

fn validate_required_f64(batch: &RecordBatch, row: usize, column: &str, expected: f64) -> Result<(), EvidenceError> {
    let observed = required_f64(batch, column, row, "campaign_ledger.query_columns")?;
    if observed.to_bits() != expected.to_bits() {
        return Err(campaign_error(
            "campaign_ledger.query_columns",
            format!("{column} row {row} disagrees with typed member"),
        ));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tests::campaign_ledger;

    fn replace_column(batch: RecordBatch, name: &str, replacement: ArrayRef) -> RecordBatch {
        let index = batch.schema().index_of(name).unwrap();
        let mut columns = batch.columns().to_vec();
        columns[index] = replacement;
        RecordBatch::try_new(batch.schema(), columns).unwrap()
    }

    #[test]
    fn campaign_ledger_round_trip_is_typed_but_not_a_release() {
        let table = campaign_ledger();
        let bytes = encode_campaign_ledger_parquet(&table).unwrap();
        assert_eq!(
            decode_campaign_ledger_parquet(&bytes).unwrap(),
            table.canonicalize().unwrap()
        );
        assert!(!String::from_utf8_lossy(&bytes).contains("fern.release_metrics_table.v1"));
    }

    #[test]
    fn campaign_query_columns_cannot_disagree_with_member_json() {
        let table = campaign_ledger();
        let bytes = encode_campaign_ledger_parquet(&table).unwrap();
        let (batch, metadata) = read_campaign_batch(&bytes).unwrap();
        let changed_batch = replace_column(batch, "run_hash", Arc::new(StringArray::from(vec!["wrong-run"])));
        let changed = write_campaign_batch(&changed_batch, metadata).unwrap();
        assert_eq!(
            decode_campaign_ledger_parquet(&changed).unwrap_err().field(),
            "campaign_ledger.query_columns"
        );
    }

    #[test]
    fn campaign_decision_query_columns_cannot_disagree_with_member_json() {
        let table = campaign_ledger();
        let bytes = encode_campaign_ledger_parquet(&table).unwrap();
        let (batch, metadata) = read_campaign_batch(&bytes).unwrap();
        let changed_batch = replace_column(batch, "decision_count", Arc::new(UInt64Array::from(vec![99_u64])));
        let changed = write_campaign_batch(&changed_batch, metadata).unwrap();
        assert_eq!(
            decode_campaign_ledger_parquet(&changed).unwrap_err().field(),
            "campaign_ledger.query_columns"
        );
    }
}
