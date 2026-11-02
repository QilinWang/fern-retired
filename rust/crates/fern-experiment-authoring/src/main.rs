//! `fern-experiment` - Fern's checked experiment-authoring commands.
//!
//! This binary is the process entry for the authoring crate. It probes one
//! implementation, resolves an experiment or ablation draft, and forks one
//! checked experiment. It does not inspect Cards or execute Plans.

use std::io::{Read, Write};
use std::path::{Path, PathBuf};

use anyhow::Context;
use clap::{Args, Parser, Subcommand};
use fern_experiment_authoring::{
    DEFAULT_EXPERIMENT_COMPONENT_HOME, fork_experiment_resolution_v2, probe_implementation_bundle_v2,
    resolve_ablation_draft_v2, resolve_experiment_draft_v2,
};
use fern_json_contract::{JsonContract, checked_from_slice};

#[derive(Debug, Parser)]
#[command(
    name = "fern-experiment",
    about = "Probe implementations and resolve checked Fern experiments."
)]
struct Cli {
    /// Repository root containing experiment components and source bytes.
    #[arg(long, default_value = ".")]
    root: PathBuf,
    #[command(subcommand)]
    command: ExperimentCommand,
}

#[derive(Debug, Subcommand)]
enum ExperimentCommand {
    /// Import the declared implementation and freeze its actual generated capabilities with its source bytes.
    ProbeImplementation(ProbeImplementation),
    /// Resolve one inline-or-reference V2 idea into the complete JSON a worker would receive.
    ResolveExperiment(ResolveExperiment),
    /// Resolve a V2 control/treatment pair and require one implementation plus exact declared differences.
    ResolveAblation(ResolveAblation),
    /// Fork one checked experiment, changing exactly one component field while keeping implementation fixed.
    ForkExperiment(ForkExperiment),
}

#[derive(Debug, Args)]
struct ResolveExperiment {
    /// V2 Experiment Draft JSON, or `-` to read one pasted draft from stdin.
    #[arg(long = "from")]
    source: PathBuf,
    /// Checked implementation-bundle resolution produced by `probe-implementation`.
    #[arg(long)]
    implementation: PathBuf,
    /// Content-addressed component-card home used only when the draft contains `ref` components.
    #[arg(long, default_value = DEFAULT_EXPERIMENT_COMPONENT_HOME)]
    component_home: PathBuf,
    /// Atomically write the checked resolution here instead of printing it.
    #[arg(long = "out")]
    output: Option<PathBuf>,
}

#[derive(Debug, Args)]
struct ResolveAblation {
    /// V2 Ablation Draft JSON, or `-` to read one pasted pair from stdin.
    #[arg(long = "from")]
    source: PathBuf,
    /// One checked implementation bundle shared by both arms.
    #[arg(long)]
    implementation: PathBuf,
    /// Content-addressed component-card home used only when either arm contains a `ref` component.
    #[arg(long, default_value = DEFAULT_EXPERIMENT_COMPONENT_HOME)]
    component_home: PathBuf,
    /// Atomically write the checked resolution here instead of printing it.
    #[arg(long = "out")]
    output: Option<PathBuf>,
}

#[derive(Debug, Args)]
struct ProbeImplementation {
    /// V2 Implementation Bundle Draft JSON, or `-` to read it from stdin.
    #[arg(long = "from")]
    source: PathBuf,
    /// Exact Python interpreter used to import the declared capability provider.
    #[arg(long)]
    python: PathBuf,
    /// Atomically write the checked implementation bundle here instead of printing it.
    #[arg(long = "out")]
    output: Option<PathBuf>,
}

