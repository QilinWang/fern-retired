# RETIRED 2026-08-28 (night) — `crates/fern-artifact`

No workspace member depended on this crate once `fern_run` and `fern-cli-rs`
retired the same night (measured from every remaining `Cargo.toml` and
`cargo tree -i`). It held the typed artifact contracts, identity digests, stage-runner protocol and CAS helpers. The C# owners: `Fern.Scheduler` AttemptArtifactContracts / AttemptArtifactManifest, `Fern.Registry` SourceContentIdentity and the evidence identities, `Fern.StageRunner.Cli`. Preserved whole,
manifest and tests included; its tests are the readable record of the
contracts the C# side reproduces (the corpus goldens over 8,994 sealed
snapshots and 1,876 committed attempts are the executable one).
