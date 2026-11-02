using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fern.Experiment;

/// <summary>
/// A JSON value written the way Rust's serde_json writes it, so that bytes
/// produced here can stand in for bytes produced there inside an identity
/// preimage.
/// </summary>
/// <remarks>
/// Two things distinguish this from <see cref="CanonicalJson"/>, which copies
/// numbers as the text it was given: here numbers are VALUES. A float is
/// laid out by the ryu rules serde_json uses (<c>1.0</c> stays <c>1.0</c>,
/// <c>1e-7</c> stays <c>1e-7</c>, <c>1e16</c> stays <c>1e16</c>) and an
/// integer prints as an integer. That is what identity scheme 2 of
/// <c>fern.cfg.v2</c> needs: it re-serializes a typed config, filling every
/// implicit field with its default, so the defaults must be formatted the
/// way serde would have formatted them, not the way they happen to appear in
/// some stored text. Object keys are sorted by their UTF-8 bytes, which is
/// what serde_json's <c>Map</c> (a BTreeMap in this workspace) does at every
/// depth.
/// </remarks>
public abstract record SerdeJsonValue
{
    public sealed record Null : SerdeJsonValue;

    public sealed record Bool(bool Value) : SerdeJsonValue;

    /// <summary>An integer serde would hold as i64 or u64 and print bare.</summary>
    public sealed record Integer(string Digits) : SerdeJsonValue
    {
        public static Integer Of(long value) => new(value.ToString(CultureInfo.InvariantCulture));

        public static Integer Of(ulong value) => new(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A finite f64, printed by <see cref="SerdeFloat.Format(double)"/>.</summary>
    public sealed record Float(double Value) : SerdeJsonValue;

    /// <summary>A finite f32, printed by <see cref="SerdeFloat.Format(float)"/>.</summary>
    public sealed record Float32(float Value) : SerdeJsonValue;

    public sealed record Text(string Value) : SerdeJsonValue;

    public sealed record Array(IReadOnlyList<SerdeJsonValue> Items) : SerdeJsonValue;

    /// <summary>Keys are sorted by ordinal (UTF-8 byte) order when written.</summary>
    public sealed record Object(IReadOnlyDictionary<string, SerdeJsonValue> Members) : SerdeJsonValue
    {
        public static Object Of(params (string Key, SerdeJsonValue Value)[] members)
        {
            var map = new Dictionary<string, SerdeJsonValue>(StringComparer.Ordinal);
            foreach (var (key, value) in members)
            {
                map.Add(key, value);
            }

            return new Object(map);
        }
    }

    /// <summary>
    /// Convert a parsed JSON element into a value serde_json would hold after
    /// parsing the same text: a number with a fraction or exponent becomes an
    /// f64, any other number an integer, so that re-serialization normalizes
    /// <c>1.50</c> to <c>1.5</c> and leaves <c>150</c> alone, exactly as
    /// <c>serde_json::Value</c> does.
    /// </summary>
    /// <summary>
    /// Convert a node the generated writers built into the value serde_json
    /// would hold: a node the writer created from a typed double is a float
    /// however integral its value (60.0 prints as 60.0, never 60), a typed
    /// long is an integer, and a node parsed from text is read as
    /// <see cref="FromElement"/> reads it.
    /// </summary>
    public static SerdeJsonValue FromNode(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return new Null();
            case JsonObject members:
            {
                var map = new Dictionary<string, SerdeJsonValue>(StringComparer.Ordinal);
                foreach (var (key, value) in members)
                {
                    if (!map.TryAdd(key, FromNode(value)))
                    {
                        throw new JsonException($"duplicate key {key} is not a serde_json document");
                    }
                }
                return new Object(map);
            }
            case JsonArray items:
                return new Array(items.Select(FromNode).ToList());
            case JsonValue value:
                if (value.TryGetValue<JsonElement>(out var element))
                {
                    return FromElement(element);
                }
                if (value.TryGetValue<long>(out var integral))
                {
                    return Integer.Of(integral);
                }
                if (value.TryGetValue<double>(out var real))
                {
                    return new Float(real);
                }
                if (value.TryGetValue<bool>(out var flag))
                {
                    return new Bool(flag);
                }
                if (value.TryGetValue<string>(out var text))
                {
                    return new Text(text);
                }
                throw new JsonException($"a JSON value of {value.GetValueKind()} the writers never produce");
            default:
                throw new JsonException($"a JSON node of {node.GetType().Name} the writers never produce");
        }
    }

    public static SerdeJsonValue FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return new Null();
            case JsonValueKind.True:
                return new Bool(true);
            case JsonValueKind.False:
                return new Bool(false);
            case JsonValueKind.String:
                return new Text(element.GetString()!);
            case JsonValueKind.Number:
            {
                var text = element.GetRawText();
                if (text.Contains('.') || text.Contains('e') || text.Contains('E'))
                {
                    return new Float(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
                }

                if (text.StartsWith('-') ? long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
                                         : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    return new Integer(text);
                }

                // serde_json without arbitrary_precision parses an integer that
                // overflows i64/u64 as f64.
                return new Float(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            case JsonValueKind.Array:
            {
                var items = new List<SerdeJsonValue>();
                foreach (var item in element.EnumerateArray())
                {
                    items.Add(FromElement(item));
                }

                return new Array(items);
            }
            case JsonValueKind.Object:
            {
                var members = new Dictionary<string, SerdeJsonValue>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    // serde_json's Map keeps the LAST duplicate key; JSON with
                    // duplicate keys never leaves serde, so refuse it here.
                    if (!members.TryAdd(property.Name, FromElement(property.Value)))
                    {
                        throw new JsonException($"duplicate key {property.Name} is not a serde_json document");
                    }
                }

                return new Object(members);
            }
            default:
                throw new JsonException($"unsupported JSON value kind {element.ValueKind}");
        }
    }

