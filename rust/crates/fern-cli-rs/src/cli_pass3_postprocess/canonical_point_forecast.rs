//! Canonical point-forecast artifact shared by trained and zero-shot workers.
//!
//! The two producers use different raw shapes:
//! - trained models: `pred [N,S,H,D]` (the point forecast is the sample mean);
//! - pretrained quantile models: `median [N,H,D]`, with some adapters folding
//!   channels into the window axis.
//!
//! This module is the one normalization boundary.  The production adapters
//! currently emit an explicitly legacy point view without a complete typed meaning
//! at `point_forecast [case=1,forecast_origin,horizon,channel]`.  The separate
//! `write_v1` path emits the real Pass-6 contract only after the caller supplies
//! point meaning and source identity.  Neither stores a target copy: one shared
//! ScientificCase owns the reference trajectory.

use std::collections::{BTreeMap, HashMap};
use std::fs;
use std::path::Path;

use fern_artifact::{
    DensePointForecastV1, ReportedForecastMeaningV1, TENSOR_LAYOUT_SCHEMA, TensorAxis, TensorByteOrder, TensorDtype,
    TensorLayout, TensorStorageOrder, decode_dense_point, encode_dense_point_f32, predictive_sample_mean_v1,
};
use safetensors::tensor::TensorView;
use safetensors::{Dtype, SafeTensors, serialize};
use serde::Deserialize;

use crate::{Fallible, invalid_input, write_file_durably};

pub(crate) use fern_run::results::POINT_FORECAST_FILE;
pub(crate) const POINT_FORECAST_TENSOR: &str = "point_forecast";
const LEGACY_POINT_FORECAST_SCHEMA: &str = "fern.legacy_point_forecast.v1";
const SCHEMA_METADATA: &str = "schema";
const AXIS_ORDER_METADATA: &str = "axis_order";
const CHANNEL_IDENTITY_METADATA: &str = "channel_identity";
const CANONICAL_AXES: [&str; 4] = ["case", "forecast_origin", "horizon", "channel"];

/// Typed observation used later by Pass 6C to recompute scientific content.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct CanonicalPointForecast {
    pub(crate) layout: TensorLayout,
    pub(crate) canonical_f32_le_bytes: Vec<u8>,
}

/// Write the new typed V1 artifact.  Unlike the legacy adapters below, this
/// requires the predeclared point functional and source relation up front.
pub(crate) fn write_v1(
    out_path: &Path,
    shape: [usize; 4],
    values: &[f32],
    reported_meaning: &ReportedForecastMeaningV1,
) -> Fallible<()> {
    let bytes = encode_dense_point_f32(shape, values, reported_meaning)
        .map_err(|error| invalid_input(format!("invalid DensePointForecastV1: {error}")))?;
    write_file_durably(out_path, &bytes)?;
    read_v1(out_path)?;
    Ok(())
}

pub(crate) fn read_v1(path: &Path) -> Fallible<DensePointForecastV1> {
    let bytes = fs::read(path)?;
    decode_dense_point(&bytes)
        .map_err(|error| invalid_input(format!("invalid DensePointForecastV1 at {}: {error}", path.display())))
}

#[derive(Debug, Deserialize)]
struct ChannelIdentity {
    schema: String,
    axis_kind: String,
    tensor_channel_count: Option<usize>,
    world_channel_count: Option<usize>,
    fold_order: Option<String>,
}

/// Canonical native quantiles retained beside the cross-model point view.
/// Values are row-major `[case=1,forecast_origin,horizon,channel,quantile]`.
pub(crate) struct CanonicalQuantileForecast {
    pub(crate) shape: [usize; 5],
    pub(crate) values: Vec<f32>,
    pub(crate) levels: Vec<f32>,
}

