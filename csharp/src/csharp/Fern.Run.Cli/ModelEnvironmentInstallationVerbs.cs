using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fern.Registry;
using Fern.Scheduler;
using Fern.Site;

namespace Fern.Run.Cli;

/// <summary>The public Site preparation command for pinned model environments.</summary>
public static class ModelEnvironmentInstallationVerbs
{
    const int OutputLimitCharacters = 4 * 1024 * 1024;
    static readonly TimeSpan InstallDeadline = TimeSpan.FromMinutes(30);
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static void Add(Command site)
    {
        site.Subcommands.Add(InstallModelEnvironment());
        site.Subcommands.Add(ReconcileEnvironmentInstallLease());
    }

    static Command InstallModelEnvironment()
    {
        var command = new Command(
            "install-model-environment",
            "Install and verify one closed model set in the exact interpreter named by an execution profile; uses uv pip, never uv sync.");
        var on = new Option<string>("--on")
        {
            Description = "Execution profile whose Site and interpreter are prepared.",
            Required = true,
        };
        var model = new Option<string[]>("--model")
        {
            Description = "Installable model; repeat for chronos_bolt_small, chronos_bolt_base, chronos2, or zeus.",
            Required = true,
            AllowMultipleArgumentsPerToken = false,
        };
        var output = new Option<string?>("--out")
        {
            Description = "Also write a copy of the typed installation receipt here. The Registry Home is the admission authority.",
        };
        var registry = new Option<string>("--registry")
        {
            Description = "Registry Home that stores the environment admission compile will consume.",
            DefaultValueFactory = _ => ExperimentLaunchVerbsDefaultRegistry(),
        };
        var actor = new Option<string>("--actor")
        {
            Description = "Stable actor identity recorded on the admission receipt.",
            Required = true,
        };
        command.Options.Add(on);
        command.Options.Add(model);
        command.Options.Add(output);
        command.Options.Add(registry);
        var repairAssets = new Option<bool>("--repair-assets")
        {
            Description = "Authorize deleting and re-materializing pinned model snapshot files that drifted "
                + "from the admitted manifest. A corrupted-in-place blob passes the presence check and is "
                + "invisible to reinstall, so this explicit flag is the only route that repairs it; the "
                + "mutation runs under a lease scoped to the shared model cache.",
        };
        command.Options.Add(actor);
        command.Options.Add(repairAssets);
        command.SetAction(parsed => Execute(
            parsed.GetValue(on)!,
            parsed.GetValue(model) ?? [],
            parsed.GetValue(output),
            parsed.GetValue(registry)!,
            parsed.GetValue(actor)!,
            parsed.GetValue(repairAssets)));
        return command;
    }

    static Command ReconcileEnvironmentInstallLease()
    {
        var command = new Command(
            "reconcile-environment-install-lease",
            "Release an expired model-environment install lease only after observing that its marked owner process is absent.");
        var on = new Option<string>("--on")
        {
            Description = "Execution profile whose Site owned the install lease.",
            Required = true,
        };
        var lease = new Option<string>("--lease")
        {
            Description = "Expired environment install lease id to reconcile.",
            Required = true,
        };
        var registry = new Option<string>("--registry")
        {
            Description = "Registry Home holding the install lease.",
            DefaultValueFactory = _ => ExperimentLaunchVerbsDefaultRegistry(),
        };
        var actor = new Option<string>("--actor")
        {
            Description = "Stable observer identity recorded on the reconcile release receipt.",
            Required = true,
        };
        foreach (var option in new Option[] { on, lease, registry, actor })
        {
            command.Options.Add(option);
        }
        command.SetAction(parsed => ReconcileEnvironmentInstallLease(
            parsed.GetValue(on)!,
            parsed.GetValue(lease)!,
            parsed.GetValue(registry)!,
            parsed.GetValue(actor)!));
        return command;
    }

    static string ExperimentLaunchVerbsDefaultRegistry() => Fern.Registry.RegistryHome.DefaultRegistryPath;

