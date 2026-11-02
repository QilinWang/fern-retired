using System.Diagnostics;
using System.Text;
using Xunit;

namespace Fern.Scheduler.Tests;

/// <summary>
/// `fern-run check-corrections` and `fern-run refresh-corrections`, ported from
/// the Go fern-card (tools/cardbox-go/fern/corrections.go).
///
/// The golden is the digest bytes: a fixture carrying a retired entry, a long
/// entry summarised by a [digest] block and a short entry with none is
/// projected here and must equal, byte for byte and hash for hash, what
/// `fern-card refresh-corrections` wrote for the same source. The second
/// golden is the repository's own committed digest: the checked-in
/// .ai/corrections_active.md must still be exactly what this projector
/// produces from .ai/corrections.md, which the read-only check verb proves
/// without writing anything.
///
/// Every refusal the Go projector could raise has a case below, because a
/// refusal that silently became a successful projection is the one failure a
/// digest cannot survive.
/// </summary>
public sealed class CorrectionsDigestVerbTests
{
    const string ThemeIndex = "- **deciding whether something is running**: 229c, 240\n";

    const string Entry240 =
        "240. A CORRECT PROJECTION IS STILL A BAD MONITOR.\n"
        + "     It led with hashes when the reader wanted a model and a world.\n"
        + "     narrative 2.\n"
        + "     narrative 3.\n"
        + "     narrative 4.\n"
        + "     narrative 5.\n"
        + "     narrative 6.\n"
        + "     narrative 7.\n"
        + "     narrative 8.\n"
        + "     narrative 9.\n"
        + "     narrative 10.\n"
        + "     narrative 11.\n"
        + "     narrative 12.\n\n";

    const string Block240 =
        "\n## Active Digest Blocks\n\n"
        + "### correction-240\n"
        + "[digest]\n"
        + "Rule: Lead a monitor with the model and world, never with hashes.\n"
        + "Action: Put authority facts below the answer, not in place of it.\n"
        + "[/digest]\n";

    /// <summary>The fixture the byte golden is pinned on: history, a summarised long entry, a short one.</summary>
    const string GoldenSource =
        "# Corrections\n\n"
        + "- **deciding whether something is running**: 229c, 240, 241\n\n"
        + "229c. THE STORAGE FLOOR MOVED.\n"
        + "     [superseded 2026-08-13: moved into the runtime gate]\n\n"
        + "240. A CORRECT PROJECTION IS STILL A BAD MONITOR.\n"
        + "     It led with hashes when the reader wanted a model and a world.\n"
        + "     narrative 2.\n"
        + "     narrative 3.\n"
        + "     narrative 4.\n"
        + "     narrative 5.\n"
        + "     narrative 6.\n"
        + "     narrative 7.\n"
        + "     narrative 8.\n"
        + "     narrative 9.\n"
        + "     narrative 10.\n"
        + "     narrative 11.\n"
        + "     narrative 12.\n\n"
        + "241. BANK COUNT IS NOT IN THE RECIPE NAME.\n"
        + "     Only in the hash preimage.\n\n"
        + "## Active Digest Blocks\n\n"
        + "### correction-240\n"
        + "[digest]\n"
        + "Rule: Lead a monitor with the model and world, never with hashes.\n"
        + "Action: Put authority facts below the answer, not in place of it.\n"
        + "[/digest]\n";

    /// <summary>Exactly the bytes `fern-run refresh-corrections` writes for <see cref="GoldenSource"/>: the Go
    /// fern-card golden with one deliberate change, the regenerating command named in the header (2026-09-02).</summary>
    const string GoldenDigest =
        "# Corrections — active digest (GENERATED, do not edit)\n"
        + "# Source: .ai/corrections.md. Regenerate: fern-run refresh-corrections\n"
        + "# 1 entries are history (superseded, enforced by the control\n"
        + "# plane, or dormant) and live only in the source file; nothing here was reworded.\n"
        + "\n"
        + "- **deciding whether something is running**: 240, 241\n"
        + "\n"
        + "240. Rule: Lead a monitor with the model and world, never with hashes.\n"
        + "     Action: Put authority facts below the answer, not in place of it.\n"
        + "     Full entry: .ai/corrections.md:8\n"
        + "\n"
        + "241. BANK COUNT IS NOT IN THE RECIPE NAME.\n"
        + "     Only in the hash preimage.\n";

    static readonly string GoldenReceipt =
        "{\n"
        + "  \"schema\": \"fern.knowledge.corrections-digest-refresh.v2\",\n"
        + "  \"status\": \"written\",\n"
        + "  \"source_path\": \".ai/corrections.md\",\n"
        + "  \"digest_path\": \".ai/corrections_active.md\",\n"
        + "  \"source_lines\": 31,\n"
        + "  \"digest_lines\": 13,\n"
        + "  \"history_entries\": 1\n"
        + "}\n";

