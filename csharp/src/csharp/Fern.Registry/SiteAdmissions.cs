using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Fern.Registry;

/// <summary>
/// What was admitted on a Site, kept on the Site's own row.
///
/// CHG 2026-09-11 night (slice 9 of the cell port): the machine admission
/// (verify-machine-admission: this machine's card, interpreter and storage
/// as observed) and the environment admission (install-model-environment:
/// the model set this interpreter holds) were rows of `machine_admissions`
/// and `site_environment_admissions`, keyed by the old Site registry's id,
/// which was the execution profile's id. On the one-store schema the Site is
/// the `sites` row `register-site` mints from that profile, named by the same
/// id, and the two admissions are two objects in its capabilities, beside
/// what the profile declared. One row says what a machine is and what was
/// admitted on it; the placement reads the same row. A shared store rather
/// than two private ones because both authorities write the same column
/// under the same rule: read, set one key, write back, inside the caller's
/// transaction.
/// </summary>
public static class SiteAdmissions
{
    public const string MachineKey = "machine_admission";
    public const string EnvironmentKey = "environment_admission";

    /// <summary>The records serialize by their fields, snake_case like every other key of the capabilities object.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>The capabilities object of the Site named, or null when no Site carries that name.</summary>
    public static JsonObject? Capabilities(SqliteConnection connection, string siteName)
    {
        if (string.IsNullOrWhiteSpace(siteName))
        {
            return null;
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT capabilities_json FROM sites WHERE name = $name";
        command.Parameters.AddWithValue("$name", siteName);
        var json = command.ExecuteScalar() as string;
        if (json is null)
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    public static bool SiteExists(SqliteConnection connection, string siteName) => Capabilities(connection, siteName) is not null;

    /// <summary>One admission of the Site named, or null when the Site is absent or nothing of that kind was admitted.</summary>
    public static T? Read<T>(SqliteConnection connection, string siteName, string key) where T : class
    {
        var capabilities = Capabilities(connection, siteName);
        if (capabilities is null || capabilities[key] is not JsonObject admission)
        {
            return null;
        }
        try
        {
            return admission.Deserialize<T>(Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Set one admission on the Site named, keeping every other key, and let
    /// the caller adjust the object once more (the environment admission also
    /// widens <c>admitted_models</c>). Runs inside the caller's transaction and
    /// throws when the Site is absent, which the callers refuse before reaching
    /// here.
    /// </summary>
    public static void Write<T>(SqliteConnection connection, string siteName, string key, T admission, Action<JsonObject>? also = null)
    {
        var capabilities = Capabilities(connection, siteName)
            ?? throw new RegistryFault($"Site {siteName} is not registered; register-site mints it from the execution profile");
        capabilities[key] = JsonSerializer.SerializeToNode(admission, Json);
        also?.Invoke(capabilities);
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE sites SET capabilities_json = $capabilities WHERE name = $name";
        update.Parameters.AddWithValue("$capabilities", capabilities.ToJsonString());
        update.Parameters.AddWithValue("$name", siteName);
        update.ExecuteNonQuery();
    }

    /// <summary>Every Site with its capabilities, for a search by an admission's identity.</summary>
    public static IReadOnlyList<(string Name, JsonObject Capabilities)> All(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, capabilities_json FROM sites ORDER BY id";
        using var reader = command.ExecuteReader();
        var sites = new List<(string, JsonObject)>();
        while (reader.Read())
        {
            JsonObject capabilities;
            try
            {
                capabilities = JsonNode.Parse(reader.GetString(1)) as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                capabilities = new JsonObject();
            }
            sites.Add((reader.GetString(0), capabilities));
        }
        return sites;
    }

    /// <summary>The cards a Site declares (register-site --device / --observe-cards), first one first.</summary>
    public static IReadOnlyList<string> DeclaredCards(JsonObject capabilities) =>
        capabilities["devices"] is JsonArray cards
            ? cards.Select(card => card?.GetValue<string>()).Where(card => !string.IsNullOrEmpty(card)).Select(card => card!).ToList()
            : [];
}
