using Xunit;

namespace Fern.Registry.Tests;

public sealed class DeviceGrantTests
{
    const string Gpu = "GPU-4070-UUID";

    [Fact]
    public void TwoProfilesOnOneUuidContendForTheSameSingleGrant()
    {
        // The dual-profile falsifier: local-4070 and local-4070-zeus are two
        // interpreters on ONE card. Environment compatibility must never
        // create capacity, so the second acquire is refused naming the holder.
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var first = DeviceGrantAuthority.Acquire(
            writer, Gpu, "local-4070", "cell", "cell-attempt-17", maxConcurrent: 1);
        Assert.True(first.TryGetValue(out var grant), Detail(first));
        Assert.Equal(1, grant.GrantGeneration);

        var second = DeviceGrantAuthority.Acquire(
            writer, Gpu, "local-4070-zeus", "cell", "cell-attempt-18", maxConcurrent: 1);
        Assert.True(second.TryGetRefusal(out var refusal), Detail(second));
        Assert.Contains("cell-attempt-17", refusal.Detail, StringComparison.Ordinal);
        Assert.Contains("generation 1", refusal.Detail, StringComparison.Ordinal);
        Assert.Contains("lost transport does not release", refusal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ADifferentPhysicalDeviceGrantsIndependently()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        Assert.True(DeviceGrantAuthority.Acquire(
            writer, Gpu, "local-4070", "cell", "cell-a", 1).IsOk);
        var other = DeviceGrantAuthority.Acquire(
            writer, "GPU-A4500-UUID", "runpod-a4500", "cell", "cell-b", 1);
        Assert.True(other.IsOk, Detail(other));
    }

    [Fact]
    public void OwnerReleaseAdvancesTheGenerationForTheSuccessor()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var first = DeviceGrantAuthority.Acquire(
            writer, Gpu, "local-4070", "cell", "cell-a", 1).Expect("DeviceGrantAuthority.Acquire");
        Assert.True(DeviceGrantAuthority.Release(writer, first.GrantId, "cell-a", "cell accepted").IsOk);

        var successor = DeviceGrantAuthority.Acquire(
            writer, Gpu, "local-4070", "cell", "cell-b", 1);
        Assert.True(successor.TryGetValue(out var next), Detail(successor));
        Assert.Equal(2, next.GrantGeneration);
    }

    [Fact]
    public void ANonOwnerCannotReleaseButReconcileReleaseCanWithItsOutcome()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var grant = DeviceGrantAuthority.Acquire(
            writer, Gpu, "local-4070", "cell", "cell-a", 1).Expect("DeviceGrantAuthority.Acquire");

        var stolen = DeviceGrantAuthority.Release(writer, grant.GrantId, "cell-b", "mine now");
        Assert.True(stolen.TryGetRefusal(out var refusal));
        Assert.Contains("owned by cell-a", refusal.Detail, StringComparison.Ordinal);

