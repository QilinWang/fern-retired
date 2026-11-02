//! Canonical native probability-forecast safetensors encodings.
//!
//! These are deliberately separate from the common dense point view.  A
//! quantile or sample model keeps its native answer, and the point artifact report
//! records the exact checked derivation from one of these source artifacts.

use std::collections::HashMap;

use safetensors::tensor::TensorView;
use safetensors::{Dtype, SafeTensors, serialize};

use super::{
    ArtifactRole, ContractError, DensePointForecastV1, ExactRatio, ForecastMeaningV1, QUANTILE_FORECAST_SCHEMA,
    ReportedForecastMeaningV1, SAMPLE_FORECAST_SCHEMA, SampleStatisticV1, TENSOR_LAYOUT_SCHEMA, TensorAxis,
    TensorByteOrder, TensorDtype, TensorLayout, TensorStorageOrder,
};

pub const QUANTILE_FORECAST_TENSOR_NAME: &str = "quantile_forecast";
pub const SAMPLE_FORECAST_TENSOR_NAME: &str = "sample_forecast";

const SCHEMA_METADATA: &str = "schema";
const AXIS_ORDER_METADATA: &str = "axis_order";
const REPORTED_MEANING_METADATA: &str = "reported_forecast_meaning";
const QUANTILE_AXES: [&str; 5] = ["case", "forecast_origin", "horizon", "channel", "quantile"];
// Fern emits `[forecast_origin, sample, horizon, channel]`.  The durable
// layout adds the case axis without silently reinterpreting the native bytes.
const SAMPLE_AXES: [&str; 5] = ["case", "forecast_origin", "sample", "horizon", "channel"];

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct QuantileForecastV1 {
    pub layout: TensorLayout,
    pub reported_meaning: ReportedForecastMeaningV1,
    pub canonical_f32_le_bytes: Vec<u8>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SampleForecastV1 {
    pub layout: TensorLayout,
    pub reported_meaning: ReportedForecastMeaningV1,
    pub canonical_f32_le_bytes: Vec<u8>,
}

/// Canonical point values derived by Fern from one native forecast.
///
/// The values are row-major `[case,forecast_origin,horizon,channel]`.  This is
/// not yet proof that a producer's reported point agrees.  Only
/// [`check_reported_point_against_derived_values_v1`] can earn that stronger
/// relation.
#[derive(Debug, Clone, PartialEq)]
pub struct DerivedPointValuesV1 {
    shape: [usize; 4],
    values: Vec<f32>,
}

impl DerivedPointValuesV1 {
    pub const fn shape(&self) -> [usize; 4] {
        self.shape
    }

    pub fn values(&self) -> &[f32] {
        &self.values
    }
}

/// A reported dense point artifact whose values Fern independently derived
/// from its declared native probability source and compared exactly.
///
/// Fields stay private so callers cannot obtain the `Checked` stage by
/// relabelling a decoded worker file.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CheckedPointForecastV1 {
    reported: DensePointForecastV1,
}

impl CheckedPointForecastV1 {
    pub fn reported(&self) -> &DensePointForecastV1 {
        &self.reported
    }
}

pub fn encode_quantile_forecast_f32(
    shape: [usize; 5],
    values: &[f32],
    reported_meaning: &ReportedForecastMeaningV1,
) -> Result<Vec<u8>, ContractError> {
    encode_native_forecast(
        QUANTILE_FORECAST_SCHEMA,
        QUANTILE_FORECAST_TENSOR_NAME,
        ArtifactRole::QuantileForecast,
        QUANTILE_AXES,
        shape,
        values,
        reported_meaning,
    )
}

pub fn decode_quantile_forecast(bytes: &[u8]) -> Result<QuantileForecastV1, ContractError> {
    let decoded = decode_native_forecast(
        bytes,
        QUANTILE_FORECAST_SCHEMA,
        QUANTILE_FORECAST_TENSOR_NAME,
        ArtifactRole::QuantileForecast,
        QUANTILE_AXES,
    )?;
    Ok(QuantileForecastV1 {
        layout: decoded.layout,
        reported_meaning: decoded.reported_meaning,
        canonical_f32_le_bytes: decoded.canonical_f32_le_bytes,
    })
}

