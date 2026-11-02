using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Fern.Scheduler;

/// <summary>
/// The one JSON rendering every host verb uses when the Rust verb rendered
/// through <c>checked_pretty_json</c>: Rust serialized into a
/// <c>serde_json::Value</c>, whose objects are B-tree maps, so every object's
/// keys came out sorted at every depth. System.Text.Json writes declaration
/// order, which disagrees with the old executable on byte one (measured
/// 2026-08-28 against <c>target/debug/fern-run list-pending-runs</c>). Sort
/// once here rather than reordering every record by hand. Verbs whose Rust
/// side forwarded C# bytes unchanged (list-runs, compare-runs) keep
/// declaration order and must not call this.
///
/// CHG 2026-08-28: moved from Fern.Run.Cli into Fern.Scheduler so the report
/// owners that live here — <see cref="Preflight"/> above all — render through
/// the same function the CLI does. Fern.Run.Cli references this project, so
/// every previous caller reaches it unchanged; the reverse was impossible,
/// which is why a second copy of the f64 rule had grown here.
/// </summary>
public static class ReportJson
{
    public static string SortedPretty(object value, JsonSerializerOptions options)
    {
        var rustNumbers = new JsonSerializerOptions(options);
        rustNumbers.Converters.Add(new RustDouble());
        var node = JsonSerializer.SerializeToNode(value, rustNumbers)
            ?? throw new InvalidOperationException("a report serialized to JSON null");
        return SortedPretty(node);
    }

    /// <summary>
    /// The same rendering for a report already built as a node. A caller on this
    /// path spells its own doubles with <see cref="Number"/>, because a
    /// <c>JsonValue</c> writes itself and never reaches a converter.
    /// </summary>
    public static string SortedPretty(JsonNode node) =>
        // serde writes UTF-8 as is and escapes only quotes, backslashes and
        // control characters; System.Text.Json's default escaper would turn
        // "·" into "\u00B7" and "<" into "\u003C".
        SortKeys(node)!.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    /// <summary>
    /// One f64 as raw JSON, for a report assembled as nodes rather than
    /// serialized from a record. A missing or non-finite value is null, which is
    /// what serde writes for both.
    /// </summary>
    public static JsonNode? Number(double? value) =>
        value is { } number && double.IsFinite(number) ? JsonNode.Parse(Spell(number)) : null;

    /// <summary>
    /// serde_json's f64 spelling: a whole number keeps its ".0" ("0.0", "64.0"),
    /// everything else is the shortest round-trip form with a lower-case
    /// exponent and no zero padding ("1.5e-7", "1e21"), and NaN or an infinity
    /// becomes null. System.Text.Json writes "0", "1.5E-07" and refuses NaN,
    /// which is where the old executable and this host disagreed.
    ///
    /// This is the only place the rule is written. Both renderings above go
    /// through it, so a report built from a record and a report built from nodes
    /// cannot spell the same number two ways.
    /// </summary>
    public static string Spell(double value)
    {
        var text = value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var exponent = text.IndexOf('E');
        if (exponent < 0)
        {
            return text.Contains('.') ? text : text + ".0";
        }
        var mantissa = text[..exponent];
        var power = text[(exponent + 1)..];
        var negative = power.StartsWith('-');
        power = power.TrimStart('+', '-').TrimStart('0');
        if (power.Length == 0)
        {
            power = "0";
        }
        return $"{mantissa}e{(negative ? "-" : "")}{power}";
    }

    /// <summary>
    /// The OTHER Rust rendering, for the identity documents rather than the
    /// reports: `serde_json::to_vec` on a typed value — compact, DECLARATION
    /// order, and every absent optional written as null rather than omitted.
    ///
    /// It is a separate entry point rather than a flag because the two are
    /// opposite by design: <see cref="SortedPretty(JsonNode)"/> reorders keys
    /// because Rust's report path went through a B-tree map, and this one must
    /// not, because Rust's identity path did not. Both spell their doubles with
    /// <see cref="Spell"/>, so a number cannot mean two things depending on
    /// which document carries it.
    ///
    /// A caller on this path builds its own nodes and spells its own doubles
    /// with <see cref="Number"/>; a subtree parsed from bytes someone else
    /// wrote is emitted with its original number text intact, which is what
    /// keeps a passed-through document byte-identical to how it arrived.
    /// </summary>
    public static string CompactDeclarationOrder(JsonNode node)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            // serde escapes only what JSON requires and writes UTF-8 through;
            // System.Text.Json escapes non-ASCII by default, which would change
            // the bytes a digest is taken over.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            node.WriteTo(writer);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    sealed class RustDouble : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDouble();

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (!double.IsFinite(value))
            {
                writer.WriteNullValue();
                return;
            }
            writer.WriteRawValue(Spell(value), skipInputValidation: true);
        }
    }

    static JsonNode? SortKeys(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject source:
                var sorted = new JsonObject();
                foreach (var pair in source.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    sorted[pair.Key] = SortKeys(pair.Value?.DeepClone());
                }
                return sorted;
            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    items.Add(SortKeys(item?.DeepClone()));
                }
                return items;
            default:
                return node?.DeepClone();
        }
    }
}