/// Reinterpret a pretrained adapter's native quantiles without dropping the
/// probability axis. Folded channel adapters are unfolded by the same metadata
/// contract used for their point view.
pub(crate) fn read_quantile_source_raw(raw_path: &Path) -> Fallible<CanonicalQuantileForecast> {
    let bytes = fs::read(raw_path)?;
    let (_, header) = SafeTensors::read_metadata(&bytes)?;
    let metadata = header.metadata().clone().unwrap_or_default();
    let tensors = SafeTensors::deserialize(&bytes)?;
    let quantiles = tensors.tensor("quantiles")?;
    let quantile_levels = tensors.tensor("quantile_levels")?;
    require_f32("quantiles", quantiles.dtype())?;
    require_f32("quantile_levels", quantile_levels.dtype())?;
    let shape = quantiles.shape();
    if shape.len() != 4 || shape.iter().any(|length| *length == 0) {
        return Err(invalid_input(format!(
            "pretrained native quantiles require [N,H,D,Q] with positive axes, found {shape:?}"
        )));
    }
    let level_shape = quantile_levels.shape();
    if level_shape != [shape[3]] {
        return Err(invalid_input(format!(
            "quantile_levels must have shape [Q={}], found {level_shape:?}",
            shape[3]
        )));
    }
    let source = f32_values("quantiles", quantiles.data())?;
    let levels = f32_values("quantile_levels", quantile_levels.data())?;
    let (flat_origins, horizon, tensor_channels, level_count) = (shape[0], shape[1], shape[2], shape[3]);
    let channel_identity = metadata
        .get(CHANNEL_IDENTITY_METADATA)
        .map(|text| serde_json::from_str::<ChannelIdentity>(text))
        .transpose()
        .map_err(|error| invalid_input(format!("invalid channel_identity metadata: {error}")))?;

    if let Some(identity) = &channel_identity {
        if identity.schema != "fern.channel_identity.v1" {
            return Err(invalid_input(format!(
                "unsupported channel_identity schema {:?}",
                identity.schema
            )));
        }
        if identity
            .tensor_channel_count
            .is_some_and(|count| count != tensor_channels)
        {
            return Err(invalid_input(format!(
                "channel_identity declares tensor_channel_count {:?}, but quantiles have D={tensor_channels}",
                identity.tensor_channel_count
            )));
        }
        if identity.axis_kind == "per_channel"
            && identity
                .world_channel_count
                .is_some_and(|count| count != tensor_channels)
        {
            return Err(invalid_input(format!(
                "per-channel quantiles have D={tensor_channels}, but channel_identity declares world_channel_count {:?}",
                identity.world_channel_count
            )));
        }
    }

    match channel_identity {
        Some(identity) if identity.axis_kind == "flattened_into_windows" => {
            if tensor_channels != 1 || identity.fold_order.as_deref() != Some("windows_then_channels") {
                return Err(invalid_input(
                    "folded pretrained quantiles require D=1 and fold_order=windows_then_channels",
                ));
            }
            let channels = identity
                .world_channel_count
                .filter(|count| *count > 0)
                .ok_or_else(|| invalid_input("folded pretrained quantiles are missing world_channel_count"))?;
            if flat_origins % channels != 0 {
                return Err(invalid_input(format!(
                    "folded pretrained quantiles have {flat_origins} rows, not a multiple of {channels} channels"
                )));
            }
            let origins = flat_origins / channels;
            let mut unfolded = Vec::with_capacity(origins * horizon * channels * level_count);
            for origin in 0..origins {
                for step in 0..horizon {
                    for channel in 0..channels {
                        for quantile in 0..level_count {
                            let source_index =
                                (((origin * channels + channel) * horizon + step) * level_count) + quantile;
                            unfolded.push(source[source_index]);
                        }
                    }
                }
            }
            Ok(CanonicalQuantileForecast {
                shape: [1, origins, horizon, channels, level_count],
                values: unfolded,
                levels,
            })
        }
        Some(identity) if identity.axis_kind != "per_channel" => Err(invalid_input(format!(
            "unsupported channel_identity.axis_kind {:?}",
            identity.axis_kind
        ))),
        _ => Ok(CanonicalQuantileForecast {
            shape: [1, flat_origins, horizon, tensor_channels, level_count],
            values: source,
            levels,
        }),
    }
}

