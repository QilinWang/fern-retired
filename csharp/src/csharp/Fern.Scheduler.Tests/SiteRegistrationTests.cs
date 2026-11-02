using System.Text.Json.Nodes;
using System.Text.Json;
using Fern.Registry;
using Fern.Scheduler;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// Registering one Worker Site from its execution profile. Nothing here reaches
/// a machine: reading a profile and writing a row are both local, and that is
/// the point — a Site is registered before anyone tries to reach it.
/// </summary>
public sealed class SiteRegistrationTests
{
    // CHG 2026-09-26 (source-roots-stop-being-full-copies, slice 4): the key the retired
    // remote installer wrote; here it stands for any key another command recorded.
    const string ReceiptCapability = "installation_receipt";

    [Fact]
    public void AnSshProfileRegistersTheMachineItDeclares()
    {
        var profile = WriteProfile("""
            {"schema":"fern.execution_profile.v5","profile_id":"runpod-a4500","executor":"ssh",
             "ssh_destination":"root@198.51.100.7:22160","device":"cuda",
             "source_root":"/workspace/fern-runtime/source",
             "python":"/workspace/fern-runtime/home/.venv/bin/python",
             "worker":{"module":"fern_train.worker","slots":2},
             "admitted_models":["fern","dlinear"]}
            """);
        using var writer = RegistryHome.OpenWriter(TestRegistry.CreateRegistry());

        var declared = SiteRegistration.Read(profile);
        Assert.True(declared.IsOk, declared.RefusalDetail);
        var registered = SiteRegistration.Register(writer, "runpod-a4500", declared.Value);

        Assert.True(registered.TryGetValue(out var row), Refusal(registered));
        Assert.False(row.AlreadyRegistered);
        var (capabilities, capacity) = ReadSite(writer, row.SiteId);
        Assert.Equal("ssh", capabilities.GetProperty("executor").GetString());
        Assert.Equal("root@198.51.100.7:22160", capabilities.GetProperty("ssh_destination").GetString());
        Assert.Equal("/workspace/fern-runtime/source", capabilities.GetProperty("source_root").GetString());
        Assert.Equal("/workspace/fern-runtime/home/.venv/bin/python", capabilities.GetProperty("python").GetString());
        Assert.Equal(
            new[] { "fern", "dlinear" },
            capabilities.GetProperty("admitted_models").EnumerateArray().Select(model => model.GetString()));
        Assert.Equal(2, capacity.GetProperty("slots").GetInt64());
    }

    /// <summary>
    /// A machine registered twice is one machine. The second registration
    /// updates what it declares; a second row would be a second machine to the
    /// scheduler, which would then place two cells where one fits.
    /// </summary>
    [Fact]
    public void RegisteringTheSameNameAgainUpdatesRatherThanDuplicates()
    {
        var first = WriteProfile("""
            {"executor":"local","source_root":"/a","python":"/a/python","worker":{"slots":1}}
            """);
        var second = WriteProfile("""
            {"executor":"local","source_root":"/b","python":"/b/python","worker":{"slots":4}}
            """);
        using var writer = RegistryHome.OpenWriter(TestRegistry.CreateRegistry());

        var before = SiteRegistration.Register(writer, "local-4070", SiteRegistration.Read(first).Value);
        var after = SiteRegistration.Register(writer, "local-4070", SiteRegistration.Read(second).Value);

        Assert.True(before.TryGetValue(out var firstRow), Refusal(before));
        Assert.True(after.TryGetValue(out var secondRow), Refusal(after));
        Assert.Equal(firstRow.SiteId, secondRow.SiteId);
        Assert.True(secondRow.AlreadyRegistered);
        var (capabilities, capacity) = ReadSite(writer, secondRow.SiteId);
        Assert.Equal("/b", capabilities.GetProperty("source_root").GetString());
        Assert.Equal(4, capacity.GetProperty("slots").GetInt64());
        Assert.Equal(1L, Scalar(writer, "SELECT COUNT(*) FROM sites"));
    }

