using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fern.Registry;

/// <summary>Existing Registry JSON wire settings and generic serialization, independent of historical Task execution.</summary>
public static class RegistryJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>
    /// Closed result objects whose null members are part of the cross-language
    /// wire contract. Commands choose this explicitly; ordinary sparse Task
    /// documents keep using <see cref="Options"/>.
    /// </summary>
    public static readonly JsonSerializerOptions CompleteResultOptions = new(Options)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new RegistryFault($"JSON for {typeof(T).Name} is empty");

    public static byte[] Utf8Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static string Snake<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonSerializer.Serialize(value, Options).Trim('"');
}
