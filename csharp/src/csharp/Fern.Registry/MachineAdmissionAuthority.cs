using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Fern.Registry;

/// <summary>
/// The current Site-level proof that one physical machine generation and its
/// basic runtime were observed. This is not an environment install, a
/// model-link proof, a smoke run, or a scientific result.
/// </summary>
public sealed record MachineAdmission(
    string SiteId,
    string MachineAdmissionIdentity,
    string MachineGeneration,
    string ExecutionProfileRevision,
    string Executor,
    string? EndpointSessionId,
    string Hostname,
    string Device,
    string? DeviceId,
    bool CudaAvailable,
    string? CudaDeviceName,
    string? CudaComputeCapability,
    long? CudaTotalMemoryBytes,
    string? DriverVersion,
    string PythonAbi,
    string PythonVersion,
    string PythonExecutable,
    string TorchVersion,
    string? TorchCudaRuntime,
    long? CudnnVersion,
    string StorageDigest,
    string StorageJson,
    string ObservedFingerprintDigest,
    string ObservedFingerprintJson,
    string ReceiptJson,
    string SemanticDigest,
    string Outcome,
    long RecordedAtMs,
    string RecordedBy);

public sealed record MachineAdmissionWrite(
    string SiteId,
    string MachineGeneration,
    string ExecutionProfileRevision,
    string Executor,
    string? EndpointSessionId,
    string Hostname,
    string Device,
    string? DeviceId,
    bool CudaAvailable,
    string? CudaDeviceName,
    string? CudaComputeCapability,
    long? CudaTotalMemoryBytes,
    string? DriverVersion,
    string PythonAbi,
    string PythonVersion,
    string PythonExecutable,
    string TorchVersion,
    string? TorchCudaRuntime,
    long? CudnnVersion,
    string StorageDigest,
    string StorageJson,
    string ObservedFingerprintDigest,
    string ObservedFingerprintJson,
    string ReceiptJson,
    string RecordedBy);

/// <summary>
/// Status is <c>admitted</c> for a first Site proof,
/// <c>already_admitted</c> for an exact replay,
/// <c>endpoint_session_refreshed</c> when only the transport session changed,
/// <c>reobserved</c> when the same machine proof was sampled again, and
/// <c>replaced</c> when the Site now points at a different machine identity.
/// </summary>
public sealed record MachineAdmissionRecordOutcome(
    string Status,
    MachineAdmission Admission,
    string? TransitionReceiptId);

public static class MachineAdmissionAuthority
{
    public const string ObjectKind = "csharp:machine-admission";
    public const string PolicyRevision = "fern.machine_admission.v1";
    public const string ReceiptSchema = "fern.machine_admission_receipt.v1";


    public static string Identity(
        string siteId,
        string machineGeneration,
        string device,
        string? deviceId,
        string hostname,
        string pythonAbi,
        string pythonVersion,
        string pythonExecutable,
        string torchVersion,
        string? torchCudaRuntime,
        long? cudnnVersion,
        string? driverVersion,
        string storageDigest)
    {
        foreach (var (name, value) in new[]
                 {
                     ("site", siteId),
                     ("machine_generation", machineGeneration),
                     ("device", device),
                     ("hostname", hostname),
                     ("python_abi", pythonAbi),
                     ("python_version", pythonVersion),
                     ("python_executable", pythonExecutable),
                     ("torch_version", torchVersion),
                     ("storage_digest", storageDigest),
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"machine admission identity requires {name}");
            }
        }