/// Convert one trained-model raw handoff into the canonical point forecast.
pub(crate) fn write_from_training_raw(raw_path: &Path, out_path: &Path) -> Fallible<()> {
    let bytes = fs::read(raw_path)?;
    let tensors = SafeTensors::deserialize(&bytes)?;
    let pred = tensors.tensor("pred")?;
    require_f32("pred", pred.dtype())?;
    let shape = pred.shape();
    if shape.len() != 4 || shape.iter().any(|length| *length == 0) {
        return Err(invalid_input(format!(
            "trained point forecast requires pred [N,S,H,D] with positive axes, found {shape:?}"
        )));
    }
    let (windows, samples, horizon, channels) = (shape[0], shape[1], shape[2], shape[3]);
    let source = f32_values("pred", pred.data())?;
    let mut point = Vec::with_capacity(windows * horizon * channels);
    let mut sample_values = Vec::with_capacity(samples);
    for window in 0..windows {
        for step in 0..horizon {
            for channel in 0..channels {
                sample_values.clear();
                sample_values.extend(
                    (0..samples)
                        .map(|sample| source[((window * samples + sample) * horizon + step) * channels + channel]),
                );
                point.push(
                    predictive_sample_mean_v1(&sample_values)
                        .map_err(|error| invalid_input(format!("invalid trained sample mean: {error}")))?,
                );
            }
        }
    }
    write_canonical(out_path, [1, windows, horizon, channels], &point)
}

/// Read and validate the file that the two adapters above produce.
pub(crate) fn read_canonical(path: &Path) -> Fallible<CanonicalPointForecast> {
    let bytes = fs::read(path)?;
    read_canonical_bytes(&bytes, &path.display().to_string())
}

/// Parse bytes already obtained through an Attempt-safe reader.
pub(crate) fn read_canonical_bytes(bytes: &[u8], source_name: &str) -> Fallible<CanonicalPointForecast> {
    let (_, header) = SafeTensors::read_metadata(bytes)?;
    let metadata = header.metadata().clone().unwrap_or_default();
    if metadata.get(SCHEMA_METADATA).map(String::as_str) != Some(LEGACY_POINT_FORECAST_SCHEMA) {
        return Err(invalid_input(format!(
            "{source_name} is missing schema {LEGACY_POINT_FORECAST_SCHEMA}",
        )));
    }
    let expected_axes = serde_json::to_string(&CANONICAL_AXES)?;
    if metadata.get(AXIS_ORDER_METADATA) != Some(&expected_axes) {
        return Err(invalid_input(format!(
            "{source_name} does not declare canonical axis order {expected_axes}",
        )));
    }
    let tensors = SafeTensors::deserialize(bytes)?;
    let tensor = tensors.tensor(POINT_FORECAST_TENSOR)?;
    require_f32(POINT_FORECAST_TENSOR, tensor.dtype())?;
    let shape = tensor.shape();
    if shape.len() != 4 || shape[0] != 1 || shape.iter().any(|length| *length == 0) {
        return Err(invalid_input(format!(
            "canonical point forecast requires [case=1,forecast_origin,horizon,channel], found {shape:?}"
        )));
    }
    f32_values(POINT_FORECAST_TENSOR, tensor.data())?;
    let layout = TensorLayout {
        schema: TENSOR_LAYOUT_SCHEMA.to_string(),
        dtype: TensorDtype::Float32,
        byte_order: TensorByteOrder::LittleEndian,
        storage_order: TensorStorageOrder::RowMajor,
        axes: CANONICAL_AXES
            .iter()
            .zip(shape)
            .map(|(name, length)| TensorAxis::try_new(*name, *length as u64))
            .collect::<Result<Vec<_>, _>>()
            .map_err(|error| invalid_input(format!("invalid canonical point layout: {error}")))?,
    };
    layout
        .validate()
        .map_err(|error| invalid_input(format!("invalid canonical point layout: {error}")))?;
    Ok(CanonicalPointForecast {
        layout,
        canonical_f32_le_bytes: tensor.data().to_vec(),
    })
}

