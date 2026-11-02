//! CLI Pass 3 Arrow IPC helpers for analysis-cohort tables.
//!
//! Parent-directory creation, pretty JSON writes, coverage-level defaults, and
//! safetensors numeric decoding live in `fern_post::artifact_io` so the worker
//! can score artifacts without this crate.

use std::io::Cursor;
use std::path::Path;

use arrow_array::RecordBatch;
use arrow_ipc::{reader::FileReader, writer::FileWriter};
use arrow_select::concat::concat_batches;

use crate::Fallible;

/// Read every batch from one Arrow IPC file into the single table shape used
/// by Fern's accepted-matrix and analysis-cohort contracts.
pub fn read_ipc_record_batch(path: &Path) -> Fallible<RecordBatch> {
    let reader = FileReader::try_new(std::fs::File::open(path)?, None)?;
    let schema = reader.schema();
    let batches = reader.collect::<Result<Vec<_>, _>>()?;
    Ok(concat_batches(&schema, &batches)?)
}

/// Encode one complete table as an Arrow IPC file. Callers own publication
/// durability because each artifact boundary already has its own write law.
pub fn record_batch_ipc_bytes(batch: &RecordBatch) -> Fallible<Vec<u8>> {
    let mut bytes = Cursor::new(Vec::new());
    let mut writer = FileWriter::try_new(&mut bytes, &batch.schema())?;
    writer.write(batch)?;
    writer.finish()?;
    drop(writer);
    Ok(bytes.into_inner())
}

#[cfg(test)]
mod tests {
    use std::sync::Arc;

    use arrow_array::{ArrayRef, Int64Array, StringViewArray};
    use arrow_schema::{DataType, Field, Schema};

    use super::*;

    #[test]
    fn arrow_ipc_bytes_round_trip_the_declared_batch_exactly() {
        let schema = Arc::new(Schema::new(vec![
            Field::new("identity", DataType::Utf8View, false),
            Field::new("value", DataType::Int64, true),
        ]));
        let batch = RecordBatch::try_new(
            schema,
            vec![
                Arc::new(StringViewArray::from(vec!["a", "b"])) as ArrayRef,
                Arc::new(Int64Array::from(vec![Some(1), None])),
            ],
        )
        .unwrap();

        let bytes = record_batch_ipc_bytes(&batch).unwrap();
        let mut reader = FileReader::try_new(Cursor::new(bytes), None).unwrap();
        let reopened = reader.next().unwrap().unwrap();

        assert!(reader.next().is_none());
        assert_eq!(reopened, batch);
    }
}
