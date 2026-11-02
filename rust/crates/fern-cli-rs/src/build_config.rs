//! Internal typed config selection for PlanRun.
//!
//! The public `build-config` route is `fern-experiment` through the Python
//! caller and Just selection document. C# owns defaults, admission, and the
//! `fern.cfg.v2` response; this module retains only PlanRun's typed selection.

use std::path::PathBuf;
use strum::IntoEnumIterator;

use clap::Args;
use fern_experiment_types::FernDensityVariantV1;
use fern_run::resolved_cfg::{DeviceKind, NoiseKind, ResolvedConfigSpec};
use serde::Serialize;

pub(crate) use crate::presets::FernArchitectureVariant;
use crate::presets::{RecipeBlueprint, RecipeEvalName, RecipeLoaderName, RecipeModelName, RecipeTrainName};
use crate::{Fallible, invalid_input};

#[derive(Debug, Args)]
pub struct BuildConfig {
    /// Model config defaults to materialize.
    #[arg(long, value_parser = parse_model_name)]
    pub(crate) model: ConfigModelName,
    /// Fern architecture identity. `current` preserves the active research
    /// implementation; `icml_pinned` selects the recoverable paper-bounds
    /// reconstruction used only by explicitly named comparison recipes.
    #[arg(long, default_value = "current", value_parser = FernArchitectureVariant::parse)]
    pub(crate) fern_architecture: FernArchitectureVariant,
    /// Width of the base cloud Fern's predictive samples are drawn from, σ₀.
    /// Omitted keeps the 0.1 written into `fr.py` and `fr_cfg.py`, and with it
    /// every existing recipe's knob set and run hash.
    #[arg(long)]
    pub(crate) source_std: Option<f64>,
    /// Emit one magnitude per (channel, patch-group) block on top of the
    /// per-step stretch. Off keeps every existing recipe's knob set.
    #[arg(long, default_value_t = false)]
    pub(crate) fern_radial_head: bool,
    /// Fern predictive density. The four mixture values retain all component
    /// densities and train with exact log-sum-exp likelihood.
    #[arg(long, default_value = "single_gaussian", value_parser = parse_fern_density)]
    pub(crate) fern_density: FernDensityVariantV1,
    /// Number of finite density components. Use one for single_gaussian and at
    /// least two for a mixture.
    #[arg(long, default_value_t = 1)]
    pub(crate) fern_components: u64,
    /// Number of components evaluated by sparse_moe. It must be at least two
    /// and smaller than --fern-components; other densities require one.
    #[arg(long, default_value_t = 1)]
    pub(crate) fern_active_components: u64,
    /// Sequence/input length in timesteps.
    #[arg(long, default_value_t = 336)]
    pub(crate) seq_len: u64,
    /// Prediction horizon in timesteps.
    #[arg(long, default_value_t = 64)]
    pub(crate) pred_len: u64,
    /// Channel count, when known at plan time.
    #[arg(long)]
    pub(crate) channels: Option<u64>,
    /// Treat each channel as an independent target stream.
    #[arg(long, default_value_t = false)]
    pub(crate) channel_independent: bool,
    /// Evaluation/loss preset. Valid: huber, huber_proj500, nll_s16e32,
    /// eig_nll_s4e32, huber_eval_crps, huber_crps, crps_s4e48,
    /// huber_eval_crps_e32_no_swd, eig_nll_s16e32_no_swd, eig_nll_s1e1.
    /// `eig_nll_s1e1` is the one that pairs Fern's own exact likelihood with a
    /// single predictive sample, which is what the seam-attached densities
    /// need; `nll_*` presets train the plain diagonal NLL, not Fern's.
    #[arg(long, default_value = "huber", value_parser = RecipeEvalName::parse)]
    pub(crate) eval: RecipeEvalName,
    /// Loader preset. Valid: p70_20_10_none_bs95, p70_20_10_standard_bs95,
    /// p70_20_10_standard_bs24, p70_10_20_none_bs128,
    /// p70_10_20_robust_mad_bs128. Sets partition fractions, normalization,
    /// and batch size / workers in fern.cfg.v2.
    #[arg(long, default_value = "p70_20_10_none_bs95", value_parser = RecipeLoaderName::parse)]
    pub(crate) loader: RecipeLoaderName,
    /// Training scalar preset. Valid: default_train, lr9e4_ep50_pat5_grace0,
    /// lr3e4_ep50_pat5_grace3. Controls lr/epochs/patience/grace period.
    #[arg(long, default_value = "default_train", value_parser = RecipeTrainName::parse)]
    pub(crate) train: RecipeTrainName,
    /// Observation-noise law recorded in fern.cfg.v2 (`data.noise_kind`) and
    /// in run identity as protocol `noise_law`. Valid: gaussian, laplace,
    /// student_t3. Non-Gaussian laws are variance-matched to one so relative
    /// doses stay train-std-scale interventions; serie-rs banks generate all
    /// three laws.
    #[arg(long, default_value = "gaussian", value_parser = NoiseKind::parse)]
    pub(crate) noise_kind: NoiseKind,
    /// Lag-one autocorrelation of the observation noise, in (-1, 1). A
    /// SEPARATE axis from --noise-kind: that says what one reading's error
    /// looks like, this says how much it remembers the previous one. Variance
    /// is held at one for every phi, so a relative dose keeps its meaning.
    /// Zero is every lane run so far and is omitted from the hashed config.
    #[arg(long, default_value_t = 0.0)]
    pub(crate) noise_ar1_phi: f64,
    /// Runtime device recorded in config and run identity.
    #[arg(long, value_parser = parse_device_kind)]
    pub(crate) device: DeviceKind,
    /// Optional JSON file containing a full fern.cfg.v2 loss block.
    #[arg(long)]
    pub(crate) loss_json: Option<PathBuf>,
    /// Optional JSON file containing a Fern patch spec {L,P,G} for Brenier_patch_spec.
    #[arg(long)]
    pub(crate) fern_patch_json: Option<PathBuf>,
    /// EnbPI member identity, set only by recipe-target expansion — never a
    /// CLI flag, so `build-config` cannot mint a member by hand.
    #[clap(skip)]
    pub(crate) bootstrap: Option<fern_run::csharp_experiment::RecipeTargetBootstrapMember>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, strum::EnumIter)]
pub(crate) enum ConfigModelName {
    Fern,
    Attraos,
    ChronosBoltBase,
    ChronosBoltSmall,
    Zeus,
    Chronos2,
    Crossformer,
    Dlinear,
    Duet,
    Film,
    Fits,
    Frets,
    Hi,
    Itransformer,
    Koopa,
    Lightts,
    Moderntcn,
    Mtsmixer,
    Naive,
    Patchtst,
    Pfnn,
    Segrnn,
    Softs,
    Sparsetsf,
    Stemgnn,
    Tide,
    Timemixer,
    Timesnet,
    Timexer,
    Tirex,
    Timesfm25_200m,
}

/// What happens when a pretrained model reports non-monotone marginal
/// quantiles. This is a paper-facing policy, so it is typed and hashed with
/// the model config; adapters may not silently sort their own output.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, serde::Deserialize, schemars::JsonSchema)]
#[serde(rename_all = "snake_case")]
pub(crate) enum QuantileCrossingPolicy {
    NotApplicable,
    ReportRaw,
    ModelNativeMonotoneRepair,
}

impl QuantileCrossingPolicy {
    pub(crate) fn value(self) -> &'static str {
        match self {
            Self::NotApplicable => "not_applicable",
            Self::ReportRaw => "report_raw",
            Self::ModelNativeMonotoneRepair => "model_native_monotone_repair",
        }
    }

    #[cfg(any(fern_role_operator, fern_role_matrix_worker, test))]
    pub(crate) fn parse(raw: &str) -> Result<Self, String> {
        match raw {
            "not_applicable" => Ok(Self::NotApplicable),
            "report_raw" => Ok(Self::ReportRaw),
            "model_native_monotone_repair" => Ok(Self::ModelNativeMonotoneRepair),
            _ => Err(format!("unknown quantile crossing policy {raw:?}")),
        }
    }
}

