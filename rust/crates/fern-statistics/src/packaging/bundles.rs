//! What each upload contains, declared once.
//!
//! These lists are transcribed MECHANICALLY from the shell scripts they
//! replace, not retyped: a hand-copied path is a file the reviewer silently
//! does not receive. The build machinery is in `lib.rs`; this file is data.

use super::{Bundle, Copy, FlatFiles, PdfPolicy, Rewrite, SourceTree, TreeRewrite};

/// The Technical Supplement upload: the appendix PDF, its source, and the
/// machine-readable evidence every number in it comes from.
///
/// A SEPARATE upload from the main paper and from the reproducibility
/// checklist. The main paper stands on its own and reviewers are not required
/// to read this, so it carries no claim the paper depends on — only the fuller
/// tables, the derivations, and the artifacts a reader can check against.
pub const SUPPLEMENT: Bundle = Bundle {
    name: "supplement",
    purpose: "the technical supplement a reviewer may read, and the evidence under it",
    out: "paper/supplement",
    archive: "paper/supplement.zip",
    copies: &[
        Copy {
            target: "supplement.pdf",
            source: "paper/appendix.pdf",
        },
        Copy {
            target: "source/appendix.tex",
            source: "paper/appendix.tex",
        },
        Copy {
            target: "source/supplement_tables.tex",
            source: "results/paper_campaign/supplement_tables.tex",
        },
        Copy {
            target: "source/main_table.tex",
            source: "results/paper_campaign/main_table.tex",
        },
        Copy {
            target: "source/refs.bib",
            source: "paper/refs.bib",
        },
        Copy {
            target: "source/appendix.bbl",
            source: "paper/appendix.bbl",
        },
        Copy {
            target: "source/aaai2027.sty",
            source: "paper/aaai2027.sty",
        },
        Copy {
            target: "source/aaai2027.bst",
            source: "paper/aaai2027.bst",
        },
        Copy {
            target: "source/figures/fig2_adaptation_gap.pdf",
            source: "paper/figures/fig2_adaptation_gap.pdf",
        },
        Copy {
            target: "evidence/hard_switch_adaptation.json",
            source: "results/paper_campaign/hard_switch_adaptation.json",
        },
        Copy {
            target: "evidence/two_dial_interaction.json",
            source: "results/paper_campaign/two_dial_interaction.json",
        },
        Copy {
            target: "evidence/interval_coverage_simulation.json",
            source: "results/paper_campaign/interval_coverage_simulation.json",
        },
        Copy {
            target: "evidence/correlated_clean_target.json",
            source: "results/paper_campaign/correlated_clean_target.json",
        },
        Copy {
            target: "evidence/contamination_response.json",
            source: "results/paper_campaign/contamination_response.json",
        },
        Copy {
            target: "evidence/three_route_receipts.json",
            source: "results/paper_campaign/three_route_receipts.json",
        },
        Copy {
            target: "evidence/clean_target_response.json",
            source: "results/paper_campaign/clean_target_response.json",
        },
        Copy {
            target: "evidence/clean_target_ept.json",
            source: "results/paper_campaign/clean_target_ept.json",
        },
        Copy {
            target: "evidence/main_table.json",
            source: "results/paper_campaign/main_table.json",
        },
        Copy {
            target: "evidence/metric_disagreement.json",
            source: "results/paper_campaign/metric_disagreement.json",
        },
        Copy {
            target: "evidence/probabilistic_statistics.json",
            source: "results/paper_campaign/probabilistic_statistics.json",
        },
        Copy {
            target: "evidence/real_sensor_statistics.json",
            source: "results/paper_campaign/real_sensor_statistics.json",
        },
        Copy {
            target: "evidence/holdout_confirmation.json",
            source: "results/paper_campaign/holdout_confirmation.json",
        },
        Copy {
            target: "evidence/random_initial_condition_confirmation.json",
            source: "results/paper_campaign/random_initial_condition_confirmation.json",
        },
        Copy {
            target: "evidence/kalman_oracle.json",
            source: "results/paper_campaign/kalman_oracle.json",
        },
        Copy {
            target: "evidence/trivial_baselines.json",
            source: "results/paper_campaign/trivial_baselines.json",
        },
        Copy {
            target: "evidence/classical_baselines.json",
            source: "results/paper_campaign/classical_baselines.json",
        },
        Copy {
            target: "evidence/training_seed_sensitivity.json",
            source: "results/paper_campaign/training_seed_sensitivity.json",
        },
        Copy {
            target: "evidence/margin_erosion.json",
            source: "results/paper_campaign/margin_erosion.json",
        },
        Copy {
            target: "evidence/realized_energy.json",
            source: "results/paper_campaign/realized_energy.json",
        },
        Copy {
            target: "evidence/interval_width_accounting.json",
            source: "results/paper_campaign/interval_width_accounting.json",
        },
        Copy {
            target: "evidence/floor_removed_spread.json",
            source: "results/paper_campaign/floor_removed_spread.json",
        },
        Copy {
            target: "evidence/full_matrix.tsv",
            source: "results/paper_campaign/full_matrix.tsv",
        },
        Copy {
            target: "evidence/numbers_manifest.tsv",
            source: "results/paper_campaign/numbers_manifest.tsv",
        },
        Copy {
            target: "evidence/numbers_manifest_v2.tsv",
            source: "results/paper_campaign/numbers_manifest_v2.tsv",
        },
    ],
    trees: &[],
    flat_files: &[],
    // The appendix `\input{}`s tables that live under results/ in the
    // repository; inside the bundle every input sits alongside appendix.tex.
    rewrites: &[
        Rewrite {
            file: "source/appendix.tex",
            from: "{../results/paper_campaign/supplement_tables.tex}",
            to: "{supplement_tables}",
        },
        Rewrite {
            file: "source/appendix.tex",
            from: "{../results/paper_campaign/main_table.tex}",
            to: "{main_table}",
        },
    ],
    tree_rewrites: &[],
    pdf_policy: None,
    max_archive_bytes: None,
};

