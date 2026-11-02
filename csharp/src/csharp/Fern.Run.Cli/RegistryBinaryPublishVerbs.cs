using System.CommandLine;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fern.Scheduler;
using Fern.Install;

namespace Fern.Run.Cli;

// The surviving half of the Go fern-card install-knowledge-reconcile-service
// (tools/cardbox-go/fern/knowledge_install.go): its publishBinary published a
// digest-named fern-registry under <deployment-root>/registry-binaries/ and
// refused an existing name whose bytes disagreed. The reconcile service, its
// timer and their receipt retire with fern-card; the content-addressed publish
// moves here and grows a receipt of its own, because a generated thing carries
// its provenance or does not persist.

/// <summary>
/// Publish one built fern-registry into a content-addressed store: `fern-run publish-registry-binary`;
/// and any other control-plane member the same way: `fern-run publish-binary --member NAME`.
///
/// CHG 2026-09-11 night (slice 10 of the cell port): a build is identified by
/// its inputs (ControlMembers.InputDigest), so `--build` first looks for a
/// published copy whose receipt carries the same input digest and answers
/// with it instead of building; `--rebuild` builds anyway. The Registry
/// receipt is v3 for the one field this adds, <c>input_digest</c>, null when
/// the binary came by `--from`, whose inputs nobody here observed.
/// </summary>
public static class RegistryBinaryPublishVerbs
{
    const string PublishSchema = "fern.registry-binary-publish.v3";
    const string Publisher = "fern-run publish-registry-binary";