pub fn encode_sample_forecast_f32(
    shape: [usize; 5],
    values: &[f32],
    reported_meaning: &ReportedForecastMeaningV1,
) -> Result<Vec<u8>, ContractError> {
    encode_native_forecast(
        SAMPLE_FORECAST_SCHEMA,
        SAMPLE_FORECAST_TENSOR_NAME,
        ArtifactRole::PredictiveSamples,
        SAMPLE_AXES,
        shape,
        values,
        reported_meaning,
    )
}

/// Encode one Fern-native sample tensor without an axis permutation.
///
/// Fern produces `[forecast_origin, sample, horizon, channel]`; the durable
/// artifact adds only the leading `case=1` axis.  Keeping this adapter beside
/// the canonical encoder prevents callers from flattening native bytes and
/// merely relabelling them as a different layout.
pub fn encode_single_case_fern_samples_f32(
    native_shape: [usize; 4],
    values: &[f32],
    reported_meaning: &ReportedForecastMeaningV1,
) -> Result<Vec<u8>, ContractError> {
    let [forecast_origins, samples, horizon, channels] = native_shape;
    encode_sample_forecast_f32(
        [1, forecast_origins, samples, horizon, channels],
        values,
        reported_meaning,
    )
}

pub fn decode_sample_forecast(bytes: &[u8]) -> Result<SampleForecastV1, ContractError> {
    let decoded = decode_native_forecast(
        bytes,
        SAMPLE_FORECAST_SCHEMA,
        SAMPLE_FORECAST_TENSOR_NAME,
        ArtifactRole::PredictiveSamples,
        SAMPLE_AXES,
    )?;
    Ok(SampleForecastV1 {
        layout: decoded.layout,
        reported_meaning: decoded.reported_meaning,
        canonical_f32_le_bytes: decoded.canonical_f32_le_bytes,
    })
}

/// Apply the V1 predictive-sample point functional.
///
/// Mean uses ordered f32 accumulation through [`predictive_sample_mean_v1`].
/// `LowerMedian` selects order statistic `(S-1)/2`, matching PyTorch's
/// dimensional median for even sample counts. Both rules are part of the
/// versioned artifact operation; changing either requires a new derivation
/// version.
pub fn derive_point_from_predictive_samples_v1(
    forecast: &SampleForecastV1,
    statistic: SampleStatisticV1,
) -> Result<DerivedPointValuesV1, ContractError> {
    let [cases, origins, samples, horizon, channels] = five_axis_lengths(&forecast.layout, "predictive_samples")?;
    let ForecastMeaningV1::PredictiveSamples { sample_count, .. } = forecast.reported_meaning.meaning else {
        return Err(ContractError::new(
            "predictive_samples.reported_meaning",
            "must declare predictive-sample meaning",
        ));
    };
    if samples as u64 != sample_count {
        return Err(ContractError::new(
            "predictive_samples.sample_count",
            "layout and reported_meaning disagree",
        ));
    }
    let source = decode_canonical_f32(&forecast.canonical_f32_le_bytes, "predictive_samples")?;
    let mut point = Vec::with_capacity(cases * origins * horizon * channels);
    let mut scratch = Vec::with_capacity(samples);
    for case in 0..cases {
        for origin in 0..origins {
            for step in 0..horizon {
                for channel in 0..channels {
                    scratch.clear();
                    for sample in 0..samples {
                        let index =
                            ((((case * origins + origin) * samples + sample) * horizon + step) * channels) + channel;
                        scratch.push(source[index]);
                    }
                    let value = match statistic {
                        SampleStatisticV1::Mean => predictive_sample_mean_v1(&scratch)?,
                        SampleStatisticV1::LowerMedian => {
                            scratch.sort_by(f32::total_cmp);
                            scratch[(samples - 1) / 2]
                        }
                    };
                    point.push(value);
                }
            }
        }
    }
    Ok(DerivedPointValuesV1 {
        shape: [cases, origins, horizon, channels],
        values: point,
    })
}