/// The anonymous paper submission: paper, checklist, compilable source, the
/// four figures, exact tables, claim-level evidence, and promised code.
pub const AAAI_SUBMIT: Bundle = Bundle {
    name: "aaai-submit",
    purpose: "the anonymous paper submission and the code/evidence promised by its checklist",
    out: "paper/aaai_submit",
    archive: "paper/aaai_submit.zip",
    copies: &[
        Copy {
            target: "main.pdf",
            source: "paper/main.pdf",
        },
        Copy {
            target: "ReproducibilityChecklist.pdf",
            source: "paper/ReproducibilityChecklist.pdf",
        },
        Copy {
            target: "source/main.tex",
            source: "paper/main.tex",
        },
        Copy {
            target: "source/refs.bib",
            source: "paper/refs.bib",
        },
        Copy {
            target: "source/main.bbl",
            source: "paper/main.bbl",
        },
        Copy {
            target: "source/aaai2027.sty",
            source: "paper/aaai2027.sty",
        },
        Copy {
            target: "source/aaai2027.bst",
            source: "paper/aaai2027.bst",
        },
        Copy {
            target: "source/main_table_compact.tex",
            source: "results/paper_campaign/main_table_compact.tex",
        },
        Copy {
            target: "source/uncertainty_table.tex",
            source: "results/paper_campaign/uncertainty_table.tex",
        },
        Copy {
            target: "source/figures/fig1_measurement_ledger.pdf",
            source: "paper/figures/fig1_measurement_ledger.pdf",
        },
        Copy {
            target: "source/figures/fig2_adaptation_gap.pdf",
            source: "paper/figures/fig2_adaptation_gap.pdf",
        },
        Copy {
            target: "source/figures/fig3_reversal_fixed_path.pdf",
            source: "paper/figures/fig3_reversal_fixed_path.pdf",
        },
        Copy {
            target: "source/figures/fig4_foundation_utility.pdf",
            source: "paper/figures/fig4_foundation_utility.pdf",
        },
        Copy {
            target: "evidence/main_table.json",
            source: "results/paper_campaign/main_table.json",
        },
        Copy {
            target: "evidence/clean_target_response.json",
            source: "results/paper_campaign/clean_target_response.json",
        },
        Copy {
            target: "evidence/hard_switch_adaptation.json",
            source: "results/paper_campaign/hard_switch_adaptation.json",
        },
        Copy {
            target: "evidence/holdout_confirmation.json",
            source: "results/paper_campaign/holdout_confirmation.json",
        },
        Copy {
            target: "evidence/metric_disagreement.json",
            source: "results/paper_campaign/metric_disagreement.json",
        },
        Copy {
            target: "evidence/real_sensor_statistics.json",
            source: "results/paper_campaign/real_sensor_statistics.json",
        },
        Copy {
            target: "evidence/two_dial_interaction.json",
            source: "results/paper_campaign/two_dial_interaction.json",
        },
        Copy {
            target: "evidence/interval_coverage_simulation.json",
            source: "results/paper_campaign/interval_coverage_simulation.json",
        },
        Copy {
            target: "evidence/three_route_receipts.json",
            source: "results/paper_campaign/three_route_receipts.json",
        },
        Copy {
            target: "evidence/clean_target_ept.json",
            source: "results/paper_campaign/clean_target_ept.json",
        },
        Copy {
            target: "code/README.md",
            source: "scripts/code_release_readme.md",
        },
        Copy {
            target: "code/requirements.txt",
            source: "scripts/code_release_requirements.txt",
        },
    ],
    trees: &[
        SourceTree {
            source: "serie",
            target: "code/serie",
            extensions: &["py", "rs", "toml"],
            remove_named: &["pyproject.toml", "pyvenv.cfg"],
            skip_directories: &[".venv", "assets", "demo", "tmp", "__pycache__"],
        },
        SourceTree {
            source: "fern-cli-rs/src",
            target: "code/fern-cli-rs",
            extensions: &["py", "rs", "toml"],
            remove_named: &[],
            skip_directories: &["target", "__pycache__"],
        },
        SourceTree {
            source: "retired/serie-rs/src",
            target: "code/serie-rs",
            extensions: &["py", "rs", "toml"],
            remove_named: &[],
            skip_directories: &["target", "__pycache__"],
        },
        SourceTree {
            source: "fern_run/src",
            target: "code/fern_run",
            extensions: &["py", "rs", "toml"],
            remove_named: &[],
            skip_directories: &["target", "__pycache__"],
        },
        SourceTree {
            source: "crates/fern-statistics/src",
            target: "code/fern-statistics",
            extensions: &["rs"],
            remove_named: &[],
            skip_directories: &["target"],
        },
        SourceTree {
            source: "crates/fern-json-contract/src",
            target: "code/fern-json-contract",
            extensions: &["rs"],
            remove_named: &[],
            skip_directories: &["target"],
        },
        SourceTree {
            source: "analysis_declarations",
            target: "code/analysis_declarations",
            extensions: &["json"],
            remove_named: &[],
            skip_directories: &[],
        },
        SourceTree {
            source: "python/fern_analysis",
            target: "code/fern_analysis",
            extensions: &["py"],
            remove_named: &[],
            skip_directories: &["__pycache__"],
        },
    ],
    flat_files: &[
        FlatFiles {
            source: ".",
            target: "code/analysis",
            prefixes: &["fr_"],
            suffixes: &[".py"],
            exact_names: &[],
            exclude_names: &[],
            exclude_prefixes: &[],
            exclude_contains: &[],
            required: true,
        },
        FlatFiles {
            source: "scripts",
            target: "code/scripts",
            prefixes: &[],
            suffixes: &[".py"],
            exact_names: &[],
            exclude_names: &[],
            exclude_prefixes: &[],
            exclude_contains: &[],
            required: true,
        },
    ],
    rewrites: &[Rewrite {
        file: "source/main.tex",
        from: "../results/paper_campaign/main_table_compact.tex",
        to: "main_table_compact.tex",
    }],
    tree_rewrites: &[
        TreeRewrite {
            target: "code",
            extensions: &["py", "sh", "rs", "toml", "md", "txt"],
            from: "/home/user/study",
            to: "/path/to/study",
        },
        TreeRewrite {
            target: "code",
            extensions: &["py", "sh", "rs", "toml", "md", "txt"],
            from: "/home/user",
            to: "/path/to/home",
        },
    ],
    pdf_policy: Some(PdfPolicy {
        file: "main.pdf",
        max_pages: 9,
        max_references_page: 8,
    }),
    max_archive_bytes: None,
};

