# RETIRED 2026-08-28 — `crates/fern-evidence`

Moved here whole and unchanged (Cargo.toml, src, tests) when `fern-run
evidence` left the Rust binary. It is no longer a workspace member and nothing
depends on it: `fern-cli-rs/src/evidence_commands.rs` was its only caller, and
that file retired the same evening to
`retired/fern-cli-rs/evidence_commands_2026_08_28.rs`.

The caller census was run with no include filter. Outside this crate and
`retired/`, the name appeared in exactly three live places: the workspace
`Cargo.toml`, `fern-cli-rs/Cargo.toml`, and one path-to-description line in
`scripts/check_compute_drift.py`. All three were updated.

## What the C# port took

`fern-run evidence verify` is served by `csharp/Fern.Run.Cli/
EvidenceAndTaskVerbs.cs`, over three owners in `csharp/Fern.Scheduler/`:

| this crate | the C# owner |
| --- | --- |
| `release_metrics_parquet.rs` — `decode_release_metrics_parquet`, `validate_columns`, the row and metadata readers | `ReleaseMetricsParquet.cs` (physical) and `ReleaseMetricsDecode.cs` (typed) |
| `lib.rs` — `ReleaseMetricsTableV1`, `canonicalize_structure`, `canonical_bytes`, `content_digest` | `ReleaseMetricsTable.cs` |
| `lib.rs` — `EvidenceReleaseV1::canonicalize`, `::digest` | `EvidenceReleaseIdentity.cs` |
| `crates/fern-artifact` — `check_reopened_payloads`, `scientific_artifact_content_digest` (non-tensor branch) | `EvidenceReleaseIdentity.CheckReopenedPayloads` |

Of 32 public functions and 20 public types in `lib.rs`, `verify` reached
seven functions. Four subtrees are carried through the C# port as the bytes
serde already wrote rather than re-modelled — the metric specification, and the
manifest's reported artifact, metric contract and tolerance — because a
verifier proves bytes and does not interpret the science. The consequence is
recorded in `ReleaseMetricsTable.cs`: this crate also VALIDATED those subtrees
on the way through, and the port does not.

## What stayed here, and why it was not ported

- **`campaign_ledger_parquet.rs`, all 520 lines.** The operational campaign
  ledger, which is `project-campaign` and `verify-campaign` — a different verb
  answering a different question, with no caller now.
- **The encoder half of `release_metrics_parquet.rs`** —
  `encode_release_metrics_parquet`, `write_release_batch`, the `RecordBatch`
  construction. A verifier reads; it never writes a release.
- **The building and freezing path in `lib.rs`** — what `plan-release`,
  `build` and `freeze` used. That pipeline ran once, for the migration
  close-out whose artifacts are at
  `results/evidence_releases/migration-closeout-quantile-v1/`.
- **Policy-card canonicalization and digest, and acceptance-decision
  validation.** The Registry owns these: `csharp/Fern.Registry/
  EvidenceReleases.cs` and `csharp/Fern.Scheduler/EvidencePolicyIdentity.cs`
  already held them, which is why Rust's `verify` called the C# authority for
  the release half rather than deciding it here.
- **`canonicalize_against_required_coordinates`, the selection variants of
  `canonicalize_against` and `canonical_bytes_against*`, and the campaign
  summary types.** Release-construction concerns, not verification.

## The one thing verification could not prove, on this machine

There is no end-to-end ACCEPTANCE golden, by either implementation. The frozen
release under `results/evidence_releases/` is not registered in any Registry
Home here — `results/paper_campaign/lifecycle.db` answers "unknown frozen
evidence release", and the campaign's other database is not a migrated Home.
That directory holds the release's artifacts, not its registration. What was
proved instead is refusal equivalence against `target/debug/fern-run` (three
receipts byte-identical, two differing only in a nested library or subprocess
string) and, one layer down, that the table rebuilt from the frozen file hashes
to exactly the content digest that release froze.