impl ConfigModelName {
    pub(crate) fn value(self) -> &'static str {
        match self {
            Self::Fern => "fern",
            Self::Attraos => "attraos",
            Self::ChronosBoltBase => "chronos_bolt_base",
            Self::ChronosBoltSmall => "chronos_bolt_small",
            Self::Zeus => "zeus",
            Self::Chronos2 => "chronos2",
            Self::Crossformer => "crossformer",
            Self::Dlinear => "dlinear",
            Self::Duet => "duet",
            Self::Film => "film",
            Self::Fits => "fits",
            Self::Frets => "frets",
            Self::Hi => "hi",
            Self::Itransformer => "itransformer",
            Self::Koopa => "koopa",
            Self::Lightts => "lightts",
            Self::Moderntcn => "moderntcn",
            Self::Mtsmixer => "mtsmixer",
            Self::Naive => "naive",
            Self::Patchtst => "patchtst",
            Self::Pfnn => "pfnn",
            Self::Segrnn => "segrnn",
            Self::Softs => "softs",
            Self::Sparsetsf => "sparsetsf",
            Self::Stemgnn => "stemgnn",
            Self::Tide => "tide",
            Self::Timemixer => "timemixer",
            Self::Timesnet => "timesnet",
            Self::Timexer => "timexer",
            Self::Tirex => "tirex",
            Self::Timesfm25_200m => "timesfm_2p5_200m",
        }
    }

    pub(crate) fn from_recipe_model(model: RecipeModelName) -> Self {
        match model {
            RecipeModelName::Fern | RecipeModelName::FernIcmlPinned | RecipeModelName::FernWideScale => Self::Fern,
            RecipeModelName::Dlinear => Self::Dlinear,
            RecipeModelName::Duet => Self::Duet,
            RecipeModelName::Patchtst => Self::Patchtst,
            RecipeModelName::Timemixer => Self::Timemixer,
            RecipeModelName::ChronosBoltBase => Self::ChronosBoltBase,
            RecipeModelName::ChronosBoltSmall => Self::ChronosBoltSmall,
            RecipeModelName::Zeus => Self::Zeus,
            RecipeModelName::Chronos2 => Self::Chronos2,
            RecipeModelName::Tirex => Self::Tirex,
            RecipeModelName::Timesfm25_200m => Self::Timesfm25_200m,
        }
    }
}

impl BuildConfig {
    pub(crate) fn from_recipe_target(
        recipe: &RecipeBlueprint,
        pred_len: i64,
        bootstrap: Option<fern_run::csharp_experiment::RecipeTargetBootstrapMember>,
    ) -> Fallible<Self> {
        let seq_len = u64::try_from(recipe.plan.seq_len()).map_err(|_| invalid_input("recipe seq_len must be >= 1"))?;
        let pred_len = u64::try_from(pred_len).map_err(|_| invalid_input("recipe pred_len must be >= 1"))?;
        let (fern_density, fern_components, fern_active_components) = recipe
            .fern_density
            .map(|density| {
                (
                    density.variant(),
                    density.component_count(),
                    density.sparse_active_component_count().unwrap_or(1),
                )
            })
            .unwrap_or((FernDensityVariantV1::SingleGaussian, 1, 1));
        Ok(Self {
            model: ConfigModelName::from_recipe_model(recipe.model),
            fern_architecture: recipe
                .fern_architecture
                .unwrap_or_else(|| FernArchitectureVariant::from_recipe_model(recipe.model)),
            source_std: recipe.source_std,
            fern_radial_head: recipe.fern_radial_head,
            fern_density,
            fern_components,
            fern_active_components,
            seq_len,
            pred_len,
            channels: None,
            channel_independent: false,
            eval: recipe.eval,
            loader: recipe.loader,
            train: recipe.train,
            noise_kind: recipe.noise_kind,
            noise_ar1_phi: recipe.noise_ar1_phi,
            device: DeviceKind::Cuda,
            loss_json: None,
            fern_patch_json: None,
            bootstrap,
        })
    }
}

fn read_optional_overlay_utf8(path: Option<&PathBuf>, label: &str) -> Fallible<Option<String>> {
    path.map(|path| {
        String::from_utf8(std::fs::read(path)?)
            .map_err(|error| invalid_input(format!("{label} {} is not UTF-8: {error}", path.display())))
    })
    .transpose()
}

/// Complete typed selection sent to C# ConfigResolver. Paths never cross this
/// boundary: Rust reads selected optional documents once and C# owns every
/// base default, loss/patch admission and returned config.
#[derive(Serialize)]
struct ConfigBuildRequest<'a> {
    model: &'a str,
    fern_architecture: &'a str,
    source_std: Option<f64>,
    fern_radial_head: bool,
    fern_density: &'a str,
    fern_components: u64,
    fern_active_components: u64,
    seq_len: u64,
    pred_len: u64,
    channels: Option<u64>,
    channel_independent: bool,
    eval: &'a str,
    loader: &'a str,
    train: &'a str,
    noise_kind: &'a str,
    noise_ar1_phi: f64,
    device: &'a str,
    #[serde(skip_serializing_if = "Option::is_none")]
    loss_json: Option<&'a str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    fern_patch_json: Option<&'a str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    bootstrap: Option<ConfigBuildBootstrap>,
}

#[derive(Serialize)]
struct ConfigBuildBootstrap {
    bootstrap_idx: u64,
    bootstrap_seed: u64,
    block_size: u64,
    ensemble_size: u64,
}

fn device_name(device: DeviceKind) -> &'static str {
    match device {
        DeviceKind::Cpu => "cpu",
        DeviceKind::Cuda => "cuda",
    }
}

pub(crate) fn build_resolved_config(args: BuildConfig) -> Fallible<ResolvedConfigSpec> {
    let loss_json = read_optional_overlay_utf8(args.loss_json.as_ref(), "loss JSON")?;
    let fern_patch_json = read_optional_overlay_utf8(args.fern_patch_json.as_ref(), "Fern patch JSON")?;
    let request = ConfigBuildRequest {
        model: args.model.value(),
        fern_architecture: args.fern_architecture.value(),
        source_std: args.source_std,
        fern_radial_head: args.fern_radial_head,
        fern_density: args.fern_density.value(),
        fern_components: args.fern_components,
        fern_active_components: args.fern_active_components,
        seq_len: args.seq_len,
        pred_len: args.pred_len,
        channels: args.channels,
        channel_independent: args.channel_independent,
        eval: args.eval.value(),
        loader: args.loader.value(),
        train: args.train.value(),
        noise_kind: args.noise_kind.value(),
        noise_ar1_phi: args.noise_ar1_phi,
        device: device_name(args.device),
        loss_json: loss_json.as_deref(),
        fern_patch_json: fern_patch_json.as_deref(),
        bootstrap: args.bootstrap.as_ref().map(|member| ConfigBuildBootstrap {
            bootstrap_idx: member.bootstrap_idx,
            bootstrap_seed: member.bootstrap_seed,
            block_size: member.block_size,
            ensemble_size: member.ensemble_size,
        }),
    };
    let response = fern_run::csharp_experiment::run_bytes("build-config", &request).map_err(invalid_input)?;
    serde_json::from_slice(&response)
        .map_err(|error| invalid_input(format!("build-config returned invalid resolved config: {error}")))
}

