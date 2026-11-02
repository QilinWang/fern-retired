using Xunit;

namespace Fern.Registry.Tests;

public sealed class GpuDeviceIdentityTests
{
    const string WithUuid = """
    {"schema":"fern.observed_execution_fingerprint.v2","cuda_devices":[
      {"index":0,"name":"NVIDIA RTX A4500","uuid":"GPU-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}]}
    """;

    const string DevicesWithoutUuid = """
    {"schema":"fern.observed_execution_fingerprint.v2","cuda_devices":[
      {"index":0,"name":"NVIDIA RTX A4500","capability":[8,6]}]}
    """;

    const string WithSelectedUuid = """
    {"schema":"fern.observed_execution_fingerprint.v2","cuda_current_device":1,"cuda_devices":[
      {"index":0,"name":"NVIDIA RTX A4500","uuid":"GPU-first"},
      {"index":1,"name":"NVIDIA RTX 4090","uuid":"GPU-selected"}]}
    """;

    [Fact]
    public void from_fingerprint_reads_the_first_cuda_uuid()
    {
        Assert.Equal(
            "GPU-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            GpuDeviceIdentity.FromFingerprintJson(WithUuid));
    }

    [Fact]
    public void from_fingerprint_reads_the_current_cuda_device_when_selected()
    {
        Assert.Equal(
            "GPU-selected",
            GpuDeviceIdentity.FromFingerprintJson(WithSelectedUuid));
    }

    [Fact]
    public void an_empty_fingerprint_is_not_a_gpu()
    {
        Assert.Null(GpuDeviceIdentity.FromFingerprintJson("{}"));
        Assert.Null(GpuDeviceIdentity.RequireForGpuCapacity("{}", 1));
    }

    [Fact]
    public void cuda_devices_without_a_uuid_refuse_gpu_capacity()
    {
        var error = Assert.Throws<ArgumentException>(
            () => GpuDeviceIdentity.RequireForGpuCapacity(DevicesWithoutUuid, 1));
        Assert.Contains("GPU UUID", error.Message, StringComparison.Ordinal);
        Assert.Null(GpuDeviceIdentity.RequireForGpuCapacity(DevicesWithoutUuid, 0));
    }
}
