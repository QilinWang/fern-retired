using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fern.Install;

/// <summary>
/// Pass 2 addendum (reference:7930679b4dc5): caller-supplied JSON is strict;
/// a stored frozen record is tolerant and lists every unknown field.
/// </summary>
public static class JsonReadPolicy
{
    public static readonly JsonSerializerOptions CallerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static readonly JsonSerializerOptions StoredOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static CallerJson<T> ReadCaller<T>(string json) where T : class
    {
        using var document = JsonDocument.Parse(json);
        var known = JsonFieldNames<T>.Names;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!known.Contains(property.Name))
            {
                return new CallerJson<T>.UnknownField(property.Name);
            }
        }

        var value = JsonSerializer.Deserialize<T>(json, CallerOptions);
        if (value is null)
        {
            throw new InvalidOperationException("caller JSON deserialized to null");
        }

        return new CallerJson<T>.Accepted(value);
    }

    public static StoredJson<T> ReadStored<T>(string json) where T : class
    {
        using var document = JsonDocument.Parse(json);
        var known = JsonFieldNames<T>.Names;
        var unknown = new List<UnknownStoredField>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!known.Contains(property.Name))
            {
                unknown.Add(new UnknownStoredField(property.Name, property.Value.GetRawText()));
            }
        }

        var value = JsonSerializer.Deserialize<T>(json, StoredOptions);
        if (value is null)
        {
            throw new InvalidOperationException("stored JSON deserialized to null");
        }

        return new StoredJson<T>(value, unknown);
    }

    public static string Write(object value) =>
        JsonSerializer.Serialize(value, CallerOptions);
}

public sealed record UnknownStoredField(string FieldName, string RawJson);

/// <summary>
/// CHG 2026-09-10: a union, not an abstract record, for the reason recorded on
/// <see cref="InstallOutcome"/>: the hierarchy let a reader that had excluded
/// only <see cref="UnknownField"/> write <c>((CallerJson&lt;T&gt;.Accepted)read).Value</c>,
/// which is an <c>InvalidCastException</c> waiting for a third case. That cast
/// does not compile on a union. The cases stay nested, so caller spellings are
/// unchanged. Values of this type live only in locals and returns, never in a
/// field or a collection — see docs/design/original/2026-09-10-when-an-outcome-becomes-a-union.md.
/// </summary>
public union CallerJson<T>(CallerJson<T>.Accepted, CallerJson<T>.UnknownField)
    where T : class
{
    public sealed record Accepted(T Value);

    public sealed record UnknownField(string FieldName);
}

public sealed record StoredJson<T>(T Value, IReadOnlyList<UnknownStoredField> UnknownFields)
    where T : class;

static class JsonFieldNames<T>
{
    public static readonly FrozenSet<string> Names = Build();

    static FrozenSet<string> Build()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in typeof(T).GetProperties())
        {
            var named = property.GetCustomAttributes(typeof(JsonPropertyNameAttribute), inherit: false);
            if (named.Length == 1)
            {
                names.Add(((JsonPropertyNameAttribute)named[0]).Name);
                continue;
            }

            names.Add(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
        }

        return names.ToFrozenSet(StringComparer.Ordinal);
    }
}