// Retired 2026-08-28: ConfigResolver/ConfigBuildCli now owns this complete
// Rust constructor, including its preset tables and Fern admission rules. Keep
// the original body together until its historical source file is archived;
// `build_resolved_config` above has no route into it.
#[cfg(any())]
mod retired_rust_config_constructor {
use super::*;

#[derive(Debug, Clone, Copy)]
struct LoaderPreset {
    batch_size: u64,
    num_workers: u64,
    data: DataConfig,
}

/// Loader preset names encode partition + normalization + batch size, e.g.
/// `p70_20_10_none_bs95` = 70/20/10 split, no normalization, batch 95.
fn loader_preset(loader: RecipeLoaderName) -> LoaderPreset {
    let (batch_size, normalization) = match loader {
        RecipeLoaderName::P70_20_10NoneBs95 => (95, NormalizationKind::None),
        RecipeLoaderName::P70_20_10StandardBs95 => (95, NormalizationKind::Standard),
        RecipeLoaderName::P70_20_10StandardBs24 => (24, NormalizationKind::Standard),
        RecipeLoaderName::P70_10_20NoneBs128 => (128, NormalizationKind::None),
        RecipeLoaderName::P70_10_20RobustMadBs128 => (128, NormalizationKind::RobustMad),
    };
    let (val_end, test_start) = match loader {
        RecipeLoaderName::P70_20_10NoneBs95
        | RecipeLoaderName::P70_20_10StandardBs95
        | RecipeLoaderName::P70_20_10StandardBs24 => (0.9, 0.9),
        RecipeLoaderName::P70_10_20NoneBs128 | RecipeLoaderName::P70_10_20RobustMadBs128 => (0.8, 0.8),
    };
    LoaderPreset {
        batch_size,
        num_workers: 0,
        data: DataConfig {
            train_start: 0.0,
            train_end: 0.7,
            val_start: 0.7,
            val_end,
            test_start,
            test_end: 1.0,
            normalization,
            // This is only the loader default. build_resolved_config overrides
            // it from args/recipe noise_kind; Laplace and Student-t3 both use
            // variance-matched serie-rs banks in their extended lanes.
            noise_kind: NoiseKind::Gaussian,
            noise_ar1_phi: 0.0,
        },
    }
}

#[derive(Debug, Clone, Copy)]
struct TrainPreset {
    learning_rate: f64,
    epochs: u64,
    patience: u64,
    grace_period: u64,
}

fn train_preset(train: RecipeTrainName) -> TrainPreset {
    match train {
        RecipeTrainName::Default => TrainPreset {
            learning_rate: 3e-4,
            epochs: 50,
            patience: 5,
            grace_period: 1,
        },
        RecipeTrainName::IcmlMain => TrainPreset {
            learning_rate: 9e-4,
            epochs: 50,
            patience: 5,
            grace_period: 0,
        },
        RecipeTrainName::IcmlShock => TrainPreset {
            learning_rate: 3e-4,
            epochs: 50,
            patience: 5,
            grace_period: 3,
        },
    }
}

fn execution_kind_for_model(model: ConfigModelName) -> ExecutionKind {
    match model {
        ConfigModelName::ChronosBoltBase
        | ConfigModelName::ChronosBoltSmall
        | ConfigModelName::Chronos2
        | ConfigModelName::Tirex
        | ConfigModelName::Timesfm25_200m
        | ConfigModelName::Zeus => ExecutionKind::PretrainedInference,
        _ => ExecutionKind::Train,
    }
}

fn loss_preset(eval: RecipeEvalName) -> LossConfig {
    match eval {
        RecipeEvalName::Huber => huber_loss(0),
        RecipeEvalName::HuberProj500 => huber_loss(500),
        RecipeEvalName::NllS16E32 => nll_loss(16, 32),
        RecipeEvalName::FernNllS4E32 => fern_nll_loss(4, 32, true),
        RecipeEvalName::HuberEvalCrps => huber_eval_crps_loss(32, true),
        RecipeEvalName::HuberCrps => huber_crps_loss(16, 32),
        RecipeEvalName::CrpsS4E48 => crps_loss(4, 48),
        RecipeEvalName::HuberEvalCrpsE32NoSwd => huber_eval_crps_loss(32, false),
        RecipeEvalName::FernNllS16E32NoSwd => fern_nll_loss(16, 32, false),
        RecipeEvalName::FernNllS1E1 => fern_nll_loss(1, 1, true),
    }
}

fn pretrained_inference_loss() -> LossConfig {
    LossConfig {
        train: Vec::new(),
        eval: metric_configs(
            &[],
            &[
                MetricName::Mse,
                MetricName::Mae,
                MetricName::Crps,
                MetricName::Swd,
                MetricName::Ept,
            ],
        ),
        huber_delta: huber_delta(),
        num_proj_swd: 0,
        probability: ProbabilityConfig {
            num_train_samples: 1,
            num_eval_samples: 1,
            quantile_levels: baseline_quantile_levels(),
        },
        smoothing: smooth_policy_none(),
    }
}

fn huber_loss(num_proj_swd: u64) -> LossConfig {
    LossConfig {
        train: metric_configs(&[(MetricName::Huber, 1.0)], &[MetricName::Mse, MetricName::Mae]),
        eval: metric_configs(
            &[(MetricName::Mse, 0.1), (MetricName::Mae, 1.0), (MetricName::Swd, 0.1)],
            &[MetricName::Huber, MetricName::Ept],
        ),
        huber_delta: huber_delta(),
        num_proj_swd,
        probability: pointwise_probability(),
        smoothing: smooth_policy_none(),
    }
}

fn nll_loss(num_train_samples: u64, num_eval_samples: u64) -> LossConfig {
    LossConfig {
        train: metric_configs(&[(MetricName::Nll, 1.0)], &[MetricName::Crps]),
        eval: metric_configs(
            &[(MetricName::Nll, 1.0)],
            &[MetricName::Mse, MetricName::Mae, MetricName::Crps],
        ),
        huber_delta: huber_delta(),
        num_proj_swd: 0,
        probability: probability(num_train_samples, num_eval_samples),
        smoothing: smooth_policy_none(),
    }
}

fn fern_nll_loss(num_train_samples: u64, num_eval_samples: u64, include_swd_ept: bool) -> LossConfig {
    let eval_monitor = if include_swd_ept {
        vec![
            MetricName::Mse,
            MetricName::Mae,
            MetricName::Swd,
            MetricName::Ept,
            MetricName::Crps,
            MetricName::Nll,
        ]
    } else {
        vec![MetricName::Mse, MetricName::Mae, MetricName::Crps, MetricName::Nll]
    };
    LossConfig {
        train: metric_configs(&[(MetricName::FernNll, 1.0)], &[MetricName::Nll]),
        eval: metric_configs(&[(MetricName::FernNll, 1.0)], &eval_monitor),
        huber_delta: huber_delta(),
        num_proj_swd: 0,
        probability: probability(num_train_samples, num_eval_samples),
        smoothing: smooth_policy_none(),
    }
}

fn huber_eval_crps_loss(num_eval_samples: u64, include_swd_ept: bool) -> LossConfig {
    let eval = if include_swd_ept {
        metric_configs(
            &[(MetricName::Mse, 0.1), (MetricName::Mae, 1.0), (MetricName::Swd, 0.1)],
            &[MetricName::Huber, MetricName::Ept, MetricName::Crps, MetricName::Nll],
        )
    } else {
        metric_configs(
            &[],
            &[
                MetricName::Mse,
                MetricName::Mae,
                MetricName::Huber,
                MetricName::Crps,
                MetricName::Nll,
            ],
        )
    };
    LossConfig {
        train: metric_configs(&[(MetricName::Huber, 1.0)], &[MetricName::Mse, MetricName::Mae]),
        eval,
        huber_delta: huber_delta(),
        num_proj_swd: 0,
        probability: probability(1, num_eval_samples),
        smoothing: smooth_policy_none(),
    }
}

fn huber_crps_loss(num_train_samples: u64, num_eval_samples: u64) -> LossConfig {
    LossConfig {
        train: metric_configs(
            &[(MetricName::Huber, 1.0), (MetricName::Crps, 0.1)],
            &[MetricName::Mse, MetricName::Mae, MetricName::Nll],
        ),
        eval: metric_configs(
            &[
                (MetricName::Mse, 0.1),
                (MetricName::Mae, 1.0),
                (MetricName::Swd, 0.1),
                (MetricName::Crps, 0.1),
            ],
            &[MetricName::Huber, MetricName::Ept, MetricName::Nll],
        ),
        huber_delta: huber_delta(),
        num_proj_swd: 0,
        probability: probability(num_train_samples, num_eval_samples),
        smoothing: smooth_policy_none(),
    }
}

fn crps_loss(num_train_samples: u64, num_eval_samples: u64) -> LossConfig {
    LossConfig {
        train: metric_configs(&[(MetricName::Crps, 1.0)], &[MetricName::Nll]),
        eval: metric_configs(
            &[(MetricName::Crps, 1.0)],
            &[
                MetricName::Mse,
                MetricName::Mae,
                MetricName::Swd,
                MetricName::Ept,
                MetricName::Nll,
            ],
        ),
        huber_delta: huber_delta(),
        num_proj_swd: 0,
        probability: probability(num_train_samples, num_eval_samples),
        smoothing: smooth_policy_none(),
    }
}

fn metric_configs(active: &[(MetricName, f64)], monitor: &[MetricName]) -> Vec<MetricConfig> {
    metric_order()
        .into_iter()
        .filter_map(|name| {
            if let Some((_, weight)) = active.iter().find(|(active_name, _)| *active_name == name) {
                Some(MetricConfig {
                    name,
                    role: MetricRole::Active,
                    weight: *weight,
                })
            } else if monitor.contains(&name) {
                Some(MetricConfig {
                    name,
                    role: MetricRole::Monitor,
                    weight: 0.0,
                })
            } else {
                None
            }
        })
        .collect()
}

fn metric_order() -> [MetricName; 8] {
    [
        MetricName::Mse,
        MetricName::Mae,
        MetricName::Huber,
        MetricName::Swd,
        MetricName::Ept,
        MetricName::Crps,
        MetricName::Nll,
        MetricName::FernNll,
    ]
}

fn huber_delta() -> HuberDeltaConfig {
    HuberDeltaConfig { train: 1.0, eval: 1.0 }
}

fn pointwise_probability() -> ProbabilityConfig {
    probability(1, 1)
}

fn probability(num_train_samples: u64, num_eval_samples: u64) -> ProbabilityConfig {
    ProbabilityConfig {
        num_train_samples,
        num_eval_samples,
        quantile_levels: vec![0.05, 0.1, 0.25, 0.5, 0.75, 0.9, 0.95],
    }
}

fn baseline_quantile_levels() -> Vec<f64> {
    vec![0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9]
}

fn smooth_policy_none() -> SmoothPolicyConfig {
    let spec = SmoothingSpecConfig {
        kind: SmoothingKind::None,
        window: 3,
        alpha: 0.1,
    };
    SmoothPolicyConfig {
        train: spec.clone(),
        val: spec.clone(),
        test: spec,
    }
}

fn model_knobs(
    model: ConfigModelName,
    fern_architecture: FernArchitectureVariant,
    source_std: Option<f64>,
    fern_radial_head: bool,
    fern_density: FernDensityVariantV1,
    fern_components: u64,
    fern_active_components: u64,
    seq_len: u64,
    pred_len: u64,
    loss: &LossConfig,
) -> Fallible<BTreeMap<String, Value>> {
    if model != ConfigModelName::Fern
        && (fern_density != FernDensityVariantV1::SingleGaussian || fern_components != 1 || fern_active_components != 1)
    {
        return Err(invalid_input(
            "--fern-density, --fern-components, and --fern-active-components are valid only with --model fern",
        ));
    }
    let mut knobs = BTreeMap::new();
    knobs.insert("shape_contract".to_string(), shape_contract(model));
    match model {
        ConfigModelName::ChronosBoltBase => {
            insert_pretrained_baseline_knobs(
                &mut knobs,
                "chronos",
                "amazon/chronos-bolt-base",
                "5d9f166d69f47aef3401367a7b842e78fe97b121",
                seq_len,
                pred_len,
                &loss.probability.quantile_levels,
                QuantileCrossingPolicy::ReportRaw,
            );
        }
        ConfigModelName::ChronosBoltSmall => {
            insert_pretrained_baseline_knobs(
                &mut knobs,
                "chronos_small",
                "amazon/chronos-bolt-small",
                "772f3d25d38aec6d914c8949dab4462e2d46f5d8",
                seq_len,
                pred_len,
                &loss.probability.quantile_levels,
                QuantileCrossingPolicy::ReportRaw,
            );
        }
        // Zeus emits nine quantile levels natively — the same grid the
        // campaign's receipts already store — so no level is interpolated on
        // the way in. The pinned revision is the snapshot `scripts/
        // zeus_canary.py` measured: all 167 parameters loaded, crossing rate
        // 0.0000%, every output finite. Its worker needs a different
        // interpreter (flash-attn ships no wheel for torch 2.12), which
        // `run --python` already selects per sweep.
        ConfigModelName::Zeus => {
            insert_pretrained_baseline_knobs(
                &mut knobs,
                "zeus",
                "GestaltCog/zeus",
                "4b458a2ec89b30c114da99d0837e33e4343070f3",
                seq_len,
                pred_len,
                &loss.probability.quantile_levels,
                QuantileCrossingPolicy::ReportRaw,
            );
        }
        ConfigModelName::Chronos2 => {
            insert_pretrained_baseline_knobs(
                &mut knobs,
                "chronos2",
                "amazon/chronos-2",
                "29ec3766d36d6f73f0696f85560a422f50e8498c",
                seq_len,
                pred_len,
                &loss.probability.quantile_levels,
                QuantileCrossingPolicy::ReportRaw,
            );
        }
        ConfigModelName::Tirex => {
            insert_pretrained_baseline_knobs(
                &mut knobs,
                "tirex",
                "NX-AI/TiRex",
                "63c740922493f5fbe60b277609ec62babfba2762",
                seq_len,
                pred_len,
                &loss.probability.quantile_levels,
                QuantileCrossingPolicy::ReportRaw,
            );
            knobs.insert("checkpoint_max_context".to_string(), json!(2016));
            knobs.insert("native_patch_length".to_string(), json!(32));
            knobs.insert(
                "multichannel_strategy".to_string(),
                json!("channel_independent_marginal_quantiles"),
            );
            knobs.insert("package_requirement".to_string(), json!("tirex-ts==1.4.2"));
        }
        ConfigModelName::Timesfm25_200m => {
            insert_pretrained_baseline_knobs(
                &mut knobs,
                "timefm",
                "google/timesfm-2.5-200m-pytorch",
                "1d952420fba87f3c6dee4f240de0f1a0fbc790e3",
                seq_len,
                pred_len,
                &loss.probability.quantile_levels,
                QuantileCrossingPolicy::ModelNativeMonotoneRepair,
            );
            knobs.insert("normalize_inputs".to_string(), json!(true));
            knobs.insert("use_continuous_quantile_head".to_string(), json!(true));
            knobs.insert("force_flip_invariance".to_string(), json!(true));
            knobs.insert("infer_is_positive".to_string(), json!(true));
            knobs.insert("fix_quantile_crossing".to_string(), json!(true));
        }
        ConfigModelName::Fern => {
            validate_fern_density_selection(fern_density, fern_components, fern_active_components)?;
            knobs.insert("fern_architecture".to_string(), json!(fern_architecture.value()));
            // A NEW density is a new implementation beside the old one, never a
            // rewrite of it. The single-Gaussian Fern that the whole campaign
            // ran keeps the exact knob set it always had -- including the
            // historical `num_moe` name -- so its config hash, target hash and
            // every completed coordinate still resolve. The mixture knobs
            // appear only when a mixture is actually selected, where a new
            // identity is correct because it IS a different model.
            //
            // Writing them unconditionally moved every Fern coordinate in the
            // campaign: one stored LGSSM cell went from target_hash
            // dc85f49833ca324a to 3109f0c6..., which resume reads as never
            // computed. Caught 2026-08-17 by comparing a stored hash against a
            // freshly built one; `fern_identity_is_stable_for_single_gaussian`
            // below now fails if it happens again.
            if fern_density == FernDensityVariantV1::SingleGaussian && fern_components == 1 {
                knobs.insert("num_moe".to_string(), json!(1));
            } else {
                knobs.insert("fern_density".to_string(), json!(fern_density.value()));
                knobs.insert("mixture_component_count".to_string(), json!(fern_components));
            }
            if fern_density == FernDensityVariantV1::SparseMoe {
                knobs.insert(
                    "sparse_active_component_count".to_string(),
                    json!(fern_active_components),
                );
            }
            if fern_density != FernDensityVariantV1::SingleGaussian {
                knobs.insert("mixture_relative_std_floor".to_string(), json!(0.001));
            }
            // Absent for every variant but the wide-scale one, so no existing
            // recipe's knob set, config hash, or run hash moves.
            if let Some(ceiling) = fern_architecture.brenier_scale_ceiling() {
                knobs.insert("brenier_scale_ceiling".to_string(), json!(ceiling));
            }
            // A recipe FIELD, not an architecture name: the value travels as
            // data from the recipe identity, so a new one needs no new enum
            // arm and no redeployed binary. Absent keeps the built-in 0.1, so
            // no existing recipe's knob set or run hash moves.
            if let Some(source_std) = source_std {
                knobs.insert("source_std".to_string(), json!(source_std));
            }
            if fern_radial_head {
                knobs.insert("fern_radial_head".to_string(), json!(true));
            }
            knobs.insert("Brenier_patch_spec".to_string(), patch_for_pred_len(pred_len)?);
            knobs.insert("Coupling_patch_spec".to_string(), full_patch_for_pred_len(pred_len));
            knobs.insert("block_size".to_string(), json!(24));
            knobs.insert("dim_augment".to_string(), json!(128));
            knobs.insert("dim_hidden".to_string(), json!(128));
            knobs.insert("eig_mode".to_string(), json!("w2_dirac"));
            knobs.insert("factory_schemas".to_string(), json!({}));
            knobs.insert("householder_backend".to_string(), json!("torch"));
            knobs.insert("num_eval_samples".to_string(), json!(loss.probability.num_eval_samples));
            knobs.insert("num_reflects".to_string(), json!(24));
            knobs.insert(
                "num_train_samples".to_string(),
                json!(loss.probability.num_train_samples),
            );
            knobs.insert("revin".to_string(), json!(false));
            knobs.insert("revin_affine".to_string(), json!(true));
            knobs.insert("revin_subtract_last".to_string(), json!(false));
            knobs.insert("w_eig".to_string(), json!(0.0));
        }
        ConfigModelName::Attraos => {
            knobs.insert("FFT_evolve".to_string(), json!(false));
            knobs.insert("PSR_delay".to_string(), json!(1));
            knobs.insert("PSR_dim".to_string(), json!(3));
            knobs.insert("PSR_type".to_string(), json!("merged_seq"));
            knobs.insert("d_state".to_string(), json!(16));
            knobs.insert("dt_init_floor".to_string(), json!(1e-4));
            knobs.insert("dt_max".to_string(), json!(1e-1));
            knobs.insert("dt_min".to_string(), json!(1e-3));
            knobs.insert("dt_rank".to_string(), json!(8));
            knobs.insert("dt_scale".to_string(), json!(1.0));
            knobs.insert("e_layers".to_string(), json!(1));
            knobs.insert("multi_res".to_string(), json!(false));
            knobs.insert("patch_len".to_string(), json!(24));
        }
        ConfigModelName::Crossformer => {
            knobs.insert("baseline".to_string(), json!(false));
            knobs.insert("dropout".to_string(), json!(0.05));
            knobs.insert("factor".to_string(), json!(10));
            knobs.insert("hidden_act".to_string(), json!("gelu"));
            knobs.insert("hidden_size".to_string(), json!(512));
            knobs.insert("intermediate_size".to_string(), json!(2048));
            knobs.insert("n_heads".to_string(), json!(8));
            knobs.insert("num_layers".to_string(), json!(2));
            knobs.insert("patch_len".to_string(), json!(16));
            knobs.insert("win_size".to_string(), json!(2));
        }
        ConfigModelName::Dlinear => {
            knobs.insert("individual".to_string(), json!(false));
            knobs.insert("moving_avg".to_string(), json!(25));
            knobs.insert("stride".to_string(), json!(1));
        }
        ConfigModelName::Duet => {
            knobs.insert("channel_independence".to_string(), json!(true));
            knobs.insert("dropout".to_string(), json!(0.2));
            knobs.insert("fc_dropout".to_string(), json!(0.2));
            knobs.insert("hidden_act".to_string(), json!("gelu"));
            knobs.insert("hidden_size".to_string(), json!(512));
            knobs.insert("intermediate_size".to_string(), json!(2048));
            knobs.insert("loss_coef".to_string(), json!(1.0));
            knobs.insert("moving_avg".to_string(), json!(25));
            knobs.insert("n_heads".to_string(), json!(8));
            knobs.insert("noisy_gating".to_string(), json!(true));
            knobs.insert("num_experts".to_string(), json!(4));
            knobs.insert("num_layers".to_string(), json!(2));
            knobs.insert("router_hidden_act".to_string(), json!("relu"));
            knobs.insert("router_hidden_size".to_string(), json!(256));
            knobs.insert("top_k".to_string(), json!(1));
        }
        ConfigModelName::Film => {
            knobs.insert("hidden_size".to_string(), json!([256]));
            knobs.insert("multiscale".to_string(), json!([1, 2, 4]));
            knobs.insert("use_revin".to_string(), json!(true));
        }
        ConfigModelName::Fits => {
            knobs.insert("base_period".to_string(), json!(24));
            knobs.insert("cut_freq".to_string(), Value::Null);
            knobs.insert("h_order".to_string(), json!(6));
            knobs.insert("individual".to_string(), json!(false));
        }
        ConfigModelName::Frets => {
            knobs.insert("channel_independence".to_string(), json!(false));
            knobs.insert("dropout".to_string(), json!(0.0));
            knobs.insert("embed_size".to_string(), json!(128));
            knobs.insert("hidden_act".to_string(), json!("leaky_relu"));
            knobs.insert("hidden_size".to_string(), json!(256));
            knobs.insert("scale".to_string(), json!(0.02));
            knobs.insert("sparsity_threshold".to_string(), json!(0.01));
        }
        ConfigModelName::Hi => {
            knobs.insert("reverse".to_string(), json!(false));
        }
        ConfigModelName::Itransformer => {
            knobs.insert("dropout".to_string(), json!(0.1));
            knobs.insert("hidden_act".to_string(), json!("gelu"));
            knobs.insert("hidden_size".to_string(), json!(256));
            knobs.insert("intermediate_size".to_string(), json!(1024));
            knobs.insert("n_heads".to_string(), json!(1));
            knobs.insert("num_layers".to_string(), json!(1));
            knobs.insert("output_attentions".to_string(), json!(false));
            knobs.insert("use_revin".to_string(), json!(true));
        }
        ConfigModelName::Koopa => {
            knobs.insert("alpha".to_string(), json!(0.20));
            knobs.insert("dynamic_dim".to_string(), json!(32));
            knobs.insert("hidden_dim".to_string(), json!(128));
            knobs.insert("hidden_layers".to_string(), json!(2));
            knobs.insert("mask_spectrum".to_string(), Value::Null);
            knobs.insert("multistep".to_string(), json!(true));
            knobs.insert("num_blocks".to_string(), json!(2));
            knobs.insert("seg_len".to_string(), json!(24));
        }
        ConfigModelName::Lightts => {
            knobs.insert("chunk_size".to_string(), json!(16));
            knobs.insert("hidden_size".to_string(), json!(512));
        }
        ConfigModelName::Moderntcn => {
            knobs.insert("affine".to_string(), json!(true));
            knobs.insert("decomposition".to_string(), json!(false));
            knobs.insert("dims".to_string(), json!([64, 64, 64, 64]));
            knobs.insert("downsample_ratio".to_string(), json!(2));
            knobs.insert("dropout".to_string(), json!(0.3));
            knobs.insert("ffn_ratio".to_string(), json!(2));
            knobs.insert("head_dropout".to_string(), json!(0.0));
            knobs.insert("individual".to_string(), json!(false));
            knobs.insert("kernel_size".to_string(), json!(25));
            knobs.insert("large_size".to_string(), json!(51));
            knobs.insert("num_blocks".to_string(), json!(1));
            knobs.insert("patch_size".to_string(), json!(8));
            knobs.insert("patch_stride".to_string(), json!(4));
            knobs.insert("revin".to_string(), json!(true));
            knobs.insert("small_kernel_merged".to_string(), json!(false));
            knobs.insert("small_size".to_string(), json!(5));
            knobs.insert("subtract_last".to_string(), json!(false));
            knobs.insert("use_multi_scale".to_string(), json!(false));
        }
        ConfigModelName::Mtsmixer => {
            knobs.insert("channel_hidden_size".to_string(), json!(16));
            knobs.insert("down_sampling".to_string(), json!(2));
            knobs.insert("fac_C".to_string(), json!(false));
            knobs.insert("fac_T".to_string(), json!(false));
            knobs.insert("individual".to_string(), json!(false));
            knobs.insert("num_layers".to_string(), json!(2));
            knobs.insert("temporal_hidden_size".to_string(), json!(512));
            knobs.insert("use_layer_norm".to_string(), json!(true));
            knobs.insert("use_revin".to_string(), json!(true));
        }
        ConfigModelName::Naive => {}
        ConfigModelName::Patchtst => {
            knobs.insert("affine".to_string(), json!(false));
            knobs.insert("attn_dropout".to_string(), json!(0.1));
            knobs.insert("decomposition".to_string(), json!(false));
            knobs.insert("dropout".to_string(), json!(0.1));
            knobs.insert("factor".to_string(), json!(3));
            knobs.insert("fc_dropout".to_string(), json!(0.1));
            knobs.insert("head_dropout".to_string(), json!(0.1));
            knobs.insert("hidden_act".to_string(), json!("gelu"));
            knobs.insert("hidden_size".to_string(), json!(128));
            knobs.insert("individual".to_string(), json!(false));
            knobs.insert("intermediate_size".to_string(), json!(128));
            knobs.insert("kernel_size".to_string(), json!(25));
            knobs.insert("n_heads".to_string(), json!(4));
            knobs.insert("norm_type".to_string(), json!("batch_norm"));
            knobs.insert("num_layers".to_string(), json!(2));
            knobs.insert("output_attentions".to_string(), json!(false));
            knobs.insert("padding".to_string(), json!(true));
            knobs.insert("patch_len".to_string(), json!(16));
            knobs.insert("patch_stride".to_string(), json!(8));
            knobs.insert("revin".to_string(), json!(true));
            knobs.insert("subtract_last".to_string(), json!(false));
            knobs.insert("task_name".to_string(), json!("long_term_forecast"));
        }
        ConfigModelName::Pfnn => {
            knobs.insert("hidden_dim".to_string(), json!(96));
            knobs.insert("latent_dim".to_string(), json!(96));
            knobs.insert("pfnn_aux_contrastive_weight".to_string(), json!(0.0));
            knobs.insert("pfnn_aux_identity_weight".to_string(), json!(0.0));
            knobs.insert("pfnn_init_scale".to_string(), json!(1.0));
        }
        ConfigModelName::Segrnn => {
            knobs.insert("dropout".to_string(), json!(0.1));
            knobs.insert("hidden_act".to_string(), json!("gelu"));
            knobs.insert("hidden_size".to_string(), json!(256));
            knobs.insert("num_layers".to_string(), json!(1));
            knobs.insert("seg_len".to_string(), json!(16));
        }
        ConfigModelName::Softs => {
            knobs.insert("core_size".to_string(), json!(128));
            knobs.insert("dropout".to_string(), json!(0.05));
            knobs.insert("hidden_act".to_string(), json!("gelu"));
            knobs.insert("hidden_size".to_string(), json!(512));
            knobs.insert("intermediate_size".to_string(), json!(2048));
            knobs.insert("num_layers".to_string(), json!(2));
            knobs.insert("use_revin".to_string(), json!(true));
        }
        ConfigModelName::Sparsetsf => {
            knobs.insert(
                "period_len".to_string(),
                json!(sparse_tsf_default_period_len(seq_len, pred_len)?),
            );
        }
        ConfigModelName::Stemgnn => {
            knobs.insert("dropout".to_string(), json!(0.5));
            knobs.insert("hidden_size".to_string(), json!(5));
            knobs.insert("leaky_rate".to_string(), json!(0.2));
            knobs.insert("num_blocks".to_string(), json!(2));
        }
        ConfigModelName::Tide => {
            knobs.insert("bias".to_string(), json!(true));
            knobs.insert("dropout".to_string(), json!(0.3));
            knobs.insert("hidden_size".to_string(), json!(256));
            knobs.insert("intermediate_size".to_string(), json!(256));
            knobs.insert("num_decoder_layers".to_string(), json!(2));
            knobs.insert("num_encoder_layers".to_string(), json!(2));
            knobs.insert("num_timestamps".to_string(), json!(4));
            knobs.insert("timestamps_encode_size".to_string(), json!(2));
            knobs.insert("use_revin".to_string(), json!(true));
        }
        ConfigModelName::Timemixer => {
            knobs.insert("channel_independence".to_string(), json!(true));
            knobs.insert("d_ff".to_string(), json!(32));
            knobs.insert("d_model".to_string(), json!(16));
            knobs.insert("decomp_method".to_string(), json!("moving_avg"));
            knobs.insert("down_sampling_layers".to_string(), json!(3));
            knobs.insert("down_sampling_method".to_string(), json!("avg"));
            knobs.insert("down_sampling_window".to_string(), json!(2));
            knobs.insert("dropout".to_string(), json!(0.1));
            knobs.insert("e_layers".to_string(), json!(2));
            knobs.insert("embed".to_string(), Value::Null);
            knobs.insert("freq".to_string(), Value::Null);
            knobs.insert("hidden_act".to_string(), json!("gelu"));
            knobs.insert("hidden_size".to_string(), Value::Null);
            knobs.insert("intermediate_size".to_string(), Value::Null);
            knobs.insert("moving_avg".to_string(), json!(25));
            knobs.insert("num_layers".to_string(), Value::Null);
            knobs.insert("task_name".to_string(), json!("long_term_forecast"));
            knobs.insert("timestamp_sizes".to_string(), Value::Null);
            knobs.insert("top_k".to_string(), json!(5));
            knobs.insert("use_norm".to_string(), Value::Null);
            knobs.insert("use_revin".to_string(), Value::Null);
            knobs.insert("use_timestamps".to_string(), json!(false));
        }
        ConfigModelName::Timesnet => {
            knobs.insert("dropout".to_string(), json!(0.1));
            knobs.insert("hidden_size".to_string(), json!(256));
            knobs.insert("intermediate_size".to_string(), json!(1024));
            knobs.insert("num_kernels".to_string(), json!(3));
            knobs.insert("num_layers".to_string(), json!(1));
            knobs.insert("timestamp_sizes".to_string(), Value::Null);
            knobs.insert("top_k".to_string(), json!(5));
            knobs.insert("use_timestamps".to_string(), json!(false));
        }
        ConfigModelName::Timexer => {
            knobs.insert("dropout".to_string(), json!(0.1));
            knobs.insert("hidden_act".to_string(), json!("gelu"));
            knobs.insert("hidden_size".to_string(), json!(256));
            knobs.insert("intermediate_size".to_string(), json!(1024));
            knobs.insert("n_heads".to_string(), json!(1));
            knobs.insert("num_layers".to_string(), json!(1));
            knobs.insert("output_attentions".to_string(), json!(false));
            knobs.insert("patch_len".to_string(), json!(16));
            knobs.insert("use_revin".to_string(), json!(true));
        }
    }
    Ok(knobs)
}

fn insert_pretrained_baseline_knobs(
    knobs: &mut BTreeMap<String, Value>,
    flavor: &str,
    hf_model_id: &str,
    hf_revision: &str,
    seq_len: u64,
    pred_len: u64,
    quantile_levels: &[f64],
    quantile_crossing_policy: QuantileCrossingPolicy,
) {
    knobs.insert("baseline_kind".to_string(), json!("pretrained_foundation_forecaster"));
    knobs.insert("inference_flavor".to_string(), json!(flavor));
    knobs.insert("hf_model_id".to_string(), json!(hf_model_id));
    knobs.insert("hf_revision".to_string(), json!(hf_revision));
    knobs.insert("max_context".to_string(), json!(seq_len));
    knobs.insert("max_horizon".to_string(), json!(pred_len));
    knobs.insert("quantile_levels".to_string(), json!(quantile_levels));
    knobs.insert(
        "quantile_crossing_policy".to_string(),
        json!(quantile_crossing_policy.value()),
    );
    knobs.insert("quantile_diagnostic_kind".to_string(), json!("model_belief_quantiles"));
}

fn shape_contract(model: ConfigModelName) -> Value {
    let pred_layout = if model == ConfigModelName::Fern {
        "b s h d"
    } else {
        "b h d"
    };
    json!({
        "pred_card": {"layout": pred_layout},
        "target_card": {"layout": "b h d"}
    })
}

fn patch_for_pred_len(pred_len: u64) -> Fallible<Value> {
    for p in [48_u64, 48, 32, 24, 8] {
        if pred_len % p == 0 {
            return Ok(json!({"L": pred_len, "P": p, "G": pred_len / p}));
        }
    }
    Err(invalid_input(format!(
        "pred_len {pred_len} not divisible by any Fern patch size: 48, 32, 24, 8"
    )))
}

fn full_patch_for_pred_len(pred_len: u64) -> Value {
    json!({"L": pred_len, "P": pred_len, "G": 1})
}

fn sparse_tsf_default_period_len(seq_len: u64, pred_len: u64) -> Fallible<u64> {
    for period_len in [24_u64, 16, 12, 8, 4, 2, 1] {
        if seq_len % period_len == 0 && pred_len % period_len == 0 {
            return Ok(period_len);
        }
    }
    Err(invalid_input(format!(
        "No valid SparseTSF period_len for seq_len={seq_len}, pred_len={pred_len}"
    )))
}

fn parse_model_name(raw: &str) -> Result<ConfigModelName, String> {
    ConfigModelName::iter()
        .find(|model| model.value() == raw)
        .ok_or_else(|| {
            let valid = ConfigModelName::iter()
                .map(|model| model.value())
                .collect::<Vec<_>>()
                .join(", ");
            format!("unknown model {raw:?}; valid: {valid}")
        })
}

fn parse_fern_density(raw: &str) -> Result<FernDensityVariantV1, String> {
    FernDensityVariantV1::parse(raw).map_err(|error| error.to_string())
}

fn validate_fern_density_selection(
    density: FernDensityVariantV1,
    component_count: u64,
    active_component_count: u64,
) -> Fallible<()> {
    match density {
        FernDensityVariantV1::SingleGaussian => {
            if component_count != 1 || active_component_count != 1 {
                return Err(invalid_input(
                    "single_gaussian requires --fern-components 1 and --fern-active-components 1",
                ));
            }
        }
        FernDensityVariantV1::SparseMoe => {
            if component_count < 3 || active_component_count < 2 || active_component_count >= component_count {
                return Err(invalid_input(
                    "sparse_moe requires --fern-components >= 3 and 2 <= --fern-active-components < --fern-components",
                ));
            }
        }
        FernDensityVariantV1::MixtureSourceSharedHidden | FernDensityVariantV1::MixtureFernTransportsSharedHidden => {
            // `--fern-components 1` is legal here and only here: one component
            // with a zero source offset is the Core's own single-Gaussian law.
            if active_component_count != 1 {
                return Err(invalid_input(format!(
                    "{} leaves --fern-active-components at 1",
                    density.value()
                )));
            }
        }
        FernDensityVariantV1::BoundedMdn
        | FernDensityVariantV1::SharedTransportMixtureSource
        | FernDensityVariantV1::MixtureOfFernTransports => {
            if component_count < 2 || active_component_count != 1 {
                return Err(invalid_input(format!(
                    "{} requires --fern-components >= 2 and leaves --fern-active-components at 1",
                    density.value()
                )));
            }
        }
    }
    Ok(())
}

}

