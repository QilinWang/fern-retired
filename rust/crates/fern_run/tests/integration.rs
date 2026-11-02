//! One linker target for the `fern_run` integration contracts.
//!
//! These modules still exercise separate contracts and retain their original
//! teaching comments. Only Cargo's expensive static-link boundary is shared.

#[path = "integration/error_messages.rs"]
mod error_messages;
#[path = "integration/run_lifecycle.rs"]
mod run_lifecycle;