        var reconciled = DeviceGrantAuthority.ReconcileRelease(
            writer, grant.GrantId, "remote execution absent after restart reconciliation");
        Assert.True(reconciled.IsOk, Detail(reconciled));
        Assert.Empty(DeviceGrantAuthority.LiveForDevice(writer.Connection, Gpu));
    }

    [Fact]
    public void ADeclaredCapacityOfTwoAllowsTwoThenRefusesTheThird()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        Assert.True(DeviceGrantAuthority.Acquire(writer, Gpu, "site-a", "cell", "cell-a", 2).IsOk);
        Assert.True(DeviceGrantAuthority.Acquire(writer, Gpu, "site-b", "cell", "cell-b", 2).IsOk);
        var third = DeviceGrantAuthority.Acquire(writer, Gpu, "site-a", "cell", "cell-c", 2);
        Assert.True(third.TryGetRefusal(out var refusal));
        Assert.Contains("2 live grant(s) of 2", refusal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ACpuProfileDoesNotClaimACudaValidationGrant()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var granted = DeviceGrantAuthority.BeginInstallCudaValidation(
            writer, "local-4070", "cpu", "environment-install:cpu");
        Assert.True(granted.TryGetValue(out var grant), Detail(granted));
        Assert.Null(grant);
        Assert.Empty(DeviceGrantAuthority.LiveForDevice(writer.Connection, Gpu));
    }

    [Fact]
    public void CudaValidationIsRefusedWhileACellHoldsTheDeviceAndReleasedAfterwardsLetsACellAcquire()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        SeedOneStoreSite(writer, "runpod-a4500", Gpu);

        Assert.True(DeviceGrantAuthority.Acquire(
            writer, Gpu, "runpod-a4500", "cell", "cell-attempt-71", 1).IsOk);

        var blocked = DeviceGrantAuthority.BeginInstallCudaValidation(
            writer, "runpod-a4500", "cuda", "environment-install:9");
        Assert.True(blocked.TryGetRefusal(out var refusal), Detail(blocked));
        Assert.Contains("cell-attempt-71", refusal.Detail, StringComparison.Ordinal);
        Assert.Contains("lost transport does not release", refusal.Detail, StringComparison.Ordinal);

        Assert.True(DeviceGrantAuthority.Release(
            writer,
            DeviceGrantAuthority.LiveForDevice(writer.Connection, Gpu)[0].GrantId,
            "cell-attempt-71",
            "cell accepted").IsOk);

        var validation = DeviceGrantAuthority.BeginInstallCudaValidation(
            writer, "runpod-a4500", "cuda", "environment-install:9");
        Assert.True(validation.TryGetValue(out var grant), Detail(validation));
        Assert.Equal("environment_install", grant!.OwnerKind);
        Assert.Equal(2, grant.GrantGeneration);
        Assert.True(DeviceGrantAuthority.Release(
            writer, grant.GrantId, "environment-install:9", "cuda validation finished").IsOk);
        Assert.Empty(DeviceGrantAuthority.LiveForDevice(writer.Connection, Gpu));

        var cell = DeviceGrantAuthority.Acquire(
            writer, Gpu, "runpod-a4500", "cell", "cell-attempt-72", 1);
        Assert.True(cell.TryGetValue(out var next), Detail(cell));
        Assert.Equal(3, next.GrantGeneration);
    }

    [Fact]
    public void CudaValidationOnASiteNobodyRegisteredIsRefused()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var refused = DeviceGrantAuthority.BeginInstallCudaValidation(
            writer, "runpod-a4500", "cuda", "environment-install:9");
        Assert.True(refused.TryGetRefusal(out var refusal), Detail(refused));
        Assert.Contains("requires that Site to be registered", refusal.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// CHG 2026-09-11 night (slice 6, row 35): the card comes from the Site's
    /// own declaration; a registered Site that declares no card is refused
    /// rather than occupying an untracked one.
    /// </summary>
    [Fact]
    public void CudaValidationOnASiteThatDeclaresNoCardIsRefused()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        SeedOneStoreSite(writer, "runpod-a4500", null);
        var refused = DeviceGrantAuthority.BeginInstallCudaValidation(
            writer, "runpod-a4500", "cuda", "environment-install:9");
        Assert.True(refused.TryGetRefusal(out var refusal), Detail(refused));
        Assert.Contains("requires a declared card", refusal.Detail, StringComparison.Ordinal);
    }

    /// <summary>The one-store Site row with its declared card, which is what the CUDA validation grant reads (slice 6).</summary>
    static void SeedOneStoreSite(RegistryWriter writer, string name, string? gpu)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO sites (name, capabilities_json, capacity_json, created_at)
            VALUES ($name, $capabilities, '{"slots":1}', '2026-09-11')
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$capabilities", gpu is null ? """{"device":"cuda","devices":[]}""" : $$"""{"device":"cuda","devices":["{{gpu}}"]}""");
        command.ExecuteNonQuery();
    }

    [Fact]
    public void AnInstallOwnerIsADistinctKindAndReleasesIdempotently()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = RegistryHome.OpenWriter(path);
        var grant = DeviceGrantAuthority.Acquire(
            writer, Gpu, "local-4070", "environment_install", "environment-install:abc", 1)
            .Expect("DeviceGrantAuthority.Acquire");
        Assert.Equal("environment_install", grant.OwnerKind);
        Assert.True(DeviceGrantAuthority.Release(
            writer, grant.GrantId, "environment-install:abc", "cuda validation finished").IsOk);
        Assert.True(DeviceGrantAuthority.Release(
            writer, grant.GrantId, "environment-install:abc", "cuda validation finished").IsOk);
    }


    static void SeedMachineAdmission(RegistryWriter writer, string siteId, string deviceId)
    {
        var recorded = MachineAdmissionAuthority.Record(
            writer,
            MachineAdmissionTests.Write(
                siteId: siteId,
                endpoint: "endpoint:a",
                deviceId: deviceId,
                generation: "machine:" + siteId));
        Assert.True(recorded.IsOk, Detail(recorded));
    }

    static string Detail<T>(RegistryResult<T> result) =>
        result.TryGetRefusal(out var refusal) ? refusal.Detail : "";
}
