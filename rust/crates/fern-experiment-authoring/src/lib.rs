//! Turn a V2 experiment draft into a checked manifest, and probe the
//! implementation it names.
//!
//! This is the AUTHORING half of the V2 experiment surface, and it is a
//! separate crate for one reason: the import boundary. Its two modules lived
//! in `fern-recipes`, which `fern-scheduler` and `fern-worker` depend on for
//! the compiled types they execute. That made every scheduler build compile
//! the resolver, the Python capability probe and `jsonschema` for code a
//! scheduler never calls, and it made an edit to the authoring path rebuild
//! the machines that run Plans.
//!
//! The line the split follows is what each side DOES. Authoring decides
//! whether a draft may become a Plan: it expands references, validates handler
//! parameters against the implementation's own generated schemas, computes
//! digests, and refuses. Execution runs a Plan that already exists and asks
//! none of those questions. `fern-recipes` keeps the compiled types both sides
//! name; only the authoring side depends on this.

mod csharp_resolution_warnings;
mod experiment_composition;
mod implementation_bundle;

pub use experiment_composition::{
    DEFAULT_EXPERIMENT_COMPONENT_HOME, fork_experiment_resolution_v2, resolve_ablation_draft_v2,
    resolve_experiment_draft_v2, verify_ablation_resolution_v2, verify_experiment_resolution_v2,
};
pub use implementation_bundle::{
    model_link_receipt_from_python_output, probe_implementation_bundle_v2, probe_model_link_v2,
    python_capability_probe_source, python_model_link_probe_source, python_model_link_request,
    reprobe_implementation_bundle_v2, reprobe_implementation_bundle_v2_from_python_outputs,
    verify_implementation_bundle_v2,
};

/// What went wrong while resolving an experiment or probing the implementation
/// it names.
///
/// A plain message, deliberately. This boundary refuses drafts for reasons a
/// person has to read and act on — a handler parameter the implementation does
/// not accept, an ablation whose arms differ where they were declared not to —
/// and every one of those is a sentence, not a variant a caller can branch on.
///
/// CHG 2026-08-21: moved here with the code, having moved to `fern-recipes`
/// from `fern-knowledge` earlier the same day. An error type names the boundary
/// that produced it, and this is now that boundary's own crate.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ExperimentCompositionError {
    message: String,
}

impl ExperimentCompositionError {
    pub(crate) fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
        }
    }
}

impl std::fmt::Display for ExperimentCompositionError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl std::error::Error for ExperimentCompositionError {}

impl From<std::io::Error> for ExperimentCompositionError {
    fn from(error: std::io::Error) -> Self {
        Self::new(error.to_string())
    }
}

impl From<serde_json::Error> for ExperimentCompositionError {
    fn from(error: serde_json::Error) -> Self {
        Self::new(error.to_string())
    }
}