    /// <summary>
    /// Slice S1 of how-a-site-is-reached-and-started. On 2026-09-25 the
    /// Beijing Site was re-registered after its SSH port changed, and the
    /// installation receipt install-study-source had recorded on its row was
    /// gone. Every key another command records on the row survives a
    /// re-registration; the models environment admission added stay admitted.
    /// </summary>
    [Fact]
    public void ReRegisteringKeepsWhatOtherCommandsRecordedOnTheSite()
    {
        var profile = WriteProfile("""
            {"executor":"ssh","ssh_destination":"root@example:27464","source_root":"/a","python":"/a/python",
             "admitted_models":["fern"],"worker":{"slots":1}}
            """);
        var moved = WriteProfile("""
            {"executor":"ssh","ssh_destination":"root@example:50300","source_root":"/a","python":"/a/python",
             "admitted_models":["fern"],"worker":{"slots":1}}
            """);
        using var writer = RegistryHome.OpenWriter(TestRegistry.CreateRegistry());
        Assert.True(SiteRegistration.Register(writer, "bj", SiteRegistration.Read(profile).Value).TryGetValue(out var row));
        writer.WriteImmediate(connection =>
        {
            var capabilities = SiteAdmissions.Capabilities(connection, "bj")!;
            capabilities[ReceiptCapability] = "/root/fern-runtime/receipts/installation.json";
            capabilities[SiteAdmissions.MachineKey] = new JsonObject { ["machine_generation"] = 3 };
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE sites SET capabilities_json = $capabilities WHERE id = $id";
            update.Parameters.AddWithValue("$capabilities", capabilities.ToJsonString());
            update.Parameters.AddWithValue("$id", row.SiteId);
            update.ExecuteNonQuery();
            // The environment admission is written by the writer the admission
            // authority uses, with the models it adds.
            SiteAdmissions.Write(connection, "bj", SiteAdmissions.EnvironmentKey,
                new SiteEnvironmentAdmission("bj", "env-1", "rev-1", "/a/python", "python", ["conditional_nsf"],
                    "installed", "sha256:env", null, "{}", 1, "test"),
                also: stored => stored["admitted_models"] = new JsonArray("fern", "conditional_nsf"));
            return true;
        });

        Assert.True(SiteRegistration.Register(writer, "bj", SiteRegistration.Read(moved).Value).TryGetValue(out var again));

        var (after, _) = ReadSite(writer, row.SiteId);
        Assert.Equal("root@example:50300", after.GetProperty("ssh_destination").GetString());
        Assert.Equal("/root/fern-runtime/receipts/installation.json",
            after.GetProperty(ReceiptCapability).GetString());
        Assert.Equal(3, after.GetProperty(SiteAdmissions.MachineKey).GetProperty("machine_generation").GetInt64());
        Assert.True(after.TryGetProperty(SiteAdmissions.EnvironmentKey, out _));
        Assert.Equal(new[] { "fern", "conditional_nsf" },
            after.GetProperty("admitted_models").EnumerateArray().Select(model => model.GetString()));
        Assert.Equal(
            new[] { ReceiptCapability, SiteAdmissions.MachineKey, SiteAdmissions.EnvironmentKey },
            again.KeptKeys);
    }

    /// <summary>A stored row that is not a JSON object cannot keep what others recorded, so it is not overwritten.</summary>
    [Fact]
    public void AStoredRowThatCannotBeReadIsNotOverwritten()
    {
        var profile = WriteProfile("""
            {"executor":"local","source_root":"/a","python":"/a/python","worker":{"slots":1}}
            """);
        using var writer = RegistryHome.OpenWriter(TestRegistry.CreateRegistry());
        Assert.True(SiteRegistration.Register(writer, "box", SiteRegistration.Read(profile).Value).TryGetValue(out var row));
        using (var damage = writer.Connection.CreateCommand())
        {
            damage.CommandText = "UPDATE sites SET capabilities_json = '[1,2]' WHERE id = $id";
            damage.Parameters.AddWithValue("$id", row.SiteId);
            damage.ExecuteNonQuery();
        }

        var again = SiteRegistration.Register(writer, "box", SiteRegistration.Read(profile).Value);

        Assert.True(again.TryGetRefusal(out var refusal));
        Assert.Contains("not a JSON object; nothing was replaced", refusal.Detail, StringComparison.Ordinal);
        using var stored = writer.Connection.CreateCommand();
        stored.CommandText = "SELECT capabilities_json FROM sites";
        Assert.Equal("[1,2]", stored.ExecuteScalar() as string);
    }

