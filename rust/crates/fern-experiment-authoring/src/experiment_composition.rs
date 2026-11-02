//! Resolve small V2 experiment drafts into complete immutable run manifests.
//!
//! This is an authoring/check boundary, not a scheduler.  Inline component
//! cards and content-addressed component files enter the same normalization
//! path; implementation-generated JSON Schemas validate every requested
//! handler parameter; and the result contains no filesystem path or runtime
//! default for a worker to reinterpret.

use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::{Component, Path};

use fern_json_contract::{JsonContract, checked_from_slice};
use fern_recipes::{
    ABLATION_RESOLUTION_SCHEMA_V2, ACTION_COMPONENT_CARD_SCHEMA_V2, ARCHITECTURE_COMPONENT_CARD_SCHEMA_V2,
    AblationDraftV2, AblationResolutionV2, ActionComponentCardV2, ActionSourceV2, ArchitectureComponentCardV2,
    ComponentSourceV2, CrossComponentConstraintV2, CrossComponentRelationV2, DENSITY_COMPONENT_CARD_SCHEMA_V2,
    DensityComponentCardV2, EXPERIMENT_FORK_RESOLUTION_SCHEMA_V2, EXPERIMENT_MANIFEST_SCHEMA_V2,
    EXPERIMENT_RESOLUTION_SCHEMA_V2, ExperimentBudgetV2, ExperimentComponentReferencesV2, ExperimentComponentRoleV2,
    ExperimentComponentsSourceV2, ExperimentCostEstimateV2, ExperimentDraftV2, ExperimentForkResolutionV2,
    ExperimentResolutionV2, HandlerCapabilityV2, HandlerRequestV2, IMPLEMENTATION_CAPABILITIES_SCHEMA_V2,
    ImplementationBundleResolutionV2, ImplementationCapabilitiesV2, InferentialFeasibilityV2, InferentialRequirementV2,
    RECIPE_COMPONENT_CARD_SCHEMA_V2, RecipeComponentCardV2, ResolvedActionV2, ResolvedExperimentArmV2,
    ResolvedExperimentComponentsV2, ResolvedExperimentManifestV2, WorkloadArmPreviewV2, WorkloadPreviewV2,
};
use schemars::JsonSchema;
use serde::{Serialize, de::DeserializeOwned};
use serde_json::Value;
use sha2::{Digest, Sha256};

use crate::ExperimentCompositionError;

pub const DEFAULT_EXPERIMENT_COMPONENT_HOME: &str = "docs/experiment-components";
const MAX_COMPONENT_CARD_BYTES: u64 = 1024 * 1024;

/// Resolve one scratch declaration without registering a Plan or writing a
/// component card.  `component_home` is consulted only for `{"ref": ...}`
/// sources, so a fully inline idea needs no archive setup first.
pub fn resolve_experiment_draft_v2(
    root: &Path,
    component_home: &Path,
    draft: ExperimentDraftV2,
    implementation_bundle: ImplementationBundleResolutionV2,
) -> Result<ExperimentResolutionV2, ExperimentCompositionError> {
    crate::implementation_bundle::verify_implementation_bundle_v2(&implementation_bundle)?;
    require_schema(
        &draft.schema,
        fern_recipes::EXPERIMENT_DRAFT_SCHEMA_V2,
        "experiment draft",
    )?;
    let question = normalize_nonempty(draft.question, "experiment question")?;
    let design_ref = normalize_optional_nonempty(draft.design_ref, "experiment design_ref")?;
    let budget = normalize_budget(draft.budget)?;
    let cost_estimate = normalize_cost_estimate(draft.cost_estimate)?;
    let inference = validate_inference(draft.inference)?;
    let implementation = implementation_bundle.implementation.clone();
    let capabilities = normalize_capabilities(implementation_bundle.capabilities.clone(), &implementation)?;
    let capability_manifest_sha256 = prefixed_sha256("capabilities", &capabilities)?;
    let components = resolve_components(root, component_home, draft.components)?;
    let resolved = resolve_arm(components, implementation, &capabilities, capability_manifest_sha256)?;
    let arms = [("experiment", &resolved)];
    let workload = workload_preview(&arms, budget.as_ref(), cost_estimate.as_ref())?;
    let warnings = crate::csharp_resolution_warnings::resolution_warnings_via_csharp(&arms)?;
    let resolution = ExperimentResolutionV2 {
        schema: EXPERIMENT_RESOLUTION_SCHEMA_V2.to_string(),
        question,
        design_ref,
        inference,
        workload,
        warnings,
        implementation_bundle,
        resolved,
    };
    verify_experiment_resolution_v2(&resolution)?;
    Ok(resolution)
}

/// Resolve a control/treatment pair whose implementation exists exactly once
/// at the pair root.  The observed semantic leaf differences must equal the
/// declared JSON pointers exactly.
pub fn resolve_ablation_draft_v2(
    root: &Path,
    component_home: &Path,
    draft: AblationDraftV2,
    implementation_bundle: ImplementationBundleResolutionV2,
) -> Result<AblationResolutionV2, ExperimentCompositionError> {
    crate::implementation_bundle::verify_implementation_bundle_v2(&implementation_bundle)?;
    require_schema(&draft.schema, fern_recipes::ABLATION_DRAFT_SCHEMA_V2, "ablation draft")?;
    let question = normalize_nonempty(draft.question, "ablation question")?;
    let design_ref = normalize_optional_nonempty(draft.design_ref, "ablation design_ref")?;
    let budget = normalize_budget(draft.budget)?;
    let cost_estimate = normalize_cost_estimate(draft.cost_estimate)?;
    let inference = validate_inference(draft.inference)?;
    let implementation = implementation_bundle.implementation.clone();
    let declared_changes = normalize_declared_changes(draft.declared_changes)?;
    let declared_fact_changes = normalize_declared_fact_changes(draft.declared_fact_changes)?;
    let capabilities = normalize_capabilities(implementation_bundle.capabilities.clone(), &implementation)?;
    let capability_manifest_sha256 = prefixed_sha256("capabilities", &capabilities)?;

    let control_components = resolve_components(root, component_home, draft.control)?;
    let treatment_components = resolve_components(root, component_home, draft.treatment)?;
    require_exact_declared_changes(
        &control_components.cards,
        &treatment_components.cards,
        &declared_changes,
    )?;

    let control = resolve_arm(
        control_components,
        implementation.clone(),
        &capabilities,
        capability_manifest_sha256.clone(),
    )?;
    let treatment = resolve_arm(
        treatment_components,
        implementation.clone(),
        &capabilities,
        capability_manifest_sha256,
    )?;
    require_exact_declared_fact_changes(
        &control.manifest.configuration_facts,
        &treatment.manifest.configuration_facts,
        &declared_fact_changes,
    )?;
    let arms = [("control", &control), ("treatment", &treatment)];
    let workload = workload_preview(&arms, budget.as_ref(), cost_estimate.as_ref())?;
    let warnings = crate::csharp_resolution_warnings::resolution_warnings_via_csharp(&arms)?;
    let resolution = AblationResolutionV2 {
        schema: ABLATION_RESOLUTION_SCHEMA_V2.to_string(),
        question,
        design_ref,
        inference,
        implementation,
        declared_changes,
        declared_fact_changes,
        workload,
        warnings,
        implementation_bundle,
        control,
        treatment,
    };
    verify_ablation_resolution_v2(&resolution)?;
    Ok(resolution)
}

/// Recompute every derived component and manifest identity in a saved result.
pub fn verify_experiment_resolution_v2(resolution: &ExperimentResolutionV2) -> Result<(), ExperimentCompositionError> {
    require_schema(
        &resolution.schema,
        EXPERIMENT_RESOLUTION_SCHEMA_V2,
        "experiment resolution",
    )?;
    normalize_nonempty(resolution.question.clone(), "experiment question")?;
    normalize_optional_nonempty(resolution.design_ref.clone(), "experiment design_ref")?;
    verify_inference(resolution.inference)?;
    crate::implementation_bundle::verify_implementation_bundle_v2(&resolution.implementation_bundle)?;
    if resolution.implementation_bundle.implementation != resolution.resolved.manifest.implementation {
        return Err(ExperimentCompositionError::new(
            "experiment resolution implementation bundle differs from its resolved manifest",
        ));
    }
    verify_arm(&resolution.resolved, &resolution.implementation_bundle.capabilities)?;
    let arms = [("experiment", &resolution.resolved)];
    verify_warnings(&resolution.warnings, &arms)?;
    verify_workload(&resolution.workload, &arms)
}

/// Recompute the stated warnings from the resolved arms and require the saved
/// list to equal them.
///
/// A warning is derived, so it is checked like every other derived field. The
/// point is not tampering so much as staleness: a receipt edited by hand, or
/// saved by an older build, would otherwise keep telling a reader something
/// that is no longer true of the experiment it names.
fn verify_warnings(
    observed: &[String],
    arms: &[(&str, &ResolvedExperimentArmV2)],
) -> Result<(), ExperimentCompositionError> {
    let expected = crate::csharp_resolution_warnings::resolution_warnings_via_csharp(arms)?;
    if observed != expected.as_slice() {
        return Err(ExperimentCompositionError::new(format!(
            "saved warnings {observed:?} do not match the resolved experiment's own {expected:?}"
        )));
    }
    Ok(())
}

/// Recheck the same-implementation and exact-change laws on a saved pair.
pub fn verify_ablation_resolution_v2(resolution: &AblationResolutionV2) -> Result<(), ExperimentCompositionError> {
    require_schema(&resolution.schema, ABLATION_RESOLUTION_SCHEMA_V2, "ablation resolution")?;
    normalize_nonempty(resolution.question.clone(), "ablation question")?;
    normalize_optional_nonempty(resolution.design_ref.clone(), "ablation design_ref")?;
    verify_inference(resolution.inference)?;
    crate::implementation_bundle::verify_implementation_bundle_v2(&resolution.implementation_bundle)?;
    if resolution.implementation_bundle.implementation != resolution.implementation {
        return Err(ExperimentCompositionError::new(
            "ablation resolution implementation bundle differs from its shared implementation",
        ));
    }
    let declared_changes = normalize_declared_changes(resolution.declared_changes.clone())?;
    if declared_changes != resolution.declared_changes {
        return Err(ExperimentCompositionError::new(
            "ablation resolution declared_changes are not in canonical sorted order",
        ));
    }
    let declared_fact_changes = normalize_declared_fact_changes(resolution.declared_fact_changes.clone())?;
    if declared_fact_changes != resolution.declared_fact_changes {
        return Err(ExperimentCompositionError::new(
            "ablation resolution declared_fact_changes are not in canonical sorted order",
        ));
    }
    for (arm_name, arm) in [("control", &resolution.control), ("treatment", &resolution.treatment)] {
        verify_arm(arm, &resolution.implementation_bundle.capabilities)?;
        if arm.manifest.implementation != resolution.implementation {
            return Err(ExperimentCompositionError::new(format!(
                "ablation {arm_name} implementation {} differs from the pair implementation {}",
                arm.manifest.implementation.as_str(),
                resolution.implementation.as_str()
            )));
        }
    }
    require_exact_declared_changes(
        &resolution.control.manifest.components,
        &resolution.treatment.manifest.components,
        &resolution.declared_changes,
    )?;
    require_exact_declared_fact_changes(
        &resolution.control.manifest.configuration_facts,
        &resolution.treatment.manifest.configuration_facts,
        &resolution.declared_fact_changes,
    )?;
    let arms = [("control", &resolution.control), ("treatment", &resolution.treatment)];
    verify_warnings(&resolution.warnings, &arms)?;
    verify_workload(&resolution.workload, &arms)
}

/// Fork one checked parent by changing exactly one component JSON pointer.
/// Code identity is inherited by construction; changing implementation is a
/// separate authoring operation requiring another probed bundle.
#[allow(clippy::too_many_arguments)]
pub fn fork_experiment_resolution_v2(
    root: &Path,
    component_home: &Path,
    parent: &ExperimentResolutionV2,
    changed_pointer: &str,
    replacement: Value,
    question: String,
    design_ref: Option<String>,
    budget: Option<ExperimentBudgetV2>,
    cost_estimate: Option<ExperimentCostEstimateV2>,
    implementation_bundle: ImplementationBundleResolutionV2,
) -> Result<ExperimentForkResolutionV2, ExperimentCompositionError> {
    verify_experiment_resolution_v2(parent)?;
    validate_declared_pointer(changed_pointer)?;

    // A fork changes one scientific component, not its provenance ancestry.
    // Callers may replace the Design deliberately, but omission inherits the
    // checked parent's reference instead of silently dropping it.
    let design_ref = design_ref.or_else(|| parent.design_ref.clone());

    let original = serde_json::to_value(&parent.resolved.manifest.components)
        .map_err(|error| ExperimentCompositionError::new(format!("cannot inspect parent components: {error}")))?;
    let mut changed = original.clone();
    set_component_pointer(&mut changed, changed_pointer, replacement)?;
    let mut changed_paths = Vec::new();
    collect_differences(&original, &changed, "/components", &mut changed_paths);
    changed_paths.sort();
    if changed_paths != [changed_pointer.to_string()] {
        return Err(ExperimentCompositionError::new(format!(
            "fork must change exactly {changed_pointer:?}; observed component changes {changed_paths:?}"
        )));
    }

    let changed: ResolvedExperimentComponentsV2 = serde_json::from_value(changed).map_err(|error| {
        ExperimentCompositionError::new(format!(
            "forked component value no longer satisfies the V2 component envelope: {error}"
        ))
    })?;
    let inherited_budget = ExperimentBudgetV2 {
        max_cells: parent.workload.max_cells.unwrap_or(u64::MAX),
        max_gpu_seconds: parent.workload.max_gpu_seconds,
    };
    let budget = budget.or_else(|| parent.workload.max_cells.map(|_| inherited_budget));
    let inherited_cost_estimate = match (
        parent.workload.gpu_seconds_per_cell,
        parent.workload.estimate_basis.clone(),
    ) {
        (Some(gpu_seconds_per_cell), Some(basis)) => Some(ExperimentCostEstimateV2 {
            gpu_seconds_per_cell,
            basis,
        }),
        _ => None,
    };
    let draft = ExperimentDraftV2 {
        schema: fern_recipes::EXPERIMENT_DRAFT_SCHEMA_V2.to_string(),
        question,
        design_ref,
        budget,
        cost_estimate: cost_estimate.or(inherited_cost_estimate),
        inference: parent.inference.map(|value| value.requirement),
        components: inline_component_sources(changed),
    };
    let resolved = resolve_experiment_draft_v2(root, component_home, draft, implementation_bundle)?;
    let fork = ExperimentForkResolutionV2 {
        schema: EXPERIMENT_FORK_RESOLUTION_SCHEMA_V2.to_string(),
        parent_manifest_sha256: parent.resolved.manifest_sha256.clone(),
        inherited_implementation: parent.resolved.manifest.implementation.clone(),
        changed_paths,
        workload: resolved.workload.clone(),
        resolved,
    };
    verify_fork_resolution_v2(parent, &fork)?;
    Ok(fork)
}