#[derive(Debug, Args)]
struct ForkExperiment {
    /// Checked V2 Experiment Resolution JSON to use as the parent.
    #[arg(long = "from")]
    source: PathBuf,
    /// Checked implementation bundle inherited by the child.
    #[arg(long)]
    implementation: PathBuf,
    /// One exact JSON pointer below `/components`.
    #[arg(long = "set")]
    changed_pointer: String,
    /// JSON value replacing the selected field, for example `9`, `true`, or `"student_t"`.
    #[arg(long)]
    value_json: String,
    /// Scientific question for the child experiment.
    #[arg(long)]
    question: String,
    /// Optional replacement Research Design identity; omitted forks inherit the parent's reference.
    #[arg(long)]
    design_ref: Option<String>,
    /// Refuse the fork when its exact expanded cell count exceeds this bound.
    #[arg(long)]
    max_cells: Option<u64>,
    /// Measured planning rate used to print an estimated GPU duration.
    #[arg(long, requires = "cost_basis")]
    gpu_seconds_per_cell: Option<u64>,
    /// Receipt, benchmark, or bounded observation supporting the planning rate.
    #[arg(long, requires = "gpu_seconds_per_cell")]
    cost_basis: Option<String>,
    /// Refuse the fork when its estimated GPU duration exceeds this bound.
    #[arg(long, requires_all = ["max_cells", "gpu_seconds_per_cell"])]
    max_gpu_seconds: Option<u64>,
    /// Content-addressed component-card home; normally unused because a fork starts from resolved bodies.
    #[arg(long, default_value = DEFAULT_EXPERIMENT_COMPONENT_HOME)]
    component_home: PathBuf,
    /// Atomically write the checked fork resolution here instead of printing it.
    #[arg(long = "out")]
    output: Option<PathBuf>,
}

fn main() {
    let cli = Cli::parse();
    if let Err(error) = try_run(cli) {
        eprintln!("experiment authoring failed: {error}");
        std::process::exit(1);
    }
}

fn try_run(args: Cli) -> anyhow::Result<()> {
    let root = args.root.canonicalize()?;
    match args.command {
        ExperimentCommand::ProbeImplementation(input) => {
            let draft_bytes = read_text_source(
                &root,
                &input.source,
                2 * 1024 * 1024,
                "V2 Implementation Bundle Draft JSON",
            )?;
            let draft = checked_from_slice::<fern_recipes::ImplementationBundleDraftV2>(&draft_bytes)?;
            let python = if input.python.is_absolute() {
                input.python
            } else {
                root.join(input.python)
            };
            let resolution = probe_implementation_bundle_v2(&root, &python, draft)?;
            write_contract_or_print(&root, input.output.as_deref(), &resolution)?;
        }
        ExperimentCommand::ResolveExperiment(input) => {
            if input.source == Path::new("-") && input.implementation == Path::new("-") {
                anyhow::bail!(
                    "resolve-experiment cannot read both draft and implementation bundle from the same stdin stream"
                );
            }
            let draft_bytes = read_text_source(&root, &input.source, 1024 * 1024, "V2 Experiment Draft JSON")?;
            let implementation_bytes = read_text_source(
                &root,
                &input.implementation,
                4 * 1024 * 1024,
                "V2 implementation bundle resolution JSON",
            )?;
            let draft = checked_from_slice::<fern_recipes::ExperimentDraftV2>(&draft_bytes)?;
            let implementation =
                checked_from_slice::<fern_recipes::ImplementationBundleResolutionV2>(&implementation_bytes)?;
            let resolution = resolve_experiment_draft_v2(&root, &input.component_home, draft, implementation)?;
            write_contract_or_print(&root, input.output.as_deref(), &resolution)?;
        }
        ExperimentCommand::ResolveAblation(input) => {
            if input.source == Path::new("-") && input.implementation == Path::new("-") {
                anyhow::bail!(
                    "resolve-ablation cannot read both draft and implementation bundle from the same stdin stream"
                );
            }
            let draft_bytes = read_text_source(&root, &input.source, 2 * 1024 * 1024, "V2 Ablation Draft JSON")?;
            let implementation_bytes = read_text_source(
                &root,
                &input.implementation,
                4 * 1024 * 1024,
                "V2 implementation bundle resolution JSON",
            )?;
            let draft = checked_from_slice::<fern_recipes::AblationDraftV2>(&draft_bytes)?;
            let implementation =
                checked_from_slice::<fern_recipes::ImplementationBundleResolutionV2>(&implementation_bytes)?;
            let resolution = resolve_ablation_draft_v2(&root, &input.component_home, draft, implementation)?;
            write_contract_or_print(&root, input.output.as_deref(), &resolution)?;
        }
        ExperimentCommand::ForkExperiment(input) => {
            if input.source == Path::new("-") && input.implementation == Path::new("-") {
                anyhow::bail!(
                    "fork-experiment cannot read both parent and implementation bundle from the same stdin stream"
                );
            }
            let parent_bytes = read_text_source(
                &root,
                &input.source,
                4 * 1024 * 1024,
                "V2 parent Experiment Resolution JSON",
            )?;
            let implementation_bytes = read_text_source(
                &root,
                &input.implementation,
                4 * 1024 * 1024,
                "V2 implementation bundle resolution JSON",
            )?;
            let parent = checked_experiment_parent(&parent_bytes)?;
            let implementation =
                checked_from_slice::<fern_recipes::ImplementationBundleResolutionV2>(&implementation_bytes)?;
            let replacement = serde_json::from_str(&input.value_json)
                .with_context(|| format!("--value-json is not one complete JSON value: {:?}", input.value_json))?;
            let budget = input.max_cells.map(|max_cells| fern_recipes::ExperimentBudgetV2 {
                max_cells,
                max_gpu_seconds: input.max_gpu_seconds,
            });
            let cost_estimate =
                input
                    .gpu_seconds_per_cell
                    .map(|gpu_seconds_per_cell| fern_recipes::ExperimentCostEstimateV2 {
                        gpu_seconds_per_cell,
                        basis: input
                            .cost_basis
                            .expect("clap requires --cost-basis with --gpu-seconds-per-cell"),
                    });
            let resolution = fork_experiment_resolution_v2(
                &root,
                &input.component_home,
                &parent,
                &input.changed_pointer,
                replacement,
                input.question,
                input.design_ref,
                budget,
                cost_estimate,
                implementation,
            )?;
            write_contract_or_print(&root, input.output.as_deref(), &resolution)?;
        }
    }
    Ok(())
}

