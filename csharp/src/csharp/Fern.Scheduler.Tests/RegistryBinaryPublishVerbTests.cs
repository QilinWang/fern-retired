using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// `fern-run publish-registry-binary`, the surviving half of the Go fern-card
/// install-knowledge-reconcile-service: a fern-registry is stored under its own
/// content digest, so republishing the same bytes changes nothing and a name
/// whose bytes disagree is refused rather than overwritten.
/// </summary>
public sealed class RegistryBinaryPublishVerbTests
{
    [Fact]
    public void BuildAndExistingBinaryAreExplicitExclusivePublicationInputs()
    {
        var (source, into) = Workspace(ValidSource);
        var both = CorrectionsDigestVerbTests.RunFernRun("publish-registry-binary", "--build", "--from", source, "--into", into);
        var neither = CorrectionsDigestVerbTests.RunFernRun("publish-registry-binary", "--into", into);
        Assert.Equal(2, both.ExitCode);
        Assert.Equal(2, neither.ExitCode);
        Assert.Contains("choose exactly one", both.StandardError);
        Assert.Empty(Directory.GetFiles(into));
    }

    const string VersionJson = """{"schema":"fern.registry_binary_version.v1","role":"registry-home","built_from_commit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","built_from_dirty_tree":false,"source_archive_sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","dotnet_runtime":".NET source-runtime","run_plan_protocol":"fern.run_plan_parent_child.v2"}""";
    const string ValidSource = "#!/bin/sh\nprintf '%s\\n' '" + VersionJson + "'\n";

    [Theory]
    [InlineData("exit 7", "exited 7")]
    [InlineData("printf 'not-json'", "JSON")]
    [InlineData("printf '{}'", "schema")]
    public void InvalidVersionNeverBorrowsThePublishersCommit(string script, string expected)
    {
        var (source, into) = Workspace("#!/bin/sh\n" + script + "\n");
        var result = CorrectionsDigestVerbTests.RunFernRun("publish-registry-binary", "--from", source, "--into", into);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(expected, result.StandardError);
        Assert.Empty(Directory.GetFiles(into));
    }

    [Fact]
    public void RepublishingTheSameBytesIsANoOp()
    {
        var (source, into) = Workspace(ValidSource);

        var first = CorrectionsDigestVerbTests.RunFernRun("publish-registry-binary", "--from", source, "--into", into);
        var published = first.StandardOutput.Trim();
        var firstBytes = File.ReadAllBytes(published);
        var firstReceipt = File.ReadAllBytes(published + ".receipt.json");
        var firstWrittenAt = File.GetLastWriteTimeUtc(published);

        var second = CorrectionsDigestVerbTests.RunFernRun("publish-registry-binary", "--from", source, "--into", into);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(published, second.StandardOutput.Trim());
        Assert.Equal(
            Path.Combine(into, "fern-registry." + Sha256(source)),
            published);
        Assert.Equal(firstBytes, File.ReadAllBytes(published));
        Assert.Equal(firstReceipt, File.ReadAllBytes(published + ".receipt.json"));
        Assert.Equal(firstWrittenAt, File.GetLastWriteTimeUtc(published));
        Assert.Contains("unchanged", second.StandardError, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(into, "fern-registry.*.receipt.json"));
    }

    [Fact]
    public void ADigestNameHoldingDifferentBytesIsRefused()
    {
        var (source, into) = Workspace("#!/bin/sh\nexit 7\n");
        var occupied = Path.Combine(into, "fern-registry." + Sha256(source));
        File.WriteAllText(occupied, "someone else's bytes\n");

        var refused = CorrectionsDigestVerbTests.RunFernRun(
            "publish-registry-binary", "--from", source, "--into", into);

        Assert.Equal(1, refused.ExitCode);
        Assert.Equal(
            $"publish-registry-binary failed: existing published binary {occupied} has wrong bytes",
            refused.StandardError.Trim());
        Assert.Equal("someone else's bytes\n", File.ReadAllText(occupied));
        Assert.False(File.Exists(occupied + ".receipt.json"));
    }