struct ResolvedComponentsWithReferences {
    cards: ResolvedExperimentComponentsV2,
    references: ExperimentComponentReferencesV2,
}

fn resolve_components(
    root: &Path,
    component_home: &Path,
    source: ExperimentComponentsSourceV2,
) -> Result<ResolvedComponentsWithReferences, ExperimentCompositionError> {
    let (recipe_ref, recipe) = resolve_component(
        root,
        component_home,
        ExperimentComponentRoleV2::World,
        "recipe",
        source.recipe,
        normalize_recipe_card,
    )?;
    let (architecture_ref, architecture) = resolve_component(
        root,
        component_home,
        ExperimentComponentRoleV2::Architecture,
        "architecture",
        source.architecture,
        normalize_architecture_card,
    )?;
    let (action_ref, action) = match source.action {
        ActionSourceV2::StateOnly => (None, ResolvedActionV2::StateOnly),
        ActionSourceV2::Conditioned { component } => {
            let (reference, card) = resolve_component(
                root,
                component_home,
                ExperimentComponentRoleV2::Action,
                "action",
                component,
                normalize_action_card,
            )?;
            (Some(reference), ResolvedActionV2::Conditioned { card })
        }
    };
    let (density_ref, density) = resolve_component(
        root,
        component_home,
        ExperimentComponentRoleV2::Density,
        "density",
        source.density,
        normalize_density_card,
    )?;
    Ok(ResolvedComponentsWithReferences {
        cards: ResolvedExperimentComponentsV2 {
            recipe,
            architecture,
            action,
            density,
        },
        references: ExperimentComponentReferencesV2 {
            recipe: recipe_ref,
            architecture: architecture_ref,
            action: action_ref,
            density: density_ref,
        },
    })
}

fn resolve_component<T, F>(
    root: &Path,
    component_home: &Path,
    role: ExperimentComponentRoleV2,
    address_kind: &str,
    source: ComponentSourceV2<T>,
    normalize: F,
) -> Result<(String, T), ExperimentCompositionError>
where
    T: Clone + Serialize + DeserializeOwned + JsonSchema + JsonContract,
    F: FnOnce(T) -> Result<T, ExperimentCompositionError>,
{
    let (expected_reference, raw) = match source {
        ComponentSourceV2::Inline(card) => (None, card),
        ComponentSourceV2::Ref(reference) => {
            let hex = parse_component_reference(&reference, address_kind)?;
            let bytes = read_component_card(root, component_home, address_kind, hex)?;
            let card = checked_from_slice::<T>(&bytes).map_err(|error| {
                ExperimentCompositionError::new(format!(
                    "{address_kind} component {reference} does not satisfy {}: {error}",
                    T::SCHEMA_ID
                ))
            })?;
            (Some(reference), card)
        }
    };
    let card = normalize(raw)?;
    let observed_reference = component_reference(address_kind, &card)?;
    if let Some(expected) = expected_reference {
        if expected != observed_reference {
            return Err(ExperimentCompositionError::new(format!(
                "{address_kind} component reference {expected} resolves to {observed_reference}; the stored card bytes do not match the requested identity"
            )));
        }
    }
    // `role` is carried separately from the address prefix because Recipe owns
    // four handler roles.  This assertion prevents an accidental caller from
    // resolving a density card through the recipe address space.
    let expected_role = match address_kind {
        "recipe" => ExperimentComponentRoleV2::World,
        "architecture" => ExperimentComponentRoleV2::Architecture,
        "action" => ExperimentComponentRoleV2::Action,
        "density" => ExperimentComponentRoleV2::Density,
        _ => {
            return Err(ExperimentCompositionError::new(format!(
                "unknown component address kind {address_kind:?}"
            )));
        }
    };
    if role != expected_role {
        return Err(ExperimentCompositionError::new(format!(
            "component address kind {address_kind:?} cannot resolve role {}",
            role.value()
        )));
    }
    Ok((observed_reference, card))
}

fn read_component_card(
    root: &Path,
    component_home: &Path,
    address_kind: &str,
    hex: &str,
) -> Result<Vec<u8>, ExperimentCompositionError> {
    let home = if component_home.is_absolute() {
        component_home.to_path_buf()
    } else {
        root.join(component_home)
    };
    require_real_directory(&home, "experiment component home")?;
    let role_home = home.join(address_kind);
    require_real_directory(&role_home, &format!("{address_kind} component home"))?;
    let path = role_home.join(format!("{hex}.json"));
    let metadata = fs::symlink_metadata(&path).map_err(|error| {
        ExperimentCompositionError::new(format!(
            "cannot inspect {address_kind} component {}: {error}",
            path.display()
        ))
    })?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        return Err(ExperimentCompositionError::new(format!(
            "{address_kind} component {} must be one regular file, not a symlink or directory",
            path.display()
        )));
    }
    if metadata.len() > MAX_COMPONENT_CARD_BYTES {
        return Err(ExperimentCompositionError::new(format!(
            "{address_kind} component {} is {} bytes; maximum is {} bytes",
            path.display(),
            metadata.len(),
            MAX_COMPONENT_CARD_BYTES
        )));
    }
    fs::read(&path).map_err(|error| {
        ExperimentCompositionError::new(format!(
            "cannot read {address_kind} component {}: {error}",
            path.display()
        ))
    })
}

fn require_real_directory(path: &Path, noun: &str) -> Result<(), ExperimentCompositionError> {
    let metadata = fs::symlink_metadata(path).map_err(|error| {
        ExperimentCompositionError::new(format!("cannot inspect {noun} {}: {error}", path.display()))
    })?;
    if metadata.file_type().is_symlink() || !metadata.is_dir() {
        return Err(ExperimentCompositionError::new(format!(
            "{noun} {} must be a real directory",
            path.display()
        )));
    }
    Ok(())
}

fn parse_component_reference<'a>(
    reference: &'a str,
    address_kind: &str,
) -> Result<&'a str, ExperimentCompositionError> {
    let prefix = format!("{address_kind}:sha256:");
    let Some(hex) = reference.strip_prefix(&prefix) else {
        return Err(ExperimentCompositionError::new(format!(
            "{address_kind} component reference {reference:?} must start with {prefix:?}"
        )));
    };
    if hex.len() != 64
        || !hex
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return Err(ExperimentCompositionError::new(format!(
            "{address_kind} component reference {reference:?} must end with 64 lowercase hexadecimal digits"
        )));
    }
    Ok(hex)
}

fn normalize_recipe_card(mut card: RecipeComponentCardV2) -> Result<RecipeComponentCardV2, ExperimentCompositionError> {
    require_schema(&card.schema, RECIPE_COMPONENT_CARD_SCHEMA_V2, "recipe component card")?;
    normalize_handler_request(&mut card.world, ExperimentComponentRoleV2::World)?;
    normalize_handler_request(&mut card.evaluator, ExperimentComponentRoleV2::Evaluator)?;
    normalize_handler_request(&mut card.loader, ExperimentComponentRoleV2::Loader)?;
    normalize_handler_request(&mut card.trainer, ExperimentComponentRoleV2::Trainer)?;
    if card.context_length == 0 {
        return Err(ExperimentCompositionError::new(
            "recipe context_length must be positive",
        ));
    }
    normalize_grid(&mut card.grid)?;
    if let Some(factorial) = &mut card.factorial {
        factorial
            .normalize(&card.grid)
            .map_err(|error| ExperimentCompositionError::new(format!("invalid recipe factorial: {error}")))?;
    }
    Ok(card)
}

fn normalize_architecture_card(
    mut card: ArchitectureComponentCardV2,
) -> Result<ArchitectureComponentCardV2, ExperimentCompositionError> {
    require_schema(
        &card.schema,
        ARCHITECTURE_COMPONENT_CARD_SCHEMA_V2,
        "architecture component card",
    )?;
    normalize_handler_fields(
        &mut card.handler,
        &card.parameters,
        ExperimentComponentRoleV2::Architecture,
    )?;
    Ok(card)
}

fn normalize_action_card(mut card: ActionComponentCardV2) -> Result<ActionComponentCardV2, ExperimentCompositionError> {
    require_schema(&card.schema, ACTION_COMPONENT_CARD_SCHEMA_V2, "action component card")?;
    normalize_handler_fields(&mut card.handler, &card.parameters, ExperimentComponentRoleV2::Action)?;
    Ok(card)
}

fn normalize_density_card(
    mut card: DensityComponentCardV2,
) -> Result<DensityComponentCardV2, ExperimentCompositionError> {
    require_schema(&card.schema, DENSITY_COMPONENT_CARD_SCHEMA_V2, "density component card")?;
    normalize_handler_fields(&mut card.handler, &card.parameters, ExperimentComponentRoleV2::Density)?;
    Ok(card)
}

fn normalize_handler_request(
    request: &mut HandlerRequestV2,
    role: ExperimentComponentRoleV2,
) -> Result<(), ExperimentCompositionError> {
    normalize_handler_fields(&mut request.handler, &request.parameters, role)
}

fn normalize_handler_fields(
    handler: &mut String,
    parameters: &Value,
    role: ExperimentComponentRoleV2,
) -> Result<(), ExperimentCompositionError> {
    validate_token(handler, &format!("{} handler", role.value()))?;
    if !parameters.is_object() {
        return Err(ExperimentCompositionError::new(format!(
            "{} handler {handler:?} parameters must be a JSON object; use {{}} when it has no parameters",
            role.value()
        )));
    }
    Ok(())
}

pub(crate) fn normalize_capabilities(
    mut capabilities: ImplementationCapabilitiesV2,
    selected_implementation: &fern_experiment_types::ImplementationBundleHashV2,
) -> Result<ImplementationCapabilitiesV2, ExperimentCompositionError> {
    require_schema(
        &capabilities.schema,
        IMPLEMENTATION_CAPABILITIES_SCHEMA_V2,
        "implementation capability manifest",
    )?;
    if &capabilities.implementation != selected_implementation {
        return Err(ExperimentCompositionError::new(format!(
            "capability manifest belongs to {}, but the experiment selects {}",
            capabilities.implementation.as_str(),
            selected_implementation.as_str()
        )));
    }
    if capabilities.generators.is_empty() {
        return Err(ExperimentCompositionError::new(
            "implementation capability manifest must name at least one actual generator",
        ));
    }
    crate::implementation_bundle::validate_model_link_probe_v2(&capabilities.model_link_probe)?;
    for generator in &capabilities.generators {
        // CHG 2026-08-28: the generator provider is the C# capability authority's
        // own name, "Fern.Experiment" (csharp_resolution_warnings.rs expects that
        // exact spelling), so it may carry capitals where every other token
        // stays lowercase. Before this, every implementation probe refused.
        validate_provider_token(&generator.provider, "capability generator provider")?;
        validate_plain_sha256(&generator.source_sha256, "capability generator source_sha256")?;
    }
    for generator in &mut capabilities.generators {
        canonicalize_relative_path_set(
            &mut generator.source_paths,
            &format!("capability generator {} source_paths", generator.provider),
        )?;
    }
    capabilities
        .generators
        .sort_by(|left, right| (left.kind, &left.provider).cmp(&(right.kind, &right.provider)));
    for pair in capabilities.generators.windows(2) {
        if pair[0].kind == pair[1].kind && pair[0].provider == pair[1].provider {
            return Err(ExperimentCompositionError::new(format!(
                "implementation capability manifest repeats {:?} generator {:?}",
                pair[0].kind, pair[0].provider
            )));
        }
    }
    if capabilities.handlers.is_empty() {
        return Err(ExperimentCompositionError::new(
            "implementation capability manifest must name at least one handler",
        ));
    }
    for capability in &mut capabilities.handlers {
        validate_token(
            &capability.handler,
            &format!("{} capability handler", capability.role.value()),
        )?;
        validate_closed_parameter_schema(capability)?;
        validate_composition_schema(capability)?;
        normalize_cross_component_constraints(capability)?;
        for key in capability.emits.keys() {
            validate_token(
                key,
                &format!("{} {} emitted fact", capability.role.value(), capability.handler),
            )?;
        }
        canonicalize_token_set(
            &mut capability.provides,
            &format!("{} {} provides", capability.role.value(), capability.handler),
        )?;
        canonicalize_token_set(
            &mut capability.requires,
            &format!("{} {} requires", capability.role.value(), capability.handler),
        )?;
    }
    capabilities
        .handlers
        .sort_by(|left, right| (left.role, &left.handler).cmp(&(right.role, &right.handler)));
    for pair in capabilities.handlers.windows(2) {
        if pair[0].role == pair[1].role && pair[0].handler == pair[1].handler {
            return Err(ExperimentCompositionError::new(format!(
                "implementation capability manifest repeats {} handler {:?}",
                pair[0].role.value(),
                pair[0].handler
            )));
        }
    }
    Ok(capabilities)
}

fn validate_composition_schema(capability: &HandlerCapabilityV2) -> Result<(), ExperimentCompositionError> {
    let Some(schema) = &capability.composition_schema else {
        return Ok(());
    };
    let label = format!(
        "{} handler {:?} composition_schema",
        capability.role.value(),
        capability.handler
    );
    inspect_composition_schema(schema, &label)?;
    jsonschema::validator_for(schema)
        .map(|_| ())
        .map_err(|error| ExperimentCompositionError::new(format!("{label} is invalid JSON Schema: {error}")))
}

fn inspect_composition_schema(schema: &Value, label: &str) -> Result<(), ExperimentCompositionError> {
    match schema {
        Value::Object(object) => {
            for forbidden in ["$ref", "$dynamicRef", "default"] {
                if object.contains_key(forbidden) {
                    return Err(ExperimentCompositionError::new(format!(
                        "{label} uses {forbidden:?}; V2 composition checks must be self-contained and may not fill defaults"
                    )));
                }
            }
            for value in object.values() {
                inspect_composition_schema(value, label)?;
            }
        }
        Value::Array(values) => {
            for value in values {
                inspect_composition_schema(value, label)?;
            }
        }
        _ => {}
    }
    Ok(())
}

fn normalize_cross_component_constraints(
    capability: &mut HandlerCapabilityV2,
) -> Result<(), ExperimentCompositionError> {
    for constraint in &capability.cross_component_constraints {
        for (side, pointer) in [
            ("left_pointer", constraint.left_pointer.as_str()),
            ("right_pointer", constraint.right_pointer.as_str()),
        ] {
            validate_component_relative_pointer(pointer).map_err(|error| {
                ExperimentCompositionError::new(format!(
                    "{} handler {:?} cross-component {side} is invalid: {error}",
                    capability.role.value(),
                    capability.handler
                ))
            })?;
        }
        if constraint.left_pointer == constraint.right_pointer {
            return Err(ExperimentCompositionError::new(format!(
                "{} handler {:?} cross-component constraint compares one pointer to itself",
                capability.role.value(),
                capability.handler
            )));
        }
    }
    capability.cross_component_constraints.sort();
    for pair in capability.cross_component_constraints.windows(2) {
        if pair[0] == pair[1] {
            return Err(ExperimentCompositionError::new(format!(
                "{} handler {:?} repeats a cross-component constraint",
                capability.role.value(),
                capability.handler
            )));
        }
    }
    Ok(())
}

