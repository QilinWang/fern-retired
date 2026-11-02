# RETIRED 2026-08-28 — `crates/fern-statistics` (the `fern-stat` binary)

Owner decision 2026-08-28 (evening): "statistics we have moved most to
python … the remaining cut is easy, don't dwell". The crate is moved here
whole and unchanged (Cargo.toml, src, tests); it is no longer a workspace
member and nothing depends on it. `Cargo.lock` lost only the entries it
alone pulled in.

The decision record `docs/decisions/2026-08-12-statistics-crate-migration.md`
describes the EARLIER direction (Python checks ported INTO this crate); the
owner reversed that on 2026-08-27 — statistics are Python's
(`python/fern_analysis/`, run as `python -m fern_analysis`), and the
repository-health checks are C#/Go territory. Read that document as history.

## The eight `fern-stat` verbs and where each went

| verb | after retirement |
| --- | --- |
| `declaration-freeze-request` | Python: `fern_analysis/analysis_request.py` builds the same frozen request contract |
| `research-compute-statistic` | Python: `fern_analysis/analysis_execution.py` runs a declared statistic; `analysis_admission.py` admits it through Go `fern-home registry-admit-statistic` |
| `research-list-statistics` | the declarations directory `analysis_declarations/` is the list |
| `refresh` | none — the recompute-everything chain (`refresh.rs`) is recoverable here; a paper refresh re-homes in Python when a paper needs it |
| `pack` | none — upload-bundle assembly, recoverable here |
| `recipe-compute-titration-statistics`, `campaign-compute-titration-statistics` | none in Rust; the titration numbers the paper cites are computed by the Python analyses |
| `run-set-compute-interval-calibration` | none — HPD-calibration run selection, recoverable here |

What left the crate before it retired: `src/repo_health/cli_defaults.rs`
moved verbatim to `fern-cli-rs/src/cli_defaults.rs` (test-only), because the
rule it holds — no scope-bearing `fern-run` argument may carry a default —
is about fern-run's own CLI and its one caller is fern-run's hygiene test.

Mentions that still name `fern-stat` and were NOT swept (owner: skip the
Justfile; docs are read, not executed): `Justfile:10,31,35`, `README.md`,
`AGENTS.md`, `repo_roadmap.md`, `scripts/check_paper_numbers.py` (status
comment). `python/fern_analysis/accepted.py:103` still tells a caller to run
`fern-stat research-compute-statistic`; that sentence now points at
`python -m fern_analysis`.
