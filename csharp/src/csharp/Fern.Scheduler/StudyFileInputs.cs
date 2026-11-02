using System.Security.Cryptography;
using System.Text.Json;

namespace Fern.Scheduler;

/// <summary>Transport consumes the compiler's file declarations, not dataset-specific loaders.</summary>
public sealed record StudyFileInput(string Path, string Sha256, ulong Bytes);

public static class StudyFileInputs
{
    public static IReadOnlyList<StudyFileInput> Read(string compiledStudy, string sourceRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(compiledStudy));
        var root = document.RootElement;
        if (root.GetProperty("schema").GetString() != "fern.compiled-study.v1")
            throw new StudySourceRegistration.Refusal("declared inputs require a compiled Study, not its authored source");
        var inputs = root.GetProperty("dataset").GetProperty("inputs");
        var result = new Dictionary<string, StudyFileInput>(StringComparer.Ordinal);
        foreach (var entry in inputs.EnumerateObject())
        {
            var input = entry.Value;
            var declared = input.GetProperty("path").GetString() ?? throw new StudySourceRegistration.Refusal("dataset input has no path");
            var full = System.IO.Path.GetFullPath(declared, sourceRoot);
            var relative = System.IO.Path.GetRelativePath(sourceRoot, full).Replace('\\', '/');
            if (relative.Split('/').Any(part => part is "" or "." or "..") || System.IO.Path.IsPathRooted(relative))
                throw new StudySourceRegistration.Refusal($"dataset input must be a file below source root: {declared}");
            var digest = input.GetProperty("sha256").GetString() ?? "";
            if (digest.Length != 64 || digest.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new StudySourceRegistration.Refusal($"dataset input has invalid SHA-256: {declared}");
            var value = new StudyFileInput(relative, digest, input.GetProperty("byte_count").GetUInt64());
            if (result.TryGetValue(relative, out var previous) && previous != value)
                throw new StudySourceRegistration.Refusal($"dataset input has conflicting identities: {declared}");
            result[relative] = value;
        }
        return result.Values.OrderBy(input => input.Path, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// CHG 2026-09-27 (owner: a registration checks only a study's core files): the files the
    /// study's cells load, as the compiler lists them in <c>cell_code_files</c>
    /// (fern_train.cell_code_files); null for a study compiled before the list existed.
    /// </summary>
    public static IReadOnlySet<string>? CellCodeFiles(string compiledStudy)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(compiledStudy));
        if (!document.RootElement.TryGetProperty("cell_code_files", out var files)) return null;
        return files.EnumerateArray()
            .Select(file => file.GetString() ?? throw new StudySourceRegistration.Refusal("cell_code_files holds an entry that is not a path"))
            .ToHashSet(StringComparer.Ordinal);
    }

    public static void Verify(string sourceRoot, IReadOnlyList<StudyFileInput> inputs)
    {
        foreach (var input in inputs)
        {
            if (System.IO.Path.IsPathRooted(input.Path) || input.Path.Contains('\\')
                || input.Path.Split('/').Any(part => part is "" or "." or ".."))
                throw new StudySourceRegistration.Refusal($"dataset input must be a normalized relative file: {input.Path}");
            var path = System.IO.Path.Combine(sourceRoot, input.Path);
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new StudySourceRegistration.Refusal($"declared dataset input is missing or is not a regular file: {input.Path}");
            for (var parent = info.Directory; parent is not null && parent.FullName != sourceRoot; parent = parent.Parent)
                if (parent.LinkTarget is not null)
                    throw new StudySourceRegistration.Refusal($"declared dataset input traverses a symlink: {input.Path}");
            using var stream = File.OpenRead(path);
            if ((ulong)stream.Length != input.Bytes || Convert.ToHexStringLower(SHA256.HashData(stream)) != input.Sha256)
                throw new StudySourceRegistration.Refusal($"declared dataset input bytes differ from the compiled Study: {input.Path}");
        }
    }
}
