namespace Fern.Scheduler;

/// <summary>
/// Select admissible execution profiles before Study registration. Selection
/// does not claim a GPU: the existing Site queue owns capacity and dispatch.
/// </summary>
public static class StudyExecution
{
    public enum Mode { Profile, Auto, PodOnly, LocalOnly, Hybrid }

    public sealed record Selection(
        IReadOnlyList<string> Profiles,
        IReadOnlyList<ProfileValidationResult> Observations,
        string? Problem);

    /// <summary>
    /// Auto prefers an observable SSH Site; Hybrid preserves every eligible
    /// Site. Profile keeps the explicit --on admission path without probing.
    /// The observation operation is explicit so tests never contact a machine.
    /// Example: Select(root, Mode.Auto, ["runpod", "local-4070"],
    /// ObserverContext.Host, version, Preflight.ValidateProfile).
    /// </summary>
    public static Selection Select(
        string workspaceRoot, Mode mode, IReadOnlyList<string> profileIds,
        ObserverContext observer, string fernVersion,
        Func<ProfileValidationRequest, ProfileValidationResult> observe)
    {
        var observations = new List<ProfileValidationResult>();
        if (!Enum.IsDefined(mode) || !Enum.IsDefined(observer))
            return new([], observations, "unknown execution mode or observer");
        if (profileIds.Count == 0 || profileIds.Any(id => !ExecutionProfileV5.ValidProfileId(id)))
            return new([], observations, "execution selection requires valid profile ids");
        if (profileIds.Distinct(StringComparer.Ordinal).Count() != profileIds.Count)
            return new([], observations, "execution candidates must not repeat a profile");
        if (mode == Mode.Profile)
            return profileIds.Count == 1
                ? new([profileIds[0]], observations, null)
                : new([], observations, "explicit --on without --execution names one profile");

        var profiles = new List<ExecutionProfileV5>();
        foreach (var id in profileIds)
        {
            var path = Path.Combine(WorktreeControlPaths.Profiles(workspaceRoot), id + ".json");
            if (!File.Exists(path))
            {
                observations.Add(Preflight.Blocked(id, path, observer,
                    [new(PreflightProblemCode.MissingPrerequisite, "no saved execution profile", "declare this profile or choose another candidate")]));
                continue;
            }
            var loaded = ExecutionProfileV5.Read(WorktreeControlPaths.Profiles(workspaceRoot), id, null);
            if (!loaded.IsOk)
                return new([], observations, loaded.Failure.Detail);
            if (mode == Mode.PodOnly && loaded.Value.Executor != "ssh"
                || mode == Mode.LocalOnly && loaded.Value.Executor != "local")
                continue;
            if (loaded.Value.Device != "cuda")
                return new([], observations, $"profile {id} declares {loaded.Value.Device}; training execution modes require cuda");
            profiles.Add(loaded.Value);
        }

        var selected = new List<string>();
        // This order is preference, never a polling loop or a busy-GPU test.
        // A ready but occupied Pod stays selected and its Site queues the work.
        foreach (var profile in profiles.OrderBy(profile => profile.Executor == "ssh" ? 0 : 1))
        {
            var observation = observe(new(workspaceRoot, profile.ProfileId, observer, fernVersion));
            observations.Add(observation);
            if (observation.ProfileId != profile.ProfileId || observation.GpuWorkStarted)
                return new([], observations, "execution observation changed its profile or started GPU work");
            if (observation.Status == ProfileValidationStatus.NeedsHostCheck)
                return new([], observations, observation.NextStep);
            if (observation.Status != ProfileValidationStatus.Ready)
            {
                if (observation.ObservationFailure?.Category == Fern.Site.SiteFailureCategory.HostUnreachable)
                    continue;
                return new([], observations, $"execution profile {profile.ProfileId} refused: {string.Join("; ", observation.Problems.Select(problem => problem.Meaning))}");
            }
            if (observation.ProfileRevision != profile.Revision || observation.Device != "cuda")
                return new([], observations, $"profile {profile.ProfileId} changed during execution selection");
            selected.Add(profile.ProfileId);
            if (mode == Mode.Auto) break;
        }
        return selected.Count == 0
            ? new([], observations, "no eligible GPU execution profile is ready; no training started")
            : new(selected, observations, null);
    }
}
