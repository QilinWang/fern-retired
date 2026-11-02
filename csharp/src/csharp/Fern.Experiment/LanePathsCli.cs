using System.Text.Json;

namespace Fern.Experiment;

/// <summary>
/// The typed <c>fern-experiment show-lane-paths</c> operation.  It owns the
/// three companion paths for one scientific lane, so callers cannot assemble
/// a database, artifact root and scratch root from different lane spellings.
/// </summary>
public static class LanePathsCli
{
    public const string Operation = "show-lane-paths";
    public const string ReportSchema = "fern.lane_paths.v1";

    public sealed record Request(string Lane, string CampaignRoot, string WorkRoot, bool PublicReport = false);

    sealed record Receipt(string Schema, string Lane, string Db, string ArtifactRoot, string WorkRoot);
    sealed record PublicReceipt(
        string ArtifactRoot,
        string Db,
        string Lane,
        string Note,
        string Schema,
        string WorkRoot);

    static readonly JsonSerializerOptions ReplyOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static TextResult<byte[]> Execute(byte[] requestBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(requestBytes);
            var fields = StrictJson.StructFields(document.RootElement, "$", ["lane", "campaign_root", "work_root", "public_report"]);
            var request = new Request(
                StrictJson.RequiredString(fields, "$", "lane"),
                StrictJson.RequiredString(fields, "$", "campaign_root"),
                StrictJson.RequiredString(fields, "$", "work_root"),
                StrictJson.OptionalBool(fields, "$", "public_report"));
            if (string.IsNullOrEmpty(request.Lane) || request.Lane.IndexOfAny(['/', '\\', ' ']) >= 0)
            {
                return TextResult<byte[]>.Refused("a lane name is one path segment, with no separators or spaces");
            }

            var laneDirectory = Path.Combine(request.CampaignRoot, request.Lane);
            var database = Path.Combine(laneDirectory, "fern.db");
            var artifactRoot = Path.Combine(laneDirectory, "runs");
            var workRoot = Path.Combine(request.WorkRoot, request.Lane);
            if (request.PublicReport)
            {
                var pretty = new JsonSerializerOptions(ReplyOptions) { WriteIndented = true };
                return TextResult<byte[]>.Ok(JsonSerializer.SerializeToUtf8Bytes(new PublicReceipt(
                    artifactRoot,
                    database,
                    request.Lane,
                    "derived here so no caller composes these by hand",
                    ReportSchema,
                    workRoot), pretty));
            }
            return TextResult<byte[]>.Ok(JsonSerializer.SerializeToUtf8Bytes(new Receipt(
                ReportSchema, request.Lane, database, artifactRoot, workRoot), ReplyOptions));
        }
        catch (JsonException error)
        {
            return TextResult<byte[]>.Refused($"{Operation}: request is not typed JSON: {error.Message}");
        }
        catch (StrictJson.Refusal error)
        {
            return TextResult<byte[]>.Refused($"{Operation}: {error.Message}");
        }
    }
}