fn validate_closed_parameter_schema(capability: &HandlerCapabilityV2) -> Result<(), ExperimentCompositionError> {
    let label = format!("{} handler {:?}", capability.role.value(), capability.handler);
    inspect_parameter_schema(&capability.parameter_schema, &label, true)?;
    jsonschema::validator_for(&capability.parameter_schema)
        .map(|_| ())
        .map_err(|error| {
            ExperimentCompositionError::new(format!("{label} parameter_schema is invalid JSON Schema: {error}"))
        })
}

fn inspect_parameter_schema(schema: &Value, label: &str, top_level: bool) -> Result<(), ExperimentCompositionError> {
    let object = schema
        .as_object()
        .ok_or_else(|| ExperimentCompositionError::new(format!("{label} parameter_schema must be a JSON object")))?;
    for forbidden in [
        "$ref",
        "default",
        "allOf",
        "anyOf",
        "if",
        "then",
        "else",
        "patternProperties",
        "unevaluatedProperties",
    ] {
        if object.contains_key(forbidden) {
            return Err(ExperimentCompositionError::new(format!(
                "{label} parameter_schema uses {forbidden:?}; V2 requires one closed explicit object schema with no runtime defaults or alternate branches"
            )));
        }
    }
    if let Some(branches) = object.get("oneOf") {
        if top_level || object.len() != 1 {
            return Err(ExperimentCompositionError::new(format!(
                "{label} parameter_schema may use oneOf only as one nested explicit tagged union"
            )));
        }
        let branches = branches.as_array().ok_or_else(|| {
            ExperimentCompositionError::new(format!("{label} parameter_schema oneOf must be an array"))
        })?;
        if branches.len() < 2 {
            return Err(ExperimentCompositionError::new(format!(
                "{label} parameter_schema tagged union must declare at least two branches"
            )));
        }
        let nullable_closed_record = branches.len() == 2
            && branches
                .iter()
                .filter(|branch| branch.get("type").and_then(Value::as_str) == Some("null"))
                .count()
                == 1;
        let mut tags = BTreeSet::new();
        let mut saw_null = false;
        for (ordinal, branch) in branches.iter().enumerate() {
            let branch_label = format!("{label} tagged-union branch {ordinal}");
            let branch_object = branch
                .as_object()
                .ok_or_else(|| ExperimentCompositionError::new(format!("{branch_label} must be a JSON object")))?;
            if branch_object.get("type").and_then(Value::as_str) == Some("null") {
                if branch_object.len() != 1 || saw_null {
                    return Err(ExperimentCompositionError::new(format!(
                        "{label} parameter_schema may declare exactly one bare null branch"
                    )));
                }
                saw_null = true;
                continue;
            }
            inspect_parameter_schema(branch, &branch_label, false)?;
            if nullable_closed_record {
                continue;
            }
            let tag = branch_object
                .get("properties")
                .and_then(Value::as_object)
                .and_then(|properties| properties.get("kind"))
                .and_then(Value::as_object)
                .and_then(|kind| kind.get("const"))
                .and_then(Value::as_str)
                .ok_or_else(|| {
                    ExperimentCompositionError::new(format!(
                        "{branch_label} must be a closed object with one required string kind const"
                    ))
                })?;
            if !tags.insert(tag.to_string()) {
                return Err(ExperimentCompositionError::new(format!(
                    "{label} parameter_schema repeats tagged-union kind {tag:?}"
                )));
            }
        }
        if tags.is_empty() && !nullable_closed_record {
            return Err(ExperimentCompositionError::new(format!(
                "{label} parameter_schema tagged union has no closed object branch"
            )));
        }
        return Ok(());
    }
    let object_schema =
        top_level || object.get("type").and_then(Value::as_str) == Some("object") || object.contains_key("properties");
    if object_schema {
        if object.get("type").and_then(Value::as_str) != Some("object") {
            return Err(ExperimentCompositionError::new(format!(
                "{label} parameter_schema object must declare type=object"
            )));
        }
        if object.get("additionalProperties").and_then(Value::as_bool) != Some(false) {
            return Err(ExperimentCompositionError::new(format!(
                "{label} parameter_schema must declare additionalProperties=false so misspelled parameters fail closed"
            )));
        }
        let properties = object.get("properties").and_then(Value::as_object).ok_or_else(|| {
            ExperimentCompositionError::new(format!("{label} parameter_schema must declare properties"))
        })?;
        let required = match object.get("required") {
            Some(Value::Array(values)) => values
                .iter()
                .map(|value| {
                    value.as_str().map(str::to_string).ok_or_else(|| {
                        ExperimentCompositionError::new(format!(
                            "{label} parameter_schema required entries must be strings"
                        ))
                    })
                })
                .collect::<Result<BTreeSet<_>, _>>()?,
            None if properties.is_empty() => BTreeSet::new(),
            None => {
                return Err(ExperimentCompositionError::new(format!(
                    "{label} parameter_schema must require every declared property; V2 runtime does not fill omitted defaults"
                )));
            }
            Some(_) => {
                return Err(ExperimentCompositionError::new(format!(
                    "{label} parameter_schema required must be an array"
                )));
            }
        };
        let property_names = properties.keys().cloned().collect::<BTreeSet<_>>();
        if required != property_names {
            return Err(ExperimentCompositionError::new(format!(
                "{label} parameter_schema required fields {required:?} do not exactly equal properties {property_names:?}; every V2 parameter must be explicit"
            )));
        }
        for (name, property) in properties {
            inspect_parameter_schema(property, &format!("{label} parameter {name:?}"), false)?;
        }
    }
    if let Some(items) = object.get("items") {
        inspect_parameter_schema(items, &format!("{label} array items"), false)?;
    }
    Ok(())
}

fn resolve_arm(
    components: ResolvedComponentsWithReferences,
    implementation: fern_experiment_types::ImplementationBundleHashV2,
    capabilities: &ImplementationCapabilitiesV2,
    capability_manifest_sha256: String,
) -> Result<ResolvedExperimentArmV2, ExperimentCompositionError> {
    let configuration_facts = validate_selected_handlers(&components.cards, capabilities)?;
    let manifest = ResolvedExperimentManifestV2 {
        schema: EXPERIMENT_MANIFEST_SCHEMA_V2.to_string(),
        implementation,
        capability_manifest_sha256,
        component_refs: components.references,
        components: components.cards,
        configuration_facts,
    };
    let manifest_sha256 = prefixed_sha256("experiment", &manifest)?;
    Ok(ResolvedExperimentArmV2 {
        manifest_sha256,
        manifest,
    })
}

fn validate_selected_handlers(
    components: &ResolvedExperimentComponentsV2,
    capabilities: &ImplementationCapabilitiesV2,
) -> Result<BTreeMap<String, Value>, ExperimentCompositionError> {
    let mut selected = Vec::new();
    select_handler(
        &mut selected,
        capabilities,
        ExperimentComponentRoleV2::World,
        &components.recipe.world,
    )?;
    select_handler(
        &mut selected,
        capabilities,
        ExperimentComponentRoleV2::Evaluator,
        &components.recipe.evaluator,
    )?;
    select_handler(
        &mut selected,
        capabilities,
        ExperimentComponentRoleV2::Loader,
        &components.recipe.loader,
    )?;
    select_handler(
        &mut selected,
        capabilities,
        ExperimentComponentRoleV2::Trainer,
        &components.recipe.trainer,
    )?;
    refuse_zero_epochs_for_a_training_execution(&components.recipe.trainer.parameters)?;
    select_handler_fields(
        &mut selected,
        capabilities,
        ExperimentComponentRoleV2::Architecture,
        &components.architecture.handler,
        &components.architecture.parameters,
    )?;
    if let ResolvedActionV2::Conditioned { card } = &components.action {
        select_handler_fields(
            &mut selected,
            capabilities,
            ExperimentComponentRoleV2::Action,
            &card.handler,
            &card.parameters,
        )?;
    }
    select_handler_fields(
        &mut selected,
        capabilities,
        ExperimentComponentRoleV2::Density,
        &components.density.handler,
        &components.density.parameters,
    )?;

    let component_values = factorial_component_values(components)?;
    if components.recipe.factorial.is_some() {
        let architecture_capability = selected
            .iter()
            .copied()
            .find(|capability| capability.role == ExperimentComponentRoleV2::Architecture)
            .ok_or_else(|| ExperimentCompositionError::new("factorial has no selected architecture capability"))?;
        for component_value in &component_values {
            let parameters = component_value.pointer("/architecture/parameters").ok_or_else(|| {
                ExperimentCompositionError::new("factorial component projection has no architecture parameters")
            })?;
            validate_capability_parameters(
                architecture_capability,
                parameters,
                capabilities.implementation.as_str(),
            )?;
        }
    }

    let provided = selected
        .iter()
        .flat_map(|capability| capability.provides.iter().cloned())
        .collect::<BTreeSet<_>>();
    let missing = selected
        .iter()
        .flat_map(|capability| {
            capability
                .requires
                .iter()
                .filter(|required| !provided.contains(*required))
                .map(|required| format!("{}:{} requires {required}", capability.role.value(), capability.handler))
        })
        .collect::<Vec<_>>();
    if !missing.is_empty() {
        return Err(ExperimentCompositionError::new(format!(
            "selected handlers have unsatisfied interfaces: {}",
            missing.join("; ")
        )));
    }
    for capability in &selected {
        for component_value in &component_values {
            if let Some(schema) = &capability.composition_schema {
                let validator = jsonschema::validator_for(schema).map_err(|error| {
                    ExperimentCompositionError::new(format!(
                        "{} handler {:?} has an invalid generated composition schema: {error}",
                        capability.role.value(),
                        capability.handler
                    ))
                })?;
                let errors = validator
                    .iter_errors(component_value)
                    .map(|error| format!("{} at {}", error, error.instance_path()))
                    .collect::<Vec<_>>();
                if !errors.is_empty() {
                    return Err(ExperimentCompositionError::new(format!(
                        "{} handler {:?} rejects a factorial cross-card composition: {}",
                        capability.role.value(),
                        capability.handler,
                        errors.join("; ")
                    )));
                }
            }
            for constraint in &capability.cross_component_constraints {
                validate_cross_component_constraint(component_value, capability, constraint)?;
            }
        }
    }
    let mut facts = BTreeMap::new();
    for capability in selected {
        for (key, value) in &capability.emits {
            if facts.insert(key.clone(), value.clone()).is_some() {
                return Err(ExperimentCompositionError::new(format!(
                    "selected handlers give emitted fact {key:?} more than one owner; each configuration fact must have exactly one owner"
                )));
            }
        }
    }
    Ok(facts)
}

fn factorial_component_values(
    components: &ResolvedExperimentComponentsV2,
) -> Result<Vec<Value>, ExperimentCompositionError> {
    let base = serde_json::to_value(components)
        .map_err(|error| ExperimentCompositionError::new(format!("cannot inspect resolved components: {error}")))?;
    let Some(factorial) = &components.recipe.factorial else {
        return Ok(vec![base]);
    };
    let mut variants = vec![base];
    for factor in &factorial.factors {
        let mut expanded = Vec::with_capacity(variants.len() * 2);
        for variant in variants {
            for level in &factor.levels {
                let mut cell = variant.clone();
                let parameters = cell
                    .pointer_mut("/architecture/parameters")
                    .and_then(Value::as_object_mut)
                    .ok_or_else(|| {
                        ExperimentCompositionError::new("factorial component projection has no architecture object")
                    })?;
                parameters.insert(factor.model_knob.clone(), level.clone());
                expanded.push(cell);
            }
        }
        variants = expanded;
    }
    Ok(variants)
}

/// Zero epochs is refused for a TRAINING execution, and legal for the others.
///
/// Owner ruling 2026-08-26: a run that never enters the training loop still
/// writes a model, and selection compares one checkpoint per epoch, so a
/// zero-epoch training run selects from nothing and then reports a score for a
/// model nobody chose.
///
/// It is NOT a schema minimum, and that distinction cost a wrong commit on the
/// day this was written. Commit 4c67c89b deliberately relaxed the trainer
/// schema from `minimum: 1` to `minimum: 0` in the same change that added
/// `evaluation_only`, because an evaluation-only or pretrained-inference
/// execution declares no training -- and every zero-shot model in this campaign
/// is one of those. Raising the schema minimum back to 1 refuses zero epochs
/// correctly for training and makes Chronos, TiRex and Zeus unauthorable, which
/// is why the rule has to read execution_kind rather than the number alone.
///
/// Three epochs is thin and only warns, in `resolution_warnings`. Zero is not
/// thin; for a training execution it is absent.
fn refuse_zero_epochs_for_a_training_execution(parameters: &Value) -> Result<(), ExperimentCompositionError> {
    if parameters.get("execution_kind").and_then(Value::as_str) != Some("train") {
        return Ok(());
    }
    if parameters.get("epochs").and_then(Value::as_u64) == Some(0) {
        return Err(ExperimentCompositionError::new(
            "a training execution declares 0 epochs, and the minimum for execution_kind \"train\" is 1: \
             selection compares one checkpoint per epoch, so this run would select from nothing and still \
             report a score. An execution meant to do no training declares execution_kind \"evaluation_only\" \
             or \"pretrained_inference\", where 0 epochs is legal."
                .to_string(),
        ));
    }
    Ok(())
}

fn select_handler<'a>(
    selected: &mut Vec<&'a HandlerCapabilityV2>,
    capabilities: &'a ImplementationCapabilitiesV2,
    role: ExperimentComponentRoleV2,
    request: &HandlerRequestV2,
) -> Result<(), ExperimentCompositionError> {
    select_handler_fields(selected, capabilities, role, &request.handler, &request.parameters)
}

fn select_handler_fields<'a>(
    selected: &mut Vec<&'a HandlerCapabilityV2>,
    capabilities: &'a ImplementationCapabilitiesV2,
    role: ExperimentComponentRoleV2,
    handler: &str,
    parameters: &Value,
) -> Result<(), ExperimentCompositionError> {
    let capability = capabilities
        .handlers
        .iter()
        .find(|capability| capability.role == role && capability.handler == handler)
        .ok_or_else(|| {
            ExperimentCompositionError::new(format!(
                "implementation {} does not report {} handler {handler:?}",
                capabilities.implementation.as_str(),
                role.value()
            ))
        })?;
    validate_capability_parameters(capability, parameters, capabilities.implementation.as_str())?;
    selected.push(capability);
    Ok(())
}

