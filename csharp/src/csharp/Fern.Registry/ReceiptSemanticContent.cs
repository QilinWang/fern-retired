using System.Text;
using System.Text.Json;

namespace Fern.Registry;

/// <summary>
/// How a Registry authority classifies one incoming receipt against what it
/// has already accepted under the same receipt identity. Exact replay is
/// idempotent; the same identity with different content is a conflict that is
/// never resolved by last-write-wins.
/// </summary>
public enum ReceiptReplayVerdict
{
    FirstAcceptance,
    IdempotentReplay,
    ContentConflict,
}

/// <summary>
/// The shared semantic-digest rule for every receipt family (phase,
/// environment, installation, model-link, terminal).
///
/// A receipt has four field layers: identity-bearing fields and evidence
/// fields decide what was proved and enter this digest; measurement fields
/// (timings) and observation metadata (when it ran, from which process and
/// endpoint) describe the occasion and stay out, so the exact same proof
/// replayed a minute later is digest-identical.
///
/// What the preimage covers, exactly: the canonicalized JSON bytes of every
/// top-level field not named in the caller's exclusion set, with object keys
/// sorted ordinally at every depth, array order preserved, and number
/// spellings preserved as produced. Exclusion applies at the top level only.
/// The digest is stable for one producer replaying one receipt through this
/// function; it is not a cross-language byte contract.
///
/// CHG 2026-09-26 (owner: "get rid of 256 entirely"): no digest is taken.
/// <see cref="Canonical"/> returns the canonicalized text itself, and
/// <see cref="Classify"/> compares two such texts; "preimage" above now means
/// that text. The class was ReceiptSemanticDigest.
/// </summary>
public static class ReceiptSemanticContent
{
    /// <summary>
    /// The baseline observation-metadata field names: facts about when and
    /// through what a receipt was produced, never about what it proved.
    /// A family passes this set, or this set union its own measurement names.
    /// </summary>
    public static readonly IReadOnlySet<string> ObservationMetadataFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "created_at_ms",
        "recorded_at_ms",
        "observed_at_ms",
        "elapsed_ms",
        "elapsed_seconds",
        "timings_ms",
        "measurements",
        "pid",
        "process_start_ticks",
        "ssh_endpoint",
        "ssh_port",
        "endpoint_session_id",
    };

    public static string Canonical(string receiptJson, IReadOnlySet<string> excludedTopLevelFields)
    {
        using var document = JsonDocument.Parse(receiptJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("a receipt is a JSON object");
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject()
                         .Where(property => !excludedTopLevelFields.Contains(property.Name))
                         .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static ReceiptReplayVerdict Classify(string? acceptedContent, string incomingContent)
    {
        if (string.IsNullOrWhiteSpace(incomingContent))
        {
            throw new ArgumentException("an incoming receipt has content");
        }
        if (string.IsNullOrWhiteSpace(acceptedContent))
        {
            return ReceiptReplayVerdict.FirstAcceptance;
        }
        return string.Equals(acceptedContent, incomingContent, StringComparison.Ordinal)
            ? ReceiptReplayVerdict.IdempotentReplay
            : ReceiptReplayVerdict.ContentConflict;
    }

    static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                // Preserve the producer's spelling; 1 and 1.0 stay distinct.
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException($"unsupported receipt JSON value kind {element.ValueKind}");
        }
    }
}
