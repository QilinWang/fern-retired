//! Does every written record in this repository point at exactly one real thing?
//!
//! A MODULE, not a crate. It WAS a crate for four hours on 2026-08-12, split
//! out because an invented rule fired — guest modules at 929 lines against 362
//! of the crate's own — and the split looked like obeying a rule rather than
//! what it was: a threshold I had made up doing the deciding. The same mistake
//! then produced `fern-release` an hour later.
//!
//! Measured against the rule that replaced them, it earns nothing: no invariant
//! that needs a wall, no dependency to isolate (`serde_json` is
//! already here), one consumer, and an API that grew three modules the day it
//! was created. Crates are houses; this is a drawer.
//!
//! It lives under `fern-statistics` because `fern-stat refresh` is what runs
//! it, and because a front door may run checks from more than one domain. The
//! question here is genuinely different from the rest of the crate — *is the
//! repository's bookkeeping honest*, not *are the research numbers right* — and
//! that difference is worth a module boundary and a paragraph, not a wall.
//!
//! Ownership is decided by the question answered, not by who calls. These three
//! answer one question, in three places:
//!
//! - `cli_defaults` — does a command declare a default for an argument that
//!   decides which data it reads or whose identity it runs under?
//!
//! CHG 2026-08-27: `cli_defaults` is the ONLY one left. `lane_references`,
//! `model_name_collisions`, `pending_patches`, `doc_citations`, `model_variants`
//! and `artifact_freshness` went back to Python on 2026-08-26 (they were Python
//! before 2026-08-12 and had stopped running when they became Rust). This
//! module head still listed all of them a day after they left, which is the
//! thing this crate's own `doc_citations` check exists to catch — found by
//! review, not by the check, because nothing runs the check.
//!
//! `cli_defaults` stays because it walks `fern-stat`'s own `clap::Command`
//! tree, which cannot move without reimplementing a parser, and because it is
//! the only one of the seven that already HAS a caller: a `#[test]` in main.rs.
//!
//! THIS MODULE STAYS THIN. Repository files, git state, document
//! relationships, and a verdict. It may not compute a scientific metric, decide
//! whether evidence is accepted, touch the run lifecycle, or launch anything —
//! those have authority boundaries and belong behind them.
//!
//! WHERE `model_name_collisions` WENT, since this paragraph used to record it
//! as a stated debt. The fact it establishes is that `fern` and
//! `fern_icml_pinned` are two models under one name — a statement about the
//! recipe catalog, not about this repository's filesystem — so it lodged here
//! waiting for a catalog domain that could read completed run rows. That
//! domain never arrived. It is `scripts/check_model_name_collisions.py` now,
//! and it asks `fern-run list-runs` for the rows, which is the owning command
//! answering its own question. The debt is settled rather than still owed.

// CHG 2026-08-26: `lab_ledger` went back to Python — scripts/check_lab_ledger.py,
// where the check started — after its Python matched the Rust byte-for-byte on
// the real repository. The Rust is retired whole at
// retired/crates/fern-statistics/src/lab_ledger_2026_08_26.rs.
pub mod cli_defaults;

/// The one question this crate answers.
///
/// A hand-written reference must resolve to a real file. `lane_references` and
/// `lab_ledger` are both that: source names a lane directory, an index names a
/// script or an artifact or a conclusion note, and the thing named must be
/// there. Both exist because a hand-written index rots silently — the row still
/// reads fine after the file it points at is gone.
/// (CHG 2026-08-26: `lab_ledger` answers it from scripts/check_lab_ledger.py
/// now; the question and its two directions are unchanged.)
pub const HOME_QUESTION: &str = "a written reference must resolve to a real file";

/// Modules that do NOT answer that question and are here anyway.
///
/// `model_name_collisions` is about a recorded experiment identity being
/// ambiguous — two models under one `model_type` — which is a statement about
/// the recipe catalog, not about this repository's filesystem. Its actual
/// knowledge is `recipe_variant`: what a recipe name says about the model. That
/// convention is built in `fern-cli-rs/src/presets.rs`, so the domain that
/// should own it does not exist as a crate yet; Pass 3 of the statistics
/// migration is what extracts it.
///
/// The list is explicit so that admitting a second lodger is a deliberate edit
/// with a name and a destination, not a drift.
///
/// CHG 2026-08-26: it is EMPTY, and that is the resolution rather than an
/// oversight. Both lodgers — `model_name_collisions` and `model_variants` —
/// were waiting for a domain crate that never arrived. They went back to
/// Python instead (scripts/check_model_name_collisions.py and
/// scripts/register_model_variants.py, which still owns the writing half), so
/// the question "where does this belong" is answered and the drawer is shut.
pub const LODGERS: [(&str, &str); 0] = [];

#[cfg(test)]
mod tests {
    use super::*;

    /// Every file here answers the module's question or is a named lodger.
    ///
    /// No size threshold. What decides is the question answered and whether the
    /// thing has a home.
    #[test]
    fn every_file_answers_the_home_question_or_is_a_declared_lodger() {
        let source = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("src/repo_health");
        let mut modules: Vec<String> = std::fs::read_dir(&source)
            .unwrap()
            .flatten()
            .filter_map(|entry| {
                let path = entry.path();
                let stem = path.file_stem()?.to_str()?.to_string();
                (path.extension()?.to_str()? == "rs" && stem != "mod").then_some(stem)
            })
            .collect();
        modules.sort();
        let at_home = ["cli_defaults"];
        let mut expected: Vec<String> = at_home
            .iter()
            .map(|name| name.to_string())
            .chain(LODGERS.iter().map(|(name, _)| name.to_string()))
            .collect();
        expected.sort();
        assert_eq!(
            modules, expected,
            "a file here must answer {HOME_QUESTION:?}, or be listed in LODGERS with where it belongs"
        );
        for (name, destination) in LODGERS {
            assert!(
                !destination.is_empty(),
                "{name} lodges here without saying where it should live, which is how a drawer starts"
            );
        }
    }
}