fn validate_capability_parameters(
    capability: &HandlerCapabilityV2,
    parameters: &Value,
    implementation: &str,
) -> Result<(), ExperimentCompositionError> {
    let validator = jsonschema::validator_for(&capability.parameter_schema).map_err(|error| {
        ExperimentCompositionError::new(format!(
            "{} handler {:?} has an invalid generated parameter schema: {error}",
            capability.role.value(),
            capability.handler
        ))
    })?;
    let errors = validator
        .iter_errors(parameters)
        .map(|error| format!("{} at {}", error, error.instance_path()))
        .collect::<Vec<_>>();
    if !errors.is_empty() {
        return Err(ExperimentCompositionError::new(format!(
            "{} handler {:?} parameters are unsupported by implementation {}: {}",
            capability.role.value(),
            capability.handler,
            implementation,
            errors.join("; ")
        )));
    }
    Ok(())
}

fn validate_cross_component_constraint(
    components: &Value,
    capability: &HandlerCapabilityV2,
    constraint: &CrossComponentConstraintV2,
) -> Result<(), ExperimentCompositionError> {
    let left = components.pointer(&constraint.left_pointer).ok_or_else(|| {
        ExperimentCompositionError::new(format!(
            "{} handler {:?} cross-card constraint cannot find left pointer {:?}",
            capability.role.value(),
            capability.handler,
            constraint.left_pointer
        ))
    })?;
    let right = components.pointer(&constraint.right_pointer).ok_or_else(|| {
        ExperimentCompositionError::new(format!(
            "{} handler {:?} cross-card constraint cannot find right pointer {:?}",
            capability.role.value(),
            capability.handler,
            constraint.right_pointer
        ))
    })?;
    let satisfied = match constraint.relation {
        CrossComponentRelationV2::Equal => left == right,
        CrossComponentRelationV2::LessThan => match (left.as_f64(), right.as_f64()) {
            (Some(left), Some(right)) => left.is_finite() && right.is_finite() && left < right,
            _ => false,
        },
        CrossComponentRelationV2::EveryMultipleOf => {
            let divisor = right.as_u64().filter(|value| *value > 0).ok_or_else(|| {
                ExperimentCompositionError::new(format!(
                    "{} handler {:?} cross-card divisor at {:?} must be a positive integer",
                    capability.role.value(),
                    capability.handler,
                    constraint.right_pointer
                ))
            })?;
            match left {
                Value::Array(values) => values
                    .iter()
                    .all(|value| value.as_u64().is_some_and(|value| value % divisor == 0)),
                value => value.as_u64().is_some_and(|value| value % divisor == 0),
            }
        }
    };
    if !satisfied {
        return Err(ExperimentCompositionError::new(format!(
            "{} handler {:?} cross-card constraint failed: {} {:?} {:?}; left={}, right={}",
            capability.role.value(),
            capability.handler,
            constraint.left_pointer,
            constraint.relation,
            constraint.right_pointer,
            left,
            right
        )));
    }
    Ok(())
}

fn verify_arm(
    arm: &ResolvedExperimentArmV2,
    capabilities: &ImplementationCapabilitiesV2,
) -> Result<(), ExperimentCompositionError> {
    require_schema(
        &arm.manifest.schema,
        EXPERIMENT_MANIFEST_SCHEMA_V2,
        "resolved run manifest",
    )?;
    validate_prefixed_sha256(
        &arm.manifest.capability_manifest_sha256,
        "capabilities",
        "capability_manifest_sha256",
    )?;
    let expected_capability_manifest_sha256 = prefixed_sha256("capabilities", capabilities)?;
    if arm.manifest.capability_manifest_sha256 != expected_capability_manifest_sha256 {
        return Err(ExperimentCompositionError::new(format!(
            "resolved run manifest capability identity {} does not match its pinned implementation {}",
            arm.manifest.capability_manifest_sha256, expected_capability_manifest_sha256
        )));
    }
    let normalized = normalize_resolved_components(arm.manifest.components.clone())?;
    if normalized != arm.manifest.components {
        return Err(ExperimentCompositionError::new(
            "resolved run manifest components are not canonical",
        ));
    }
    let expected_configuration_facts = validate_selected_handlers(&arm.manifest.components, capabilities)?;
    if arm.manifest.configuration_facts != expected_configuration_facts {
        return Err(ExperimentCompositionError::new(format!(
            "resolved run manifest configuration_facts {:?} do not match facts {:?} rederived from its pinned implementation",
            arm.manifest.configuration_facts, expected_configuration_facts
        )));
    }
    for key in arm.manifest.configuration_facts.keys() {
        validate_token(key, "resolved configuration fact")?;
    }
    let expected_refs = component_references(&arm.manifest.components)?;
    if expected_refs != arm.manifest.component_refs {
        return Err(ExperimentCompositionError::new(format!(
            "resolved run manifest component_refs {:?} do not match component bodies {:?}",
            arm.manifest.component_refs, expected_refs
        )));
    }
    let expected_manifest_sha256 = prefixed_sha256("experiment", &arm.manifest)?;
    if expected_manifest_sha256 != arm.manifest_sha256 {
        return Err(ExperimentCompositionError::new(format!(
            "resolved run manifest identity {} does not match its canonical bytes {}",
            arm.manifest_sha256, expected_manifest_sha256
        )));
    }
    Ok(())
}

fn normalize_resolved_components(
    components: ResolvedExperimentComponentsV2,
) -> Result<ResolvedExperimentComponentsV2, ExperimentCompositionError> {
    Ok(ResolvedExperimentComponentsV2 {
        recipe: normalize_recipe_card(components.recipe)?,
        architecture: normalize_architecture_card(components.architecture)?,
        action: match components.action {
            ResolvedActionV2::StateOnly => ResolvedActionV2::StateOnly,
            ResolvedActionV2::Conditioned { card } => ResolvedActionV2::Conditioned {
                card: normalize_action_card(card)?,
            },
        },
        density: normalize_density_card(components.density)?,
    })
}

fn component_references(
    components: &ResolvedExperimentComponentsV2,
) -> Result<ExperimentComponentReferencesV2, ExperimentCompositionError> {
    Ok(ExperimentComponentReferencesV2 {
        recipe: component_reference("recipe", &components.recipe)?,
        architecture: component_reference("architecture", &components.architecture)?,
        action: match &components.action {
            ResolvedActionV2::StateOnly => None,
            ResolvedActionV2::Conditioned { card } => Some(component_reference("action", card)?),
        },
        density: component_reference("density", &components.density)?,
    })
}

fn require_exact_declared_changes(
    control: &ResolvedExperimentComponentsV2,
    treatment: &ResolvedExperimentComponentsV2,
    declared: &[String],
) -> Result<(), ExperimentCompositionError> {
    let control = serde_json::to_value(control)
        .map_err(|error| ExperimentCompositionError::new(format!("cannot inspect control components: {error}")))?;
    let treatment = serde_json::to_value(treatment)
        .map_err(|error| ExperimentCompositionError::new(format!("cannot inspect treatment components: {error}")))?;
    let mut observed = Vec::new();
    collect_differences(&control, &treatment, "/components", &mut observed);
    observed.sort();
    let declared_set = declared.iter().cloned().collect::<BTreeSet<_>>();
    let observed_set = observed.iter().cloned().collect::<BTreeSet<_>>();
    if declared_set != observed_set {
        let undeclared = observed_set.difference(&declared_set).cloned().collect::<Vec<_>>();
        let unchanged = declared_set.difference(&observed_set).cloned().collect::<Vec<_>>();
        return Err(ExperimentCompositionError::new(format!(
            "ablation differences do not equal declared_changes; undeclared={undeclared:?}, declared_but_unchanged={unchanged:?}, observed={observed:?}"
        )));
    }
    if observed.is_empty() {
        return Err(ExperimentCompositionError::new(
            "ablation control and treatment are identical; an ablation must change at least one declared field",
        ));
    }
    Ok(())
}

fn collect_differences(control: &Value, treatment: &Value, path: &str, output: &mut Vec<String>) {
    match (control, treatment) {
        (Value::Object(left), Value::Object(right)) => {
            let keys = left.keys().chain(right.keys()).collect::<BTreeSet<_>>();
            for key in keys {
                let child = format!("{path}/{}", escape_json_pointer_token(key));
                match (left.get(key), right.get(key)) {
                    (Some(left), Some(right)) => collect_differences(left, right, &child, output),
                    _ => output.push(child),
                }
            }
        }
        (Value::Array(left), Value::Array(right)) if left != right => output.push(path.to_string()),
        _ if control != treatment => output.push(path.to_string()),
        _ => {}
    }
}

fn escape_json_pointer_token(raw: &str) -> String {
    raw.replace('~', "~0").replace('/', "~1")
}

fn normalize_declared_changes(mut changes: Vec<String>) -> Result<Vec<String>, ExperimentCompositionError> {
    if changes.is_empty() {
        return Err(ExperimentCompositionError::new(
            "ablation declared_changes must name at least one exact /components/... JSON pointer",
        ));
    }
    for pointer in &changes {
        validate_declared_pointer(pointer)?;
    }
    changes.sort();
    for pair in changes.windows(2) {
        if pair[0] == pair[1] {
            return Err(ExperimentCompositionError::new(format!(
                "ablation declared_changes repeats JSON pointer {:?}",
                pair[0]
            )));
        }
    }
    Ok(changes)
}

fn normalize_declared_fact_changes(mut changes: Vec<String>) -> Result<Vec<String>, ExperimentCompositionError> {
    canonicalize_token_set(&mut changes, "ablation declared_fact_changes")?;
    Ok(changes)
}

fn require_exact_declared_fact_changes(
    control: &BTreeMap<String, Value>,
    treatment: &BTreeMap<String, Value>,
    declared: &[String],
) -> Result<(), ExperimentCompositionError> {
    let observed = control
        .keys()
        .chain(treatment.keys())
        .collect::<BTreeSet<_>>()
        .into_iter()
        .filter(|key| control.get(*key) != treatment.get(*key))
        .cloned()
        .collect::<BTreeSet<_>>();
    let declared = declared.iter().cloned().collect::<BTreeSet<_>>();
    if observed != declared {
        let undeclared = observed.difference(&declared).cloned().collect::<Vec<_>>();
        let unchanged = declared.difference(&observed).cloned().collect::<Vec<_>>();
        return Err(ExperimentCompositionError::new(format!(
            "ablation semantic facts do not equal declared_fact_changes; undeclared={undeclared:?}, declared_but_unchanged={unchanged:?}, observed={observed:?}"
        )));
    }
    Ok(())
}

fn validate_declared_pointer(pointer: &str) -> Result<(), ExperimentCompositionError> {
    if !pointer.starts_with("/components/") || pointer.ends_with('/') {
        return Err(ExperimentCompositionError::new(format!(
            "ablation change {pointer:?} must be an exact JSON pointer below /components/"
        )));
    }
    let bytes = pointer.as_bytes();
    let mut index = 0;
    while index < bytes.len() {
        if bytes[index] == b'~' {
            if index + 1 >= bytes.len() || !matches!(bytes[index + 1], b'0' | b'1') {
                return Err(ExperimentCompositionError::new(format!(
                    "ablation change {pointer:?} contains an invalid JSON pointer escape"
                )));
            }
            index += 2;
        } else {
            index += 1;
        }
    }
    Ok(())
}

fn validate_component_relative_pointer(pointer: &str) -> Result<(), ExperimentCompositionError> {
    if !pointer.starts_with('/') || pointer == "/" || pointer.ends_with('/') {
        return Err(ExperimentCompositionError::new(format!(
            "pointer {pointer:?} must be a non-root JSON pointer inside the resolved components object"
        )));
    }
    let bytes = pointer.as_bytes();
    let mut index = 0;
    while index < bytes.len() {
        if bytes[index] == b'~' {
            if index + 1 >= bytes.len() || !matches!(bytes[index + 1], b'0' | b'1') {
                return Err(ExperimentCompositionError::new(format!(
                    "pointer {pointer:?} contains an invalid JSON pointer escape"
                )));
            }
            index += 2;
        } else {
            index += 1;
        }
    }
    Ok(())
}

fn normalize_grid(grid: &mut std::collections::BTreeMap<String, Vec<Value>>) -> Result<(), ExperimentCompositionError> {
    if grid.is_empty() {
        return Err(ExperimentCompositionError::new(
            "recipe grid must declare at least one finite axis",
        ));
    }
    for (axis, values) in grid {
        validate_token(axis, "recipe grid axis")?;
        if values.is_empty() {
            return Err(ExperimentCompositionError::new(format!(
                "recipe grid axis {axis:?} must contain at least one value"
            )));
        }
        let mut keyed = values
            .drain(..)
            .map(|value| canonical_json_bytes(&value).map(|key| (key, value)))
            .collect::<Result<Vec<_>, _>>()?;
        keyed.sort_by(|left, right| left.0.cmp(&right.0));
        for pair in keyed.windows(2) {
            if pair[0].0 == pair[1].0 {
                return Err(ExperimentCompositionError::new(format!(
                    "recipe grid axis {axis:?} repeats value {}",
                    pair[0].1
                )));
            }
        }
        values.extend(keyed.into_iter().map(|(_, value)| value));
    }
    Ok(())
}

fn normalize_budget(
    budget: Option<ExperimentBudgetV2>,
) -> Result<Option<ExperimentBudgetV2>, ExperimentCompositionError> {
    if budget.as_ref().is_some_and(|budget| budget.max_cells == 0) {
        return Err(ExperimentCompositionError::new(
            "experiment budget max_cells must be positive",
        ));
    }
    if budget
        .as_ref()
        .and_then(|budget| budget.max_gpu_seconds)
        .is_some_and(|seconds| seconds == 0)
    {
        return Err(ExperimentCompositionError::new(
            "experiment budget max_gpu_seconds must be positive",
        ));
    }
    Ok(budget)
}

fn normalize_cost_estimate(
    cost: Option<ExperimentCostEstimateV2>,
) -> Result<Option<ExperimentCostEstimateV2>, ExperimentCompositionError> {
    cost.map(|mut cost| {
        if cost.gpu_seconds_per_cell == 0 {
            return Err(ExperimentCompositionError::new(
                "experiment cost estimate gpu_seconds_per_cell must be positive",
            ));
        }
        cost.basis = normalize_nonempty(cost.basis, "experiment cost estimate basis")?;
        Ok(cost)
    })
    .transpose()
}

fn validate_inference(
    requirement: Option<InferentialRequirementV2>,
) -> Result<Option<InferentialFeasibilityV2>, ExperimentCompositionError> {
    requirement
        .map(|requirement| {
            let (paired_units, alpha) = match requirement {
                InferentialRequirementV2::ExactTwoSidedSign {
                    paired_units,
                    alpha,
                } => (paired_units, alpha),
            };
            if paired_units == 0 {
                return Err(ExperimentCompositionError::new(
                    "inferential requirement paired_units must be positive",
                ));
            }
            if !alpha.is_finite() || !(0.0 < alpha && alpha < 1.0) {
                return Err(ExperimentCompositionError::new(format!(
                    "inferential requirement alpha must be finite and strictly between 0 and 1, got {alpha}"
                )));
            }
            let minimum_attainable_p = if paired_units > 1075 {
                0.0
            } else {
                2.0_f64.powf(1.0 - paired_units as f64)
            };
            if minimum_attainable_p > alpha {
                return Err(ExperimentCompositionError::new(format!(
                    "exact two-sided sign test with {paired_units} paired units cannot reach declared alpha {alpha}; minimum attainable p is {minimum_attainable_p}"
                )));
            }
            Ok(InferentialFeasibilityV2 {
                requirement,
                minimum_attainable_p,
            })
        })
        .transpose()
}

