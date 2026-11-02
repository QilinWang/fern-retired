using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Fern.Registry.Tests;

public sealed class MachineAdmissionTests
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    [Fact]
    public void RecordMachineAdmissionReplaysExactProof()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = Open(path);

        var recorded = MachineAdmissionAuthority.Record(writer, Write());

        Assert.True(recorded.TryGetValue(out var outcome), Detail(recorded));
        Assert.Equal("admitted", outcome.Status);
        Assert.StartsWith("sha256:", outcome.Admission.MachineAdmissionIdentity, StringComparison.Ordinal);
        Assert.Contains("fern.machine_admission_receipt.v1", outcome.Admission.ReceiptJson, StringComparison.Ordinal);

        var replay = MachineAdmissionAuthority.Record(writer, Write());
        Assert.True(replay.TryGetValue(out var replayed), Detail(replay));
        Assert.Equal("already_admitted", replayed.Status);
        Assert.Equal(outcome.Admission.MachineAdmissionIdentity, replayed.Admission.MachineAdmissionIdentity);
    }

    [Fact]
    public void EndpointSessionChangeRefreshesTheCurrentRowWithoutChangingMachineIdentity()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = Open(path);
        var first = MachineAdmissionAuthority.Record(writer, Write(endpoint: "endpoint:old"));
        Assert.True(first.TryGetValue(out var firstOutcome), Detail(first));

        var changed = MachineAdmissionAuthority.Record(writer, Write(endpoint: "endpoint:new"));

        Assert.True(changed.TryGetValue(out var changedOutcome), Detail(changed));
        Assert.Equal("endpoint_session_refreshed", changedOutcome.Status);
        Assert.Equal(firstOutcome.Admission.MachineAdmissionIdentity, changedOutcome.Admission.MachineAdmissionIdentity);
        Assert.Equal("endpoint:new", changedOutcome.Admission.EndpointSessionId);
        Assert.Equal("endpoint:new", MachineAdmissionAuthority.Current(writer.Connection, "site-a")?.EndpointSessionId);
    }

    [Fact]
    public void ChangedPhysicalGpuReplacesTheCurrentMachineAdmission()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = Open(path);
        var first = MachineAdmissionAuthority.Record(writer, Write(deviceId: "GPU-old", generation: "machine:old"));
        Assert.True(first.TryGetValue(out var firstOutcome), Detail(first));

        var changed = MachineAdmissionAuthority.Record(writer, Write(deviceId: "GPU-new", generation: "machine:new"));

        Assert.True(changed.TryGetValue(out var changedOutcome), Detail(changed));
        Assert.Equal("replaced", changedOutcome.Status);
        Assert.NotEqual(firstOutcome.Admission.MachineAdmissionIdentity, changedOutcome.Admission.MachineAdmissionIdentity);
        Assert.Equal("GPU-new", MachineAdmissionAuthority.Current(writer.Connection, "site-a")?.DeviceId);
    }

    [Fact]
    public void CudaMachineAdmissionRequiresAGpuUuid()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = Open(path);

        var refused = MachineAdmissionAuthority.Record(writer, Write(deviceId: null));

        Assert.True(refused.TryGetRefusal(out var refusal), Detail(refused));
        Assert.Contains("GPU UUID", refusal.Detail, StringComparison.Ordinal);
        Assert.Null(MachineAdmissionAuthority.Current(writer.Connection, "site-a"));
    }

    [Fact]
    public void RecordRefusesStorageDigestThatDoesNotHashStorageJson()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = Open(path);

        var refused = MachineAdmissionAuthority.Record(
            writer,
            Write(storageDigestOverride: "sha256:" + new string('9', 64)));

        Assert.True(refused.TryGetRefusal(out var refusal), Detail(refused));
        Assert.Contains("storage_digest", refusal.Detail, StringComparison.Ordinal);
        Assert.Contains("does not match", refusal.Detail, StringComparison.Ordinal);
        Assert.Null(MachineAdmissionAuthority.Current(writer.Connection, "site-a"));
    }

    [Fact]
    public void RecordRefusesObservedFingerprintDigestThatDoesNotHashFingerprintJson()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = Open(path);

        var refused = MachineAdmissionAuthority.Record(
            writer,
            Write(fingerprintDigestOverride: "sha256:" + new string('8', 64)));

        Assert.True(refused.TryGetRefusal(out var refusal), Detail(refused));
        Assert.Contains("observed_fingerprint_digest", refusal.Detail, StringComparison.Ordinal);
        Assert.Contains("does not match", refusal.Detail, StringComparison.Ordinal);
        Assert.Null(MachineAdmissionAuthority.Current(writer.Connection, "site-a"));
    }

    [Fact]
    public void RecordRefusesReceiptWhoseMachineIdentityDoesNotMatchTheWrite()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = Open(path);

        var refused = MachineAdmissionAuthority.Record(
            writer,
            Write(receiptIdentityOverride: "sha256:" + new string('7', 64)));

        Assert.True(refused.TryGetRefusal(out var refusal), Detail(refused));
        Assert.Contains("machine_admission_identity", refusal.Detail, StringComparison.Ordinal);
        Assert.Null(MachineAdmissionAuthority.Current(writer.Connection, "site-a"));
    }

    [Fact]
    public void RecordRefusesReceiptWhoseGpuDoesNotMatchTheWrite()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = Open(path);

        var refused = MachineAdmissionAuthority.Record(
            writer,
            Write(receiptDeviceIdOverride: "GPU-other"));

        Assert.True(refused.TryGetRefusal(out var refusal), Detail(refused));
        Assert.Contains("device_id", refusal.Detail, StringComparison.Ordinal);
        Assert.Null(MachineAdmissionAuthority.Current(writer.Connection, "site-a"));
    }

    [Fact]
    public void RecordRefusesFingerprintWhoseGpuDoesNotMatchTheWrite()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = Open(path);

        var refused = MachineAdmissionAuthority.Record(
            writer,
            Write(fingerprintDeviceIdOverride: "GPU-other"));

        Assert.True(refused.TryGetRefusal(out var refusal), Detail(refused));
        Assert.Contains("cuda_devices", refusal.Detail, StringComparison.Ordinal);
        Assert.Null(MachineAdmissionAuthority.Current(writer.Connection, "site-a"));
    }

    [Fact]
    public void RecordRefusesReceiptStorageThatDoesNotMatchStorageJson()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = Open(path);

        var refused = MachineAdmissionAuthority.Record(
            writer,
            Write(receiptStorageJsonOverride:
                """[{"requested_path":"/workspace/other","inspected_path":"/workspace","filesystem":"overlay","verdict":"hard_stop_passed"}]"""));

        Assert.True(refused.TryGetRefusal(out var refusal), Detail(refused));
        Assert.Contains("receipt.storage", refusal.Detail, StringComparison.Ordinal);
        Assert.Null(MachineAdmissionAuthority.Current(writer.Connection, "site-a"));
    }

    [Fact]
    public void RecordRefusesReceiptWhosePythonRuntimeDoesNotMatchTheWrite()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = Open(path);

        var refused = MachineAdmissionAuthority.Record(
            writer,
            Write(receiptPythonExecutableOverride: "/other/python"));

        Assert.True(refused.TryGetRefusal(out var refusal), Detail(refused));
        Assert.Contains("receipt.python.executable", refusal.Detail, StringComparison.Ordinal);
        Assert.Null(MachineAdmissionAuthority.Current(writer.Connection, "site-a"));
    }

    internal static MachineAdmissionWrite Write(
        string siteId = "site-a",
        string endpoint = "endpoint:old",
        string? deviceId = "GPU-a",
        string generation = "machine:a",
        string pythonExecutable = "/workspace/.venv/bin/python",
        string? storageDigestOverride = null,
        string? fingerprintDigestOverride = null,
        string? receiptIdentityOverride = null,
        string? receiptDeviceIdOverride = null,
        string? fingerprintDeviceIdOverride = null,
        string? receiptStorageJsonOverride = null,
        string? receiptPythonExecutableOverride = null)
    {
        var hostname = "pod-host";
        var pythonAbi = "cpython-3.12";
        var pythonVersion = "3.12.10";
        var torchVersion = "2.9.1+cu130";
        var torchCudaRuntime = "13.0";
        long? cudnnVersion = 9_800L;
        var cudaDeviceName = deviceId is null ? null : "NVIDIA RTX A4500";
        var cudaComputeCapability = deviceId is null ? null : "8.6";
        long? cudaTotalMemory = deviceId is null ? null : 20_401_094_656L;
        var driverVersion = deviceId is null ? null : "555.99";
        var storageJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RequestedPath = "/workspace/work",
                InspectedPath = "/workspace",
                Filesystem = "overlay",
                Verdict = "hard_stop_passed",
            },
        }, JsonOptions);
        var storageDigest = storageDigestOverride ?? Sha256Prefixed(storageJson);
        var fingerprintJson = FingerprintJson(
            hostname,
            pythonVersion,
            pythonExecutable,
            torchVersion,
            torchCudaRuntime,
            cudnnVersion,
            deviceId,
            fingerprintDeviceIdOverride,
            cudaDeviceName,
            cudaComputeCapability,
            cudaTotalMemory,
            driverVersion);
        var fingerprintDigest = fingerprintDigestOverride ?? Sha256Prefixed(fingerprintJson);
        var identity = MachineAdmissionAuthority.Identity(
            siteId,
            generation,
            "cuda",
            deviceId,
            hostname,
            pythonAbi,
            pythonVersion,
            pythonExecutable,
            torchVersion,
            torchCudaRuntime,
            cudnnVersion,
            driverVersion,
            storageDigest);
        var receipt = ReceiptJson(
            identity,
            receiptIdentityOverride,
            generation,
            endpoint,
            hostname,
            deviceId,
            receiptDeviceIdOverride,
            pythonAbi,
            pythonVersion,
            pythonExecutable,
            torchVersion,
            torchCudaRuntime,
            cudnnVersion,
            cudaDeviceName,
            cudaComputeCapability,
            cudaTotalMemory,
            driverVersion,
            storageDigest,
            storageJson,
            fingerprintDigest,
            siteId,
            receiptStorageJsonOverride,
            receiptPythonExecutableOverride);

        return new MachineAdmissionWrite(
            siteId,
            generation,
            "profile-revision-a",
            "ssh",
            endpoint,
            hostname,
            "cuda",
            deviceId,
            true,
            cudaDeviceName,
            cudaComputeCapability,
            cudaTotalMemory,
            driverVersion,
            pythonAbi,
            pythonVersion,
            pythonExecutable,
            torchVersion,
            torchCudaRuntime,
            cudnnVersion,
            storageDigest,
            storageJson,
            fingerprintDigest,
            fingerprintJson,
            receipt,
            "test");
    }

    static string FingerprintJson(
        string hostname,
        string pythonVersion,
        string pythonExecutable,
        string torchVersion,
        string? torchCudaRuntime,
        long? cudnnVersion,
        string? deviceId,
        string? fingerprintDeviceIdOverride,
        string? cudaDeviceName,
        string? cudaComputeCapability,
        long? cudaTotalMemory,
        string? driverVersion)
    {
        object[] devices = deviceId is null
            ? []
            : [
                new
                {
                    Index = 0U,
                    Name = cudaDeviceName!,
                    Capability = cudaComputeCapability!.Split('.').Select(uint.Parse).ToArray(),
                    TotalMemoryBytes = (ulong)cudaTotalMemory!.Value,
                    MultiProcessorCount = 58U,
                    Uuid = fingerprintDeviceIdOverride ?? deviceId,
                    DriverVersion = driverVersion,
                },
            ];

        return JsonSerializer.Serialize(new
        {
            Schema = "fern.observed_execution_fingerprint.v2",
            ObservedAtUnixMs = 1UL,
            FernVersion = "test",
            Platform = new
            {
                Hostname = hostname,
                System = "Linux",
                Release = "test",
                Machine = "x86_64",
            },
            Python = new
            {
                RequestedExecutable = pythonExecutable,
                SysExecutable = pythonExecutable,
                Version = pythonVersion,
                Implementation = "CPython",
                WorkerModule = "fern_stage_runtime.worker",
                WorkerModuleAvailable = true,
            },
            Torch = new
            {
                Version = torchVersion,
                CudaRuntimeVersion = torchCudaRuntime,
                CudnnVersion = cudnnVersion,
                DeterministicAlgorithms = false,
                Float32MatmulPrecision = "highest",
                CudaMatmulAllowTf32 = true,
                CudnnAllowTf32 = true,
                CudnnDeterministic = false,
                CudnnBenchmark = false,
            },
            CudaAvailable = true,
            CudaCurrentDevice = deviceId is null ? null : (uint?)0U,
            CudaVisibleDevices = (string?)null,
            CudaDeviceOrder = (string?)null,
            NvidiaVisibleDevices = (string?)null,
            CudaDevices = devices,
        }, JsonOptions);
    }

    static string ReceiptJson(
        string identity,
        string? receiptIdentityOverride,
        string generation,
        string endpoint,
        string hostname,
        string? deviceId,
        string? receiptDeviceIdOverride,
        string pythonAbi,
        string pythonVersion,
        string pythonExecutable,
        string torchVersion,
        string? torchCudaRuntime,
        long? cudnnVersion,
        string? cudaDeviceName,
        string? cudaComputeCapability,
        long? cudaTotalMemory,
        string? driverVersion,
        string storageDigest,
        string storageJson,
        string fingerprintDigest,
        string siteId = "site-a",
        string? receiptStorageJsonOverride = null,
        string? receiptPythonExecutableOverride = null)
    {
        var storage = JsonSerializer.Deserialize<JsonElement>(receiptStorageJsonOverride ?? storageJson);
        object? cudaDevice = cudaDeviceName is null
            ? null
            : new
            {
                Name = cudaDeviceName,
                ComputeCapability = cudaComputeCapability,
                TotalMemoryBytes = cudaTotalMemory,
                DriverVersion = driverVersion,
            };

        return JsonSerializer.Serialize(new
        {
            Schema = MachineAdmissionAuthority.ReceiptSchema,
            Outcome = "machine_admitted",
            ProofLayer = "machine-admission",
            Observer = "test.machine_admission_verifier",
            SiteId = siteId,
            MachineAdmissionIdentity = receiptIdentityOverride ?? identity,
            MachineGeneration = generation,
            EndpointSessionId = endpoint,
            ExecutionProfileRevision = "profile-revision-a",
            Executor = "ssh",
            Hostname = hostname,
            Device = "cuda",
            DeviceId = receiptDeviceIdOverride ?? deviceId,
            CudaAvailable = true,
            CudaDevice = cudaDevice,
            Python = new
            {
                Abi = pythonAbi,
                Version = pythonVersion,
                Executable = receiptPythonExecutableOverride ?? pythonExecutable,
                RequestedExecutable = pythonExecutable,
                Implementation = "CPython",
                WorkerModule = "fern_stage_runtime.worker",
                WorkerModuleAvailable = true,
            },
            Torch = new
            {
                Version = torchVersion,
                CudaRuntime = torchCudaRuntime,
                CudnnVersion = cudnnVersion,
                DeterministicAlgorithms = false,
                Float32MatmulPrecision = "highest",
                CudaMatmulAllowTf32 = true,
                CudnnAllowTf32 = true,
                CudnnDeterministic = false,
                CudnnBenchmark = false,
            },
            StorageDigest = storageDigest,
            Storage = storage,
            ObservedFingerprintDigest = fingerprintDigest,
            ObservedAtMs = 1L,
            ProcessLiveness = "probe_completed",
            SemanticProgress = "machine_observation_completed",
            TerminalOutcome = "machine_admitted",
            RecordedBy = "test",
        }, JsonOptions);
    }

    static string Sha256Prefixed(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    static string Detail<T>(RegistryResult<T> result) =>
        result.TryGetRefusal(out var refusal) ? refusal.Detail : "";

    /// <summary>CHG 2026-09-11 night (slice 9): an admission lives on a registered Site's row, so the fixtures register the Sites the tests name.</summary>
    static RegistryWriter Open(string path)
    {
        var writer = RegistryHome.OpenWriter(path);
        foreach (var site in new[] { "site-a", "runpod-a4500", "site-content", "site-effect" })
        {
            Assert.True(RegistryOperations.RegisterSite(writer, site, """{"device":"cuda","executor":"ssh","devices":["GPU-a"]}""", """{"slots":1}""").IsOk);
        }
        return writer;
    }
}
