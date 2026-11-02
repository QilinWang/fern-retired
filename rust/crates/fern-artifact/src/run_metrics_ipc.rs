//! The `metrics.ipc` run receipt, written with direct Arrow IPC.
//!
//! Moved here from the CLI's polars route (owner decision 2026-08-14): the
//! receipt is one row of scalars, the readers in this workspace are already
//! Arrow-native or dtype-tolerant, and a DataFrame engine for a one-row
//! write was the mismatch correction 90 measured. The column vocabulary is
//! preserved exactly as the polars writer produced it: a null cell is a
//! null Float64, booleans are Boolean, numbers Float64, strings Utf8View.
//! CHG 2026-08-26: the 2026-08-14 note said a real campaign receipt had
//! 52 Float64, 10 Utf8View, 1 Boolean. That count was never a stored
//! schema. Every 67-column receipt still on disk (4,820 files, already in
//! this shape on 2026-07-28) has 56 Float64, 10 Utf8View, 1 Boolean. The
//! four Float64 columns between 52 and 56 are the 5%/95% interval fields
//! `q05_q95_coverage`, `q05_q95_crossing_rate`, `q05_q95_mean_width`, and
//! `q05_q95_nominal` — present since those files were written; the first
//! three are always null because no campaign model reports 5% and 95%
//! quantiles, and nominal is the advertised 0.90 width, not a measured
//! 5/95 score. Receipts written before 2026-08-26 carry those four q05_q95
//! keys; they were never populated. Stored files keep them. New writes,
//! including quantile_diag.json, drop them. Nothing re-runs this census.
//! Owner 2026-08-26: those four columns are useless and must not be written
//! again. The live scorer stopped emitting them 2026-08-21, and the
//! metrics-row mapper drops them if they reappear. Column count is
//! not a contract. Readers key on column presence, not dtype — that reader
//! law predates this move and is unchanged by it — so extra columns are
//! inert by design. The durable invariant is
//! `every_column_the_writer_emits_is_one_of_the_three_receipt_dtypes`.
//!
//! The writer takes columns of equal length rather than enforcing one row:
//! the one-row contract is the READER'S law (`compact_metrics` refuses a
//! multi-row frame as a different artifact class), and its regression test
//! needs to construct the refused shape through this same writer.

use std::sync::Arc;

use arrow_array::{ArrayRef, BooleanArray, Float64Array, RecordBatch, StringViewArray};
use arrow_ipc::writer::FileWriter;
use arrow_schema::{DataType, Field, Schema};

use super::ContractError;

/// One cell of the run-metrics receipt. `Null` is a null number: the
/// polars writer typed absent metrics as null Float64 and readers rely on
/// the column existing.
#[derive(Debug, Clone, PartialEq)]
pub enum RunMetricsCellV1 {
    Null,
    Bool(bool),
    Number(f64),
    Text(String),
}

fn ipc_error(error: impl std::fmt::Display) -> ContractError {
    ContractError::new("run_metrics.ipc", error.to_string())
}

