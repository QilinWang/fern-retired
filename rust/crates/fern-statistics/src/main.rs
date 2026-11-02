//! `fern-stat` — the campaign's statistics surface.
//!
//! Separate from `fern-run` because the two answer different questions.
//! `fern-run` decides what computes and who owns a GPU; this decides whether
//! the numbers the paper states still match the data underneath them. Keeping
//! them in one crate is how 5,734 lines of analysis ended up inside the
//! launcher.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::process::ExitCode;

use clap::{Parser, Subcommand};
use fern_statistics::refresh::{COMPUTE_DRIFT_HELP, STALE_ARTIFACT_HELP, campaign_refresh_chain};
use fern_statistics::{OnFailure, StepOutcome, chain_is_trustworthy, run_chain};

#[derive(Parser)]
#[command(name = "fern-stat", about = "Recompute and check every number the paper cites")]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    /// Freeze one scientific declaration into the exact request Python receives.
    DeclarationFreezeRequest(DeclarationFreezeRequest),
    /// Compute one checked-in research statistic and admit it through Registry Home.
    ResearchComputeStatistic(ResearchComputeStatistic),
    /// List research statistics that have checked-in declarations.
    ResearchListStatistics(ResearchListStatistics),
    /// Recompute every statistic in dependency order and check the results.
    Refresh(Refresh),
    /// Build one upload bundle from its declared manifest.
    Pack(Pack),
    // Moved here from `fern-run` on 2026-08-26. These compute a statistic and
    // admit it; they never launch or materialize anything, so they belong with
    // the statistics binary rather than the runner.
    /// Compute titration summary metrics for one recipe's done runs and write one analyses row.
    RecipeComputeTitrationStatistics(fern_statistics::titration_statistics::ComputeRecipeTitrationStatistics),
    /// Compute titration summary metrics for one campaign's done runs and write one analyses row.
    CampaignComputeTitrationStatistics(fern_statistics::titration_statistics::ComputeCampaignTitrationStatistics),
    /// Select HPD-calibration runs; refuse until durable ScientificCase, cell, and forecast bindings exist.
    RunSetComputeIntervalCalibration(fern_statistics::run_set_interval_calibration::ComputeRunSetIntervalCalibration),
}

#[derive(clap::Args)]
struct Refresh {
    /// Interpreter that runs the statistics layers.
    #[arg(long)]
    python: Option<PathBuf>,
    /// Digest-fixed Registry Home binary that admits checked analyses.
    #[arg(long)]
    fern_home: PathBuf,
    /// C# Registry binary (fern-registry) that checks lab citations against Registry Home.
    #[arg(long)]
    fern_registry: PathBuf,
    /// Repository root. Every step runs with this as its working directory.
    #[arg(long, default_value = ".")]
    root: PathBuf,
    /// Print the chain and exit without running anything.
    #[arg(long)]
    dry_run: bool,
    /// Emit the per-step report as JSON instead of a table.
    #[arg(long)]
    json: bool,
    /// Run only the steps whose program or description contains this text, for
    /// re-running one step after fixing it. Never silent: the run says how many
    /// steps it skipped, so a filtered run cannot be read as a full refresh.
    #[arg(long)]
    only: Option<String>,
}

fn main() -> ExitCode {
    let cli = Cli::parse();
    match cli.command {
        Command::DeclarationFreezeRequest(args) => declaration_freeze_request(args),
        Command::ResearchComputeStatistic(args) => research_compute_statistic(args),
        Command::ResearchListStatistics(args) => research_list_statistics(args),
        Command::Refresh(args) => refresh(args),
        Command::RecipeComputeTitrationStatistics(args) => reported(
            "recipe-compute-titration-statistics",
            fern_statistics::titration_statistics::try_compute_recipe_titration_statistics(args, recipe_hash_by_name),
        ),
        Command::CampaignComputeTitrationStatistics(args) => reported(
            "campaign-compute-titration-statistics",
            fern_statistics::titration_statistics::try_compute_campaign_titration_statistics(args),
        ),
        Command::RunSetComputeIntervalCalibration(args) => reported(
            "run-set-compute-interval-calibration",
            fern_statistics::run_set_interval_calibration::try_compute_run_set_interval_calibration(
                args,
                recipe_hash_by_name,
            ),
        ),
        Command::Pack(args) => pack(args),
    }
}