fn parse_model_name(raw: &str) -> Result<ConfigModelName, String> {
    ConfigModelName::iter()
        .find(|model| model.value() == raw)
        .ok_or_else(|| {
            let valid = ConfigModelName::iter()
                .map(|model| model.value())
                .collect::<Vec<_>>()
                .join(", ");
            format!("unknown model {raw:?}; valid: {valid}")
        })
}

fn parse_fern_density(raw: &str) -> Result<FernDensityVariantV1, String> {
    FernDensityVariantV1::parse(raw).map_err(|error| error.to_string())
}

pub(crate) fn parse_device_kind(raw: &str) -> Result<DeviceKind, String> {
    match raw {
        "cpu" => Ok(DeviceKind::Cpu),
        "cuda" => Ok(DeviceKind::Cuda),
        _ => Err(format!("unknown device {raw:?}; valid: cpu, cuda")),
    }
}

#[cfg(any())]
mod tests {
    use super::*;

    /// A preset the enum knows but nothing else does is a name that fails at
    /// the surface a person types. The compiler already forces `loss_preset` to
    /// have an arm, so what this closes is everything it cannot force: that the
    /// name parses back, that the arm produces a trainable objective, and that
    /// `--eval` actually lists it. The last one is why this test exists —
    /// `eig_nll_s1e1` was added with its arm compiled and its help line stale.
    #[test]
    fn every_eval_preset_parses_trains_and_is_listed_in_help() {
        use clap::{Args as ClapArgs, Command};
        use strum::IntoEnumIterator;

        let command = BuildConfig::augment_args(Command::new("build-config"));
        let eval_help = command
            .get_arguments()
            .find(|argument| argument.get_id() == "eval")
            .expect("build-config must expose --eval")
            .get_help()
            .expect("--eval must document which presets are valid")
            .to_string();

        for preset in RecipeEvalName::iter() {
            let name = preset.value();
            assert_eq!(
                RecipeEvalName::parse(name).expect("a preset's own name must parse"),
                preset,
                "{name:?} does not round-trip through RecipeEvalName::parse"
            );
            assert!(
                !loss_preset(preset).train.is_empty(),
                "{name:?} materializes no training objective, so selecting it trains nothing"
            );
            assert!(
                eval_help.contains(name),
                "--eval help does not list {name:?}; a person cannot discover a preset \
                 that only the enum knows about"
            );
        }
    }

