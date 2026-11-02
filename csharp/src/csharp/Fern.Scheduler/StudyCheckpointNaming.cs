using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fern.Experiment;
using Fern.Registry;
using Fern.Site;

namespace Fern.Scheduler;

/// <summary>
/// Registry acceptance, scientific identity, then Site publication; no training or Registry mutation.
/// Called by fern-run name-checkpoint. Pin: docs/how-to/study-collection.md.
/// </summary>
public static class StudyCheckpointNaming
{
    static string RequiredText(JsonElement value, string field)
    {
        var text = value.GetProperty(field).GetString();
        return !string.IsNullOrWhiteSpace(text) ? text : throw new JsonException($"checkpoint {field} must be a nonempty string");
    }

    public static TextResult<string> Execute(string registry, string study, long revision, string cell,
        string producerResults, string checkpointStore, string name)
    {
        try
        {
            using var reader = RegistryHome.OpenReader(registry);
            var accepted = RegistryOperations.ReadAcceptedStudyCell(reader.Connection, study, revision, cell);
            if (!accepted.TryGetValue(out var source))
            {
                accepted.TryGetRefusal(out var refusal);
                return TextResult<string>.Refused(refusal.Detail);
            }
            using var configDocument = JsonDocument.Parse(source.ConfigJson);
            var config = configDocument.RootElement;
            if (config.GetProperty("execution").GetProperty("kind").GetString() != "train")
                return TextResult<string>.Refused("only an accepted training cell can name producer weights");
            using var acceptedDocument = JsonDocument.Parse(source.ResultJson);
            var registeredResult = acceptedDocument.RootElement;
            string resultSha256;
            if (registeredResult.GetProperty("schema").GetString() == StudyCollect.SavedResultSchema)
            {
                if (!registeredResult.GetProperty("saved").GetBoolean() || registeredResult.GetProperty("study").GetString() != study)
                    return TextResult<string>.Refused("accepted result reference is not saved for this study");
                resultSha256 = RequiredText(registeredResult, "sha256");
            }
            else resultSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.ResultJson)));
            var resultPath = Path.GetFullPath(producerResults);
            var producerRoot = Path.GetDirectoryName(resultPath)!;
            var resultCheck = StageAttemptSite.VerifyInstalledSource(producerRoot,
                [new(Path.GetFileName(resultPath), new FileInfo(resultPath).Length, "sha256:" + resultSha256)]);
            if (!resultCheck.IsOk) return TextResult<string>.Refused("producer result: " + resultCheck.Failure.Detail);
            var resultBytes = File.ReadAllBytes(resultPath);
            if (Convert.ToHexStringLower(SHA256.HashData(resultBytes)) != resultSha256)
                return TextResult<string>.Refused("producer result changed after verification");
            using var resultDocument = JsonDocument.Parse(resultBytes);
            var result = resultDocument.RootElement;
            // CHG 2026-09-27 (config-hash-leaves, slice 1): the configuration is compared whole; the
            // hash beside it decided nothing the comparison did not.
            if (result.GetProperty("schema").GetString() != StudyCollect.UnitResultsSchema
                || result.GetProperty("study").GetString() != study || result.GetProperty("cell").GetString() != cell
                || !JsonElement.DeepEquals(result.GetProperty("config"), config))
                return TextResult<string>.Refused("producer result identity or training configuration differs from its accepted registration");
            var checkpoint = result.GetProperty("facts").GetProperty("retained_checkpoints").GetProperty(cell);
            var file = RequiredText(checkpoint, "file");
            if (checkpoint.GetProperty("placement").GetString() != "producer"
                || string.IsNullOrWhiteSpace(file) || file is "." or ".."
                || file != Path.GetFileName(file) || file.Contains('\\'))
                return TextResult<string>.Refused("retained checkpoint must name one producer filename");
            var bytes = checkpoint.GetProperty("byte_size").GetInt64();
            var digest = RequiredText(checkpoint, "sha256");
            var runtime = config.GetProperty("runtime");
            var receipt = new NamedCheckpointReceipt(NamedCheckpointPublication.ReceiptSchema, name, digest, bytes,
                // CHG 2026-09-27 (config-hash-leaves, slice 4): the receipt carries the training
                // configuration itself, which an evaluation compares whole.
                study, cell, source.AttemptId, source.Commit, config.Clone(),
                RequiredText(runtime, "dtype"), RequiredText(runtime, "amp_dtype"),
                DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture), source.StudyId, source.Revision,
                source.CellId, source.SiteId, source.Site, source.SourceId, resultSha256);
            var published = NamedCheckpointPublication.Publish(producerRoot, new(file, bytes, "sha256:" + digest), checkpointStore, receipt);
            return published.IsOk ? TextResult<string>.Ok(published.Value) : TextResult<string>.Refused(published.Failure.Detail);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                                     or KeyNotFoundException or InvalidOperationException or ArgumentException or RegistryFault)
        {
            return TextResult<string>.Refused($"checkpoint naming refused: {error.Message}");
        }
    }
}
