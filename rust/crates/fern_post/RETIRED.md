# RETIRED 2026-08-28 — `fern_post`

Owner decision 2026-08-28 (evening): postprocessing is Python's
(`fern.postprocess` Stage); "the remaining cut is easy, don't dwell". The
crate is moved here whole and unchanged and is no longer a workspace member.

Measured before the cut: its only code use was
`crates/fern-statistics/src/postprocess_io.rs` (retired at the same time);
`fern-cli-rs` and `fern-worker` carried a Cargo dependency edge with no
`use fern_post::` behind it (a comment in `fern_run/src/results.rs:441`
mentions it by name only). Both edges are removed.

Contents: `artifact_io.rs`, `diagnostics.rs`, `sequence_tensors.rs` — the
typed readers for a run's artifact files and sequence tensors. The C#
`AttemptArtifactContracts` and the Python postprocess Stage are the owners
of what they read.