    /// <summary>A key registration owns and the profile stopped declaring is removed, and said to be.</summary>
    [Fact]
    public void AnOwnedKeyTheProfileNoLongerDeclaresIsRemoved()
    {
        var withAddress = WriteProfile("""
            {"executor":"ssh","ssh_destination":"root@example:27464","source_root":"/a","python":"/a/python","worker":{"slots":1}}
            """);
        var local = WriteProfile("""
            {"executor":"local","source_root":"/a","python":"/a/python","worker":{"slots":1}}
            """);
        using var writer = RegistryHome.OpenWriter(TestRegistry.CreateRegistry());
        Assert.True(SiteRegistration.Register(writer, "box", SiteRegistration.Read(withAddress).Value).TryGetValue(out var row));

        Assert.True(SiteRegistration.Register(writer, "box", SiteRegistration.Read(local).Value).TryGetValue(out var again));

        var (after, _) = ReadSite(writer, row.SiteId);
        Assert.False(after.TryGetProperty("ssh_destination", out _));
        Assert.Equal("local", after.GetProperty("executor").GetString());
        Assert.Equal(new[] { "ssh_destination" }, again.RemovedKeys);
    }

    [Fact]
    public void AProfileThatNamesNoExecutorIsRefused()
    {
        var profile = WriteProfile("""
            {"schema":"fern.execution_profile.v5","source_root":"/a","python":"/a/python"}
            """);

        var declared = SiteRegistration.Read(profile);

        Assert.False(declared.IsOk);
        Assert.Contains("names no executor", declared.RefusalDetail, StringComparison.Ordinal);
        Assert.Contains("cannot be placed on", declared.RefusalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnSshProfileWithNoDestinationAndAnUnknownExecutorAreEachRefused()
    {
        var noDestination = SiteRegistration.Read(WriteProfile("""
            {"executor":"ssh","source_root":"/a","python":"/a/python"}
            """));
        var unknownExecutor = SiteRegistration.Read(WriteProfile("""
            {"executor":"kubernetes","source_root":"/a","python":"/a/python"}
            """));

        Assert.Contains("no ssh_destination", noDestination.RefusalDetail, StringComparison.Ordinal);
        Assert.Contains("neither local nor ssh", unknownExecutor.RefusalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void AProfileWithNoSourceRootOrNoInterpreterIsRefusedByField()
    {
        var noSourceRoot = SiteRegistration.Read(WriteProfile("""
            {"executor":"local","python":"/a/python"}
            """));
        var noPython = SiteRegistration.Read(WriteProfile("""
            {"executor":"local","source_root":"/a"}
            """));

        Assert.Contains("names no source_root", noSourceRoot.RefusalDetail, StringComparison.Ordinal);
        Assert.Contains("names no python", noPython.RefusalDetail, StringComparison.Ordinal);
    }

    static string Refusal<T>(RegistryResult<T> result)
    {
        result.TryGetRefusal(out var refusal);
        return refusal.Detail;
    }

    static (JsonElement Capabilities, JsonElement Capacity) ReadSite(RegistryWriter writer, long siteId)
    {
        using var read = writer.Connection.CreateCommand();
        read.CommandText = "SELECT capabilities_json, capacity_json FROM sites WHERE id = $id";
        read.Parameters.AddWithValue("$id", siteId);
        using var row = read.ExecuteReader();
        Assert.True(row.Read());
        return (JsonDocument.Parse(row.GetString(0)).RootElement.Clone(),
                JsonDocument.Parse(row.GetString(1)).RootElement.Clone());
    }

    static long Scalar(RegistryWriter writer, string sql)
    {
        using var command = writer.Connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    static string WriteProfile(string json)
    {
        var path = Path.Combine(
            Directory.CreateTempSubdirectory("fern-site-profile-").FullName, "profile.json");
        File.WriteAllText(path, json);
        return path;
    }
}