/// Arrow IPC file bytes for a run-metrics frame. Each column's cells must
/// be one type (nulls coerce into a numeric column); mixed-type columns
/// refuse with the column named.
pub fn run_metrics_ipc_bytes(columns: &[(String, Vec<RunMetricsCellV1>)]) -> Result<Vec<u8>, ContractError> {
    let row_count = columns.first().map(|(_, cells)| cells.len()).unwrap_or(0);
    let mut fields = Vec::with_capacity(columns.len());
    let mut arrays: Vec<ArrayRef> = Vec::with_capacity(columns.len());
    for (name, cells) in columns {
        if cells.len() != row_count {
            return Err(ipc_error(format!(
                "column {name:?} has {} cells; the frame's first column has {row_count}",
                cells.len()
            )));
        }
        let has_bool = cells.iter().any(|cell| matches!(cell, RunMetricsCellV1::Bool(_)));
        let has_number = cells.iter().any(|cell| matches!(cell, RunMetricsCellV1::Number(_)));
        let has_text = cells.iter().any(|cell| matches!(cell, RunMetricsCellV1::Text(_)));
        if usize::from(has_bool) + usize::from(has_number) + usize::from(has_text) > 1 {
            return Err(ipc_error(format!(
                "column {name:?} mixes cell types; one column, one type"
            )));
        }
        let (data_type, array): (DataType, ArrayRef) = if has_bool {
            let values: Vec<Option<bool>> = cells
                .iter()
                .map(|cell| match cell {
                    RunMetricsCellV1::Bool(flag) => Some(*flag),
                    _ => None,
                })
                .collect();
            (DataType::Boolean, Arc::new(BooleanArray::from(values)))
        } else if has_text {
            let values: Vec<Option<&str>> = cells
                .iter()
                .map(|cell| match cell {
                    RunMetricsCellV1::Text(text) => Some(text.as_str()),
                    _ => None,
                })
                .collect();
            (DataType::Utf8View, Arc::new(StringViewArray::from(values)))
        } else {
            // Numbers, or a column of nothing but nulls: null Float64.
            let values: Vec<Option<f64>> = cells
                .iter()
                .map(|cell| match cell {
                    RunMetricsCellV1::Number(value) => Some(*value),
                    _ => None,
                })
                .collect();
            (DataType::Float64, Arc::new(Float64Array::from(values)))
        };
        fields.push(Field::new(name.clone(), data_type, true));
        arrays.push(array);
    }
    let schema = Arc::new(Schema::new(fields));
    let batch = RecordBatch::try_new(schema.clone(), arrays).map_err(ipc_error)?;
    let mut bytes = Vec::new();
    let mut writer = FileWriter::try_new(&mut bytes, &schema).map_err(ipc_error)?;
    writer.write(&batch).map_err(ipc_error)?;
    writer.finish().map_err(ipc_error)?;
    drop(writer);
    Ok(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;
    use arrow_ipc::reader::FileReader;

    #[test]
    fn one_row_receipt_round_trips_with_the_polars_column_vocabulary() {
        let bytes = run_metrics_ipc_bytes(&[
            ("mse".to_string(), vec![RunMetricsCellV1::Number(1.5)]),
            ("note".to_string(), vec![RunMetricsCellV1::Text("clean".to_string())]),
            ("converged".to_string(), vec![RunMetricsCellV1::Bool(true)]),
            ("crps".to_string(), vec![RunMetricsCellV1::Null]),
        ])
        .unwrap();
        let reader = FileReader::try_new(std::io::Cursor::new(bytes), None).unwrap();
        let schema = reader.schema();
        assert_eq!(schema.field(0).data_type(), &DataType::Float64);
        assert_eq!(
            schema.field(1).data_type(),
            &DataType::Utf8View,
            "the dtype history actually wrote"
        );
        assert_eq!(schema.field(2).data_type(), &DataType::Boolean);
        assert_eq!(
            schema.field(3).data_type(),
            &DataType::Float64,
            "null cell is a null number"
        );
        let batches: Vec<_> = reader.collect::<Result<_, _>>().unwrap();
        assert_eq!(batches.len(), 1);
        assert_eq!(batches[0].num_rows(), 1);
        assert!(batches[0].column(3).is_null(0));
    }

    /// Column count is not a contract. Extra columns must still be one of
    /// Float64, Boolean, or Utf8View; a fourth dtype would be a vocabulary
    /// change, not a wider receipt.
    #[test]
    fn every_column_the_writer_emits_is_one_of_the_three_receipt_dtypes() {
        let bytes = run_metrics_ipc_bytes(&[
            ("mse".to_string(), vec![RunMetricsCellV1::Number(1.5)]),
            ("note".to_string(), vec![RunMetricsCellV1::Text("clean".to_string())]),
            ("converged".to_string(), vec![RunMetricsCellV1::Bool(true)]),
            ("crps".to_string(), vec![RunMetricsCellV1::Null]),
            ("extra".to_string(), vec![RunMetricsCellV1::Number(0.0)]),
        ])
        .unwrap();
        let reader = FileReader::try_new(std::io::Cursor::new(bytes), None).unwrap();
        for field in reader.schema().fields() {
            assert!(
                matches!(
                    field.data_type(),
                    DataType::Float64 | DataType::Boolean | DataType::Utf8View
                ),
                "column {:?} has {:?}; extra columns must still be Float64, Boolean, or Utf8View",
                field.name(),
                field.data_type()
            );
        }
    }

    #[test]
    fn a_mixed_type_column_refuses_naming_the_column() {
        let error = run_metrics_ipc_bytes(&[(
            "mse".to_string(),
            vec![RunMetricsCellV1::Number(1.0), RunMetricsCellV1::Text("two".to_string())],
        )])
        .unwrap_err()
        .to_string();
        assert!(error.contains("mse"), "{error}");
    }
}
