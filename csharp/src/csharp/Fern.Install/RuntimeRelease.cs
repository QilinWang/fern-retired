using Fern.Registry;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Fern.Install;

/// <summary>One installed binary as a release names it.</summary>
public sealed record RuntimeReleaseMember(
    [property: JsonPropertyName("member")] string Member,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("byte_size")] long ByteSize,
    [property: JsonPropertyName("input_digest")] string? InputDigest,
    [property: JsonPropertyName("asset")] string Asset);

/// <summary>
/// What one published runtime is: a complete installed binary set, named by
/// the set identity the installer minted, with the bytes of each member
/// addressed by content.
/// </summary>
public sealed record RuntimeReleaseManifest(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("binary_set_id")] string BinarySetId,
    [property: JsonPropertyName("source_revision")] string SourceRevision,
    [property: JsonPropertyName("built_from_commit")] string BuiltFromCommit,
    [property: JsonPropertyName("published_at")] string PublishedAt,
    [property: JsonPropertyName("publisher")] string Publisher,
    [property: JsonPropertyName("members")] IReadOnlyList<RuntimeReleaseMember> Members);

/// <summary>
/// A machine downloads its runtime instead of being pushed one.
///
/// CHG 2026-09-12 (owner's intent
/// program-11-fern-result-experience.machines-download-the-runtime): publishing
/// 45 MB of fern-run to a rented machine meant uploading it over SSH from a
/// home connection, and a Pod restart destroyed the installed copy. The
/// machines measured 22.6 MB/s (Pod) and 4.9 MB/s (AutoDL) pulling a GitHub
/// release asset, so they fetch far faster than they can be pushed to.
///
/// The shape is one long-lived release acting as a content-addressed store:
/// every asset is named <c>&lt;member&gt;.&lt;sha256&gt;</c>, so publishing a set
/// whose fern-site did not change uploads nothing for fern-site, and a
/// manifest asset <c>runtime-set.&lt;set&gt;.json</c> says which digests form
/// that set. <c>runtime-set.latest.json</c> is replaced on each publish and is
/// what a machine asks for when it does not name a set. This is the same
/// content addressing the deployment root uses, lifted to a place a rented
/// machine can read.
///
/// Installing is the ordinary installer: the assets are downloaded, verified
/// against the digests in their own names, and handed to
/// <see cref="ControlPlaneInstaller"/>, which probes them, publishes them
/// content-addressed, writes the installation receipt and activates the set.
/// A downloaded set is therefore indistinguishable from a locally built one,
/// which is the point: <c>select-runtime</c>, the verifier and the Site row
/// read exactly what they read today.
/// </summary>
public static class RuntimeRelease
{
    public const string ManifestSchema = "fern.runtime-release.v2";

    /// <summary>
    /// The manifest shapes an install accepts. CHG 2026-09-12: v1 carried
    /// `commit_present_in_repository`, a check against the repository holding
    /// the release. It could only ever be false and read as if the repository
    /// were missing: the mirror there is a CODE-ONLY REBUILD with its own
    /// commit ids, because four blobs over GitHub's 100 MB limit sit in this
    /// history and the real one cannot be pushed at all
    /// (docs/how-to/github-backup.md). The binaries' provenance is
    /// `built_from_commit` with the archive digest the binary itself reports,
    /// which is a fact about this repository, not about the mirror.
    /// </summary>
    public static readonly IReadOnlyList<string> ReadableManifestSchemas =
    [
        "fern.runtime-release.v1",
        "fern.runtime-release.v2",
    ];
    public const string DefaultRepository = "QilinWang/fern-private";
    public const string DefaultTag = "runtime";
    public const string LatestManifest = "runtime-set.latest.json";

