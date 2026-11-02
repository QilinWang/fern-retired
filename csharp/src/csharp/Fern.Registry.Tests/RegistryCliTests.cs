using Fern.Registry;
using Xunit;

namespace Fern.Registry.Tests;

public sealed class RegistryCliTests
{
    [Fact]
    public void VersionReportsEmbeddedIdentityWithoutRegistryOrRuntimeGit()
    {
        var result = ProductionHome.RunFernRegistryIn(Path.GetTempPath(), ["version", "--json"], "", 10_000,
            new Dictionary<string, string> { ["FERN_SOURCE_ARCHIVE_SHA256"] = "forged-runtime-archive" });
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Stderr);
        using var json = System.Text.Json.JsonDocument.Parse(result.Stdout);
        var report = json.RootElement;
        Assert.Equal("fern.registry_binary_version.v1", report.GetProperty("schema").GetString());
        Assert.Equal("registry-home", report.GetProperty("role").GetString());
        Assert.Matches("^[a-f0-9]{40}$", report.GetProperty("built_from_commit").GetString()!);
        Assert.True(report.GetProperty("built_from_dirty_tree").ValueKind is
            System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False);
        Assert.DoesNotContain("forged-runtime-archive", result.Stdout);
    }

    [Theory]
    [InlineData("project-run-plan")]
    [InlineData("load-registered-stage-plan")]
    [InlineData("load-stage-plan-instance-input")]
    [InlineData("load-checked-producer-output-source")]
    [InlineData("load-run-plan-definition")]
    [InlineData("project-stage-plan-nodes")]
    [InlineData("active-run-plan-ids-for-profile")]
    [InlineData("next-ready-stage")]
    [InlineData("unspawned-authorization-candidates")]
    [InlineData("require-current-run-plan-permit")]
    [InlineData("run-plan-attempt-binary-version")]
    [InlineData("plan-is-operator-parked")]
    [InlineData("stage-plan-is-complete")]
    [InlineData("stage-plan-has-blocked-instance")]
    [InlineData("registered-stage-plan-digest")]
    [InlineData("training-worker-request-digest")]
    public void RetiredRunPlanAdaptersRefuseBeforeOpeningRegistry(string operation)
    {
        var missing = Path.Combine(Path.GetTempPath(), "fern-retired-plan-adapter-" + Guid.NewGuid(), "registry.db");
        var result = ProductionHome.RunFernRegistry(
            [operation, "--registry", missing], "not-json", 10_000);
        Assert.Equal(RegistryCli.ExitFault, result.Exit);
        Assert.Empty(result.Stdout);
        Assert.Contains("unknown operation " + operation, result.Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
    }

    [Theory]
    [InlineData("list-knowledge-events")]
    [InlineData("record-knowledge-projection")]
    public void RetiredCardProjectionCommandsRefuseBeforeOpeningRegistry(string operation)
    {
        var missing = Path.Combine(Path.GetTempPath(), "fern-retired-command-" + Guid.NewGuid(), "registry.db");
        var result = ProductionHome.RunFernRegistry(
            [operation, "--registry", missing], "{}", 10_000);
        Assert.Equal(RegistryCli.ExitFault, result.Exit);
        Assert.Equal("", result.Stdout);
        Assert.Contains("unknown operation " + operation, result.Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
    }

    [Theory]
    [InlineData("grid-gaps")]
    [InlineData("export-accepted-matrix")]
    public void CentralRegistryReportsRequireAnExplicitDatabaseBeforeAnyEffect(string operation)
    {
        var result = ProductionHome.RunFernRegistry([operation], "", 10_000);
        Assert.Equal(RegistryCli.ExitFault, result.Exit);
        Assert.Equal("", result.Stdout);
        Assert.Contains("requires --registry", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void registry_probe_readiness_crosses_the_public_process_and_opens_sqlite()
    {
        var path = ProductionHome.CreateRegistry();

        var result = ProductionHome.RunFernRegistry(
            ["registry-probe-readiness", "--registry", path], "", 10_000);

        Assert.Equal(RegistryCli.ExitOk, result.Exit);
        Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        Assert.Equal(
            "{\"schema\":\"fern.registry_readiness.v1\",\"status\":\"readable\"}",
            result.Stdout.Trim());
    }

    [Fact]
    public void RetiredResourceSlotDeclarationRefusesBeforeOpeningRegistry()
    {
        // CHG 2026-09-28: declaration-specific strict JSON tests are preserved
        // verbatim in retired/csharp/resource-slot-declaration-2026-09-28/RegistryCliTests.cs.
        var absent = Path.Combine(Path.GetTempPath(), "fern-retired-resource-slot-" + Guid.NewGuid(), "registry.db");
        var result = ProductionHome.RunFernRegistry(
            ["declare-resource-slot", "--registry", absent], "not-json", 10_000);
        Assert.Equal(RegistryCli.ExitFault, result.Exit);
        Assert.Equal("", result.Stdout);
        Assert.Equal("fern-registry: unknown operation declare-resource-slot" + Environment.NewLine, result.Stderr);
        Assert.False(File.Exists(absent));
        Assert.False(File.Exists(absent + ".access"));
        Assert.False(Directory.Exists(Path.GetDirectoryName(absent)));
    }

    [Fact]
    public void missing_registry_is_a_fault_not_a_receipt()
    {
        var missing = Invoke(["registry-probe-readiness"], "");
        Assert.Equal(RegistryCli.ExitFault, missing.Exit);
        Assert.Equal("", missing.Stdout);
        Assert.Contains("--registry", missing.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void subprocess_exit_status_is_meaningful()
    {
        var missingRegistry = ProductionHome.RunFernRegistry(["registry-probe-readiness"], "", 10_000);
        Assert.Equal(RegistryCli.ExitFault, missingRegistry.Exit);
        Assert.True(string.IsNullOrWhiteSpace(missingRegistry.Stdout), missingRegistry.Stdout);
        Assert.Contains("requires --registry", missingRegistry.Stderr, StringComparison.Ordinal);
    }

    static (int Exit, string Stdout, string Stderr) Invoke(string[] args, string stdin)
    {
        using var input = new StringReader(stdin);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = RegistryCli.Run(args, input, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }
}
