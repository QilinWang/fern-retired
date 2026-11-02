using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fern.Site;

/// <summary>The existing Python named-checkpoint v1 receipt plus exact registered provenance.</summary>
public sealed record NamedCheckpointReceipt(string Schema, string Name, string Sha256, long ByteSize,
    string Study, string Cell, long Attempt, string Commit, JsonElement Config, string Dtype, string AmpDtype,
    string NamedAt, long StudyId, long Revision, long CellId, long SiteId, string Site,
    long SourceId, string ResultSha256);
// CHG 2026-09-26 (cards-without-hashes, schema 21): source_bundle_sha256 left the receipt with
// the column it copied; SourceId and Commit name the source. Older receipts keep the field.

/// <summary>
/// Publish accepted producer weights locally under an immutable name. Registry acceptance belongs to the caller.
/// The workflow is docs/how-to/study-collection.md, "Name an accepted producer checkpoint".
/// </summary>
public static class NamedCheckpointPublication
{
    public const string ReceiptSchema = "fern.named_checkpoint.v1";
    static readonly Regex Name = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z", RegexOptions.CultureInvariant);
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };

    public static SiteResult<string> Publish(string producerRoot, AttemptFile weights, string storeRoot, NamedCheckpointReceipt receipt)
    {
        string? staging = null;
        try
        {
            if (!Name.IsMatch(receipt.Name))
                return SiteResult<string>.Permanent("checkpoint name must be one path segment of letters, digits, '.', '-' or '_' (at most 100 characters)");
            if (receipt.Schema != ReceiptSchema || receipt.Sha256.Length != 64 || receipt.ByteSize < 1
                || weights.ByteCount != receipt.ByteSize || weights.ByteDigest != "sha256:" + receipt.Sha256
                || weights.RelativePath != Path.GetFileName(weights.RelativePath) || weights.RelativePath.Contains('\\'))
                return SiteResult<string>.Permanent("checkpoint receipt and single producer filename must describe the same bytes");
            var sourceCheck = StageAttemptSite.VerifyInstalledSource(producerRoot, [weights]);
            if (!sourceCheck.IsOk) return sourceCheck.RefusalAs<string>();
            var root = Path.Combine(Path.GetFullPath(storeRoot), "checkpoints");
            var rootCheck = StageAttemptSite.VerifyInstalledSource(root, [], allowMissing: true);
            if (!rootCheck.IsOk) return rootCheck.RefusalAs<string>();
            Directory.CreateDirectory(root);
            var destination = Path.Combine(root, receipt.Name);
            if (Directory.Exists(destination) || File.Exists(destination) || new FileInfo(destination).LinkTarget is not null)
                return SiteResult<string>.Permanent($"checkpoint name {receipt.Name} already exists; names are immutable");
            staging = Path.Combine(root, $".{receipt.Name}.{Guid.NewGuid():N}.partial");
            Directory.CreateDirectory(staging);
            using (var input = new FileStream(Path.Combine(producerRoot, weights.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(Path.Combine(staging, "model.safetensors"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            var receiptBytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Json);
            using (var output = new FileStream(Path.Combine(staging, "receipt.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(receiptBytes);
                output.Flush(flushToDisk: true);
            }
            var declared = new AttemptFile[]
            {
                new("model.safetensors", receipt.ByteSize, "sha256:" + receipt.Sha256),
                new("receipt.json", receiptBytes.LongLength, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(receiptBytes))),
            };
            var copyCheck = StageAttemptSite.VerifyInstalledSource(staging, declared);
            if (!copyCheck.IsOk) return copyCheck.RefusalAs<string>();
            sourceCheck = StageAttemptSite.VerifyInstalledSource(producerRoot, [weights]);
            if (!sourceCheck.IsOk) return sourceCheck.RefusalAs<string>();
            // A competing publisher owns its final name. Rename never replaces it.
            Directory.Move(staging, destination);
            staging = null;
            return SiteResult<string>.Ok(destination);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return SiteResult<string>.Permanent($"checkpoint publication refused: {error.Message}");
        }
        finally
        {
            if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}
