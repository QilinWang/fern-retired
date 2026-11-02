using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;

namespace Fern.Experiment;

/// <summary>
/// The byte form every Fern content address is computed over, reproduced exactly.
///
/// This is the sorted-key convention -- Rust calls it
/// <c>fern_json_contract::canonical_json_bytes</c> -- and it names source
/// contents, implementations, stage plans and recipes. Getting it wrong does not
/// raise: both sides remain a valid sha256 over valid JSON, and the only symptom
/// is that already-accepted work stops being findable under its own name.
///
/// Three things System.Text.Json does by default that are wrong here, all
/// measured on 2026-08-26:
///
///   it escapes every non-ASCII character, where serde_json leaves them raw;
///   it escapes &lt;, &gt; and &amp;, and disagrees with Go on the hex case;
///   it does NOT sort dictionary keys, where Rust and Go both do.
///
/// So none of the work below is defensive tidiness. Each line is one of those
/// three differences, closed.
/// </summary>
public static class CanonicalJson
{
    // Rust String is always valid UTF-8. The throwing encoder makes that same
    // admission rule explicit before C# can replace an unpaired surrogate with
    // U+FFFD and silently mint a different cross-language identity.
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Canonical bytes for one JSON document: keys sorted, no whitespace,
    /// non-ASCII left raw, numbers byte-for-byte as they were written.
    /// </summary>
    /// <remarks>
    /// Numbers are copied as their original text rather than parsed and
    /// reformatted. A round trip through double would turn 1.0 into 1, or
    /// 1e-7 into 1E-07, and either changes the digest while looking like the
    /// same number.
    /// </remarks>
    public static byte[] Bytes(JsonElement value)
    {
        var output = new StringBuilder();
        Write(value, output);
        return StrictUtf8.GetBytes(output.ToString());
    }

    /// <summary>
    /// The same canonical bytes for a mutable node, so a caller building a
    /// JsonObject does not have to invent its own conversion.
    ///
    /// CHG 2026-09-10: this overload exists because Fern.Scheduler carried a
    /// SECOND CanonicalJson, in its own namespace, taking JsonNode — so a bare
    /// call inside that project bound to the copy and a fully-qualified one
    /// reached this class, and the two disagreed on numbers. The copy is gone
    /// and its callers come here.
    ///
    /// The node goes through `ToJsonString` and `JsonDocument.Parse` before the
    /// element walk, and that round trip is REQUIRED rather than lazy. I tried
    /// walking the JsonNode directly on 2026-09-10 to save a serialize and a
    /// parse per digest; fifteen preimage and study-launch digests changed.
    ///
    /// The reason is that this class copies a number's ORIGINAL TEXT, and a node
    /// built in code has none. `JsonValue.Create(1.0)` holds a CLR double, not
    /// the characters "1.0", so there is no raw text to copy until something
    /// writes one. `ToJsonString` is what writes it. Parsing that back gives
    /// every value the text form the digest is defined over, which is exactly
    /// what a caller who assembled a JsonObject in memory has not got.
    ///
    /// So the cost is real and so is the reason: a serialize and a parse buy the
    /// one property this whole class exists for.
    /// </summary>
    public static byte[] Bytes(JsonNode value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return Bytes(document.RootElement);
    }

    /// <summary>The 64-hex sha256 of the canonical bytes, without a prefix.</summary>
    public static string DigestHex(JsonElement value) =>
        Convert.ToHexString(SHA256.HashData(Bytes(value))).ToLowerInvariant();

    /// <summary>
    /// The serde_json escaping of one string, quotes included, as raw JSON
    /// text. Exposed for the declaration-order card writers: Utf8JsonWriter's
    /// encoders escape an embedded quote as \u0022 where serde writes \", so
    /// a card string value whose alphabet is not statically ASCII-safe must be
    /// written through this instead of Utf8JsonWriter.WriteString. Measured
    /// 2026-08-26 against the card-identity golden: canonical_config_json was
    /// the first value to differ, and those bytes are hashed.
    /// </summary>
    internal static string SerdeQuotedString(string value)
    {
        var output = new StringBuilder(value.Length + 2);
        WriteString(value, output);
        return output.ToString();
    }

