//! Registered Worker Sites: which machines Registry Home may hand a Stage to.
//!
//! A Worker Site is not a second machine vocabulary beside the execution
//! profile. `site_id` IS the profile id, and registering a Site records which
//! profile revision was registered plus the machine facts observed at that
//! moment. This module holds the shared Site facts and the C# reader receipt;
//! it dispatches nothing. Go resolves the Plan's one execution profile to this
//! registered Site before dispatch.
//!
//! Nothing here enters scientific identity. Site, GPU, runtime and runner bytes
//! are provenance a later execution receipt cites, so moving the same
//! experiment to another machine leaves the scientific question unchanged while
//! a difference between two machines stays traceable to its exact Site.

use serde::{Deserialize, Serialize};
use std::path::Path;

use crate::lifecycle::{LifecycleResult, sha256_hex};

/// The machine facts a registration observed, already reduced to the fields a
/// person or a later claim reads. The full fingerprint travels beside them as
/// JSON so nothing observed is thrown away.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SiteCapabilityFacts {
    pub device: String,
    pub hostname: String,
    pub cuda_available: bool,
    pub cuda_device_name: Option<String>,
    /// Rendered as `major.minor`, the same way a profile declares it.
    pub cuda_compute_capability: Option<String>,
    pub cuda_total_memory_bytes: Option<i64>,
    pub python_version: String,
    pub python_executable: String,
    pub torch_version: String,
    /// Bytes of the deployed runner. A local development profile has none,
    /// because its binary is rebuilt rather than published.
    pub worker_binary_sha256: Option<String>,
    pub source_root: String,
    /// `ObservedExecutionFingerprint::environment_content_digest` — stable
    /// across two probes of an unchanged machine, which is what makes
    /// re-registration idempotent instead of a new row every minute.
    pub observed_environment_digest: String,
    pub observed_at_ms: i64,
    /// The complete `ObservedExecutionFingerprint` as observed.
    pub observed_fingerprint_json: String,
}

impl SiteCapabilityFacts {
    /// The digest a Site must present at claim time.
    ///
    /// It covers who the Site is, which profile revision it was registered
    /// against, and the environment content observed — not the observation
    /// time, so probing the same machine again reproduces the same digest.
    pub fn capability_digest(&self, site_id: &str, execution_profile_revision: &str) -> String {
        sha256_hex(
            [
                "fern.worker_site_capability.v1",
                site_id,
                execution_profile_revision,
                &self.observed_environment_digest,
                self.worker_binary_sha256.as_deref().unwrap_or(""),
                &self.source_root,
            ]
            .join("\u{1f}")
            .as_bytes(),
        )
    }
}

/// One registered Site as a reader sees it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct WorkerSiteRow {
    pub site_id: String,
    pub execution_profile_id: String,
    pub execution_profile_revision: String,
    pub executor: String,
    pub device: String,
    pub enabled: bool,
    pub capability_digest: String,
    pub max_concurrent_stages: i64,
    pub max_gpu_stages: i64,
    pub allowed_handlers: Vec<String>,
    pub registered_by: String,
    pub registered_at_ms: i64,
    pub updated_at_ms: i64,
    pub disabled_reason: Option<String>,
    /// The capability the Site currently publishes, when its row is present.
    pub capability: Option<SiteCapabilityFacts>,
    /// The exact Stage Attempts this Site holds right now, read from the
    /// Attempt ledger -- never from a PID, a GPU snapshot, or systemd. Its
    /// length is what the budget check counts.
    pub holding: Vec<HeldStage>,
}

/// One Stage a Site is holding, named so a person reading `site list` can see
/// which cell is on which machine without opening the registry.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct HeldStage {
    pub stage_attempt_id: String,
    pub plan_id: String,
    pub stage_plan_instance_id: String,
    pub stage_id: String,
    pub state: String,
}

/// Every registered Site, with its published capability and what it holds now.
/// The C# Registry reader is the active query owner.
pub fn list_worker_sites_from_registry(registry: &Path) -> LifecycleResult<Vec<WorkerSiteRow>> {
    crate::csharp_registry::run_json("list-worker-sites", registry, &serde_json::json!({}))
        .map_err(crate::lifecycle::LifecycleError::InvalidRequest)
}

#[cfg(test)]
mod tests {
    use super::list_worker_sites_from_registry;

    #[test]
    fn csharp_reader_returns_a_typed_empty_site_list_from_a_migrated_registry() {
        let directory = std::env::temp_dir().join(format!(
            "fern_worker_site_reader_{}_{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .expect("system clock after epoch")
                .as_nanos()
        ));
        std::fs::create_dir_all(&directory).expect("create test Registry Home");
        let registry = directory.join("fern.db");
        crate::csharp_registry::migrate_test_registry_via_home(&registry);

        let sites = list_worker_sites_from_registry(&registry).expect("typed C# WorkerSite receipt");

        assert!(sites.is_empty());
        std::fs::remove_dir_all(&directory).expect("remove test Registry Home");
    }
}
