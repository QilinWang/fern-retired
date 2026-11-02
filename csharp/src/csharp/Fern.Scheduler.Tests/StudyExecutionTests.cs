using System.Text.Json.Nodes;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>Machine selection tests inject observations; no CUDA, SSH, or training starts.</summary>
public sealed class StudyExecutionTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("fern-study-selection-").FullName;
    readonly List<string> observed = [];

    public StudyExecutionTests()
    {
        Directory.CreateDirectory(WorktreeControlPaths.Profiles(root));
        foreach (var pair in new[] { ("remote", "runpod"), ("desktop", "local-4070") })
        {
            var profile = JsonNode.Parse(File.ReadAllText(Path.Combine(WorktreeControlPaths.Profiles(TestRegistry.RepoRoot()), pair.Item2 + ".json")))!.AsObject();
            profile["profile_id"] = pair.Item1;
            foreach (var field in new[] { "source_root", "python", "campaign_root", "artifact_root", "work_root", "known_data_root" })
                profile[field] = $"//fixture/{pair.Item1}/{field}";
            if (profile.ContainsKey("registry_path"))
                profile["registry_path"] = Path.Combine(root, "lifecycle.db");
            File.WriteAllText(Path.Combine(WorktreeControlPaths.Profiles(root), pair.Item1 + ".json"), profile.ToJsonString());
        }
    }

    ProfileValidationResult Observe(ProfileValidationRequest request, bool remoteReady)
    {
        observed.Add(request.ProfileId);
        var profile = ExecutionProfileV5.Read(WorktreeControlPaths.Profiles(root), request.ProfileId, null).Value;
        var report = Preflight.ForProfile(request.ProfileId, request.ProfileId + ".json", profile, request.Observer);
        return request.ProfileId == "remote" && !remoteReady
            ? Preflight.Close(report with { Problems = [new(PreflightProblemCode.MissingPrerequisite, "SSH unavailable; remote state unknown", "repair endpoint")], ObservationFailure = Fern.Site.SiteFailure.HostUnreachable("connection refused") })
            : report;
    }

    [Fact]
    public void AutoUsesPodWithoutObservingUnavailableLocalHost()
    {
        var result = StudyExecution.Select(root, StudyExecution.Mode.Auto, ["desktop", "remote"], ObserverContext.Host, "test",
            request => Observe(request, true));
        Assert.Null(result.Problem);
        Assert.Equal(["remote"], result.Profiles);
        Assert.Equal(["remote"], observed);
    }

    [Fact]
    public void AutoFallsBackOnceAndRetainsTheRemoteRefusal()
    {
        var result = StudyExecution.Select(root, StudyExecution.Mode.Auto, ["remote", "desktop"], ObserverContext.Host, "test",
            request => Observe(request, false));
        Assert.Null(result.Problem);
        Assert.Equal(["desktop"], result.Profiles);
        Assert.Equal(["remote", "desktop"], observed);
        Assert.Contains("remote state unknown", result.Observations[0].Problems[0].Meaning);
    }

    [Fact]
    public void PodOnlyCannotFallbackToDesktop()
    {
        var result = StudyExecution.Select(root, StudyExecution.Mode.PodOnly, ["remote", "desktop"], ObserverContext.Host, "test",
            request => Observe(request, false));
        Assert.NotNull(result.Problem);
        Assert.Empty(result.Profiles);
        Assert.Equal(["remote"], observed);
    }

    [Fact]
    public void LocalOnlyDoesNotProbeRemoteAndHybridKeepsBothCandidates()
    {
        var local = StudyExecution.Select(root, StudyExecution.Mode.LocalOnly, ["remote", "desktop"], ObserverContext.Host, "test",
            request => Observe(request, true));
        Assert.Equal(["desktop"], local.Profiles);
        Assert.Equal(["desktop"], observed);
        observed.Clear();
        var hybrid = StudyExecution.Select(root, StudyExecution.Mode.Hybrid, ["remote", "desktop"], ObserverContext.Host, "test",
            request => Observe(request, true));
        Assert.Null(hybrid.Problem);
        Assert.Equal(["remote", "desktop"], hybrid.Profiles);
    }

    [Fact]
    public void SandboxUncertaintyDoesNotBecomeAHostFailure()
    {
        var result = StudyExecution.Select(root, StudyExecution.Mode.Auto, ["remote", "desktop"], ObserverContext.CodexSandbox, "test",
            request => Preflight.Close(Observe(request, true) with { Problems = [new(PreflightProblemCode.HostCheckRequired, "needs host check", "repeat outside sandbox")] }));
        Assert.NotNull(result.Problem);
        Assert.Empty(result.Profiles);
        Assert.Equal(["remote"], observed);
    }

    [Fact]
    public void DuplicateCandidatesRefuseBeforeObservation()
    {
        var result = StudyExecution.Select(root, StudyExecution.Mode.Hybrid, ["remote", "remote"], ObserverContext.Host, "test",
            request => Observe(request, true));
        Assert.NotNull(result.Problem);
        Assert.Empty(observed);
    }

    [Theory]
    [InlineData(PreflightProblemCode.InvalidProfile)]
    [InlineData(PreflightProblemCode.ProfileDrift)]
    [InlineData(PreflightProblemCode.MissingPrerequisite)]
    public void UnclassifiedOrConfigurationRefusalsDoNotTriggerFallback(PreflightProblemCode code)
    {
        var result = StudyExecution.Select(root, StudyExecution.Mode.Auto, ["remote", "desktop"], ObserverContext.Host, "test",
            request => Preflight.Close(Observe(request, true) with { Problems = [new(code, "requires repair", "repair")] }));
        Assert.NotNull(result.Problem);
        Assert.Empty(result.Profiles);
        Assert.Equal(["remote"], observed);
    }

    [Fact]
    public void ExplicitProfileAdmissionDoesNotObserveMachines()
    {
        var result = StudyExecution.Select(root, StudyExecution.Mode.Profile, ["remote"], ObserverContext.Host, "test",
            _ => throw new InvalidOperationException("must not probe"));
        Assert.Equal(["remote"], result.Profiles);
    }

    [Fact]
    public void RemoteStorageUsesSiteBytesAndDoesNotInspectTheSamePathLocally()
    {
        var report = Preflight.Blocked("remote", "remote.json", ObserverContext.Host, []) with { Status = ProfileValidationStatus.Ready };
        var checkedReport = Preflight.Close(Preflight.WithRemoteStorage(report,
            [new("/not/on/this/host", "/workspace", "pod-volume", 20UL << 30, 40UL << 30)]));
        Assert.Equal(ProfileValidationStatus.Ready, checkedReport.Status);
        Assert.Equal(20, checkedReport.Storage.Single().AvailableGib);
        var full = Preflight.Close(Preflight.WithRemoteStorage(report,
            [new("/workspace", "/workspace", "pod-volume", 1UL << 30, 40UL << 30)]));
        Assert.Equal(ProfileValidationStatus.Blocked, full.Status);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
