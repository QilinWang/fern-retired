//! Every scientific protocol field must be selectable, not merely hashed.
//!
//! THE MISTAKE THIS PREVENTS. `ExperimentProtocolIdentity` is documented as the
//! set of fields that "must change `run_hash` when their meaning changes", and
//! it does that job: two protocols at one cell get different run hashes and can
//! never be confused as runs. Nobody asked the second question -- how does a
//! person ask for one of them? A hash prevents a collision; it cannot answer
//! "give me every gaussian row". So the fields lived only inside
//! `invocation_json`, fifteen analysis scripts each parsed that JSON by hand,
//! and the ones that forgot silently reported another estimand's numbers.
//!
//! Measured 2026-08-04 before the repair: 4,071 distinct
//! (dataset, model, dose, trajectory, bank) coordinates, of which 933 held more
//! than one protocol. A reader keying on the columns alone kept whichever row it
//! read last -- which is how a headline figure printed a dose crossing that does
//! not exist.
//!
//! THE RULE, and it is mechanical: a field that enters run identity AND names a
//! scientific choice must also be a column. This test enumerates the fields and
//! fails on any that is neither a column nor explicitly declared unselectable
//! with a reason. Adding a protocol field therefore forces the decision at the
//! moment it is added, instead of three weeks later in someone's analysis.

use std::collections::BTreeSet;

/// Fields of `ExperimentProtocolIdentity`, as they appear in the packet.
/// Kept here rather than derived by reflection so that adding a field to the
/// struct without adding it here fails the round-trip check below.
const PROTOCOL_FIELDS: &[&str] = &[
    "schema",
    "truth_kind",
    "noise_placement",
    "noise_law",
    "noise_ar1_phi",
    "relative_dose",
    "split_treatment_scheme",
    "checkpoint_execution",
    "noise_bank_seed",
    "training_scale_hash",
    "training_scale_hash_kind",
    "training_scale_preimage",
    "checkpoint_revision",
    "quantile_crossing_policy",
];

/// Protocol field -> the `runs` column that makes it selectable.
const SELECTABLE_AS_COLUMN: &[(&str, &str)] = &[
    ("noise_law", "noise_law"),
    ("split_treatment_scheme", "split_treatment_scheme"),
    ("noise_ar1_phi", "noise_ar1_phi"),
    ("relative_dose", "relative_dose"),
    ("checkpoint_execution", "checkpoint_execution_mode"),
];

/// Protocol field -> why nobody needs to select rows by it. Each of these was
/// checked against the campaign: none of them ever distinguishes two rows that
/// share a (dataset, model, dose, trajectory, bank) coordinate, so promoting it
/// would add a column that no query would use.
const NOT_SELECTABLE: &[(&str, &str)] = &[
    ("schema", "the packet's own version tag, identical for every row"),
    ("noise_placement", "one value across the whole campaign"),
    (
        "truth_kind",
        "determined by the world, which is already the `dataset` column",
    ),
    (
        "quantile_crossing_policy",
        "determined by the model, which is already the `model_type` column",
    ),
    (
        "noise_bank_seed",
        "the bank ORDINAL is what analyses group by, and it is already a column",
    ),
    (
        "training_scale_hash",
        "a derived digest; relative_dose is the choice a human makes",
    ),
    ("training_scale_hash_kind", "follows relative_dose one-for-one"),
    ("training_scale_preimage", "the derivation behind that digest"),
    (
        "checkpoint_revision",
        "provenance of a reused checkpoint, not a slice anyone selects",
    ),
];

/// The same rule applied to WHERE and HOW a run executed, which lives in the
/// resolved config rather than the protocol. Only the fields that actually vary
/// are here: measured across 8,148 rows, `runtime.device` takes two values and
/// `execution.kind` two, while dtype, amp_dtype and compile take one each. A
/// constant cannot distinguish two rows, so promoting it would add a column no
/// query would ever use.
const EXECUTION_SELECTABLE_AS_COLUMN: &[(&str, &str)] = &[
    ("runtime.device", "runtime_device"),
    ("execution.kind", "execution_kind"),
];

