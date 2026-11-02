using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

public sealed class InstallationIdentityTests
{
    [Fact]
    public void ControlIdentityCarriesOperationalProtocolOnly()
    {
        var identity = InstallationIdentities.Scheduler();
        Assert.Equal(InstallationIdentityContract.Schema, identity.Schema);
        Assert.Equal(InstallationIdentityContract.SchedulerRole, identity.Role);
        Assert.Equal(InstallationIdentityContract.RunPlanProtocol, identity.RunPlanProtocol);
        var json = JsonReadPolicy.Write(identity);
        Assert.DoesNotContain("recipe_handler", json, StringComparison.Ordinal);
        var parsed = Assert.IsType<InstallationIdentityParse.Accepted>(
            InstallationIdentities.Parse(json, InstallationIdentityContract.SchedulerRole));
        Assert.Equal(identity.Role, parsed.Identity.Role);
        Assert.Equal(
            InstallationIdentityContract.RegistryHomeRole,
            InstallationIdentities.Control(InstallationIdentityContract.RegistryHomeRole).Role);
    }

    [Fact]
    public void ParseRefusesUnknownFieldsAndWrongContracts()
    {
        var extra = Assert.IsType<InstallationIdentityParse.UnknownField>(
            InstallationIdentities.Parse(
                """{"schema":"fern.installation_identity.v4","role":"scheduler","run_plan_protocol":"fern.run_plan_parent_child.v2","recipe_handler_capabilities":{}}""",
                InstallationIdentityContract.SchedulerRole));
        Assert.Equal("recipe_handler_capabilities", extra.FieldName);
        var unknown = InstallationIdentities.Parse(
            """{"schema":"fern.installation_identity.v4","role":"scheduler","run_plan_protocol":"fern.run_plan_parent_child.v2","invented_field":1}""",
            InstallationIdentityContract.SchedulerRole);
        Assert.Equal("invented_field", Assert.IsType<InstallationIdentityParse.UnknownField>(unknown).FieldName);

        var worker = Assert.IsType<InstallationIdentityParse.Refused>(
            InstallationIdentities.Parse(
                """{"schema":"fern.installation_identity.v4","role":"matrix-worker","run_plan_protocol":"fern.run_plan_parent_child.v2"}""",
                InstallationIdentityContract.SchedulerRole));
        Assert.Contains("expected \"scheduler\"", worker.Detail, StringComparison.Ordinal);

        var schema = Assert.IsType<InstallationIdentityParse.Refused>(
            InstallationIdentities.Parse(
                """{"schema":"fern.installation_identity.v3","role":"scheduler","run_plan_protocol":"fern.run_plan_parent_child.v2"}""",
                InstallationIdentityContract.SchedulerRole));
        Assert.Contains("expected \"fern.installation_identity.v4\"", schema.Detail, StringComparison.Ordinal);

        var protocol = Assert.IsType<InstallationIdentityParse.Refused>(
            InstallationIdentities.Parse(
                """{"schema":"fern.installation_identity.v4","role":"scheduler","run_plan_protocol":"future"}""",
                InstallationIdentityContract.SchedulerRole));
        Assert.Contains("expected \"fern.run_plan_parent_child.v2\"", protocol.Detail, StringComparison.Ordinal);

        var two = Assert.IsType<InstallationIdentityParse.Refused>(
            InstallationIdentities.Parse(
                """{"schema":"fern.installation_identity.v4","role":"scheduler","run_plan_protocol":"fern.run_plan_parent_child.v2"}{}""",
                InstallationIdentityContract.SchedulerRole));
        Assert.Contains("one JSON object", two.Detail, StringComparison.Ordinal);
    }
}
