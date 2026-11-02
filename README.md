# Fern: the C# and Rust implementations (retired)

This repository holds the two retired implementations of Fern's control plane:
the C# Home, registry, scheduler and site that ran the research until
2026-09-28, and the Rust workspace that came before them. The running system is
now Gleam Home and Scheduler with Gleam Site Node executing Python science
stages. Nothing here is maintained, deployed, or supported.

Both trees build from a clean checkout. Neither is a migration path.

## What is inside

| Directory | What it is | Size |
|---|---|---|
| `csharp/` | the last complete C# tree, at the paths it had when it was retired: 464 files, ten projects including their tests | 686 C# sources in the original repository; this tree keeps the runtime, its tests and its fixtures |
| `rust/` | the last complete Rust workspace: 13 crates, `Cargo.toml` and `Cargo.lock`, 178 files | crates for experiment authoring, experiment types, the JSON contract, recipes, the CLI, the stage runner, evidence and statistics |

The C# tree keeps its original layout: the solution is
`csharp/src/csharp/Fern.sln`, and project references are relative to it.

## How to build

```sh
cd csharp/src/csharp && dotnet build Fern.sln     # .NET 11 preview SDK, pinned in csharp/global.json
cd rust && cargo build --workspace               # Rust 1.97, dependencies pinned in Cargo.lock
```

The C# build reports four warnings from `Fern.BuildIdentity.targets`, which ask
git for a commit hash; outside a checkout of the original repository they
report the missing identity and nothing else.

One C# detail is worth a look: `csharp/src/csharp/Fern.Registry/SerdeJsonRendering.cs`
reproduces the byte layout of Rust's `serde_json` output, including the sorted
keys that come from `BTreeMap`, because identity strings were hashed across two
implementations and had to match exactly.

One Rust detail is worth a look: `rust/crates/fern-recipes/src/dataset_catalog.rs`
reads a dataset catalogue into the binary with `include_str!`. In the original
repository that file lived in the C# tree, so these two implementations were
built against each other.

## What replaced what

| Old responsibility | Current owner |
|---|---|
| Registry scheduling, study and source registration, queue, retries, site terms and claims | Gleam Home and Scheduler |
| C# `RunStudyCells` to `ResidentWorkerHost` to `fern-site` phase RPC | Gleam Site Node cells, stage calls, Python stages |
| Human views, actions and results | Gleam CLI, Home, Lustre Dashboard; Python result store |
| Artifact hold, GC, location and relocation (six `RegistryCli` verbs) | retired with no replacement; there was no current caller |
| Provider lifecycle, runtime selection and the .NET set installer | Gleam Home machine install, start and check |
| CI, release and nightly | Gleam packages and bundled OTP releases |

The full boundary table, with the evidence and the limit of each line, is the
retirement inventory in the Fern repository
(`docs/programs/2026-09-28-retire-csharp-owners/retirement-inventory.md`).

## What this is not

It is not a migration path: the old SQLite state was never migrated, and no
import tool is provided. It is not a compatibility layer for current Fern. It
is not built by CI and is not a supported release. The original GitHub
workflows are not included, because the nightly job required a self-hosted GPU
machine that no longer exists.

## Why it is published

The same system exists in three implementations: Rust, then C#, then Gleam.
The C# one is the largest and the heaviest, and it is the one that was retired
wholesale. Publishing it makes the retirement decision checkable instead of
merely described: the source is here, the table above says what each part
became, and the history of how it was taken apart is in the Fern repository.

## Licence

MIT. See `LICENSE`.