/// Report a moved command's outcome the way `fern-run` did: the name, then the
/// error, then a failing exit code.
fn reported(command: &str, outcome: fern_statistics::Fallible<()>) -> ExitCode {
    match outcome {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("{command} failed: {error}");
            ExitCode::FAILURE
        }
    }
}

/// The recipe identity, from the C# catalog authority.
///
/// The receipt is selected from C#'s complete validated catalog, rather than
/// rebuilding the Rust catalog or re-hashing a local identity preimage.
fn recipe_hash_by_name(name: &str) -> fern_statistics::Fallible<String> {
    fern_run::csharp_experiment::resolve_catalog_recipe_hash(name).map_err(fern_statistics::invalid_input)
}

#[derive(clap::Args)]
struct ResearchListStatistics {
    /// Repository root containing `analysis_declarations/`.
    #[arg(long, default_value = ".")]
    root: PathBuf,
}

/// One analysis a caller can run, and the implementations that declare it.
///
/// A declaration file is named `<kind>.json`, or `<kind>.<implementation>.json`
/// when the same analysis is written more than once — today
/// `quantile_forecast_summary` has a `python` and a `rust` implementation. The
/// KIND is the thing a caller names; the implementation is a flag, because
/// asking for "the quantile forecast summary" is the question and choosing
/// which code computes it is a second, separate one.
#[derive(Debug)]
struct DeclaredStatistic {
    kind: String,
    implementations: Vec<String>,
}

/// Read every declaration and group it by the analysis it implements.
///
/// Every file is validated on the way past. A directory holding one unreadable
/// declaration cannot answer "what can I run" honestly, so it refuses rather
/// than listing the rest and leaving the caller to wonder what was skipped.
fn declared_statistics(root: &Path) -> Result<Vec<DeclaredStatistic>, String> {
    let directory = root.join("analysis_declarations");
    let entries =
        std::fs::read_dir(&directory).map_err(|error| format!("analysis declarations cannot be opened: {error}"))?;
    let mut grouped: BTreeMap<String, Vec<String>> = BTreeMap::new();
    for entry in entries.flatten() {
        let path = entry.path();
        if path.extension().and_then(|value| value.to_str()) != Some("json") {
            continue;
        }
        let Some(stem) = path.file_stem().and_then(|value| value.to_str()) else {
            continue;
        };
        let bytes = std::fs::read(&path).map_err(|error| format!("{} cannot be read: {error}", path.display()))?;
        let declaration =
            fern_json_contract::checked_from_slice::<fern_statistics::analysis_contract::AnalysisDeclarationV1>(&bytes)
                .map_err(|error| format!("{} is not a valid analysis declaration: {error}", path.display()))?;
        declaration
            .validate()
            .map_err(|error| format!("{} is not a valid analysis declaration: {error}", path.display()))?;

        // The DECLARATION names its implementation; the filename only has to
        // agree. Deriving the name from the filename let a rename silently
        // change which implementation answered — the file could say `rust`
        // while the declaration said `python` and nothing noticed. The suffix
        // is now a readable index into the declaration, and a disagreement is
        // refused rather than resolved in the filename's favour.
        let (kind, suffix) = match stem.split_once('.') {
            Some((kind, suffix)) => (kind, Some(suffix.replace('_', "-"))),
            None => (stem, None),
        };
        let declared = declaration.implementation.clone();
        if declared != suffix {
            return Err(match (declared, suffix) {
                (Some(declared), Some(suffix)) => format!(
                    "{} declares implementation {declared:?} but its filename says {suffix:?}; whichever is wrong is answering as the other",
                    path.display()
                ),
                (Some(declared), None) => format!(
                    "{} declares implementation {declared:?} but its filename carries no suffix; name it <kind>.{declared}.json",
                    path.display()
                ),
                (None, Some(suffix)) => format!(
                    "{} is named as implementation {suffix:?} but declares no `implementation` field; add it",
                    path.display()
                ),
                (None, None) => unreachable!("equal values cannot reach this branch"),
            });
        }
        let entry = grouped.entry(kind.replace('_', "-")).or_default();
        if let Some(suffix) = suffix {
            entry.push(suffix);
        }
    }
    Ok(grouped
        .into_iter()
        .map(|(kind, mut implementations)| {
            implementations.sort();
            DeclaredStatistic { kind, implementations }
        })
        .collect())
}

