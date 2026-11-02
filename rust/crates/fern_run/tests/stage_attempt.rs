//! Stage Attempt SQL authority is exercised by the C# Registry and Scheduler
//! owner targets. The exact source-test mapping and preserved Rust source live
//! under `retired/fern_run/`.
//!
//! This intentionally empty Rust target remains as a compile boundary while
//! callers finish leaving `fern_run`; it must not grow another local SQLite
//! fixture or test-only command surface.