fn runs_columns() -> BTreeSet<String> {
    let schema = std::fs::read_to_string(
        std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .expect("workspace root")
            .join("retired/fern_run/schema_diesel_2026_08_28.rs"),
    )
    .expect("retired Diesel runs table is readable");
    let table = schema
        .split("runs (id) {")
        .nth(1)
        .expect("the retired Diesel schema declares the runs table");
    let body = table.split("\n    }").next().expect("the table body ends");
    body.lines()
        .filter_map(|line| line.split("->").next())
        .map(|name| name.trim().trim_start_matches("//").trim().to_string())
        .filter(|name| !name.is_empty() && !name.contains(' '))
        .collect()
}

#[test]
fn every_protocol_field_is_either_a_column_or_declared_unselectable() {
    let declared: BTreeSet<&str> = SELECTABLE_AS_COLUMN
        .iter()
        .map(|(field, _)| *field)
        .chain(NOT_SELECTABLE.iter().map(|(field, _)| *field))
        .collect();
    let undecided: Vec<&&str> = PROTOCOL_FIELDS
        .iter()
        .filter(|field| !declared.contains(**field))
        .collect();
    assert!(
        undecided.is_empty(),
        "protocol field(s) {undecided:?} are hashed but nobody decided whether a \
         person can select rows by them. Add a column and list it in \
         SELECTABLE_AS_COLUMN, or state in NOT_SELECTABLE why no query needs it. \
         A hash keeps two protocols from colliding; only a column lets someone \
         ask for one of them."
    );
}

#[test]
fn each_selectable_protocol_field_really_has_its_column() {
    let columns = runs_columns();
    for (field, column) in SELECTABLE_AS_COLUMN.iter().chain(EXECUTION_SELECTABLE_AS_COLUMN) {
        assert!(
            columns.contains(*column),
            "field {field:?} claims to be selectable as runs.{column}, \
             which is not in the retired Diesel runs table"
        );
    }
}

#[test]
fn no_field_is_both_selectable_and_declared_unselectable() {
    for (field, _) in SELECTABLE_AS_COLUMN {
        assert!(
            !NOT_SELECTABLE.iter().any(|(other, _)| other == field),
            "protocol field {field:?} appears in both lists, so this test would \
             pass whichever answer is wrong"
        );
    }
}

#[test]
fn every_reason_for_not_promoting_a_field_is_a_reason() {
    for (field, reason) in NOT_SELECTABLE {
        assert!(
            reason.len() >= 25,
            "protocol field {field:?} is excluded from the columns with the \
             note {reason:?}, which is too short to review"
        );
    }
}

#[test]
fn the_field_list_matches_the_struct() {
    // The list above is hand-maintained, so it can rot. This reads the struct
    // and fails when the two disagree in either direction: a new field nobody
    // classified, or a stale entry for a field that no longer exists.
    let source = std::fs::read_to_string(std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("src/plan_run.rs"))
        .expect("plan_run.rs is readable");
    let body = source
        .split("pub(crate) struct ExperimentProtocolIdentity {")
        .nth(1)
        .expect("the struct is declared")
        .split("\n}")
        .next()
        .expect("the struct body ends");

    let in_struct: BTreeSet<String> = body
        .lines()
        .map(str::trim)
        .filter(|line| !line.starts_with("//") && !line.starts_with('#') && line.contains(':'))
        .filter_map(|line| line.split(':').next())
        .map(|name| name.trim().to_string())
        .filter(|name| !name.is_empty() && !name.contains(' '))
        .collect();
    let listed: BTreeSet<String> = PROTOCOL_FIELDS.iter().map(|f| f.to_string()).collect();

    assert_eq!(
        in_struct, listed,
        "ExperimentProtocolIdentity and PROTOCOL_FIELDS disagree. Every field of \
         that struct needs a decision here: a column, or a stated reason that no \
         query needs it."
    );
}