fn research_list_statistics(args: ResearchListStatistics) -> ExitCode {
    let declared = match declared_statistics(&args.root) {
        Ok(declared) => declared,
        Err(error) => {
            eprintln!("{error}");
            return ExitCode::FAILURE;
        }
    };
    // Every line is a name `research-compute-statistic` accepts. It used to print file stems,
    // so `quantile-forecast-summary.python` was advertised and then refused by
    // the compute command's own validator for containing a dot — a list of things that
    // do not work is worse than no list.
    for analysis in declared {
        match analysis.implementations.as_slice() {
            [] => println!("{}", analysis.kind),
            implementations => println!("{}  --implementation {}", analysis.kind, implementations.join("|")),
        }
    }
    ExitCode::SUCCESS
}

#[derive(clap::Args)]
struct ResearchComputeStatistic {
    /// Statistic name, for example `split-conformal`. `research-list-statistics` prints every name this accepts.
    name: String,
    /// Which implementation of that analysis to run, when more than one declares it. Required only then.
    #[arg(long)]
    implementation: Option<String>,
    #[arg(long, default_value = ".")]
    root: PathBuf,
    #[arg(long, default_value = "results/paper_campaign/lifecycle.db")]
    registry: PathBuf,
    #[arg(long)]
    python: Option<PathBuf>,
    /// Digest-fixed Registry Home binary; no PATH fallback is allowed.
    #[arg(long)]
    fern_home: PathBuf,
}

fn research_compute_statistic(args: ResearchComputeStatistic) -> ExitCode {
    if args.name.is_empty()
        || !args
            .name
            .bytes()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'-')
    {
        eprintln!(
            "statistic name must contain only lowercase letters, digits, and hyphens; the implementation goes in --implementation, not in the name"
        );
        return ExitCode::FAILURE;
    }
    let file_stem = match declaration_stem(&args.root, &args.name, args.implementation.as_deref()) {
        Ok(stem) => stem,
        Err(error) => {
            eprintln!("{error}");
            return ExitCode::FAILURE;
        }
    };
    let campaign = PathBuf::from("results/paper_campaign");
    compute_statistic(StatisticComputation {
        root: args.root,
        declaration: PathBuf::from("analysis_declarations").join(format!("{file_stem}.json")),
        request: campaign.join(format!("{file_stem}.request.json")),
        report: campaign.join(format!("{file_stem}.report.json")),
        receipt: campaign.join(format!("{file_stem}.checked.json")),
        registry: args.registry,
        python: args.python,
        fern_home: args.fern_home,
    })
}

/// Resolve one analysis name, and an optional implementation, to the exact
/// declaration file that runs it.
///
/// The refusals are the point. Naming an analysis that two implementations
/// declare used to produce `No such file or directory` against a path the
/// caller never typed, because the name was turned straight into a filename.
/// Now it names the analysis, says how many implementations declare it, and
/// lists them — the caller can act on that without reading the directory.
fn declaration_stem(root: &Path, name: &str, implementation: Option<&str>) -> Result<String, String> {
    let declared = declared_statistics(root)?;
    let Some(analysis) = declared.iter().find(|analysis| analysis.kind == name) else {
        let available = declared
            .iter()
            .map(|analysis| analysis.kind.as_str())
            .collect::<Vec<_>>()
            .join(", ");
        return Err(format!(
            "no research statistic is called {name:?}; `fern-stat research-list-statistics` prints every name this accepts: {available}"
        ));
    };
    let stem = |suffix: Option<&str>| match suffix {
        Some(suffix) => format!("{}.{}", name.replace('-', "_"), suffix.replace('-', "_")),
        None => name.replace('-', "_"),
    };
    match (implementation, analysis.implementations.as_slice()) {
        (None, []) => Ok(stem(None)),
        (None, [only]) => Ok(stem(Some(only))),
        (None, several) => Err(format!(
            "{name} is declared by {} implementations and none was named; pass --implementation {}",
            several.len(),
            several.join(" or --implementation ")
        )),
        (Some(chosen), several) if several.iter().any(|declared| declared == chosen) => Ok(stem(Some(chosen))),
        (Some(chosen), []) => Err(format!(
            "{name} has one implementation and it is not named {chosen:?}; drop --implementation"
        )),
        (Some(chosen), several) => Err(format!(
            "{name} has no implementation called {chosen:?}; it declares {}",
            several.join(", ")
        )),
    }
}

