using Xunit;

namespace Fern.Registry.Tests;

public sealed class ExecutionPlacementIdentityTests
{
    const string Gpu = "GPU-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    const string OtherGpu = "GPU-bbbbbbbb-cccc-dddd-eeee-ffffffffffff";

    [Fact]
    public void SshPortChangeIsNotANewMachineOrGpu()
    {
        var yesterday = ExecutionPlacementIdentity.MachineGeneration("ssh", "pod-host", Gpu);
        var today = ExecutionPlacementIdentity.MachineGeneration("ssh", "pod-host", Gpu);
        var yesterdayEndpoint = ExecutionPlacementIdentity.EndpointSession("ssh", "root@192.0.2.1:43001");
        var todayEndpoint = ExecutionPlacementIdentity.EndpointSession("ssh", "root@192.0.2.1:43002");

        Assert.Equal(yesterday, today);
        Assert.NotEqual(yesterdayEndpoint, todayEndpoint);
        Assert.NotEqual(yesterday, yesterdayEndpoint);
    }

    [Fact]
    public void ANewGpuOnTheSameSiteNameIsANewMachineGeneration()
    {
        var beforeRebuild = ExecutionPlacementIdentity.MachineGeneration("ssh", "pod-host", Gpu);
        var afterRebuild = ExecutionPlacementIdentity.MachineGeneration("ssh", "pod-host", OtherGpu);

        Assert.NotEqual(beforeRebuild, afterRebuild);
    }

    [Fact]
    public void TwoInterpretersOnOneGpuShareOneMachineGeneration()
    {
        var chronos = ExecutionPlacementIdentity.MachineGeneration("ssh", "pod-host", Gpu);
        var zeus = ExecutionPlacementIdentity.MachineGeneration("ssh", "pod-host", Gpu);

        Assert.Equal(chronos, zeus);
        Assert.NotEqual(
            chronos,
            ExecutionPlacementIdentity.MachineGeneration("local", "laptop", Gpu));
    }

    [Fact]
    public void PythonAbiIsMajorMinorNotTheExactPatch()
    {
        Assert.Equal("cpython-3.12", ExecutionPlacementIdentity.PythonAbi("3.12.7"));
        Assert.Equal("cpython-3.12", ExecutionPlacementIdentity.PythonAbi("3.12.11 (main, Jan 1 2026)"));
        Assert.NotEqual(
            ExecutionPlacementIdentity.PythonAbi("3.12.7"),
            ExecutionPlacementIdentity.PythonAbi("3.11.9"));
    }

}
