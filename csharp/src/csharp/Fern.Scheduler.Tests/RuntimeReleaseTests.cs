using System.Text.Json;
using Fern.Registry;
using Xunit;
using Fern.Install;

namespace Fern.Scheduler.Tests;

/// <summary>
/// A machine downloads its runtime instead of being pushed one (owner's intent
/// program-11-fern-result-experience.machines-download-the-runtime, 2026-09-12).
///
/// The claims falsified here are the ones that do not need the network: a
/// published set is named by content, so the asset a machine asks for is the
/// digest it must receive; the bootstrap is one request and no JSON on the
/// machine; and a machine that pulls its work installs the same set with no
/// execution profiles, no SSH helper and a Home it names by address, leaving a
/// receipt the ordinary verifier accepts. What the network does — the upload,
/// the download, the digests matching over the wire — was run against the real
/// private repository on 2026-09-12 and is recorded in the commit.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class RuntimeReleaseTests
{
    [Fact]
    public void WrongCompleteSetIdentityLeavesThePreviousActivationSelected()
    {
        var root = Directory.CreateTempSubdirectory("fern-release-refusal-").FullName;
        var registry = TestRegistry.CreateRegistry();
        try
        {
            var stores = ProbeFixtures.WriteControlStores(Path.Combine(root, "stores"));
            var config = InstallFixtures.ConfigFor(root, registry, stores.DeployRoot, stores.ProfilesRoot, stores.SshHelper);
            var prior = UnionCase.Of<Accepted<Installation>>(ControlPlaneInstaller.Install(config)).Value;
            var changed = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(config))!;
            changed["source_run_binary"] = ProbeFixtures.WriteProbe("# another valid build\nexit 0");
            changed["expected_binary_set_id"] = "sha256:" + new string('a', 64);
            _ = UnionCase.Of<Refused>(ControlPlaneInstaller.Install(changed.Deserialize<InstallConfig>()!));
            using var inventory = BuildInventory.Open(config.BuildInventory);
            var active = UnionCase.Of<Accepted<ActiveBinaryMember>>(inventory.ResolveActive("fern-run")).Value;
            Assert.Equal(prior.RunBinaryPath, active.Path);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(Path.GetDirectoryName(registry)!, true);
        }
    }

    sealed class OverlappingDownloads(RuntimeReleaseManifest manifest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) =>
            Task.FromResult(Send(request, cancellation));
        public ManualResetEventSlim First { get; } = new();
        public ManualResetEventSlim Second { get; } = new();
        public ManualResetEventSlim ReleaseFirst { get; } = new();
        public ManualResetEventSlim ReleaseSecond { get; } = new();
        int downloads;
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellation)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/tags/", StringComparison.Ordinal))
                return Json(new { assets = new[] {
                    new { name = RuntimeRelease.LatestManifest, id = 1, size = 1 },
                    new { name = manifest.Members[0].Asset, id = 2, size = 1 } } });
            if (path.EndsWith("/1", StringComparison.Ordinal)) return Json(manifest);
            var first = Interlocked.Increment(ref downloads) == 1;
            (first ? First : Second).Set();
            if (!(first ? ReleaseFirst : ReleaseSecond).Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            throw new IOException("download failed after overlapping preparation");
        }
        static HttpResponseMessage Json(object value) => new(System.Net.HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }

    [Fact]
    public async Task OneFailedPullCannotDeleteAnotherPullsDownloads()
    {
        var root = Directory.CreateTempSubdirectory("fern-overlapping-pulls-").FullName;
        var digest = new string('b', 64);
        var set = UnionCase.Of<Accepted<string>>(BuildInventory.SetIdFor([new("fern-run", digest, "fern-run")])).Value;
        var manifest = new RuntimeReleaseManifest(RuntimeRelease.ManifestSchema, set, "source", "commit", "now", "test",
            [new RuntimeReleaseMember("fern-run", digest, 1, null, "fern-run." + digest)]);
        var handler = new OverlappingDownloads(manifest);
        using var github = new GitHubReleases("test-token", handler: handler);
        var request = new RuntimeRelease.InstallRequest("owner/repo", "test", null, "http://home/", root, root, root, "", "");
        try
        {
            var first = Task.Run(() => Assert.Throws<IOException>(() => RuntimeRelease.Install(github, request)));
            Assert.True(handler.First.Wait(TimeSpan.FromSeconds(5)));
            var second = Task.Run(() => Assert.Throws<IOException>(() => RuntimeRelease.Install(github, request)));
            Assert.True(handler.Second.Wait(TimeSpan.FromSeconds(5)));
            var staging = Path.Combine(root, "runtime-release-downloads");
            Assert.Equal(2, Directory.GetDirectories(staging).Length);
            handler.ReleaseFirst.Set();
            await first;
            Assert.Single(Directory.GetDirectories(staging));
            handler.ReleaseSecond.Set();
            await second;
            Assert.Empty(Directory.GetDirectories(staging));
        }
        finally
        {
            handler.ReleaseFirst.Set(); handler.ReleaseSecond.Set();
            Directory.Delete(root, true);
        }
    }
    [Fact]
    public void AssetsAndManifestsAreNamedByTheContentTheyCarry()
    {
        var set = "sha256:0fdc3d0ea4964d74cc86477ee9c274aac9da17795633864fda29e4aa61a1feee";
        Assert.Equal("0fdc3d0ea496", RuntimeRelease.ShortSet(set));
        Assert.Equal("0fdc3d0ea496", RuntimeRelease.ShortSet(set["sha256:".Length..]));
        Assert.Equal("runtime-set.0fdc3d0ea496.json", RuntimeRelease.ManifestAsset(set));
        var refused = Assert.Throws<InvalidOperationException>(() => RuntimeRelease.ShortSet("not-a-digest"));
        Assert.Contains("is not a sha256 identity", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBootstrapIsOneRequestAndReadsNoJsonOnTheMachine()
    {
        var command = RuntimeRelease.BootstrapCommand("QilinWang/fern-private", 558582369, "/workspace/.fern-token", "./fern-run");
        Assert.StartsWith("curl -fsSL ", command, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer $(cat /workspace/.fern-token)", command, StringComparison.Ordinal);
        Assert.Contains("Accept: application/octet-stream", command, StringComparison.Ordinal);
        Assert.Contains("https://api.github.com/repos/QilinWang/fern-private/releases/assets/558582369", command, StringComparison.Ordinal);
        Assert.EndsWith("-o ./fern-run && chmod +x ./fern-run", command, StringComparison.Ordinal);
        // One request: nothing to parse, nothing to list, no second round trip.
        Assert.Equal(1, command.Split("curl").Length - 1);
    }

    [Fact]
    public void AManifestNamesEveryMemberByDigestAndSurvivesItsOwnSerialization()
    {
        var manifest = new RuntimeReleaseManifest(
            RuntimeRelease.ManifestSchema,
            "sha256:" + new string('a', 64),
            "cafe" + new string('0', 36) + "+archive:" + new string('b', 64),
            "cafe" + new string('0', 36),
            PublishedAt: "2026-09-12T00:00:00.0000000+00:00",
            Publisher: "fern-run publish-runtime-release",
            Members:
            [
                new RuntimeReleaseMember("fern-run", new string('c', 64), 44233422, "sha256:" + new string('d', 64), "fern-run." + new string('c', 64)),
            ]);
        var document = JsonSerializer.Serialize(manifest);
        Assert.Contains("\"binary_set_id\"", document, StringComparison.Ordinal);
        Assert.Contains("\"built_from_commit\"", document, StringComparison.Ordinal);
        // The mirror that holds the release has its own commit ids, so the
        // manifest claims nothing about it; what it names is this repository's
        // commit, which every binary repeats from `version --json`.
        Assert.DoesNotContain("commit_present_in_repository", document, StringComparison.Ordinal);
        Assert.Contains("\"input_digest\"", document, StringComparison.Ordinal);
        var read = JsonSerializer.Deserialize<RuntimeReleaseManifest>(document);
        Assert.NotNull(read);
        // Field by field: a record's own equality compares the member list by
        // reference, which would pass whatever the list held.
        Assert.Equal(manifest with { Members = [] }, read! with { Members = [] });
        Assert.Equal(manifest.Members, read.Members);
        var member = Assert.Single(read.Members);
        Assert.Equal(member.Member + "." + member.Sha256, member.Asset);
    }

    /// <summary>
    /// The shape a Pod installs in: it answers to a Home it reaches over HTTP,
    /// it places no work so it holds no execution profiles, and it reaches no
    /// other machine so it holds no SSH helper. The receipt records all three
    /// facts, and the verifier that governs a locally built set accepts it
    /// unchanged — which is what makes a downloaded runtime the same thing as
    /// a built one.
    /// </summary>
    [Fact]
    public void APullWorkerInstallsWithNoProfilesNoSshHelperAndAHomeItNamesByAddress()
    {
        var root = Directory.CreateTempSubdirectory("fern-pull-worker-").FullName;
        var stores = ProbeFixtures.WriteControlStores(Path.Combine(root, "stores"));
        const string home = "http://127.0.0.1:8621/";
        var config = new InstallConfig
        {
            SourceSiteBinary = ProbeFixtures.WriteControlProbe(InstallationIdentityContract.WorkerSiteRole),
            SourceExperimentBinary = ProbeFixtures.WriteProbe("exit 0"),
            SourceRunBinary = ProbeFixtures.WriteProbe("exit 0"),
            SourceCompileBinary = ProbeFixtures.WriteProbe("exit 0"),
            SourceRevision = "published-release",
            Registry = home,
            DeployRoot = stores.DeployRoot,
            ProfilesRoot = null,
            SshHelper = null,
            WorkRoot = Path.Combine(root, "work"),
            DeploymentRoot = Path.Combine(root, "deploy"),
            BuildInventory = Path.Combine(root, "deploy", BuildInventory.FileName),
        };

        var installation = UnionCase.Of<Accepted<Installation>>(ControlPlaneInstaller.Install(config)).Value;
        var receipt = JsonDocument.Parse(File.ReadAllText(installation.ReceiptPath)).RootElement;
        // Recorded without the trailing slash: two spellings of one Home are
        // one identity, and the receipt is what a later verification compares.
        Assert.Equal("http://127.0.0.1:8621", receipt.GetProperty("registry").GetString());
        Assert.Equal(JsonValueKind.Null, receipt.GetProperty("profiles_root").ValueKind);
        Assert.Equal(JsonValueKind.Null, receipt.GetProperty("ssh_helper").ValueKind);

        var verified = InstallationVerification.VerifyMaterialized(installation.ReceiptPath, home);
        var accepted = Assert.IsType<InstallationVerificationOutcome.Accepted>(verified);
        Assert.Null(accepted.Installation.ProfilesRoot);
        Assert.Null(accepted.Installation.SshHelper);
        Assert.Equal("http://127.0.0.1:8621", accepted.Installation.RegistryPath);
        Assert.Equal(installation.RunBinaryPath, accepted.Installation.RunBinaryPath);

        // A different Home is a different installation, by name.
        var other = InstallationVerification.VerifyMaterialized(installation.ReceiptPath, "http://127.0.0.1:9999/");
        var refused = Assert.IsType<InstallationVerificationOutcome.Refused>(other);
        Assert.Contains("http://127.0.0.1:8621", refused.Detail, StringComparison.Ordinal);

        Directory.Delete(root, recursive: true);
    }
}
