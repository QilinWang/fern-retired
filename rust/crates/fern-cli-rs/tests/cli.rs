//! One linker target for the `fern-run` integration contracts.
//!
//! Each source file remains a separate module so its fixtures, imports, and
//! test names stay isolated. Keeping the entry point here prevents Cargo from
//! statically linking the full CLI dependency graph once per source file.

#[path = "cli/db_lifecycle_cli.rs"]
mod db_lifecycle_cli;
#[path = "cli/protocol_columns_are_selectable.rs"]
mod protocol_columns_are_selectable;
#[path = "cli/run_registry_maintenance_cli.rs"]
mod run_registry_maintenance_cli;