    /// The seam-attached densities refuse more than one predictive sample, so
    /// the ladder is only selectable if some preset pairs Fern's OWN likelihood
    /// with one sample. `nll_*` presets do not: they train the plain diagonal
    /// NLL, which never reaches the exact log-sum-exp over components.
    #[test]
    fn one_preset_pairs_ferns_own_likelihood_with_a_single_sample() {
        use strum::IntoEnumIterator;

        let single_sample_fern_nll = RecipeEvalName::iter()
            .filter(|preset| {
                let loss = loss_preset(*preset);
                loss.probability.num_train_samples == 1
                    && loss.probability.num_eval_samples == 1
                    && loss
                        .train
                        .iter()
                        .any(|metric| metric.name == MetricName::FernNll && metric.weight > 0.0)
            })
            .map(|preset| preset.value())
            .collect::<Vec<_>>();
        assert_eq!(
            single_sample_fern_nll,
            ["eig_nll_s1e1"],
            "exactly one preset must pair Fern's exact likelihood with a single \
             predictive sample; the seam-attached densities cannot be selected without it"
        );
    }

    #[test]
    fn builds_fern_projected_huber_config() {
        let cfg = build_resolved_config(BuildConfig {
            model: ConfigModelName::Fern,
            fern_architecture: FernArchitectureVariant::Current,
            source_std: None,
            fern_radial_head: false,
            fern_density: FernDensityVariantV1::SingleGaussian,
            fern_components: 1,
            fern_active_components: 1,
            seq_len: 336,
            pred_len: 64,
            channels: Some(3),
            channel_independent: false,
            eval: RecipeEvalName::HuberProj500,
            loader: RecipeLoaderName::P70_10_20NoneBs128,
            train: RecipeTrainName::IcmlMain,
            noise_kind: NoiseKind::Gaussian,
            noise_ar1_phi: 0.0,
            device: DeviceKind::Cuda,
            loss_json: None,
            fern_patch_json: None,
            bootstrap: None,
        })
        .unwrap();

        cfg.validate().unwrap();
        assert_eq!(cfg.model.model_type, "fern");
        assert_eq!(cfg.train.batch_size, 128);
        assert_eq!(cfg.optim.learning_rate, 9e-4);
        assert_eq!(cfg.loss.num_proj_swd, 500);
        assert_eq!(cfg.model.knobs["Brenier_patch_spec"]["P"], json!(32));
        // p70_10_20_none_bs128: 70/10/20 split, no normalization.
        assert_eq!(cfg.data.train_end, 0.7);
        assert_eq!(cfg.data.val_end, 0.8);
        assert_eq!(cfg.data.test_start, 0.8);
        assert_eq!(cfg.data.normalization, NormalizationKind::None);
        assert_eq!(cfg.data.noise_kind, NoiseKind::Gaussian);
    }

