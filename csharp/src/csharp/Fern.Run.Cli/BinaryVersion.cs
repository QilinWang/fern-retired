using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fern.Run.Cli;

/// <summary>
/// The identity this binary reports about itself: the same facts the Rust
/// fern-run embedded (`fern.binary_version.v1`), so receipts that name a tool
/// keep one schema across the cutover. `source_archive_sha256` is present only
/// when the installer built the binary from a sealed archive.
/// </summary>
public sealed record BinaryVersionReport(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("crate_version")] string CrateVersion,
    [property: JsonPropertyName("built_from_commit")] string BuiltFromCommit,
    [property: JsonPropertyName("built_from_dirty_tree")] bool BuiltFromDirtyTree,
    [property: JsonPropertyName("source_archive_sha256")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SourceArchiveSha256,
    [property: JsonPropertyName("run_plan_protocol")] string RunPlanProtocol)
{
    public const string SchemaId = "fern.binary_version.v1";

    /// <summary>The parent/child Plan protocol this binary speaks; unchanged from the Rust value.</summary>
    public const string ProtocolId = "fern.run_plan_parent_child.v2";

    public static BinaryVersionReport FromThisBinary()
    {
        var assembly = typeof(BinaryVersionReport).Assembly;
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value ?? "", StringComparer.Ordinal);
        var commit = metadata.GetValueOrDefault("FernBuiltFromCommit", "").Trim();
        var dirtyLines = metadata.GetValueOrDefault("FernBuiltFromDirtyTree", "0").Trim();
        return new BinaryVersionReport(
            SchemaId,
            assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            commit.Length == 40 ? commit : "unrecorded",
            dirtyLines != "0",
            // CHG: source identity is embedded at build time, not supplied by
            // the shell that later invokes an installed binary.
            metadata.GetValueOrDefault("FernCommittedSourceArchiveSha256"),
            ProtocolId);
    }

    /// <summary>Rust printed this through checked_pretty_json: sorted keys.</summary>
    public string PrettyJson() => ReportJson.SortedPretty(this, new JsonSerializerOptions());

    public string OneLine() =>
        $"fern-run {CrateVersion} from commit {BuiltFromCommit}{(BuiltFromDirtyTree ? " (dirty tree)" : "")}; "
        + $"archive {SourceArchiveSha256 ?? "development build"}; protocol {RunPlanProtocol}";
}