    internal static int ReconcileEnvironmentInstallLease(
        string profileId,
        string leaseId,
        string registryPath,
        string actorId)
    {
        var workspaceRoot = WorktreeControlPaths.RepoRoot();
        var profile = ExecutionProfileV5.Read(WorktreeControlPaths.Profiles(workspaceRoot), profileId, null);
        if (!profile.IsOk) return Refuse(profile.Failure.Detail);
        if (!File.Exists(registryPath))
        {
            return Refuse($"Registry Home {registryPath} does not exist");
        }

        using var writer = RegistryHome.OpenWriter(registryPath);
        var lease = EnvironmentInstallLeaseAuthority.UnreleasedById(writer.Connection, leaseId);
        if (lease is null)
        {
            return Refuse($"environment install lease {leaseId} is not an unreleased lease in {registryPath}");
        }
        if (lease.SiteId != profile.Value.ProfileId)
        {
            return Refuse(
                $"environment install lease {lease.LeaseId} belongs to Site {lease.SiteId}, not profile {profile.Value.ProfileId}");
        }

        var observation = ObserveInstallAttemptProcesses(profile.Value, workspaceRoot, lease.OwnerAttemptId);
        if (!observation.IsOk)
        {
            return Refuse("environment install lease process observation refused: " + observation.Failure.Detail);
        }
        var observationReceipt = ModelEnvironmentInstallation.BuildInstallLeaseProcessAbsenceReceipt(
            lease,
            profile.Value,
            actorId,
            observation.Value);
        if (!observationReceipt.IsOk)
        {
            return Refuse("environment install lease reconciliation refused: " + observationReceipt.Failure.Detail);
        }

        var released = EnvironmentInstallLeaseAuthority.ReconcileRelease(
            writer,
            new EnvironmentInstallLeaseReconcileRelease(
                lease.LeaseId,
                lease.OwnerAttemptId,
                actorId,
                "owner_process_absent",
                observationReceipt.Value));
        if (!released.TryGetValue(out var receipt))
        {
            return Refuse(
                released.TryGetRefusal(out var refusal)
                    ? refusal.Detail
                    : "environment install lease reconciliation returned neither receipt nor refusal");
        }

        Console.Out.WriteLine(ReportJson.SortedPretty(new
        {
            schema = "fern.environment_install_lease_reconcile_release.v1",
            outcome = receipt.Outcome,
            lease_id = receipt.LeaseId,
            owner_attempt_id = receipt.OwnerAttemptId,
            fencing_token = receipt.FencingToken,
            release_reason = receipt.ReleaseReason,
            transition_receipt_id = receipt.TransitionReceiptId,
            process_observation = JsonSerializer.Deserialize<JsonElement>(observationReceipt.Value),
        }, Wire));
        return 0;
    }