    /// A completed campaign is a set of coordinates, and a coordinate is a
    /// hash of the config. So a knob added to the ORDINARY Fern config — even
    /// one that changes nothing about what the model computes — silently
    /// orphans every cell already run under it.
    ///
    /// This hash is copied from a run recorded before the mixture work began:
    /// `results/runs/lg_ssm_base_fern_huber_.../lg_ssm_h64_traj0_clean_noise0_s42_2e64`.
    /// It is deliberately a literal. Deriving the expected value from the same
    /// code that produces it would agree with any change, which is the failure
    /// this test exists to catch.
    #[test]
    fn fern_identity_is_stable_for_single_gaussian() {
        let knobs = model_knobs(
            ConfigModelName::Fern,
            FernArchitectureVariant::Current,
            None,
            false,
            FernDensityVariantV1::SingleGaussian,
            1,
            1,
            336,
            64,
            &loss_preset(RecipeEvalName::Huber),
        )
        .unwrap();

        // The historical name the whole campaign was recorded under.
        assert_eq!(knobs["num_moe"], json!(1));
        // The mixture vocabulary belongs to the mixture, and must not appear
        // in a config that declares no mixture.
        for added in [
            "fern_density",
            "mixture_component_count",
            "sparse_active_component_count",
            "mixture_relative_std_floor",
        ] {
            assert!(
                !knobs.contains_key(added),
                "{added} must be absent from an ordinary Fern config; adding it moves every completed coordinate"
            );
        }
        // And the knobs that a new experiment declares are absent until declared.
        for optional in ["source_std", "brenier_scale_ceiling", "fern_radial_head"] {
            assert!(
                !knobs.contains_key(optional),
                "{optional} must be absent unless a recipe declares it"
            );
        }
    }