/// Select an exact retained quantile as the V1 point value.
pub fn select_exact_quantile_point_v1(
    forecast: &QuantileForecastV1,
    quantile: ExactRatio,
) -> Result<DerivedPointValuesV1, ContractError> {
    let [cases, origins, horizon, channels, quantiles] = five_axis_lengths(&forecast.layout, "quantile_forecast")?;
    let ForecastMeaningV1::Quantiles { ref levels } = forecast.reported_meaning.meaning else {
        return Err(ContractError::new(
            "quantile_forecast.reported_meaning",
            "must declare quantile meaning",
        ));
    };
    if quantiles != levels.len() {
        return Err(ContractError::new(
            "quantile_forecast.levels",
            "layout and reported_meaning disagree",
        ));
    }
    let quantile_index = levels.iter().position(|level| *level == quantile).ok_or_else(|| {
        ContractError::new(
            "quantile_forecast.quantile",
            format!(
                "requested exact level {}/{} is not retained",
                quantile.numerator, quantile.denominator
            ),
        )
    })?;
    let source = decode_canonical_f32(&forecast.canonical_f32_le_bytes, "quantile_forecast")?;
    let mut point = Vec::with_capacity(cases * origins * horizon * channels);
    for case in 0..cases {
        for origin in 0..origins {
            for step in 0..horizon {
                for channel in 0..channels {
                    let index = ((((case * origins + origin) * horizon + step) * channels + channel) * quantiles)
                        + quantile_index;
                    point.push(source[index]);
                }
            }
        }
    }
    Ok(DerivedPointValuesV1 {
        shape: [cases, origins, horizon, channels],
        values: point,
    })
}

/// V1's one canonical sample mean: finite f32 values accumulated in sample
/// order as f32, then divided once by the exact sample count.
///
/// The legacy trained-point adapter already used this arithmetic.  Naming it
/// here lets the typed producer and the independent checker share one versioned
/// rule instead of silently using f32 in one place and f64 in another.
pub fn predictive_sample_mean_v1(values: &[f32]) -> Result<f32, ContractError> {
    if values.is_empty() {
        return Err(ContractError::new(
            "predictive_sample_mean.values",
            "requires at least one sample",
        ));
    }
    let mut sum = 0.0_f32;
    for (index, value) in values.iter().copied().enumerate() {
        if !value.is_finite() {
            return Err(ContractError::new(
                "predictive_sample_mean.values",
                format!("sample {index} must be finite"),
            ));
        }
        sum += value;
    }
    Ok(sum / values.len() as f32)
}

/// Compare a producer-reported point artifact with Fern's independently
/// derived values.  V1 is exact because both sides use the same versioned f32
/// arithmetic over the same retained source bytes.
pub fn check_reported_point_against_derived_values_v1(
    reported: &DensePointForecastV1,
    derived: &DerivedPointValuesV1,
) -> Result<CheckedPointForecastV1, ContractError> {
    let reported_shape = four_axis_lengths(&reported.layout, "reported_point")?;
    if reported_shape != derived.shape {
        return Err(ContractError::new(
            "reported_point.shape",
            format!(
                "reported shape {reported_shape:?} differs from independently derived shape {:?}",
                derived.shape
            ),
        ));
    }
    let derived_bytes = derived
        .values
        .iter()
        .flat_map(|value| value.to_le_bytes())
        .collect::<Vec<_>>();
    if reported.canonical_f32_le_bytes != derived_bytes {
        let reported_values = decode_canonical_f32(&reported.canonical_f32_le_bytes, "reported_point")?;
        let first_difference = reported_values
            .iter()
            .zip(&derived.values)
            .position(|(reported, expected)| reported.to_bits() != expected.to_bits())
            .expect("unequal canonical byte vectors contain one unequal f32");
        return Err(ContractError::new(
            "reported_point.values",
            format!(
                "first mismatch at canonical value {first_difference}: worker reported {:?}, Fern derived {:?}",
                reported_values[first_difference], derived.values[first_difference]
            ),
        ));
    }
    Ok(CheckedPointForecastV1 {
        reported: reported.clone(),
    })
}