#[derive(clap::Args)]
struct StatisticComputation {
    #[arg(long, default_value = ".")]
    root: PathBuf,
    #[arg(long)]
    declaration: PathBuf,
    #[arg(long)]
    request: PathBuf,
    #[arg(long)]
    report: PathBuf,
    #[arg(long)]
    receipt: PathBuf,
    #[arg(long, default_value = "results/paper_campaign/lifecycle.db")]
    registry: PathBuf,
    #[arg(long)]
    python: Option<PathBuf>,
    /// Digest-fixed Registry Home binary; no PATH fallback is allowed.
    #[arg(long)]
    fern_home: PathBuf,
}

fn compute_statistic(args: StatisticComputation) -> ExitCode {
    let request = match fern_statistics::analysis_contract::declare_analysis(
        &args.root,
        &args.declaration,
        &args.request,
        &args.fern_home,
    ) {
        Ok(request) => request,
        Err(error) => {
            eprintln!("analysis declaration refused before Python started: {error}");
            return ExitCode::FAILURE;
        }
    };
    match fern_statistics::analysis_contract::execute_python_statistic(
        fern_statistics::analysis_contract::PythonStatisticExecution {
            root: args.root,
            request,
            request_path: args.request,
            report_path: args.report,
            receipt_path: args.receipt,
            registry_path: args.registry,
            python: args.python,
            fern_home: args.fern_home,
        },
    ) {
        Ok(receipt) => {
            println!(
                "research statistic {} admitted as {}",
                receipt.kind, receipt.analysis_hash
            );
            print_stated_knowledge_events(&receipt);
            ExitCode::SUCCESS
        }
        Err(error) => {
            eprintln!("research statistic refused after Python exited: {error}");
            ExitCode::FAILURE
        }
    }
}

#[derive(clap::Args)]
struct DeclarationFreezeRequest {
    #[arg(long, default_value = ".")]
    root: PathBuf,
    /// Typed scientific declaration written before computation.
    #[arg(long)]
    declaration: PathBuf,
    /// Exact request Python will receive.
    #[arg(long)]
    request: PathBuf,
    /// Digest-fixed Registry Home binary that checks accepted evidence freshness.
    #[arg(long)]
    fern_home: PathBuf,
}

fn declaration_freeze_request(args: DeclarationFreezeRequest) -> ExitCode {
    match fern_statistics::analysis_contract::declare_analysis(
        &args.root,
        &args.declaration,
        &args.request,
        &args.fern_home,
    ) {
        Ok(request) => {
            let rows = match &request.evidence {
                fern_statistics::analysis_contract::ResolvedAnalysisEvidenceV1::Artifacts { inputs } => {
                    format!("{} exact input artifact(s)", inputs.len())
                }
                fern_statistics::analysis_contract::ResolvedAnalysisEvidenceV1::AcceptedMatrix {
                    row_count, ..
                }
                | fern_statistics::analysis_contract::ResolvedAnalysisEvidenceV1::AcceptedMatrixWithArtifacts {
                    row_count,
                    ..
                } => {
                    format!("{row_count} exact evidence row(s)")
                }
            };
            println!(
                "froze {} from {}; request {}",
                request.kind,
                rows,
                args.request.display()
            );
            ExitCode::SUCCESS
        }
        Err(error) => {
            eprintln!("statistic declaration refused before computation: {error}");
            ExitCode::FAILURE
        }
    }
}

