using System.Text.Json;

namespace Fern.Experiment;

/// <summary>
/// Strict JSON reading primitives shared by the typed wire readers -- the
/// stand-in for what serde derive gives Rust for free. Every family's reader
/// is built from these; every family's ERROR TYPE and SERIALIZER stay its own,
/// because a serializer belongs to an identity family, never to a project.
///
/// A struct-shaped object refuses unknown and duplicate fields
/// (<c>deny_unknown_fields</c> plus serde's duplicate-field check); a
/// map-shaped object takes the last value for a repeated key exactly as
/// serde's BTreeMap insert does.
/// </summary>
internal static class StrictJson
{
    /// <summary>
    /// Internal control flow standing in for Rust's <c>?</c>. Never escapes a
    /// wire reader: each family's public boundary catches it and returns the
    /// refusal as that family's typed error.
    /// </summary>
    internal sealed class Refusal : Exception
    {
        public Refusal(string path, string detail) : base($"{detail} at {path}")
        {
            Path = path;
            Detail = detail;
        }

        public string Path { get; }

        public string Detail { get; }
    }

    internal static Refusal Refuse(string path, string detail) => new(path, detail);

    /// <summary>
    /// Fields of one struct-shaped object: unknown fields and duplicate fields
    /// are refused.
    /// </summary>
    internal static Dictionary<string, JsonElement> StructFields(JsonElement element, string path, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Refuse(path, "invalid type: expected a JSON object");
        }
        var fields = new Dictionary<string, JsonElement>();
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                var expected = string.Join(", ", allowed.Select(name => $"`{name}`"));
                throw Refuse(path, $"unknown field `{property.Name}`, expected one of {expected}");
            }
            if (!fields.TryAdd(property.Name, property.Value))
            {
                throw Refuse(path, $"duplicate field `{property.Name}`");
            }
        }
        return fields;
    }

    /// <summary>
    /// Entries of one map-shaped object. A repeated key takes the LAST value,
    /// because serde deserializes a BTreeMap by inserting each entry and
    /// BTreeMap::insert overwrites; matching that exactly matters more than
    /// improving on it.
    /// </summary>
    internal static SortedDictionary<string, T> MapEntries<T>(
        JsonElement element, string path, Func<JsonElement, string, T> readEntry)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Refuse(path, "invalid type: expected a JSON object");
        }
        var entries = new SortedDictionary<string, T>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            entries[property.Name] = readEntry(property.Value, $"{path}.{property.Name}");
        }
        return entries;
    }

    internal static JsonElement Required(Dictionary<string, JsonElement> fields, string path, string name) =>
        fields.TryGetValue(name, out var value) ? value : throw Refuse(path, $"missing field `{name}`");

    internal static string RequiredString(Dictionary<string, JsonElement> fields, string path, string name)
    {
        var value = Required(fields, path, name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Refuse($"{path}.{name}", "invalid type: expected a string");
        }
        return value.GetString()!;
    }

    /// <summary>Option&lt;String&gt;: absent and explicit null both decode to no value.</summary>
    internal static string? OptionalString(Dictionary<string, JsonElement> fields, string path, string name)
    {
        if (!fields.TryGetValue(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw Refuse($"{path}.{name}", "invalid type: expected a string or null"),
        };
    }

    /// <summary>
    /// <c>#[serde(default)]</c> on a bool: absent means false, but an explicit
    /// null is refused -- default fills only a MISSING field.
    /// </summary>
    internal static bool OptionalBool(Dictionary<string, JsonElement> fields, string path, string name)
    {
        if (!fields.TryGetValue(name, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Refuse($"{path}.{name}", "invalid type: expected a boolean"),
        };
    }

    /// <summary>
    /// <c>#[serde(default)]</c> on a Vec&lt;String&gt;: absent means empty, but an
    /// explicit null is refused for the same reason as <see cref="OptionalBool"/>.
    /// </summary>
    internal static List<string> OptionalStringList(Dictionary<string, JsonElement> fields, string path, string name) =>
        fields.TryGetValue(name, out var value) ? StringList(value, $"{path}.{name}") : [];

    internal static bool RequiredBool(Dictionary<string, JsonElement> fields, string path, string name) =>
        Required(fields, path, name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Refuse($"{path}.{name}", "invalid type: expected a boolean"),
        };

    internal static uint RequiredU32(Dictionary<string, JsonElement> fields, string path, string name)
    {
        var value = Required(fields, path, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out var number))
        {
            throw Refuse($"{path}.{name}", "invalid value: expected an unsigned 32-bit integer");
        }
        return number;
    }

    internal static ulong RequiredU64(Dictionary<string, JsonElement> fields, string path, string name)
    {
        var value = Required(fields, path, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var number))
        {
            throw Refuse($"{path}.{name}", "invalid value: expected an unsigned 64-bit integer");
        }
        return number;
    }

    /// <summary>Option&lt;u64&gt;: absent and explicit null both decode to no value.</summary>
    internal static ulong? OptionalU64(Dictionary<string, JsonElement> fields, string path, string name)
    {
        if (!fields.TryGetValue(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var number))
        {
            throw Refuse($"{path}.{name}", "invalid value: expected an unsigned 64-bit integer or null");
        }
        return number;
    }

    internal static long RequiredI64(Dictionary<string, JsonElement> fields, string path, string name)
    {
        var value = Required(fields, path, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        {
            throw Refuse($"{path}.{name}", "invalid value: expected a signed 64-bit integer");
        }
        return number;
    }

    internal static List<string> RequiredStringList(Dictionary<string, JsonElement> fields, string path, string name)
    {
        var value = Required(fields, path, name);
        return StringList(value, $"{path}.{name}");
    }

    internal static List<string> StringList(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Refuse(path, "invalid type: expected an array of strings");
        }
        var items = new List<string>();
        var ordinal = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw Refuse($"{path}[{ordinal}]", "invalid type: expected a string");
            }
            items.Add(item.GetString()!);
            ordinal++;
        }
        return items;
    }
}