fn four_axis_lengths(layout: &TensorLayout, field: &str) -> Result<[usize; 4], ContractError> {
    let lengths = layout
        .axes
        .iter()
        .map(|axis| {
            usize::try_from(axis.length)
                .map_err(|_| ContractError::new(format!("{field}.shape"), "axis length does not fit usize"))
        })
        .collect::<Result<Vec<_>, _>>()?;
    lengths
        .try_into()
        .map_err(|_| ContractError::new(format!("{field}.shape"), "requires exactly four canonical axes"))
}

fn five_axis_lengths(layout: &TensorLayout, field: &str) -> Result<[usize; 5], ContractError> {
    let lengths = layout
        .axes
        .iter()
        .map(|axis| {
            usize::try_from(axis.length)
                .map_err(|_| ContractError::new(format!("{field}.shape"), "axis length does not fit usize"))
        })
        .collect::<Result<Vec<_>, _>>()?;
    lengths
        .try_into()
        .map_err(|_| ContractError::new(format!("{field}.shape"), "requires exactly five canonical axes"))
}

fn decode_canonical_f32(bytes: &[u8], field: &str) -> Result<Vec<f32>, ContractError> {
    if !bytes.len().is_multiple_of(4) {
        return Err(ContractError::new(
            format!("{field}.payload"),
            "canonical payload is not a whole number of f32 values",
        ));
    }
    Ok(bytes
        .chunks_exact(4)
        .map(|chunk| f32::from_le_bytes([chunk[0], chunk[1], chunk[2], chunk[3]]))
        .collect())
}

struct DecodedNativeForecast {
    layout: TensorLayout,
    reported_meaning: ReportedForecastMeaningV1,
    canonical_f32_le_bytes: Vec<u8>,
}

#[allow(clippy::too_many_arguments)]
fn encode_native_forecast<const N: usize>(
    schema: &str,
    tensor_name: &str,
    role: ArtifactRole,
    axes: [&str; N],
    shape: [usize; N],
    values: &[f32],
    reported_meaning: &ReportedForecastMeaningV1,
) -> Result<Vec<u8>, ContractError> {
    let field = role.as_str();
    if shape.contains(&0) {
        return Err(ContractError::new(
            format!("{field}.shape"),
            "all canonical axes must be positive",
        ));
    }
    let expected = shape.iter().try_fold(1_usize, |count, axis| {
        count
            .checked_mul(*axis)
            .ok_or_else(|| ContractError::new(format!("{field}.shape"), "shape product overflows usize"))
    })?;
    if values.len() != expected {
        return Err(ContractError::new(
            format!("{field}.values"),
            format!("found {} values, expected {expected} for {shape:?}", values.len()),
        ));
    }
    require_finite(values, field)?;
    let layout = layout_from_shape(axes, shape, field)?;
    reported_meaning.validate_for(role, &layout)?;
    let payload = values.iter().flat_map(|value| value.to_le_bytes()).collect::<Vec<_>>();
    let view = TensorView::new(Dtype::F32, shape.to_vec(), &payload)
        .map_err(|error| ContractError::new(format!("{field}.tensor"), error.to_string()))?;
    let tensors = HashMap::from([(tensor_name.to_string(), view)]);
    let metadata = Some(HashMap::from([
        (SCHEMA_METADATA.to_string(), schema.to_string()),
        (
            AXIS_ORDER_METADATA.to_string(),
            serde_json::to_string(axes.as_slice())
                .map_err(|error| ContractError::new(format!("{field}.axis_order"), error.to_string()))?,
        ),
        (
            REPORTED_MEANING_METADATA.to_string(),
            serde_json::to_string(reported_meaning)
                .map_err(|error| ContractError::new(format!("{field}.reported_forecast_meaning"), error.to_string()))?,
        ),
    ]));
    serialize(tensors, metadata).map_err(|error| ContractError::new(format!("{field}.safetensors"), error.to_string()))
}

