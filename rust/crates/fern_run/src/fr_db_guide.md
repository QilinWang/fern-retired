# db.rs: Experiment Registry — Annotated Walkthrough

> **Migration update (2026-06): `db.rs` is now Diesel, not sqlx.**
> The reading order below still holds — and after the reorg the file physically
> follows it (passes ②→⑧ in sequence). What changed is the *mechanics*: the sqlx
> specifics are retired and shown ~~struck through~~ below, with the Diesel reality
> next to them.
>
> **Why strikethrough instead of deleting:** this guide is a teaching artifact, so
> seeing the old sqlx framing beside the new Diesel one *is* the lesson — and it
> matches the repo rule "keep the existing explanation, add incremental notes
> instead of replacing it." Nothing is silently rewritten.
>
> **Migration update (2026-08-25):** mutable Registry Home processes now open
> and migrate through Go. The one SQL chain moved to
> `tools/fern-control/registry/migrations`.
>
> **Migration update (2026-08-28):** the final Rust pool/migration caller moved
> to Go Registry Home. `init_pool`, `run_migrations`, the r2d2 customizer, and
> the embedded-migration dependency are retired intact at
> `retired/fern_run/db_init_pool_migration_substrate.rs`. The historical lesson
> remains below, but it no longer describes an active Fern database owner.
>
> **Retired sqlx → retired Diesel, at a glance** (the active owner is now Go
> migration admission plus C# Registry/RunData transactions):
>
> | ~~retired (sqlx)~~ | ~~retired Rust Diesel~~ / active owner |
> |---|---|
> | `SqlitePool` / `SqlitePoolOptions` | ~~`DbPool = Pool<ConnectionManager<SqliteConnection>>` (r2d2)~~; Go/C# own connections |
> | `SqliteConnectOptions` (WAL, foreign_keys) | ~~`SqlitePragmas` customizer~~; each active owner configures its connection |
> | `sqlx::migrate!("./migrations")` | `fern-home registry-apply-migrations` over the one Go-embedded chain |
> | `#[derive(FromRow)]` | `#[derive(Queryable, Selectable)]` + `RunRow::as_select()` |
> | positional `.bind(x)` | value attached to its column: `col.eq(x)`, `.filter(...)`, `NewRun` |
> | `query_as::<_, T>(sql)` | typed DSL: `runs::table.filter(...).load(conn)` |
> | `ON CONFLICT … COALESCE(excluded.x, x)` | `.on_conflict(run_hash).do_update().set(coalesce(excluded(x), x))` |
> | dynamic SQL `(? IS NULL OR col = ?)` | boxed query: `.into_boxed()` + conditional `.filter(...)` |
> | `async fn` / `.await?` | sync fns over `&mut SqliteConnection` (callers use `spawn_blocking`) |
> | `query!()` + committed `.sqlx` cache | Diesel checks queries against `schema.rs` — no DB or cache at build |
>
> sqlx now survives as annotated retired snippets and migration-history context,
> not as the live DB implementation.

## How to use this guide

~~`db_annotated.rs`~~ `db.rs` is the Rust experiment database, rebuilt as a tutorial
with inline annotations in the same style as `main.rs`. It's the Rust
twin of `fr_db.py` (now via Diesel, not sqlx).

**Don't read it top to bottom.** Follow the passes below — each one
adds a layer, building on the vocabulary of the previous pass.

The central insight that frames everything:
> In Python, experiment identity is scattered across RunId, DoseLabel,
> RunMeta, RunResult, and fr_db — five courier classes. In Rust, the
> **database row IS the metadata**. Two write structs (RunRegistration,
> Metrics), two read structs (RunRow, CompareRow), and free functions.
> The couriers collapse.

---

## Reading sequence

### Pass ① Tiny leaves — the vocabulary

Read these first. Five items, zero internal dependencies.

| Section | Item | What you learn |
|---------|------|----------------|
| §1 | `git_sha()` | `Command::new`, the Option chain (`.ok()` → `.and_then()` → `.map()` → `.unwrap_or_default()`) |
| §2 | caller-owned `artifact_dir` | `PathBuf` in a write contract; DB stores paths but does not derive them |
| §3 | `Metrics` struct | `Option<f64>`, `#[derive(Default)]`, `..Default::default()` fill-in-the-blanks |
| §4 | `HEAVY_FILES` / `VALID_METRICS` | `const`, `&[&str]` slice of string literals |
| §5 | `pin_run()` / `unpin_run()` | `pub async fn`, `Result<(), E>`, `.bind()`, `.execute()`, `.await?`, `Ok(())`, auto-commit |

**Rust concepts:** `&T`, `mut`, `format!()`, Option chain, `const`, `pub async fn`, `Result`, `.await?`, `Ok(())`.

---

### Pass ② DB lifecycle — retired Rust pool, active Go schema admission

| Section | Item | What you learn |
|---------|------|----------------|
| §6 | `SCHEMA_V1` | `r#"..."#` raw string literal, the shared schema |
| §7 | retired `init_pool()` | The annotated r2d2 ownership experiment; retained only in the retired capsule |
| §8 | `fern-home registry-apply-migrations` | The active typed migration admission over the sole migration chain |

**Key concept:** the singleton was first replaced by explicit Rust ownership,
then the entire ownership boundary moved: Go admits the schema and C# owns each
Registry/RunData transaction. No global cache and no second Rust migration
executor remain. The fern-control migration chain is the source of truth.

---

### Pass ③ Data shapes — the four structs

| Section | Item | What you learn |
|---------|------|----------------|
| §9 | `RunRow` | ~~`#[derive(FromRow, Serialize)]`~~ `#[derive(Queryable, Selectable, Serialize)]`; `Option<T>` = nullable, `String` = NOT NULL; reads use `RunRow::as_select()` (columns matched by name, not position) |
| §10 | `CompareRow` | Aggregation-specific struct, SQL alias must match field name |
| §11 | `RunRegistration` | Write-side contract, flattens RunId + DoseLabel + kwargs into one struct |
| §12 | `RunFilter` | `#[derive(Default)]`, `..Default::default()`, bag of optional parameters |

**Key concept:** Four structs for four purposes. Mixing them up = compile error.

---

### Pass ④ Write path — terminal states

| Section | Item | What you learn |
|---------|------|----------------|
| §13 | `complete()` | Multiple metric fields, `Option<f64>` updates, Copy types don't need `&` |
| ~~§13b~~ | ~~Sidebar: `query!()` vs `query()`~~ | ~~Compile-time vs runtime SQL checking~~ → N/A under Diesel: queries are checked against `schema.rs` at compile time, with no `DATABASE_URL` and no `.sqlx` cache |
| §14 | `mark_failed()` | `&str` parameter vs `String` field, first-class failure state |

**The rule:** small values (numbers) copy, big values (strings) borrow.

---

### Pass ⑤ Write path — registration (the UPSERT)

| Section | Item | What you learn |
|---------|------|----------------|
| §15 | `enroll()` (→ `write_run`) | `to_string_lossy()`, ~~19 binds~~ one `NewRun` (Insertable), `.on_conflict(run_hash).do_update()`, `excluded(..)`, `coalesce(..)`, `RunHandle` return, ownership transfer |

**The biggest function.** The UPSERT enables crash-retry: same parameters → same path → same row → reset to running.

---

### Pass ⑥ Read path — querying the registry

| Section | Item | What you learn |
|---------|------|----------------|
| §16 | `list_runs()` (dynamic) | ~~Runtime SQL building, conditional `.bind()`~~ → `.into_boxed()` + conditional `.filter(col.eq(x))`; `.as_deref().unwrap_or()` |
| ~~§16b~~ | ~~`list_runs()` (fixed-shape)~~ | ~~`(? IS NULL OR col = ?)` pattern~~ → the boxed-query `.filter()` chain replaces both the dynamic and fixed-shape sqlx forms |
| §17 | `compare()` | GROUP BY aggregation, fixed-shape, `CompareRow` |
| ~~§18~~ | ~~`latest()`~~ | ~~ORDER BY finished_at DESC, `artifact_dir IS NOT NULL`~~ → retired (2026-07): no caller after the Leptos/Bun dashboard era; frozen verbatim in `retired/fern_run/db_latest_best_read_api_2026_07_01.rs` |
| ~~§19~~ | ~~`best_by_*()`~~ | ~~`macro_rules!`, `concat!()`, enumerated functions replace whitelist~~ → retired (2026-07) with §18, same freeze file |

**Evolution visible in the file:**
- §16: dynamic SQL (how Python does it)
- §16b: fixed-shape SQL (the clean Rust pattern)
- ~~§19: enumerated functions via macro (the final form)~~ → the §19 macro lesson now lives in the freeze file above

---

### Pass ⑦ Artifact lifecycle — slim_run

| Section | Item | What you learn |
|---------|------|----------------|
| §20 | `slim_run()` | Tuple destructuring from `query_as`, `let Some(x) = ... else { return }`, `std::fs::metadata`, `remove_file().ok()`, DB + filesystem crossover |

**Three Option-handling patterns now in the file:**
- `if let Some(x) = ...` — "do extra work if present"
- `.unwrap_or("default")` — "give me value or fallback"
- `let Some(x) = ... else { return }` — "I need this, bail if absent"

---

### Pass ⑧ Tests — the in-memory safety net

| Section | Item | What you learn |
|---------|------|----------------|
| §21 | ~~`test_pool()`~~ `test_conn()` / `sample_reg()` | `:memory:` SQLite, `.unwrap()` in tests, `.into()` for &str→String |
| §22 | `test_enroll_and_complete` | Happy path, `assert!`, `assert_eq!`, float comparison with epsilon |
| §23 | `test_mark_failed` | Sad path, `.as_deref()` for Option<String> == Option<&str> |
| §24 | `test_compare` | `.iter().find(\|r\| ...)`, closure syntax |
| ~~§25~~ | ~~`test_latest`~~ | ~~`Metrics::default()` = all None~~ → retired with `latest()` to the §18/§19 freeze file |
| §26 | `test_reenroll_resets_status` | **The UPSERT story** — same reg → same db_id, error_msg cleared |
| §27 | `test_pin_prevents_slim` | Guard check: freed == 0 when pinned |

**Read §26 carefully** — it's the test that proves crash-retry works.

---

## Python → Rust collapse map

| Python | Rust | What changed |
|--------|------|-------------|
| `get_db()` + `_db_cache` singleton | Go migration admission + C# operation-scoped connection | No global, no cache reset, one authority per operation |
| `RunId` + `DoseLabel` + `cfg` kwargs | `RunRegistration` struct | One struct, compile-checked |
| `metrics: dict` with `.get()` | `Metrics` struct with `Option<f64>` | Typo = compile error |
| `sqlite3.Row` (dict-like) | `RunRow` / `CompareRow` | Column drift = compile error |
| `_VALID_BEST_METRICS` + whitelist | `best_by_mse()`, `best_by_crps()`, ... | Functions ARE the whitelist |
| `db.commit()` after every write | C# owner transaction commits one admitted operation | Refusal means zero canonical mutation |
| `_db_cache = None` reset | Not needed | Singleton eliminated |
| `try/except` → `return ""` | Option chain `.ok().and_then()...` | Control flow → data flow |
| `datetime.now().isoformat()` (local) | `Utc::now().to_rfc3339()` (UTC) | Timezone bug fixed |
| `if dataset: clauses.append(...)` | `(? IS NULL OR dataset = ?)` | Dynamic → fixed-shape SQL |
| `backfill()` / `backfill_csv()` | Retired / recovery-only | Python-only archaeology |
| `slim_all()` | No equivalent | Jupyter-only workflow |
| `load_result()` → `RunResult` | `/api/arrow/:id/:file` endpoint | Browser fetches directly |

---

## Concept index

| Concept | Section | Python equivalent |
|---------|---------|-------------------|
| `&T` borrowing | §2 | No equivalent (reference-counted) |
| `let mut` | §2 | All variables mutable by default |
| `format!()` / `concat!()` | §2 / §19 | f-strings |
| Option chain `.ok().and_then().map()` | §1 | try/except |
| `.unwrap_or_default()` | §1 | `except: return ""` |
| `const &[&str]` | §4 | Module-level tuple |
| `#[derive(Default)]` | §3 | No equivalent |
| `..Default::default()` | §3 / tests | No equivalent |
| `Option<f64>` | §3 | `float \| None` |
| `pub async fn` | §5 | `async def` |
| `Result<(), E>` / `.await?` / `Ok(())` | §5 | Implicit exception propagation |
| `r#"..."#` raw string | §6 | `"""..."""` |
| Builder pattern | §7 | Separate PRAGMA calls |
| `#[derive(FromRow)]` | §9 | `sqlite3.Row` dict lookup |
| `#[derive(Serialize)]` | §9 | `json.dumps(dict(row))` |
| `.as_deref()` | §16 | No equivalent (one string type) |
| `query_as::<_, T>` turbofish | §16 | No equivalent (rows are shapeless) |
| `(? IS NULL OR col = ?)` | §16b | `if dataset: clauses.append(...)` |
| `macro_rules!` | §19 | No equivalent |
| `let Some(x) = ... else { return }` | §20 | `if x is None: return` |
| Tuple destructuring from query | §20 | `row["col"]` dict access |
| `#[cfg(test)]` / `mod tests` | §21 | Test file convention |
| ~~`#[tokio::test]`~~ plain `#[test]` (Diesel is sync) | §22 | ~~`@pytest.mark.asyncio`~~ plain `def test_*` |
| `.into()` for &str → String | §21 | No equivalent (one string type) |
| `assert!` / `assert_eq!` | §22 | `assert` |
| `.iter().find(\|r\| ...)` | §24 | `next(r for r in rows if ...)` |

---

## What to do after reading

1. **Read pass by pass** — don't skip ahead
2. **Cross-reference retired/fr_db/fr_db_python_only_legacy.py** — each section notes its Python twin
3. **Cross-reference main.rs** — the Axum handlers call these functions
4. **Run the tests** — `cargo test -p fern_run` exercises the Diesel library path, and `cargo test -p fern-cli-rs` exercises the CLI lifecycle bridge
5. **Break something** — rename a RunRow field and watch the compile error cascade
6. **Notice what disappeared** — no commit(), no singleton, no courier classes, no whitelist