    static void Write(JsonElement value, StringBuilder output)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                var first = true;
                var properties = value.EnumerateObject()
                    .Select(property => (Property: property, Utf8Name: Utf8BytesOf(property.Name)))
                    .ToList();
                properties.Sort(static (left, right) =>
                    left.Utf8Name.AsSpan().SequenceCompareTo(right.Utf8Name));
                foreach (var (property, _) in properties)
                {
                    if (!first) output.Append(',');
                    first = false;
                    WriteString(property.Name, output);
                    output.Append(':');
                    Write(property.Value, output);
                }
                output.Append('}');
                return;

            case JsonValueKind.Array:
                output.Append('[');
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem) output.Append(',');
                    firstItem = false;
                    Write(item, output);
                }
                output.Append(']');
                return;

            case JsonValueKind.String:
                WriteString(value.GetString()!, output);
                return;

            // Raw text, deliberately: see the remarks on Bytes.
            case JsonValueKind.Number:
                output.Append(value.GetRawText());
                return;

            case JsonValueKind.True:
                output.Append("true");
                return;
            case JsonValueKind.False:
                output.Append("false");
                return;
            case JsonValueKind.Null:
                output.Append("null");
                return;

            default:
                throw new InvalidOperationException($"canonical JSON cannot encode {value.ValueKind}");
        }
    }

    /// <summary>
    /// A JSON string escaped the way serde_json escapes one: the two mandatory
    /// escapes, the five short forms, \u00XX for the remaining control
    /// characters, and everything else -- including all non-ASCII -- verbatim.
    /// </summary>
    static void WriteString(string value, StringBuilder output)
    {
        EnsureWellFormedUtf16(value);
        output.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                default:
                    if (character < 0x20)
                    {
                        output.Append("\\u").Append(((int)character).ToString("x4"));
                    }
                    else
                    {
                        // Non-ASCII stays raw. This is the line that makes C#
                        // agree with serde_json instead of with its own default.
                        output.Append(character);
                    }
                    break;
            }
        }
        output.Append('"');
    }

    static byte[] Utf8BytesOf(string value)
    {
        EnsureWellFormedUtf16(value);
        return StrictUtf8.GetBytes(value);
    }

    static void EnsureWellFormedUtf16(string value)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException error)
        {
            throw new ArgumentException(
                "text contains an unpaired UTF-16 surrogate and cannot represent a Rust String",
                nameof(value),
                error);
        }
    }
}

/// <summary>
/// SHA-256 digest in the Fern wire form <c>sha256:</c> plus 64 lowercase hex digits.
///
/// Used for subject, implementation and artifact-content hashes. It lives beside
/// the canonical byte form because the two answer one question together: these
/// bytes, under this digest, are the name of a thing.
/// </summary>
public readonly record struct Sha256Digest
{
    const string Sha256Prefix = "sha256:";
    const int Sha256HexLength = 64;

    readonly string _value;

    Sha256Digest(string value) => _value = value;

    public static Sha256Digest FromBytes(ReadOnlySpan<byte> bytes) =>
        new($"{Sha256Prefix}{Convert.ToHexStringLower(SHA256.HashData(bytes))}");

    public static TextResult<Sha256Digest> Parse(string value)
    {
        if (!value.StartsWith(Sha256Prefix, StringComparison.Ordinal))
        {
            return TextResult<Sha256Digest>.Refused("sha256: must start with sha256:");
        }
        var hex = value.AsSpan(Sha256Prefix.Length);
        if (hex.Length != Sha256HexLength || !AllLowercaseHex(hex))
        {
            return TextResult<Sha256Digest>.Refused(
                "sha256: must contain exactly 64 lowercase hexadecimal digits");
        }
        return TextResult<Sha256Digest>.Ok(new Sha256Digest(value));
    }

    /// <summary>
    /// The full <c>sha256:</c>-prefixed text. <c>default(Sha256Digest)</c> is the
    /// one hole C# leaves in a validated struct; reading it is a defect.
    /// </summary>
    public string Value =>
        _value ?? throw new InvalidOperationException("default Sha256Digest carries no value; use Parse or FromBytes");

    public override string ToString() => Value;

    static bool AllLowercaseHex(ReadOnlySpan<char> hex)
    {
        foreach (var character in hex)
        {
            var isDigit = character is >= '0' and <= '9';
            var isLowerHex = character is >= 'a' and <= 'f';
            if (!isDigit && !isLowerHex)
            {
                return false;
            }
        }
        return true;
    }
}