    internal sealed record RegistryBinaryPublishReceipt(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("source_path")] string SourcePath,
        [property: JsonPropertyName("published_path")] string PublishedPath,
        [property: JsonPropertyName("byte_size")] long ByteSize,
        [property: JsonPropertyName("provenance")] string Provenance,
        [property: JsonPropertyName("source_version_probe")] string SourceVersionProbe,
        [property: JsonPropertyName("source_version_probe_detail")] string SourceVersionProbeDetail,
        [property: JsonPropertyName("built_from_commit")] string BuiltFromCommit,
        [property: JsonPropertyName("built_from_dirty_tree")] bool BuiltFromDirtyTree,
        [property: JsonPropertyName("source_archive_sha256")] string? SourceArchiveSha256,
        [property: JsonPropertyName("input_digest")] string? InputDigest,
        [property: JsonPropertyName("dotnet_runtime")] string DotnetRuntime,
        [property: JsonPropertyName("published_at")] string PublishedAt,
        [property: JsonPropertyName("publisher")] string Publisher,
        [property: JsonPropertyName("publisher_version")] string PublisherVersion);

    public static void Add(Command parent)
    {
        parent.Subcommands.Add(PublishRegistryBinary());
        parent.Subcommands.Add(PublishBinary());
    }

    /// <summary>
    /// `publish-binary --member NAME [--deployment-root DIR] [--rebuild]`: one
    /// control-plane member, built from the committed source and published
    /// under its directory of the deployment root with a receipt beside it,
    /// or the published copy built from the same inputs. fern-registry keeps
    /// its own verb, whose receipt is the one `select-runtime` reads.
    /// </summary>
    static Command PublishBinary()
    {
        var command = new Command(
            "publish-binary",
            "Build one control-plane member (fern-site, fern-stage-runner, fern-experiment, fern-run, fern-compile) from the committed "
            + "source and publish it under <deployment-root>/<its directory>/<name>.<sha256> with a receipt beside it; a published "
            + "copy built from the same inputs is reused instead.");
        var member = new Option<string>("--member") { Description = "the member to publish, by executable name", Required = true };
        var deploymentRoot = new Option<string>("--deployment-root") { Description = "root for immutable local binaries and receipts", DefaultValueFactory = _ => Fern.Registry.RepositoryLayout.DeployDirectory /* CHG 2026-09-26: was deploy */ };
        var rebuild = new Option<bool>("--rebuild") { Description = "build even when a published copy was built from the same inputs" };
        command.Options.Add(member);
        command.Options.Add(deploymentRoot);
        command.Options.Add(rebuild);
        command.SetAction(parsed =>
        {
            var name = parsed.GetValue(member)!;
            if (name == "fern-registry")
            {
                Console.Error.WriteLine("fern-registry is published by publish-registry-binary --build, whose receipt select-runtime reads");
                return 2;
            }
            ControlMember chosen;
            try { chosen = ControlMembers.Named(name); }
            catch (InvalidOperationException unknown) { Console.Error.WriteLine(unknown.Message); return 2; }
            try
            {
                return InstallCli.PublishMember(chosen, parsed.GetValue(deploymentRoot)!, parsed.GetValue(rebuild), Console.Out, Console.Error);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
            { Console.Error.WriteLine($"{name} build/publication refused: " + error.Message); return 1; }
        });
        return command;
    }

    static Command PublishRegistryBinary()
    {
        var command = new Command(
            "publish-registry-binary",
            "Copy one built fern-registry into <dir>/fern-registry.<sha256> with a publish receipt beside it.");
        var from = new Option<string?>("--from")
        {
            Description = "the built fern-registry executable to publish; mutually exclusive with --build",
        };
        var into = new Option<string>("--into")
        {
            Description = "directory that holds the digest-named fern-registry binaries",
            Required = true,
        };
        command.Options.Add(from);
        command.Options.Add(into);

        var build = new Option<bool>("--build") { Description = "Build the current committed Registry with the existing isolated installation owner before publishing, unless a published copy was built from the same inputs" };
        var rebuild = new Option<bool>("--rebuild") { Description = "with --build: build even when a published copy was built from the same inputs" };
        command.Options.Add(build);
        command.Options.Add(rebuild);
        command.SetAction(parsed =>
        {
            var source = parsed.GetValue(from);
            var wantsBuild = parsed.GetValue(build);
            var directory = parsed.GetValue(into)!;
            if ((source is not null) == wantsBuild)
            { Console.Error.WriteLine("choose exactly one of --from or --build"); return 2; }
            if (!wantsBuild) return PublishFrom(source!, directory, null, Console.Out, Console.Error);
            using var stdout = new StringWriter(CultureInfo.InvariantCulture);
            using var stderr = new StringWriter(CultureInfo.InvariantCulture);
            try
            {
                var result = InstallCli.PublishRegistryFromCommit((inputDigest, buildRegistry) =>
                {
                    if (!parsed.GetValue(rebuild) && FindPublished(directory, inputDigest) is { } published)
                    {
                        stderr.WriteLine($"fern-registry unchanged at {published}: published from the same inputs ({inputDigest})");
                        stdout.WriteLine(published);
                        return 0;
                    }
                    return PublishFrom(buildRegistry(), directory, inputDigest, stdout, stderr);
                }, Console.Error);
                Console.Error.Write(stderr.ToString());
                Console.Out.Write(stdout.ToString());
                return result;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
            { Console.Error.WriteLine("Registry build/publication refused: " + error.Message); return 1; }
        });
        return command;
    }

    /// <summary>
    /// A published fern-registry whose receipt says it was built from exactly
    /// these inputs by a committed build, with its bytes still matching; the
    /// newest when several. Null when none.
    /// </summary>
    static string? FindPublished(string directory, string inputDigest)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) return null;
        var candidates = new List<(string PublishedAt, string Path)>();
        foreach (var receiptPath in Directory.EnumerateFiles(root, "fern-registry.*.receipt.json"))
        {
            RegistryBinaryPublishReceipt? receipt;
            try { receipt = JsonSerializer.Deserialize<RegistryBinaryPublishReceipt>(File.ReadAllBytes(receiptPath)); }
            catch (JsonException) { continue; }
            if (receipt is null || receipt.Schema != PublishSchema || receipt.InputDigest != inputDigest || receipt.BuiltFromDirtyTree) continue;
            var binary = receiptPath[..^".receipt.json".Length];
            if (!File.Exists(binary) || Digest(binary) != receipt.Sha256) continue;
            candidates.Add((receipt.PublishedAt, binary));
        }
        return candidates.Count == 0 ? null : candidates.OrderByDescending(candidate => candidate.PublishedAt, StringComparer.Ordinal).First().Path;
    }

    static int PublishFrom(string source, string directory, string? inputDigest, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            if (!File.Exists(source))
            {
                stderr.WriteLine(
                    $"publish-registry-binary failed: fern-registry executable {source} is not a regular file");
                return 1;
            }

            var sourcePath = Path.GetFullPath(source);
            var digest = Digest(sourcePath);
            Directory.CreateDirectory(directory);
            var final = Path.Combine(Path.GetFullPath(directory), "fern-registry." + digest);

            var status = "published";
            if (File.Exists(final))
            {
                var stored = Digest(final);
                if (!string.Equals(stored, digest, StringComparison.Ordinal))
                {
                    stderr.WriteLine(
                        $"publish-registry-binary failed: existing published binary {final} has wrong bytes");
                    return 1;
                }
                status = "unchanged";
            }
            else
            {
                CopyContentAddressed(sourcePath, final, digest);
            }

            var receiptPath = final + ".receipt.json";
            try
            {
                if (!File.Exists(receiptPath))
                {
                    var receipt = BuildReceipt(sourcePath, final, digest, inputDigest);
                    var stagingReceipt = receiptPath + ".publish-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.WriteAllText(stagingReceipt, ReportJson.SortedPretty(receipt, new JsonSerializerOptions()) + "\n");
                        File.Move(stagingReceipt, receiptPath, overwrite: false);
                    }
                    finally
                    {
                        if (File.Exists(stagingReceipt)) File.Delete(stagingReceipt);
                    }
                }
                else
                {
                    stderr.WriteLine("existing receipt preserved; build provenance was not recertified");
                }
            }
            finally
            {
                // CHG: a failed new candidate must release its digest name so
                // the unchanged input can retry; pre-existing files and any
                // published receipt belong to their original operation.
                if (status == "published" && !File.Exists(receiptPath)) File.Delete(final);
            }

            stderr.WriteLine($"fern-registry {status} at {final}");
            stdout.WriteLine(final);
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            stderr.WriteLine("publish-registry-binary failed: " + error.Message);
            return 1;
        }
    }

    /// <summary>
    /// Copy through a staging file in the same directory, re-hash what was
    /// copied, and only then claim the digest-fixed name; the Go publishBinary
    /// refused a source that changed underneath the copy the same way.
    /// </summary>
    static void CopyContentAddressed(string source, string final, string digest)
    {
        var staging = final + ".publish-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, staging, overwrite: false);
            var copied = Digest(staging);
            if (!string.Equals(copied, digest, StringComparison.Ordinal))
            {
                throw new IOException(
                    $"binary source changed while publishing: expected {digest}, copied {copied}");
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    staging,
                    UnixFileMode.UserRead | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            File.Move(staging, final, overwrite: false);
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }

    static RegistryBinaryPublishReceipt BuildReceipt(string sourcePath, string final, string digest, string? inputDigest)
    {
        // CHG: probe and measure the verified destination, never the mutable
        // original path. Recheck bytes after execution before writing a receipt.
        using var version = ProbeSourceVersion(final);
        var report = version.RootElement;
        string RequiredString(string field)
        {
            if (!report.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
                throw new IOException($"version --json requires string field {field}");
            return value.GetString()!;
        }

        if (RequiredString("schema") != "fern.registry_binary_version.v1")
            throw new IOException("version --json schema is not fern.registry_binary_version.v1");
        if (RequiredString("role") != "registry-home")
            throw new IOException("version --json role is not registry-home");
        if (RequiredString("run_plan_protocol") != BinaryVersionReport.ProtocolId)
            throw new IOException("version --json run_plan_protocol does not match this publisher");
        var commit = RequiredString("built_from_commit");
        if (!IsHex(commit, 40))
            throw new IOException("version --json built_from_commit must be a full 40-character Git identity");
        if (!report.TryGetProperty("built_from_dirty_tree", out var dirty)
            || dirty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new IOException("version --json requires boolean field built_from_dirty_tree");
        string? archive = null;
        if (report.TryGetProperty("source_archive_sha256", out var archiveValue) && archiveValue.ValueKind != JsonValueKind.Null)
        {
            if (archiveValue.ValueKind != JsonValueKind.String || !IsHex(archiveValue.GetString()!, 64))
                throw new IOException("version --json source_archive_sha256 must be a 64-character digest or null");
            archive = archiveValue.GetString();
        }
        if (archive is not null && dirty.GetBoolean())
            throw new IOException("version --json source_archive_sha256 cannot claim committed source with built_from_dirty_tree=true");
        var runtime = RequiredString("dotnet_runtime");
        if (Digest(final) != digest)
            throw new IOException("published binary changed during version probe: " + final);

        return new RegistryBinaryPublishReceipt(
            PublishSchema, digest, sourcePath, final, new FileInfo(final).Length,
            "fern-registry-version-json", "ok", "reported by the verified published binary",
            commit, dirty.GetBoolean(), archive, inputDigest, runtime,
            DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            Publisher, BinaryVersionReport.FromThisBinary().OneLine());
    }

    /// <summary>
    /// Ask the binary being published what it is. Three outcomes are different
    /// facts and the receipt keeps them apart: the child never started, it ran
    /// and refused (no such verb), or it ran and answered.
    /// CHG: only a valid answer now earns a receipt; failed, timed-out and
    /// excessive-output probes refuse using the shared installer reader.
    /// </summary>
    static JsonDocument ProbeSourceVersion(string publishedPath)
    {
        var outcome = BoundedProbe.Run(publishedPath, ["version", "--json"],
            IdentityProbe.TimeoutMs, IdentityProbe.MaxOutputBytes, null, null);
        if (outcome is not BoundedProbe.Outcome.Completed completed)
            throw new IOException(outcome switch
            {
                BoundedProbe.Outcome.TimedOut => $"version --json exceeded {IdentityProbe.TimeoutMs} ms",
                BoundedProbe.Outcome.UnboundedOutput => $"version --json exceeded the {IdentityProbe.MaxOutputBytes}-byte output limit",
                _ => $"version --json could not start: {outcome}",
            });
        if (completed.ExitCode != 0)
            throw new IOException($"version --json exited {completed.ExitCode}: {FirstLine(completed.Stderr)}");
        try
        {
            var report = JsonDocument.Parse(completed.Stdout);
            if (report.RootElement.ValueKind != JsonValueKind.Object)
            {
                report.Dispose();
                throw new IOException("version --json must return a JSON object");
            }
            return report;
        }
        catch (JsonException error)
        {
            throw new IOException("version --json did not print valid JSON: " + error.Message, error);
        }
    }

    static bool IsHex(string value, int length) =>
        value.Length == length && value.All(character => char.IsAsciiHexDigit(character));

    static string Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    static string FirstLine(string value)
    {
        var trimmed = value.Trim();
        var newline = trimmed.IndexOf('\n');
        return newline < 0 ? trimmed : trimmed[..newline];
    }
}