    [Fact]
    public void TheReceiptCarriesTheDigestSizeProvenanceAndPublisher()
    {
        var body = ValidSource;
        var (source, into) = Workspace(body);

        var published = CorrectionsDigestVerbTests
            .RunFernRun("publish-registry-binary", "--from", source, "--into", into)
            .StandardOutput.Trim();
        var receipt = JsonNode.Parse(File.ReadAllText(published + ".receipt.json")) as JsonObject;

        Assert.NotNull(receipt);
        Assert.Equal("fern.registry-binary-publish.v3", receipt["schema"]!.GetValue<string>());
        // Slice 10: a binary given by path has no observed inputs; the field is
        // present and null, so a reader can tell "unknown" from "absent".
        Assert.True(receipt!.ContainsKey("input_digest"));
        Assert.Null(receipt["input_digest"]);
        Assert.Equal(Sha256(source), receipt["sha256"]!.GetValue<string>());
        Assert.Equal(source, receipt["source_path"]!.GetValue<string>());
        Assert.Equal(published, receipt["published_path"]!.GetValue<string>());
        Assert.Equal(Encoding.UTF8.GetByteCount(body), receipt["byte_size"]!.GetValue<long>());
        Assert.StartsWith(".NET ", receipt["dotnet_runtime"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("fern-run publish-registry-binary", receipt["publisher"]!.GetValue<string>());
        Assert.StartsWith("fern-run ", receipt["publisher_version"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(
            DateTimeOffset.TryParse(receipt["published_at"]!.GetValue<string>(), out _),
            "published_at is not a timestamp");

        // The shell stub answers `version --json` with exit 7, so provenance
        // falls back to this repository's HEAD and says so.
        // CHG: that was the historical v1 behavior. The valid stub now names
        // its own identity; failed probes below must not create any receipt.
        Assert.Equal("ok", receipt["source_version_probe"]!.GetValue<string>());
        Assert.Equal("fern-registry-version-json", receipt["provenance"]!.GetValue<string>());
        Assert.Equal(new string('a', 40), receipt["built_from_commit"]!.GetValue<string>());
        Assert.False(receipt["built_from_dirty_tree"]!.GetValue<bool>());
        Assert.Equal(new string('b', 64), receipt["source_archive_sha256"]!.GetValue<string>());
        Assert.Equal(".NET source-runtime", receipt["dotnet_runtime"]!.GetValue<string>());
    }

    [Fact]
    public void ASourceThatCannotStartIsRefusedWithoutAReceipt()
    {
        // A file with no executable bit is a real binary as far as the copy is
        // concerned and a child process that never starts as far as the probe is.
        var (source, into) = Workspace("not an executable at all\n", executable: false);

        var result = CorrectionsDigestVerbTests.RunFernRun("publish-registry-binary", "--from", source, "--into", into);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(Directory.GetFiles(into, "*.receipt.json"));
    }

    [Fact]
    public void VersionRunsFromVerifiedDestinationEvenIfOriginalSourceDisappears()
    {
        var (source, into) = Workspace(ValidSource);
        File.WriteAllText(source, "#!/bin/sh\ncase \"$0\" in */registry-binaries/*) ;; *) exit 9;; esac\n"
            + "rm -- '" + source + "'\n" + "printf '%s\\n' '" + VersionJson + "'\n");
        var digest = Sha256(source);
        var length = new FileInfo(source).Length;
        var result = CorrectionsDigestVerbTests.RunFernRun("publish-registry-binary", "--from", source, "--into", into);
        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(source));
        var published = result.StandardOutput.Trim();
        var receipt = JsonNode.Parse(File.ReadAllText(published + ".receipt.json"))!;
        Assert.Equal(digest, Sha256(published));
        Assert.Equal(digest, receipt["sha256"]!.GetValue<string>());
        Assert.Equal(length, receipt["byte_size"]!.GetValue<long>());
    }

    [Fact]
    public void ExistingHistoricalReceiptIsPreservedWithoutRecertification()
    {
        var (source, into) = Workspace("#!/bin/sh\nexit 7\n");
        var published = Path.Combine(into, "fern-registry." + Sha256(source));
        File.Copy(source, published);
        const string historical = "{\"schema\":\"fern.registry-binary-publish.v1\",\"provenance\":\"git-head-at-publish\"}\n";
        File.WriteAllText(published + ".receipt.json", historical);
        var result = CorrectionsDigestVerbTests.RunFernRun("publish-registry-binary", "--from", source, "--into", into);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(historical, File.ReadAllText(published + ".receipt.json"));
        Assert.Contains("not recertified", result.StandardError);
    }

    [Fact]
    public void ChangedDestinationDuringVersionProbeEarnsNoReceipt()
    {
        var (source, into) = Workspace("#!/bin/sh\nchmod u+w \"$0\"\nprintf '\\n# changed\\n' >> \"$0\"\n"
            + "printf '%s\\n' '" + VersionJson + "'\n");
        var result = CorrectionsDigestVerbTests.RunFernRun("publish-registry-binary", "--from", source, "--into", into);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("changed during version probe", result.StandardError);
        Assert.Empty(Directory.GetFiles(into));
    }

    [Fact]
    public void ChangedDestinationIsRemovedAndTheSameSourceBytesCanRetry()
    {
        var (source, into) = Workspace(ValidSource);
        var marker = Path.Combine(Path.GetDirectoryName(source)!, "first-version-probe");
        File.WriteAllText(source, "#!/bin/sh\nif [ ! -e '" + marker + "' ]; then\n"
            + "touch '" + marker + "'\nchmod u+w \"$0\"\nprintf '\\n# changed\\n' >> \"$0\"\nfi\n"
            + "printf '%s\\n' '" + VersionJson + "'\n");
        var digest = Sha256(source);
        var published = Path.Combine(into, "fern-registry." + digest);

        var first = CorrectionsDigestVerbTests.RunFernRun("publish-registry-binary", "--from", source, "--into", into);
        Assert.Equal(1, first.ExitCode);
        Assert.Contains("changed during version probe", first.StandardError);
        Assert.Empty(Directory.GetFiles(into));
        Assert.Equal(digest, Sha256(source));

        var retry = CorrectionsDigestVerbTests.RunFernRun("publish-registry-binary", "--from", source, "--into", into);
        Assert.Equal(0, retry.ExitCode);
        Assert.Equal(published, retry.StandardOutput.Trim());
        Assert.Equal(digest, Sha256(published));
        var receipt = JsonNode.Parse(File.ReadAllText(published + ".receipt.json"))!;
        Assert.Equal(digest, receipt["sha256"]!.GetValue<string>());
    }

    [Fact]
    public void FailedProbePreservesAnExistingUnreceiptedBinary()
    {
        var (source, into) = Workspace("#!/bin/sh\nexit 7\n");
        var published = Path.Combine(into, "fern-registry." + Sha256(source));
        File.Copy(source, published);
        var original = File.ReadAllBytes(published);
        var writtenAt = File.GetLastWriteTimeUtc(published);

        var result = CorrectionsDigestVerbTests.RunFernRun("publish-registry-binary", "--from", source, "--into", into);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("exited 7", result.StandardError);
        Assert.Equal(original, File.ReadAllBytes(published));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(published));
        Assert.False(File.Exists(published + ".receipt.json"));
    }

    [Fact]
    public void FailedReceiptPublicationRemovesOnlyTheNewCandidate()
    {
        var (source, into) = Workspace(ValidSource);
        var published = Path.Combine(into, "fern-registry." + Sha256(source));
        var receiptDirectory = Directory.CreateDirectory(published + ".receipt.json").FullName;
        var existing = Path.Combine(receiptDirectory, "existing");
        File.WriteAllText(existing, "keep");

        var result = CorrectionsDigestVerbTests.RunFernRun("publish-registry-binary", "--from", source, "--into", into);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.False(File.Exists(published));
        Assert.Empty(Directory.GetFiles(into));
        Assert.Equal("keep", File.ReadAllText(existing));
    }

    [Fact]
    public void DevelopmentIdentityDoesNotInventAnArchive()
    {
        var report = JsonNode.Parse(VersionJson)!;
        report["source_archive_sha256"] = null;
        report["built_from_dirty_tree"] = true;
        var (source, into) = Workspace("#!/bin/sh\nprintf '%s' '" + report.ToJsonString() + "'\n");
        var result = CorrectionsDigestVerbTests.RunFernRun("publish-registry-binary", "--from", source, "--into", into);
        Assert.Equal(0, result.ExitCode);
        var receipt = JsonNode.Parse(File.ReadAllText(result.StandardOutput.Trim() + ".receipt.json"))!;
        Assert.Null(receipt["source_archive_sha256"]);
        Assert.True(receipt["built_from_dirty_tree"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("schema", "wrong")]
    [InlineData("role", "scheduler")]
    [InlineData("run_plan_protocol", "wrong")]
    [InlineData("built_from_commit", "unrecorded")]
    [InlineData("built_from_dirty_tree", "false")]
    [InlineData("source_archive_sha256", "invalid")]
    public void MalformedIdentityFieldsAreNamedAndRefused(string field, string replacement)
    {
        var report = JsonNode.Parse(VersionJson)!;
        report[field] = replacement;
        var (source, into) = Workspace("#!/bin/sh\nprintf '%s' '" + report.ToJsonString() + "'\n");
        var result = CorrectionsDigestVerbTests.RunFernRun("publish-registry-binary", "--from", source, "--into", into);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(field, result.StandardError);
        Assert.Empty(Directory.GetFiles(into, "*.receipt.json"));
    }

    [Theory]
    [InlineData("sleep 8", "5000 ms")]
    [InlineData("sleep 8 &\nexit 0", "5000 ms")]
    [InlineData("while :; do printf '0123456789abcdef'; done", "65536-byte")]
    [InlineData("while :; do printf '0123456789abcdef' >&2; done", "65536-byte")]
    public void VersionProcessAndBothOutputStreamsAreBounded(string body, string expected)
    {
        var (source, into) = Workspace("#!/bin/sh\n" + body + "\n");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = CorrectionsDigestVerbTests.RunFernRun("publish-registry-binary", "--from", source, "--into", into);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expected, result.StandardError);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
        Assert.Empty(result.StandardOutput);
        Assert.Empty(Directory.GetFiles(into, "*.receipt.json"));
    }

    [Fact]
    public void AMissingSourceIsRefusedBeforeAnythingIsWritten()
    {
        var (_, into) = Workspace("#!/bin/sh\nexit 7\n");
        var absent = Path.Combine(into, "not-built-yet");

        var refused = CorrectionsDigestVerbTests.RunFernRun(
            "publish-registry-binary", "--from", absent, "--into", into);

        Assert.Equal(1, refused.ExitCode);
        Assert.Equal(
            $"publish-registry-binary failed: fern-registry executable {absent} is not a regular file",
            refused.StandardError.Trim());
        Assert.Empty(Directory.GetFiles(into, "fern-registry.*"));
    }

    static (string Source, string Into) Workspace(string body, bool executable = true)
    {
        var root = Path.Combine(Path.GetTempPath(), $"fern-run-publish-{Guid.NewGuid():N}");
        var into = Path.Combine(root, "registry-binaries");
        Directory.CreateDirectory(into);
        var source = Path.Combine(root, "fern-registry");
        File.WriteAllBytes(source, Encoding.UTF8.GetBytes(body));
        if (executable && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                source,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return (source, into);
    }

    static string Sha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
