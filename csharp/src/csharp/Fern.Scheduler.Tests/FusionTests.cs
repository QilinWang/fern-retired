using Xunit;

namespace Fern.Scheduler.Tests;

public sealed class FusionTests
{
    [Fact]
    public void OneProcessHoldsTheRegistryWriterAndTheScheduler()
    {
        var root = TestRegistry.RepoRoot();
        var schedulerProject = File.ReadAllText(
            Path.Combine(root, "src", "csharp", "Fern.Scheduler", "Fern.Scheduler.csproj"));
        Assert.Contains("Fern.Registry.csproj", schedulerProject, StringComparison.Ordinal);

        var operatorProject = File.ReadAllText(
            Path.Combine(root, "src", "csharp", "Fern.Run.Cli", "Fern.Run.Cli.csproj"));
        Assert.Contains("Fern.Scheduler.csproj", operatorProject, StringComparison.Ordinal);
        Assert.Contains("<OutputType>Exe</OutputType>", operatorProject, StringComparison.Ordinal);

        var orchestrator = File.ReadAllText(
            Path.Combine(root, "src", "csharp", "Fern.Scheduler", "StudyOrchestrator.cs"));
        // CHG 2026-09-11 night (slice 5): the orchestrator holds the store
        // through IStudyStore, which on the Home's machine is the writer itself
        // (SqliteStudyStore) and on another machine the wire to it; the claim
        // is the store's. One process still holds both halves.
        Assert.Contains("IStudyStore", orchestrator, StringComparison.Ordinal);
        Assert.Contains("store.TryStartAttempt", orchestrator, StringComparison.Ordinal);
        Assert.DoesNotContain("fern-home", orchestrator, StringComparison.Ordinal);
        Assert.DoesNotContain("fern-registry", orchestrator, StringComparison.Ordinal);
        // CHG 2026-09-11: this read `SchedulerServe.cs` until the resident
        // scheduler retired with the Plan layer. What this test is FOR -- one
        // process holding both the Registry writer and the thing that decides
        // what runs -- is unchanged, and the file that holds both is now the
        // orchestrator `run-study-cells` drives.
    }
}
