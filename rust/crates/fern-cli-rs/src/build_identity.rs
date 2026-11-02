//! Compile-time identity printed by `fern-run version`.
//!
//! This module says what the executable IS. It never locates runtime inputs and
//! never enters scientific run identity. `build.rs` supplies the Git/archive
//! facts, while this public command gives deploy and preflight one typed reader.

use fern_artifact::ByteDigest;
use fern_json_contract::JsonContract;
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};

/// Aggregate contract for the run-plan parent and its private child commands.
/// A wire-shape change must deliberately advance this label.
pub(crate) const RUN_PLAN_PROTOCOL: &str = "fern.run_plan_parent_child.v2";
pub(crate) const INSTALLATION_IDENTITY_SCHEMA: &str = "fern.installation_identity.v4";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub(crate) enum InstalledBinaryRole {
    Scheduler,
    MatrixWorker,
    RegistryHome,
    WorkerSite,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub(crate) struct InstallationIdentityV4 {
    pub(crate) schema: String,
    pub(crate) role: InstalledBinaryRole,
    pub(crate) run_plan_protocol: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub(crate) recipe_handler_capabilities: Option<fern_recipes::RecipeHandlerCapabilitiesV1>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub(crate) recipe_handler_capabilities_sha256: Option<String>,
}

pub(crate) fn installation_identity(role: InstalledBinaryRole) -> InstallationIdentityV4 {
    let (recipe_handler_capabilities, recipe_handler_capabilities_sha256) = match role {
        InstalledBinaryRole::Scheduler | InstalledBinaryRole::RegistryHome | InstalledBinaryRole::WorkerSite => {
            (None, None)
        }
        InstalledBinaryRole::MatrixWorker => {
            let capabilities = fern_recipes::recipe_handler_capabilities();
            let digest = recipe_handler_capabilities_sha256(&capabilities);
            (Some(capabilities), Some(digest))
        }
    };
    InstallationIdentityV4 {
        schema: INSTALLATION_IDENTITY_SCHEMA.to_string(),
        role,
        run_plan_protocol: RUN_PLAN_PROTOCOL.to_string(),
        recipe_handler_capabilities,
        recipe_handler_capabilities_sha256,
    }
}

/// Fingerprint the worker's structured handler registry.
///
/// This deliberately does not hash a source file: declarations may move to
/// data-only Recipe Extensions without changing executable capability, while
/// a new handler enum or a changed family/plan mapping changes this record.
pub(crate) fn recipe_handler_capabilities_sha256(capabilities: &fern_recipes::RecipeHandlerCapabilitiesV1) -> String {
    let bytes = serde_json::to_vec(capabilities).expect("typed recipe capabilities serialize");
    ByteDigest::from_bytes(&bytes)
        .as_str()
        .strip_prefix("sha256:")
        .expect("Fern byte digests use the sha256 prefix")
        .to_string()
}

pub(crate) fn parse_installation_identity(output: &[u8]) -> Result<InstallationIdentityV4, String> {
    let identity: InstallationIdentityV4 =
        serde_json::from_slice(output).map_err(|error| format!("installation identity JSON is invalid: {error}"))?;
    if identity.schema != INSTALLATION_IDENTITY_SCHEMA {
        return Err(format!(
            "installation identity schema is {:?}, expected {:?}",
            identity.schema, INSTALLATION_IDENTITY_SCHEMA
        ));
    }
    if identity.role != InstalledBinaryRole::MatrixWorker {
        if identity.recipe_handler_capabilities.is_some() || identity.recipe_handler_capabilities_sha256.is_some() {
            return Err("non-worker installation identity must not carry recipe handler capabilities".to_string());
        }
        return Ok(identity);
    }
    let capabilities = identity
        .recipe_handler_capabilities
        .as_ref()
        .ok_or_else(|| "matrix-worker installation identity is missing recipe handler capabilities".to_string())?;
    let digest = identity.recipe_handler_capabilities_sha256.as_ref().ok_or_else(|| {
        "matrix-worker installation identity is missing its recipe handler capability digest".to_string()
    })?;
    if capabilities.schema != fern_recipes::RECIPE_HANDLER_CAPABILITIES_SCHEMA {
        return Err(format!(
            "recipe handler capability schema is {:?}, expected {:?}",
            capabilities.schema,
            fern_recipes::RECIPE_HANDLER_CAPABILITIES_SCHEMA
        ));
    }
    let expected_digest = recipe_handler_capabilities_sha256(capabilities);
    if digest != &expected_digest {
        return Err(format!(
            "recipe handler capability digest is {:?}, expected {expected_digest:?}",
            digest
        ));
    }
    Ok(identity)
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
pub(crate) struct BinaryVersionReport {
    pub(crate) schema: String,
    pub(crate) crate_version: String,
    pub(crate) built_from_commit: String,
    pub(crate) built_from_dirty_tree: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub(crate) source_archive_sha256: Option<String>,
    pub(crate) run_plan_protocol: String,
}

impl JsonContract for BinaryVersionReport {
    const SCHEMA_ID: &'static str = "fern.binary_version.v1";
}

pub(crate) fn report() -> BinaryVersionReport {
    BinaryVersionReport {
        schema: BinaryVersionReport::SCHEMA_ID.to_string(),
        crate_version: env!("CARGO_PKG_VERSION").to_string(),
        built_from_commit: env!("FERN_BUILT_FROM_COMMIT").to_string(),
        built_from_dirty_tree: env!("FERN_BUILT_FROM_DIRTY_TREE") == "true",
        source_archive_sha256: option_env!("FERN_BUILT_FROM_SOURCE_ARCHIVE_SHA256").map(str::to_string),
        run_plan_protocol: RUN_PLAN_PROTOCOL.to_string(),
    }
}

/// Read `fern-run version` from either an ordinary subprocess or the maintained
/// SSH PTY, where stdout and the one human stderr line share one byte stream.
pub(crate) fn parse_report_output(output: &[u8]) -> Result<BinaryVersionReport, String> {
    let text = std::str::from_utf8(output).map_err(|error| format!("version output is not UTF-8: {error}"))?;
    let json_start = text
        .find('{')
        .ok_or_else(|| "version output contains no JSON object".to_string())?;
    let json_end = text
        .rfind('}')
        .ok_or_else(|| "version output contains no complete JSON object".to_string())?;
    let report: BinaryVersionReport = serde_json::from_str(&text[json_start..=json_end])
        .map_err(|error| format!("version JSON is invalid: {error}"))?;
    if report.schema != BinaryVersionReport::SCHEMA_ID {
        return Err(format!(
            "version schema is {:?}, expected {:?}",
            report.schema,
            BinaryVersionReport::SCHEMA_ID
        ));
    }
    Ok(report)
}

// CHG 2026-08-28: the operator `fern-run version` verb moved to the C# host
// (csharp/Fern.Run.Cli). This stays because `fern-worker version` still prints
// the same typed report, and `deploy` and the Worker-Site identity probe parse
// it through `parse_report_output` below.

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn embedded_binary_identity_is_typed_and_machine_readable() {
        let report = report();
        let json: serde_json::Value = serde_json::from_str(&report.checked_pretty_json().unwrap()).unwrap();

        assert_eq!(json["schema"], BinaryVersionReport::SCHEMA_ID);
        assert_eq!(json["crate_version"], env!("CARGO_PKG_VERSION"));
        assert_eq!(json["run_plan_protocol"], RUN_PLAN_PROTOCOL);
        assert!(
            report.built_from_commit.len() == 40
                && report
                    .built_from_commit
                    .bytes()
                    .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
        );
        if let Some(digest) = &report.source_archive_sha256 {
            assert!(
                digest.len() == 64
                    && digest
                        .bytes()
                        .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
            );
        }
    }

    #[test]
    fn version_reader_accepts_the_human_line_merged_by_an_ssh_pty() {
        let expected = report();
        let mixed = format!(
            "fern-run human summary\n{}\n",
            serde_json::to_string_pretty(&expected).unwrap()
        );

        assert_eq!(parse_report_output(mixed.as_bytes()).unwrap(), expected);
    }

    #[test]
    fn installation_identity_preserves_the_binary_role() {
        for role in [
            InstalledBinaryRole::Scheduler,
            InstalledBinaryRole::MatrixWorker,
            InstalledBinaryRole::RegistryHome,
            InstalledBinaryRole::WorkerSite,
        ] {
            let expected = installation_identity(role);
            let bytes = serde_json::to_vec(&expected).unwrap();
            assert_eq!(parse_installation_identity(&bytes).unwrap(), expected);
            match role {
                InstalledBinaryRole::Scheduler
                | InstalledBinaryRole::RegistryHome
                | InstalledBinaryRole::WorkerSite => {
                    assert!(expected.recipe_handler_capabilities.is_none());
                    assert!(expected.recipe_handler_capabilities_sha256.is_none());
                }
                InstalledBinaryRole::MatrixWorker => {
                    assert_eq!(expected.recipe_handler_capabilities_sha256.as_ref().unwrap().len(), 64);
                }
            }
        }
    }

    #[test]
    fn scheduler_identity_matches_the_language_neutral_fixture() {
        let expected = installation_identity(InstalledBinaryRole::Scheduler);
        let fixture = include_bytes!("../../tools/fern-control/scheduler/testdata/installation-identity-v4.json");
        assert_eq!(parse_installation_identity(fixture).unwrap(), expected);
    }

    #[test]
    fn scheduler_identity_refuses_scientific_handler_capabilities() {
        let mut identity = installation_identity(InstalledBinaryRole::Scheduler);
        let capabilities = fern_recipes::recipe_handler_capabilities();
        identity.recipe_handler_capabilities_sha256 = Some(recipe_handler_capabilities_sha256(&capabilities));
        identity.recipe_handler_capabilities = Some(capabilities);

        let error = parse_installation_identity(&serde_json::to_vec(&identity).unwrap()).unwrap_err();
        assert!(error.contains("non-worker installation identity must not carry"));
    }

    #[test]
    fn handler_capability_identity_is_stable_and_bound_to_the_typed_record() {
        let mut capabilities = fern_recipes::recipe_handler_capabilities();
        let before = recipe_handler_capabilities_sha256(&capabilities);
        assert_eq!(before, recipe_handler_capabilities_sha256(&capabilities));

        capabilities.evals.push("future-evaluator".to_string());
        assert_ne!(before, recipe_handler_capabilities_sha256(&capabilities));
    }

    #[test]
    fn installation_identity_reader_refuses_a_forged_capability_digest() {
        let mut identity = installation_identity(InstalledBinaryRole::MatrixWorker);
        identity.recipe_handler_capabilities.as_mut().unwrap().models[0].fits_weights ^= true;
        let bytes = serde_json::to_vec(&identity).unwrap();
        assert!(
            parse_installation_identity(&bytes)
                .unwrap_err()
                .contains("capability digest")
        );
    }
}
