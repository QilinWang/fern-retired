using Xunit;

namespace Fern.Registry.Tests;

public sealed class SiteEnvironmentAdmissionTests
{
    [Fact]
    public void RecordWritesTheCurrentAdmissionForTheSite()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = Open(path);
        var machine = AdmitMachine(writer, "runpod-a4500");
        var recorded = SiteEnvironmentAdmissionAuthority.Record(
            writer,
            Write(machine, "sha256:" + new string('a', 64)));
        Assert.True(recorded.TryGetValue(out var created), Detail(recorded));
        Assert.Equal("admitted", created.Status);
        Assert.False(string.IsNullOrWhiteSpace(created.TransitionReceiptId));

        var current = SiteEnvironmentAdmissionAuthority.Current(writer.Connection, "runpod-a4500");
        Assert.NotNull(current);
        Assert.Equal(created.EnvironmentIdentity, current.EnvironmentIdentity);
        Assert.Equal(["chronos2"], current.Models);
        Assert.Equal("GPU-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", current.DeviceId);
        Assert.Equal(machine.MachineAdmissionIdentity, current.MachineAdmissionIdentity);

        var replayed = SiteEnvironmentAdmissionAuthority.Record(
            writer,
            Write(machine, created.EnvironmentIdentity));
        Assert.True(replayed.TryGetValue(out var same), Detail(replayed));
        Assert.Equal("replayed", same.Status);
        Assert.Null(same.TransitionReceiptId);
    }

    [Fact]
    public void AChangedIdentityReplacesTheCurrentAdmission()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = Open(path);
        var machine = AdmitMachine(writer, "runpod-a4500");
        Assert.True(SiteEnvironmentAdmissionAuthority.Record(
            writer, Write(machine, "sha256:" + new string('a', 64))).IsOk);
        var replaced = SiteEnvironmentAdmissionAuthority.Record(
            writer, Write(machine, "sha256:" + new string('b', 64), models: ["chronos2", "chronos_bolt_base"]));
        Assert.True(replaced.TryGetValue(out var next), Detail(replaced));
        Assert.Equal("replaced", next.Status);

        var current = SiteEnvironmentAdmissionAuthority.Current(writer.Connection, "runpod-a4500");
        Assert.NotNull(current);
        Assert.Equal("sha256:" + new string('b', 64), current.EnvironmentIdentity);
        Assert.Equal(["chronos2", "chronos_bolt_base"], current.Models);
        Assert.Null(SiteEnvironmentAdmissionAuthority.Current(writer.Connection, "runpod-a4500-zeus"));
    }

    [Fact]
    public void RecordRefusesWithoutCurrentMachineAdmission()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = Open(path);

        var refused = SiteEnvironmentAdmissionAuthority.Record(
            writer,
            new SiteEnvironmentAdmissionWrite(
                "runpod-a4500",
                "sha256:" + new string('a', 64),
                "sha256:" + new string('1', 64),
                "/workspace/fern-runtime/home/.venv/bin/python",
                "DefaultChronos",
                ["chronos2"],
                "already_ready",
                "sha256:" + new string('d', 64),
                "GPU-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                """{"schema":"fern.model_environment_installation.v1","machine_admission_identity":"sha256:missing"}""",
                "tester",
                "machine:a",
                null,
                null,
                null,
                null,
                "sha256:missing",
                1));

        Assert.True(refused.TryGetRefusal(out var refusal), Detail(refused));
        Assert.Contains("no current MachineAdmission", refusal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordRefusesAStaleMachineAdmissionParent()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = Open(path);
        var first = AdmitMachine(writer, "runpod-a4500", deviceId: "GPU-old", generation: "machine:old");
        var replacement = AdmitMachine(writer, "runpod-a4500", deviceId: "GPU-new", generation: "machine:new");

        var refused = SiteEnvironmentAdmissionAuthority.Record(
            writer,
            Write(first, "sha256:" + new string('a', 64)));

        Assert.NotEqual(first.MachineAdmissionIdentity, replacement.MachineAdmissionIdentity);
        Assert.True(refused.TryGetRefusal(out var refusal), Detail(refused));
        Assert.Contains("is not the current MachineAdmission", refusal.Detail, StringComparison.Ordinal);
    }

    static SiteEnvironmentAdmissionWrite Write(
        MachineAdmission machine,
        string identity,
        IReadOnlyList<string>? models = null) =>
        new(
            machine.SiteId,
            identity,
            "sha256:" + new string('1', 64),
            machine.PythonExecutable,
            "DefaultChronos",
            models ?? ["chronos2"],
            "already_ready",
            "sha256:" + new string('d', 64),
            machine.DeviceId,
            Receipt(machine, identity, "already_ready"),
            "tester",
            machine.MachineGeneration,
            machine.EndpointSessionId,
            null,
            null,
            null,
            machine.MachineAdmissionIdentity,
            machine.RecordedAtMs);

    static string Detail<T>(RegistryResult<T> result) =>
        result.TryGetRefusal(out var refusal) ? refusal.Render() : "operation returned no result";

    [Fact]
    public void EffectEvidenceAndTheRepairedOutcomeRoundTrip()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = Open(path);
        var machine = AdmitMachine(writer, "site-effect", python: "/venv/bin/python", deviceId: "GPU-effect");
        var recorded = SiteEnvironmentAdmissionAuthority.Record(writer, new SiteEnvironmentAdmissionWrite(
            "site-effect", "sha256:env-e", "rev-1", "/venv/bin/python", "DefaultChronos",
            ["chronos_bolt_small"], "repaired_environment", "sha256:digest", "GPU-effect",
            Receipt(machine, "sha256:env-e", "repaired_environment"), "test", machine.MachineGeneration, machine.EndpointSessionId,
            "environment-install:abc", """{"ready":false,"last_phase":"checkpoint_local_check"}""",
            """{"ready":true,"package_count":4}""",
            machine.MachineAdmissionIdentity, machine.RecordedAtMs));
        Assert.True(recorded.IsOk);

        var current = SiteEnvironmentAdmissionAuthority.Current(writer.Connection, "site-effect");
        Assert.NotNull(current);
        Assert.Equal("repaired_environment", current!.Outcome);
        Assert.Equal("environment-install:abc", current.InstallAttemptId);
        Assert.Contains("checkpoint_local_check", current.BeforeEffectJson);
        Assert.Contains("package_count", current.AfterEffectJson);
    }

    static SiteEnvironmentAdmissionWrite ContentWrite(
        MachineAdmission machine,
        string outcome,
        string receipt,
        string? installAttempt) => new(
        "site-content", "sha256:env-content", "rev-1", "/venv/bin/python", "DefaultChronos",
        ["chronos_bolt_small"], outcome, "sha256:digest", machine.DeviceId,
        receipt, "test", machine.MachineGeneration, machine.EndpointSessionId, installAttempt, null, null,
        machine.MachineAdmissionIdentity, machine.RecordedAtMs);

    /// <summary>
    /// The repaired-receipt regression the reader caught: an asset repair
    /// keeps the environment identity (identity excludes outcome and the
    /// manifest by design), so classification must be by receipt content or
    /// the repair evidence is silently dropped from the durable row.
    /// </summary>
    [Fact]
    public void SameIdentityWithNewProofContentUpdatesTheDurableRow()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = Open(path);
        var machine = AdmitMachine(writer, "site-content", python: "/venv/bin/python", deviceId: "GPU-content");
        Assert.True(SiteEnvironmentAdmissionAuthority.Record(writer, ContentWrite(machine,
            "already_ready",
            Receipt(machine, "sha256:env-content", "already_ready", ""","packages":{"torch":"2.9.1"}"""),
            null)).IsOk);

        var second = SiteEnvironmentAdmissionAuthority.Record(writer, ContentWrite(machine,
            "repaired_environment",
            Receipt(machine, "sha256:env-content", "repaired_environment", ""","packages":{"torch":"2.9.1"},"asset_manifest":{"a@b":[]}"""),
            "environment-install:xyz"));
        Assert.True(second.TryGetValue(out var record));
        Assert.Equal("updated", record.Status);

        var current = SiteEnvironmentAdmissionAuthority.Current(writer.Connection, "site-content");
        Assert.Equal("repaired_environment", current!.Outcome);
        Assert.Equal("environment-install:xyz", current.InstallAttemptId);
    }

    [Fact]
    public void SameIdentityAndSameProofContentReplaysEvenAcrossAttempts()
    {
        var path = ProductionHome.CreateRegistry();
        using var writer = Open(path);
        var machine = AdmitMachine(writer, "site-content", python: "/venv/bin/python", deviceId: "GPU-content");
        Assert.True(SiteEnvironmentAdmissionAuthority.Record(writer, ContentWrite(machine,
            "already_ready",
            Receipt(machine, "sha256:env-content", "already_ready", ""","install_lease_id":"L1","packages":{"torch":"2.9.1"}"""),
            null)).IsOk);

        var replay = SiteEnvironmentAdmissionAuthority.Record(writer, ContentWrite(machine,
            "already_ready",
            Receipt(machine, "sha256:env-content", "already_ready", ""","install_lease_id":"L2","packages":{"torch":"2.9.1"}"""),
            null));
        Assert.True(replay.TryGetValue(out var record));
        Assert.Equal("replayed", record.Status);
    }

    static MachineAdmission AdmitMachine(
        RegistryWriter writer,
        string siteId,
        string python = "/workspace/fern-runtime/home/.venv/bin/python",
        string deviceId = "GPU-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        string generation = "machine:a")
    {
        var recorded = MachineAdmissionAuthority.Record(
            writer,
            MachineAdmissionTests.Write(
                siteId: siteId,
                endpoint: "endpoint:a",
                deviceId: deviceId,
                generation: generation,
                pythonExecutable: python));
        Assert.True(recorded.TryGetValue(out var outcome), Detail(recorded));
        return outcome.Admission;
    }

    static string Receipt(
        MachineAdmission machine,
        string environmentIdentity,
        string outcome,
        string extra = "") =>
        $$"""
        {"schema":"fern.model_environment_installation.v1","outcome":"{{outcome}}","site_id":"{{machine.SiteId}}","environment_identity":"{{environmentIdentity}}","machine_generation":"{{machine.MachineGeneration}}","device_id":"{{machine.DeviceId}}","machine_admission_identity":"{{machine.MachineAdmissionIdentity}}"{{extra}}}
        """;

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