    /// <summary>
    /// The one request a machine with nothing installed makes: fetch fern-run
    /// by asset id, with the token it holds. Every other binary comes from
    /// <c>install-runtime-release</c> afterwards, so this is the whole
    /// bootstrap and there is no JSON to read on that machine.
    /// </summary>
    public static string BootstrapCommand(string repository, long runAssetId, string tokenPath, string destination) =>
        $"curl -fsSL -H \"Authorization: Bearer $(cat {tokenPath})\" -H \"Accept: application/octet-stream\" "
        + $"https://api.github.com/repos/{repository}/releases/assets/{runAssetId} -o {destination} && chmod +x {destination}";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string ManifestAsset(string binarySetId) =>
        "runtime-set." + ShortSet(binarySetId) + ".json";

    /// <summary>The twelve hex characters of a set id that name its manifest and read back to a person.</summary>
    public static string ShortSet(string binarySetId)
    {
        var hex = binarySetId.StartsWith("sha256:", StringComparison.Ordinal) ? binarySetId[7..] : binarySetId;
        if (hex.Length < 12 || !hex.All(char.IsAsciiHexDigit))
        {
            throw new InvalidOperationException($"binary set id {binarySetId} is not a sha256 identity");
        }
        return hex[..12];
    }

    public sealed record PublishRequest(
        string DeploymentRoot,
        string Repository,
        string Tag,
        bool DryRun);

    /// <param name="RunAssetId">
    /// The id of this set's fern-run asset, which is the one thing a bare
    /// machine needs before it can run anything: with it the machine fetches
    /// the binary with one request and no JSON to read.
    /// </param>
    public sealed record PublishReport(
        RuntimeReleaseManifest Manifest,
        IReadOnlyList<string> Uploaded,
        IReadOnlyList<string> Unchanged,
        string ReleaseUrl,
        long RunAssetId);

    /// <summary>
    /// Publish the deployment root's active binary set. Every member is read
    /// from the build inventory, which is the authority for what is installed
    /// here, and verified against its recorded digest before it is uploaded.
    /// </summary>
    public static PublishReport Publish(GitHubReleases github, PublishRequest request)
    {
        var deploymentRoot = Path.GetFullPath(request.DeploymentRoot);
        var inventoryPath = Path.Combine(deploymentRoot, BuildInventory.FileName);
        if (!File.Exists(inventoryPath))
        {
            throw new InvalidOperationException(
                $"no build inventory at {inventoryPath}; install-control-plane writes it, and only an installed set can be published");
        }

        var members = new List<(ControlMember Member, ActiveBinaryMember Active)>();
        using (var inventory = BuildInventory.Open(inventoryPath))
        {
            foreach (var member in ControlMembers.InstalledSet)
            {
                var resolution = inventory.ResolveActive(member.Name);
                members.Add((member, resolution switch
                {
                    Refused refused => throw new InvalidOperationException($"active binary set: {refused.Detail}"),
                    Accepted<ActiveBinaryMember> accepted => accepted.Value,
                    _ => throw new InvalidOperationException($"active binary set: {member.Name} resolved to nothing"),
                }));
            }
        }

        var setId = members[0].Active.SetId;
        if (members.Any(entry => entry.Active.SetId != setId))
        {
            throw new InvalidOperationException("the build inventory's active members disagree about their set identity");
        }
        var sourceRevision = members[0].Active.SourceRevision;
        var commit = sourceRevision.Split('+')[0];

        var manifestMembers = members.Select(entry => new RuntimeReleaseMember(
            entry.Member.Name,
            entry.Active.Sha256,
            new FileInfo(entry.Active.Path).Length,
            InputDigestOf(entry.Active.Path),
            entry.Member.Name + "." + entry.Active.Sha256)).ToList();

        var manifest = new RuntimeReleaseManifest(
            ManifestSchema,
            setId,
            sourceRevision,
            commit,
            PublishedAt: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Publisher: "fern-run publish-runtime-release",
            Members: manifestMembers);

        var uploaded = new List<string>();
        var unchanged = new List<string>();
        if (request.DryRun)
        {
            foreach (var member in manifestMembers) uploaded.Add(member.Asset);
            return new PublishReport(manifest, uploaded, unchanged, $"(dry run) {request.Repository} release {request.Tag}", RunAssetId: 0);
        }

        var release = github.Release(request.Repository, request.Tag) ?? CreateRelease(github, request);
        var releaseId = release["id"]!.GetValue<long>();
        var assets = GitHubReleases.Assets(release);

        foreach (var (member, active) in members)
        {
            var assetName = member.Name + "." + active.Sha256;
            var size = new FileInfo(active.Path).Length;
            if (assets.TryGetValue(assetName, out var present) && present.Size == size)
            {
                // Content addressed: the same name is the same bytes.
                unchanged.Add(assetName);
                continue;
            }
            if (assets.ContainsKey(assetName))
            {
                github.DeleteAsset(request.Repository, assets[assetName].Id);
            }
            github.UploadAsset(request.Repository, releaseId, assetName, active.Path, "application/octet-stream");
            uploaded.Add(assetName);
        }

        var document = JsonSerializer.Serialize(manifest, Json) + "\n";
        foreach (var name in new[] { ManifestAsset(setId), LatestManifest })
        {
            if (assets.TryGetValue(name, out var existing))
            {
                github.DeleteAsset(request.Repository, existing.Id);
            }
            var staging = Path.Combine(Path.GetTempPath(), $"fern-manifest-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(staging, document);
                github.UploadAsset(request.Repository, releaseId, name, staging, "application/json");
                uploaded.Add(name);
            }
            finally
            {
                if (File.Exists(staging)) File.Delete(staging);
            }
        }

        // Read the release once more: the asset ids of what was just uploaded
        // are what a bare machine fetches fern-run by.
        var published = github.Release(request.Repository, request.Tag) ?? release;
        var runAsset = GitHubReleases.Assets(published)
            .TryGetValue(manifestMembers.Single(member => member.Member == "fern-run").Asset, out var run) ? run.Id : 0;
        return new PublishReport(
            manifest, uploaded, unchanged,
            published["html_url"]?.GetValue<string>() ?? request.Tag,
            runAsset);
    }

