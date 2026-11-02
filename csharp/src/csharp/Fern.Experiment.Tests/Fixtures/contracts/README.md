# Fern.Experiment contract fixtures

These are active, implementation-independent contract inputs. Tests read them
because their canonical JSON, identity preimages, refusals, or typed lowering
shapes remain public Fern contracts, not because the retired Rust executable is
an authority.

The initial bytes were copied without rewriting from the intact 2026-08-28
retirement shelves:

- `experiment/` from `retired/crates/fern-experiment-types-2026-08-28/fixtures/`;
- `recipe/` from `retired/crates/fern-recipes-2026-08-28/fixtures/`;
- `run/` from `retired/crates/fern_run-2026-08-28/fixtures/`;
- `artifact/` from `retired/crates/fern-artifact-2026-08-28/fixtures/`.

The retired copies remain unchanged as migration history. A current test must
name the contract it protects. Do not regenerate these files from the C# code
to make a disagreement pass. A deliberate contract change edits the active
fixture and its consuming assertion together, while the retired source bytes
stay intact.