fn decode_native_forecast<const N: usize>(
    bytes: &[u8],
    schema: &str,
    tensor_name: &str,
    role: ArtifactRole,
    axes: [&str; N],
) -> Result<DecodedNativeForecast, ContractError> {
    let field = role.as_str();
    let (_, header) = SafeTensors::read_metadata(bytes)
        .map_err(|error| ContractError::new(format!("{field}.safetensors"), error.to_string()))?;
    let metadata = header.metadata().clone().unwrap_or_default();
    if metadata.get(SCHEMA_METADATA).map(String::as_str) != Some(schema) {
        return Err(ContractError::new(
            format!("{field}.schema"),
            format!("must declare {schema}"),
        ));
    }
    let expected_axes = serde_json::to_string(axes.as_slice())
        .map_err(|error| ContractError::new(format!("{field}.axis_order"), error.to_string()))?;
    if metadata.get(AXIS_ORDER_METADATA) != Some(&expected_axes) {
        return Err(ContractError::new(
            format!("{field}.axis_order"),
            format!("must declare canonical axes {expected_axes}"),
        ));
    }
    let reported_meaning_text = metadata.get(REPORTED_MEANING_METADATA).ok_or_else(|| {
        ContractError::new(
            format!("{field}.reported_forecast_meaning"),
            "missing typed forecast reported_meaning metadata",
        )
    })?;
    let reported_meaning = serde_json::from_str::<ReportedForecastMeaningV1>(reported_meaning_text)
        .map_err(|error| ContractError::new(format!("{field}.reported_forecast_meaning"), error.to_string()))?;
    let tensors = SafeTensors::deserialize(bytes)
        .map_err(|error| ContractError::new(format!("{field}.safetensors"), error.to_string()))?;
    let tensor_names = tensors.names();
    if tensor_names.len() != 1 || tensor_names[0] != tensor_name {
        return Err(ContractError::new(
            format!("{field}.tensors"),
            format!("must contain only {tensor_name:?}; found {tensor_names:?}"),
        ));
    }
    let tensor = tensors
        .tensor(tensor_name)
        .map_err(|error| ContractError::new(format!("{field}.tensor"), error.to_string()))?;
    if tensor.dtype() != Dtype::F32 {
        return Err(ContractError::new(
            format!("{field}.dtype"),
            format!("must be F32, found {:?}", tensor.dtype()),
        ));
    }
    let shape: [usize; N] = tensor.shape().try_into().map_err(|_| {
        ContractError::new(
            format!("{field}.shape"),
            format!("requires {N} axes, found {:?}", tensor.shape()),
        )
    })?;
    if shape.contains(&0) {
        return Err(ContractError::new(
            format!("{field}.shape"),
            "all canonical axes must be positive",
        ));
    }
    let layout = layout_from_shape(axes, shape, field)?;
    reported_meaning.validate_for(role, &layout)?;
    let values = tensor
        .data()
        .chunks_exact(4)
        .map(|chunk| f32::from_le_bytes(chunk.try_into().expect("four-byte chunk")))
        .collect::<Vec<_>>();
    require_finite(&values, field)?;
    Ok(DecodedNativeForecast {
        layout,
        reported_meaning,
        canonical_f32_le_bytes: tensor.data().to_vec(),
    })
}

