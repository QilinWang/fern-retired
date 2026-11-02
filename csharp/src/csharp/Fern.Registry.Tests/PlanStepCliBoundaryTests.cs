using Xunit;

namespace Fern.Registry.Tests;

public sealed class PlanStepCliBoundaryTests
{
    [Fact]
    public void RegistryChildDoesNotRestoreRetiredRunViewOrInspectFacades()
    {
        var registry = ProductionHome.CreateRegistry();
        foreach (var operation in new[]
                 {
                     "audit-run-registry-data",
                     "summarize-run-storage-data",
                     "list-lifecycle-run-data",
                     "inspect-monitor",
                     "inspect-attempt-explanation",
                 })
        {
            var result = ProductionHome.RunFernRegistry(
                [operation, "--registry", registry],
                "{}",
                10_000);
            Assert.Equal(RegistryCli.ExitFault, result.Exit);
            Assert.Equal(string.Empty, result.Stdout);
            Assert.Contains($"unknown operation {operation}", result.Stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RegistryChildDoesNotExposeSchedulerOwnedPlanMutations()
    {
        var registry = ProductionHome.CreateRegistry();
        foreach (var operation in new[]
                 {
                     "renew-plan", "relinquish-plan", "start-plan-step", "satisfy-plan-step",
                     "claim-scheduler", "renew-scheduler", "release-scheduler",
                     "pause-scheduling", "release-scheduling-pause",
                     "inspect-scheduler-office", "inspect-scheduler-route",
                     "resume-plan", "cancel-plan",
                 })
        {
            var result = ProductionHome.RunFernRegistry(
                [operation, "--registry", registry],
                "{}",
                10_000);
            Assert.Equal(RegistryCli.ExitFault, result.Exit);
            Assert.Equal(string.Empty, result.Stdout);
            Assert.Contains($"unknown operation {operation}", result.Stderr, StringComparison.Ordinal);
        }
    }
}
