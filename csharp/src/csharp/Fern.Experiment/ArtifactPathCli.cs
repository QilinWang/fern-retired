using System.Globalization;
using System.Text.Json;

namespace Fern.Experiment;

/// <summary>
/// The typed <c>fern-experiment build-artifact-dir</c> operation.  One owner
/// turns a resolved Run's immutable display coordinates into its local
/// artifact directory; Rust passes the selected fields but does not normalize
/// or compose the short identifier after this boundary.
/// </summary>
public static class ArtifactPathCli
{
    public const string Operation = "build-artifact-dir";
    public const string ReportSchema = "fern.artifact_path.v1";

    public sealed record Request(
        string Root,
        string RunCollection,
        string? Dataset,
        string ModelName,
        long PredLen,
        long Seed,
        double NoiseSigma,
        long TrajectoryDrawOrdinal,
        long NoiseDrawOrdinal,
        string? RunHash,
        string ShortIdFormat);

    sealed record Receipt(string Schema, string ArtifactDir);

    static readonly JsonSerializerOptions ReplyOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static TextResult<byte[]> Execute(byte[] requestBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(requestBytes);
            var fields = StrictJson.StructFields(document.RootElement, "$", [
                "root", "run_collection", "dataset", "model_name", "pred_len", "seed", "noise_sigma",
                "trajectory_draw_ordinal", "noise_draw_ordinal", "run_hash", "short_id_format",
            ]);
            var request = new Request(
                StrictJson.RequiredString(fields, "$", "root"),
                StrictJson.RequiredString(fields, "$", "run_collection"),
                StrictJson.OptionalString(fields, "$", "dataset"),
                StrictJson.RequiredString(fields, "$", "model_name"),
                RequiredInt64(fields, "pred_len"),
                RequiredInt64(fields, "seed"),
                RequiredFiniteDouble(fields, "noise_sigma"),
                RequiredInt64(fields, "trajectory_draw_ordinal"),
                RequiredInt64(fields, "noise_draw_ordinal"),
                StrictJson.OptionalString(fields, "$", "run_hash"),
                StrictJson.RequiredString(fields, "$", "short_id_format"));
            return TextResult<byte[]>.Ok(JsonSerializer.SerializeToUtf8Bytes(
                new Receipt(ReportSchema, BuildArtifactDirectory(request)), ReplyOptions));
        }
        catch (JsonException error)
        {
            return TextResult<byte[]>.Refused($"{Operation}: request is not typed JSON: {error.Message}");
        }
        catch (StrictJson.Refusal error)
        {
            return TextResult<byte[]>.Refused($"{Operation}: {error.Message}");
        }
        catch (ArgumentException error)
        {
            return TextResult<byte[]>.Refused($"{Operation}: {error.Message}");
        }
    }

    /// <summary>The closed, local path projection used by both CLI and Run packet callers.</summary>
    public static string BuildArtifactDirectory(Request request)
    {
        var shortId = new List<string>();
        switch (request.ShortIdFormat)
        {
            case "single-run":
                shortId.Add(PathComponent(request.ModelName));
                break;
            case "grid-draw":
                if (request.Dataset is not null) shortId.Add(PathComponent(request.Dataset));
                break;
            default:
                throw new ArgumentException(
                    $"short_id_format must be \"single-run\" or \"grid-draw\", not \"{request.ShortIdFormat}\"");
        }

        shortId.Add($"h{request.PredLen}");
        if (request.ShortIdFormat == "grid-draw") shortId.Add($"traj{request.TrajectoryDrawOrdinal}");
        shortId.Add(request.NoiseSigma == 0.0 ? "clean" : $"sig{SigmaToPath(request.NoiseSigma)}");
        if (request.ShortIdFormat == "grid-draw") shortId.Add($"noise{request.NoiseDrawOrdinal}");
        shortId.Add($"s{request.Seed}");
        shortId.Add(PathComponent(FirstFourUnicodeScalars(request.RunHash ?? "pending")));
        return Path.Combine(request.Root, PathComponent(request.RunCollection), string.Join("_", shortId));
    }

    /// <summary>Rust's stable human path spelling for one finite noise sigma.</summary>
    public static string SigmaToPath(double value)
    {
        var text = value.ToString("G", CultureInfo.InvariantCulture);
        if (text.Contains('E', StringComparison.OrdinalIgnoreCase))
        {
            text = value.ToString("F12", CultureInfo.InvariantCulture);
        }
        if (text.Contains('.')) text = text.TrimEnd('0').TrimEnd('.');
        if (text.Length == 0 || text == "-0") text = "0";
        return text.Replace("-", "neg", StringComparison.Ordinal).Replace(".", "p", StringComparison.Ordinal);
    }

    /// <summary>One portable, local artifact-name path component.</summary>
    public static string PathComponent(string value)
    {
        var output = new System.Text.StringBuilder();
        var lastWasUnderscore = false;
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if ((character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') || character is '-' or '=' or '_')
            {
                output.Append(character);
                lastWasUnderscore = false;
            }
            else if (!lastWasUnderscore)
            {
                output.Append('_');
                lastWasUnderscore = true;
            }
        }
        var trimmed = output.ToString().Trim('_');
        return trimmed.Length == 0 ? "standalone" : trimmed;
    }

    /// <summary>Rust <c>str::chars().take(4)</c>, not a UTF-16 code-unit slice.</summary>
    static string FirstFourUnicodeScalars(string value)
    {
        var end = 0;
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            end += rune.Utf16SequenceLength;
            if (++count == 4) break;
        }
        return value[..end];
    }

    static long RequiredInt64(Dictionary<string, JsonElement> fields, string name)
    {
        var value = StrictJson.Required(fields, "$", name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var parsed))
        {
            throw StrictJson.Refuse($"$.{name}", "invalid type: expected an i64 JSON number");
        }
        return parsed;
    }

    static double RequiredFiniteDouble(Dictionary<string, JsonElement> fields, string name)
    {
        var value = StrictJson.Required(fields, "$", name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var parsed) || !double.IsFinite(parsed))
        {
            throw StrictJson.Refuse($"$.{name}", "invalid type: expected a finite JSON number");
        }
        return parsed;
    }
}
