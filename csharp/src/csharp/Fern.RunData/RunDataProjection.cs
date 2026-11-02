using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fern.RunData;

/// <summary>Exact lookup result for the immutable config preimage stored by enrollment.</summary>
public sealed record RunConfigPreimageReport(string Schema, string RequestedConfigHash, string? ConfigJson);

/// <summary>The enrollment-owned immutable config-preimage lookup.</summary>
public static class RunConfigPreimageStore
{
    public const string ReadSchema = "fern.run_config_preimage_read.v1";
    const int ReaderTimeoutSeconds = 15;

    public static RunConfigPreimageReport Find(string databasePath, string configHash)
    {
        if (!File.Exists(databasePath))
        {
            throw new RunDataFault($"run database {databasePath} is not a regular file");
        }
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(databasePath),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = ReaderTimeoutSeconds,
            }.ToString());
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA query_only = ON";
            pragma.ExecuteNonQuery();
            using var command = connection.CreateCommand();
            command.CommandTimeout = ReaderTimeoutSeconds;
            command.CommandText = "SELECT config_json FROM configs WHERE config_hash = $config_hash";
            command.Parameters.AddWithValue("$config_hash", configHash);
            var value = command.ExecuteScalar();
            return new RunConfigPreimageReport(
                ReadSchema,
                configHash,
                value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture));
        }
        catch (RunDataFault)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RunDataFault($"cannot read config preimage {configHash}", exception);
        }
    }
}

/// <summary>Shared faithful derivations for fresh enrollment and historical protocol backfill.</summary>
public static class RunDataProjection
{
    public static RunProtocolColumns ProtocolFromInvocation(string? invocationJson)
    {
        if (invocationJson is null) return new(null, null, null, null, null, null, null);
        try
        {
            using var packet = JsonDocument.Parse(invocationJson);
            var root = packet.RootElement;
            var runtimeDevice = TextAt(root, "identity", "config", "runtime", "device");
            var executionKind = TextAt(root, "identity", "config", "execution", "kind");
            if (!TryAt(root, out var protocol, "identity", "context", "protocol"))
                return new(null, null, null, null, null, runtimeDevice, executionKind);
            var ar1 = protocol.TryGetProperty("noise_ar1_phi", out var ar1Value) && ar1Value.TryGetDouble(out var number)
                ? number
                : 0.0;
            bool? relativeDose = protocol.TryGetProperty("relative_dose", out var dose) && dose.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? dose.GetBoolean()
                : null;
            return new(
                TextAt(protocol, "noise_law"),
                TextAt(protocol, "split_treatment_scheme"),
                ar1,
                relativeDose,
                TextAt(protocol, "checkpoint_execution", "mode"),
                runtimeDevice,
                executionKind);
        }
        catch (JsonException)
        {
            return new(null, null, null, null, null, null, null);
        }
    }

    public static string? ModelConfigHash(string? configJson)
    {
        if (configJson is null) return null;
        try
        {
            using var config = JsonDocument.Parse(configJson);
            if (!config.RootElement.TryGetProperty("model", out var model)) return null;
            var canonical = PythonStyleJson(model);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..12].ToLowerInvariant();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? TextAt(JsonElement root, params string[] path) =>
        TryAt(root, out var value, path) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static bool TryAt(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var segment in path)
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value)) return false;
        return true;
    }

    static string PythonStyleJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(", ", value.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"{JsonSerializer.Serialize(property.Name)}: {PythonStyleJson(property.Value)}")) + "}",
        JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(PythonStyleJson)) + "]",
        JsonValueKind.Number => NormalizeJsonNumber(value),
        _ => value.GetRawText(),
    };

    static string NormalizeJsonNumber(JsonElement value)
    {
        var raw = value.GetRawText();
        if (value.TryGetInt64(out var signed)) return signed.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetUInt64(out var unsigned)) return unsigned.ToString(CultureInfo.InvariantCulture);
        var normalized = value.GetDouble().ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        var exponent = normalized.IndexOf('e');
        if (exponent >= 0)
        {
            var prefix = normalized[..(exponent + 1)];
            var suffix = normalized[(exponent + 1)..];
            var sign = suffix.StartsWith('-') ? "-" : suffix.StartsWith('+') ? "+" : "";
            suffix = suffix.TrimStart('+', '-').TrimStart('0');
            normalized = prefix + sign + (suffix.Length == 0 ? "0" : suffix);
        }
        if (!normalized.Contains('.') && !normalized.Contains('e') && (raw.Contains('.') || raw.Contains('e') || raw.Contains('E')))
            normalized += ".0";
        return normalized;
    }
}

public sealed record RunProtocolColumns(
    string? NoiseLaw,
    string? SplitTreatmentScheme,
    double? NoiseAr1Phi,
    bool? RelativeDose,
    string? CheckpointExecutionMode,
    string? RuntimeDevice,
    string? ExecutionKind)
{
    public bool IsEmpty => NoiseLaw is null && SplitTreatmentScheme is null && NoiseAr1Phi is null
        && RelativeDose is null && CheckpointExecutionMode is null && RuntimeDevice is null && ExecutionKind is null;
}