fn layout_from_shape<const N: usize>(
    axes: [&str; N],
    shape: [usize; N],
    field: &str,
) -> Result<TensorLayout, ContractError> {
    let axes = axes
        .iter()
        .zip(shape)
        .map(|(name, length)| {
            u64::try_from(length)
                .map_err(|_| ContractError::new(format!("{field}.shape"), "axis length does not fit u64"))
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

fn require_finite(values: &[f32], field: &str) -> Result<(), ContractError> {
    if let Some((index, _)) = values.iter().enumerate().find(|(_, value)| !value.is_finite()) {
        return Err(ContractError::new(
            format!("{field}.values"),
            format!("value {index} must be finite"),
        ));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        ArtifactKey, ArtifactReference, ExactRatio, ForecastMeaningV1, ForecastValueSpace, PointKind,
        REPORTED_FORECAST_MEANING_SCHEMA, ReportedPointDerivationV1, ScientificCaseId, ScientificCaseReference,
        ScientificContentDigest,
    };

    fn base_reported_meaning(meaning: ForecastMeaningV1) -> ReportedForecastMeaningV1 {
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
            meaning,
        }
    }

    #[test]
    fn quantile_round_trip_pins_levels_and_quantile_last_axis() {
        let reported_meaning = base_reported_meaning(ForecastMeaningV1::Quantiles {
            levels: vec![
                ExactRatio::new(1, 10).unwrap(),
                ExactRatio::new(1, 2).unwrap(),
                ExactRatio::new(9, 10).unwrap(),
            ],
        });
        let values = (0..12).map(|value| value as f32).collect::<Vec<_>>();
        let bytes = encode_quantile_forecast_f32([1, 1, 2, 2, 3], &values, &reported_meaning).unwrap();
        let decoded = decode_quantile_forecast(&bytes).unwrap();
        assert_eq!(decoded.canonical_f32_le_bytes.len(), values.len() * 4);
        assert_eq!(decoded.layout.axes[4].name, "quantile");

        let wrong_shape = encode_quantile_forecast_f32([1, 1, 2, 3, 2], &values, &reported_meaning).unwrap_err();
        assert_eq!(wrong_shape.field, "reported_forecast_meaning.channel_order");
    }

    #[test]
    fn native_forecast_reader_refuses_a_second_tensor_in_the_same_container() {
        let reported_meaning = base_reported_meaning(ForecastMeaningV1::Quantiles {
            levels: vec![ExactRatio::new(1, 2).unwrap()],
        });
        let values = [1.0_f32, 2.0];
        let canonical = values.iter().flat_map(|value| value.to_le_bytes()).collect::<Vec<_>>();
        let quantiles = TensorView::new(Dtype::F32, vec![1, 1, 1, 2, 1], &canonical).unwrap();
        let truth = TensorView::new(Dtype::F32, vec![1, 1, 1, 2, 1], &canonical).unwrap();
        let metadata = HashMap::from([
            (SCHEMA_METADATA.to_string(), QUANTILE_FORECAST_SCHEMA.to_string()),
            (
                AXIS_ORDER_METADATA.to_string(),
                serde_json::to_string(&QUANTILE_AXES).unwrap(),
            ),
            (
                REPORTED_MEANING_METADATA.to_string(),
                serde_json::to_string(&reported_meaning).unwrap(),
            ),
        ]);
        let mixed = serialize(
            HashMap::from([
                (QUANTILE_FORECAST_TENSOR_NAME.to_string(), quantiles),
                ("truth".to_string(), truth),
            ]),
            Some(metadata),
        )
        .unwrap();

        let error = decode_quantile_forecast(&mixed).unwrap_err().to_string();
        assert!(error.contains("must contain only"), "got: {error}");
        assert!(error.contains("truth"), "got: {error}");
    }

    #[test]
    fn sample_round_trip_pins_fern_native_origin_then_sample_order() {
        let reported_meaning = base_reported_meaning(ForecastMeaningV1::PredictiveSamples {
            sample_count: 3,
            sampling_contract_version: 1,
        });
        let values = (0..12).map(|value| value as f32).collect::<Vec<_>>();
        let bytes = encode_single_case_fern_samples_f32([2, 3, 1, 2], &values, &reported_meaning).unwrap();
        let decoded = decode_sample_forecast(&bytes).unwrap();
        assert_eq!(decoded.layout.axes[1].name, "forecast_origin");
        assert_eq!(decoded.layout.axes[2].name, "sample");
        assert_eq!(decoded.layout.axes[1].length, 2);
        assert_eq!(decoded.layout.axes[2].length, 3);
        assert_eq!(
            decoded.canonical_f32_le_bytes,
            values.iter().flat_map(|value| value.to_le_bytes()).collect::<Vec<_>>()
        );
        assert!(encode_single_case_fern_samples_f32([2, 3, 1, 2], &[f32::INFINITY; 12], &reported_meaning).is_err());
        assert!(decode_quantile_forecast(&bytes).is_err());
    }

    #[test]
    fn sample_point_derivation_keeps_each_origins_samples_together() {
        let reported_meaning = base_reported_meaning(ForecastMeaningV1::PredictiveSamples {
            sample_count: 2,
            sampling_contract_version: 1,
        });
        // Each origin owns two sample rows, each with two channels.
        let bytes = encode_single_case_fern_samples_f32(
            [2, 2, 1, 2],
            &[1.0, 10.0, 3.0, 14.0, 10.0, 100.0, 14.0, 140.0],
            &reported_meaning,
        )
        .unwrap();
        let decoded = decode_sample_forecast(&bytes).unwrap();
        let mean = derive_point_from_predictive_samples_v1(&decoded, SampleStatisticV1::Mean).unwrap();
        let lower = derive_point_from_predictive_samples_v1(&decoded, SampleStatisticV1::LowerMedian).unwrap();
        assert_eq!(mean.shape, [1, 2, 1, 2]);
        assert_eq!(mean.values, [2.0, 12.0, 12.0, 120.0]);
        assert_eq!(lower.values, [1.0, 10.0, 10.0, 100.0]);
    }

    #[test]
    fn exact_quantile_selection_uses_the_declared_level_per_origin() {
        let reported_meaning = base_reported_meaning(ForecastMeaningV1::Quantiles {
            levels: vec![
                ExactRatio::new(1, 10).unwrap(),
                ExactRatio::new(1, 2).unwrap(),
                ExactRatio::new(9, 10).unwrap(),
            ],
        });
        let bytes = encode_quantile_forecast_f32(
            [1, 2, 1, 2, 3],
            &[1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 10.0, 20.0, 30.0, 40.0, 50.0, 60.0],
            &reported_meaning,
        )
        .unwrap();
        let decoded = decode_quantile_forecast(&bytes).unwrap();
        let point = select_exact_quantile_point_v1(&decoded, ExactRatio::new(1, 2).unwrap()).unwrap();
        assert_eq!(point.shape, [1, 2, 1, 2]);
        assert_eq!(point.values, [2.0, 5.0, 20.0, 50.0]);
        assert!(select_exact_quantile_point_v1(&decoded, ExactRatio::new(1, 4).unwrap()).is_err());
    }

    #[test]
    fn checked_point_is_earned_only_after_exact_independent_comparison() {
        let sample_reference = ArtifactReference {
            artifact_key: ArtifactKey::parse("artifact:samples").unwrap(),
            role: ArtifactRole::PredictiveSamples,
            content_digest: ScientificContentDigest::from_bytes(b"samples"),
        };
        let sample_reported_meaning = base_reported_meaning(ForecastMeaningV1::PredictiveSamples {
            sample_count: 3,
            sampling_contract_version: 1,
        });
        let sample_bytes = encode_single_case_fern_samples_f32(
            [1, 3, 1, 2],
            &[100_000_000.0, 2.0, 1.0, 4.0, -100_000_000.0, 6.0],
            &sample_reported_meaning,
        )
        .unwrap();
        let samples = decode_sample_forecast(&sample_bytes).unwrap();
        let derived = derive_point_from_predictive_samples_v1(&samples, SampleStatisticV1::Mean).unwrap();
        assert_eq!(derived.values(), &[0.0, 4.0], "V1 pins ordered f32 accumulation");

        let point_reported_meaning = base_reported_meaning(ForecastMeaningV1::DensePoint {
            point_kind: PointKind::ConditionalMean,
            derivation: ReportedPointDerivationV1::PredictiveSampleReduction {
                source: sample_reference,
                statistic: SampleStatisticV1::Mean,
                derivation_version: 1,
            },
        });
        let reported_bytes =
            crate::encode_dense_point_f32(derived.shape(), derived.values(), &point_reported_meaning).unwrap();
        let reported = crate::decode_dense_point(&reported_bytes).unwrap();
        let checked = check_reported_point_against_derived_values_v1(&reported, &derived).unwrap();
        assert_eq!(checked.reported(), &reported);

        let wrong_bytes = crate::encode_dense_point_f32(derived.shape(), &[1.0, 4.0], &point_reported_meaning).unwrap();
        let wrong = crate::decode_dense_point(&wrong_bytes).unwrap();
        let error = check_reported_point_against_derived_values_v1(&wrong, &derived).unwrap_err();
        assert_eq!(error.field, "reported_point.values");
    }
}
