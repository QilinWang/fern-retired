using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fern.Registry;
using Fern.Site;

namespace Fern.Scheduler;

public sealed record MachineStorageBoundary(
    string RequestedPath,
    string InspectedPath,
    string Filesystem,
    string Verdict);

/// <summary>
/// Verification Pass M0: observe the Site/machine boundary once and publish the
/// machine admission Registry consumers can reuse. No package install,
/// model-link probe, smoke run, Plan, Cell, or scientific result happens here.
/// </summary>
public static class MachineAdmissionVerification
{
    public const string ObserverIdentity = "csharp.machine_admission_verifier.v1";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    public static RegistryResult<MachineAdmissionRecordOutcome> ObserveAndRecord(
        RegistryWriter writer,
        string workspaceRoot,
        string profileId,
        string observerVersion,
        string recordedBy)
    {
        var observed = Observe(workspaceRoot, profileId, observerVersion, recordedBy);
        if (!observed.IsOk)
        {
            return Reject(observed.Failure.Detail);
        }

        return MachineAdmissionAuthority.Record(writer, observed.Value);
    }

    public static SiteResult<MachineAdmissionWrite> Observe(
        string workspaceRoot,
        string profileId,
        string observerVersion,
        string recordedBy)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)
            || string.IsNullOrWhiteSpace(profileId)
            || string.IsNullOrWhiteSpace(observerVersion)
            || string.IsNullOrWhiteSpace(recordedBy))
        {
            return SiteResult<MachineAdmissionWrite>.Permanent(
                "machine admission verification requires workspace root, profile id, observer version, and actor");
        }

        var loaded = ExecutionProfileV5.Read(WorktreeControlPaths.Profiles(workspaceRoot), profileId, expectedRevision: null);
        if (!loaded.IsOk)
        {
            return loaded.RefusalAs<MachineAdmissionWrite>();
        }

        var profile = loaded.Value;
        var knownRootProblem = ApplyDeclaredKnownDataRoot(profile);
        if (knownRootProblem is not null)
        {
            return SiteResult<MachineAdmissionWrite>.Permanent(knownRootProblem);
        }

        if (profile.Executor == "ssh")
        {
            var helper = StagePackageTransport.MaintainedSshHelper(WorktreeControlPaths.SshHelper(workspaceRoot));
            if (!helper.IsOk)
            {
                return helper.RefusalAs<MachineAdmissionWrite>();
            }

            var remote = ObservedExecutionProbe.ObserveRemote(
                profile,
                helper.Value,
                workspaceRoot,
                observerVersion,
                ReadyExecutionGate.RemoteProbeDeadline);
            if (!remote.IsOk)
            {
                return remote.RefusalAs<MachineAdmissionWrite>();
            }

            var drift = ReadyExecutionGate.DriftFrom(remote.Value.Fingerprint, profile);
            if (drift.Count != 0)
            {
                return SiteResult<MachineAdmissionWrite>.Permanent(
                    DriftDetail(profile.ProfileId, drift, "remote execution profile drift"));
            }

            var storage = RemoteStorageBoundaries(remote.Value.Storage);
            if (!storage.IsOk)
            {
                return storage.RefusalAs<MachineAdmissionWrite>();
            }

            foreach (var path in remote.Value.Paths)
            {
                var problem = RemotePathProblem(profile, path);
                if (problem is not null)
                {
                    return SiteResult<MachineAdmissionWrite>.Permanent(problem);
                }
            }

            return BuildWrite(profile, remote.Value.Fingerprint, storage.Value, recordedBy);
        }

        var local = ObservedExecutionProbe.Observe(
            profile,
            observerVersion,
            ReadyExecutionGate.LocalProbeDeadline);
        if (!local.IsOk)
        {
            return local.RefusalAs<MachineAdmissionWrite>();
        }

        var localDrift = ReadyExecutionGate.DriftFrom(local.Value, profile);
        if (localDrift.Count != 0)
        {
            return SiteResult<MachineAdmissionWrite>.Permanent(
                DriftDetail(profile.ProfileId, localDrift, "execution profile drift"));
        }

        var localStorage = LocalStorageBoundaries(profile);
        if (!localStorage.IsOk)
        {
            return localStorage.RefusalAs<MachineAdmissionWrite>();
        }

        return BuildWrite(profile, local.Value, localStorage.Value, recordedBy);
    }

    public static SiteResult<MachineAdmissionWrite> BuildWrite(
        ExecutionProfileV5 profile,
        ObservedExecutionFingerprint observed,
        IReadOnlyList<MachineStorageBoundary> storage,
        string recordedBy)
    {
        if (storage.Count == 0)
        {
            return SiteResult<MachineAdmissionWrite>.Permanent(
                "machine admission requires at least one storage hard-stop boundary");
        }

        string pythonAbi;
        string machineGeneration;
        string? deviceId;
        string fingerprintJson;
        try
        {
            pythonAbi = ExecutionPlacementIdentity.PythonAbi(observed.Python.Version);
            fingerprintJson = ObservedExecutionProbe.Render(observed);
            deviceId = GpuDeviceIdentity.RequireForGpuCapacity(
                fingerprintJson,
                profile.Device == "cuda" ? 1 : 0);
            machineGeneration = ExecutionPlacementIdentity.MachineGeneration(
                profile.Executor,
                observed.Platform.Hostname,
                deviceId);
        }
        catch (ArgumentException error)
        {
            return SiteResult<MachineAdmissionWrite>.Permanent(error.Message);
        }

        var selected = ReadyExecutionGate.SelectedDevice(observed);
        if (profile.Device == "cuda" && selected is null)
        {
            return SiteResult<MachineAdmissionWrite>.Permanent(
                "a CUDA machine admission requires one selected CUDA device");
        }

        var storageJson = JsonSerializer.Serialize(
            storage.OrderBy(item => item.RequestedPath, StringComparer.Ordinal), JsonOptions);
        var storageDigest = Sha256Prefixed(storageJson);
        var endpointSession = ExecutionPlacementIdentity.EndpointSession(
            profile.Executor,
            profile.SshDestination);
        var fingerprintDigest = ObservedExecutionProbe.ObservationDigest(observed);
        var driverVersion = selected?.DriverVersion;
        var cudaCapability = selected is null
            ? null
            : string.Join(".", selected.Capability.Select(part => part.ToString()));
        var cudaMemory = selected is null ? null : (long?)checked((long)selected.TotalMemoryBytes);
        var cudnn = observed.Torch.CudnnVersion is null
            ? null
            : (long?)checked((long)observed.Torch.CudnnVersion.Value);
        var identity = MachineAdmissionAuthority.Identity(
            profile.ProfileId,
            machineGeneration,
            profile.Device,
            deviceId,
            observed.Platform.Hostname,
            pythonAbi,
            observed.Python.Version,
            observed.Python.SysExecutable,
            observed.Torch.Version,
            observed.Torch.CudaRuntimeVersion,
            cudnn,
            driverVersion,
            storageDigest);
        var receipt = JsonSerializer.Serialize(new
        {
            schema = MachineAdmissionAuthority.ReceiptSchema,
            outcome = "machine_admitted",
            proof_layer = "machine-admission",
            observer = ObserverIdentity,
            site_id = profile.ProfileId,
            machine_admission_identity = identity,
            machine_generation = machineGeneration,
            endpoint_session_id = endpointSession,
            execution_profile_revision = profile.Revision,
            executor = profile.Executor,
            hostname = observed.Platform.Hostname,
            device = profile.Device,
            device_id = deviceId,
            cuda_available = observed.CudaAvailable,
            cuda_device = selected is null
                ? null
                : new
                {
                    name = selected.Name,
                    compute_capability = cudaCapability,
                    total_memory_bytes = cudaMemory,
                    driver_version = driverVersion,
                },
            python = new
            {
                abi = pythonAbi,
                version = observed.Python.Version,
                executable = observed.Python.SysExecutable,
                requested_executable = observed.Python.RequestedExecutable,
                implementation = observed.Python.Implementation,
                worker_module = observed.Python.WorkerModule,
                worker_module_available = observed.Python.WorkerModuleAvailable,
            },
            torch = new
            {
                version = observed.Torch.Version,
                cuda_runtime = observed.Torch.CudaRuntimeVersion,
                cudnn_version = cudnn,
                deterministic_algorithms = observed.Torch.DeterministicAlgorithms,
                float32_matmul_precision = observed.Torch.Float32MatmulPrecision,
                cuda_matmul_allow_tf32 = observed.Torch.CudaMatmulAllowTf32,
                cudnn_allow_tf32 = observed.Torch.CudnnAllowTf32,
                cudnn_deterministic = observed.Torch.CudnnDeterministic,
                cudnn_benchmark = observed.Torch.CudnnBenchmark,
            },
            storage_digest = storageDigest,
            storage = JsonSerializer.Deserialize<JsonElement>(storageJson),
            observed_fingerprint_digest = fingerprintDigest,
            observed_at_ms = checked((long)observed.ObservedAtUnixMs),
            process_liveness = "probe_completed",
            semantic_progress = "machine_observation_completed",
            terminal_outcome = "machine_admitted",
            recorded_by = recordedBy,
        }, JsonOptions);

        return SiteResult<MachineAdmissionWrite>.Ok(new MachineAdmissionWrite(
            profile.ProfileId,
            machineGeneration,
            profile.Revision,
            profile.Executor,
            endpointSession,
            observed.Platform.Hostname,
            profile.Device,
            deviceId,
            observed.CudaAvailable,
            selected?.Name,
            cudaCapability,
            cudaMemory,
            driverVersion,
            pythonAbi,
            observed.Python.Version,
            observed.Python.SysExecutable,
            observed.Torch.Version,
            observed.Torch.CudaRuntimeVersion,
            cudnn,
            storageDigest,
            storageJson,
            fingerprintDigest,
            fingerprintJson,
            receipt,
            recordedBy));
    }

    static SiteResult<IReadOnlyList<MachineStorageBoundary>> LocalStorageBoundaries(ExecutionProfileV5 profile)
    {
        var roots = new[] { profile.CampaignRoot, profile.ArtifactRoot, profile.WorkRoot }
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var boundaries = new List<MachineStorageBoundary>();
        foreach (var root in roots)
        {
            var reading = StorageProbe.Inspect(root);
            if (reading.Check is not { } check)
            {
                return SiteResult<IReadOnlyList<MachineStorageBoundary>>.Permanent(
                    reading.Refusal ?? $"cannot inspect storage for {root}");
            }
            if (!seen.Add(check.Filesystem))
            {
                continue;
            }
            if (check.HardStop)
            {
                return SiteResult<IReadOnlyList<MachineStorageBoundary>>.Permanent(
                    $"{check.Filesystem} has only {check.AvailableGib:F1} GiB / {check.FreePercent:F1}% free; "
                    + $"Fern must not admit a machine below {ReadyExecutionGate.CampaignStorageHardStopFreeGib} GiB");
            }

            boundaries.Add(new MachineStorageBoundary(
                root,
                check.InspectedPath,
                check.Filesystem,
                "hard_stop_passed"));
        }

        return SiteResult<IReadOnlyList<MachineStorageBoundary>>.Ok(boundaries);
    }

    static SiteResult<IReadOnlyList<MachineStorageBoundary>> RemoteStorageBoundaries(
        IReadOnlyList<RemoteStorage> storage)
    {
        if (storage.Count == 0)
        {
            return SiteResult<IReadOnlyList<MachineStorageBoundary>>.Permanent(
                "remote machine admission requires the Site storage report; remote state is unknown");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var boundaries = new List<MachineStorageBoundary>();
        foreach (var volume in storage)
        {
            if (volume.TotalBytes == 0)
            {
                return SiteResult<IReadOnlyList<MachineStorageBoundary>>.Permanent(
                    $"remote storage probe returned zero total bytes for {volume.RequestedPath}");
            }
            if (!seen.Add(volume.Filesystem))
            {
                continue;
            }
            var availableGib = volume.AvailableBytes / (1024.0 * 1024.0 * 1024.0);
            if (availableGib < ReadyExecutionGate.CampaignStorageHardStopFreeGib)
            {
                return SiteResult<IReadOnlyList<MachineStorageBoundary>>.Permanent(
                    $"remote filesystem {volume.Filesystem} has only {availableGib:F1} GiB free; "
                    + $"Fern must not admit a machine below {ReadyExecutionGate.CampaignStorageHardStopFreeGib} GiB");
            }

            boundaries.Add(new MachineStorageBoundary(
                volume.RequestedPath,
                volume.InspectedPath,
                volume.Filesystem,
                "hard_stop_passed"));
        }

        return SiteResult<IReadOnlyList<MachineStorageBoundary>>.Ok(boundaries);
    }

    static string? RemotePathProblem(ExecutionProfileV5 profile, RemotePath path)
    {
        if (profile.KnownDataRoot is { } known && path.RequestedPath == known)
        {
            return path.Exists && path.IsDirectory
                ? null
                : $"the remote recorded-data root {path.RequestedPath} is absent or is not a directory";
        }

        return path.WritableAncestor
            ? null
            : $"the remote path {path.RequestedPath} has no writable existing ancestor";
    }

    static string? ApplyDeclaredKnownDataRoot(ExecutionProfileV5 profile)
    {
        if (profile.KnownDataRoot is not { } declared)
        {
            return null;
        }

        var existing = Environment.GetEnvironmentVariable("SERIE_KNOWN_ROOT");
        if (existing is null)
        {
            Environment.SetEnvironmentVariable("SERIE_KNOWN_ROOT", declared);
            return null;
        }

        return Path.TrimEndingDirectorySeparator(existing) == Path.TrimEndingDirectorySeparator(declared)
            ? null
            : $"SERIE_KNOWN_ROOT={existing} disagrees with the execution profile, which declares {declared}";
    }

    static string DriftDetail(string profileId, IReadOnlyList<ExecutionProfileDrift> drift, string label)
    {
        var lines = string.Join("\n", drift.Select(item =>
            $"- {label} at {item.Field}: expected {item.Expected}; observed {item.Observed}"));
        return $"cannot admit machine for profile \"{profileId}\":\n{lines}\nNo GPU work started.";
    }

    static string Sha256Prefixed(string text) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    static RegistryResult<MachineAdmissionRecordOutcome> Reject(string detail) =>
        RegistryResult<MachineAdmissionRecordOutcome>.Rejected(
            new RegistryRefusal("refused", "invalid_request", detail));
}