fn verify_inference(observed: Option<InferentialFeasibilityV2>) -> Result<(), ExperimentCompositionError> {
    let expected = validate_inference(observed.map(|value| value.requirement))?;
    if expected != observed {
        return Err(ExperimentCompositionError::new(
            "saved inferential feasibility does not match its declared exact test",
        ));
    }
    Ok(())
}

fn recipe_cell_count(recipe: &RecipeComponentCardV2) -> Result<u64, ExperimentCompositionError> {
    recipe.grid.iter().try_fold(1_u64, |product, (axis, values)| {
        let count = u64::try_from(values.len()).map_err(|_| {
            ExperimentCompositionError::new(format!("recipe grid axis {axis:?} length does not fit u64"))
        })?;
        product.checked_mul(count).ok_or_else(|| {
            ExperimentCompositionError::new(format!(
                "recipe grid cell count overflows u64 while multiplying axis {axis:?}"
            ))
        })
    })
}

/// Refuse an evaluation term list whose weights sum to zero.
///
/// The training worker keeps the best checkpoint on a STRICT decrease of the
/// validation objective, and that objective is the weighted sum of the
/// evaluation terms — the `role` field is documentation and is never read by
/// the loss deck. Weights that sum to zero therefore make the objective
/// exactly 0.0 at every epoch: it improves once, at the first epoch against an
/// initial best of infinity, and never again. Validation-best selection
/// freezes at the first epoch and the run reports an untrained model whatever
/// its epoch count.
///
/// Measured on the Level 0A paired canary, 2026-08-21: accepted checkpoints at
/// 300 and at 3000 epochs were bit-identical across all 197 tensors and sat
/// 6.0e-04 from initialization, at a learning rate of 3e-04 with two optimizer
/// steps per epoch.
fn refuse_zero_weight_validation_objective(
    arm: &str,
    recipe: &RecipeComponentCardV2,
) -> Result<(), ExperimentCompositionError> {
    let Some(terms) = recipe.evaluator.parameters.get("eval").and_then(Value::as_array) else {
        return Ok(());
    };
    let mut declared = Vec::with_capacity(terms.len());
    let mut total_weight = 0.0_f64;
    for term in terms {
        let name = term.get("name").and_then(Value::as_str).unwrap_or("<unnamed>");
        let role = term.get("role").and_then(Value::as_str).unwrap_or("<no role>");
        let weight = term.get("weight").and_then(Value::as_f64).unwrap_or(0.0);
        declared.push(format!("{name} ({role}, weight {weight})"));
        // CHG 2026-08-21 (review): the schema refuses a negative weight first;
        // this keeps the refusal even for a record that bypassed the schema,
        // because `abs()` here would have let weight -1 read as an objective.
        if weight < 0.0 {
            return Err(ExperimentCompositionError::new(format!(
                "arm {arm:?} declares evaluation term {name} ({role}) with negative weight {weight}: \
                 the loss deck sums weight * value, so a negative weight rewards a worse loss; \
                 weights are non-negative and at least one must be strictly positive."
            )));
        }
        total_weight += weight;
    }
    if total_weight <= 0.0 {
        let listed = if declared.is_empty() {
            "no evaluation terms at all".to_string()
        } else {
            declared.join(", ")
        };
        return Err(ExperimentCompositionError::new(format!(
            "arm {arm:?} declares a validation objective whose weights sum to zero: {listed}. \
             A validation objective that is identically zero cannot select a checkpoint: it \
             improves once at the first epoch and never again, so the run reports its first-epoch \
             model whatever its epoch count. Give at least one evaluation term a non-zero weight."
        )));
    }
    Ok(())
}

fn workload_preview(
    arms: &[(&str, &ResolvedExperimentArmV2)],
    budget: Option<&ExperimentBudgetV2>,
    cost_estimate: Option<&ExperimentCostEstimateV2>,
) -> Result<WorkloadPreviewV2, ExperimentCompositionError> {
    let mut previews = Vec::with_capacity(arms.len());
    let mut total_cells = 0_u64;
    for (name, arm) in arms {
        refuse_zero_weight_validation_objective(name, &arm.manifest.components.recipe)?;
        let cells = recipe_cell_count(&arm.manifest.components.recipe)?;
        total_cells = total_cells.checked_add(cells).ok_or_else(|| {
            ExperimentCompositionError::new("experiment workload cell count overflows u64 across arms")
        })?;
        previews.push(WorkloadArmPreviewV2 {
            arm: (*name).to_string(),
            cells,
        });
    }
    if let Some(budget) = budget {
        if total_cells > budget.max_cells {
            return Err(ExperimentCompositionError::new(format!(
                "resolved experiment expands to {total_cells} cells, exceeding the declared max_cells budget {}",
                budget.max_cells
            )));
        }
    }
    let estimated_gpu_seconds = cost_estimate
        .map(|cost| {
            total_cells
                .checked_mul(cost.gpu_seconds_per_cell)
                .ok_or_else(|| ExperimentCompositionError::new("experiment estimated GPU seconds overflow u64"))
        })
        .transpose()?;
    if budget.and_then(|budget| budget.max_gpu_seconds).is_some() && estimated_gpu_seconds.is_none() {
        return Err(ExperimentCompositionError::new(
            "experiment budget max_gpu_seconds requires an explicit cost_estimate",
        ));
    }
    if let (Some(estimate), Some(maximum)) = (estimated_gpu_seconds, budget.and_then(|budget| budget.max_gpu_seconds)) {
        if estimate > maximum {
            return Err(ExperimentCompositionError::new(format!(
                "resolved experiment estimates {estimate} GPU seconds, exceeding the declared max_gpu_seconds budget {maximum}"
            )));
        }
    }
    Ok(WorkloadPreviewV2 {
        arms: previews,
        total_cells,
        max_cells: budget.map(|budget| budget.max_cells),
        estimated_gpu_seconds,
        gpu_seconds_per_cell: cost_estimate.map(|cost| cost.gpu_seconds_per_cell),
        estimate_basis: cost_estimate.map(|cost| cost.basis.clone()),
        max_gpu_seconds: budget.and_then(|budget| budget.max_gpu_seconds),
    })
}

fn verify_workload(
    observed: &WorkloadPreviewV2,
    arms: &[(&str, &ResolvedExperimentArmV2)],
) -> Result<(), ExperimentCompositionError> {
    let budget = observed.max_cells.map(|max_cells| ExperimentBudgetV2 {
        max_cells,
        max_gpu_seconds: observed.max_gpu_seconds,
    });
    let cost_estimate = match (observed.gpu_seconds_per_cell, observed.estimate_basis.clone()) {
        (Some(gpu_seconds_per_cell), Some(basis)) => Some(ExperimentCostEstimateV2 {
            gpu_seconds_per_cell,
            basis,
        }),
        (None, None) => None,
        _ => {
            return Err(ExperimentCompositionError::new(
                "saved workload cost estimate must carry both gpu_seconds_per_cell and estimate_basis",
            ));
        }
    };
    let expected = workload_preview(arms, budget.as_ref(), cost_estimate.as_ref())?;
    if &expected != observed {
        return Err(ExperimentCompositionError::new(format!(
            "saved workload preview {observed:?} does not match resolved grid {expected:?}"
        )));
    }
    Ok(())
}

fn inline_component_sources(components: ResolvedExperimentComponentsV2) -> ExperimentComponentsSourceV2 {
    ExperimentComponentsSourceV2 {
        recipe: ComponentSourceV2::Inline(components.recipe),
        architecture: ComponentSourceV2::Inline(components.architecture),
        action: match components.action {
            ResolvedActionV2::StateOnly => ActionSourceV2::StateOnly,
            ResolvedActionV2::Conditioned { card } => ActionSourceV2::Conditioned {
                component: ComponentSourceV2::Inline(card),
            },
        },
        density: ComponentSourceV2::Inline(components.density),
    }
}

fn set_component_pointer(
    target: &mut Value,
    pointer: &str,
    replacement: Value,
) -> Result<(), ExperimentCompositionError> {
    let relative = pointer.strip_prefix("/components").ok_or_else(|| {
        ExperimentCompositionError::new(format!("fork pointer {pointer:?} must begin with /components/"))
    })?;
    validate_component_relative_pointer(relative)?;
    if let Some(slot) = target.pointer_mut(relative) {
        *slot = replacement;
        return Ok(());
    }

    let (parent_pointer, raw_key) = relative
        .rsplit_once('/')
        .ok_or_else(|| ExperimentCompositionError::new(format!("fork pointer {pointer:?} has no component field")))?;
    let parent = target.pointer_mut(parent_pointer).ok_or_else(|| {
        ExperimentCompositionError::new(format!(
            "fork pointer {pointer:?} has no existing parent object {parent_pointer:?}"
        ))
    })?;
    let object = parent.as_object_mut().ok_or_else(|| {
        ExperimentCompositionError::new(format!(
            "fork pointer {pointer:?} can add a field only below an existing JSON object"
        ))
    })?;
    let key = decode_json_pointer_token(raw_key);
    if object.insert(key, replacement).is_some() {
        return Err(ExperimentCompositionError::new(format!(
            "fork pointer {pointer:?} unexpectedly replaced an existing field through the add path"
        )));
    }
    Ok(())
}

fn decode_json_pointer_token(raw: &str) -> String {
    raw.replace("~1", "/").replace("~0", "~")
}

fn verify_fork_resolution_v2(
    parent: &ExperimentResolutionV2,
    fork: &ExperimentForkResolutionV2,
) -> Result<(), ExperimentCompositionError> {
    require_schema(
        &fork.schema,
        EXPERIMENT_FORK_RESOLUTION_SCHEMA_V2,
        "experiment fork resolution",
    )?;
    verify_experiment_resolution_v2(parent)?;
    verify_experiment_resolution_v2(&fork.resolved)?;
    if fork.parent_manifest_sha256 != parent.resolved.manifest_sha256 {
        return Err(ExperimentCompositionError::new(
            "experiment fork parent_manifest_sha256 does not name the supplied checked parent",
        ));
    }
    if fork.inherited_implementation != parent.resolved.manifest.implementation
        || fork.resolved.resolved.manifest.implementation != parent.resolved.manifest.implementation
    {
        return Err(ExperimentCompositionError::new(
            "experiment fork changed implementation; use a separate probed implementation-bundle operation",
        ));
    }
    if fork.resolved.resolved.manifest.capability_manifest_sha256 != parent.resolved.manifest.capability_manifest_sha256
    {
        return Err(ExperimentCompositionError::new(
            "experiment fork changed the implementation capability manifest",
        ));
    }
    if fork.workload != fork.resolved.workload {
        return Err(ExperimentCompositionError::new(
            "experiment fork workload does not match its resolved child",
        ));
    }
    let mut observed = Vec::new();
    let parent_components = serde_json::to_value(&parent.resolved.manifest.components)
        .map_err(|error| ExperimentCompositionError::new(format!("cannot inspect fork parent: {error}")))?;
    let child_components = serde_json::to_value(&fork.resolved.resolved.manifest.components)
        .map_err(|error| ExperimentCompositionError::new(format!("cannot inspect fork child: {error}")))?;
    collect_differences(&parent_components, &child_components, "/components", &mut observed);
    observed.sort();
    if observed != fork.changed_paths || observed.len() != 1 {
        return Err(ExperimentCompositionError::new(format!(
            "experiment fork must preserve one exact component change; recorded={:?}, observed={observed:?}",
            fork.changed_paths
        )));
    }
    Ok(())
}

fn canonicalize_token_set(values: &mut Vec<String>, label: &str) -> Result<(), ExperimentCompositionError> {
    for value in values.iter() {
        validate_token(value, label)?;
    }
    values.sort();
    for pair in values.windows(2) {
        if pair[0] == pair[1] {
            return Err(ExperimentCompositionError::new(format!(
                "{label} repeats token {:?}",
                pair[0]
            )));
        }
    }
    Ok(())
}

fn canonicalize_relative_path_set(values: &mut Vec<String>, label: &str) -> Result<(), ExperimentCompositionError> {
    if values.is_empty() {
        return Err(ExperimentCompositionError::new(format!("{label} must not be empty")));
    }
    for value in values.iter() {
        let path = Path::new(value);
        if path.is_absolute()
            || !path
                .components()
                .all(|component| matches!(component, Component::Normal(_)))
        {
            return Err(ExperimentCompositionError::new(format!(
                "{label} entry {value:?} must be a safe relative path"
            )));
        }
    }
    values.sort();
    for pair in values.windows(2) {
        if pair[0] == pair[1] {
            return Err(ExperimentCompositionError::new(format!(
                "{label} repeats path {:?}",
                pair[0]
            )));
        }
    }
    Ok(())
}

fn validate_provider_token(value: &str, label: &str) -> Result<(), ExperimentCompositionError> {
    if value.is_empty()
        || !value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
    {
        return Err(ExperimentCompositionError::new(format!(
            "{label} {value:?} must use ASCII letters, digits, dot, underscore, or hyphen"
        )));
    }
    Ok(())
}

fn validate_token(value: &str, label: &str) -> Result<(), ExperimentCompositionError> {
    if value.is_empty()
        || !value
            .bytes()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || matches!(byte, b'.' | b'_' | b'-'))
    {
        return Err(ExperimentCompositionError::new(format!(
            "{label} {value:?} must use lowercase ASCII letters, digits, dot, underscore, or hyphen"
        )));
    }
    Ok(())
}

fn require_schema(observed: &str, expected: &str, noun: &str) -> Result<(), ExperimentCompositionError> {
    if observed != expected {
        return Err(ExperimentCompositionError::new(format!(
            "{noun} declares schema {observed:?}; expected {expected:?}"
        )));
    }
    Ok(())
}

fn normalize_nonempty(value: String, label: &str) -> Result<String, ExperimentCompositionError> {
    let normalized = value.trim().to_string();
    if normalized.is_empty() {
        return Err(ExperimentCompositionError::new(format!("{label} must not be empty")));
    }
    Ok(normalized)
}

fn normalize_optional_nonempty(
    value: Option<String>,
    label: &str,
) -> Result<Option<String>, ExperimentCompositionError> {
    value.map(|value| normalize_nonempty(value, label)).transpose()
}

fn validate_plain_sha256(value: &str, label: &str) -> Result<(), ExperimentCompositionError> {
    validate_prefixed_sha256(value, "", label)
}