    static int Execute(
        string profileId,
        IReadOnlyList<string> models,
        string? outputPath,
        string registryPath,
        string actorId,
        bool repairAssets)
    {
        var elapsed = Stopwatch.StartNew();
        var workspaceRoot = WorktreeControlPaths.RepoRoot();
        var profile = ExecutionProfileV5.Read(WorktreeControlPaths.Profiles(workspaceRoot), profileId, null);
        if (!profile.IsOk) return Refuse(profile.Failure.Detail);

        var plan = ModelEnvironmentInstallation.Resolve(models, profile.Value.Python);
        if (!plan.IsOk) return Refuse(plan.Failure.Detail);
        if (!File.Exists(registryPath))
        {
            return Refuse($"Registry Home {registryPath} does not exist");
        }

        using var writer = RegistryHome.OpenWriter(registryPath);
        var attemptId = "environment-install:" + Guid.NewGuid().ToString("N");
        EnvironmentInstallLease? lease = null;
        DeviceGrant? cudaGrant = null;
        // The lease end reason is the durable answer to "could this install
        // have changed the environment roots". It starts as no change, moves
        // to a possible-change fact when the first mutating command starts,
        // and becomes an admitted-environment fact only after the Registry
        // accepts the environment admission.
        var leaseEndReason = EnvironmentInstallLeaseAuthority.NoEnvironmentChangeStartedReleaseReason;
        int ReturnInstallRefusal(string detail)
        {
            if (lease is not null
                && leaseEndReason == EnvironmentInstallLeaseAuthority.EnvironmentChangeMayHaveHappenedReleaseReason)
            {
                Console.Error.WriteLine(
                    "site install-model-environment outcome="
                    + EnvironmentInstallLeaseAuthority.EnvironmentChangeMayHaveHappenedReleaseReason
                    + ": environment roots "
                    + string.Join(", ", lease.MutationRoots)
                    + " may have changed; retry must run this command's precheck before mutating again");
            }
            return Refuse(detail);
        }

        var consumedMachine = EnvironmentInstallationAdmission.ConsumeCurrentMachineAdmission(writer, profile.Value);
        if (!consumedMachine.IsOk)
        {
            return ReturnInstallRefusal(consumedMachine.Failure.Detail);
        }

        var checkedEnvironment = SiteResult<SiteCommandRun>.Permanent("model-environment precheck has not run");
        var machine = consumedMachine.Value;
        var outcome = "already_ready";
        var beforeEffectJson = """{"ready":false,"detail":"model-environment precheck has not run"}""";

        try
        {
            var heldForPrecheck = HoldCudaValidationGrant(
                writer, profile.Value, attemptId, ref cudaGrant);
            if (!heldForPrecheck.IsOk) return ReturnInstallRefusal(heldForPrecheck.Failure.Detail);

            Phase("precheck", elapsed);
            checkedEnvironment = Run(
                profile.Value,
                workspaceRoot,
                [profile.Value.Python, "-c", ProbePython, ProbeRequest(plan.Value, localOnly: true, device: profile.Value.Device)]);
            DropCudaValidationGrant(writer, attemptId, ref cudaGrant, "precheck cuda validation finished");
            // Before-effect evidence (R16): what the precheck observed before any
            // mutation. A zero exit code from uv is never the compliance fact;
            // the receipt proves the state on both sides of the effect.
            beforeEffectJson = JsonSerializer.Serialize(new
            {
                ready = checkedEnvironment.IsOk,
                detail = checkedEnvironment.IsOk
                    ? null
                    : Truncate(checkedEnvironment.Failure.Detail, 600),
                last_phase = checkedEnvironment.IsOk
                    ? null
                    : ModelEnvironmentInstallation.LastReportedPhase(checkedEnvironment.Failure.Detail),
            });

            if (repairAssets && checkedEnvironment.IsOk)
            {
                // Deliberate snapshot repair: a corrupted-in-place blob passes
                // the local presence check, uv reinstall no-ops, and
                // snapshot_download keeps any file that exists -- so delete
                // plus re-materialize is the only repairing route, and it runs
                // only under this explicit flag with an asset-scoped lease.
                // Packages are compared LIVE from the precheck probe, so real
                // package drift blocks the repair here. The Python runtime
                // comes from the consumed MachineAdmission parent; this repair
                // path must not run a second machine/runtime probe just to
                // regain facts M0 already proved.
                var precheckJson = ModelEnvironmentInstallation.LastJsonObjectLine(checkedEnvironment.Value.StandardOutput)
                    ?? ModelEnvironmentInstallation.LastJsonObjectLine(checkedEnvironment.Value.StandardError);
                var admittedNow = SiteEnvironmentAdmissionAuthority.Current(writer.Connection, profile.Value.ProfileId);
                if (precheckJson is not null && admittedNow is not null)
                {
                    using var precheckProbe = JsonDocument.Parse(precheckJson);
                    JsonElement? observedManifest =
                        precheckProbe.RootElement.TryGetProperty("asset_manifest", out var preManifest)
                            ? preManifest
                            : null;
                    using var admittedReceipt = JsonDocument.Parse(admittedNow.ReceiptJson);
                    var observedPackages = precheckProbe.RootElement.TryGetProperty("packages", out var livePackages)
                        && livePackages.ValueKind == JsonValueKind.Object
                        ? livePackages.EnumerateObject().ToDictionary(
                            item => item.Name, item => item.Value.GetString() ?? "", StringComparer.Ordinal)
                        : new Dictionary<string, string>(StringComparer.Ordinal);
                    var echoPython = admittedReceipt.RootElement.TryGetProperty("python_version", out var recordedPython)
                        ? recordedPython.GetString() ?? ""
                        : "";
                    var assetDrift = ModelEnvironmentInstallation.DriftAgainstAdmission(
                        admittedNow.ReceiptJson, admittedNow.EnvironmentIdentity, observedPackages, echoPython, observedManifest);
                    var repairTargets = plan.Value.Assets
                        .Where(asset => assetDrift.DriftedAssetKeys.Contains(asset.Repository + "@" + asset.Revision))
                        .ToArray();
                    if (!assetDrift.HasNonAssetDrift && repairTargets.Length > 0)
                    {
                        var acquiredRepair = AcquireInstallLease(
                            writer, profile.Value, machine, attemptId, actorId,
                            ModelEnvironmentInstallation.AssetRepairMutationRoots());
                        if (!acquiredRepair.IsOk) return ReturnInstallRefusal(acquiredRepair.Failure.Detail);
                        lease = acquiredRepair.Value;
                        outcome = "repaired_environment";
                        leaseEndReason = EnvironmentInstallLeaseAuthority.EnvironmentChangeMayHaveHappenedReleaseReason;
                        Phase("repair_assets", elapsed);
                        var repaired = Run(profile.Value, workspaceRoot,
                            [
                                profile.Value.Python, "-c", RepairPython,
                                JsonSerializer.Serialize(new
                                {
                                    assets = repairTargets.Select(asset => new
                                    {
                                        repository = asset.Repository,
                                        revision = asset.Revision,
                                    }),
                                }),
                            ],
                            installAttemptId: attemptId);
                        if (!repaired.IsOk) return ReturnInstallRefusal(repaired.Failure.Detail);
                        var heldForRepair = HoldCudaValidationGrant(
                            writer, profile.Value, attemptId, ref cudaGrant);
                        if (!heldForRepair.IsOk) return ReturnInstallRefusal(heldForRepair.Failure.Detail);
                        Phase("verify_assets", elapsed);
                        checkedEnvironment = Run(
                            profile.Value,
                            workspaceRoot,
                            [profile.Value.Python, "-c", ProbePython, ProbeRequest(plan.Value, localOnly: false, device: profile.Value.Device)],
                            installAttemptId: attemptId);
                        DropCudaValidationGrant(writer, attemptId, ref cudaGrant, "repair cuda validation finished");
                        if (!checkedEnvironment.IsOk) return ReturnInstallRefusal(checkedEnvironment.Failure.Detail);
                    }
                }
            }

            if (!checkedEnvironment.IsOk)
            {
                // A failure whose LAST reported phase is the checkpoint check
                // means imports, CUDA, and packages all passed and only the
                // model snapshot is missing or incomplete. That is repaired by
                // re-materializing the snapshot alone (R15): a narrower
                // mutation than an install, with the lease scoped to exactly
                // the shared model cache.
                var assetRepairOnly = ModelEnvironmentInstallation.LastReportedPhase(
                    checkedEnvironment.Failure.Detail) == "checkpoint_local_check";
                var acquired = AcquireInstallLease(
                    writer, profile.Value, machine, attemptId, actorId,
                    assetRepairOnly
                        ? ModelEnvironmentInstallation.AssetRepairMutationRoots()
                        : ModelEnvironmentInstallation.MutationRoots(profile.Value.Python));
                if (!acquired.IsOk) return ReturnInstallRefusal(acquired.Failure.Detail);
                lease = acquired.Value;
                outcome = assetRepairOnly ? "repaired_environment" : "installed";
                leaseEndReason = EnvironmentInstallLeaseAuthority.EnvironmentChangeMayHaveHappenedReleaseReason;
                if (!assetRepairOnly)
                {
                    if (plan.Value.InterpreterBootstrapArgv.Count != 0)
                    {
                        Phase("create_interpreter", elapsed);
                        var bootstrapped = Run(
                            profile.Value,
                            workspaceRoot,
                            plan.Value.InterpreterBootstrapArgv,
                            installAttemptId: attemptId);
                        if (!bootstrapped.IsOk) return ReturnInstallRefusal(bootstrapped.Failure.Detail);
                    }

                    Phase("install_packages", elapsed);
                    var installed = Run(
                        profile.Value,
                        workspaceRoot,
                        plan.Value.UvArgv,
                        installAttemptId: attemptId);
                    if (!installed.IsOk) return ReturnInstallRefusal(installed.Failure.Detail);
                }

                var heldForInstalled = HoldCudaValidationGrant(
                    writer, profile.Value, attemptId, ref cudaGrant);
                if (!heldForInstalled.IsOk) return ReturnInstallRefusal(heldForInstalled.Failure.Detail);
                Phase("verify_assets", elapsed);
                checkedEnvironment = Run(
                    profile.Value,
                    workspaceRoot,
                    [profile.Value.Python, "-c", ProbePython, ProbeRequest(plan.Value, localOnly: false, device: profile.Value.Device)],
                    installAttemptId: attemptId);
                DropCudaValidationGrant(writer, attemptId, ref cudaGrant, "post-install cuda validation finished");
                if (!checkedEnvironment.IsOk) return ReturnInstallRefusal(checkedEnvironment.Failure.Detail);
            }

            JsonDocument probe;
            try
            {
                var json = ModelEnvironmentInstallation.LastJsonObjectLine(checkedEnvironment.Value.StandardOutput)
                    ?? ModelEnvironmentInstallation.LastJsonObjectLine(checkedEnvironment.Value.StandardError);
                if (json is null)
                {
                    return ReturnInstallRefusal(
                        "model-environment postcondition probe emitted no JSON object "
                        + "(SSH merges remote phase lines into stdout; expected a final {packages...} line)");
                }

                probe = JsonDocument.Parse(json);
            }
            catch (JsonException error)
            {
                return ReturnInstallRefusal("model-environment postcondition probe emitted invalid JSON: " + error.Message);
            }

            using (probe)
            {
                if (probe.RootElement.TryGetProperty("valid", out _)
                    || probe.RootElement.TryGetProperty("admitted", out _))
                {
                    return ReturnInstallRefusal(
                        "model-environment probe reported an admission decision; the probe observes packages "
                        + "and Python, and Fern admits against policy");
                }

                var packages = probe.RootElement.GetProperty("packages")
                    .EnumerateObject()
                    .ToDictionary(item => item.Name, item => item.Value.GetString() ?? "", StringComparer.Ordinal);
                JsonElement? assetManifest = probe.RootElement.TryGetProperty("asset_manifest", out var manifestElement)
                    ? manifestElement
                    : null;
                var pythonAbi = machine.PythonAbi;

                if (pythonAbi != ModelEnvironmentInstallation.RequiredPythonAbi)
                {
                    return ReturnInstallRefusal(
                        $"MachineAdmission Python ABI {pythonAbi} is not {ModelEnvironmentInstallation.RequiredPythonAbi}");
                }

                var deviceId = machine.DeviceId;
                var machineGeneration = machine.MachineGeneration;
                var endpointSession = machine.EndpointSessionId;
                var identity = ModelEnvironmentInstallation.ReceiptIdentity(
                    machine.MachineAdmissionIdentity,
                    machineGeneration,
                    pythonAbi,
                    plan.Value,
                    packages);
                if (outcome != "installed")
                {
                    // The environment imports, but "imports" is not "is the
                    // admitted one". Compare the live observation against the
                    // Registry admission and refuse drift by name instead of
                    // silently re-admitting whatever is installed now.
                    Phase("drift_check", elapsed);
                    var admittedNow = SiteEnvironmentAdmissionAuthority.Current(writer.Connection, profile.Value.ProfileId);
                    if (admittedNow is not null)
                    {
                        var drift = ModelEnvironmentInstallation.DriftAgainstAdmission(
                            admittedNow.ReceiptJson,
                            identity,
                            packages,
                            machine.PythonVersion,
                            assetManifest);
                        if (drift.Sentences.Count != 0)
                        {
                            return ReturnInstallRefusal(
                                "refused_environment_drift: the environment imports but is not the admitted one -- "
                                + string.Join("; ", drift.Sentences)
                                + (drift.HasNonAssetDrift
                                    ? " -- reinstall through this command to replace the admission deliberately"
                                    : " -- drifted snapshot files can be re-materialized with --repair-assets, "
                                      + "or reinstall to replace the admission deliberately"));
                        }
                    }
                }

                var afterEffectJson = JsonSerializer.Serialize(new
                {
                    ready = true,
                    package_count = packages.Count,
                    asset_snapshots = assetManifest is { ValueKind: JsonValueKind.Object } observedManifest
                        ? observedManifest.EnumerateObject().Count()
                        : 0,
                });
                var mutationRoots = ModelEnvironmentInstallation.MutationRoots(profile.Value.Python);
                var receipt = JsonSerializer.Serialize(new
                {
                    schema = "fern.model_environment_installation.v1",
                    authority_kind = "environment_install_attempt",
                    authority_scope = lease?.MutationRoots ?? mutationRoots,
                    mutation_mode = "in_place_shared_interpreter",
                    install_lease_id = lease?.LeaseId,
                    fencing_token = lease?.FencingToken,
                    owner_attempt_id = lease is null ? null : attemptId,
                    outcome,
                    mutation = outcome switch
                    {
                        "already_ready" => "none",
                        "repaired_environment" => "asset_snapshot_rematerialized",
                        _ => "packages_installed",
                    },
                    before_effect = JsonSerializer.Deserialize<JsonElement>(beforeEffectJson),
                    after_effect = JsonSerializer.Deserialize<JsonElement>(afterEffectJson),
                    environment_identity = identity,
                    site_id = profile.Value.ProfileId,
                    site_name = profile.Value.ProfileId,
                    machine_admission_identity = machine.MachineAdmissionIdentity,
                    machine_admission_recorded_at_ms = machine.RecordedAtMs,
                    machine_admission_receipt_schema = MachineAdmissionAuthority.ReceiptSchema,
                    machine_generation = machineGeneration,
                    endpoint_session_id = endpointSession,
                    execution_profile_revision = profile.Value.Revision,
                    executor = profile.Value.Executor,
                    interpreter = profile.Value.Python,
                    python_abi = pythonAbi,
                    python_version = machine.PythonVersion,
                    environment_kind = plan.Value.Environment.ToString(),
                    models = plan.Value.Models,
                    packages,
                    assets = plan.Value.Assets.Select(asset => new
                    {
                        model = asset.Model,
                        repository = asset.Repository,
                        revision = asset.Revision,
                    }),
                    asset_manifest = assetManifest,
                    observed_environment_digest = machine.ObservedEnvironmentDigest,
                    device_id = deviceId,
                    uv_operation = "pip_install",
                    uv_sync = false,
                }, new JsonSerializerOptions { WriteIndented = true });

                var recorded = SiteEnvironmentAdmissionAuthority.Record(
                    writer,
                    new SiteEnvironmentAdmissionWrite(
                        profile.Value.ProfileId,
                        identity,
                        profile.Value.Revision,
                        profile.Value.Python,
                        plan.Value.Environment.ToString(),
                        plan.Value.Models,
                        outcome,
                        machine.ObservedEnvironmentDigest,
                        deviceId,
                        receipt,
                        actorId,
                        machineGeneration,
                        endpointSession,
                        lease is null ? null : attemptId,
                        beforeEffectJson,
                        afterEffectJson,
                        machine.MachineAdmissionIdentity,
                        machine.RecordedAtMs));
                if (!recorded.TryGetValue(out var admission))
                {
                    return ReturnInstallRefusal(
                        recorded.TryGetRefusal(out var refusal)
                            ? refusal.Detail
                            : "the environment admission returned neither a receipt nor a refusal");
                }

                Console.Error.WriteLine(
                    $"environment admission {admission.Status} {admission.SiteId} "
                    + $"{admission.EnvironmentIdentity} in {registryPath}");
                if (lease is not null)
                {
                    Console.Error.WriteLine(
                        $"environment install lease {lease.LeaseId} fencing token {lease.FencingToken} "
                        + $"roots {string.Join(", ", lease.MutationRoots)}");
                }

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    Console.Out.WriteLine(receipt);
                }
                else
                {
                    WriteAtomically(outputPath, receipt);
                    Console.Error.WriteLine($"wrote model-environment receipt copy {outputPath}");
                }
            }

            leaseEndReason = EnvironmentInstallLeaseAuthority.EnvironmentAdmittedReleaseReason;
            return 0;
        }
        finally
        {
            DropCudaValidationGrant(writer, attemptId, ref cudaGrant, "install attempt ended");
            if (lease is not null)
            {
                var released = EnvironmentInstallLeaseAuthority.Release(
                    writer, lease.LeaseId, attemptId, leaseEndReason);
                if (released.TryGetRefusal(out var releaseRefusal))
                {
                    Console.Error.WriteLine(
                        "environment install lease release refused: " + releaseRefusal.Detail);
                }
            }
        }
    }

    static SiteResult<DeviceGrant?> HoldCudaValidationGrant(
        RegistryWriter writer,
        ExecutionProfileV5 profile,
        string attemptId,
        ref DeviceGrant? cudaGrant)
    {
        if (cudaGrant is not null)
        {
            return SiteResult<DeviceGrant?>.Ok(cudaGrant);
        }

        var begun = DeviceGrantAuthority.BeginInstallCudaValidation(
            writer, profile.ProfileId, profile.Device, attemptId);
        if (begun.TryGetValue(out var grant))
        {
            cudaGrant = grant;
            if (grant is not null)
            {
                Console.Error.WriteLine(
                    $"environment cuda validation grant {grant.GrantId} generation {grant.GrantGeneration} "
                    + $"on {grant.DeviceId}");
            }

            return SiteResult<DeviceGrant?>.Ok(grant);
        }

        begun.TryGetRefusal(out var refusal);
        return SiteResult<DeviceGrant?>.Permanent(refusal.Detail);
    }

    static void DropCudaValidationGrant(
        RegistryWriter writer,
        string attemptId,
        ref DeviceGrant? cudaGrant,
        string reason)
    {
        if (cudaGrant is null)
        {
            return;
        }

        var released = DeviceGrantAuthority.Release(writer, cudaGrant.GrantId, attemptId, reason);
        if (released.TryGetRefusal(out var refusal))
        {
            Console.Error.WriteLine("environment cuda validation grant release refused: " + refusal.Detail);
        }
        else
        {
            Console.Error.WriteLine(
                $"environment cuda validation grant {cudaGrant.GrantId} generation {cudaGrant.GrantGeneration} "
                + $"released ({reason})");
        }

        cudaGrant = null;
    }

    static SiteResult<EnvironmentInstallLease> AcquireInstallLease(
        RegistryWriter writer,
        ExecutionProfileV5 profile,
        ConsumedMachineAdmission machine,
        string attemptId,
        string actorId,
        IReadOnlyList<string> mutationRoots)
    {
        var acquired = EnvironmentInstallLeaseAuthority.Acquire(
            writer,
            new EnvironmentInstallLeaseAcquire(
                machine.MachineGeneration,
                profile.Python,
                profile.ProfileId,
                mutationRoots,
                attemptId,
                actorId,
                (long)InstallDeadline.TotalMilliseconds + 60_000));
        if (acquired.TryGetValue(out var lease))
        {
            return SiteResult<EnvironmentInstallLease>.Ok(lease);
        }

        return SiteResult<EnvironmentInstallLease>.Permanent(
            acquired.TryGetRefusal(out var refusal)
                ? refusal.Detail
                : "the environment install lease returned neither a grant nor a refusal");
    }

    internal static SiteResult<ModelEnvironmentInstallation.EnvironmentInstallProcessScan> ObserveInstallAttemptProcesses(
        ExecutionProfileV5 profile,
        string workspaceRoot,
        string ownerAttemptId)
    {
        if (profile.Executor == "local")
        {
            return ModelEnvironmentInstallation.ScanLocalInstallAttemptProcesses(ownerAttemptId);
        }

        var observed = Run(
            profile,
            workspaceRoot,
            ["/usr/bin/env", "python3", "-c", ModelEnvironmentInstallation.InstallAttemptProcessScanPython, ownerAttemptId],
            deadline: TimeSpan.FromSeconds(30));
        if (!observed.IsOk)
        {
            return observed.RefusalAs<ModelEnvironmentInstallation.EnvironmentInstallProcessScan>();
        }

        var json = ModelEnvironmentInstallation.LastJsonObjectLine(observed.Value.StandardOutput)
            ?? ModelEnvironmentInstallation.LastJsonObjectLine(observed.Value.StandardError);
        if (json is null)
        {
            return SiteResult<ModelEnvironmentInstallation.EnvironmentInstallProcessScan>.Permanent(
                "environment install process observer emitted no JSON object");
        }

        var parsed = ModelEnvironmentInstallation.ParseInstallAttemptProcessScan(json);
        if (!parsed.IsOk)
        {
            return parsed;
        }
        if (parsed.Value.OwnerAttemptId != ownerAttemptId)
        {
            return SiteResult<ModelEnvironmentInstallation.EnvironmentInstallProcessScan>.Permanent(
                $"environment install process observer scanned {parsed.Value.OwnerAttemptId}, not {ownerAttemptId}");
        }

        return parsed;
    }

    static string ProbeRequest(ModelEnvironmentInstallationPlan plan, bool localOnly, string device) => JsonSerializer.Serialize(new
    {
        imports = plan.RequiredImports,
        packages = plan.RequiredPackages,
        assets = plan.Assets.Select(asset => new { repository = asset.Repository, revision = asset.Revision }),
        local_only = localOnly,
        device,
    });

    /// <summary>
    /// Delete each named snapshot (files and their content-addressed blobs)
    /// and re-materialize it from the pinned immutable revision. Runs only
    /// under the asset-scoped install lease with the operator's explicit
    /// --repair-assets authorization.
    /// </summary>
    const string RepairPython =
        """
        import json
        import os
        import shutil
        import sys

        request = json.loads(sys.argv[1])
        os.environ["HF_HUB_DISABLE_PROGRESS_BARS"] = "1"
        from huggingface_hub import snapshot_download
        for asset in request["assets"]:
            sys.stderr.write("model-environment phase=repair_delete\n")
            try:
                snapshot = snapshot_download(
                    repo_id=asset["repository"],
                    revision=asset["revision"],
                    local_files_only=True,
                )
            except Exception:
                continue
            blobs = set()
            for parent, _dirs, names in os.walk(snapshot):
                for name in names:
                    path = os.path.join(parent, name)
                    if os.path.islink(path):
                        blobs.add(os.path.realpath(path))
            shutil.rmtree(snapshot)
            for blob in blobs:
                if os.path.exists(blob):
                    os.remove(blob)
            sys.stderr.write("model-environment phase=repair_download\n")
            snapshot_download(
                repo_id=asset["repository"],
                revision=asset["revision"],
                local_files_only=False,
            )
        print(json.dumps({"repaired_assets": len(request["assets"])}))
        """;

    const string ProbePython =
        """
        import importlib
        import importlib.metadata as metadata
        import json
        import os
        import sys

        request = json.loads(sys.argv[1])
        saved_stdout = os.dup(1)
        saved_stderr = os.dup(2)
        quiet = os.open(os.devnull, os.O_WRONLY)
        def phase(name):
            os.write(saved_stderr, f"model-environment phase={name}\n".encode())
        try:
            os.dup2(quiet, 1)
            os.dup2(quiet, 2)
            os.environ["HF_HUB_DISABLE_PROGRESS_BARS"] = "1"
            phase("python_start")
            phase("imports")
            for module in request["imports"]:
                importlib.import_module(module)
            if request.get("device") == "cuda":
                phase("torch_cuda_check")
                import torch
                if not torch.cuda.is_available():
                    raise RuntimeError("cuda profile but torch.cuda.is_available is false")
            phase("checkpoint_local_check")
            from huggingface_hub import snapshot_download
            asset_manifest = {}
            for asset in request["assets"]:
                snapshot = snapshot_download(
                    repo_id=asset["repository"],
                    revision=asset["revision"],
                    local_files_only=request["local_only"],
                )
                # The revision pin proves WHICH snapshot; this manifest proves
                # the snapshot is WHOLE: every file with its byte size and, for
                # cache symlinks, the content-named blob it points at. A
                # truncated or missing shard fails the later drift comparison
                # instead of passing on a successful import.
                files = []
                for parent, _dirs, names in os.walk(snapshot):
                    for name in names:
                        file_path = os.path.join(parent, name)
                        blob = os.path.basename(os.readlink(file_path)) if os.path.islink(file_path) else None
                        files.append({
                            "file": os.path.relpath(file_path, snapshot),
                            "size": os.stat(file_path).st_size,
                            "blob": blob,
                        })
                files.sort(key=lambda entry: entry["file"])
                asset_manifest[asset["repository"] + "@" + asset["revision"]] = files
            phase("package_version_check")
            packages = {package: metadata.version(package) for package in request["packages"]}
        finally:
            os.dup2(saved_stdout, 1)
            os.dup2(saved_stderr, 2)
            os.close(saved_stdout)
            os.close(saved_stderr)
            os.close(quiet)
        print(json.dumps(
            {"python": sys.executable, "packages": packages, "asset_manifest": asset_manifest},
            sort_keys=True,
            separators=(",", ":"),
        ))
        """;

    static void Phase(string phase, Stopwatch elapsed) => Console.Error.WriteLine(
        $"model-environment phase={phase} elapsed_seconds={elapsed.Elapsed.TotalSeconds:F3}");

    sealed record SiteCommandRun(string StandardOutput, string StandardError);

    static SiteResult<SiteCommandRun> Run(
        ExecutionProfileV5 profile,
        string workspaceRoot,
        IReadOnlyList<string> argv,
        string? installAttemptId = null,
        TimeSpan? deadline = null)
    {
        if (argv.Count == 0) return SiteResult<SiteCommandRun>.Permanent("Site command argv is empty");
        var commandLocation = profile.Executor == "ssh" ? "remote" : "local";
        var environmentChangeStarted = !string.IsNullOrWhiteSpace(installAttemptId);
        string NoVerifiedReply(string detail) =>
            environmentChangeStarted
                ? $"{commandLocation} environment command {detail} after environment change started; no verified environment reply was accepted"
                : $"{commandLocation} environment command {detail} before environment change started; no environment mutation was requested";

        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (profile.Executor == "ssh")
        {
            if (profile.SshDestination is not { Length: > 0 } destination)
                return SiteResult<SiteCommandRun>.Permanent("SSH execution profile has no ssh_destination");
            var helper = StagePackageTransport.MaintainedSshHelper(WorktreeControlPaths.SshHelper(workspaceRoot));
            if (!helper.IsOk) return helper.RefusalAs<SiteCommandRun>();
            start.FileName = helper.Value;
            start.Environment["RUNPOD_SSH_TARGET"] = destination;
            start.Environment["RUNPOD_COMMAND_TIMEOUT_SECONDS"] = ((long)(deadline ?? InstallDeadline).TotalSeconds).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--");
            if (!string.IsNullOrWhiteSpace(installAttemptId))
            {
                start.ArgumentList.Add("/usr/bin/env");
                start.ArgumentList.Add(ModelEnvironmentInstallation.InstallAttemptProcessMarker + "=" + installAttemptId);
            }
        }
        else
        {
            start.FileName = argv[0];
            argv = argv.Skip(1).ToArray();
            if (!string.IsNullOrWhiteSpace(installAttemptId))
            {
                start.Environment[ModelEnvironmentInstallation.InstallAttemptProcessMarker] = installAttemptId;
            }
        }
        foreach (var part in argv) start.ArgumentList.Add(part);

        try
        {
            using var process = Process.Start(start);
            if (process is null) return SiteResult<SiteCommandRun>.Permanent("the operating system started no Site command");
            process.StandardInput.Close();
            var stdout = ReadBounded(process.StandardOutput);
            var stderr = ReadBounded(process.StandardError);
            var processDeadline = deadline ?? InstallDeadline;
            if (!process.WaitForExit((int)processDeadline.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return SiteResult<SiteCommandRun>.Permanent(
                    NoVerifiedReply($"gave no reply within {processDeadline.TotalSeconds:F0} seconds"));
            }
            var output = stdout.GetAwaiter().GetResult();
            var errors = stderr.GetAwaiter().GetResult();
            if (output is null || errors is null)
            {
                return SiteResult<SiteCommandRun>.Permanent(
                    NoVerifiedReply("emitted more than the 4 MiB bounded stream limit"));
            }
            if (errors.Length > 0)
            {
                Console.Error.Write(errors);
            }
            if (process.ExitCode == 0)
            {
                return SiteResult<SiteCommandRun>.Ok(new(output, errors));
            }

            var failedCommandOutput = (errors.Length == 0 ? output : errors).Trim();
            return SiteResult<SiteCommandRun>.Permanent(
                NoVerifiedReply($"returned exit code {process.ExitCode}")
                + (failedCommandOutput.Length == 0 ? "" : ": " + failedCommandOutput));
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
        {
            return SiteResult<SiteCommandRun>.Permanent(
                $"cannot start {commandLocation} environment command: " + error.Message);
        }
    }

    static async Task<string?> ReadBounded(StreamReader reader)
    {
        var value = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var count = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0) return value.ToString();
            if (value.Length + count > OutputLimitCharacters) return null;
            value.Append(buffer, 0, count);
        }
    }

    static void WriteAtomically(string path, string receipt)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, receipt + Environment.NewLine, new UTF8Encoding(false));
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    static string Truncate(string value, int limit) =>
        value.Length <= limit ? value : value[..limit] + " ...";

    static int Refuse(string detail)
    {
        Console.Error.WriteLine("site install-model-environment failed: " + detail);
        return 1;
    }
}
