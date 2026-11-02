# db.rs Diesel Migration Instructions

This note is for the incremental `fern_run/src/db.rs` Diesel migration. It is
an execution contract for future Claude/Codex passes, not a completed migration.

## Core Rule

Follow the original `db.rs` pass structure from `fern_run/src/fr_db_guide.md`.
Do **not** invent new pass numbers for each write function. The pass numbers are
the teaching/migration spine:

1. Pass ① Tiny leaves — vocabulary
2. Pass ② DB lifecycle — pool init and schema migration
3. Pass ③ Data shapes — the four structs
4. Pass ④ Write path — terminal states
5. Pass ⑤ Write path — registration / UPSERT
6. Pass ⑥ Read path — querying the registry
7. Pass ⑦ Artifact lifecycle — `slim_run`
8. Pass ⑧ Tests — in-memory safety net

When migrating, say which original pass and section are being changed, for
example: "Pass ① §5 `pin_run` / `unpin_run`".

## File Policy

Before replacing live code:

1. Snapshot the fully functional SQLx `fern_run/src/db.rs` into
   `fern_run/retired/old_db.rs`.
2. Keep `fern_run/src/db.rs` as the live incremental working file.
3. A failure after that point means the changed live `db.rs` failed, not that the
   original reference is lost.

The retired `old_db.rs` is a frozen reference. Do not rewrite, reformat, or
"clean up" its annotations.

`db_diesel.rs` is only a consult scratch file. Keep it out of `retired/`. Delete
it when the migration is complete; do not archive it as the canonical record.

## Rewrite Shape

For each migrated pass, keep the original SQLx logic visible near the new Diesel
logic:

```rust
// ==================== ORIGINAL SQLX (Pass N §X) ====================
// original code, commented
// --------------------------------------------------------------------

// ===================== NEW DIESEL (Pass N §X) ======================
// live replacement
// --------------------------------------------------------------------
```

Use big visual separators such as `====` or long `----` lines so the old and new
blocks are obvious while reading.

Shared explanation can live before or after the blocks. Do not duplicate every
old inline annotation. For detailed original teaching comments, consult:

- `fern_run/retired/old_db.rs`
- `fern_run/src/fr_db_guide.md`

## Annotation Rule

The existing aligned teaching comments are protected. Do not delete, compress,
or rewrite pointer annotations such as `//  │` and `//  └─` without explicit
permission. If a migrated block keeps that style, mimic it. If rustfmt would
break pointer alignment, use `#[rustfmt::skip]` on the smallest enclosing item.

## Migration Order

Migrate in original pass order unless the user explicitly overrides the order.
Do not jump to low-call-count functions only because they are easy.

Current instruction snapshot:

- `fail()` has already been changed by Claude; leave that work in place.
- The next requested work is Pass ① only.
- Do not run a full recompile unless the user asks; use lighter checks if
  needed and report exactly what was or was not run.

## Cutover Discipline

Each pass should be understandable in isolation:

- preserve the original pass/section labels;
- keep SQLx original and Diesel replacement adjacent;
- avoid broad rewrites outside the active pass;
- do not delete algorithmic or experimental code as collateral cleanup;
- after a pass is proven, remove scratch-only duplication rather than keeping
  permanent compatibility layers.