    #[test]
    fn each_fern_mixture_density_replaces_the_historical_moe_knob() {
        for (density, component_count, active_component_count) in [
            (FernDensityVariantV1::BoundedMdn, 3, 1),
            (FernDensityVariantV1::SparseMoe, 4, 2),
            (FernDensityVariantV1::SharedTransportMixtureSource, 3, 1),
            (FernDensityVariantV1::MixtureOfFernTransports, 3, 1),
        ] {
            let cfg = build_resolved_config(BuildConfig {
                model: ConfigModelName::Fern,
                fern_architecture: FernArchitectureVariant::Current,
                source_std: None,
                fern_radial_head: false,
                fern_density: density,
                fern_components: component_count,
                fern_active_components: active_component_count,
                seq_len: 48,
                pred_len: 24,
                channels: Some(2),
                channel_independent: false,
                eval: RecipeEvalName::FernNllS4E32,
                loader: RecipeLoaderName::P70_20_10NoneBs95,
                train: RecipeTrainName::Default,
                noise_kind: NoiseKind::Gaussian,
                noise_ar1_phi: 0.0,
                device: DeviceKind::Cpu,
                loss_json: None,
                fern_patch_json: None,
                bootstrap: None,
            })
            .unwrap();

            assert_eq!(cfg.model.knobs["fern_density"], density.value());
            assert_eq!(cfg.model.knobs["mixture_component_count"], component_count);
            assert_eq!(cfg.model.knobs["mixture_relative_std_floor"], 0.001);
            // A mixture is a different model, so it carries the new names and
            // not the historical one. The single-Gaussian arm keeps `num_moe`;
            // that is asserted separately, because its identity must not move.
            assert!(!cfg.model.knobs.contains_key("num_moe"));
            if density == FernDensityVariantV1::SparseMoe {
                assert_eq!(cfg.model.knobs["sparse_active_component_count"], active_component_count);
            } else {
                assert!(!cfg.model.knobs.contains_key("sparse_active_component_count"));
            }
            assert!(
                fern_run::model_architecture::model_architecture_card(&cfg)
                    .unwrap()
                    .is_some()
            );
        }
    }

