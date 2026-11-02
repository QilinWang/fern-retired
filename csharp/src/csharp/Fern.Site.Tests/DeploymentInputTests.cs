using System.Text;
using Xunit;

namespace Fern.Site.Tests;

/// <summary>
/// Whether this Worker Site is running the source Home thinks it is. Ported
/// from tools/fern-control/site/source_test.go at 79af3ca5.
/// </summary>
public sealed class DeploymentInputTests
{
    [Fact]
    public void LocalDigestIsWhatSha256SumPrints()
    {
        using var home = new SiteFixture.TemporaryDirectory();
        File.WriteAllBytes(home.Join("empty"), []);
        File.WriteAllBytes(home.Join("fern"), Encoding.UTF8.GetBytes("fern"));

        var digests = DeploymentSourceReading.LocalDigests(home.Path, ["empty", "fern"]);

        // The bare hex sha256sum prints, with no "sha256:" prefix: a person
        // must be able to check it with the ordinary tool.
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", digests["empty"]);
        Assert.Equal("f0a5da17e0ffa3a21c2b07c964d465799eb0deb62905ccb1b271ce7716614746", digests["fern"]);
    }

    [Fact]
    public void MatchingInputsReadAsInSync()
    {
        var comparison = DeploymentSourceReading.CompareSource(
            ["fr_tr.py"],
            new Dictionary<string, string> { ["fr_tr.py"] = "aa" },
            new Dictionary<string, string> { ["fr_tr.py"] = "aa" });

        Assert.Contains("in sync", DeploymentSourceReading.DescribeSource("runpod-5090", comparison), StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedLockfileAloneIsStaleAndNamesTheFile()
    {
        var comparison = DeploymentSourceReading.CompareSource(
            ["fr_tr.py", "uv.lock"],
            new Dictionary<string, string> { ["fr_tr.py"] = "aaaaaaaaaaaaaa", ["uv.lock"] = "bbbbbbbbbbbbbb" },
            new Dictionary<string, string> { ["fr_tr.py"] = "aaaaaaaaaaaaaa", ["uv.lock"] = "cccccccccccccc" });

        var text = DeploymentSourceReading.DescribeSource("runpod-5090", comparison);

        Assert.Contains("STALE", text, StringComparison.Ordinal);
        // CHG 2026-08-28: the disagreeing input is now uv.lock, and the
        // closing sentence no longer explains Cargo.lock. Nothing compiles on
        // a Site, so the Rust lock left the watched list with the Rust tree;
        // uv.lock is a lock that still decides what a Stage runs.
        Assert.Contains("uv.lock: differs", text, StringComparison.Ordinal);
        // The file that AGREES is not named: a report that lists everything is
        // a report nobody reads.
        Assert.DoesNotContain("fr_tr.py:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Cargo.lock", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingSiteFileIsNotCalledADifference()
    {
        var comparison = DeploymentSourceReading.CompareSource(
            ["src/serie/uv.lock"],
            new Dictionary<string, string> { ["src/serie/uv.lock"] = "aa" },
            new Dictionary<string, string>());

        Assert.Contains(
            "missing on the Worker Site",
            DeploymentSourceReading.DescribeSource("runpod-5090", comparison),
            StringComparison.Ordinal);
    }

    [Fact]
    public void LocalEditIsNamedRatherThanCounted()
    {
        var changed = DeploymentSourceReading.ChangedInputs(
            new Dictionary<string, string> { ["fr_tr.py"] = "aa", ["Cargo.lock"] = "bb" },
            new Dictionary<string, string> { ["fr_tr.py"] = "aa", ["Cargo.lock"] = "cc" });

        Assert.Equal(["Cargo.lock"], changed);
    }

    [Fact]
    public void AppearingOrDeletedInputIsAChange()
    {
        var changed = DeploymentSourceReading.ChangedInputs(
            new Dictionary<string, string> { ["uv.lock"] = "aa" },
            new Dictionary<string, string> { ["src/serie/uv.lock"] = "bb" });

        Assert.Equal(["src/serie/uv.lock (appeared)", "uv.lock (deleted)"], changed);
    }

    /// <summary>
    /// Pin the complete current deployment-input contract. A changed member is
    /// reviewed here rather than inferred from a retired implementation.
    /// </summary>
    [Fact]
    public void WatchedListMatchesTheCurrentDeploymentContract()
    {
        Assert.Equal(
            new[]
            {
                "python/fern_stage_runtime/__init__.py",
                "python/fern_train/cell.py",
                "python/fern_train/components/action.py",
                "python/fern_train/components/core.py",
                "python/fern_train/components/density.py",
                "python/fern_train/components/__init__.py",
                "python/fern_train/config.py",
                "python/fern_train/data.py",
                "python/fern_train/__init__.py",
                "python/fern_train/loss.py",
                "python/fern_train/model.py",
                "python/fern_train/probes.py",
                "python/fern_train/specs.py",
                "python/fern_train/study.py",
                "python/fern_train/train.py",
                "python/fern_train/worker.py",
                "python/fern_train/zero_shot.py",
                "src/serie/api.py",
                "src/serie/controlled.py",
                "src/serie/counterfactual.py",
                "src/serie/__init__.py",
                "src/serie/noise.py",
                "src/serie/pipeline.py",
                "src/serie/specs.py",
                "src/serie/worlds/factory.py",
                "src/serie/worlds/__init__.py",
                "uv.lock",
                "src/serie/uv.lock",
            },
            DeploymentSourceReading.ReviewedDeploymentInputs);
    }

    static string RepositoryPath(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "csharp", "Fern.sln")) /* CHG 2026-08-28: Cargo.toml retired with the last crate; the C# solution marks the root */)
        {
            directory = directory.Parent;
        }
        var root = directory?.FullName
            ?? throw new InvalidOperationException("no repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine(root, relativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"{path} is missing. It is the Rust implementation this list is checked against; without it the "
                + "check is unproven, so this fails rather than skips.",
                path);
        }
        return path;
    }
}
