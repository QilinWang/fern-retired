//! Canonical run sequence artifact reader.
//!
//! A run cell stores sequence data as one safetensors file containing named
//! tensors. The shape contract is `[S,T,D]`: sample, timestep, feature.
//! `pred` and `truth` are required; eigenvalue summaries are optional and use
//! data-role names (`eigenvalue_max`, `eigenvalue_sum`, `eigenvalue_logdet`).

use std::collections::HashMap;
use std::error::Error;
use std::fs;
use std::path::{Path, PathBuf};

use ndarray::Array3;
use safetensors::tensor::TensorView;
use safetensors::{Dtype, SafeTensors};

pub const SEQUENCE_TENSORS_FILE: &str = "sequence_tensors.safetensors";
pub const SEQUENCE_TENSOR_SCHEMA: &str = "fern.sequence_tensors.v1";
pub const SEQUENCE_AXIS_ORDER: &str = "[S,T,D]";

pub const PRED_TENSOR: &str = "pred";
pub const TRUTH_TENSOR: &str = "truth";
pub const EIGENVALUE_MAX_TENSOR: &str = "eigenvalue_max";
pub const EIGENVALUE_SUM_TENSOR: &str = "eigenvalue_sum";
pub const EIGENVALUE_LOGDET_TENSOR: &str = "eigenvalue_logdet";
/// Fern closed-form calibration statistic saved beside the point forecast.
pub const PRED_MAHALANOBIS_SQ_TENSOR: &str = "pred_mahalanobis_sq";
/// Fern per-patch probability-integral-transform values.
pub const PRED_PIT_TENSOR: &str = "pred_pit";
/// Metadata key carrying the dimension of the joint Mahalanobis region.
pub const PRED_MAHALANOBIS_DOF_METADATA: &str = "pred_mahalanobis_dof";

pub const FEATURE_NAMES_METADATA: &str = "feature_names";
pub const SCHEMA_METADATA: &str = "schema";
pub const AXIS_ORDER_METADATA: &str = "axis_order";
pub const STRIDE_METADATA: &str = "stride";

type Fallible<T> = Result<T, Box<dyn Error>>;

#[derive(Debug, Clone)]
pub struct SequenceTensors {
    pub path: PathBuf,
    pub metadata: HashMap<String, String>,
    pub feature_names: Vec<String>,
    pub pred: Array3<f64>,
    pub truth: Array3<f64>,
    pub eigenvalue_max: Option<Array3<f64>>,
    pub eigenvalue_sum: Option<Array3<f64>>,
    pub eigenvalue_logdet: Option<Array3<f64>>,
}

impl SequenceTensors {
    pub fn load_dir(dir: impl AsRef<Path>) -> Fallible<Self> {
        Self::load_file(dir.as_ref().join(SEQUENCE_TENSORS_FILE))
    }

    pub fn load_file(path: impl AsRef<Path>) -> Fallible<Self> {
        let path = path.as_ref().to_path_buf();
        let bytes = fs::read(&path)?;
        let (_, header_metadata) = SafeTensors::read_metadata(&bytes)?;
        let metadata = header_metadata.metadata().clone().unwrap_or_default();
        let tensors = SafeTensors::deserialize(&bytes)?;

        let pred = read_required_array3(&tensors, PRED_TENSOR)?;
        let truth = read_required_array3(&tensors, TRUTH_TENSOR)?;
        if truth.shape()[0] != 1 {
            return Err(invalid_data(format!(
                "{TRUTH_TENSOR} expects S=1, got shape {:?}",
                truth.shape()
            )));
        }
        if pred.shape()[1..] != truth.shape()[1..] {
            return Err(invalid_data(format!(
                "pred/truth shape mismatch: pred={:?}, truth={:?}",
                pred.shape(),
                truth.shape()
            )));
        }

        let eigenvalue_max = read_optional_array3(&tensors, EIGENVALUE_MAX_TENSOR)?;
        let eigenvalue_sum = read_optional_array3(&tensors, EIGENVALUE_SUM_TENSOR)?;
        let eigenvalue_logdet = read_optional_array3(&tensors, EIGENVALUE_LOGDET_TENSOR)?;
        for (name, arr) in [
            (EIGENVALUE_MAX_TENSOR, eigenvalue_max.as_ref()),
            (EIGENVALUE_SUM_TENSOR, eigenvalue_sum.as_ref()),
            (EIGENVALUE_LOGDET_TENSOR, eigenvalue_logdet.as_ref()),
        ] {
            if let Some(arr) = arr {
                if arr.shape() != pred.shape() {
                    return Err(invalid_data(format!(
                        "{name} shape {:?} must match pred shape {:?}",
                        arr.shape(),
                        pred.shape()
                    )));
                }
            }
        }

        let feature_names = feature_names_from_metadata(&metadata, pred.shape()[2])?;
        Ok(Self {
            path,
            metadata,
            feature_names,
            pred,
            truth,
            eigenvalue_max,
            eigenvalue_sum,
            eigenvalue_logdet,
        })
    }

