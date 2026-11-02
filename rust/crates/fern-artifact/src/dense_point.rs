//! Canonical dense point-forecast safetensors encoding.
//!
//! Model adapters decide how to obtain the declared point functional.  This
//! module owns the shared container schema, semantic metadata, finite-value
//! checks, and canonical `[case, forecast_origin, horizon, channel]` layout.

use std::collections::{BTreeMap, HashMap};

use safetensors::tensor::TensorView;
use safetensors::{Dtype, SafeTensors, serialize};

use super::{
    ContractError, DENSE_POINT_FORECAST_SCHEMA, ReportedForecastMeaningV1, TENSOR_LAYOUT_SCHEMA, TensorAxis,
    TensorByteOrder, TensorDtype, TensorLayout, TensorStorageOrder,
};

pub const DENSE_POINT_TENSOR_NAME: &str = "point_forecast";
const SCHEMA_METADATA: &str = "schema";
const AXIS_ORDER_METADATA: &str = "axis_order";
const REPORTED_MEANING_METADATA: &str = "reported_forecast_meaning";
const CANONICAL_AXES: [&str; 4] = ["case", "forecast_origin", "horizon", "channel"];

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DensePointForecastV1 {
    pub layout: TensorLayout,
    pub reported_meaning: ReportedForecastMeaningV1,
    pub canonical_f32_le_bytes: Vec<u8>,
}

impl DensePointForecastV1 {
    pub fn f32_values(&self) -> Vec<f32> {
        self.canonical_f32_le_bytes
            .chunks_exact(4)
            .map(|chunk| f32::from_le_bytes(chunk.try_into().expect("four-byte chunk")))
            .collect()
    }
}

pub fn encode_dense_point_f32(
    shape: [usize; 4],
    values: &[f32],
    reported_meaning: &ReportedForecastMeaningV1,
) -> Result<Vec<u8>, ContractError> {
    if shape.contains(&0) {
        return Err(ContractError::new(
            "dense_point.shape",
            "all four canonical axes must be positive",
        ));
    }
    let expected = shape.iter().try_fold(1_usize, |count, axis| {
        count
            .checked_mul(*axis)
            .ok_or_else(|| ContractError::new("dense_point.shape", "shape product overflows usize"))
    })?;
    if values.len() != expected {
        return Err(ContractError::new(
            "dense_point.values",
            format!("found {} values, expected {expected} for {shape:?}", values.len()),
        ));
    }
    for (index, value) in values.iter().enumerate() {
        if !value.is_finite() {
            return Err(ContractError::new(
                "dense_point.values",
                format!("value {index} must be finite"),
            ));
        }
    }
    let layout = layout_from_shape(shape)?;
    reported_meaning.validate_for(super::ArtifactRole::DensePointForecast, &layout)?;
    let payload = values.iter().flat_map(|value| value.to_le_bytes()).collect::<Vec<_>>();
    let view = TensorView::new(Dtype::F32, shape.to_vec(), &payload)
        .map_err(|error| ContractError::new("dense_point.tensor", error.to_string()))?;
    let tensors = HashMap::from([(DENSE_POINT_TENSOR_NAME.to_string(), view)]);
    let metadata = Some(HashMap::from([
        (SCHEMA_METADATA.to_string(), DENSE_POINT_FORECAST_SCHEMA.to_string()),
        (
            AXIS_ORDER_METADATA.to_string(),
            serde_json::to_string(&CANONICAL_AXES)
                .map_err(|error| ContractError::new("dense_point.axis_order", error.to_string()))?,
        ),
        (
            REPORTED_MEANING_METADATA.to_string(),
            serde_json::to_string(reported_meaning)
                .map_err(|error| ContractError::new("dense_point.reported_forecast_meaning", error.to_string()))?,
        ),
    ]));
    serialize(tensors, metadata).map_err(|error| ContractError::new("dense_point.safetensors", error.to_string()))
}