    static JsonObject CreateRelease(GitHubReleases github, PublishRequest request)
    {
        // A release needs a commit the repository holds, and this mirror's
        // history is a filtered rebuild with its own ids, so the tag names its
        // default branch and claims nothing about the source. The manifest
        // names the commit the binaries were built from, in the working
        // repository, which is where that identity means something.
        var target = github.BranchHead(request.Repository, "main")
            ?? throw new InvalidOperationException($"{request.Repository} has no main branch to tag a release against");
        return github.CreateRelease(
            request.Repository,
            request.Tag,
            target,
            "Fern control-plane runtime",
            "Content-addressed control binaries: every asset is <member>.<sha256>, and runtime-set.<set>.json "
            + "names the members of one installed binary set. A machine installs one with "
            + "`fern-run install-runtime-release`.\n\n"
            + "The tag names this repository's main branch because a release needs a commit this repository "
            + "holds. It is not the commit the binaries were built from, and it cannot be: the code here is a "
            + "filtered mirror with its own commit ids (four blobs over GitHub's 100 MB limit are in the real "
            + "history, so it cannot be pushed; see docs/how-to/github-backup.md). Each manifest's "
            + "built_from_commit names the commit in the working repository, and every binary reports the same "
            + "commit and archive digest from `version --json`.");
    }

