//! The scheduler lease: who is the one duty officer of this Registry Home
//! (resident_scheduler step 3, authority substrate).
//!
//! Three keys, three questions — Go Registry Home owns the first mutation and
//! its verifier; this Rust module retains only the typed permit/election wire
//! shapes used by callers that already cross that process boundary:
//!   scheduler lease  = "who may run the dispatch/reconcile loop?"
//!   plan permit      = "who may advance this one plan?"    (run_plan.rs)
//!   worker leases    = "who may write this cell's result?" (lifecycle.rs)
//!
//! The election is one atomic transaction: claim wins iff no current lease
//! exists or the current one is EXPIRED, and every win takes a HIGHER
//! fencing epoch. The private capability lives only in the winner's memory
//! (the registry stores its digest), so a crashed scheduler's authority is
//! unrecoverable by design — its successor waits out at most one TTL and
//! claims with the next epoch. The loser of an election receives a refusal
//! naming the holder and exits; it never retries against a live leader and
//! never runs a backup loop. Every dispatch mutation re-proves the epoch
//! inside its own transaction via `require_current_scheduler`, so a fenced
//! scheduler's writes are refused by the database and the winner never has
//! to kill the loser.

/// What the elected loop holds. The capability crosses no receipt, no debug
/// string, and no disk — same discipline as the worker capability.
#[derive(Clone, serde::Deserialize)]
pub struct SchedulerPermit {
    pub scheduler_lease_id: String,
    pub fencing_epoch: i64,
    pub capability: String,
}

impl std::fmt::Debug for SchedulerPermit {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SchedulerPermit")
            .field("scheduler_lease_id", &self.scheduler_lease_id)
            .field("fencing_epoch", &self.fencing_epoch)
            .field("capability", &"[redacted]")
            .finish()
    }
}

#[derive(Debug, serde::Deserialize)]
pub enum SchedulerElection {
    Won {
        permit: SchedulerPermit,
        expires_at_ms: i64,
    },
    /// A live leader exists. The correct response is to exit — report the
    /// holder, do not wait-loop against it, never reconcile beside it.
    Refused {
        holder_actor: String,
        holder_host: String,
        fencing_epoch: i64,
        expires_at_ms: i64,
    },
}
