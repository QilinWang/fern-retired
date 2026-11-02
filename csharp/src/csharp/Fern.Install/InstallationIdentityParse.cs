using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fern.Install;

// The installation identity vocabulary, moved here verbatim on 2026-09-11 from
// the retired CycleTypes.cs. What a deployed binary answers when it is asked
// what it is; the parser below is its only reader, so the words and their
// reader are now one file. `run_plan_protocol` keeps the retired Plan word
// because it is a field name a deployed binary already prints: renaming it
// would refuse every binary installed before the rename.

public sealed class InstallationIdentity
{
    [JsonPropertyName("schema")]
    public required string Schema { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("run_plan_protocol")]
    public required string RunPlanProtocol { get; init; }
}

public static class InstallationIdentityContract
{
    public const string Schema = "fern.installation_identity.v4";
    public const string RunPlanProtocol = "fern.run_plan_parent_child.v2";
    public const string SchedulerRole = "scheduler";
    public const string RegistryHomeRole = "registry-home";
    public const string WorkerSiteRole = "worker-site";
}

public abstract record InstallationIdentityParse
{
    public sealed record Accepted(InstallationIdentity Identity) : InstallationIdentityParse;

    public sealed record UnknownField(string FieldName) : InstallationIdentityParse;

    public sealed record Refused(string Detail) : InstallationIdentityParse;
}

/// <summary>
/// Installation identity is caller-supplied probe JSON, so it is strict per
/// Pass 2 addendum
/// (reference:7930679b4dc5ecbdeff33f81d7a6c99cd92024da0d1c57d0bc708e169e34c7b3).
/// </summary>
public static class InstallationIdentities
{
    public static InstallationIdentity Control(string role) =>
        new()
        {
            Schema = InstallationIdentityContract.Schema,
            Role = role,
            RunPlanProtocol = InstallationIdentityContract.RunPlanProtocol,
        };

    public static InstallationIdentity Scheduler() =>
        Control(InstallationIdentityContract.SchedulerRole);

    public static InstallationIdentityParse Parse(string json, string expectedRole)
    {
        if (json.Contains("}{", StringComparison.Ordinal))
        {
            return new InstallationIdentityParse.Refused("installation identity must contain one JSON object");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new InstallationIdentityParse.Refused("installation identity must contain one JSON object");
            }
        }
        catch (JsonException)
        {
            return new InstallationIdentityParse.Refused("installation identity JSON is invalid");
        }

        var read = JsonReadPolicy.ReadCaller<InstallationIdentity>(json);
        if (read is CallerJson<InstallationIdentity>.UnknownField unknown)
        {
            return new InstallationIdentityParse.UnknownField(unknown.FieldName);
        }

        if (read is not CallerJson<InstallationIdentity>.Accepted accepted)
        {
            return new InstallationIdentityParse.Refused("installation identity JSON is invalid");
        }

        var identity = accepted.Value;
        if (identity.Schema != InstallationIdentityContract.Schema)
        {
            return new InstallationIdentityParse.Refused(
                $"installation identity schema is \"{identity.Schema}\", expected \"{InstallationIdentityContract.Schema}\"");
        }

        if (identity.Role != expectedRole)
        {
            return new InstallationIdentityParse.Refused(
                $"installation identity role is \"{identity.Role}\", expected \"{expectedRole}\"");
        }

        if (identity.RunPlanProtocol != InstallationIdentityContract.RunPlanProtocol)
        {
            return new InstallationIdentityParse.Refused(
                $"installation identity protocol is \"{identity.RunPlanProtocol}\", expected \"{InstallationIdentityContract.RunPlanProtocol}\"");
        }

        return new InstallationIdentityParse.Accepted(identity);
    }
}