/// The code-and-data field: source plus compact machine-readable evidence,
/// deliberately excluding live databases and retained tensors.
pub const CODE_AND_DATA: Bundle = Bundle {
    name: "code-and-data",
    purpose: "the standalone code-and-data supplement, capped at 50 MB",
    out: "paper/code_and_data",
    archive: "paper/code_and_data.zip",
    copies: &[
        Copy {
            target: "README.md",
            source: "scripts/code_release_readme.md",
        },
        Copy {
            target: "requirements.txt",
            source: "scripts/code_release_requirements.txt",
        },
        Copy {
            target: "data/world_statistics.json",
            source: "results/paper_campaign/world_statistics/world_statistics.json",
        },
    ],
    trees: &[
        SourceTree {
            source: "serie",
            target: "code/serie",
            extensions: &["py", "rs", "toml"],
            remove_named: &["pyproject.toml", "pyvenv.cfg", "Cargo.toml"],
            skip_directories: &[".venv", "assets", "demo", "tmp", "__pycache__"],
        },
        SourceTree {
            source: "retired/serie-rs",
            target: "code/serie-rs",
            extensions: &["py", "rs", "toml"],
            remove_named: &["pyproject.toml", "Cargo.toml"],
            skip_directories: &["target", "__pycache__"],
        },
        SourceTree {
            source: "fern-cli-rs/src",
            target: "code/fern-cli-rs",
            extensions: &["py", "rs", "toml"],
            remove_named: &["pyproject.toml", "Cargo.toml"],
            skip_directories: &["target", "__pycache__"],
        },
        SourceTree {
            source: "fern_run/src",
            target: "code/fern_run",
            extensions: &["py", "rs", "toml"],
            remove_named: &["pyproject.toml", "Cargo.toml"],
            skip_directories: &["target", "__pycache__"],
        },
        SourceTree {
            source: "crates/fern-statistics/src",
            target: "code/fern-statistics",
            extensions: &["rs"],
            remove_named: &[],
            skip_directories: &["target"],
        },
        SourceTree {
            source: "crates/fern-json-contract/src",
            target: "code/fern-json-contract",
            extensions: &["rs"],
            remove_named: &[],
            skip_directories: &["target"],
        },
        SourceTree {
            source: "analysis_declarations",
            target: "code/analysis_declarations",
            extensions: &["json"],
            remove_named: &[],
            skip_directories: &[],
        },
        SourceTree {
            source: "python/fern_analysis",
            target: "code/fern_analysis",
            extensions: &["py"],
            remove_named: &[],
            skip_directories: &["__pycache__"],
        },
    ],
    flat_files: &[
        FlatFiles {
            source: ".",
            target: "code/analysis",
            prefixes: &["fr_"],
            suffixes: &[".py"],
            exact_names: &[],
            exclude_names: &[],
            exclude_prefixes: &[],
            exclude_contains: &[],
            required: true,
        },
        FlatFiles {
            source: "scripts",
            target: "code/checks",
            prefixes: &["check_", "make_"],
            suffixes: &[".py"],
            exact_names: &["three_route_receipts.py", "snapshot_campaign_databases.py"],
            exclude_names: &[],
            exclude_prefixes: &[],
            exclude_contains: &[],
            required: true,
        },
        FlatFiles {
            source: "results/paper_campaign",
            target: "data",
            prefixes: &[],
            suffixes: &[".json", ".tsv"],
            exact_names: &[],
            exclude_names: &["injection_audit.json"],
            exclude_prefixes: &["dispatch"],
            exclude_contains: &["readiness"],
            required: true,
        },
    ],
    rewrites: &[],
    tree_rewrites: &[
        TreeRewrite {
            target: "code",
            extensions: &["py", "sh", "rs", "toml", "md", "txt"],
            from: "/home/user/study",
            to: "/path/to/study",
        },
        TreeRewrite {
            target: "code",
            extensions: &["py", "sh", "rs", "toml", "md", "txt"],
            from: "/home/user",
            to: "/path/to/home",
        },
        TreeRewrite {
            target: "data",
            extensions: &["json", "tsv"],
            from: "/home/user/study",
            to: "/path/to/study",
        },
        TreeRewrite {
            target: "data",
            extensions: &["json", "tsv"],
            from: "/home/user",
            to: "/path/to/home",
        },
    ],
    pdf_policy: None,
    max_archive_bytes: Some(50_000_000),
};

