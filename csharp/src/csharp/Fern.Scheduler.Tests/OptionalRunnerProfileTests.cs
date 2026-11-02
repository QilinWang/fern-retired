using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Retirement proof for the pre-deployed fern-worker profile route. Whole-Cell
/// execution profiles name the Python interpreter and Site transport inputs;
/// a fern-worker path or digest is no longer an accepted optional extension.
/// </summary>
public sealed class OptionalRunnerProfileTests
{
    [Theory]
    [InlineData("worker_binary", "/remote/fern/bin/fern-worker.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("worker_binary_sha256", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void RetiredWorkerFieldsAreRejected(string field, string value)
    {
        var root = Directory.CreateTempSubdirectory("fern-retired-worker-profile-").FullName;
        Directory.CreateDirectory(WorktreeControlPaths.Profiles(root));
        var declaration = Declaration("whole-cell-site");
        declaration[field] = value;
        File.WriteAllText(
            Path.Combine(WorktreeControlPaths.Profiles(root), "whole-cell-site.json"),
            declaration.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

        var read = ExecutionProfileV5.Read(
            WorktreeControlPaths.Profiles(root),
            "whole-cell-site",
            expectedRevision: null);

        Assert.False(read.IsOk);
        Assert.Contains($"unknown or duplicate property {field}", read.Failure.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 2): a profile
    /// that says where its cells take their code from is read, not refused as
    /// declaring an unknown property.
    /// </summary>
    [Fact]
    public void AProfileDeclaringWhereItsCodeComesFromIsRead()
    {
        var directory = Directory.CreateTempSubdirectory("fern-code-from-profile-").FullName;
        var declaration = Declaration("local-home");
        declaration["executor"] = "local";
        declaration.Remove("ssh_destination");
        declaration.Remove("registry_home_profile_id");
        declaration["code_from"] = "commit";
        declaration["repository"] = "/home/user/study";
        File.WriteAllText(Path.Combine(directory, "local-home.json"), declaration.ToJsonString());
        var read = ExecutionProfileV5.Read(directory, "local-home", null);
        Assert.True(read.IsOk, read.IsOk ? "" : read.Failure.Detail);
    }

    [Fact]
    public void ExplicitRegistryPathIsValidatedAndBoundToTheProfileRevision()
    {
        var directory = Directory.CreateTempSubdirectory("fern-registry-path-profile-").FullName;
        try
        {
            var declaration = Declaration("local-home");
            declaration["executor"] = "local";
            declaration.Remove("ssh_destination");
            declaration.Remove("registry_home_profile_id");
            var file = Path.Combine(directory, "local-home.json");
            File.WriteAllText(file, declaration.ToJsonString());
            var historical = ExecutionProfileV5.Read(directory, "local-home", null);
            Assert.True(historical.IsOk, historical.IsOk ? "" : historical.Failure.Detail);
            Assert.Null(historical.Value.RegistryPath);
            Assert.Equal(historical.Value.Revision,
                Fern.Experiment.ExecutionProfileIdentity.RevisionHash(declaration.ToJsonString()).Value);
            declaration["registry_path"] = "/independent/state/registry.db";
            File.WriteAllText(file, declaration.ToJsonString());
            var first = ExecutionProfileV5.Read(directory, "local-home", null);
            Assert.True(first.IsOk, first.IsOk ? "" : first.Failure.Detail);
            Assert.Equal("/independent/state/registry.db", first.Value.RegistryPath);
            Assert.Equal(first.Value.Revision,
                Fern.Experiment.ExecutionProfileIdentity.RevisionHash(declaration.ToJsonString()).Value);

            declaration["registry_path"] = "/another/registry.db";
            File.WriteAllText(file, declaration.ToJsonString());
            var changed = ExecutionProfileV5.Read(directory, "local-home", first.Value.Revision);
            Assert.False(changed.IsOk);
            Assert.Contains("changed after admission", changed.Failure.Detail);

            declaration["registry_path"] = "relative/registry.db";
            File.WriteAllText(file, declaration.ToJsonString());
            var invalid = ExecutionProfileV5.Read(directory, "local-home", null);
            Assert.False(invalid.IsOk);
            Assert.Contains("registry_path", invalid.Failure.Detail);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void AdmittedModelsAreAcceptedWithoutChangingExecutionIdentity()
    {
        var directory = Directory.CreateTempSubdirectory("fern-model-admission-profile-").FullName;
        try
        {
            var declaration = Declaration("whole-cell-site");
            declaration["admitted_models"] = new JsonArray("visual_dynamics", "fern");
            var file = Path.Combine(directory, "whole-cell-site.json");
            File.WriteAllText(file, declaration.ToJsonString());

            var first = ExecutionProfileV5.Read(directory, "whole-cell-site", null);
            Assert.True(first.IsOk, first.IsOk ? "" : first.Failure.Detail);
            Assert.Equal(Fern.Experiment.ExecutionProfileIdentity.RevisionHash(declaration.ToJsonString()).Value,
                first.Value.Revision);
            var registered = SiteRegistration.Read(file);
            Assert.True(registered.IsOk, registered.IsOk ? "" : registered.Failure.Detail);
            Assert.Equal(new[] { "visual_dynamics", "fern" }, registered.Value.AdmittedModels);

            declaration["admitted_models"] = new JsonArray("visual_dynamics");
            File.WriteAllText(file, declaration.ToJsonString());
            var changed = ExecutionProfileV5.Read(directory, "whole-cell-site", first.Value.Revision);
            Assert.True(changed.IsOk, changed.IsOk ? "" : changed.Failure.Detail);
            Assert.Equal(first.Value.Revision, changed.Value.Revision);
            Assert.Equal(new[] { "visual_dynamics" }, SiteRegistration.Read(file).Value.AdmittedModels);

            declaration["admitted_models"] = new JsonArray();
            File.WriteAllText(file, declaration.ToJsonString());
            var none = ExecutionProfileV5.Read(directory, "whole-cell-site", null);
            Assert.True(none.IsOk, none.IsOk ? "" : none.Failure.Detail);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"fern\"")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[1]")]
    [InlineData("[\" \" ]")]
    [InlineData("[\"fern\",\"fern\"]")]
    public void InvalidAdmittedModelListsAreRejected(string models)
    {
        var directory = Directory.CreateTempSubdirectory("fern-model-admission-profile-").FullName;
        try
        {
            var declaration = Declaration("whole-cell-site");
            declaration["admitted_models"] = JsonNode.Parse(models);
            File.WriteAllText(Path.Combine(directory, "whole-cell-site.json"), declaration.ToJsonString());
            var read = ExecutionProfileV5.Read(directory, "whole-cell-site", null);
            Assert.False(read.IsOk);
            Assert.Contains("admitted_models must be an array of distinct non-empty strings", read.Failure.Detail);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    static JsonObject Declaration(string profileId) => new()
    {
        ["schema"] = "fern.execution_profile.v5",
        ["profile_id"] = profileId,
        ["executor"] = "ssh",
        ["ssh_destination"] = "root@example.invalid:22",
        ["registry_home_profile_id"] = "local-4070",
        ["device"] = "cuda",
        ["source_root"] = "/workspace/fern-runtime/source",
        ["python"] = "/workspace/fern-runtime/home/.venv/bin/python",
        ["worker"] = new JsonObject { ["module"] = "fern_train.worker", ["slots"] = 1 },
        ["supervisor"] = new JsonObject
        {
            ["max_restarts"] = 3,
            ["restart_delay_seconds"] = 5,
            ["max_target_seconds"] = 3600,
            ["max_sweep_seconds"] = 86400,
        },
        ["accelerator"] = new JsonObject { ["min_memory_gib"] = 16 },
        ["campaign_root"] = "/workspace/fern-runtime/campaign",
        ["artifact_root"] = "/workspace/fern-runtime/artifacts",
        ["work_root"] = "/workspace/fern-runtime/work",
        ["warn_free_gib"] = 50,
        ["warn_free_percent"] = 10,
    };
}
