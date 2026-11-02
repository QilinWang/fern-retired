using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Fern.Scheduler;

/// <summary>
/// What under <c>deploy/</c> is still selected, and by whom.
///
/// Every family there is content-addressed: a 64-hex digest in the file or
/// directory name. The retention rule
/// (docs/design/decisions/2026-08-12-binary-identity-and-deploy.md, section E)
/// is that a digest is collectable only when nothing durable names it —
/// never by age, never by a /proc scan. This inventory consults exactly the
/// durable places a digest can be named from:
///
///   1. a Site supervisor's binary while its office row is open (Registry Home; CHG 2026-09-11 night, slice 8: was a running Plan attempt's);
///   2. the installation receipt of a live systemd unit
///      (deploy/scheduler-services, deploy/knowledge-reconcile-services),
///      recognized by the unit file's bytes, not by the receipt's age;
///   3. the Registry binary work.toml selects;
///   4. an execution profile's remote source_root digest;
///   5. deploy/deployments.jsonl: the remote worker binary and the source
///      archive each deployment shipped;
///   6. the Registry's registered source contents and implementations (the
///      CAS), and the blobs their manifests declare.
///
/// A materialized source root is a rebuildable copy of a registered content.
/// It is kept while any Plan attempt is running (a Site may hold it open);
/// otherwise it is removable, and its row says how many files it carries
/// beyond its declaration — an installer build wrote obj/ and bin/ into one
/// on 2026-09-08, which the frozen-root audit must name.
///
/// The inventory reads; it never removes. Removal is the caller's, after a
/// fresh inspection, and only of paths this inventory called removable.
/// </summary>
public static class DeployInventory
{
    public sealed record Item(string Path, long Bytes, string Decision, string Reason);

    /// <param name="DeployRoot">the deploy/ directory to inventory</param>
    /// <param name="ProfilesRoot">the profiles/ directory whose JSON files name remote source roots</param>
    /// <param name="WorkToml">the work.toml whose <c>[registry] facts</c> selects a Registry binary; null when none</param>
    /// <param name="SystemdUserDirectory">where live unit files sit (normally ~/.config/systemd/user)</param>
    public sealed record Request(string DeployRoot, string ProfilesRoot, string? WorkToml, string SystemdUserDirectory);

    public const string Removable = "removable";
    public const string Keep = "keep";
    public const string NothingNamesIt =
        "no open Site supervisor, live installation receipt, profile, work.toml or deployment record names this digest";

    static readonly Regex Digest = new("[0-9a-f]{64}", RegexOptions.Compiled);

    // CHG 2026-09-26 (docs/programs/2026-09-26-source-roots-stop-being-full-copies,
    // slice 4): study-source-roots joins them. Installed sources are retired and
    // nothing names those trees; a running attempt still keeps them (below).
    static readonly string[] MaterializedRootFamilies =
        ["source-content-roots", "stage-source-roots", "implementation-source-roots", "study-source-roots"];