fn validate_prefixed_sha256(value: &str, kind: &str, label: &str) -> Result<(), ExperimentCompositionError> {
    let prefix = if kind.is_empty() {
        "sha256:".to_string()
    } else {
        format!("{kind}:sha256:")
    };
    let Some(hex) = value.strip_prefix(&prefix) else {
        return Err(ExperimentCompositionError::new(format!(
            "{label} must start with {prefix:?}"
        )));
    };
    if hex.len() != 64
        || !hex
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return Err(ExperimentCompositionError::new(format!(
            "{label} must end with 64 lowercase hexadecimal digits"
        )));
    }
    Ok(())
}

fn component_reference<T: Serialize>(address_kind: &str, card: &T) -> Result<String, ExperimentCompositionError> {
    prefixed_sha256(address_kind, card)
}

fn prefixed_sha256<T: Serialize>(kind: &str, value: &T) -> Result<String, ExperimentCompositionError> {
    let value = serde_json::to_value(value)
        .map_err(|error| ExperimentCompositionError::new(format!("cannot project canonical {kind} JSON: {error}")))?;
    let bytes = canonical_json_bytes(&value)?;
    Ok(format!("{kind}:sha256:{:x}", Sha256::digest(bytes)))
}

pub(crate) fn canonical_json_bytes(value: &Value) -> Result<Vec<u8>, ExperimentCompositionError> {
    fern_json_contract::canonical_json_bytes(value)
        .map_err(|error| ExperimentCompositionError::new(format!("cannot encode canonical JSON: {error}")))
}

#[cfg(test)]
mod tests {
    use super::*;
    use fern_recipes::{
        IMPLEMENTATION_BUNDLE_DRAFT_SCHEMA_V2, ImplementationBundleDraftV2, ImplementationBundleResolutionV2,
        ImplementationRuntimeV2, PYTHON_CAPABILITY_PROBE_SCHEMA_V2, PythonCapabilityProbeV2, PythonModelLinkProbeV2,
    };
    use serde_json::json;
    use std::path::PathBuf;
    use std::sync::atomic::{AtomicU64, Ordering};

    static SCRATCH_SEQUENCE: AtomicU64 = AtomicU64::new(0);

    struct ScratchDirectory(PathBuf);

    impl ScratchDirectory {
        fn new() -> Self {
            let sequence = SCRATCH_SEQUENCE.fetch_add(1, Ordering::Relaxed);
            let path =
                std::env::temp_dir().join(format!("fern-experiment-composition-{}-{sequence}", std::process::id()));
            fs::create_dir(&path).unwrap();
            for relative in [
                "csharp/Fern.Experiment.Cli/Program.cs",
                "csharp/Fern.Experiment/Catalog/recipe_source_capabilities_v1.json",
                "csharp/Fern.Experiment/RecipeCatalogRecords.cs",
                "csharp/Fern.Experiment/V2HandlerCapabilitiesCli.cs",
            ] {
                let source = path.join(relative);
                fs::create_dir_all(source.parent().unwrap()).unwrap();
                fs::write(source, format!("fixture {relative}\n")).unwrap();
            }
            fs::write(path.join("provider.py"), "# provider fixture\n").unwrap();
            fs::write(path.join("uv.lock"), "fixture lock\n").unwrap();
            Self(path)
        }
    }

    impl Drop for ScratchDirectory {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn no_parameters() -> Value {
        json!({
            "type": "object",
            "properties": {},
            "additionalProperties": false
        })
    }

    fn required_parameter(name: &str, parameter: Value) -> Value {
        json!({
            "type": "object",
            "properties": {(name): parameter},
            "required": [name],
            "additionalProperties": false
        })
    }

    fn capability(
        role: ExperimentComponentRoleV2,
        handler: &str,
        parameter_schema: Value,
        provides: &[&str],
        requires: &[&str],
    ) -> HandlerCapabilityV2 {
        let emits = match handler {
            "fern.householder_spd" => BTreeMap::from([("geometry.family".to_string(), json!("householder_spd"))]),
            "fern.single_gaussian" => BTreeMap::from([
                ("latent.family".to_string(), json!("gaussian")),
                ("predictive_law.family".to_string(), json!("single_gaussian")),
            ]),
            "fern.student_t_radial" => BTreeMap::from([
                ("latent.family".to_string(), json!("student_t_radial")),
                ("predictive_law.family".to_string(), json!("student_t")),
            ]),
            "fern.source_mixture" => BTreeMap::from([
                ("latent.family".to_string(), json!("gaussian_mixture")),
                ("predictive_law.family".to_string(), json!("mixture")),
            ]),
            _ => BTreeMap::new(),
        };
        HandlerCapabilityV2 {
            role,
            handler: handler.to_string(),
            parameter_schema,
            composition_schema: None,
            cross_component_constraints: Vec::new(),
            emits,
            provides: provides.iter().map(|value| (*value).to_string()).collect(),
            requires: requires.iter().map(|value| (*value).to_string()).collect(),
        }
    }

    fn python_handlers() -> Vec<HandlerCapabilityV2> {
        vec![
            capability(
                ExperimentComponentRoleV2::Architecture,
                "fern.householder_spd",
                json!({
                    "type": "object",
                    "properties": {
                        "radial_head": {"type": "boolean"},
                        "scale_outputs": {"type": "integer", "minimum": 1}
                    },
                    "required": ["radial_head", "scale_outputs"],
                    "additionalProperties": false
                }),
                &["fern.hidden.v2", "fern.geometry.v2"],
                &[],
            ),
            capability(
                ExperimentComponentRoleV2::Action,
                "fern.separate_tde",
                required_parameter("action_dim", json!({"type": "integer", "minimum": 1})),
                &["fern.action_context.v2"],
                &["fern.hidden.v2"],
            ),
            capability(
                ExperimentComponentRoleV2::Density,
                "fern.single_gaussian",
                no_parameters(),
                &["forecast.density.v2"],
                &["fern.geometry.v2"],
            ),
            capability(
                ExperimentComponentRoleV2::Density,
                "fern.student_t_radial",
                required_parameter("dof", json!({"type": "number", "exclusiveMinimum": 2.0})),
                &["forecast.density.v2"],
                &["fern.geometry.v2"],
            ),
            capability(
                ExperimentComponentRoleV2::Density,
                "fern.source_mixture",
                required_parameter("components", json!({"type": "integer", "minimum": 2, "maximum": 8})),
                &["forecast.density.v2"],
                &["fern.geometry.v2"],
            ),
        ]
    }

    fn try_implementation_bundle_with_handlers(
        root: &Path,
        handlers: Vec<HandlerCapabilityV2>,
    ) -> Result<ImplementationBundleResolutionV2, ExperimentCompositionError> {
        let source_files = [
            "csharp/Fern.Experiment.Cli/Program.cs",
            "csharp/Fern.Experiment/Catalog/recipe_source_capabilities_v1.json",
            "csharp/Fern.Experiment/RecipeCatalogRecords.cs",
            "csharp/Fern.Experiment/V2HandlerCapabilitiesCli.cs",
            "provider.py",
        ]
        .into_iter()
        .map(str::to_string)
        .collect();
        crate::implementation_bundle::build_implementation_bundle_v2(
            root,
            ImplementationBundleDraftV2 {
                schema: IMPLEMENTATION_BUNDLE_DRAFT_SCHEMA_V2.to_string(),
                runtime: ImplementationRuntimeV2::PythonModule {
                    module: "provider".to_string(),
                },
                protocol_version: 1,
                source_files,
                dependency_lock_files: vec!["uv.lock".to_string()],
                active_feature_flags: Vec::new(),
            },
            |root, module| {
                assert_eq!(module, "provider");
                Ok(crate::implementation_bundle::PythonProbeObservation {
                    provider_file: root.join("provider.py"),
                    capabilities: PythonCapabilityProbeV2 {
                        schema: PYTHON_CAPABILITY_PROBE_SCHEMA_V2.to_string(),
                        generator_source_files: vec!["provider.py".to_string()],
                        model_link_probe: PythonModelLinkProbeV2 {
                            entrypoint: "model_link_probe".to_string(),
                            protocol_version: 1,
                        },
                        handlers: handlers.clone(),
                        stage_pipeline_template: None,
                    },
                    loaded_source_files: vec!["provider.py".to_string()],
                })
            },
        )
    }

    fn implementation_bundle_with_handlers(
        root: &Path,
        handlers: Vec<HandlerCapabilityV2>,
    ) -> ImplementationBundleResolutionV2 {
        try_implementation_bundle_with_handlers(root, handlers).unwrap()
    }

    fn implementation_bundle(root: &Path) -> ImplementationBundleResolutionV2 {
        implementation_bundle_with_handlers(root, python_handlers())
    }

    fn recipe() -> RecipeComponentCardV2 {
        RecipeComponentCardV2 {
            schema: RECIPE_COMPONENT_CARD_SCHEMA_V2.to_string(),
            world: HandlerRequestV2 {
                handler: "lg_ssm".to_string(),
                parameters: json!({
                    "channels": 1,
                    "noise_kind": "gaussian",
                    "noise_ar1_phi": 0.0,
                    "relative_dose": true,
                    "run_whiten": false,
                    "run_rewind": false
                }),
            },
            evaluator: HandlerRequestV2 {
                handler: "resolved_loss".to_string(),
                parameters: json!({
                    "train": [{"name": "huber", "role": "active", "weight": 1.0}],
                    "eval": [{"name": "huber", "role": "active", "weight": 0.5}],
                    "huber_delta": {"train": 1.0, "eval": 1.0},
                    "num_proj_swd": 0,
                    "probability": {
                        "num_train_samples": 1,
                        "num_eval_samples": 1,
                        "quantile_levels": [0.1, 0.5, 0.9]
                    },
                    "smoothing": {
                        "train": {"kind": "none", "window": 1, "alpha": 1.0},
                        "val": {"kind": "none", "window": 1, "alpha": 1.0},
                        "test": {"kind": "none", "window": 1, "alpha": 1.0}
                    }
                }),
            },
            loader: HandlerRequestV2 {
                handler: "explicit_loader".to_string(),
                parameters: json!({
                    "train_start": 0.0,
                    "train_end": 0.61,
                    "val_start": 0.61,
                    "val_end": 0.83,
                    "test_start": 0.83,
                    "test_end": 1.0,
                    "normalization": "none",
                    "batch_size": 7,
                    "num_workers": 0
                }),
            },
            trainer: HandlerRequestV2 {
                handler: "python_training_worker".to_string(),
                parameters: json!({
                    "execution_kind": "train",
                    "checkpoint_source": null,
                    // state_forecast because this fixture trains no action head:
                    // its architecture card is state-only. An action-conditioned
                    // fixture would need state_and_action_forecast, and that is a
                    // scientific choice, not a default.
                    "training_target": {"kind": "state_forecast"},
                    "task_name": "long_term_forecast",
                    "channel_independent": false,
                    "epochs": 50,
                    "patience": 5,
                    "grace_period": 1,
                    "optim": {
                        "type": "adamw",
                        "learning_rate": 0.0003,
                        "weight_decay": 0.0,
                        "max_grad_norm": 0.0,
                        "muon_momentum": 0.95,
                        "muon_beta2": 0.999,
                        "muon_ns_steps": 5
                    },
                    "scheduler": {
                        "type": "none",
                        "plateau_patience": 10,
                        "factor": 0.1,
                        "warmup_epochs": 5,
                        "eta_min": 0.00001
                    },
                    "dtype": "float32",
                    "amp_dtype": "none",
                    "compile": {
                        "enabled": false,
                        "mode": "default",
                        "fullgraph": false,
                        "dynamic": false
                    },
                    "artifact_policy": "slim"
                }),
            },
            context_length: 336,
            grid: std::collections::BTreeMap::from([
                ("bank".to_string(), vec![json!(42)]),
                ("horizon".to_string(), vec![json!(336)]),
                ("noise_dose".to_string(), vec![json!(0.0)]),
            ]),
            factorial: None,
        }
    }

    fn architecture(scale_outputs: u64) -> ArchitectureComponentCardV2 {
        ArchitectureComponentCardV2 {
            schema: ARCHITECTURE_COMPONENT_CARD_SCHEMA_V2.to_string(),
            handler: "fern.householder_spd".to_string(),
            parameters: json!({"radial_head": false, "scale_outputs": scale_outputs}),
        }
    }

    fn density(handler: &str, parameters: Value) -> DensityComponentCardV2 {
        DensityComponentCardV2 {
            schema: DENSITY_COMPONENT_CARD_SCHEMA_V2.to_string(),
            handler: handler.to_string(),
            parameters,
        }
    }

    fn components(scale_outputs: u64, density: DensityComponentCardV2) -> ExperimentComponentsSourceV2 {
        ExperimentComponentsSourceV2 {
            recipe: ComponentSourceV2::Inline(recipe()),
            architecture: ComponentSourceV2::Inline(architecture(scale_outputs)),
            action: ActionSourceV2::StateOnly,
            density: ComponentSourceV2::Inline(density),
        }
    }

    fn experiment(density: DensityComponentCardV2) -> ExperimentDraftV2 {
        ExperimentDraftV2 {
            schema: fern_recipes::EXPERIMENT_DRAFT_SCHEMA_V2.to_string(),
            question: "Does this idea resolve before any GPU work?".to_string(),
            design_ref: None,
            budget: None,
            cost_estimate: None,
            inference: None,
            components: components(8, density),
        }
    }

    fn experiment_with_default_bundle(
        root: &Path,
        density: DensityComponentCardV2,
    ) -> (ExperimentDraftV2, ImplementationBundleResolutionV2) {
        let bundle = implementation_bundle(root);
        let draft = experiment(density);
        (draft, bundle)
    }

    #[test]
    fn current_fern_baseline_resolves_to_one_complete_manifest() {
        let root = ScratchDirectory::new();
        let (draft, bundle) = experiment_with_default_bundle(&root.0, density("fern.single_gaussian", json!({})));
        let implementation = bundle.implementation.clone();
        let result =
            resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle).unwrap();

        assert_eq!(result.resolved.manifest.implementation, implementation);
        assert_eq!(result.resolved.manifest.components.recipe.context_length, 336);
        assert_eq!(
            result.resolved.manifest.components.density.handler,
            "fern.single_gaussian"
        );
        assert_eq!(result.workload.total_cells, 1);
        verify_experiment_resolution_v2(&result).unwrap();
    }