fn write_canonical(path: &Path, shape: [usize; 4], values: &[f32]) -> Fallible<()> {
    let expected = shape.iter().product::<usize>();
    if values.len() != expected {
        return Err(invalid_input(format!(
            "canonical point payload has {} values, expected {expected} for {shape:?}",
            values.len()
        )));
    }
    if values.iter().any(|value| !value.is_finite()) {
        return Err(invalid_input("canonical point forecast contains NaN or infinity"));
    }
    let payload = values.iter().flat_map(|value| value.to_le_bytes()).collect::<Vec<_>>();
    let mut tensors = BTreeMap::new();
    tensors.insert(
        POINT_FORECAST_TENSOR,
        TensorView::new(Dtype::F32, shape.to_vec(), &payload)?,
    );
    let metadata = HashMap::from([
        (SCHEMA_METADATA.to_string(), LEGACY_POINT_FORECAST_SCHEMA.to_string()),
        (AXIS_ORDER_METADATA.to_string(), serde_json::to_string(&CANONICAL_AXES)?),
    ]);
    let bytes = serialize(tensors, Some(metadata))?;
    write_file_durably(path, &bytes)?;
    // A successful write is not the postcondition. Reopen through the same
    // typed reader Pass 6C will use, so malformed metadata or layout fails the
    // parent before the run can be marked complete.
    read_canonical(path)?;
    Ok(())
}

fn require_f32(name: &str, dtype: Dtype) -> Fallible<()> {
    if dtype != Dtype::F32 {
        return Err(invalid_input(format!(
            "{name} has dtype {dtype:?}; canonical point forecasts require F32"
        )));
    }
    Ok(())
}