    static string? InputDigestOf(string publishedPath)
    {
        var receipt = ControlMemberPublications.ReceiptPath(publishedPath);
        if (!File.Exists(receipt)) return null;
        try
        {
            return JsonSerializer.Deserialize<ControlMemberPublishReceipt>(File.ReadAllBytes(receipt))?.InputDigest;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public sealed record InstallRequest(
        string Repository,
        string Tag,
        string? BinarySetId,
        string Registry,
        string WorkRoot,
        string DeploymentRoot,
        string DeployRoot,
        string ProfilesRoot,
        string SshHelper);

    public sealed record InstallReport(
        RuntimeReleaseManifest Manifest,
        Installation Installation,
        long DownloadedBytes,
        double DownloadSeconds);

    /// <summary>
    /// Fetch a published set and install it here. The digests are checked
    /// against the manifest before anything is published into the deployment
    /// root, and the installation itself is the ordinary one, so what this
    /// leaves behind is a receipt, an activated set and per-member receipts
    /// that a later local build can reuse.
    /// </summary>
    public static InstallReport Install(GitHubReleases github, InstallRequest request)
    {
        var release = github.Release(request.Repository, request.Tag)
            ?? throw new InvalidOperationException($"{request.Repository} has no release tagged {request.Tag}");
        var assets = GitHubReleases.Assets(release);
        var manifestName = request.BinarySetId is { Length: > 0 } named ? ManifestAsset(named) : LatestManifest;
        if (!assets.TryGetValue(manifestName, out var manifestAsset))
        {
            throw new InvalidOperationException(
                $"release {request.Tag} of {request.Repository} has no {manifestName}; it holds "
                + string.Join(", ", assets.Keys.Where(name => name.StartsWith("runtime-set.", StringComparison.Ordinal)).Order(StringComparer.Ordinal)));
        }
        var manifest = JsonSerializer.Deserialize<RuntimeReleaseManifest>(github.ReadAssetText(request.Repository, manifestAsset.Id))
            ?? throw new InvalidOperationException($"{manifestName} is not a runtime manifest");
        if (!ReadableManifestSchemas.Contains(manifest.Schema))
        {
            throw new InvalidOperationException(
                $"{manifestName} is schema \"{manifest.Schema}\", and this binary reads "
                + string.Join(" or ", ReadableManifestSchemas));
        }

        // CHG: verify the complete declaration before publication and give each
        // download invocation its own mutable files, even for the same set.
        var declaredSet = BuildInventory.SetIdFor(manifest.Members.Select(member =>
            new BinarySetMember(member.Member, member.Sha256, member.Asset)).ToList()) switch
        {
            Refused refused => throw new InvalidOperationException(refused.Detail),
            Accepted<string> accepted => accepted.Value,
        };
        if (declaredSet != manifest.BinarySetId)
            throw new InvalidOperationException($"the manifest members identify {declaredSet}, not {manifest.BinarySetId}");
        if (manifest.Members.Any(member => member.Member is not ("fern-site" or "fern-stage-runner" or "fern-experiment" or "fern-run" or "fern-compile")))
            throw new InvalidOperationException("the release names an unknown runtime member");
        var downloads = Path.Combine(Path.GetFullPath(request.DeploymentRoot), "runtime-release-downloads", ShortSet(manifest.BinarySetId) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloads);
        var downloadedBytes = 0L;
        var started = DateTimeOffset.UtcNow;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var member in manifest.Members)
            {
                if (!assets.TryGetValue(member.Asset, out var asset))
                {
                    throw new InvalidOperationException($"release {request.Tag} has no asset {member.Asset}, which {manifestName} names");
                }
                var destination = Path.Combine(downloads, member.Member);
                var size = github.DownloadAsset(request.Repository, asset.Id, destination);
                downloadedBytes += size;
                if (size != member.ByteSize)
                {
                    throw new InvalidOperationException(
                        $"{member.Asset} arrived as {size} bytes, and the manifest declares {member.ByteSize}");
                }
                var digest = DurableFiles.FileSha256(destination);
                if (!string.Equals(digest, member.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"{member.Asset} arrived with SHA-256 {digest}, and the manifest declares {member.Sha256}");
                }
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(destination,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                files[member.Member] = destination;
            }
            var downloadSeconds = (DateTimeOffset.UtcNow - started).TotalSeconds;

            var config = new InstallConfig
            {
                ExpectedBinarySetId = manifest.BinarySetId,
                SourceSiteBinary = Required(files, "fern-site"),
                SourceExperimentBinary = Required(files, "fern-experiment"),
                SourceRunBinary = Required(files, "fern-run"),
                SourceCompileBinary = Required(files, "fern-compile"),
                SourceRevision = manifest.SourceRevision,
                BuildInventory = Path.Combine(Path.GetFullPath(request.DeploymentRoot), BuildInventory.FileName),
                Registry = request.Registry,
                WorkRoot = request.WorkRoot,
                DeployRoot = request.DeployRoot,
                ProfilesRoot = request.ProfilesRoot,
                SshHelper = request.SshHelper,
                DeploymentRoot = request.DeploymentRoot,
                MemberInputs = manifest.Members
                    .Where(member => member.InputDigest is { Length: > 0 })
                    .ToDictionary(member => member.Member, member => member.InputDigest!, StringComparer.Ordinal),
            };
            var locked = InstallLock.Acquire(request.DeploymentRoot, "install downloaded runtime " + manifest.BinarySetId);
            if (!locked.IsOk) throw new InvalidOperationException(locked.Refusal);
            using var held = locked.Value;
            var installed = ControlPlaneInstaller.Install(config) switch
            {
                Refused refused => throw new InvalidOperationException(refused.Detail),
                Accepted<Installation> accepted => accepted.Value,
            };

            var installedSet = installed.BinarySetId;
            if (!string.Equals(installedSet, manifest.BinarySetId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"the installed set is {installedSet} and the manifest published {manifest.BinarySetId}");
            }
            WriteMemberReceipts(manifest, installed);
            return new InstallReport(manifest, installed, downloadedBytes, downloadSeconds);
        }
        finally
        {
            if (Directory.Exists(downloads)) Directory.Delete(downloads, recursive: true);
        }
    }

    /// <summary>
    /// Give each published copy the receipt its bytes earned when they were
    /// built, so a machine that later builds from source reuses them instead
    /// of rebuilding what it just downloaded.
    /// </summary>
    static void WriteMemberReceipts(RuntimeReleaseManifest manifest, Installation installed)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fern-site"] = installed.SiteBinaryPath,
            ["fern-experiment"] = installed.ExperimentBinaryPath,
            ["fern-run"] = installed.RunBinaryPath,
            ["fern-compile"] = installed.CompileBinaryPath,
        };
        foreach (var member in manifest.Members)
        {
            if (member.InputDigest is not { Length: > 0 } digest) continue;
            if (!paths.TryGetValue(member.Member, out var path) || !File.Exists(path)) continue;
            var receiptPath = ControlMemberPublications.ReceiptPath(path);
            if (File.Exists(receiptPath)) continue;
            var receipt = new ControlMemberPublishReceipt(
                ControlMemberPublications.ReceiptSchema,
                member.Member,
                member.Sha256,
                path,
                member.ByteSize,
                digest,
                manifest.BuiltFromCommit,
                BuiltFromDirtyTree: false,
                SourceArchiveSha256: ArchiveOf(manifest.SourceRevision),
                Recipe: string.Join(' ', ControlMembers.Recipe(ControlMembers.Named(member.Member))),
                Toolchain: "published runtime release",
                PublishedAt: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Publisher: "fern-run install-runtime-release");
            var staging = receiptPath + ".publish-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(staging, JsonSerializer.Serialize(receipt, Json) + "\n");
                File.Move(staging, receiptPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(receiptPath))
            {
                // Another install wrote it first; theirs stands.
            }
            finally
            {
                if (File.Exists(staging)) File.Delete(staging);
            }
        }
    }

    static string? ArchiveOf(string sourceRevision)
    {
        const string marker = "+archive:";
        var at = sourceRevision.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? null : sourceRevision[(at + marker.Length)..];
    }

    static string Required(IReadOnlyDictionary<string, string> files, string member) =>
        files.TryGetValue(member, out var path)
            ? path
            : throw new InvalidOperationException($"the published set has no {member}");
}