    #[test]
    fn recipe_density_selection_reaches_the_same_four_resolved_configs() {
        use crate::presets::{RecipeFamilyName, RecipeFernDensitySpec};

        for (density, component_count, sparse_active_component_count) in [
            (FernDensityVariantV1::BoundedMdn, 3, None),
            (FernDensityVariantV1::SparseMoe, 4, Some(2)),
            (FernDensityVariantV1::SharedTransportMixtureSource, 3, None),
            (FernDensityVariantV1::MixtureOfFernTransports, 3, None),
        ] {
            let density_spec =
                RecipeFernDensitySpec::new(density, component_count, sparse_active_component_count).unwrap();
            let recipe = RecipeBlueprint::new(RecipeFamilyName::LgSsmBase).fern_density(density_spec);
            let config = build_resolved_config(BuildConfig::from_recipe_target(&recipe, 64, None).unwrap()).unwrap();

            assert_eq!(config.model.knobs["fern_density"], density.value());
            assert_eq!(config.model.knobs["mixture_component_count"], component_count);
            assert_eq!(
                config
                    .model
                    .knobs
                    .get("sparse_active_component_count")
                    .and_then(Value::as_u64),
                sparse_active_component_count,
            );
        }
    }

    #[test]
    fn loader_presets_carry_partition_and_normalization() {
        let robust = loader_preset(RecipeLoaderName::P70_10_20RobustMadBs128);
        assert_eq!(robust.batch_size, 128);
        assert_eq!(robust.data.normalization, NormalizationKind::RobustMad);
        assert_eq!((robust.data.val_end, robust.data.test_start), (0.8, 0.8));

        let standard = loader_preset(RecipeLoaderName::P70_20_10StandardBs24);
        assert_eq!(standard.batch_size, 24);
        assert_eq!(standard.data.normalization, NormalizationKind::Standard);
        assert_eq!((standard.data.val_end, standard.data.test_start), (0.9, 0.9));
    }

    #[test]
    fn builds_all_config_model_defaults() {
        for model in ConfigModelName::iter() {
            let cfg = build_resolved_config(BuildConfig {
                model,
                fern_architecture: FernArchitectureVariant::Current,
                source_std: None,
                fern_radial_head: false,
                fern_density: FernDensityVariantV1::SingleGaussian,
                fern_components: 1,
                fern_active_components: 1,
                seq_len: 336,
                pred_len: 64,
                channels: Some(3),
                channel_independent: false,
                eval: RecipeEvalName::Huber,
                loader: RecipeLoaderName::P70_20_10NoneBs95,
                train: RecipeTrainName::Default,
                noise_kind: NoiseKind::Gaussian,
                noise_ar1_phi: 0.0,
                device: DeviceKind::Cuda,
                loss_json: None,
                fern_patch_json: None,
                bootstrap: None,
            })
            .unwrap_or_else(|err| panic!("{} default config failed: {err}", model.value()));
            cfg.validate()
                .unwrap_or_else(|errors| panic!("{} default config invalid: {errors:?}", model.value()));
            assert_eq!(cfg.model.model_type, model.value());
        }
    }

    #[test]
    fn builds_original_tirex_marginal_quantile_config() {
        let cfg = build_resolved_config(BuildConfig {
            model: ConfigModelName::Tirex,
            fern_architecture: FernArchitectureVariant::Current,
            source_std: None,
            fern_radial_head: false,
            fern_density: FernDensityVariantV1::SingleGaussian,
            fern_components: 1,
            fern_active_components: 1,
            seq_len: 336,
            pred_len: 64,
            channels: Some(3),
            channel_independent: false,
            eval: RecipeEvalName::Huber,
            loader: RecipeLoaderName::P70_20_10NoneBs95,
            train: RecipeTrainName::Default,
            noise_kind: NoiseKind::Gaussian,
            noise_ar1_phi: 0.0,
            device: DeviceKind::Cuda,
            loss_json: None,
            fern_patch_json: None,
            bootstrap: None,
        })
        .unwrap();

        assert_eq!(cfg.execution.kind, ExecutionKind::PretrainedInference);
        assert!(cfg.task.channel_independent);
        assert_eq!(cfg.model.knobs["inference_flavor"], json!("tirex"));
        assert_eq!(cfg.model.knobs["hf_model_id"], json!("NX-AI/TiRex"));
        assert_eq!(
            cfg.model.knobs["hf_revision"],
            json!("63c740922493f5fbe60b277609ec62babfba2762")
        );
        assert_eq!(cfg.model.knobs["quantile_crossing_policy"], json!("report_raw"));
        assert_eq!(
            cfg.model.knobs["multichannel_strategy"],
            json!("channel_independent_marginal_quantiles")
        );
    }

    #[test]
    fn applies_loss_and_patch_overlays() {
        let cfg = build_resolved_config(BuildConfig {
            model: ConfigModelName::Fern,
            fern_architecture: FernArchitectureVariant::Current,
            source_std: None,
            fern_radial_head: false,
            fern_density: FernDensityVariantV1::SingleGaussian,
            fern_components: 1,
            fern_active_components: 1,
            seq_len: 336,
            pred_len: 96,
            channels: Some(3),
            channel_independent: false,
            eval: RecipeEvalName::Huber,
            loader: RecipeLoaderName::P70_20_10NoneBs95,
            train: RecipeTrainName::Default,
            noise_kind: NoiseKind::Gaussian,
            noise_ar1_phi: 0.0,
            device: DeviceKind::Cuda,
            loss_json: None,
            fern_patch_json: None,
            bootstrap: None,
        })
        .unwrap();
        let loss = fern_nll_loss(5, 9, false);
        let response = fern_run::csharp_experiment::apply_config_overlays_bytes(
            &serde_json::to_vec(&cfg).unwrap(),
            Some(&serde_json::to_string(&loss).unwrap()),
            Some(r#"{"L":96,"P":24,"G":4}"#),
        )
        .unwrap();
        let cfg: ResolvedConfigSpec = serde_json::from_slice(&response).unwrap();

        assert_eq!(cfg.loss.probability.num_train_samples, 5);
        assert_eq!(cfg.loss.probability.num_eval_samples, 9);
        assert_eq!(cfg.model.knobs["num_train_samples"], json!(5));
        assert_eq!(cfg.model.knobs["num_eval_samples"], json!(9));
        assert_eq!(cfg.model.knobs["Brenier_patch_spec"]["P"], json!(24));
    }
}

#[cfg(test)]
mod csharp_child_tests {
    use super::*;

    #[test]
    fn real_csharp_child_builds_all_config_model_defaults() {
        for model in ConfigModelName::iter() {
            let config = build_resolved_config(BuildConfig {
                model,
                fern_architecture: FernArchitectureVariant::Current,
                source_std: None,
                fern_radial_head: false,
                fern_density: FernDensityVariantV1::SingleGaussian,
                fern_components: 1,
                fern_active_components: 1,
                seq_len: 336,
                pred_len: 64,
                channels: Some(3),
                channel_independent: false,
                eval: RecipeEvalName::Huber,
                loader: RecipeLoaderName::P70_20_10NoneBs95,
                train: RecipeTrainName::Default,
                noise_kind: NoiseKind::Gaussian,
                noise_ar1_phi: 0.0,
                device: DeviceKind::Cuda,
                loss_json: None,
                fern_patch_json: None,
                bootstrap: None,
            })
            .unwrap_or_else(|error| panic!("{} C# config failed: {error}", model.value()));
            config
                .validate()
                .unwrap_or_else(|errors| panic!("{} C# config invalid: {errors:?}", model.value()));
            assert_eq!(config.model.model_type, model.value());
        }
    }
}