/// Say what the registration stated, in the words of what it actually did.
///
/// The Card Update no longer happens here: registering the analysis states one
/// immutable event per named evidence path, and `fern-card reconcile-events`
/// writes the Update afterwards. Printing "attached" would claim a Card write
/// this command did not perform.
fn print_stated_knowledge_events(receipt: &fern_statistics::analysis_contract::CheckedAnalysisReceiptV1) {
    for event in &receipt.knowledge_events {
        println!(
            "{} projection {} for {} ({})",
            receipt.analysis_hash, event.status, event.design_ref, event.event_id
        );
    }
    if !receipt.knowledge_events.is_empty() {
        println!(
            "run `fern-card reconcile-events` to write the Card Updates, or `fern-home list-knowledge-events --selection pending` to see what is waiting"
        );
    }
}

#[derive(clap::Args)]
struct Pack {
    /// Which bundle to build.
    #[arg(long)]
    bundle: String,
    /// Repository root.
    #[arg(long, default_value = ".")]
    root: PathBuf,
    /// List the bundles this knows how to build and exit.
    #[arg(long)]
    list: bool,
}

fn pack(args: Pack) -> ExitCode {
    use fern_statistics::packaging::bundles::ALL;
    if args.list {
        for bundle in ALL {
            println!("  {:<14} {}", bundle.name, bundle.purpose);
        }
        return ExitCode::SUCCESS;
    }
    let Some(bundle) = ALL.iter().find(|bundle| bundle.name == args.bundle) else {
        eprintln!(
            "unknown bundle {:?}; known: {}",
            args.bundle,
            ALL.iter().map(|b| b.name).collect::<Vec<_>>().join(", ")
        );
        return ExitCode::FAILURE;
    };
    match fern_statistics::packaging::build(bundle, &args.root) {
        Ok(report) => {
            for line in &report.lines {
                println!("{line}");
            }
            println!(
                "\n{}: {} files, {:.1} KiB archive",
                bundle.name,
                report.files,
                report.archive_bytes as f64 / 1024.0
            );
            ExitCode::SUCCESS
        }
        Err(error) => {
            eprintln!("{error}");
            eprintln!("nothing was written; the bundle already in place is untouched");
            ExitCode::FAILURE
        }
    }
}

/// `%Y-%m-%dT%H:%M:%S` in local time, the stamp the memory file records.
fn now_stamp() -> String {
    let output = std::process::Command::new("date")
        .arg("+%Y-%m-%dT%H:%M:%S")
        .output()
        .ok();
    output
        .and_then(|out| String::from_utf8(out.stdout).ok())
        .map(|text| text.trim().to_string())
        .unwrap_or_default()
}

