using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace Fern.Registry;

/// <summary>The explicit installed context shared by local operational readers.</summary>
public sealed record RuntimeSelection(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("registry")] string Registry,
    [property: JsonPropertyName("run_binary")] string RunBinary,
    [property: JsonPropertyName("registry_binary")] string RegistryBinary,
    [property: JsonPropertyName("experiment_binary")] string ExperimentBinary,
    [property: JsonPropertyName("compile_binary")] string CompileBinary,
    [property: JsonPropertyName("installation_receipt")] string InstallationReceipt,
    [property: JsonPropertyName("binary_set_id")] string BinarySetId,
    [property: JsonPropertyName("registry_sha256")] string RegistrySha256,
    [property: JsonPropertyName("built_from_commit")] string BuiltFromCommit,
    [property: JsonPropertyName("publisher_version")] string PublisherVersion)
{
    public const string CurrentSchema = "fern.runtime_selection.v1";

    public static RuntimeSelection Read(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("FERN_RUNTIME_SELECTION");
        if (path is null)
        {
            var root = new DirectoryInfo(Environment.CurrentDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "work.toml"))) root = root.Parent;
            if (root is null) throw new RegistryFault("No workspace runtime selection; set FERN_RUNTIME_SELECTION or pass an explicit Registry path");
            // CHG 2026-09-26: deploy/ moved to runtime/deploy/ (RepositoryLayout).
            path = Path.Combine(root.FullName, RepositoryLayout.DeployDirectory, "current", "runtime.json");
        }
        try
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("FERN_RUNTIME_SELECTION must name a file");
            var bytes = File.ReadAllBytes(path);
            var selection = JsonSerializer.Deserialize<RuntimeSelection>(bytes,
                new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
            if (selection is null || selection.Schema != CurrentSchema)
                throw new InvalidOperationException("Unknown runtime selection schema");
            foreach (var value in new[] { selection.Registry, selection.RunBinary, selection.RegistryBinary,
                selection.ExperimentBinary, selection.CompileBinary, selection.InstallationReceipt })
                if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || !File.Exists(value))
                    throw new InvalidOperationException($"Selected runtime file is missing or not absolute: {value}");
            if (string.IsNullOrWhiteSpace(selection.BinarySetId) || string.IsNullOrWhiteSpace(selection.RegistrySha256)
                || string.IsNullOrWhiteSpace(selection.BuiltFromCommit) || string.IsNullOrWhiteSpace(selection.PublisherVersion))
                throw new InvalidOperationException("Runtime selection lacks installed provenance");
            var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var identity = directory.ResolveLinkTarget(returnFinalTarget: true)?.Name ?? directory.Name;
            if (identity != Convert.ToHexStringLower(SHA256.HashData(bytes)))
                throw new InvalidOperationException("Runtime selection differs from its published content identity");
            return selection;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        { throw new RegistryFault($"Runtime selection refused ({path}): {error.Message}"); }
    }
}