pub fn decode_dense_point(bytes: &[u8]) -> Result<DensePointForecastV1, ContractError> {
    let (_, header) = SafeTensors::read_metadata(bytes)
        .map_err(|error| ContractError::new("dense_point.safetensors", error.to_string()))?;
    let metadata = header.metadata().clone().unwrap_or_default();
    if metadata.get(SCHEMA_METADATA).map(String::as_str) != Some(DENSE_POINT_FORECAST_SCHEMA) {
        return Err(ContractError::new(
            "dense_point.schema",
            format!("must declare {DENSE_POINT_FORECAST_SCHEMA}"),
        ));
    }
    let expected_axes = serde_json::to_string(&CANONICAL_AXES)
        .map_err(|error| ContractError::new("dense_point.axis_order", error.to_string()))?;
    if metadata.get(AXIS_ORDER_METADATA) != Some(&expected_axes) {
        return Err(ContractError::new(
            "dense_point.axis_order",
            format!("must declare canonical axes {expected_axes}"),
        ));
    }
    let reported_meaning_text = metadata.get(REPORTED_MEANING_METADATA).ok_or_else(|| {
        ContractError::new(
            "dense_point.reported_forecast_meaning",
            "missing typed reported forecast meaning metadata",
        )
    })?;
    let reported_meaning = serde_json::from_str::<ReportedForecastMeaningV1>(reported_meaning_text)
        .map_err(|error| ContractError::new("dense_point.reported_forecast_meaning", error.to_string()))?;
    let tensors = SafeTensors::deserialize(bytes)
        .map_err(|error| ContractError::new("dense_point.safetensors", error.to_string()))?;
    let tensor_names = tensors.names();
    if tensor_names.len() != 1 || tensor_names[0] != DENSE_POINT_TENSOR_NAME {
        return Err(ContractError::new(
            "dense_point.tensors",
            format!("must contain only {DENSE_POINT_TENSOR_NAME:?}; found {tensor_names:?}"),
        ));
    }
    let tensor = tensors
        .tensor(DENSE_POINT_TENSOR_NAME)
        .map_err(|error| ContractError::new("dense_point.tensor", error.to_string()))?;
    if tensor.dtype() != Dtype::F32 {
        return Err(ContractError::new(
            "dense_point.dtype",
            format!("must be F32, found {:?}", tensor.dtype()),
        ));
    }
    let shape: [usize; 4] = tensor.shape().try_into().map_err(|_| {
        ContractError::new(
            "dense_point.shape",
            format!("requires four axes, found {:?}", tensor.shape()),
        )
    })?;
    if shape.contains(&0) {
        return Err(ContractError::new(
            "dense_point.shape",
            "all four canonical axes must be positive",
        ));
    }
    let layout = layout_from_shape(shape)?;
    reported_meaning.validate_for(super::ArtifactRole::DensePointForecast, &layout)?;
    for (index, chunk) in tensor.data().chunks_exact(4).enumerate() {
        let value = f32::from_le_bytes(chunk.try_into().expect("four-byte chunk"));
        if !value.is_finite() {
            return Err(ContractError::new(
                "dense_point.values",
                format!("value {index} must be finite"),
            ));
        }
    }
    Ok(DensePointForecastV1 {
        layout,
        reported_meaning,
        canonical_f32_le_bytes: tensor.data().to_vec(),
    })
}

fn layout_from_shape(shape: [usize; 4]) -> Result<TensorLayout, ContractError> {
    let axes = CANONICAL_AXES
        .iter()
        .zip(shape)
        .map(|(name, length)| {
            u64::try_from(length)
                .map_err(|_| ContractError::new("dense_point.shape", "axis length does not fit u64"))
                .and_then(|length| TensorAxis::try_new(*name, length))
        })
        .collect::<Result<Vec<_>, _>>()?;
    let layout = TensorLayout {
        schema: TENSOR_LAYOUT_SCHEMA.to_string(),
        dtype: TensorDtype::Float32,
        byte_order: TensorByteOrder::LittleEndian,
        storage_order: TensorStorageOrder::RowMajor,
        axes,
    };
    layout.validate()?;
    Ok(layout)
}