fn refresh(args: Refresh) -> ExitCode {
    let executable = match std::env::current_exe() {
        Ok(path) => path,
        Err(error) => {
            eprintln!("cannot locate fern-stat for the declared analysis steps: {error}");
            return ExitCode::FAILURE;
        }
    };
    let python = match fern_statistics::python_interpreter(args.python.as_deref()) {
        Ok(path) => path,
        Err(error) => {
            eprintln!("{error}");
            return ExitCode::FAILURE;
        }
    };
    let mut steps = campaign_refresh_chain(
        &python.to_string_lossy(),
        &executable.to_string_lossy(),
        &args.fern_home.to_string_lossy(),
        &args.fern_registry.to_string_lossy(),
    );
    let declared = steps.len();
    if let Some(pattern) = &args.only {
        steps.retain(|step| step.executable_argv().join(" ").contains(pattern) || step.description.contains(pattern));
        if steps.is_empty() {
            eprintln!("--only {pattern} matched none of the {declared} steps");
            return ExitCode::FAILURE;
        }
    }
    if args.dry_run {
        println!("{} steps, in order:", steps.len());
        for step in &steps {
            let kind = if step.artifact.is_some() { "layer" } else { "check" };
            let gate = match step.on_failure {
                OnFailure::Stop => "fatal",
                OnFailure::Report => "advisory",
            };
            println!(
                "  {kind:<6} {gate:<9} {:<52} {}",
                step.executable_argv().join(" "),
                step.description
            );
        }
        return ExitCode::SUCCESS;
    }

    let reports = match run_chain(&steps, &args.root) {
        Ok(reports) => reports,
        Err(error) => {
            eprintln!("cannot run the refresh chain: {error}");
            return ExitCode::FAILURE;
        }
    };

    if args.json {
        match serde_json::to_string_pretty(&serde_json::json!({
            "schema": "fern.statistics_refresh.v1",
            "steps": reports,
        })) {
            Ok(text) => println!("{text}"),
            Err(error) => {
                eprintln!("cannot render the report: {error}");
                return ExitCode::FAILURE;
            }
        }
    } else {
        println!("{} steps", reports.len());
        println!("{}", "-".repeat(96));
        for report in &reports {
            let name = report.artifact.as_deref().unwrap_or(&report.program);
            println!(
                "  {:<9} {:>4}s  {:<48} {}",
                report.outcome.label(),
                report.seconds,
                name.rsplit('/').next().unwrap_or(name),
                report.description
            );
            if let Some(output) = &report.failure_output {
                for line in output.lines() {
                    println!("        {line}");
                }
            }
        }
        println!("{}", "-".repeat(96));
    }

    let advisory_failures: Vec<&str> = steps
        .iter()
        .zip(&reports)
        .filter(|(step, report)| report.outcome == StepOutcome::Failed && step.on_failure == OnFailure::Report)
        .map(|(_, report)| report.description)
        .collect();
    for description in &advisory_failures {
        eprintln!("reported, not fatal: {description}");
    }
    if reports
        .iter()
        .any(|r| r.outcome == StepOutcome::Failed && r.description.contains("code did not change"))
    {
        eprintln!("{COMPUTE_DRIFT_HELP}");
    }
    if reports
        .iter()
        .any(|r| r.outcome == StepOutcome::Failed && r.description.contains("newer than its data"))
    {
        eprintln!("{STALE_ARTIFACT_HELP}");
    }

    if chain_is_trustworthy(&steps, &reports) {
        let changed = reports.iter().filter(|r| r.outcome == StepOutcome::Changed).count();
        if let Some(pattern) = &args.only {
            // A filtered run proves nothing about the steps it did not run, and
            // the line it prints must not be mistakable for the one above.
            println!(
                "{} of {declared} steps run (--only {pattern}); {changed} artifact(s) moved. \
                 This is NOT a full refresh.",
                steps.len()
            );
        } else {
            println!("all layers current ({changed} artifact(s) moved)");
        }
        ExitCode::SUCCESS
    } else {
        eprintln!("the chain failed; its outputs are not current");
        ExitCode::FAILURE
    }
}

#[cfg(test)]
mod cli_hygiene {
    use clap::CommandFactory;

