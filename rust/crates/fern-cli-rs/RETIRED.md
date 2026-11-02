# RETIRED 2026-08-28 (night) — `fern-cli-rs`, the Rust `fern-run` operator CLI

The crate that began the day with 73 verbs and 34,568 lines ends it with no
verb the C# host does not serve. Its last two, `plan-ablation` and
`run-ablation`, were proved tonight: a paired DLinear Ablation (control one
training epoch, treatment two, one shared cell coordinate) resolved with the
Rust `fern-experiment`, compiled and registered through the C# host, and run
to completion by the C# scheduler on local-4070 — Plan
`acc4f22dacdbad486b2c3d5f074ba0e6c10d1ae4525841f0f82190f0d011c4fd`
(`dlinear-epochs-pair-local-4070-20260828`), six Stage starts, every Stage
committed, in one dispatch cycle. Grok's DLinear Plan
`csharp-dlinear-local-4070-20260828d` had already proved the experiment pair.

Where each of the 73 verbs went is the inventory:
`docs/references/user/2026-08-28-fern-run-command-inventory.md`; the
retirement passes are `docs/references/user/2026-08-28-rust-first-principle-cuts.md`
and the per-verb copies under `retired/fern-cli-rs/`. The whole crate is
preserved here as it stood at the end (14.8k lines), so a retired copy and
the live tree are never the only record.

What nothing else needed: no crate depended on it (a binary crate); the
installer already named the C# host as the installing `fern-run`; Python's
one locator `fr_tr.fern_run_csharp_cmd` points at the host. Still naming the
Rust binary, deliberately untouched: two Justfile recipes (`cancel-plan`,
`cancel-matrix`, owner rule: Justfiles are out of scope) and permission
entries in `.claude/settings.local.json`. The installed content-addressed
`deploy/run-operator-binaries/fern-run.b897b7eb…` keeps serving as the
old-owner witness for the C# goldens that still compare against it.

`build.rs` (the role cfg) and `.cargo/config.toml`'s `[env] FERN_PYTHON`
(for this crate's DGP-bank tests) leave with it.
