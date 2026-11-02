using System.Text.Json;

namespace Fern.Scheduler;

/// <summary>
/// What a Plan registration takes from an execution profile: the Site's own
/// interpreter and roots, and Registry Home's interpreter, roots and campaign
/// root — which for an SSH Site come from the LOCAL profile it names through
/// registry_home_profile_id, never a path on the remote machine.
///
/// Its own file since 2026-09-04. The same function was written twice, once in
/// ExperimentLaunchPlanning and once in ExperimentLaunchVerbs; the first left
/// with the recipe route (retired/csharp/recipe-route-2026-09-04), and this is
/// the surviving copy, moved verbatim, that run-study and the scheduler's
/// profile readers both call.
/// </summary>
public static class ProfileLaunchPathsReader
{
    public static ProfileLaunchPaths? RequiredProfilePaths(string profileId)
    {
        var worker = ReadProfileElement(profileId);
        if (worker is null)
        {
            return null;
        }
        return LaunchPathsOf(worker.Value, ReadProfileElement);
    }

    public static ProfileLaunchPaths? RequiredProfilePaths(Fern.Scheduler.ExecutionProfileV5 worker)
    {
        var home = worker;
        if (string.Equals(worker.Executor, "ssh", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(worker.RegistryHomeProfileId))
            {
                return null;
            }
            var loaded = Fern.Scheduler.ExecutionProfileV5.Read(
                Fern.Scheduler.WorktreeControlPaths.Profiles(WorktreeControlPaths.RepoRoot()),
                worker.RegistryHomeProfileId,
                expectedRevision: null);
            if (!loaded.IsOk || !string.Equals(loaded.Value.Executor, "local", StringComparison.Ordinal))
            {
                return null;
            }
            home = loaded.Value;
        }

        // CHG: omitted local Home selection follows the current installed context.
        var registryPath = home.RegistryPath ?? Fern.Registry.RegistryHome.DefaultRegistryPath;
        if (string.IsNullOrWhiteSpace(registryPath)
            || !Path.IsPathFullyQualified(registryPath)
            || string.IsNullOrWhiteSpace(home.KnownDataRoot)
            || !Path.IsPathFullyQualified(home.KnownDataRoot))
        {
            return null;
        }

        return new ProfileLaunchPaths(
            worker.Python,
            worker.WorkRoot,
            worker.ArtifactRoot,
            worker.Device,
            home.CampaignRoot,
            registryPath,
            home.Python,
            home.WorkRoot,
            home.ArtifactRoot,
            home.KnownDataRoot);
    }

    public static JsonElement? ReadProfileElement(string profileId)
    {
        var path = Path.Combine(WorktreeControlPaths.Profiles(WorktreeControlPaths.RepoRoot()), profileId + ".json");
        if (!File.Exists(path))
        {
            return null;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }


    public static ProfileLaunchPaths? LaunchPathsOf(JsonElement worker, Func<string, JsonElement?> loadProfile)
    {
        var python = OptionalString(worker, "python");
        var workRoot = OptionalString(worker, "work_root");
        var artifactRoot = OptionalString(worker, "artifact_root");
        var device = OptionalString(worker, "device");
        if (string.IsNullOrWhiteSpace(python)
            || string.IsNullOrWhiteSpace(workRoot)
            || string.IsNullOrWhiteSpace(artifactRoot))
        {
            return null;
        }

        var home = worker;
        if (string.Equals(OptionalString(worker, "executor"), "ssh", StringComparison.Ordinal))
        {
            var homeId = OptionalString(worker, "registry_home_profile_id");
            if (string.IsNullOrWhiteSpace(homeId))
            {
                return null;
            }
            var loaded = loadProfile(homeId);
            if (loaded is null || !string.Equals(OptionalString(loaded.Value, "executor"), "local", StringComparison.Ordinal))
            {
                return null;
            }
            home = loaded.Value;
        }

        var registryPath = OptionalString(home, "registry_path") ?? Fern.Registry.RegistryHome.DefaultRegistryPath;
        var campaignRoot = OptionalString(home, "campaign_root");
        var homePython = OptionalString(home, "python");
        var homeWorkRoot = OptionalString(home, "work_root");
        var homeArtifactRoot = OptionalString(home, "artifact_root");
        var homeKnownDataRoot = OptionalString(home, "known_data_root");
        if (string.IsNullOrWhiteSpace(registryPath)
            || !Path.IsPathFullyQualified(registryPath)
            || string.IsNullOrWhiteSpace(campaignRoot)
            || string.IsNullOrWhiteSpace(homePython)
            || string.IsNullOrWhiteSpace(homeWorkRoot)
            || string.IsNullOrWhiteSpace(homeArtifactRoot)
            || string.IsNullOrWhiteSpace(homeKnownDataRoot)
            || !Path.IsPathFullyQualified(homeKnownDataRoot))
        {
            return null;
        }

        return new ProfileLaunchPaths(
            python,
            workRoot,
            artifactRoot,
            device ?? string.Empty,
            campaignRoot,
            registryPath,
            homePython,
            homeWorkRoot,
            homeArtifactRoot,
            homeKnownDataRoot);
    }

    public static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    /// <summary>
    /// The two machines a launch touches, kept apart by name.
    ///
    /// Python, WorkRoot and ArtifactRoot are the SITE's: they are lowered into
    /// the Stage configs the Site executes, so they must be paths on the
    /// machine that runs the training. HomePython, HomeWorkRoot and
    /// HomeArtifactRoot are Registry Home's, where the launch verb itself runs.
    /// CampaignRoot and HomeKnownDataRoot are Home's too — the local SQLite
    /// authority and the declared recorded-data root used by the DGP-bank
    /// child before anything is handed to a Site.
    ///
    /// CHG: RegistryPath, not CampaignRoot, selects the SQLite authority.
    /// CampaignRoot continues to select historical campaign files.
    /// For a local profile every Home field equals its Site twin, because the
    /// two machines are one machine.
    /// </summary>
    public sealed record ProfileLaunchPaths(
        string Python,
        string WorkRoot,
        string ArtifactRoot,
        string Device,
        string CampaignRoot,
        string RegistryPath,
        string HomePython,
        string HomeWorkRoot,
        string HomeArtifactRoot,
        string HomeKnownDataRoot);
}