    /// An argument that selects the data, the machine, or the identity may not
    /// carry a default. See `repo_health::cli_defaults` for why, and for the
    /// three failures on 2026-08-12 that this is the mechanical half of.
    #[test]
    fn no_scope_bearing_argument_carries_a_default() {
        let command = super::Cli::command();
        let found = fern_statistics::repo_health::cli_defaults::defaulted_scope_arguments(&command);
        assert!(
            found.is_empty(),
            "{}",
            fern_statistics::repo_health::cli_defaults::describe(&found)
        );
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    /// A scratch root holding real declarations copied from the repository.
    ///
    /// Real ones, because `declared_statistics` validates every file it reads
    /// and a hand-written stub would test the fixture rather than the contract.
    fn scratch_root(files: &[&str]) -> PathBuf {
        let nonce = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
        let root = std::env::temp_dir().join(format!("fern-stat-analyses-{}-{nonce}", std::process::id()));
        let declarations = root.join("analysis_declarations");
        std::fs::create_dir_all(&declarations).unwrap();
        // `env!` is fine HERE and nowhere near the binary: a test's working
        // directory is its crate, not the workspace, and this path is resolved
        // at test time on the machine that compiled it. Correction 281 is about
        // what a DEPLOYED binary must read, which is a different question.
        let repository = Path::new(env!("CARGO_MANIFEST_DIR")).join("../..");
        for file in files {
            std::fs::copy(
                repository.join("analysis_declarations").join(file),
                declarations.join(file),
            )
            .unwrap();
        }
        root
    }

    #[test]
    fn one_named_implementation_needs_no_flag_and_retired_ones_refuse() {
        let root = scratch_root(&["canary_selection.json", "quantile_forecast_summary.python.json"]);

        // A plain declaration resolves to its own file.
        assert_eq!(
            declaration_stem(&root, "canary-selection", None).unwrap(),
            "canary_selection"
        );

        // The sole active implementation resolves with or without its name.
        assert_eq!(
            declaration_stem(&root, "quantile-forecast-summary", None).unwrap(),
            "quantile_forecast_summary.python"
        );
        assert_eq!(
            declaration_stem(&root, "quantile-forecast-summary", Some("python")).unwrap(),
            "quantile_forecast_summary.python"
        );

        // The retired Rust twin is not preserved as a compatibility route.
        let wrong = declaration_stem(&root, "quantile-forecast-summary", Some("rust")).unwrap_err();
        assert!(wrong.contains("no implementation called \"rust\""), "{wrong}");
        assert!(wrong.contains("python"), "{wrong}");

        let unknown = declaration_stem(&root, "not-a-statistic", None).unwrap_err();
        assert!(unknown.contains("no research statistic is called"), "{unknown}");
        assert!(unknown.contains("canary-selection"), "{unknown}");
        std::fs::remove_dir_all(root).unwrap();
    }

    /// The declaration names its implementation; the filename only indexes it.
    ///
    /// Deriving the implementation from the filename meant a rename silently
    /// changed which implementation answered: the file said one thing, the
    /// declaration another, and the filename won without anybody being told.
    #[test]
    fn a_renamed_declaration_is_refused_rather_than_answering_as_the_other() {
        let root = scratch_root(&["quantile_forecast_summary.python.json"]);
        let declarations = root.join("analysis_declarations");
        // The bytes still declare `python`; only the name says `rust`.
        std::fs::rename(
            declarations.join("quantile_forecast_summary.python.json"),
            declarations.join("quantile_forecast_summary.rust.json"),
        )
        .unwrap();
        let refusal = declared_statistics(&root).unwrap_err();
        assert!(refusal.contains("declares implementation \"python\""), "{refusal}");
        assert!(refusal.contains("filename says \"rust\""), "{refusal}");

        // A suffix with no field, and a field with no suffix, are both refused
        // by name rather than guessed at.
        std::fs::rename(
            declarations.join("quantile_forecast_summary.rust.json"),
            declarations.join("quantile_forecast_summary.json"),
        )
        .unwrap();
        let no_suffix = declared_statistics(&root).unwrap_err();
        assert!(no_suffix.contains("filename carries no suffix"), "{no_suffix}");
        std::fs::remove_dir_all(root).unwrap();
    }

    /// Every line `research-list-statistics` prints is a name
    /// `research-compute-statistic` accepts. It used
    /// to print file stems, so `quantile-forecast-summary.python` was
    /// advertised and then refused by the compute command's validator for its dot.
    #[test]
    fn every_advertised_name_is_one_compute_command_accepts() {
        let root = scratch_root(&["canary_selection.json", "quantile_forecast_summary.python.json"]);
        for analysis in declared_statistics(&root).unwrap() {
            assert!(
                !analysis.kind.contains('.'),
                "an advertised name must not carry an implementation suffix: {}",
                analysis.kind
            );
            let chosen = analysis.implementations.first().map(String::as_str);
            declaration_stem(&root, &analysis.kind, chosen)
                .unwrap_or_else(|error| panic!("advertised {} but compute refuses it: {error}", analysis.kind));
        }
        std::fs::remove_dir_all(root).unwrap();
    }
}