        return Hash(
            "fern.machine_admission.identity.v1",
            siteId.Trim(),
            machineGeneration.Trim(),
            device.Trim(),
            string.IsNullOrWhiteSpace(deviceId) ? "cpu" : deviceId.Trim(),
            hostname.Trim(),
            pythonAbi.Trim(),
            pythonVersion.Trim(),
            pythonExecutable.Trim(),
            torchVersion.Trim(),
            string.IsNullOrWhiteSpace(torchCudaRuntime) ? "none" : torchCudaRuntime.Trim(),
            cudnnVersion?.ToString(CultureInfo.InvariantCulture) ?? "none",
            string.IsNullOrWhiteSpace(driverVersion) ? "none" : driverVersion.Trim(),
            storageDigest.Trim());
    }



    /// <summary>The current machine admission of the Site named, from its row. CHG 2026-09-11 night (slice 9): was the machine_admissions table.</summary>
    public static MachineAdmission? Current(SqliteConnection connection, string siteId) =>
        SiteAdmissions.Read<MachineAdmission>(connection, siteId, SiteAdmissions.MachineKey);

    /// <summary>The Site whose machine admission carries this identity, if any.</summary>
    public static MachineAdmission? CurrentByIdentity(SqliteConnection connection, string identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            return null;
        }
        foreach (var (name, _) in SiteAdmissions.All(connection))
        {
            var admission = Current(connection, name);
            if (admission is not null && admission.MachineAdmissionIdentity == identity)
            {
                return admission;
            }
        }
        return null;
    }

    public static RegistryResult<MachineAdmissionRecordOutcome> Record(
        RegistryWriter writer,
        MachineAdmissionWrite write)
    {
        var refused = Validate(write);
        if (refused is not null)
        {
            return Reject(refused);
        }

        string identity;
        try
        {
            identity = Identity(
                write.SiteId,
                write.MachineGeneration,
                write.Device,
                write.DeviceId,
                write.Hostname,
                write.PythonAbi,
                write.PythonVersion,
                write.PythonExecutable,
                write.TorchVersion,
                write.TorchCudaRuntime,
                write.CudnnVersion,
                write.DriverVersion,
                write.StorageDigest);
        }
        catch (ArgumentException error)
        {
            return Reject(error.Message);
        }

        refused = ValidateEvidenceConsistency(write, identity);
        if (refused is not null)
        {
            return Reject(refused);
        }

        var semanticDigest = SemanticDigest(write);

        return writer.WriteImmediate(connection => RecordInTransaction(
            connection, write, identity, semanticDigest));
    }

    static RegistryResult<MachineAdmissionRecordOutcome> RecordInTransaction(
        SqliteConnection connection,
        MachineAdmissionWrite write,
        string identity,
        string semanticDigest)
    {
        // CHG 2026-09-11 night (slice 9): the admission lives on the Site's
        // row, so the Site must exist first; register-site mints it.
        if (!SiteAdmissions.SiteExists(connection, write.SiteId))
        {
            return Reject($"Site {write.SiteId} is not registered in this Home; register-site mints it from the execution profile before verify-machine-admission");
        }

        var nowMs = DatabaseNowMs(connection);
        var existing = Current(connection, write.SiteId);
        if (existing is not null
            && existing.MachineAdmissionIdentity == identity
            && existing.SemanticDigest == semanticDigest
            && string.Equals(existing.EndpointSessionId, write.EndpointSessionId, StringComparison.Ordinal)
            && string.Equals(existing.ExecutionProfileRevision, write.ExecutionProfileRevision, StringComparison.Ordinal))
        {
            return RegistryResult<MachineAdmissionRecordOutcome>.Ok(
                new MachineAdmissionRecordOutcome("already_admitted", existing, null));
        }

        var status = existing switch
        {
            null => "admitted",
            { MachineAdmissionIdentity: var prior } when prior != identity => "replaced",
            { EndpointSessionId: var priorEndpoint } when priorEndpoint != write.EndpointSessionId => "endpoint_session_refreshed",
            _ => "reobserved",
        };

        SiteAdmissions.Write(connection, write.SiteId, SiteAdmissions.MachineKey, Admitted(write, identity, semanticDigest, nowMs));

        var receiptId = AppendReceipt(
            connection,
            write,
            status,
            existing?.MachineAdmissionIdentity,
            identity,
            nowMs);
        var current = Current(connection, write.SiteId)
            ?? throw new RegistryFault($"machine admission for Site {write.SiteId} was not readable after write");
        return RegistryResult<MachineAdmissionRecordOutcome>.Ok(
            new MachineAdmissionRecordOutcome(status, current, receiptId));
    }

    static string? Validate(MachineAdmissionWrite write)
    {
        if (string.IsNullOrWhiteSpace(write.SiteId)
            || string.IsNullOrWhiteSpace(write.MachineGeneration)
            || string.IsNullOrWhiteSpace(write.ExecutionProfileRevision)
            || write.Executor is not ("local" or "ssh")
            || string.IsNullOrWhiteSpace(write.Hostname)
            || string.IsNullOrWhiteSpace(write.Device)
            || string.IsNullOrWhiteSpace(write.PythonAbi)
            || string.IsNullOrWhiteSpace(write.PythonVersion)
            || string.IsNullOrWhiteSpace(write.PythonExecutable)
            || string.IsNullOrWhiteSpace(write.TorchVersion)
            || string.IsNullOrWhiteSpace(write.StorageDigest)
            || string.IsNullOrWhiteSpace(write.StorageJson)
            || string.IsNullOrWhiteSpace(write.ObservedFingerprintDigest)
            || string.IsNullOrWhiteSpace(write.ObservedFingerprintJson)
            || string.IsNullOrWhiteSpace(write.ReceiptJson)
            || string.IsNullOrWhiteSpace(write.RecordedBy))
        {
            return "machine admission requires site, machine generation, profile revision, executor, hostname, device, Python ABI/runtime, Torch runtime, storage digest, observed fingerprint, receipt, and actor";
        }

        if (write.Device == "cuda" && string.IsNullOrWhiteSpace(write.DeviceId))
        {
            return "a CUDA machine admission requires the observed GPU UUID device_id";
        }

        try
        {
            using var storage = JsonDocument.Parse(write.StorageJson);
            if (storage.RootElement.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
            {
                return "machine admission storage_json must be a JSON object or array";
            }
            using var fingerprint = JsonDocument.Parse(write.ObservedFingerprintJson);
            if (fingerprint.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "machine admission observed_fingerprint_json must be a JSON object";
            }
            using var receipt = JsonDocument.Parse(write.ReceiptJson);
            var root = receipt.RootElement;
            if (!root.TryGetProperty("schema", out var schema)
                || schema.GetString() != ReceiptSchema
                || !root.TryGetProperty("outcome", out var outcome)
                || outcome.GetString() != "machine_admitted")
            {
                return $"machine admission receipt must be {ReceiptSchema} with outcome machine_admitted";
            }
        }
        catch (JsonException error)
        {
            return "machine admission JSON is invalid: " + error.Message;
        }

        return null;
    }

    static string? ValidateEvidenceConsistency(MachineAdmissionWrite write, string identity)
    {
        var expectedStorageDigest = Sha256Prefixed(write.StorageJson);
        if (!string.Equals(write.StorageDigest, expectedStorageDigest, StringComparison.Ordinal))
        {
            return $"machine admission storage_digest {write.StorageDigest} does not match {expectedStorageDigest}";
        }

        var expectedFingerprintDigest = Sha256Prefixed(write.ObservedFingerprintJson);
        if (!string.Equals(write.ObservedFingerprintDigest, expectedFingerprintDigest, StringComparison.Ordinal))
        {
            return $"machine admission observed_fingerprint_digest {write.ObservedFingerprintDigest} does not match {expectedFingerprintDigest}";
        }

        try
        {
            using var storage = JsonDocument.Parse(write.StorageJson);
            using var fingerprint = JsonDocument.Parse(write.ObservedFingerprintJson);
            using var receipt = JsonDocument.Parse(write.ReceiptJson);

            var receiptRoot = receipt.RootElement;
            var fingerprintRoot = fingerprint.RootElement;

            var receiptProblem = FirstProblem(
                RequireText(receiptRoot, "schema", ReceiptSchema, "machine admission receipt"),
                RequireText(receiptRoot, "outcome", "machine_admitted", "machine admission receipt"),
                RequireText(receiptRoot, "site_id", write.SiteId, "machine admission receipt"),
                RequireText(receiptRoot, "machine_admission_identity", identity, "machine admission receipt"),
                RequireText(receiptRoot, "machine_generation", write.MachineGeneration, "machine admission receipt"),
                RequireNullableText(receiptRoot, "endpoint_session_id", write.EndpointSessionId, "machine admission receipt"),
                RequireText(receiptRoot, "execution_profile_revision", write.ExecutionProfileRevision, "machine admission receipt"),
                RequireText(receiptRoot, "executor", write.Executor, "machine admission receipt"),
                RequireText(receiptRoot, "hostname", write.Hostname, "machine admission receipt"),
                RequireText(receiptRoot, "device", write.Device, "machine admission receipt"),
                RequireNullableText(receiptRoot, "device_id", write.DeviceId, "machine admission receipt"),
                RequireBoolean(receiptRoot, "cuda_available", write.CudaAvailable, "machine admission receipt"),
                RequireText(receiptRoot, "storage_digest", write.StorageDigest, "machine admission receipt"),
                RequireText(receiptRoot, "observed_fingerprint_digest", write.ObservedFingerprintDigest, "machine admission receipt"),
                RequireText(receiptRoot, "terminal_outcome", "machine_admitted", "machine admission receipt"));
            if (receiptProblem is not null)
            {
                return receiptProblem;
            }

            if (!receiptRoot.TryGetProperty("storage", out var receiptStorage))
            {
                return "machine admission receipt.storage is required";
            }
            if (!JsonElement.DeepEquals(receiptStorage, storage.RootElement))
            {
                return "machine admission receipt.storage does not match storage_json";
            }

            if (!receiptRoot.TryGetProperty("python", out var receiptPython)
                || receiptPython.ValueKind != JsonValueKind.Object)
            {
                return "machine admission receipt.python must be an object";
            }
            receiptProblem = FirstProblem(
                RequireText(receiptPython, "abi", write.PythonAbi, "machine admission receipt.python"),
                RequireText(receiptPython, "version", write.PythonVersion, "machine admission receipt.python"),
                RequireText(receiptPython, "executable", write.PythonExecutable, "machine admission receipt.python"));
            if (receiptProblem is not null)
            {
                return receiptProblem;
            }

            if (!receiptRoot.TryGetProperty("torch", out var receiptTorch)
                || receiptTorch.ValueKind != JsonValueKind.Object)
            {
                return "machine admission receipt.torch must be an object";
            }
            receiptProblem = FirstProblem(
                RequireText(receiptTorch, "version", write.TorchVersion, "machine admission receipt.torch"),
                RequireNullableText(receiptTorch, "cuda_runtime", write.TorchCudaRuntime, "machine admission receipt.torch"),
                RequireNullableLong(receiptTorch, "cudnn_version", write.CudnnVersion, "machine admission receipt.torch"));
            if (receiptProblem is not null)
            {
                return receiptProblem;
            }

            if (write.CudaDeviceName is null)
            {
                if (!receiptRoot.TryGetProperty("cuda_device", out var receiptCuda)
                    || receiptCuda.ValueKind != JsonValueKind.Null)
                {
                    return "machine admission receipt.cuda_device must be null when no CUDA device was selected";
                }
            }
            else
            {
                if (!receiptRoot.TryGetProperty("cuda_device", out var receiptCuda)
                    || receiptCuda.ValueKind != JsonValueKind.Object)
                {
                    return "machine admission receipt.cuda_device must be an object for an observed CUDA device";
                }
                receiptProblem = FirstProblem(
                    RequireText(receiptCuda, "name", write.CudaDeviceName, "machine admission receipt.cuda_device"),
                    RequireNullableText(receiptCuda, "compute_capability", write.CudaComputeCapability, "machine admission receipt.cuda_device"),
                    RequireNullableLong(receiptCuda, "total_memory_bytes", write.CudaTotalMemoryBytes, "machine admission receipt.cuda_device"),
                    RequireNullableText(receiptCuda, "driver_version", write.DriverVersion, "machine admission receipt.cuda_device"));
                if (receiptProblem is not null)
                {
                    return receiptProblem;
                }
            }

            var fingerprintProblem = FirstProblem(
                RequireText(fingerprintRoot, "schema", "fern.observed_execution_fingerprint.v2", "machine admission observed_fingerprint_json"),
                RequireBoolean(fingerprintRoot, "cuda_available", write.CudaAvailable, "machine admission observed_fingerprint_json"));
            if (fingerprintProblem is not null)
            {
                return fingerprintProblem;
            }

            if (!fingerprintRoot.TryGetProperty("platform", out var platform)
                || platform.ValueKind != JsonValueKind.Object)
            {
                return "machine admission observed_fingerprint_json.platform must be an object";
            }
            fingerprintProblem = RequireText(platform, "hostname", write.Hostname, "machine admission observed_fingerprint_json.platform");
            if (fingerprintProblem is not null)
            {
                return fingerprintProblem;
            }

            if (!fingerprintRoot.TryGetProperty("python", out var fingerprintPython)
                || fingerprintPython.ValueKind != JsonValueKind.Object)
            {
                return "machine admission observed_fingerprint_json.python must be an object";
            }
            fingerprintProblem = FirstProblem(
                RequireText(fingerprintPython, "version", write.PythonVersion, "machine admission observed_fingerprint_json.python"),
                RequireText(fingerprintPython, "sys_executable", write.PythonExecutable, "machine admission observed_fingerprint_json.python"));
            if (fingerprintProblem is not null)
            {
                return fingerprintProblem;
            }

            if (!fingerprintRoot.TryGetProperty("torch", out var fingerprintTorch)
                || fingerprintTorch.ValueKind != JsonValueKind.Object)
            {
                return "machine admission observed_fingerprint_json.torch must be an object";
            }
            fingerprintProblem = FirstProblem(
                RequireText(fingerprintTorch, "version", write.TorchVersion, "machine admission observed_fingerprint_json.torch"),
                RequireNullableText(fingerprintTorch, "cuda_runtime_version", write.TorchCudaRuntime, "machine admission observed_fingerprint_json.torch"),
                RequireNullableLong(fingerprintTorch, "cudnn_version", write.CudnnVersion, "machine admission observed_fingerprint_json.torch"));
            if (fingerprintProblem is not null)
            {
                return fingerprintProblem;
            }

            if (write.DeviceId is not null)
            {
                var cudaDevice = FindFingerprintCudaDevice(fingerprintRoot, write.DeviceId);
                if (cudaDevice is null)
                {
                    return $"machine admission observed_fingerprint_json.cuda_devices does not contain device_id {write.DeviceId}";
                }
                fingerprintProblem = FirstProblem(
                    RequireText(cudaDevice.Value, "name", write.CudaDeviceName ?? "", "machine admission observed_fingerprint_json.cuda_devices[]"),
                    RequireCapability(cudaDevice.Value, write.CudaComputeCapability),
                    RequireNullableUnsignedLong(cudaDevice.Value, "total_memory_bytes", write.CudaTotalMemoryBytes, "machine admission observed_fingerprint_json.cuda_devices[]"),
                    RequireNullableText(cudaDevice.Value, "driver_version", write.DriverVersion, "machine admission observed_fingerprint_json.cuda_devices[]"));
                if (fingerprintProblem is not null)
                {
                    return fingerprintProblem;
                }
            }
        }
        catch (JsonException error)
        {
            return "machine admission JSON is invalid: " + error.Message;
        }

        return null;
    }

    static JsonElement? FindFingerprintCudaDevice(JsonElement fingerprintRoot, string deviceId)
    {
        if (!fingerprintRoot.TryGetProperty("cuda_devices", out var devices)
            || devices.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var device in devices.EnumerateArray())
        {
            if (device.ValueKind == JsonValueKind.Object
                && device.TryGetProperty("uuid", out var uuid)
                && uuid.ValueKind == JsonValueKind.String
                && uuid.GetString() == deviceId)
            {
                return device.Clone();
            }
        }

        return null;
    }

    static string? RequireText(JsonElement owner, string name, string expected, string context)
    {
        if (!owner.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() != expected)
        {
            return $"{context}.{name} must be {expected}";
        }

        return null;
    }

    static string? RequireNullableText(JsonElement owner, string name, string? expected, string context)
    {
        if (!owner.TryGetProperty(name, out var value))
        {
            return $"{context}.{name} is required";
        }

        if (expected is null)
        {
            return value.ValueKind == JsonValueKind.Null
                ? null
                : $"{context}.{name} must be null";
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() != expected)
        {
            return $"{context}.{name} must be {expected}";
        }

        return null;
    }

    static string? RequireBoolean(JsonElement owner, string name, bool expected, string context)
    {
        if (!owner.TryGetProperty(name, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || value.GetBoolean() != expected)
        {
            return $"{context}.{name} must be {expected.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}";
        }

        return null;
    }

    static string? RequireNullableLong(JsonElement owner, string name, long? expected, string context)
    {
        if (!owner.TryGetProperty(name, out var value))
        {
            return $"{context}.{name} is required";
        }

        if (expected is null)
        {
            return value.ValueKind == JsonValueKind.Null
                ? null
                : $"{context}.{name} must be null";
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var actual) || actual != expected.Value)
        {
            return $"{context}.{name} must be {expected.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        return null;
    }

    static string? RequireNullableUnsignedLong(JsonElement owner, string name, long? expected, string context)
    {
        if (expected is null)
        {
            return RequireNullableLong(owner, name, null, context);
        }

        if (expected.Value < 0)
        {
            return $"{context}.{name} must be non-negative";
        }

        if (!owner.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetUInt64(out var actual)
            || actual != (ulong)expected.Value)
        {
            return $"{context}.{name} must be {expected.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        return null;
    }

    static string? RequireCapability(JsonElement owner, string? expected)
    {
        if (expected is null)
        {
            return null;
        }

        if (!owner.TryGetProperty("capability", out var capability)
            || capability.ValueKind != JsonValueKind.Array)
        {
            return "machine admission observed_fingerprint_json.cuda_devices[].capability must be an array";
        }

        string actual;
        try
        {
            actual = string.Join(".", capability.EnumerateArray().Select(part => part.GetUInt32().ToString(CultureInfo.InvariantCulture)));
        }
        catch (InvalidOperationException)
        {
            return "machine admission observed_fingerprint_json.cuda_devices[].capability must contain unsigned integer values";
        }
        catch (FormatException)
        {
            return "machine admission observed_fingerprint_json.cuda_devices[].capability must contain unsigned integer values";
        }

        return actual == expected
            ? null
            : $"machine admission observed_fingerprint_json.cuda_devices[].capability must be {expected}";
    }

    static string? FirstProblem(params string?[] candidates) =>
        candidates.FirstOrDefault(problem => problem is not null);




    /// <summary>The admission as recorded: the write's observation with its identity, its semantic digest and the moment.</summary>
    static MachineAdmission Admitted(MachineAdmissionWrite write, string identity, string semanticDigest, long nowMs) => new(
        write.SiteId, identity, write.MachineGeneration, write.ExecutionProfileRevision, write.Executor,
        write.EndpointSessionId, write.Hostname, write.Device, write.DeviceId, write.CudaAvailable,
        write.CudaDeviceName, write.CudaComputeCapability, write.CudaTotalMemoryBytes, write.DriverVersion,
        write.PythonAbi, write.PythonVersion, write.PythonExecutable, write.TorchVersion, write.TorchCudaRuntime,
        write.CudnnVersion, write.StorageDigest, write.StorageJson, write.ObservedFingerprintDigest,
        write.ObservedFingerprintJson, write.ReceiptJson, semanticDigest, "machine_admitted", nowMs, write.RecordedBy);

    static string SemanticDigest(MachineAdmissionWrite write) => Hash(
        "fern.machine_admission.semantic.v1",
        write.SiteId,
        write.MachineGeneration,
        write.ExecutionProfileRevision,
        write.Executor,
        write.EndpointSessionId ?? "",
        write.Hostname,
        write.Device,
        write.DeviceId ?? "",
        write.PythonAbi,
        write.PythonVersion,
        write.PythonExecutable,
        write.TorchVersion,
        write.TorchCudaRuntime ?? "",
        write.CudnnVersion?.ToString(CultureInfo.InvariantCulture) ?? "",
        write.DriverVersion ?? "",
        write.StorageDigest,
        write.ObservedFingerprintDigest,
        Sha256(write.ReceiptJson));

    static string AppendReceipt(
        SqliteConnection connection,
        MachineAdmissionWrite write,
        string status,
        string? prior,
        string next,
        long nowMs)
    {
        var written = Journals.AppendTransitionReceiptInOpenTransaction(
            connection,
            new TransitionReceiptWrite(
                OperationName: "machine-admission " + status.Replace('_', '-'),
                ObjectKind: ObjectKind,
                ObjectId: write.SiteId,
                PriorState: prior,
                NextState: next,
                ActorKind: "cli",
                ActorId: write.RecordedBy,
                InputIdentityJson: JsonSerializer.Serialize(new
                {
                    site_id = write.SiteId,
                    machine_generation = write.MachineGeneration,
                    endpoint_session_id = write.EndpointSessionId,
                    execution_profile_revision = write.ExecutionProfileRevision,
                }),
                OutputIdentityJson: JsonSerializer.Serialize(new
                {
                    site_id = write.SiteId,
                    machine_admission_identity = next,
                    status,
                }),
                PolicyRevision: PolicyRevision,
                CorrelationId: write.SiteId,
                OccurredAtMs: nowMs));
        if (written.TryGetValue(out var receiptId))
        {
            return receiptId;
        }

        if (written.TryGetRefusal(out var refusal))
        {
            throw new RegistryFault(refusal.Detail);
        }

        throw new RegistryFault("cannot append machine-admission receipt");
    }

    static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    static long DatabaseNowMs(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(unixepoch('subsec') * 1000 AS INTEGER)";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static string Hash(params string[] parts)
    {
        var material = string.Join('\u001f', parts);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    static string Sha256Prefixed(string value) => "sha256:" + Sha256(value);

    static RegistryResult<MachineAdmissionRecordOutcome> Reject(string detail) =>
        RegistryResult<MachineAdmissionRecordOutcome>.Rejected(
            new RegistryRefusal("refused", "invalid_request", detail));
}