fn checked_experiment_parent(bytes: &[u8]) -> anyhow::Result<fern_recipes::ExperimentResolutionV2> {
    if let Ok(parent) = checked_from_slice::<fern_recipes::ExperimentResolutionV2>(bytes) {
        return Ok(parent);
    }
    let fork = checked_from_slice::<fern_recipes::ExperimentForkResolutionV2>(bytes)
        .context("V2 parent must be an Experiment Resolution or a checked fork produced from one")?;
    Ok(fork.resolved)
}

fn write_contract_or_print<T: JsonContract + serde::Serialize>(
    root: &Path,
    output: Option<&Path>,
    value: &T,
) -> anyhow::Result<()> {
    let Some(output) = output else {
        println!("{}", value.checked_pretty_json()?);
        return Ok(());
    };
    let output = if output.is_absolute() {
        output.to_path_buf()
    } else {
        root.join(output)
    };
    let file_name = output
        .file_name()
        .context("--out must name a JSON file, not a directory")?;
    let parent = output
        .parent()
        .unwrap_or(root)
        .canonicalize()
        .with_context(|| format!("cannot resolve --out parent for {}", output.display()))?;
    let output = parent.join(file_name);
    if let Ok(metadata) = std::fs::symlink_metadata(&output) {
        if metadata.file_type().is_symlink() || !metadata.is_file() {
            anyhow::bail!(
                "--out {} must be absent or one regular file, not a symlink or directory",
                output.display()
            );
        }
    }
    let mut bytes = value.checked_pretty_json()?.into_bytes();
    bytes.push(b'\n');
    let mut staged = tempfile::Builder::new()
        .prefix(".fern-v2-json-")
        .suffix(".partial")
        .tempfile_in(&parent)
        .with_context(|| format!("cannot stage checked JSON beside {}", output.display()))?;
    staged
        .as_file_mut()
        .write_all(&bytes)
        .with_context(|| format!("cannot write staged checked JSON for {}", output.display()))?;
    staged
        .as_file_mut()
        .sync_all()
        .with_context(|| format!("cannot sync staged checked JSON for {}", output.display()))?;
    staged
        .persist(&output)
        .map_err(|error| error.error)
        .with_context(|| format!("cannot atomically publish checked JSON to {}", output.display()))?;
    println!("wrote {}", output.display());
    Ok(())
}

