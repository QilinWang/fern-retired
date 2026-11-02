//! `fern_post` — DB-independent post-processing math for Fern.
//!
//! Statistical diagnostics on forecast residuals: the normality battery
//! (Shapiro–Wilk, Anderson–Darling, KS, Cramér–von Mises, Ljung–Box, Jarque–Bera,
//! Lilliefors), joint multivariate normality (Mardia, Henze–Zirkler), p-value
//! uniformity, and whitened-residual calibration.
//!
//! Extracted from `fern_run` so the CLI and other crates can use it WITHOUT pulling in
//! the DB stack (diesel/SQLite). Imports only ndarray/rayon/statrs/normality/serde.
//! Parity with scipy/statsmodels/pingouin is documented in PARITY.md.
//!
//! End-of-run writers (calibration, sequence scores, quantile diagnostics) live
//! here so the worker can score artifacts without compiling the campaign
//! statistics crate.

pub mod artifact_io;
pub mod diagnostics;
pub mod sequence_tensors;

/// Shared error type for public post-process writers.
pub type Fallible<T> = Result<T, Box<dyn std::error::Error + Send + Sync>>;

/// Report an input that cannot name a valid post-process operation.
pub fn invalid_input(message: impl Into<String>) -> Box<dyn std::error::Error + Send + Sync> {
    Box::new(std::io::Error::new(std::io::ErrorKind::InvalidInput, message.into()))
}
