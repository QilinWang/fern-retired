using System.Text.Json;
using Fern.Registry;
using Fern.Scheduler;
using Xunit;

namespace Fern.Scheduler.Tests;

public sealed class MachineAdmissionVerificationTests
{
    [Fact]
    public void MachineAdmissionIdentityIgnoresEndpointSessionButReceiptNamesIt()
    {
        var first = MachineAdmissionVerification.BuildWrite(
            Profile("runpod-a4500.example:1111"),
            Fingerprint(),
            Storage(),
            "test");
        var second = MachineAdmissionVerification.BuildWrite(
            Profile("runpod-a4500.example:2222"),
            Fingerprint(),
            Storage(),
            "test");

        Assert.True(first.IsOk, first.RefusalDetail);
        Assert.True(second.IsOk, second.RefusalDetail);
        var firstIdentity = ReceiptIdentity(first.Value.ReceiptJson);
        var secondIdentity = ReceiptIdentity(second.Value.ReceiptJson);
        Assert.Equal(firstIdentity, secondIdentity);
        Assert.NotEqual(first.Value.EndpointSessionId, second.Value.EndpointSessionId);
    }

    [Fact]
    public void MachineAdmissionVerificationRefusesCudaWithoutGpuUuid()
    {
        var missingUuid = Fingerprint(uuid: null);

        var built = MachineAdmissionVerification.BuildWrite(
            Profile("runpod-a4500.example:1111"),
            missingUuid,
            Storage(),
            "test");

        Assert.False(built.IsOk);
        Assert.Contains("GPU UUID", built.Failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void MachineAdmissionVerificationAndRegistryUseTheSelectedCudaDevice()
    {
        var path = Home();
        using var writer = RegistryHome.OpenWriter(path);
        var built = MachineAdmissionVerification.BuildWrite(
            Profile("runpod-a4500.example:1111", "GPU-selected"),
            TwoGpuFingerprint(),
            Storage(),
            "test");

        Assert.True(built.IsOk, built.RefusalDetail);
        Assert.Equal("GPU-selected", built.Value.DeviceId);
        Assert.Equal("NVIDIA RTX 4090", built.Value.CudaDeviceName);

        var recorded = MachineAdmissionAuthority.Record(writer, built.Value);

        Assert.True(recorded.TryGetValue(out var outcome), Detail(recorded));
        Assert.Equal("GPU-selected", outcome.Admission.DeviceId);
        Assert.Equal("NVIDIA RTX 4090", outcome.Admission.CudaDeviceName);
    }

    [Fact]
    public void MachineAdmissionCommandIsOnTheFernRunSurface()
    {
        var help = FernRunHost.Invoke("--help");

        Assert.Equal(0, help.Exit);
        Assert.Contains("verify-machine-admission", help.Stdout, StringComparison.Ordinal);
    }

    static IReadOnlyList<MachineStorageBoundary> Storage() =>
    [
        new("/workspace/work", "/workspace", "overlay", "hard_stop_passed"),
    ];

    static ExecutionProfileV5 Profile(string endpoint, string deviceUuid = "GPU-test") => new(
        ProfileId: "runpod-a4500",
        Revision: "profile-revision-a",
        Executor: "ssh",
        SshDestination: endpoint,
        RegistryHomeProfileId: "local-home",
        Device: "cuda",
        SourceRoot: "/workspace/study",
        Python: "/workspace/.venv/bin/python",
        WorkerModule: "fern_train.worker",
        WorkerSlots: 1,
        MaxRestarts: 0,
        RestartDelaySeconds: 0,
        MaxTargetSeconds: 60,
        MaxSweepSeconds: 60,
        CampaignRoot: "/workspace/campaign",
        ArtifactRoot: "/workspace/artifacts",
        WorkRoot: "/workspace/work",
        AcceleratorMinMemoryGib: 7,
        AcceleratorMinComputeCapability: [8u, 0u],
        AcceleratorRequiredDeviceUuid: deviceUuid,
        KnownDataRoot: null);

    static ObservedExecutionFingerprint Fingerprint(string? uuid = "GPU-test") => new(
        ObservedExecutionProbe.Schema,
        1_000,
        "test-version",
        new ObservedPlatform("pod-host", "Linux", "6.1", "x86_64"),
        new ObservedPythonRuntime(
            "/workspace/.venv/bin/python",
            "/workspace/.venv/bin/python",
            "3.12.10",
            "CPython",
            "fern_train.worker",
            true),
        new ObservedTorchRuntime(
            "2.9.1+cu130",
            "13.0",
            9_800UL,
            false,
            "highest",
            true,
            true,
            false,
            false),
        true,
        0,
        null,
        "PCI_BUS_ID",
        uuid,
        [new ObservedCudaDevice(
            0,
            "NVIDIA RTX A4500",
            [8u, 6u],
            20_401_094_656UL,
            56u,
            uuid,
            "555.99")]);

    static ObservedExecutionFingerprint TwoGpuFingerprint() => new(
        ObservedExecutionProbe.Schema,
        1_000,
        "test-version",
        new ObservedPlatform("pod-host", "Linux", "6.1", "x86_64"),
        new ObservedPythonRuntime(
            "/workspace/.venv/bin/python",
            "/workspace/.venv/bin/python",
            "3.12.10",
            "CPython",
            "fern_train.worker",
            true),
        new ObservedTorchRuntime(
            "2.9.1+cu130",
            "13.0",
            9_800UL,
            false,
            "highest",
            true,
            true,
            false,
            false),
        true,
        1,
        null,
        "PCI_BUS_ID",
        "GPU-selected",
        [
            new ObservedCudaDevice(
                0,
                "NVIDIA RTX A4500",
                [8u, 6u],
                20_401_094_656UL,
                56u,
                "GPU-first",
                "555.99"),
            new ObservedCudaDevice(
                1,
                "NVIDIA RTX 4090",
                [8u, 9u],
                25_000_000_000UL,
                128u,
                "GPU-selected",
                "560.00"),
        ]);

    static string ReceiptIdentity(string receiptJson)
    {
        using var document = JsonDocument.Parse(receiptJson);
        return document.RootElement.GetProperty("machine_admission_identity").GetString()!;
    }

    static string Detail<T>(RegistryResult<T> result) =>
        result.TryGetRefusal(out var refusal) ? refusal.Detail : "";

    static void ForceLeaseDeadlineIntoThePast(RegistryWriter writer, string leaseId)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = "UPDATE environment_install_leases SET issued_at_ms = 0, deadline_ms = 1 WHERE lease_id = $lease";
        command.Parameters.AddWithValue("$lease", leaseId);
        command.ExecuteNonQuery();
    }

    /// <summary>CHG 2026-09-11 night (slice 9): an admission lives on a registered Site's row, so the Home registers the Sites these tests admit on.</summary>
    static string Home()
    {
        var path = TestRegistry.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        foreach (var site in new[] { "runpod-a4500" })
        {
            Assert.True(RegistryOperations.RegisterSite(writer, site, """{"device":"cuda","executor":"ssh","devices":["GPU-a"]}""", """{"slots":1}""").IsOk);
        }
        return path;
    }
}