    /// <summary>Compact, key-sorted UTF-8 bytes, as <c>serde_json::to_vec</c> of a <c>Value</c>.</summary>
    public byte[] ToSerdeBytes()
    {
        var output = new StringBuilder();
        Write(this, output);
        return new UTF8Encoding(false, true).GetBytes(output.ToString());
    }

    public string ToSerdeString()
    {
        var output = new StringBuilder();
        Write(this, output);
        return output.ToString();
    }

    static void Write(SerdeJsonValue value, StringBuilder output)
    {
        switch (value)
        {
            case Null:
                output.Append("null");
                break;
            case Bool boolean:
                output.Append(boolean.Value ? "true" : "false");
                break;
            case Integer integer:
                output.Append(integer.Digits);
                break;
            case Float f64:
                output.Append(SerdeFloat.Format(f64.Value));
                break;
            case Float32 f32:
                output.Append(SerdeFloat.Format(f32.Value));
                break;
            case Text text:
                output.Append(CanonicalJson.SerdeQuotedString(text.Value));
                break;
            case Array array:
                output.Append('[');
                for (var index = 0; index < array.Items.Count; index++)
                {
                    if (index > 0)
                    {
                        output.Append(',');
                    }

                    Write(array.Items[index], output);
                }

                output.Append(']');
                break;
            case Object obj:
            {
                output.Append('{');
                var first = true;
                foreach (var key in obj.Members.Keys.Order(StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        output.Append(',');
                    }

                    first = false;
                    output.Append(CanonicalJson.SerdeQuotedString(key)).Append(':');
                    Write(obj.Members[key], output);
                }

                output.Append('}');
                break;
            }
            default:
                throw new InvalidOperationException($"unwritten SerdeJsonValue {value.GetType().Name}");
        }
    }
}

/// <summary>
/// The ryu "pretty" layout serde_json uses for finite floats: the shortest
/// digit string that round-trips, placed as a plain decimal when the decimal
/// point lands within <c>-5 &lt; kk &lt;= 16</c> (kk = digits before the
/// point, negative for leading zeros), an integer-valued float keeps a
/// trailing <c>.0</c>, and everything else is <c>d.ddde&lt;exp&gt;</c> with
/// no plus sign and no zero padding. A non-finite value has no serde
/// representation (serde_json writes <c>null</c> for it) and is refused
/// here because no config field may carry one.
/// </summary>
public static class SerdeFloat
{
    public static string Format(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "a non-finite float has no serde identity bytes");
        }

        // .NET Core 3.0+ "R" is the shortest round-trip form; we only take its
        // digits and exponent and re-lay them out the ryu way.
        var (negative, digits, exponent) = Decompose(value.ToString("R", CultureInfo.InvariantCulture));
        return Layout(negative, digits, exponent);
    }

    public static string Format(float value)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "a non-finite float has no serde identity bytes");
        }

        var (negative, digits, exponent) = Decompose(value.ToString("R", CultureInfo.InvariantCulture));
        return Layout(negative, digits, exponent);
    }

    /// <summary>Split a round-trip string into sign, significant digits, and the power of ten of its last digit.</summary>
    static (bool Negative, string Digits, int Exponent) Decompose(string text)
    {
        var negative = text.StartsWith('-');
        if (negative)
        {
            text = text[1..];
        }

        var exponent = 0;
        var exponentAt = text.IndexOfAny(['E', 'e']);
        if (exponentAt >= 0)
        {
            exponent = int.Parse(text[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..exponentAt];
        }

        var point = text.IndexOf('.');
        if (point >= 0)
        {
            exponent -= text.Length - point - 1;
            text = text.Remove(point, 1);
        }

        var digits = text.TrimStart('0');
        if (digits.Length == 0)
        {
            return (negative, "0", 0);
        }

        var trailing = digits.Length - digits.TrimEnd('0').Length;
        digits = digits[..^trailing];
        exponent += trailing;
        return (negative, digits, exponent);
    }

    static string Layout(bool negative, string digits, int exponent)
    {
        var output = new StringBuilder();
        if (negative)
        {
            output.Append('-');
        }

        if (digits == "0")
        {
            return output.Append("0.0").ToString();
        }

        var length = digits.Length;
        var kk = length + exponent;
        if (exponent >= 0 && kk <= 16)
        {
            output.Append(digits).Append('0', exponent).Append(".0");
        }
        else if (kk > 0 && kk <= 16)
        {
            output.Append(digits, 0, kk).Append('.').Append(digits, kk, length - kk);
        }
        else if (kk > -5 && kk <= 0)
        {
            output.Append("0.").Append('0', -kk).Append(digits);
        }
        else
        {
            output.Append(digits[0]);
            if (length > 1)
            {
                output.Append('.').Append(digits, 1, length - 1);
            }

            output.Append('e').Append((kk - 1).ToString(CultureInfo.InvariantCulture));
        }

        return output.ToString();
    }
}
