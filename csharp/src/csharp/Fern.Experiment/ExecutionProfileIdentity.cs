using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fern.Experiment;

/// <summary>
/// The profile revision a compiled Experiment names: sha256 over serde's
/// compact, declaration-order encoding of the typed profile, matching
/// <c>ExecutionProfile::resolve</c>.
/// </summary>
public static class ExecutionProfileIdentity
{
    public static TextResult<string> RevisionHash(string profileJson)
    {
        try
        {
            using var document = JsonDocument.Parse(profileJson);
            var bytes = SerializeDeclaration(document.RootElement);
            return TextResult<string>.Ok(
                "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        catch (JsonException error)
        {
            return TextResult<string>.Refused($"cannot parse execution profile: {error.Message}");
        }
        catch (InvalidOperationException error)
        {
            return TextResult<string>.Refused(error.Message);
        }
    }

    static byte[] SerializeDeclaration(JsonElement profile)
    {
        if (profile.TryGetProperty("worker_binary", out _)
            || profile.TryGetProperty("worker_binary_sha256", out _))
        {
            throw new InvalidOperationException(
                "execution profile contains retired fern-worker fields");
        }

        var output = new StringBuilder();
        output.Append('{');
        WriteField(output, true, "schema", profile.GetProperty("schema"));
        WriteField(output, false, "profile_id", profile.GetProperty("profile_id"));
        WriteField(output, false, "executor", profile.GetProperty("executor"));
        WriteOptional(output, "ssh_destination", profile);
        WriteOptional(output, "registry_home_profile_id", profile);
        WriteField(output, false, "device", profile.GetProperty("device"));
        WriteField(output, false, "source_root", profile.GetProperty("source_root"));
        WriteField(output, false, "python", profile.GetProperty("python"));
        WriteRaw(output, false, "worker", CompactObject(profile.GetProperty("worker"), ["module", "slots"]));
        WriteRaw(output, false, "supervisor", CompactObject(
            profile.GetProperty("supervisor"),
            ["max_restarts", "restart_delay_seconds", "max_target_seconds", "max_sweep_seconds"]));
        if (profile.TryGetProperty("accelerator", out var accelerator)
            && accelerator.ValueKind is not JsonValueKind.Null)
        {
            WriteRaw(output, false, "accelerator", CompactAccelerator(accelerator));
        }

        WriteOptional(output, "known_data_root", profile);
        WriteOptional(output, "registry_path", profile);
        WriteField(output, false, "campaign_root", profile.GetProperty("campaign_root"));
        WriteField(output, false, "artifact_root", profile.GetProperty("artifact_root"));
        WriteField(output, false, "work_root", profile.GetProperty("work_root"));
        WriteField(output, false, "warn_free_gib", profile.GetProperty("warn_free_gib"));
        WriteField(output, false, "warn_free_percent", profile.GetProperty("warn_free_percent"));
        output.Append('}');
        return new UTF8Encoding(false, true).GetBytes(output.ToString());
    }

    static void WriteOptional(StringBuilder output, string name, JsonElement parent)
    {
        if (parent.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null)
        {
            WriteField(output, false, name, value);
        }
    }

    static void WriteField(StringBuilder output, bool first, string name, JsonElement value)
    {
        if (!first) output.Append(',');
        output.Append(CanonicalJson.SerdeQuotedString(name));
        output.Append(':');
        output.Append(value.GetRawText());
    }

    static void WriteRaw(StringBuilder output, bool first, string name, string compact)
    {
        if (!first) output.Append(',');
        output.Append(CanonicalJson.SerdeQuotedString(name));
        output.Append(':');
        output.Append(compact);
    }

    static string CompactObject(JsonElement value, IReadOnlyList<string> order)
    {
        var output = new StringBuilder();
        output.Append('{');
        var first = true;
        foreach (var name in order)
        {
            WriteField(output, first, name, value.GetProperty(name));
            first = false;
        }

        output.Append('}');
        return output.ToString();
    }

    static string CompactAccelerator(JsonElement accelerator)
    {
        var output = new StringBuilder();
        output.Append('{');
        WriteField(output, true, "min_memory_gib", accelerator.GetProperty("min_memory_gib"));
        if (accelerator.TryGetProperty("min_compute_capability", out var capability)
            && capability.ValueKind is JsonValueKind.Array)
        {
            output.Append(",\"min_compute_capability\":[");
            var first = true;
            foreach (var item in capability.EnumerateArray())
            {
                if (!first) output.Append(',');
                first = false;
                output.Append(item.GetUInt32().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            output.Append(']');
        }

        if (accelerator.TryGetProperty("required_device_uuid", out var uuid)
            && uuid.ValueKind is not JsonValueKind.Null)
        {
            WriteField(output, false, "required_device_uuid", uuid);
        }

        output.Append('}');
        return output.ToString();
    }
}