    #[test]
    fn resolve_refuses_a_validation_objective_whose_weights_sum_to_zero() {
        let root = ScratchDirectory::new();
        let (mut draft, bundle) = experiment_with_default_bundle(&root.0, density("fern.single_gaussian", json!({})));
        let ComponentSourceV2::Inline(recipe) = &mut draft.components.recipe else {
            panic!("fixture recipe is inline");
        };
        // Exactly what the shipped templates declared: every evaluation term a
        // monitor at weight zero. The objective is then 0.0 at every epoch and
        // validation-best selection can never move off the first one.
        recipe.evaluator.parameters["eval"] = json!([
            {"name": "huber", "role": "monitor", "weight": 0.0},
            {"name": "mse", "role": "monitor", "weight": 0.0}
        ]);

        let error = resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle)
            .unwrap_err()
            .to_string();
        assert!(error.contains("weights sum to zero"), "{error}");
        assert!(error.contains("huber (monitor, weight 0)"), "{error}");
        assert!(error.contains("mse (monitor, weight 0)"), "{error}");
        assert!(error.contains("first-epoch"), "{error}");
    }

    #[test]
    fn resolve_refuses_a_negative_evaluation_weight() {
        let root = ScratchDirectory::new();
        let (mut draft, bundle) = experiment_with_default_bundle(&root.0, density("fern.single_gaussian", json!({})));
        let ComponentSourceV2::Inline(recipe) = &mut draft.components.recipe else {
            panic!("fixture recipe is inline");
        };
        // weight -1 passed the old `abs()` sum and made selection reward a
        // worse loss. The schema refuses it now; the message must name it.
        recipe.evaluator.parameters["eval"] = json!([
            {"name": "huber", "role": "active", "weight": -1.0},
            {"name": "mse", "role": "monitor", "weight": 0.0}
        ]);
        let error = resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle)
            .unwrap_err()
            .to_string();
        assert!(error.contains("-1") && error.contains("weight"), "{error}");
    }

    #[test]
    fn resolve_refuses_zero_epochs() {
        let root = ScratchDirectory::new();
        let (mut draft, bundle) = experiment_with_default_bundle(&root.0, density("fern.single_gaussian", json!({})));
        let ComponentSourceV2::Inline(recipe) = &mut draft.components.recipe else {
            panic!("fixture recipe is inline");
        };
        // Zero epochs ran no training loop and still saved a model.
        recipe.trainer.parameters["epochs"] = json!(0);
        let error = resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle)
            .unwrap_err()
            .to_string();
        assert!(error.contains("epochs"), "{error}");
        assert!(error.contains("minimum"), "{error}");
    }

    /// The other direction, and the one that was missing when a blanket schema
    /// minimum of 1 was briefly committed on 2026-08-26.
    ///
    /// Zero epochs is how an execution says it does no training, and every
    /// zero-shot model in this campaign -- Chronos, TiRex, Zeus -- is such an
    /// execution. A rule that refuses zero epochs by the NUMBER alone makes
    /// them unauthorable, and the refusal test above passes just as happily
    /// while it does. Only this test notices.
    #[test]
    fn an_evaluation_only_execution_may_declare_zero_epochs() {
        let root = ScratchDirectory::new();
        let (mut draft, bundle) = experiment_with_default_bundle(&root.0, density("fern.single_gaussian", json!({})));
        let ComponentSourceV2::Inline(recipe) = &mut draft.components.recipe else {
            panic!("fixture recipe is inline");
        };
        recipe.trainer.parameters["execution_kind"] = json!("evaluation_only");
        recipe.trainer.parameters["epochs"] = json!(0);
        resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle)
            .expect("an evaluation-only execution declaring no training must resolve");
    }

    #[test]
    fn resolve_accepts_a_validation_objective_carried_by_a_single_weighted_term() {
        let root = ScratchDirectory::new();
        let (mut draft, bundle) = experiment_with_default_bundle(&root.0, density("fern.single_gaussian", json!({})));
        let ComponentSourceV2::Inline(recipe) = &mut draft.components.recipe else {
            panic!("fixture recipe is inline");
        };
        // The monitors may all be zero as long as ONE term carries weight; the
        // `role` string is documentation and the loss deck never reads it.
        recipe.evaluator.parameters["eval"] = json!([
            {"name": "huber", "role": "active", "weight": 0.25},
            {"name": "mse", "role": "monitor", "weight": 0.0},
            {"name": "ept", "role": "monitor", "weight": 0.0}
        ]);

        resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle)
            .expect("one weighted evaluation term is a usable validation objective");
    }

    /// Resolve one draft after `mutate` has edited its inline recipe, and
    /// return the warnings it carries. Every warning test differs only in that
    /// edit, so the fixture and the call belong in one place.
    fn warnings_after(mutate: impl FnOnce(&mut RecipeComponentCardV2)) -> Vec<String> {
        let root = ScratchDirectory::new();
        let (mut draft, bundle) = experiment_with_default_bundle(&root.0, density("fern.single_gaussian", json!({})));
        let ComponentSourceV2::Inline(recipe) = &mut draft.components.recipe else {
            panic!("fixture recipe is inline");
        };
        mutate(recipe);
        resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle)
            .expect("every shape under test is legal and must resolve")
            .warnings
    }

    #[test]
    fn an_ordinary_draft_carries_no_warnings() {
        assert_eq!(warnings_after(|_| {}), Vec::<String>::new());
    }

    #[test]
    fn training_one_objective_and_selecting_on_another_is_warned_with_both_named() {
        let warnings = warnings_after(|recipe| {
            recipe.evaluator.parameters["train"] = json!([{"name": "huber", "role": "active", "weight": 1.0}]);
            recipe.evaluator.parameters["eval"] = json!([{"name": "mse", "role": "active", "weight": 1.0}]);
        });
        assert_eq!(warnings.len(), 1, "{warnings:?}");
        assert!(warnings[0].contains("trains on huber"), "{warnings:?}");
        assert!(warnings[0].contains("selects checkpoints on mse"), "{warnings:?}");
    }

    #[test]
    fn a_lone_weighted_term_beside_monitors_names_the_term_that_selects() {
        let warnings = warnings_after(|recipe| {
            recipe.evaluator.parameters["eval"] = json!([
                {"name": "huber", "role": "active", "weight": 0.5},
                {"name": "mse", "role": "monitor", "weight": 0.0},
                {"name": "ept", "role": "monitor", "weight": 0.0}
            ]);
        });
        assert_eq!(warnings.len(), 1, "{warnings:?}");
        assert!(
            warnings[0].contains("selects checkpoints on huber alone"),
            "{warnings:?}"
        );
        assert!(warnings[0].contains("mse, ept"), "{warnings:?}");
        assert!(warnings[0].contains("weight 0 and only monitored"), "{warnings:?}");
    }

    #[test]
    fn a_training_term_at_weight_zero_is_warned_as_training_nothing() {
        let warnings = warnings_after(|recipe| {
            recipe.evaluator.parameters["train"] = json!([
                {"name": "huber", "role": "active", "weight": 1.0},
                {"name": "swd", "role": "active", "weight": 0.0}
            ]);
        });
        assert_eq!(warnings.len(), 1, "{warnings:?}");
        assert!(warnings[0].contains("training term swd at weight 0"), "{warnings:?}");
        assert!(warnings[0].contains("trains nothing"), "{warnings:?}");
    }

    #[test]
    fn a_run_with_almost_no_optimizer_steps_says_selection_has_nothing_to_choose_from() {
        // No trainer schema admits `steps_per_epoch`, so what a draft can
        // declare today is the epoch count, and selection compares one
        // checkpoint per epoch.
        let warnings = warnings_after(|recipe| {
            recipe.trainer.parameters["epochs"] = json!(3);
        });
        assert_eq!(warnings.len(), 1, "{warnings:?}");
        assert!(warnings[0].contains("declares 3 epochs"), "{warnings:?}");
        assert!(warnings[0].contains("one checkpoint per epoch"), "{warnings:?}");
        assert!(
            warnings[0].contains("selection has almost nothing to choose from"),
            "{warnings:?}"
        );

        // Ten is the threshold, and it is not a warning.
        let enough = warnings_after(|recipe| {
            recipe.trainer.parameters["epochs"] = json!(10);
        });
        assert_eq!(enough, Vec::<String>::new());
    }

    #[test]
    fn a_saved_receipt_cannot_carry_a_warning_the_experiment_does_not_produce() {
        let root = ScratchDirectory::new();
        let (draft, bundle) = experiment_with_default_bundle(&root.0, density("fern.single_gaussian", json!({})));
        let mut resolution =
            resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle).unwrap();
        verify_experiment_resolution_v2(&resolution).unwrap();

        resolution.warnings = vec!["a warning nobody computed".to_string()];
        let refusal = verify_experiment_resolution_v2(&resolution).unwrap_err().to_string();
        assert!(refusal.contains("do not match"), "{refusal}");
    }

    #[test]
    fn grid_preview_refuses_a_twenty_eight_cell_idea_over_budget() {
        let root = ScratchDirectory::new();
        let (mut draft, bundle) = experiment_with_default_bundle(&root.0, density("fern.single_gaussian", json!({})));
        let ComponentSourceV2::Inline(recipe) = &mut draft.components.recipe else {
            panic!("fixture recipe is inline");
        };
        recipe.grid.insert(
            "bank".to_string(),
            [7, 1955, 2023, 4].into_iter().map(|value| json!(value)).collect(),
        );
        recipe
            .grid
            .insert("noise_dose".to_string(), (0..7).map(|value| json!(value)).collect());
        draft.budget = Some(ExperimentBudgetV2 {
            max_cells: 27,
            max_gpu_seconds: None,
        });

        let error = resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle)
            .unwrap_err()
            .to_string();
        assert!(error.contains("28 cells"), "{error}");
        assert!(error.contains("max_cells budget 27"), "{error}");
    }

    #[test]
    fn workload_prints_receipted_gpu_time_and_enforces_its_budget() {
        let root = ScratchDirectory::new();
        let (mut draft, bundle) = experiment_with_default_bundle(&root.0, density("fern.single_gaussian", json!({})));
        let ComponentSourceV2::Inline(recipe) = &mut draft.components.recipe else {
            panic!("fixture recipe is inline");
        };
        recipe.grid.insert(
            "bank".to_string(),
            [7, 1955, 2023, 4].into_iter().map(|value| json!(value)).collect(),
        );
        recipe
            .grid
            .insert("noise_dose".to_string(), (0..7).map(|value| json!(value)).collect());
        draft.cost_estimate = Some(ExperimentCostEstimateV2 {
            gpu_seconds_per_cell: 668,
            basis: "receipt:fern-cell-timing".to_string(),
        });
        draft.budget = Some(ExperimentBudgetV2 {
            max_cells: 28,
            max_gpu_seconds: Some(18_703),
        });

        let error = resolve_experiment_draft_v2(
            &root.0,
            Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME),
            draft.clone(),
            bundle.clone(),
        )
        .unwrap_err()
        .to_string();
        assert!(error.contains("18704 GPU seconds"), "{error}");
        assert!(error.contains("max_gpu_seconds budget 18703"), "{error}");

        draft.budget.as_mut().unwrap().max_gpu_seconds = Some(18_704);
        let resolved =
            resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle).unwrap();
        assert_eq!(resolved.workload.total_cells, 28);
        assert_eq!(resolved.workload.estimated_gpu_seconds, Some(18_704));
        assert_eq!(
            resolved.workload.estimate_basis.as_deref(),
            Some("receipt:fern-cell-timing")
        );
    }

    #[test]
    fn exact_sign_test_must_be_able_to_reach_its_declared_alpha() {
        let root = ScratchDirectory::new();
        let (mut draft, bundle) = experiment_with_default_bundle(&root.0, density("fern.single_gaussian", json!({})));
        draft.inference = Some(InferentialRequirementV2::ExactTwoSidedSign {
            paired_units: 4,
            alpha: 0.05,
        });

        let error = resolve_experiment_draft_v2(
            &root.0,
            Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME),
            draft.clone(),
            bundle.clone(),
        )
        .unwrap_err()
        .to_string();
        assert!(error.contains("4 paired units"), "{error}");
        assert!(error.contains("minimum attainable p is 0.125"), "{error}");

        draft.inference = Some(InferentialRequirementV2::ExactTwoSidedSign {
            paired_units: 6,
            alpha: 0.05,
        });
        let resolved =
            resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle).unwrap();
        assert_eq!(resolved.inference.unwrap().minimum_attainable_p, 0.03125);
    }

    #[test]
    fn inline_card_promotion_to_reference_keeps_the_manifest_identical() {
        let root = ScratchDirectory::new();
        let component_home = root.0.join(DEFAULT_EXPERIMENT_COMPONENT_HOME);
        let density_card = density("fern.single_gaussian", json!({}));
        let reference = component_reference("density", &density_card).unwrap();
        let hex = parse_component_reference(&reference, "density").unwrap();
        let density_home = component_home.join("density");
        fs::create_dir_all(&density_home).unwrap();
        fs::write(
            density_home.join(format!("{hex}.json")),
            serde_json::to_vec_pretty(&density_card).unwrap(),
        )
        .unwrap();

        let bundle = implementation_bundle(&root.0);
        let inline = resolve_experiment_draft_v2(
            &root.0,
            Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME),
            experiment(density_card),
            bundle.clone(),
        )
        .unwrap();
        let mut referenced_draft = experiment(density("fern.single_gaussian", json!({})));
        referenced_draft.components.density = ComponentSourceV2::Ref(reference);
        let referenced = resolve_experiment_draft_v2(
            &root.0,
            Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME),
            referenced_draft,
            bundle,
        )
        .unwrap();

        assert_eq!(inline.resolved, referenced.resolved);
    }

    #[test]
    fn rank_or_component_limit_fails_before_registration() {
        let root = ScratchDirectory::new();
        let (draft, bundle) =
            experiment_with_default_bundle(&root.0, density("fern.source_mixture", json!({"components": 16})));
        let implementation = bundle.implementation.clone();
        let error = resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle)
            .unwrap_err()
            .to_string();

        assert!(error.contains("maximum"), "{error}");
        assert!(error.contains("components"), "{error}");
        assert!(error.contains(implementation.as_str()), "{error}");
    }

    #[test]
    fn density_family_ablation_has_one_implementation_and_only_declared_changes() {
        let root = ScratchDirectory::new();
        let bundle = implementation_bundle(&root.0);
        let implementation = bundle.implementation.clone();
        let draft = AblationDraftV2 {
            schema: fern_recipes::ABLATION_DRAFT_SCHEMA_V2.to_string(),
            question: "Does a non-Gaussian density family help?".to_string(),
            design_ref: None,
            budget: None,
            cost_estimate: None,
            inference: None,
            declared_changes: vec![
                "/components/density/parameters/dof".to_string(),
                "/components/density/handler".to_string(),
            ],
            declared_fact_changes: vec!["latent.family".to_string(), "predictive_law.family".to_string()],
            control: components(8, density("fern.single_gaussian", json!({}))),
            treatment: components(8, density("fern.student_t_radial", json!({"dof": 8.0}))),
        };
        let result =
            resolve_ablation_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle).unwrap();

        assert_eq!(result.control.manifest.implementation, implementation);
        assert_eq!(result.treatment.manifest.implementation, result.implementation);
        assert_ne!(result.control.manifest_sha256, result.treatment.manifest_sha256);
        assert_eq!(result.workload.total_cells, 2);
        assert_eq!(
            result.control.manifest.configuration_facts["predictive_law.family"],
            json!("single_gaussian")
        );
        assert_eq!(
            result.treatment.manifest.configuration_facts["predictive_law.family"],
            json!("student_t")
        );
        verify_ablation_resolution_v2(&result).unwrap();
    }

    #[test]
    fn scalar_radial_head_cannot_claim_a_predictive_law_change() {
        let root = ScratchDirectory::new();
        let bundle = implementation_bundle(&root.0);
        let mut treatment = components(8, density("fern.single_gaussian", json!({})));
        let ComponentSourceV2::Inline(architecture) = &mut treatment.architecture else {
            panic!("fixture architecture is inline");
        };
        architecture.parameters["radial_head"] = json!(true);
        let draft = AblationDraftV2 {
            schema: fern_recipes::ABLATION_DRAFT_SCHEMA_V2.to_string(),
            question: "Does a scalar radial head leave the Gaussian family?".to_string(),
            design_ref: None,
            budget: None,
            cost_estimate: None,
            inference: None,
            declared_changes: vec!["/components/architecture/parameters/radial_head".to_string()],
            declared_fact_changes: vec!["predictive_law.family".to_string()],
            control: components(8, density("fern.single_gaussian", json!({}))),
            treatment,
        };

        let error = resolve_ablation_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle)
            .unwrap_err()
            .to_string();
        assert!(error.contains("declared_but_unchanged"), "{error}");
        assert!(error.contains("predictive_law.family"), "{error}");
    }

    #[test]
    fn fork_changes_one_field_and_inherits_the_exact_implementation() {
        let root = ScratchDirectory::new();
        let (mut draft, bundle) = experiment_with_default_bundle(&root.0, density("fern.single_gaussian", json!({})));
        draft.design_ref = Some("research-design:fork-parent".to_string());
        let implementation = bundle.implementation.clone();
        let parent = resolve_experiment_draft_v2(
            &root.0,
            Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME),
            draft,
            bundle.clone(),
        )
        .unwrap();
        let fork = fork_experiment_resolution_v2(
            &root.0,
            Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME),
            &parent,
            "/components/architecture/parameters/scale_outputs",
            json!(9),
            "Does one extra scale output help?".to_string(),
            None,
            Some(ExperimentBudgetV2 {
                max_cells: 1,
                max_gpu_seconds: None,
            }),
            None,
            bundle,
        )
        .unwrap();

        assert_eq!(
            fork.changed_paths,
            ["/components/architecture/parameters/scale_outputs"]
        );
        assert_eq!(fork.inherited_implementation, implementation);
        assert_eq!(fork.resolved.design_ref.as_deref(), Some("research-design:fork-parent"));
        assert_eq!(
            fork.resolved.resolved.manifest.implementation,
            fork.inherited_implementation
        );
        assert_eq!(fork.workload.total_cells, 1);
    }

    #[test]
    fn implementation_generated_cross_card_constraint_rejects_shape_mismatch() {
        let root = ScratchDirectory::new();
        let mut handlers = python_handlers();
        let density_capability = handlers
            .iter_mut()
            .find(|item| item.handler == "fern.source_mixture")
            .unwrap();
        density_capability.parameter_schema = json!({
            "type": "object",
            "properties": {
                "axes": {"type": "integer", "minimum": 1},
                "components": {"type": "integer", "minimum": 2, "maximum": 8}
            },
            "required": ["axes", "components"],
            "additionalProperties": false
        });
        density_capability
            .cross_component_constraints
            .push(CrossComponentConstraintV2 {
                left_pointer: "/architecture/parameters/scale_outputs".to_string(),
                relation: CrossComponentRelationV2::Equal,
                right_pointer: "/density/parameters/axes".to_string(),
            });
        let bundle = implementation_bundle_with_handlers(&root.0, handlers);
        let draft = experiment(density("fern.source_mixture", json!({"axes": 7, "components": 4})));

        let error = resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle)
            .unwrap_err()
            .to_string();
        assert!(error.contains("cross-card constraint failed"), "{error}");
        assert!(error.contains("scale_outputs"), "{error}");
        assert!(error.contains("/density/parameters/axes"), "{error}");
    }

    /// Every "limit" a handler could want to declare about values owned by
    /// ANOTHER card is already expressible, and already enforced, by the
    /// composition schema the provider emits beside that handler.
    ///
    /// Written on 2026-08-21 before adding a typed `limits` block to the
    /// capability record, to find out whether one was needed. It is not: this
    /// is the owner's own measured case — the scale head needs an even context
    /// length greater than one, and context_length lives on the recipe card
    /// while the constraint belongs to the density handler — and the existing
    /// mechanism refuses it, naming the role, the handler, the failing value
    /// and its exact location.
    #[test]
    fn a_handler_limit_on_another_cards_value_is_already_expressible_and_enforced() {
        let root = ScratchDirectory::new();
        let mut handlers = python_handlers();
        let density_capability = handlers
            .iter_mut()
            .find(|item| item.handler == "fern.single_gaussian")
            .unwrap();
        // min_context, context_parity, max_channels and max_horizon, all in
        // one generated schema over the resolved components object.
        density_capability.composition_schema = Some(json!({
            "type": "object",
            "properties": {
                "recipe": {
                    "type": "object",
                    "properties": {
                        "context_length": {"type": "integer", "minimum": 2, "multipleOf": 2},
                        "world": {
                            "type": "object",
                            "properties": {
                                "parameters": {
                                    "type": "object",
                                    "properties": {"channels": {"type": "integer", "maximum": 8}}
                                }
                            }
                        },
                        "grid": {
                            "type": "object",
                            "properties": {
                                "horizon": {"type": "array", "items": {"type": "integer", "maximum": 64}}
                            }
                        }
                    }
                }
            }
        }));
        let bundle = implementation_bundle_with_handlers(&root.0, handlers);

        let mut draft = experiment(density("fern.single_gaussian", json!({})));
        let ComponentSourceV2::Inline(recipe) = &mut draft.components.recipe else {
            panic!("fixture recipe is inline");
        };
        recipe.context_length = 1;

        let error = resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle)
            .unwrap_err()
            .to_string();
        // "a factorial cross-card composition" is the wording the refusal has
        // carried since before this test was written (line 1031). The test asked
        // for "this", so it could never have passed; it was masked by an earlier
        // failure until 2026-08-26. Asserting the stable half keeps every
        // behavioural claim -- the refusal happens, and names the handler, the
        // pointer and the rule -- without pinning an adjective.
        assert!(error.contains("cross-card composition"), "{error}");
        assert!(error.contains("/recipe/context_length"), "{error}");
        assert!(error.contains("fern.single_gaussian"), "{error}");
        // The value and the rule it broke are both in the message, which is
        // what a bespoke `limits` refusal would have had to produce by hand.
        assert!(error.contains('1'), "{error}");
        // Verbatim, 2026-08-21: `density handler "fern.single_gaussian"
        // rejects this cross-card composition: 1 is less than the minimum of 2
        // at /recipe/context_length; 1 is not a multiple of 2 at
        // /recipe/context_length; 336 is greater than the maximum of 64 at
        // /recipe/grid/horizon/0`. Every violated limit at once, each with its
        // value and its pointer, and generated rather than hand-written.
        assert!(error.contains("is not a multiple of 2"), "{error}");
    }

    /// A capability record with no composition schema constrains nothing, so
    /// every record written before limits existed still resolves.
    #[test]
    fn a_handler_without_a_composition_schema_constrains_no_other_card() {
        let root = ScratchDirectory::new();
        let bundle = implementation_bundle(&root.0);
        let mut draft = experiment(density("fern.single_gaussian", json!({})));
        let ComponentSourceV2::Inline(recipe) = &mut draft.components.recipe else {
            panic!("fixture recipe is inline");
        };
        recipe.context_length = 1;
        resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle)
            .expect("an unconstrained handler accepts a context length it never declared a limit on");
    }

    #[test]
    fn undeclared_treatment_change_is_named_exactly() {
        let root = ScratchDirectory::new();
        let bundle = implementation_bundle(&root.0);
        let draft = AblationDraftV2 {
            schema: fern_recipes::ABLATION_DRAFT_SCHEMA_V2.to_string(),
            question: "Does the n+1 radial law help?".to_string(),
            design_ref: None,
            budget: None,
            cost_estimate: None,
            inference: None,
            declared_changes: vec!["/components/density/handler".to_string()],
            declared_fact_changes: vec!["latent.family".to_string(), "predictive_law.family".to_string()],
            control: components(8, density("fern.single_gaussian", json!({}))),
            treatment: components(9, density("fern.student_t_radial", json!({"dof": 8.0}))),
        };
        let error = resolve_ablation_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle)
            .unwrap_err()
            .to_string();

        assert!(
            error.contains("/components/architecture/parameters/scale_outputs"),
            "{error}"
        );
        assert!(error.contains("/components/density/parameters/dof"), "{error}");
    }

    #[test]
    fn ablation_arms_cannot_carry_separate_implementations() {
        let root = ScratchDirectory::new();
        let implementation = implementation_bundle(&root.0).implementation;
        let mut value = serde_json::to_value(AblationDraftV2 {
            schema: fern_recipes::ABLATION_DRAFT_SCHEMA_V2.to_string(),
            question: "one implementation".to_string(),
            design_ref: None,
            budget: None,
            cost_estimate: None,
            inference: None,
            declared_changes: vec!["/components/density/handler".to_string()],
            declared_fact_changes: vec!["latent.family".to_string(), "predictive_law.family".to_string()],
            control: components(8, density("fern.single_gaussian", json!({}))),
            treatment: components(8, density("fern.student_t_radial", json!({"dof": 8.0}))),
        })
        .unwrap();
        value["control"]["implementation"] = json!({"ref": implementation.as_str()});

        let error = serde_json::from_value::<AblationDraftV2>(value)
            .unwrap_err()
            .to_string();
        assert!(error.contains("unknown field"), "{error}");
        assert!(error.contains("implementation"), "{error}");
    }

    #[test]
    fn capability_schema_cannot_hide_a_runtime_default() {
        let root = ScratchDirectory::new();
        let mut handlers = python_handlers();
        let density_capability = handlers
            .iter_mut()
            .find(|item| item.handler == "fern.single_gaussian")
            .unwrap();
        density_capability.parameter_schema = json!({
            "type": "object",
            "properties": {"temperature": {"type": "number", "default": 1.0}},
            "required": ["temperature"],
            "additionalProperties": false
        });

        let error = try_implementation_bundle_with_handlers(&root.0, handlers)
            .unwrap_err()
            .to_string();
        assert!(error.contains("default"), "{error}");
    }

    #[test]
    fn capability_schema_refuses_an_untagged_nested_union() {
        let root = ScratchDirectory::new();
        let mut handlers = python_handlers();
        let density_capability = handlers
            .iter_mut()
            .find(|item| item.handler == "fern.single_gaussian")
            .unwrap();
        density_capability.parameter_schema = json!({
            "type": "object",
            "properties": {
                "source": {
                    "oneOf": [
                        {
                            "type": "object",
                            "properties": {"path": {"type": "string"}},
                            "required": ["path"],
                            "additionalProperties": false
                        },
                        {
                            "type": "object",
                            "properties": {"digest": {"type": "string"}},
                            "required": ["digest"],
                            "additionalProperties": false
                        }
                    ]
                }
            },
            "required": ["source"],
            "additionalProperties": false
        });

        let error = try_implementation_bundle_with_handlers(&root.0, handlers)
            .unwrap_err()
            .to_string();
        assert!(error.contains("required string kind const"), "{error}");
    }

    #[test]
    fn capability_schema_accepts_one_nullable_closed_record() {
        let root = ScratchDirectory::new();
        let mut handlers = python_handlers();
        let density_capability = handlers
            .iter_mut()
            .find(|item| item.handler == "fern.single_gaussian")
            .unwrap();
        density_capability.parameter_schema = json!({
            "type": "object",
            "properties": {
                "optional_settings": {
                    "oneOf": [
                        {"type": "null"},
                        {
                            "type": "object",
                            "properties": {"weight": {"type": "number"}},
                            "required": ["weight"],
                            "additionalProperties": false
                        }
                    ]
                }
            },
            "required": ["optional_settings"],
            "additionalProperties": false
        });

        try_implementation_bundle_with_handlers(&root.0, handlers).unwrap();
    }

    #[test]
    fn selected_handlers_must_close_their_interface_requirements() {
        let root = ScratchDirectory::new();
        let mut handlers = python_handlers();
        let architecture = handlers
            .iter_mut()
            .find(|item| item.role == ExperimentComponentRoleV2::Architecture)
            .unwrap();
        architecture.provides.clear();
        let bundle = implementation_bundle_with_handlers(&root.0, handlers);
        let draft = experiment(density("fern.single_gaussian", json!({})));

        let error = resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle)
            .unwrap_err()
            .to_string();
        assert!(error.contains("fern.geometry.v2"), "{error}");
    }

    #[test]
    fn saved_resolution_rejects_tampered_component_bytes() {
        let root = ScratchDirectory::new();
        let (draft, bundle) = experiment_with_default_bundle(&root.0, density("fern.single_gaussian", json!({})));
        let mut result =
            resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle).unwrap();
        result.resolved.manifest.components.architecture.parameters["scale_outputs"] = json!(99);

        let error = verify_experiment_resolution_v2(&result).unwrap_err().to_string();
        assert!(
            error.contains("component_refs") || error.contains("identity"),
            "{error}"
        );
    }

    #[test]
    fn saved_resolution_rederives_capability_identity_from_implementation() {
        let root = ScratchDirectory::new();
        let (draft, bundle) = experiment_with_default_bundle(&root.0, density("fern.single_gaussian", json!({})));
        let mut result =
            resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle).unwrap();
        result.resolved.manifest.capability_manifest_sha256 = format!("capabilities:sha256:{}", "f".repeat(64));
        result.resolved.manifest_sha256 = prefixed_sha256("experiment", &result.resolved.manifest).unwrap();

        let error = verify_experiment_resolution_v2(&result).unwrap_err().to_string();
        assert!(error.contains("does not match its pinned implementation"), "{error}");
    }

    #[test]
    fn saved_resolution_rederives_configuration_facts_from_implementation() {
        let root = ScratchDirectory::new();
        let (draft, bundle) = experiment_with_default_bundle(&root.0, density("fern.single_gaussian", json!({})));
        let mut result =
            resolve_experiment_draft_v2(&root.0, Path::new(DEFAULT_EXPERIMENT_COMPONENT_HOME), draft, bundle).unwrap();
        result
            .resolved
            .manifest
            .configuration_facts
            .insert("predictive_law.family".to_string(), json!("forged_law"));
        result.resolved.manifest_sha256 = prefixed_sha256("experiment", &result.resolved.manifest).unwrap();

        let error = verify_experiment_resolution_v2(&result).unwrap_err().to_string();
        assert!(error.contains("rederived from its pinned implementation"), "{error}");
    }
}
