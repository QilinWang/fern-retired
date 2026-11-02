# RETIRED 2026-08-28 (night) — `crates/fern-execution-profile`

No workspace member depended on this crate once `fern_run` and `fern-cli-rs`
retired the same night (measured from every remaining `Cargo.toml` and
`cargo tree -i`). It held the execution profile (v5) declaration, its revision identity and the campaign storage thresholds. The C# owners: `Fern.Scheduler` ExecutionProfile (ExecutionProfileV5), Preflight.StorageProbe, `Fern.Experiment` ExecutionProfileIdentity. Preserved whole,
manifest and tests included; its tests are the readable record of the
contracts the C# side reproduces (the corpus goldens over 8,994 sealed
snapshots and 1,876 committed attempts are the executable one).