fn f32_values(name: &str, bytes: &[u8]) -> Fallible<Vec<f32>> {
    if !bytes.len().is_multiple_of(4) {
        return Err(invalid_input(format!(
            "{name} has {} payload bytes, not a whole number of f32 values",
            bytes.len()
        )));
    }
    let values = bytes
        .chunks_exact(4)
        .map(|chunk| f32::from_le_bytes([chunk[0], chunk[1], chunk[2], chunk[3]]))
        .collect::<Vec<_>>();
    if values.iter().any(|value| !value.is_finite()) {
        return Err(invalid_input(format!("{name} contains NaN or infinity")));
    }
    Ok(values)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_root(label: &str) -> std::path::PathBuf {
        let root = std::env::temp_dir().join(format!(
            "fern_point_forecast_{label}_{}_{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir_all(&root).unwrap();
        root
    }

    fn f32_bytes(values: &[f32]) -> Vec<u8> {
        values.iter().flat_map(|value| value.to_le_bytes()).collect()
    }

    #[test]
    fn trained_samples_become_one_mean_point_forecast() {
        let root = temp_root("trained");
        let raw = root.join("raw.safetensors");
        let out = root.join(POINT_FORECAST_FILE);
        let bytes = f32_bytes(&[1.0, 10.0, 3.0, 14.0]);
        fs::write(
            &raw,
            serialize(
                [("pred", TensorView::new(Dtype::F32, vec![1, 2, 1, 2], &bytes).unwrap())],
                None,
            )
            .unwrap(),
        )
        .unwrap();

        write_from_training_raw(&raw, &out).unwrap();
        let observed = read_canonical(&out).unwrap();
        assert_eq!(
            observed.layout.axes.iter().map(|axis| axis.length).collect::<Vec<_>>(),
            [1, 1, 1, 2]
        );
        assert_eq!(
            f32_values("point", &observed.canonical_f32_le_bytes).unwrap(),
            [2.0, 12.0]
        );
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn trained_sample_mean_uses_the_same_ordered_f32_rule_as_the_checker() {
        let root = temp_root("trained-mean-v1");
        let raw = root.join("raw.safetensors");
        let out = root.join(POINT_FORECAST_FILE);
        let bytes = f32_bytes(&[100_000_000.0, 1.0, -100_000_000.0]);
        fs::write(
            &raw,
            serialize(
                [("pred", TensorView::new(Dtype::F32, vec![1, 3, 1, 1], &bytes).unwrap())],
                None,
            )
            .unwrap(),
        )
        .unwrap();

        write_from_training_raw(&raw, &out).unwrap();
        let observed = read_canonical(&out).unwrap();
        assert_eq!(
            f32_values("point", &observed.canonical_f32_le_bytes).unwrap(),
            [0.0],
            "V1 accumulates the three f32 samples in their stored order"
        );
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn native_quantiles_keep_every_level_in_canonical_axis_order() {
        let root = temp_root("native-quantiles");
        let raw = root.join("raw.safetensors");
        let values = f32_bytes(&[1.0, 2.0, 3.0, 10.0, 20.0, 30.0]);
        let levels = f32_bytes(&[0.1, 0.5, 0.9]);
        fs::write(
            &raw,
            serialize(
                [
                    (
                        "quantiles",
                        TensorView::new(Dtype::F32, vec![1, 1, 2, 3], &values).unwrap(),
                    ),
                    (
                        "quantile_levels",
                        TensorView::new(Dtype::F32, vec![3], &levels).unwrap(),
                    ),
                ],
                None,
            )
            .unwrap(),
        )
        .unwrap();

        let observed = read_quantile_source_raw(&raw).unwrap();
        assert_eq!(observed.shape, [1, 1, 1, 2, 3]);
        assert_eq!(observed.values, [1.0, 2.0, 3.0, 10.0, 20.0, 30.0]);
        assert_eq!(observed.levels, [0.1, 0.5, 0.9]);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn native_quantiles_unfold_channels_without_moving_the_quantile_axis() {
        let root = temp_root("native-quantiles-folded");
        let raw = root.join("raw.safetensors");
        // Worker rows are channel-major within each origin. The canonical
        // result is [origin,horizon,channel,quantile].
        let values = f32_bytes(&[1.0, 2.0, 10.0, 20.0]);
        let levels = f32_bytes(&[0.25, 0.75]);
        let metadata = HashMap::from([(
            CHANNEL_IDENTITY_METADATA.to_string(),
            serde_json::json!({
                "schema": "fern.channel_identity.v1",
                "axis_kind": "flattened_into_windows",
                "tensor_channel_count": 1,
                "world_channel_count": 2,
                "fold_order": "windows_then_channels"
            })
            .to_string(),
        )]);
        fs::write(
            &raw,
            serialize(
                [
                    (
                        "quantiles",
                        TensorView::new(Dtype::F32, vec![2, 1, 1, 2], &values).unwrap(),
                    ),
                    (
                        "quantile_levels",
                        TensorView::new(Dtype::F32, vec![2], &levels).unwrap(),
                    ),
                ],
                Some(metadata),
            )
            .unwrap(),
        )
        .unwrap();

        let observed = read_quantile_source_raw(&raw).unwrap();
        assert_eq!(observed.shape, [1, 1, 1, 2, 2]);
        assert_eq!(observed.values, [1.0, 2.0, 10.0, 20.0]);
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn reader_rejects_a_noncanonical_case_axis() {
        let root = temp_root("bad_case");
        let path = root.join(POINT_FORECAST_FILE);
        write_canonical(&path, [1, 1, 1, 1], &[1.0]).unwrap();
        let bytes = f32_bytes(&[1.0, 2.0]);
        let metadata = HashMap::from([
            (SCHEMA_METADATA.to_string(), LEGACY_POINT_FORECAST_SCHEMA.to_string()),
            (
                AXIS_ORDER_METADATA.to_string(),
                serde_json::to_string(&CANONICAL_AXES).unwrap(),
            ),
        ]);
        fs::write(
            &path,
            serialize(
                [(
                    POINT_FORECAST_TENSOR,
                    TensorView::new(Dtype::F32, vec![2, 1, 1, 1], &bytes).unwrap(),
                )],
                Some(metadata),
            )
            .unwrap(),
        )
        .unwrap();
        assert!(read_canonical(&path).is_err());
        fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn non_finite_point_forecasts_fail_before_completion() {
        let root = temp_root("non_finite");
        let raw = root.join("raw.safetensors");
        let out = root.join(POINT_FORECAST_FILE);
        let bytes = f32_bytes(&[f32::NAN]);
        fs::write(
            &raw,
            serialize(
                [("pred", TensorView::new(Dtype::F32, vec![1, 1, 1, 1], &bytes).unwrap())],
                None,
            )
            .unwrap(),
        )
        .unwrap();
        let error = write_from_training_raw(&raw, &out).unwrap_err().to_string();
        assert!(error.contains("NaN or infinity"), "got: {error}");
        assert!(!out.exists());
        fs::remove_dir_all(root).unwrap();
    }
}
