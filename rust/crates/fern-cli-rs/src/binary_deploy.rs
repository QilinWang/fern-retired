//! The deployment lock, the local deployed-binary path, and the storage floor
//! that guard publishing bytes for an SSH Worker Site.
//!
//! RETIRED 2026-08-28: the deployment transaction itself — `fern-run deploy`,
//! which built a checked candidate, published it, verified it on the Site and
//! cut the profile over — left this file for the C# host
//! (`csharp/Fern.Run.Cli/DeployVerbs.cs`, with the receipt and the lock in
//! `csharp/Fern.Scheduler/BinaryDeployment.cs`). Its source and the original
//! module header are preserved verbatim at
//! `retired/fern-cli-rs/binary_deploy_2026_08_28.rs`.
//!
//! What is left is what Rust still CALLS, and nothing else:
//!
//! * `inspect_deploy_storage` — experiment_v2's freeze-source storage floor.
//! * `local_deployed_binary_path` — where run_sweep's SSH Plan start looks for
//!   the local twin of the profile's remote worker digest.
//! * `lock_for_plan_start` — run_sweep's two Plan-start call sites, which take
//!   the same `flock` as a deployment so a Plan cannot launch against a profile
//!   being rewritten. The C# owner takes that lock through a libc `flock`
//!   P/Invoke for exactly this reason: .NET's managed `FileStream.Lock` is an
//!   fcntl byte-range lock and would not exclude this one.

use std::fs::{self, File, OpenOptions};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{Duration, Instant};

use anyhow::{Context, Result, bail, ensure};
use fern_execution_profile::{
    CAMPAIGN_STORAGE_HARD_STOP_FREE_GIB, CAMPAIGN_STORAGE_WARN_FREE_GIB, ExecutionProfile, ExecutionProfileExecutor,
};
use rustix::io::{FdFlags, fcntl_getfd, fcntl_setfd};

const DEPLOY_LOCK_TIMEOUT: Duration = Duration::from_secs(120);
const DEPLOY_LOCK_POLL: Duration = Duration::from_millis(50);
const GIB: u64 = 1024 * 1024 * 1024;

/// Serialize an SSH Plan's profile preflight and Attempt insert with deploy.
///
/// Local Plans do not use the remote deployment cache and therefore do not
/// wait behind a Pod build/upload. The caller keeps the returned file alive
/// until Registry Home has committed the new Plan Attempt.
pub(crate) fn lock_for_plan_start(profile_id: &str) -> crate::Fallible<Option<File>> {
    let workspace_root = deployment_workspace_root()?;
    let profile_path = workspace_root
        .join(ExecutionProfile::default_path(profile_id).map_err(|issue| crate::invalid_input(issue.explanation))?);
    let profile = ExecutionProfile::read(&profile_path).map_err(crate::invalid_input)?;
    if profile.executor == ExecutionProfileExecutor::Local {
        return Ok(None);
    }
    let deploy_root = workspace_root.join("deploy");
    fs::create_dir_all(&deploy_root)?;
    lock_deploy(&deploy_root)
        .map(Some)
        .map_err(|error| crate::invalid_input(error.to_string()))
}

fn deployment_workspace_root() -> crate::Fallible<PathBuf> {
    let helper = crate::run_sweep::remote_ssh_helper_path()?;
    helper
        .parent()
        .and_then(Path::parent)
        .map(Path::to_path_buf)
        .ok_or_else(|| crate::invalid_input(format!("cannot derive workspace root from {}", helper.display())))
}

/// Apply Fern's same hard-stop and warning thresholds before copying an
/// immutable executable into any local deployment root. A caller may carry an
/// explicit owner reason for one operation without weakening other callers.
pub(crate) fn inspect_deploy_storage(workspace_root: &Path, owner_override_reason: Option<&str>) -> Result<()> {
    if let Some(reason) = owner_override_reason {
        ensure!(
            !reason.trim().is_empty(),
            "owner storage override reason must not be empty"
        );
    }
    let owner_override_reason = owner_override_reason.map(str::trim);
    let mut watched = vec![workspace_root.to_path_buf()];
    let windows_volume = PathBuf::from("/mnt/c");
    if windows_volume.exists() {
        watched.push(windows_volume);
    }
    for path in watched {
        let output = Command::new("df")
            .args(["-B1", "--output=avail"])
            .arg(&path)
            .output()
            .with_context(|| format!("cannot inspect free space for {}", path.display()))?;
        ensure!(output.status.success(), "df failed for {}", path.display());
        let available = String::from_utf8(output.stdout)?
            .lines()
            .rev()
            .find(|line| !line.trim().is_empty())
            .context("df returned no available-byte row")?
            .trim()
            .parse::<u64>()?;
        let available_gib = available / GIB;
        if available < CAMPAIGN_STORAGE_HARD_STOP_FREE_GIB * GIB {
            if let Some(reason) = owner_override_reason {
                eprintln!(
                    "OWNER STORAGE OVERRIDE: {} has {available_gib} GiB free, below {} GiB; reason: {reason}",
                    path.display(),
                    CAMPAIGN_STORAGE_HARD_STOP_FREE_GIB
                );
            } else {
                bail!(
                    "deployment storage hard stop: {} has {available_gib} GiB free, below {} GiB",
                    path.display(),
                    CAMPAIGN_STORAGE_HARD_STOP_FREE_GIB
                );
            }
        }
        if available < CAMPAIGN_STORAGE_WARN_FREE_GIB * GIB {
            eprintln!(
                "warning: {} has {available_gib} GiB free; deployment warning begins below {} GiB",
                path.display(),
                CAMPAIGN_STORAGE_WARN_FREE_GIB
            );
        }
    }
    Ok(())
}

fn lock_deploy(deploy_root: &Path) -> Result<File> {
    let path = deploy_root.join(".deploy.lock");
    let file = OpenOptions::new().read(true).write(true).create(true).open(&path)?;
    let fd_flags = fcntl_getfd(&file).context("cannot inspect deployment lock descriptor flags")?;
    fcntl_setfd(&file, fd_flags | FdFlags::CLOEXEC)
        .context("cannot make deployment lock close across supervisor exec")?;
    let started = Instant::now();
    loop {
        match file.try_lock() {
            Ok(()) => return Ok(file),
            Err(fs::TryLockError::WouldBlock) if started.elapsed() < DEPLOY_LOCK_TIMEOUT => {
                std::thread::sleep(DEPLOY_LOCK_POLL);
            }
            Err(fs::TryLockError::WouldBlock) => {
                bail!("timed out waiting for another fern-run deploy to finish; nothing was changed")
            }
            Err(fs::TryLockError::Error(error)) => return Err(error).context("cannot lock deployment operation"),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn deploy_lock_is_close_on_exec_and_does_not_follow_a_supervisor_child() {
        let root = std::env::temp_dir().join(format!(
            "fern-deploy-lock-inheritance-test-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap_or_default()
                .as_millis()
        ));
        fs::create_dir_all(&root).unwrap();
        let guard = lock_deploy(&root).unwrap();
        assert!(fcntl_getfd(&guard).unwrap().contains(FdFlags::CLOEXEC));

        let mut child = Command::new("sh").args(["-c", "sleep 10"]).spawn().unwrap();
        drop(guard);
        let probe = OpenOptions::new()
            .read(true)
            .write(true)
            .open(root.join(".deploy.lock"))
            .unwrap();
        assert!(
            probe.try_lock().is_ok(),
            "an exec child inherited the deployment lock after the parent released it"
        );
        child.kill().unwrap();
        child.wait().unwrap();
        fs::remove_dir_all(root).unwrap();
    }
}