/// The internal handoff reviewed by the other agents. Its untracked visual
/// studies are preserved from the existing handoff, while every tracked copy
/// below is overwritten from its named source of truth.
pub const AAAI_UPLOAD: Bundle = Bundle {
    name: "aaai-upload",
    purpose: "the internal review handoff; not the archive sent to AAAI",
    out: "paper/aaai_upload",
    archive: "paper/aaai_upload/aaai_upload.zip",
    copies: &[
        Copy {
            target: "PAPER_main.pdf",
            source: "paper/main.pdf",
        },
        Copy {
            target: "PAPER_appendix.pdf",
            source: "paper/appendix.pdf",
        },
        Copy {
            target: "FIG1_ledger_and_protocol.pdf",
            source: "figures_by_question/titration-ledger/titration_ledger.pdf",
        },
        Copy {
            target: "FIG2_adaptation_gap.pdf",
            source: "figures_by_question/adaptation-gap/adaptation_gap.pdf",
        },
        Copy {
            target: "FIG3_coverage_width_wis.pdf",
            source: "results/paper_campaign/world_statistics/fig_s5_foundation_quantiles_gaussian.pdf",
        },
        Copy {
            target: "CHECKLIST_ReproducibilityChecklist.pdf",
            source: "paper/ReproducibilityChecklist.pdf",
        },
        Copy {
            target: "CHECKLIST_ReproducibilityChecklist.tex",
            source: "paper/ReproducibilityChecklist.tex",
        },
        Copy {
            target: "16_main_table.tex",
            source: "results/paper_campaign/main_table_compact.tex",
        },
        Copy {
            target: "18_appendix.tex",
            source: "paper/appendix.tex",
        },
        Copy {
            target: "20_main.tex",
            source: "paper/main.tex",
        },
        Copy {
            target: "21_ai.tex",
            source: "paper/ai.tex",
        },
        Copy {
            target: "refs.bib",
            source: "paper/refs.bib",
        },
        Copy {
            target: "aaai2027.sty",
            source: "paper/aaai2027.sty",
        },
        Copy {
            target: "aaai2027.bst",
            source: "paper/aaai2027.bst",
        },
        Copy {
            target: "22_clean_target_response.json",
            source: "results/paper_campaign/clean_target_response.json",
        },
        Copy {
            target: "23_metric_disagreement.json",
            source: "results/paper_campaign/metric_disagreement.json",
        },
        Copy {
            target: "24_extended_lanes_statistics.json",
            source: "docs/campaign_statistics/extended_lanes_statistics.json",
        },
        Copy {
            target: "25_world_statistics.json",
            source: "results/paper_campaign/world_statistics/world_statistics.json",
        },
        Copy {
            target: "26_probabilistic_statistics.json",
            source: "results/paper_campaign/probabilistic_statistics.json",
        },
        Copy {
            target: "27_holdout_confirmation.json",
            source: "results/paper_campaign/holdout_confirmation.json",
        },
        Copy {
            target: "28_degradation_decomposition.json",
            source: "results/paper_campaign/degradation_decomposition.json",
        },
        Copy {
            target: "29_two_dial_interaction.json",
            source: "results/paper_campaign/two_dial_interaction.json",
        },
        Copy {
            target: "30_main_table.json",
            source: "results/paper_campaign/main_table.json",
        },
    ],
    trees: &[SourceTree {
        source: "paper/aaai_upload",
        target: "",
        extensions: &[],
        remove_named: &[
            "aaai_upload.zip",
            "SHA256SUMS.txt",
            "31_AAAI27_SUBMISSION_CHECKPOINT.md",
        ],
        skip_directories: &[],
    }],
    flat_files: &[],
    rewrites: &[],
    tree_rewrites: &[],
    pdf_policy: Some(PdfPolicy {
        file: "PAPER_main.pdf",
        max_pages: 9,
        max_references_page: 8,
    }),
    max_archive_bytes: None,
};

/// Every bundle this crate knows how to build.
pub const ALL: [&Bundle; 4] = [&SUPPLEMENT, &AAAI_SUBMIT, &AAAI_UPLOAD, &CODE_AND_DATA];