    pub fn feature_name(&self, feature_idx: usize) -> Fallible<&str> {
        self.feature_names.get(feature_idx).map(String::as_str).ok_or_else(|| {
            invalid_data(format!(
                "feature index {feature_idx} out of range; feature names: {:?}",
                self.feature_names
            ))
        })
    }

    pub fn residuals_from_mean_prediction(&self) -> Vec<Vec<f64>> {
        let shape = self.pred.shape();
        let (s_len, t_len, d_len) = (shape[0], shape[1], shape[2]);
        let mut residuals = vec![vec![0.0; d_len]; t_len];
        for t in 0..t_len {
            for d in 0..d_len {
                let mut sum = 0.0;
                for s in 0..s_len {
                    sum += self.pred[[s, t, d]];
                }
                residuals[t][d] = self.truth[[0, t, d]] - sum / s_len as f64;
            }
        }
        residuals
    }

    pub fn prediction_samples_by_t(&self, feature_idx: usize) -> Fallible<Vec<Vec<f64>>> {
        self.feature_name(feature_idx)?;
        let shape = self.pred.shape();
        let (s_len, t_len) = (shape[0], shape[1]);
        let mut by_t = vec![Vec::with_capacity(s_len); t_len];
        for t in 0..t_len {
            for s in 0..s_len {
                by_t[t].push(self.pred[[s, t, feature_idx]]);
            }
        }
        Ok(by_t)
    }

    pub fn truth_values(&self, feature_idx: usize) -> Fallible<Vec<f64>> {
        self.feature_name(feature_idx)?;
        let t_len = self.truth.shape()[1];
        Ok((0..t_len).map(|t| self.truth[[0, t, feature_idx]]).collect())
    }
}

fn read_required_array3(tensors: &SafeTensors<'_>, name: &str) -> Fallible<Array3<f64>> {
    tensor_view_to_array3(tensors.tensor(name)?, name)
}

fn read_optional_array3(tensors: &SafeTensors<'_>, name: &str) -> Fallible<Option<Array3<f64>>> {
    if tensors.names().contains(&name) {
        Ok(Some(tensor_view_to_array3(tensors.tensor(name)?, name)?))
    } else {
        Ok(None)
    }
}

fn tensor_view_to_array3(view: TensorView<'_>, name: &str) -> Fallible<Array3<f64>> {
    if view.dtype() != Dtype::F64 {
        return Err(invalid_data(format!("{name} must be F64; got {:?}", view.dtype())));
    }
    let shape = view.shape();
    if shape.len() != 3 {
        return Err(invalid_data(format!("{name} expects [S,T,D], got shape {shape:?}")));
    }
    let mut values = Vec::with_capacity(shape.iter().product());
    for chunk in view.data().chunks_exact(8) {
        values.push(f64::from_le_bytes(chunk.try_into().expect("F64 chunks are 8 bytes")));
    }
    Ok(Array3::from_shape_vec((shape[0], shape[1], shape[2]), values)?)
}