/// Return the metadata in deterministic key order for audit/debug output.
pub fn dense_point_metadata(bytes: &[u8]) -> Result<BTreeMap<String, String>, ContractError> {
    let (_, header) = SafeTensors::read_metadata(bytes)
        .map_err(|error| ContractError::new("dense_point.safetensors", error.to_string()))?;
    Ok(header.metadata().clone().unwrap_or_default().into_iter().collect())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        ArtifactKey, ArtifactReference, ArtifactRole, ForecastMeaningV1, ForecastValueSpace, ModelImplementationHash,
        PointKind, REPORTED_FORECAST_MEANING_SCHEMA, ReportedPointDerivationV1, SampleStatisticV1, ScientificCaseId,
        ScientificCaseReference, ScientificContentDigest,
    };

    fn reported_meaning() -> ReportedForecastMeaningV1 {
        ReportedForecastMeaningV1 {
            schema: REPORTED_FORECAST_MEANING_SCHEMA.to_string(),
            scientific_cases: vec![ScientificCaseReference {
                case_id: ScientificCaseId::parse("case-1").unwrap(),
                manifest_digest: ScientificContentDigest::from_bytes(b"case"),
            }],
            forecast_origin_plan: ArtifactReference {
                artifact_key: ArtifactKey::parse("artifact:origins").unwrap(),
                role: ArtifactRole::ForecastOriginPlan,
                content_digest: ScientificContentDigest::from_bytes(b"origins"),
            },
            channel_order: vec!["x".to_string(), "y".to_string()],
            value_space: ForecastValueSpace::Physical,
            scale_reference: None,
            meaning: ForecastMeaningV1::DensePoint {
                point_kind: PointKind::NativePointHead,
                derivation: ReportedPointDerivationV1::NativePointHead {
                    producer_implementation: ModelImplementationHash::parse(format!(
                        "implementation:sha256:{}",
                        "66".repeat(32)
                    ))
                    .unwrap(),
                },
            },
        }
    }

    #[test]
    fn dense_point_round_trip_checks_axes_reported_meaning_and_finite_values() {
        let values = [1.0, 2.0, 3.0, 4.0];
        let bytes = encode_dense_point_f32([1, 1, 2, 2], &values, &reported_meaning()).unwrap();
        let decoded = decode_dense_point(&bytes).unwrap();
        assert_eq!(decoded.f32_values(), values);
        assert_eq!(decoded.reported_meaning, reported_meaning());
        assert_eq!(
            dense_point_metadata(&bytes)
                .unwrap()
                .get(SCHEMA_METADATA)
                .map(String::as_str),
            Some(DENSE_POINT_FORECAST_SCHEMA)
        );
        assert!(encode_dense_point_f32([1, 1, 1, 1], &[f32::NAN], &reported_meaning()).is_err());

        let mut wrong_channels = reported_meaning();
        wrong_channels.channel_order.pop();
        assert!(encode_dense_point_f32([1, 1, 2, 2], &values, &wrong_channels).is_err());
    }

    #[test]
    fn dense_point_reader_refuses_a_second_tensor_in_the_same_container() {
        let values = [1.0_f32];
        let canonical = values.iter().flat_map(|value| value.to_le_bytes()).collect::<Vec<_>>();
        let point = TensorView::new(Dtype::F32, vec![1, 1, 1, 1], &canonical).unwrap();
        let truth = TensorView::new(Dtype::F32, vec![1, 1, 1, 1], &canonical).unwrap();
        let metadata = HashMap::from([
            (SCHEMA_METADATA.to_string(), DENSE_POINT_FORECAST_SCHEMA.to_string()),
            (
                AXIS_ORDER_METADATA.to_string(),
                serde_json::to_string(&CANONICAL_AXES).unwrap(),
            ),
            (
                REPORTED_MEANING_METADATA.to_string(),
                serde_json::to_string(&reported_meaning()).unwrap(),
            ),
        ]);
        let mixed = serialize(
            HashMap::from([
                (DENSE_POINT_TENSOR_NAME.to_string(), point),
                ("truth".to_string(), truth),
            ]),
            Some(metadata),
        )
        .unwrap();

        let error = decode_dense_point(&mixed).unwrap_err().to_string();
        assert!(error.contains("must contain only"), "got: {error}");
        assert!(error.contains("truth"), "got: {error}");
    }

    #[test]
    fn same_values_with_different_reported_meanings_have_different_content_identity() {
        let native = reported_meaning();
        let native_bytes = encode_dense_point_f32([1, 1, 1, 2], &[1.0, 2.0], &native).unwrap();
        let native_decoded = decode_dense_point(&native_bytes).unwrap();
        let native_reported_artifact = crate::ReportedArtifactV1::from_payloads(
            ArtifactKey::parse("artifact:native").unwrap(),
            ArtifactRole::DensePointForecast,
            "application/x-safetensors",
            &native_bytes,
            &native_decoded.canonical_f32_le_bytes,
            Some(native_decoded.layout),
            Some(native),
        )
        .unwrap();

        let mut median = reported_meaning();
        median.meaning = ForecastMeaningV1::DensePoint {
            point_kind: PointKind::ConditionalMedian,
            derivation: ReportedPointDerivationV1::ExactQuantileSelection {
                source: ArtifactReference {
                    artifact_key: ArtifactKey::parse("artifact:quantiles").unwrap(),
                    role: ArtifactRole::QuantileForecast,
                    content_digest: ScientificContentDigest::from_bytes(b"quantiles"),
                },
                quantile: crate::ExactRatio::new(1, 2).unwrap(),
                derivation_version: 1,
            },
        };
        let median_bytes = encode_dense_point_f32([1, 1, 1, 2], &[1.0, 2.0], &median).unwrap();
        let median_decoded = decode_dense_point(&median_bytes).unwrap();
        let median_reported_artifact = crate::ReportedArtifactV1::from_payloads(
            ArtifactKey::parse("artifact:median").unwrap(),
            ArtifactRole::DensePointForecast,
            "application/x-safetensors",
            &median_bytes,
            &median_decoded.canonical_f32_le_bytes,
            Some(median_decoded.layout),
            Some(median),
        )
        .unwrap();
        assert_ne!(
            native_reported_artifact.content_digest,
            median_reported_artifact.content_digest
        );
    }

    #[test]
    fn sample_mean_and_sample_median_have_distinct_reported_meanings() {
        let source = ArtifactReference {
            artifact_key: ArtifactKey::parse("artifact:samples").unwrap(),
            role: ArtifactRole::PredictiveSamples,
            content_digest: ScientificContentDigest::from_bytes(b"samples"),
        };
        let mut mean = reported_meaning();
        mean.meaning = ForecastMeaningV1::DensePoint {
            point_kind: PointKind::ConditionalMean,
            derivation: ReportedPointDerivationV1::PredictiveSampleReduction {
                source: source.clone(),
                statistic: SampleStatisticV1::Mean,
                derivation_version: 1,
            },
        };
        let mut median = mean.clone();
        median.meaning = ForecastMeaningV1::DensePoint {
            point_kind: PointKind::ConditionalMedian,
            derivation: ReportedPointDerivationV1::PredictiveSampleReduction {
                source,
                statistic: SampleStatisticV1::LowerMedian,
                derivation_version: 1,
            },
        };
        let values = [1.0, 2.0];
        let mean_bytes = encode_dense_point_f32([1, 1, 1, 2], &values, &mean).unwrap();
        let median_bytes = encode_dense_point_f32([1, 1, 1, 2], &values, &median).unwrap();
        let mean_decoded = decode_dense_point(&mean_bytes).unwrap();
        let median_decoded = decode_dense_point(&median_bytes).unwrap();
        let mean_reported_artifact = crate::ReportedArtifactV1::from_payloads(
            ArtifactKey::parse("artifact:mean").unwrap(),
            ArtifactRole::DensePointForecast,
            "application/x-safetensors",
            &mean_bytes,
            &mean_decoded.canonical_f32_le_bytes,
            Some(mean_decoded.layout),
            Some(mean),
        )
        .unwrap();
        let median_reported_artifact = crate::ReportedArtifactV1::from_payloads(
            ArtifactKey::parse("artifact:median-from-samples").unwrap(),
            ArtifactRole::DensePointForecast,
            "application/x-safetensors",
            &median_bytes,
            &median_decoded.canonical_f32_le_bytes,
            Some(median_decoded.layout),
            Some(median),
        )
        .unwrap();
        assert_ne!(
            mean_reported_artifact.content_digest,
            median_reported_artifact.content_digest
        );

        let mut inconsistent = reported_meaning();
        inconsistent.meaning = ForecastMeaningV1::DensePoint {
            point_kind: PointKind::ConditionalMedian,
            derivation: ReportedPointDerivationV1::PredictiveSampleReduction {
                source: ArtifactReference {
                    artifact_key: ArtifactKey::parse("artifact:samples-again").unwrap(),
                    role: ArtifactRole::PredictiveSamples,
                    content_digest: ScientificContentDigest::from_bytes(b"samples-again"),
                },
                statistic: SampleStatisticV1::Mean,
                derivation_version: 1,
            },
        };
        assert!(encode_dense_point_f32([1, 1, 1, 2], &values, &inconsistent).is_err());
    }
}