    public static IReadOnlyList<Item> Inspect(SqliteConnection registry, Request request)
    {
        var deploy = Path.GetFullPath(request.DeployRoot);
        if (!Directory.Exists(deploy)) throw new DeployInventoryRefusal($"deploy root does not exist: {deploy}");

        var selected = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Select(string digest, string reason)
        {
            if (!selected.TryGetValue(digest, out var reasons)) selected[digest] = reasons = [];
            if (!reasons.Contains(reason)) reasons.Add(reason);
        }

        var runningAttempts = RunningAttemptSupervisors(registry);
        foreach (var (attempt, digest) in runningAttempts)
        {
            // CHG 2026-09-11 night (slice 8): was "running Plan attempt {attempt} execs this supervisor at its next stage boundary".
            if (digest is not null) Select(digest, $"open Site {attempt} runs this binary until its office ends");
        }
        foreach (var (receipt, unit, digests) in LiveInstallationReceipts(deploy, request.SystemdUserDirectory))
        {
            foreach (var digest in digests) Select(digest, $"installation receipt {receipt} of live unit {unit}");
        }
        if (request.WorkToml is not null && WorkTomlRegistryBinary(request.WorkToml) is { } workBinary)
        {
            Select(workBinary, "work.toml selects this Registry binary");
        }
        foreach (var (profile, digest) in ProfileSourceRoots(request.ProfilesRoot))
        {
            Select(digest, $"profile {profile} names this source archive as its remote source_root");
        }
        foreach (var (record, digest) in DeploymentRecords(Path.Combine(deploy, "deployments.jsonl")))
        {
            Select(digest, $"deployment record {record}");
        }
        // CHG 2026-09-13, the owner's cut: the frozen source's catalogue left the
        // Home, so nothing registers a source content or an implementation any
        // more. The stores under the deploy root that those rows used to name --
        // source-contents, source-blobs, implementations, and the roots
        // materialized from them -- are therefore named by nothing, which is
        // what this inventory exists to report. A view a running attempt is
        // training from is still protected below, by the attempt rather than by
        // a row. retired/csharp/frozen-source-2026-09-13/.
        IReadOnlyCollection<string> contents = [];
        IReadOnlyCollection<string> implementations = [];
        var declaredBlobs = new HashSet<string>(StringComparer.Ordinal);
        var declaredFiles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var hex in contents)
        {
            var manifest = Path.Combine(deploy, "source-contents", hex, "manifest.json");
            if (!File.Exists(manifest)) continue;
            var files = new HashSet<string>(StringComparer.Ordinal);
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifest));
            if (document.RootElement.TryGetProperty("files", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String) files.Add(path.GetString()!);
                    if (entry.TryGetProperty("byte_digest", out var digest) && digest.ValueKind == JsonValueKind.String
                        && Digest.Match(digest.GetString()!) is { Success: true } blob)
                    {
                        declaredBlobs.Add(blob.Value);
                    }
                }
            }
            declaredFiles[hex] = files;
        }

        var items = new List<Item>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(deploy).OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                if (name == "binaries" || name.EndsWith("-binaries", StringComparison.Ordinal))
                {
                    items.AddRange(BinaryFamily(entry, selected));
                }
                else if (MaterializedRootFamilies.Contains(name))
                {
                    items.AddRange(MaterializedRoots(entry, selected, runningAttempts.Count > 0, declaredFiles));
                }
                else if (name is "source-contents" or "implementations")
                {
                    items.AddRange(RegisteredDirectories(entry, selected));
                }
                else if (name == "source-blobs")
                {
                    items.AddRange(Blobs(entry, declaredBlobs));
                }
                else if (name == "implementation-snapshots")
                {
                    items.Add(new Item(entry, DirectoryBytes(entry), Keep, "V1 snapshot store; audited and reclaimed by `fern-run map-legacy-source-snapshots`"));
                }
                else
                {
                    items.Add(new Item(entry, DirectoryBytes(entry), Keep, "records and receipts; not content-addressed, not inventoried"));
                }
            }
            else if (name.EndsWith(".tgz", StringComparison.Ordinal))
            {
                items.Add(Archive(entry, selected));
            }
            else
            {
                items.Add(new Item(entry, new FileInfo(entry).Length, Keep, "record; not content-addressed"));
            }
        }
        return items;
    }

    static IEnumerable<Item> BinaryFamily(string family, Dictionary<string, List<string>> selected)
    {
        foreach (var file in Directory.EnumerateFiles(family).OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            var digest = Digest.Match(name);
            var bytes = new FileInfo(file).Length;
            if (!digest.Success)
            {
                yield return new Item(file, bytes, Keep, "not a content-addressed name; not inventoried");
            }
            else if (selected.TryGetValue(digest.Value, out var reasons))
            {
                yield return new Item(file, bytes, Keep, string.Join("; ", reasons));
            }
            else
            {
                yield return new Item(file, bytes, Removable, NothingNamesIt);
            }
        }
    }

    static IEnumerable<Item> MaterializedRoots(string family, Dictionary<string, List<string>> selected, bool anyRunning, Dictionary<string, HashSet<string>> declaredFiles)
    {
        foreach (var root in Directory.EnumerateDirectories(family).OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(root);
            var digest = Digest.Match(name);
            var bytes = DirectoryBytes(root);
            if (!digest.Success)
            {
                yield return new Item(root, bytes, Keep, "not a content-addressed name; not inventoried");
                continue;
            }
            var registered = selected.TryGetValue(digest.Value, out var reasons) && reasons.Any(r => r.StartsWith("registered ", StringComparison.Ordinal));
            var undeclared = declaredFiles.TryGetValue(digest.Value, out var declared) ? UndeclaredFileCount(root, declared) : (int?)null;
            var carried = undeclared is > 0 ? $"; carries {undeclared} file(s) beyond its declaration (a build wrote into this frozen root)" : "";
            if (!registered)
            {
                yield return new Item(root, bytes, anyRunning ? Keep : Removable,
                    (anyRunning ? "a Plan attempt is running and may hold this root open; " : "") + "materialized root of no registered source content" + carried);
            }
            else if (anyRunning)
            {
                yield return new Item(root, bytes, Keep, "a Plan attempt is running and may hold this materialized root open" + carried);
            }
            else
            {
                yield return new Item(root, bytes, Removable, "materialized copy of a registered source content; rebuilt from source-blobs on demand" + carried);
            }
        }
    }

    static IEnumerable<Item> RegisteredDirectories(string family, Dictionary<string, List<string>> selected)
    {
        foreach (var directory in Directory.EnumerateDirectories(family).OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var digest = Digest.Match(Path.GetFileName(directory));
            var bytes = DirectoryBytes(directory);
            if (digest.Success && selected.TryGetValue(digest.Value, out var reasons))
            {
                yield return new Item(directory, bytes, Keep, string.Join("; ", reasons));
            }
            else
            {
                yield return new Item(directory, bytes, Removable, "not registered in Registry Home");
            }
        }
    }

    static IEnumerable<Item> Blobs(string family, HashSet<string> declaredBlobs)
    {
        long keptBytes = 0, removableBytes = 0;
        var kept = 0;
        var removable = new List<Item>();
        foreach (var file in Directory.EnumerateFiles(family, "*", SearchOption.AllDirectories))
        {
            var digest = Digest.Match(Path.GetFileName(file));
            var bytes = new FileInfo(file).Length;
            if (digest.Success && declaredBlobs.Contains(digest.Value))
            {
                kept++;
                keptBytes += bytes;
            }
            else
            {
                removableBytes += bytes;
                removable.Add(new Item(file, bytes, Removable, "no registered source content declares this blob"));
            }
        }
        yield return new Item(family, keptBytes, Keep, $"{kept} blob(s) declared by registered source contents");
        foreach (var item in removable.OrderBy(item => item.Path, StringComparer.Ordinal)) yield return item;
    }

    static Item Archive(string file, Dictionary<string, List<string>> selected)
    {
        var bytes = new FileInfo(file).Length;
        var digest = Digest.Match(Path.GetFileName(file));
        if (!digest.Success)
        {
            return new Item(file, bytes, Keep, "not content-addressed; deploy/README.md keeps it as the Pod build record");
        }
        return selected.TryGetValue(digest.Value, out var reasons)
            ? new Item(file, bytes, Keep, string.Join("; ", reasons))
            : new Item(file, bytes, Removable, NothingNamesIt);
    }

    static int UndeclaredFileCount(string root, HashSet<string> declared)
    {
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (!declared.Contains(relative)) count++;
        }
        return count;
    }

    static List<(string Attempt, string? Digest)> RunningAttemptSupervisors(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // CHG 2026-09-11 night (slice 8): the process holding a binary set was the
        // Plan attempt's supervisor; it is the Site's supervisor now, whose office
        // row names its binary until it ends.
        command.CommandText = "SELECT id, binary_sha256 FROM supervisors WHERE ended_at IS NULL ORDER BY id";
        using var rows = command.ExecuteReader();
        var attempts = new List<(string, string?)>();
        while (rows.Read())
        {
            var digest = rows.IsDBNull(1) ? null : Digest.Match(rows.GetString(1)) is { Success: true } match ? match.Value : null;
            attempts.Add(("supervisor:" + rows.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture), digest));
        }
        return attempts;
    }

    static HashSet<string> RegisteredIds(SqliteConnection connection, string sql, string prefix)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var rows = command.ExecuteReader();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (rows.Read())
        {
            var id = rows.GetString(0);
            var hex = id.StartsWith(prefix, StringComparison.Ordinal) ? id[prefix.Length..] : id;
            if (Digest.IsMatch(hex)) ids.Add(hex);
        }
        return ids;
    }

    /// <summary>
    /// An installation receipt is live when the unit file it names exists with
    /// the bytes it recorded. Every install of the same service name left a
    /// receipt beside the live one; only the matching one selects binaries.
    /// </summary>
    static IEnumerable<(string Receipt, string Unit, IReadOnlyList<string> Digests)> LiveInstallationReceipts(string deploy, string systemdUserDirectory)
    {
        foreach (var family in new[] { "scheduler-services", "knowledge-reconcile-services" })
        {
            var directory = Path.Combine(deploy, family);
            if (!Directory.Exists(directory)) continue;
            foreach (var receipt in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(receipt));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var unitPath = StringProperty(root, "unit_path") ?? StringProperty(root, "service_path");
                var unitDigest = StringProperty(root, "unit_sha256") ?? StringProperty(root, "service_sha256");
                if (unitPath is null || unitDigest is null) continue;
                var unit = Path.Combine(systemdUserDirectory, Path.GetFileName(unitPath));
                if (!File.Exists(unit)) continue;
                var expected = Digest.Match(unitDigest);
                if (!expected.Success) continue;
                string actual;
                using (var stream = File.OpenRead(unit)) actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (actual != expected.Value) continue;
                var digests = new List<string>();
                foreach (var property in root.EnumerateObject())
                {
                    if (!property.Name.EndsWith("_binary_sha256", StringComparison.Ordinal) || property.Value.ValueKind != JsonValueKind.String) continue;
                    var digest = Digest.Match(property.Value.GetString()!);
                    if (digest.Success) digests.Add(digest.Value);
                }
                yield return (Path.GetRelativePath(deploy, receipt), Path.GetFileName(unit), digests);
            }
        }
    }

    /// <summary>work.toml's <c>[registry] facts = ["&lt;binary&gt;", ...]</c>: the first element is the selected Registry binary.</summary>
    static string? WorkTomlRegistryBinary(string workToml)
    {
        if (!File.Exists(workToml)) return null;
        var match = Regex.Match(File.ReadAllText(workToml), "^\\s*facts\\s*=\\s*\\[\\s*\"([^\"]+)\"", RegexOptions.Multiline);
        if (!match.Success) return null;
        var digest = Digest.Match(Path.GetFileName(match.Groups[1].Value));
        return digest.Success ? digest.Value : null;
    }

    static IEnumerable<(string Profile, string Digest)> ProfileSourceRoots(string profilesRoot)
    {
        if (!Directory.Exists(profilesRoot)) yield break;
        foreach (var file in Directory.EnumerateFiles(profilesRoot, "*.json").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
            var sourceRoot = StringProperty(document.RootElement, "source_root");
            if (sourceRoot is null) continue;
            var digest = Digest.Match(sourceRoot.TrimEnd('/').Split('/')[^1]);
            if (digest.Success) yield return (Path.GetFileNameWithoutExtension(file), digest.Value);
        }
    }

    static IEnumerable<(string Record, string Digest)> DeploymentRecords(string index)
    {
        if (!File.Exists(index)) yield break;
        foreach (var line in File.ReadLines(index))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) continue;
            var profile = StringProperty(root, "profile_id") ?? "?";
            var when = root.TryGetProperty("deployed_at_unix_ms", out var at) && at.TryGetInt64(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd")
                : "?";
            foreach (var key in new[] { "binary_sha256", "source_archive_sha256" })
            {
                var value = StringProperty(root, key);
                if (value is not null && Digest.Match(value) is { Success: true } digest)
                {
                    yield return ($"{when} {profile} {key}", digest.Value);
                }
            }
        }
    }

    static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static long DirectoryBytes(string path)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.LinkTarget is null) total += info.Length;  // a symlink's bytes belong to its target
        }
        return total;
    }
}

public sealed class DeployInventoryRefusal(string message) : Exception(message);
