//! Stored frozen Plan JSON uses the same types as callers, with serde defaults
//! for fields added after those records were frozen.
//!
//! The 2026-08-26 census (reference:88981116) found 66 unreadable Plans:
//! 49 missing `training_target`, 17 missing `kind`. None failed on an unknown
//! field. Defaults live on the types: `TrainingTargetConfig::StateForecast`
//! and ActionConfig without a tag. Extra-field tolerance is a different
//! disease and is not this repair.

use serde::de::DeserializeOwned;

use crate::lifecycle::{LifecycleError, LifecycleResult};

pub fn parse_stored<T: DeserializeOwned>(json: &str) -> LifecycleResult<T> {
    serde_json::from_str(json)
        .map_err(|error| LifecycleError::InvalidRequest(format!("stored record is unreadable: {error}")))
}