    [Fact]
    public void RefreshedFixtureDigestIsThePinnedProjection()
    {
        var root = TemporaryRoot(GoldenSource);
        var refreshed = RunFernRun("refresh-corrections", "--root", root);

        Assert.Equal(0, refreshed.ExitCode);
        Assert.Equal(GoldenReceipt, refreshed.StandardOutput);
        Assert.Equal(
            GoldenDigest,
            new UTF8Encoding(false).GetString(File.ReadAllBytes(Path.Combine(root, ".ai", "corrections_active.md"))));
    }

    [Fact]
    public void RefreshWritesOnceAndThenReportsUnchanged()
    {
        var root = TemporaryRoot(Source("240. ACTIVE.\n", 1, ""));
        var first = RunFernRun("refresh-corrections", "--root", root);
        var second = RunFernRun("refresh-corrections", "--root", root);

        Assert.Contains("\"status\": \"written\"", first.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"status\": \"unchanged\"", second.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(
            first.StandardOutput.Replace("\"written\"", "\"unchanged\"", StringComparison.Ordinal),
            second.StandardOutput);
        Assert.Equal(0, RunFernRun("check-corrections", "--root", root).ExitCode);
    }

    /// <summary>
    /// The repository's own committed digest, proved read-only: `check-corrections`
    /// only reports "typed" when .ai/corrections_active.md equals this projector's
    /// output for .ai/corrections.md byte for byte.
    /// </summary>
    [Fact]
    public void CommittedRepositoryDigestIsExactlyThisProjection()
    {
        var inventory = RunFernRun("check-corrections", "--root", TestRegistry.RepoRoot());

        Assert.Equal(0, inventory.ExitCode);
        Assert.Contains("\"classification\": \"typed\"", inventory.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"diagnostics\": []", inventory.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"schema\": \"fern.knowledge.inventory.v2\"", inventory.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleDigestIsRefusedAndNamedInTheInventory()
    {
        var root = TemporaryRoot(Source("240. ACTIVE.\n", 1, ""));
        Assert.Equal(0, RunFernRun("refresh-corrections", "--root", root).ExitCode);
        File.WriteAllText(Path.Combine(root, ".ai", "corrections.md"), Source("240. CHANGED.\n", 1, ""));

        var stale = RunFernRun("check-corrections", "--root", root);

        Assert.Equal(2, stale.ExitCode);
        Assert.Contains("\"classification\": \"invalid\"", stale.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "generated .ai/corrections_active.md is missing or stale; run fern-run refresh-corrections",
            stale.StandardOutput,
            StringComparison.Ordinal);
        Assert.Equal(
            "fern-run: Corrections check refused: generated .ai/corrections_active.md is missing or stale; "
            + "run fern-run refresh-corrections",
            stale.StandardError.Trim());
    }

    [Fact]
    public void AnUnreadableCorrectionsSourceIsRefusedByPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fern-run-corrections-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var refreshed = RunFernRun("refresh-corrections", "--root", root);

        Assert.Equal(2, refreshed.ExitCode);
        Assert.StartsWith(
            $"fern-run: could not read Corrections source {root}/.ai/corrections.md:",
            refreshed.StandardError.Trim(),
            StringComparison.Ordinal);
    }

    public static TheoryData<string, string, string> Refusals()
    {
        var secondBlock = Block240[("\n## Active Digest Blocks\n".Length)..];
        var overLimit =
            "\n## Active Digest Blocks\n\n### correction-240\n[digest]\nRule: "
            + new string('x', 985) + "\nAction: \n[/digest]\n";
        var data = new TheoryData<string, string, string>
        {
            {
                "unknown-id",
                Source(Entry240, 1, Block240.Replace("correction-240", "correction-999", StringComparison.Ordinal)),
                "digest block(s) name correction(s) [\"999\"] that no entry declares; "
                + "a summary of nothing cannot be projected"
            },
            {
                "digest-block-for-a-short-entry",
                Source("240. SHORT RULE.\n", 1, Block240),
                "digest block 240 names a 1-line entry; blocks are only for entries longer than 12 lines. "
                + "Remove the block so it cannot replace a different short rule by ID."
            },
            {
                "duplicate-theme-index",
                Source("240. ACTIVE.\n", 2, ""),
                "corrections digest would carry 2 theme-index blocks; the source has a duplicate index. "
                + "Keep the newest block in .ai/corrections.md and delete the rest."
            },
            {
                "long-entry-without-a-block",
                Source(LongEntry("260"), 1, ""),
                "correction(s) 260 (21 lines) are longer than 12 lines and have no digest block. "
                + "Add one under ## Active Digest Blocks in .ai/corrections.md, or tag the entry "
                + "superseded/enforced-by-code if it is history."
            },
            {
                "over-the-line-budget",
                Source(ShortEntries(2000), 1, ""),
                "the active digest would be 2004 lines, over the 2000 budget. Every session loads this file, "
                + "so shorten a block, retire an entry the control plane now enforces, or raise the budget "
                + "deliberately with the measurement that justifies it."
            },
            {
                "missing-required-field",
                Source(
                    Entry240,
                    1,
                    Block240.Replace(
                        "Action: Put authority facts below the answer, not in place of it.\n",
                        "",
                        StringComparison.Ordinal)),
                "digest block 240 has no Action: line"
            },
            {
                "unknown-field",
                Source(Entry240, 1, Block240.Replace("Action:", "Consequence:", StringComparison.Ordinal)),
                "digest block 240 has unrecognised field(s) [\"Consequence\"]; "
                + "the fields are Rule, Action, Exception, Evidence"
            },
            {
                "block-not-opening-with-a-field",
                Source(
                    Entry240,
                    1,
                    Block240.Replace("Rule: Lead", "A hand-written sentence.\nRule: Lead", StringComparison.Ordinal)),
                "digest block 240 must open with a Rule: line"
            },
            {
                "repeated-field",
                Source(
                    Entry240,
                    1,
                    Block240.Replace("Action: Put", "Rule: a second rule.\nAction: Put", StringComparison.Ordinal)),
                "digest block 240 repeats the Rule: field"
            },
            {
                "block-over-1000-characters",
                Source(Entry240, 1, overLimit),
                "digest block 240 is 1001 characters, over the 1000 limit; "
                + "shorten it rather than letting it be truncated"
            },
            {
                "duplicate-block",
                Source(Entry240, 1, Block240 + secondBlock),
                "correction 240 has more than one digest block; a second block would silently override the first"
            },
            {
                "unclosed-block",
                Source(Entry240, 1, Block240.Replace("[/digest]\n", "", StringComparison.Ordinal)),
                "digest block for 240 is missing its [/digest]"
            },
            {
                "heading-before-close",
                Source(
                    Entry240,
                    1,
                    Block240.Replace("Rule:", "### correction-241\nRule:", StringComparison.Ordinal)),
                "digest block for 240 is missing its [/digest]"
            },
            {
                "second-opener",
                Source(Entry240, 1, Block240.Replace("Rule:", "[digest]\nRule:", StringComparison.Ordinal)),
                "digest block 240 opens a second [digest] at appendix line 5 before closing the first; "
                + "everything above it would be discarded"
            },
            {
                "heading-without-block",
                Source(
                    Entry240,
                    1,
                    Block240.Replace("### correction-240\n", "### correction-240\n### correction-240\n", StringComparison.Ordinal)),
                "### correction-240 is followed by another heading at appendix line 4 without a [digest] of its own"
            },
            {
                "trailing-heading",
                Source(Entry240, 1, "\n## Active Digest Blocks\n\n### correction-240\n"),
                "### correction-240 has no [digest] block"
            },
            {
                "opener-without-a-heading",
                Source(Entry240, 1, "\n## Active Digest Blocks\n\n[digest]\nRule: a.\nAction: b.\n[/digest]\n"),
                "[digest] at appendix line 3 has no ### correction-<id> heading"
            },
            {
                "close-without-an-opener",
                Source(Entry240, 1, "\n## Active Digest Blocks\n\n### correction-240\n[/digest]\n"),
                "[/digest] at appendix line 4 closes nothing"
            },
            {
                "content-after-close",
                Source(Entry240, 1, Block240 + "\nEvidence: a line the author wrote after the block closed.\n"),
                "appendix line 9 is outside any digest block and would be silently dropped: "
                + "\"Evidence: a line the author wrote after the block closed.\""
            },
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void ARefusedSourceIsNeverProjected(string name, string source, string expected)
    {
        var root = TemporaryRoot(source);
        var refreshed = RunFernRun("refresh-corrections", "--root", root);

        Assert.Equal(2, refreshed.ExitCode);
        Assert.Equal("fern-run: " + expected, refreshed.StandardError.Trim());
        Assert.False(
            File.Exists(Path.Combine(root, ".ai", "corrections_active.md")),
            $"{name} wrote a digest despite refusing");
    }

    static string Source(string entries, int indexBlocks, string appendix)
    {
        var builder = new StringBuilder("# Corrections\n\n");
        for (var block = 0; block < indexBlocks; block++)
        {
            builder.Append(ThemeIndex).Append('\n');
        }
        return builder.Append(entries).Append(appendix).ToString();
    }

    static string LongEntry(string id)
    {
        var builder = new StringBuilder($"{id}. A LONG NEW CORRECTION.\n");
        for (var index = 0; index < 20; index++)
        {
            builder.Append($"     narrative line {index}.\n");
        }
        return builder.ToString();
    }

    static string ShortEntries(int count)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < count; index++)
        {
            builder.Append($"{1000 + index}. SHORT.\n");
        }
        return builder.ToString();
    }

    static string TemporaryRoot(string source)
    {
        var root = Path.Combine(Path.GetTempPath(), $"fern-run-corrections-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".ai"));
        File.WriteAllBytes(Path.Combine(root, ".ai", "corrections.md"), new UTF8Encoding(false).GetBytes(source));
        return root;
    }

    internal static (int ExitCode, string StandardOutput, string StandardError) RunFernRun(params string[] arguments)
    {
        var repository = TestRegistry.RepoRoot();
        var executable = Path.Combine(repository, "src", "csharp", "Fern.Run.Cli", "bin", "Debug", "net11.0", "fern-run");
        Assert.True(File.Exists(executable), $"build fern-run before this test: {executable}");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("cannot start fern-run");
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, errors.GetAwaiter().GetResult());
    }
}
