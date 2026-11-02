using System.Text;
using System.Text.Json;
using Fern.Registry;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Slice 3 of the cell port (row 29): a machine declares its cards at
/// registration, from its profile, by hand, or by asking nvidia-smi on
/// itself; registering the same name again is the refresh.
/// </summary>
public sealed class SiteRegistrationDevicesTests
{
    sealed class ListingHost(string stdout, int exitCode = 0) : WorkerEnvironmentPreparation.IHost
    {
        public bool IsLinux => true;
        public string? Program { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public BoundedProbe.Outcome Run(string program, IReadOnlyList<string> arguments, long timeoutMs, long maxBytes,
            string workingDirectory, IReadOnlyDictionary<string, string> environment)
        {
            Program = program;
            Arguments = arguments;
            return new BoundedProbe.Outcome.Completed(exitCode, Encoding.UTF8.GetBytes(stdout), "");
        }
    }

    static string Profile(string devices = "") =>
        $$"""
        {"schema":"fern.execution_profile.v5","profile_id":"box","executor":"local","device":"cuda",
         "source_root":"/home/x/source","python":"/home/x/.venv/bin/python","worker":{"slots":2},
         "admitted_models":["fern"]{{devices}}}
        """;

    static string Written(string profileJson, IReadOnlyList<string>? extra = null)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("fern-profile-").FullName, "box.json");
        File.WriteAllText(path, profileJson);
        var declared = SiteRegistration.Read(path);
        Assert.True(declared.IsOk, declared.RefusalDetail);
        var declaring = extra is null ? declared.Value : declared.Value with { Devices = [.. declared.Value.Devices, .. extra] };
        using var writer = RegistryHome.OpenWriter(TestRegistry.CreateRegistry());
        Assert.True(SiteRegistration.Register(writer, "box", declaring).IsOk);
        using var read = writer.Connection.CreateCommand();
        read.CommandText = "SELECT capabilities_json FROM sites WHERE name = 'box'";
        return (string)read.ExecuteScalar()!;
    }

    static IReadOnlyList<string> Devices(string capabilitiesJson)
    {
        using var document = JsonDocument.Parse(capabilitiesJson);
        return document.RootElement.TryGetProperty("devices", out var devices)
            ? devices.EnumerateArray().Select(card => card.GetString()!).ToList()
            : [];
    }

    [Fact]
    public void AProfileThatNamesNoCardIsCountedBySlots()
    {
        Assert.Empty(Devices(Written(Profile())));
    }

    [Fact]
    public void AProfilesCardsAreDeclaredOnTheSiteInOrder()
    {
        var capabilities = Written(Profile(""","devices":["GPU-1111","GPU-2222"]"""));
        Assert.Equal(["GPU-1111", "GPU-2222"], Devices(capabilities));
    }

    [Fact]
    public void CardsGivenByHandJoinTheProfilesAndAreReadBackByTheClaim()
    {
        var capabilities = Written(Profile(""","devices":["GPU-1111"]"""), extra: ["GPU-3333"]);
        Assert.Equal(["GPU-1111", "GPU-3333"], Devices(capabilities));
    }

    [Fact]
    public void ObservingCardsAsksNvidiaSmiForUuidsAndKeepsOnlyThem()
    {
        var host = new ListingHost("GPU-aaaa-1\nGPU-bbbb-2\n\nnot a card\n");
        var observed = SiteRegistration.ObserveCards(host, nvidiaSmi: "/opt/nvidia-smi");
        Assert.True(observed.IsOk, observed.RefusalDetail);
        Assert.Equal(["GPU-aaaa-1", "GPU-bbbb-2"], observed.Value);
        Assert.Equal("/opt/nvidia-smi", host.Program);
        Assert.Equal(["--query-gpu=uuid", "--format=csv,noheader"], host.Arguments);
    }

    [Fact]
    public void AnNvidiaSmiThatFailsIsARefusalNotAnEmptyDeclaration()
    {
        var observed = SiteRegistration.ObserveCards(new ListingHost("", exitCode: 9), nvidiaSmi: "/opt/nvidia-smi");
        Assert.False(observed.IsOk);
        Assert.Contains("did not list the cards", observed.RefusalDetail, StringComparison.Ordinal);
    }
}
