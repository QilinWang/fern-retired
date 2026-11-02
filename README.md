# Fern: the retired C# and Rust implementations

**Status: retired on 2026-09-28 and not maintained.** This code is published as
an archive. It builds, and the test suites that need nothing but this repository
pass. No issue will be fixed and no pull request will be merged. The running
system is Gleam Home and Scheduler with a Gleam Site Node executing Python
science stages.

## What is inside

| Directory | What it is |
|---|---|
| `csharp/` | the last complete C# tree at the paths it had when it was retired: 464 files, ten projects, the registry and scheduler with their tests |
| `rust/` | the last complete Rust workspace: `Cargo.toml`, `Cargo.lock`, 13 crates in the tree and four of them workspace members that `cargo build --workspace` builds |

The C# tree keeps its original layout, so the solution is
`csharp/src/csharp/Fern.sln` and project references stay relative to it.

## Build

```sh
cd csharp/src/csharp && dotnet build Fern.sln     # .NET 11 preview SDK, pinned in csharp/global.json
cd rust && cargo build --workspace               # Rust 1.97, dependencies pinned in Cargo.lock
```

Measured on a clean export: the C# solution builds in about 20 s with 0 errors
and 4 warnings, and the Rust workspace in about 37 s with 0 errors. The four C#
warnings come from `Fern.BuildIdentity.targets`, which asks git for a commit
hash; outside a checkout of the original repository it reports the missing
identity and nothing else.

## Test status

The suites that need a sibling tree fail here, and only for that reason: those
trees are not part of this archive. The numbers below are from one run on the
export.

| Suite | Result | What it needs that this repository does not contain |
|---|---|---|
| C# `Fern.Registry.Tests` | 337 of 337 pass | nothing |
| C# `Fern.Scheduler.Tests` | 507 of 619 pass | the built Gleam scheduler shipment (112 tests report `no Gleam shipment entrypoint`) |
| Rust `fern-experiment-types` | 41 of 41 pass | nothing |
| Rust `fern-json-contract` | 5 of 5 pass | nothing |
| Rust `fern-recipes` | 25 of 26 pass | `fr_core.py`, the reviewed Python worker source |
| Rust `fern-experiment-authoring` | 5 of 44 pass | the C# `fern-experiment` command-line binary, built at the path it had before retirement |

```sh
cd csharp/src/csharp && dotnet test Fern.sln
cd rust && cargo test --workspace --no-fail-fast
```

## Running the commands

Both command-line entrances start and parse their arguments; they then want the
registry or the configuration they were built against, which lived outside this
repository.

```sh
dotnet run --project csharp/src/csharp/Fern.Registry.Cli -- --help
cargo run -p fern-experiment-authoring --bin fern-experiment -- --help
```

## What replaced what

| Old responsibility | Current owner |
|---|---|
| Registry scheduling, study and source registration, queue, retries, site terms and claims | Gleam Home and Scheduler |
| C# `RunStudyCells` to `ResidentWorkerHost` to `fern-site` phase RPC | Gleam Site Node cells, stage calls, Python stages |
| Human views, actions and results | Gleam CLI, Home, Lustre Dashboard; Python result store |
| Artifact hold, GC, location and relocation (six `RegistryCli` verbs) | retired with no replacement; it had no current caller |
| Provider lifecycle, runtime selection and the .NET set installer | Gleam Home machine install, start and check |
| CI, release and nightly | Gleam packages and bundled OTP releases |

The full boundary table, with the evidence and the limit of each line, is the
retirement inventory in the Fern repository
(`docs/programs/2026-09-28-retire-csharp-owners/retirement-inventory.md`).

## Why it is published

The same system exists in three implementations: Rust, then C#, then Gleam. The
C# one is the largest and the heaviest, and it is the one that was retired
wholesale. Publishing it makes that decision checkable instead of merely
described: the source is here, the table above says what each part became, and
the Fern repository records how the export was assembled and what was removed.

Two details show what the repository was: `csharp/src/csharp/Fern.Registry/SerdeJsonRendering.cs`
reproduces the byte layout of Rust's `serde_json` output, including the sorted
keys that come from `BTreeMap`, because identity strings were hashed across two
implementations and had to match exactly; and
`rust/crates/fern-recipes/src/dataset_catalog.rs` embeds a dataset catalogue
that lived in the C# tree, so the two implementations were built against each
other.

## What this is not

It is not a migration path: the old SQLite state was never migrated, and no
import tool is provided. It is not a compatibility layer for current Fern. It is
not built by CI. The original Git hosting workflows are not included, because
the nightly job required a self-hosted GPU machine that no longer exists.

## Licence

MIT. See `LICENSE`.
