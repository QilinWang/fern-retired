# RETIRED 2026-08-28 (night) — the Cargo workspace

The last four crates — `fern-experiment-authoring` (the `fern-experiment` binary;
`resolve-ablation` and `fork-experiment` were its last Rust-only verbs, now on the
C# `fern-run` host, and a fresh install names that host as the experiment
compiler), `fern-recipes`, `fern-experiment-types` and `fern-json-contract` — moved
to `retired/crates/<name>-2026-08-28/` together with the workspace manifest,
the lock, `rustfmt.toml`, `.cargo/config.toml` and `.config/nextest.toml`.
Nothing under the repository root compiles as Rust any more.

Still on disk, deliberately: `target/` (8.8 GiB of build output, the owner's
call to remove — a destructive step under CLAUDE.md's protocol) and the
installed content-addressed Rust binaries under `deploy/` (the old-owner
witnesses the C# goldens compare against, and the members of the production
installation receipt until a fresh install replaces them). The Go tools under
`tools/fern-control` and `tools/fern-go` stay, as ruled.

What still names Rust paths and is meant to: the retired shelves themselves;
`///` provenance comments in C# ("Ported from fern-cli-rs/src/…"); the C#
tests' shelf-aware readers (`RustFixtures`, `RustWitness`,
`ProductionHome.RustWitness`), which resolve a `crates/<c>/…`, `fern_run/…` or
`fern-cli-rs/…` path to its dated shelf; and the two Justfile recipes the
owner ruled out of scope.