fn read_text_source(root: &Path, source: &Path, maximum_bytes: usize, noun: &str) -> anyhow::Result<Vec<u8>> {
    if source == Path::new("-") {
        return read_bounded_text_bytes(std::io::stdin(), "stdin", maximum_bytes, noun);
    }
    let path = if source.is_absolute() {
        source.to_path_buf()
    } else {
        root.join(source)
    };
    let metadata =
        std::fs::symlink_metadata(&path).with_context(|| format!("cannot inspect {noun} {}", path.display()))?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        anyhow::bail!(
            "{noun} {} must be one regular file, not a symlink or directory",
            path.display()
        );
    }
    if metadata.len() > maximum_bytes as u64 {
        anyhow::bail!(
            "{noun} {} is {} bytes; maximum is {} bytes",
            path.display(),
            metadata.len(),
            maximum_bytes
        );
    }
    let file = std::fs::File::open(&path).with_context(|| format!("cannot open {noun} {}", path.display()))?;
    read_bounded_text_bytes(file, &path.display().to_string(), maximum_bytes, noun)
}

fn read_bounded_text_bytes(
    reader: impl Read,
    source_label: &str,
    maximum_bytes: usize,
    noun: &str,
) -> anyhow::Result<Vec<u8>> {
    let mut bytes = Vec::new();
    reader
        .take(maximum_bytes as u64 + 1)
        .read_to_end(&mut bytes)
        .with_context(|| format!("cannot read {noun} {source_label}"))?;
    if bytes.len() > maximum_bytes {
        anyhow::bail!("{noun} {source_label} exceeds the maximum of {maximum_bytes} bytes");
    }
    Ok(bytes)
}

#[cfg(test)]
mod tests {
    use clap::Parser;

    #[test]
    fn direct_commands_keep_the_existing_authoring_arguments() {
        let probe = super::Cli::try_parse_from([
            "fern-experiment",
            "probe-implementation",
            "--from",
            "implementation-draft.json",
            "--python",
            "/home/user/.venv/bin/python",
        ])
        .unwrap();
        assert!(matches!(
            probe.command,
            super::ExperimentCommand::ProbeImplementation(_)
        ));

        let experiment = super::Cli::try_parse_from([
            "fern-experiment",
            "resolve-experiment",
            "--from",
            "idea.json",
            "--implementation",
            "implementation.json",
        ])
        .unwrap();
        let super::ExperimentCommand::ResolveExperiment(input) = experiment.command else {
            panic!("resolve-experiment must remain one direct command");
        };
        assert_eq!(input.source, std::path::Path::new("idea.json"));
        assert_eq!(input.implementation, std::path::Path::new("implementation.json"));
        assert_eq!(
            input.component_home,
            std::path::Path::new(fern_experiment_authoring::DEFAULT_EXPERIMENT_COMPONENT_HOME)
        );

        let ablation = super::Cli::try_parse_from([
            "fern-experiment",
            "resolve-ablation",
            "--from",
            "pair.json",
            "--implementation",
            "implementation.json",
        ])
        .unwrap();
        assert!(matches!(ablation.command, super::ExperimentCommand::ResolveAblation(_)));

        let fork = super::Cli::try_parse_from([
            "fern-experiment",
            "fork-experiment",
            "--from",
            "parent.json",
            "--implementation",
            "implementation.json",
            "--set",
            "/components/density/parameters/components",
            "--value-json",
            "6",
            "--question",
            "Does K=6 help?",
            "--max-cells",
            "12",
            "--gpu-seconds-per-cell",
            "668",
            "--cost-basis",
            "receipt:fern-cell-timing",
            "--max-gpu-seconds",
            "10000",
            "--out",
            "child.json",
        ])
        .unwrap();
        let super::ExperimentCommand::ForkExperiment(input) = fork.command else {
            panic!("fork-experiment must remain one direct command");
        };
        assert_eq!(input.changed_pointer, "/components/density/parameters/components");
        assert_eq!(input.value_json, "6");
        assert_eq!(input.max_cells, Some(12));
        assert_eq!(input.gpu_seconds_per_cell, Some(668));
        assert_eq!(input.max_gpu_seconds, Some(10_000));
        assert_eq!(input.output.as_deref(), Some(std::path::Path::new("child.json")));
    }
}
