# RETIRED 2026-08-28 (night) — `fern_run`, the Rust run library

Measured after `fern-cli-rs` retired the same night: no workspace member
depends on this crate. `crates/fern-experiment-authoring` never did (its
`csharp_resolution_warnings.rs` says it "intentionally cannot depend on
`fern_run`"); `crates/fern-recipes` names it only in a comment explaining why
its dependency deliberately is not `fern_run`. All 18,038 lines were reachable
only from the crate itself and its 145 tests. The census that established this,
module by module with internal and external edges and the C# twin of each test:
`docs/references/user/2026-08-28-fern-run-lib-test-census.md`.

What the C# side owns now, by module: the Registry (`Fern.Registry`:
lifecycle, db, run_plan, stage_attempt, worker_site, csharp_registry, the
matrix and evidence identities); source content and implementation snapshots
(`Fern.Registry` SourceContentIdentity, `Fern.Scheduler` SourceFreeze);
binary deployment (`Fern.Scheduler` BinaryDeployment, Installer); the
scheduler office (`Fern.Scheduler`); experiment resolution, lowering and the
build-config identity (`Fern.Experiment`: ExperimentCompile, ExperimentLowering,
ExperimentAdmission, RecipePlanner); artifact reading (`Fern.Scheduler`
AttemptArtifactManifest / AttemptArtifactContracts).

Promises that exist only here, recoverable: 52 test functions across
`resolved_cfg` (15), `model_architecture` (13), `binary_deployment` (12) and
`csharp_experiment` (12) have no C# test of the same name. `resolved_cfg` is
the old owner of the config-hash identity — the `identity_scheme: 2` /
58f682fb→34c7e47c question the owner still has to rule on — and the installed
content-addressed Rust binaries under `deploy/run-operator-binaries/` remain
the executable witness for that identity; this source is the readable one.

The crate is preserved whole, Cargo manifest and tests included.
