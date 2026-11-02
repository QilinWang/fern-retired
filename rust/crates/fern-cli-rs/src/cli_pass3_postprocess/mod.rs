//! Artifact representations shared by worker workflows.
//!
//! Scientific postprocessing is executed by the Python `fern.postprocess`
//! Stage. This module retains only the typed point-forecast representation
//! still consumed by the legacy worker path.

#[cfg(any(fern_role_matrix_worker, test))]
pub(crate) mod canonical_point_forecast;
