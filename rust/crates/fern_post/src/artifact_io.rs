//! Mechanical artifact IO used by the end-of-run writers.
//!
//! Keep this module mechanical: parent-directory creation, pretty JSON writes,
//! coverage-level defaults, and safetensors numeric decoding. Scientific
//! scoring stays in the writer modules.

use std::fs;
use std::io::Write;
use std::path::Path;

use ndarray::{ArrayD, IxDyn};
use safetensors::Dtype;
use safetensors::tensor::TensorView;
use serde::Serialize;

use crate::{Fallible, invalid_input};

pub const DEFAULT_COVERAGE_LEVELS: &[f64] = &[0.50, 0.80, 0.90, 0.95];

pub fn ensure_parent_dir(path: &Path) -> Fallible<()> {
    if let Some(parent) = path.parent().filter(|parent| !parent.as_os_str().is_empty()) {
        fs::create_dir_all(parent)?;
    }
    Ok(())
}

fn write_file_durably(path: &Path, bytes: &[u8]) -> Fallible<()> {
    let directory = path
        .parent()
        .ok_or_else(|| invalid_input(format!("{path:?} has no parent directory")))?;
    let mut temp_name = path.file_name().unwrap_or_default().to_os_string();
    temp_name.push(".partial");
    let temp_path = directory.join(temp_name);
    let mut file = fs::File::create(&temp_path)?;
    file.write_all(bytes)?;
    file.sync_all()?;
    drop(file);
    fs::rename(&temp_path, path)?;
    if let Ok(directory_handle) = fs::File::open(directory) {
        let _ = directory_handle.sync_all();
    }
    Ok(())
}

pub fn write_pretty_json<T: Serialize>(path: &Path, value: &T) -> Fallible<()> {
    ensure_parent_dir(path)?;
    write_file_durably(path, (serde_json::to_string_pretty(value)? + "\n").as_bytes())
}

pub fn resolve_coverage_levels(levels: Vec<f64>) -> Fallible<Vec<f64>> {
    let resolved = if levels.is_empty() {
        DEFAULT_COVERAGE_LEVELS.to_vec()
    } else {
        levels
    };
    for level in &resolved {
        if !level.is_finite() || *level <= 0.0 || *level >= 1.0 {
            return Err(invalid_input(format!(
                "coverage levels must be finite values in (0,1); got {level}"
            )));
        }
    }
    Ok(resolved)
}

pub fn tensor_view_to_f64_values(view: TensorView<'_>, name: &str) -> Fallible<Vec<f64>> {
    match view.dtype() {
        Dtype::F64 => Ok(view
            .data()
            .chunks_exact(8)
            .map(|chunk| f64::from_le_bytes(chunk.try_into().expect("F64 chunks are 8 bytes")))
            .collect()),
        Dtype::F32 => Ok(view
            .data()
            .chunks_exact(4)
            .map(|chunk| f32::from_le_bytes(chunk.try_into().expect("F32 chunks are 4 bytes")) as f64)
            .collect()),
        dtype => Err(invalid_input(format!("{name} must be F32 or F64; got {dtype:?}"))),
    }
}

pub fn tensor_view_to_array_d(
    view: TensorView<'_>,
    name: &str,
    rank: usize,
    expected_shape: &str,
) -> Fallible<ArrayD<f64>> {
    let shape = view.shape();
    if shape.len() != rank {
        return Err(invalid_input(format!(
            "{name} expects {expected_shape}, got shape {shape:?}"
        )));
    }
    Ok(ArrayD::from_shape_vec(
        IxDyn(shape),
        tensor_view_to_f64_values(view, name)?,
    )?)
}