fn feature_names_from_metadata(metadata: &HashMap<String, String>, d_len: usize) -> Fallible<Vec<String>> {
    let names = if let Some(raw) = metadata.get(FEATURE_NAMES_METADATA) {
        serde_json::from_str::<Vec<String>>(raw)?
    } else {
        (0..d_len).map(|i| format!("feat_{i}")).collect()
    };
    if names.len() != d_len {
        return Err(invalid_data(format!(
            "{FEATURE_NAMES_METADATA} length {} does not match D={d_len}",
            names.len()
        )));
    }
    Ok(names)
}

fn invalid_data(message: impl Into<String>) -> Box<dyn Error> {
    Box::new(std::io::Error::new(std::io::ErrorKind::InvalidData, message.into()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use safetensors::{serialize, tensor::TensorView};
    use std::collections::HashMap;

    #[test]
    fn loads_sequence_tensor_group_and_computes_residuals() {
        let pred_values: Vec<f64> = vec![
            1.0, 2.0, 3.0, 4.0, //
            2.0, 4.0, 6.0, 8.0,
        ];
        let truth_values: Vec<f64> = vec![1.5, 2.0, 4.0, 5.0];
        let eigen_values: Vec<f64> = vec![
            0.1, 0.2, 0.3, 0.4, //
            0.5, 0.6, 0.7, 0.8,
        ];
        let pred_bytes = f64_bytes(&pred_values);
        let truth_bytes = f64_bytes(&truth_values);
        let eigen_bytes = f64_bytes(&eigen_values);
        let tensors = vec![
            (
                PRED_TENSOR,
                TensorView::new(Dtype::F64, vec![2, 2, 2], &pred_bytes).unwrap(),
            ),
            (
                TRUTH_TENSOR,
                TensorView::new(Dtype::F64, vec![1, 2, 2], &truth_bytes).unwrap(),
            ),
            (
                EIGENVALUE_MAX_TENSOR,
                TensorView::new(Dtype::F64, vec![2, 2, 2], &eigen_bytes).unwrap(),
            ),
        ];
        let mut metadata = HashMap::new();
        metadata.insert(FEATURE_NAMES_METADATA.to_string(), "[\"x\",\"y\"]".to_string());
        metadata.insert(SCHEMA_METADATA.to_string(), SEQUENCE_TENSOR_SCHEMA.to_string());
        metadata.insert(AXIS_ORDER_METADATA.to_string(), SEQUENCE_AXIS_ORDER.to_string());
        metadata.insert(STRIDE_METADATA.to_string(), "1".to_string());
        let bytes = serialize(tensors, Some(metadata)).unwrap();
        let path = std::env::temp_dir().join(format!(
            "fern_post_sequence_tensors_test_{}.safetensors",
            std::process::id()
        ));
        fs::write(&path, bytes).unwrap();

        let loaded = SequenceTensors::load_file(&path).unwrap();
        fs::remove_file(&path).unwrap();

        assert_eq!(loaded.feature_names, vec!["x", "y"]);
        assert_eq!(loaded.pred.shape(), &[2, 2, 2]);
        assert!(loaded.eigenvalue_max.is_some());
        assert!(loaded.eigenvalue_sum.is_none());
        assert_eq!(
            loaded.prediction_samples_by_t(1).unwrap(),
            vec![vec![2.0, 4.0], vec![4.0, 8.0]]
        );
        assert_eq!(loaded.truth_values(0).unwrap(), vec![1.5, 4.0]);
        assert_eq!(
            loaded.residuals_from_mean_prediction(),
            vec![vec![0.0, -1.0], vec![-0.5, -1.0]]
        );
    }

    fn f64_bytes(values: &[f64]) -> Vec<u8> {
        values.iter().flat_map(|v| v.to_le_bytes()).collect()
    }
}
